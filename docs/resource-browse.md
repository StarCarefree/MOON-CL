# 浏览页的五类资源

浏览页先选择“数据包 / 地图 / 整合包 / 光影包 / 材质包”，再多选 MCL、CurseForge、Modrinth、MC百科探索器。默认 MCL 与 CurseForge，MCL 目录优先；SRE 只在整合包列表出现，不额外显示置顶说明。聚合、独立分页和百科优先去重规则见 [explorers.md](explorers.md)。非整合包页面隐藏 SRE 推荐区与“导入整合包”按钮。

## 实际数据来源

| 类型 | CurseForge classId | Modrinth 查询类型 | MC百科索引 |
| --- | --- | --- | --- |
| 数据包 | 6945 | datapack（兼容响应中的旧 mod + datapack 标记） | modlist.html?api=5，详情 class/{id}.html |
| 地图 | 17 | 官方类型表没有 world，页面明确提示切换来源 | 没有可识别的独立地图索引 |
| 整合包 | 4471 | modpack | modpack.html |
| 光影包 | 6552 | shader | 没有可识别的独立光影索引 |
| 材质包 | 12 | resourcepack | 没有可识别的独立材质索引 |

不能把搜索“光影”得到的 OptiFine、Iris 等模组当成光影包，也不能把整合包的“地图”玩法标签当成地图下载。不可用的来源/类型组合在界面明确提示，不请求其他类型顶替；CurseForge 提供全部五类。

## 实现与安全边界

- `BrowseResourceTypes` 统一定义五类资源和来源支持情况。请求、返回、详情、下载消息保留 `resourceType`；原调用不传时默认 modpack。
- 平台请求使用真实分类 / project_type，返回类型必须与当前列表一致。列表、详情和作者/版本缓存均按资源类型隔离。Modrinth 数据包搜索实际可能返回 project_type=mod，仅在明确含 datapack 标记时接受。
- MC百科数据包固定使用 api=5，不混用整合包的加载器和分类编号。详情必须实际声明数据包支持；版本只从数据包分支提取，不拿 Forge 分支冒充。ID 使用 `mcmod-datapack-N`，与 `mcmod-N` 整合包分开。
- 原生下载页按 DataPack / World / ModPack / Shader / ResourcePack 进入对应文件列表，不再统一当成整合包。百科只对对应类型的 CurseForge 项目提供自动衔接；错误类型链接只作为作者外链，不误下模组。
- 光影元数据显示 Iris/OptiFine 等真实环境；非整合包列表不发送 Forge/Fabric 加载器筛选。资源下载不代表已经安装，手动放置要求仍由资源作者说明。

## 验证

```powershell
# PCL-CY 目录，.NET 10 SDK
dotnet test .\PCL.Core.Test\PCL.Core.Test.csproj -c Debug -p:Platform=x64 --filter 'FullyQualifiedName~PCL.Core.Test.UI'
node '.\Plain Craft Launcher 2\Modules\WebUi\www\frontend.test.mjs'
node '.\Plain Craft Launcher 2\Modules\WebUi\www\platform.test.mjs'
node '.\Plain Craft Launcher 2\Modules\WebUi\www\resource.test.mjs'
```

169 项相关核心测试通过。分类浏览器回归覆盖五类切换、同 ID 跨类型缓存、旧响应、SRE/导入按钮隔离、不可用来源不请求网络、百科 class/modpack 数字 ID 碰撞及 360–1600px 布局。真实接口验证 CurseForge 五类、Modrinth 四类共 9 个组合；真实百科数据包页面解析出 32 条列表和示例作者/版本。主工程 Debug 构建通过（0 错误）。未执行实际文件下载、安装或游戏启动。此前全量核心测试存在 HashCache/SQLite 等无关失败，不宣称全量通过。
