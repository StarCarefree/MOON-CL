#Requires -Version 7.0
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptPath = Join-Path $PSScriptRoot 'Ensure-DotNet10.ps1'
. $scriptPath

$failed = 0
function Assert-Equal($Name, $Actual, $Expected) {
    if ($Actual -ne $Expected) {
        Write-Host "FAIL $Name actual=$Actual expected=$Expected"
        $script:failed++
    }
    else { Write-Host "OK   $Name" }
}

function Assert-Throws($Name, [scriptblock]$Action, [string]$Pattern) {
    try {
        & $Action
        Write-Host "FAIL $Name did not throw"
        $script:failed++
    }
    catch {
        if ($_.Exception.Message -notmatch $Pattern) {
            Write-Host "FAIL $Name message=$($_.Exception.Message)"
            $script:failed++
        }
        else { Write-Host "OK   $Name" }
    }
}

Assert-Equal 'plan-missing' (Resolve-DotNetLaunchPlan $false $false $false) 'NeedConsent'
Assert-Equal 'plan-registered' (Resolve-DotNetLaunchPlan $false $false $true) 'Launch'
Assert-Equal 'plan-env-ok' (Resolve-DotNetLaunchPlan $true $true $false) 'Launch'
Assert-Equal 'plan-env-hides' (Resolve-DotNetLaunchPlan $true $false $true) 'ClearEnvAndLaunch'
Assert-Equal 'plan-both-missing' (Resolve-DotNetLaunchPlan $true $false $false) 'NeedConsent'

Assert-Equal 'trust-builds' (Test-IsTrustedMicrosoftHost 'builds.dotnet.microsoft.com') $true
Assert-Equal 'trust-aka' (Test-IsTrustedMicrosoftHost 'aka.ms') $true
Assert-Equal 'trust-reject' (Test-IsTrustedMicrosoftHost 'example.com') $false

$good = @'
{
  "latest-release": "10.0.13",
  "releases": [
    {
      "release-version": "10.0.13",
      "windowsdesktop": {
        "version": "10.0.13",
        "files": [
          {
            "name": "windowsdesktop-runtime-win-x64.zip",
            "rid": "win-x64",
            "url": "https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/10.0.13/windowsdesktop-runtime-10.0.13-win-x64.zip",
            "hash": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
          },
          {
            "name": "windowsdesktop-runtime-win-x64.exe",
            "rid": "win-x64",
            "url": "https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/10.0.13/windowsdesktop-runtime-10.0.13-win-x64.exe",
            "hash": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
          }
        ]
      }
    }
  ]
}
'@
$spec = Get-DesktopInstallerSpecFromJson $good 'x64'
Assert-Equal 'spec-name' $spec.Name 'windowsdesktop-runtime-win-x64.exe'
Assert-Equal 'spec-not-zip' ($spec.Url.EndsWith('.exe')) $true
if ($spec.Url -match '\.zip') {
    Write-Host 'FAIL spec selected zip'
    $failed++
}
else { Write-Host 'OK   spec-url-exe' }

Assert-Throws 'reject-http' { Assert-MicrosoftHttpsUrl 'http://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/10.0.13/windowsdesktop-runtime-10.0.13-win-x64.exe' } 'HTTPS'
Assert-Throws 'reject-zip' { Assert-MicrosoftHttpsUrl 'https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/10.0.13/windowsdesktop-runtime-10.0.13-win-x64.zip' } 'zip'
 Assert-Throws 'reject-host' { Assert-DesktopInstallerUrl 'https://example.com/windowsdesktop-runtime-10.0.13-win-x64.exe' 'x64' } 'Untrusted host'
Assert-Throws 'reject-arm-on-x64' { Assert-DesktopInstallerUrl 'https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/10.0.13/windowsdesktop-runtime-10.0.13-win-arm64.exe' 'x64' } 'Unexpected'

$temp = Join-Path $env:PI_SCRATCH_DIR 'dotnet10-hash-test.bin'
[System.IO.File]::WriteAllBytes($temp, [byte[]](1, 2, 3, 4))
$hash = (Get-FileHash -LiteralPath $temp -Algorithm SHA512).Hash.ToLowerInvariant()
Assert-Sha512 $temp $hash
Write-Host 'OK   sha512-match'
 Assert-Throws 'sha512-mismatch' { Assert-Sha512 $temp ('ab' * 64) } 'SHA512 mismatch'

$publish = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Publish-Portable.ps1') -Raw
if ($publish -notmatch "--self-contained', 'false'" -or $publish -notmatch 'SelfContained=false') {
    Write-Host 'FAIL publish script is not framework-dependent'
    $failed++
}
else { Write-Host 'OK   publish-fdd' }
if ($publish -match "--self-contained', 'true'" -or $publish -match 'SelfContained=true') {
    Write-Host 'FAIL publish script still packages a self-contained runtime'
    $failed++
}
else { Write-Host 'OK   publish-not-self-contained' }
foreach ($name in @('Ensure-DotNet10.ps1', 'Start-PCL-CY.cmd', 'dotnet-runtime.json')) {
    if ($publish -notmatch [regex]::Escape($name)) {
        Write-Host "FAIL publish script does not ship $name"
        $failed++
    }
    else { Write-Host "OK   ships-$name" }
}

if ($failed -gt 0) {
    Write-Host "FAILED $failed"
    exit 1
}
Write-Host 'ALL PASSED'
exit 0
