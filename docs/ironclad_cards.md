# 铁甲战士 (Ironclad) 全卡牌数据库

> 包含中英文名对照、卡牌ID、费用、品质、效果、算法评分等  
> 方便比对修改 — 修改后保存，可用于调整 CharacterConfigs.cs 和 params.json

---

## 基础牌 (Starter Cards) — 初始卡组

| # | 英文名 | 中文名 | 卡牌ID | 费用 | 品质 | 类型 | 效果 | 升级效果 | 算法评分 | 优先级 |
|---|--------|--------|--------|------|------|------|------|----------|----------|--------|
| 1 | Strike | 打击 | `STRIKE_IRONCLAD` | 1 | 基础 | 攻击 | 造成 6 点伤害 | 造成 9 点伤害 | 10 (BASIC_ATTACK) | 11 (ATTACK) |
| 2 | Defend | 防御 | `DEFEND_IRONCLAD` | 1 | 基础 | 技能 | 获得 5 点格挡 | 获得 8 点格挡 | 15 (BASIC_BLOCK) | 12 (BLOCK) |
| 3 | Bash | 痛击 | `BASH` | 2 | 基础 | 攻击 | 造成 8 点伤害，施加 2 层易伤 | 造成 10 点伤害，施加 3 层易伤 | 25 (DEBUFF) | 6 (VULNERABLE) |

> **说明**: Strike/Defend 通过前缀匹配 (`STRIKE_`/`DEFEND_`) 识别为基础牌，商店和事件的移除优先级最高。Bash 虽然也是初始卡但属于精确匹配，且具有易伤效果，升级优先级较高。

---

## 普通品质 (Common)

