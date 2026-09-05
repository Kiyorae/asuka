[CmdletBinding()]
param(
    [ValidateSet('x64', 'ARM64')] [string] $Platform = 'x64',
    [ValidateSet('Debug', 'Release')] [string] $Configuration = 'Release',
    [ValidatePattern('^(?:0|[1-9][0-9]{0,4})\.(?:0|[1-9][0-9]{0,4})\.(?:0|[1-9][0-9]{0,4})\.(?:0|[1-9][0-9]{0,4})$')]
    [ValidateScript({ @($_ -split '\.' | ForEach-Object { [uint32]::Parse($_) -le 65535 }) -notcontains $false })]
    [string] $PackageVersion = '0.1.0.1',
    [ValidatePattern('^[0-9A-Fa-f]{40,64}$')] [string] $CertificateThumbprint,
    [switch] $UnsignedInstallable
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$timestamp = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ')
$nonce = [Guid]::NewGuid().ToString('N').Substring(0, 12)
$outputRoot = Join-Path $repositoryRoot ("artifacts\msix\runs\{0}-{1}-{2}-{3}" -f $Platform, $Configuration, $timestamp, $nonce)
$stageRoot = Join-Path $outputRoot 'project'
$projectPath = Join-Path $stageRoot 'Asuka.App\Asuka.App.csproj'
$developmentPublisher = 'CN=Asuka Development'
# Microsoft-defined Windows 11 AllowUnsigned marker. This is deliberately a
# fixed value, not a project-specific OID.
$unsignedDevelopmentOid = 'OID.2.25.311729368913984317654407730594956997722=1'
$unsignedDevelopmentPublisher = "$developmentPublisher, $unsignedDevelopmentOid"
New-Item -ItemType Directory -Path $outputRoot | Out-Null

function Fail([string] $Message) { throw "MSIX verification failed: $Message" }

function Copy-SourceTree([string] $Name) {
    $source = Join-Path $repositoryRoot ("src\{0}" -f $Name)
    $destination = Join-Path $stageRoot $Name
    if (-not (Test-Path -LiteralPath $source -PathType Container)) { Fail "source project '$Name' was not found" }
    # Each invocation gets a new staging directory. No source or prior package is
    # ever read as output, and build artefacts are deliberately excluded.
    & robocopy $source $destination /E /XD bin obj artifacts /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -gt 7) { Fail "could not stage project '$Name' (robocopy exit code $LASTEXITCODE)" }
}

function Get-PackageEntries([string] $PackagePath) {
    Add-Type -AssemblyName System.IO.Compression
    $archive = [System.IO.Compression.ZipFile]::OpenRead($PackagePath)
    try { return @($archive.Entries | ForEach-Object { $_.FullName }) }
    finally { $archive.Dispose() }
}

function Read-ZipEntryText([string] $PackagePath, [string] $EntryName) {
    Add-Type -AssemblyName System.IO.Compression
    $archive = [System.IO.Compression.ZipFile]::OpenRead($PackagePath)
    try {
        $entry = $archive.GetEntry($EntryName)
        if ($null -eq $entry) { Fail "'$EntryName' was not found in $PackagePath" }
        $reader = [System.IO.StreamReader]::new($entry.Open())
        try { return $reader.ReadToEnd() } finally { $reader.Dispose() }
    } finally { $archive.Dispose() }
}

