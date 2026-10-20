# 实验性启动 Agent

**默认关闭。需要按实例测量，不保证所有整合包更快或绝对无错。** 在启动器实例的额外 JVM 参数中添加：

```text
-Dpclmoon.startup.enabled=true
```

启动器只对 **64 位 Java 17 / 21** 自动注入；其他 Java、外部参数文件、不完整引号或重复 Agent 保持原启动路径。Agent 以 Java 17 字节码编译，在 Java 17 和 21 分别验证。

## 按运行环境选择路径

- **Java 17：Forge 专用试验路径。** 不安装 JDK `URLClassPath` / `ZipFile` 查找补丁，避免在 Forge 模块加载上承担收益很小的通用索引开销。
- **Java 21：原有保守 JAR 位索引、整条类路径缺失判断。** 动态目录、Manifest 扩展路径、自定义 handler 等绕过或失效；追加路径/关闭前后递增 generation，在途旧结果不得重新登记。
- **Forge 原始字节正缓存：已移除。** 真实实例中 128 个已登记来源全部旁路、命中为 0，不值得保留资格检查与失效逻辑；现在只剩下面的哈希限定直接读取。
- **Forge 直接读取：** 仅在 `securejarhandler 2.1.10` 的 `ModuleClassLoader.class` SHA-256 与已检查版本完全一致时，将 `getClassBytes` 的 Optional/Stream 包装替换为直接 open/readAllBytes/close。缺失返回空数组，读取/关闭异常和 suppressed 关系保留；后续 Access Transformer、Mixin、签名和 defineClass 仍执行。其他版本/已有字节码修改不启用此替换。
- 直接读取路径已在真实 Forge 中确认执行。

## 关闭与诊断

```text
-Dpclmoon.startup.disabled=true                 # 禁用整个 Agent
-Dpclmoon.startup.forgeCache.disabled=true      # 禁用 Forge 读取优化层（保留兼容旧参数名）
-Dpclmoon.startup.forgeFastRead.disabled=true   # 同样禁用哈希限定的直接读取替换（任一为 true 即不装）
-Dpclmoon.startup.classpathCache.disabled=true  # 禁用 Java 21 整条类路径缓存
-Dpclmoon.startup.metrics=true                  # 开启诊断计数，默认关闭
-Dpclmoon.startup.durationMs=60000               # Java 21 JDK 查找缓存窗口；Java 17 仅为 metrics 打印时间，限制在 1000..120000 ms
```

删除启用参数即可恢复启动器原始注入策略。提取资源失败不追加 Agent；未知类/方法形态不修改原字节码。Agent 成功安装后保留转换器，供后续正常重转换重新应用钩子。bootstrap helper 按内容哈希命名（`pcl-moon-startup-helper-<sha256前16位>.jar`）写到 Agent JAR 同目录，跨启动复用、不写 %TEMP%。

追加 bootstrap helper 可能限制应用类 CDS 共享，这是实测时必须包含的成本。未验证任意 JVM、第三方 Agent 或 Mod 的组合，不能作无错误保证。

## 构建与检查

无需下载 Java 依赖，使用 JDK 17+（建议用 JDK 17 编译同一交付 JAR）：

```bash
python -X utf8 tools/startup-agent/build.py --javac 'C:/Program Files/Java/jdk-17/bin/javac.exe'
python -X utf8 tools/startup-agent/test.py --java 'C:/Program Files/Java/jdk-17/bin/java.exe'
python -X utf8 tools/startup-agent/test.py --java 'C:/Program Files/Zulu/zulu-21/bin/java.exe'
python -X utf8 scripts/test-benchmark-instance-startup.py
dotnet test PCL.Core.Test/PCL.Core.Test.csproj -c Release --filter 'FullyQualifiedName~StartupAccelerationPolicyTest|FullyQualifiedName~JvmLaunchTuningTest' -p:UseSharedCompilation=false
```

本轮 Java 17/21 各 10 组回归通过：资源/MR/碰撞、动态目录/Manifest、路径追加/关闭、重转换、实际计时器、204 项 Forge 缓存/变换检查及 14 项直接读取异常检查；测试包含 `-Xverify:all`。参数脚本 12 项、C# 策略相关 51 项通过。完整游戏加载、主菜单交互和存档运行是不同验收，不用“窗口出现”冒充全部完成。

## 实例计时

`scripts/benchmark-instance-startup.py` 保留现代版本 JSON 的 JVM/游戏参数，包含 Forge 模块参数；使用临时离线身份，拒绝自动进入世界/服务器，仅终止自己创建的进程。本地库解压至输出目录，记录 Mod 内容哈希、Java、阶段与 ERROR/FATAL 数量。它不下载依赖，不支持继承型/旧版 JSON。

```bash
python -X utf8 scripts/benchmark-instance-startup.py \
  --instance '<隔离副本目录>' --minecraft-root 'D:/StarRailHarpyExpress' \
  --java 'C:/Program Files/Java/jdk-17/bin/java.exe' \
  --output "artifacts/test/startup-prefetch/new-baseline" --heap 6G --timeout 180 --settle-seconds 12
# 对照仅增加 --agent。helper 会生成在 agent 同目录，先复制到产物目录再用副本，
# 避免在 Resources/ 下产生运行期文件：
cp 'Plain Craft Launcher 2/Resources/pcl-moon-startup-agent.jar' artifacts/test/startup-prefetch/
#   --agent 'artifacts/test/startup-prefetch/pcl-moon-startup-agent.jar'
```

输出目录必须不存在。`--jfr` 与 `--jvm-arg=-Dname=value` 用于独立诊断，不把带采样/统计的时间混入无采样组。`first_atlas` 只是首次图集创建日志，不是主菜单可交互时间；命令生成、原生库准备、登录及启动器准备时间不包含在 Java 进程阶段计时内。

肉鸽生化去除启动优化 Mod 后的完整样本、限制与恢复说明见 [forge-roguelike-experiment.md](forge-roguelike-experiment.md)。
