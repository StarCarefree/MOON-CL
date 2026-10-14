#Requires -Version 7.0
<#
.SYNOPSIS
  把 PCL-CY 打成一个框架依赖的单 exe。本脚本不会在被点源时发布。

.DESCRIPTION
  产物只有一个 exe：前面是 .NET Framework 4.8 启动器（不依赖 .NET 10），后面附加压缩后的启动器载荷。
  未安装对应架构的 .NET 10 Desktop Runtime 时，先弹出窗口；只有点「同意并下载」才会从微软官方地址下载并安装。
  体积靠「不打包 .NET 10、不压进单文件内部、外层只压缩一次」来减。WPF 不修剪，原生库必须进包，缺了就失败。
  WebView2 Fixed Version 不是编译库。传入 -FixedRuntimeDirectory 才会打进同一个 exe；不传则 exe 更小，浏览页需要下次再带上。

  不要在其它 dotnet build / publish 还在跑时执行本脚本。

.EXAMPLE
  双击 scripts\Publish-SingleExe.bat
  或 .\scripts\Publish-SingleExe.ps1 -Architecture x64 -Configuration Release
.EXAMPLE
  .\scripts\Publish-SingleExe.ps1 -Architecture x64 -FixedRuntimeDirectory D:\wv2\fixed-x64 -ReadyToRun
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('x64', 'arm64')]
    [string]$Architecture,

    [ValidateSet('Debug', 'Release', 'Beta', 'CI')]
    [string]$Configuration = 'Release',

    [string]$OutputDirectory = '',

    [string]$DotNetPath = '',

    [string]$FixedRuntimeDirectory = '',

    [switch]$ReadyToRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$Architecture = $Architecture.ToLowerInvariant()
$script:RepoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$script:ProjectDir = Join-Path $script:RepoRoot 'Plain Craft Launcher 2'
$script:ProjectFile = Join-Path $script:ProjectDir 'Plain Craft Launcher 2.csproj'
$script:StubDir = Join-Path $PSScriptRoot 'single-exe'
$script:Magic = 'MOONCLPKGv1'
$script:FooterSize = 80

function Write-Step([string]$Message) { Write-Host "[single-exe] $Message" }
function Throw-Single([string]$Message) { throw "Publish-SingleExe: $Message" }

