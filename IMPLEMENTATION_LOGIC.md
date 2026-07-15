# TokenSpire2 Mod 实现逻辑详解

> **版本:** v3  
> **最后更新:** 2026-07-14  
> **适用游戏:** Slay the Spire 2 (Godot 4 + C#)  
> **Mod 类型:** 全自动 AI 战斗 Bot + LAN 多人合作 + AI 角色聊天

---

## 目录

1. [整体架构概览](#1-整体架构概览)
2. [入口与初始化流程](#2-入口与初始化流程)
3. [配置系统 (AppConfig)](#3-配置系统-appconfig)
4. [屏幕检测系统 (ScreenDetector)](#4-屏幕检测系统-screendetector)
5. [主控制循环 (AutoSlayNode._Process)](#5-主控制循环-autoslaynode_process)
6. [决策引擎 (DecisionEngine)](#6-决策引擎-decisionengine)
7. [运行状态追踪 (RunContext)](#7-运行状态追踪-runcontext)
8. [战斗求解器 (IroncladSolver)](#8-战斗求解器-ironcladsolver)
9. [各类 Decider 详解](#9-各类-decider-详解)
10. [ICardSelector 选卡系统](#10-icardselector-选卡系统)
11. [Harmony 补丁系统](#11-harmony-补丁系统)
12. [多人模式架构](#12-多人模式架构)
13. [AI 聊天系统](#13-ai-聊天系统)
14. [启动器 (GUI Launcher)](#14-启动器-gui-launcher)
15. [卡住检测与恢复](#15-卡住检测与恢复)
16. [日志与诊断系统](#16-日志与诊断系统)
17. [完整数据流图](#17-完整数据流图)

---

## 1. 整体架构概览

TokenSpire2 是一个高度模块化的 STS2 Mod，核心功能包括：

```
┌─────────────────────────────────────────────────────────────┐
│                    TokenSpire2 Mod                           │
├─────────────────────────────────────────────────────────────┤
│  MainFile.cs          ← 入口 [ModInitializer]                │
│  AutoSlayNode.cs      ← 主 Bot 控制器 (_Process 每帧循环)     │
├─────────────────────────────────────────────────────────────┤
│  Core/                 ← 基础设施层                           │
│    AppConfig.cs        ← 全局配置单例                         │
│    ScreenDetector.cs   ← 屏幕/界面状态检测                     │
│    StuckDetector.cs    ← 卡住检测与恢复                       │
│    RunContext.cs       ← 持久运行状态追踪                     │
│    GameScreen.cs       ← 游戏界面枚举                         │
├─────────────────────────────────────────────────────────────┤
│  Solver/               ← AI 决策层                            │
│    DecisionEngine.cs   ← 决策路由器（唯一入口）                │
│    IroncladSolver.cs   ← 战斗求解器（出牌规划）                │
│    MapDecider.cs       ← 地图路径选择                         │
│    CardRewardDecider.cs← 选卡奖励评估                         │
│    EventDecider.cs     ← 事件选择                             │
│    ShopDecider.cs      ← 商店购买策略                         │
│    RestDecider.cs      ← 火堆休息/升级决策                     │
│    CardGridDecider.cs  ← 升级/删除/转化选卡                    │
│    ChooseCardDecider.cs← 战斗中选卡（Headbutt/Armaments等）    │
│    SimpleSelectDecider ← 简单选卡确认                          │
│    RelicDecider.cs     ← 遗物选择                             │
│    TreasureDecider.cs  ← 宝箱处理                             │
│    BundleDecider.cs    ← 卡牌捆绑包选择                        │
│    BossStrategy.cs     ← Boss 特定策略                        │
│    CharacterConfigs.cs ← 角色特定权重配置                      │
│    CardDatabase.cs     ← 卡牌数据库单例                        │
│    CardClassifier.cs   ← 卡牌分类工具                         │
│    SolverParams.cs     ← 可调参数加载器                        │
│    Tiebreaker.cs       ← 平局确定性选择（多人模式安全）         │
│    DecisionLogger.cs   ← 决策审计日志                          │
├─────────────────────────────────────────────────────────────┤
│  AutoSlayCardSelector.cs ← ICardSelector 实现（游戏引擎级钩子） │
├─────────────────────────────────────────────────────────────┤
│  Patches/               ← Harmony 运行时 IL 注入补丁           │
│    ForceENetHostPatch.cs     ← 强制 ENet Host 模式             │
│    SteamPersonaNamePatch.cs  ← Steam 显示名称替换              │
│    SteamIdPatch.cs           ← Steam ID 覆盖                  │
│    ENetClientNetIdPatch.cs   ← 网络 ID 覆盖                   │
│    FlavorTextPatch.cs        ← 角色文本替换（AI 对话注入）      │
│    PatchRegistry.cs          ← 补丁注册管理                    │
├─────────────────────────────────────────────────────────────┤
│  Chat/                  ← AI 角色聊天系统                      │
│    ChatEngine.cs        ← DeepSeek API 客户端                 │
│    ConversationManager.cs← 文件级共享对话日志                  │
│    ChatLogger.cs        ← 聊天持久化                          │
│    CharacterProfileManager.cs ← 角色人设管理                   │
│    GameStateExtractor.cs ← 游戏状态提取（供 LLM 上下文）       │
│    PromptLibrary.cs     ← 模块化 Prompt 模板库                 │
│    CombatRecorder.cs    ← 战斗事件录制器                       │
│    AiChatConfig.cs      ← AI 聊天配置                          │
├─────────────────────────────────────────────────────────────┤
│  AutoBattle/            ← 事件驱动控制器（骨架，逐步迁移中）    │
│    IScreenHandler.cs    ← 屏幕处理器接口                       │
│    ScreenDispatcher.cs  ← 屏幕分发器                          │
│    AutoBattleController.cs ← 事件驱动控制器                    │
├─────────────────────────────────────────────────────────────┤
│  tools/Launcher/        ← GUI 启动器（WinForms）               │
│    MainForm.cs          ← 主窗口：配置角色、种子、Bot 数量等    │
└─────────────────────────────────────────────────────────────┘
```

**设计原则:**
- **确定性优先:** 多人模式下所有决策必须是确定性的（相同输入→相同输出），防止 `StateDivergence`
- **绝不随机:** 所有 Decider 基于启发式评分，不使用 `Random` 做决策
- **启发式驱动:** 每个决策都有明确的评分逻辑和可解释的原因
- **安全默认:** 不确定时选择最保守的操作（不选牌、不回血、不走危险路线）

---

## 2. 入口与初始化流程

### 2.1 `MainFile.Initialize()` — 模组加载入口

```csharp
[ModInitializer(nameof(Initialize))]
public partial class MainFile : Node
{
    public static void Initialize()
    {
        // ① 分配控制台窗口
        // ② 确定 mod 目录路径
        // ③ AppConfig.Initialize() — 加载配置
        // ④ CardDatabase.Initialize() — 加载卡牌数据库
        // ⑤ AiChatConfig + CharacterProfileManager 初始化
        // ⑥ Harmony.PatchAll() — 安装所有 IL 补丁
        // ⑦ AttachNodes() — 将 AutoSlayNode 挂载到 Godot 场景树
    }
}
```

**执行顺序图：**

```
游戏启动 → 扫描 mods/ 目录 → 找到 TokenSpire2/
  → 加载 DLL → 发现 [ModInitializer] 特性
  → 调用 MainFile.Initialize()
    → Step 1: AppConfig 加载配置（--config CLI 参数 → 环境变量 → batch_config.json）
    → Step 2: CardDatabase 加载卡牌评分数据库
    → Step 3: AiChatConfig + CharacterProfileManager 加载 AI 配置
    → Step 4: Harmony 安装所有补丁（Steam ID / 网络 / 角色文本）
    → Step 5: AttachNodes() 将 AutoSlayNode 添加到场景树根节点
```

### 2.2 AutoSlayNode 挂载

`AttachNodes()` 检查场景树根节点是否已有 `AutoSlayNode`，如果没有则手动创建并添加：

```csharp
var autoSlay = new AutoSlayNode();
autoSlay.Name = "AutoSlayNode";
root.AddChild(autoSlay);
```

`AutoSlayNode` 继承自 Godot 的 `Node`，因此拥有完整的生命周期：
- `_Ready()` — 节点进入场景树时调用一次
- `_Process(double delta)` — 每帧调用（60fps）
- `_ExitTree()` — 节点离开场景树时调用

---

## 3. 配置系统 (AppConfig)

### 3.1 配置加载优先级

```
CLI 参数 --config <path>      ← 最高优先级（每个实例独立配置）
  ↓
环境变量 TOKENSPIRE2_CONFIG
  ↓
{mod目录}/batch_config.json   ← 最低优先级（默认路径）
```

### 3.2 配置结构

```csharp
public class AppConfig
{
    // ── 单机模式 ──
    bool AutoBattleEnabled;    // 是否启用自动战斗
    bool AutoBattlePaused;     // 是否暂停自动战斗
    string AutoBattleScope;    // 自动战斗范围
    bool BatchMode;            // 批处理模式（自动种子+角色）
    string Seed;               // 种子
    string Character;          // 角色
    float HpMultiplier;        // HP 倍率

    // ── 多人模式 ──
    bool MultiplayerMode;      // 是否多人模式
    bool IsMultiplayerHost;    // 是否主机
    string SteamPersonaName;   // Steam 显示名称

    // ── AI 聊天 ──
    bool AiChatEnabled;        // 是否启用 AI 聊天
    string AiChatCharacter;    // AI 角色名
}
```

### 3.3 多人模式下的配置传递

每个游戏实例通过 `--config` CLI 参数接收独立配置文件。Launcher 为每个实例生成独立的 JSON：

```
实例1 (Host):    coop_configs/ironclad_host.json   → Character=IRONCLAD, IsHost=true
实例2 (Bot):     coop_configs/silent_bot.json      → Character=SILENT, IsHost=false, AutoBattle=true
实例3 (Bot):     coop_configs/defect_bot.json      → Character=DEFECT, IsHost=false, AutoBattle=true
```

---

## 4. 屏幕检测系统 (ScreenDetector)

### 4.1 设计目标

在 Godot 渲染主线程中，通过检测场景树节点状态来判断当前游戏界面。

### 4.2 检测优先级（从高到低）

```
① 叠加层 (Overlays)          — NOverlayStack 栈顶检测
  ├── NCardRewardSelectionScreen  → OVERLAY_CARD_REWARD
  ├── NRewardsScreen              → OVERLAY_REWARDS
  ├── NGameOverScreen             → GAME_OVER
  ├── NChooseACardSelectionScreen → OVERLAY_CHOOSE_CARD
  ├── NChooseABundleSelectionScreen → OVERLAY_CHOOSE_BUNDLE
  ├── NChooseARelicSelection      → OVERLAY_CHOOSE_RELIC
  ├── NDeckUpgradeSelectScreen等   → OVERLAY_DECK_GRID
  ├── NSimpleCardSelectScreen     → OVERLAY_SIMPLE_SELECT
  └── NCrystalSphereScreen        → OVERLAY_CRYSTAL_SPHERE

② 战斗 (Combat)               — CombatManager.Instance.IsInProgress

③ 地图 (Map)                  — NMapScreen.Instance.IsOpen

④ 多人界面 (Multiplayer)
  ├── CharacterSelectRoom      → CHARACTER_SELECT
  ├── MultiplayerHostSubmenu   → MULTIPLAYER_HOST_SUBMENU
  ├── Friend List (刷新按钮)    → MULTIPLAYER_FRIEND_LIST
  ├── MultiplayerSubmenu       → MULTIPLAYER_SUBMENU
  └── LobbyRoom               → LOBBY

⑤ 房间 (Rooms)                — 通过场景树节点检测
  ├── EventRoom                → EVENT
  ├── TreasureRoom             → TREASURE
  ├── RestSiteRoom             → REST
  └── MerchantRoom             → SHOP

⑥ 战斗胜利                    — NCombatRoom.ProceedButton.IsEnabled

⑦ 主菜单                      — "MainMenu" 节点可见
```

### 4.3 稳定性过滤

为了防止界面切换期间的误判，`ScreenDetector` 包含稳定性过滤：

```csharp
const int STABILITY_THRESHOLD = 3; // 同一界面必须持续 3 帧

public static GameScreen Detect()
{
    var screen = DetectInternal();
    if (screen == _previousScreen && _stabilityCounter < 3)
        _stabilityCounter++;
    else if (screen != _previousScreen)
        _stabilityCounter = 0;
    // 只有稳定 3 帧后才返回新界面
    return _stabilityCounter >= 3 ? screen : _previousScreen;
}
```

---

## 5. 主控制循环 (AutoSlayNode._Process)

### 5.1 每帧执行流程

`_Process(double delta)` 是整个 Mod 的核心循环，每帧（约60fps）执行一次。流程图如下：

```
_Process(delta)
  │
  ├── ① F1/F2/F3 快捷键检测
  │     F1 → 切换 _autoNavigate （地图自动导航）
  │     F2 → 切换 _autoBattle   （自动战斗）
  │     F3 → 切换 _autoEvent    （自动事件）
  │
  ├── ② 心跳日志（每 5 秒）
  │
  ├── ③ 手动模式检查 (_disableAutoPlay)
  │     → 解锁全部内容和快速模式，跳过所有自动逻辑
  │
  ├── ④ LLM 等待检查 (_cardSelector.IsPendingLlm)
  │     → 如果 LLM 异步选卡还在进行，跳过本帧
  │
  ├── ⑤ 冷却计时 (_cooldown, _combatCardDelay)
  │
  ├── ⑥ 多人模式错误弹窗消除
  │
  ├── ⑦ 卡住检测（战斗和非战斗）
  │     → 超时则写诊断文件并强制恢复
  │
  ├── ⑧ 种子覆盖 + HP倍率应用
  │
  ├── ⑨ LLM 响应处理
  │     → 等待 LLM 完成 → 解析结果 → 执行
  │
  ├── ⑩ 战斗中叠加层处理（Headbutt/Armaments 选卡等）
  │     → DispatchOverlay()
  │
  ├── ⑪ 战斗计划执行 (_combatPlan)
  │     → ExecuteNextCombatStep() 逐张出牌
  │
  ├── ⑫ 战斗状态 (_autoBattle 开启)
  │     ├── 等待抽牌动画完成（稳定性检查）
  │     ├── IroncladSolver 求解
  │     ├── LLM 或解算器生成出牌计划
  │     ├── 执行出牌计划
  │     └── EndTurnViaUiOrApi 结束回合
  │
  ├── ⑬ 非战斗自动导航 (_autoNavigate 开启)
  │     ├── HandleMainMenu()    — 主菜单导航
  │     ├── HandleMap()          — 地图选路
  │     ├── HandleShop()         — 商店处理
  │     ├── HandleRest()         — 火堆处理
  │     ├── HandleEvent()        — 事件处理
  │     ├── HandleTreasure()     — 宝箱处理
  │     └── HandleRewards()      — 奖励屏幕
  │
  └── ⑭ AI 聊天检测 (TrySendAiChat)
```

### 5.2 特性开关系统

```
┌──────────┬─────────────┬──────────────────────────┐
│   按键   │   内部变量    │          功能            │
├──────────┼─────────────┼──────────────────────────┤
│   F1    │_autoNavigate│ 自动地图导航+商店+火堆+事件 │
│   F2    │_autoBattle  │ 自动战斗（出牌+结束回合）    │
│   F3    │_autoEvent   │ 自动选卡奖励+遗物+药水       │
└──────────┴─────────────┴──────────────────────────┘

IsFullAuto = _autoNavigate && _autoBattle && _autoEvent
IsHostManualMode = multiplayerMode && isHost && !IsFullAuto
```

### 5.3 多人模式下的主机手动模式

当主机在多人模式下关闭任何自动功能时，`IsHostManualMode = true`，此时：
- **所有自动决策被跳过** — 地图、商店、火堆、事件、选卡奖励等
- **ICardSelector 仍然注册** 但会在 `AutoSlayCardSelector` 内部检测主机模式
- **只有 AI 聊天继续运行** — Bot 仍会和主机对话

---

## 6. 决策引擎 (DecisionEngine)

### 6.1 设计理念

`DecisionEngine` 是所有非战斗决策的**唯一入口**。它的职责是：

1. **稳定性门控** — 确保游戏状态稳定后才做决策
2. **路由** — 根据 `GameScreen` 分发到对应的 Decider
3. **状态刷新** — 每次决策前刷新 `RunContext`

### 6.2 决策流程

```
DecisionEngine.Decide(screen, delta)
  │
  ├── StateStabilityDetector.IsStableForDecision()
  │   → 不稳定 → return false（等待游戏状态稳定）
  │
  ├── _runState.Refresh()
  │   → 更新 HP、金币、牌组、遗物、药水、协同标志等
  │
  └── switch(screen)
      ├── MAP                   → MapDecider.Decide()
      ├── EVENT                 → EventDecider.Decide()
      ├── REST                  → RestDecider.Decide()
      ├── SHOP                  → ShopDecider.Decide()
      ├── TREASURE              → TreasureDecider.Decide()
      ├── OVERLAY_CARD_REWARD   → CardRewardDecider.Decide()
      ├── OVERLAY_CHOOSE_CARD   → ChooseCardDecider.Decide()
      ├── OVERLAY_CHOOSE_BUNDLE → BundleDecider.Decide()
      ├── OVERLAY_CHOOSE_RELIC  → RelicDecider.Decide()
      ├── OVERLAY_DECK_GRID     → CardGridDecider.Decide()
      ├── OVERLAY_SIMPLE_SELECT → SimpleSelectDecider.Decide()
      └── OVERLAY_CRYSTAL_SPHERE→ CrystalSphereDecider.Decide()
```

### 6.3 战斗中选卡上下文

当 Solver 打出触发选卡的牌时（如 Headbutt、True Grit、Armaments），`DecisionEngine` 会记录选卡上下文：

```csharp
public static void SetPendingCardSelect(string cardId)
{
    // HEADBUTT, WARCRY      → "PUT_ON_TOP"  （选最好的牌放牌堆顶）
    // TRUE_GRIT, BURNING_PACT → "EXHAUST"    （选最差的牌消耗）
    // ARMAMENTS             → "UPGRADE"     （选最佳升级目标）
    // EXHUME, HOLOGRAM      → "RETRIEVE"    （从消耗/弃牌堆取回）
    // SECRET_TECHNIQUE      → "FETCH_SKILL" （选技能牌）
    // SECRET_WEAPON         → "FETCH_ATTACK"（选攻击牌）
}
```

这个上下文有 2 秒超时，超时后自动清除。

---

## 7. 运行状态追踪 (RunContext)

### 7.1 职责

`RunContext` 在每次决策前从游戏引擎刷新状态，提供：
- 基础状态：角色、HP、金币、楼层、章节
- 牌组分析：卡牌数量、攻击/技能/能力牌统计、费用曲线、过牌密度
- 遗物与药水：遗物列表、药水槽位
- 协同标志：力量、敏捷、消耗、格挡、中毒、弃牌、充能球等

### 7.2 协同检测逻辑

```
职业协同检测：
  IRONCLAD: 消耗(Feel No Pain/Dark Embrace/Corruption)
            格挡(Barricade/Entrench/Body Slam)
            自伤(Rupture/Hemokinesis)
  SILENT:   中毒(Noxious Fumes/Catalyst/Envenom)
            弃牌(Tactician/Reflex/Calculated Gamble)
  DEFECT:   充能球(Electrodynamics/Loop/Capacitor)
            集中(Defragment/Biased Cognition/Consume)
```

### 7.3 卡牌分类

`RunContext` 维护静态卡牌分类集合：

| 分类 | 示例卡牌 | 用于 |
|------|---------|------|
| `_drawCardIds` | Pommel Strike, Backflip, Skim | 过牌密度计算 |
| `_energyCardIds` | Offering, Adrenaline, Turbo | 能量引擎评估 |
| `_aoeCardIds` | Cleave, Whirlwind, Electrodynamics | AOE 能力评估 |
| `_scalingCardIds` | Demon Form, Noxious Fumes, Defragment | 成长能力评估 |

### 7.4 流派协同组合检测

`RunContext` 同时追踪 `ComboDatabase` 中定义的流派协同（如力量流需要的核心卡组合、壁垒流需要的组件等），为 `CardRewardDecider` 提供协同加分依据。

---

## 8. 战斗求解器 (IroncladSolver)

### 8.1 核心算法

尽管名为 `IroncladSolver`，它支持**所有五个角色**（Ironclad、Silent、Defect、Regent、Necrobinder）。

```
输入:
  - 手牌列表 (List<CardModel>)
  - 战斗状态 (敌人HP、意图、玩家能量/HP/格挡/力量/敏捷/易伤/虚弱)
  - 角色配置 (CharacterConfig)

处理:
  ┌────────────────────────────────────────────┐
  │  ① 为每张手牌计算优先级分数                 │
  │     基础分 + 上下文加成 + 协同加成           │
  │                                            │
  │  ② 生成候选出牌序列（排列组合）              │
  │     - 按优先级排序                          │
  │     - 考虑能量消耗                          │
  │     - 应用 BEFORE/AFTER 规则               │
  │     (如：Shockwave BEFORE 攻击牌)           │
  │                                            │
  │  ③ 对每个候选序列评估状态                    │
  │     - 模拟出牌后的HP/格挡/敌人状态           │
  │     - 计算序列加成分                        │
  │                                            │
  │  ④ 选择最佳序列                             │
  │     - 最高总分 = 优先级分 + 序列加成 + 状态分 │
  └────────────────────────────────────────────┘

输出:
  - 推荐出牌序列 (List<CombatAction>)
  - 每张牌的目标选择（敌人或自身）
```

### 8.2 二维评分系统

```
第一维：卡牌本身价值
  - powerValue:    能力牌的长期价值
  - attackValue:   攻击牌的伤害价值
  - blockValue:    格挡牌的防御价值
  - utilityValue:  功能性（过牌/能量/debuff）

第二维：当前战斗上下文
  - enemyContext:  敌人意图（攻击/防御/buff/debuff）
  - hpContext:     玩家HP状态
  - energyContext: 当前能量

最终分数 = 第一维分数 × 第二维权重
```

### 8.3 BEFORE/AFTER 出牌规则

硬编码的出牌顺序规则确保关键 combo：

```
BEFORE 规则 (此牌必须在XX之前出):
  Shockwave     → BEFORE 攻击牌（先上易伤）
  Battle Trance → BEFORE 其他牌（先过牌）
  Offering      → BEFORE 高费牌（先回能量）

AFTER 规则 (此牌必须在XX之后出):
  攻击牌        → AFTER Shockwave（享受易伤加成）
  格挡牌        → AFTER Power Through（先获得状态牌）
```

### 8.4 角色特定策略

`CharacterConfigs.cs` 为每个角色定义了独特的权重：

```
IRONCLAD: 力量成长优先 > 攻击 > 格挡
SILENT:   中毒堆叠优先 > 弃牌引擎 > 敏捷
DEFECT:   充能球/集中优先 > 格挡(冰球) > 攻击(电球)
REGENT:   星辰协同优先 > 能力牌 > 攻击
NECROBINDER: 灵魂协同优先 > 召唤 > 格挡
```

### 8.5 Boss 策略

`BossStrategy.cs` 包含针对特定 Boss 的策略调整：

```
Slime Boss:  保留高伤害牌等分裂后使用
Guardian:    控制伤害节奏避免触发防御形态
Hexaghost:   前几回合多出攻击（伤害与HP相关）
Bronze Auto: 优先击杀小怪
Champ:       前半段控制伤害，后半段爆发
```

---

## 9. 各类 Decider 详解

### 9.1 MapDecider — 地图路径选择

**核心逻辑：**

```
① 获取所有可达节点 (NMapPoint)
② 为每条路径评分：
   - 精英节点: 高分（遗物奖励）
   - 火堆节点: 中分（升级/回血）
   - 商店节点: 中分（花钱去处）
   - 问号节点: 低分（事件不可预测）
   - 普通战斗: 低分（无奖励）
③ 根据 HP 状态调整：
   - HP < 40%: 火堆权重 ↑↑，精英权重 ↓
   - HP > 70%: 精英权重 ↑，火堆权重 ↓
④ 选择最优路径
⑤ 找到路径起点并 ForceClick
```

**安全机制：**
- 单路径不死锁：只有一个选择时直接选
- 重算保护：`_recalcBackoff` 防止无限重新计算
- 多人模式适配：由 `_autoNavigate` 开关控制

### 9.2 CardRewardDecider — 选卡奖励

**评分维度（18个阶段）：**

```
Phase 1-3:   基础过滤
  - 去除诅咒/状态牌
  - 检查 MAX_COPIES 限制（不重复拿太多同名牌）
  - 稀有度基础分

Phase 4-7:   卡牌价值
  - 攻击/格挡/技能/能力基础分
  - 费用效率（0费→加分，3费→扣分）
  - 升级状态加分

Phase 8-12:  牌组适配
  - 攻击牌过少 → 攻击牌加分
  - 格挡牌过少 → 格挡牌加分
  - 高费牌过多 → 低费牌加分

Phase 13-15: 协同加成
  - 已有点燃 + 看到力量牌 → 加分
  - 已有毒雾 + 看到催化剂 → 大加分
  - 已有冰球 + 看到集中 → 加分

Phase 16-18: 最终决策
  - 前25%分位阈值过滤
  - 高于绝对阈值的保留
  - 平局 → Tiebreaker 确定性选择
```

**跳过逻辑：**
```
满足任一条件 → SKIP:
  - 所有选择都低于阈值
  - 牌组已达软上限(~35张)
  - 所有选择都是 Strike/Defend 变体
```

### 9.3 EventDecider — 事件决策

事件决策基于预定义的**事件特定策略**：

```
常见事件处理：
  Big Fish:       选香蕉(回血) > 选遗物 > 选钱
  Golden Idol:    拿走（扣血换钱）- HP不够则放弃
  Scrap Ooze:     根据HP决定挖几次
  Living Wall:    优先删牌 > 升级 > 变形
  Council/Ghosts: 根据牌组决定是否拿幽灵牌
  Mushrooms:      HP>50% → 战斗(拿遗物)，HP<40% → 跳过
  Ssssserpent:    HP>65% → 拿钱扣血，否则跳过
```

**HP 安全下限:**
- 事件扣血后 HP 不会低于 60（硬编码安全线）

### 9.4 ShopDecider — 商店购物

**优先级顺序：**

```
① 删牌 (Card Removal)  ← 最优先
   目标: Curse > Status > Strike > Defend

② 购买遗物               ← 永久提升
   评分: 稀有度 + 与牌组协同

③ 购买药水               ← 补满空槽
   优先: 格挡药水 > 能量药水 > 攻击药水

④ 购买卡牌               ← 最低优先级
   仅在牌组缺少关键组件时购买
```

**Host 手动模式：**
- 跳过所有自动购买
- 仅处理 Proceed 按钮

### 9.5 RestDecider — 火堆决策

```
火堆选项:
  HealRestSiteOption:  恢复 30% HP
  SmithRestSiteOption: 升级一张牌
  MendRestSiteOption:  治疗其他玩家（多人模式）

决策逻辑:
  if HP < 40%:
      回血优先（安全第一）
  else if HP < 65% 且有优质升级目标:
      比较 回血价值 vs 升级价值
  else:
      升级优先

升级目标选择:
  - 优先: 未升级的能力牌/高费牌
  - 其次: 未升级的核心攻击/格挡牌
  - 跳过: Strike/Defend 变体
```

### 9.6 CardGridDecider — 升级/删除/转化界面

处理火堆和事件中的卡牌网格选择：

```
根据屏幕类型区分:
  UPGRADE:     选最佳升级目标（同 RestDecider 逻辑）
  REMOVE:      选最差牌（Curse > Status > Strike > Defend）
  TRANSFORM:   选最差牌（同 REMOVE）
  ENCHANT:     选最佳附魔目标
```

**双重处理阶段：**
- Phase 1: 检查 ICardSelector 是否已预选（多人模式下 Bot 的自动选择）
- Phase 2: 如果 ICardSelector 未处理（Host 手动模式），进行 UI 点击

### 9.7 ChooseCardDecider — 战斗中选卡

处理如 Headbutt（选牌放牌堆顶）、True Grit（选牌消耗）等场景：

```
上下文感知:
  PUT_ON_TOP: 选最高分牌（下回合抽到）
  EXHAUST:    选最差牌（Curse > Status > Strike > Defend）
  UPGRADE:    选最佳升级目标
  RETRIEVE:   选最高分消耗/弃牌堆牌
```

---

## 10. ICardSelector 选卡系统

### 10.1 架构

`ICardSelector` 是 STS2 游戏引擎级别的全局钩子：

```csharp
// 注册（_Ready 时）
_cardSelector = new AutoSlayCardSelector(_rng, null);
_cardSelectorScope = CardSelectCmd.UseSelector(_cardSelector);

// 注销（_ExitTree 时）
_cardSelectorScope?.Dispose();
```

**关键理解：** 这个钩子是**全局且持久**的 — 一旦注册，**所有**卡牌选择界面都会调用它，包括：
- 火堆升级/删除界面
- 战斗中的 Headbutt/Armaments/True Grit 选择
- 商店删牌界面
- 事件中的卡牌选择

### 10.2 上下文感知评分

```csharp
// REMOVE / TRANSFORM: 选最差牌
Curse(500) > Status(400) > Strike(300) > Defend(250)

// UPGRADE: 选最佳升级目标
未升级能力牌 > 未升级高费牌 > 优质起始牌(Bash/Neutralize)

// EXHAUST: 选最差牌 (同 REMOVE)
// PUT_ON_TOP / RETRIEVE / FETCH: 选最佳牌

// 默认（未知上下文）：保守策略
// 仅选 Strike/Defend，绝不选优质牌
```

### 10.3 多人模式下的确定性问题

`GetSelectedCardReward` 在多人模式下使用 FNV-1a 哈希做确定性选择：

```csharp
// 多人模式：FNV-1a 确定性哈希
if (_multiplayerMode)
{
    int hash = Fnv1aHash(card.Id.Entry);
    return options.OrderBy(o => hash).First();
}

// 单人模式：随机选择
return options[_rng.Next(options.Count)];
```

### 10.4 Host 手动模式

当 `IsHostManualMode = true` 时，`AutoSlayCardSelector` **返回空结果**，让游戏引擎显示原生 UI，允许主机手动选择。

---

## 11. Harmony 补丁系统

### 11.1 补丁列表

| 补丁文件 | 目标方法 | 功能 |
|---------|---------|------|
| `ForceENetHostPatch.cs` | `ENetClient.ConnectToHost` | 强制使用 ENet Host 模式，跳过 Steam Matchmaking |
| `SteamPersonaNamePatch.cs` | Steam 身份获取 | 替换 Steam 显示名称为自定义名称 |
| `SteamIdPatch.cs` | Steam ID 获取 | 覆盖 Steam ID（防止同一 Steam 账号多实例冲突） |
| `ENetClientNetIdPatch.cs` | ENet NetId 分配 | 覆盖网络 ID 分配逻辑 |
| `FlavorTextPatch.cs` | 角色文本获取 | 注入 AI 聊天文本到游戏 UI |

### 11.2 补丁注册

```csharp
// MainFile.Initialize() 中
var harmony = new Harmony("TokenSpire2");
harmony.PatchAll(typeof(MainFile).Assembly);
```

`Harmony.PatchAll()` 会扫描整个 Assembly 中所有标记了 `[HarmonyPatch]` 特性的类，并自动安装对应的 Prefix/Postfix/Transpiler 补丁。

### 11.3 多人模式的关键补丁

**ENet 直接连接绕过 Steam 匹配流程：**

```
正常流程:
  游戏 → Steam Matchmaking → 查找好友 → 加入游戏

补丁后流程:
  游戏 → 直接 ENet.ConnectToHost("127.0.0.1", port)
       → 跳过 Steam 验证 → 直接进入多人游戏
```

这使得同一台机器上的多个实例可以通过 `localhost` 进行多人游戏。

---

## 12. 多人模式架构

### 12.1 模式概述

STS2 使用 **锁步 (lockstep)** 多人模式（`--fastmp` 模式）。在锁步模式中：
- 所有游戏实例执行**完全相同的**游戏逻辑
- 通过 ENet 传输的是**玩家输入**（而非游戏状态）
- **确定性是关键** — 任何不确定的行为都会导致 `StateDivergence`（状态分歧）

### 12.2 TokenSpire2 的多人架构

```
┌─────────────────────────────────────────────────────┐
│                  Launcher (MainForm)                  │
│  ┌─────────┐  ┌─────────┐  ┌─────────┐              │
│  │Config生成│  │进程启动  │  │DL监控   │              │
│  └─────────┘  └─────────┘  └─────────┘              │
├─────────────────────────────────────────────────────┤
│                   Host 实例                           │
│  AutoSlayNode: IsHost=true, AutoBattle=off           │
│  - 人类玩家操作界面                                   │
│  - F1/F2/F3 控制 Bot 自动行为                         │
│  - ForceENetHostPatch 启用 ENet Server               │
├─────────────────────────────────────────────────────┤
│                   Bot 实例 1                          │
│  AutoSlayNode: IsHost=false, AutoBattle=on           │
│  - 自动战斗+导航+事件                                  │
│  - AI 聊天机器人                                      │
│  - ENet Client → 连接 localhost                       │
├─────────────────────────────────────────────────────┤
│                   Bot 实例 2                          │
│  AutoSlayNode: IsHost=false, AutoBattle=on           │
│  - 自动战斗+导航+事件                                  │
│  - AI 聊天机器人                                      │
│  - ENet Client → 连接 localhost                       │
└─────────────────────────────────────────────────────┘
```

### 12.3 确定性设计

为防止 `StateDivergence`，以下系统在多人模式下使用确定性方法：

```
① Tiebreaker:        FNV-1a 哈希替代 Random
② CardSelector:      FNV-1a 哈希替代 Random（选卡奖励）
③ 所有 Decider:      基于评分（纯确定性计算）
④ 战斗 Solver:       基于优先级+上下文（纯确定性计算）
⑤ 出牌目标选择:      确定性优先级（Boss > 低HP 精英 > 普通敌人）
```

### 12.4 主机手动模式 (IsHostManualMode)

当主机关闭任何自动开关时：

```
IsHostManualMode = multiplayerMode && isHost && !IsFullAuto

影响:
  ✅ 战斗自动 → 暂停（主机手动出牌）
  ✅ 地图导航 → 跳过（主机手动选路）
  ✅ 商店购物 → 跳过（主机手动购物）
  ✅ 火堆决策 → 跳过（主机手动升级/回血）
  ✅ 事件选择 → 跳过（主机手动选择）
  ❌ AI 聊天 → 继续（不影响）
```

### 12.5 结束回合机制

多人模式下结束回合必须通过网络安全的 UI 点击而非直接 API 调用：

```csharp
// ❌ 不安全: PlayerCmd.EndTurn() — 可能导致 StateDivergence
// ✅ 安全:   点击 EndTurnButton UI 按钮 — 通过网络同步
void EndTurnViaUiOrApi(PlayerModel player)
{
    // ① 查找 EndTurnButton
    // ② ValidateEndTurn() 检查是否可以结束
    // ③ ForceClick/Press EndTurnButton → 通过 ENet 同步
}
```

---

## 13. AI 聊天系统

### 13.1 整体架构

```
┌───────────────────────────────────────────────┐
│               ConversationManager              │
│        (文件级共享对话日志 chat_log.txt)         │
│  跨进程通信 — 所有 Bot 实例共享同一文件           │
├───────────────────────────────────────────────┤
│  Bot 1 (Delilah):  ChatEngine → DeepSeek API  │
│  Bot 2 (Elysia):   ChatEngine → DeepSeek API  │
│  Bot 3 (Seele):    ChatEngine → DeepSeek API  │
├───────────────────────────────────────────────┤
│            CharacterProfileManager             │
│       (personas/*.md — 角色人设文件)            │
└───────────────────────────────────────────────┘
```

### 13.2 对话流程

```
① 战斗开始时:
   CombatRecorder.OnCombatStart()
   → 预生成开场白（ConsumePreGeneratedDialogue）
   → 写入共享日志 + 发送 ping

② 每 5 秒 (TrySendAiChat):
   检查共享日志是否更新
   → 如果轮到自己发言:
     → GameStateExtractor 提取当前游戏状态
     → PromptLibrary 组合 Prompt
     → ChatEngine.SendAsync() 调用 DeepSeek API
     → 解析 LLM 响应
     → 写入共享日志 + 发送 ping

③ 对话轮换:
   基于共享日志的最后发言者 + 时间戳
   → 防止同时发言
   → 防止重复内容（_recentMessages 去重）
```

### 13.3 角色人设系统

每个角色拥有独立的 Markdown 人设文件：

```
mods/TokenSpire2/personas/
├── delilah.md   ← kaomoji: tsundere, 傲娇系
├── elysia.md    ← kaomoji: gentle, 温柔系
├── seele.md     ← kaomoji: sweet, 甜系
└── TEMPLATE.md  ← 人设模板
```

人设文件中的元数据：
```markdown
<!-- kaomoji: tsundere -->
<!-- archetype: 傲娇 -->
```

### 13.4 Prompt 模块化

`PromptLibrary.cs` 将 Prompt 拆分为可组合的模块：

```
系统 Prompt:
  persona_prompt (角色人设)
  + sts2_knowledge (杀戮尖塔世界观)
  + combat_rules (战斗规则背景)
  + memes (社区梗知识)

请求 Prompt:
  context (当局游戏状态)
  + event (刚刚发生的事件)
  + instruction (生成要求：6-8句，每句≤15字)
```

### 13.5 跨进程对话同步

```
Bot 1 发言 → ConversationManager.Append("Delilah", text)
  → 写入 chat_log.txt
  → 更新内存中的对话历史

Bot 2 轮询 → 检测到 chat_log.txt 更新
  → 读取新内容
  → 检查是否是自己的回合
  → 生成回应 → 写入 chat_log.txt
```

---

## 14. 启动器 (GUI Launcher)

### 14.1 功能

WinForms GUI 应用 (`tools/Launcher/MainForm.cs`)，提供：

```
① 角色选择:    每个窗口的角色下拉选择（Ironclad/Silent/Defect/Regent/Necrobinder）
② 种子输入:    固定种子或随机
③ Bot 数量:    1 Host + 0~2 Bot
④ AI 角色分配: 为每个 Bot 选择角色人设
⑤ 延迟启动:    顺序启动多个游戏实例，间隔可配置
⑥ 进程管理:    批量关闭所有游戏实例
```

### 14.2 启动流程

```
① 用户点击 "Launch"
② 生成配置文件:
   Window 0 (Host):  coop_config_ironclad_host.json
   Window 1 (Bot 1): coop_config_silent_bot.json
   Window 2 (Bot 2): coop_config_defect_bot.json
③ 写入信号文件 (marker files):
   host_ready.signal
   bot1_joined.signal
   bot2_joined.signal
④ 顺序启动游戏进程:
   Process.Start("SlayTheSpire2.exe", "--config coop_config_ironclad_host.json --fastmp")
   → 等待 Host 就绪信号
   Process.Start("SlayTheSpire2.exe", "--config coop_config_silent_bot.json --fastmp")
   → 等待 Bot1 加入信号
   Process.Start("SlayTheSpire2.exe", "--config coop_config_defect_bot.json --fastmp")
```

---

## 15. 卡住检测与恢复

### 15.1 三层检测

```
Tier 1: 战斗不活动检测
  - 超时: 45 秒
  - 触发: _lastCombatActivity > 45s
  - 恢复: 强制结束回合

Tier 2: 非战斗同屏检测
  - 超时: 45 秒（常规）/ 90 秒（战斗相邻截面）
  - 触发: 同一 screenId 持续超时
  - 恢复: 重置状态 + 冷却

Tier 3: 多人模式特殊保护
  - Bot 等待人类: 300 秒（5分钟）
  - 人类玩家: 永不杀死进程
```

### 15.2 恢复机制

```
检测到卡住 → 写诊断文件(stuck_diagnostics.txt)
  → 信号运行完成(SignalRunComplete)
  → 重置卡住状态
  → 强制执行恢复操作（如结束回合）
  → 设置冷却避免立即重试
```

---

## 16. 日志与诊断系统

### 16.1 日志层级

```
MainFile.Logger (STS2 内置 Logger)
├── Info:    常规操作日志
├── Warn:    警告（非致命问题）
├── Error:   错误（需要恢复）
└── Debug:   详细调试信息

专用日志:
├── BattleLogger:     战斗事件记录
├── BossPlayLogger:   Boss 战详细记录（存到 E 盘）
├── DecisionLogger:   决策审计日志
├── CombatRecorder:   战斗录像（供 AI 聊天摘要）
└── ConversationManager: 对话日志
```

### 16.2 Boss 战详细日志

`BossPlayLogger` 在 Boss 战中记录：
- 每回合手牌、能量、HP
- 每张打出的牌及其目标
- 敌人意图和 HP
- 牌组快照（战前牌组构成）

日志写入至 `E:\TokenSpire2_BossLogs\`。

---

## 17. 完整数据流图

### 17.1 启动流程

```
用户启动 Launcher
  → 选择角色、种子、Bot数量
  → 点击 Launch
  → 为每个实例生成 --config JSON
  → 顺序启动游戏进程
    → 游戏加载 Mod
      → MainFile.Initialize()
        → AppConfig 加载配置
        → CardDatabase 加载
        → Harmony 补丁安装
        → AutoSlayNode 挂载到场景树
      → AutoSlayNode._Ready()
        → 读取多人配置
        → 设置功能开关
        → 注册 ICardSelector
        → 初始化 AI 聊天
      → _Process 每帧循环开始
```

### 17.2 一帧内的决策流程

```
_Process(delta)
  │
  ├── 主菜单 ──→ HandleMainMenu()
  │              → 检测到多人子菜单
  │              → Host: 创建大厅
  │              → Bot: 加入大厅
  │              → 选择角色
  │              → 准备就绪
  │
  ├── 角色选择 ──→ 自动选择预配置角色
  │              → 确认
  │
  ├── 大厅 ──→ Bot: 自动准备就绪
  │           → Host: 等待所有玩家就绪后启程
  │
  ├── 地图 ──→ MapDecider.Decide()
  │           → 获取可达节点
  │           → 评分每条路径
  │           → ForceClick 目标节点
  │
  ├── 战斗 ──→ 等待抽牌完成
  │           → IroncladSolver 求解
  │           → 执行出牌计划
  │           → EndTurnViaUiOrApi
  │
  ├── 战斗胜利 ──→ 点击 Proceed
  │
  ├── 奖励 ──→ 等待 NRewardsScreen
  │           → LLM/Decider 处理卡牌/金币/药水
  │           → CardRewardDecider 选牌或跳过
  │
  ├── 商店 ──→ ShopDecider.Decide()
  │           → 删牌 → 买遗物 → 买药水 → 离开
  │
  ├── 火堆 ──→ RestDecider.Decide()
  │           → 回血或升级
  │
  ├── 事件 ──→ EventDecider.Decide()
  │           → 事件特定策略选择
  │
  ├── 宝箱 ──→ TreasureDecider.Decide()
  │           → 打开宝箱
  │           → RelicDecider 选遗物
  │
  └── 游戏结束 ──→ SignalRunComplete()
                  → 在 batch 模式下退出游戏
```

### 17.3 多人模式锁步同步

```
帧 N:
  Host:   用户按下 F2 → _autoBattle = false
  Bot 1:  F2 未按下 → _autoBattle = true
  Bot 2:  F2 未按下 → _autoBattle = true

  所有实例通过 ENet 传输输入
  → 每个实例执行相同的游戏逻辑

帧 N+1:
  Host:   手动出牌 (玩家操作)
  Bot 1:  IroncladSolver 自动出牌
  Bot 2:  IroncladSolver 自动出牌

关键:
  - 所有决策必须确定性 → 防止 StateDivergence
  - Tiebreaker 使用 FNV-1a 哈希而非 Random
  - 卡牌奖励选择使用确定性哈希
```

---

## 附录 A: 关键设计决策

| 决策 | 原因 |
|------|------|
| ICardSelector 全局注册 | 需要在所有选卡场景自动响应 |
| Solver 而非 ML | 启发式算法可解释、可调试、确定性强 |
| 分数制而非规则制 | 允许细粒度权重调优，多目标权衡 |
| 文件级共享日志 | 跨进程 AI 对话同步的最简方案 |
| Harmony 补丁 | 运行时 IL 注入，无需修改游戏代码 |
| ForceClick 而非 Press | 跳过游戏动画/验证逻辑，快速决策 |
| 确定性哈希 (FNV-1a) | 多人锁步模式必须确定性选择 |

## 附录 B: 已知限制

1. **ICardSelector 全局注册** — 永不注销，在 Host 手动模式下需内部判断
2. **_Process 主线计算** — 所有逻辑在渲染线程执行，高负载时可能掉帧
3. **路径硬编码** — 部分 UI 节点路径硬编码，游戏更新可能失效
4. **Steam 绕过** — 多人模式依赖 Harmony 补丁绕过 Steam，可能被反作弊检测
5. **DeepSeek API 依赖** — AI 聊天需要网络连接和 API Key

---

> **本文档描述的是 TokenSpire2 v3 的实现逻辑。不包含已删除的 Couch Coop 和 TCP Broker 系统。**
