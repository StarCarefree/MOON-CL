param(
    [Parameter(Mandatory)][string]$MlsJar,
    [Parameter(Mandatory)][string]$FabricLoaderJar,
    [string]$JavaHome = $env:JAVA_HOME,
    [string]$OutputDirectory = (Join-Path $env:PI_SCRATCH_DIR 'mls-real-ipc')
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$java = if ($JavaHome) { Join-Path $JavaHome 'bin\java.exe' } else { (Get-Command java).Source }
$javac = if ($JavaHome) { Join-Path $JavaHome 'bin\javac.exe' } else { (Get-Command javac).Source }
$work = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory $work -Force | Out-Null
& $javac --release 8 -Xlint:-options -encoding UTF-8 -d $work (Join-Path $root 'tools\loading-bridge\test\MlsIpcHarness.java')
if ($LASTEXITCODE -ne 0) { throw 'IPC harness compilation failed' }
$bridge = Join-Path $root 'Plain Craft Launcher 2\Resources\mooncl-mls-bridge.jar'
$info = [Diagnostics.ProcessStartInfo]::new($java)
$info.WorkingDirectory = $work; $info.UseShellExecute = $false
$info.RedirectStandardOutput = $true; $info.RedirectStandardError = $true
foreach ($arg in @("-javaagent:$bridge", '-cp', "$work;$MlsJar;$FabricLoaderJar", 'MlsIpcHarness')) { $info.ArgumentList.Add($arg) }
$path = Join-Path $work 'progress.json'
Remove-Item $path -ErrorAction SilentlyContinue
$info.Environment['MOONCL_MLS_PROGRESS_FILE'] = $path
$info.Environment['MOONCL_MLS_PROGRESS_TOKEN'] = 'harness-token'
$process = [Diagnostics.Process]::Start($info)
$out = $process.StandardOutput.ReadToEndAsync(); $err = $process.StandardError.ReadToEndAsync()
$observed = [Collections.Generic.HashSet[string]]::new()
try {
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    while (!$process.HasExited -and [DateTime]::UtcNow -lt $deadline) {
        if (Test-Path $path) { try { $null = $observed.Add([IO.File]::ReadAllText($path)) } catch [IO.IOException] { } }
        Start-Sleep -Milliseconds 30
    }
    if (!$process.HasExited) { throw 'MLS IPC test timed out' }
    if (Test-Path $path) { $null = $observed.Add([IO.File]::ReadAllText($path)) }
    $out.Result; $err.Result
    if ($process.ExitCode -ne 0) { throw "MLS harness failed: $($process.ExitCode)" }
    $observed | Set-Content (Join-Path $work 'observed.jsonl') -Encoding utf8
    $packets = @($observed | ForEach-Object { $_ | ConvertFrom-Json })
    if (!($packets | Where-Object { $_.current -eq 1 -and $_.total -eq 4 -and $_.detail.Contains('测试模组') -and $_.detail.Contains([char]0) })) { throw 'Entrypoint count / modified UTF round-trip failed' }
    if (!($packets | Where-Object { $_.current -eq 42 -and $_.total -eq 100 })) { throw 'Custom progress did not arrive' }
    if (!($packets | Where-Object status -eq 'ready')) { throw 'Completion packet 255 did not arrive' }
    if ($packets | Where-Object token -ne 'harness-token') { throw 'Wrong session token' }
    "PASS: real MLS JAR binary IPC -> native bridge; entrypoint 1/4, custom 42/100, Unicode/NUL, ready event"
} finally { if (!$process.HasExited) { $process.Kill($true) }; $process.Dispose() }