function Get-ScratchRoot {
    if (-not [string]::IsNullOrWhiteSpace($env:PI_SCRATCH_DIR)) {
        return [System.IO.Path]::GetFullPath($env:PI_SCRATCH_DIR)
    }
    return [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
}

function Assert-LastExitCode([string]$Name) {
    if ($null -ne $LASTEXITCODE -and $LASTEXITCODE -ne 0) {
        Throw-Single "$Name 失败，exit code=$LASTEXITCODE"
    }
}

function Get-MsbuildPlatform([string]$Arch) {
    if ($Arch -eq 'arm64') { return 'ARM64' }
    return 'x64'
}

function Get-ExpectedPeMachine([string]$Arch) {
    if ($Arch -eq 'arm64') { return [uint16]0xAA64 }
    return [uint16]0x8664
}

function Get-PeMachine([string]$Path) {
    $fs = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
    try {
        $br = [System.IO.BinaryReader]::new($fs)
        if ($br.ReadUInt16() -ne 0x5A4D) { Throw-Single "不是 PE: $Path" }
        $null = $fs.Seek(0x3C, [System.IO.SeekOrigin]::Begin)
        $peOff = $br.ReadInt32()
        $null = $fs.Seek($peOff, [System.IO.SeekOrigin]::Begin)
        if ($br.ReadUInt32() -ne 0x4550) { Throw-Single "不是 PE: $Path" }
        return $br.ReadUInt16()
    }
    finally { $fs.Dispose() }
}

function Test-DotNetHasSdk([string]$DotNetExe) {
    $sdk = Join-Path (Split-Path -Parent $DotNetExe) 'sdk'
    return Test-Path -LiteralPath $sdk -PathType Container
}

function Resolve-DotNetExe {
    $candidates = New-Object System.Collections.Generic.List[string]
    function Add-DotNet([string]$Path) {
        if ([string]::IsNullOrWhiteSpace($Path)) { return }
        $full = $Path
        if (Test-Path -LiteralPath $Path -PathType Container) { $full = Join-Path $Path 'dotnet.exe' }
        if (-not (Test-Path -LiteralPath $full -PathType Leaf)) { return }
        if (-not $candidates.Contains($full)) { $candidates.Add($full) }
    }
    Add-DotNet $DotNetPath
    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($cmd -and $cmd.Source) { Add-DotNet $cmd.Source }
    foreach ($name in @('DOTNET_ROOT', 'DOTNET_ROOT_X64', 'DOTNET_ROOT_ARM64')) {
        Add-DotNet ([Environment]::GetEnvironmentVariable($name, 'Process'))
        Add-DotNet ([Environment]::GetEnvironmentVariable($name, 'User'))
        Add-DotNet ([Environment]::GetEnvironmentVariable($name, 'Machine'))
    }
    foreach ($hit in @(Get-Command dotnet -All -ErrorAction SilentlyContinue)) {
        if ($hit.Source) { Add-DotNet $hit.Source }
    }
    $withSdk = @($candidates | Where-Object { Test-DotNetHasSdk $_ })
    if ($withSdk.Count -gt 0) { return $withSdk[0] }
    if (-not [string]::IsNullOrWhiteSpace($DotNetPath)) {
        Throw-Single "DotNetPath 旁边没有 SDK: $DotNetPath"
    }
    Throw-Single '未找到带 SDK 的 dotnet.exe。PATH 上的 dotnet 可能只是运行时，请用 -DotNetPath 指定 .NET 10 SDK。'
}

function Initialize-ExternalCaches {
    $nugetRoot = Join-Path $script:ScratchRoot 'pcl-cy-dotnet-cache'
    $packages = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $nugetRoot 'nuget-packages' }
    $httpCache = if ($env:NUGET_HTTP_CACHE_PATH) { $env:NUGET_HTTP_CACHE_PATH } else { Join-Path $nugetRoot 'nuget-http-cache' }
    $cliHome = if ($env:DOTNET_CLI_HOME) { $env:DOTNET_CLI_HOME } else { Join-Path $nugetRoot 'dotnet-cli-home' }
    foreach ($p in @($packages, $httpCache, $cliHome, $script:WorkRoot)) {
        New-Item -ItemType Directory -Force -Path $p | Out-Null
    }
    $env:NUGET_PACKAGES = $packages
    $env:NUGET_HTTP_CACHE_PATH = $httpCache
    $env:DOTNET_CLI_HOME = $cliHome
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_NOLOGO = '1'
}

function Resolve-OutputFile {
    if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
        $dir = Join-Path $script:RepoRoot ("artifacts\single-exe-win-{0}" -f $Architecture)
    }
    elseif ([System.IO.Path]::IsPathRooted($OutputDirectory)) {
        $dir = [System.IO.Path]::GetFullPath($OutputDirectory)
    }
    else {
        $dir = [System.IO.Path]::GetFullPath((Join-Path (Get-Location).Path $OutputDirectory))
    }
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    return (Join-Path $dir 'MOONCL.exe')
}

