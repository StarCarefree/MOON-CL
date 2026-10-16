# 游戏库、预设下载与更新

## 使用

默认 WebUI 已改为游戏启动器布局：左上账号头像进入原有登录/账号面板；左侧鼠标悬停或键盘聚焦展开；下半部分实例独立滚动；目录选择固定最底部。未下载的 StarRailHarpyExpress 常驻灰色。右下角主按钮根据安装和 Packwiz 状态显示下载游戏、更新游戏或启动游戏。

- 第一次点击 **下载游戏**：优先使用开发预设 `G:\download\(PCL推荐使用)StarRailHarpyExpress V4.3.3-Release (1).zip`。文件不存在时下载 `https://gitee.com/starrailexpress/starrailexpress/releases/download/modpack/(PCL%E6%8E%A8%E8%8D%90%E4%BD%BF%E7%94%A8)StarRailHarpyExpress%20V4.3.3-Release.zip`。
- 更多操作 → **下载夸克整合包**：使用分享 `https://pan.quark.cn/s/e3f623dd58d6`，精确定位 `SRE哈比列车/4.4.0/公开版（不含地图）StarRailHarpyExpress V4.4.0-beta1.mrpack`，下载后调用启动器原生安装器。安装到新实例，不覆写已装旧版。
- 更多操作 → **导入实例**：保留启动器原有手动 ZIP/MRPACK 导入入口。
- 服务配置：填写目录 JSON 的 HTTP(S) 地址。夸克服务留空使用官方匿名 API；填写时使用下述自建解析协议。

配置保存在程序同级 `PCL/launcher-library.json`。每个受管实例在 `PCL/launcher-pack.json` 保存预设 ID 与包内版本，安装完成才写入。现有名称含 StarRailHarpyExpress 的实例也会识别为列车预设。下载/更新时禁止切换实例目录；游戏运行时不执行更新。原有微软、LittleSkin/Authlib、离线账号和其他下载设置页面仍由原生控制器处理。

## 从一个 IP 提供目录、背景、图标和整合包

提供不依赖第三方包的 Python 3.10+ 测试服务（只读原始整合包）：

```powershell
python .\scripts\serve-pack-catalog.py
# 启动器的“整合包目录地址”填 http://127.0.0.1:8091/catalog.json
```

提供给局域网其他机器时，明确指定监听地址和资源：

```powershell
python .\scripts\serve-pack-catalog.py --host 0.0.0.0 --port 8091 `
  --archive 'G:\download\(PCL推荐使用)StarRailHarpyExpress V4.3.3-Release (1).zip' `
  --icon 'C:\packs\icon.png' --background 'C:\packs\background.jpg'
# 其他机器填写 http://服务器IP:8091/catalog.json，而非 0.0.0.0
```

脚本支持并发、HEAD 和字节 Range 请求，不提供任意本地文件浏览。启动时计算 SHA256 并读取包内版本；替换包后须重启服务。开发脚本没有账户鉴权，公网部署应使用可信 HTTPS 反向代理，不应暴露私有资源。

已有服务器可以直接托管 JSON，URL 字段支持相对此 JSON 的地址。最多 100 个预设，ID 必须唯一且只含小写字母、数字、`_`、`-`。服务器不能指定客户端任意本地导入路径。

```json
{
  "packs": [{
    "id": "starrail-harpy-express",
    "name": "StarRailHarpyExpress",
    "description": "SRE 哈比列车",
    "version": "V4.3.0",
    "minecraft": "1.21.1",
    "icon": "assets/icon.png",
    "background": "assets/background.jpg",
    "downloadUrl": "packs/StarRailHarpyExpress.zip",
    "fileName": "StarRailHarpyExpress.zip",
    "sha256": "替换为真实的64位十六进制SHA256",
    "shareUrl": "https://pan.quark.cn/s/e3f623dd58d6",
    "sharePath": "SRE哈比列车/4.4.0/公开版（不含地图）StarRailHarpyExpress V4.4.0-beta1.mrpack"
  }]
}
```

`sha256` 可省略，但正式发布建议必须提供。下载流写入 `.partial`，完整下载并通过哈希检查后改名，再调用 `ModModpack.ModpackInstall`；进度、取消和错误来自真实任务。不会把 URL 解析完成当成安装完成。

## 与 mod-loading-screen 共用更新逻辑

更新源不从外层 ZIP 名称猜测，而是读取所选实例 `config/mod-loading-screen/update.properties`，支持激活源 `sources/activeSource/source.<id>.packwizUrl` 及旧 `.updatemod/config.json` 回退，尊重 `autoUpdate`。不主动切源，也不复制 Java 项目中清空 live 目录的切源策略。

测试 ZIP 已核实：