| # | 英文名 | 中文名 | 卡牌ID | 费用 | 品质 | 类型 | 效果 | 升级效果 | 算法评分 | 优先级 | 最大数量 |
|---|--------|--------|--------|------|------|------|------|----------|----------|--------|----------|
| 4 | Anger | 愤怒 | `ANGER` | 0 | 普通 | 攻击 | 造成 6 点伤害，在弃牌堆放入一张复制 | 造成 8 点伤害 | 22 (ZERO_COST) | 11 (ATTACK) | ∞ |
| 5 | Armaments | 武装 | `ARMAMENTS` | 1 | 普通 | 技能 | 获得 5 点格挡，升级手牌中一张随机牌 | 升级手牌中所有牌 | 30 (DRAW) | -1 (UPGRADE_HAND) | ∞ |
| 6 | Body Slam | 全身撞击 | `BODY_SLAM` | 1(0) | 普通 | 攻击 | 造成等同于当前格挡值的伤害 | 费用降为 0 | 35 (PREMIUM_ATTACK) | 11 (ATTACK) | ∞ |
| 7 | Clothesline | 铁斩波 | `CLOTHESLINE` | 2 | 普通 | 攻击 | 造成 12 点伤害，施加 2 层虚弱 | 造成 14 点伤害，施加 3 层虚弱 | 25 (DEBUFF) | 6 (VULNERABLE) | ∞ |
| 8 | Flex | 活动肌肉 | `FLEX` | 0 | 普通 | 技能 | 获得 2 点力量，回合结束时失去 | 获得 4 点力量 | 5 (FLEX) | 5 (STRENGTH_DEX) | ∞ |
| 9 | Havoc | 浩劫 | `HAVOC` | 1(0) | 普通 | 技能 | 打出牌库顶的牌并消耗 | 费用降为 0 | 5 (FLEX) | 13 (FLEX) | ∞ |
| 10 | Headbutt | 头槌 | `HEADBUTT` | 1 | 普通 | 攻击 | 造成 9 点伤害，将弃牌堆一张牌置于牌库顶 | 造成 12 点伤害 | 20 (DRAW) | 9 (DRAW_FILTER) | ∞ |
| 11 | Heavy Blade | 重刃 | `HEAVY_BLADE` | 2 | 普通 | 攻击 | 造成 14 点伤害，力量加成×3 | 造成 14 点伤害，力量加成×5 | 30 (AOE) | 11 (ATTACK) | ∞ |
| 12 | Iron Wave | 铁壁波动 | `IRON_WAVE` | 1 | 普通 | 攻击 | 造成 5 点伤害，获得 5 点格挡 | 造成 7 点伤害，获得 7 点格挡 | 25 (DEBUFF) | 11 (ATTACK) | ∞ |
| 13 | Perfected Strike | 完美打击 | `PERFECTED_STRIKE` | 2 | 普通 | 攻击 | 造成 6 点伤害，牌组中每张带"打击"的牌+2伤害 | 造成 6 点伤害，每张+3伤害 | 10 (BASIC_ATTACK) | 11 (ATTACK) | ∞ |
| 14 | Pommel Strike | 剑柄打击 | `POMMEL_STRIKE` | 1 | 普通 | 攻击 | 造成 9 点伤害，抽 1 张牌 | 造成 10 点伤害，抽 2 张牌 | 20 (DRAW) | 9 (DRAW_FILTER) | ∞ |
| 15 | Shrug It Off | 耸肩无视 | `SHRUG_IT_OFF` | 1 | 普通 | 技能 | 获得 8 点格挡，抽 1 张牌 | 获得 11 点格挡，抽 1 张牌 | 20 (DRAW) | 9 (DRAW_FILTER) | ∞ |
| 16 | Sword Boomerang | 回旋镖 | `SWORD_BOOMERANG` | 1 | 普通 | 攻击 | 随机敌人造成 3 点伤害 ×3次 | ×4次 | 30 (AOE) | 11 (ATTACK) | ∞ |
| 17 | Thunderclap | 雷霆一击 | `THUNDERCLAP` | 1 | 普通 | 攻击 | 对所有敌人造成 4 点伤害，施加 1 层易伤 | 造成 7 点伤害 | 30 (AOE) | 6 (VULNERABLE) | ∞ |
| 18 | True Grit | 坚毅 | `TRUE_GRIT` | 1 | 普通 | 技能 | 获得 7 点格挡，消耗手牌中一张随机牌 | 获得 9 点格挡 | 18 (EXHAUST_SYNERGY) | 0 (DECK_THINNER) | ∞ |
| 19 | Twin Strike | 双重打击 | `TWIN_STRIKE` | 1 | 普通 | 攻击 | 造成 5 点伤害 ×2次 | 造成 7 点伤害 ×2次 | 30 (AOE) | 11 (ATTACK) | ∞ |
| 20 | Warcry | 战吼 | `WARCRY` | 0 | 普通 | 技能 | 抽 1 张牌，将手牌一张牌置于牌库顶 | 抽 2 张牌 | 20 (DRAW) | 9 (DRAW_FILTER) | ∞ |
| 21 | Wild Strike | 狂野打击 | `WILD_STRIKE` | 1 | 普通 | 攻击 | 造成 12 点伤害，将一张伤口洗入抽牌堆 | 造成 17 点伤害 | 10 (BASIC_ATTACK) | 11 (ATTACK) | ∞ |
| 22 | Bloodletting | 放血 | `BLOODLETTING` | 0 | 普通 | 技能 | 失去 3 点生命，获得 1 点能量 | 失去 3 点生命，获得 2 点能量 | 25 (BUFF_ENERGY) | -3 (FREE_ENERGY) | ∞ |
| 23 | Hemokinesis | 血之回响 | `HEMOKINESIS` | 1 | 普通 | 攻击 | 失去 2 点生命，造成 15 点伤害 | 失去 2 点生命，造成 20 点伤害 | 30 (AOE) | 11 (ATTACK) | ∞ |
| 24 | Inflame | 燃烧 | `INFLAME` | 1 | 普通 | 能力 | 获得 2 点力量 | 获得 3 点力量 | 30 (AOE) | 5 (STRENGTH_DEX) | ∞ |
| 25 | Rage | 狂怒 | `RAGE` | 0 | 普通 | 技能 | 本回合每打出一张攻击牌获得 3 点格挡 | 获得 5 点格挡 | 15 (FLEX) | 10 (BUFF) | ∞ |
| 26 | Cleave | 顺劈斩 | `CLEAVE` | 1 | 普通 | 攻击 | 对所有敌人造成 8 点伤害 | 造成 11 点伤害 | 30 (AOE) | 11 (ATTACK) | ∞ |
| 27 | Whirlwind | 旋风斩 | `WHIRLWIND` | X | 普通 | 攻击 | 对所有敌人造成 5 点伤害 ×X次 | 造成 8 点伤害 ×X次 | 15 (X_COST) | 11 (ATTACK) | ∞ |
| 28 | Ghostly Armor | 幽灵铠甲 | `GHOSTLY_ARMOR` | 1 | 普通 | 技能 | 获得 7 点格挡，此卡为虚无 | 获得 10 点格挡 | 10 (BASIC_ATTACK) | 10 (BUFF) | ∞ |
| 29 | Sentinel | 哨卫 | `SENTINEL` | 1 | 普通 | 技能 | 获得 5 点格挡，消耗时获得 2 点能量 | 获得 8 点格挡，获得 3 点能量 | 15 (FLEX) | 13 (FLEX) | ∞ |
| 30 | Spot Weakness | 观察弱点 | `SPOT_WEAKNESS` | 1 | 普通 | 技能 | 如果敌人意图攻击，获得 3 点力量 | 获得 4 点力量 | 30 (AOE) | 5 (STRENGTH_DEX) | ∞ |
| 31 | Flame Barrier | 火焰屏障 | `FLAME_BARRIER` | 2 | 普通 | 技能 | 获得 12 点格挡，本回合每次被攻击造成 4 点伤害 | 获得 16 点格挡，造成 6 点伤害 | 25 (DEBUFF) | 10 (BUFF) | ∞ |
| 32 | Entrench | 巩固 | `ENTRENCH` | 2(1) | 普通 | 技能 | 格挡值翻倍 | 费用降为 1 | 20 (DRAW) | 10 (BUFF) | ∞ |
| 33 | Exhume | 挖掘 | `EXHUME` | 1(0) | 普通 | 技能 | 选择一张消耗的牌放入手牌，消耗 | 费用降为 0 | 15 (FLEX) | 13 (FLEX) | ∞ |
| 34 | Combust | 自燃 | `COMBUST` | 1 | 普通 | 能力 | 每回合失去 1 点生命，对所有敌人造成 5 点伤害 | 造成 7 点伤害 | 15 (FLEX) | 8 (POWER) | ∞ |
| 35 | Evolve | 进化 | `EVOLVE` | 1 | 普通 | 能力 | 每当你抽到状态牌时，抽 1 张牌 | 抽 2 张牌 | 15 (FLEX) | 8 (POWER) | 1 |
| 36 | Fire Breathing | 火焰吐息 | `FIRE_BREATHING` | 1 | 普通 | 能力 | 每当你抽到或获得状态牌，对所有敌人造成 6 点伤害 | 造成 10 点伤害 | 12 (FLEX) | 8 (POWER) | 1 |
| 37 | Metallicize | 金属化 | `METALLICIZE` | 1 | 普通 | 能力 | 每回合结束时获得 3 点格挡 | 获得 4 点格挡 | 15 (FLEX) | 8 (POWER) | ∞ |
| 38 | Second Wind | 重整旗鼓 | `SECOND_WIND` | 1 | 普通 | 技能 | 消耗手牌中所有非攻击牌，每张获得 7 点格挡 | 每张 10 点格挡 | 18 (EXHAUST_SYNERGY) | 0 (DECK_THINNER) | ∞ |
| 39 | Disarm | 缴械 | `DISARM` | 1 | 普通 | 技能 | 敌人失去 2 点力量，消耗 | 失去 3 点力量 | 10 (BASIC_ATTACK) | 13 (FLEX) | ∞ |
| 40 | Dropkick | 飞踢 | `DROPKICK` | 1 | 普通 | 攻击 | 造成 5 点伤害，如果敌人有易伤抽1张牌获得1能量 | 造成 8 点伤害 | 20 (DRAW) | 11 (ATTACK) | ∞ |
| 41 | Pummel | 连续拳 | `PUMMEL` | 1 | 普通 | 攻击 | 造成 2 点伤害 ×4次，消耗 | ×5次 | 25 (DEBUFF) | 11 (ATTACK) | ∞ |

