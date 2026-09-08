[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = Resolve-Path (Join-Path $PSScriptRoot '..')

Push-Location $repositoryRoot
try {
    dotnet restore Asuka.slnx -p:Platform=x64
    if ($LASTEXITCODE -ne 0) { throw 'dotnet restore failed.' }

    & (Join-Path $PSScriptRoot 'check-native-ui.ps1') -RepositoryRoot $repositoryRoot

    dotnet format Asuka.slnx --verify-no-changes --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'dotnet format verification failed.' }

    dotnet restore src/Asuka.App/Asuka.App.csproj -p:Platform=ARM64
    if ($LASTEXITCODE -ne 0) { throw 'ARM64 restore failed.' }

    dotnet build src/Asuka.App/Asuka.App.csproj --configuration Release --no-restore -p:Platform=ARM64 -nodeReuse:false
    if ($LASTEXITCODE -ne 0) { throw 'ARM64 app build failed.' }

    # Restore x64 last so a subsequent --no-restore developer build uses the
    # default desktop architecture rather than the cross-compiled assets file.
    dotnet restore Asuka.slnx -p:Platform=x64
    if ($LASTEXITCODE -ne 0) { throw 'x64 restore failed after ARM64 validation.' }

    dotnet build Asuka.slnx --no-restore -p:Platform=x64
    if ($LASTEXITCODE -ne 0) { throw 'dotnet build failed.' }

    dotnet test tests/Asuka.Tests/Asuka.Tests.csproj --no-build --no-restore -p:Platform=x64
    if ($LASTEXITCODE -ne 0) { throw 'dotnet test failed.' }
}
finally {
    Pop-Location
}
