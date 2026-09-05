[CmdletBinding()]
param(
    [ValidateSet('x64', 'ARM64')]
    [string] $Platform = 'x64',

    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',

    [ValidatePattern('^(?:0|[1-9][0-9]{0,4})\.(?:0|[1-9][0-9]{0,4})\.(?:0|[1-9][0-9]{0,4})\.(?:0|[1-9][0-9]{0,4})$')]
    [ValidateScript({ @($_ -split '\.' | ForEach-Object { [uint32]::Parse($_) -le 65535 }) -notcontains $false })]
    [string] $Version = '0.1.0.1',

    [string] $OutputDirectory = 'artifacts/release'
)

$ErrorActionPreference = 'Stop'

function Fail([string] $Message) {
    throw "Release packaging failed: $Message"
}

function Get-FullPath([string] $Path, [string] $BasePath) {
    if ([IO.Path]::IsPathRooted($Path)) {
        return [IO.Path]::GetFullPath($Path)
    }

    return [IO.Path]::GetFullPath((Join-Path $BasePath $Path))
}

function Test-ChildPath([string] $ChildPath, [string] $ParentPath) {
    $child = [IO.Path]::GetFullPath($ChildPath)
    $parent = [IO.Path]::GetFullPath($ParentPath).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $prefix = $parent + [IO.Path]::DirectorySeparatorChar
    return $child.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)
}

function Copy-ReleaseFile([string] $SourcePath, [string] $RelativePath, [string] $StagePath) {
    if (-not (Test-Path -LiteralPath $SourcePath -PathType Leaf)) {
        Fail "release source '$SourcePath' was not found"
    }

    $destination = Join-Path $StagePath ($RelativePath -replace '/', [IO.Path]::DirectorySeparatorChar)
    $destinationDirectory = Split-Path -Parent $destination
    New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null
    Copy-Item -LiteralPath $SourcePath -Destination $destination -Force
    if (-not (Test-Path -LiteralPath $destination -PathType Leaf) -or (Get-Item -LiteralPath $destination).Length -le 0) {
        Fail "staged release file '$RelativePath' is missing or empty"
    }
}

function Write-ChecksumManifest([string] $StagePath, [string[]] $RelativePaths) {
    $lines = foreach ($relativePath in $RelativePaths) {
        $fullPath = Join-Path $StagePath ($relativePath -replace '/', [IO.Path]::DirectorySeparatorChar)
        if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
            Fail "cannot checksum missing staged file '$relativePath'"
        }

        $hash = (Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $relativePath"
    }
    [IO.File]::WriteAllText((Join-Path $StagePath 'SHA256SUMS'), ($lines -join "`n") + "`n", [Text.UTF8Encoding]::new($false))
}