---

## 罕见品质 (Uncommon)

| # | 英文名 | 中文名 | 卡牌ID | 费用 | 品质 | 类型 | 效果 | 升级效果 | 算法评分 | 优先级 | 最大数量 |
|---|--------|--------|--------|------|------|------|------|----------|----------|--------|----------|
| 42 | Battle Trance | 战斗专注 | `BATTLE_TRANCE` | 0 | 罕见 | 技能 | 抽 3 张牌，本回合无法再抽牌 | 抽 4 张牌 | 33 (DRAW_DETECTION) | 2 (ENERGY_DRAW) | ∞ |
| 43 | Blood for Blood | 以血还血 | `BLOOD_FOR_BLOOD` | 4(3) | 罕见 | 攻击 | 每受到一次伤害费用减 1，造成 18 点伤害 | 造成 22 点伤害 | 10 (BASIC_ATTACK) | 11 (ATTACK) | ∞ |
| 44 | Burning Pact | 燃烧契约 | `BURNING_PACT` | 1 | 罕见 | 技能 | 消耗 1 张牌，抽 2 张牌 | 抽 3 张牌 | 18 (EXHAUST_SYNERGY) | 0 (DECK_THINNER) | ∞ |
| 45 | Carnage | 残杀 | `CARNAGE` | 2 | 罕见 | 攻击 | 造成 20 点伤害，虚无 | 造成 28 点伤害 | 30 (AOE) | 11 (ATTACK) | ∞ |
| 46 | Dark Embrace | 黑暗之拥 | `DARK_EMBRACE` | 2(1) | 罕见 | 能力 | 每当你消耗一张牌时抽 1 张牌 | 费用降为 1 | 27 (future) | 3 (POWER_S) | 1 |
| 47 | Demon Form | 恶魔形态 | `DEMON_FORM` | 3 | 罕见 | 能力 | 每回合开始时获得 2 点力量 | 获得 3 点力量 | 30 (future) | 3 (POWER_S) | 1 |
| 48 | Double Tap | 双重释放 | `DOUBLE_TAP` | 1 | 罕见 | 技能 | 本回合打出的下一张攻击牌打出两次 | 下两张攻击牌 | 20 (DRAW) | 7 (DOUBLER) | ∞ |
| 49 | Feel No Pain | 无惧疼痛 | `FEEL_NO_PAIN` | 1 | 罕见 | 能力 | 每当你消耗一张牌时获得 3 点格挡 | 获得 4 点格挡 | 18 (future) | 8 (POWER) | 1 |
| 50 | Intimidate | 威吓 | `INTIMIDATE` | 0 | 罕见 | 技能 | 对所有敌人施加 1 层虚弱，消耗 | 施加 2 层虚弱 | 10 (DEBUFF_WEAK) | 6 (VULNERABLE) | ∞ |
| 51 | Limit Break | 突破极限 | `LIMIT_BREAK` | 1 | 罕见 | 技能 | 力量翻倍，消耗 | 不消耗 | 35 (PREMIUM_ATTACK) | 5 (STRENGTH_DEX) | 1 |
| 52 | Offering | 祭品 | `OFFERING` | 0 | 罕见 | 技能 | 失去 6 点生命，抽 3 张牌，获得 2 点能量 | 抽 5 张牌 | 50 (ENERGY_DRAW) | -3 (FREE_ENERGY) | ∞ |
| 53 | Power Through | 硬撑 | `POWER_THROUGH` | 1 | 罕见 | 技能 | 获得 15 点格挡，将 2 张伤口洗入弃牌堆 | 获得 20 点格挡 | 20 (DRAW) | 12 (BLOCK) | ∞ |
| 54 | Rampage | 暴走 | `RAMPAGE` | 1 | 罕见 | 攻击 | 造成 8 点伤害，本场战斗每打出一次伤害+5 | 造成 8 点伤害，伤害+8 | 10 (BASIC_ATTACK) | 11 (ATTACK) | ∞ |
| 55 | Reaper | 死神 | `REAPER` | 2 | 罕见 | 攻击 | 对所有敌人造成 4 点伤害，回复等量生命，消耗 | 造成 5 点伤害 | 30 (AOE) | 11 (ATTACK) | ∞ |
| 56 | Rupture | 撕裂 | `RUPTURE` | 1 | 罕见 | 能力 | 每当你从卡牌失去生命时获得 1 点力量 | 获得 2 点力量 | 20 (future) | 3 (POWER_S) | ∞ |
| 57 | Searing Blow | 灼热打击 | `SEARING_BLOW` | 2 | 罕见 | 攻击 | 造成 12 点伤害，可多次升级 | 每次升级伤害+4 | 10 (BASIC_ATTACK) | 11 (ATTACK) | ∞ |
| 58 | Sever Soul | 断魂斩 | `SEVER_SOUL` | 2 | 罕见 | 攻击 | 造成 16 点伤害，消耗手牌中所有非攻击牌 | 造成 22 点伤害 | 18 (EXHAUST_SYNERGY) | 11 (ATTACK) | ∞ |
| 59 | Shockwave | 震荡波 | `SHOCKWAVE` | 2 | 罕见 | 技能 | 对所有敌人施加 3 层虚弱和 3 层易伤，消耗 | 施加 5 层 | 25 (DEBUFF) | 6 (VULNERABLE) | ∞ |
| 60 | Uppercut | 上勾拳 | `UPPERCUT` | 2 | 罕见 | 攻击 | 造成 13 点伤害，施加 1 层虚弱和 1 层易伤 | 施加 2 层 | 25 (DEBUFF) | 6 (VULNERABLE) | ∞ |
| 61 | Berserk | 狂暴 | `BERSERK` | 0 | 罕见 | 能力 | 获得 2 点能量，施加 2 层易伤 | 获得 1 点能量，施加 1 层易伤 | 22 (future) | 8 (POWER) | 1 |
| 62 | Corruption | 腐化 | `CORRUPTION` | 3(2) | 罕见 | 能力 | 所有技能牌费用降为 0，技能牌变为虚无 | 费用降为 2 | 40 (future) | 1 (SETUP) | 1 |
| 63 | Juggernaut | 势不可挡 | `JUGGERNAUT` | 2 | 罕见 | 能力 | 每当你获得格挡时对随机敌人造成 5 点伤害 | 造成 7 点伤害 | 15 (future) | 8 (POWER) | 1 |
| 64 | Barricade | 壁垒 | `BARRICADE` | 3(2) | 罕见 | 能力 | 格挡不再在你的回合开始时消失 | 费用降为 2 | 20 (future) | 8 (POWER) | 1 |

