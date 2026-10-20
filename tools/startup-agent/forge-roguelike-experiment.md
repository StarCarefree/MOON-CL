# 肉鸽生化：去除启动优化 Mod 后的 Agent 试验

## 用户约束与参考价值

- 参考源码：`G:/download/LightLoad-master`。用户提供的 `lightload-1.0.1.jar` **没有被安装、加载或打包进启动器**；在用户明确只作参考后停止参考项目构建尝试。
- LightLoad 面向 Forge 1.20.1 / Java 17，模块类读取、Jar-in-Jar/模块包索引与资源查找优化有参考价值，不是 Fabric/JDK URLClassPath 位索引的直接替代。
- 源项目标注 All Rights Reserved，并说明部分资源/模型思路改编自 LGPL Lightspeed；本次没有复制其实现代码。自有实现只借鉴“转换前读取层、严格失效、可关闭”的设计方向。
- 未移植 Resolver 图闭包、Mixin 配置索引、AT 内部重写、Mod 生命周期重排等更侵入的补丁。尤其没有照搬其默认资源失败隔离：参考项目 `ResourceReloadFailureGuard` 会把部分失败 Future 转为完成，不能据此证明加载正确。

## 测试实例与隔离

原实例：`D:/StarRailHarpyExpress/versions/Roguelike Bio-肉鸽生化[导入包] v1.0`。

隔离副本：`$PI_SCRATCH_DIR/roguelike-test-root/versions/Roguelike Bio-肉鸽生化[导入包] v1.0`，共享依赖/资源只读使用 `D:/StarRailHarpyExpress`。不复制存档、截图、备份目录；不进入世界。没有删除原实例的 Mod、手工修改其 JVM 参数或替换安装的启动器 EXE。最初原样基线运行会产生游戏正常日志/缓存，不代表原目录完全没有运行时写入。

按用户要求，从**测试副本**移出以下五个启动相关 JAR：

```text
Fastload-Reforged-mc1.20.1-3.4.0.jar
lazydfu-0.1.3.jar
lightspeed-1.20.1-2.0.1hotfix.jar
modernfix-forge-5.27.85+mc1.20.1.jar
smoothboot(reloaded)-mc1.20.1-0.0.4.jar
```

备份位于 `$PI_SCRATCH_DIR/roguelike-disabled-startup-mods/`，文件 SHA-256 清单为同级 `roguelike-disabled-startup-mods.json`。恢复副本时将这些 JAR 复制回其 `mods/`，不要与新一次“无启动优化 Mod”计时混为一组。临时目录可能随会话清理；原实例仍保留原文件。

没有发现其余 Mod 将这五项声明为必需依赖。FerriteCore、Embeddium、ImmediatelyFast、Flerovium、TACZ 渲染优化等保留，避免同时改变非启动测试条件。

- Minecraft 1.20.1，Forge 47.4.0，securejarhandler 2.1.10。
- Eclipse Adoptium Java 17.0.20+8，G1，Xms512M / Xmx6G，854×480，临时离线身份。
- 原实例 62 个顶层 JAR，日志解析 68 个 Mod；移出后 57 个 JAR，日志解析 64 个 Mod。文件数和加载器解析数不应混用。
- 原始 Mod 集合 SHA-256：`84ba2df94b0bd8093cc56a9e4639b5c76d0d19a51ba03ce7d16b4bd14462f921`。
- 去除后 Mod 集合 SHA-256：`1df222c2f2c7b2980651162be06f4b1f321275705880012f94f976c5a5041c4d`；四轮正式对照均一致。

## 实现与探索结果

