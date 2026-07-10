# TokenSpire2 — 全面技术文档 / Comprehensive Technical README

> **最后更新**: 2026-07-09
> **状态**: LAN双实例多人模式基本可用，但存在底层架构问题（见第6节）

---

## 目录

1. [项目概述](#1-项目概述)
2. [架构总览](#2-架构总览)
3. [所有已实现功能](#3-所有已实现功能)
4. [所有 Harmony 补丁清单](#4-所有-harmony-补丁清单)
5. [所有遇到的 Bug 及修复](#5-所有遇到的-bug-及修复)
6. [LAN 多人连接的实现经验](#6-lan-多人连接的实现经验)
7. [底层架构问题](#7-底层架构问题)
8. [启动流程](#8-启动流程)
9. [配置文件说明](#9-配置文件说明)
10. [文件结构](#10-文件结构)

---

## 1. 项目概述

TokenSpire2 是 Slay the Spire 2 的 MOD，包含两大核心功能：

- **自动战斗系统 (Auto-Battle)**: AI 自动进行战斗决策（出牌、选牌、地图路线、商店、事件等）
- **LAN 双实例多人模式**: 两台游戏实例通过 TCP Broker 进行局域网多人联机，一人一机

### 运行模式

| 模式 | 说明 | CoopMode | TOKENSPIRE2_ROLE |
|------|------|----------|------------------|
| 单人自动战斗 | 单实例，AI控制一切 | false | (未设置) |
| LAN Host | 双实例，本机创建房间 | true | host |
| LAN Client | 双实例，本机自动加入 | true | client |

---

## 2. 架构总览

```
┌─────────────────────────────────────────────────────────────────┐
│                      launch_lan.ps1                             │
│  启动 BrokerServer.exe → 启动 Host 实例 → 启动 Client 实例      │
└─────────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────────┐
│              BrokerServer.exe (127.0.0.1:9999)                  │
│  纯 TCP 中继：4字节大端长度前缀 + UTF-8 JSON                    │
│  Kind: 0=Registration, 1=RegistrationAccepted,                  │
│        2=Envelope(消息转发), 3=PeerRegistered                   │
└─────────────────────────────────────────────────────────────────┘
                    │                       │
            ┌───────▼───────┐       ┌───────▼───────┐
            │  STS2 HOST    │       │  STS2 CLIENT  │
            │  (Human)      │       │  (Bot)        │
            │  role=host    │       │  role=client   │
            │  IsHuman=true │       │  IsBot=true    │
            └───────────────┘       └───────────────┘
```

### 核心数据流

```
CLIENT 加入流程:
  1. AutoSlayNode: 点击 Multiplayer → Join Friends
  2. 游戏调用 JoinFlow.Begin(IClientConnectionInitializer, SceneTree)
  3. BrokerClientJoinFlowPatch.Prefix 拦截
  4. BeginStandardBrokerJoinAsync:
     a. 创建 BrokerNetGameService（TCP连接到Broker）
     b. 等待 Host 发来的 InitialGameInfoMessage
     c. 发送 ClientLobbyJoinRequestMessage（唯一的真实加入请求）
     d. 等待 ClientLobbyJoinResponseMessage
     e. Stash 响应（用于后续防重复）
     f. 存入 BrokerPendingNetGameServiceRegistry
     g. 返回 JoinResult（缓存供后续调用）
  5. 游戏调用 InitializeMultiplayerAsClient
  6. BrokerLobbyServiceSubstitutionPatch 从 Registry 取出 service
  7. 游戏代码通过 BrokerBackedNetService 发送消息（重复join被抑制）

HOST 创建流程:
  1. AutoSlayNode: 点击 Multiplayer → Host Game → Start
  2. 游戏调用 InitializeMultiplayerAsHost
  3. BrokerLobbyServiceSubstitutionPatch 注入 BrokerNetGameService
  4. 游戏代码通过 BrokerBackedNetService 收发消息

消息传输:
  Game → BrokerNetGameService → BrokerBackedNetService 
  → BrokerClientConnection(TCP) → BrokerServer → 另一实例
```

---

## 3. 所有已实现功能

### 3.1 自动战斗系统 (Auto-Battle)

#### AutoSlayNode.cs (主控制器，约4200行)
- **场景树附着**: 直接 AddChild 到 SceneTree.Root，不依赖 Harmony 补丁
- **T键切换**: 按 T 键暂停/恢复自动战斗
- **种子管理**: 从 seed.txt 读取种子，通过 NGame.DebugSeedOverride 注入
- **HP 提升**: 将所有角色 HP 设为 999（用于优化测试）
- **全角色解锁**: 自动解锁所有角色、卡牌、历史节点

#### 卡住检测 (Stuck Detection)
- **战斗卡住**: 45秒无战斗活动 → Kill 进程
- **非战斗卡住**: 45秒停留在同一屏幕 → Kill 进程
- **Coop 模式保护**: `!IsCoopMode` 守卫禁止在 co-op 模式中 Kill
- **人类玩家保护**: `!IsHumanPlayer` 守卫禁止 Kill 人类玩家的实例
- **多人等待保护**: `PlayerActionsDisabled` 时不累积卡住计时器

#### 决策引擎 (DecisionEngine.cs)
- 屏幕检测 → 决策路由 → Handler 执行

#### 求解器 (Solver/)
| 文件 | 功能 |
|------|------|
| IroncladSolver.cs | 战斗出牌求解器（贪心 + 序列优化） |
| CharacterConfigs.cs | 角色配置（优先级权重、BEFORE/AFTER规则） |
| CardRewardDecider.cs | 选牌决策（23个阶段评分） |
| MapDecider.cs | 地图路线规划 |
| ShopDecider.cs | 商店购买决策 |
| EventDecider.cs | 事件选择决策 |
| RestDecider.cs | 营火休息/升级决策 |
| CardGridDecider.cs | 升级/转化/移除选择 |
| SimpleSelectDecider.cs | 简单选择屏幕（药水等） |
| ChooseCardDecider.cs | 选牌屏幕（Armaments, True Grit 等） |
| TreasureDecider.cs | 宝箱遗物选择 |
| BundleDecider.cs | 卡牌捆绑包选择 |
| CrystalSphereDecider.cs | 水晶球选择 |
| BossStrategy.cs | Boss 特定策略 |
| ComboDatabase.cs | 卡牌组合数据库 |
| RunState.cs | 持久运行状态跟踪 |
| GameStateDetector.cs | 统一屏幕检测 |
| StateStabilityDetector.cs | 安全决策时机检测 |
| SolverParams.cs | 从 params.json 加载可调参数 |
| DecisionLogger.cs | 决策审计日志 |
| Tiebreaker.cs | 随机平局打破器 |
| BossPlayLogger.cs | Boss 战出牌详情记录 |
| UiUtils.cs | UI 工具方法 |

#### 游戏处理器 (Handlers/)
| 文件 | 功能 |
|------|------|
| CombatHandler.cs | 战斗循环控制 |
| MapHandler.cs | 地图节点选择 |
| CardRewardHandler.cs | 卡牌奖励选择 |
| ShopHandler.cs | 商店交互 |
| EventRoomHandler.cs | 事件房间交互 |
| RestSiteHandler.cs | 营火交互 |
| RewardsHandler.cs | 奖励收集 |
| TreasureRoomHandler.cs | 宝箱交互 |
| GameOverHandler.cs | 游戏结束处理 |
| CardGridHandler.cs | 卡牌网格（升级/转化） |
| SimpleCardSelectHandler.cs | 简单卡牌选择 |
| ChooseACardHandler.cs | 选一张牌 |
| ChooseABundleHandler.cs | 选捆绑包 |
| ChooseARelicHandler.cs | 选遗物 |
| CrystalSphereHandler.cs | 水晶球 |
| PotionHelper.cs | 药水使用辅助 |

### 3.2 LAN 多人模式

#### TCP Broker 系统
- **BrokerServer.exe**: 独立 TCP 中继服务器，监听 127.0.0.1:9999
- **协议**: 4字节大端长度前缀 + UTF-8 JSON
- **消息类型**: Registration(0), RegistrationAccepted(1), Envelope(2), PeerRegistered(3)

#### Coop 管理系统
- **CoopManager.cs**: 配置管理，从 coop_config.json 加载，支持运行时修改
- **CoopConfigUI.cs**: 游戏内配置 UI
- **角色判断**: IsHost/IsClient 基于 TOKENSPIRE2_ROLE 环境变量
- **标记文件**: enable-local-broker-host.txt / enable-local-broker-client.txt

#### 多人菜单导航 (AutoSlayNode.HandleMultiplayerMainMenu)
- Host: 点击 Multiplayer → Host Game → Start → 等待Client → 选角色 → Embark
- Client: 点击 Multiplayer → Join Friends → 自动加入 → 自动Ready → 自动选角色 → Embark

---

## 4. 所有 Harmony 补丁清单

### 核心补丁 (LocalCoopPatchInstaller.DefaultPatchTypes)

| 补丁类 | 目标方法 | 功能 |
|--------|----------|------|
| **BrokerClientJoinFlowPatch** | `JoinFlow.Begin` | 拦截客户端加入流程，执行 Broker 握手 |
| **BrokerHostSteamStartupBypassPatch** | `NetHostGameService.StartSteamHost` | 跳过 Steam Host 启动 |
| **BrokerHostENetStartupBypassPatch** | `NetHostGameService.StartENetHost` | 跳过 ENet Host 启动 |
| **BrokerClientSteamStartupBypassPatch** | `NetClientGameService.StartSteamClient` | 跳过 Steam Client 启动 |
| **BrokerClientENetStartupBypassPatch** | `NetClientGameService.StartENetClient` | 跳过 ENet Client 启动 |
| **BrokerClientConnectBypassPatch** | `NetClientGameService.Connect` | 跳过 P2P 连接 |
| **SteamCrashSuppressionPatch** | `SteamInitializer.add_SteamNoLongerRunning` | 阻止 "Steam应用已崩溃" 弹窗 |
| **BrokerForceLobbyTransitionPatch** | `StartRunLobby.HandlePlayerReadyMessage` 等4个方法 | 强制大厅→游戏转换 |
| **BrokerLobbyServiceSubstitutionPatch** | `NCharacterSelectScreen.InitializeMultiplayerAsHost/Client` | 注入 Broker 网络服务 |
| **BrokerBeginRunPatch** | `StartRunLobby.BeginRunForAllPlayers` | 抑制 BrokerNetGameService 类型转换异常 |
| **RunIdentityLaunchPatch** | `RunManager.Launch` | 对齐 LocalContext.NetId |
| **RunIdentityDualRoleAdventure*GuardPatch** (2个) | LocalMultiControl 多个方法 | 禁止本地双角色切换 |
| **RunIdentityLocalUiAlignmentPatch** | - | 本地 UI 对齐 |
| **RunIdentityRewardAlignmentPatch** | - | 奖励对齐 |
| **RunIdentityPotionAnimationGuardPatch** | - | 禁止药水动画 |
| **RunIdentityRelicInventoryVisualGuardPatch** | - | 禁止遗物库存视觉 |
| **RunIdentityRemoteEventUiGuardPatch** | - | 禁止远程事件 UI |
| **RunIdentityLocalActionGuardPatch** | - | 本地动作守卫 |
| **RunIdentityRemoteMutationGuardPatch** (2个) | - | 禁止远程变更 |
| **SteamControllerInputSelectionPatches** | - | Steam 控制器输入选择 |
| **ControllerInputOwnershipPatches** | - | 控制器输入所有权 |

### 已禁用/空操作补丁

| 补丁类 | 原因 |
|--------|------|
| **BrokerClientLobbyHandshakePatch** | 空操作 — 曾发送重复的 ClientLobbyJoinRequestMessage，导致3玩家bug |
| **BrokerJoinFriendScreenPatch** | 已删除 — 创建了第三条重复加入路径 |

---

## 5. 所有遇到的 Bug 及修复

### Bug #1: 3玩家出现在大厅（重大架构bug）
- **现象**: Host 创建房间后，Client 加入，大厅出现 3 个玩家（Host + 2个重复的Client）
- **根因**: ClientLobbyJoinRequestMessage 被发送了**两次**:
  1. `BeginStandardBrokerJoinAsync` 第154行发送一次
  2. `InitializeMultiplayerAsClient` 游戏代码通过 broker service 再发送一次
- **修复历程**:
  - **第一次尝试**: 删除 BrokerJoinFriendScreenPatch（第三个冗余代码路径）
  - **第二次尝试**: 在 BrokerBackedNetService 中添加重复检测（stash 响应）
  - **第三次尝试**: 将 BrokerClientLobbyHandshakePatch 改为空操作
  - **最终方案**: 保留 BrokerClientJoinFlow 的完整握手流程，并在 BrokerBackedNetService 中添加 `_stashedJoinResponse` 用于抑制重复请求
- **关键代码**: `BrokerClientJoinFlowPatch.cs` 中的 `_cachedJoinResult` 缓存和 `_joinInProgress` 并发守卫

### Bug #2: 非战斗卡住检测杀死 Co-op 实例
- **现象**: 两个实例在 45 秒后都被杀死
- **根因**: `HandleMainMenu` 的 `if (IsCoopMode) return 2.0;` 太早返回，阻止了所有导航代码执行，包括 `HandleMultiplayerMainMenu`
- **修复**: 
  1. 移除 HandleMainMenu 顶部的 co-op 提前返回
  2. 在 4 个具体的批量模式 Kill 路径上添加 `!IsCoopMode` 守卫:
     - `_runCompleteSignaled` 冻结
     - 批量 Abandon 代码
     - 玩家死亡批量信号
     - 死亡继续批量信号

### Bug #3: LocalContext.GetMe 返回 null
- **现象**: 战斗中卡牌无法打出，回合无法结束
- **根因**: `LocalContext.NetId` 未设置，导致 `LocalContext.GetMe(runState)` 返回 null
- **修复**: `RunIdentityLaunchPatch` 在 `RunManager.Launch` Postfix 中调用 `RunIdentityAlignment.AlignBrokerRun` 主动设置 NetId

### Bug #4: Steam 崩溃错误弹窗
- **现象**: 启动时弹出 "Steam应用已崩溃" 错误对话框
- **根因**: 无 Steam 运行时，`SteamInitializer` 检测到 Steam 未运行并触发事件
- **修复**: `SteamCrashSuppressionPatch` 拦截 `add_SteamNoLongerRunning`，阻止事件订阅

### Bug #5: 大厅→游戏过渡卡住
- **现象**: 两个玩家都 Ready 了但游戏不开始
- **根因**: 原生代码尝试通过 Steam/ENet 建立 P2P 连接，在 Broker 模式下连接失败
- **修复**: 
  - 3个 Client Bypass 补丁跳过 Steam/ENet Client 连接
  - `BrokerForceLobbyTransitionPatch` 检测到全员 Ready 后主动调用 `BeginRunForAllPlayers`

### Bug #6: 共享标记文件覆盖
- **现象**: Host 和 Client 共享 `enable-local-broker.txt`，Client 覆盖 Host 配置
- **根因**: 两个实例同时读写同一个文件
- **修复**: 使用每个实例独立的标记文件 `enable-local-broker-host.txt` 和 `enable-local-broker-client.txt`

### Bug #7: 客户端重复点击 "Join Friends"
- **现象**: Client 日志反复显示 "MP: Clicking Multiplayer" → "MP: Clicking join friends: JoinButton" → "Join already completed"
- **根因**: `HandleMultiplayerMainMenu` 中 Client 加入后没有正确过渡到角色选择（加入成功后 lobby UI 关闭但 char select 还没显示），所以每帧都重新找到 Multiplayer 按钮并点击
- **状态**: **未完全修复** — 这是底层架构问题的体现（见第7节）

### Bug #8: BrokerClientENetStartupBypassPatch TargetMethod 异常
- **现象**: `StartENetClient` 方法不存在时 Harmony 抛出 DynamicMethod 创建失败
- **根因**: STS2 更新后方法签名变化，`AccessTools.Method` 匹配到错误的方法
- **修复**: 使用 `AccessTools.DeclaredMethod` 进行精确名称匹配，并添加返回类型验证

### Bug #9: BrokerClientConnectBypassPatch TargetMethod 异常
- **现象**: 同上，`Connect` 方法签名变化
- **修复**: 使用 fallback 扫描逻辑 + `IsSpecialName` 过滤

### Bug #10: 非战斗卡住检测在战斗结束后误触发
- **现象**: 战斗结束后停留在奖励屏幕 → 45秒被 Kill
- **根因**: 战斗结束后 `_lastScreenType` 是 COMBAT 相关但奖励收集期间屏幕不变
- **修复**: 区分 Combat 屏幕使用 90 秒 timeout（`COMBAT_NONCOMBAT_STUCK_TIMEOUT`）

### Bug #11-20: 自动战斗系统 Bug（简要）
- **Turn skipping**: 出牌计划未考虑到实际能量消耗
- **Over-blocking**: 防御权重过高，即使敌人下回合不攻击
- **Card upgrades not happening**: 升级屏幕类型检测遗漏
- **Potions never used**: 求解器不包含药水动作空间
- **Map route deadlock**: 单路径时重复计算陷入循环
- **Card reward skip threshold**: 绝对阈值 50 导致好牌也被跳过
- **Panic button logic**: 安全时也打 PANIC_BUTTON
- **HP-cost cards blocked**: 0 能量时 HP 消耗牌被错误阻塞
- **AOE targeting**: 多敌人时不对齐最高血量敌人
- **Rest/upgrade priority**: 升级优先级低于治疗即使应升级

---

## 6. LAN 多人连接的实现经验

### 6.1 核心原理

STS2 的原生多人使用 Steam Networking / ENet 进行 P2P 连接。我们的方案用 TCP Broker 替代了所有 P2P 通信：

```
原生:  Steam/ENet P2P (Game ↔ Game)
Broker: TCP (Game → Broker → Game)
```

### 6.2 关键 Hook 点

```
┌──────────────────────────────────────────────────────┐
│  1. JoinFlow.Begin                                   │
│     ↓ BrokerClientJoinFlowPatch.Prefix               │
│     拦截客户端加入流程，执行 Broker 握手              │
├──────────────────────────────────────────────────────┤
│  2. NCharacterSelectScreen.InitializeMultiplayerAs*  │
│     ↓ BrokerLobbyServiceSubstitutionPatch.Prefix     │
│     将参数中的 NetService 替换为 BrokerNetGameService │
├──────────────────────────────────────────────────────┤
│  3. NetHostGameService.StartSteam*/StartENet*        │
│     ↓ Bypass Patches.Prefix                          │
│     跳过原生 P2P 连接启动                             │
├──────────────────────────────────────────────────────┤
│  4. RunManager.Launch                                │
│     ↓ RunIdentityLaunchPatch.Postfix                 │
│     设置 LocalContext.NetId                          │
├──────────────────────────────────────────────────────┤
│  5. StartRunLobby.HandlePlayerReadyMessage 等        │
│     ↓ BrokerForceLobbyTransitionPatch.Postfix        │
│     检测全员Ready → 主动调用 BeginRunForAllPlayers    │
└──────────────────────────────────────────────────────┘
```

### 6.3 重要教训

1. **Join 请求必须只发一次**: 多个代码路径发送 ClientLobbyJoinRequestMessage 会导致重复玩家创建
2. **NetId 必须主动设置**: `LocalContext.NetId` 不会自动设置，必须在 `RunManager.Launch` 时主动调用 `AlignBrokerRun`
3. **Bypass 不是可选的**: 即使 Broker 正常转发消息，如果原生 Steam/ENet 代码同时运行，会导致 "Multiplayer data desync" 错误
4. **标记文件隔离**: 双实例不能用同一个标记文件
5. **AutoSlayNode 的 co-op 守卫必须精确**: 过于宽泛的守卫会阻塞导航，过于宽松的守卫会导致误 Kill
6. **角色选择延迟**: Host 需要在角色选择等待 Client 加入（MP_HOST_EMBARK_DELAY = 60 秒）

### 6.4 消息流

```
Client 发送: ClientLobbyJoinRequestMessage
  → BrokerBackedNetService.SendMessageAsync
  → BrokerClientEnvelopeTransport.SendEnvelopeAsync
  → BrokerClientConnection (TCP write: 4-byte len + JSON)
  → BrokerServer (TCP relay)
  → Host 的 BrokerClientConnection
  → Host 处理 → 创建玩家槽位 → 发送 ClientLobbyJoinResponseMessage

Host 发送: LobbyPlayerSetReadyMessage
  → (同上反向)
```

---

## 7. 底层架构问题

### 7.1 当前确认的架构问题

**问题 1: HandleMultiplayerMainMenu 的线性状态机不可靠**

当前实现依赖一个线性状态机：
```
MultiplayerSubmenu → JoinGame → (等待) → CharacterSelect → Embark
```

但实际的 UI 状态转换不是线性的：
- 加入成功后 CharacterSelect 不会立即出现
- 中间可能有过渡动画、Lobby 屏幕等
- 当状态不在预期顺序中时，代码回到第一步重新点击 Multiplayer → 导致死循环

**根本问题**: UI 导航缺乏状态确认机制。代码假设点击一个按钮后就会立即进入下一个预期状态，但实际过渡时间不可预测。

**问题 2: Host 和 Client 的时序依赖脆弱**

Host 必须等待 Client 加入才能 Embark（60秒延迟），但：
- Client 可能还没启动
- Client 可能启动失败（Steam 弹窗等）
- 网络延迟不可预测

如果时序不对，就会出现 Host 在 CharacterSelect 等待而 Client 还在 Join Friend 循环的情况。

**问题 3: BrokerForceLobbyTransitionPatch 的反射检测不可靠**

`AreAllPlayersReady` 通过反射检测玩家 Ready 状态，但：
- 字段名可能随游戏更新改变
- 如果反射失败，返回 false → 永远不触发 BeginRunForAllPlayers → 游戏卡在大厅

**问题 4: 重复 Join 抑制方案复杂**

当前的 `_cachedJoinResult` + `_stashedJoinResponse` 双重缓存机制过于复杂。更简单的方案是：
- 在 `BeginStandardBrokerJoinAsync` 中**不发送** ClientLobbyJoinRequestMessage
- 只在 `InitializeMultiplayerAsClient` 中发送一次
- 但当前的实现发送了两次（因为已有的流程依赖它）

### 7.2 建议的改进方向

1. **重构为基于事件的状态机**: 不用线性步骤，而是根据当前检测到的 UI 状态决定动作
2. **添加超时和重试**: 每个步骤添加独立超时，超时后重试而非死循环
3. **解耦 Join 流程**: 让 Join 请求只在一个地方发送
4. **添加心跳检测**: Host 和 Client 之间添加心跳，用于检测对方是否存活

---

## 8. 启动流程

### 8.1 文件准备

确保以下文件存在：
```
mods/TokenSpire2/
  ├── coop_config.json          # {"CoopMode":true, "AutoBattleEnabled":true, ...}
  ├── enable-local-broker-host.txt    # 内容: Host=127.0.0.1:9999;ClientIndex=0
  ├── enable-local-broker-client.txt  # 内容: Host=127.0.0.1:9999;ClientIndex=1
  ├── params.json               # 求解器参数
  └── seed.txt                  # (可选) 固定种子
```

### 8.2 启动命令

```powershell
# 一键启动全部
cd E:\SteamLibrary\steamapps\common\Slay the Spire 2\mods\TokenSpire2
.\launch_lan.ps1

# 或手动启动
# 1. BrokerServer
BrokerServer\bin\Release\net8.0\BrokerServer.exe --port 9999 --session-id coop-test

# 2. Host
set TOKENSPIRE2_ROLE=host
SlayTheSpire2.exe

# 3. Client (15秒后)
set TOKENSPIRE2_ROLE=client
SlayTheSpire2.exe -- --audio-driver Dummy
```

### 8.3 启动时序

```
T+0s:   BrokerServer 启动，监听 127.0.0.1:9999
T+2s:   Host 实例启动，加载 TokenSpire2 mod
T+3s:   Host 到达主菜单，HandleMultiplayerMainMenu 开始导航
T+5s:   Host 创建游戏房间
T+15s:  Client 实例启动
T+18s:  Client 到达主菜单，点击 Join Friends
T+20s:  Client 通过 Broker 发送 Join 请求
T+25s:  Client 进入角色选择
T+25s:  Host 检测到 Client 加入，开始60秒倒计时
T+85s:  Host 选择角色并 Embark
```

---

## 9. 配置文件说明

### coop_config.json
```json
{
  "AutoBattleEnabled": true,    // 是否启用自动战斗
  "AutoBattlePaused": false,    // 自动战斗是否暂停（T键切换）
  "AutoBattleScope": 1,         // 0=仅战斗, 1=全部
  "CoopMode": true,             // 是否多人模式
  "BotPlayerSlot": 0,           // Bot 控制的玩家槽位
  "AutoStartEnabled": true      // 是否自动开始新局
}
```

### enable-local-broker-{role}.txt
```
Host=127.0.0.1:9999
ClientIndex=0
```

### params.json (求解器参数，约200个参数)
关键参数类别：
- `attackWeight`, `blockWeight`, `powersWeight`: 出牌权重
- `comboBonus`, `sequencingBonus`: 组合/顺序奖励
- `cardPickMinScore`: 选牌最低分阈值
- `upgradePriorityMultiplier`: 升级优先级倍数
- `stuckTimeout`: 卡住超时时间

---

## 10. 文件结构

```
mods/TokenSpire2/
├── MainFile.cs                          # MOD 入口点
├── AutoSlayNode.cs                      # 主控制器（约4200行）
├── coop_config.json                     # Coop 配置
├── params.json                          # 求解器参数
├── launch_lan.ps1                       # LAN 双实例启动脚本
├── BrokerServer/                        # TCP Broker 服务器
│   └── Program.cs                       # 单文件，约320行
├── src/
│   ├── AutoSlayCardSelector.cs          # 卡牌选择辅助
│   ├── AutoSlayHelpers.cs               # UI 查找辅助
│   ├── AutoSlayPatch.cs                 # 自动战斗补丁
│   ├── Coop/
│   │   ├── CoopManager.cs               # Coop 配置管理
│   │   ├── CoopConfigUI.cs              # 游戏内配置 UI
│   │   ├── LocalCoopMod.cs              # LocalCoop Mod 定义
│   │   ├── Patches/                     # 所有 Harmony 补丁（见第4节）
│   │   │   ├── BrokerClientJoinFlowPatch.cs
│   │   │   ├── BrokerLobbyServiceSubstitutionPatch.cs
│   │   │   ├── BrokerForceLobbyTransitionPatch.cs
│   │   │   ├── BrokerClientLobbyHandshakePatch.cs
│   │   │   ├── BrokerBeginRunPatch.cs
│   │   │   ├── BrokerHostStartupBypassPatches.cs
│   │   │   ├── BrokerClientStartupBypassPatches.cs
│   │   │   ├── SteamCrashSuppressionPatch.cs
│   │   │   ├── RunIdentityLaunchPatch.cs
│   │   │   ├── RunIdentityDualRoleAdventureGuardPatch.cs
│   │   │   ├── RunIdentityLocalUiAlignmentPatch.cs
│   │   │   ├── RunIdentityRewardAlignmentPatch.cs
│   │   │   ├── RunIdentityPotionAnimationGuardPatch.cs
│   │   │   ├── RunIdentityRelicInventoryVisualGuardPatch.cs
│   │   │   ├── RunIdentityRemoteEventUiGuardPatch.cs
│   │   │   ├── RunIdentityLocalActionGuardPatch.cs
│   │   │   ├── RunIdentityRemoteMutationGuardPatch.cs
│   │   │   ├── SteamControllerInputSelectionPatches.cs
│   │   │   ├── ControllerInputOwnershipPatches.cs
│   │   │   └── (Diagnostics Patches)
│   │   └── Runtime/                     # Broker 运行时
│   │       ├── BrokerModStartup.cs
│   │       ├── BrokerModeSettings.cs
│   │       ├── BrokerClientConfig.cs
│   │       ├── BrokerClientConnection.cs
│   │       ├── BrokerClientEnvelopeTransport.cs
│   │       ├── BrokerClientJoinFlow.cs
│   │       ├── BrokerClientRole.cs
│   │       ├── BrokerClientInputMode.cs
│   │       ├── BrokerClientRegistrationInfo.cs
│   │       ├── BrokerConnectedPeer.cs
│   │       ├── BrokerControllerDeviceAssignment.cs
│   │       ├── BrokerEnvelope.cs
│   │       ├── BrokerEnvelopeMessageSerializer.cs
│   │       ├── BrokerEnvelopeTransportConnector.cs
│   │       ├── BrokerEventLog.cs
│   │       ├── BrokerHostStartupBypass.cs
│   │       ├── BrokerLobbyServiceSubstitution.cs
│   │       ├── BrokerNetGameService.cs
│   │       ├── BrokerNetServiceFactory.cs
│   │       ├── BrokerPendingNetGameServiceRegistry.cs
│   │       ├── BrokerPlayerId.cs
│   │       ├── BrokerBackedNetService.cs
│   │       ├── RunIdentityAlignment.cs
│   │       ├── RunIdentityDiagnostics.cs
│   │       ├── RunIdentityDualRoleAdventureGuard.cs
│   │       ├── RunIdentityLocalActionGuard.cs
│   │       ├── RunIdentityRemoteMutationGuard.cs
│   │       ├── LocalCoopInputRouter.cs
│   │       ├── LocalCoopPatchInstaller.cs
│   │       ├── ControllerAssignmentService.cs
│   │       ├── ControllerInputOwnership.cs
│   │       ├── SteamControllerInputSelection.cs
│   │       ├── CharacterSelectInputDiagnostics.cs
│   │       ├── PassiveTransportDiagnostics.cs
│   │       ├── TransportSeamProbe.cs
│   │       └── IBrokerEnvelopeTransport.cs
│   ├── Handlers/                        # 游戏处理器
│   │   ├── CombatHandler.cs
│   │   ├── MapHandler.cs
│   │   ├── CardRewardHandler.cs
│   │   ├── ShopHandler.cs
│   │   ├── EventRoomHandler.cs
│   │   ├── RestSiteHandler.cs
│   │   ├── RewardsHandler.cs
│   │   ├── TreasureRoomHandler.cs
│   │   ├── GameOverHandler.cs
│   │   ├── CardGridHandler.cs
│   │   ├── SimpleCardSelectHandler.cs
│   │   ├── ChooseACardHandler.cs
│   │   ├── ChooseABundleHandler.cs
│   │   ├── ChooseARelicHandler.cs
│   │   ├── CrystalSphereHandler.cs
│   │   └── PotionHelper.cs
│   ├── Solver/                          # 决策求解器
│   │   ├── IroncladSolver.cs
│   │   ├── CharacterConfigs.cs
│   │   ├── CardRewardDecider.cs
│   │   ├── MapDecider.cs
│   │   ├── ShopDecider.cs
│   │   ├── EventDecider.cs
│   │   ├── RestDecider.cs
│   │   ├── CardGridDecider.cs
│   │   ├── SimpleSelectDecider.cs
│   │   ├── ChooseCardDecider.cs
│   │   ├── RelicDecider.cs
│   │   ├── TreasureDecider.cs
│   │   ├── BundleDecider.cs
│   │   ├── CrystalSphereDecider.cs
│   │   ├── BossStrategy.cs
│   │   ├── ComboDatabase.cs
│   │   ├── MultiplayerCards.cs
│   │   ├── BattleLogger.cs
│   │   ├── BossPlayLogger.cs
│   │   ├── RunState.cs
│   │   ├── GameStateDetector.cs
│   │   ├── StateStabilityDetector.cs
│   │   ├── DecisionEngine.cs
│   │   ├── DecisionLogger.cs
│   │   ├── SolverParams.cs
│   │   ├── StatsDatabase.cs
│   │   ├── Tiebreaker.cs
│   │   ├── CardEffectReader.cs
│   │   ├── CardModelInspector.cs
│   │   └── UiUtils.cs
│   └── Llm/                            # LLM 集成（实验性）
│       ├── LlmClient.cs
│       ├── LlmConfig.cs
│       ├── GameStateSerializer.cs
│       ├── PromptStrings.cs
│       └── RunSummaryLogger.cs
└── (Python 脚本在 E:/mods/ 和 E:/code/ 目录下)
```

---

## 变更历史

| 日期 | 关键变更 |
|------|----------|
| 2026-07-05 | 初始 TokenSpire2 自动战斗系统 |
| 2026-07-06 | Couch Coop 集成，T键切换 |
| 2026-07-07 | LAN 双实例多人模式，TCP Broker |
| 2026-07-08 | 修复 AutoSlayNode co-op 守卫（Bug #2），修复 Steam 弹窗（Bug #4） |
| 2026-07-09 | 全面文档化，确认底层架构问题 |

---

> **注意**: 本文档的目标是记录**所有实现细节、所有 Bug、所有教训**，以便未来的开发者（或 AI）能够完全理解系统。如果有任何细节缺失，请补充。
