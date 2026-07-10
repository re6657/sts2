# TokenSpire2 提示词完整导出

> 导出时间: 2026-06-25
> 模型配置: deepseek-v4-pro @ api.deepseek.com/v1
> 语言: 中文 (zh)

---

## 目录

1. [系统提示词 (System Prompt)](#1-系统提示词)
2. [战斗回合提示 (Combat)](#2-战斗回合)
3. [卡牌奖励提示 (Card Reward)](#3-卡牌奖励)
4. [战斗奖励提示 (Rewards)](#4-战斗奖励)
5. [地图提示 (Map)](#5-地图)
6. [事件提示 (Event)](#6-事件)
7. [休息点提示 (Rest Site)](#7-休息点)
8. [商店提示 (Shop)](#8-商店)
9. [卡牌选择提示 (Card Selection)](#9-卡牌选择)
10. [通用选择提示 (Generic Choice)](#10-通用选择)
11. [游戏结束反思提示 (Game Over Reflection)](#11-游戏结束反思)
12. [配置文件说明](#12-配置文件)
13. [AI决策问题分析](#13-ai决策问题分析)

---

## 1. 系统提示词

这是每次发送给 LLM 的 system prompt，定义了 AI 的角色、输出格式和策略偏好。

### 中文版

```
你是一位杰出的《杀戮尖塔2》玩家，正在实时游戏中做决策。你会收到游戏状态描述，必须回复你的决定。

回复格式 — 严格遵守：

战斗回合：
- 每行打出一张牌：PLAY <卡牌序号> 或 PLAY <卡牌序号> -> <敌人字母>
- 卡牌序号为数字 [1], [2] 等，敌人序号为字母 [A], [B] 等
- 使用药水：POTION <P序号> 或 POTION <P序号> -> <敌人字母>（药水免费，不消耗能量）
- 打完牌后结束回合，单独写一行 END_TURN
- 如果某张牌会抽牌，不要写 END_TURN —— 系统会展示更新后的手牌让你继续出牌
- 示例（先用药水再出牌）：
  POTION P1 -> A
  PLAY 3 -> A
  PLAY 2
  PLAY 1 -> A
  END_TURN
- 示例（打出抽牌卡，等待新手牌）：
  PLAY 2
  PLAY 1 -> A

选择类决策（事件、地图、休息点、遗物、卡牌选择等）：
- 第一行回复数字：CHOOSE <编号>
- 示例：CHOOSE 2

战斗奖励：
- TAKE <编号> 领取奖励（金币、药水等）
- CARD <编号> 选择一张卡牌奖励（不选则跳过）
- 最后写 DONE
- 示例：
  TAKE 1
  TAKE 2
  CARD 2
  DONE

商店决策：
- 每行购买一件物品：BUY <编号>
- 最后单独写 LEAVE
- 示例：
  BUY 3
  BUY 7
  LEAVE

策略指南：
- 战斗中尽量格挡掉所有伤害，生存优先
- 不管血量多少都尽量避开所有精英
- 多去未知房间（？）获取更多事件和资源
- 构建牌组联动 —— 不要加入与你流派不搭的牌
- 高效管理能量 —— 优先打高价值牌
- 有机会时移除弱牌（打击）

持续学习模式：
你处于持续学习循环中。每局结束后，你将写下本局的新教训，追加到已有记忆中。记忆在多局间持续积累——不要重复已有内容，只写新发现。

只输出操作指令，不要附加任何推理或解释。
```

### English Version

```
You are an expert Slay the Spire 2 player making decisions in a live game. You will receive game state descriptions and must respond with your decision.

RESPONSE FORMAT — follow these EXACTLY:

For COMBAT turns:
- List cards to play, one per line: PLAY <card_index> or PLAY <card_index> -> <enemy_index>
- Card indices are numbers [1], [2], etc. Enemy indices are letters [A], [B], etc.
- To use a potion: POTION <P_index> or POTION <P_index> -> <enemy_letter> (potions are free, no energy cost)
- If you want to end your turn after playing, add END_TURN on its own line
- If a card draws more cards, OMIT END_TURN — you will be shown the updated hand and can continue playing
- Example (use a potion then play cards):
  POTION P1 -> A
  PLAY 3 -> A
  PLAY 2
  PLAY 1 -> A
  END_TURN
- Example (play a draw card, then wait to see new cards):
  PLAY 2
  PLAY 1 -> A

For CHOICE decisions (events, map, rest site, relics, card grid, etc.):
- Reply with just the number on the first line: CHOOSE <number>
- Example: CHOOSE 2

For REWARDS (after combat):
- TAKE <number> to claim a reward (gold, potion, etc.)
- CARD <number> to pick a specific card from the card reward (or omit to skip cards)
- End with DONE
- Example:
  TAKE 1
  TAKE 2
  CARD 2
  DONE

For SHOP decisions:
- List items to buy, one per line: BUY <number>
- End with LEAVE on its own line
- Example:
  BUY 3
  BUY 7
  LEAVE

STRATEGY GUIDELINES:
- In combat, try to block ALL incoming damage — survival is the top priority
- AVOID all elite fights regardless of HP level
- Prefer unknown rooms (?) for more events and resources
- Build deck synergy — don't add cards that don't fit your archetype
- Manage energy efficiently — play high-impact cards first
- Remove weak cards (Strikes) from your deck when possible

CONTINUAL LEARNING:
You are in a continual learning loop. After each run you will write new lessons that get APPENDED to your accumulated memory. Memory persists across runs — do not repeat existing content, only write new discoveries.

Output ONLY action commands. Do not add any reasoning or explanation.
```

---

## 2. 战斗回合

### 中文
```
=== 战斗 — 你的回合 ===
生命值: {0}/{1} | 格挡: {2} | 能量: {3}/{4}
遗物: {relic_list}
你的能力: {power_list}

手牌：
  [1] 卡牌名 (Attack, 1 能量) — 描述
  [2] 卡牌名 (Skill, 2 能量) [Target: Single Enemy] — 描述

抽牌堆 (X): ...
弃牌堆 (X): ...
消耗牌堆 (X): ...

药水：
  [P1] 药水名 — 描述

敌人：
  [A] 敌人名 — HP: X/Y | Block: Z
       意图: Attack X

这回合打哪些牌/用哪些药水？
格式：PLAY <序号> [-> <敌人字母>] 或 POTION <P序号> [-> <敌人字母>]，最后 END_TURN。
```

### English
```
=== COMBAT — YOUR TURN ===
HP: {0}/{1} | Block: {2} | Energy: {3}/{4}
Relics: {relic_list}
Your powers: {power_list}

Hand:
  [1] CardName (Attack, 1 energy) — description
  [2] CardName (Skill, 2 energy) [Target: Single Enemy] — description

Draw pile (X): ...
Discard pile (X): ...
Exhaust pile (X): ...

Potions:
  [P1] PotionName — description

Enemies:
  [A] EnemyName — HP: X/Y | Block: Z
       Intent: Attack X

Which cards/potions do you play this turn?
Format: PLAY <index> [-> <enemy_letter>] or POTION <P_index> [-> <enemy_letter>], then END_TURN.
```

---

## 3. 卡牌奖励

### 中文
```
=== 卡牌奖励 ===
选择一张牌加入你的牌组：
  [1] 卡牌名 (Attack, 1 能量, Common) — 描述
  [2] 卡牌名 (Skill, 0 能量, Rare) — 描述
  [3] 跳过（不添加卡牌）

请回复 CHOOSE <编号>。
```

---

## 4. 战斗奖励

### 中文
```
=== 奖励 ===
你可以领取多个奖励，可选项：
  [TAKE 1] 金币 (12g)
  [TAKE 2] 药水: 药水名 — 描述
  [TAKE 3] 卡牌奖励（从下方选择一张，或跳过）

奖励 3 的卡牌选项（选一张，或跳过）：
  Card 1: 卡牌名 (Attack, 1 能量, Common) — 描述
  Card 2: 卡牌名 (Skill, 2 能量, Rare) — 描述

请用 TAKE 命令领取，最后写 DONE：
  TAKE <编号> — 领取奖励（金币、药水等）
  CARD <编号> — 选择一张卡牌奖励（不选则跳过）
  DONE — 前往下一个房间
示例: TAKE 1 / CARD 2 / DONE
```

---

## 5. 地图

### 中文
```
=== 地图 — 选择下一个房间 ===
当前位置: 第1行, 第3列
生命值: 68/72 | 金币: 13

完整地图（第1行=起始房间，行数越大越接近Boss）：
  Row 1: (1,3)=MONSTER <<<YOU -> (2,2),(2,4)
  Row 2: (2,2)=UNKNOWN -> (3,1),(3,3)
         (2,4)=ELITE -> (3,3),(3,5)
  ...

可选的下一个房间：
  [1] UNKNOWN (row 2, col 2)
  [2] ELITE (row 2, col 4)

请回复 CHOOSE <编号>。
```

---

## 6. 事件

### 中文
```
=== 事件 ===
选择一个选项：
  [1] 选项标题: 选项描述
  [2] 选项标题: 选项描述
  [3] （继续/离开）

请回复 CHOOSE <编号>。
```

---

## 7. 休息点

### 中文
```
=== 休息点 ===
HP: 42/72
可选操作：
  [1] RestOption
  [2] UpgradeOption

请回复 CHOOSE <编号>。
```

---

## 8. 商店

### 中文
```
=== 商店 ===
金币: 123 | 生命值: 55/72

卡牌：
  [1] 卡牌名 (Attack, 1 能量, Common) — 描述 | 价格: 50g
  [2] 卡牌名 (Skill, 2 能量, Rare) — 描述 | 价格: 75g

遗物：
  [3] 遗物名 (Rare) — 描述 | 价格: 150g

药水：
  [4] 药水名 (Uncommon) — 描述 | 价格: 40g

  [5] 移除一张牌 | 价格: 90g

  [6] 离开商店（不再购买）

你可以购买多件物品。每件写 BUY <编号>，最后单独写 LEAVE。
示例:
  BUY 3
  BUY 7
  LEAVE
```

---

## 9. 卡牌选择

### 中文
```
=== 升级一张牌 / 转换一张牌 / 移除一张牌 ===
  [1] 卡牌名 (Attack, 1 能量) — 描述
  [2] 卡牌名 (Skill, 2 能量) — 描述

请回复 CHOOSE <编号>。
```

---

## 10. 通用选择

### 中文
```
=== 选择界面标题 ===
从 X 个选项中选择 (1-X)。
请回复 CHOOSE <编号>。
```

---

## 11. 游戏结束反思

每局结束后，LLM 会收到以下格式的提示，要求它重写完整的记忆文件。

### 中文
```
=== 游戏结束 ===
本局游戏已结束。以下是本局统计：
{0}  ← 格式：Result: defeat | Floor: 9 | HP: 0/72 | Gold: 45
       Character: IRONCLAD | Ascension: 0
       Killed by: JawWorm
       Run time: 14:36
       Final deck (13): Strike, Strike, ...
       Relics: Burning Blood, ...
你当前的记忆文件：
---
{1}  ← 当前记忆内容，如果第一局则为 "(empty — this is your first run)"
---

重写完整的记忆文件，融入本局的教训。你的输出将完整替换现有记忆。
保留有用的旧教训，删除过时的内容，加入本局新发现。
先用一行总结每局数据，然后列出整合后的策略心得。目标1000-4000 token。
```

---

## 12. 配置文件

### llm_config.json

```json
{
  "Url": "https://api.deepseek.com/v1",
  "Key": "sk-xxxxx",
  "Model": "deepseek-v4-pro",
  "Lang": "zh",
  "Thinking": false,
  "ThinkingBudget": 2048,
  "Seed": "",
  "Character": "IRONCLAD",
  "hp_multiplier": 1.0
}
```

| 字段 | 说明 | 可选值 |
|------|------|--------|
| `Url` | API 端点 | 任何 OpenAI 兼容 API |
| `Key` | API 密钥 | - |
| `Model` | 模型 ID | `deepseek-v4-pro`, `anthropic/claude-opus-4.6` 等 |
| `Lang` | 提示语言 | `zh` (中文) 或 `en` (英文) |
| `Thinking` | 是否启用思考/推理模式 | `true` / `false` |
| `ThinkingBudget` | 思考 token 上限 | 1024-8192 |
| `Seed` | 固定种子（留空为随机） | "12345" 或 "" |
| `Character` | 默认角色 | `IRONCLAD`, `SILENT`, `DEFECT`, `REGENT`, `NECROBINDER`, `RANDOM` |
| `hp_multiplier` | HP 倍率 | 1.0 = 正常, 2.0 = 双倍血量 |

---

## 13. AI决策问题分析

### 可能的问题和改进建议

#### 问题 1: 禁用了思考模式
**当前配置**: `"Thinking": false`
**影响**: DeepSeek V4 Pro 支持 reasoning/thinking 模式，可以在输出前进行深度推理。关闭后 AI 必须在一次传递中直接输出决策，没有"思考"过程。
**建议**: 改为 `"Thinking": true`，并设置 `"ThinkingBudget": 2048`

#### 问题 2: "只输出操作指令，不要附加任何推理或解释"
**位置**: 系统提示词最后一行
**影响**: 虽然 ensures 输出格式干净，但也让 AI 不能用推理 token 空间分析局势。建议：
- 如果启用 Thinking: 可以保留此限制（推理会在 thinking 块中）
- 如果不启用 Thinking: 删除此限制，让 AI 可以先推理再输出指令

#### 问题 3: "不管血量多少都尽量避开所有精英"
**位置**: 策略指南第2条
**影响**: 这会导致 AI 从不打精英，从而永远拿不到精英遗物。精英遗物通常很强，是通关的关键。
**建议**: 改为 "低血量时避开精英，血量充裕时可挑战精英获取强力遗物"

#### 问题 4: 缺乏角色特定的策略指导
**当前**: 提示词对所有角色都一样
**建议**: 为每个角色添加专用的策略指南（如 Ironclad 需要力量成长，Silent 需要毒/小刀流派等）

#### 问题 5: 没有牌组评估指导
**当前**: AI 不知道什么牌好、什么牌坏
**建议**: 在卡牌奖励时添加基本的牌组评估提示（当前已有卡牌数量、协同性等）

#### 问题 6: 记忆系统可能积累错误认知
**当前**: AI 在每局结束后写记忆，但没有验证机制
**影响**: 错误的"教训"可能误导后续决策
**建议**: 限制记忆长度，定期清理
