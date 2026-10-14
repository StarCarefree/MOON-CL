#Requires -Version 7.0
<#
.SYNOPSIS
     发布 PCL-CY Windows 便携包：框架依赖单文件（不打包 .NET 10，不修剪 WPF）+ 同级 Fixed Version WebView2 Runtime。

.DESCRIPTION
     产物不包含 .NET 10 运行时，也不自包含 Desktop Runtime。用户运行 Start-PCL-CY.cmd，同意后才从微软官方地址下载并安装。
     不要求预装系统 WebView2。禁止安装 Evergreen / 系统 WebView2 Runtime，禁止用 Chrome 冒充 Runtime。
    Fixed Version 放到 exe 同级 WebView2Runtime\（宿主应优先读该目录）。
    NuGet / .NET SDK 缓存在仓库外（$env:PI_SCRATCH_DIR 或 TEMP）。

.PARAMETER Architecture
    x64 或 arm64。两种架构是独立包，不可混用 Runtime。

.PARAMETER Configuration
    Debug / Release / Beta。默认 Release。

.PARAMETER OutputDirectory
    发布目录。默认 <repo>/artifacts/portable-win-<arch>。

.PARAMETER DotNetPath
    dotnet 可执行文件或其所在目录。省略则用 PATH。

.PARAMETER FixedRuntimeDirectory
    已解包的 WebView2 Fixed Version 根目录（内含 msedgewebview2.exe）。

.PARAMETER FixedRuntimeCab
    官方 Fixed Version CAB 路径。使用 Windows expand.exe 解压。

.PARAMETER DownloadUrl
    微软 https 可信域上的 Fixed Version CAB。禁止 Evergreen 安装包。

.PARAMETER Sha256
    若给出，则对 CAB（本地下载或 -FixedRuntimeCab）做严格 SHA256 校验。

.PARAMETER CreateZip
    另外生成 ZIP（不含把 LICENSE 声明丢掉）。

.EXAMPLE
    .\scripts\Publish-Portable.ps1 -Architecture x64 -FixedRuntimeDirectory D:\wv2\fixed-x64

.EXAMPLE
    .\scripts\Publish-Portable.ps1 -Architecture arm64 -Configuration Release -FixedRuntimeCab D:\wv2\runtime.cab -CreateZip
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

    [string]$FixedRuntimeCab = '',

    [string]$DownloadUrl = '',

    [string]$Sha256 = '',

    [switch]$CreateZip
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$Architecture = $Architecture.ToLowerInvariant()
$script:RepoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$script:ProjectDir = Join-Path $script:RepoRoot 'Plain Craft Launcher 2'
$script:ProjectFile = Join-Path $script:ProjectDir 'Plain Craft Launcher 2.csproj'

