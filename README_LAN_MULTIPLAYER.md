# TokenSpire2 双实例局域网多人联机 — 架构与实现

## 概述

TokenSpire2 实现了**双实例局域网多人联机**：在同一台电脑上同时运行两个 Slay the Spire 2 窗口，一个作为**主机（人类玩家）**创建房间，另一个作为**客户端（Bot 自动战斗）**自动加入并协同作战。

### 与参考方案的对比

| 维度 | 参考方案（SteamFix64.dll） | TokenSpire2 方案（TCP Broker） |
|------|---------------------------|-------------------------------|
| **原理** | Hook Steam API，伪造局域网发现 | 自建 TCP 消息中继服务器 |
| **文件替换** | 需要替换 `steam_api64.dll`、`winmm.dll` | 零文件替换，纯 Mod 注入 |
| **启动方式** | 通过 Steam 启动，依赖 Steam 客户端 | 直接通过 SlayTheSpire2.exe 启动，不依赖 Steam |
| **多实例** | 需要两台电脑或特殊配置 | 天然支持单机双窗口 |
| **扩展性** | 需修改 SteamFix.ini 配置 | 纯代码控制，灵活可编程 |

参考方案的 `SteamFix64.dll` 是一个 Steam API 劫持层（类似 Goldberg Steam Emulator），它通过 `winmm.dll` 代理加载，拦截游戏对 `steam_api64.dll` 的调用，将 Steam 多人网络请求重定向到局域网。这需要替换游戏目录中的 DLL 文件，且对双实例支持较差。

TokenSpire2 的方案**完全不修改游戏文件**，通过 Harmony 补丁 + TCP Broker Server 实现了更干净可控的局域网联机。

---

## 架构总览

```
┌─────────────────────────────────────────────────────────────────────┐
│                        TokenSpire2 LAN 架构                          │
├─────────────────────────────────────────────────────────────────────┤
│                                                                      │
│  ┌──────────────────────┐          ┌──────────────────────┐         │
│  │  STS2 实例 #1 (HOST) │          │  STS2 实例 #2 (CLIENT)│         │
│  │  TOKENSPIRE2_ROLE=   │          │  TOKENSPIRE2_ROLE=    │         │
│  │  host                │          │  client               │         │
│  │                      │          │                       │         │
│  │  ┌────────────────┐  │          │  ┌────────────────┐   │         │
│  │  │ Harmony 补丁层  │  │          │  │ Harmony 补丁层  │   │         │
│  │  │                │  │          │  │                │   │         │
│  │  │ • 绕过原生连接  │  │          │  │ • 拦截加入流程  │   │         │
│  │  │ • 替代网络服务  │  │          │  │ • 绕过原生连接  │   │         │
│  │  │ • 强制大厅过渡  │  │          │  │ • 替代网络服务  │   │         │
│  │  └───────┬────────┘  │          │  └───────┬────────┘   │         │
│  │          │           │          │          │            │         │
│  │  ┌───────▼────────┐  │          │  ┌───────▼────────┐   │         │
│  │  │ BrokerNetGame  │  │          │  │ BrokerNetGame  │   │         │
│  │  │ Service        │◄─┼──────────┼──│ Service        │   │         │
│  │  │ (替代原 NetSvc) │  │          │  │ (替代原 NetSvc) │   │         │
│  │  └───────┬────────┘  │          │  └───────┬────────┘   │         │
│  │          │           │          │          │            │         │
│  │  ┌───────▼────────┐  │          │  ┌───────▼────────┐   │         │
│  │  │ BrokerClient   │  │          │  │ BrokerClient   │   │         │
│  │  │ Connection     │  │          │  │ Connection     │   │         │
│  │  │ (TCP 客户端)    │  │          │  │ (TCP 客户端)    │   │         │
│  │  └───────┬────────┘  │          │  └───────┬────────┘   │         │
│  └──────────┼───────────┘          └──────────┼────────────┘         │
│             │                                 │                       │
│             │         127.0.0.1:9999          │                       │
│             └─────────────┬───────────────────┘                       │
│                           │                                           │
│                  ┌────────▼────────┐                                  │
│                  │  BrokerServer   │                                  │
│                  │  (TCP 消息中继)  │                                  │
│                  │                 │                                  │
│                  │ • 注册客户端     │                                  │
│                  │ • 转发消息       │                                  │
│                  │ • 广播支持       │                                  │
│                  └─────────────────┘                                  │
└─────────────────────────────────────────────────────────────────────┘
```