- 外层文件名标为 4.3.3，但 `modrinth.index.json` 内部 `versionId=V4.3.0`，Minecraft 1.21.1 / Fabric 0.19.3。导入后优先显示内部版本。
- 真正配置为 `http://yd.aurorian.cn:25588/pack/pack.toml`。
- 当前该地址的 Packwiz 清单是局部受管文件集，并非 4.4.0 整包发行目录；其 `version=1.0.0` 不能作为整包升级版本。

启动器在实例列表就绪后，对整合包自动检查一次。失败、超时或没有更新也会结束，并用提示给出结果，不会每 750 毫秒重试，也不会把清单请求占住启动。清单读取最多等 20 秒。本地有 `config/mod-loading-screen/update.properties` 时用它；目标没有 mod-loading-screen，或这份配置文件不存在时，仍按整合包配置里的 `packwizUrl` 更新，不因文件不存在而跳过。比较的是 `pack.toml` **原始字节 SHA256** 与缓存哈希，不是外层 ZIP 或 `versionId`。同一游戏目录不会在五分钟刷新里重复下载清单；浏览页「检查更新」可以再查一次。发现新清单时显示更新游戏。确认后找 Java 21+，调用实例或整合包里的更新器：

```text
java -jar <instance>/mod-loading-screen-*.jar --gameDir=<instance> --packwizUrl=<configured-url> --direct
```

运行前备份 `mods/config/resourcepacks/.updatemod/packwiz.json/pack.toml` 到实例 `.launcher-backups/<timestamp-id>`；失败或取消后终止子进程并恢复这些状态，保留失败日志和备份。不会处理 `saves`。成功需更新器退出码 0 且写入预期清单哈希；清单过程中变化会要求重新检查。Java 更新器没有机器可读进度协议，因此显示不确定进度，而不是虚构百分比。HTTP 来源会在确认框提示风险。

边界：这是对包自带可信更新器的封装，受管目录备份不是完整实例备份，也不保证断电/强制终止启动器时自动恢复。已有 JAR 的 Packwiz 快速校验不保证发现“文件存在但已损坏”；本功能不宣称完整修复所有文件。备份默认保留，用户确认无需回退后可手动清理。

## 夸克下载适配与当前阻碍

`G:\Quark` 是官方浏览器安装目录，并非 HTTP 下载服务。夸克网盘客户端由 `qkclouddrive:` 协议唤起，同样不是下载服务。默认仍走官方匿名 token → 分页 detail → `file/download?pr=ucpro&fr=win`，保留目标 `share_fid_token`。不使用会触发目标文件大小限制的 `fr=pc`，也不读取浏览器 Cookie。

可选自建服务：`POST <服务地址>/resolve`，请求与返回结构如下：

```json
{"shareUrl":"https://pan.quark.cn/s/e3f623dd58d6","path":"SRE哈比列车/4.4.0/公开版（不含地图）StarRailHarpyExpress V4.4.0-beta1.mrpack"}
```

```json
{"url":"https://下载服务器/有效签名地址","fileName":"StarRailHarpyExpress.mrpack","headers":{"Referer":"https://pan.quark.cn/","User-Agent":"可信服务返回的UA"}}
```

响应头只接受 Cookie、Referer、User-Agent；令牌和签名 URL 不发往 WebUI。元数据响应限制为 4 MiB，解析超时 30 秒，取消可传播。自建服务应仅提供给可信客户端。

实测（本机直接调用官方接口，未使用自建解析服务）：分享目录和目标 `.mrpack` 都存在，约 93,571,003 字节，匿名 `fr=win` 能解析出签名 URL，但 CDN GET 仍返回 **HTTP 412**。改用 `fr=pc` 时官方直接拒绝，业务码 `23018`，提示 `download file size limit`。已按官方协议唤起夸克网盘客户端 `qkclouddrive://openShareModal?pwdid=e3f623dd58d6&source=13&pwd_id=e3f623dd58d6&share_dn=e3f623dd58d6`（不带 user_token）。窗口标题为「分享链接」，监听的下载目录没有新的 `.mrpack`。浏览器本地接口 `127.0.0.1:9128` 只有通知路由，没有下载路由。因此未授权时，夸克客户端只能打开分享窗口。

