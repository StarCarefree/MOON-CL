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

启动器在实例列表就绪后，对整合包自动检查一次。本地有 `config/mod-loading-screen/update.properties` 时用它；目标没有 mod-loading-screen，或这份配置文件不存在时，仍按整合包配置里的 `packwizUrl` 更新，不因文件不存在而跳过。比较的是 `pack.toml` **原始字节 SHA256** 与缓存哈希，不是外层 ZIP 或 `versionId`。同一游戏目录不会在五分钟刷新里重复下载清单；浏览页「检查更新」可以再查一次。发现新清单时显示更新游戏。确认后找 Java 21+，调用实例或整合包里的更新器：

```text
java -jar <instance>/mod-loading-screen-*.jar --gameDir=<instance> --packwizUrl=<configured-url> --direct
```

运行前备份 `mods/config/resourcepacks/.updatemod/packwiz.json/pack.toml` 到实例 `.launcher-backups/<timestamp-id>`；失败或取消后终止子进程并恢复这些状态，保留失败日志和备份。不会处理 `saves`。成功需更新器退出码 0 且写入预期清单哈希；清单过程中变化会要求重新检查。Java 更新器没有机器可读进度协议，因此显示不确定进度，而不是虚构百分比。HTTP 来源会在确认框提示风险。

边界：这是对包自带可信更新器的封装，受管目录备份不是完整实例备份，也不保证断电/强制终止启动器时自动恢复。已有 JAR 的 Packwiz 快速校验不保证发现“文件存在但已损坏”；本功能不宣称完整修复所有文件。备份默认保留，用户确认无需回退后可手动清理。

## 夸克下载适配与当前阻碍

`G:\Quark` 是官方浏览器安装目录，并非 HTTP 下载服务。夸克网盘客户端由 `qkclouddrive:` 协议唤起，同样不是下载服务。实现没有扫描用户登录态或修改网盘，默认走官方匿名 token → 分页 detail → `file/download?pr=ucpro&fr=win`，保留目标 `share_fid_token`。不使用会触发目标文件大小限制的 `fr=pc`，也不默认转存分享。

可选自建服务：`POST <服务地址>/resolve`，请求与返回结构如下：

```json
{"shareUrl":"https://pan.quark.cn/s/e3f623dd58d6","path":"SRE哈比列车/4.4.0/公开版（不含地图）StarRailHarpyExpress V4.4.0-beta1.mrpack"}
```

```json
{"url":"https://下载服务器/有效签名地址","fileName":"StarRailHarpyExpress.mrpack","headers":{"Referer":"https://pan.quark.cn/","User-Agent":"可信服务返回的UA"}}
```

响应头只接受 Cookie、Referer、User-Agent；令牌和签名 URL 不发往 WebUI。元数据响应限制为 4 MiB，解析超时 30 秒，取消可传播。自建服务应仅提供给可信客户端。

实测（本机直接调用官方接口，未使用自建解析服务）：分享目录和目标 `.mrpack` 都存在，约 93,571,003 字节，匿名 `fr=win` 能解析出签名 URL，但 CDN GET 仍返回 **HTTP 412**。改用 `fr=pc` 时官方直接拒绝，业务码 `23018`，提示 `download file size limit`。已按官方协议唤起夸克网盘客户端 `qkclouddrive://openShareModal?pwdid=e3f623dd58d6&source=13&pwd_id=e3f623dd58d6&share_dn=e3f623dd58d6`（不带 user_token）。窗口标题为「分享链接」，监听的下载目录没有新的 `.mrpack`。浏览器本地接口 `127.0.0.1:9128` 只有 `desktop_info`、`desktop_share_visiting`、`desktop_caller` 等通知路由，没有下载路由；当时 `desktop_info.isLogin` 为 false。因此夸克启动器只能打开分享窗口，不能自动保存这个嵌套文件。412 时优先唤起该协议；未注册网盘客户端时才回退到浏览器分享页。不会把已有同名文件当作下载成功。

## 验证命令

```powershell
# SDK不在默认PATH时用本机有效SDK路径
& 'I:\dev\dotnet\dotnet.exe' build '.\Plain Craft Launcher 2\Plain Craft Launcher 2.csproj' `
  --no-restore -c Release -p:Platform=x64 -p:OutputPath=bin\LibraryCheck\
& 'I:\dev\dotnet\dotnet.exe' test '.\PCL.Core.Test\PCL.Core.Test.csproj' --no-restore `
  --filter 'FullyQualifiedName~PackCatalogTest|FullyQualifiedName~PackTransferTest|FullyQualifiedName~ExternalPackUpdaterTest|FullyQualifiedName~QuarkPackClientTest|FullyQualifiedName~QuarkClientLaunchTest'
node --check '.\Plain Craft Launcher 2\Modules\WebUi\www\app.js'
python .\scripts\tests\test_pack_catalog_server.py
```

本次隔离烟测通过真实预设 ZIP 的本机 HTTP 下载/SHA256/内部元数据读取，以及包内实际 MLS JAR 对临时 Packwiz 清单的更新和二次哈希检查；没有在既有实例上执行整包升级，也未启动游戏。
