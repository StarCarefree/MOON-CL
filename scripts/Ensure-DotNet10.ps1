#Requires -Version 5.1
<#
.SYNOPSIS
  Start PCL-CY after .NET 10 Desktop Runtime is present.
  The portable package does not contain the runtime. Download and install
  happen only after the user clicks Yes.
#>
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:ReleasesUrl = 'https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json'
$script:TrustedHostSuffixes = @(
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

function Get-Utf8Text([string]$Base64) {
    return [System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($Base64))
}

function Get-ConsentPrompt {
    return (Get-Utf8Text 'UENMLUNZIOmcgOimgSAuTkVUIDEwIERlc2t0b3AgUnVudGltZSDmiY3og73miZPlvIDjgIIKCuWuieijheWMheS4jemZhOW4piAuTkVUIDEw77yM5Lul5YeP5bCP5Y6L57yp5YyF5L2T56ev44CCCuaYr+WQpuWQjOaEj+S7juW+rui9r+WumOaWueWcsOWdgOS4i+i9veW5tuWuieijhe+8nwrlronoo4XpnIDopoHnrqHnkIblkZjmnYPpmZDjgILlj6rmnInngrnigJzmmK/igJ3miY3kvJrkuIvovb3lubblronoo4XvvIznhLblkI7lkK/liqjjgIIK54K54oCc5ZCm4oCd5YiZ5LiN5Lya5LiL6L2944CC')
}

function Get-DeclinedText {
    return (Get-Utf8Text '5bey5Y+W5raI44CC5pyq5LiL6L2944CB5pyq5a6J6KOFIC5ORVQgMTDjgII=')
}

function Get-MissingAfterInstallText {
    return (Get-Utf8Text '5a6J6KOF56iL5bqP5bey57uT5p2f77yM5L2G5LuN5pyq5om+5YiwIC5ORVQgMTAgRGVza3RvcCBSdW50aW1l44CC6K+35p+l55yL5a6J6KOF56qX5Y+j5piv5ZCm5oql6ZSZ5ZCO6YeN6K+V44CC')
}

function Get-DownloadFailedText {
    return (Get-Utf8Text '5LiL6L29IC5ORVQgMTAgRGVza3RvcCBSdW50aW1lIOWksei0peOAguacquWuieijheOAgg==')
}

function Get-UacCancelledText {
    return (Get-Utf8Text '5pyq6I635b6X566h55CG5ZGY5p2D6ZmQ77yM5bey5Y+W5raI5a6J6KOF44CC5pyq5pS55Yqo57O757ufIC5ORVTjgII=')
}

function Test-IsTrustedMicrosoftHost([string]$HostName) {
    if ([string]::IsNullOrWhiteSpace($HostName)) { return $false }
    $h = $HostName.Trim().TrimEnd('.').ToLowerInvariant()
    foreach ($suffix in $script:TrustedHostSuffixes) {
        if ($h -eq $suffix -or $h.EndsWith('.' + $suffix, [StringComparison]::Ordinal)) {
            return $true
        }
    }
    return $false
}

function Assert-MicrosoftHttpsUrl([string]$Url) {
    if ([string]::IsNullOrWhiteSpace($Url)) { throw 'URL is empty.' }
    $uri = $null
    if (-not [Uri]::TryCreate($Url, [UriKind]::Absolute, [ref]$uri)) {
        throw "Not an absolute URL: $Url"
    }
    if ($uri.Scheme -ne 'https') { throw "HTTPS required: $Url" }
    if ($uri.UserInfo.Length -gt 0) { throw "Userinfo is not allowed: $Url" }
    if (-not $uri.IsDefaultPort) { throw "Non-default port is not allowed: $Url" }
    if (-not (Test-IsTrustedMicrosoftHost $uri.Host)) { throw "Untrusted host: $($uri.Host)" }
    if ($uri.AbsolutePath.ToLowerInvariant().EndsWith('.zip')) {
        throw "Refusing .NET runtime zip: $Url"
    }
    return $uri
}

function Assert-DesktopInstallerUrl([string]$Url, [string]$Arch) {
    $uri = Assert-MicrosoftHttpsUrl $Url
    if ($uri.Host -ne 'builds.dotnet.microsoft.com') {
        throw "Installer must come from builds.dotnet.microsoft.com: $($uri.Host)"
    }
    $name = [System.IO.Path]::GetFileName($uri.AbsolutePath)
    $pattern = '^windowsdesktop-runtime-10\.0\.\d+-win-' + [regex]::Escape($Arch) + '\.exe$'
    if ($name -notmatch $pattern) {
        throw "Unexpected Desktop Runtime installer name: $name"
    }
}

function Get-DesktopInstallerSpecFromJson([string]$Json, [string]$Arch) {
    if ($Arch -ne 'x64' -and $Arch -ne 'arm64') { throw "Unsupported architecture: $Arch" }
    $doc = $Json | ConvertFrom-Json
    $version = [string]$doc.'latest-release'
    if ([string]::IsNullOrWhiteSpace($version)) { throw 'releases.json is missing latest-release.' }
    $rel = $null
    foreach ($item in @($doc.releases)) {
        if ([string]$item.'release-version' -eq $version) { $rel = $item; break }
    }
    if ($null -eq $rel) { throw "latest-release $version was not found." }
    $wanted = 'windowsdesktop-runtime-win-' + $Arch + '.exe'
    $file = $null
    foreach ($item in @($rel.windowsdesktop.files)) {
        $name = [string]$item.name
        if ($name.EndsWith('.zip', [StringComparison]::OrdinalIgnoreCase)) { continue }
        if ([string]$item.rid -eq ('win-' + $Arch) -and $name -eq $wanted) {
            $file = $item
            break
        }
    }
    if ($null -eq $file) { throw "releases.json has no $wanted" }
    $url = [string]$file.url
    Assert-DesktopInstallerUrl $url $Arch
    $hash = ([string]$file.hash).Trim().ToLowerInvariant()
    if ($hash -notmatch '^[0-9a-f]{128}$') { throw "Installer hash is not SHA512: $wanted" }
    return [pscustomobject]@{
        Version = [string]$rel.windowsdesktop.version
        Url     = $url
        Hash    = $hash
        Name    = $wanted
    }
}

function Get-AkaDesktopInstallerUrl([string]$Arch) {
    if ($Arch -eq 'arm64') { return 'https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-arm64.exe' }
    if ($Arch -eq 'x64') { return 'https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe' }
    throw "Unsupported architecture: $Arch"
}

function Resolve-DotNetLaunchPlan {
    param(
        [bool]$EnvRootSet,
        [bool]$EnvRootHasDesktop10,
        [bool]$RegisteredHasDesktop10
    )
    if ($EnvRootSet -and $EnvRootHasDesktop10) { return 'Launch' }
    if ($RegisteredHasDesktop10 -and $EnvRootSet -and -not $EnvRootHasDesktop10) { return 'ClearEnvAndLaunch' }
    if ($RegisteredHasDesktop10) { return 'Launch' }
    return 'NeedConsent'
}

function Test-Framework10Directory([string]$Root, [string]$Name) {
    if ([string]::IsNullOrWhiteSpace($Root)) { return $false }
    $dir = Join-Path (Join-Path $Root 'shared') $Name
    if (-not (Test-Path -LiteralPath $dir -PathType Container)) { return $false }
    $hits = @(Get-ChildItem -LiteralPath $dir -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -like '10.*' })
    return $hits.Count -gt 0
}

