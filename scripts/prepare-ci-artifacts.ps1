[CmdletBinding()]
param(
    [Parameter(Mandatory)] [psobject] $PackageEvidence,
    [Parameter(Mandatory)] [ValidateSet('x64', 'ARM64')] [string] $Platform,
    [Parameter(Mandatory)] [ValidatePattern('^[0-9a-fA-F]{40}(?:[0-9a-fA-F]{24})?$')] [string] $SourceCommit,
    [ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')] [string] $Repository = 'Kiyorae/asuka',
    [string] $OutputDirectory = 'artifacts/ci'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$architecture = $Platform.ToLowerInvariant()
$artifactName = "Asuka-windows-$architecture-unsigned-dev"
$msixName = "$artifactName.msix"
$expectedPublisher = 'CN=Asuka Development, OID.2.25.311729368913984317654407730594956997722=1'

function Fail([string] $Message) { throw "CI artifact preparation failed: $Message" }

foreach ($property in @('Package', 'Platform', 'Configuration', 'Version', 'Bytes', 'UnsignedInstallable', 'Signed', 'HasAppxSignature', 'Sha256', 'Publisher')) {
    if ($null -eq $PackageEvidence.PSObject.Properties[$property]) { Fail "package evidence is missing '$property'" }
}
if ($PackageEvidence.Platform -cne $Platform -or $PackageEvidence.Configuration -cne 'Release' -or
    $PackageEvidence.UnsignedInstallable -isnot [bool] -or -not $PackageEvidence.UnsignedInstallable -or
    $PackageEvidence.Signed -isnot [bool] -or $PackageEvidence.Signed -or
    $PackageEvidence.HasAppxSignature -isnot [bool] -or $PackageEvidence.HasAppxSignature -or
    $PackageEvidence.Publisher -cne $expectedPublisher -or
    [string]$PackageEvidence.Sha256 -cnotmatch '^[0-9A-Fa-f]{64}$') {
    Fail 'evidence must describe the requested Release package with the dedicated unsigned development identity'
}
[xml] $props = Get-Content -LiteralPath (Join-Path $repositoryRoot 'Directory.Build.props') -Raw
$versions = @($props.Project.PropertyGroup.AsukaVersion | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
if ($versions.Count -ne 1 -or [string]$PackageEvidence.Version -cne [string]$versions[0]) {
    Fail 'package version does not match the current source version'
}
$version = [string]$versions[0]
$packagePath = [IO.Path]::GetFullPath([string]$PackageEvidence.Package)
$runsRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts/msix/runs'))
$relativePackage = [IO.Path]::GetRelativePath($runsRoot, $packagePath)
if ([IO.Path]::IsPathRooted($relativePackage) -or $relativePackage -match '^\.\.(?:[\\/]|$)' -or
    [IO.Path]::GetFileName($packagePath) -cne "Asuka-$Platform-Release.msix") {
    Fail 'the exact package returned by package.ps1 must be inside artifacts/msix/runs'
}
$packageItem = Get-Item -LiteralPath $packagePath -ErrorAction Stop
if ($packageItem.PSIsContainer -or $packageItem.Length -le 0 -or $packageItem.Length -ne $PackageEvidence.Bytes) {
    Fail 'the returned package is empty or its size changed after packaging'
}
for ($path = $packagePath; $path -ine $runsRoot; $path = [IO.Path]::GetDirectoryName($path)) {
    if ((Get-Item -LiteralPath $path).Attributes -band [IO.FileAttributes]::ReparsePoint) {
        Fail 'package output must not traverse a symbolic link or junction'
    }
}

$outputRoot = [IO.Path]::GetFullPath($OutputDirectory, $repositoryRoot)
$artifactDirectory = Join-Path $outputRoot $artifactName
if (Test-Path -LiteralPath $artifactDirectory) { Fail 'artifact directory already exists; refusing to mix outputs from separate runs' }

# Keep the exact source package immutable while verifying and copying it. This
# consumes one build's evidence; no newest-file search or old-output fallback.
$source = [IO.FileStream]::new($packagePath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
try {
    $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($source))
    if ($hash -ine $PackageEvidence.Sha256) { Fail 'package SHA-256 changed after packaging' }
    Add-Type -AssemblyName System.IO.Compression
    $archive = [IO.Compression.ZipFile]::OpenRead($packagePath)
    try {
        if (@($archive.Entries | Where-Object { $_.FullName -ieq 'AppxSignature.p7x' }).Count -ne 0) {
            Fail 'development package unexpectedly contains a publisher signature'
        }
        $manifests = @($archive.Entries | Where-Object { $_.FullName -ceq 'AppxManifest.xml' })
        if ($manifests.Count -ne 1) { Fail 'expected exactly one package manifest' }
        $reader = [IO.StreamReader]::new($manifests[0].Open())
        try { [xml] $manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
        $namespaces = [Xml.XmlNamespaceManager]::new($manifest.NameTable)
        $namespaces.AddNamespace('f', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10')
        $identity = $manifest.SelectSingleNode('/f:Package/f:Identity', $namespaces)
        $application = $manifest.SelectSingleNode('/f:Package/f:Applications/f:Application', $namespaces)
        $windows = $manifest.SelectSingleNode('/f:Package/f:Dependencies/f:TargetDeviceFamily[@Name="Windows.Desktop"]', $namespaces)
        if ($null -eq $identity -or $identity.Name -cne 'Asuka.Windows' -or $identity.Publisher -cne $expectedPublisher -or
            $identity.ProcessorArchitecture -cne $architecture -or $identity.Version -cne $version -or
            $null -eq $application -or $application.EntryPoint -cne 'Windows.FullTrustApplication' -or
            $null -eq $windows -or $windows.MinVersion -cne '10.0.26100.0') {
            Fail 'package manifest does not match the verified development identity, architecture, version, or Windows baseline'
        }
    }
    finally { $archive.Dispose() }

    New-Item -ItemType Directory -Path $artifactDirectory -ErrorAction Stop | Out-Null
    $source.Position = 0
    $destination = [IO.FileStream]::new((Join-Path $artifactDirectory $msixName), [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $source.CopyTo($destination) } finally { $destination.Dispose() }
}
finally { $source.Dispose() }

Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'install-unsigned.ps1') -Destination $artifactDirectory -ErrorAction Stop
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'LICENSE') -Destination $artifactDirectory -ErrorAction Stop
$readme = @'
Asuka Windows ARCHITECTURE unsigned development package

Built from commit SOURCE_COMMIT; package version PACKAGE_VERSION.
Source: https://github.com/SOURCE_REPOSITORY/tree/SOURCE_COMMIT
Repository: https://github.com/SOURCE_REPOSITORY
Asuka is licensed under AGPL-3.0-or-later; see the included LICENSE.
This CI artifact is for developer preview/testing on Windows 11 24H2
(build 26100) or newer. Choose x64 for Intel/AMD Windows or arm64 for ARM Windows.
The application and its .NET/Windows App SDK runtimes are self-contained.

1. Download this artifact from the expected repository's successful CI run.
   Extract the complete ZIP into a new folder. In PowerShell, enter that folder.
   Check every file before executing the bundled installation script:

   Get-Content .\SHA256SUMS.txt | ForEach-Object {
       $expected, $name = $_ -split '  ', 2
       if ((Get-FileHash -LiteralPath $name -Algorithm SHA256).Hash -ine $expected) {
           throw "SHA-256 mismatch: $name"
       }
   }
   $packageHash = ((Get-Content .\SHA256SUMS.txt | Where-Object { $_ -match '\.msix$' }) -split '\s+', 2)[0]
   .\install-unsigned.ps1 -Package .\PACKAGE_NAME -ExpectedSha256 $packageHash -VerifyOnly

2. To install, open PowerShell as Administrator and enter the extracted folder.
   Repeat the verification above, then run:

   .\install-unsigned.ps1 -Package .\PACKAGE_NAME -ExpectedSha256 $packageHash

   The script verifies the digest, identity, OS and architecture, copies the MSIX
   into protected staging, then invokes Add-AppxPackage -AllowUnsigned. It does
   not request elevation or install/trust any certificate. If downloaded-script
   policy blocks it, review the script and unblock that file with Unblock-File.

This package has NO publisher signature or publisher-trust assurance.
SHA256SUMS.txt detects file changes; it is not a publisher signature. Verify the
source commit and CI run before installing. Pull-request artifacts may contain
unmerged contributor code. No credentials or signing secrets are used by CI.

The dedicated AllowUnsigned publisher is:
CN=Asuka Development, OID.2.25.311729368913984317654407730594956997722=1
Its package family differs from signed Asuka releases. Do not use it as a signed
production update. Windows also refuses downgrades from a newer installed
development version; remove that development installation through Windows
Settings first if a downgrade is intended (removal can delete its local data).

The CI ZIP contains exactly this README-Windows.txt, install-unsigned.ps1,
LICENSE, PACKAGE_NAME and SHA256SUMS.txt. It contains no staging directories.
'@
$readme = $readme.Replace('ARCHITECTURE', $architecture).Replace('SOURCE_COMMIT', $SourceCommit.ToLowerInvariant()).Replace('SOURCE_REPOSITORY', $Repository).Replace('PACKAGE_VERSION', $version).Replace('PACKAGE_NAME', $msixName)
[IO.File]::WriteAllText((Join-Path $artifactDirectory 'README-Windows.txt'), $readme + "`n", [Text.UTF8Encoding]::new($false))
$payloadNames = @($msixName, 'install-unsigned.ps1', 'README-Windows.txt', 'LICENSE')
$checksums = foreach ($name in $payloadNames) {
    $digest = (Get-FileHash -LiteralPath (Join-Path $artifactDirectory $name) -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($name -ceq $msixName -and $digest -ine $hash) { Fail 'copied MSIX differs from its build evidence' }
    "$digest  $name"
}
[IO.File]::WriteAllText((Join-Path $artifactDirectory 'SHA256SUMS.txt'), ($checksums -join "`n") + "`n", [Text.UTF8Encoding]::new($false))
$expectedNames = @($payloadNames + 'SHA256SUMS.txt')
$entries = @(Get-ChildItem -LiteralPath $artifactDirectory -Force)
if ($entries.Count -ne $expectedNames.Count -or @($entries | Where-Object { $_.PSIsContainer -or $_.Name -cnotin $expectedNames -or $_.Length -le 0 }).Count -ne 0) {
    Fail 'artifact file list is not exact'
}
[pscustomobject]@{ ArtifactDirectory = $artifactDirectory; ArtifactName = $artifactName; Package = $msixName; Sha256 = $hash; SourceCommit = $SourceCommit }