---

## 稀有品质 (Rare)

| # | 英文名 | 中文名 | 卡牌ID | 费用 | 品质 | 类型 | 效果 | 升级效果 | 算法评分 | 优先级 | 最大数量 |
|---|--------|--------|--------|------|------|------|------|----------|----------|--------|----------|
| 65 | Bludgeon | 狂宴 | `BLUDGEON` | 3 | 稀有 | 攻击 | 造成 32 点伤害 | 造成 42 点伤害 | 40 (PREMIUM_ATTACK) | 11 (ATTACK) | ∞ |
| 66 | Brutality | 残暴 | `BRUTALITY` | 0 | 稀有 | 能力 | 每回合失去 1 点生命，抽 1 张牌 | 抽 1 张牌 (不失去生命) | 18 (future) | 8 (POWER) | ∞ |
| 67 | Exhume | 挖掘 | `EXHUME` | 1(0) | 稀有 | 技能 | 选择一张消耗的牌放入手牌，消耗 | 费用降为 0 | 15 (FLEX) | 13 (FLEX) | ∞ |
| 68 | Feed | 盛宴 | `FEED` | 1 | 稀有 | 攻击 | 造成 10 点伤害，如果致命获得 3 点最大生命，消耗 | 造成 12 点伤害，获得 4 点最大生命 | 35 (PREMIUM_ATTACK) | 11 (ATTACK) | ∞ |
| 69 | Fiend Fire | 恶魔火 | `FIEND_FIRE` | 2 | 稀有 | 攻击 | 消耗所有手牌，每张造成 7 点伤害 | 每张造成 10 点伤害 | 30 (AOE) | 11 (ATTACK) | ∞ |
| 70 | Immolate | 燔祭 | `IMMOLATE` | 2 | 稀有 | 攻击 | 对所有敌人造成 20 点伤害，将一张灼伤洗入弃牌堆 | 造成 28 点伤害 | 35 (PREMIUM_ATTACK) | 11 (ATTACK) | ∞ |
| 71 | Impervious | 坚不可摧 | `IMPERVIOUS` | 2 | 稀有 | 技能 | 获得 30 点格挡，消耗 | 获得 40 点格挡 | 35 (PREMIUM_BLOCK) | 12 (BLOCK) | ∞ |
| 72 | Reaper | 收割 | `REAPER` | 2 | 稀有 | 攻击 | 对所有敌人造成 5 点伤害，回复等量生命 | 造成 7 点伤害 | 35 (PREMIUM_ATTACK) | 11 (ATTACK) | ∞ |

---

## STS2 新增 Ironclad 卡牌 (New STS2 Cards)

