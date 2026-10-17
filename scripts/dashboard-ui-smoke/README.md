# 我的游戏组件工作台

原生 WPF 页面，沿用 MOONCL 深灰 / 琥珀金主题，不覆盖右侧原有账号和启动操作。

- 页面右上角「添加组件」打开可搜索的组件目录；添加后进入草稿编辑模式。
- 「编辑布局」中可拖动标题排序，也可用上下移按钮。组件设置支持自定义标题与「占满一行」。
- 可选择单列 / 双列；内容宽度小于 620 DIP 时自动单列，不改变保存的偏好。
- 便签可编辑多行内容；版本快捷入口可绑定当前游戏文件夹内的已安装版本。便签和快捷入口可重复添加。
- 「保存布局」才写入 `Paths.Data/game-dashboard.json`；取消恢复本轮编辑前的状态，恢复默认也需保存才生效。
- 内置组件：当前游戏、今日问候、最近游玩、我的游戏库、日历、游玩统计、版本快捷入口、便签。日历仅展示日期，不伪造每日游玩记录。最近游玩依据 `logs/latest.log` 的修改时间，共享目录的版本合并显示，不代表精确启动历史。
- 现有自定义主页内容、社区提示、调试日志及运行中实例卡片仍保留在工作台下方。

## 验证

先将主项目编译到独立目录（不替换正在运行的启动器）：

```bash
dotnet build 'Plain Craft Launcher 2/Plain Craft Launcher 2.csproj' -c Debug -p:Platform=x64 -p:OutputPath='D:/pcl-cy/PCL-CY/artifacts/dashboard-build/' --no-restore
dotnet test PCL.Core.Test/PCL.Core.Test.csproj -c Debug -p:Platform=x64 --no-restore --filter 'FullyQualifiedName~GameDashboardLayoutTest'
dotnet run --project scripts/dashboard-ui-smoke/Dashboard.UiSmoke.csproj -- 'D:/pcl-cy/PCL-CY/artifacts/dashboard-build' "$PI_SCRATCH_DIR"
```

可用 `-p:LauncherOutput=...` 指定其他主项目构建目录，并把同一路径传给测试程序第一个参数。需要 .NET 10 SDK 与 Windows WPF 运行时；本机实际验证使用 `I:/dev/dotnet/dotnet.exe`，运行时环境 `DOTNET_ROOT` / `DOTNET_ROOT_X64` 同指向 `I:/dev/dotnet`。

UI 冒烟测试使用独立、随机命名的临时配置目录，不启动完整启动器生命周期，不登录、不下载、不启动游戏。验证 19 项：原有游戏卡片重挂载、排序、单 / 双列切换、窄窗口、取消恢复、实际组件目录搜索和添加、便签设置、无效版本入口、空布局、配置写入及新页面恢复；截图输出到临时目录。配置模型单测 11 项覆盖默认配置、深拷贝、往返、空布局、损坏 / 超大文件、字段规范化和写入失败保护。

未执行真人鼠标拖放与真实 Minecraft 启动测试；排序逻辑、替代按钮、真实弹窗和原生布局已在独立控件实例中验证。截图不包含完整启动器外壳。