1. Agent 改为 Java 17 字节码，启动器显式开关支持 64 位 Java 17/21。
2. 新增短时正缓存，只存原始类字节，按 reader/reference identity 隔离，双向 clone，有 TTL、预算、文件状态检查与关闭失效。真实实例诊断中 128 个已登记来源全部旁路、**raw cache hits=0**；因此没有放宽过滤器/动态源限制来追求命中。
3. 独立实现与原始读取语义等价的直接读取路径，省去 Optional/Stream/lambda 包装。只对已检查的 `ModuleClassLoader.class` SHA-256 `62e3eaa069098d55f5da70e6dbc2a35a1e622d68804b2af6049583151bcb6f16` 启用。其他版本不做该替换。
4. 真实诊断确认直接读取路径被安装，30 秒窗口内执行约 17,388 次直接读取。后续 transform、Mixin、签名和类定义不省略。统计运行不计入下面无统计对照。
5. 初始 Java 17 同时安装通用 JDK JAR 索引的版本没有证明提速（某轮图集 50.104 秒）；仅约 4 千次缺失查询不值得据此宣称收益。最终 Java 17 已撤掉 JDK 查找补丁，使用 Forge 专用路径。Java 21 原路径保留。

## 最终无统计 A/B/B/A

A 为无 Agent，B 为自有 Forge 专用 Agent；同一副本、同一 Mod 集合、Java、堆设置、采样终点。表内时间从 Java 进程创建开始，loader→render 单独扣除最初 JVM/加载器日志前时间。

| 顺序 | 条件 | loader→render | 首批纹理图集 |
| --- | --- | ---: | ---: |
| 1 | A 基线 | 17.140 s | 38.524 s |
| 2 | B Agent | 16.427 s | 35.747 s |
| 3 | B Agent | 14.863 s | 35.281 s |
| 4 | A 基线 | 15.596 s | 34.658 s |
| 中位数 | A 基线 | 16.368 s | 36.591 s |
| 中位数 | B Agent | 15.645 s | 35.514 s |

本轮中位数变化约 **-0.723 秒（加载器至渲染）/ -1.077 秒（首批图集）**。每组仅两次，基线波动范围包含 Agent 值，**不证明每次更快或统计显著**，故仍默认关闭。不要与早期含启动优化 Mod 的 68.786 / 50.268 / 33.457 秒等不同目录、预热、采样条件的运行直接相减。

所有四轮均在图集后继续观察 12 秒，进程未提前退出，Mod 哈希相同且加载数量均为 64；8 条 ERROR 内容逐项相同：

- gsskins/yacl 的 Mixin 配置缺少 minVersion（2 条）。
- YACL 的 TACZ 配置缺少 `rps` / `forceDefaultHitAndKillSounds`（2 条）。
- Embeddium 报告被其他 Mod Mixin 修改（1 条）。
- GeckoLib 无法解析 `melee_3` 动画（1 条）。
- gd656killicon 纹理提取失败（2 条）。

这些是该基线已有问题，未在本任务中修复。没有据此声称“日志无错误”。

## 验证与限制

- Java 17 与 Java 21 各十组 Java 回归通过；包含原资源/类路径行为、204 项 Forge 缓存与实际变换测试、14 项直接读取/open/close/异常关系检查。
- Java 17 定时回归检查真实 premain 启动的 Forge 计时器；Java 21 检查原 JDK 缓存计时器。回归包含 `-Xverify:all`。
- 参数生成与 native 路径安全 12 项测试通过，启动器策略与 JVM tuning 相关 51 项 C# 测试通过。
- `first_atlas` 不是主菜单可交互时刻；未做存档/服务器/长时间游戏测试，也未测从点击启动到登录、补全下载等完整端到端时间。
- 未清空系统文件缓存，运行前 CPU 约 17%–26%，无法排除所有磁盘/网络/系统负载影响。
- 最终启动器 Release 构建通过，0 错误（有警告）；开发构建位于 `$PI_SCRATCH_DIR/forge-agent-dotnet/bin/Plain Craft Launcher 2.exe`，未覆盖用户安装的 EXE。
- 交付 Agent 为 31,003 字节，全部类 major=61；SHA-256 `ba7e3c55d8b921a6c887b01af53caa1bf7fd6da737326d1d738bf7cff6be313d`。检查确认只含自有 `pclmoon/startup` 类，不含 LightLoad 或测试 fixture。
- 缓存文件状态检查不是完整内容校验；未知来源保守旁路，不承诺任意热替换与第三方 Agent 组合无错。
- 任何验证都不等于所有 Mod 组合的无错误保证，保留整个 Agent / Forge 层 / 直接读取的关闭参数。

实际控制开关、构建与复测命令见 [README.md](README.md)。
