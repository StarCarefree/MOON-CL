# 多选探索器与百科优先合并

## 使用

各类资源分别浏览。来源可多选：MCL 整合包、CurseForge、Modrinth、MC百科。默认启用 MCL、CurseForge 和 MC百科，Modrinth 默认未勾选；MCL 选择项及目录条目仍在列表最前。搜索、类型、来源和筛选集中在同一面板，上方发现区跟随当前来源及结果生成，不再固定本地预设。MCL 仅提供整合包，其他资源类型沿用各平台的支持范围。

CurseForge 和 Modrinth 各自加载、重试、分页，切换一个探索器不会清掉其他探索器的页码。搜索/游戏版本/适用的加载器/排序会发给已启用的平台；百科的专属分类值独立保留，不影响 MCL 条目或平台 API。取消某来源后隐藏其条目并停止新的浏览请求，已有请求可完成并缓存；不会取消启动器正在执行的安装或游戏更新。

## 百科优先

同时启用百科和公开平台时，按百科实际 CF/MR 项目链接与平台 Slug/项目 ID 精确对应，只保留百科条目。域名、项目路径和资源类型均校验，忽略查询参数、片段和项目下的版本/文件子路径；不按相似名称合并，不跨类型或跨平台误合并。取消百科后，公开平台条目恢复。MCL 目录不参与这项替换，始终优先保留。

去重作用于当前已加载、通过百科本地文字筛选的结果，不冒充全站完整索引。百科列表没有项目链接时，后台每次只核对一个详情，复用宿主的 15 分钟缓存与 1.5 秒限速。确认后即时去掉对应平台行；未确认时不猜测隐藏。限流、失败或超时会暂停关联队列并给出重试，页面隐藏/原生弹窗时暂停，返回后恢复。尚未完成核对时界面明确显示进度或失败提示。

百科条目仍显示百科介绍与作者外链，下载优先走对应 CurseForge 项目的原生文件列表；只有 Modrinth 关联时，也可进入 Modrinth 文件列表。后台按明确来源读取项目并再次验证资源类型，避免全数字 MR ID 被当成 CF ID。无可识别项目时保留手动下载说明。

## MCL 的 index.json

复用“服务配置 → MCL 远端目录源”的地址列表和既有 `PackCatalogClient`，不是页面内写死的新目录。主源/备用源、304/ETag、响应上限、失败保留上次成功数据、作者/加载器/版本与安装/更新流程保持不变。浏览器只向宿主发读取目录请求，不直接联网或接触 API Key。配置存在且尚未返回目录时，MCL 探索器自动请求一次；可手动刷新。启动器原有的周期检查仍独立运行。

没有配置目录地址时明确提示配置，不虚构远端数据；保留已有预设与内置 SRE 测试包。目录格式沿用现有协议：

```json
{"packs":[{"id":"mcl-example-pack","name":"示例整合包","version":"1.0","minecraft":"1.21.1","author":"作者","loader":"NeoForge","downloadUrl":"files/example.mrpack","fileName":"example.mrpack"}]}
```

相对下载/图片地址按实际成功目录 URL 解析。仅展示目录元数据不会下载归档。具体生产目录地址需在现有服务配置中提供，本次未猜测或写入任何部署地址。

## 实现与验证

`explorer-identity.js` 是独立的纯身份/合并模块，作为资源随应用打包并加入资源内容哈希。UI 的请求标识与缓存保留来源/资源类型/筛选/页码信息。新增 `mcmod links` 消息只复用受限详情读取，不接受任意抓取 URL。

```powershell
node '.\Plain Craft Launcher 2\Modules\WebUi\www\explorer-identity.test.mjs'
# PLAYWRIGHT_MODULE 可指向已有 Playwright 安装，使用系统 Edge
node '.\Plain Craft Launcher 2\Modules\WebUi\www\explorers.test.mjs'
node '.\Plain Craft Launcher 2\Modules\WebUi\www\frontend.test.mjs'
node '.\Plain Craft Launcher 2\Modules\WebUi\www\platform.test.mjs'
node '.\Plain Craft Launcher 2\Modules\WebUi\www\resource.test.mjs'
node '.\Plain Craft Launcher 2\Modules\WebUi\www\discovery.test.mjs'
dotnet test .\PCL.Core.Test\PCL.Core.Test.csproj -c Debug -p:Platform=x64 --filter 'FullyQualifiedName~PCL.Core.Test.UI'
```

170 项相关核心测试与全部上述 JS/浏览器回归通过，覆盖多选、独立分页、关联去重、暂停恢复、限流、MCL 优先和 360–1600px 布局。本机隔离 TCP/HTTP 的 index.json 读取验证通过（相对归档地址正确解析，未下载归档）。未验证具体生产目录部署、真实归档下载/安装或游戏启动。全量核心测试已有无关缓存失败，不宣称全量通过。
