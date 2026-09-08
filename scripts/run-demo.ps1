[CmdletBinding()]
param(
    [ValidateSet('Milky', 'OneBot11', 'OneBot12')] [string] $Protocol = 'Milky',
    [ValidateRange(1, 30)] [double] $IntervalSeconds = 3,
    [switch] $NoBuild
)

$ErrorActionPreference = 'Stop'
$demoRepository = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$demoProject = Join-Path $demoRepository 'src\Asuka.App\Asuka.App.csproj'
$demoExecutable = Join-Path $demoRepository 'src\Asuka.App\bin\x64\Release\net10.0-windows10.0.26100.0\win-x64\Asuka.exe'
if (-not $NoBuild) {
    & dotnet build $demoProject --configuration Release -p:Platform=x64 -nodeReuse:false
    if ($LASTEXITCODE -ne 0) { throw 'The demo build failed.' }
}
if (-not (Test-Path -LiteralPath $demoExecutable -PathType Leaf)) { throw "Build the app first: $demoExecutable" }
$demoInterval = $IntervalSeconds.ToString('0.###', [Globalization.CultureInfo]::InvariantCulture)
$demoArguments = @('--demo', "--demo-protocol=$($Protocol.ToLowerInvariant())", "--demo-interval=$demoInterval")
# This is the interactive demo window requested by the launcher, not a background helper.
Start-Process -FilePath $demoExecutable -ArgumentList $demoArguments -WorkingDirectory (Split-Path -Parent $demoExecutable) -WindowStyle Normal
