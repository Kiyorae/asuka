[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = Resolve-Path (Join-Path $PSScriptRoot '..')

Push-Location $repositoryRoot
try {
    dotnet restore Matcha.slnx -p:Platform=x64
    if ($LASTEXITCODE -ne 0) { throw 'dotnet restore failed.' }

    $forbidden = & rg -n --glob '*.cs' --glob '*.xaml' --glob '*.csproj' --glob '!**/obj/**' --glob '!**/bin/**' '(WebView2?|BlazorWebView|CefSharp|Electron)' src
    $searchExitCode = $LASTEXITCODE
    if ($searchExitCode -eq 0) {
        $forbidden | Write-Error
        throw 'Browser-engine references are forbidden in application source.'
    }
    if ($searchExitCode -ne 1) { throw 'Unable to audit browser-engine references.' }

    dotnet format Matcha.slnx --verify-no-changes --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'dotnet format verification failed.' }

    dotnet restore src/Matcha.App/Matcha.App.csproj -p:Platform=ARM64
    if ($LASTEXITCODE -ne 0) { throw 'ARM64 restore failed.' }

    dotnet build src/Matcha.App/Matcha.App.csproj --configuration Release --no-restore -p:Platform=ARM64 -nodeReuse:false
    if ($LASTEXITCODE -ne 0) { throw 'ARM64 app build failed.' }

    # Restore x64 last so a subsequent --no-restore developer build uses the
    # default desktop architecture rather than the cross-compiled assets file.
    dotnet restore Matcha.slnx -p:Platform=x64
    if ($LASTEXITCODE -ne 0) { throw 'x64 restore failed after ARM64 validation.' }

    dotnet build Matcha.slnx --no-restore -p:Platform=x64
    if ($LASTEXITCODE -ne 0) { throw 'dotnet build failed.' }

    dotnet test tests/Matcha.Tests/Matcha.Tests.csproj --no-build --no-restore -p:Platform=x64
    if ($LASTEXITCODE -ne 0) { throw 'dotnet test failed.' }
}
finally {
    Pop-Location
}
