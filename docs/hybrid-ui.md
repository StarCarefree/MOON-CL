# 原生主界面 + WebView2 浏览补充

## 当前方案

主窗口、固定侧栏、账号/启动区、原版本列表、版本设置，以及所有原下载/设置/工具页面继续由 .NET 10 / WPF 展示和执行。WebView2 只用于“浏览”整合包和额外服务，不再覆盖标题栏/侧栏，也不再序列化原生控件树来替代页面。

视觉参考 WeGame 的炭黑界面、固定侧栏、清晰内容层次与暖金主操作。主界面品牌是 PCL-MOON，上游能力来自 PCL CE，不使用 WeGame 品牌或商业游戏图片/假统计。“商店”入口改为“浏览”；启动按钮下方是“版本列表”和“版本设置”，版本设置右缘与启动按钮右缘对齐。没有选中版本时版本设置保持可见但禁用。版本自动更新时启动按钮改为“更新中”，不可点击，并在按钮内显示进度。

原生我的游戏只展示真实本地版本。SRE (`starrail-harpy-express` / StarRailHarpyExpress) 仍作为普通 MCL 预设出现在浏览页，卡片不再标注“测试整合包”。详情展示目录里的 Gitee 下载地址和夸克分享；下载/更新/夸克/配置继续调用现有 LauncherLibrary 后端，界面不假下载。

侧栏本地版本和旧版本列表都会把正在启动或已经启动的实例暂时排到各自列表前面，最近开始的在前。游戏结束后恢复原分类和排序，不改收藏、卡片或 PCL.ini。

浏览目录现支持 MC 百科整合包、站点筛选与专属详情 UI，SRE 继续置顶。来源和下载衔接说明见 [mcmod-browse.md](mcmod-browse.md)。

## 精简发布包（用户最终确认）

- 路径：`artifacts/PCL-CY-portable-win-x64-Release.zip`
- 大小：332,360,764 字节。
- SHA-256：`88EEE8CC3B8D1D12CF0E3DCEC4450DF5BD91E4A9647DC1E76BBB576DEB46041E`
- 当前包是框架依赖，`SelfContained=false`，**不内嵌 .NET 10**。这保留了另一处已修改、用户最终确认的精简发布策略。
- 完整解压后从 `Start-PCL-CY.cmd` 启动；若缺少对应架构 .NET 10 Desktop Runtime，启动脚本在用户同意后下载安装。
- 包内仍携带 WebView2 Fixed Version 155.0.4283.45；不可只拷走 EXE。
- 已核验 EXE、启动脚本、WebView2Runtime/msedgewebview2.exe、LICENSE、LICENCE、PORTABLE-VERIFY.txt；ZIP 不含 PCL 用户数据。

## 实际验证

- 主工程独立目录 Debug 编译/自包含测试副本发布、最终 Release 精简发布通过。隔离构建避免覆盖正在运行的调试 DLL。
- 相关单元测试 22/22 通过，确认默认原生、`--browse`/`--webview` 显式打开补充浏览，以及运行时/消息来源检查。
- 真实隔离 UI 冒烟测试通过：默认启动时 browserCreated=false 且原生可见；原启动/版本控件仍为原 MyButton，版本按钮在启动按钮下方；原版本列表正确打开，未选版本不能进入版本设置。
- 浏览页“返回我的游戏”按钮真正切回原生页面；30 个原下载/设置/工具路由均由原 WPF 控制器显示；反复往返不会销毁浏览宿主。
- 检查了搜索空结果/清除、组字中 Escape 不清空既有搜索词、浏览重进时的实际键盘焦点。
- 实际截图：`artifacts/hybrid-ui/native-0-0.png`、`artifacts/hybrid-ui/browse.png`，以及该目录中的原设置/下载/版本列表截图。
- `scripts/Test-WebUi.mjs` 使用 Node 22+ 无第三方 npm 依赖；`--hybrid-smoke` 和测试捕获仅编译到 Debug，并要求独立 PCL_UI_TEST_DIR。正式分发不能开启远程调试端口。
- 全量核心测试：333 项，287 通过，46 失败。失败主要涉及 SQLite 文件占用、缓存断言等非本次 UI 改动；未扩大范围修复。

## 边界

未执行真实账号登录、游戏启动、SRE 安装/更新、夸克解析下载，以及缺少 .NET 时的实际安装流程。未验证 ARM64 或 Windows 10。联机服务配置仍需构建/部署者提供；不因页面能显示而宣称联网功能已验证。

原生弹窗出现时暂时隐藏 WebView2 子窗口，避免 HWND 空域挡住原弹窗；返回原生不会取消已开始的整合包任务。旧版本列表、自定义主页与页面事件链均继续保留，不承诺把它们改造成 HTML。
