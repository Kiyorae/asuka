[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $OutputDirectory,
    [ValidateSet('x64', 'arm64')]
    [string] $Architecture = 'x64',
    [switch] $GenerateTestFixture
)

$ErrorActionPreference = 'Stop'
$sourceDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../native/Asuka.SilkDecoder'))
$buildDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $buildDirectory -Force | Out-Null
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
if (!(Test-Path -LiteralPath $vswhere)) { throw 'Building the bundled SILK decoder requires the Visual Studio C++ build tools.' }
$visualStudio = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (!$visualStudio) { throw 'Install the Visual Studio Desktop development with C++ workload to build the bundled SILK decoder.' }
Import-Module (Join-Path $visualStudio 'Common7/Tools/Microsoft.VisualStudio.DevShell.dll')
Enter-VsDevShell -VsInstallPath $visualStudio -SkipAutomaticLocation -DevCmdArguments "-arch=$Architecture -host_arch=x64" | Out-Null

$entryPoint = if ($GenerateTestFixture) { 'fixtures/generate-tone.c' } else { 'decoder.c' }
$executable = if ($GenerateTestFixture) { 'asuka-silk-fixture.exe' } else { 'asuka-silk-decoder.exe' }
$compilerArguments = @(
    '/nologo', '/O2', '/MT', '/GS', '/guard:cf', '/DNDEBUG', '/DNO_ASM',
    '/D_CRT_SECURE_NO_WARNINGS', '/D_CRT_NONSTDC_NO_WARNINGS',
    ('/I"' + (Join-Path $sourceDirectory 'vendor/interface') + '"'),
    ('/I"' + (Join-Path $sourceDirectory 'vendor/src') + '"'),
    ('"' + (Join-Path $sourceDirectory $entryPoint) + '"')
)
$compilerArguments += Get-ChildItem -LiteralPath (Join-Path $sourceDirectory 'vendor/src') -Filter '*.c' |
    Sort-Object Name | ForEach-Object { '"' + $_.FullName + '"' }
$compilerArguments += @('/Fe:' + $executable, '/link', '/DYNAMICBASE', '/NXCOMPAT', '/OPT:REF', '/OPT:ICF', '/Brepro')
$responseFile = Join-Path $buildDirectory 'compile.rsp'
[IO.File]::WriteAllLines($responseFile, $compilerArguments)
Push-Location $buildDirectory
try {
    $buildLog = & cl.exe "@$responseFile" 2>&1
    $buildLog | Set-Content -LiteralPath (Join-Path $buildDirectory 'build.log')
    if ($LASTEXITCODE -ne 0) { throw "SILK decoder build failed: $($buildLog -join [Environment]::NewLine)" }
}
finally { Pop-Location }
Write-Output (Join-Path $buildDirectory $executable)
