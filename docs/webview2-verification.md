> **当前 UI 已改为原生主界面与 WebView2 浏览互补。** 最新截图、精简包校验和本轮测试见 [hybrid-ui.md](hybrid-ui.md)。下文为此前全 Web 展示/自包含阶段的历史记录，不代表当前 UI。

 # WebView2 / .NET 10 验证记录
 
 ## 打包合同（当前）
 
 便携压缩包 **不再打包 .NET 10**。发布为框架依赖（`SelfContained=false`），不附带 `windowsdesktop-runtime` zip，也不再把运行时嵌进单文件 exe。
 
 用户运行 `Start-PCL-CY.cmd`。已安装对应架构的 .NET 10 Desktop Runtime 时直接启动。未安装时先询问；只有同意后才从微软官方地址下载 exe 安装包并安装。拒绝则不下载。
 
 下面的体积、哈希和“随 EXE 包含 .NET”描述的是 **改合同之前** 的自包含包，不能当作当前产物。
 
 ## 历史自包含产物
# WebView2 / .NET 10 验证记录

## 产物

- Windows x64 Release 便携包：`artifacts/PCL-CY-portable-win-x64-Release.zip`
- 大小：403,542,365 字节。
- SHA-256：`05BDEFACA39DBB9CB2AE30312C341C667F94485B44899F450B97F629CAE58484`
- 随 EXE 包含 .NET Core / Windows Desktop 10.0.13；随包目录包含 WebView2 Fixed Version 155.0.4283.45。
- ZIP 共 265 个条目，程序、WebView2Runtime/msedgewebview2.exe、LICENSE、LICENCE、PORTABLE-VERIFY.txt 均已检查；不包含运行后产生的 PCL 用户数据。
- 已更新 CI 自包含构建，并在发布流程额外上传完整 portable ZIP。GitHub Actions 本身未在本地运行。

必须完整解压 ZIP，不能只拷贝 EXE。固定浏览器运行时不会自动更新；分发者须跟进安全版本。

## 已执行检查

1. 主项目 Debug 编译、Debug 自包含发布和 Release 便携发布通过。
2. WebView2 相关 MSTest：22/22 通过（包括 13 个新增测试用例）。
3. Node 无依赖 CDP 冒烟测试：30 个下载/设置/工具路由均有 HTML 侧栏与页面内容，无退出 WebView2 的按钮。
4. 真实 WebView2 环境验证了增量快照保留输入节点、焦点和值；按钮 Enter 仅触发一次；成组单选框方向键选择会生成回写消息。
5. 首次启动测试中，用户明确授权后才在独立副本接受协议；通过 Escape 拒绝可选遥测，确认原弹窗取消逻辑能回传。
6. JavaScript / PowerShell 语法检查和 `git diff --check` 通过。
7. 本机 Windows 11 x64 上，测试进程使用不存在的 DOTNET_ROOT / DOTNET_ROOT_X64、关闭多级查找，仍能运行自包含程序；bootstrap 确认使用随包运行时而非系统 WebView2。这不是一台无 .NET 的干净虚拟机测试。

实际页面截图：`artifacts/webui-verified.png`。测试脚本：`scripts/Test-WebUi.mjs`。

## 测试隔离

真实 UI 测试使用 Debug 副本和项目已有的 PCL_PATH / PCL_PATH_SHARED / PCL_PATH_LOCAL / PCL_PATH_TEMP 环境变量；WEBVIEW2_USER_DATA_FOLDER 与 DOTNET_BUNDLE_EXTRACT_BASE_DIR 同样使用独立目录，未关闭现有正式实例。

运行 CDP 脚本需要 Node 22+ 和测试进程的 `WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=--remote-debugging-port=9435`。首次运行的协议必须由用户自行接受，或在获得明确授权后给**测试脚本**设置 `PCL_SMOKE_ACCEPT_EULA=1`。不要把调试端口参数用于正式分发。

## 边界与未完成验收

- 目前是 HTML 展示层 + WPF 页面/业务兼容层，不是彻底移除 WPF。页面路由通过不等于所有自定义绘制、富文本链接、皮肤交互、拖放以及文件对话框都完成了等价迁移与验收。
- 未执行账号登录、实际游戏启动、整合包安装/更新/导入；未测试 ARM64 包和 Windows 10 机器。
- 本地构建未注入联机服务器配置；联机页面能显示，但联网联机功能不可据此宣称可用。未配置时显示连接失败提示，不再请求空 URL。
- 本轮全量核心测试运行结果：269 项，222 通过，47 失败；主要失败包括 SQLite 文件占用、缓存断言以及已有语言键缺失。未扩大范围修复这些业务问题。最终另外复跑的 22 项 WebView2 相关测试全部通过。
- 其他会话新增的整合包库与页面风格改动已保留；没有对其安装/更新业务做全面审查。
