# MOONCL 独立游戏启动窗口

点击“启动游戏”后打开独立、非模态的原生 WPF 窗口，不再占用主页内容区。不设置 Owner、不置顶，主启动器最小化或隐藏不会连带收起它。每次启动单独管理进度和取消，支持并发启动不同实例。

## 开关

设置 → 个性化 → 基础选项 → **启用游戏启动动画（MOONCL）**。

默认开启，沿用本地配置键 `UiShowLaunchAnimation`，下次启动游戏生效。关闭后没有独立窗口、没有桥接 JVM 参数，仍保留原启动面板；导出启动脚本也不打开窗口。

## 两阶段真实进度

1. **启动器准备**：由该启动会话的真实加载器提供 Java、账号、依赖、文件补全和启动参数等进度。
2. **游戏加载**：从用户提供的 `G:\exmo\mod-loading-screen` 工程的原生 IPC 获取 entrypoint 阶段、模组计数、自定义进度条和完成事件。只展示当前阶段的真实百分比，不把两个阶段拼成估算总百分比。未知总数显示 `--`。

参考工程实际是 Gaming32 Mod Loading Screen 的本地衍生版，版本 1.0.5。其原协议是子 JVM stdin 二进制流，不是 TCP/JSON 或普通游戏日志。MOONCL 自带约 9 KiB Java 8 桥接 agent，把原 Swing **显示子进程**替换为无界面事件转发器；生产端和模组加载逻辑不变。没有修改 `G:\exmo` 工程/JAR，也没有复制安装参考模组。

**前提与范围**：参考模组必须已在该实例中安装，或已通过 JVM agent 配置启用；Fabric Loader ≥0.12.0 / Quilt 支持，Forge/NeoForge/原版不支持此模组的内部计数。后者不注入桥接，按既有游戏窗口状态交接。未连接、headless、禁用原 IPC 或不兼容构建不虚报百分比。桥接不绕过整合包原有的 agent/完整性策略。

收到模组 final-entrypoint 完成事件后，仍等待既有游戏窗口就绪信号，再执行启动器原有的隐藏、最小化或退出设置。此处不测量后续进入存档的加载，也不保证所有资源重载已经完成。

## 交互与动画

- 固定双阶段布局、暖金进度条、月轨缓动与月光呼吸；阶段切换短过渡，重复快照不重启动画。
- 初始 760×440，最小 540×400；长中文版本名省略并提供提示，不需要滚动。
- **收起/关闭窗口**只结束渲染，不取消游戏，进度接收继续。Java 转发进程继续排空原 IPC，避免关窗导致原模组 broken pipe 退出游戏。
- **取消启动**只取消对应启动会话；窗口不使用全局“最新实例”来决定取消对象。
- 最小化、隐藏、卸载、完成时暂停/清理动画与计时器；遵循 Windows 客户区动画开关。
- 每次启动使用独立临时目录和随机标识；通道丢失不当作成功，收尾清理临时文件。

## 验证

- 当前工作区 Debug WPF 构建：0 错误，保留仓库现有警告。
- 独立 WPF 窗口/配置/会话烟测：**47 项通过**。覆盖两阶段、未知进度、嵌入资源在工作线程提取、真实桥接输出的 C# 接收、最小化、并发取消隔离、收起后继续接收、临时文件清理与 YAML 开关持久化。
- `LaunchProgressStateTest` / `MlsProgressChannelTest`：8 项通过；核心 UI 测试：**215/215**。
- 实际本地参考 JAR 的 IPC producer → Java relay：通过，验证 entrypoint `1/4`、custom `42/100`、中文/emoji/NUL modified UTF 和包 255。此测试直接调用原 JAR IPC 方法，没有启动 Minecraft 或运行参考 agent 的 updater/guard。
- 全量核心测试：479 通过、46 失败、3 跳过。失败中有 SQLite 文件占用、事件总线未初始化等，未扩大范围修复。
- 截图：[启动器准备](images/launch-window-launcher.png)、[游戏加载](images/launch-screen.png)、[紧凑窗口](images/launch-screen-compact.png)、[完成](images/launch-window-ready.png)。界面截图使用测试数据，不冒充真实游戏启动。

复现（仓库根目录，先构建主程序，使用机器上可用的 .NET 10 SDK）：

```powershell
.\scripts\Build-LoadingBridge.ps1 -JavaHome '<JDK 9+ directory>'
.\scripts\Test-LoadingBridge.ps1 -JavaHome '<JDK directory>' `
  -MlsJar 'G:\exmo\mod-loading-screen\build\libs\mod-loading-screen-1.0.5.jar' `
  -FabricLoaderJar '<fabric-loader.jar>'
dotnet run --project .\scripts\launch-ui-smoke\Launch.UiSmoke.csproj `
  -p:LauncherBuildDir='<isolated launcher build>' -- `
  '.\docs\images' "$env:PI_SCRATCH_DIR\launch-preferences" `
  "$env:PI_SCRATCH_DIR\mls-real-ipc\observed.jsonl"
```

桥接细节见 [tools/loading-bridge/README.md](../tools/loading-bridge/README.md)。

尚未执行真实账号登录或完整 Minecraft/整合包启动；该整合包的 guard 策略、游戏入口注入兼容性需实机验证。未替换现有运行中的 EXE，未重新发布安装包。
