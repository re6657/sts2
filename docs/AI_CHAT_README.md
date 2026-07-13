# AI 对话系统 —— 数据注入说明

当 Bot 在战斗中发送 AI 对话时，每次 API 请求包含 **三个部分**：

```
┌─────────────────────────────────────────────┐
│           DeepSeek API 请求结构              │
├─────────────────────────────────────────────┤
│  system:  角色人设 (Part B)                 │
│           + 格式要求 (Part A)                │
│  user:    当前游戏局势数据                    │
└─────────────────────────────────────────────┘
```

---

## 一、共享系统提示 (Part A) — 硬编码

**位置**: `src/Chat/ChatEngine.cs` → `SharedPrompt`

```
你正在和玩家一起玩《杀戮尖塔2》多人合作模式。根据当前游戏局势，用角色的语气写6~8句短对话。

格式要求（严格遵守）：
- 每句独立一行，用换行分隔
- 每句不超过15个中文字符
- 不要编号，不要加前缀（如 1. 2. - 等）
- 每句都应该是独立的一句台词
- 符合角色的说话风格和口癖
- 可以：评论战局、给建议、吐槽、鼓励、和其他Bot互动
- 不要：重复之前说过的话、说"作为AI"之类的话
```

---

## 二、角色人设 (Part B) — 从文件加载

**位置**: `characters/{角色ID}.md`

用户可以通过创建/编辑 `.md` 文件自定义角色。当前内置三个角色：

| 角色 ID | 显示名 | 口癖特征 |
|---------|--------|----------|
| `delilah` | 德丽莎·月下初拥 | 叫玩家"人类"，说话结巴带叠词，喜欢用"哦/呢/嘛/啦/吧"结尾 |
| `seele` | 希儿·Vollerei | 三重人格，每句话必有"……"，叫玩家"大哥哥" |
| `elysia` | 爱莉希雅 | 粉色妖精小姐，喜欢用"~"波浪号，"对不对？"反问，叫玩家"亲爱的农场主" |

**自定义角色**: 复制 `characters/TEMPLATE.md` → 编辑 → 重启启动器即可在下拉菜单中看到。

---

## 三、游戏局势数据 (User Message) — 每次实时提取

**位置**: `src/Chat/GameStateExtractor.cs` → `BuildContext()`

每次调用 DeepSeek API 时，实时从游戏状态中提取以下数据，组装成中文文本：

### 完整字段说明

```
【当前局势】                          ← 固定标题

角色：{职业} | HP：{当前}/{最大} | 格挡：{格挡值} | 能量：{当前}/{最大} | 星星：{星数}

手牌：{卡名1}({费用}) {卡名2}({费用}) ...

敌人：{敌人名} HP {当前}/{最大} 格挡{格挡} 意图：{意图类型} | ...

回合：第{N}回合 | 力量：{±N} | 敏捷：{±N} | 易伤：{N} | 虚弱：{N} | 脆弱：{N}

遗物：{遗物1} {遗物2} ...

⚠️ 血量危险！   ← HP < 30% 时出现
⚠️ 血量偏低     ← HP < 50% 时出现
```

### 各字段提取来源

| 字段 | 来源 | API |
|---|---|---|
| **角色** | `player.Character.Id.Entry` | 英→中映射 (IRONCLAD→铁甲战士) |
| **HP** | `player.Creature.CurrentHp` / `MaxHp` | 即时 |
| **格挡** | `player.Creature.Block` | 即时 |
| **能量** | `player.PlayerCombatState.Energy` / `player.MaxEnergy` | 即时 |
| **星星** | `player.PlayerCombatState.Stars` | Necrobinder 专用，>0 才显示 |
| **手牌** | `PileType.Hand.GetPile(player).Cards` | 每张卡：中文名 (通过 `TitleLocString.GetFormattedText()`) + 费用 (`EnergyCost.GetResolved()`) |
| **敌人** | `CombatManager.DebugOnlyGetState().Enemies` | 仅存活敌人：怪物 ID → 怪物名 / HP / 格挡 / 意图类型 (`NextMove.Intents[0].IntentType`) |
| **回合** | `player.PlayerCombatState.TurnNumber` | 整数 |
| **力量/敏捷** | `player.Creature.Powers` | 按 Power 类型名匹配 `"Strength"`/`"Dexterity"`，非零才显示 |
| **易伤/虚弱/脆弱** | `player.Creature.Powers` | 匹配 `"Vulnerable"`/`"Weak"`/`"Frail"`，>0 才显示 |
| **遗物** | `player.Relics` | 取前 8 个，用 `Id.Entry` 作为遗物名 |
| **血量警告** | `HP / MaxHP` | <30%: 危险 / <50%: 偏低 |