function Test-Desktop10At([string]$Root) {
    return (Test-Framework10Directory $Root 'Microsoft.NETCore.App') -and (Test-Framework10Directory $Root 'Microsoft.WindowsDesktop.App')
}

function Get-EffectiveEnvRoot([string]$Arch) {
    $archVar = if ($Arch -eq 'arm64') { 'DOTNET_ROOT_ARM64' } else { 'DOTNET_ROOT_X64' }
    $specific = [Environment]::GetEnvironmentVariable($archVar, 'Process')
    if (-not [string]::IsNullOrWhiteSpace($specific)) {
        return [pscustomobject]@{ Name = $archVar; Path = $specific.Trim() }
    }
    $root = [Environment]::GetEnvironmentVariable('DOTNET_ROOT', 'Process')
    if (-not [string]::IsNullOrWhiteSpace($root)) {
        return [pscustomobject]@{ Name = 'DOTNET_ROOT'; Path = $root.Trim() }
    }
    return $null
}

function Get-RegisteredDotNetRoots([string]$Arch) {
    $roots = New-Object System.Collections.Generic.List[string]
    $key = 'HKLM:\SOFTWARE\dotnet\Setup\InstalledVersions\' + $Arch
    try {
        $item = Get-ItemProperty -LiteralPath $key -Name InstallLocation -ErrorAction Stop
        $loc = [string]$item.InstallLocation
        if (-not [string]::IsNullOrWhiteSpace($loc)) { $roots.Add($loc.TrimEnd('\')) }
    }
    catch { }
    $programFiles = [Environment]::GetFolderPath('ProgramFiles')
    if (-not [string]::IsNullOrWhiteSpace($programFiles)) {
        $roots.Add((Join-Path $programFiles 'dotnet'))
        if ($Arch -eq 'x64') { $roots.Add((Join-Path $programFiles 'dotnet\x64')) }
    }
    $unique = New-Object System.Collections.Generic.List[string]
    foreach ($root in $roots) {
        if (-not $unique.Contains($root)) { $unique.Add($root) }
    }
    return @($unique)
}

function Test-RegisteredDesktop10([string]$Arch) {
    foreach ($root in @(Get-RegisteredDotNetRoots $Arch)) {
        if (Test-Desktop10At $root) { return $true }
    }
    return $false
}

function Read-PackageArchitecture([string]$Dir) {
    $path = Join-Path $Dir 'dotnet-runtime.json'
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return '' }
    $doc = Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json
    $arch = [string]$doc.architecture
    if ($arch -eq 'x64' -or $arch -eq 'arm64') { return $arch }
    return ''
}

function Get-PeMachine([string]$Path) {
    $fs = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
    try {
        $br = New-Object System.IO.BinaryReader($fs)
        $fs.Position = 0x3C
        $pe = $br.ReadInt32()
        $fs.Position = $pe + 4
        return $br.ReadUInt16()
    }
    finally { $fs.Dispose() }
}

function Get-ArchitectureFromExe([string]$Path) {
    $machine = Get-PeMachine $Path
    if ($machine -eq 0x8664) { return 'x64' }
    if ($machine -eq 0xAA64) { return 'arm64' }
    throw "Unsupported launcher PE machine 0x$($machine.ToString('X4')): $Path"
}

function Find-LauncherExe([string]$Dir) {
    $preferred = Join-Path $Dir 'Plain Craft Launcher 2.exe'
    if (Test-Path -LiteralPath $preferred -PathType Leaf) { return $preferred }
    $hits = @(Get-ChildItem -LiteralPath $Dir -Filter '*.exe' -File | Where-Object {
            $_.Name -notmatch '^(msedgewebview2|notification_helper|elevated_tracing_service|copilot_setup|copilotapp|mscopilot|platform_experiences_helper)\.exe$'
        })
    if ($hits.Count -eq 1) { return $hits[0].FullName }
    $named = @($hits | Where-Object { $_.Name -like 'PCL2_CE_*.exe' })
    if ($named.Count -eq 1) { return $named[0].FullName }
    throw "Launcher exe was not found in $Dir"
}

function Format-ProcessArguments([string[]]$Items) {
    if ($null -eq $Items -or $Items.Count -eq 0) { return '' }
    $parts = New-Object System.Collections.Generic.List[string]
    foreach ($item in $Items) {
        if ($null -eq $item) { continue }
        if ($item -match '[\s"]') {
            $parts.Add('"' + ($item.Replace('"', '\"')) + '"')
        }
        else { $parts.Add($item) }
    }
    return ($parts -join ' ')
}

function Start-LauncherProcess([string]$Exe, [bool]$ClearDotNetRoot, [string[]]$LaunchArgs) {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $Exe
    $psi.WorkingDirectory = Split-Path -Parent $Exe
    $psi.UseShellExecute = $false
    $psi.Arguments = Format-ProcessArguments $LaunchArgs
    if ($ClearDotNetRoot) {
        foreach ($name in @('DOTNET_ROOT', 'DOTNET_ROOT_X64', 'DOTNET_ROOT_ARM64', 'DOTNET_ROOT_X86')) {
            if ($psi.EnvironmentVariables.ContainsKey($name)) {
                [void]$psi.EnvironmentVariables.Remove($name)
            }
        }
    }
    [void][System.Diagnostics.Process]::Start($psi)
}

function Write-BootstrapLog([string]$Dir, [string]$Message) {
    $path = Join-Path $Dir 'dotnet-runtime.log'
    $line = '{0} {1}' -f ([DateTime]::Now.ToString('o')), $Message
    Add-Content -LiteralPath $path -Value $line -Encoding UTF8
}

function Show-Notice([string]$Message, [string]$IconName) {
    Add-Type -AssemblyName System.Windows.Forms
    $icon = [System.Windows.Forms.MessageBoxIcon]::Information
    if ($IconName -eq 'Error') { $icon = [System.Windows.Forms.MessageBoxIcon]::Error }
    if ($IconName -eq 'Question') { $icon = [System.Windows.Forms.MessageBoxIcon]::Question }
     [void][System.Windows.Forms.MessageBox]::Show($Message, 'PCL-MOON', [System.Windows.Forms.MessageBoxButtons]::OK, $icon)
}

function Request-InstallConsent {
    Add-Type -AssemblyName System.Windows.Forms
    $result = [System.Windows.Forms.MessageBox]::Show(
        (Get-ConsentPrompt),
         'PCL-MOON',
        [System.Windows.Forms.MessageBoxButtons]::YesNo,
        [System.Windows.Forms.MessageBoxIcon]::Question,
        [System.Windows.Forms.MessageBoxDefaultButton]::Button2)
    return ($result -eq [System.Windows.Forms.DialogResult]::Yes)
}

function Save-MicrosoftHttpsFile {
    param(
        [Parameter(Mandatory = $true)][string]$Url,
        [Parameter(Mandatory = $true)][string]$Destination,
        [scriptblock]$ValidateFinalUrl
    )
    try {
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    }
    catch { }
    $current = $Url
    for ($hop = 0; $hop -le 5; $hop++) {
        Assert-MicrosoftHttpsUrl $current | Out-Null
        $req = [System.Net.HttpWebRequest]::Create($current)
        $req.AllowAutoRedirect = $false
        $req.UserAgent = 'PCL-CY-Ensure-DotNet10/1.0'
        $req.Timeout = 180000
        $req.ReadWriteTimeout = 180000
        $resp = $null
        try {
            try { $resp = $req.GetResponse() }
            catch [System.Net.WebException] {
                $resp = $_.Exception.Response
                if ($null -eq $resp) { throw }
            }
            $status = [int]$resp.StatusCode
            if ($status -ge 300 -and $status -lt 400) {
                $loc = [string]$resp.Headers['Location']
                if ([string]::IsNullOrWhiteSpace($loc)) { throw "Redirect is missing Location: $current" }
                $current = (New-Object System.Uri((New-Object System.Uri($current)), $loc)).AbsoluteUri
                continue
            }
            if ($status -ne 200) { throw "HTTP $status for $current" }
            if ($null -ne $ValidateFinalUrl) { & $ValidateFinalUrl $current }
            $parent = Split-Path -Parent $Destination
            if (-not (Test-Path -LiteralPath $parent)) {
                New-Item -ItemType Directory -Path $parent | Out-Null
            }
            $out = New-Object System.IO.FileStream($Destination, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
            try { $resp.GetResponseStream().CopyTo($out) }
            finally { $out.Dispose() }
            return $current
        }
        finally {
            if ($resp) { $resp.Dispose() }
        }
    }
    throw "Too many redirects: $Url"
}

function Assert-Sha512([string]$FilePath, [string]$Expected) {
    $want = $Expected.Trim().ToLowerInvariant()
    if ($want -notmatch '^[0-9a-f]{128}$') { throw "SHA512 must be 128 hex chars." }
    $actual = (Get-FileHash -LiteralPath $FilePath -Algorithm SHA512).Hash.ToLowerInvariant()
    if ($actual -ne $want) {
        Remove-Item -LiteralPath $FilePath -Force -ErrorAction SilentlyContinue
        throw "SHA512 mismatch. expected=$want actual=$actual"
    }
}

function Assert-PeFile([string]$FilePath) {
    $fs = [System.IO.File]::Open($FilePath, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
    try {
        if ($fs.Length -lt 1048576) { throw "Installer is too small: $($fs.Length)" }
        $buf = New-Object byte[] 2
        [void]$fs.Read($buf, 0, 2)
        if ($buf[0] -ne 0x4D -or $buf[1] -ne 0x5A) { throw 'Installer is not a PE file.' }
    }
    finally { $fs.Dispose() }
}

function Get-DownloadDirectory([string]$PackageDir, [string]$Arch) {
    $temp = [System.IO.Path]::GetTempPath()
    $destDir = Join-Path (Join-Path $temp 'pcl-cy-dotnet') $Arch
    $full = [System.IO.Path]::GetFullPath($destDir)
    $package = [System.IO.Path]::GetFullPath($PackageDir).TrimEnd('\')
    if ($full.TrimEnd('\') -eq $package -or $full.StartsWith($package + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Refusing to download the runtime into the portable package.'
    }
    return $full
}

function Install-DesktopRuntimeExe([string]$Path) {
    try {
        $proc = Start-Process -FilePath $Path -ArgumentList '/install', '/passive', '/norestart' -Verb RunAs -Wait -PassThru
    }
    catch {
        throw (Get-UacCancelledText)
    }
    return [int]$proc.ExitCode
}

function Invoke-EnsureDotNet10Main {
    param([string[]]$LaunchArgs)
    $dir = $PSScriptRoot
    if ([string]::IsNullOrWhiteSpace($dir)) {
        $dir = Split-Path -Parent $MyInvocation.MyCommand.Path
    }
    $exe = Find-LauncherExe $dir
    $arch = Read-PackageArchitecture $dir
    if ([string]::IsNullOrWhiteSpace($arch)) { $arch = Get-ArchitectureFromExe $exe }
    $envRoot = Get-EffectiveEnvRoot $arch
    $envSet = $null -ne $envRoot
    $envOk = $envSet -and (Test-Desktop10At $envRoot.Path)
    $registeredOk = Test-RegisteredDesktop10 $arch
    $plan = Resolve-DotNetLaunchPlan -EnvRootSet $envSet -EnvRootHasDesktop10 $envOk -RegisteredHasDesktop10 $registeredOk
    Write-BootstrapLog $dir ("plan=$plan arch=$arch envSet=$envSet envOk=$envOk registeredOk=$registeredOk")
    if ($plan -eq 'NeedConsent') {
        if (-not (Request-InstallConsent)) {
            Show-Notice (Get-DeclinedText) 'Information'
            Write-BootstrapLog $dir 'consent=no; no download'
            return 2
        }
        Write-BootstrapLog $dir 'consent=yes'
        $destDir = Get-DownloadDirectory $dir $arch
        New-Item -ItemType Directory -Force -Path $destDir | Out-Null
        $dest = Join-Path $destDir 'windowsdesktop-runtime.exe'
        $spec = $null
        try {
            $jsonPath = Join-Path $destDir 'releases.json'
            Save-MicrosoftHttpsFile -Url $script:ReleasesUrl -Destination $jsonPath | Out-Null
            $spec = Get-DesktopInstallerSpecFromJson (Get-Content -LiteralPath $jsonPath -Raw -Encoding UTF8) $arch
        }
        catch {
            Write-BootstrapLog $dir ("releases.json unavailable: " + $_.Exception.Message)
            $spec = [pscustomobject]@{
                Version = ''
                Url     = (Get-AkaDesktopInstallerUrl $arch)
                Hash    = ''
                Name    = 'aka.ms'
            }
        }
        try {
             $boundArch = $arch
             $validateInstaller = {
                 param($u)
                 Assert-DesktopInstallerUrl $u $boundArch
             }.GetNewClosure()
             $finalUrl = Save-MicrosoftHttpsFile -Url $spec.Url -Destination $dest -ValidateFinalUrl $validateInstaller
        }
        catch {
            Write-BootstrapLog $dir ("download failed: " + $_.Exception.Message)
            Show-Notice (Get-DownloadFailedText) 'Error'
            return 1
        }
        Assert-PeFile $dest
        if (-not [string]::IsNullOrWhiteSpace($spec.Hash)) { Assert-Sha512 $dest $spec.Hash }
        Write-BootstrapLog $dir "downloaded $finalUrl"
        $code = Install-DesktopRuntimeExe $dest
        Write-BootstrapLog $dir "installer exit=$code"
        $registeredOk = Test-RegisteredDesktop10 $arch
        if (-not $registeredOk -and $code -ne 0 -and $code -ne 3010) {
            Show-Notice ((Get-MissingAfterInstallText) + " exit=$code") 'Error'
            return 1
        }
        if (-not $registeredOk) {
            Show-Notice (Get-MissingAfterInstallText) 'Error'
            return 1
        }
        Remove-Item -LiteralPath $dest -Force -ErrorAction SilentlyContinue
        $envRoot = Get-EffectiveEnvRoot $arch
        $envSet = $null -ne $envRoot
        $envOk = $envSet -and (Test-Desktop10At $envRoot.Path)
        $plan = Resolve-DotNetLaunchPlan -EnvRootSet $envSet -EnvRootHasDesktop10 $envOk -RegisteredHasDesktop10 $true
    }
    $clear = $plan -eq 'ClearEnvAndLaunch'
    if ($clear) {
        Write-BootstrapLog $dir 'launching with DOTNET_ROOT cleared so the registered .NET 10 install is visible'
    }
    Start-LauncherProcess $exe $clear $LaunchArgs
    return 0
}

$script:DotSource = $MyInvocation.InvocationName -eq '.' -or $MyInvocation.Line -match '^\s*\.\s+'
if (-not $script:DotSource) {
    $exitCode = 1
    try {
        $exitCode = Invoke-EnsureDotNet10Main -LaunchArgs $args
    }
    catch {
        $message = $_.Exception.Message
        try { Show-Notice $message 'Error' } catch { Write-Host $message }
        $exitCode = 1
    }
    exit $exitCode
}
