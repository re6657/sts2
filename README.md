# TokenSpire2 — Slay the Spire 2 全自动 AI Mod

> **Godot 4.5.1 Mono | C# (.NET 9) | Harmony 补丁 | DFS 战斗求解器 | LAN 多人联机**

TokenSpire2 是一个 Slay the Spire 2 的全自动 AI 模组，支持 **Ironclad、Silent、Defect、Necrobinder、Regent** 五个角色。核心功能包括：基于 DFS 搜索的战斗出牌求解器、22 阶段卡牌抓取评分、地图寻路、事件决策、商店购买、火堆管理等非战斗决策系统。同时支持基于 ENet 的 LAN 多人联机（1 人类主机 + N 个 AI Bot）。

---

## 目录

1. [架构总览](#1-架构总览)
2. [启动与初始化流程](#2-启动与初始化流程)
3. [主循环 (AutoSlayNode._Process)](#3-主循环-autoslaynode_process)
4. [核心模块详解](#4-核心模块详解)
   - [4.1 Core 基础层](#41-core-基础层)
   - [4.2 Harmony 补丁层](#42-harmony-补丁层)
   - [4.3 Solver 决策层](#43-solver-决策层)
   - [4.4 Handlers 交互层](#44-handlers-交互层)
   - [4.5 AutoBattle 控制器](#45-autobattle-控制器)
   - [4.6 LLM 模块（已禁用）](#46-llm-模块已禁用)
5. [战斗求解器详解](#5-战斗求解器详解)
6. [非战斗决策系统](#6-非战斗决策系统)
7. [LAN 多人联机实现](#7-lan-多人联机实现)
8. [游戏内部接口对照表](#8-游戏内部接口对照表)
9. [参数系统 (params.json)](#9-参数系统-paramsjson)
10. [启动脚本](#10-启动脚本)
11. [自动化优化工作流](#11-自动化优化工作流)
12. [构建与部署](#12-构建与部署)
13. [已知限制与 Bug](#13-已知限制与-bug)
14. [版本历史](#14-版本历史)

---

## 1. 架构总览

```
┌─────────────────────────────────────────────────────────────┐
│                    MainFile.Initialize()                     │
│  [ModInitializer] 入口 → AllocConsole → AppConfig →         │
│  CardDatabase → Harmony.PatchAll → AttachNodes              │
└────────────────────────┬────────────────────────────────────┘
                         │
                         ▼
┌─────────────────────────────────────────────────────────────┐
│                AutoSlayNode (挂载到场景树根)                  │
│                                                              │
│  _Process(delta) — 每帧执行                                  │
│    │                                                         │
│    ├─ T键暂停 / F1导航 / F2战斗 / F3事件 切换               │
│    ├─ 主线：主菜单 → 选角 → 地图 → 战斗/事件/商店/火堆      │
│    ├─ 多人：主菜单 → Multiplayer → Host/Join                │
│    │                                                         │
│    ├─ COMBAT: 抽牌检测 → IroncladSolver.Solve() → 执行出牌  │
│    ├─ MAP:    DecisionEngine → MapDecider.Decide()           │
│    ├─ EVENT:  DecisionEngine → EventDecider.Decide()         │
│    ├─ REST:   DecisionEngine → RestDecider.Decide()          │
│    ├─ SHOP:   DecisionEngine → ShopDecider.Decide()          │
│    ├─ REWARD: DecisionEngine → CardRewardDecider.Decide()    │
│    ├─ GRID:   DecisionEngine → CardGridDecider.Decide()      │
│    └─ RELIC:  DecisionEngine → RelicDecider.Decide()         │
│                                                              │
│    ├─ 卡死检测: StuckDetector (战斗45s / 非战斗45s)          │
│    └─ 稳定性检测: StateStabilityDetector                     │
└─────────────────────────────────────────────────────────────┘
```

### 源代码目录结构

```
src/
├── AutoSlayNode.cs              ★ 主循环控制器 (~3600行)
├── AutoSlayCardSelector.cs      战斗中选牌回调
├── AutoSlayHelpers.cs           递归子节点查找 + UI交互工具
├── AutoSlayPatch.cs             Harmony 补丁：NGame._Ready 挂载节点
├── MainFile.cs                  [ModInitializer] 入口
│
├── Core/                        核心基础层
│   ├── AppConfig.cs             统一配置（JSON加载、CLI参数、SignalFile）
│   ├── GameScreen.cs            屏幕类型枚举
│   ├── ScreenDetector.cs        屏幕/覆盖层检测
│   ├── RunContext.cs            运行时状态快照
│   └── StuckDetector.cs         卡死检测与恢复
│
├── Patches/                     Harmony 补丁层
│   ├── ForceENetHostPatch.cs    --fastmp 强制 ENet Host
│   ├── SteamPersonaNamePatch.cs 多实例唯一身份（PersonaName + SteamID + NetId）
│   ├── PatchRegistry.cs         补丁注册表
│   └── PatchStubs.cs            Broker补丁迁移动态文档
│
├── Solver/                      决策算法层
│   ├── IroncladSolver.cs        ★ DFS 战斗求解器
│   ├── DecisionEngine.cs        决策路由器
│   ├── CharacterConfigs.cs      角色配置（卡牌优先级 + 前后置规则）
│   ├── SolverParams.cs          从 params.json 加载可调参数
│   ├── BossStrategy.cs          Boss 特定策略
│   ├── ComboDatabase.cs         卡牌组合数据库
│   ├── CardDatabase.cs          卡牌 JSON 数据库（双维出牌）
│   ├── CardClassifier.cs        卡牌分类工具
│   ├── CardEffectReader.cs      卡牌效果反射读取
│   ├── CardModelInspector.cs    卡牌模型运行时类型发现
│   ├── CardRewardDecider.cs     ★ 22阶段卡牌抓取评分
│   ├── CardGridDecider.cs       升级/删除/变化/附魔选牌
│   ├── ChooseCardDecider.cs     战斗中选牌（消耗/置顶/搜索）
│   ├── BundleDecider.cs         卡包选择
│   ├── MapDecider.cs            ★ DFS 地图寻路
│   ├── EventDecider.cs          ★ 事件关键词评分
│   ├── RestDecider.cs           火堆决策（升级/休息/删除/钥匙）
│   ├── ShopDecider.cs           商店购买（删牌 → 遗物 → 药水）
│   ├── TreasureDecider.cs       宝箱开启
│   ├── RelicDecider.cs          遗物名称匹配 + OP.GG统计评分
│   ├── CrystalSphereDecider.cs  水晶球事件
│   ├── SimpleSelectDecider.cs   简单选择（如药水丢弃）
│   ├── RunState.cs              运行时状态追踪
│   ├── DecisionLogger.cs        决策审计日志
│   ├── BattleLogger.cs          战斗日志
│   ├── BossPlayLogger.cs        Boss 战详细出牌记录
│   ├── GameStateDetector.cs     游戏状态检测
│   ├── StateStabilityDetector.cs 安全决策时机判断
│   ├── StatsDatabase.cs         OP.GG 统计数据
│   ├── Tiebreaker.cs            随机平局打破
│   ├── UiUtils.cs               UI 交互工具
│   └── MultiplayerCards.cs      多人卡牌优先级
│
├── Handlers/                    屏幕交互层
│   ├── CombatHandler.cs         战斗处理（EndTurn 等）
│   ├── MapHandler.cs            地图节点点击
│   ├── EventRoomHandler.cs      事件选项交互
│   ├── RestSiteHandler.cs       火堆选项点击
│   ├── ShopHandler.cs           商店购买执行
│   ├── RewardsHandler.cs        奖励领取
│   ├── CardRewardHandler.cs     卡牌奖励选取
│   ├── CardGridHandler.cs       牌组网格操作
│   ├── ChooseACardHandler.cs    选牌操作
│   ├── ChooseABundleHandler.cs  卡包选择操作
│   ├── ChooseARelicHandler.cs   遗物选择操作
│   ├── CrystalSphereHandler.cs  水晶球交互
│   ├── SimpleCardSelectHandler.cs 简单选牌交互
│   ├── TreasureRoomHandler.cs   宝箱交互
│   ├── GameOverHandler.cs       游戏结束界面
│   └── PotionHelper.cs          药水目标解析
│
├── AutoBattle/                  事件驱动控制器（骨架）
│   ├── AutoBattleController.cs
│   ├── ScreenDispatcher.cs
│   ├── IScreenHandler.cs
│   ├── HandlerBase.cs
│   ├── ScreenHandlers.cs
│   └── OverlayHandlers.cs
│
└── Llm/                         LLM 模块（已禁用）
    ├── LlmClient.cs             HTTP SSE 流式客户端
    ├── LlmConfig.cs             JSON 配置加载
    ├── GameStateSerializer.cs   游戏状态序列化
    ├── PromptStrings.cs         双语提示词模板
    └── RunSummaryLogger.cs      单局统计日志
```

---

## 2. 启动与初始化流程

### 2.1 Mod 加载顺序

```
1. DLL 加载 → static ctor 执行
2. [ModuleInitializer] TokenSpire2ModuleInit (AutoSlayPatch.cs)
   └── LocalCoopPatchInstaller.Install()（Broker 补丁，如存在）
3. [ModInitializer] MainFile.Initialize()
   ├── AllocConsole() — 分配控制台窗口
   ├── AppConfig.Initialize(modDirectory) — 加载配置
   │   ├── 解析 --config <path> CLI 参数
   │   ├── 回退到环境变量 TOKENSPIRE2_CONFIG
   │   ├── 最后回退到 batch_config.json
   │   └── 写入 per-instance signal 文件
   ├── CardDatabase.Initialize() — 加载卡牌 JSON 数据库
   ├── harmony.PatchAll(assembly) — 安装所有 Harmony 补丁
   └── AttachNodes() — AutoSlayNode 挂载到场景树根
```

### 2.2 AppConfig 配置加载

**配置优先级**（由高到低）：
1. `--config <path>` CLI 参数（每个实例独立配置）
2. 环境变量 `TOKENSPIRE2_CONFIG`
3. `mods/TokenSpire2/batch_config.json`（默认路径）

**配置文件格式** (`token_spire_host.json` / `token_spire_botN.json`):
```json
{
  "Seed": "ABC123",
  "Character": "IRONCLAD",
  "MultiplayerMode": true,
  "IsMultiplayerHost": true,
  "SteamPersonaName": "Player",
  "AutoBattleEnabled": false,
  "SignalFile": "config_read_host.signal"
}
```

**Signal 文件机制**：
- 每个实例读取配置后写入独立的信号文件
- Host: `config_read_host.signal`
- BotN: `config_read_botN.signal`
- Launcher 脚本等待信号文件出现后再启动下一个实例
- 这保证了实例间不会发生文件写入竞态

### 2.3 Harmony 补丁安装

Harmony 补丁在两个层级安装：
- **Layer 1** (`AutoSlayPatch.cs` 的 `[ModuleInitializer]`): Broker/LAN 补丁在 MainFile 之前安装
- **Layer 2** (`MainFile.Initialize()`): `harmony.PatchAll(typeof(MainFile).Assembly)` 安装项目中所有 `[HarmonyPatch]` 类

---

## 3. 主循环 (AutoSlayNode._Process)

### 3.1 核心状态机

`_Process(delta)` 是 Godot 帧循环，每帧执行一次。按优先级处理：

```
每帧 _Process(delta):
  1. F1/F2/F3/T 按键检测
  2. 卡死检测（StuckDetector）
  3. 战斗计划执行（有 _combatPlan 时逐卡打出）
  4. 非战斗决策（按屏幕类型路由）
  5. 多人联机导航
```

### 3.2 关键状态标志

| 标志 | 用途 |
|------|------|
| `_combatTurnRequested` | 本回合已请求过求解器，不重复请求 |
| `_drawJustFinished` | 抽牌刚完成，等待 0.3s 稳定 |
| `_combatCardDelay` | 出牌间帧延迟计数器 |
| `_combatPlan` | 当前出牌计划 (List\<CombatAction\>) |
| `_combatPlanStep` | 计划执行位置索引 |
| `_combatPlanEndTurn` | 计划执行完后是否结束回合 |
| `_paused` | T 键暂停（暂停所有自动化） |
| `_autoNavigate` | F1 — 自动地图寻路 + 火堆/商店决策 |
| `_autoBattle` | F2 — 自动战斗出牌 |
| `_autoEvent` | F3 — 自动事件选择 + 卡牌奖励 |

### 3.3 多人模式初始化

```csharp
if (_isMultiplayerHost) {
    _autoNavigate = false; _autoBattle = false; _autoEvent = false;
} else {
    _autoNavigate = true;  _autoBattle = true;  _autoEvent = true;
}
```

- **Host（人类）**：默认关闭所有自动功能，可手动 F1/F2/F3 开启
- **Client（Bot）**：默认开启所有自动功能（全 AI 控制）

### 3.4 抽牌检测流程

```
战斗回合流程:
  1. 敌方回合: PlayerActionsDisabled=true → 重置标志
  2. 玩家回合开始: PlayerActionsDisabled=false, hand=0
  3. 抽牌检测: handCount==0 → 等待
  4. 首张卡出现: handCount>0 → _drawJustFinished=true → 等待 0.3s
  5. 0.3s 后: 求解器运行
  6. 求解器输出计划 → _combatPlan 填充
  7. 逐帧执行出牌（每张间隔 0.4s → 30f → 调到 60f）
  8. 计划执行完 → END_TURN
```

---

## 4. 核心模块详解

### 4.1 Core 基础层

#### AppConfig (`Core/AppConfig.cs`)

**线程安全单例**，存储所有运行时配置：

| 属性 | 类型 | 说明 |
|------|------|------|
| `AutoBattleEnabled` | bool | 是否启用自动战斗 |
| `AutoBattlePaused` | bool | T 键暂停状态 |
| `AutoBattleScope` | int | 自动化范围 (0=战斗, 1=全部) |
| `BatchMode` | bool | 批量测试模式 |
| `Seed` | string? | 游戏种子 |
| `Character` | string | 角色选择 (默认 IRONCLAD) |
| `MultiplayerMode` | bool | 多人模式 |
| `IsMultiplayerHost` | bool | 是否为主机 |
| `SteamPersonaName` | string | Steam 显示名称（用于身份识别） |

**关键方法**：
- `Initialize(modDirectory)` — 加载配置，解析 CLI 参数，写入 signal 文件
- `TogglePause()` — T 键暂停/恢复

#### GameScreen (`Core/GameScreen.cs`)

屏幕类型枚举，用于 `ScreenDetector` 识别当前游戏界面：

```csharp
NONE, MAIN_MENU, CHARACTER_SELECT, MAP, COMBAT, EVENT,
REST, SHOP, TREASURE, VICTORY, GAME_OVER, LOADING,
OVERLAY_CARD_REWARD, OVERLAY_DECK_GRID, OVERLAY_CHOOSE_CARD,
OVERLAY_CHOOSE_RELIC, OVERLAY_SIMPLE_SELECT, OVERLAY_CHOOSE_BUNDLE,
OVERLAY_CRYSTAL_SPHERE, MULTIPLAYER_MENU, MULTIPLAYER_LOBBY
```

#### ScreenDetector (`Core/ScreenDetector.cs`)

**静态工具类**，通过反射检测当前游戏屏幕：
- `Detect()` — 返回标准化的 `GameScreen` 枚举
- `DetectRaw()` — 返回原始屏幕类型名称
- 检测逻辑基于 Godot 节点类型（如 `NCombatScreen`, `NMapScreen`, `NEventRoom` 等）

#### RunContext (`Core/RunContext.cs`)

**运行时状态快照**，每次决策前刷新：

| 分类 | 追踪数据 |
|------|---------|
| 基础 | Character, HP, MaxHP, Gold, Floor, Act |
| 牌库 | DeckCardIds, AttackCount, SkillCount, PowerCount, 费用曲线 |
| 遗物 | RelicIds, HasEnergyRelic, HasStrengthScaling, ... |
| 药水 | PotionSlotCount, PotionCount, PotionIds |
| 协同标志 | HasExhaustSynergy, HasBlockSynergy, HasSelfDamageSynergy |
| 非Ironclad | HasPoisonSynergy, HasDiscardSynergy, HasOrbSynergy, HasFocusScaling, HasStarSynergy |
| 诊断 | CountBasicStrikes, CountBasicDefends, CountUpgradedCards, AvgDamagePerCard, ... |

**关键方法**：
- `Refresh()` — 从 `RunManager.DebugOnlyGetState()` 拉取最新状态
- `IsBasicStrikeName(cardId)` / `IsBasicDefendName(cardId)` — 前缀匹配识别基础牌
- `CountCardsById(fragment)` — 前缀匹配统计特定 ID 卡牌数量

#### StuckDetector (`Core/StuckDetector.cs`)

**三级卡死检测**：

| 级别 | 超时 | 说明 |
|------|------|------|
| 战斗无活动 | 45s | 战斗中无任何出牌/动作 |
| 非战斗同屏 | 45s | 同一非战斗界面卡住 |
| 战斗相邻同屏 | 90s | COMBAT 界面的兜底超时 |

**关键方法**：
- `Update(delta)` → `StuckResult` — 每帧调用，检测是否卡死
- `MarkActivity()` — 标记有活动（重置计时器）
- `Reset()` — 场景转换时重置所有计时器
- `WriteDiagnostics()` — 卡死时写入诊断文件

**安全机制**：
- 人类玩家实例永不自杀 (`IsHumanPlayer`)
- 多人模式永不自杀 (`NeverKill`)

### 4.2 Harmony 补丁层

#### ForceENetHostPatch (`Patches/ForceENetHostPatch.cs`)

**目的**：当 `--fastmp` 参数存在时，强制 Host 使用 ENet 而非 Steam Lobby。

**补丁目标**：`NetHostGameService.StartSteamHost(int maxClients)`

```csharp
// 拦截 StartSteamHost，重定向到 StartENetHost
[HarmonyPatch(typeof(NetHostGameService), "StartSteamHost")]
static bool Prefix(NetHostGameService __instance, int maxClients,
                   ref Task<NetErrorInfo?> __result)
{
    if (!CommandLineHelper.HasArg("fastmp")) return true; // 不用 fastmp → 原逻辑
    // 强制 ENet: 127.0.0.1:33771, maxClients=3 (host + 3 clients)
    var error = __instance.StartENetHost(33771, maxClients: 3);
    __result = Task.FromResult<NetErrorInfo?>(error);
    return false; // 跳过原 Steam Host
}
```

#### SteamPersonaNamePatch (`Patches/SteamPersonaNamePatch.cs`)

**目的**：解决同一 Steam 账号多实例时的身份冲突。所有实例共享 `AccountId=1000` → 相同的 `NetId=1000` → ENet Host 拒绝重复 NetId → Bot2 无法加入。

包含三个补丁类：

**① `SteamPersonaNamePatch`** — 覆写显示名称
```csharp
[HarmonyPatch(typeof(SteamFriends), nameof(SteamFriends.GetPersonaName))]
// 返回 AppConfig.SteamPersonaName ("Player" / "Bot1" / "Bot2" / "Bot3")
```

**② `SteamUserIDPatch`** — 覆写 SteamID（已确认运行但未修复 NetId）
```csharp
[HarmonyPatch(typeof(SteamUser), nameof(SteamUser.GetSteamID))]
// Bot1 → AccountId=1001, Bot2→1002, Bot3→1003
// Player (Host) → AccountId=1000
```

**③ `LocalContextNetIdPatch`** — 覆写 LocalContext.NetId（**当前破坏 Host 初始化**）
```csharp
[HarmonyPatch(typeof(MegaCrit.Sts2.Core.Context.LocalContext), "get_NetId")]
// 直接覆写 NetId getter — 目前导致 Host 无法写入 signal 文件
```

**已知问题**：
- `SteamUserIDPatch` 修改了 `GetSteamID()` 返回值，但游戏可能缓存了早期 NetId（在 Harmony 补丁生效前）
- `LocalContextNetIdPatch` 破坏了 Host 初始化（signal 文件不出现在 45s 超时内），可能是 `get_NetId` 方法名不匹配或是 property 非 virtual

#### PatchRegistry (`Patches/PatchRegistry.cs`)

**补丁注册工具**：
- `InstallAll()` — 从 assembly 安装所有补丁
- `UninstallAll()` — 卸载所有补丁
- `PatchAll(Type)` — 安装单个补丁类

#### PatchStubs (`Patches/PatchStubs.cs`)

**迁移文档文件** — 记录 Broker/LAN 补丁从 `src/Coop/Patches/` 迁移到 `src/Patches/` 的计划。目前所有 Broker 补丁由 `LocalCoopPatchInstaller.Install()` 安装（Layer 1），该文件是目标架构的占位符。

### 4.3 Solver 决策层

#### DecisionEngine (`Solver/DecisionEngine.cs`)

**中央决策路由器**，根据 `GameScreen` 类型将决策分派到对应的 Decider：

```csharp
Dispatch(screen):
  MAIN_MENU              → 放弃旧run → 选角色 → 开始标准run（或多人导航）
  MAP                    → MapDecider.Decide()
  COMBAT                 → IroncladSolver.Solve()
  EVENT                  → EventDecider.Decide()
  REST                   → RestDecider.Decide()
  SHOP                   → ShopDecider.Decide()
  TREASURE               → TreasureDecider.Decide()
  OVERLAY_CARD_REWARD    → CardRewardDecider.Decide()
  OVERLAY_DECK_GRID      → CardGridDecider.Decide()
  OVERLAY_CHOOSE_RELIC   → RelicDecider.Decide()
  OVERLAY_CHOOSE_CARD    → ChooseCardDecider.Decide()
  OVERLAY_CHOOSE_BUNDLE  → BundleDecider.Decide()
  OVERLAY_SIMPLE_SELECT  → SimpleSelectDecider.Decide()
  OVERLAY_CRYSTAL_SPHERE → CrystalSphereDecider.Decide()
  GAME_OVER / VICTORY    → 记录结果，写入 run_complete.txt
```

### 4.4 Handlers 交互层

每个 Handler 负责与特定游戏屏幕的 Godot UI 交互（点击按钮、选择选项等）。

| Handler | 处理屏幕 | 关键操作 |
|---------|---------|---------|
| `CombatHandler` | 战斗 | EndTurnViaUiOrApi() |
| `MapHandler` | 地图 | 点击 NMapPoint 节点 |
| `EventRoomHandler` | 事件 | ForceClick 选项按钮 |
| `RestSiteHandler` | 火堆 | 点击 Smith/Rest/Toke 按钮 |
| `ShopHandler` | 商店 | 购买物品、点击离开 |
| `RewardsHandler` | 奖励 | 领取奖励按钮 |
| `CardRewardHandler` | 卡牌奖励 | 选择卡牌 |
| `CardGridHandler` | 牌组网格 | 升级/删除/变化/附魔 |
| `ChooseACardHandler` | 战斗中选牌 | 消耗/置顶/搜索 |
| `ChooseARelicHandler` | 遗物选择 | 选择遗物 |
| `TreasureRoomHandler` | 宝箱 | 打开宝箱、拾取遗物 |
| `GameOverHandler` | 游戏结束 | 点击继续/返回主菜单 |

#### AutoSlayHelpers (`AutoSlayHelpers.cs`)

**UI 交互工具集**：
- `FindFirst<T>(node)` / `FindAll<T>(node)` — 递归子节点查找（支持 Godot 节点树搜索）
- `ForceClick(button)` — 强制点击按钮（带重试和验证）
- `ClickButtonByText()` — 按文本内容查找并点击按钮

### 4.5 AutoBattle 控制器

`AutoBattle/` 目录包含事件驱动架构的骨架实现：

- `AutoBattleController` — 事件驱动的主控制器
- `ScreenDispatcher` — 屏幕类型到 Handler 的分发
- `IScreenHandler` — Handler 接口定义
- `HandlerBase` — Handler 基类
- `ScreenHandlers` — 屏幕级 Handler 实现
- `OverlayHandlers` — 覆盖层 Handler 实现

**注意**：此架构当前为骨架，实际运行时仍使用 `AutoSlayNode._Process` 中的直接检测逻辑。

### 4.6 LLM 模块（已禁用）

LLM 模块完整实现但**永久禁用**（`_llm` 在 `_Ready()` 中始终为 `null`）：

- **LlmClient.cs**: HTTP SSE 流式客户端，支持 OpenRouter thinking，对话历史，跨局记忆
- **GameStateSerializer.cs**: 将战斗/地图/事件/商店/火堆状态序列化为双语提示词文本
- **PromptStrings.cs**: 完整的中英双语提示词模板
- **RunSummaryLogger.cs**: 单局 JSON 统计
- **LlmConfig.cs**: JSON 配置加载器

---

## 5. 战斗求解器详解

### 5.1 IroncladSolver — DFS 搜索架构

**文件**: `src/Solver/IroncladSolver.cs` (~2000+ 行)

```
Solve(hand, enemies, energy, state, config)
  │
  ├─ 1. 卡牌过滤: CanPlayCard() — 能量 + 合法性检查
  ├─ 2. 卡牌排序: 按 CombinedScore(Priority, OrderPriority) 降序
  ├─ 3. DFS 搜索: 枚举所有可玩牌 × 能量选项 × 目标
  │     ├─ Clone state
  │     ├─ Apply card effects (伤害/格挡/debuff/buff/球/毒/星)
  │     ├─ 模拟抽牌（从弃牌堆洗牌）
  │     ├─ 药水使用（免费行动，穿插在出牌中）
  │     ├─ EvaluateState() → 评分
  │     └─ Recursive Search(depth+1)
  ├─ 4. 选择最优计划: List<SolveAction>
  └─ 5. GreedyFallback: 无合法计划时的兜底
```

**搜索限制**：
- `max_search_states`: 6000（最大搜索状态数）
- `max_cards_per_turn`: 15（每回合最多出牌数）

### 5.2 二维出牌评分系统

**核心创新**：将出牌决策分为两个独立维度：

| 维度 | 含义 | 范围 | 例子(Bash) | 例子(Strike) |
|------|------|------|-----------|-------------|
| `play_priority` | 有多需要打出这张牌 | 0-100 | 60 (中等) | 85 (高) |
| `play_order` | 应该多早打出（排序） | 0-100 | 92 (很早期) | 40 (灵活) |

**组合分数**（决定"下一张打什么"）：
```
combinedScore = context.selectionWeight × play_priority
              + context.orderWeight × play_order
```

**上下文权重**随回合状态动态调整：
- 能量紧张 (<2) → selectionWeight=0.80, orderWeight=0.20
- 能量充裕 (≥3) → selectionWeight=0.40, orderWeight=0.60
- Boss 战 → selectionWeight=0.50, orderWeight=0.50
- 致命一击 → selectionWeight=1.00, orderWeight=0.00

> Bash: 易伤应该先于打击打出→高 play_order (92)
> Strike: 更基础的价值牌→高 play_priority (85)
> 结果: Bash 排序在前（先打出），但能量紧张时 Strike 优先级更高（更可靠）

### 5.3 EvaluateState() — 20+ 维评分

| # | 维度 | 计算方式 |
|---|------|---------|
| 1 | 击杀奖励 | KillBase + MaxHP × 系数 |
| 2 | 造成伤害 | DamagePerPoint × 伤害倍率 × 精英/Boss 修正 |
| 3 | 集火奖励 | FocusFireMultiplier × 单一目标集中伤害 |
| 4 | Boss 优先 | BossPriorityMultiplier × 对 Boss 伤害 |
| 5 | 仆从惩罚 | MinionDamagePenalty × 对非 Boss 目标伤害 |
| 6 | 受伤惩罚 | 剩余敌人伤害 × HealthPenalty |
| 7 | 低血量惩罚 | HP < 阈值时额外扣分 |
| 8 | 有效格挡 | BlockPerNeededPoint × 吸收的实际伤害 |
| 9 | 溢出格挡 | BlockPerExcessPoint × 超出的格挡（减半） |
| 10 | 挂易伤 | VulnerablePerStack × 易伤层数 |
| 11 | 挂虚弱 | WeakPerStack × 虚弱层数 |
| 12 | 力量成长 | StrengthPerPoint × 剩余回合数 |
| 13 | 敏捷成长 | DexterityPerPoint × 剩余回合数 |
| 14 | 能力牌估值 | PowerPerPlayed × 剩余回合数 |
| 15 | 能量剩余 | EnergyPerPoint × 剩余能量 |
| 16 | 出牌顺序 | TwoDimOrderingScore（提前打setup牌加分） |
| 17 | 硬性顺序 | BEFORE/AFTER 规则违反惩罚 |
| 18 | 能量效率 | NetEnergy × 正向奖励 |
| 19 | 未来价值 | 已激活能力 × discount_rate 折现 |
| 20 | 牌库质量 | 消耗基础牌/薄牌库奖励 |
| 21 | 毒药价值 | PoisonPerStack × 回合数 |
| 22 | 充能球价值 | Lightning/Frost/Dark/Plasma × Focus |
| 23 | 星保留 | StarPerStack（Necrobinder/Regent） |
| 24 | Boss 对策 | Queen/Crab/Beast 等特定倍率调整 |

### 5.4 Boss 特定策略 (`BossStrategy.cs`)

每个 Boss 有独立参数调整：

| Boss | 策略 |
|------|------|
| **Queen** | 直冲本体，需要大量过牌 |
| **Kaiser Crab** | AOE 优先（打双钳） |
| **Ceremonial Beast** | 150HP 前 DPS 竞速，Ringing 回合用大牌 |
| **Vantom** | 多段攻击破 Slippery Shield |
| **The Kin** | AOE 为主（3 目标） |
| **Insatiable** | MAX DPS，不惜代价输出 |
| **Knowledge Demon** | 大牌加分 |
| **Aeonglass** | 低速控制，重视防御 |

**BossStrategy.Adjustment** 参数：
- `DamageMult` — 伤害倍率修正
- `BlockMult` — 格挡倍率修正
- `SelectionWeightMult` — 选牌权重修正 (>1 = 更重视牌本身价值)
- `OrderWeightMult` — 排序权重修正 (>1 = 更重视出牌顺序)

### 5.5 多角色特殊机制

| 机制 | 角色 | 实现 |
|------|------|------|
| **充能球** | Defect | 完整模拟 Channel/Evoke/Focus/Loop，包括 Dualcast 两次 Evoke |
| **毒药** | Silent | 计算每回合持续伤害 |
| **星星** | Necrobinder | 追踪星星消耗 |
| **药水** | 全部 | 免费行动，可穿插在出牌中 |
| **X-cost 牌** | 全部 | 总是花全部剩余能量 |
| **抽牌模拟** | 全部 | 从抽牌堆抽，堆空时洗弃牌堆 |
| **Buffer/Intangible/Thorns** | 全部 | 完整模拟敌人 Buff |

### 5.6 CardDatabase + CardClassifier

**CardDatabase** (`Solver/CardDatabase.cs`)：
- 线程安全单例，加载 `Cards/<Character>Cards.json`
- 提供 `GetPlayPriority(cardId)`, `GetPlayOrder(cardId)`, `GetChineseName(cardId)`
- 每张卡包含：id, name_cn, name_en, type, cost, rarity, play_priority, play_order, effects

**CardClassifier** (`Solver/CardClassifier.cs`)：
- 共享卡牌分类工具：`IsDrawCard()`, `IsEnergyCard()`, `IsAoeCard()`, `IsScalingCard()`
- `IsVulnerableCard()`, `IsWeakCard()`, `IsStrengthCard()`, `IsPoisonCard()`, `IsOrbCard()`

---

## 6. 非战斗决策系统

### 6.1 MapDecider — 地图寻路

**算法**：DFS 枚举所有路径 + 启发式评分

```
PlanBestPath():
  → BuildAdjacency() — 反射 Point.PointsTo 获取节点连接
  → EnumeratePaths() — DFS 枚举所有可能路径
  → 路由评分：
      火堆 +50, 商店 +30~60, 精英 -100, 未知 +20~40
      路径组合: 至少1个火堆 +30, 至少2个 +20, 商店+火堆 +15
  → 逐节点点击，0.5s 后验证是否被接受
```

**单节点快速路径**：只有 1 个可选节点时跳过规划，直接点击。
**已点击追踪**：`HashSet<(int row, int col)>` 防重复点击。
**卡住恢复**：超时后 replan（重新规划）。

### 6.2 CardRewardDecider — 22 阶段卡牌奖励评分

**核心决策流程**：
```
对所有3张可选卡牌 ScoreCard() 评分
  → 按分数排序
  → 双重门槛验证
  → 通过门槛的卡牌中选最高分
  → 0 张合格 → SKIP
```

**双重门槛公式**：
```
相对门槛 = maxScore × 0.75  (在最高分25%范围内)
绝对门槛 = Base(25) + 牌库大小压力 + Act惩罚 - 紧急缓解
最终门槛 = max(相对门槛, 绝对门槛, 10.0)
```

**22 阶段评分**（Phase 1 → Phase 22）:

| Phase | 内容 |
|-------|------|
| 1 | 原始数值效率（伤害/能量 + 格挡/能量） |
| 2 | 卡牌类型（能力/零费/X费） |
| 3 | Debuff 价值（易伤/虚弱/中毒） |
| 4 | Buff 价值（能量/力量/敏捷） |
| 5 | AOE 奖励 |
| 6 | 过牌检测 |
| 7 | HP 代价卡（有/无自伤协同） |
| 8 | Act 阶段修正 |
| 9 | 牌组协同（力量/消耗/格挡/自伤/毒/弃牌/球/集中/星） |
| 9b | 卡牌组合（ComboSynergy，500+ 对） |
| 10 | 冗余惩罚（可叠/不可叠） |
| 11 | 费用曲线平衡 |
| 12 | 类型平衡（攻击/技能比例） |
| 13 | 角色优先级（CharacterConfig Tier） |
| 14 | 统计数据加权（OP.GG 胜率） |
| 15 | 升级加分 |
| 16 | 端口化缺口诊断（伤害/格挡/过牌/能量/AOE/成长） |
| 17 | 站未来可行性（高潜力牌+协同检测） |
| 18 | 过渡牌 vs 终端牌 |
| 19 | 运转闭合检测（过牌+能量闭环） |
| 20 | 敲位压力（待升级牌 vs 剩余火堆） |
| 21 | 基础牌删除进度 |
| 22 | Boss 对策 |

**硬性跳过规则**：
- 诅咒/状态牌 → 分数 = -1000
- MAX_COPIES 达到上限 → 分数 = -500
- 绝对阈值以下 → 不合格

### 6.3 EventDecider — 事件关键词评分

**算法**：文本关键词匹配 + 已知事件硬编码策略

**评分基线**：50 分

**正面关键词**：
| 关键词 | 加分 | 条件 |
|--------|------|------|
| "heal" / "restore" | +30 | 低 HP 时 |
| "relic" / "artifact" | +40 | 总是 |
| "card" (非 curse) | +25 | 总是 |
| "gold" / "money" | +20 | 总是 |
| "upgrade" / "smith" | +25 | 总是 |
| "transform" | +20 | 总是 |
| "remove" / "purge" | +25 | 总是 |
| "strength" / "power" | +20 | 总是 |

**负面检测**：
- HP 代价检测 → 低 HP 时硬阻止
- 诅咒检测 → 有消耗协同小扣分，无协同大扣分
- 重复选择 ≥3 次 → 硬阻止

**已知事件硬编码**（部分）：Scrap Ooze, The Library, Cursed Tome, Moai Head, Vampires, Ghost Council, Bonfire Spirits, Mysterious Sphere, The Joust, Big Fish, SSSSerpent, Winding Halls, The Cleric, Living Wall

### 6.4 RestDecider — 火堆决策

**决策优先级**：
```
HP < 50% → 休息优先
HP ≥ 55% → 升级优先
无可升级非基础牌 → 升级 -40 惩罚
大牌库有基础牌 → 删除优先
```

**各选项评分**（修改自 params.json）：
- `RestLowHpThreshold`: 0.30（低于此阈值大力休息）
- `RestMediumHpMax`: 0.55（低于此阈值中等休息）
- `SmithHpThreshold`: 0.50（高于此阈值才能升级）

### 6.5 ShopDecider — 商店决策

**优先级**: 删牌(0) > 遗物(1) > 药水(2)。**绝对不买卡牌**。

- 删基础牌：500 + basicCount×20（最高优先级）
- 遗物：OP.GG 统计胜率 × 500 + 类型加分
- 最低黄金储备：50g
- 购买后验证黄金变化（竞态保护）

### 6.6 RelicDecider — 遗物选择

**名称子串匹配** + **角色特定加成** + **OP.GG 统计胜率**：

| 层级 | 类型 | 加分 | 示例 |
|------|------|------|------|
| S | 能量遗物 | +250 | Sozu, Coffee Dripper |
| A | 力量/敏捷 | +150 | Vajra, Shuriken |
| A | 过牌/能量引擎 | +130 | Top, Sundial |
| B | 防御 | +90 | Anchor, Thread and Needle |
| C | 特殊/小众 | +30 | Darkstone, Cauldron |

**Boss 遗物特殊处理**：Busted Crown（小牌库 -80）、Fusion Hammer（-20）、Tiny House（+40）

### 6.7 CardGridDecider — 升级/删除/变化/附魔

**绝对优先级：永远优先处理 Strike 和 Defend**

| 操作 | Strike/Defend 分数 | 已升级牌分数 | 其他牌 |
|------|-------------------|-------------|--------|
| 删除 | -500 (最优先) | +500 (禁止) | 质量评分 |
| 变化 | 复用删除逻辑 | +500 (禁止) | 质量评分 |
| 升级 | -2000 (禁止) | -500 (禁止) | 增益估算 × 系数 |
| 附魔 | -2000 (禁止) | -500 (禁止) | 类型加分 |

### 6.8 ChooseCardDecider — 战斗中选牌

**上下文感知** — 根据触发来源卡牌选择不同策略：

| 卡牌 | 上下文 | 策略 |
|------|--------|------|
| Headbutt, Warcry | PUT_ON_TOP | 选最好的牌放到牌库顶 |
| True Grit, Burning Pact | EXHAUST | 选最差的牌消耗掉 |
| Armaments | UPGRADE | 选升级收益最高的牌 |
| Exhume, Hologram | RETRIEVE | 选最有价值的牌拿回手 |
| Secret Technique | FETCH_SKILL | 选最好的技能牌 |
| Secret Weapon | FETCH_ATTACK | 选最好的攻击牌 |

---

## 7. LAN 多人联机实现

### 7.1 架构演进历史

TokenSpire2 的多人联机经历了多次迭代：

```
V1: Couch Coop (单实例) → 放弃（无法真正 LAN）
V2: TCP Broker (独立中继进程) → 放弃（太多补丁，不稳定）
V3: SteamFix64 DLL 代理 → 放弃（脆弱的 Steam API 劫持）
V4: Virtual Friend 注入 → 放弃（游戏不用 SteamFriends 枚举）
V5: --fastmp ENet 直连 → ★ 当前方案
```

### 7.2 当前方案：--fastmp ENet 直连

**核心设计**：
- Host 启动 ENet 服务器在 `127.0.0.1:33771`
- Bot 通过 `--fastmp join` 直接 ENet 连接到 Host
- 绕过 Steam Matchmaking 和 Friends List
- 通过 Harmony 补丁处理身份冲突

### 7.3 网络传输流程

```
Host (人类):                          Bot (AI):
  │                                     │
  ├─ --fastmp host_standard             ├─ --fastmp join
  ├─ ForceENetHostPatch                 ├─ 游戏内 FastMpJoin 逻辑
  │   └─ StartENetHost(33771, 3)       │   └─ ENet 连接 127.0.0.1:33771
  ├─ HandleMultiplayerMenu()            ├─ HandleMultiplayerMenu()
  │   └─ Multiplayer → Host → Standard  │   └─ 等待 Host Lobby 可见
  ├─ 人类手动导航                       ├─ AI 自动跟随 Host
  ├─ 人类手动出牌/结束回合              ├─ IroncladSolver 自动出牌
  └─ 人类做非战斗决策                   └─ 跟随 Host（不做独立决策）
```

### 7.4 启动脚本 (`launch_lan.ps1`)

```powershell
.\launch_lan.ps1 -Character IRONCLAD -Seed ABC123 -BotCount 2
# BotCount: 1=2人, 2=3人, 3=4人
```

**顺序启动流程**：
1. 清理旧 signal/config 文件
2. 写入 Host 配置 → `token_spire_host.json`
3. 写入 BotN 配置 → `token_spire_botN.json`（每个 Bot 独立 config）
4. 启动 Host → 等待 `config_read_host.signal`
5. 逐个启动 Bot → 等待各自 `config_read_botN.signal`
6. 所有实例就绪 → 输出汇总

### 7.5 关键 Harmony 补丁

| 补丁 | 作用 | 状态 |
|------|------|------|
| `ForceENetHostPatch` | Host 使用 ENet 而非 Steam Lobby | ✅ 工作 |
| `SteamPersonaNamePatch` | 覆写显示名称 | ✅ 工作 |
| `SteamUserIDPatch` | 覆写 SteamID (AccountId) | ✅ 运行但 NetId 仍为 1000 |
| `LocalContextNetIdPatch` | 直接覆写 NetId | ❌ 破坏 Host 初始化 |

### 7.6 当前主要问题

**Bot2 无法加入（重复 NetId）**：
- 所有实例共享同一个 Steam 账号 → `AccountId=1000`
- 游戏从 AccountId 派生出 `NetId=1000`（在 `LocalContext` 中缓存）
- Host 的 ENet 服务器拒绝重复 NetId 的连接
- `SteamUserIDPatch` 修改了 `GetSteamID()` 返回值，但 NetId 可能在补丁生效前已缓存
- `LocalContextNetIdPatch` 导致 Host 初始化失败（signal 文件不出现）

**可能的解决方案**：
- 在 `MainFile.Initialize()` 中通过反射直接设置 `LocalContext` 的静态字段（补丁生效前）
- 找到 NetId 的真正缓存位置并覆盖
- 使用 `ENetClient` 直接 API 调用，手动传入 NetId

### 7.7 Unity launcher (`launch.ps1`)

**支持所有模式**：
```powershell
.\launch.ps1 -Mode solo_bot          # 单角色自动战斗
.\launch.ps1 -Mode solo_player        # 单角色正常游戏
.\launch.ps1 -Mode coop_1bot          # 1人 + 1Bot (2窗口)
.\launch.ps1 -Mode coop_2bot          # 1人 + 2Bot (3窗口)
.\launch.ps1 -Mode coop_3bot          # 1人 + 3Bot (4窗口)
```

Coop 模式使用与 `launch_lan.ps1` 相同的顺序启动模式（Host 先启动，逐个 Bot 后启动）。

---

## 8. 游戏内部接口对照表

### 8.1 MegaCrit.Sts2 命名空间总览

| 命名空间 | 用途 |
|---------|------|
| `MegaCrit.Sts2.Core.Combat` | 战斗管理、回合管理 |
| `MegaCrit.Sts2.Core.Commands` | 玩家命令（出牌、结束回合） |
| `MegaCrit.Sts2.Core.Context` | 本地玩家上下文、RunManager |
| `MegaCrit.Sts2.Core.Entities.Cards` | 卡牌模型、卡牌类型 |
| `MegaCrit.Sts2.Core.Entities.Creatures` | 生物（玩家/敌人） |
| `MegaCrit.Sts2.Core.Entities.Merchant` | 商店 |
| `MegaCrit.Sts2.Core.Entities.Multiplayer` | 多人网络服务 |
| `MegaCrit.Sts2.Core.Entities.Players` | 玩家实体 |
| `MegaCrit.Sts2.Core.Helpers` | 命令行解析、工具函数 |
| `MegaCrit.Sts2.Core.Logging` | 游戏内置日志 |
| `MegaCrit.Sts2.Core.Modding` | ModInitializer 特性 |
| `MegaCrit.Sts2.Core.Models` | 游戏模型（RunModel, PlayerModel 等） |
| `MegaCrit.Sts2.Core.Multiplayer` | 多人网络服务接口 |
| `MegaCrit.Sts2.Core.Nodes` | Godot 节点基类 |
| `MegaCrit.Sts2.Core.Nodes.Screens` | 屏幕节点（Map, Combat, Shop 等） |
| `MegaCrit.Sts2.Core.Nodes.Screens.Overlays` | 覆盖层（选牌、遗物等） |
| `MegaCrit.Sts2.Core.Nodes.Events` | 事件房间 |
| `MegaCrit.Sts2.Core.Nodes.RestSite` | 火堆 |
| `MegaCrit.Sts2.Core.Nodes.Screens.Map` | 地图节点 |
| `MegaCrit.Sts2.Core.Nodes.Rewards` | 奖励按钮 |
| `MegaCrit.Sts2.Core.Nodes.Cards.Holders` | 卡牌持有者 |
| `MegaCrit.Sts2.Core.Runs` | Run 管理 |
| `MegaCrit.Sts2.Core.Saves` | 存档管理 |
| `MegaCrit.Sts2.Core.Settings` | 游戏设置 |
| `MegaCrit.Sts2.Core.Timeline` | 时间线/动画 |
| `MegaCrit.Sts2.Core.Unlocks` | 解锁系统 |

### 8.2 核心类/接口速查表

#### 运行管理

| 类/方法 | 功能 | 使用位置 |
|---------|------|---------|
| `RunManager.Instance` | 单例，管理整个 Run | AutoSlayNode, RunContext, 所有 Decider |
| `RunManager.Instance.DebugOnlyGetState()` | 获取当前 RunState | RunContext.Refresh() |
| `RunManager.Instance.AbandonRun()` | 放弃当前 Run | AutoSlayNode（清旧存档） |

#### 本地玩家

| 类/方法 | 功能 | 使用位置 |
|---------|------|---------|
| `LocalContext.GetMe(RunState)` | 获取本地玩家 PlayerModel | RunContext.Refresh() |
| `LocalContext.NetId` | 网络 ID（从 AccountId 派生） | 多人联机 |
| `LocalContext.AddPlayerDebug()` | Debug 增加玩家（Couch Coop） | 多人模式 |

#### 战斗

| 类/方法 | 功能 | 使用位置 |
|---------|------|---------|
| `CombatManager.Instance` | 战斗管理器单例 | AutoSlayNode |
| `CombatManager.Instance.IsInProgress` | 是否在战斗中 | AutoSlayNode._Process() |
| `CombatManager.Instance.GetHand(Player)` | 获取手牌 | IroncladSolver.Solve() |
| `CombatManager.Instance.GetEnemies()` | 获取敌人列表 | IroncladSolver.Solve() |
| `CombatManager.Instance.PlayerActionsDisabled` | 玩家是否不能行动 | AutoSlayNode（检测回合切换） |
| `PlayerCmd.EndTurn(Player, canBackOut)` | 结束回合 | CombatHandler.EndTurnViaUiOrApi() |

#### 卡牌

| 类/方法 | 功能 | 使用位置 |
|---------|------|---------|
| `CardModel.Id.Entry` | 卡牌 ID 字符串 | 所有 Solver, RunContext |
| `CardModel.Type` | 卡牌类型 (Attack/Skill/Power) | RunContext, CardRewardDecider |
| `CardModel.EnergyCost.Canonical` | 标准费用 | IroncladSolver |
| `CardModel.CanPlay(out reason, out _)` | 是否可打出 | IroncladSolver.Solve() |
| `CardModel.TryManualPlay(target)` | 手动打出卡牌（本地） | AutoSlayNode |
| `CardModel.IsUpgraded` | 是否已升级 | CardGridDecider |
| `CardType.Attack / Skill / Power` | 卡牌类型枚举 | 通用 |
| `PileType.Draw.GetPile(player).Cards` | 抽牌堆 | IroncladSolver |

#### 网络/多人

| 类/方法 | 功能 | 使用位置 |
|---------|------|---------|
| `NetHostGameService.StartSteamHost(maxClients)` | 创建 Steam Lobby | ForceENetHostPatch |
| `NetHostGameService.StartENetHost(port, maxClients)` | 创建 ENet 服务器 | ForceENetHostPatch |
| `CommandLineHelper.HasArg("fastmp")` | 检测 --fastmp 参数 | ForceENetHostPatch |
| `NJoinFriendScreen.FastMpJoin` | --fastmp 的 Join 逻辑 | 游戏内置 |

#### Steam API

| 类/方法 | 功能 | 使用位置 |
|---------|------|---------|
| `SteamFriends.GetPersonaName()` | 获取 Steam 昵称 | SteamPersonaNamePatch |
| `SteamUser.GetSteamID()` | 获取 CSteamID | SteamUserIDPatch |
| `CSteamID.GetAccountID()` | 获取 AccountID | SteamUserIDPatch |
| `CSteamID(AccountID_t, EUniverse, EAccountType)` | 构造 CSteamID | SteamUserIDPatch |

#### Godot 节点/屏幕

| 类 | 功能 | 使用位置 |
|----|------|---------|
| `NCombatScreen` | 战斗界面 | ScreenDetector |
| `NMapScreen` | 地图界面 | MapDecider, ScreenDetector |
| `NMapScreen.Instance` | 地图单例 | MapDecider, RunContext |
| `NMapPoint` | 地图节点 | MapDecider, MapHandler |
| `NEventRoom` | 事件房间 | EventDecider, EventRoomHandler |
| `NRestSite` | 火堆 | RestDecider, RestSiteHandler |
| `NShopScreen` | 商店 | ShopDecider, ShopHandler |
| `NTreasureRoom` | 宝箱房间 | TreasureDecider |
| `NOverlayStack.Instance` | 覆盖层栈 | ScreenDetector, StuckDetector |
| `NOverlayStack.Instance.Peek()` | 查看顶部覆盖层 | ScreenDetector |
| `NOverlayStack.Instance.ScreenCount` | 覆盖层数量 | ScreenDetector |
| `NCardRewardOverlay` | 卡牌奖励覆盖层 | CardRewardDecider |
| `NDeckUpgradeSelectScreen` | 升级选牌界面 | CardGridDecider |
| `NDeckRemoveSelectScreen` | 删牌选牌界面 | CardGridDecider |
| `NDeckTransformSelectScreen` | 变化选牌界面 | CardGridDecider |
| `NDeckEnchantSelectScreen` | 附魔选牌界面 | CardGridDecider |
| `NGameOverScreen` | 游戏结束界面 | GameOverHandler |
| `NCharacterSelectScreen` | 角色选择界面 | AutoSlayNode |
| `NMultiplayerHostSubmenu` | 多人 Host 子菜单 | AutoSlayNode.HandleMultiplayerMenu |
| `NSubmenuButton` | 子菜单按钮 | AutoSlayNode |

#### 存档/解锁

| 类/方法 | 功能 | 使用位置 |
|---------|------|---------|
| `SaveManager.DeleteAllSaves()` | 删除所有存档 | AutoSlayNode（防 continue? 弹窗） |
| `UnlockManager.UnlockAll()` | 解锁所有卡牌/节点 | AutoSlayNode.UnlockAll() |

#### 反射工具

| 方法 | 用途 |
|------|------|
| `CardModelInspector.TestWithCard()` | 运行时发现 DynamicVar 类型/属性 |
| `CardEffectReader.ReadEffects(CardModel)` | 读取卡牌效果值（伤害/格挡/debuff等） |
| `CardEffectReader.FallbackEstimate(string cardId)` | 反射失败时的硬编码兜底（~40 Ironclad 卡） |
| `AutoSlayHelpers.FindFirst<T>(Node)` | 递归查找第一个指定类型子节点 |
| `AutoSlayHelpers.FindAll<T>(Node)` | 递归查找所有指定类型子节点 |
| `AutoSlayHelpers.ForceClick(Button)` | 强制点击按钮（模拟输入） |

### 8.3 Harmony 特性

| 特性 | 用途 |
|------|------|
| `[ModInitializer(nameof(Method))]` | Mod 入口点 |
| `[ModuleInitializer]` | DLL 加载时最先执行 |
| `[HarmonyPatch(typeof(Target), "MethodName")]` | 声明补丁目标 |
| `[HarmonyPatch(typeof(Target), nameof(Target.Method))]` | 类型安全的目标声明 |
| `[HarmonyPrefix]` | 在原方法前执行 |
| `[HarmonyPostfix]` | 在原方法后执行 |
| `harmony.PatchAll(assembly)` | 安装 Assembly 中所有补丁 |
| `harmony.CreateClassProcessor(type).Patch()` | 安装单个类的补丁 |

---

## 9. 参数系统 (params.json)

所有可调参数集中在 `params.json`，修改后无需重新编译。

### 参数组总览

| 组 | 包含 | 用途 |
|----|------|------|
| `combat_solver` | scoring, safety | 战斗求解器评分权重 |
| `combat_sequencing` | 出牌顺序奖励/惩罚 | 二维出牌排序参数 |
| `future_value` | max_turns, discount_rate | 能力牌未来价值估算 |
| `deck_quality` | 薄牌库奖励 | 牌库质量评分 |
| `card_reward` | 22阶段权重 + 跳过阈值 | 卡牌抓取评分 |
| `map` | 节点评分、路径奖励 | 地图寻路 |
| `rest` | 阈值、各选项评分 | 火堆决策 |
| `event` | HP代价、关键词 | 事件选择 |
| `shop` | 黄金储备、遗物/卡牌价值 | 商店购买 |
| `potion` | 药水使用策略 | 药水 |

### 关键参数示例

```json
{
  "combat_solver": {
    "max_search_states": 6000,
    "scoring": {
      "damage_per_point": 16,
      "block_per_needed_point": 3.5,
      "focus_fire_multiplier": 16.0,
      "boss_priority_multiplier": 8.0,
      "elite_block_multiplier": 0.75,
      "boss_block_multiplier": 0.6
    }
  },
  "combat_sequencing": {
    "two_dimensional_ordering_enabled": true,
    "two_dim_ordering_score_per_point": 2.0,
    "two_dim_ordering_penalty_per_point": 3.0
  },
  "future_value": {
    "max_turns": 8,
    "discount_rate": 0.5
  }
}
```

---

## 10. 启动脚本

### launch.ps1 — 统一启动器

```powershell
# 单人
.\launch.ps1 -Mode solo_bot -Character SILENT
.\launch.ps1 -Mode solo_player -Seed ABC123

# 多人
.\launch.ps1 -Mode coop_1bot    # 1人 + 1Bot (2窗口)
.\launch.ps1 -Mode coop_2bot    # 1人 + 2Bot (3窗口)
.\launch.ps1 -Mode coop_3bot    # 1人 + 3Bot (4窗口)
```

### launch_lan.ps1 — LAN 专用启动器

```powershell
.\launch_lan.ps1 -Character IRONCLAD -BotCount 2  # 3人
.\launch_lan.ps1 -BotCount 3                       # 4人
.\launch_lan.ps1 -BotCount 1 -Seed ABC123          # 2人
```

### 启动参数

| 参数 | 值 | 用途 |
|------|-----|------|
| `--fastmp host_standard` | Host | 创建 ENet 服务器 + 标准 Host 流程 |
| `--fastmp join` | Client | ENet 连接 127.0.0.1:33771 |
| `--config <path>` | 所有实例 | 指定 per-instance 配置文件 |

### 配置文件位置

| 文件 | 实例 | 用途 |
|------|------|------|
| `token_spire_host.json` | Host | Host 配置 |
| `token_spire_bot1.json` | Bot1 | Bot1 配置 |
| `token_spire_bot2.json` | Bot2 | Bot2 配置 |
| `token_spire_bot3.json` | Bot3 | Bot3 配置 |
| `config_read_host.signal` | Host | Host 就绪信号 |
| `config_read_botN.signal` | BotN | BotN 就绪信号 |

---

## 11. 自动化优化工作流

### 11.1 遗传算法优化器

```
optimizer.py
  → 随机生成参数组合（种群 20 个体，43 个基因）
  → batch_runner.py 逐局测试
  → 评估 HP 损失（fitness 函数）
  → 锦标赛选择 k=3 + 均匀交叉 (50%) + 高斯变异 (30%)
  → 精英保留 (top 20%)
  → 退化检测（低于最佳 70% → 回滚）
  → 迭代至收敛
```

### 11.2 完整流水线

```
full_auto_pipeline.py
  1. 能力审计 → 检测 AI 能否处理所有卡牌/遗物/事件
  2. 灵敏度分析 → 确定哪些参数对胜率影响最大
  3. 遗传算法优化 → 基于分析结果聚焦优化关键参数
```

### 11.3 CombatSimulator

独立 Python 战斗模拟器（`E:/CombatSimulator/`）：
- 离线模拟，不依赖游戏引擎
- 贪心求解器 (8,280次) + DFS 验证 (210次)
- 31 个原子级评分权重
- 按 Act 分阶段优化

---

## 12. 构建与部署

### 前提条件

- .NET 9.0 SDK
- Slay the Spire 2 (Steam) — `E:\SteamLibrary\steamapps\common\Slay the Spire 2`
- Godot 4.5.1（mod 开发）
- Windows 11（构建目标）

### 构建命令

```bash
cd "E:\SteamLibrary\steamapps\common\Slay the Spire 2\mods\TokenSpire2"
dotnet build -c Release
```

### 构建产物

- `mods/TokenSpire2/TokenSpire2.dll` — 编译后的 mod DLL
- `mods/TokenSpire2/TokenSpire2.pck` — Godot PCK (格式 v3)，包含 `mod_manifest.json`

### 引用依赖

| 依赖 | 来源 |
|------|------|
| `0Harmony.dll` | 游戏 data 目录（Harmony 补丁框架） |
| `sts2.dll` | 游戏 data 目录（游戏核心代码） |
| `Steamworks.NET.dll` | 游戏 data 目录（Steam API） |
| `Alchyr.Sts2.BaseLib` | NuGet（ModInitializer 特性 + 工具） |
| `BepInEx.AssemblyPublicizer` | NuGet（编译时公开 internal 类型） |

---

## 13. 已知限制与 Bug

### 🔴 严重

| # | 问题 | 状态 |
|---|------|------|
| 1 | Bot2/Bot3 无法加入 LAN 房间（重复 NetId=1000） | 调查中 |
| 2 | `LocalContextNetIdPatch` 导致 Host 初始化失败 | 已隔离 |

### 🟡 中等

| # | 问题 | 说明 |
|---|------|------|
| 3 | 事件文本解析不可靠 | 回退到位置启发式（粗糙） |
| 4 | 遗物名称子串匹配 | 可能误判（如 "VajraMaster" 匹配 "vajra"） |
| 5 | 多处硬编码 Godot 节点路径 | 游戏更新可能破坏 |
| 6 | ChooseCardDecider 上下文可能过时 | 有兜底回退 |
| 7 | 非 Ironclad 卡牌效果反射失败时回退值不准 | FallbackEstimate 只覆盖 ~40 Ironclad 卡 |
| 8 | 充能球被动效果不在出牌间模拟 | Defect 多卡计划可能不准确 |
| 9 | 抽牌模拟不随机 | 总是从抽牌堆顶部按顺序抽 |

### 🟢 轻微

| # | 问题 | 说明 |
|---|------|------|
| 10 | 大量 LLM 死代码（~500+ 行） | 不影响功能 |
| 11 | 升级价值评估粗糙（只计数） | RestDecider |
| 12 | `_drawJustFinished` 未在战斗结束时重置 | 极低概率影响下局战斗 |

---

## 14. 版本历史

| 版本 | 日期 | 变更 |
|------|------|------|
| v0.3.0 | 2026-07-12 | --fastmp ENet LAN 联机、二维出牌算法、CardDatabase |
| v0.2.0 | 2026-07-08 | Coop 集成、B14/B17 修复、Act 2 参数优化、中文支持 |
| v0.1.0 | 2026-07-01 | 初始版本，5 决策系统 + MCTS 战斗求解器 |

---

## 致谢

- **STS2CouchCoop**: 本地多人模组架构参考
- **CommunicationMod**: 决策模组架构参考
- **Slay the Spire 2 Modding Discord**: 技术支持和 API 文档
- **alchyr**: BaseLib + ModAnalyzers NuGet 包
- **OP.GG**: 卡牌/遗物胜率统计数据