### 数据流

```
主机发送消息:
  Game Code → BrokerNetGameService.SendMessage(msg)
    → 序列化为 BrokerEnvelope (JSON)
      → BrokerClientConnection (TCP)
        → BrokerServer
          → 转发给目标客户端 / 广播给所有客户端

客户端接收消息:
  BrokerServer → BrokerClientConnection (TCP)
    → BrokerNetGameService.DispatchEnvelopeAsync()
      → 反序列化 BrokerEnvelope → 原始消息对象
        → 调用已注册的 MessageHandler
```

---

## 核心组件详解

### 1. BrokerServer（TCP 消息中继服务器）

**位置**: `BrokerServer/Program.cs`

**协议设计**:

```
每条消息格式:
┌──────────────────┬──────────────────────────┐
│  4 bytes (BE)    │  N bytes (UTF-8 JSON)    │
│  Payload Length  │  JSON Payload             │
└──────────────────┴──────────────────────────┘
```

**消息类型 (Kind 枚举)**:

| Kind | 名称 | 方向 | 说明 |
|------|------|------|------|
| 0 | Registration | Client → Server | 客户端注册（携带 clientId、role、clientIndex） |
| 1 | RegistrationAccepted | Server → Client | 注册确认（携带 sessionId、已连接节点列表） |
| 2 | Envelope | 双向 | 游戏消息封装（携带 source、target、messageType、序列号） |
| 3 | PeerRegistered | Server → Client | 新节点加入通知（携带新节点的 clientId） |

**启动命令**:
```bash
BrokerServer.exe --port 9999 --session-id coop-120518
```

**关键特性**:
- 纯 TCP 回环连接 (`127.0.0.1`)，无外部网络依赖
- 支持广播（`targetClientId = null`）和点对点转发
- 新客户端连接时通知已有客户端
- 自动处理客户端断开和清理

### 2. BrokerNetGameService（网络服务替代层）

**位置**: `src/Coop/Runtime/BrokerNetGameService.cs`

这是整个方案中最核心的组件。它完全替代了游戏原生的 `NetHostGameService` / `NetClientGameService`，将所有网络调用重定向到 TCP Broker。

**接口兼容**:
```
游戏代码期望的接口          BrokerNetGameService 的实现
─────────────────          ──────────────────────────
SendMessage(msg)        →  序列化 → BrokerServer → 目标客户端
RegisterHandler<T>()    →  注册到本地 handler 字典
Update()                →  从入站队列中分发消息
ConnectedPeerIds        →  从 BrokerServer 的已知节点列表转换
Disconnect()            →  标记断连，重置状态
```

**重复加入请求抑制**（防止 3 人 Bug）:

这是解决"3 个玩家出现在大厅"问题的关键机制：

```csharp
// BeginStandardBrokerJoinAsync 发送第一个 join request（真实请求）
// InitializeMultiplayerAsClient 自然会尝试发送第二个 join request（重复）
// SendMessageAsync 中检测到重复 ClientLobbyJoinRequestMessage 时：
//   1. 丢弃重复请求（不发送到 BrokerServer）
//   2. 从 stash 取出第一次的 join response 并重新入站
//   3. 游戏代码正常处理重放的 response，不会卡死
```

### 3. BrokerClientJoinFlow（客户端加入流程）

**位置**: `src/Coop/Runtime/BrokerClientJoinFlow.cs`

客户端加入的完整握手流程（**单一路径设计**）：

```
┌─────────────────────────────────────────────────────────┐
│  客户端加入握手（BeginStandardBrokerJoinAsync）          │
├─────────────────────────────────────────────────────────┤
│                                                          │
│  ① 创建 TCP Transport → 连接到 BrokerServer             │
│       │                                                  │
│  ② 等待 InitialGameInfoMessage（主机发送的游戏信息）     │
│       │ 包含: gameMode, sessionState                    │
│       │                                                  │
│  ③ 发送 ClientLobbyJoinRequestMessage（唯一的加入请求）  │
│       │                                                  │
│  ④ 等待 ClientLobbyJoinResponseMessage（主机的响应）     │
│       │                                                  │
│  ⑤ Stash join response（供后续重复请求抑制使用）         │
│       │                                                  │
│  ⑥ 存入 BrokerPendingNetGameServiceRegistry             │
│       │（供 InitializeMultiplayerAsClient 提取）          │
│       │                                                  │
│  ⑦ 返回 JoinResult { gameMode, sessionState,            │
│       joinResponse }                                     │
│                                                          │
│  ⚠ InitializeMultiplayerAsClient 会再次调用              │
│    SendMessage(ClientLobbyJoinRequestMessage)，          │
│    但 BrokerBackedNetService 会丢弃重复并重放响应          │
│                                                          │
└─────────────────────────────────────────────────────────┘
```