function Invoke-DotNetPublish([string]$DotNetExe, [string]$OutDir) {
    $rid = "win-$Architecture"
    $platform = Get-MsbuildPlatform $Architecture
    $readyToRunValue = 'false'
    if ($ReadyToRun) { $readyToRunValue = 'true' }
    $publishArgs = @(
        'publish', $script:ProjectFile,
        '-c', $Configuration,
        '-r', $rid,
        '--self-contained', 'false',
        '-p:SelfContained=false',
        '-p:PublishSingleFile=true',
        '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:IncludeAllContentForSelfExtract=false',
        '-p:EnableCompressionInSingleFile=false',
        '-p:PublishTrimmed=false',
        '-p:EnableTrimAnalyzer=false',
        "-p:PublishReadyToRun=$readyToRunValue",
        '-p:DebugType=none',
        '-p:DebugSymbols=false',
        '-p:CopyOutputSymbolsToPublishDirectory=false',
        '-p:SatelliteResourceLanguages=zh-CN%3Bzh-Hans%3Bzh-TW%3Bzh-Hant%3Ben%3Ben-US%3Ben-GB',
        '-p:TieredCompilation=true',
        '-p:TieredCompilationQuickJit=true',
        '-p:TieredPGO=true',
        '-p:ServerGarbageCollection=false',
        '-p:DebuggerSupport=false',
        "-p:RuntimeIdentifier=$rid",
        "-p:Platform=$platform",
        ('-p:OutputPath=' + (Join-Path $script:WorkRoot 'build\')),
        '-o', $OutDir,
        '--nologo',
        '--disable-build-servers'
    )
    Write-Step ("dotnet " + ($publishArgs -join ' '))
    & $DotNetExe @publishArgs
    Assert-LastExitCode 'dotnet publish'
}

function Test-FileContainsText([string]$Path, [string]$Name) {
    $ascii = [System.Text.Encoding]::ASCII.GetString([System.IO.File]::ReadAllBytes($Path))
    if ($ascii.Contains($Name)) { return $true }
    $utf16 = [System.Text.Encoding]::Unicode.GetString([System.IO.File]::ReadAllBytes($Path))
    return $utf16.Contains($Name)
}

function Assert-NoMissingLibraries([string]$PublishDir, [string]$LauncherExe) {
    foreach ($name in @('WebView2Loader.dll', 'e_sqlite3.dll')) {
        $loose = Join-Path $PublishDir $name
        $inBundle = Test-FileContainsText $LauncherExe $name
        $onDisk = Test-Path -LiteralPath $loose -PathType Leaf
        if (-not $inBundle -and -not $onDisk) {
            Throw-Single "漏库: 单文件和发布目录都没有 $name。拒绝为了体积丢掉原生库。"
        }
        Write-Step ("库 {0}: bundle={1} loose={2}" -f $name, $inBundle, $onDisk)
    }
    $bytes = [System.Text.Encoding]::ASCII.GetString([System.IO.File]::ReadAllBytes($LauncherExe))
    if ($bytes.Contains('includedFrameworks')) {
        Throw-Single '启动器 exe 含 includedFrameworks，拒绝把 .NET 运行时打进去。'
    }
}

function Copy-PublishPayload([string]$PublishDir, [string]$Stage) {
    New-Item -ItemType Directory -Force -Path $Stage | Out-Null
    $copied = 0
    foreach ($file in @(Get-ChildItem -LiteralPath $PublishDir -File)) {
        if ($file.Extension -in '.pdb', '.xml') { continue }
        if ($file.Name -match '(?i)^(windowsdesktop-runtime|dotnet-runtime).*\.(zip|exe)$') {
            Throw-Single "发布目录出现 .NET 运行时包: $($file.Name)"
        }
        Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $Stage $file.Name) -Force
        $copied++
    }
    foreach ($dirName in @('runtimes')) {
        $src = Join-Path $PublishDir $dirName
        if (Test-Path -LiteralPath $src -PathType Container) {
            Copy-Item -LiteralPath $src -Destination (Join-Path $Stage $dirName) -Recurse -Force
        }
    }
    foreach ($pair in @(
            @{ Src = Join-Path $script:RepoRoot 'LICENSE'; Name = 'LICENSE' }
            @{ Src = Join-Path $script:ProjectDir 'LICENCE'; Name = 'LICENCE' }
        )) {
        if (-not (Test-Path -LiteralPath $pair.Src -PathType Leaf)) {
            Throw-Single "缺少许可证: $($pair.Src)"
        }
        Copy-Item -LiteralPath $pair.Src -Destination (Join-Path $Stage $pair.Name) -Force
    }
    if (-not (Test-Path -LiteralPath (Join-Path $Stage 'Plain Craft Launcher 2.exe') -PathType Leaf)) {
        Throw-Single '发布目录没有 Plain Craft Launcher 2.exe'
    }
    $utf8 = New-Object System.Text.UTF8Encoding $false
    $manifest = (@{ architecture = $Architecture; channel = '10.0'; selfContained = $false; trimmed = $false } | ConvertTo-Json -Compress)
    [System.IO.File]::WriteAllText((Join-Path $Stage 'runtime.json'), $manifest, $utf8)
    Write-Step "已暂存 $copied 个发布文件，未丢弃 dll/exe/json。"
}

function Copy-FixedRuntime([string]$Stage) {
    if ([string]::IsNullOrWhiteSpace($FixedRuntimeDirectory)) {
        Write-Step '未嵌入 WebView2 Fixed Version。WebView2Loader 仍必须在包内；浏览器运行时未打进 exe，以减小体积。'
        return
    }
    $root = [System.IO.Path]::GetFullPath($FixedRuntimeDirectory)
    $exe = Join-Path $root 'msedgewebview2.exe'
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
        Throw-Single "FixedRuntimeDirectory 缺少 msedgewebview2.exe: $root"
    }
    $machine = Get-PeMachine $exe
    $expected = Get-ExpectedPeMachine $Architecture
    if ($machine -ne $expected) {
        Throw-Single ("WebView2 PE machine=0x{0:X4} 与 {1} 不匹配" -f $machine, $Architecture)
    }
    $dest = Join-Path $Stage 'WebView2Runtime'
    New-Item -ItemType Directory -Force -Path $dest | Out-Null
    Copy-Item -Path (Join-Path $root '*') -Destination $dest -Recurse -Force
    Write-Step "已嵌入 WebView2Runtime（会明显增大单 exe）"
}

