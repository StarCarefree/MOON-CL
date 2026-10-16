 # PCL-CY 便携包：框架依赖 .NET 10 与 WebView2 Fixed Version
 
 本文说明如何用 `scripts/Publish-Portable.ps1` 打出 **不附带 .NET 10**、且 **无需安装系统 WebView2** 的 Windows 便携包。压缩包不再塞进 .NET 10 运行时，以减小体积。

x64 与 ARM64 是 **两套独立包**，Runtime、RID、PE 架构必须一致，不能混用。

## 构建命令

在仓库根目录（`D:\pcl-cy\PCL-CY`）用 PowerShell 7：

```powershell
# 推荐：本地已解包的 Fixed Version 目录（目录内必须有 msedgewebview2.exe）
.\scripts\Publish-Portable.ps1 -Architecture x64 -Configuration Release -FixedRuntimeDirectory <FixedVersion根目录>

# ARM64 + CAB（Windows expand.exe 解压）
.\scripts\Publish-Portable.ps1 -Architecture arm64 -Configuration Release -FixedRuntimeCab <官方.cab路径>

# 微软 https CAB + 严格 SHA256
.\scripts\Publish-Portable.ps1 -Architecture x64 -Configuration Release `
  -DownloadUrl 'https://<microsoft-host>/.../xxx.cab' `
  -Sha256 '<64位hex>'

# 指定 SDK、输出目录、打 ZIP
.\scripts\Publish-Portable.ps1 -Architecture x64 -Configuration Beta `
  -DotNetPath 'C:\Program Files\dotnet\dotnet.exe' `
  -OutputDirectory 'artifacts\portable-win-x64' `
  -FixedRuntimeDirectory <FixedVersion根目录> `
  -CreateZip
```

参数：

| 参数 | 说明 |
|---|---|
| `Architecture` | 必填：`x64` 或 `arm64` |
| `Configuration` | `Debug` / `Release` / `Beta` / `CI`，默认 `Release` |
| `OutputDirectory` | 默认 `artifacts/portable-win-<arch>` |
| `DotNetPath` | `dotnet.exe` 或其所在目录；省略则用 PATH |
| `FixedRuntimeDirectory` | 已解包 Fixed Version 根目录 |
| `FixedRuntimeCab` | 官方 CAB；解压到 `$env:PI_SCRATCH_DIR`（未设置则 TEMP） |
| `DownloadUrl` | 仅微软 https 可信域上的 **CAB** |
| `Sha256` | 给出则对 CAB **严格**校验 |
| `CreateZip` | 额外生成 `PCL-CY-portable-win-<arch>-<config>.zip` |

`FixedRuntimeDirectory` / `FixedRuntimeCab` / `DownloadUrl` **三选一**。都不给时，脚本会探测微软官方页面/API 里**已经写明的**架构匹配 CAB；**禁止猜 CDN 路径**，也 **不会** 改用 Evergreen。探不到就失败，要求你改传目录或 CAB。

 发布属性（脚本显式传入，WPF **不修剪**，**不自包含**）：
 
 - `SelfContained=false`（框架依赖，不把 .NET 10 打进 exe 或 zip）
 - `PublishSingleFile=true`
 - `IncludeNativeLibrariesForSelfExtract=true`
 - `PublishTrimmed=false`
 - RID：`win-x64` / `win-arm64`
 - Platform：`x64` / `ARM64`
 
 脚本会把 `Start-PCL-CY.cmd`、`Ensure-DotNet10.ps1` 和 `dotnet-runtime.json` 放进产物。入口是 `Start-PCL-CY.cmd`，不是直接双击 exe。