function Test-ReleaseArchive([string] $ArchivePath, [string] $StagePath, [string[]] $ExpectedFiles) {
    Add-Type -AssemblyName System.IO.Compression
    $archive = [IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        $fileEntries = @($archive.Entries | Where-Object { -not $_.FullName.EndsWith('/') } | ForEach-Object { $_.FullName })
        $expectedEntries = @($ExpectedFiles + 'SHA256SUMS')
        $unexpected = @($fileEntries | Where-Object { $_ -notin $expectedEntries })
        $missing = @($expectedEntries | Where-Object { $_ -notin $fileEntries })
        if ($unexpected.Count -ne 0 -or $missing.Count -ne 0 -or $fileEntries.Count -ne $expectedEntries.Count) {
            Fail "archive file entries are not exact (missing: $($missing -join ', '); unexpected: $($unexpected -join ', '))"
        }

        foreach ($entryName in $expectedEntries) {
            $entry = $archive.GetEntry($entryName)
            if ($null -eq $entry -or $entry.Length -le 0) {
                Fail "archive entry '$entryName' is missing or empty"
            }
        }

        $checksumEntry = $archive.GetEntry('SHA256SUMS')
        $reader = [IO.StreamReader]::new($checksumEntry.Open())
        try { $checksumLines = @($reader.ReadToEnd().Trim().Split("`n", [StringSplitOptions]::RemoveEmptyEntries) | ForEach-Object { $_.TrimEnd("`r") }) }
        finally { $reader.Dispose() }
        if ($checksumLines.Count -ne $ExpectedFiles.Count) {
            Fail "SHA256SUMS contains $($checksumLines.Count) entries; expected $($ExpectedFiles.Count)"
        }

        foreach ($expectedFile in $ExpectedFiles) {
            $matchingLine = @($checksumLines | Where-Object { $_ -match ('^[0-9a-f]{64}  ' + [regex]::Escape($expectedFile) + '$') })
            if ($matchingLine.Count -ne 1) { Fail "SHA256SUMS has no exact entry for '$expectedFile'" }
            $expectedHash = $matchingLine[0].Substring(0, 64).ToUpperInvariant()
            $actualHash = (Get-FileHash -LiteralPath (Join-Path $StagePath ($expectedFile -replace '/', [IO.Path]::DirectorySeparatorChar)) -Algorithm SHA256).Hash
            if ($actualHash -cne $expectedHash) { Fail "SHA256SUMS does not match staged '$expectedFile'" }
        }
    } finally {
        $archive.Dispose()
    }
}

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$propsPath = Join-Path $repositoryRoot 'Directory.Build.props'
$packageScript = Join-Path $PSScriptRoot 'package.ps1'
$unsignedInstaller = Join-Path $PSScriptRoot 'install-unsigned.ps1'
$releaseInstaller = Join-Path $PSScriptRoot 'release\Install-Matcha.ps1'
$releaseReadme = Join-Path $PSScriptRoot 'release\README-Windows.txt'
$licensePath = Join-Path $repositoryRoot 'LICENSE'

if (-not (Test-Path -LiteralPath $propsPath -PathType Leaf)) { Fail 'Directory.Build.props was not found' }
$propsText = [IO.File]::ReadAllText($propsPath)
$versionMatches = [regex]::Matches($propsText, '<MatchaVersion>\s*([^<\s]+)\s*</MatchaVersion>')
if ($versionMatches.Count -ne 1) { Fail 'Directory.Build.props must contain exactly one MatchaVersion element' }
$declaredVersion = $versionMatches[0].Groups[1].Value
if ($declaredVersion -cne $Version) {
    Fail "requested Version '$Version' does not match Directory.Build.props MatchaVersion '$declaredVersion'"
}

$releaseOutput = Get-FullPath $OutputDirectory $repositoryRoot
New-Item -ItemType Directory -Path $releaseOutput -Force | Out-Null
if (-not (Test-Path -LiteralPath $releaseOutput -PathType Container)) { Fail "output directory '$releaseOutput' could not be created" }

$releaseArchitecture = $Platform.ToLowerInvariant()
$archiveName = "Matcha-v$Version-windows-$releaseArchitecture-unsigned.zip"
$archivePath = Join-Path $releaseOutput $archiveName
if (Test-Path -LiteralPath $archivePath) { Fail "refusing to overwrite existing release archive '$archivePath'" }

$runsRoot = Join-Path $repositoryRoot 'artifacts\release\runs'
New-Item -ItemType Directory -Path $runsRoot -Force | Out-Null
$timestamp = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ')
$nonce = [Guid]::NewGuid().ToString('N').Substring(0, 12)
$runRoot = Join-Path $runsRoot ("$Platform-$Configuration-$timestamp-$nonce")
if (-not (Test-ChildPath $runRoot $runsRoot)) { Fail 'refusing to create temporary directory outside artifacts/release/runs' }
New-Item -ItemType Directory -Path $runRoot -ErrorAction Stop | Out-Null
$stageRoot = Join-Path $runRoot 'stage'

