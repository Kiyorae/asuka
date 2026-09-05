[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string] $Package,
    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9A-Fa-f]{64}$')]
    [string] $ExpectedSha256,
    [switch] $VerifyOnly
)

$ErrorActionPreference = 'Stop'
$developmentPublisher = 'CN=Matcha Development, OID.2.25.311729368913984317654407730594956997722=1'
$expectedHash = $ExpectedSha256.ToUpperInvariant()

function Fail([string] $Message) { throw "Unsigned MSIX installation refused: $Message" }

# Windows PowerShell 5.1 exposes ZipFile from the FileSystem companion
# assembly; modern PowerShell resolves it from System.IO.Compression.
try { Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction Stop }
catch { Add-Type -AssemblyName System.IO.Compression -ErrorAction Stop }

function Read-ZipEntryText([string] $PackagePath, [string] $EntryName) {
    $archive = [System.IO.Compression.ZipFile]::OpenRead($PackagePath)
    try {
        $entry = $archive.GetEntry($EntryName)
        if ($null -eq $entry) { Fail "'$EntryName' was not found in the package" }
        $reader = [System.IO.StreamReader]::new($entry.Open())
        try { return $reader.ReadToEnd() } finally { $reader.Dispose() }
    } finally { $archive.Dispose() }
}

function Get-PackageEntries([string] $PackagePath) {
    $archive = [System.IO.Compression.ZipFile]::OpenRead($PackagePath)
    try { return @($archive.Entries | ForEach-Object { $_.FullName }) }
    finally { $archive.Dispose() }
}

function Test-ElevatedAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Test-ArchitectureCompatible([string] $PackageArchitecture) {
    $nativeArchitecture = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
    switch ($PackageArchitecture.ToLowerInvariant()) {
        'arm64' { return $nativeArchitecture -eq 'arm64' }
        # Windows 11 on ARM includes x64 emulation; x64 packages are also valid on AMD64.
        'x64' { return $nativeArchitecture -in @('x64', 'arm64') }
        default { return $false }
    }
}

