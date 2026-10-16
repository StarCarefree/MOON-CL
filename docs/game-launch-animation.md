# MOONCL 独立游戏启动窗口

点击“启动游戏”后打开独立、非模态的原生 WPF 窗口，不再占用主页内容区。不设置 Owner、不置顶，主启动器最小化或隐藏不会连带收起它。每次启动单独管理进度和取消，支持并发启动不同实例。

## 开关

设置 → 个性化 → 基础选项 → **启用游戏启动动画（MOONCL）**。

默认开启，沿用本地配置键 `UiShowLaunchAnimation`，下次启动游戏生效。关闭后没有独立窗口、没有桥接 JVM 参数，仍保留原启动面板；导出启动脚本也不打开窗口。

## 两阶段真实进度

1. **启动器准备**：由该启动会话的真实加载器提供 Java、账号、依赖、文件补全和启动参数等进度。
2. **游戏加载**：启动器自己读取该实例的 Fabric/Quilt 入口点与自定义进度计数，显示当前阶段的 `current/total` 和目标名称。不连接、不启动、不依赖 `mod-loading-screen`。未知总数显示 `--`，不把两个阶段拼成估算总百分比。

Forge/NeoForge/原版没有这套入口点计数，按既有游戏窗口状态交接，不虚报百分比。

收到最终入口点完成和既有游戏窗口就绪信号后，再执行启动器原有的隐藏、最小化或退出设置。此处不测量后续进入存档的加载。


## 交互与动画

- 固定双阶段布局、暖金进度条、月轨缓动与月光呼吸；阶段切换短过渡，重复快照不重启动画。
- 窗口首次显示时整窗短淡入，内容从略低、略小回到原位。不延迟 Show；最小化、关闭、结束展示，或系统关闭客户区动画时，立即停在完全不透明的最终状态。进度更新不重播入场。
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
