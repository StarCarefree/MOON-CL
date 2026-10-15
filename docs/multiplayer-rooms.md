# 联机房间（第一阶段）

左侧 **联机房间** 使用原生 WPF。当前实现创建 / 邀请码加入 / 可选密码 / 成员列表 / 退出与房主解散 / 15 秒心跳 / 连接信息发布 / Minecraft 状态检测；不自动启动游戏、安装模组、修改防火墙或分享存档。

## 部署与使用

1. 部署现有 `mooncl-cloud`，公开 HTTPS 房间接口，见 [服务协议与反向代理](../mooncl-cloud/docs/rooms.md)。无需新增数据库、缓存、消息队列；单实例内存状态，重启会清空房间。
2. 启动器中填写服务根地址与玩家名。默认地址可通过 `MOONCL_ROOM_SERVICE` 设置；仅回环地址允许 HTTP 调试。不会把一个未部署的公网地址假装成可用服务。
3. 房主先自行启动游戏、进入世界并开放到局域网，记录实际 LAN 端口，选择连接方式创建房间。访客输入邀请码与密码加入。页面跳转不会中断心跳。
4. 访客点击 **建立 / 检测连接**，收到远端 Minecraft 状态响应后复制地址进入多人游戏。本阶段不检查模组或存档兼容性。房主退出会解散房间；掉线在 75 秒租约超时后移除。

## 三种现成的连接方式

| 方式 | 依赖 | 游戏数据路径 |
|---|---|---|
| IPv6 | 系统 TCP/IPv6，双方有公网 IPv6；房主手动放行 LAN 端口 | 访客直连房主 |
| FRP | 房主自备官方 `frpc.exe`、frps 节点凭据及分配的远程 TCP 端口 | 经过 frps；访客无须 frpc |
| P2P | 双方自备官方 `frpc.exe` 和该 frps 节点的本机凭据，使用原生 XTCP | frp 打洞后点对点；协调节点辅助，不保证所有 NAT 能成功 |

frp 适配使用 0.61+ 原生 TOML：`frpc verify -c` 校验后 `frpc -c` 启动。只接受用户明确选择的可信 `.exe` 完整路径，不自动下载或执行房间传来的程序。建议房主和访客使用相同 frp 版本。节点的 `user` 留空；XTCP 的代理名与共享密钥由房主随机生成。frps 节点要按官方文档启用/放行 XTCP 所需 UDP 与控制端口；公网 NAT 穿透必须双机实测。

- 房间协调服务 **不转发游戏流量**，没有公网房间列表，不把节点 token 发给成员。IPv6/FRP/P2P 连接由用户明确选择，失败不静默降级到收费或流量中继。
- **无 frpc / 无节点并不影响 IPv6**。XTCP 失败时解散并改用 FRP 建房，当前没有无缝迁移或自动回退。
- frpc 进程运行不代表隧道已连通。连接检测发送 Minecraft Java server-list status 并读取远端响应，不把本机 P2P 监听成功当成远端已连接。禁用服务器状态响应时可能检测失败，但可手动尝试地址。
- TLS 开启，建议提供节点 CA 文件；留空遵循 frp 默认 TLS 信任策略，并非自动完成节点证书身份验证。
- FRP/XTCP 房主仅转发 `127.0.0.1:LAN端口`；XTCP 访客仅监听 `127.0.0.1:访客端口`，不会对局域网开放入口。IPv6 则要求游戏监听 IPv6。
- 非敏感设置保存在 `%LOCALAPPDATA%\MOONCL\multiplayer.json`；密码、成员 token、frps token 不写入该文件。frpc 运行配置位于仅当前用户可读的随机临时目录，退出时删除；Windows 作业对象在启动器异常结束时终止其自有子进程。强制结束可能遗留受 ACL 保护的临时文件，但不会自动清理其他实例的目录。
- 共享邀请码、密码与 P2P 密钥只给可信好友；退出后不要继续分享地址。玩家名只是显示名，不等同于 Minecraft 正版身份认证。FRP 公网游戏端口仍需依靠 Minecraft 正版验证 / 白名单等控制访问。

## 后续“一键联机”接口

- `RoomSession.CreateAsync(... Connection: null)`：先创建等待游戏启动的房间。
- `RoomSession.PublishConnectionAsync(connection, ct)`：房主在游戏启动、发现 LAN 端口后发布完整连接信息；访客下一次心跳收到变化，会清理旧的访客隧道。
- `HybridRooms.GetLaunchTarget()` → `RoomLaunchTarget(Host, Port, RoomCode)`：取得当前有效的客户端连接目标；不启动进程、不宣称已验证兼容性。XTCP 在访客进程启动前没有目标。
- `RoomConnectivity.ProbeAsync(target, ct)`：有界、8 秒超时的 Minecraft 状态检测，可供后续游戏启动流程复用。

游戏启动、端口自动发现与一键进入服务器留待下一阶段，当前按钮明确禁用。

## 开发检查

```powershell
dotnet test .\PCL.Core.Test\PCL.Core.Test.csproj -p:Platform=x64 --filter FullyQualifiedName~Multiplayer
cd mooncl-cloud
.\gradlew.bat test
```

测试涵盖授权、退出失败清理、过期、JSON 协议、地址与端口校验、frp 配置转义与 Win32 作业对象清理、真正的远端状态响应，以及假连接/超大响应拒绝。跨公网 IPv6 / FRP / XTCP 仍需部署节点与两台设备实测。
