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
$layout = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'single-exe\PackageLayout.ps1') -Raw

Assert-True 'fdd' ($publish.Contains("--self-contained', 'false'") -and $publish.Contains('SelfContained=false'))
Assert-True 'not-self-contained' (-not $publish.Contains('SelfContained=true') -and -not $publish.Contains("--self-contained', 'true'"))
Assert-True 'single-file' $publish.Contains('PublishSingleFile=true')
Assert-True 'no-trim' ($publish.Contains('PublishTrimmed=false') -and $publish.Contains('EnableTrimAnalyzer=false'))
Assert-True 'native-libs' $publish.Contains('IncludeNativeLibrariesForSelfExtract=true')
Assert-True 'no-inner-compression' $publish.Contains('EnableCompressionInSingleFile=false')
Assert-True 'missing-lib-guard' $publish.Contains('漏库')
Assert-True 'no-tail-magic' (-not $stub.Contains('MOONCLPKGv1'))
Assert-True 'no-footer-append' (-not $publish.Contains('Add-MoonclFooter') -and -not $stub.Contains('FooterSize'))
Assert-True 'embedded-resource' ($publish.Contains(',MOONCL.App') -and $stub.Contains('GetManifestResourceStream'))
Assert-True 'no-second-compression' $publish.Contains('CompressionLevel]::NoCompression')
Assert-True 'overlay-guard' ($publish.Contains('Assert-NoPeOverlay') -and $layout.Contains('Assert-NoPeOverlay'))
Assert-True 'dot-source-guard' $publish.Contains('$script:DotSource')
Assert-True 'consent-method' $stub.Contains('DownloadDesktopRuntimeAfterConsent')
Assert-True 'consent-required' $stub.Contains('consent required')
Assert-True 'agree-calls-download' $stub.Contains('_host.DownloadMissingAfterConsent(this, _missing)')
Assert-True 'no-accept-button' $stub.Contains('AcceptButton = null')
Assert-True 'bundle-extract-cache' $stub.Contains('DOTNET_BUNDLE_EXTRACT_BASE_DIR')
Assert-True 'clear-dotnet-root' $stub.Contains('ClearEnvAndLaunch')
Assert-True 'tiered-pgo' ($publish.Contains('TieredPGO=true') -and $stub.Contains('DOTNET_TieredPGO'))
Assert-True 'ready-to-run-default-on' ($publish.Contains("if (`$NoReadyToRun) { 'false' } else { 'true' }"))
 Assert-True 'no-r2r-bat' ((Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Publish-SingleExe-NoR2R.bat') -Raw).Contains('-NoReadyToRun'))
Assert-True 'quick-jit-for-loops' ($publish.Contains('TieredCompilationQuickJitForLoops=true') -and $stub.Contains('DOTNET_TC_QuickJitForLoops'))
Assert-True 'lists-missing' $stub.Contains('启动前缺少这些组件')
Assert-True 'names-vc-dll' $stub.Contains('vcruntime140.dll')
Assert-True 'vc-redist-url' $stub.Contains('vc_redist.x64.exe')
Assert-True 'webview-consent' $stub.Contains('LinkId=2124708')
Assert-True 'skip-does-not-download' ($stub.Contains('跳过并启动') -and $stub.Contains('private void OnSkip'))

$ctorStart = $stub.IndexOf('public ConsentForm(')
$ctorEnd = $stub.IndexOf('private static string Describe(')
$ctor = $stub.Substring($ctorStart, $ctorEnd - $ctorStart)
Assert-True 'ctor-does-not-download' (-not $ctor.Contains('Download'))

$tokens = $null
$errors = $null
[System.Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Publish-SingleExe.ps1'), [ref]$tokens, [ref]$errors) | Out-Null
Assert-True 'script-parses' ($errors.Count -eq 0)
$layoutErrors = $null
[System.Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'single-exe\PackageLayout.ps1'), [ref]$tokens, [ref]$layoutErrors) | Out-Null
Assert-True 'layout-parses' ($layoutErrors.Count -eq 0)

if ($failed -gt 0) {
    Write-Host "FAILED $failed"
    exit 1
}
Write-Host 'ALL PASSED'
exit 0