function Test-SelfContainedMsix([string] $MsixPath, [bool] $ExpectUnsignedInstallable) {
    $entries = Get-PackageEntries $MsixPath
    [xml]$manifest = Read-ZipEntryText $MsixPath 'AppxManifest.xml'
    $manager = [System.Xml.XmlNamespaceManager]::new($manifest.NameTable)
    $manager.AddNamespace('f', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10')
    $identity = $manifest.SelectSingleNode('/f:Package/f:Identity', $manager)
    $application = $manifest.SelectSingleNode('/f:Package/f:Applications/f:Application', $manager)
    $targetDeviceFamily = $manifest.SelectSingleNode('/f:Package/f:Dependencies/f:TargetDeviceFamily[@Name="Windows.Desktop"]', $manager)
    if ($null -eq $identity -or $identity.Name -ne 'Asuka.Windows') { Fail 'generated manifest identity is not Asuka.Windows' }
    $expectedPublisher = if ($ExpectUnsignedInstallable) { $unsignedDevelopmentPublisher } else { $developmentPublisher }
    if ($identity.Publisher -cne $expectedPublisher) { Fail "generated publisher '$($identity.Publisher)' does not match expected '$expectedPublisher'" }
    if ($identity.Version -ne $PackageVersion) { Fail "generated manifest version '$($identity.Version)' does not match requested '$PackageVersion'" }
    $expectedArchitecture = if ($Platform -eq 'ARM64') { 'arm64' } else { 'x64' }
    if ($identity.ProcessorArchitecture -ine $expectedArchitecture) { Fail "generated processor architecture '$($identity.ProcessorArchitecture)' does not match requested '$Platform'" }
    if ($null -eq $application -or $application.EntryPoint -ne 'Windows.FullTrustApplication') { Fail 'generated manifest does not use Windows.FullTrustApplication' }
    if ($null -eq $targetDeviceFamily -or $targetDeviceFamily.MinVersion -ne '10.0.26100.0') { Fail 'generated manifest does not require the supported Windows 11 24H2 baseline' }
    $hasSignature = @($entries | Where-Object { $_ -ieq 'AppxSignature.p7x' }).Count -gt 0
    if ($ExpectUnsignedInstallable -and $hasSignature) { Fail 'unsigned-installable package unexpectedly contains AppxSignature.p7x' }
    if ((-not $ExpectUnsignedInstallable) -and (-not $CertificateThumbprint) -and $hasSignature) { Fail 'unsigned verification package unexpectedly contains AppxSignature.p7x' }
    if ((-not $ExpectUnsignedInstallable) -and $CertificateThumbprint -and (-not $hasSignature)) { Fail 'signed package is missing AppxSignature.p7x' }

    $runtimeConfigs = @($entries | Where-Object { $_ -match '(?i)(?:^|/)Asuka\.runtimeconfig\.json$' })
    if ($runtimeConfigs.Count -ne 1) { Fail "expected one Asuka.runtimeconfig.json, found $($runtimeConfigs.Count)" }
    $runtimeConfigText = Read-ZipEntryText $MsixPath $runtimeConfigs[0]
    if ($runtimeConfigText -match '"frameworks"') { Fail 'runtimeconfig declares a framework dependency; package is not self-contained' }
    if ($runtimeConfigText -notmatch 'includedFrameworks') { Fail 'runtimeconfig has no includedFrameworks marker for a self-contained deployment' }
    foreach ($runtimeFile in @('coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'System.Private.CoreLib.dll')) {
        if (-not ($entries | Where-Object { $_ -ieq $runtimeFile })) { Fail "self-contained runtime file '$runtimeFile' is missing" }
    }
    [pscustomobject]@{ Identity = $identity; EntryCount = $entries.Count; RuntimeConfig = $runtimeConfigs[0]; HasSignature = $hasSignature }
}

if ($UnsignedInstallable -and $CertificateThumbprint) {
    throw '-UnsignedInstallable and -CertificateThumbprint are mutually exclusive.'
}

if ($CertificateThumbprint) {
    $certificate = Get-Item -LiteralPath ('Cert:\CurrentUser\My\' + $CertificateThumbprint) -ErrorAction Stop
    if ($certificate.Subject -cne $developmentPublisher) { throw "Certificate subject '$($certificate.Subject)' must match the MSIX publisher '$developmentPublisher'." }
    $signingArguments = @('-p:AppxPackageSigningEnabled=true', ("-p:PackageCertificateThumbprint={0}" -f $CertificateThumbprint))
} else {
    $signingArguments = @('-p:AppxPackageSigningEnabled=false')
    if ($UnsignedInstallable) {
        Write-Warning 'Building a Windows 11 AllowUnsigned preview package. It is unsigned, requires an elevated administrator install, and has a deliberately different package identity from signed releases. Microsoft recommends this path only for testing, not broad distribution.'
    } else {
        Write-Warning 'Building an unsigned verification package. It cannot be installed or distributed until signed by a certificate matching the manifest publisher.'
    }
}

Push-Location $repositoryRoot
try {
    Copy-SourceTree 'Asuka.Core'; Copy-SourceTree 'Asuka.Protocols'; Copy-SourceTree 'Asuka.App'
    $stagedManifestPath = Join-Path $stageRoot 'Asuka.App\Package.appxmanifest'
    $stagedManifest = [System.IO.File]::ReadAllText($stagedManifestPath, [System.Text.Encoding]::UTF8)
    $identityVersionPattern = '(<Identity\s+[\s\S]*?Version=")[^"]+(")'
    if (-not [regex]::IsMatch($stagedManifest, $identityVersionPattern)) { Fail 'could not locate Identity Version in staged Package.appxmanifest' }
    $updatedManifest = [regex]::Replace($stagedManifest, $identityVersionPattern, ('${1}' + $PackageVersion + '${2}'), 1)
    if ($UnsignedInstallable) {
        $publisherPattern = '(Publisher=")[^"]+(")'
        if (-not [regex]::IsMatch($updatedManifest, $publisherPattern)) { Fail 'could not locate Identity Publisher in staged Package.appxmanifest' }
        $updatedManifest = [regex]::Replace($updatedManifest, $publisherPattern, ('${1}' + $unsignedDevelopmentPublisher + '${2}'), 1)
    }
    [System.IO.File]::WriteAllText($stagedManifestPath, $updatedManifest, [System.Text.UTF8Encoding]::new($false))

    $buildArguments = @(
        'build', $projectPath, '--configuration', $Configuration, ("-p:Platform={0}" -f $Platform),
        '-p:GenerateAppxPackageOnBuild=true', '-p:AppxSymbolPackageEnabled=false', '-p:AppxBundle=Never',
        ("-p:AppxPackageDir={0}\\" -f $outputRoot), ("-p:AppxPackageName=Asuka-{0}-{1}" -f $Platform, $Configuration),
        '-p:WindowsAppSDKSelfContained=true', '-p:SelfContained=true', '-nodeReuse:false'
    ) + $signingArguments
    & dotnet @buildArguments
    if ($LASTEXITCODE -ne 0) { Fail 'MSIX build command failed' }
    $packages = @(Get-ChildItem -LiteralPath $outputRoot -File -Recurse -Filter '*.msix')
    if ($packages.Count -ne 1) { Fail "expected exactly one fresh .msix in isolated output, found $($packages.Count)" }
    $package = $packages[0]
    if ($package.Name -ne ("Asuka-{0}-{1}.msix" -f $Platform, $Configuration)) { Fail "package name '$($package.Name)' does not match platform/configuration '$Platform/$Configuration'" }
    if ($package.Length -le 0) { Fail 'package is empty' }
    $evidence = Test-SelfContainedMsix $package.FullName $UnsignedInstallable
    $sha256 = (Get-FileHash -LiteralPath $package.FullName -Algorithm SHA256).Hash
    [pscustomobject]@{
        Package = $package.FullName; Bytes = $package.Length; Platform = $Platform; Configuration = $Configuration
        Version = $evidence.Identity.Version; EntryPoint = 'Windows.FullTrustApplication'; RuntimeConfig = $evidence.RuntimeConfig
        ArchiveEntries = $evidence.EntryCount; Signed = $evidence.HasSignature
        Publisher = $evidence.Identity.Publisher; HasAppxSignature = $evidence.HasSignature
        UnsignedInstallable = [bool]$UnsignedInstallable; Sha256 = $sha256
    }
    if ($UnsignedInstallable) {
        Write-Warning 'This is an unsigned Windows 11 preview/testing package. Its Publisher/package family intentionally differs from signed Asuka releases; do not treat it as a production or broadly distributed release.'
        Write-Host ("Verify:  .\scripts\install-unsigned.ps1 -Package '{0}' -ExpectedSha256 {1} -VerifyOnly" -f $package.FullName, $sha256)
        Write-Host ("Install: .\scripts\install-unsigned.ps1 -Package '{0}' -ExpectedSha256 {1}" -f $package.FullName, $sha256)
    }
} finally { Pop-Location }