| # | 英文名 | 中文名 | 卡牌ID | 费用 | 品质 | 类型 | 效果 | 升级效果 | 算法评分 | 优先级 | 最大数量 |
|---|--------|--------|--------|------|------|------|------|----------|----------|--------|----------|
| 73 | Panic Button | 紧急按钮 | `PANIC_BUTTON` | 0 | 普通 | 技能 | 获得 6 点格挡，下回合少抽 1 张牌 | 获得 9 点格挡 | 20 (DRAW) | 12 (BLOCK) | ∞ |
| 74 | Setup Strike | 预备打击 | `SETUP_STRIKE` | 1 | 普通 | 攻击 | 造成 7 点伤害，下回合获得 1 点力量 | 造成 7 点伤害，获得 2 点力量 | 25 (DEBUFF) | 5 (STRENGTH_DEX) | ∞ |
| 75 | Molten Fist | 熔岩之拳 | `MOLTEN_FIST` | 1 | 普通 | 攻击 | 造成 9 点伤害，下张攻击牌伤害+3 | 造成 9 点伤害，伤害+5 | 25 (DEBUFF) | 7 (DOUBLER) | ∞ |
| 76 | Expect a Fight | 备战 | `EXPECT_A_FIGHT` | 2 | 普通 | 技能 | 获得 10 点格挡，获得 1 点能量 | 获得 14 点格挡 | 25 (DEBUFF) | 2 (ENERGY_DRAW) | ∞ |
| 77 | One Two Punch | 连击拳 | `ONE_TWO_PUNCH` | 1 | 普通 | 攻击 | 造成 5 点伤害，下一张攻击牌打出两次 | 造成 7 点伤害 | 25 (DEBUFF) | 7 (DOUBLER) | ∞ |
| 78 | Unrelenting | 不屈 | `UNRELENTING` | 1 | 普通 | 攻击 | 造成 8 点伤害，若此牌在弃牌堆打出+4伤害 | 造成 10 点伤害 | 25 (DEBUFF) | 7 (DOUBLER) | ∞ |
| 79 | Juggling | 杂耍 | `JUGGLING` | 1 | 普通 | 技能 | 获得 6 点格挡，下一张技能牌打出两次 | 获得 9 点格挡 | 20 (DRAW) | 7 (DOUBLER) | ∞ |
| 80 | Unmovable | 岿然不动 | `UNMOVABLE` | 2 | 罕见 | 能力 | 每回合获得 1 层人工制品 | 获得 2 层 | 20 (future) | 1 (SETUP) | ∞ |
| 81 | Forgotten Ritual | 遗忘仪式 | `FORGOTTEN_RITUAL` | 1 | 罕见 | 技能 | 消耗 2 张牌，获得 2 点能量 | 消耗 3 张牌 | 25 (BUFF_ENERGY) | 2 (ENERGY_DRAW) | ∞ |
| 82 | Pyre | 柴堆 | `PYRE` | 1 | 罕见 | 攻击 | 造成 7 点伤害，消耗一张牌，伤害+7 | 造成 10 点伤害 | 18 (EXHAUST_SYNERGY) | 2 (ENERGY_DRAW) | ∞ |
| 83 | Drum of Battle | 战鼓 | `DRUM_OF_BATTLE` | 1 | 罕见 | 能力 | 每打出一张攻击牌获得 1 点能量（每回合限 1 次） | 限 2 次 | 25 (BUFF_ENERGY) | 2 (ENERGY_DRAW) | ∞ |
| 84 | Brand | 烙印 | `BRAND` | 1 | 罕见 | 技能 | 对一名敌人施加 3 层易伤，获得 2 点力量 | 施加 5 层 | 25 (DEBUFF) | 5 (STRENGTH_DEX) | ∞ |
| 85 | Dominate | 支配 | `DOMINATE` | 2 | 罕见 | 攻击 | 造成 15 点伤害，如果目标有易伤获得 2 点力量 | 造成 20 点伤害 | 25 (DEBUFF) | 5 (STRENGTH_DEX) | ∞ |
| 86 | Fight Me | 来战 | `FIGHT_ME` | 1 | 罕见 | 技能 | 获得 8 点格挡，敌人获得 1 点力量，你获得 3 点力量 | 获得 12 点格挡 | 25 (DEBUFF) | 5 (STRENGTH_DEX) | ∞ |
| 87 | Taunt | 嘲讽 | `TAUNT` | 0 | 罕见 | 技能 | 敌人获得 1 点力量，对所有敌人施加 2 层易伤 | 施加 3 层 | 25 (DEBUFF) | 6 (VULNERABLE) | ∞ |
| 88 | Tremble | 战栗 | `TREMBLE` | 1 | 罕见 | 技能 | 对所有敌人施加 2 层易伤和 2 层虚弱 | 施加 3 层 | 25 (DEBUFF) | 6 (VULNERABLE) | ∞ |
| 89 | Crimson Mantle | 深红斗篷 | `CRIMSON_MANTLE` | 2 | 罕见 | 能力 | 每回合获得 4 点格挡，如果本回合受到伤害获得 1 点力量 | 获得 6 点格挡 | 16 (future) | 8 (POWER) | ∞ |
| 90 | Cruelty | 残忍 | `CRUELTY` | 1 | 罕见 | 能力 | 每当敌人受到易伤伤害时，获得 1 点力量 | 获得 2 点力量 | 18 (future) | 8 (POWER) | ∞ |
| 91 | Vicious | 凶残 | `VICIOUS` | 1 | 罕见 | 能力 | 每当你造成暴击（力量加成攻击），额外造成 3 点伤害 | 造成 5 点伤害 | 16 (future) | 8 (POWER) | ∞ |
| 92 | Aggression | 侵略 | `AGGRESSION` | 1 | 罕见 | 能力 | 获得 1 点力量，对随机敌人施加 2 层易伤 | 获得 2 点力量 | 16 (future) | 8 (POWER) | ∞ |
| 93 | Hellraiser | 地狱使者 | `HELLRAISER` | 2 | 罕见 | 能力 | 每当你消耗牌时获得 1 点力量 | 获得 2 点力量 | 18 (future) | 8 (POWER) | ∞ |
| 94 | Stampede | 铁蹄 | `STAMPEDE` | 2 | 罕见 | 攻击 | 造成 8 点伤害 ×2次，每有 1 点力量额外攻击一次 | ×3次基础 | 25 (DEBUFF) | 8 (POWER) | ∞ |
| 95 | Inferno | 地狱火 | `INFERNO` | 3 | 罕见 | 攻击 | 对所有敌人造成 25 点伤害，消耗所有手牌 | 造成 35 点伤害 | 30 (AOE) | 8 (POWER) | ∞ |
| 96 | Stone Armor | 石肤铠甲 | `STONE_ARMOR` | 2 | 罕见 | 技能 | 获得 4 点格挡和 4 层金属化 | 获得 6 点格挡和 6 层金属化 | 20 (future) | 8 (POWER) | ∞ |
| 97 | Fasten | 紧固 | `FASTEN` | 1 | 普通 | 技能 | 获得 8 点格挡，获得 1 层多层护甲 | 获得 12 点格挡 | 20 (DRAW) | 12 (BLOCK) | ∞ |
| 98 | Blood Wall | 血墙 | `BLOOD_WALL` | 2 | 普通 | 技能 | 失去 3 点生命，获得 18 点格挡 | 失去 3 点生命，获得 24 点格挡 | 25 (DEBUFF) | 12 (BLOCK) | ∞ |
| 99 | Poke | 戳刺 | `POKE` | 0 | 普通 | 攻击 | 造成 3 点伤害，如果敌人有易伤获得 1 点能量 | 造成 5 点伤害 | 15 (FLEX) | 11 (ATTACK) | ∞ |
| 100 | Greed | 贪婪 | `GREED` | 1 | 普通 | 攻击 | 造成 6 点伤害，如果致命获得 5 金币 | 获得 10 金币 | 5 (FLEX) | 11 (ATTACK) | ∞ |
| 101 | Ashen Strike | 灰烬打击 | `ASHEN_STRIKE` | 1 | 普通 | 攻击 | 造成 7 点伤害，消耗牌组中状态牌造成双倍伤害 | 造成 10 点伤害 | 18 (EXHAUST_SYNERGY) | 11 (ATTACK) | ∞ |
| 102 | Cinder | 余烬 | `CINDER` | 0 | 普通 | 攻击 | 造成 4 点伤害，消耗 | 造成 7 点伤害 | 15 (FLEX) | 11 (ATTACK) | ∞ |
| 103 | Breakthrough | 突破 | `BREAKTHROUGH` | 2 | 普通 | 攻击 | 造成 14 点伤害，如果本回合打出过技能牌费用降为 0 | 造成 18 点伤害 | 20 (DRAW) | 11 (ATTACK) | ∞ |
| 104 | Dismantle | 肢解 | `DISMANTLE` | 2 | 普通 | 攻击 | 造成 15 点伤害，敌人失去 2 点力量 | 造成 20 点伤害 | 25 (DEBUFF) | 11 (ATTACK) | ∞ |
| 105 | Snakebite | 蛇咬 | `SNAKEBITE` | 1 | 普通 | 攻击 | 造成 6 点伤害，施加 2 层中毒 | 造成 8 点伤害，施加 3 层 | 10 (BASIC_ATTACK) | 11 (ATTACK) | ∞ |
| 106 | Peck | 啄击 | `PECK` | 0 | 普通 | 攻击 | 造成 2 点伤害 ×2次 | ×3次 | 15 (FLEX) | 11 (ATTACK) | ∞ |
| 107 | Thrash | 乱打 | `THRASH` | 2 | 普通 | 攻击 | 随机敌人造成 8 点伤害 ×3次 | ×4次 | 30 (AOE) | 11 (ATTACK) | ∞ |
| 108 | Bully | 欺凌 | `BULLY` | 1 | 普通 | 攻击 | 造成 8 点伤害，如果敌人有虚弱伤害+5 | 造成 12 点伤害 | 20 (DRAW) | 11 (ATTACK) | ∞ |
| 109 | Pillage | 掠夺 | `PILLAGE` | 1 | 稀有 | 攻击 | 造成 7 点伤害，抽 1 张牌，如果致命再抽 2 张 | 造成 10 点伤害 | 25 (DEBUFF) | 9 (DRAW_FILTER) | ∞ |
| 110 | Spite | 怨恨 | `SPITE` | 1 | 稀有 | 技能 | 获得 5 点格挡，每有一点力量额外+2格挡，抽 1 张牌 | 获得 8 点格挡 | 20 (DRAW) | 9 (DRAW_FILTER) | ∞ |
| 111 | Stoke | 添柴 | `STOKE` | 0 | 稀有 | 技能 | 获得 3 点格挡，抽 1 张牌，消耗 | 获得 6 点格挡 | 15 (FLEX) | 9 (DRAW_FILTER) | ∞ |
| 112 | Spoils Map | 战利品地图 | `SPOILS_MAP` | 1 | 稀有 | 技能 | 获得 6 点格挡，抽 2 张牌，将一张消耗牌放入弃牌堆 | 获得 9 点格挡 | 20 (DRAW) | 9 (DRAW_FILTER) | ∞ |
| 113 | Colossus | 巨像 | `COLOSSUS` | 2 | 稀有 | 技能 | 获得 20 点格挡，下回合少抽 2 张牌 | 获得 30 点格挡 | 25 (DEBUFF) | 10 (BUFF) | ∞ |
| 114 | Prolong | 延长 | `PROLONG` | 1 | 稀有 | 技能 | 获得 7 点格挡，延长所有敌人的 debuff 持续时间 | 获得 10 点格挡 | 20 (DRAW) | 10 (BUFF) | ∞ |
| 115 | Flick Flack | 弹跳 | `FLICK_FLACK` | 0 | 稀有 | 技能 | 获得 4 点格挡，本回合每打出一张攻击牌再+2格挡 | 获得 6 点格挡 | 15 (FLEX) | 10 (BUFF) | ∞ |
| 116 | Evil Eye | 邪眼 | `EVIL_EYE` | 1 | 稀有 | 技能 | 施加 2 层虚弱和 2 层易伤，消耗 | 不消耗 | 15 (FLEX) | 13 (FLEX) | ∞ |
| 117 | Clumsy | 笨拙 | `CLUMSY` | 0 | 稀有 | 技能 | 获得 4 点格挡，将一张"笨拙"洗入弃牌堆 | 获得 7 点格挡 | 5 (FLEX) | 13 (FLEX) | ∞ |
| 118 | Doubt | 怀疑 | `DOUBT` | 0 | 稀有 | 技能 | 敌人失去 1 点力量，消耗 | 敌人失去 2 点力量 | 10 (BASIC_ATTACK) | 13 (FLEX) | ∞ |