function Test-UnsignedPackage([string] $PackagePath) {
    $entries = Get-PackageEntries $PackagePath
    if (@($entries | Where-Object { $_ -ieq 'AppxSignature.p7x' }).Count -ne 0) { Fail 'the package is signed; AllowUnsigned is only for the dedicated unsigned development identity' }
    [xml]$manifest = Read-ZipEntryText $PackagePath 'AppxManifest.xml'
    $manager = [System.Xml.XmlNamespaceManager]::new($manifest.NameTable)
    $manager.AddNamespace('f', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10')
    $identity = $manifest.SelectSingleNode('/f:Package/f:Identity', $manager)
    $application = $manifest.SelectSingleNode('/f:Package/f:Applications/f:Application', $manager)
    $targetDeviceFamily = $manifest.SelectSingleNode('/f:Package/f:Dependencies/f:TargetDeviceFamily[@Name="Windows.Desktop"]', $manager)
    if ($null -eq $identity -or $identity.Name -cne 'Matcha.Windows') { Fail 'package identity Name is not Matcha.Windows' }
    if ($identity.Publisher -cne $developmentPublisher -or $identity.Publisher -notmatch '(?:^|,\s*)OID\.2\.25\.311729368913984317654407730594956997722=1(?:,|$)') {
        Fail 'publisher is not the dedicated Microsoft AllowUnsigned development identity'
    }
    if ($null -eq $application -or $application.EntryPoint -cne 'Windows.FullTrustApplication') { Fail 'package is not a Matcha full-trust application' }
    if ($null -eq $targetDeviceFamily -or [string]::IsNullOrWhiteSpace($targetDeviceFamily.MinVersion)) {
        Fail 'package has no Windows.Desktop minimum-version declaration'
    }
    try { $minimumWindowsVersion = [version]$targetDeviceFamily.MinVersion }
    catch { Fail "package has an invalid Windows.Desktop MinVersion '$($targetDeviceFamily.MinVersion)'" }
    if ([Environment]::OSVersion.Version -lt $minimumWindowsVersion) {
        Fail "package requires Windows $minimumWindowsVersion or newer; this device is $([Environment]::OSVersion.Version)"
    }
    if (-not (Test-ArchitectureCompatible $identity.ProcessorArchitecture)) {
        Fail "package architecture '$($identity.ProcessorArchitecture)' is not compatible with this '$([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture)' device"
    }
    return [pscustomobject]@{ Identity = $identity; Application = $application }
}

function Test-ProtectedStagingParent([string] $Path) {
    $writeMask = [int64]([Security.AccessControl.FileSystemRights]::WriteData -bor
        [Security.AccessControl.FileSystemRights]::AppendData -bor
        [Security.AccessControl.FileSystemRights]::WriteAttributes -bor
        [Security.AccessControl.FileSystemRights]::WriteExtendedAttributes -bor
        [Security.AccessControl.FileSystemRights]::Delete -bor
        [Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles -bor
        [Security.AccessControl.FileSystemRights]::ChangePermissions -bor
        [Security.AccessControl.FileSystemRights]::TakeOwnership) -bor 0x50000000L # GENERIC_WRITE | GENERIC_ALL
    $trustedWriteSids = @(
        'S-1-5-18', # LocalSystem
        'S-1-5-32-544', # Builtin Administrators
        'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464' # TrustedInstaller
    )
    $accessControl = Get-Acl -LiteralPath $Path
    $ownerSid = $accessControl.GetOwner([Security.Principal.SecurityIdentifier]).Value
    if ($ownerSid -notin $trustedWriteSids) {
        Fail "staging parent '$Path' has untrusted owner SID '$ownerSid'"
    }
    foreach ($rule in $accessControl.Access) {
        if ($rule.AccessControlType -ne [Security.AccessControl.AccessControlType]::Allow) { continue }
        # Some built-in app-container principals cannot be translated by every
        # PowerShell host. Read-only entries are irrelevant to this check, so
        # only resolve an identity after confirming that its rule can write.
        if (([int64]$rule.FileSystemRights -band [int64]$writeMask) -eq 0) { continue }
        try { $sid = $rule.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value }
        catch { Fail "could not resolve write-capable staging ACL identity '$($rule.IdentityReference)'" }
        # CREATOR OWNER is the standard generic inheritance template on Program
        # Files. The new directory's inherited ACL is immediately replaced with
        # the protected SYSTEM/Administrators DACL below. Do not ignore any other
        # inherit-only writer, because it would become effective on the child.
        if (($sid -ceq 'S-1-3-0') -and
            (($rule.PropagationFlags -band [Security.AccessControl.PropagationFlags]::InheritOnly) -ne 0)) {
            continue
        }
        if ($sid -notin $trustedWriteSids) {
            Fail "staging parent '$Path' grants non-privileged SID '$sid' write-like access"
        }
    }
}

function Set-ProtectedStagingDirectory([string] $Path) {
    $administrators = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
    $localSystem = [Security.Principal.SecurityIdentifier]::new('S-1-5-18')
    $security = [Security.AccessControl.DirectorySecurity]::new()
    $security.SetAccessRuleProtection($true, $false)
    $security.SetOwner($administrators)
    $inheritance = [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor [Security.AccessControl.InheritanceFlags]::ObjectInherit
    foreach ($sid in @($administrators, $localSystem)) {
        $rule = [Security.AccessControl.FileSystemAccessRule]::new(
            $sid,
            [Security.AccessControl.FileSystemRights]::FullControl,
            $inheritance,
            [Security.AccessControl.PropagationFlags]::None,
            [Security.AccessControl.AccessControlType]::Allow)
        [void]$security.AddAccessRule($rule)
    }
    # Set-Acl is available in both the inbox Windows PowerShell 5.1 host and
    # modern PowerShell. FileSystemAclExtensions only exists on modern .NET.
    Set-Acl -LiteralPath $Path -AclObject $security
}

function Test-ProtectedStagingDirectory([string] $Path) {
    $administrators = 'S-1-5-32-544'
    $localSystem = 'S-1-5-18'
    $security = Get-Acl -LiteralPath $Path
    $owner = $security.GetOwner([Security.Principal.SecurityIdentifier]).Value
    if ($owner -cne $administrators) { Fail "secure staging directory '$Path' is not owned by Builtin Administrators" }
    if (-not $security.AreAccessRulesProtected) { Fail "secure staging directory '$Path' still inherits ACL entries" }
    $rules = @($security.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]))
    if ($rules.Count -ne 2) { Fail "secure staging directory '$Path' has $($rules.Count) ACL entries; expected exactly two" }
    foreach ($rule in $rules) {
        if ($rule.AccessControlType -ne [Security.AccessControl.AccessControlType]::Allow -or
            $rule.IsInherited -or
            $rule.IdentityReference.Value -notin @($administrators, $localSystem) -or
            $rule.FileSystemRights -ne [Security.AccessControl.FileSystemRights]::FullControl -or
            $rule.InheritanceFlags -ne ([Security.AccessControl.InheritanceFlags]::ContainerInherit -bor [Security.AccessControl.InheritanceFlags]::ObjectInherit)) {
            Fail "secure staging directory '$Path' has an unexpected ACL entry"
        }
    }
}

function Get-StreamSha256([IO.Stream] $Stream) {
    $Stream.Position = 0
    $sha = [Security.Cryptography.SHA256]::Create()
    # Convert.ToHexString is unavailable in Windows PowerShell 5.1/.NET
    # Framework; BitConverter produces the same uppercase hexadecimal value.
    try { return [BitConverter]::ToString($sha.ComputeHash($Stream)).Replace('-', '') }
    finally { $sha.Dispose() }
}

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT -or [Environment]::OSVersion.Version.Build -lt 26100) {
    Fail 'Windows 11 version 24H2 (build 26100) or newer is required for this Matcha package.'
}
$resolvedPackage = Resolve-Path -LiteralPath $Package -ErrorAction Stop
if ($resolvedPackage.Count -ne 1 -or -not (Test-Path -LiteralPath $resolvedPackage.Path -PathType Leaf)) { Fail 'the package path must resolve to exactly one file' }
if ([IO.Path]::GetExtension($resolvedPackage.Path) -ine '.msix') { Fail 'only an exact .msix file is accepted; .msixbundle and directory paths are refused' }

