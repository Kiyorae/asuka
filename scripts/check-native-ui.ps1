[CmdletBinding()]
param(
    [string] $RepositoryRoot = (Join-Path $PSScriptRoot '..')
)

$ErrorActionPreference = 'Stop'
Push-Location (Resolve-Path -LiteralPath $RepositoryRoot).Path
try {
    $pattern = '(WebView2?|BlazorWebView|CefSharp|Electron)'
    $ripgrep = Get-Command -Name rg -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -ne $ripgrep) {
        $browserReferences = & $ripgrep.Source -n --glob '*.cs' --glob '*.xaml' --glob '*.csproj' --glob '!**/obj/**' --glob '!**/bin/**' $pattern src
        $searchExitCode = $LASTEXITCODE
        if ($searchExitCode -notin 0, 1) { throw 'Unable to audit browser-engine references.' }
    } else {
        # Hosted Windows images do not guarantee ripgrep. Keep the same narrow
        # source scan and attribution allowlist with built-in PowerShell tools.
        $root = (Get-Location).Path
        $browserReferences = @(Get-ChildItem -LiteralPath src -Recurse -File |
            Where-Object { $_.Extension -in @('.cs', '.xaml', '.csproj') -and $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } |
            ForEach-Object {
                $relative = $_.FullName.Substring($root.Length).TrimStart([char[]]@('/', '\'))
                Select-String -LiteralPath $_.FullName -Pattern $pattern -CaseSensitive |
                    ForEach-Object { '{0}:{1}:{2}' -f $relative, $_.LineNumber, $_.Line }
            })
    }

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