---

## 算法评分体系说明

### 优先级 (Priority) 体系
回合内出牌顺序，数字越小越优先：

| 优先级 | 常量 | 说明 |
|--------|------|------|
| -3 | `FREE_ENERGY` | 0费能量获取 — Offering, Bloodletting |
| -2 | `TUTOR` | 搜索/检索牌 |
| -1 | `UPGRADE_HAND` | 手牌升级 — Armaments (升级后) |
| 0 | `DECK_THINNER` | 牌组瘦身 — Burning Pact, Second Wind, True Grit |
| 1 | `SETUP` | S级核心 — Corruption, Unmovable |
| 2 | `ENERGY_DRAW` | 能量+过牌 — Battle Trance, Offering 等 |
| 3 | `POWER_S` | S级能力 — Demon Form, Dark Embrace, Rupture |
| 4 | `EXHAUST_DRAW` | 消耗+过牌 |
| 5 | `STRENGTH_DEX` | 力量/敏捷缩放 |
| 6 | `VULNERABLE` | 易伤/虚弱 (攻击前先上!) |
| 7 | `DOUBLER` | 双发 — Double Tap, Molten Fist |
| 8 | `POWER` | 其他能力牌 |
| 9 | `DRAW_FILTER` | 过牌/筛选 |
| 10 | `BUFF` | 增益格挡 — Rage, Entrench |
| 11 | `ATTACK` | 攻击牌 |
| 12 | `BLOCK` | 格挡牌 |
| 13 | `FLEX` | 灵活/情境 — Disarm, Havoc, Exhume |
| 14 | `LAST` | 最后手段 |