function New-PayloadZip([string]$Stage, [string]$ZipPath) {
    if (Test-Path -LiteralPath $ZipPath) { Remove-Item -LiteralPath $ZipPath -Force }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $Stage,
        $ZipPath,
        [System.IO.Compression.CompressionLevel]::Optimal,
        $false)
}

function Add-MoonclFooter([string]$ExePath, [string]$ZipPath, [string]$Arch) {
    $zipLength = (Get-Item -LiteralPath $ZipPath).Length
    $sha = [System.Security.Cryptography.SHA256]::Create()
    $zipStream = [System.IO.File]::OpenRead($ZipPath)
    try { $hash = $sha.ComputeHash($zipStream) }
    finally { $zipStream.Dispose(); $sha.Dispose() }
    $start = (Get-Item -LiteralPath $ExePath).Length
    $out = [System.IO.File]::Open($ExePath, [System.IO.FileMode]::Append, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
    $input = [System.IO.File]::OpenRead($ZipPath)
    try {
        $input.CopyTo($out)
        $footer = New-Object byte[] $script:FooterSize
        $magic = [System.Text.Encoding]::ASCII.GetBytes($script:Magic)
        [Array]::Copy($magic, $footer, $magic.Length)
        [Array]::Copy([BitConverter]::GetBytes([int64]$start), 0, $footer, 16, 8)
        [Array]::Copy([BitConverter]::GetBytes([int64]$zipLength), 0, $footer, 24, 8)
        [Array]::Copy($hash, 0, $footer, 32, 32)
        $footer[64] = if ($Arch -eq 'arm64') { 2 } else { 1 }
        $out.Write($footer, 0, $footer.Length)
    }
    finally {
        $input.Dispose()
        $out.Dispose()
    }
}

function Find-RoslynCsc([string]$DotNetExe) {
    $roots = New-Object System.Collections.Generic.List[string]
    $beside = Join-Path (Split-Path -Parent $DotNetExe) 'sdk'
    if (Test-Path -LiteralPath $beside -PathType Container) { $roots.Add($beside) }
    foreach ($name in @('DOTNET_ROOT', 'DOTNET_ROOT_X64', 'DOTNET_ROOT_ARM64')) {
        foreach ($scope in @('Process', 'User', 'Machine')) {
            $value = [Environment]::GetEnvironmentVariable($name, $scope)
            if ([string]::IsNullOrWhiteSpace($value)) { continue }
            $sdk = Join-Path $value.Trim() 'sdk'
            if ((Test-Path -LiteralPath $sdk -PathType Container) -and -not $roots.Contains($sdk)) { $roots.Add($sdk) }
        }
    }
    $hits = New-Object System.Collections.Generic.List[string]
    foreach ($sdkRoot in $roots) {
        foreach ($file in @(Get-ChildItem -LiteralPath $sdkRoot -Filter 'csc.dll' -Recurse -ErrorAction SilentlyContinue)) {
            if ($file.FullName -match '\\Roslyn\\bincore\\csc\.dll$' -and -not $hits.Contains($file.FullName)) {
                $hits.Add($file.FullName)
            }
        }
    }
    if ($hits.Count -eq 0) { Throw-Single "找不到 SDK Roslyn csc.dll。已查: $($roots -join '; ')" }
    return @($hits | Sort-Object -Descending)[0]
}

function Compile-Bootstrapper([string]$DotNetExe, [string]$OutExe) {
    $csc = Find-RoslynCsc $DotNetExe
    $fx = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
    $refs = @(
        'mscorlib.dll', 'System.dll', 'System.Core.dll', 'System.Drawing.dll',
        'System.Windows.Forms.dll', 'System.Net.Http.dll',
        'System.IO.Compression.dll', 'System.IO.Compression.FileSystem.dll',
        'System.Web.Extensions.dll'
    )
    $refArgs = @()
    foreach ($name in $refs) {
        $path = Join-Path $fx $name
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            Throw-Single "缺少 .NET Framework 引用: $path"
        }
        $refArgs += ('/r:' + $path)
    }
    $platform = if ($Architecture -eq 'arm64') { 'arm64' } else { 'x64' }
    $icon = Join-Path $script:ProjectDir 'Images\icon.ico'
    $cscArgs = @(
        $csc,
        '/nologo', '/noconfig', '/nostdlib+', '/optimize+', '/deterministic+',
        '/target:winexe',
        ('/platform:' + $platform),
        '/langversion:7.3',
        '/codepage:65001',
        '/utf8output',
        ('/win32manifest:' + (Join-Path $script:StubDir 'app.manifest')),
        ('/out:' + $OutExe)
    ) + $refArgs
    if (Test-Path -LiteralPath $icon -PathType Leaf) {
        $cscArgs += ('/win32icon:' + $icon)
    }
    $cscArgs += (Join-Path $script:StubDir 'Bootstrapper.cs')
    Write-Step "编译 .NET Framework 启动器（不链接 .NET 10）"
    & $DotNetExe exec @cscArgs
    Assert-LastExitCode 'csc'
    if (-not (Test-Path -LiteralPath $OutExe -PathType Leaf)) {
        Throw-Single "启动器没有生成: $OutExe"
    }
}

