#Requires -Version 7.0
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$failed = 0
function Assert-True([string]$Name, [bool]$Condition) {
    if ($Condition) { Write-Host "OK   $Name" }
    else { Write-Host "FAIL $Name"; $script:failed++ }
}

. (Join-Path $PSScriptRoot 'Publish-SingleExe.ps1') -Architecture x64
$testRoot = Join-Path (Get-ScratchRoot) ('single-exe-cache-test-' + [guid]::NewGuid().ToString('N'))
$originalRepo = $script:RepoRoot
function Invoke-TestDotNet {
    $script:CapturedArgs = @($args)
    $global:LASTEXITCODE = 0
}
try {
    $script:ScratchRoot = $testRoot
    $script:WorkRoot = Join-Path $testRoot 'work-1'
    Invoke-DotNetPublish 'Invoke-TestDotNet' (Join-Path $script:WorkRoot 'publish')
    $first = @($script:CapturedArgs | Where-Object { $_ -like '-p:OutputPath=*' })[0]
    Assert-True 'build-servers-enabled-by-default' ($script:CapturedArgs -notcontains '--disable-build-servers')
    $script:WorkRoot = Join-Path $testRoot 'work-2'
    Invoke-DotNetPublish 'Invoke-TestDotNet' (Join-Path $script:WorkRoot 'publish')
    $second = @($script:CapturedArgs | Where-Object { $_ -like '-p:OutputPath=*' })[0]
    Assert-True 'output-stable-between-process-work-directories' ($first -eq $second)
    $script:Architecture = 'arm64'
    Invoke-DotNetPublish 'Invoke-TestDotNet' (Join-Path $testRoot 'publish')
    Assert-True 'architectures-isolated' ($script:CapturedArgs -notcontains $first)
    $script:Architecture = 'x64'
    $script:Configuration = 'Beta'
    Invoke-DotNetPublish 'Invoke-TestDotNet' (Join-Path $testRoot 'publish')
    Assert-True 'configurations-isolated' ($script:CapturedArgs -notcontains $first)
    $script:Configuration = 'Release'
    $script:ReadyToRun = $true
    Invoke-DotNetPublish 'Invoke-TestDotNet' (Join-Path $testRoot 'publish')
    Assert-True 'ready-to-run-isolated' ($script:CapturedArgs -notcontains $first)
    $script:ReadyToRun = $false
    $script:RepoRoot = Join-Path $testRoot 'other-repo'
    Invoke-DotNetPublish 'Invoke-TestDotNet' (Join-Path $testRoot 'publish')
    Assert-True 'repositories-isolated' ($script:CapturedArgs -notcontains $first)
    $script:RepoRoot = $originalRepo

    Assert-True 'does-not-skip-build-or-restore' ($script:CapturedArgs -notcontains '--no-build' -and $script:CapturedArgs -notcontains '--no-restore')
    $cacheDir = Get-BuildOutputDirectory
    New-Item -ItemType Directory -Path $cacheDir, $script:WorkRoot -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $cacheDir 'keep.txt'), 'cached build')
    [IO.File]::WriteAllText((Join-Path $script:WorkRoot 'payload.zip'), 'temporary payload')
    Clear-SingleExeWork
    Assert-True 'cleanup-preserves-build-output' (Test-Path -LiteralPath (Join-Path $cacheDir 'keep.txt'))
    Assert-True 'cleanup-removes-packaging-files' (-not (Test-Path -LiteralPath $script:WorkRoot))
    $script:NoBuildCache = $true
    Assert-True 'cache-opt-out-uses-temporary-output' ((Get-BuildOutputDirectory) -eq (Join-Path $script:WorkRoot 'build\'))
    $script:NoBuildCache = $false
    $script:DisableBuildServers = $true
    Invoke-DotNetPublish 'Invoke-TestDotNet' (Join-Path $testRoot 'publish')
    Assert-True 'build-server-opt-out' ($script:CapturedArgs -contains '--disable-build-servers')
    $script:DisableBuildServers = $false

    # Both versions exist; lexical sorting would incorrectly select 10.0.9.
    $fakeDotNet = Join-Path $testRoot 'sdk-host\dotnet.ps1'
    foreach ($version in @('10.0.9', '10.0.100')) {
        $csc = Join-Path (Split-Path $fakeDotNet) "sdk\$version\Roslyn\bincore\csc.dll"
        New-Item -ItemType Directory -Path (Split-Path $csc) -Force | Out-Null
        [IO.File]::WriteAllText($csc, '')
    }
    [IO.File]::WriteAllText($fakeDotNet, "`$global:LASTEXITCODE = 0; '10.0.100'")
    $selected = Find-RoslynCsc $fakeDotNet
    Assert-True 'roslyn-matches-selected-sdk' ($selected -eq (Join-Path (Split-Path $fakeDotNet) 'sdk\10.0.100\Roslyn\bincore\csc.dll'))
    Remove-Item -LiteralPath $selected
    $rejected = $false
    try { Find-RoslynCsc $fakeDotNet | Out-Null } catch { $rejected = $true }
    Assert-True 'missing-selected-sdk-does-not-use-another-compiler' $rejected
}
finally {
    $script:RepoRoot = $originalRepo
    if (Test-Path -LiteralPath $testRoot) { Remove-Item -LiteralPath $testRoot -Recurse -Force }
}
if ($failed -gt 0) { Write-Host "FAILED $failed"; exit 1 }
Write-Host 'ALL PASSED'
