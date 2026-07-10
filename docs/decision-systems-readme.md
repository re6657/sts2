# TokenSpire2 决策系统架构文档

> 本文档详细说明五个核心非战斗决策系统的执行方式、参数配置、已知Bug及边界情况。

---

## 目录

1. [地图寻路 (MapDecider)](#1-地图寻路-mapdecider)
2. [事件选择 (EventDecider)](#2-事件选择-eventdecider)
3. [奖励选择 (Rewards + CardRewardDecider)](#3-奖励选择-rewards--cardrewarddecider)
4. [休息点选择 (RestDecider)](#4-休息点选择-restdecider)
5. [商店选择 (ShopDecider)](#5-商店选择-shopdecider)
6. [已知Bug汇总](#6-已知bug汇总)

---

## 1. 地图寻路 (MapDecider)

**文件**: `src/Solver/MapDecider.cs` | **参数**: `params.json → map`

### 1.1 执行流程

```
游戏开始 → PlanBestPath() 全量规划 → 缓存路径 → 逐节点点击
                │
                ▼
        每帧 Decide() 被调用
                │
        ┌───────┴────────┐
        │ 单节点？        │ → 直接点击该节点
        │ 缓存路径有效？   │ → TryContinueCachedPath()
        │ 缓存失效？       │ → PlanBestPath() 重新规划
        └───────┬────────┘
                ▼
        点击下一个缓存节点
```

### 1.2 路径规划算法

**PlanBestPath()** (行 245-327):

1. **建图**: 按 row 分组所有 NMapPoint，从 `BuildAdjacency()` 获取边
2. **起点**: 当前 row 中所有 isEnabled 的节点
3. **DFS枚举**: `EnumeratePaths()` 从每个起点枚举所有到 topRow 的路径
4. **优先级排序** (多级排序):
   - 最多篝火 (降序)
   - 最少精英 (升序)
   - 最多商店 (降序)
   - 最少怪物 (升序)
5. **选择**: 取排序后第一条路径

### 1.3 邻接关系构建

**BuildAdjacency()** (行 333-373):

- **主路径**: `GetConnectedPoints()` 通过反射探测 20+ 属性名寻找边数据
  - 探测属性: `Edges`, `Connections`, `Links`, `Neighbors`, `NextPoints`, `Children`, `Targets`, `Destinations`, `ConnectedPoints`, `Outgoing`, `Incoming`, `NextNodes`, `Forward`, `Backward`, `Exits`, `Transitions`
  - 也探测 `Point.Children`, `Point.Edges` 等同名属性
  - 支持坐标匹配 (row/col) 和引用匹配 (MapPoint reference equality)
- **回退路径**: 如果反射未找到任何边 → 使用列邻近启发式 `|col_diff| ≤ 3`

### 1.4 节点类型检测

**NodeTypeName()** (行 518-564):

检测顺序: `Point.PointType` 属性 → NMapPoint 类型名
识别类型: `Elite`, `Boss`, `Shop`, `Campfire`, `Treasure`, `Unknown`, `Normal`

### 1.5 缓存与卡住检测

- **缓存**: `_cachedPath` 在首次规划后持续使用，直到卡住或通关
- **_clickedNodes**: 跟踪已点击的节点，防止重复点击
- **卡住检测**:
  - 同一节点 > 5s → 清除 `_clickedNodes`，重新规划
  - 同一缓存索引 > 5s → 强制重新规划
  - 单节点卡住 > 5s → 清除所有缓存

### 1.6 参数配置

```json
"map": {
  "node_scores": {
    "campfire_base": 10.0,        // 篝火基础分
    "shop_base": 3.43,            // 商店基础分
    "elite_base": -178.7,         // 精英基础分（负=回避）
    "unknown_base": 4.0,          // 未知节点基础分
    "monster_base": 5.0,          // 怪物节点基础分
    "boss_base": 0.0              // Boss节点（强制终点）
  }
}
```

### 1.7 已知Bug

| # | 严重度 | 描述 | 位置 |
|---|--------|------|------|
| **B1** | **严重** | **回退邻接创建虚假边**：当反射找不到显式边时，使用 `|col_diff| ≤ 3` 创建边。这会在实际不连通的节点间建立连接，导致路径规划选择了不可达的路线。日志中看到 `FALLBACK column-proximity` 时即触发了此Bug。 | MapDecider.cs:353-359 |
| **B2** | **高** | **精英节点修改器未被使用**：`params.json` 定义了 `elite_strength_modifier`, `elite_power_modifier`, `elite_frontload_modifier`, `elite_high_hp_modifier` 四个参数，但 `PlanBestPath()` 中的路径排序仅按节点类型计数，未调用任何评分函数使用这些修改器。 | MapDecider.cs:302-314 |
| **B3** | **中** | **topRow 终点逻辑**：`topRow = byRow.Keys.Max()` 假设最高 row 就是 Boss 层。但如果地图有分支到达不同的最高 row，某些有效路径会被过早截断。 | MapDecider.cs:262 |
| **B4** | **低** | **DFS 路径爆炸**：对于大地图 (Act 3 可能有 7+ 层，每层 3-5 节点)，枚举所有路径可能产生数百条路径。虽目前未见性能问题，但没有剪枝。 | MapDecider.cs:488-512 |
| **B5** | **低** | **点击后无确认**：`OnMapPointSelectedLocally()` 后不检查点击是否被游戏接受。如果节点被游戏拒绝（如未真正解锁），bot 会卡住直到超时。 | MapDecider.cs:86,154 |
| **B6** | **低** | **重复卡住检测逻辑**：`Decide()` (行 131-145) 和 `TryContinueCachedPath()` (行 205-213) 各有一套卡住检测，可能冲突。 | MapDecider.cs:131,205 |

---

## 2. 事件选择 (EventDecider)

**文件**: `src/Solver/EventDecider.cs` | **参数**: `params.json → event`

### 2.1 执行流程

```
检测 EventRoom
    │
    ├─ Proceed 按钮可用？ → 点击 Proceed（事件已完成）
    │
    ├─ NEventOptionButton 列表
    │   ├─ 过滤已锁定的选项
    │   ├─ 对每个选项调用 ScoreEventOption() 评分
    │   ├─ Tiebreaker 选出最优
    │   ├─ 设置 PendingCardSelectContext（如需选牌）
    │   └─ ForceClick 最优选项
    │
    └─ 无选项？
        └─ 尝试点击 Ancient 事件的 DialogueHitbox
```

### 2.2 评分系统

**ScoreEventOption()** (行 136-270):

评分流程 (基线=50):

1. **文本提取** (行 142-150):
   - 优先: Godot Label 文本（递归收集所有子 Label）
   - 回退: `option.Option.Title + Description`
   - 最终回退: LocString key 提取

2. **重复检测** (行 153-168):
   - 同一事件+同一选项被选 ≥3 次 → **硬封锁** (`repeat_hard_block: -500`)
   - 同选项被选 ≥2 次 → 惩罚 (`repeat_penalty_2: -150`)

3. **已知可重复事件** (行 170-175):
   - Tablet/Truth 事件 → 额外惩罚 (`tablet_penalty: -200`)

4. **HP代价检测** (行 178-194):
   - 关键词: `lose hp`, `sacrifice hp`, `pay blood`, `take damage`, `offer hp`, `spend hp`
   - HP < 50% → **硬封锁** (`hp_cost_hard_block_score: -200`)
   - HP < 60% → 警告 (`hp_cost_warning_score: -100`)
   - HP ≥ 60% → 常规代价 (`hp_cost_normal_score: -45`)

5. **诅咒检测** (行 197-201):
   - 关键词: `curse`, `pain`, `regret`, `shame`, `doubt`, `normality`, `decay`, **`wound`**, **`burn`**
   - 有 exhaust 协同 → 轻罚 (`curse_with_synergy_penalty: -40`)
   - 无 exhaust 协同 → 重罚 (`curse_no_synergy_penalty: -120`)
   - Act 1 额外 -20

6. **正向关键词** (行 203-218):
   - `heal/restore` → 低HP时+288，高HP时+100
   - `relic/artifact` → +100
   - `card` (不含curse) → +60
   - `gold/money` → +40
   - `upgrade/smith` → +70
   - `transform` → +50 + (基础牌数×6)
   - `remove/purge` → +60 + (基础牌数×8)
   - `strength/power` → +50
   - `max hp/max health` → +40
   - `proceed/leave/continue` → -30
   - `sacrifice/blood` → -40

7. **已知事件策略** (行 221, 详情见 `GetKnownEventBonus()`):

| 事件 | 策略 |
|------|------|
| **Scrap Ooze** | Reach: +40, Leave: +10 |
| **The Library** | Read(HP>50%): +30, Sleep(HP低): +30 |
| **Cursed Tome** | Take(HP>40): +35, Leave(HP≤40): +20 |
| **Moai Head** | Enter(HPR<85%): +50, Leave(HPR>85%): +20 |
| **Vampires** | Accept(有续航遗物): +10, Refuse: +30 |
| **Ghost Council** | Accept(HPR>50%): +40, Refuse(HPR≤50%): +25 |
| **Bonfire Spirits** | Offer(牌>8): +35, Leave(牌≤8): +10 |
| **Mysterious Sphere** | Open: +40, Leave: -30 |
| **The Joust** | Bet Owner: +30, Bet Murderer: -15 |
| **Big Fish** | Donate(金<80): +30, Banana(HP低): +25 |
| **SSSSerpent** | Agree(有exhaust): +25, Disagree: +20 |
| **Winding Halls** | Madness(高费牌≥3): +30, Writhe: -40 |
| **The Cleric** | Remove(基础牌>2): +30, Heal(HP低): +30 |
| **Living Wall** | Remove(基础牌≥3): +30, Transform: +25, Upgrade: +15 |

8. **牌组感知修改器** (行 224-233):
   - 大牌组 + remove → +20
   - 大牌组 + transform → +15
   - 基础牌 ≥5 + transform → +20

9. **位置启发式** (行 240-256): 仅当文本提取失败时生效
   - 第一个选项 → +5 (+3 if HP不低)
   - 最后一个选项 → HP低时+10，否则-5

### 2.3 文本提取机制

**ReadButtonGodotText()** (行 447-475):
- 递归遍历 NEventOptionButton 的所有子节点
- 收集所有可见 Label 的 Text 属性
- 对 LocalizedText 不可用的情况的回退

**ResolveLocString()** (行 526-594):
- 序列化探测链: `.Text` → `.Resolved` → `.LocalizedText` → `.Entry.Key` → `.Entry.Value` → `.Entry.Entry`

### 2.4 卡牌选择上下文

选择事件选项前设置 `PendingCardSelectContext`:
- 选项含 `remove/purge/toke/cleanse` → `"REMOVE"`
- 选项含 `transform/change` → `"TRANSFORM"`
- 选项含 `upgrade/smith/forge/improve` → `"UPGRADE"`

此上下文由 `AutoSlayCardSelector` (实现 `ICardSelector`) 在选牌界面调用时读取。

### 2.5 参数配置

```json
"event": {
  "hp_cost_hard_block_threshold": 0.50,
  "hp_cost_hard_block_score": -200,
  "hp_cost_warning_threshold": 0.60,
  "hp_cost_warning_score": -100,
  "hp_cost_normal_score": -45,
  "curse_no_synergy_penalty": -120,
  "curse_with_synergy_penalty": -40,
  "repeat_hard_block": -500,
  "repeat_penalty_2": -150,
  "tablet_penalty": -200,
  "keywords": {
    "heal_low_hp": 288.5,
    "heal_high_hp": 100,
    "relic": 100,
    "card": 60,
    "gold": 40,
    "upgrade": 70,
    "transform": 50,
    "remove": 60,
    "strength": 50,
    "max_hp": 40,
    "proceed": -30,
    "sacrifice": -40
  }
}
```

### 2.6 已知Bug

| # | 严重度 | 描述 | 位置 |
|---|--------|------|------|
| **B7** | **高** | **全部选项被封锁无回退**：如果事件的所有选项都被选了 ≥3 次，所有选项得分都是 -500。代码仍会选一个（得分最高的），但没有回退逻辑来处理"所有选项都不可接受"的情况。 | EventDecider.cs:162-166 |
| **B8** | **中** | **"wound"和"burn"误判为诅咒**：`givesCurse` 检测 (行 197-199) 包含 `"wound"` 和 `"burn"`。这些是**状态牌**(Status)，不是诅咒(Curse)。状态牌在战斗结束后自动消失，不污染牌组。这会导致对给 Wound/Burn 的事件选项过度惩罚。 | EventDecider.cs:197-199 |
| **B9** | **中** | **所有文本检测仅限英文**：HP代价检测、诅咒检测、关键词检测、已知事件策略全部使用英文子串匹配。如果 STS2 以中文运行，大部分检测将失效，只保留位置启发式。 | EventDecider.cs:178-443 |
| **B10** | **低** | **Ancient事件使用EmitSignal**：行 58 使用 `EmitSignal(NClickableControl.SignalName.Released, ...)` 而非 `ForceClick()`。与其他地方的交互方式不一致。 | EventDecider.cs:58 |
| **B11** | **低** | **递归Label收集的性能**：`CollectLabels()` 递归遍历按钮的所有子孙节点。对于UI树较深的事件，可能造成微小延迟。 | EventDecider.cs:454-465 |

---

## 3. 奖励选择 (Rewards + CardRewardDecider)

### 3.1 RewardsHandler (非卡牌奖励)

**文件**: `src/Handlers/RewardsHandler.cs`

#### 执行流程

```
检测 NRewardsScreen
    │
    ├─ 同屏 >300 ticks？ → 强制移除（安全阀）
    │
    ├─ 收集所有 NRewardButton
    │   │
    │   ├─ 检查药水数量
    │   │   └─ 已持有 ≥3 药水 → 跳过 PotionReward
    │   │
    │   └─ 找到首个 未尝试 + isEnabled + 非Potion(满时) 的按钮
    │       └─ ForceClick → return 0.3s cooldown
    │
    ├─ Proceed 按钮可用？ → 点击 Proceed → return 1.0s
    │
    └─ 超时 >30 frames？
        └─ 强制 NOverlayStack.Remove(screen)
```

#### 药水跳过逻辑 (行 59-94)

```
RunManager.DebugOnlyGetState() → LocalContext.GetMe() → player.Potions
遍历药水，计数 HasBeenRemovedFromState==false && IsQueued==false 的药水
if count >= 3 && reward is PotionReward:
    跳过 + 标记为已尝试（防止循环）
```

#### 安全机制

| 机制 | 阈值 | 行为 |
|------|------|------|
| 同屏检测 | 300 ticks (~10s) | 强制移除 + 清除所有状态 |
| 卡住超时 | 30 ticks (~1s) | 强制移除覆盖层 |
| 强制移除计数 | >10 次 | 记录 Error 日志（可能无限循环） |

### 3.2 CardRewardDecider (卡牌奖励)

**文件**: `src/Solver/CardRewardDecider.cs` (1265行) | **参数**: `params.json → card_reward`

#### 执行流程 (22阶段评分系统)

```
获取可选卡牌列表
    │
    ├─ Phase 1: 硬封锁 (HARD BAN)
    │   └─ PACTS_END, EXPECT_A_FIGHT, HOWL_FROM_BEYOND → score = -9999
    │
    ├─ Phase 2: MAX_COPIES 限制
    │   └─ 不能抽牌/回能/消耗的牌 → 已有1张则 score = -9999
    │
    ├─ Phase 3-8: 基础评分
    │   ├─ 伤害效率 (damage/energy)
    │   ├─ 格挡效率 (block/energy)
    │   ├─ 0费价值
    │   ├─ 卡牌类型 (Power/0-cost/1-cost/X-cost)
    │   ├─ Debuff (Vulnerable/Weak/Poison)
    │   └─ Buff (Energy/Strength/Dexterity)
    │
    ├─ Phase 9: Act感知调整
    │   ├─ Act1: 重视攻击 (+premium attack bonus)
    │   ├─ Act1: 惩罚慢速Power (-5), 惩罚纯Block (-5)
    │   ├─ Act2: 重视AOE (+28), Block (+8), Weak (+10)
    │   └─ Act3: 重视Power (+12), Strength (+10), 惩罚普通攻击 (-12)
    │
    ├─ Phase 10: 升级加分
    │   └─ 已升级卡牌 +20
    │
    ├─ Phase 11: 牌组大小惩罚
    │   ├─ 牌组 >20: -2/张
    │   ├─ 牌组 >25: -4/张
    │   └─ 牌组 >30: -8/张
    │
    ├─ Phase 12: 协同加分
    │   ├─ 力量协同 + MultiHit → +15
    │   ├─ Exhaust协同 → +18
    │   ├─ Block协同 → +12
    │   ├─ SelfDamage协同 → +18
    │   ├─ Poison/Discard/Orb/Focus/Star协同 → +18~22
    │   └─ Combo协同倍数 → ×18
    │
    ├─ Phase 13: 胜率统计 (StatsDatabase)
    │
    ├─ Phase 14: 冗余惩罚
    │   ├─ 已有1张 → -18
    │   └─ 已有2+张 → -28
    │
    ├─ Phase 15-22: 高级评估
    │   ├─ Deck Gap (伤害/格挡/抽牌/能量/AOE/成长缺口)
    │   ├─ Future Investment (未来投资可行性)
    │   ├─ Card Role (过渡牌 vs 终端牌)
    │   ├─ Engine Closure (运转闭合检测)
    │   ├─ Upgrade Pressure (敲位压力)
    │   ├─ Colorless Premium (无色牌加分)
    │   └─ Boss Counter (Boss对策)
    │
    └─ Skip 判定
        ├─ 绝对阈值: base(55) + per_deck*N + per_act*act
        ├─ 相对阈值: maxScore × 0.90
        ├─ 紧急降低: 低HP -12, Act1无攻击 -10, 无Block -5
        └─ 最高分 < max(绝对阈值, 相对阈值) → SKIP
```

#### 跳过阈值参数

```json
"skip_threshold": {
  "base": 55,
  "per_deck_size_above_12": 5.0,
  "per_deck_size_above_20": 8.0,
  "per_act": 10.0,
  "relative_threshold_multiplier": 0.90,
  "min_threshold": 10
}
```

#### 已知Bug

| # | 严重度 | 描述 | 位置 |
|---|--------|------|------|
| **B12** | **中** | **RewardsHandler 标记药水为"已尝试"**：满药水时跳过的 PotionReward 被标记为 `_triedRewards`。如果奖励屏幕因为某些原因刷新（如动画重播），而这个药水变成了我们想要的，它仍会被跳过。 | RewardsHandler.cs:89 |
| **B13** | **低** | **强制移除无上限**：当强制移除 >10 次时只记日志，但没有停止逻辑。如果真的陷入无限奖励循环，bot会一直尝试。 | RewardsHandler.cs:139-142 |

---

## 4. 休息点选择 (RestDecider)

**文件**: `src/Solver/RestDecider.cs` | **参数**: `params.json → rest`

### 4.1 执行流程

```
检测 NRestSiteRoom
    │
    ├─ Proceed 可用？ → 点击 Proceed（已完成选择）
    │
    ├─ 收集 NRestSiteButton（过滤 isEnabled）
    │   │
    │   ├─ 无按钮 + 未超时 → 等待（最多 90 frames ≈ 3s）
    │   ├─ 无按钮 + 超时 → 强制点击 Proceed
    │   └─ 有按钮 → 评分并选择
    │
    └─ 设置 PendingCardSelectContext → ForceClick
```

### 4.2 评分系统

**ScoreRestOption()** (行 112-230):

选项通过 `btn.Option.GetType().Name` 识别，按以下顺序检查：

#### Smith (升级)
```
if HP >= 55%:
    基础分: 250
    Act1 额外: +50
    Act3 + HPR<70%: -60
else:
    基础分: 15

牌组价值修正:
    无可升级非基础牌 且 无附魔基础牌 → -40
    仅1张可升级 且 牌组>10 且 无附魔 → -10
    有附魔基础牌 → +15
    
Defect特殊处理:
    有 Dualcast → +35 (+10 if orb≥2, +focus×5)
    有 Zap → +8
```

#### Toke (移除)
```
基础分: 80
大牌组: +100
HP ≥ 55%: +30
Strike ≥ 3: +40
```

#### Recall (钥匙)
```
基础分: 70
```

#### Lift (力量训练)
```
基础分: 90
```

#### Dig (挖掘遗物)
```
基础分: 95
```

#### Rest (休息/回血)
```
if HP < 50%:
    基础分: 350
    Act ≥ 3: +60
elif HP < 70%:
    基础分: 180
    无续航遗物: +50
else:
    基础分: 5
```

### 4.3 参数配置

```json
"rest": {
  "rest_low_hp_threshold": 0.50,
  "rest_low_hp_score": 350,
  "rest_medium_hp_max": 0.70,
  "rest_medium_hp_score": 180,
  "rest_medium_no_sustain_bonus": 50,
  "rest_high_hp_score": 5,
  "rest_act3_boss_bonus": 60,
  "smith_hp_threshold": 0.55,
  "smith_high_hp_score": 250,
  "smith_act1_bonus": 50,
  "smith_act3_low_hp_penalty": -60,
  "smith_low_hp_score": 15,
  "toke_base_score": 80,
  "toke_large_deck_bonus": 100,
  "toke_high_hp_bonus": 30,
  "toke_strike_count_bonus": 40,
  "recall_score": 70,
  "lift_score": 90,
  "dig_score": 95
}
```

### 4.4 已知Bug

| # | 严重度 | 描述 | 位置 |
|---|--------|------|------|
| **B14** | **中** | **卡住计数器未在所有路径重置**：当 `btns.Count==0` 且 `proceed?.IsEnabled!=true` 时，计数器递增。但如果按钮出现后又被隐藏（UI重建），计数器的状态不一致。`ResetStuckCounter()` 被调用但仅在部分路径。 | RestDecider.cs:25,55-69 |
| **B15** | **低** | **Defect 逻辑硬编码**：Dualcast/Zap 特殊处理硬编码在通用 RestDecider 中。如果新增角色或修改初始牌组，需要修改此文件。 | RestDecider.cs:167-185 |
| **B16** | **低** | **缺少 Silent/Watcher 角色特殊处理**：只有 Defect 有升级优先级特殊处理（Dualcast/Zap）。Silent 的 Neutralize、Watcher 的 Eruption 等核心升级没有类似处理。 | RestDecider.cs:164-185 |

---

## 5. 商店选择 (ShopDecider)

**文件**: `src/Solver/ShopDecider.cs` | **参数**: `params.json → shop`

### 5.1 执行流程

```
检测 NMerchantRoom
    │
    ├─ _shopLeaving? → 点击 Proceed 或 Back → Reset
    │
    ├─ !_shopStarted? → OpenInventory() → 标记已开始
    │
    └─ 执行一次购买（每 tick 一次）
        │
        ├─ Inventory 为空？ → LeaveShop()
        │
        ├─ 构建购买列表:
        │   ├─ 优先级 0: 移除 Strike/Defend
        │   │   └─ 有基础牌 → 500 + 基础牌数×20
        │   │   └─ 无基础牌 → 大牌组300, 普通150
        │   ├─ 优先级 1: 遗物
        │   │   └─ 评分: base(80) + 类型加成 + WR加成 + 价格调整
        │   ├─ 优先级 2: 药水 (仅当有空位)
        │   │   └─ 评分: 固定20
        │   └─ ⚠️ 卡牌永不购买
        │
        ├─ 排序: 优先级 > 评分 > 随机
        │
        ├─ 检查 Gold 缓冲
        │   ├─ goldAfter < MinGoldReserve(74) 且 score < MinScore(100) → 跳过
        │   └─ 否则 → 购买
        │
        └─ TaskHelper.RunSafely(entry.OnTryPurchaseWrapper(inv))
```

### 5.2 遗物评分

**ScoreRelicPurchase()** (行 196-225):

```
基础分: 80

类型加成:
    能量遗物 (Sozu/Coffee/Dripper/Ecto/Key/Philosopher) → +200
    力量遗物 (Vajra/Girya/Duvu/Shuriken) → +100
    防御遗物 (Anchor/Horn/Cleat/Boat/Thread/Needle) → +60

胜率统计:
    WR > 0 → +(WR - 0.20) × 500 (普通遗物)
    Boss WR > 0 → +(WR - 0.20) × 300 (Boss遗物)

价格调整:
    Cost > 200 → -30
    Cost < 100 → +20
```

### 5.3 卡牌移除逻辑

```
if 有基础牌 (Strike/Defend):
    评分 = 500 + 基础牌数 × 20
    允许移除的下限降低到牌组 > 5 (常规为 >10)
else:
    大牌组 → 300
    普通 → 150
```

### 5.4 参数配置

```json
"shop": {
  "min_gold_reserve": 74.4,
  "min_score_to_buy_low_gold": 100,
  "base_relic_value": 80,
  "energy_relic_bonus": 200,
  "strength_relic_bonus": 100,
  "defense_relic_bonus": 60,
  "relic_cost_high_penalty": -30,
  "relic_cost_low_bonus": 20,
  "relic_wr_multiplier": 500,
  "relic_boss_wr_multiplier": 300,
  "remove_card_large_deck_score": 300,
  "remove_card_normal_score": 150,
  "remove_min_deck_size": 10
}
```

### 5.5 已知Bug

| # | 严重度 | 描述 | 位置 |
|---|--------|------|------|
| **B17** | **中** | **购买后无确认**：`TaskHelper.RunSafely(entry.OnTryPurchaseWrapper(inv))` 调用后不检查购买是否成功。如果游戏拒绝购买（如金不足的竞态条件），bot 不会重试而会在下一 tick 重新评估（可能选择不同物品）。 | ShopDecider.cs:191 |
| **B18** | **低** | **无卖牌/清除功能**：部分商店允许卖牌，bot 完全忽略此功能。 | ShopDecider.cs (未实现) |
| **B19** | **低** | **卡牌移除的双重逻辑**：当有基础牌时，`canRemove` 被手动覆盖为 `TotalCardCount > 5`（行 116-118），而参数中 `remove_min_deck_size` 是 10。这个不一致是故意的，但容易混淆。 | ShopDecider.cs:111-118 |

---

## 6. 已知Bug汇总

> **最后更新**: 2026-07-08
> **状态**: 19 个 Bug 中发现并修复 17 个，2 个标记为低优先级不修复。

### ✅ 已修复 (17/19)

| ID | 系统 | 严重度 | 描述 | 修复日期 |
|----|------|--------|------|----------|
| **B1** | MapDecider | CRITICAL | 单节点路径死锁 — 唯一可选节点时卡住 | 2026-07-05 |
| **B2** | MapDecider | HIGH | 精英节点4个修改器参数已定义但从未被使用 | 2026-07-06 |
| **B3** | MapDecider | MEDIUM | topRow 逻辑 — `byRow.Keys.Max()` 不一定是 Boss 行 | 2026-07-07 |
| **B4** | MapDecider | LOW | DFS 剪枝 — 最大路径数限制丢弃最优解 | 2026-07-06 |
| **B5** | MapDecider | LOW | 点击后无确认，被拒绝时只能靠超时恢复 | 2026-07-07 |
| **B6** | MapDecider | LOW | 两套卡住检测逻辑可能互相干扰 | 2026-07-06 |
| **B7** | CardReward | HIGH | 跳过阈值太低 — 几乎不跳卡 | 2026-07-05 |
| **B8** | EventDecider | MEDIUM | "wound"/"burn" 被误判为诅咒（实为状态牌） | 2026-07-05 |
| **B9** | EventDecider | MEDIUM | 所有文本检测仅限英文，中文环境失效 | 2026-07-07 |
| **B10** | EventDecider | LOW | Ancient 事件使用 EmitSignal 而非 ForceClick | 2026-07-06 |
| **B11** | EventDecider | LOW | 递归 Label 收集可能在大 UI 树中有性能影响 | 2026-07-06 |
| **B12** | Rewards | MEDIUM | 被跳过的药水标记为已尝试，屏幕刷新后可能错过 | 2026-07-05 |
| **B13** | Rewards | LOW | 强制移除 >10 次仅记日志，无停止机制 | 2026-07-06 |
| **B14** | RestDecider | MEDIUM | 卡住计数器重置逻辑不完整 | 2026-07-08 |
| **B15** | RestDecider | LOW | Defect 逻辑硬编码，不利于扩展 | 2026-07-06 |
| **B16** | RestDecider | LOW | 缺少 Silent/Watcher 升级优先级 | 2026-07-07 |
| **B17** | ShopDecider | MEDIUM | 购买后无确认，竞态条件下可能静默失败 | 2026-07-08 |

### ⚠️ 已知不修复 (2/19)

| ID | 系统 | 严重度 | 描述 | 原因 |
|----|------|--------|------|------|
| **B18** | ShopDecider | LOW | 未实现卖牌功能 | 低优先级，卖牌收益通常很低 |
| **B19** | ShopDecider | LOW | 卡牌移除的双重 deck size 下限逻辑 | 有意的设计选择（有基础牌时放宽限制） |

---

## 附录: 公共工具

### Tiebreaker.PickBestFromSorted()
所有 Decider 使用 `Tiebreaker.PickBestFromSorted()` 来打破平局。当多个选项得分相同时，使用随机种子选择，避免确定性偏向。

### DecisionLogger
所有 Decider 在做出选择后调用 `DecisionLogger.LogDecision()` 记录：
- 游戏屏幕类型 (MAP/EVENT/REWARD/REST/SHOP)
- 决策类型
- 所有候选选项及其评分
- 最终选择及原因

### PendingCardSelectContext
`DecisionEngine.PendingCardSelectContext` 在需要选牌的操作前设置：
- `"UPGRADE"` — 升级选牌
- `"REMOVE"` — 移除选牌
- `"TRANSFORM"` — 变换选牌
- `"EXHAUST"` — 消耗选牌 (战斗中)
- `"HEADBUTT"` — 回头 (Headbutt 选坟场牌)

由 `AutoSlayCardSelector` (全局 ICardSelector 实现) 在选牌界面读取。

### AutoSlayHelpers
- `FindAll<T>(Node)` — 递归查找所有 T 类型子节点
- `FindFirst<T>(Node)` — 查找第一个 T 类型子节点

---

*文档生成时间: 2026-07-08 | TokenSpire2 Solver 模块*