### 卡牌评分 (Stage Weights)
卡牌奖励选择时的评分维度，满分取决于各维度加权：

| 评分维度 | 分数范围 | 说明 |
|----------|----------|------|
| `ENERGY_DRAW` | 50 | 能量+过牌类最高 |
| `STRENGTH` | 45 | 力量类卡牌 |
| `PREMIUM_ATTACK` | 40 | 优质攻击 (Bludgeon, Immolate) |
| `PREMIUM_BLOCK` | 35 | 优质格挡 (Impervious, Shrug It Off) |
| `AOE` | 30 | 群体伤害 |
| `DEBUFF` | 25 | 减益 (易伤/虚弱) |
| `DRAW` | 20 | 过牌 |
| `POWER` | 20 | 能力牌基础评分 |
| `BASIC_BLOCK` | 15 | 基础格挡 |
| `BASIC_ATTACK` | 10 | 基础攻击 |
| `FLEX` | 5 | 灵活/情境牌 |
| `HP_COST_SYNERGY` | +5 | 有自伤协同 |
| `EXHAUST_SYNERGY` | +18 | 有消耗协同 (Dark Embrace / Feel No Pain) |
| `STRENGTH_SYNERGY` | +15 | 有力量协同 (多段攻击) |
| `BUFF_ENERGY` | 25 | 能量获取 |
| `BUFF_STRENGTH` | 30 | 力量获取 |
| `BUFF_DEXTERITY` | 16 | 敏捷获取 |
| `DEBUFF_VULNERABLE` | 25 | 易伤施加 |
| `DEBUFF_WEAK` | 10 | 虚弱施加 |
| `DRAW_DETECTION` | 33 | 过牌发动机 |
| `X_COST_FLEXIBILITY` | 15 | X费用灵活性 |

### 战力评估 (Power Per Turn Values)
能力牌每回合期望价值 (用于 future_value 估计):

