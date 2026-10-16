#Requires -Version 7.0
[CmdletBinding()]
param(
    [string]$DotNetPath = '',
    [string]$EasyTierPath = '',
    [string]$JarPath = '',
    [string]$ScreenshotDirectory = '',
    [switch]$SkipLauncherBuild,
    [switch]$SkipNetworkTests
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot
$candidates = @($DotNetPath, $(if ($env:DOTNET_ROOT) { Join-Path $env:DOTNET_ROOT 'dotnet.exe' }), $((Get-Command dotnet -ErrorAction SilentlyContinue).Source))
$dotnet = $candidates | Where-Object { $_ -and (Test-Path $_) -and (Test-Path (Join-Path (Split-Path $_) 'sdk')) } | Select-Object -First 1
if (!$dotnet) { throw 'Specify -DotNetPath pointing to a .NET 10 SDK, not a runtime-only host.' }
$env:DOTNET_ROOT = Split-Path $dotnet
if ($EasyTierPath) { $env:MOONCL_TEST_EASYTIER_CORE = (Resolve-Path $EasyTierPath).Path }
$scratchBase = if ($env:PI_SCRATCH_DIR) { $env:PI_SCRATCH_DIR } else { [IO.Path]::GetTempPath() }
$scratch = Join-Path $scratchBase ('rooms-check-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $scratch | Out-Null
if (!$ScreenshotDirectory) { $ScreenshotDirectory = Join-Path $scratch 'screenshots' }
if (!$JarPath) { $JarPath = Join-Path $repo 'mooncl-cloud\build\libs\mooncl-cloud-1.0.0.jar' }
if (!(Test-Path $JarPath)) { throw 'Build mooncl-cloud bootJar first; the coordinator artifact is missing.' }
function Check-Exit([string]$Step) { if ($LASTEXITCODE -ne 0) { throw "$Step failed (exit=$LASTEXITCODE). See $scratch" } }
if (!$SkipLauncherBuild) {
    & $dotnet build (Join-Path $repo 'Plain Craft Launcher 2\Plain Craft Launcher 2.csproj') --no-restore -c Debug -p:Platform=x64 -p:UseSharedCompilation=false *> (Join-Path $scratch 'launcher-build.log')
    Check-Exit 'Launcher build'
}
if (!$SkipNetworkTests) {
    & $dotnet test (Join-Path $repo 'scripts\room-tests\Room.Network.Test.csproj') -p:NuGetAudit=false -p:UseSharedCompilation=false --logger 'trx;LogFileName=network.trx' --results-directory $scratch *> (Join-Path $scratch 'network-tests.log')
    Get-Content (Join-Path $scratch 'network-tests.log') -Tail 8
    Check-Exit 'Network tests'
}
& $dotnet build (Join-Path $repo 'scripts\room-ui-smoke\Room.UiSmoke.csproj') -p:NuGetAudit=false -p:UseSharedCompilation=false *> (Join-Path $scratch 'ui-build.log')
Check-Exit 'UI smoke build'
function Free-Port {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    try { $listener.Start(); return $listener.LocalEndpoint.Port } finally { $listener.Stop() }
}
$publicPort = Free-Port; $adminPort = Free-Port
while ($adminPort -eq $publicPort) { $adminPort = Free-Port }
$java = (Get-Command java -ErrorAction Stop).Source
$start = [Diagnostics.ProcessStartInfo]::new($java)
$start.UseShellExecute = $false; $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
$start.ArgumentList.Add('-Xms32m'); $start.ArgumentList.Add('-Xmx128m')
$start.ArgumentList.Add('-jar'); $start.ArgumentList.Add((Resolve-Path $JarPath).Path)
$start.Environment['MOONCL_PUBLIC_BIND'] = '127.0.0.1'
$start.Environment['MOONCL_PUBLIC_PORT'] = "$publicPort"
$start.Environment['MOONCL_ADMIN_PORT'] = "$adminPort"
$start.Environment['MOONCL_ADMIN_BIND'] = '127.0.0.1'
$start.Environment['MOONCL_ADMIN_TOKEN'] = [Guid]::NewGuid().ToString('N')
$start.Environment['MOONCL_DATA_DIR'] = Join-Path $scratch 'coordinator-data'
$process = [Diagnostics.Process]::Start($start)
$stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()
try {
    $ready = $false
    for ($i = 0; $i -lt 40; $i++) {
        if ($process.HasExited) { throw 'Isolated coordinator exited; see the server log.' }
        try { if ((Invoke-WebRequest "http://127.0.0.1:$publicPort/healthz" -TimeoutSec 1).StatusCode -eq 200) { $ready = $true; break } } catch { }
        Start-Sleep -Milliseconds 250
    }
    if (!$ready) { throw 'Isolated coordinator readiness timeout.' }
    $ui = Join-Path $repo 'scripts\room-ui-smoke\bin\Debug\net10.0-windows\Room.UiSmoke.dll'
    & $dotnet $ui "http://127.0.0.1:$publicPort" $scratch $ScreenshotDirectory *> (Join-Path $scratch 'ui-http.log')
    $code = $LASTEXITCODE
    Get-Content (Join-Path $scratch 'ui-http.log') -Tail 20
    if ($code -ne 0) { throw "UI HTTP smoke failed; see $scratch" }
    Write-Output "ROOM_CHECK_PASS · coordinator artifact: $JarPath"
    Write-Output "Reports: $scratch"
} finally {
    if (!$process.HasExited) { $process.Kill($true); $process.WaitForExit(5000) | Out-Null }
    [IO.File]::WriteAllText((Join-Path $scratch 'coordinator.log'), $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult())
    $process.Dispose()
}
