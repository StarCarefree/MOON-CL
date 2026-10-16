# 浏览页：MC 百科整合包

MC百科现同时接入整合包和数据包；数据包使用独立 class 页面与缓存命名空间。五类资源及来源限制见 [resource-browse.md](resource-browse.md)。

浏览页仍是 WPF 中的 WebView2 补充页面，原生侧栏、账号、启动、版本管理和现有 SRE 下载/更新逻辑不变。

## 使用

- SRE（`starrail-harpy-express`）固定在列表首位，不随筛选隐藏。目录卡片点击后显示详情，包含实际可用的 MC 版本、整合包版本、加载器、作者、分类和来源。缺失字段显示“未提供”，不把 MC 版本当成整合包发布版本。
- 默认启用 MCL 与 CurseForge，百科默认未勾选。百科探索器可与其他来源多选；取消后隐藏百科条目，恢复公开平台的对应条目。精确关联去重及 MR 下载衔接说明见 [explorers.md](explorers.md)。
- MC 版本、加载器、分类通过百科站点筛选后分页加载；名称/描述搜索仅作用于当前已加载条目。SRE 始终置顶且不参与筛选。
- 百科详情存在 CurseForge 项目时，“选择版本并下载”优先进入对应原生文件列表；仅关联 Modrinth 时也保留原生下载入口，由玩家选择实际版本。
- 无可识别 CF / Modrinth 项目时，保留作者网盘/项目等外链和手动下载提示；整合包可导入，数据包按作者说明放入存档。百科原页面始终可打开。
- 外链由系统浏览器打开，不会在启动器中嵌入第三方网页、执行其脚本或模拟下载成功。

## 数据适配

`PCL.Core/Minecraft/Launcher/McmodCatalog.cs` 使用 HtmlAgilityPack 读取：

- 列表：`https://www.mcmod.cn/modpack.html?page=N`，从 `modlist-block` 提取 ID、名称、英文名、简介、封面，从 `common-pages` 读取页数。
- 详情：`https://www.mcmod.cn/modpack/{id}.html`，从 `class-title`、`class-info-left`、`class-category`、`text-area` 等区块提取资料。
- 筛选：`mcver`、`api`、`category`；加载器参数依照网页实际映射（Forge=1、Fabric=2、Quilt=3、NeoForge=7）。
- 作者相关链接中的 `link.mcmod.cn/target/{Base64}` 本地解码，仅接收不含凭据的 HTTP(S) 地址。CurseForge 自动下载入口仅认可准确主机名和 `/minecraft/modpacks/{slug}` 路径，且必须是后端解析结果。

只按用户当前页/点击详情读取，不全站预抓取。HTTP 响应最大 4 MiB，网络超时 30 秒，含排队上限 35 秒，15 分钟内存缓存最多 128 项；请求串行且间隔至少 1.5 秒。遇到 HTTP 429 按 Retry-After 冷却 1–5 分钟，仍可浏览缓存，不自动连环重试。不跟随 HTTP 重定向。网站结构变化、拒绝访问或超时显示错误与重试，不冒充空列表。

`WebUiController.Mcmod.cs` 只接收受限的 `list`、`detail`、`download` 操作；页面无法指定后端请求任意 URL。列表 `requestId` 原样回传以隔离同页筛选竞争。宿主关闭后取消请求，过期 WebView 回调不会写回界面。

原有 `Resources/mcmod.buf` 仍只用于模组译名，不用于这份整合包列表。预设目录 JSON 可额外提供 `author`、`loader` 字段，旧目录无需改格式。

## 验证

```powershell
# 使用带 .NET 10 SDK 的 dotnet；构建到独立输出目录以免覆盖运行实例
# 在 PCL-CY 目录执行
dotnet test .\PCL.Core.Test\PCL.Core.Test.csproj -c Debug -p:Platform=x64 --filter 'FullyQualifiedName~PCL.Core.Test.UI'
dotnet build '.\Plain Craft Launcher 2\Plain Craft Launcher 2.csproj' -c Debug -p:Platform=x64
node --check '.\Plain Craft Launcher 2\Modules\WebUi\www\app.js'
# 安装 Playwright 或通过 PLAYWRIGHT_MODULE 指向已有安装，默认使用系统 Edge
node '.\Plain Craft Launcher 2\Modules\WebUi\www\frontend.test.mjs'
```

本轮 UI/目录核心测试 132 项通过，浏览器交互与 360–1600px 布局测试通过；真实百科首页及示例 `/modpack/1426.html` 的 HTTP 获取与解析通过。站点筛选曾触发 HTTP 429，已加冷却；冷却后单次复核筛选查询返回 HTTP 200、32 条结果。全量核心测试尝试遇到其他模块失败并超时（SQLite 文件占用、EventBus 初始化、编码及本地化等），不据此宣称全量通过。主工程 Debug 构建成功，0 个错误。未执行真实归档下载/安装、账号登录或游戏启动。
