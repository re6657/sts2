# TokenSpire2 核心算法详解

> 自动生成日期: 2026-07-07

---

## 目录
1. [出牌算法 (IroncladSolver)](#1-出牌算法-ironcladsolver)
2. [抓牌算法 (CardRewardDecider)](#2-抓牌算法-cardrewarddecider)
3. [已知问题 & BUG](#3-已知问题--bug)
4. [6点需求实现状态](#4-6点需求实现状态)

---

## 1. 出牌算法 (IroncladSolver)

### 文件: `src/Solver/IroncladSolver.cs`

### 1.1 核心架构

```
DFS状态空间搜索 + 启发式评估
```

- **搜索算法**: 深度优先搜索 (DFS)，枚举所有可玩牌的所有可能的打出顺序
- **状态限制**: `MaxSearchStates` 最大搜索状态数 (默认500), `MaxCardsPerTurn` 每回合最多出牌数
- **评估函数**: `EvaluateState()` 对每个搜索到的终态打分，选择最高分方案
- **每回合**: 执行整个计划（一次性打出所有牌），然后 END_TURN

### 1.2 卡牌优先级别 (CardPriority)

每张卡牌有一个 Priority 数值，**越小越优先打出**:

| Priority | 含义 | 示例 |
|----------|------|------|
| 1 | 免费过牌/能量 | Offering, Bloodletting, Adrenaline |
| 2 | 0费设置牌 | Inflame, Warcry, Flex |
| 5 | 脆弱/减益 | Bash, Uppercut, Thunderclap |
| 6 | 力量 | Spot Weakness, Limit Break |
| 8 | 输出主力 | Pommel Strike, Headbutt, Sword Boomerang |
| 10 | AOE | Immolate, Cleave, Whirlwind |
| 12 | 防御 | Shrug It Off, True Grit, Flame Barrier |
| 14 | 普通攻击 | Strike, Defend (基础牌 — 最低优先级) |
| 17 | 慢速能力牌 | Demon Form, Barricade |

**同一个 Priority 内按 (BaseDamage + BaseBlock) 降序排列**。

### 1.3 搜索过程

```
For each card in hand (ordered by priority):
  For each valid energy spend:
    For each valid target:
      1. Clone state
      2. Deduct energy/stars
      3. Apply card effects (damage, block, debuffs, etc.)
      4. Simulate card draw
      5. Apply Potion effects (free action)
      6. Evaluate state → score
      7. Recursive Search(deeper)
```

**搜索空间**: N cards × E energy options × T targets × depth = 可达数万种状态。

### 1.4 EvaluateState() 评分维度 (20+ 维度)

| # | 评分维度 | 系数 | 说明 |
|---|---------|------|------|
| 1 | 击杀奖励 | KillBase + MaxHP × 系数 | 击败敌人得分 |
| 2 | 造成伤害 | DamagePerPoint × 伤害倍率 | 精英×1.35, Boss×1.8 |
| 3 | 集火奖励 | FocusFireMultiplier | 集中伤害一个敌人加分 |
| 4 | Boss优先 | BossPriorityMultiplier | 打Boss不打杂兵加分 |
| 5 | 杂兵惩罚 | MinionDamagePenalty | Boss健康时打杂兵扣分 |
| 6 | 受伤惩罚 | HealthPenalty | 剩余伤害扣分 |
| 7 | 低血量惩罚 | LowHpRatio | HP<阈值时额外扣分 |
| 8 | 格挡价值 | BlockPerNeededPoint | 有效格挡加分 |
| 9 | 溢出格挡 | BlockPerExcessPoint | 多余格挡减半 |
| 10 | 敌人脆弱 | VulnerablePerStack | 挂脆弱加分 |
| 11 | 敌人虚弱 | WeakPerStack | 挂虚弱加分 |
| 12 | 力量成长 | StrengthPerPoint × 剩余回合 | 力量=持续收益 |
| 13 | 敏捷成长 | DexterityPerPoint × 剩余回合 | 敏捷=持续收益 |
| 14 | 能力牌 | PowerPerPlayed × 剩余回合 | 能力牌全持续 |
| 15 | 能量剩余 | EnergyPerPoint | 剩余能量微加分 |
| 16 | 出牌顺序 | Sequencing bonus | 提前打setup牌加分 |
| 17 | 硬性顺序 | BEFORE/AFTER规则 | 违反顺序扣分 |
| 18 | 能量效率 | NetEnergy | 净能量正数加分 |
| 19 | 能力估值 | FutureValue | 已激活能力持续估值 |
| 20 | 牌库质量 | DeckQuality | 消耗基础牌/牌库变薄加分 |
| 21 | 毒药价值 | PoisonPerStack × 回合 | 毒药=持续伤害 |
| 22 | 球价值 | Orb 闪电/冰/暗/等离子 | 球=被动持续收益 |
| 23 | 星星保留 | StarPerStack | 死灵绑定者专用 |
| 24 | Boss对策 | Boss专项倍率 | Queen/Crab等特殊Boss |

### 1.5 Boss 特定策略 (BossStrategy)

每个Boss有独立参数:
- **Queen**: 直冲本体，需要大量过牌
- **Kaiser Crab**: AOE优先（打双钳）
- **Ceremonial Beast**: 150HP前DPS竞速，Ringing回合用大牌
- **Vantom**: 多段攻击破Slippery Shield
- **The Kin**: AOE为主（3目标）
- 等等...

### 1.6 特殊机制支持

- **球系统 (Defect)**: 完全模拟球的channel/evoke/Focus/Loop，包括Dualcast两次evoke同一球
- **毒药 (Silent)**: 计算每回合持续伤害
- **星星 (Necrobinder)**: 追踪星星消耗
- **药水**: 免费行动，可穿插在出牌中
- **X-cost牌**: 总是花全部剩余能量
- **抽牌模拟**: 从抽牌堆抽牌，抽牌堆空时洗弃牌堆
- **Buffer/Intangible/Thorns**: 完整模拟敌人buff

---

## 2. 抓牌算法 (CardRewardDecider)

### 文件: `src/Solver/CardRewardDecider.cs`

### 2.1 核心流程

```
选牌界面打开
  → 对所有3张卡牌 ScoreCard() 评分
  → 按分数排序
  → 双重门槛验证 (相对 + 绝对)
  → 只选择同时通过双门槛的卡牌
  → 如果0张合格 → SKIP (跳过)
  → 如果≥1张合格 → PICK 最高分那张
```

### 2.2 双重门槛公式 (2026-07-07 修复)

```
// CHECK 1: 相对门槛 — 分数必须在最高分的25%范围内
relThreshold = maxScore × 0.75
effectiveRelThreshold = max(relThreshold, 10.0)

// CHECK 2: 绝对门槛 — 分数必须高于牌库上下文门槛
// 随牌库增大和Act推进而升高，防止大牌库后期"矮子里面拔将军"
absoluteThreshold = CalculateSkipThreshold(state)
  = Base(25) + deckSizePressure + actPenalty - emergencyRelief

// 取两者中较高者作为最终门槛
finalThreshold = max(effectiveRelThreshold, absoluteThreshold)

if card.score >= finalThreshold → 合格
else → 不合格
```

**示例**:
- 三张牌分数 [210, 36, 28], 10张牌库Act1 → 相对=158, 绝对=25 → 最终=158 → 只有210合格 → PICK 210
- 三张牌分数 [50, 48, 47], 10张牌库Act1 → 相对=37.5, 绝对=25 → 最终=37.5 → 全部合格 → PICK 50
- 三张牌分数 [50, 48, 47], 30张牌库Act3 → 相对=37.5, 绝对≈102 → 最终=102 → 全部不合格 → SKIP ✅
- 三张牌分数 [8, 5, 3] → 相对=10, 绝对≥10 → 最终≥10 → 全部不合格 → SKIP

### 2.3 ScoreCard() 22阶段评分系统

#### Phase 1: 原始数值效率
```
伤害/能量 × rawEfficiencyDamagePerEnergy
格挡/能量 × rawEfficiencyBlockPerEnergy
0费卡直接 × ZeroCostDamagePerPoint
X-cost卡灵活加分
```

#### Phase 2: 卡牌类型基础分
```
能力牌 + powerBonus
0费卡 + zeroCostBonus
1费卡 + oneCostBonus (流畅曲线)
```

#### Phase 3: 减益/增益价值
```
易伤 × vulnerablePerStack
虚弱 × weakPerStack
毒 × poisonPerStack
能量获取 × energyGainPerPoint
力量 × strengthAmount
敏捷 × dexterityAmount
AOE + aoeBonus
```

#### Phase 4: 过牌检测
```
DrawCardSet 中的卡 + drawBonus
(Pommel Strike, Battle Trance, Backflip, Acrobatics...)
```

#### Phase 5: HP代价
```
有自伤协同 → HPcost × withSynergyBonus
无自伤协同 → HPcost × withoutSynergyPenalty (负数)
```

#### Phase 6: Act感知调整
```
Act 1:
  + 伤害/能量额外加分
  + 顶级Act1攻击卡特殊加分 (Carnage, Immolate, Uppercut...)
  + 2费≥12伤害额外加分
  - 纯能力/纯防御惩罚 (Act1需要前场伤害！)

Act 2:
  + AOE加分 (多敌人)
  + 格挡加分
  + 虚弱加分
  - 低伤害高费攻击惩罚

Act 3:
  + 能力牌加分
  + 力量加分
  - 普通攻击惩罚 (需要终端卡)
  - 低伤害高费攻击惩罚
```

#### Phase 7: 牌库大小
```
牌库>20张 → 越来越挑剔 (门槛越来越高)
牌库>25张 → 更严格
牌库>30张 → 极难通过
牌库≤12张 → 精简奖励 (正向加分)
```

#### Phase 8: 牌库协同
```
力量协同 + 多段攻击 (Twin Strike, Heavy Blade...)
消耗协同 + 消耗牌 (Feel No Pain + True Grit...)
格挡协同 + 技能格挡牌 (Barricade + Shrug...)
自伤协同 + 自伤牌 (Rupture + Hemokinesis...)
能量遗物 + 高费牌

Silent:
  毒协同 + 毒牌
  弃牌协同 + 弃牌牌

Defect:
  球协同 + 球牌
  集中协同 + 集中牌

Necrobinder/Regent:
  星协同 + 星牌
```

#### Phase 8b: 卡牌组合 (Combo Synergy)
```
从 500+ 对组合数据库中查找
社区验证的 combo 额外乘数:
  Corruption + Dark Embrace → 1.8×
  Rupture + Bloodletting → 1.5×
  Compress + Overclock → 1.6×
  ...
```

#### Phase 9: 冗余惩罚
```
非可叠牌:
  已有1张 → 小惩罚
  已有2张 → 大惩罚

可叠牌 (Anger, Claw, Shrug...):
  无惩罚
```

#### Phase 10: 费用曲线平衡
```
高费占比太高 → 0-1费加分，2+费扣分
3费无能量遗物小牌库 → 大惩罚
0费大牌库 → 加分
```

#### Phase 11: 类型平衡
```
技能太多 → 技能惩罚
攻击太少 → 攻击加分
无格挡 → 格挡牌加分
```

#### Phase 12: 角色优先级 (CharacterConfig)
```
从角色配置表读取
Tier S (1) = +45
Tier A (2) = +40
Tier B (3) = +35
...
Tier F (10) = +0
```

#### Phase 13: 统计数据权重
```
从 OP.GG 统计数据库读取
高胜率卡 + bonus
低胜率卡 - penalty
Act1大牌库 → 降低统计权重 (不准确)
```

#### Phase 14: 升级加分
```
已升级卡片 + upgradeBonus
```

#### Phase 15: 端口化缺口诊断
```
6种缺口检测:
  1. 伤害缺口 → 奖励攻击牌
  2. 格挡缺口 → 奖励防御牌
  3. 过牌缺口 → 奖励过牌
  4. 能量缺口 → 奖励能量牌
  5. AOE缺口 (Act2+) → 奖励AOE
  6. 成长缺口 (Act3+) → 奖励成长牌
```

#### Phase 16: 站未来可行性
```
高潜力牌 (Demon Form, Wraith Form...)
  + 有协同 → 正面加分
  - 无协同 → 负面惩罚 (别拿)
```

#### Phase 17: 过渡牌 vs 终端牌
```
过渡牌 (Carnage, Dash...):
  Act1 → 加分
  Act2+ → 已有3张过渡 → 惩罚

终端牌 (Demon Form, Echo Form...):
  Act1 → 有足够过渡基础 → 可拿
  Act2+ → 大加分

混合牌 (Pommel Strike, Shrug...):
  全期通用 → 加分
```

#### Phase 18: 运转闭合检测
```
已有过牌+能量 → 已闭合 → 这两种都加分
只有过牌无能量 → 急需能量 → 能量牌大加分
只有能量无过牌 → 急需过牌 → 过牌大加分
两者都无 → 两种都加分
```

#### Phase 19: 敲位压力
```
需要升级的牌多，火堆少 → 再拿需要敲的牌扣分
升级位置充裕 → 可以拿需要敲的牌
```

#### Phase 20: 基础牌删除进度
```
基础牌占比 > 40% → 惩罚 (牌库臃肿，谨慎拿牌)
基础牌占比 < 15% → 奖励 (牌组精简，可大胆拿牌)
```

#### Phase 21: 无色牌质量
```
无色优质牌 (Flying Sword, Grand Prize...) → 固定加分
```

#### Phase 22: Boss对策
```
根据当前Act Boss调整:
  Vantom → 多段攻击/虚弱加分
  The Kin → AOE加分
  Kaiser Crab → AOE加分
  Knowledge Demon → 大牌加分
  等等...
```

### 2.4 硬性跳过规则

```
1. 诅咒/状态牌 → 分数=-1000 (永不会选)
2. MAX_COPIES 达到上限 → 分数=-500 (永不会选)
   - 每种牌有最大推荐数量
   - 例如 Thunderclap max=1, Pommel Strike max=2
```

---

## 3. 已知问题 & BUG

### 🔴 BUG #1: ~~IroncladSolver.IsBasicCardByName 精确匹配bug~~ → ✅ 已修复

**已修复** (2026-07-07): 改用 `StartsWith("STRIKE", StringComparison.OrdinalIgnoreCase)` 前缀匹配，覆盖所有角色变体。

### 🟡 问题 #2: 出牌算法每回合一次性执行

当前架构: 每回合一次性规划所有出牌，一气呵成打出。
- 优点: 速度快，每回合只需一次DFS
- 缺点: 无法根据实际情况动态调整（比如抽到的新牌无法在当前回合使用）

### 🟡 问题 #3: 抽牌模拟不随机

`search()` 中的抽牌模拟总是从抽牌堆顶部按顺序抽，没有随机化。真实游戏中抽牌是随机的。

---

## 4. 6点需求实现状态

### 需求1: 删牌/变化优先选打击和防御
- ✅ `CardGridDecider.ScoreCardForRemove()`: Strike → -500, Defend → -400 (最低分)
- ✅ `CardGridDecider.PickForTransform()`: 复用 ScoreCardForRemove 逻辑
- ✅ `CardGridDecider.IsBasicCard()`: 前缀匹配修复 (含角色后缀)
- ✅ `RunState.IsBasicStrikeName/IsBasicDefendName`: 已使用 StartsWith

### 需求2: 不删除升级过的牌
- ✅ `CardGridDecider.ScoreCardForRemove()`: `if (card.IsUpgraded) return 500` (最高分=永不选)
- ✅ 同时保护已附魔的牌: `if (GetEnchantedBonus(card) > 0) return 500`

### 需求3: 不在火堆升级打击与防御
- ✅ `CardGridDecider.ScoreCardForUpgrade()`: `if (IsBasicCard(card)) return -2000`
- ✅ `IsBasicCard()`: 前缀匹配已修复，覆盖所有角色变体
- ✅ `RestDecider`: 无可升级非基础牌时 Smith -40 惩罚 (第二层保护)
- ✅ **已验证: 升级选牌链路完整** — NDeckUpgradeSelectScreen → OVERLAY_DECK_GRID → CardGridDecider.PickForUpgrade() → ScoreCardForUpgrade() → IsBasicCard() 阻止

### 需求4: 优化寻路算法
- ✅ `MapDecider`: 单路节点快速路径 ("Single-node path")
- ✅ 已点击节点追踪 (防重复点击死循环)
- ✅ 卡住超时从15秒减少到5秒
- ✅ 路径规划: DFS枚举所有路径 + 启发式评分

### 需求5: 抓牌只抓前25%
- ✅ `CardRewardDecider`: 双重门槛 = max(相对阈值, 绝对门槛)
- ✅ 相对阈值 = maxScore × 0.75 (前25%分数范围)
- ✅ 绝对门槛 = CalculateSkipThreshold() (牌库大小+Act修正)
- ✅ 底线 = 10.0 (硬下限)
- ✅ 不合格全跳
- ✅ 日志显示 "RelThreshold + AbsThreshold + FinalThreshold" 三重信息

### 需求6: 商店优先删基础牌 → 买遗物 → 不买卡
- ✅ `ShopDecider`: 移除基础牌优先级爆增 (score=500+basicCount×20)
- ✅ 卡牌购买完全禁用 (cards列表被删除)
- ✅ 优先级: 移除(0) > 遗物(1) > 药水(2)
- ✅ 日志显示 "Removal priority boosted: N basic cards"

---

## 附录: 调用链

```
AutoSlayNode.Tick()
  → GameStateDetector 检测屏幕类型
  → StabilityDetector 确认状态稳定
  → DecisionEngine.Dispatch(screen)
      ├─ MAP → MapDecider.Decide()
      ├─ COMBAT → IroncladSolver.Solve()
      ├─ OVERLAY_CARD_REWARD → CardRewardDecider.Decide()
      ├─ OVERLAY_DECK_GRID → CardGridDecider.Decide()
      ├─ SHOP → ShopDecider.Decide()
      ├─ REST → RestDecider.Decide()
      ├─ EVENT → EventDecider.Decide()
      ├─ OVERLAY_CHOOSE_RELIC → RelicDecider.Decide()
      └─ OVERLAY_CHOOSE_CARD → ChooseCardDecider.Decide()
```