function Main {
    if (-not (Test-Path -LiteralPath $script:ProjectFile -PathType Leaf)) {
        Throw-Single "找不到工程: $script:ProjectFile"
    }
    $dotnet = Resolve-DotNetExe
    Initialize-ExternalCaches
    $publishDir = Join-Path $script:WorkRoot 'publish'
    $stage = Join-Path $script:WorkRoot 'stage'
    $zip = Join-Path $script:WorkRoot 'payload.zip'
    $stub = Join-Path $script:WorkRoot 'stub.exe'
    New-Item -ItemType Directory -Force -Path $publishDir | Out-Null
    Write-Step "dotnet=$dotnet"
    Write-Step '框架依赖单文件：不自包含、不修剪、原生库打进启动器，单文件内部不压缩以加快二次启动。'
    Invoke-DotNetPublish $dotnet $publishDir
    $launcher = Join-Path $publishDir 'Plain Craft Launcher 2.exe'
    Assert-NoMissingLibraries $publishDir $launcher
    Copy-PublishPayload $publishDir $stage
    Copy-FixedRuntime $stage
    New-PayloadZip $stage $zip
    Compile-Bootstrapper $dotnet $stub
    $outFile = Resolve-OutputFile
    if (Test-Path -LiteralPath $outFile) { Remove-Item -LiteralPath $outFile -Force }
    Copy-Item -LiteralPath $stub -Destination $outFile -Force
    Add-MoonclFooter $outFile $zip $Architecture
    $size = (Get-Item -LiteralPath $outFile).Length
    Write-Host ''
    Write-Host 'Single exe OK'
    Write-Host "  output : $outFile"
    Write-Host ("  size   : {0:N1} MiB" -f ($size / 1MB))
    Write-Host '  runtime: not bundled; consent UI downloads .NET 10 Desktop Runtime'
    Write-Host '  trim   : false; native libraries required'
}

$script:ScratchRoot = Get-ScratchRoot
$script:WorkRoot = Join-Path $script:ScratchRoot ("pcl-cy-single-exe\{0}-{1}" -f $Architecture, $PID)
$script:DotSource = $MyInvocation.InvocationName -eq '.' -or $MyInvocation.Line -match '^\s*\.\s+'
if (-not $script:DotSource) {
    try { Main }
    finally {
        if ($script:WorkRoot -and (Test-Path -LiteralPath $script:WorkRoot)) {
            try { Remove-Item -LiteralPath $script:WorkRoot -Recurse -Force -ErrorAction SilentlyContinue } catch { }
        }
    }
}