try {
    New-Item -ItemType Directory -Path $stageRoot -ErrorAction Stop | Out-Null

    # package.ps1 owns its own isolated MSIX build output. Its returned record is
    # the only accepted source package for this release invocation.
    $packageOutput = @(& $packageScript -Platform $Platform -Configuration $Configuration -PackageVersion $Version -UnsignedInstallable)
    $packageRecords = @($packageOutput | Where-Object { $null -ne $_ -and $null -ne $_.PSObject.Properties['Package'] })
    if ($packageRecords.Count -ne 1) { Fail "package.ps1 returned $($packageRecords.Count) package records; expected exactly one" }
    $packageRecord = $packageRecords[0]
    if ($packageRecord.Platform -cne $Platform -or $packageRecord.Configuration -cne $Configuration -or $packageRecord.Version -cne $Version -or -not $packageRecord.UnsignedInstallable) {
        Fail 'package.ps1 result does not match the requested unsigned release build'
    }
    $generatedMsix = Get-FullPath ([string]$packageRecord.Package) $repositoryRoot
    if (-not (Test-Path -LiteralPath $generatedMsix -PathType Leaf) -or [IO.Path]::GetExtension($generatedMsix) -ine '.msix') {
        Fail "package.ps1 did not produce a usable MSIX at '$generatedMsix'"
    }

    $msixName = "Matcha-v$Version-windows-$releaseArchitecture.msix"
    $releaseFiles = @(
        $msixName,
        'install-unsigned.ps1',
        'Install-Matcha.ps1',
        'README-Windows.txt',
        'LICENSE'
    )
    Copy-ReleaseFile $generatedMsix $msixName $stageRoot
    Copy-ReleaseFile $unsignedInstaller 'install-unsigned.ps1' $stageRoot
    Copy-ReleaseFile $releaseInstaller 'Install-Matcha.ps1' $stageRoot
    Copy-ReleaseFile $releaseReadme 'README-Windows.txt' $stageRoot
    Copy-ReleaseFile $licensePath 'LICENSE' $stageRoot
    Write-ChecksumManifest $stageRoot $releaseFiles

    # Exercise the public wrapper in the inbox Windows PowerShell host used by
    # most manual installs. VerifyOnly is read-only and catches compatibility or
    # parameter-binding failures before an archive is published.
    $windowsPowerShell = Get-Command 'powershell.exe' -CommandType Application -ErrorAction Stop
    & $windowsPowerShell.Source -NoProfile -ExecutionPolicy Bypass -File (Join-Path $stageRoot 'Install-Matcha.ps1') -VerifyOnly
    if ($LASTEXITCODE -ne 0) { Fail 'Windows PowerShell 5.1 installer verification failed' }

    # Compress-Archive receives only the staged root entries, so release ZIPs
    # never include their random staging directory name.
    $archiveInputs = @(Get-ChildItem -LiteralPath $stageRoot -Force | ForEach-Object { $_.FullName })
    Compress-Archive -Path $archiveInputs -DestinationPath $archivePath -CompressionLevel Optimal -ErrorAction Stop
    if (-not (Test-Path -LiteralPath $archivePath -PathType Leaf) -or (Get-Item -LiteralPath $archivePath).Length -le 0) {
        Fail "release archive '$archivePath' was not created or is empty"
    }
    Test-ReleaseArchive $archivePath $stageRoot $releaseFiles

    [pscustomobject]@{
        Archive = (Resolve-Path -LiteralPath $archivePath).Path
        Sha256 = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash
        Platform = $Platform
        Version = $Version
    }
} finally {
    if (Test-Path -LiteralPath $runRoot -PathType Container) {
        if (-not (Test-ChildPath $runRoot $runsRoot)) { Fail "refusing to clean temporary directory outside artifacts/release/runs: '$runRoot'" }
        Remove-Item -LiteralPath $runRoot -Recurse -Force -ErrorAction Stop
    }
}