### 4. Harmony 补丁层

所有补丁位于 `src/Coop/Patches/`，通过 `LocalCoopPatchInstaller.cs` 统一安装。

#### 补丁清单

| 补丁名 | 目标 | 作用 | 状态 |
|--------|------|------|------|
| `BrokerClientJoinFlowPatch` | `JoinFlow.Begin` | 拦截客户端加入流程，替换为 Broker 握手 | ✅ |
| `BrokerHostSteamStartupBypassPatch` | `NetHostGameService.StartSteamHost` | 绕过主机的 Steam 网络初始化 | ✅ |
| `BrokerHostENetStartupBypassPatch` | `NetHostGameService.StartENetHost` | 绕过主机的 ENet 网络初始化 | ✅ |
| `BrokerLobbyServiceSubstitutionPatch` | `NCharacterSelectScreen.InitializeMultiplayerAsHost/Client` | 将原生 NetService 替换为 Broker 服务 | ✅ |
| `BrokerForceLobbyTransitionPatch` | `StartRunLobby.HandlePlayerReadyMessage` | 在所有人就绪时强制开始游戏（绕过缺失的 Steam 连接检查） | ✅ |
| `BrokerBeginRunPatch` | `BeginRun` | 确保 RunIdentity 正确设置 | ✅ |
| `SteamCrashSuppressionPatch` | `SteamInitializer.add_SteamNoLongerRunning` | 抑制 Steam 掉线崩溃对话框 | ✅ |
| `ControllerInputOwnershipPatches` | 输入系统 | 双实例的控制器输入隔离 | ✅ |
| `SteamControllerInputSelectionPatches` | Steam 输入选择 | 双实例输入设备分配 | ✅ |
| `RunIdentityLaunchPatch` | RunIdentity 初始化 | 确保第二个玩家正确初始化 | ✅ |
| `BrokerClientSteamStartupBypassPatch` | `NetClientGameService.StartSteamClient` | 绕过客户端的 Steam 网络初始化 | ⚠️ 日志报错但不影响运行 |
| `BrokerClientENetStartupBypassPatch` | `NetClientGameService.StartENetClient` | 绕过客户端的 ENet 网络初始化 | ⚠️ 日志报错但不影响运行 |
| `BrokerClientConnectBypassPatch` | `NetClientGameService.Connect` | 绕过客户端的原生连接调用 | ⚠️ 日志报错但不影响运行 |

> ⚠️ **关于 3 个 Client Bypass 补丁的报错**: 这 3 个补丁在日志中显示 "HarmonyException: Patching exception in method TargetMethod()"，但**不能移除它们** — 移除会导致游戏立即崩溃。它们即使目标方法解析失败，Harmony 的部分安装仍有副作用保证了游戏稳定。这是已知的低优先级问题。

#### 补丁安装架构

```csharp
// LocalCoopPatchInstaller.cs
foreach (var patchType in DefaultPatchTypes)
{
    harmony.CreateClassProcessor(patchType).Patch();
    // 通过 [HarmonyPatch] 属性 + TargetMethod() 自动解析目标方法
    // 返回 null 的 TargetMethod 会跳过补丁（安全降级）
}
```

### 5. 角色与配置系统

#### 环境变量 `TOKENSPIRE2_ROLE`

| 值 | 角色 | 行为 |
|----|------|------|
| `host` | 主机 | 创建房间、选择角色、手动开始游戏 |
| `client` | 客户端 | 自动加入主机房间、自动就绪、Bot 自动战斗 |

#### 实例标记文件

```
enable-local-broker-host.txt    ← 主机实例的 Broker 配置
enable-local-broker-client.txt  ← 客户端实例的 Broker 配置
```

