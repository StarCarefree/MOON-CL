# 服务端核心官方源与镜像核验

本轮以第一方 API / 文档为依据，实际读取 JSON 与 HEAD 核验下载端点；未下载这些大体积 JAR。网络、版本及镜像可用性会变化，不把第三方镜像标成官方。

## 原版 Vanilla

- 官方版本清单：<https://piston-meta.mojang.com/mc/game/version_manifest_v2.json>。读取 versions[].url/sha1，再读取版本 JSON 的 downloads.server.url/sha1/size、javaVersion.majorVersion。
- 官方文件域名：piston-data.mojang.com；HEAD 核验成功，Content-Length 与 JSON 一致。最新版本样例需要 Java 25，因此不能始终写死 Java21。
- BMCLAPI 文档：<https://bmclapidoc.bangbang93.com/>。文档规定 Mojang 域名重写与 `/version/{version}/server`。实测 BMCLAPI 元数据和文件入口返回302到分布式节点；本机有一次节点证书吊销检查离线失败，未绕过TLS校验。
- 镜像使用官方元数据提供的摘要验证，不能把客户端 JAR 当服务端。

## Paper

- 官方下载服务文档：<https://docs.papermc.io/misc/downloads-service/>；Swagger：<https://fill.papermc.io/swagger-ui/index.html>。
- 当前官方 API：`https://fill.papermc.io/v3/projects/paper/versions/{version}` 与 `/builds`；读取 `version.java.version.minimum`，在构建中选择 STABLE。实际 `/builds` 返回数组，下载字段为 `downloads["server:default"].url/name/size/checksums.sha256`。
- 官方文件域名 fill-data.papermc.io。实测1.21.10构建130元数据与HEAD成功。
- **旧 `api.papermc.io/v2` 已实测410 sunset，不能作为正常备用 API。**
- 官方要求描述性 User-Agent/联系信息，无需 API Key，不在应用中嵌入秘密。

## Purpur

- 官方文档：<https://purpurmc.org/docs/purpur/>；OpenAPI：<https://api.purpurmc.org/openapi>。
- 官方 API：`https://api.purpurmc.org/v2/purpur/{version}/{build或latest}`，响应 build、result、md5；固定构建文件为 `/{version}/{build}/download`。
- 实测1.21.10/build2535元数据与HEAD成功。接口只有MD5，没有SHA256或Java版本字段；不可声称有官方SHA256验证，Java最低版本需结合Minecraft版本规则，或显示要求供用户确认。

## FastMirror 第三方核心镜像

- 第一方代码/文档：<https://github.com/FastMirror-MC/FastMirrorServer>。
- 实测有效API主机：`https://download.fastmirror.net/api/v3`，不是某些旧配置中的 api.fastmirror.net。支持名称 Vanilla、Paper、Purpur。
- 列版本：`/api/v3/{name}`；列构建：`/api/v3/{name}/{mc_version}`；构建元数据：`/api/v3/{name}/{mc_version}/{core_version}`；下载字段download_url，摘要sha1。
- Paper样例：`/api/v3/Paper/1.21.10/build130`，文件HEAD成功，长度与Paper官方构建一致。镜像摘要SHA1与官方SHA256算法不同，不能直接比较字符串；必须对实际文件按可信元数据指定算法校验。
- 公共镜像无API Key，仍有速率限制；源策略应区分镜像优先、官方优先、仅官方，失败需明确报出尝试过的来源。

## Spigot

- 官方说明：<https://www.spigotmc.org/wiki/buildtools/>。官方不托管成品Spigot服务端JAR，要求用户本机构建；BuildTools.jar是编译工具，不是可直接启动的服务端。
- 本轮不把非官方搬运二进制伪装为官方Spigot下载。Paper/Purpur提供Bukkit插件兼容路径，不表示所有Spigot/Folia插件都完全兼容。

## 边界

下载元数据的校验与启动参数检查不等同于真实服务器启动成功。首次启动仍须用户明确接受Minecraft EULA；安装不自动接受、也不应静默启动服务端。插件为当前用户权限的可执行代码，安装/下载必须明确目标服务器与版本，不写入客户端mods目录。

## 使用路径

1. 原生“服务端管理 → 安装服务端”中选择原版 / Paper / Purpur，选择 MC 版本与构建，沿用下载源优先级设置。
2. 安装后在控制面板检查 Java 路径、内存与启动设置；首次启动必须阅读并手动接受 EULA。
3. 资源目录首项为整合包，“服务器插件”栏目从 CurseForge Bukkit / Modrinth 插件索引浏览，点击下载进入 PCL CE 原生文件版本列表。
4. 也可从 Paper / Purpur 控制面板点击“下载插件”，携带当前服务器及 MC 版本；最终仍在原生对话框中确认目标。
5. 插件下载沿用 CompFile.ToNetFile、LoaderDownload、任务栏与原有镜像地址逻辑，目标固定为所选受管理服务器的 `plugins` 文件夹，不接受网页任意路径。
6. 服务器必须完成安装并停止；版本不匹配、覆盖同名 JAR 都要求确认。混合项目中的 Fabric / 数据包文件不会当作插件，Folia-only 文件不能装到 Paper / Purpur，Purpur-only 文件不能装到 Paper。

这只是平台标记兼容性检查，不代替插件文档的依赖、最低构建与安全要求；下载完成后由用户决定是否启动服务器。

## 本轮验证

- 启动器完整 Debug x64 构建成功（0 错误，保留已有警告）。
- 服务端管理、核心目录、镜像策略、插件目标与 UI 核心针对性测试：216/216。
- 原生服务端列表/安装窗口隔离 smoke：BAML 构造、客户端/服务端切换、下拉框、紧凑布局与明暗主题检查通过；没有点击安装/启动。
- plugins / categories / explorers / frontend / platform / resource 六组浏览器测试通过。
- 原生 `CompFile` 隔离 smoke：Fabric、数据包、Paper、Folia、Paper+Fabric，主文件选择与缓存往返均通过；不启动启动器生命周期、不执行 JAR。
- 实际 API 只读解析：Vanilla 1.21.1（Java21/SHA1）、Paper 1.21.10/build130（Java21/SHA256）、Purpur 1.21.10/build2535（MD5）均成功；实际返回 BMCLAPI/FastMirror 和官方地址，未下载完整 JAR。
- FastMirror Purpur 样例仅提供 `buildlatest`；只有“最新构建”请求允许尝试，仍须匹配官方固定构建的 MD5；显式构建缺失时只走官方。
- Paper/Purpur 官方元数据必须可用；源优先级控制文件地址顺序，不代表可绕过官方元数据选择未经确认的构建。
- 全量核心测试：449 通过、47 失败、3 跳过，含既有 HashCache SQLite 文件占用和 EventBus 未初始化问题，不能声明全量通过。
- 未执行真实服务器启动或下载的插件；启动命令、EULA、文件校验/取消清理通过本地模拟测试验证。
- 一次限定范围只读审查：未发现具体缺陷。