优先使用已有的仓库外 NuGet / HTTP / .NET CLI 缓存，否则使用 scratch/TEMP；禁止将这些缓存写入源码目录。重建不会删除输出目录内的用户数据，ZIP 只收录程序、固定运行时与许可证等发布文件。

 ## 不打包 .NET 10；同意后才下载安装
 
 便携包内：
 
 1. **不包含** .NET 10 Desktop Runtime，也不包含 `windowsdesktop-runtime-*.zip` / 自包含运行时。`runtimeconfig` 使用 `frameworks`，不能出现 `includedFrameworks`。
 2. 用户双击 `Start-PCL-CY.cmd`。若本机已有对应架构的 .NET 10 Desktop Runtime，直接启动。
 3. 若没有，先弹出确认框。只有点“是”才会从微软官方 `releases.json`（失败时回退 `aka.ms`）下载 **exe 安装包**（不是 zip），校验 SHA512 后安装，再启动。点“否”不会下载。
 4. **WebView2 Fixed Version Runtime** 仍在 exe **同级** `WebView2Runtime\`。
 
 若用户环境变量 `DOTNET_ROOT` 指向没有 .NET 10 的目录，而系统已安装 10，启动脚本只为本次进程清掉该变量，不会重复安装。
 
 产物布局：
 
 ```
 artifacts/portable-win-x64/
   Start-PCL-CY.cmd
   Ensure-DotNet10.ps1
   dotnet-runtime.json
   README-PORTABLE.txt
   Plain Craft Launcher 2.exe
   WebView2Runtime/
     msedgewebview2.exe
     msedge.dll
     ...
   LICENSE
   LICENCE
   PORTABLE-VERIFY.txt
 ```
 
 脚本会拒绝：把 .NET 运行时 zip/exe 打进包、Evergreen 引导程序、Chrome/Chromium 目录、架构不匹配的 PE、缺少许可证或缺少同意安装引导。
 
 ## 无预装 WebView2 ≠ 无文件依赖
 
 用户机器需要 .NET 10 Desktop Runtime，但不必预先自己去找安装包。便携包仍依赖随包文件，至少包括：
 
 - 框架依赖单文件 exe（不含 .NET 运行时，体积小于自包含包）
 - `Start-PCL-CY.cmd`（同意后才下载安装 .NET 10）
 - 整份 `WebView2Runtime\`
 - `LICENSE` 与 `LICENCE`
 
 解压后必须保持相对布局。只拷走 exe、删掉 `WebView2Runtime`、引导脚本或许可证，都不算完整便携包。直接双击 exe 时，若尚未安装 .NET 10，只会看到系统的缺失运行时对话框，不会自动下载。

## 宿主如何使用 `WebView2Runtime`

程序宿主（由主工程实现，本脚本不改宿主代码）必须 **优先** 使用 exe 同级 `WebView2Runtime`，例如：

- 将 `<exeDir>\WebView2Runtime` 作为 `CoreWebView2Environment.CreateAsync` 的 `browserExecutableFolder` 参数
- 或进程环境变量 `WEBVIEW2_BROWSER_EXECUTABLE_FOLDER` 指向同一目录

该目录必须是 **直接含有** `msedgewebview2.exe` 的 Fixed Version 根，而不是再套一层版本号目录。

禁止：

- 安装/引导 Evergreen 或系统 WebView2 Runtime
- 用本机 Chrome / Chromium 冒充 Runtime
- x64 包配 ARM64 Runtime（反之亦然）

## WPF 业务兼容层暂保留

当前启动器仍是 `net10.0-windows` + `UseWPF=true` 的桌面应用。便携发布 **关闭 IL 修剪**（`PublishTrimmed=false`），避免 WPF 被裁掉。WebView2 用于页面宿主，**不**表示 WPF 兼容层已经移除。

## 系统要求

- Windows 10 1809（内部版本 17763）或更高
- Windows 11
- 与包架构匹配的 CPU：x64 包只在 x64 Windows 上；ARM64 包只在 ARM64 Windows 上

更旧的 Windows 不在完整支持范围。

 ## Java / Minecraft 不属于便携包范围
 
 本包覆盖启动器程序与 WebView2 Fixed Version。**.NET 10 Desktop Runtime 不打进压缩包**，由 `Start-PCL-CY.cmd` 在用户同意后安装。下列内容仍按游戏/实例流程获取：

- Java 运行时
- Minecraft 客户端、资产、库
- 模组加载器、模组、整合包资源
- 其它第三方游戏依赖

用户能打开启动器，不代表已经具备启动游戏的全部文件。

## 固定 Runtime 的安全更新责任

Fixed Version **不会**像 Evergreen 那样随系统自动升级。分发者/维护者必须：

- 自行跟踪 Microsoft Edge WebView2 Fixed Version 安全公告
- 更换 CAB/目录后重新跑本脚本打新包
- 不要为了“省事”改回系统 Evergreen 安装器

旧便携包会一直使用打包装入的那一版 Runtime，直到你发新包。

## x64 / ARM64 独立包

| 包 | RID | MSBuild Platform | Runtime PE |
|---|---|---|---|
| `artifacts/portable-win-x64` | `win-x64` | `x64` | `msedgewebview2.exe` machine `0x8664` |
| `artifacts/portable-win-arm64` | `win-arm64` | `ARM64` | `msedgewebview2.exe` machine `0xAA64` |

不要把一个架构的 `WebView2Runtime` 拷到另一个架构的 exe 旁边。需要两种架构就打两次包。

## 校验清单

 发布成功后阅读 `PORTABLE-VERIFY.txt`，至少确认：
 
 - `SelfContained=false`、`PublishSingleFile=true`、`PublishTrimmed=false`
 - `frameworks` 含 .NET Core 与 Windows Desktop 10.x，且没有 `includedFrameworks`
 - 产物没有 `windowsdesktop-runtime*.zip` / 自包含运行时目录
 - `Start-PCL-CY.cmd` 与 `Ensure-DotNet10.ps1` 存在
 - `WebView2Runtime\msedgewebview2.exe` 存在且 PE 架构匹配
 - `LICENSE` 与 `LICENCE` 仍在产物中

## 本次验证与迁移边界

 详见 [webview2-verification.md](webview2-verification.md)。当前发布合同是框架依赖：压缩包不附带 .NET 10，用户同意后才下载安装。WPF 业务兼容层仍保留；复杂自定义控件与真实游戏/账号流程不能仅凭路由测试宣称已全部等价迁移。

## 单个 exe

双击 `scripts/Publish-SingleExe.bat`（或运行 `scripts/Publish-SingleExe.ps1 -Architecture x64`）打出一个 `MOONCL.exe`。不要在其它编译还在跑时执行。它不自包含、不修剪，原生库缺失会直接失败。

这个 exe 不再把压缩包接在 PE 尾部。银狐类规则会把「很小的壳 + 文件尾超大 overlay」报成 `Trojan.Win64.SilverFox`。启动文件改成普通嵌入资源，并写入版本信息和图标。这不能代替代码签名；未签名的新文件仍可能被个别引擎误报，需要向该引擎提交误报。

启动前会列出缺少的组件。必要项是对应架构的 .NET 10 Desktop Runtime，以及 `vcruntime140.dll`、`vcruntime140_1.dll`、`msvcp140.dll`。未随包 Fixed Version、且系统也没有 WebView2 时，浏览页组件也会列出来。只有点「同意并下载」才会从微软官方地址下载安装。取消不会下载。只缺浏览页组件时可以跳过并启动。已装齐则不弹窗。

外层 zip 不再二次压缩。二次启动跳过解压，并把 .NET 单文件原生库解到固定目录，避免每次冷启动再解一遍。默认不把 WebView2 Fixed Version 打进去，以免单 exe 变成几百 MB。需要浏览页随包时再传 `-FixedRuntimeDirectory`。`WebView2Loader.dll` 和 `e_sqlite3.dll` 仍然必须在包里。

### single exe 构建速度与缓存

命令和双击入口不变。脚本默认复用 .NET 构建服务器，并将编译输出保留在仓库外：

```text
<PI_SCRATCH_DIR 或 TEMP>/pcl-cy-single-exe-cache/<仓库路径哈希>/<架构>/<Configuration>/<il 或 r2r>/build/
```

同一仓库、架构和配置连续发布时，MSBuild 可以跳过未变化的 C#/WPF 编译；源码变化仍会正常编译和还原依赖，并非 `--no-build`。首次构建、切换配置或清除缓存后仍需完整编译。`publish`、暂存目录和 ZIP 每次重新生成并清理，不复用旧发布目录，避免已删除的文件混入新包。不要同时运行多个 build/publish，工程的 `obj` 仍共享。

脚本会显示发布、原生库校验、暂存/ZIP、外层 exe 编译/PE 校验及总耗时。遇到缓存或构建服务器问题时：

```powershell
# 不复用持久编译输出（仍由 MSBuild 检查 obj；不等同于 dotnet clean）
.\scripts\Publish-SingleExe.ps1 -Architecture x64 -NoBuildCache
# 不使用构建服务器
.\scripts\Publish-SingleExe.ps1 -Architecture x64 -DisableBuildServers
```

原生库、资源哈希、PE checksum 和 overlay 检查不变。新增快速回归测试：`pwsh -File scripts/Test-SingleExeBuildCache.ps1`；原有打包规则测试仍使用 `scripts/Test-Publish-SingleExe.ps1`。
