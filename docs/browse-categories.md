# 汇总来源状态与动态分类

## 汇总消息

CurseForge、Modrinth、MC百科、MCL 的列表状态合并为一条提示：显示当前页实际可见、合并去重后的资源总数，并注明各来源返回数量、加载中、失败及不可用状态。不再同时出现多条“当前页 X 个整合包”。来源原始数量不等于去重总数。

各来源仍独立分页和重试。失败来源的重试按钮放在汇总提示内；取消该探索器后隐藏对应按钮。百科关联核对的进度/失败也并入同一消息。空选择时不显示来源汇总。

## 分类标签

分类下拉框按已选择的探索器与当前资源类型生成，选项标注来源（如 `CurseForge · Survival`、`Modrinth · 16x`）。选中某来源分类只筛选该来源，其他已启用来源保持当前未分类查询；帮助文字明确说明范围。分类请求、缓存、迟到响应按来源与资源类型隔离，列表缓存包含分类。取消来源或分类失效后清除无效选择并恢复查询。

- CurseForge：`/v1/categories?gameId=432&classId=...`。按资源 classId 筛选响应，排除根分类，列表请求传 `categoryId`。数据包/地图/光影/材质包的分类分别来自对应 classId。
- Modrinth：`/v2/tag/category`。按 project_type 提取真实标签，保留材质分辨率、光影视觉等类别，排除加载器分组；数据包若无单独标签表则使用 API 提供的 mod 类别（与实际 datapack 查询兼容）。筛选增加独立 AND facet `categories:...`，不会替换资源类型约束。
- MC百科：整合包分类保留原有编号；数据包分类从 `modlist.html?api=5` 的分类区提取 href/onclick 编号与文字。图标型分类使用站点提供的 tooltip 说明，不虚构分类名称或编号。数据包筛选同时保留 api=5 并添加 category。
- MCL 沿用远端目录元数据，不伪造平台分类；百科无独立索引的地图、材质、光影也不显示虚假标签。

标签异步加载，失败可重试；静态离线预览不请求网络。平台后台会验证所选分类确实属于当前来源与资源类型，再请求列表，防止 CF 数字编号和 MR Slug 混用。外部标签以纯文本显示。

## 验证

```powershell
# 在 PCL-CY 目录，使用 .NET 10 SDK
dotnet test .\PCL.Core.Test\PCL.Core.Test.csproj -c Debug -p:Platform=x64 --filter 'FullyQualifiedName~PCL.Core.Test.UI'
# 现有 Playwright，系统 Edge；PLAYWRIGHT_MODULE 可指向已有安装
node '.\Plain Craft Launcher 2\Modules\WebUi\www\categories.test.mjs'
node '.\Plain Craft Launcher 2\Modules\WebUi\www\explorers.test.mjs'
node '.\Plain Craft Launcher 2\Modules\WebUi\www\frontend.test.mjs'
node '.\Plain Craft Launcher 2\Modules\WebUi\www\platform.test.mjs'
node '.\Plain Craft Launcher 2\Modules\WebUi\www\resource.test.mjs'
```

180 项相关核心测试通过，上述浏览器回归通过，主工程先前 Debug 构建成功（0 错误）；最后重新构建遇到并行夸克代码 `QuarkClientLaunch.cs:73` 缺少 `IEnumerable<>` 命名空间，未改动那部分工作。真实分类响应解析：CF 数据包 9 / 地图 7 / 材质包 17 / 光影 3；MR 数据包兼容分类 19 / 材质包 29 / 光影 19；MC百科数据包 35。真实 CF 地图 Survival 和 MR 光影 fantasy 分类筛选各返回 24 条对应类型资源。未执行实际文件下载安装。全量核心测试尝试仍因无关缓存数据库占用等失败并超时，未宣称全量通过。
