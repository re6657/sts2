# TokenSpire2 — Slay the Spire 2 AI Auto-Battle Mod

> 全自动战斗 + 非战斗决策 AI 模组。支持 Ironclad、Silent、Defect、Necrobinder、Regent 五个角色。

---

## 目录

1. [架构概览](#架构概览)
2. [五大决策系统](#五大决策系统)
3. [Bug 修复记录](#bug-修复记录)
4. [参数系统 (params.json)](#参数系统)
5. [战斗求解器 (Combat Solver)](#战斗求解器)
6. [TokenSpire2Coop 多人集成](#TokenSpire2Coop-多人集成)
7. [自动化优化工作流](#自动化优化工作流)
8. [构建与部署](#构建与部署)
9. [已知限制](#已知限制)

---

## 架构概览

```
MainFile.cs              — Mod 入口，Harmony 补丁 + 初始化
├── AutoSlayNode.cs      — 主循环（挂载到场景树，每帧运行）
│   ├── GameStateDetector.cs   — 统一屏幕/状态检测
│   ├── StateStabilityDetector.cs — 安全决策时机判断
│   └── DecisionEngine.cs      — 决策路由器
├── src/Solver/
│   ├── IroncladSolver.cs      — 战斗求解器（MCTS 变体）
│   ├── MapDecider.cs          — 地图寻路
│   ├── EventDecider.cs        — 事件选择
│   ├── CardRewardDecider.cs   — 卡牌奖励选择（22 阶段评分）
│   ├── RestDecider.cs         — 休息点决策（升级/休息/移除）
│   ├── ShopDecider.cs         — 商店购买
│   ├── CardGridDecider.cs     — 升级/转化/移除时选牌
│   ├── RunState.cs            — 运行时状态追踪
│   ├── DecisionLogger.cs      — 决策审计日志
│   ├── SolverParams.cs        — 参数加载器
│   └── CharacterConfigs.cs    — 角色特定配置
├── src/Handlers/
│   ├── CombatHandler.cs       — 战斗处理
│   ├── MapHandler.cs          — 地图处理
│   └── ...
└── params.json            — 所有可调参数（无需重新编译）
```

### 主循环流程

```
每帧 AutoSlayNode._Process(delta)
  → 检测屏幕类型（GameStateDetector）
  → 检查稳定性（StateStabilityDetector）
  → DecisionEngine 路由到对应 Decider
  → 执行决策（点击按钮 / 打出卡牌）
  → 记录审计日志（DecisionLogger）
```

---

## 五大决策系统

### 1. MapDecider — 地图寻路

**算法**: DFS 路径枚举 + 多级评分排序

```
PlanBestPath()
  → BuildAdjacency() 反射获取节点连接
  → EnumeratePaths() DFS 枚举所有路径
  → 排序：篝火↑ > 精英↓ > 商店↑ > 怪物↓
  → 逐节点点击，带确认验证
```

**关键优化**:
- Boss 节点类型检测（非简单 `row.Max()`）
- 点击后 0.5s 验证机制，被拒绝时重新选择
- 精英评分上下文感知（力量/能力/前中期/高HP 修正）
- FALLBACK 邻接修复（反射失败时的列距离启发式）

### 2. EventDecider — 事件选择

**算法**: 关键词评分 + HP 代价检测 + 诅咒感知

- 中英文双语关键词支持
- HP 代价硬阻止（HP < 50%/60% 时拒绝）
- 诅咒惩罚（区分有无协同）
- 牌组感知修正（基本牌多时提升移除/转化优先级）

### 3. CardRewardDecider — 卡牌奖励（22 阶段评分）

```
Phase  1: 原始效率（伤害/能量、格挡/能量）
Phase  2: 零费牌奖励
Phase  3: 卡牌类型（能力/零费/X费）
Phase  4: Debuff 价值（易伤/虚弱/中毒）
Phase  5: Buff 价值（能量/力量/敏捷）
Phase  6: AOE 奖励
Phase  7: 过牌检测
Phase  8: HP 代价卡
Phase  9: Act 阶段修正
Phase 10: 升级奖励
Phase 11: 牌组大小惩罚
Phase 12: 协同奖励（力量/消耗/格挡/自伤/毒/弃牌/球/专注/星）
Phase 13: 胜率统计修正
Phase 14: 冗余惩罚
Phase 15: 曲线平衡
Phase 16: 类型平衡（攻击/技能比例）
Phase 17: 牌组缺口填补
Phase 18: 未来投资
Phase 19: 卡牌角色（过渡/致胜/混合）
Phase 20: 引擎闭环
Phase 21: 升级压力
Phase 22: Boss 对策
```

**跳过逻辑**: 绝对阈值 + 相对阈值（top 分数 × 0.90），牌组 >20 张时额外加压。

### 4. RestDecider — 休息点决策

**决策**:
- HP < 50%: 休息优先
- HP ≥ 55%: 升级优先
- 牌组大可移除基础牌
- 角色特殊升级优先级（Defect Dualcast, Silent Neutralize 等）

### 5. ShopDecider — 商店购买

**优先级**: 卡牌移除 > 遗物 > 药水（不买卡牌）
- 移除基础 Strike/Defend 时额外加分
- 购买后验证黄金是否减少（竞态保护）
- 保留最低黄金储备

---

## Bug 修复记录

共发现 19 个 Bug，全部已修复或分析。

### 已修复 (CRITICAL / HIGH)

| ID | 系统 | 描述 | 修复 |
|----|------|------|------|
| **B1** | MapDecider | 单节点路径死锁 — 唯一可选节点时卡住 | ✅ 添加单节点直接点击逻辑 |
| **B2** | MapDecider | 同列节点误判 — 同列多节点时路径损坏 | ✅ 修复列内排序 |
| **B3** | MapDecider | topRow 逻辑 — `byRow.Keys.Max()` 不一定是 Boss 行 | ✅ Boss 节点类型检测 |
| **B4** | MapDecider | DFS 剪枝 — 最大路径数限制丢弃最优解 | ✅ 增加路径池并排序保留最佳 |
| **B5** | MapDecider | 点击无确认 — 被拒绝时只能等超时 | ✅ 0.5s 后验证；被拒则重试 |
| **B7** | CardReward | 跳过阈值太低 — 几乎不跳卡 | ✅ 相对阈值 0.90 + 牌组墙 |
| **B8** | EventDecider | Wound/Burn 误判为诅咒（实为状态牌） | ✅ 单独区分状态牌和诅咒 |
| **B9** | EventDecider | 仅英文文本检测，中文环境失效 | ✅ 添加 中文关键词 |
| **B12** | Rewards | 被跳过的药水标记为已尝试 | ✅ 修复已尝试标记逻辑 |
| **B14** | RestDecider | 卡住计数器重置不完整 | ✅ 点击选项后重置计数器 |
| **B17** | ShopDecider | 购买后无确认，竞态条件下可能静默失败 | ✅ 购买前后黄金对比验证 |

### 已修复 (MEDIUM / LOW)

| ID | 系统 | 描述 | 修复 |
|----|------|------|------|
| **B6** | MapDecider | 两套卡住检测互相干扰 | ✅ 统一超时逻辑 |
| **B10** | EventDecider | Ancient 事件用 EmitSignal 非 ForceClick | ✅ 改用 ForceClick |
| **B11** | EventDecider | 递归 Label 收集性能问题 | ✅ 添加深度限制 |
| **B13** | Rewards | 强制移除 >10 次仅记日志无停止 | ✅ 添加硬性上限 |
| **B15** | RestDecider | Defect 逻辑硬编码 | ✅ 扩展为 CharacterConfigs 模式 |
| **B16** | RestDecider | 缺少 Silent 升级优先级 | ✅ 添加 6 张 Silent 核心卡升级奖励 |
| **B18** | ShopDecider | 未实现卖牌功能 | ⚠️ 低优先级，未实现 |
| **B19** | ShopDecider | 双重 deck size 下限逻辑 | ✅ 文档说明，逻辑保留 |

---

## 参数系统

所有可调参数集中在 `params.json`，修改后无需重新编译。

### 参数组

```json
{
  "combat_solver":      // 战斗求解器（MCTS 参数、伤害/格挡/状态权重）
  "combat_sequencing":  // 出牌顺序（前置/后置规则、能量效率）
  "future_value":       // 能力牌未来价值估算
  "deck_quality":       // 牌组质量评分
  "card_reward": {      // 卡牌奖励（22 阶段权重 + 跳过阈值）
    "stage_weights":    // 各阶段权重
    "skip_threshold":   // 跳过判定
    "redundancy_penalty": // 冗余惩罚
    "curve_balance":    // 费用曲线
    "type_balance":     // 攻击/技能比例
    "deck_gap":         // 牌组缺口检测
    "future_investment": // 未来投资
    "card_role":        // 卡牌角色评分
    "engine_closure":   // 引擎闭环
    "upgrade_pressure": // 升级压力
    "colorless_premium": // 无色牌奖励
    "boss_counter":     // Boss 对策
  },
  "map":                // 地图寻路（节点评分、路径奖励）
  "rest":               // 休息决策（阈值、各选项评分）
  "event":              // 事件选择（HP代价、关键词）
  "shop":               // 商店（黄金储备、遗物/卡牌价值）
  "card_tiers":         // 卡牌分层
  "potion":             // 药水使用策略
}
```

### Act 2 专项优化 (2026-07-08)

基于优化报告（Act 2 通关率 11%，HP 损失 84.1），调整：
- `act2_aoe_bonus`: 28 → **40**（应对 Byrds / Slavers 等多敌战斗）
- `act2_block_bonus`: 8 → **18**（应对 Book of Stabbing / Chosen）
- `act2_weak_bonus`: 10 → **18**（虚弱对多段攻击极高价值）
- `act2_bad_attack_penalty`: -5 → **-12**（严控牌组质量）
- `aoe_gap_urgency_act2`: 0.9 → **1.0**（最高 AOE 紧迫度）

---

## 战斗求解器

### 算法

基于 Monte Carlo Tree Search 变体的前向搜索：

```
EvaluateState(state)
  → 生成全部合法动作（出牌 + 药水）
  → 对每个动作模拟执行
  → 评分：伤害价值 + 格挡价值 + 状态效果 + 未来价值
  → 选最高分动作
  → GreedyFallback 保证始终有输出
```

### 关键机制

- **专注火力**: `focus_fire_multiplier = 16.0`，优先集火单一目标
- **Boss 优先**: `boss_priority_multiplier = 8.0`，Boss 战优先击杀 Boss 而非仆从
- **仆从惩罚**: `minion_damage_penalty = 2.0`，普通战斗不优先攻击仆从
- **精英/Boss 格挡削减**: 精英 `0.75×`，Boss `0.6×`（鼓励进攻）
- **出牌排序**: BEFORE/AFTER 硬规则 + 排序奖励/惩罚
- **HP 代价卡安全**: HP < 阈值时阻止打出 HP 代价卡
- **能力牌未来价值**: 递减折现（discount_rate=0.5），最多估算 8 回合
- **牌组质量**: 薄牌组/过牌质量奖励

---

## TokenSpire2Coop 多人集成

位于 `E:\mods\TokenSpire2Coop\`，与 STS2CouchCoop 模组合并。

### 功能

- **单人自动**: 算法自动打牌 + 非战斗决策（不变）
- **多人模式**: 算法控制 Player 1，人类控制 Player 2
- **配置面板**: 左上角 ⚙ 按钮，可展开配置
- **T 键暂停**: 随时暂停/恢复自动战斗
- **作用域配置**: 仅战斗 / 战斗+事件+奖励

### 文件结构

```
E:\mods\TokenSpire2Coop\
├── src/Coop/
│   ├── CoopManager.cs        — 状态管理 + 配置持久化
│   ├── CoopConfigUI.cs       — Godot UI 控制节点
│   └── LocalCoopMod.cs       — ModId 常量
├── MainFile.cs               — 集成 LocalCoop 初始化
├── AutoSlayNode.cs           — 添加作用域检查 + T 键
└── TokenSpire2.csproj        — 独立部署目标
```

---

## 自动化优化工作流

### 遗传算法优化器

```
optimizer.py
  → 随机生成参数组合（种群）
  → batch_runner.py 逐局测试（每局 1 个 seed）
  → 评估 HP 损失
  → 选择、交叉、变异
  → 迭代至收敛
```

### 战斗模拟器

```
CombatSimulator/
  → act_optimizer.py     — 4 Act 结构，多角色支持
  → card_pick_sim.py     — 卡牌选择模拟
  → combat_engine.py     — 回合制战斗模拟
  → enemy_db.py          — 怪物数据库
```

---

## 构建与部署

### 前提

- .NET 9.0 SDK
- Slay the Spire 2 (Steam)
- Godot 4.5.1 (mod 开发)

### 构建

```bash
cd "E:\SteamLibrary\steamapps\common\Slay the Spire 2\mods\TokenSpire2"
dotnet build -c Release
```

构建产物自动部署到 `mods\TokenSpire2\TokenSpire2.dll` + `TokenSpire2.pck`。

### Coop 版本

```bash
cd "E:\mods\TokenSpire2Coop"
dotnet build -c Release
```

构建产物部署到 `mods\TokenSpire2Coop\`。

---

## 已知限制

1. **Act 2 瓶颈**: 通关率 ~11%，是整体胜率的主要瓶颈。已通过参数优化缓解，但仍需持续调优。
2. **Defect 充能球**: 充能球系统的模拟较简化，实际效果受游戏状态影响。
3. **不买卡牌**: ShopDecider 完全禁止购买卡牌（用户需求），可能错过强力商店专属卡。
4. **未实现卖牌**: B18 标记但未实现，商店卖牌功能不支持。
5. **英文优先**: 部分卡牌 ID 检测依赖英文名，中文环境下部分功能降级。
6. **Coop 模式**: Broker 可执行文件需单独构建（.NET 控制台项目），启动脚本未完成。

---

## 版本历史

| 版本 | 日期 | 变更 |
|------|------|------|
| v0.2.0 | 2026-07-08 | Coop 集成、B14/B17 修复、Act 2 参数优化、中文支持 |
| v0.1.0 | 2026-07-01 | 初始版本，5 决策系统 + MCTS 战斗求解器 |

---

## 致谢

- **STS2CouchCoop**: 本地多人模组，提供 Broker 网络架构
- **CommunicationMod**: 提供决策模组架构参考
- **Slay the Spire 2 Modding Discord**: 技术支持和 API 文档
