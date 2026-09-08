[CmdletBinding()]
param(
    [switch] $VerifyOnly,
    [switch] $SkipHostCompatibility
)

$ErrorActionPreference = 'Stop'

function Fail([string] $Message) {
    throw "Asuka release package refused: $Message"
}

if ($SkipHostCompatibility -and -not $VerifyOnly) {
    Fail '-SkipHostCompatibility is only available with -VerifyOnly; installation always checks the current device.'
}

function Get-ExactlyOneFile([string] $Directory, [string] $Filter, [string] $Description) {
    $files = @(
        Get-ChildItem -LiteralPath $Directory -File -Filter $Filter -ErrorAction Stop |
            Where-Object { $_.DirectoryName -ceq $Directory }
    )
    if ($files.Count -ne 1) {
        Fail "expected exactly one $Description, found $($files.Count)"
    }

    return $files[0]
}

$releaseDirectory = $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($releaseDirectory) -or -not (Test-Path -LiteralPath $releaseDirectory -PathType Container)) {
    Fail 'could not resolve the release directory'
}

$package = Get-ExactlyOneFile -Directory $releaseDirectory -Filter 'Asuka-*.msix' -Description 'Asuka-*.msix package'
$checksums = Get-ExactlyOneFile -Directory $releaseDirectory -Filter 'SHA256SUMS' -Description 'SHA256SUMS file'
$installer = Get-ExactlyOneFile -Directory $releaseDirectory -Filter 'install-unsigned.ps1' -Description 'install-unsigned.ps1 script'
$readme = Get-ExactlyOneFile -Directory $releaseDirectory -Filter 'README-Windows.txt' -Description 'README-Windows.txt file'
$license = Get-ExactlyOneFile -Directory $releaseDirectory -Filter 'LICENSE' -Description 'LICENSE file'
$wrapper = Get-Item -LiteralPath $PSCommandPath -ErrorAction Stop

$expectedFiles = @(
    $package,
    $installer,
    $wrapper,
    $readme,
    $license
)
$expectedNames = @($expectedFiles | ForEach-Object { $_.Name })
$actualNames = @(Get-ChildItem -LiteralPath $releaseDirectory -File -ErrorAction Stop | ForEach-Object { $_.Name })
$allowedNames = @($expectedNames + $checksums.Name)
$unexpectedNames = @($actualNames | Where-Object { $allowedNames -cnotcontains $_ })
$missingNames = @($allowedNames | Where-Object { $actualNames -cnotcontains $_ })
if ($actualNames.Count -ne $allowedNames.Count -or $unexpectedNames.Count -ne 0 -or $missingNames.Count -ne 0) {
    Fail "release directory contents are not exact (missing: $($missingNames -join ', '); unexpected: $($unexpectedNames -join ', '))"
}
foreach ($file in @($expectedFiles + $checksums)) {
    if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        Fail "release file '$($file.Name)' must not be a reparse point"
    }
}

$checksumRecords = @{}
foreach ($line in [System.IO.File]::ReadAllLines($checksums.FullName)) {
    if ([string]::IsNullOrWhiteSpace($line)) { continue }

    $match = [regex]::Match($line, '^(?<hash>[0-9A-Fa-f]{64})  (?<name>[^\\/]+)$', [System.Text.RegularExpressions.RegexOptions]::CultureInvariant)
    if (-not $match.Success) {
        Fail "SHA256SUMS contains an invalid checksum record: '$line'"
    }

    $recordName = $match.Groups['name'].Value
    if ($expectedNames -cnotcontains $recordName) {
        Fail "SHA256SUMS contains an unexpected filename '$recordName'"
    }
    if ($checksumRecords.ContainsKey($recordName)) {
        Fail "SHA256SUMS has a duplicate checksum for '$recordName'"
    }
    $checksumRecords.Add($recordName, $match.Groups['hash'].Value.ToUpperInvariant())
}

foreach ($expectedName in $expectedNames) {
    if (-not $checksumRecords.ContainsKey($expectedName)) {
        Fail "SHA256SUMS has no checksum for '$expectedName'"
    }
}

$lockedStreams = @()
try {
    foreach ($file in $expectedFiles) {
        $stream = [IO.FileStream]::new($file.FullName, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        $lockedStreams += $stream
        $sha = [Security.Cryptography.SHA256]::Create()
        try { $actualHash = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '') }
        finally { $sha.Dispose() }
        if ($actualHash -cne $checksumRecords[$file.Name]) {
            Fail "SHA-256 does not match SHA256SUMS for '$($file.Name)'"
        }
    }

    # The verified helper remains locked with FileShare.Read for the entire
    # invocation, preventing writes or deletion. Invoke the script file directly
    # for Windows PowerShell 5.1 compatibility and accurate nested error lines.
    if ($VerifyOnly) {
        & $installer.FullName -Package $package.FullName -ExpectedSha256 $checksumRecords[$package.Name] -VerifyOnly -SkipHostCompatibility:$SkipHostCompatibility
    } else {
        & $installer.FullName -Package $package.FullName -ExpectedSha256 $checksumRecords[$package.Name]
    }
} finally {
    foreach ($stream in $lockedStreams) { $stream.Dispose() }
}