if ($VerifyOnly) {
    $verifyStream = [IO.FileStream]::new($resolvedPackage.Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        $verifyHash = Get-StreamSha256 $verifyStream
        if ($verifyHash -cne $expectedHash) { Fail 'locked source package SHA-256 does not match -ExpectedSha256' }
        $evidence = Test-UnsignedPackage $resolvedPackage.Path
        [pscustomobject]@{
            Package = $resolvedPackage.Path
            Publisher = $evidence.Identity.Publisher
            Version = $evidence.Identity.Version
            Architecture = $evidence.Identity.ProcessorArchitecture
            EntryPoint = $evidence.Application.EntryPoint
            Signed = $false
            Sha256 = $verifyHash
            ReadyForAllowUnsigned = $true
        }
        return
    } finally { $verifyStream.Dispose() }
}
if (-not (Test-ElevatedAdministrator)) {
    Fail 'run this script from an already elevated Administrator PowerShell session; the script will not request UAC elevation.'
}

$programFiles = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)
if ([string]::IsNullOrWhiteSpace($programFiles) -or -not (Test-Path -LiteralPath $programFiles -PathType Container)) { Fail 'the Program Files staging parent is unavailable' }
Test-ProtectedStagingParent $programFiles
$stagingDirectory = Join-Path $programFiles ('Matcha-AllowUnsigned-' + [Guid]::NewGuid().ToString('N'))
$stagingPackage = Join-Path $stagingDirectory 'Matcha-AllowUnsigned.msix'
$sourceStream = $null
$stagingWriteStream = $null
$stagingReadLock = $null
try {
    New-Item -ItemType Directory -Path $stagingDirectory -ErrorAction Stop | Out-Null
    # Program Files prevents unprivileged creation. Immediately replace the
    # inherited ACL (which can include CREATOR OWNER) before staging a package.
    Set-ProtectedStagingDirectory $stagingDirectory
    Test-ProtectedStagingDirectory $stagingDirectory

    # FileShare.Read denies both write and delete to unprivileged source-path
    # races while the copied bytes are hashed and staged.
    $sourceStream = [IO.FileStream]::new($resolvedPackage.Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    $sourceHash = Get-StreamSha256 $sourceStream
    if ($sourceHash -cne $expectedHash) { Fail 'locked source package SHA-256 does not match -ExpectedSha256' }
    $sourceEvidence = Test-UnsignedPackage $resolvedPackage.Path
    $sourceVersion = [version][string]$sourceEvidence.Identity.Version
    $newerDevelopmentPackages = @(
        Get-AppxPackage -Name 'Matcha.Windows' -ErrorAction Stop |
            Where-Object {
                $_.Publisher -ceq $developmentPublisher -and
                [version][string]$_.Version -gt $sourceVersion
            }
    )
    if ($newerDevelopmentPackages.Count -ne 0) {
        $installedVersions = @($newerDevelopmentPackages | ForEach-Object { [string]$_.Version } | Sort-Object -Unique)
        Fail "a newer unsigned Matcha development identity is already installed ($($installedVersions -join ', ')); Windows does not permit package downgrades"
    }
    $sourceStream.Position = 0
    $stagingWriteStream = [IO.FileStream]::new($stagingPackage, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    $sourceStream.CopyTo($stagingWriteStream)
    $stagingWriteStream.Flush($true)
    $stagingWriteStream.Dispose()
    $stagingWriteStream = $null
    # A read-only FileShare.Read handle allows ZipFile and deployment reads,
    # while continuing to deny writes and deletion through deployment.
    $stagingReadLock = [IO.FileStream]::new($stagingPackage, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    $stagingHash = Get-StreamSha256 $stagingReadLock
    if ($sourceHash -cne $stagingHash) { Fail 'secure staging copy SHA-256 does not match the locked source package' }

    $stagedEvidence = Test-UnsignedPackage $stagingPackage
    if ($stagedEvidence.Identity.Version -cne $sourceEvidence.Identity.Version) { Fail 'secure staging package version changed during verification' }
    Write-Warning 'Installing the dedicated unsigned Matcha development identity from protected staging. This does not trust certificates and is not a way to bypass signatures on other packages.'
    Add-AppxPackage -AllowUnsigned -Path $stagingPackage
} finally {
    if ($null -ne $stagingReadLock) { $stagingReadLock.Dispose() }
    if ($null -ne $stagingWriteStream) { $stagingWriteStream.Dispose() }
    if ($null -ne $sourceStream) { $sourceStream.Dispose() }
    if (Test-Path -LiteralPath $stagingPackage -PathType Leaf) { Remove-Item -LiteralPath $stagingPackage -Force -ErrorAction Stop }
    if (Test-Path -LiteralPath $stagingDirectory -PathType Container) {
        # No recursive deletion: refuse cleanup if an unexpected entry exists.
        if (@(Get-ChildItem -LiteralPath $stagingDirectory -Force).Count -ne 0) { Fail "secure staging directory '$stagingDirectory' was unexpectedly non-empty during cleanup" }
        Remove-Item -LiteralPath $stagingDirectory -Force -ErrorAction Stop
    }
}
