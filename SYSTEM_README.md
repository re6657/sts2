# TokenSpire2 完整系统文档

> 自动生成日期: 2026-07-07
> 配套 ALGORITHM_README.md（出牌+抓牌算法详解）

---

## 目录

1. [系统架构概览](#1-系统架构概览)
2. [事件处理 (EventDecider)](#2-事件处理-eventdecider)
3. [火堆决策 (RestDecider)](#3-火堆决策-restdecider)
4. [删牌/变化卡牌优先级 (CardGridDecider)](#4-删牌变化卡牌优先级-cardgriddecider)
5. [路线规划 (MapDecider)](#5-路线规划-mapdecider)
6. [商店决策 (ShopDecider)](#6-商店决策-shopdecider)
7. [遗物选择 (RelicDecider)](#7-遗物选择-relicdecider)
8. [战斗中选牌 (ChooseCardDecider)](#8-战斗中选牌-choosecarddecider)
9. [宝箱处理 (TreasureDecider)](#9-宝箱处理-treasuredecider)
10. [自动测试系统启动](#10-自动测试系统启动)
11. [CombatSimulator vs 游戏内算法对比](#11-combatsimulator-vs-游戏内算法对比)
12. [已知问题汇总](#12-已知问题汇总)

---

## 1. 系统架构概览

```
AutoSlayNode._Process()  [主循环，每帧执行]
  │
  ├─ 主菜单 → 自动选角色、开始标准run
  ├─ 战斗 → IroncladSolver.Solve() → 执行出牌计划 → EndTurn
  ├─ 地图 → DecisionEngine → MapDecider.Decide()
  ├─ 事件 → DecisionEngine → EventDecider.Decide()
  ├─ 火堆 → DecisionEngine → RestDecider.Decide()
  ├─ 商店 → DecisionEngine → ShopDecider.Decide()
  ├─ 宝箱 → DecisionEngine → TreasureDecider.Decide()
  ├─ 选牌 → DecisionEngine → CardRewardDecider.Decide()
  ├─ 遗物 → DecisionEngine → RelicDecider.Decide()
  ├─ 牌组网格 → DecisionEngine → CardGridDecider.Decide()
  ├─ 战斗中选牌 → DecisionEngine → ChooseCardDecider.Decide()
  └─ 卡死检测 → 战斗30s / 非战斗45s → 写诊断JSON → 杀进程
```

**核心文件位置**:
- `src/AutoSlayNode.cs` — 主循环 (3606行)
- `src/Solver/DecisionEngine.cs` — 决策路由器
- `src/Solver/RunState.cs` — 运行时状态快照
- `src/Solver/SolverParams.cs` — 从 params.json 加载可调参数
- `params.json` — 所有可调参数的配置文件
- `batch_config.json` — 批量测试配置

---

## 2. 事件处理 (EventDecider)

### 文件: `src/Solver/EventDecider.cs`

### 2.1 核心策略

事件决策依赖**文本关键词匹配** + **已知事件的硬编码策略**。因为事件文本是动态/本地化的，不能用ID匹配。

### 2.2 决策流程

```
进入事件房间
  → 检测事件ID是否变化（变化则清空重复计数器）
  → 如果有Proceed按钮（子事件完成）→ 点击继续
  → 收集所有未锁定的选项按钮
  → 对每个选项评分 ScoreEventOption()
  → 按分数排序，选最高分
  → 记录重复次数（同一选项选3次 → 硬阻止）
  → ForceClick()
```

### 2.3 评分逻辑

**基线**: 50 分

#### 评分维度（按优先级）：

**① 重复检测**:
- 同一选项选 ≥3次 → **-1000 (硬阻止)**，防止死亡循环
- 同一选项选 ≥2次 → **中等惩罚**

**② HP代价检测** (文本包含 "lose"+"hp", "sacrifice"+"hp" 等):
- HP低于硬阈值 → 硬阻止
- HP低于警告阈值 → 警告扣分
- HP充足 → 正常扣分

**③ 诅咒检测** (文本包含 "curse", "pain", "regret", "shame", "doubt" 等):
- 有消耗协同 (Feel No Pain / Dark Embrace) → 小扣分
- 无消耗协同 → **大扣分**

**④ 正面关键词加分**:

| 关键词 | 含义 | 分数 |
|--------|------|------|
| "heal" / "restore" | 回血 | 低HP+30 / 高HP+5 |
| "relic" / "artifact" | 遗物 | +40 |
| "card" (非curse) | 卡牌 | +25 |
| "gold" / "money" | 金币 | +20 |
| "upgrade" / "smith" | 升级 | +25 |
| "transform" | 变化 | +20 |
| "remove" / "purge" | 删牌 | +25 |
| "strength" / "power" | 力量 | +20 |
| "max hp" / "max health" | 最大HP | +15 |

**⑤ 牌库感知调整**:
- 大牌库(25+张) + 删除选项 → +20
- 5+基础牌 + 变化选项 → +20
- Act1 + 诅咒选项 → -20

**⑥ 已知事件硬编码策略** (部分列举):

| 事件 | 识别词 | 策略 |
|------|--------|------|
| Scrap Ooze | "ooze", "scrap" | Reach +40, Leave +10 |
| The Library | "library", "read" | Read +30 (HP>50%), Sleep +30 (低HP) |
| Cursed Tome | "tome", "book"+"curse" | Take +35 (HP>40%), Leave +20 (HP≤40) |
| Moai Head | "moai", "statue" | Enter +50 (HP<85%), Leave +20 (HP>85%) |
| Vampires | "vampire", "bite" | Accept +10 (低HP/有续航), Refuse +30 |
| Ghost Council | "ghost", "apparition" | Accept +40 (HP>50%), Refuse +25 (HP≤50) |
| Bonfire Spirits | "bonfire", "spirit" | Offer +35 (牌库>8), Leave +10 (牌库≤8) |
| Mysterious Sphere | "sphere" | Open +40, Leave -30 (总是开) |
| The Joust | "joust", "bet" | Owner +30 (胜率~70%), Murderer +5 |
| Big Fish | "fish", "donate" | Donate +30 (金币<80), Banana +25 (低HP) |
| SSSSerpent | "serpent", "snake" | Agree +25 (有消耗协同), Disagree +20 |
| Winding Halls | "wind", "hall" | Madness +30 (3+高费牌), Writhe -40 |
| The Cleric | "cleric" | Remove +30 (2+基础牌), Heal +30 (低HP) |
| Living Wall | "wall", "living" | Forget +30, Transform +25, Upgrade +15 |

**⑦ 位置启发式** (文本解析失败时兜底):
- 第0个选项 (通常是主动选择) → +5
- 最后一个选项 (通常是离开) → 低HP +10, 否则 -5

### 2.4 已知问题
- **文本解析不可靠**: 依赖 Godot Label 文本渲染，如果 LocString 解析失败，退化为位置启发式（粗糙）
- **事件ID检测依赖反射**: 如果 `EventModel.Id` 反射失败，退化为房间类名
- **已知事件表固定**: 新事件只能靠关键词匹配，可能误判

---

## 3. 火堆决策 (RestDecider)

### 文件: `src/Solver/RestDecider.cs`

### 3.1 核心策略

遵循 "ForgottenArbiter" 模式：**HP < 50% → 休息，否则 → 升级**。

### 3.2 决策流程

```
进入火堆
  → 如果Proceed按钮激活（已做出选择）→ 点击继续
  → 卡住检测：90帧无按钮 → 强制点击Proceed
  → 对所有按钮评分 ScoreRestOption()
  → ForceClick() 最高分按钮
```

### 3.3 各选项评分

**Smith (升级)** - 基线 10:
- HP ≥ 阈值 → 高HP加分 + Act1额外加分 + Act3低HP惩罚
- HP < 阈值 → 低HP扣分
- **升级价值感知**:
  - 无可升级非基础牌 → **-40** (没东西值得升)
  - 10+牌库只有1张可升级 → **-10**
  - Defect 有 Dualcast → **+35** (双倍激发优先级极高)
  - Defect 有 Focus → 每点 +5
  - 附魔基础牌 → **+15**
- 对 Rest 有 +0.5 微小tiebreaker

**Toke (删牌)**:
- 大牌库 → 大牌库加分，否则基础分
- 高HP额外加分
- 3+ Strike → 额外加分

**Recall (钥匙)**:
- 固定分

**Lift (力量训练)**:
- 固定分

**Dig (挖遗物)**:
- 固定分

**Rest (休息)**:
- HP < 低阈值 (默认30%) → 高休息分 + Act3 Boss额外
- HP < 中阈值 (默认55%) → 中等休息分 + 无续航额外
- HP充足 → 低休息分

### 3.4 关键参数 (params.json)

```
RestLowHpThreshold: 0.30
RestMediumHpMax: 0.55
SmithHpThreshold: 0.50
```

### 3.5 已知问题
- **"Rest" 关键词必须最后匹配**: 所有火堆选项类名都含 "Rest"，必须先检查具体类型
- **升级价值评估粗糙**: 只统计未升级牌数量，不评估每张牌的升级收益

---

## 4. 删牌/变化卡牌优先级 (CardGridDecider)

### 文件: `src/Solver/CardGridDecider.cs`

### 4.1 核心策略

**绝对优先级：永远先删/变化 Strike 和 Defend（无论哪个角色）**。

### 4.2 删牌评分 (ScoreCardForRemove)

```
已升级的牌 → +500  (永不删除！)
已附魔的牌 → +500  (永不删除！)
Curse/Status → -1000  (最优先删除)
Strike (基础) → -500  (最优先删除)
Defend (基础) → -400  (第二优先删除)
其他牌 → 基于质量评分 (正数=不删)
```

**分数越低越容易被选中删除。**

### 4.3 变化卡牌评分 (ScoreCardForTransform / PickForTransform)

复用 `ScoreCardForRemove` 的逻辑 — Strike/Defend 分数最低，优先被变化。

### 4.4 升级评分 (ScoreCardForUpgrade)

```
IsBasicCard → -2000  (永远不升级 Strike/Defend！)
Curse/Status → -2000
已升级 → -500
伤害卡 → 伤害增益估算 × 8
格挡卡 → 格挡增益估算 × 7
能力牌 → +40
0-1费 → +15
3+费 → -10
```

### 4.5 附魔评分 (ScoreCardForEnchant)

```
IsBasicCard → -2000  (永远不附魔基础牌！)
已附魔 → -500
```

### 4.6 IsBasicCard() — 前缀匹配

```csharp
// 已修复：使用 StartsWith 前缀匹配
isBasicById = (id == "strike" || id.StartsWith("strike_")
            || id == "defend" || id.StartsWith("defend_")
            || id == "bash" || id == "neutralize" || id == "survivor"
            || id == "zap" || id == "dualcast")
```

这确保 `STRIKE_IRONCLAD`, `STRIKE_SILENT`, `DEFEND_REGENT` 等所有角色变体都被正确识别。

### 4.7 6点需求对照

| 需求 | 状态 | 实现 |
|------|------|------|
| 删牌/变化优先选打击防御 | ✅ | Strike=-500, Defend=-400 |
| 不删除升级过的牌 | ✅ | IsUpgraded → +500 |
| 不升级打击防御 | ✅ | IsBasicCard → -2000 |

---

## 5. 路线规划 (MapDecider)

### 文件: `src/Solver/MapDecider.cs`

### 5.1 核心策略

**DFS 全路径枚举** + **启发式评分**，选择最优路径。

### 5.2 决策流程

```
进入地图
  → 检测可用节点 (enabledPoints)
  → 如果只有1个节点 → 快速路径: 直接点击
  → 如果有已缓存路径 → 沿着缓存路径走
  → 否则 → DFS 枚举所有路径 → 评分 → 选最优 → 缓存
  → 防重复点击: 已点击节点追踪 (_clickedNodes)
```

### 5.3 路径评分维度

| 节点类型 | 基础分 | 调整 |
|---------|--------|------|
| 火堆 (Campfire) | +50 | Act1略低 |
| 商店 (Shop) | +30~60 | 金币多加分 |
| 精英 (Elite) | -100 | — |
| 未知 (Unknown/Event) | +20~40 | — |
| 普通战斗 (Monster) | +10~15 | — |
| Boss | 不计分 | 终点 |

**路径组合加分**:
- 至少1个火堆 → +30
- 至少2个火堆 → +20
- 有商店+火堆 → +15

### 5.4 关键修复

**单路节点快速路径**: 只有1个可用节点时不规划，直接点击。
```
"Single-node path: clicking (7,4) Elite"
```

**已点击追踪**: `HashSet<(int row, int col)>` 防止重复点击同一节点。

**卡住超时**: 15s → **5s**，超时后 replan。

### 5.5 已知问题
- **节点类型检测依赖反射**: `Point.PointType` 属性名如果改变会失效
- **路径不感知节点内容**: 不知道 Elite 具体是哪个精英，不知道事件具体是什么

---

## 6. 商店决策 (ShopDecider)

### 文件: `src/Solver/ShopDecider.cs`

### 6.1 核心策略

**优先级: 删牌(0) > 遗物(1) > 药水(2)。绝对不买卡牌。**

### 6.2 决策流程

```
进入商店
  → 打开商店界面
  → 每Tick执行一次购买
  → 评分所有可购买项
  → 按优先级+分数排序
  → 检查金币预留 (默认50g)
  → 购买最佳项
  → 无可购买项 → 离开
```

### 6.3 删牌评分

```csharp
if (basicCount > 0) {
    removeScore = 500 + basicCount * 20;  // 最高优先级
    // 牌库>5张就允许删 (即使RemoveMinDeckSize不满足)
}
```

日志输出:
```
[ShopDecider] Removal priority boosted: 5 basic cards (score=600)
```

### 6.4 遗物评分

```
基线分 + 类型加分 (能量+150 / 力量+100 / 防御+90)
+ OP.GG胜率加权: (winRate - 0.20) * 500
+ 价格修正: >200g扣分, <100g加分
```

### 6.5 关键参数

```
MinGoldReserve: 50  (购买后至少保留的金币)
RemoveMinDeckSize: 8  (正常最小删牌牌库)
```

### 6.6 6点需求对照

| 需求 | 状态 | 实现 |
|------|------|------|
| 优先删防御与打击 | ✅ | 500 + basicCount×20 优先级爆增 |
| 再买遗物 | ✅ | 优先级1，类型+统计双重评分 |
| 不购买卡牌 | ✅ | 卡牌评分循环完全删除 |
| 最后买药水 | ✅ | 优先级2，仅在有空格时 |

---

## 7. 遗物选择 (RelicDecider)

### 文件: `src/Solver/RelicDecider.cs`

### 7.1 核心策略

名称子串匹配 + 角色特定加成 + OP.GG统计胜率。

### 7.2 评分层级

| 层级 | 遗物类型 | 加分 | 示例 |
|------|---------|------|------|
| **S** | 能量遗物 | +250 | Sozu, Coffee Dripper, Philosopher's Stone |
| **A** | 力量/敏捷 | +150 | Vajra, Shuriken, Kunai |
| **A** | 过牌/能量引擎 | +130 | Top, Sundial, Pocket Watch |
| **B** | 防御 | +90 | Anchor, Horn Cleat, Thread and Needle |
| **B** | 续航 | +100 | Blood Vial (含"blood"+"vial") |
| **C** | 特殊/小众 | +30 | Darkstone, Maw, Cauldron |

### 7.3 Boss遗物特殊处理

```
Busted Crown + 小牌库 → -80
Fusion Hammer → -20
Calling Bell → -30
Tiny House → +40
```

### 7.4 角色特定加成

| 角色 | 额外加分 |
|------|---------|
| Ironclad | 力量+30, 消耗+40, 自伤+25, 续航+20 |
| Silent | 敏捷+35, 弃牌+30, 毒+30, 手里剑+25 |
| Defect | 球/集中+40, 球+能量+45, 能力牌+20 |
| Necrobinder | 星+35, 诅咒+30, 力量+20 |
| Regent | 星+35, 力量+30, 卡牌创造+25 |

### 7.5 OP.GG 统计加权

```
遗物胜率: (wr - 0.20) × 500
Boss遗物胜率: (bossWr - 0.20) × 300
```

35%胜率 → +75, 25% → +25, 15% → -25。

### 7.6 已知问题

- **名称匹配是子串匹配**: "VajraMaster" 会误匹配 "vajra"
- **反射失败回退**: 如果无法提取遗物ID，只能用Godot节点名

---

## 8. 战斗中选牌 (ChooseCardDecider)

### 文件: `src/Solver/ChooseCardDecider.cs`

### 8.1 核心策略

**上下文感知** — 根据触发来源卡牌选择不同策略。

### 8.2 触发源映射

| 卡牌 | 上下文 | 策略 |
|------|--------|------|
| Headbutt, Warcry | PUT_ON_TOP | 选最好的牌放到牌库顶 |
| True Grit, Burning Pact | EXHAUST | 选最差的牌消耗掉 |
| Armaments | UPGRADE | 选升级收益最高的牌 |
| Exhume, Hologram | RETRIEVE | 选最有价值的牌拿回手 |
| Secret Technique | FETCH_SKILL | 选最好的技能牌 |
| Secret Weapon | FETCH_ATTACK | 选最好的攻击牌 |

### 8.3 各上下文评分

**EXHAUST (消耗)** — 选最差的牌:
```
诅咒 → 1000 (绝对优先消耗)
状态 → 900
基础 Defend → 800 (优先消耗格挡，留攻击)
基础 Strike → 700
其他 → 300 - CardRewardDecider.ScoreCard() (反序)
```

**PUT_ON_TOP / RETRIEVE (置顶/拿回)** — 选最好的牌:
```
诅咒/状态 → -500 (决不置顶)
基础分 = CardRewardDecider.ScoreCard()
0-1费 → +10
高伤牌(2+费15+伤) → +15
高防牌(2+费12+防) → +12
能力牌 → +25
已升级 → +20
能量牌 → +15/能量
```

**UPGRADE (升级)** — Armaments专用:
```
已升级 → -200
诅咒/状态 → -800
伤害增益 = Max(3, baseDamage×0.3) × 8
格挡增益 = Max(2, baseBlock×0.3) × 7
能力牌 → +40
0-1费 → +15
3+费 → -10
基础 Strike → -30
基础 Defend → -25
```

### 8.4 已知问题

- **上下文可能过时**: 如果选牌界面出现但没有前置卡牌设置上下文，回退到通用评分

---

## 9. 宝箱处理 (TreasureDecider)

### 文件: `src/Solver/TreasureDecider.cs`

简单三步，无需决策:
1. 打开宝箱
2. 拾取遗物
3. 点击继续

---

## 10. 自动测试系统启动

### 10.1 批量测试配置文件

**文件**: `batch_config.json`
```json
{
  "Seed": "B001EE169B66B929",
  "Character": "IRONCLAD",
  "HpMultiplier": 1.0,
  "RunNumber": "1"
}
```

### 10.2 启动方式

#### 方式1: 手动启动批量测试

1. 编辑 `batch_config.json` 设置 Seed / Character / HpMultiplier
2. 确保 `mods/TokenSpire2/TokenSpire2.dll` 是最新编译版本
3. 启动游戏:
   ```bash
   cmd.exe /c "start steam://run/2868840"
   ```
4. AutoSlayNode 启动时自动:
   - 删除旧存档防止 "continue?" 弹窗
   - 读取 batch_config.json 进入批量模式
   - 自动放弃旧run → 选角色 → 开始标准run
   - 战斗中自动出牌，其他界面自动决策

#### 方式2: Python 批量跑 (scripts/batch_runner.py)

```bash
cd E:/SteamLibrary/steamapps/common/Slay the Spire 2/mods/TokenSpire2
python scripts/batch_runner.py
```

Python batch_runner:
1. 写入 batch_config.json
2. 启动游戏
3. 监控 `run_complete.txt` 文件
4. run 结束后杀游戏进程
5. 写入下一个 seed 到 batch_config.json
6. 重新启动游戏
7. 循环直到所有 seed 跑完

#### 方式3: Python 遗传算法优化 (scripts/auto_optimize.py)

```bash
python scripts/auto_optimize.py --character IRONCLAD --population 20 --generations 50
```

优化器:
1. 随机初始化参数种群 (43个基因)
2. 每代对每个个体跑 N 局游戏
3. 用 HP 损失作为 fitness 函数
4. 锦标赛选择 + 均匀交叉 + 高斯变异
5. 精英保留 (top 20%)
6. 退化检测 (低于最佳70% → 回滚)
7. 写入优化后的 params.json

#### 方式4: 完整自动化流水线 (scripts/full_auto_pipeline.py)

```bash
python scripts/full_auto_pipeline.py
```

流程:
1. 能力审计 → 检测AI能否处理所有卡牌/遗物/事件
2. 灵敏度分析 → 确定哪些参数对胜率影响最大
3. 遗传算法优化 → 基于分析结果聚焦优化关键参数

### 10.3 禁用自动播放

在 DLL 同级目录创建空文件 `DISABLE_AUTO_PLAY`（无扩展名），AutoSlayNode 将只解锁全内容，不执行自动播放。

### 10.4 Run 完成信号

游戏结束时写入 `run_complete.txt`，包含:
- Seed、Character、HP损失
- 死亡原因 (Victory / Defeat / Stuck)
- 各Act Boss击杀情况

### 10.5 卡死诊断

卡死超时时自动写入 `stuck_diagnostics.json`:
```json
{
  "Timestamp": "...",
  "RunInfo": { "Seed": "...", "Character": "...", "Act": 2 },
  "StuckReason": "CombatStuck_30s" | "NonCombatStuck_45s",
  "GameState": {
    "HP": 45, "MaxHP": 80,
    "HandCards": ["Strike_IRONCLAD", "Defend_IRONCLAD", ...],
    "Enemies": [...],
    "ScreenType": "COMBAT",
    "CombatPlanState": "Executing"
  }
}
```

---

## 11. CombatSimulator vs 游戏内算法对比

### 11.1 CombatSimulator (E:/CombatSimulator/)

| 属性 | 值 |
|------|-----|
| **是什么** | 独立战斗参数优化器的**输出数据**（纯JSON+Markdown） |
| **有源代码吗** | ❌ 没有，E:/CombatSimulator/ 中没有 .py/.cs 文件 |
| **求解器类型** | 贪心 (8,280次) + DFS验证 (210次) |
| **优化目标** | 31个评分权重 (damage_base, block_base, orb_slot_value等) |
| **优化方式** | 遗传算法，按Act分阶段优化 |
| **测试对象** | 每个Act的3个Boss |
| **输出** | 各Act最佳参数，fitness，HP损失，胜率 |

### 11.2 TokenSpire2 游戏内算法

| 属性 | 值 |
|------|-----|
| **是什么** | C# Godot mod，实时操控游戏 |
| **战斗求解器** | IroncladSolver.cs — DFS + 24维启发式评估 |
| **参数优化器** | Python 遗传算法 (scripts/optimizer.py) |
| **优化目标** | 43个基因 (params.json中的高层权重) |
| **优化方式** | 锦标赛选择 + 均匀交叉 + 高斯变异 |
| **测试方式** | 启动真实游戏，监控 run_complete.txt |

### 11.3 关键区别

| 维度 | CombatSimulator | TokenSpire2 Mod |
|------|----------------|-----------------|
| **运行方式** | 离线模拟（无游戏引擎） | 实时操控真实游戏 |
| **战斗引擎** | 独立Python模拟器 | 接入真实STS2战斗 |
| **参数粒度** | 31个原子级评分权重 | 43个高层乘法器 |
| **速度** | ~5 sims/秒 | ~30-60分钟/run |
| **精确度** | 近似模拟 | 真实游戏结果 |
| **关系** | **不是同一个算法** — CombatSimulator 是预训练/参考数据 | 实际运行时的决策系统 |

### 11.4 结论

**CombatSimulator 的数据不是你平时玩游戏时用的算法。** 它是一个独立的、更快速的原型系统，用来在模拟环境中快速测试不同参数组合。它的结果可以作为参考（比如 Act 4 的 100% 胜率参数），但你的 mod 实际运行时用的是 IroncladSolver.cs + params.json 中的参数。

形象比喻：
- **CombatSimulator** = F1赛车的风洞测试数据
- **TokenSpire2 Mod** = 实际在赛道上跑的F1赛车

---

## 12. 已知问题汇总

### 🔴 严重

| # | 问题 | 文件 | 状态 |
|---|------|------|------|
| 1 | IsBasicCardByName 精确匹配bug | IroncladSolver.cs | ✅ 已修复 |
| 2 | CombatSimulator和mod使用不同算法 | 架构 | ⚠️ 设计如此 |

### 🟡 中等

| # | 问题 | 文件 | 状态 |
|---|------|------|------|
| 3 | 事件文本解析不可靠，回退到位置启发式 | EventDecider.cs | ⚠️ 固有局限 |
| 4 | 已知事件表固定，新事件无法识别 | EventDecider.cs | ⚠️ 需手动更新 |
| 5 | 遗物名称子串匹配可能误判 | RelicDecider.cs | ⚠️ 概率极低 |
| 6 | 多处硬编码Godot节点路径 | AutoSlayNode.cs | ⚠️ 游戏更新可能破坏 |
| 7 | ChooseCardDecider上下文可能过时 | ChooseCardDecider.cs | ⚠️ 有兜底 |

### 🟢 轻微

| # | 问题 | 文件 | 状态 |
|---|------|------|------|
| 8 | 升级价值评估粗糙（只计数不评估） | RestDecider.cs | 可优化 |
| 9 | 大量LLM死代码 (~500+行) | AutoSlayNode.cs | 不影响功能 |
| 10 | 抽牌模拟不随机（按顺序抽） | IroncladSolver.cs | 可优化 |

---

## 附录A: 完整调用链

```
AutoSlayNode._Process()
  → GameStateDetector 检测屏幕类型
  → StabilityDetector 确认状态稳定
  → DecisionEngine.Dispatch(screen)
      ├─ MAIN_MENU → 放弃旧run → 选角色 → 开始标准run
      ├─ MAP → MapDecider.Decide()
      ├─ COMBAT → IroncladSolver.Solve()
      ├─ EVENT → EventDecider.Decide()
      ├─ REST → RestDecider.Decide()
      ├─ SHOP → ShopDecider.Decide()
      ├─ TREASURE → TreasureDecider.Decide()
      ├─ OVERLAY_CARD_REWARD → CardRewardDecider.Decide()
      ├─ OVERLAY_DECK_GRID → CardGridDecider.Decide()
      │    ├─ SMITH/UPGRADE → ScoreCardForUpgrade()
      │    ├─ REMOVE → ScoreCardForRemove()
      │    ├─ TRANSFORM → PickForTransform()
      │    └─ ENCHANT → ScoreCardForEnchant()
      ├─ OVERLAY_CHOOSE_RELIC → RelicDecider.Decide()
      ├─ OVERLAY_CHOOSE_CARD → ChooseCardDecider.Decide()
      └─ OVERLAY_SIMPLE_SELECT → SimpleSelectDecider.Decide()
```

## 附录B: 参数优化体系

```
CombatSimulator (外部离线系统)
  ├── 优化31个原子级评分权重
  ├── 贪心求解器 + DFS验证
  └── 输出: optimization_progress.json, optimization_report.md

TokenSpire2 优化器 (scripts/)
  ├── 优化43个高层参数
  ├── 遗传算法: 20个体 × 锦标赛选择 k=3
  ├── 均匀交叉 (50% per gene) + 高斯变异 (30% genes)
  └── 输出: params.json (被C#运行时读取)
```
