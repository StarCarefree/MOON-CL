# CurseForge / Modrinth 整合包浏览

现已扩展为数据包、地图、整合包、光影包、材质包五类列表；类型映射、来源可用性和分类隔离说明见 [resource-browse.md](resource-browse.md)。以下整合包说明仍适用于整合包分类。

## 页面行为

来源现在是可多选探索器，默认 MCL 与 CurseForge；Modrinth、MC百科可独立勾选，各自分页。启用百科时优先保留已确认关联的百科条目。MCL 远端目录配置、限速核对及完整聚合规则见 [explorers.md](explorers.md)。SRE 和原生安装/更新流程保留，不显示置顶提示。

CurseForge / Modrinth 支持平台端关键词搜索、MC 版本和加载器筛选、相关度/下载量/更新时间/最新发布排序及分页，每页 24 条。百科专属的分类不能当成平台类别传给平台接口。关键词输入防抖，输入法组字期间不发送未完成查询。各来源/查询/页码独立缓存，请求标识隔离迟到响应。

点击平台条目显示作者、支持 MC 版本、加载器、分类、介绍、下载量及实际可用的发布标签；缺失字段显示“未提供”。下载量来自 CurseForge `downloadCount` 与 Modrinth `downloads`，列表卡片和详情都显示，没有该字段时不显示 0。

## 实现

- `PCL.Core/Minecraft/Launcher/PlatformPackCatalog.cs`：固定来源请求构造、纯 JSON 解析、元数据补充、2 分钟/64 项缓存和 55 秒排队/请求期限。搜索最多 200 字符、400 页；只接收整合包类型，校验项目身份。
- CurseForge：`/v1/mods/search?gameId=432&classId=4471`，`index/pageSize` 分页，`modLoaderType` 为 Forge=1、Fabric=4、Quilt=5、NeoForge=6。按文件日期而非数组首项选择已有发布标签；额外描述不可用时保留项目简介。
- Modrinth：`/v2/search` 的 AND facets 限定 `project_type:modpack`、版本及加载器。列表 `latest_version` 是引用 ID，不展示为版本号；点击详情时解析真实 `version_number`。作者优先用列表数据，独立详情可补充团队成员。列表与详情都读取 `downloads`。
- `WebUiController.Platform.cs`：只接收 `platform` 的 list/detail/download 消息。使用现有 `ModDownload.DlModRequest`，沿用启动器鉴权和镜像设置，浏览器不能指定任意后端 URL。下载按明确来源获取工程，避免全数字 Modrinth ID 被误当 CurseForge ID。
- 所有响应回传 source、requestId、id、page。页面和宿主都检查身份，WebView 关闭后的回调不再写入页面；未修改全局下载源配置或存储 API Key。
- 第三方 HTML 介绍转为纯文本，不执行脚本。复用已存在的 HtmlAgilityPack，不新增依赖。Modrinth 详情里带换行的 Markdown 正文原样传给页面，由页面按安全子集渲染标题、列表、链接和图片；单行 HTML 仍剥成纯文本。详情里的「译」请求 `https://mod.mcimirror.top/translate/{curseforge|modrinth}/{id}`，点击时展开介绍；译文返回后再展开并滚到介绍。失败显示在按钮提示上，可再点重试。

## 验证方式

```powershell
# 从 PCL-CY 执行，dotnet 需 .NET 10 SDK
# 可用独立 OutputPath 避免覆盖正在运行的启动器
dotnet test .\PCL.Core.Test\PCL.Core.Test.csproj -c Debug -p:Platform=x64 --filter 'FullyQualifiedName~PlatformPackCatalogTest'
dotnet test .\PCL.Core.Test\PCL.Core.Test.csproj -c Debug -p:Platform=x64 --filter 'FullyQualifiedName~PCL.Core.Test.UI'
node --check '.\Plain Craft Launcher 2\Modules\WebUi\www\app.js'
# PLAYWRIGHT_MODULE 可指定现有 Playwright 安装；默认使用系统 Edge
node '.\Plain Craft Launcher 2\Modules\WebUi\www\frontend.test.mjs'
node '.\Plain Craft Launcher 2\Modules\WebUi\www\platform.test.mjs'
```

已验证真实 CurseForge（通过现有镜像端点）和 Modrinth 列表各 24 条、RLCraft 和 Fabulously Optimized 详情、作者和真实发布标签。未执行整合包归档下载、安装或游戏启动。

本轮新增适配器测试 12 项通过，UI/目录相关核心测试 152 项通过，原有百科/SRE 与新增平台浏览器交互测试通过，真实 CurseForge 数据的默认隔离与 360–1600px 布局检查通过。主工程 Debug 编译通过（0 错误）。全量核心测试尝试仍遭遇 HashCache 的 SQLite 文件占用等既有失败并超时，未据此宣称全量通过。