function Get-ScratchRoot {
    if (-not [string]::IsNullOrWhiteSpace($env:PI_SCRATCH_DIR)) {
        return [System.IO.Path]::GetFullPath($env:PI_SCRATCH_DIR)
    }
    return [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
}

function Write-Step([string]$Message) {
    Write-Host "[portable] $Message"
}

function Throw-Portable([string]$Message) {
    throw "Publish-Portable: $Message"
}

function Get-MsbuildPlatform([string]$Arch) {
    if ($Arch -eq 'arm64') { return 'ARM64' }
    return 'x64'
}

function Get-ExpectedPeMachine([string]$Arch) {
    # IMAGE_FILE_MACHINE_AMD64 / IMAGE_FILE_MACHINE_ARM64
    if ($Arch -eq 'arm64') { return [uint16]0xAA64 }
    return [uint16]0x8664
}

function Test-IsTrustedMicrosoftHost([string]$HostName) {
    if ([string]::IsNullOrWhiteSpace($HostName)) { return $false }
    $h = $HostName.Trim().TrimEnd('.').ToLowerInvariant()
    $allowed = @(
        'microsoft.com',
        'windows.net',
        'aka.ms',
        'visualstudio.com',
        'azure.com',
        'msecnd.net',
        'azureedge.net',
        'msft.net',
        'dot.net'
    )
    foreach ($suffix in $allowed) {
        if ($h -eq $suffix -or $h.EndsWith('.' + $suffix, [StringComparison]::Ordinal)) {
            return $true
        }
    }
    return $false
}

function Get-UrlFileName([Uri]$Uri) {
    $name = [System.IO.Path]::GetFileName($Uri.AbsolutePath)
    if ([string]::IsNullOrWhiteSpace($name)) { return '' }
    return $name
}

function Test-IsCabUri([Uri]$Uri) {
    return (Get-UrlFileName $Uri).EndsWith('.cab', [StringComparison]::OrdinalIgnoreCase)
}

function Test-IsForbiddenRuntimeUri([Uri]$Uri) {
    $file = (Get-UrlFileName $Uri).ToLowerInvariant()
    $full = $Uri.AbsoluteUri.ToLowerInvariant()
    if ($file.EndsWith('.exe') -or $file.EndsWith('.msi') -or $file.EndsWith('.msix')) { return $true }
    if ($file -match 'microsoftedgewebview2setup') { return $true }
    if ($full -match 'evergreen' -or $full -match 'bootstrapper') { return $true }
    if ($full -match 'webview2setup') { return $true }
    return $false
}

function Assert-MicrosoftHttpsUrl([string]$Url, [switch]$RequireCab) {
    if ([string]::IsNullOrWhiteSpace($Url)) {
        Throw-Portable 'URL 为空。'
    }
    [Uri]$uri = $null
    if (-not [Uri]::TryCreate($Url, [UriKind]::Absolute, [ref]$uri)) {
        Throw-Portable "不是绝对 URL: $Url"
    }
    if ($uri.Scheme -ne 'https') {
        Throw-Portable "仅允许 https: $Url"
    }
    if (-not (Test-IsTrustedMicrosoftHost $uri.Host)) {
        Throw-Portable "下载域不是微软可信域: $($uri.Host)"
    }
    if (Test-IsForbiddenRuntimeUri $uri) {
        Throw-Portable "拒绝 Evergreen/安装包 URL（只要 Fixed Version CAB）: $Url"
    }
    if ($RequireCab -and -not (Test-IsCabUri $uri)) {
        Throw-Portable "下载必须是 .cab（Fixed Version），禁止猜测其它链接: $Url"
    }
}

function Test-ArchitectureMatch([string]$Text, [string]$Arch) {
    if ([string]::IsNullOrWhiteSpace($Text)) { return $false }
    $t = $Text.ToLowerInvariant()
    if ($Arch -eq 'arm64') {
        return ($t -match 'arm64' -or $t -match 'aarch64')
    }
    if ($t -match 'arm64' -or $t -match 'aarch64' -or $t -match '(^|[^a-z])x86([^a-z]|$)') {
        return $false
    }
    return ($t -match 'x64' -or $t -match 'amd64' -or $t -match 'win64')
}

function Assert-Sha256([string]$FilePath, [string]$Expected) {
    if ([string]::IsNullOrWhiteSpace($Expected)) { return }
    $want = $Expected.Trim()
    if ($want.StartsWith('0x', [StringComparison]::OrdinalIgnoreCase)) {
        $want = $want.Substring(2)
    }
    $want = ($want -replace '\s', '').ToLowerInvariant()
    if ($want -notmatch '^[0-9a-f]{64}$') {
        Throw-Portable "Sha256 必须是 64 位十六进制: $Expected"
    }
    $actual = (Get-FileHash -LiteralPath $FilePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $want) {
        Throw-Portable "SHA256 校验失败。expected=$want actual=$actual file=$FilePath"
    }
    Write-Step "SHA256 校验通过: $actual"
}

function Assert-LastExitCode([string]$Name) {
    if ($null -ne $LASTEXITCODE -and $LASTEXITCODE -ne 0) {
        Throw-Portable "$Name 失败，exit code=$LASTEXITCODE"
    }
}

function Assert-PathOutsideRepo([string]$Path, [string]$Name) {
    $full = [System.IO.Path]::GetFullPath($Path)
    $repoPrefix = $script:RepoRoot.TrimEnd('\') + '\'
    if ($full.StartsWith($repoPrefix, [StringComparison]::OrdinalIgnoreCase) -or
        $full.TrimEnd('\') -eq $script:RepoRoot.TrimEnd('\')) {
        Throw-Portable "$Name 禁止放在仓库内: $full"
    }
}

function Get-PeMachine([string]$Path) {
    $fs = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
    try {
        $br = [System.IO.BinaryReader]::new($fs)
        if ($br.ReadUInt16() -ne 0x5A4D) {
            Throw-Portable "不是 PE（缺 MZ）: $Path"
        }
        $null = $fs.Seek(0x3C, [System.IO.SeekOrigin]::Begin)
        $peOff = $br.ReadInt32()
        if ($peOff -le 0 -or $peOff -gt ($fs.Length - 6)) {
            Throw-Portable "PE offset 无效: $Path"
        }
        $null = $fs.Seek($peOff, [System.IO.SeekOrigin]::Begin)
        if ($br.ReadUInt32() -ne 0x4550) {
            Throw-Portable "不是 PE（缺 PE 签名）: $Path"
        }
        return $br.ReadUInt16()
    }
    finally {
        $fs.Dispose()
    }
}

function Find-MsedgeWebView2Files([string]$Root) {
    $list = [System.Collections.Generic.List[System.IO.FileInfo]]::new()
    if (Test-Path -LiteralPath $Root -PathType Container) {
        foreach ($file in @(Get-ChildItem -LiteralPath $Root -Filter 'msedgewebview2.exe' -File -Recurse -ErrorAction SilentlyContinue)) {
            if ($file) { $list.Add($file) }
        }
    }
    return ,$list
}


function Test-LooksLikeChrome([string]$Root, [string]$ExePath) {
    $chromeExe = Get-ChildItem -LiteralPath $Root -Filter 'chrome.exe' -File -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($chromeExe -and -not $ExePath) { return $true }
    if ($chromeExe -and [string]::Equals($chromeExe.DirectoryName, [System.IO.Path]::GetDirectoryName($ExePath), [StringComparison]::OrdinalIgnoreCase)) {
        return $true
    }
    if ($ExePath -and (Test-Path -LiteralPath $ExePath -PathType Leaf)) {
        $info = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($ExePath)
        $blob = @(
            $info.ProductName
            $info.FileDescription
            $info.CompanyName
            $info.OriginalFilename
            $info.InternalName
        ) -join ' '
        if ($blob -match 'Google Chrome' -or $blob -match 'Google LLC' -or $blob -match 'Google Inc') {
            return $true
        }
        if ($blob -match 'Chrome' -and $blob -notmatch 'WebView2' -and $blob -notmatch 'Microsoft') {
            return $true
        }
    }
    $rootLower = $Root.ToLowerInvariant()
    if ($rootLower -match '\\google\\chrome(\\|$)' -or $rootLower -match '\\chromium\\') {
        return $true
    }
    return $false
}

function Resolve-FixedRuntimeRoot([string]$SearchRoot) {
    if ([string]::IsNullOrWhiteSpace($SearchRoot) -or -not (Test-Path -LiteralPath $SearchRoot -PathType Container)) {
        Throw-Portable "Fixed Version 目录不存在: $SearchRoot"
    }
    $exes = Find-MsedgeWebView2Files $SearchRoot
    if ($exes.Count -eq 0) {
        if (Test-LooksLikeChrome $SearchRoot $null) {
            Throw-Portable "拒绝 Chrome/Chromium 目录，需要 WebView2 Fixed Version（msedgewebview2.exe）: $SearchRoot"
        }
        Throw-Portable "未找到 msedgewebview2.exe: $SearchRoot"
    }
    $ranked = @(
        $exes | Sort-Object {
            $dir = $_.DirectoryName
            $score = $_.FullName.Length
            if (Test-Path -LiteralPath (Join-Path $dir 'msedge.dll')) { $score -= 100000 }
            if (Test-Path -LiteralPath (Join-Path $dir 'EmbeddedBrowserWebView.dll')) { $score -= 100000 }
            $score
        }
    )
    $exe = $ranked[0]
    $root = $exe.DirectoryName
    if (Test-LooksLikeChrome $root $exe.FullName) {
        Throw-Portable "拒绝 Chrome Runtime: $root"
    }
    $companion = @(
        @('msedge.dll', 'EmbeddedBrowserWebView.dll') |
            Where-Object { Test-Path -LiteralPath (Join-Path $root $_) }
    )
    if ($companion.Count -eq 0) {
        Throw-Portable "目录不像 WebView2 Fixed Version（缺少 msedge.dll / EmbeddedBrowserWebView.dll）: $root"
    }
    $info = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($exe.FullName)
    $blob = @($info.ProductName, $info.FileDescription, $info.CompanyName) -join ' '
    if ($blob -match 'Chrome' -and $blob -notmatch 'WebView2' -and $blob -notmatch 'Edge') {
        Throw-Portable "msedgewebview2.exe 产品信息不是 WebView2: $blob"
    }
    return $root
}

function Assert-RuntimeArchitecture([string]$RuntimeRoot, [string]$Arch) {
    $exe = Join-Path $RuntimeRoot 'msedgewebview2.exe'
    $machine = Get-PeMachine $exe
    $expected = Get-ExpectedPeMachine $Arch
    if ($machine -ne $expected) {
        Throw-Portable ("WebView2 Runtime PE machine=0x{0:X4} 与目标架构 {1} (0x{2:X4}) 不匹配: {3}" -f $machine, $Arch, $expected, $exe)
    }
    Write-Step ("已验证 msedgewebview2.exe PE machine=0x{0:X4} ({1})" -f $machine, $Arch)
}

function Invoke-ExpandCab([string]$CabPath, [string]$Destination) {
    $expand = Join-Path $env:SystemRoot 'System32\expand.exe'
    if (-not (Test-Path -LiteralPath $expand -PathType Leaf)) {
        Throw-Portable "找不到 expand.exe: $expand"
    }
    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    Write-Step "expand.exe $CabPath -> $Destination"
    & $expand -F:* $CabPath $Destination
    Assert-LastExitCode 'expand.exe'
}

function Expand-FixedRuntimeCab([string]$CabPath) {
    if (-not (Test-Path -LiteralPath $CabPath -PathType Leaf)) {
        Throw-Portable "CAB 不存在: $CabPath"
    }
    Assert-Sha256 $CabPath $Sha256
    $dest = Join-Path $script:WorkRoot ('cab-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $dest | Out-Null
    Invoke-ExpandCab $CabPath $dest
    $exes = Find-MsedgeWebView2Files $dest
    if ($exes.Count -eq 0) {
        $innerCabs = @(Get-ChildItem -LiteralPath $dest -Filter '*.cab' -File -Recurse -ErrorAction SilentlyContinue)
        foreach ($inner in $innerCabs) {
            $innerDest = Join-Path $dest ('nested-' + [System.IO.Path]::GetFileNameWithoutExtension($inner.Name))
            Invoke-ExpandCab $inner.FullName $innerDest
        }
    }
    return (Resolve-FixedRuntimeRoot $dest)
}

function Invoke-MicrosoftHttp([string]$Url, [string]$OutFile, [switch]$RequireCab) {
    Assert-MicrosoftHttpsUrl $Url
    $handler = [System.Net.Http.HttpClientHandler]::new()
    $handler.AllowAutoRedirect = $false
    $client = [System.Net.Http.HttpClient]::new($handler)
    $client.Timeout = [TimeSpan]::FromMinutes(30)
    $client.DefaultRequestHeaders.UserAgent.ParseAdd('PCL-CY-Publish-Portable/1.0')
    try {
        $current = [Uri]$Url
        for ($hop = 0; $hop -lt 12; $hop++) {
            Assert-MicrosoftHttpsUrl $current.AbsoluteUri
            if ($current.Scheme -ne 'https' -or -not (Test-IsTrustedMicrosoftHost $current.Host)) {
                Throw-Portable "重定向离开微软 https 可信域: $($current.AbsoluteUri)"
            }
            if (Test-IsForbiddenRuntimeUri $current) {
                Throw-Portable "拒绝 Evergreen/安装包: $($current.AbsoluteUri)"
            }
            $response = $client.GetAsync($current, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
            $code = [int]$response.StatusCode
            if ($code -in 301, 302, 303, 307, 308) {
                $loc = $response.Headers.Location
                $response.Dispose()
                if ($null -eq $loc) {
                    Throw-Portable "重定向缺少 Location: $($current.AbsoluteUri)"
                }
                if (-not $loc.IsAbsoluteUri) {
                    $loc = [Uri]::new($current, $loc)
                }
                $current = $loc
                continue
            }
            if (-not $response.IsSuccessStatusCode) {
                $response.Dispose()
                Throw-Portable "下载失败 HTTP $code : $($current.AbsoluteUri)"
            }
            if ($RequireCab -and -not (Test-IsCabUri $current)) {
                $response.Dispose()
                Throw-Portable "最终 URL 不是 .cab: $($current.AbsoluteUri)"
            }
            if ($OutFile) {
                $dir = Split-Path -Parent $OutFile
                New-Item -ItemType Directory -Force -Path $dir | Out-Null
                $fs = [System.IO.File]::Create($OutFile)
                try {
                    $response.Content.CopyToAsync($fs).GetAwaiter().GetResult()
                }
                finally {
                    $fs.Dispose()
                    $response.Dispose()
                }
                return $OutFile
            }
            try {
                return $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            }
            finally {
                $response.Dispose()
            }
        }
        Throw-Portable "重定向过多: $Url"
    }
    finally {
        $client.Dispose()
        $handler.Dispose()
    }
}

function Get-CabUrlsFromText([string]$Text) {
    if ([string]::IsNullOrWhiteSpace($Text)) { return @() }
    # Official Nuxt payloads escape slashes inside JSON strings.
    $Text = $Text.Replace('\u002F', '/').Replace('\/', '/')
    $cabMatches = [regex]::Matches($Text, 'https://[A-Za-z0-9._~:/?#\[\]@!$&''()*+,;=%-]+\.cab(?:\?[A-Za-z0-9._~:/?#\[\]@!$&''()*+,;=%-]*)?')
    $urls = New-Object System.Collections.Generic.List[string]
    foreach ($m in $cabMatches) {
        $u = [System.Net.WebUtility]::HtmlDecode($m.Value)
        if (-not $urls.Contains($u)) { $urls.Add($u) }
    }
    return @($urls)
}

function Select-ArchitectureCabs([string[]]$Urls, [string]$Arch) {
    $trusted = foreach ($url in @($Urls)) {
        [Uri]$uri = $null
        if (-not [Uri]::TryCreate($url, [UriKind]::Absolute, [ref]$uri)) { continue }
        if ($uri.Scheme -ne 'https') { continue }
        if (-not (Test-IsTrustedMicrosoftHost $uri.Host)) { continue }
        if (Test-IsForbiddenRuntimeUri $uri) { continue }
        if (-not (Test-IsCabUri $uri)) { continue }
        if (-not (Test-ArchitectureMatch $uri.AbsoluteUri $Arch)) { continue }
        $uri.AbsoluteUri
    }
    return @($trusted)
}

function Find-OfficialFixedRuntimeCabUrl([string]$Arch) {
    # 只探测微软官方入口，不拼接/猜测版本化 CDN 路径，不用 Evergreen fwlink。
    $endpoints = @(
        'https://edgeupdates.microsoft.com/api/products'
        'https://edgeupdates.microsoft.com/api/products?view=enterprise'
        'https://developer.microsoft.com/en-us/microsoft-edge/webview2/'
        'https://developer.microsoft.com/microsoft-edge/webview2/'
        'https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution'
    )
    $found = New-Object System.Collections.Generic.List[string]
    foreach ($endpoint in $endpoints) {
        try {
            Write-Step "探测官方渠道: $endpoint"
            $text = Invoke-MicrosoftHttp $endpoint
            foreach ($url in (Select-ArchitectureCabs (Get-CabUrlsFromText $text) $Arch)) {
                if (-not $found.Contains($url)) { $found.Add($url) }
            }
            if ($endpoint -match 'edgeupdates\.microsoft\.com') {
                try {
                    $json = $text | ConvertFrom-Json
                    $jsonText = $json | ConvertTo-Json -Depth 80 -Compress
                    foreach ($url in (Select-ArchitectureCabs (Get-CabUrlsFromText $jsonText) $Arch)) {
                        if (-not $found.Contains($url)) { $found.Add($url) }
                    }
                }
                catch {
                    # 非 JSON 则忽略，继续其它入口
                }
            }
        }
        catch {
            Write-Step ("官方渠道不可用（忽略）: {0} — {1}" -f $endpoint, $_.Exception.Message)
        }
    }
    if ($found.Count -eq 0) {
        return $null
    }
    return $found[0]
}

function Resolve-RuntimeRootFromInputs {
    $dirSet = -not [string]::IsNullOrWhiteSpace($FixedRuntimeDirectory)
    $cabSet = -not [string]::IsNullOrWhiteSpace($FixedRuntimeCab)
    $urlSet = -not [string]::IsNullOrWhiteSpace($DownloadUrl)
    $sourceCount = @($dirSet, $cabSet, $urlSet) | Where-Object { $_ } | Measure-Object | Select-Object -ExpandProperty Count
    if ($sourceCount -gt 1) {
        Throw-Portable 'FixedRuntimeDirectory / FixedRuntimeCab / DownloadUrl 只能提供其中一个。'
    }

    if ($dirSet) {
        $root = Resolve-FixedRuntimeRoot ([System.IO.Path]::GetFullPath($FixedRuntimeDirectory))
        Assert-RuntimeArchitecture $root $Architecture
        return $root
    }

    if ($cabSet) {
        $cabPath = [System.IO.Path]::GetFullPath($FixedRuntimeCab)
        $root = Expand-FixedRuntimeCab $cabPath
        Assert-RuntimeArchitecture $root $Architecture
        return $root
    }

    $url = $DownloadUrl
    if (-not $urlSet) {
        Write-Step '未提供 Runtime 输入，尝试从微软官方 Fixed Version 渠道发现架构匹配 CAB（不会使用 Evergreen）。'
        $url = Find-OfficialFixedRuntimeCabUrl $Architecture
        if ([string]::IsNullOrWhiteSpace($url)) {
            Throw-Portable @"
未能从微软官方渠道可靠发现架构=$Architecture 的 WebView2 Fixed Version CAB（禁止猜测链接，也不会暗自使用 Evergreen）。
请明确传入:
  -FixedRuntimeDirectory <已解包目录>
  或 -FixedRuntimeCab <官方 CAB>
  或 -DownloadUrl <微软 https CAB> [-Sha256 <hex>]
"@
        }
        Write-Step "发现官方 CAB: $url"
    }

    $cabOut = Join-Path $script:WorkRoot ('download-{0}.cab' -f $Architecture)
    Invoke-MicrosoftHttp $url $cabOut -RequireCab
    Assert-Sha256 $cabOut $Sha256
    $root = Expand-FixedRuntimeCab $cabOut
    Assert-RuntimeArchitecture $root $Architecture
    return $root
}

function Resolve-DotNetExe {
    if (-not [string]::IsNullOrWhiteSpace($DotNetPath)) {
        $p = [System.IO.Path]::GetFullPath($DotNetPath)
        if (Test-Path -LiteralPath $p -PathType Leaf) { return $p }
        $candidate = Join-Path $p 'dotnet.exe'
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
        Throw-Portable "DotNetPath 无效: $DotNetPath"
    }
    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($cmd -and $cmd.Source) { return $cmd.Source }
    Throw-Portable '未找到 dotnet。请用 -DotNetPath 指定 .NET 10 SDK。'
}

function Initialize-ExternalCaches {
    $nugetRoot = Join-Path $script:ScratchRoot 'pcl-cy-dotnet-cache'
    $packages = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $nugetRoot 'nuget-packages' }
    $httpCache = if ($env:NUGET_HTTP_CACHE_PATH) { $env:NUGET_HTTP_CACHE_PATH } else { Join-Path $nugetRoot 'nuget-http-cache' }
    $cliHome = if ($env:DOTNET_CLI_HOME) { $env:DOTNET_CLI_HOME } else { Join-Path $nugetRoot 'dotnet-cli-home' }
    foreach ($p in @($packages, $httpCache, $cliHome, $script:WorkRoot)) {
        New-Item -ItemType Directory -Force -Path $p | Out-Null
        Assert-PathOutsideRepo $p 'NuGet/.NET 缓存或临时目录'
    }
    $env:NUGET_PACKAGES = $packages
    $env:NUGET_HTTP_CACHE_PATH = $httpCache
    $env:DOTNET_CLI_HOME = $cliHome
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_NOLOGO = '1'
    $env:DOTNET_MULTILEVEL_LOOKUP = '0'
    Write-Step "NUGET_PACKAGES=$packages"
}

function Resolve-OutputDirectory {
    if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
        return (Join-Path $script:RepoRoot ("artifacts\portable-win-{0}" -f $Architecture))
    }
    if ([System.IO.Path]::IsPathRooted($OutputDirectory)) {
        return [System.IO.Path]::GetFullPath($OutputDirectory)
    }
    return [System.IO.Path]::GetFullPath((Join-Path (Get-Location).Path $OutputDirectory))
}

function Assert-SafeOutputDirectory([string]$Path) {
    $full = [System.IO.Path]::GetFullPath($Path)
    $repo = $script:RepoRoot.TrimEnd('\')
    if ($full.TrimEnd('\') -eq $repo -or $repo.StartsWith($full.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
        Throw-Portable 'OutputDirectory 不能是仓库根目录或其父目录。'
    }
    $forbidden = @(
        $script:ProjectDir
        (Join-Path $script:RepoRoot 'scripts')
        (Join-Path $script:RepoRoot 'docs')
        (Join-Path $script:RepoRoot 'PCL.Core')
        (Join-Path $script:RepoRoot 'PCL.Core.Test')
        (Join-Path $script:RepoRoot 'PCL.Core.SourceGenerators')
        (Join-Path $script:RepoRoot '.git')
        (Join-Path $script:RepoRoot '.github')
    )
    foreach ($f in $forbidden) {
        $protected = [System.IO.Path]::GetFullPath($f).TrimEnd('\')
        if ($full.TrimEnd('\') -eq $protected -or $full.StartsWith($protected + '\', [StringComparison]::OrdinalIgnoreCase)) {
            Throw-Portable "OutputDirectory 不能覆盖源码目录: $full"
        }
    }
}

function Invoke-DotNetPublish([string]$DotNetExe, [string]$OutDir) {
    $rid = "win-$Architecture"
    $platform = Get-MsbuildPlatform $Architecture
    Write-Step "dotnet --version"
    & $DotNetExe --version
    Assert-LastExitCode 'dotnet --version'

    $publishArgs = @(
        'publish'
        $script:ProjectFile
        '-c', $Configuration
        '-r', $rid
         '--self-contained', 'false'
         '-p:SelfContained=false'
        '-p:PublishSingleFile=true'
        '-p:IncludeNativeLibrariesForSelfExtract=true'
        '-p:PublishTrimmed=false'
        '-p:EnableTrimAnalyzer=false'
        "-p:RuntimeIdentifier=$rid"
        "-p:Platform=$platform"
        ('-p:OutputPath=' + (Join-Path $script:WorkRoot 'build\'))
        '-o', $OutDir
        '--nologo'
        '--disable-build-servers'
    )
    Write-Step ("dotnet " + ($publishArgs -join ' '))
    & $DotNetExe @publishArgs
    Assert-LastExitCode 'dotnet publish'
}

function Copy-Licenses([string]$OutDir) {
    $copied = @()
    $pairs = @(
        @{ Src = Join-Path $script:RepoRoot 'LICENSE'; Dst = Join-Path $OutDir 'LICENSE' }
        @{ Src = Join-Path $script:ProjectDir 'LICENCE'; Dst = Join-Path $OutDir 'LICENCE' }
    )
    foreach ($pair in $pairs) {
        if (-not (Test-Path -LiteralPath $pair.Src -PathType Leaf)) {
            Throw-Portable "缺少许可证文件，拒绝发布: $($pair.Src)"
        }
        Copy-Item -LiteralPath $pair.Src -Destination $pair.Dst -Force
        $copied += $pair.Dst
    }
    Write-Step '已复制 LICENSE / LICENCE'
    return $copied
}

function Copy-WebView2Runtime([string]$RuntimeRoot, [string]$OutDir) {
    $dest = Join-Path $OutDir 'WebView2Runtime'
    if (Test-Path -LiteralPath $dest) {
        Remove-Item -LiteralPath $dest -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $dest | Out-Null
    Copy-Item -Path (Join-Path $RuntimeRoot '*') -Destination $dest -Recurse -Force
    $exe = Join-Path $dest 'msedgewebview2.exe'
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
        Throw-Portable "复制后缺少 WebView2Runtime\msedgewebview2.exe"
    }
    Assert-RuntimeArchitecture $dest $Architecture
    return $dest
}
 
 function Copy-DotNetBootstrap([string]$OutDir) {
     $pairs = @(
         @{ Src = Join-Path $PSScriptRoot 'Ensure-DotNet10.ps1'; Dst = Join-Path $OutDir 'Ensure-DotNet10.ps1' }
         @{ Src = Join-Path $PSScriptRoot 'Start-PCL-CY.cmd'; Dst = Join-Path $OutDir 'Start-PCL-CY.cmd' }
     )
     foreach ($pair in $pairs) {
         if (-not (Test-Path -LiteralPath $pair.Src -PathType Leaf)) {
             Throw-Portable "缺少 .NET 10 同意安装引导: $($pair.Src)"
         }
         Copy-Item -LiteralPath $pair.Src -Destination $pair.Dst -Force
     }
     $manifest = [ordered]@{
         channel = '10.0'
         architecture = $Architecture
         framework = 'Microsoft.WindowsDesktop.App'
         minimumVersion = '10.0.0'
         selfContained = $false
         packageRuntime = $false
         entry = 'Start-PCL-CY.cmd'
         consent = 'download-and-install-after-yes'
     }
     $utf8 = New-Object System.Text.UTF8Encoding $true
     $jsonPath = Join-Path $OutDir 'dotnet-runtime.json'
     [System.IO.File]::WriteAllText($jsonPath, (($manifest | ConvertTo-Json) + [Environment]::NewLine), $utf8)
     $readme = @(
         'PCL-CY 便携包'
         '=============='
         '请双击 Start-PCL-CY.cmd 启动，不要只拷贝 exe。'
         ''
         '本压缩包不包含 .NET 10，以减小体积。'
         '若本机没有 .NET 10 Desktop Runtime，启动器会先询问。'
         '只有同意后，才会从微软官方地址下载并安装，然后打开启动器。'
         '拒绝则不会下载。'
         ''
         'WebView2 Fixed Version 仍在 WebView2Runtime\，不要删除。'
     ) -join [Environment]::NewLine
     [System.IO.File]::WriteAllText((Join-Path $OutDir 'README-PORTABLE.txt'), ($readme + [Environment]::NewLine), $utf8)
     Write-Step '已放入 Start-PCL-CY.cmd（同意后才下载安装 .NET 10）'
 }
 
 function Assert-NoBundledDotNet([string]$OutDir) {
     $bad = New-Object System.Collections.Generic.List[string]
     foreach ($path in @(
             (Join-Path $OutDir 'shared\Microsoft.NETCore.App'),
             (Join-Path $OutDir 'shared\Microsoft.WindowsDesktop.App'),
             (Join-Path $OutDir 'host\fxr'))) {
         if (Test-Path -LiteralPath $path) { $bad.Add($path) }
     }
     foreach ($hit in @(Get-ChildItem -LiteralPath $OutDir -File -ErrorAction SilentlyContinue)) {
         if ($hit.Name -match '(?i)^(windowsdesktop-runtime|dotnet-runtime).*\.(zip|exe)$') {
             $bad.Add($hit.FullName)
         }
     }
     if ($bad.Count -gt 0) {
         Throw-Portable ("产物仍包含 .NET 运行时，拒绝打包: " + ($bad -join '; '))
     }
 }

function Get-RuntimeConfigInfo([string]$OutDir) {
    $rid = "win-$Architecture"
    $candidates = New-Object System.Collections.Generic.List[string]
    foreach ($hit in @(Get-ChildItem -LiteralPath $OutDir -Filter '*.runtimeconfig.json' -File -ErrorAction SilentlyContinue)) {
        $candidates.Add($hit.FullName)
    }
    $build = Join-Path $script:WorkRoot 'build'
    if (Test-Path -LiteralPath $build -PathType Container) {
        foreach ($hit in @(Get-ChildItem -LiteralPath $build -Filter 'Plain Craft Launcher 2.runtimeconfig.json' -File -Recurse)) {
            $candidates.Add($hit.FullName)
        }
    }
     # Framework-dependent single-file still writes runtimeconfig under the build output.
    $bin = Join-Path $script:ProjectDir "bin\$Configuration"
    if (Test-Path -LiteralPath $bin -PathType Container) {
        foreach ($hit in @(Get-ChildItem -LiteralPath $bin -Filter 'Plain Craft Launcher 2.runtimeconfig.json' -File -Recurse)) {
            if ($hit.FullName -match [regex]::Escape($rid)) { $candidates.Add($hit.FullName) }
        }
    }
    $obj = Join-Path $script:ProjectDir 'obj'
    if (Test-Path -LiteralPath $obj -PathType Container) {
        foreach ($hit in @(Get-ChildItem -LiteralPath $obj -Filter '*.runtimeconfig.json' -File -Recurse -ErrorAction SilentlyContinue)) {
            $p = $hit.FullName
            if ($p -match [regex]::Escape($rid) -and $p -match [regex]::Escape($Configuration)) {
                $candidates.Add($p)
            }
        }
    }
     if ($candidates.Count -eq 0) {
         Throw-Portable '未找到 *.runtimeconfig.json，无法验证框架依赖发布。'
     }
     $sawIncluded = $false
     foreach ($path in $candidates) {
         $cfg = Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json
         $opts = $cfg.runtimeOptions
         if ($null -eq $opts) { continue }
         $included = @()
         if ($opts.PSObject.Properties.Name -contains 'includedFrameworks' -and $opts.includedFrameworks) {
             $included = @($opts.includedFrameworks)
         }
         if ($included.Count -gt 0) {
             $sawIncluded = $true
             continue
         }
         $frameworks = @()
         if ($opts.PSObject.Properties.Name -contains 'frameworks' -and $opts.frameworks) {
             $frameworks = @($opts.frameworks)
         }
         if ($frameworks.Count -eq 0) { continue }
         $names = @($frameworks | ForEach-Object { $_.name })
         return [pscustomobject]@{
             Path       = $path
             Frameworks = $frameworks
             Names      = $names
         }
     }
 
     if ($sawIncluded) {
         Throw-Portable '只找到自包含 runtimeconfig（includedFrameworks）。拒绝打包 .NET 运行时。'
     }
     Throw-Portable 'runtimeconfig.json 缺少 frameworks（不是框架依赖发布）。'
}

function Assert-PublishLayout([string]$OutDir) {
    $exeHits = @(Get-ChildItem -LiteralPath $OutDir -Filter '*.exe' -File | Where-Object { $_.Name -ne 'msedgewebview2.exe' })
    if ($exeHits.Count -eq 0) {
        Throw-Portable "发布目录没有启动器 exe: $OutDir"
     }
     $bundle = [System.Text.Encoding]::ASCII.GetString([System.IO.File]::ReadAllBytes($exeHits[0].FullName))
     if ($bundle.Contains('includedFrameworks')) {
         Throw-Portable "启动器 exe 仍嵌入 includedFrameworks，拒绝把 .NET 10 打进压缩包: $($exeHits[0].FullName)"
     }
    $info = Get-RuntimeConfigInfo $OutDir
     $names = @($info.Names)
     foreach ($fw in @($info.Frameworks)) {
         if ([string]$fw.version -notmatch '^10\.') {
             Throw-Portable "框架版本不是 10.x: $($fw.name) $($fw.version)"
         }
     }
     if ($names -notcontains 'Microsoft.NETCore.App') {
         Throw-Portable "frameworks 缺少 Microsoft.NETCore.App: $($info.Path)"
     }
     if ($names -notcontains 'Microsoft.WindowsDesktop.App') {
         Throw-Portable "frameworks 缺少 Microsoft.WindowsDesktop.App（需要 Desktop Runtime）: $($info.Path)"
     }
     Assert-NoBundledDotNet $OutDir
     foreach ($required in @('Start-PCL-CY.cmd', 'Ensure-DotNet10.ps1', 'dotnet-runtime.json', 'README-PORTABLE.txt')) {
         if (-not (Test-Path -LiteralPath (Join-Path $OutDir $required) -PathType Leaf)) {
             Throw-Portable "产物缺少 .NET 10 同意安装引导: $required"
         }
     }
    $wv = Join-Path $OutDir 'WebView2Runtime\msedgewebview2.exe'
    if (-not (Test-Path -LiteralPath $wv -PathType Leaf)) {
        Throw-Portable '产物缺少 WebView2Runtime\msedgewebview2.exe'
    }
    foreach ($lic in @('LICENSE', 'LICENCE')) {
        if (-not (Test-Path -LiteralPath (Join-Path $OutDir $lic) -PathType Leaf)) {
            Throw-Portable "产物缺少许可证: $lic"
        }
    }
    return [pscustomobject]@{
        Exe            = $exeHits[0].FullName
        RuntimeConfig  = $info
        WebView2Exe    = $wv
    }
}

function Write-VerifyChecklist([string]$OutDir, $Layout, [string]$RuntimeRoot) {
    $wvInfo = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($Layout.WebView2Exe)
    $machine = Get-PeMachine $Layout.WebView2Exe
     $frameworks = @(
         $Layout.RuntimeConfig.Frameworks | ForEach-Object { '- {0} {1}' -f $_.name, $_.version }
    ) -join [Environment]::NewLine
    $text = @"
PCL-CY portable verification
============================
Timestamp (UTC)     : $([DateTime]::UtcNow.ToString('o'))
Architecture        : $Architecture
MSBuild Platform    : $(Get-MsbuildPlatform $Architecture)
Configuration       : $Configuration
RID                 : win-$Architecture
OutputDirectory     : $OutDir
Launcher exe        : $($Layout.Exe)
 SelfContained       : false
PublishSingleFile   : true
IncludeNativeLibrariesForSelfExtract : true
PublishTrimmed      : false (WPF 不修剪)
runtimeconfig       : $($Layout.RuntimeConfig.Path)
 frameworks          :
$frameworks
WebView2 mode       : Fixed Version (NOT Evergreen, NOT system installer, NOT Chrome)
WebView2Runtime     : $(Join-Path $OutDir 'WebView2Runtime')
msedgewebview2.exe  : $($Layout.WebView2Exe)
PE machine          : 0x$($machine.ToString('X4'))
ProductName         : $($wvInfo.ProductName)
FileDescription     : $($wvInfo.FileDescription)
FileVersion         : $($wvInfo.FileVersion)
Source runtime root : $RuntimeRoot
Licenses            : LICENSE (Apache-2.0, repo) ; LICENCE (PCL 分发有限许可)
 Host contract       : 先运行 Start-PCL-CY.cmd。未安装 .NET 10 时必须等用户同意，才下载安装 Desktop Runtime。宿主仍优先使用 exe 同级 WebView2Runtime。
 DotNet package      : 压缩包不包含 .NET 10 runtime zip / self-contained runtime。
"@
    $path = Join-Path $OutDir 'PORTABLE-VERIFY.txt'
    Set-Content -LiteralPath $path -Value $text -Encoding UTF8
    Write-Step "校验清单: $path"
    return $path
}

function New-PortableZip([string]$OutDir) {
    $zipPath = Join-Path ([System.IO.Path]::GetDirectoryName($OutDir.TrimEnd('\'))) ("PCL-CY-portable-win-{0}-{1}.zip" -f $Architecture, $Configuration)
    if (Test-Path -LiteralPath $zipPath) {
        Remove-Item -LiteralPath $zipPath -Force
    }
    Write-Step "ZIP $zipPath"
    # Never distribute profile/configuration data created when the output folder was used for a test run.
    $stage = Join-Path $script:WorkRoot 'zip-content'
    New-Item -ItemType Directory -Path $stage -Force | Out-Null
     $shipNames = @('LICENSE', 'LICENCE', 'PORTABLE-VERIFY.txt', 'README-PORTABLE.txt', 'Ensure-DotNet10.ps1', 'Start-PCL-CY.cmd', 'dotnet-runtime.json')
     foreach ($file in @(Get-ChildItem -LiteralPath $OutDir -File)) {
         if ($file.Name -match '(?i)^(windowsdesktop-runtime|dotnet-runtime-).*\.(zip|exe)$') {
             Throw-Portable "拒绝把 .NET 运行时打进压缩包: $($file.Name)"
         }
         if ($file.Extension -eq '.pdb') { continue }
         if ($file.Extension -in '.exe', '.dll', '.json', '.xml', '.ps1', '.cmd' -or $shipNames -contains $file.Name) {
             Copy-Item -LiteralPath $file.FullName -Destination $stage
         }
     }
    Copy-Item -LiteralPath (Join-Path $OutDir 'WebView2Runtime') -Destination $stage -Recurse
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zipPath -Force
    return $zipPath
}

function Main {
    if (-not (Test-Path -LiteralPath $script:ProjectFile -PathType Leaf)) {
        Throw-Portable "找不到工程: $script:ProjectFile"
    }

    $outDir = Resolve-OutputDirectory
    Assert-SafeOutputDirectory $outDir
    New-Item -ItemType Directory -Force -Path $script:WorkRoot | Out-Null
    Assert-PathOutsideRepo $script:WorkRoot '临时解压目录'
    Initialize-ExternalCaches

    Write-Step "RepoRoot=$script:RepoRoot"
    Write-Step "OutputDirectory=$outDir"
    Write-Step "Scratch=$script:ScratchRoot"

    $runtimeRoot = Resolve-RuntimeRootFromInputs
    Write-Step "Fixed Version root=$runtimeRoot"

    New-Item -ItemType Directory -Force -Path $outDir | Out-Null

    $dotnet = Resolve-DotNetExe
    Write-Step "dotnet=$dotnet"
    Invoke-DotNetPublish $dotnet $outDir
    Copy-WebView2Runtime $runtimeRoot $outDir | Out-Null
     Copy-Licenses $outDir | Out-Null
     Copy-DotNetBootstrap $outDir
     $layout = Assert-PublishLayout $outDir
    $verify = Write-VerifyChecklist $outDir $layout $runtimeRoot
    $zip = $null
    if ($CreateZip) {
        $zip = New-PortableZip $outDir
    }

    Write-Host ''
    Write-Host "Portable publish OK"
    Write-Host "  output : $outDir"
    Write-Host "  verify : $verify"
    if ($zip) { Write-Host "  zip    : $zip" }
}

$script:ScratchRoot = Get-ScratchRoot
$script:WorkRoot = Join-Path $script:ScratchRoot ("pcl-cy-portable\{0}-{1}" -f $Architecture, $PID)

try {
    Main
}
finally {
    if ($script:WorkRoot -and (Test-Path -LiteralPath $script:WorkRoot)) {
        try {
            Remove-Item -LiteralPath $script:WorkRoot -Recurse -Force -ErrorAction SilentlyContinue
        }
        catch {
            # 临时目录清理失败不改变发布结果
        }
    }
}