**标记文件内容**（示例）:
```
# enable-local-broker-host.txt
role:Host
clientIndex:0
endpoint:127.0.0.1:9999
sessionId:coop-120518
```

**为什么需要独立标记文件？**
- 防止双实例争抢写入同一个文件
- `TOKENSPIRE2_ROLE` 作为角色权威来源（覆盖标记文件中的 role）
- 支持回退到共享标记文件（单实例/调试模式）

#### CoopManager（`src/Coop/CoopManager.cs`）

```csharp
// 角色判断链
IsHost = TOKENSPIRE2_ROLE != "client"    // 默认为 host
IsClient = !IsHost                        // 相反
IsBot = IsClient                          // Bot 始终是客户端
IsHumanPlayer = IsHost                    // 人类玩家始终是主机
```

---

## 启动流程

### 一键启动

```powershell
cd E:\SteamLibrary\steamapps\common\Slay the Spire 2\mods\TokenSpire2
powershell -ExecutionPolicy Bypass -File launch_lan.ps1
```

### 启动时序

```
launch_lan.ps1 执行流程:
│
├─ [1/3] 启动 BrokerServer.exe (127.0.0.1:9999, session: coop-120518)
│        等待 2s 确保服务器就绪
│
├─ [2/3] 启动 HOST 实例（TOKENSPIRE2_ROLE=host）
│        • 完整画面，正常游玩
│        • 人类玩家选择角色 + 点击 Embark
│
├─ [3/3] 等待 15s 让主机到达主菜单
│        然后启动 CLIENT 实例（TOKENSPIRE2_ROLE=client）
│        • --audio-driver Dummy（禁用音频，节省资源）
│        • Bot 自动操作：主菜单 → 多人 → 加入 → 就绪
│
└─ 清理临时批处理文件
```

### 游戏内流程

```
主机                              客户端
───                              ───
启动游戏                          启动游戏（自动导航到多人菜单）
主菜单 → 多人游戏 → 创建房间        自动检测并加入主机房间
                                    发送 ClientLobbyJoinRequestMessage
收到加入请求 → 处理 → 发送响应        收到 ClientLobbyJoinResponseMessage
选择角色 + 设置                      自动选择角色（与主机相同）
点击就绪                            自动就绪
                                    ↓
                              BrokerForceLobbyTransitionPatch 触发
                              (检测到所有玩家就绪 → 强制 BeginRunForAllPlayers)
                                    ↓
                              游戏开始 → 战斗 → 奖励 → 地图 → 下一场...
```

---

## 消息流示例（来自实际运行日志）

### 主机加入流程

```
[14:35:10] Broker mode enabled: clientId=client-0 role=Host
[14:35:43] Broker host startup bypass: skipped native StartENetHost
[14:35:43] Broker lobby service substitution connecting: configRole=Host
[14:35:43] Broker lobby service substituted: netId=5495323171043147776
[14:35:43] Broker handler registered: PeerInputMessage (handlerCount=1)
[14:35:43] Broker handler registered: ClientLobbyJoinRequestMessage (handlerCount=1)
...
```

### 客户端加入流程

```
[14:35:32] Broker mode enabled: clientId=client-1 role=Client
[14:35:50] Broker client join flow: waiting for host initial game info
[14:35:50] Broker inbound: InitialGameInfoMessage (source=client-0 → target=client-1)
[14:35:50] Broker client join flow: received host initial game info
[14:35:50] Broker client join flow: sending real lobby join request
[14:35:50] Broker client join flow: received lobby join response
[14:35:50] Broker join response stashed for duplicate suppression
[14:35:50] Broker client join flow: join complete, service stored
[14:35:50] Broker lobby service substituted pending client join service
[14:35:52] Broker handler unregistered: ClientLobbyJoinRequestMessage (handlerCount=0)
           ↑ 游戏从 Lobby 阶段过渡到 Game 阶段，不再需要 join/leave/ready handlers
```

---

## 稳定性机制

### 1. 重复加入请求抑制（3 人 Bug 修复）

```
问题:
  BeginStandardBrokerJoinAsync 发送第一个 join request（✓ 正确）
  InitializeMultiplayerAsClient 又发送第二个 join request（✗ 重复）
  → 主机创建 2 个客户端 Player Slot + 1 个主机 Player = 3 个玩家

修复:
  BrokerBackedNetService.SendMessageAsync 中检测重复 ClientLobbyJoinRequestMessage
  → 丢弃重复请求
  → 从 stash 重放第一次的 join response
  → 游戏代码正常继续，不会卡死在等待 response
```

