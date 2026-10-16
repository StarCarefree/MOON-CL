# CurseForge / Modrinth 整合包浏览

## 页面行为

浏览页增加 CurseForge、Modrinth、MC百科来源切换。默认来源是 CurseForge，**不请求、不显示 MC 百科整合包**；切换 Modrinth 也默认不混入百科。MC百科可独立浏览，也可通过明确勾选“同时显示 MC百科”加入当前平台列表。SRE 和启动器预设不变，界面不额外显示置顶提示。

CurseForge / Modrinth 支持平台端关键词搜索、MC 版本和加载器筛选、相关度/下载量/更新时间/最新发布排序及分页，每页 24 条。百科专属的分类不能当成平台类别传给平台接口。关键词输入防抖，输入法组字期间不发送未完成查询。各来源/查询/页码独立缓存，请求标识隔离迟到响应。

点击平台条目显示作者、支持 MC 版本、加载器、分类、介绍及实际可用的发布标签；缺失字段显示“未提供”。“选择版本并下载”进入启动器现有原生项目文件列表，由玩家选择版本并使用既有下载/导入流程，不自动选文件或假报下载完成。网页跳转交给系统浏览器。

## 实现

- `PCL.Core/Minecraft/Launcher/PlatformPackCatalog.cs`：固定来源请求构造、纯 JSON 解析、元数据补充、2 分钟/64 项缓存和 55 秒排队/请求期限。搜索最多 200 字符、400 页；只接收整合包类型，校验项目身份。
- CurseForge：`/v1/mods/search?gameId=432&classId=4471`，`index/pageSize` 分页，`modLoaderType` 为 Forge=1、Fabric=4、Quilt=5、NeoForge=6。按文件日期而非数组首项选择已有发布标签；额外描述不可用时保留项目简介。
- Modrinth：`/v2/search` 的 AND facets 限定 `project_type:modpack`、版本及加载器。列表 `latest_version` 是引用 ID，不展示为版本号；点击详情时解析真实 `version_number`。作者优先用列表数据，独立详情可补充团队成员。
- `WebUiController.Platform.cs`：只接收 `platform` 的 list/detail/download 消息。使用现有 `ModDownload.DlModRequest`，沿用启动器鉴权和镜像设置，浏览器不能指定任意后端 URL。下载按明确来源获取工程，避免全数字 Modrinth ID 被误当 CurseForge ID。
- 所有响应回传 source、requestId、id、page。页面和宿主都检查身份，WebView 关闭后的回调不再写入页面；未修改全局下载源配置或存储 API Key。
- 第三方 HTML 介绍转为纯文本，不执行脚本。复用已存在的 HtmlAgilityPack，不新增依赖。

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
