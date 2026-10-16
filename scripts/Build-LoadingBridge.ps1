param([string]$JavaHome = $env:JAVA_HOME)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$source = Join-Path $root 'tools\loading-bridge'
$workRoot = if ($env:PI_SCRATCH_DIR) { $env:PI_SCRATCH_DIR } else { [IO.Path]::GetTempPath() }
$work = Join-Path $workRoot ('mooncl-bridge-build-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work -Force | Out-Null
try {
    $javac = if ($JavaHome) { Join-Path $JavaHome 'bin\javac.exe' } else { (Get-Command javac).Source }
    $jar = if ($JavaHome) { Join-Path $JavaHome 'bin\jar.exe' } else { (Get-Command jar).Source }
    $sources = @(Get-ChildItem (Join-Path $source 'src') -Recurse -Filter '*.java' | ForEach-Object FullName)
    & $javac --release 8 -Xlint:-options -encoding UTF-8 -d $work @sources
    if ($LASTEXITCODE -ne 0) { throw 'Loading bridge compilation failed' }
    $output = Join-Path $root 'Plain Craft Launcher 2\Resources\mooncl-mls-bridge.jar'
    & $jar cfm $output (Join-Path $source 'MANIFEST.MF') -C $work .
    if ($LASTEXITCODE -ne 0) { throw 'Loading bridge packaging failed' }
    Get-FileHash $output -Algorithm SHA256
} finally { Remove-Item $work -Recurse -Force }