| 卡牌 | 每回合价值 | 说明 |
|------|-----------|------|
| CORRUPTION | 40.0 | 所有技能免费 — 巨大能量价值 |
| DEMON_FORM | 30.0 | +2 力量/回合 |
| DARK_EMBRACE | 27.0 | ~1.5 抽牌/回合 |
| BERSERK | 22.0 | +1 能量/回合 |
| RUPTURE | 20.0 | 自伤→力量 |
| BARRICADE | 20.0 | 格挡保留 |
| BRUTALITY | 18.0 | +1 抽牌/回合 |
| FEEL_NO_PAIN | 18.0 | 消耗→格挡 |
| HELLRAISER | 18.0 | 消耗→力量 |
| INFERNO | 17.0 | 巨大AOE |
| SPOT_WEAKNESS | 16.0 | +3(4) 力量(条件) |
| CRIMSON_MANTLE | 16.0 | 格挡+力量 |
| STAMPEDE | 16.0 | 多段攻击增强 |
| COMBUST | 15.0 | AOE + 自伤 |
| EVOLVE | 15.0 | 状态→抽牌 |
| JUGGERNAUT | 15.0 | 格挡→伤害 |
| INFLAME | 14.0 | +2(3) 力量 |
| AGGRESSION | 14.0 | 力量+易伤 |
| STONE_ARMOR | 14.0 | 格挡+金属化 |
| FIRE_BREATHING | 12.0 | 状态→AOE |
| RAGE | 12.0 | 攻击→格挡 |
| METALLICIZE | 10.0 | +3 格挡/回合 |

### 硬规则 (Hard Sequencing Rules)

**BEFORE 规则** (必须先出):
| 卡牌 | 必须在其之前出 | 原因 |
|------|---------------|------|
| OFFERING | 所有牌 | 0费能量优先级最高 |
| BLOODLETTING | 所有牌 | 0费能量优先级最高 |
| ARMAMENTS | 所有牌 | 手牌升级 |
| DEMON_FORM | 攻击牌 | 力量→攻击 |
| INFLAME | 攻击牌 | 力量→攻击 |
| CORRUPTION | 技能牌 | 技能→0费 |
| BATTLE_TRANCE | 所有牌 | 抽牌锁定 |
| DARK_EMBRACE | 技能牌 | 消耗→抽牌 |
| FEEL_NO_PAIN | 技能牌 | 消耗→格挡 |
| JUGGERNAUT | 攻击牌 | 攻击→格挡→伤害 |
| BASH | 攻击牌 | 易伤→攻击 |
| UPPERCUT | 攻击牌 | 易伤→攻击 |
| THUNDERCLAP | 攻击牌 | 易伤→攻击 |
| SHOCKWAVE | 攻击牌 | 易伤/虚弱→攻击 |
| TREMBLE | 攻击牌 | 易伤/虚弱→攻击 |
| POMMEL_STRIKE | 攻击牌 | 过牌→攻击 |
| SHRUG_IT_OFF | 攻击牌 | 过牌→攻击 |
| DOUBLE_TAP | 攻击牌 | 双发→攻击 |

**AFTER 规则** (应在其之后出):
| 卡牌 | 应在其之后出 | 原因 |
|------|-------------|------|
| BODY_SLAM | 格挡 | 需要格挡值 |
| LIMIT_BREAK | 力量 | 需要先有力量 |
| FEED | 攻击 | 确认斩杀 |
| REAPER | 攻击 | 需要多目标伤害 |
| ENTRENCH | 格挡 | 需要先有格挡 |
| FLEX | 力量 | 力量应用后(攻击前) |

### 最大数量限制 (MaxCopies)

| 卡牌 | 限制数量 | 原因 |
|------|---------|------|
| DEMON_FORM | 1 | 一张足够，速度慢 |
| BARRICADE | 1 | 只需要一张格挡保留 |
| CORRUPTION | 1 | 一张消耗所有技能 |
| DARK_EMBRACE | 1 | 一张过牌足够 |
| JUGGERNAUT | 1 | 叠加效果不明显 |
| EVOLVE | 1 | 一张处理所有状态 |
| FIRE_BREATHING | 1 | 一张足够 |
| BERSERK | 1 | 易伤副作用叠加差 |
| FEEL_NO_PAIN | 1 | 一张足够 |
| LIMIT_BREAK | 1 | 一张让力量持续翻倍 |

---

## 汇总统计

| 品质 | 数量 | 说明 |
|------|------|------|
| 基础 (Starter) | 3 | Strike, Defend, Bash |
| 普通 (Common) | 38 | 大部分为攻击和技能 |
| 罕见 (Uncommon) | 23 | 含核心能力牌 |
| 稀有 (Rare) | 8 | 高额数值和质量 |
| STS2新增 | 46 | 新版本新增 Ironclad 卡牌 |
| **总计** | **118** | |

---

> **修改指南**: 
> - 修改卡牌评分 → 调整 `params.json` 中的 `card_reward.stage_weights` 
> - 修改优先级 → 调整 `CharacterConfigs.cs` 中的 `CardPriorities`
> - 修改最大数量 → 调整 `CharacterConfigs.cs` 中的 `MaxCopies`
> - 修改战力评估 → 调整 `CharacterConfigs.cs` 中的 `PowerPerTurnValues`
> - 修改出牌顺序规则 → 调整 `CharacterConfigs.cs` 中的 `BeforeRules` / `AfterRules`
>
> **注意**: 本数据库基于代码分析构建，部分STS2新增卡牌效果可能与实际游戏有出入。建议在游戏中逐一验证。