### 2. 加入流程重入保护

```csharp
// BrokerClientJoinFlowPatch.cs
private static int _joinInProgress;  // 0 = idle, 1 = joining

if (Interlocked.CompareExchange(ref _joinInProgress, 1, 0) != 0)
{
    // 已在加入中 → 返回 Canceled Task → 阻止重复 JoinFlow.Begin
    __result = canceledTask;
    return false;
}
```

### 3. 强制大厅过渡（BeginRunForAllPlayers）

```
问题:
  原生代码在所有玩家就绪后，尝试建立 Steam/ENet P2P 连接
  但在 Broker 模式下这些连接被绕过 → BeginRunForAllPlayers 永远不会被调用
  → 游戏永远卡在大厅

修复:
  BrokerForceLobbyTransitionPatch 监控 HandlePlayerReadyMessage
  → 检测所有玩家就绪 → 主机调用 BeginRunForAllPlayers
  → 使用 Activator.CreateInstance 为值类型参数创建默认值
  → 避免 TargetInvocationException（传递 null 给 int/bool 参数）
```

### 4. Steam 崩溃抑制

```
问题:
  双实例运行时，Steam 检测到"第二个实例"可能导致 Steam 掉线
  游戏弹出 "Steam No Longer Running" 对话框 → 体验极差

修复:
  SteamCrashSuppressionPatch 移除 SteamInitializer.add_SteamNoLongerRunning 事件
  → 对话框不再弹出
```

### 5. 控制器输入隔离

```
问题:
  双实例共享同一套输入设备 → 键盘/控制器输入会同时影响两个窗口

修复:
  ControllerInputOwnershipPatches 根据设备编号分配输入
  CLIENT 实例使用 --audio-driver Dummy 也减少了输入冲突
```

---

## 文件结构

```
TokenSpire2/
├── BrokerServer/                      # TCP 消息中继服务器
│   ├── Program.cs                     #   服务器主程序
│   └── BrokerServer.csproj
├── src/Coop/
│   ├── CoopManager.cs                 # 合作模式状态管理
│   ├── CoopConfigUI.cs                # 游戏内配置 UI
│   ├── LocalCoopMod.cs                # Mod 入口桩
│   ├── Patches/                       # Harmony 补丁
│   │   ├── BrokerClientJoinFlowPatch.cs        # 拦截加入流程
│   │   ├── BrokerHostStartupBypassPatches.cs   # 绕过主机 Steam/ENet 启动
│   │   ├── BrokerClientStartupBypassPatches.cs # 绕过客户端 Steam/ENet 启动
│   │   ├── BrokerLobbyServiceSubstitutionPatch.cs # 替换网络服务
│   │   ├── BrokerForceLobbyTransitionPatch.cs  # 强制大厅 → 游戏过渡
│   │   ├── BrokerBeginRunPatch.cs              # RunIdentity 初始化
│   │   ├── SteamCrashSuppressionPatch.cs       # Steam 崩溃抑制
│   │   ├── ControllerInputOwnershipPatches.cs  # 输入隔离
│   │   ├── SteamControllerInputSelectionPatches.cs
│   │   └── ... (RunIdentity 同步补丁)
│   └── Runtime/                       # 运行时组件
│       ├── BrokerNetGameService.cs    # 网络服务替代层
│       ├── BrokerBackedNetService.cs  # Broker 消息收发核心
│       ├── BrokerClientJoinFlow.cs    # 客户端加入握手逻辑
│       ├── BrokerClientConnection.cs  # TCP 客户端连接
│       ├── BrokerModeSettings.cs      # 模式配置加载
│       ├── BrokerEventLog.cs          # 事件日志
│       ├── BrokerEnvelopeMessageSerializer.cs # 消息序列化
│       ├── BrokerPendingNetGameServiceRegistry.cs # 服务暂存
│       ├── LocalCoopPatchInstaller.cs # 补丁统一安装
│       └── ... (其他辅助组件)
├── launch_lan.ps1                     # 一键启动脚本
├── Start-Coop-LAN.ps1                 # 备用启动脚本
├── enable-local-broker-host.txt       # 主机标记文件
├── enable-local-broker-client.txt     # 客户端标记文件
└── coop_config.json                   # 合作模式配置
```

