[CmdletBinding()]
param(
    [string] $RepositoryRoot = (Join-Path $PSScriptRoot '..')
)

$ErrorActionPreference = 'Stop'
Push-Location (Resolve-Path -LiteralPath $RepositoryRoot).Path
try {
    $browserReferences = & rg -n --glob '*.cs' --glob '*.xaml' --glob '*.csproj' --glob '!**/obj/**' --glob '!**/bin/**' '(WebView2?|BlazorWebView|CefSharp|Electron)' src
    $searchExitCode = $LASTEXITCODE
    if ($searchExitCode -notin 0, 1) { throw 'Unable to audit browser-engine references.' }

    # These complete license-attribution lines do not host a browser. All other
    # matches, including additional code in ComponentLicenses.cs, are forbidden.
    $auditedLicenseLines = @(
        '"Microsoft WebView2 SDK (transitive Windows App SDK payload)",'
        'new Uri("https://www.nuget.org/packages/Microsoft.Web.WebView2/1.0.3719.77/license")),'
    )
    $forbidden = @($browserReferences | Where-Object {
        if ($_ -cmatch '^src[\\/]Asuka\.App[\\/]ComponentLicenses\.cs:[0-9]+:(?<source>.*)$') {
            return $auditedLicenseLines -cnotcontains $Matches.source.Trim()
        }
        return $true
    })
    if ($forbidden.Count -gt 0) {
        throw "Browser-engine references are forbidden in application source:`n$($forbidden -join "`n")"
    }
    Write-Host 'Native UI source audit passed.'
    # rg uses 1 for no matches; a successful standalone CI audit must exit 0.
    $global:LASTEXITCODE = 0
}
finally { Pop-Location }
