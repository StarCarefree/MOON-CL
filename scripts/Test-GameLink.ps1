#Requires -Version 7.0
[CmdletBinding()]
param([string]$DotNetPath = '', [switch]$SkipLauncherBuild, [switch]$CheckMappings)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot
$candidates = @($DotNetPath, $(if ($env:DOTNET_ROOT) { Join-Path $env:DOTNET_ROOT 'dotnet.exe' }), $((Get-Command dotnet -ErrorAction SilentlyContinue).Source))
$dotnet = $candidates | Where-Object { $_ -and (Test-Path $_) -and (Test-Path (Join-Path (Split-Path $_) 'sdk')) } | Select-Object -First 1
if (!$dotnet) { throw 'Provide a .NET 10 SDK via -DotNetPath.' }
$env:DOTNET_ROOT = Split-Path $dotnet
$base = if ($env:PI_SCRATCH_DIR) { $env:PI_SCRATCH_DIR } else { [IO.Path]::GetTempPath() }
$scratch = Join-Path $base ('game-link-check-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $scratch | Out-Null
function Check-Exit([string]$Step) { if ($LASTEXITCODE -ne 0) { throw "$Step failed; reports: $scratch" } }
& (Join-Path $repo 'tools\multiplayer-agent\build.ps1')
if (!$SkipLauncherBuild) {
    & $dotnet build (Join-Path $repo 'Plain Craft Launcher 2\Plain Craft Launcher 2.csproj') --no-restore -c Debug -v minimal *> (Join-Path $scratch 'launcher-build.log')
    Check-Exit 'Launcher build'
}
$previous = $env:MOONCL_TEST_LINK_MAPPINGS
try {
    if ($CheckMappings) { $env:MOONCL_TEST_LINK_MAPPINGS = '1' }
    else { $env:MOONCL_TEST_LINK_MAPPINGS = '' }
    & $dotnet test (Join-Path $repo 'PCL.Core.Test\PCL.Core.Test.csproj') --no-restore --filter 'FullyQualifiedName~GameLink' --logger 'trx;LogFileName=game-link.trx' --results-directory $scratch *> (Join-Path $scratch 'core-tests.log')
    Get-Content (Join-Path $scratch 'core-tests.log') -Tail 8
    Check-Exit 'Game link core tests'
} finally { $env:MOONCL_TEST_LINK_MAPPINGS = $previous }
& $dotnet build (Join-Path $repo 'scripts\room-ui-smoke\Room.UiSmoke.csproj') -p:NuGetAudit=false -v minimal *> (Join-Path $scratch 'ui-build.log')
Check-Exit 'UI smoke build'
& $dotnet (Join-Path $repo 'scripts\room-ui-smoke\bin\Debug\net10.0-windows\Room.UiSmoke.dll') 'http://127.0.0.1:1' $scratch (Join-Path $scratch 'screenshots') '--game-link-only' *> (Join-Path $scratch 'ui.log')
Get-Content (Join-Path $scratch 'ui.log') -Tail 15
Check-Exit 'Game link UI smoke'
Write-Output "GAME_LINK_CHECK_PASS · reports: $scratch"
$global:LASTEXITCODE = 0
