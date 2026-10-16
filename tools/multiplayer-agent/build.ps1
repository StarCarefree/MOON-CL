[CmdletBinding()]
param(
    [string]$OutputJar = (Join-Path $PSScriptRoot 'build\mooncl-multiplayer-agent.jar'),
    [string]$JavaHome = $env:JAVA_HOME
)
$ErrorActionPreference = 'Stop'
function JavaTool([string]$name) {
    if ($JavaHome) { return Join-Path $JavaHome "bin\$name.exe" }
    return (Get-Command $name -ErrorAction Stop).Source
}
$javac = JavaTool 'javac'
$jar = JavaTool 'jar'
$java = JavaTool 'java'
$tempRoot = if ($env:PI_SCRATCH_DIR) { $env:PI_SCRATCH_DIR } else { [IO.Path]::GetTempPath() }
$temp = Join-Path $tempRoot ('mooncl-agent-' + [Guid]::NewGuid().ToString('N'))
$classes = Join-Path $temp 'agent'
$testClasses = Join-Path $temp 'test'
$stagedJar = Join-Path $temp 'mooncl-multiplayer-agent.jar'
$savedEnv = @{}
try {
    New-Item -ItemType Directory -Path $classes, $testClasses -Force | Out-Null
    $sources = @(Get-ChildItem (Join-Path $PSScriptRoot 'src') -Filter '*.java' -Recurse | ForEach-Object FullName)
    & $javac --release 8 -encoding UTF-8 -d $classes @sources
    if ($LASTEXITCODE -ne 0) { throw 'Agent javac failed' }
    & $jar cfm $stagedJar (Join-Path $PSScriptRoot 'MANIFEST.MF') -C $classes .
    if ($LASTEXITCODE -ne 0) { throw 'jar failed' }
    $tests = @(Get-ChildItem (Join-Path $PSScriptRoot 'test') -Filter '*.java' -Recurse | ForEach-Object FullName)
    & $javac --release 8 -encoding UTF-8 -cp $classes -d $testClasses @tests
    if ($LASTEXITCODE -ne 0) { throw 'Harness javac failed' }
    $count = 0
    Get-ChildItem $classes, $testClasses -Filter '*.class' -Recurse | ForEach-Object {
        $bytes = [IO.File]::ReadAllBytes($_.FullName)
        $major = ([int]$bytes[6] -shl 8) -bor [int]$bytes[7]
        if ($major -ne 52) { throw "Non-Java-8 bytecode: $($_.FullName) major=$major" }
        $count++
    }
    Write-Host "Bytecode validation: $count classes have major version 52"
    foreach ($key in @('MOONCL_LINK_PORT', 'MOONCL_LINK_TOKEN', 'MOONCL_LINK_MAPPINGS')) {
        $savedEnv[$key] = [Environment]::GetEnvironmentVariable($key, 'Process')
        [Environment]::SetEnvironmentVariable($key, $null, 'Process')
    }
    & $java "-javaagent:$stagedJar" -cp "$stagedJar$([IO.Path]::PathSeparator)$testClasses" mooncl.agent.AgentHarness
    if ($LASTEXITCODE -ne 0) { throw 'Synthetic harness / premain smoke test failed' }
    $destination = [IO.Path]::GetFullPath($OutputJar)
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($destination)) -Force | Out-Null
    Copy-Item $stagedJar $destination -Force
    Write-Host "Built: $destination"
    Write-Host "SHA256: $((Get-FileHash $destination -Algorithm SHA256).Hash)"
}
finally {
    foreach ($key in $savedEnv.Keys) { [Environment]::SetEnvironmentVariable($key, $savedEnv[$key], 'Process') }
    if (Test-Path $temp) { Remove-Item $temp -Recurse -Force }
}