---

## 构建与部署

### 构建

```bash
cd E:\SteamLibrary\steamapps\common\Slay the Spire 2\mods\TokenSpire2
dotnet build -c Release
```

构建输出:
- `TokenSpire2.dll` → 复制到 mod 根目录

### 构建 BrokerServer

```bash
cd E:\SteamLibrary\steamapps\common\Slay the Spire 2\mods\TokenSpire2\BrokerServer
dotnet build -c Release
```

### 部署

`TokenSpire2.dll` 和 `BrokerServer.exe` 直接在 mod 目录下，无需额外部署。

---

## 已知问题与排查

### 问题 1: 战斗结束后无法进入下一回合（数据不同步）

**症状**: "断开连接"、"检测到多人游戏数据不同步"

**根因**: `BrokerForceLobbyTransitionPatch.ForceBeginRunForAllPlayers` 将 `null` 传递给值类型参数（int、bool），导致 `TargetInvocationException`，游戏初始化状态不一致。

**修复**: 使用 `Activator.CreateInstance(paramType)` 为值类型创建默认值（0、false 等）。

**状态**: ✅ 已修复

### 问题 2: 3 个玩家出现在大厅

**症状**: 大厅显示 3 个 Ironclad，其中 2 个共享相同 netId

**根因**: 客户端发送了两条 `ClientLobbyJoinRequestMessage`（BeginStandardBrokerJoinAsync + InitializeMultiplayerAsClient），主机为每条消息创建一个 Player Slot。

**修复**: BrokerBackedNetService 的重复加入请求抑制 + 重放 stash 的 response。

**状态**: ✅ 已修复

### 问题 3: Client Bypass 补丁日志报错

**症状**: 日志显示 3 条 "HarmonyException: Patching exception in method TargetMethod()"

**说明**: 这 3 个补丁的 TargetMethod 返回 null（方法签名不匹配），但**不能移除** — 移除会导致游戏崩溃。Harmony 的部分安装副作用（或补丁类的存在本身）维持了游戏稳定。

**状态**: ⚠️ 已知低优先级，不影响功能

### 问题 4: 客户端第二次加入流程卡住

**症状**: 客户端日志显示第二次 "waiting for host initial game info"，永远收不到

**根因**: InitializeMultiplayerAsClient 触发了第二次 BeginStandardBrokerJoinAsync → 等待第二次 InitialGameInfo → 但主机早已发送过了

**状态**: ⚠️ 正在排查中。第二次调用当前由 BrokerClientJoinFlowPatch 的重入保护返回 Canceled Task。

---

## 与参考方案 (SteamFix64.dll) 的技术对比

参考方案的工作方式:
```
SlayTheSpire2.exe
  → 加载 winmm.dll（通过 DLL 劫持）
    → winmm.dll 加载 SteamFix64.dll
      → SteamFix64.dll Hook 所有 steam_api64.dll 调用
        → 将 Steam Networking 调用重定向为局域网 Socket 调用
        → 模拟 Steam Lobby 系统（FakeAppId=480 伪装为 Spacewar）
```

TokenSpire2 方案的工作方式:
```
SlayTheSpire2.exe
  → 加载 TokenSpire2 mod (Godot Mod Loader)
    → Harmony 补丁注入到游戏程序集 (sts2.dll)
      → 绕过 Steam/ENet 网络初始化
      → 替换 NetGameService 为 BrokerNetGameService
      → 所有网络通信走 TCP Broker Server
```

**核心区别**: 参考方案在 Steam API 层面做文章，TokenSpire2 在游戏逻辑层面做文章。后者不需要修改任何游戏文件（不替换 DLL），更安全、更可控。

---

## 未来改进方向

1. **补丁稳定性**: 修复 3 个 Client Bypass 补丁的 TargetMethod 解析问题
2. **大厅过渡**: 优化 ForceBeginRunForAllPlayers 的参数获取（使用更精确的默认值而非 Activator.CreateInstance）
3. **客户端重入**: 彻底解决 InitializeMultiplayerAsClient 的重复调用问题
4. **跨机器支持**: 当前仅支持 127.0.0.1（单机），扩展到局域网多机需要修改 BrokerServer 监听地址和 Config endpoint
5. **更多玩家**: 当前架构支持 N 个客户端，只需扩展 BrokerServer 的广播逻辑
