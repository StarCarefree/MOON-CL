#Requires -Version 7.0
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$failed = 0
function Assert-True($Name, [bool]$Condition) {
    if (-not $Condition) {
        Write-Host "FAIL $Name"
        $script:failed++
    }
    else { Write-Host "OK   $Name" }
}

$publish = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Publish-SingleExe.ps1') -Raw
$stub = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'single-exe\Bootstrapper.cs') -Raw

Assert-True 'fdd' ($publish.Contains("--self-contained', 'false'") -and $publish.Contains('SelfContained=false'))
Assert-True 'not-self-contained' (-not $publish.Contains('SelfContained=true') -and -not $publish.Contains("--self-contained', 'true'"))
Assert-True 'single-file' $publish.Contains('PublishSingleFile=true')
Assert-True 'no-trim' ($publish.Contains('PublishTrimmed=false') -and $publish.Contains('EnableTrimAnalyzer=false'))
Assert-True 'native-libs' $publish.Contains('IncludeNativeLibrariesForSelfExtract=true')
Assert-True 'no-inner-compression' $publish.Contains('EnableCompressionInSingleFile=false')
Assert-True 'missing-lib-guard' $publish.Contains('漏库')
Assert-True 'magic' ($publish.Contains('MOONCLPKGv1') -and $stub.Contains('MOONCLPKGv1'))
Assert-True 'footer-80' ($publish.Contains('$script:FooterSize = 80') -and $stub.Contains('FooterSize = 80'))
Assert-True 'dot-source-guard' $publish.Contains('$script:DotSource')
Assert-True 'consent-method' $stub.Contains('DownloadDesktopRuntimeAfterConsent')
Assert-True 'consent-required' $stub.Contains('consent required')
Assert-True 'agree-calls-download' $stub.Contains('_host.DownloadDesktopRuntimeAfterConsent(this)')
Assert-True 'no-accept-button' $stub.Contains('AcceptButton = null')
Assert-True 'bundle-extract-cache' $stub.Contains('DOTNET_BUNDLE_EXTRACT_BASE_DIR')
Assert-True 'clear-dotnet-root' $stub.Contains('ClearEnvAndLaunch')
Assert-True 'tiered-pgo' ($publish.Contains('TieredPGO=true') -and $stub.Contains('DOTNET_TieredPGO'))

$ctor = $stub.Substring($stub.IndexOf('public ConsentForm('), 900)
Assert-True 'ctor-does-not-download' (-not $ctor.Contains('DownloadDesktopRuntimeAfterConsent'))

$tokens = $null
$errors = $null
[System.Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Publish-SingleExe.ps1'), [ref]$tokens, [ref]$errors) | Out-Null
Assert-True 'script-parses' ($errors.Count -eq 0)

if ($failed -gt 0) {
    Write-Host "FAILED $failed"
    exit 1
}
Write-Host 'ALL PASSED'
exit 0