已授权下载（参考 [wlor0623/quark-api](https://github.com/wlor0623/quark-api) 及用户提供的 quarkclouddrive 1.0.24）：未授权时先询问，同意后才打开 `pan.quark.cn` 授权页。DPAPI 令牌固定保存在 `%LOCALAPPDATA%/MOONCL/quark-open-session.bin`，可迁移旧缓存授权；刷新令牌成功立即落盘，不依赖下载成功。已授权的夸克下载直接定位所选 `.mrpack`/`.zip`，只转存一个文件到 `MOONCL`（不传 `save_all`，仍保留 512 MiB 本地转存保护），等待任务完成后调用 `multi_get_download_url`，不再先尝试匿名下载或回退到单文件直链接口。当前账号自己的分享收到 41017 时使用原文件 FID。CDN 只携带该会话自己的授权 Cookie，不读取浏览器 Cookie，也不把令牌/签名直链发往 WebUI。拒绝授权且无普通地址时不转存、不打开分享窗口。

转存接口可能仅返回 `{task_id, task_sync}`，不能把缺少 `status` 当成完成；必须查询 `/open/v1/task/query` 到 `status=2` 后再取 `save_as_top_fids` 或回查目录。目录回查以 `save_as.to_pdir_fid` 为准（`"0"` 也是有效的根目录）；轮询响应省略目录时保留前一响应值，未返回目录才回退到请求目录。`file/list` 的 `parent_fid` 参数实测生效，`pdir_fid` 会被忽略。响应中的 `~…|…` 编码 FID 不可与原始 FID 或另一个编码 FID 直接比较；只能依赖已限定父目录的列表，并精确匹配文件名、大小，普通 FID 父目录不匹配和结果不唯一仍需拒绝。

本次在线验证使用已有 DPAPI 授权，成功迁移到固定用户目录并从另一程序目录恢复。公开分享中的单个 `SDBF_b2_260804_hotfix2.zip`（51,517,289 字节）已完成转存任务轮询（`0 → 2`）、目标 FID 解析及下载直链验证；Range GET 返回 HTTP 206，仅读取 1 字节，没有安装或启动游戏。内置 SRE 分享在当前账号下为本人分享，服务端返回业务码 41017，不能用它作为跨账号转存成功的验证样本。早期匿名通道和未授权探测记录不代表本次结果。

浏览下载任务使用 `task.scope=browseQuark`，错误只在对应夸克版本面板显示；`catalog` 任务错误才进入 MCL 来源统计，`library`/无 scope 的旧协议任务保留原任务提示区域。关闭夸克面板或切换资源类型不会把下载错误带进首页和目录统计。

### HTTP 400 业务错误与下载上限

HTTP 400 不能在读取 JSON 前直接丢弃响应；应保留安全的 `errno`，对鉴权过期继续执行既有的单次刷新流程。`41017` 表示禁止转存当前账号自己的分享：此时只尝试使用选中文件的原 FID 请求直链，不查找新副本，下载结果标记为未产生转存。其他错误不会触发此分支。

内置 SRE 文件（93,571,003 字节）的 `/open/v1/file/get_download_url` 曾返回 HTTP 400、`errno=23018`、`error_info=download file size limit[52428800]`。这只能证明**单文件直链接口**在该请求下返回 50 MiB 限制，不能据此判断所有下载端点都限制 50 MiB；下面的 1.0.24 批量接口实测已证实存在可用路径。512 MiB 本地转存保护阈值也不等于下载权限或保证。错误中的上限只从严格匹配的数字字段生成，不回显任意服务端文本或令牌。授权后的接口失败只显示在当前下载操作区域，不再自动打开分享窗口。

### quarkclouddrive 1.0.24 大文件下载实测

静态检查用户提供的 `G:\download\quarkclouddrive-1.0.24\scripts\quark-drive.cjs`：该版本公开实现了 `POST /open/v1/file/multi_get_download_url`，请求体为 `{"fids":["已拥有文件FID"]}`；签名算法及默认客户端与现有实现相同，其网络层还附带当前账号的 `Authorization: Bearer`。下载直链仍使用该账号的 `x_pan_client_id` / `x_pan_access_token` Cookie；可选 clientToken 本次未使用。

使用现有 DPAPI 授权，只读取 `MOONCL` 中已转存的 `【最新】Sword Strange Tales v2.1.11.zip`（235,008,058 字节，约 224 MiB），不新增转存、不安装 CLI、不读取浏览器 Cookie。同一编码 FID：单文件接口返回 HTTP 400 / 23018 / 52,428,800 字节限制（附带 Bearer 也一样）；批量接口仅传一个 FID，返回 HTTP 200 / status=0 / errno=0 及可用直链。该批量请求仅带 req_id/access_token 查询参数、签名头及 Bearer，未带 device_id。

验证不仅生成直链：在偏移 52,428,800 字节读取 1 字节返回 HTTP 206；完整 GET 返回 HTTP 200，实际下载 235,008,058 字节，与网盘元数据一致。ZIP 全量 CRC 校验通过，6,587 个条目；本地 SHA256 为 `F1212EF8A29999EA8429A4AB47728D6F060A2A1AB504DE91CEF9E4F32EBF77A3`（仅本地摘要，未与服务端摘要比对）。测试文件保存在会话临时目录，没有导入或运行。

结论：当前账号通过此版本提供的批量接口可以下载这份超过 50 MiB 的已拥有文件；不能保证其他账号或文件无权限/配额限制。后续已完成启动器接入，具体行为见下节。

### 启动器接入与原生下载进度

`QuarkOpenClient.DownloadForAsync` 现在只调用批量端点并传入一个 FID；验证响应数组恰有一个文件，匹配名称、准确大小及可比较的 FID，拒绝目录、非 HTTPS/带凭据/片段地址。批量 API 请求省略 device_id、附带签名及当前会话 Bearer；刷新重试也更新 Bearer，匿名授权请求不带此头。

授权直链解析完成后，`QuarkNativeDownload` 建立真实 `LoaderCombo<string>` / `LoaderDownload`，登记 `LoaderTaskbarAdd` 并自动打开原生任务管理页。百分比、字节数、速度及取消使用启动器原生下载跟踪，不另建 Web 下载进度。下载完成并按分享文件大小校验后，再衔接现有原生整合包导入器。导入前弹出实例名，默认是压缩包文件名（去掉路径和 `.zip`/`.mrpack`），用户可改；取消则不安装。不再使用 `mcmod-<百科ID>-<随机后缀>`。同名已有实例时默认名自动加 ` (2)`。任务标题里的实例名因此是这个文件夹名，例如 `Modrinth 整合包安装：【最新】Sword Strange Tales v2.1.11`。

本机下载缓存是 `PCL-PackDownloads`，优先放在系统临时目录，空间不足时才改用 `I:\tmp-pcl`、`D:\pcl-cy\tmp` 或 `G:\download`。设置 → 启动显示这个路径和文件数，并可打开或一键清空；正在下载时拒绝清空。夸克网盘里的转存位置仍是根目录 `MOONCL`，只在设置中展示，清空不会删除网盘文件或已安装实例。

`DownloadFile.RequestHeaders` 是每个文件独立的不可变快照；带授权头的文件走原生顺序通道，不执行缺少这些头的分段探测。专用 HTTP 客户端关闭 Cookie jar 和自动跳转，避免凭据串到其他文件；日志与异常不含签名 URL/令牌。普通无请求头下载保留原通道。取消使用永久标记和启动门闩，阻止完成覆盖取消和延迟子任务重启；等待下载线程退出、清理临时文件后才返回，因此取消不会继续安装。

验证：夸克接口专项 111 项通过；原生隔离测试覆盖实时进度/速度、授权头隔离、拒绝跳转、尺寸错误、普通取消/原生取消及两种收尾竞态。真实 `QuarkOpenClient` 加原生下载器已完整下载内置 SRE 文件 93,571,003 字节，135 个 ZIP 条目 CRC 校验通过，未安装或启动游戏。全核心测试运行 625 项：573 通过、49 失败、3 跳过；失败分布在缓存/哈希数据库占用、配置/事件/本地化/网络等其他测试，夸克专项无失败，不宣称全量测试通过。

原生隔离测试（先构建启动器；测试输出目录应为 session scratch）：

```powershell
$launcher = (Resolve-Path '.\Plain Craft Launcher 2\bin\Debug\net10.0-windows\win-x64').Path
& 'D:\dotnet\dotnet.exe' run --project '.\scripts\quark-download-smoke\Quark.DownloadSmoke.csproj' -- $launcher "$env:PI_SCRATCH_DIR\quark-native-check"
# 可选 --live：显式使用已有授权，下载内置测试整合包到临时目录，不执行安装。
```

## 验证命令

```powershell
# SDK不在默认PATH时用本机有效SDK路径
& 'I:\dev\dotnet\dotnet.exe' build '.\Plain Craft Launcher 2\Plain Craft Launcher 2.csproj' `
  --no-restore -c Release -p:Platform=x64 -p:OutputPath=bin\LibraryCheck\
& 'I:\dev\dotnet\dotnet.exe' test '.\PCL.Core.Test\PCL.Core.Test.csproj' --no-restore `
  --filter 'FullyQualifiedName~PackCatalogTest|FullyQualifiedName~PackTransferTest|FullyQualifiedName~ExternalPackUpdaterTest|FullyQualifiedName~QuarkPackClientTest|FullyQualifiedName~QuarkClientLaunchTest|FullyQualifiedName~QuarkOpenClientTest'
node --check '.\Plain Craft Launcher 2\Modules\WebUi\www\app.js'
python .\scripts\tests\test_pack_catalog_server.py
```

本次隔离烟测通过真实预设 ZIP 的本机 HTTP 下载/SHA256/内部元数据读取，以及包内实际 MLS JAR 对临时 Packwiz 清单的更新和二次哈希检查；没有在既有实例上执行整包升级，也未启动游戏。