### 实际请求示例

**System**:
```
# 德丽莎·月下初拥（Delilah）——傲娇吸血鬼修女
【基础设定】
你是德丽莎·月下初拥，一位傲娇的吸血鬼修女...
(完整角色人设)

你正在和玩家一起玩《杀戮尖塔2》多人合作模式。根据当前游戏局势，用角色的语气写6~8句短对话。
(格式要求)
```

**User**:
```
【当前局势】
角色：铁甲战士 | HP：68/80 | 格挡：12 | 能量：3/3
手牌：痛击(2费) 打击(1费) 防御(1费) 剑柄打击(1费) 火焰吐息(1费)
敌人：大颚虫 HP 44/48 意图：Attack | 小颚虫 HP 21/25 意图：Defend
回合：第3回合 | 力量：0 | 敏捷：0
遗物：燃烧之血 意外光滑的石头
```

---

## 四、API 调用参数

| 参数 | 值 | 说明 |
|------|-----|------|
| **Model** | `deepseek-chat` | DeepSeek V4 Flash |
| **Endpoint** | `https://api.deepseek.com/v1/chat/completions` | 可在 `aichat_config.json` 修改 |
| **max_tokens** | `200` | 返回 6~8 句足够 |
| **temperature** | `0.9` | 可配置，较高以增加多样性 |
| **timeout** | 10 秒 | 超时回退到 "喵喵喵" |

---

## 五、对话频率与节奏

```
启动 → 等 _chatInterval 秒 (默认5s)
     → 调用 API 获取 6~8 句
     → 每 1.2s 发一句 (连续快速输出)
     → 全部发完 → 再等 _chatInterval 秒
     → 重复...
```

```
时间线示例:
  0.0s: 准备中...
  5.0s: API 返回 8 句话
  5.0s: "人类小心！"         ← 第1句
  6.2s: "这一刀好疼……"       ← 第2句
  7.4s: "快用防御啦~"         ← 第3句
  8.6s: "哼，让我来吧"         ← 第4句
  9.8s: "好机会呢~"           ← 第5句
 11.0s: "加油呀人类！"         ← 第6句
 12.2s: "别大意哦……"          ← 第7句
 13.4s: "再来！"              ← 第8句
 14.6s: 本批结束，等待 5s...
 19.6s: API 返回下批 7 句话...
```

---

## 六、配置文件

`aichat_config.json` (位于 mods/TokenSpire2/):

```json
{
  "ApiKey": "sk-...",          // DeepSeek API Key
  "Model": "deepseek-chat",    // 模型名
  "BaseUrl": "https://api.deepseek.com/v1",
  "IntervalSeconds": 5,        // 每批对话的间隔
  "MaxTokens": 50,             // (实际硬编码 200)
  "Temperature": 0.9           // 创造性 0~2
}
```

---

## 七、关键文件索引

| 文件 | 作用 |
|------|------|
| `src/Chat/ChatEngine.cs` | API 客户端 + SharedPrompt + 响应解析 |
| `src/Chat/GameStateExtractor.cs` | 实时提取游戏局势数据 |
| `src/Chat/CharacterProfileManager.cs` | 加载/缓存 `characters/*.md` 角色人设 |
| `src/Chat/AiChatConfig.cs` | 加载 `aichat_config.json` |
| `src/Chat/ChatLogger.cs` | 对话历史记录到 `ai_chat_history.log` |
| `src/Patches/FlavorTextPatch.cs` | 截获气泡文本，替换为 AI 内容 |
| `src/AutoSlayNode.cs` | 对话调度 (频率/队列/发送) |
| `characters/*.md` | 角色人设文件 |
| `tools/Launcher/MainForm.cs` | GUI 启动器的 Bot 角色分配 |
