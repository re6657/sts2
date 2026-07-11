#!/usr/bin/env python3
"""
generate_card_db.py — Generate per-character card JSON databases for TokenSpire2.

Reads:
  - CharacterConfigs.cs (CardPriorities → play_priority mapping)
  - STS2_Cards_by_Class.md (complete card lists)
  - STS2_AI_Priority_Tables.md (card stats for Chinese names, rarity, type)
  - CardClassifier.cs logic (category → play_order mapping)

Outputs:
  - Cards/IroncladCards.json  (87 cards)
  - Cards/SilentCards.json    (88 cards)
  - Cards/DefectCards.json    (88 cards)
  - Cards/NecrobinderCards.json (88 cards)
  - Cards/RegentCards.json    (88 cards)

Each JSON entry:
  {
    "id": "BASH",
    "name_cn": "痛击",
    "name_en": "Bash",
    "type": "Attack",
    "cost": 2,
    "rarity": "Basic",
    "play_priority": 60,
    "play_order": 92,
    "effects": { "damage": 8, "vulnerable": 2 },
    "character": "Ironclad"
  }

Usage:
  python scripts/generate_card_db.py
  (run from the TokenSpire2 mod directory)
"""

import json
import os
import re
import sys
from pathlib import Path

# ═══════════════════════════════════════════════════════════════
# Priority Tier → play_priority mapping
# ═══════════════════════════════════════════════════════════════

TIER_TO_PLAY_PRIORITY = {
    # Current tier name              # tier value  → play_priority
    "PRIORITY_MULTIPLAYER_ALLY":     100,  # -5
    "PRIORITY_MULTIPLAYER_TEAM":     100,  # -4
    "PRIORITY_FREE_ENERGY":          100,  # -3
    "PRIORITY_TUTOR":                95,   # -2
    "PRIORITY_UPGRADE_HAND":         90,   # -1
    "PRIORITY_MULTIPLAYER_SELF":     90,   # 0
    "PRIORITY_DECK_THINNER":         85,   # 0 (also maps here)
    "PRIORITY_SETUP":                88,   # 1
    "PRIORITY_ENERGY_DRAW":          82,   # 2
    "PRIORITY_POWER_S":              80,   # 3
    "PRIORITY_EXHAUST_DRAW":         70,   # 4
    "PRIORITY_STRENGTH_DEX":         75,   # 5
    "PRIORITY_VULNERABLE":           60,   # 6
    "PRIORITY_DOUBLER":              48,   # 7
    "PRIORITY_POWER":                70,   # 8
    "PRIORITY_DRAW_FILTER":          55,   # 9
    "PRIORITY_BUFF":                 45,   # 10
    "PRIORITY_ATTACK":               85,   # 11
    "PRIORITY_BLOCK":                65,   # 12
    "PRIORITY_FLEX":                 40,   # 13
    "PRIORITY_LAST":                 35,   # 14
}

# Card category → play_order mapping (from CardClassifier.cs logic)
CATEGORY_PLAY_ORDER = {
    "FREE_ENERGY":   100,
    "PREMIUM_POWER": 98,
    "TUTOR":         95,
    "HAND_UPGRADE":  93,
    "VULNERABLE":    92,
    "WEAK":          92,
    "STRENGTH":      90,
    "DEXTERITY":     90,
    "DOUBLER":       88,
    "FOCUS":         85,
    "POISON":        80,
    "ENERGY":        78,
    "SCALING":       75,
    "STAR":          72,
    "ORB":           65,
    "DRAW":          60,
    "AOE":           50,
    "MULTI_HIT":     45,
    "EXHAUST":       42,
    "DISCARD":       40,
    "SELF_DAMAGE":   35,
    "FINISHER":      20,
    "DEFAULT":       50,
}

# ═══════════════════════════════════════════════════════════════
# Card Classification Sets (mirrors CardClassifier.cs)
# ═══════════════════════════════════════════════════════════════

def _upper_set(*ids):
    return {s.upper() for s in ids}

DRAW_CARDS = _upper_set(
    "POMMEL_STRIKE", "SHRUG_IT_OFF", "BURNING_PACT", "BATTLE_TRANCE",
    "OFFERING", "WARCRY", "BACKFLIP", "DAGGER_THROW", "ESCAPE_PLAN",
    "ACROBATICS", "CALCULATED_GAMBLE", "EXPERTISE", "QUICK_SLASH",
    "HEEL_HOOK", "DROP_KICK", "SKIM", "COMPILE_DRIVER", "COOLHEADED",
    "OVERCLOCK", "REBOUND", "FTL", "SCRAPE", "DREDGE", "FETCH", "PARSE",
    "PILLAGE", "EXPECT_A_FIGHT", "SPITE", "STOKE", "HEADBUTT",
    "GRACE", "CONFESS",
)

ENERGY_CARDS = _upper_set(
    "OFFERING", "BLOODLETTING", "SEEING_RED", "SENTINEL",
    "ADRENALINE", "TACTICIAN", "CONCENTRATE",
    "TURBO", "DOUBLE_ENERGY", "RECYCLE", "AGGREGATE", "FUSION",
    "CHARGE", "HAMMER_TIME", "MANUFACTURING", "AUTOMATION",
    "CORRUPTION", "BERSERK", "DEVA_FORM",
    "FRIENDSHIP", "GENESIS", "SANCTIFY",
)

AOE_CARDS = _upper_set(
    "CLEAVE", "WHIRLWIND", "IMMOLATE", "THUNDERCLAP", "COMBUST",
    "DIE_DIE_DIE", "CORPSE_EXPLOSION", "ALL_OUT_ATTACK", "NOXIOUS_FUMES",
    "ELECTRODYNAMICS", "DOOM_AND_GLOOM", "HYPER_BEAM", "TEMPEST",
    "CONFLUENCE", "BOMBARDMENT", "STAR_EXTINGUISH", "DEFILE",
    "FLEA", "SCRAPE", "SWEEPING_BEAM", "DAZZLING_ENTRANCE",
    "CONFLAGRATION", "INFERNO",
)

MULTI_HIT_CARDS = _upper_set(
    "TWIN_STRIKE", "SWORD_BOOMERANG", "HEAVY_BLADE", "PUMMEL",
    "RIDDLE_WITH_HOLES", "BARRAGE", "TEMPEST", "WHIRLWIND",
    "SKEWER", "FLEA", "SLICE", "ENDLESS_AGONY",
)

VULNERABLE_CARDS = _upper_set(
    "BASH", "UPPERCUT", "THUNDERCLAP", "TREMBLE", "CLOTHESLINE",
    "SHOCKWAVE", "TAUNT", "INTIMIDATE",
    "TERROR", "BEAM_CELL", "GO_FOR_THE_EYES",
    "ENFEEBLING_TOUCH", "PURIFY",
    "AGGRESSION",
)

WEAK_CARDS = _upper_set(
    "CLOTHESLINE", "UPPERCUT", "SHOCKWAVE", "INTIMIDATE",
    "NEUTRALIZE", "SUCKER_PUNCH", "MALAISE", "LEG_SWEEP",
    "GO_FOR_THE_EYES", "BEAM_CELL", "PURIFY", "ENFEEBLING_TOUCH",
)

STRENGTH_CARDS = _upper_set(
    "INFLAME", "SPOT_WEAKNESS", "LIMIT_BREAK", "FLEX", "DEMON_FORM",
    "RUPTURE", "BRAND", "DOMINATE", "SETUP_STRIKE", "FIGHT_ME",
    "AGGRESSION", "JUGGERNAUT", "VICIOUS",
)

DEXTERITY_CARDS = _upper_set(
    "FOOTWORK", "DODGE_AND_ROLL", "AFTERIMAGE",
    "REPROGRAM",
)

POISON_CARDS = _upper_set(
    "DEADLY_POISON", "BOUNCING_FLASK", "NOXIOUS_FUMES", "CATALYST",
    "CORPSE_EXPLOSION", "ENVENOM", "CORROSIVE_WAVE", "POISONED_STAB",
    "FLASK", "VENOMOLOGY",
)

ORB_CARDS = _upper_set(
    "GLACIER", "CHILL", "COLD_SNAP", "BALL_LIGHTNING",
    "DARKNESS", "RAINBOW", "CHAOS", "FUSION", "ZAP",
    "ELECTRODYNAMICS", "TEMPEST", "COOLHEADED",
    "DUALCAST", "MULTI_CAST", "RECURSION", "FISSION",
)

FOCUS_CARDS = _upper_set(
    "DEFRAGMENT", "BIASED_COGNITION", "CONSUME",
)

STAR_CARDS = _upper_set(
    "CHILD_OF_THE_STARS", "ARSENAL", "GENESIS", "THE_SEALED_THRONE",
    "NECRO_MASTERY", "REAPER_FORM", "SPIRIT_OF_ASH", "VOID_FORM",
    "SANCTIFY", "CHARGE", "HAMMER_TIME", "FURNACE",
    "FRIENDSHIP", "INVOKE", "CALCIFY", "DEATH_MARCH",
    "LETHALITY", "BORROWED_TIME", "SOUL_STORM", "PAGESTORM",
)

SCALING_CARDS = _upper_set(
    "DEMON_FORM", "RUPTURE", "INFLAME", "SPOT_WEAKNESS", "LIMIT_BREAK",
    "FOOTWORK", "NOXIOUS_FUMES", "ACCURACY", "ENVENOM", "AFTERIMAGE",
    "DEFRAGMENT", "BIASED_COGNITION", "CONSUME", "CAPACITOR", "LOOP",
    "ECHO_FORM", "CREATIVE_AI", "HEATSINKS", "ELECTRODYNAMICS",
    "NECRO_MASTERY", "SPIRIT_OF_ASH", "LETHALITY", "REAPER_FORM",
    "CHILD_OF_THE_STARS", "ARSENAL", "VOID_FORM", "THE_SEALED_THRONE",
    "DARK_EMBRACE", "FEEL_NO_PAIN", "CORRUPTION", "BARRICADE",
    "JUGGERNAUT", "EVOLVE", "FIRE_BREATHING",
)

EXHAUST_CARDS = _upper_set(
    "TRUE_GRIT", "BURNING_PACT", "SEVER_SOUL", "SECOND_WIND",
    "FIEND_FIRE", "HAVOC", "SENTINEL", "PURITY",
    "RECYCLE", "TURBO", "OFFERING", "EXHUME", "CORRUPTION",
    "PANIC_BUTTON",
)

SELF_DAMAGE_CARDS = _upper_set(
    "HEMOKINESIS", "BLOODLETTING", "OFFERING", "BRUTALITY",
    "COMBUST", "RUPTURE", "BLOOD_WALL",
)

PREMIUM_POWERS = _upper_set(
    "DEMON_FORM", "DARK_EMBRACE", "CORRUPTION", "BARRICADE",
    "FEEL_NO_PAIN", "JUGGERNAUT", "RUPTURE", "INFLAME", "BERSERK",
    "WRAITH_FORM", "FOOTWORK", "NOXIOUS_FUMES", "AFTERIMAGE",
    "ENVENOM", "ACCURACY", "THOUSAND_CUTS", "INFINITE_BLADES",
    "ECHO_FORM", "DEFRAGMENT", "BIASED_COGNITION", "CREATIVE_AI",
    "ELECTRODYNAMICS", "HEATSINKS", "LOOP", "CONSUME", "CAPACITOR",
    "NECRO_MASTERY", "REAPER_FORM", "SPIRIT_OF_ASH", "LETHALITY",
    "FRIENDSHIP", "DEATH_MARCH",
    "CHILD_OF_THE_STARS", "ARSENAL", "VOID_FORM", "THE_SEALED_THRONE",
    "GENESIS",
)

DISCARD_CARDS = _upper_set(
    "TACTICIAN", "REFLEX", "PREPARED", "CALCULATED_GAMBLE",
    "CONCENTRATE", "TOOLS_OF_THE_TRADE", "ACROBATICS",
    "DAGGER_THROW", "SURVIVOR", "EXPERTISE", "STORM_OF_STEEL",
)

DOUBLER_CARDS = _upper_set(
    "DOUBLE_TAP", "BURST", "AMPLIFY", "ECHO_FORM",
    "NIGHTMARE", "PHANTASMAL_KILLER", "REAPER_FORM",
    "ONE_TWO_PUNCH", "MOLTEN_FIST",
)

TUTOR_CARDS = _upper_set(
    "SEEK", "HOLOGRAM", "SECRET_TECHNIQUE", "SECRET_WEAPON",
    "HEADBUTT", "WARCRY", "EXHUME",
)

FINISHER_CARDS = _upper_set(
    "FEED", "REAPER", "RITUAL_DAGGER", "HAND_OF_GREED",
    "FINISHER", "GRAND_FINALE",
)

FREE_ENERGY_CARDS = _upper_set(
    "OFFERING", "BLOODLETTING", "ADRENALINE",
    "TURBO", "DOUBLE_ENERGY", "AGGREGATE", "RECYCLE",
    "CONCENTRATE", "TACTICIAN",
    "FRIENDSHIP", "GENESIS", "SANCTIFY",
)

HAND_UPGRADE_CARDS = _upper_set(
    "ARMAMENTS", "APOTHEOSIS",
)


def classify_card(card_id: str) -> list[str]:
    """Return list of category tags for a card ID."""
    uid = card_id.upper()
    tags = []
    if uid in FREE_ENERGY_CARDS:
        tags.append("FREE_ENERGY")
    if uid in PREMIUM_POWERS:
        tags.append("PREMIUM_POWER")
    if uid in TUTOR_CARDS:
        tags.append("TUTOR")
    if uid in HAND_UPGRADE_CARDS:
        tags.append("HAND_UPGRADE")
    if uid in VULNERABLE_CARDS:
        tags.append("VULNERABLE")
    if uid in WEAK_CARDS:
        tags.append("WEAK")
    if uid in STRENGTH_CARDS:
        tags.append("STRENGTH")
    if uid in DEXTERITY_CARDS:
        tags.append("DEXTERITY")
    if uid in DOUBLER_CARDS:
        tags.append("DOUBLER")
    if uid in FOCUS_CARDS:
        tags.append("FOCUS")
    if uid in POISON_CARDS:
        tags.append("POISON")
    if uid in ENERGY_CARDS:
        tags.append("ENERGY")
    if uid in SCALING_CARDS:
        tags.append("SCALING")
    if uid in STAR_CARDS:
        tags.append("STAR")
    if uid in ORB_CARDS:
        tags.append("ORB")
    if uid in DRAW_CARDS:
        tags.append("DRAW")
    if uid in AOE_CARDS:
        tags.append("AOE")
    if uid in MULTI_HIT_CARDS:
        tags.append("MULTI_HIT")
    if uid in EXHAUST_CARDS:
        tags.append("EXHAUST")
    if uid in DISCARD_CARDS:
        tags.append("DISCARD")
    if uid in SELF_DAMAGE_CARDS:
        tags.append("SELF_DAMAGE")
    if uid in FINISHER_CARDS:
        tags.append("FINISHER")
    return tags


def compute_play_order(card_id: str, tags: list[str] | None = None) -> int:
    """Compute play_order from card categories. Higher = play earlier."""
    if tags is None:
        tags = classify_card(card_id)
    if not tags:
        return CATEGORY_PLAY_ORDER["DEFAULT"]
    # Use highest play_order among matched categories
    best = 0
    for tag in tags:
        order = CATEGORY_PLAY_ORDER.get(tag, 50)
        if order > best:
            best = order
    return best


def compute_play_priority_from_tier(tier_name: str) -> int:
    """Convert a priority tier constant name to play_priority score."""
    return TIER_TO_PLAY_PRIORITY.get(tier_name, 50)


# ═══════════════════════════════════════════════════════════════
# Chinese Name Database
# ═══════════════════════════════════════════════════════════════
# Auto-extracted from SlayTheSpire2.pck game localization data (1714 entries).
# These are the game's built-in Chinese/Japanese kanji card and keyword names.

CHINESE_NAMES = {
    "ABRASIVE": "磨蚀",
    "ABYSSAL_BATHS": "深渊浴场",
    "ACCELERANT": "触媒",
    "ACCELERANT_POWER": "触媒",
    "ACCURACY": "精准",
    "ACCURACY_POWER": "精准",
    "ACROBATICS": "杂技",
    "ACT2_B_EPOCH": "某处",
    "ACT3_B_EPOCH": "别处",
    "ADAPTABLE_POWER": "适者生存",
    "ADAPTATION_POWER": "适应",
    "ADAPTIVE_STRIKE": "适应打击",
    "ADRENALINE": "肾上腺素",
    "ADROIT": "伶俐",
    "AEONGLASS_BOSS": "永世沙漏",
    "AFTERIMAGE": "余像",
    "AFTERIMAGE_POWER": "余像",
    "AFTERLIFE": "来生",
    "AGGRESSION": "好勇斗狠",
    "AGGRESSION_POWER": "好勇斗狠",
    "AKABEKO": "赤牛",
    "ALCHEMICAL_COFFER": "炼金箱",
    "ALCHEMIZE": "炼制药水",
    "ALIGNMENT": "星位序列",
    "ALL_CARDS_UPGRADED": "完美牌组",
    "ALL_FOR_ONE": "万物一心",
    "ALL_OTHER_ACHIEVEMENTS": "永存者",
    "ALL_STAR": "全明星",
    "AMALGAMATOR": "熔合者",
    "AMETHYST_AUBERGINE": "紫水晶茄子",
    "ANCHOR": "锚",
    "ANGER": "愤怒",
    "ANOINTED": "天选",
    "ANTICIPATE": "预判",
    "APOTHEOSIS": "神化",
    "APPARITION": "灵体",
    "ARCANE_SCROLL": "奥术卷轴",
    "ARCHAIC_TOOTH": "古老牙齿",
    "ARMAMENTS": "武装",
    "AROMA_OF_CHAOS": "混沌芳香",
    "ARSENAL": "武器库",
    "ARSENAL_POWER": "武器库",
    "ARTIFACT_POWER": "人工制品",
    "ART_OF_WAR": "孙子兵法",
    "ASCENDERS_BANE": "进阶之灾",
    "ASHEN_STRIKE": "灰烬打击",
    "ASHWATER": "灰水",
    "ASLEEP_POWER": "沉睡",
    "ASSASSINATE": "刺杀",
    "ASTRAL_PULSE": "星界脉冲",
    "ASTROLABE": "星盘",
    "ATTACK": "攻势",
    "ATTACK_POTION": "攻击药水",
    "AUTOMATION": "自动化",
    "AUTOMATION_POWER": "自动化",
    "AXEBOTS_NORMAL": "巨斧机器人",
    "A_THOUSAND_CUTS_POWER": "凌迟",
    "BACKFLIP": "后空翻",
    "BACKSTAB": "背刺",
    "BACK_ATTACK_LEFT_POWER": "后方攻击",
    "BACK_ATTACK_RIGHT_POWER": "后方攻击",
    "BAD_LUCK": "霉运",
    "BAG_OF_MARBLES": "弹珠袋",
    "BAG_OF_PREPARATION": "准备背包",
    "BALL_LIGHTNING": "球状闪电",
    "BANSHEES_CRY": "女妖之嚎",
    "BARRAGE": "弹幕齐射",
    "BARRICADE": "壁垒",
    "BARRICADE_POWER": "壁垒",
    "BASH": "痛击",
    "BATTLEWORN_DUMMY": "战痕累累的训练假人",
    "BATTLEWORN_DUMMY_EVENT_ENCOUNTER": "历战假人",
    "BATTLEWORN_DUMMY_TIME_LIMIT_POWER": "时间限制",
    "BATTLE_TRANCE": "战斗专注",
    "BEACON_OF_HOPE": "希望灯塔",
    "BEACON_OF_HOPE_POWER": "希望灯塔",
    "BEAM_CELL": "光束射线",
    "BEATING_REMNANT": "律动残余",
    "BEAT_DOWN": "狠揍",
    "BEAT_INTO_SHAPE": "锻打成型",
    "BEAUTIFUL_BRACELET": "华美手镯",
    "BECKON": "呼唤",
    "BEETLE_JUICE": "甲虫汁",
    "BEGONE": "下去！",
    "BELIEVE_IN_YOU": "相信着你",
    "BELLOWS": "风箱",
    "BELT_BUCKLE": "腰带扣",
    "BIASED_COGNITION": "偏差认知",
    "BIASED_COGNITION_POWER": "偏差认知",
    "BIG_BANG": "大爆炸",
    "BIG_GAME_HUNTER": "精英猎手",
    "BIG_HAT": "大帽子",
    "BIG_MUSHROOM": "大蘑菇",
    "BIIIG_HUG": "大～抱抱",
    "BINARY": "两者择一",
    "BING_BONG": "宾邦",
    "BLACK_BLOOD": "黑暗之血",
    "BLACK_HOLE": "黑洞",
    "BLACK_HOLE_POWER": "黑洞",
    "BLACK_STAR": "黑星",
    "BLADE_DANCE": "刀刃之舞",
    "BLADE_OF_INK": "墨之刃",
    "BLESSED_ANTLER": "赐福鹿角",
    "BLESSING_OF_THE_FORGE": "熔炉的祝福",
    "BLIGHT_STRIKE": "荒疫打击",
    "BLOCK": "格挡",
    "BLOCK_NEXT_TURN_POWER": "下回合格挡",
    "BLOCK_POTION": "格挡药水",
    "BLOODLETTING": "放血",
    "BLOOD_POTION": "鲜血药水",
    "BLOOD_SOAKED_ROSE": "血染玫瑰",
    "BLOOD_VIAL": "小血瓶",
    "BLOOD_WALL": "血墙",
    "BLUDGEON": "重锤",
    "BLUR": "残影",
    "BLUR_POWER": "残影",
    "BODYGUARD": "护卫",
    "BODY_SLAM": "全身撞击",
    "BOLAS": "流星锤",
    "BOMBARDMENT": "轰击",
    "BONE_BREW": "骨头酿",
    "BONE_FLUTE": "骨笛",
    "BONE_SHARDS": "碎骨",
    "BONE_TEA": "骨茶",
    "BOOKMARK": "书签",
    "BOOK_OF_FIVE_RINGS": "五轮书",
    "BOOK_REPAIR_KNIFE": "修书小刀",
    "BOOMING_CONCH": "轰鸣海螺",
    "BOOST_AWAY": "高速脱离",
    "BOOT_SEQUENCE": "启动流程",
    "BORROWED_TIME": "预借时间",
    "BORROWED_TIME_POWER": "预借时间",
    "BOTTLED_POTENTIAL": "瓶装潜能",
    "BOUNCING_FLASK": "弹跳药瓶",
    "BOUND": "魂缚",
    "BOUND_PHYLACTERY": "缚魂命匣",
    "BOWLBUGS_NORMAL": "盛碗虫群",
    "BOWLBUGS_WEAK": "盛碗虫",
    "BOWLER_HAT": "圆顶礼帽",
    "BRAIN_LEECH": "脑蛭",
    "BRAND": "烙印",
    "BREAD": "面包",
    "BREAK": "破击",
    "BREAKTHROUGH": "突破",
    "BRIGHTEST_FLAME": "至亮之焰",
    "BRILLIANT_SCARF": "艳丽围巾",
    "BRIMSTONE": "硫磺",
    "BRONZE_SCALES": "铜质鳞片",
    "BUBBLE_BUBBLE": "咕嘟冒泡",
    "BUFF": "强化",
    "BUFFER": "缓冲",
    "BUFFER_POWER": "缓冲",
    "BUGSLAYER": "害虫杀手",
    "BULK_UP": "暴涨",
    "BULLET_TIME": "子弹时间",
    "BULLY": "欺凌",
    "BULWARK": "铸墙",
    "BUNDLE_OF_JOY": "新生之喜",
    "BURN": "灼伤",
    "BURNING_BLOOD": "燃烧之血",
    "BURNING_PACT": "燃烧契约",
    "BURNING_STICKS": "燃烧木棍",
    "BURROWED_POWER": "埋地",
    "BURST": "爆发",
    "BURST_POWER": "爆发",
    "BURY": "埋葬",
    "BYGONE_EFFIGY_ELITE": "旧日雕像",
    "BYRDONIS_EGG": "多尼斯异鸟蛋",
    "BYRDONIS_ELITE": "多尼斯异鸟",
    "BYRDONIS_NEST": "多尼斯异鸟巢",
    "BYRDPIP": "异鸟宝宝",
    "BYRD_SWOOP": "异鸟扑击",
    "CALAMITY": "劫难",
    "CALAMITY_POWER": "劫难",
    "CALCIFY": "钙化",
    "CALCIFY_POWER": "钙化",
    "CALCULATED_GAMBLE": "计算下注",
    "CALLING_BELL": "召唤铃铛",
    "CALL_OF_THE_VOID": "虚空之唤",
    "CALL_OF_THE_VOID_POWER": "虚空之唤",
    "CALTROPS": "铁蒺藜",
    "CANDELABRA": "烛台",
    "CAPACITOR": "扩容",
    "CAPTAINS_WHEEL": "舵盘",
    "CAPTURE_SPIRIT": "捕捉灵魂",
    "CARD_DEBUFF": "恶意",
    "CARD_REWARD": "卡牌奖励",
    "CASCADE": "倾泻",
    "CATASTROPHE": "横祸",
    "CAULDRON": "大锅",
    "CCCCOMBO": "连连连连段攻击",
    "CELESTIAL_MIGHT": "天穹之力",
    "CENTENNIAL_PUZZLE": "百年积木",
    "CEREMONIAL_BEAST_BOSS": "仪式兽",
    "CHAINS_OF_BINDING_POWER": "魂缚锁链",
    "CHANDELIER": "吊灯",
    "CHANNELING": "生成",
    "CHAOS": "混沌",
    "CHARACTER_SKILL_IRONCLAD1": "契约完成",
    "CHARACTER_SKILL_IRONCLAD2": "最重之刃",
    "CHARACTER_SKILL_NECROBINDER1": "已经死了",
    "CHARACTER_SKILL_NECROBINDER2": "喝奶补钙",
    "CHARACTER_SKILL_REGENT1": "王者之刃",
    "CHARACTER_SKILL_REGENT2": "群日凌空",
    "CHARACTER_SKILL_SILENT1": "安得奇巧",
    "CHARACTER_SKILL_SILENT2": "几滴就好",
    "CHARGE": "冲锋！！",
    "CHARGE_BATTERY": "充电",
    "CHARONS_ASHES": "卡戎之灰",
    "CHEMICAL_X": "化学物X",
    "CHILD_OF_THE_STARS": "群星之子",
    "CHILD_OF_THE_STARS_POWER": "群星之子",
    "CHILL": "冰寒",
    "CHOICES_PARADOX": "选择悖论",
    "CHOMPERS_NORMAL": "一对自动机械",
    "CHOSEN_CHEESE": "天选芝士",
    "CHRYSALIS": "结茧",
    "CINDER": "余烬",
    "CIRCLET": "头环",
    "CLARITY": "明晰提取物",
    "CLARITY_POWER": "明晰",
    "CLASH": "交锋",
    "CLAW": "爪击",
    "CLAWS": "利爪",
    "CLEANSE": "洁净",
    "CLEAR_DRAWING": "清除绘画",
    "CLOAK_AND_DAGGER": "斗篷与匕首",
    "CLOAK_CLASP": "斗篷扣",
    "CLOAK_OF_STARS": "群星斗篷",
    "CLONE": "克隆",
    "CLUMSY": "笨拙",
    "COLD_SNAP": "寒流",
    "COLLISION_COURSE": "碰撞轨迹",
    "COLORFUL_PHILOSOPHERS": "色彩哲学家",
    "COLORLESS1_EPOCH": "先子星",
    "COLORLESS2_EPOCH": "建筑师",
    "COLORLESS3_EPOCH": "两个反叛者",
    "COLORLESS4_EPOCH": "沉睡",
    "COLORLESS5_EPOCH": "买点什么吧！",
    "COLORLESS_POTION": "无色药水",
    "COLOSSAL_FLOWER": "巨大花卉",
    "COLOSSUS": "巨像",
    "COLOSSUS_POWER": "巨像",
    "COMET": "彗星",
    "COMPACT": "压缩",
    "COMPENDIUM": "百科大全",
    "COMPENDIUM_ACHIEVEMENTS": "角色数据",
    "COMPENDIUM_BESTIARY": "怪物图鉴",
    "COMPENDIUM_CARD_LIBRARY": "卡牌总览",
    "COMPENDIUM_LEADERBOARDS": "排行榜",
    "COMPENDIUM_POTION_LAB": "药水研究所",
    "COMPENDIUM_RELIC_COLLECTION": "遗物收集",
    "COMPENDIUM_RUN_HISTORY": "历史记录",
    "COMPILE_DRIVER": "编译冲击",
    "COMPLETE_ACT4": "真结局",
    "COMPLETE_TIMELINE": "历史学家",
    "CONFLAGRATION": "焚烧",
    "CONFUSED_POWER": "混乱",
    "CONQUEROR": "征服者",
    "CONQUEROR_POWER": "征服者",
    "CONSTRICT_POWER": "紧缠",
    "CONSTRUCT_MENAGERIE_NORMAL": "一些构装体",
    "CONSUMING_SHADOW": "吞噬暗影",
    "CONSUMING_SHADOW_POWER": "吞噬暗影",
    "CONTRACTILITY_POWER": "伸缩力",
    "CONVERGENCE": "汇流",
    "COOK": "烹饪",
    "COOLANT": "冷却剂",
    "COOLANT_POWER": "冷却剂",
    "COOLHEADED": "冷静头脑",
    "COORDINATE": "协同配合",
    "CORPSE_SLUGS_NORMAL": "许多噬尸蛞蝓",
    "CORPSE_SLUGS_WEAK": "一些噬尸蛞蝓",
    "CORROSIVE_WAVE": "腐蚀波",
    "CORROSIVE_WAVE_POWER": "腐蚀波",
    "CORRUPTED": "腐化",
    "CORRUPTION": "腐化",
    "CORRUPTION_POWER": "腐化",
    "COSMIC_CONCOCTION": "宇宙药剂",
    "COSMIC_INDIFFERENCE": "宇宙冷漠",
    "COUNTDOWN": "倒数计时",
    "COUNTDOWN_POWER": "倒数计时",
    "COVERED_POWER": "掩护",
    "CRAB_RAGE_POWER": "蟹之怒",
    "CRACKED_CORE": "破损核心",
    "CRASH_LANDING": "迫降",
    "CREATIVE_AI": "创造性AI",
    "CREATIVE_AI_POWER": "创造性AI",
    "CRESCENT_SPEAR": "新月长矛",
    "CRIMSON_MANTLE": "绯红披风",
    "CRIMSON_MANTLE_POWER": "绯红披风",
    "CROSSBOW": "十字弓",
    "CRUELTY": "残酷",
    "CRUELTY_POWER": "残酷",
    "CRUSH_UNDER": "下砸",
    "CRYSTAL_SPHERE": "水晶球",
    "CUBEX_CONSTRUCT_NORMAL": "方柱构装体",
    "CULTISTS_NORMAL": "邪教徒们",
    "CUNNING_POTION": "狡诈药水",
    "CURE_ALL": "痊愈药水",
    "CURIOUS_POWER": "好奇",
    "CURL_UP_POWER": "蜷身",
    "CURSED_KETTLE": "诅咒水壶",
    "CURSED_PEARL": "诅咒珍珠",
    "CURSED_RUN": "诅咒局",
    "CURSES": "诅咒局！",
    "CURSE_OF_THE_BELL": "铃铛的诅咒",
    "CUSTOM": "自定模式",
    "CUSTOM_AND_SEEDS_EPOCH": "种子",
    "CUSTOM_MP": "自定模式",
    "DAGGER_SPRAY": "匕首雨",
    "DAGGER_THROW": "投掷匕首",
    "DAILY": "每日挑战",
    "DAILY_MP": "多人每日挑战",
    "DAILY_RUN_EPOCH": "每日挑战",
    "DAMAGE_LEADER": "伤害第一",
    "DAMPEN_POWER": "抑制",
    "DANSE_MACABRE": "死亡之舞",
    "DANSE_MACABRE_POWER": "死亡之舞",
    "DARKNESS": "漆黑",
    "DARKSTONE_PERIAPT": "黑石护符",
    "DARK_EMBRACE": "黑暗之拥",
    "DARK_EMBRACE_POWER": "黑暗之拥",
    "DARK_ORB": "黑暗",
    "DARK_SHACKLES": "黑暗镣铐",
    "DARV": "达弗",
    "DARV_EPOCH": "达弗",
    "DASH": "冲刺",
    "DATA_DISK": "数据磁盘",
    "DAUGHTER_OF_THE_WIND": "风的女儿",
    "DAZED": "晕眩",
    "DEADLY_EVENTS": "致命事件",
    "DEADLY_POISON": "致命毒药",
    "DEATHBRINGER": "死亡使者",
    "DEATHS_DOOR": "死亡之门",
    "DEATH_BLOW": "濒死一击",
    "DEATH_MARCH": "死亡行军",
    "DEBILITATE": "摧残",
    "DEBILITATE_POWER": "摧残",
    "DEBRIS": "碎屑",
    "DEBT": "债务",
    "DEBUFF": "策略",
    "DEBUFFER": "弱化专家",
    "DEBUFF_STRONG": "策略",
    "DECAY": "腐朽",
    "DECIMILLIPEDE_ELITE": "残杀千足虫",
    "DECISIONS_DECISIONS": "抉择，抉择",
    "DECK": "牌组{Hotkey:choose(None):|（{}）}",
    "DECREE_OF_ENTROPY_POWER": "熵之律令",
    "DECREE_OF_UNMAKING_POWER": "亡之律令",
    "DEFEAT_GLORY_ENEMIES": "荣耀",
    "DEFEAT_HIVE_ENEMIES": "巢穴",
    "DEFEAT_ONE_BOSS": "小小开始",
    "DEFEAT_OVERGROWTH_ENEMIES": "密林",
    "DEFEAT_UNDERDOCKS_ENEMIES": "暗港",
    "DEFECT": "故障机器人",
    "DEFECT1_EPOCH": "启动",
    "DEFECT2_EPOCH": "欺骗",
    "DEFECT3_EPOCH": "你好！你好！",
    "DEFECT4_EPOCH": "处理虫子（Bug）",
    "DEFECT5_EPOCH": "对抗",
    "DEFECT6_EPOCH": "激光束",
    "DEFECT7_EPOCH": "哦，亮闪闪！",
    "DEFECT_ASCENSION10": "回响",
    "DEFECT_WIN": "故障机器人",
    "DEFEND": "守势",
    "DEFEND_DEFECT": "防御",
    "DEFEND_IRONCLAD": "防御",
    "DEFEND_NECROBINDER": "防御",
    "DEFEND_REGENT": "防御",
    "DEFEND_SILENT": "防御",
    "DEFILE": "玷污",
    "DEFLECT": "偏折",
    "DEFRAGMENT": "碎片整理",
    "DEFY": "违逆",
    "DELAY": "拖延",
    "DELICATE_FROND": "娇嫩蕨草",
    "DEMESNE": "领域",
    "DEMESNE_POWER": "领域",
    "DEMISE_POWER": "消亡",
    "DEMONIC_SHIELD": "恶魔护盾",
    "DEMON_FORM": "恶魔形态",
    "DEMON_FORM_POWER": "恶魔形态",
    "DEMON_TONGUE": "恶魔之舌",
    "DENSE_VEGETATION": "茂密的植被",
    "DENSE_VEGETATION_EVENT_ENCOUNTER": "扭动虫",
    "DEPRECATED_ACT": "已弃用阶段",
    "DEPRECATED_CARD": "弃用卡牌",
    "DEPRECATED_CHARACTER": "已弃用角色",
    "DEPRECATED_ENCHANTMENT": "弃用",
    "DEPRECATED_ENCOUNTER": "已移除遭遇战",
    "DEPRECATED_EVENT": "已删除事件",
    "DEPRECATED_POTION": "废弃药水",
    "DEPRECATED_RELIC": "弃用遗物",
    "DEPRIVED": "被剥夺者",
    "DESPAIR": "绝望",
    "DESPAIR_POWER": "绝望",
    "DEVASTATE": "葬送",
    "DEVOTED_SCULPTOR_WEAK": "虔诚雕刻师",
    "DEVOUR_LIFE": "吞噬生命",
    "DEVOUR_LIFE_POWER": "吞噬生命",
    "DEXTERITY_DOWN_POWER": "敏捷下降",
    "DEXTERITY_POTION": "敏捷药水",
    "DEXTERITY_POWER": "敏捷",
    "DIAMOND_DIADEM": "钻石头冠",
    "DIAMOND_DIADEM_POWER": "钻石头冠",
    "DIE_FOR_YOU_POWER": "为你而死",
    "DINGY_RUG": "肮脏地毯",
    "DIRGE": "挽歌",
    "DISCARD_PILE": "弃牌堆{Hotkey:choose(None):|（{}）}",
    "DISCOVERY": "发现",
    "DISCOVER_ALL_CARDS": "收藏家",
    "DISCOVER_ALL_EVENTS": "探险家",
    "DISCOVER_ALL_RELICS": "考古学家",
    "DISINTEGRATION": "瓦解",
    "DISINTEGRATION_POWER": "瓦解",
    "DISMANTLE": "拆卸",
    "DISTILLED_CHAOS": "精炼混沌",
    "DISTINGUISHED_CAPE": "卓越斗篷",
    "DISTRACTION": "声东击西",
    "DIVINE_DESTINY": "天命所归",
    "DIVINE_RIGHT": "天赋君权",
    "DODGE_AND_ROLL": "闪躲翻滚",
    "DOLLYS_MIRROR": "多利之镜",
    "DOLL_ROOM": "玩偶室",
    "DOMINATE": "主宰",
    "DOOM_POWER": "灾厄",
    "DOORS_OF_LIGHT_AND_DARK": "光与暗的门扉",
    "DOUBLE_DAMAGE_POWER": "双倍伤害",
    "DOUBLE_ENERGY": "双倍能量",
    "DOUBLE_SNECKO": "二蛇之眼",
    "DOUBT": "疑虑",
    "DRAFT": "选牌",
    "DRAGON_FRUIT": "火龙果",
    "DRAIN_POWER": "能量汲取",
    "DRAMATIC_ENTRANCE": "闪亮登场",
    "DRAW_CARDS_NEXT_TURN_POWER": "下回合抽牌",
    "DRAW_PILE": "抽牌堆{Hotkey:choose(None):|（{}）}",
    "DREAM_CATCHER": "捕梦网",
    "DREDGE": "清淤",
    "DRIFTWOOD": "浮木",
    "DROPLET_OF_PRECOGNITION": "预知之滴",
    "DROWNING_BEACON": "淹水灯塔",
    "DRUM_OF_BATTLE": "战鼓",
    "DUALCAST": "双重释放",
    "DUAL_WIELD": "双持",
    "DUPLICATION_POWER": "复制",
    "DUPLICATOR": "复制药水",
    "DUSTY_TOME": "尘封魔典",
    "DYING_STAR": "星灭",
    "ECHOING_SLASH": "回响斩击",
    "ECHO_FORM": "回响形态",
    "ECHO_FORM_POWER": "回响形态",
    "ECTOPLASM": "灵体外质",
    "EIDOLON": "幻景",
    "ELECTRIC_SHRYMP": "放电异虾",
    "EMBER_TEA": "余烬茶",
    "EMOTION_CHIP": "情感芯片",
    "EMPTY_CAGE": "空鸟笼",
    "EMPTY_SLOT": "充能球栏位",
    "ENABLE_TUTORIALS": "要看教程吗？",
    "ENDLESS_APPETITE": "无尽食欲",
    "ENDLESS_CONVEYOR": "无尽传送带",
    "END_OF_DAYS": "末日降临",
    "END_TURN": "结束回合{Hotkey:choose(None):|（{}）}",
    "ENERGY": "能量",
    "ENERGY_COUNT": "能量",
    "ENERGY_NEXT_TURN_POWER": "下回合能量",
    "ENERGY_POTION": "能量药水",
    "ENERGY_SURGE": "能量涌动",
    "ENFEEBLING_TOUCH": "弱化之触",
    "ENLIGHTENMENT": "开悟",
    "ENRAGE_POWER": "激怒",
    "ENTANGLED": "缠身",
    "ENTHRALLED": "执迷",
    "ENTOMANCER_ELITE": "蜂群术士",
    "ENTRENCH": "巩固",
    "ENTROPIC_BREW": "混沌药水",
    "ENTROPY": "熵",
    "ENTROPY_POWER": "熵",
    "ENVENOM": "涂毒",
    "ENVENOM_POWER": "涂毒",
    "EQUILIBRIUM": "均衡",
    "ERADICATE": "根除",
    "ERROR": "锁定",
    "ESCAPE": "懦弱",
    "ESCAPE_ARTIST_POWER": "逃脱大师",
    "ESCAPE_PLAN": "逃脱计划",
    "ESSENCE_OF_DARKNESS": "黑暗精华",
    "ETERNAL": "永恒",
    "ETERNAL_ARMOR": "永恒铠甲",
    "ETERNAL_FEATHER": "永恒羽毛",
    "ETHEREAL": "虚无",
    "EVENT": "事件",
    "EVENT1_EPOCH": "邪教徒们",
    "EVENT2_EPOCH": "他们回来了",
    "EVENT3_EPOCH": "高塔",
    "EVIL_EYE": "邪眼",
    "EVOKE": "激发",
    "EXHAUST": "消耗",
    "EXHAUST_PILE": "消耗牌堆{Hotkey:choose(None):|（{}）}",
    "EXOSKELETONS_NORMAL": "许多外骨骼虫",
    "EXOSKELETONS_WEAK": "外骨骼虫",
    "EXPECT_A_FIGHT": "跃跃欲试",
    "EXPERTISE": "独门技术",
    "EXPLOSIVE_AMPOULE": "爆炸安瓿",
    "EXPOSE": "暴露",
    "EXTERMINATE": "杀灭",
    "FABRICATOR_NORMAL": "组装师",
    "FAIRY_IN_A_BOTTLE": "瓶中精灵",
    "FAKE_ANCHOR": "锚？？？",
    "FAKE_BLOOD_VIAL": "小血瓶？？？",
    "FAKE_HAPPY_FLOWER": "开心小花？？？",
    "FAKE_LEES_WAFFLE": "李家华夫饼？？？",
    "FAKE_MANGO": "芒果？？？",
    "FAKE_MERCHANT": "商人？？？",
    "FAKE_MERCHANTS_RUG": "商人的地毯？？？",
    "FAKE_MERCHANT_EVENT_ENCOUNTER": "商人？？？",
    "FAKE_ORICHALCUM": "奥利哈钢？？？",
    "FAKE_SNECKO_EYE": "异蛇之眼？？？",
    "FAKE_STRIKE_DUMMY": "打击木偶？？？",
    "FAKE_VENERABLE_TEA_SET": "古茶具套装？？？",
    "FALLING_STAR": "陨星",
    "FAMISHED": "饥肠辘辘",
    "FAN_OF_KNIVES": "刀扇",
    "FAN_OF_KNIVES_POWER": "刀扇",
    "FASTEN": "勒紧",
    "FASTEN_POWER": "勒紧",
    "FATAL": "斩杀",
    "FAVORITE_CARD": "最爱好牌",
    "FEAR": "恐惧",
    "FEED": "狂宴",
    "FEEDING_FRENZY": "疯狂进食",
    "FEEL_NO_PAIN": "无惧疼痛",
    "FEEL_NO_PAIN_POWER": "无惧疼痛",
    "FENCING_MANUAL": "击剑指南",
    "FERAL": "野性",
    "FERAL_POWER": "野性",
    "FESTIVE_POPPER": "节日拉炮",
    "FETCH": "取回",
    "FIDDLE": "小提琴",
    "FIELD_OF_MAN_SIZED_HOLES": "人形洞穴之地",
    "FIEND_FIRE": "恶魔之焰",
    "FIGHT_ME": "与我一战！",
    "FIGHT_THROUGH": "强撑",
    "FINESSE": "妙计",
    "FINISHER": "终结技",
    "FIRE_POTION": "火焰药水",
    "FISHING_ROD": "钓鱼竿",
    "FISTICUFFS": "拳斗",
    "FLAK_CANNON": "散射炮",
    "FLAME_BARRIER": "火焰屏障",
    "FLAME_BARRIER_POWER": "火焰屏障",
    "FLAME_IN_THE_DARK": "暗中之火",
    "FLANKING": "夹击",
    "FLANKING_POWER": "夹击",
    "FLASH_OF_STEEL": "亮剑",
    "FLATTEN": "重压",
    "FLECHETTES": "飞镖",
    "FLEX_POTION": "肌肉药水",
    "FLICK_FLACK": "翻越撑击",
    "FLIGHT": "飞行",
    "FLOOR": "楼层",
    "FLOOR_TEN_THOUSAND": "好多楼梯",
    "FLUTTER_POWER": "振翅",
    "FLYCONID_NORMAL": "真菌与史莱姆",
    "FLYCONID_WEAK": "飞蝇菌子",
    "FOCUSED_STRIKE": "集中打击",
    "FOCUS_DOWN": "集中下降",
    "FOCUS_POTION": "集中药水",
    "FOCUS_POWER": "集中",
    "FOGMOG_NORMAL": "雾菇",
    "FOLLY": "愚行",
    "FOOTWORK": "灵动步法",
    "FORBIDDEN_GRIMOIRE": "禁忌魔典",
    "FORBIDDEN_GRIMOIRE_POWER": "禁忌魔典",
    "FOREGONE_CONCLUSION": "既定事项",
    "FOREGONE_CONCLUSION_POWER": "既定事项",
    "FORGE": "铸造",
    "FORGOTTEN_RITUAL": "被遗忘的仪式",
    "FORGOTTEN_SOUL": "遗忘之魂",
    "FORTIFIER": "固化药水",
    "FOSSIL_STALKER_NORMAL": "化石追踪者",
    "FOUL_POTION": "污浊药水",
    "FRAGRANT_MUSHROOM": "芳香蘑菇",
    "FRAIL_POWER": "脆弱",
    "FRANTIC_ESCAPE": "狂乱逃离",
    "FREE_ATTACK_POWER": "免费攻击",
    "FREE_POWER_POWER": "免费能力",
    "FREE_SKILL_POWER": "免费技能",
    "FRESNEL_LENS": "菲涅耳透镜",
    "FRIENDSHIP": "友谊",
    "FRIENDSHIP_POWER": "友谊",
    "FROG_KNIGHT_NORMAL": "青蛙骑士",
    "FROST_ORB": "冰霜",
    "FROZEN_EGG": "冻结之蛋",
    "FRUIT_JUICE": "果汁",
    "FTL": "超越光速",
    "FUEL": "燃料",
    "FUNERARY_MASK": "葬礼面具",
    "FURNACE": "熔炉",
    "FURNACE_POWER": "熔炉",
    "FUR_COAT": "皮草大衣",
    "FUSION": "聚变",
    "FUZZY_WURM_CRAWLER_WEAK": "毛绒伏地虫",
    "FYSH_OIL": "异鱼之油",
    "GALACTIC_DUST": "星系尘埃",
    "GALVANIC_POWER": "流电",
    "GALVANIZED": "流电",
    "GAMBLERS_BREW": "赌徒特酿",
    "GAMBLING_CHIP": "赌博筹码",
    "GAME_PIECE": "棋子",
    "GAMMA_BLAST": "伽马爆破",
    "GANG_UP": "群起攻之",
    "GATHER_LIGHT": "收集光辉",
    "GENESIS": "创世纪",
    "GENESIS_POWER": "创世纪",
    "GENETIC_ALGORITHM": "遗传算法",
    "GHOST_EATER": "噬魂者",
    "GHOST_IN_A_JAR": "罐装幽灵",
    "GHOST_SEED": "幽灵种子",
    "GIANT_ROCK": "巨石",
    "GIGANTIFICATION_POTION": "超巨化药水",
    "GIGANTIFICATION_POWER": "超巨化",
    "GIRYA": "壶铃",
    "GLACIER": "冰川",
    "GLAM": "华彩",
    "GLASSWORK": "玻璃工艺",
    "GLASS_EYE": "玻璃眼珠",
    "GLASS_ORB": "玻璃",
    "GLIMMER": "微光",
    "GLIMPSE_BEYOND": "彼岸一瞥",
    "GLITTER": "亮片",
    "GLITTERSTREAM": "流光溢彩",
    "GLOBE_HEAD_NORMAL": "一个孤单的电球头",
    "GLORY": "荣耀",
    "GLOW": "辉光",
    "GLOWING_ORB": "发光之球",
    "GLOWWATER_POTION": "发光水",
    "GNARLED_HAMMER": "扭曲锤子",
    "GOLDEN_COMPASS": "黄金罗盘",
    "GOLDEN_PEARL": "金色珍珠",
    "GOLD_AXE": "金斧",
    "GOLD_PLATED_CABLES": "镀金缆线",
    "GOOD_INSTINCTS": "优秀直觉",
    "GOOPY": "黏糊",
    "GORGET": "护喉甲",
    "GO_FOR_THE_EYES": "眼部攻击",
    "GRABBED": "抓取",
    "GRAND_FINALE": "华丽收场",
    "GRAVEBLAST": "坟冢爆射",
    "GRAVE_OF_THE_FORGOTTEN": "遗忘之墓",
    "GRAVE_WARDEN": "守墓人",
    "GRAVITY_POWER": "引力",
    "GREED": "贪婪",
    "GREMLIN_HORN": "地精之角",
    "GREMLIN_MERC_NORMAL": "穿着一件大衣的两只地精",
    "GUARDED_POWER": "护卫",
    "GUARDS": "护驾！！！",
    "GUIDING_STAR": "引导之星",
    "GUILTY": "愧疚",
    "GUNK_UP": "污秽攻击",
    "HAILSTORM": "冰雹风暴",
    "HAILSTORM_POWER": "冰雹风暴",
    "HAMMER_TIME": "锤子时间",
    "HAMMER_TIME_POWER": "锤子时间",
    "HAND_DRILL": "手钻",
    "HAND_OF_GREED": "贪婪之手",
    "HAND_TRICK": "手上技法",
    "HANG": "吊杀",
    "HANG_POWER": "吊杀",
    "HAPPY_FLOWER": "开心小花",
    "HARDENED_SHELL_POWER": "硬化外壳",
    "HARD_TO_KILL_POWER": "难以杀灭",
    "HATCH_POWER": "孵化",
    "HAUNT": "纠缠",
    "HAUNTED_SHIP_NORMAL": "幽灵船",
    "HAUNT_POWER": "纠缠",
    "HAVOC": "破灭",
    "HAZE": "迷雾",
    "HEADBUTT": "头槌",
    "HEAL": "回复",
    "HEART_OF_IRON": "铁心药水",
    "HEATSINKS_POWER": "散热片",
    "HEAVENLY_DRILL": "天际钻头",
    "HEFTY_TABLET": "沉重石板",
    "HEGEMONY": "霸权",
    "HEIRLOOM_HAMMER": "传承之锤",
    "HEIST_POWER": "盗窃",
    "HELICAL_DART": "螺线飞镖",
    "HELIX_DRILL": "螺旋钻击",
    "HELLO_WORLD": "你好世界",
    "HELLO_WORLD_POWER": "你好世界",
    "HELLRAISER": "地狱狂徒",
    "HELLRAISER_POWER": "地狱狂徒",
    "HEMOKINESIS": "御血术",
    "HEXED": "邪咒",
    "HEX_POWER": "恶咒",
    "HIDDEN_CACHE": "隐秘藏品",
    "HIDDEN_DAGGERS": "隐秘匕首",
    "HIDDEN_GEM": "未掘宝石",
    "HIGHLANDER": "宇宙",
    "HIGH_FIVE": "击掌",
    "HIGH_VOLTAGE_POWER": "高电压",
    "HISTORY_COURSE": "历史课",
    "HIT_POINTS": "生命值（HP）",
    "HIVE": "巢穴",
    "HOARDER": "囤积癖",
    "HOLOGRAM": "全息影像",
    "HONED": "熟能生巧",
    "HORN_CLEAT": "船夹板",
    "HOST": "创建",
    "HOTFIX": "热修复",
    "HOWL_FROM_BEYOND": "彼岸咆哮",
    "HUDDLE_UP": "抱团",
    "HUNGRY_FOR_MUSHROOMS": "蘑菇饥渴",
    "HUNTER_KILLER_NORMAL": "猎人杀手",
    "HYPERBEAM": "超能光束",
    "ICE_CREAM": "冰淇淋",
    "ICE_LANCE": "冰之长枪",
    "IGNITION": "引火",
    "ILIKESHINY": "我爱亮闪闪",
    "ILLUSION_POWER": "幻象",
    "IMBALANCED_POWER": "失衡",
    "IMBUED": "注能",
    "IMMORTALITY": "不朽",
    "IMPALE": "扎穿",
    "IMPATIENCE": "急躁",
    "IMPERVIOUS": "岿然不动",
    "IMPROVEMENT_POWER": "改善",
    "INFECTION": "感染",
    "INFERNAL_BLADE": "地狱之刃",
    "INFERNO": "狱火",
    "INFERNO_POWER": "狱火",
    "INFESTED_AUTOMATON": "被寄生的自动机械",
    "INFESTED_POWER": "寄生物",
    "INFESTED_PRISMS_ELITE": "感染棱柱",
    "INFINITE_BLADES": "无尽刀刃",
    "INFINITE_BLADES_POWER": "无尽刀刃",
    "INFLAME": "燃烧",
    "INFUSED_CORE": "注能核心",
    "INJURY": "受伤",
    "INKED": "墨染",
    "INKLETS_NORMAL": "墨宝",
    "INKY": "墨影",
    "INK_BOTTLE": "墨水瓶",
    "INNATE": "固有",
    "INSANITY": "精神错乱",
    "INSTINCT": "本能",
    "INTANGIBLE_POWER": "无实体",
    "INTERCEPT": "拦截",
    "INTERCEPT_POWER": "拦截",
    "INTERNAL_ERROR": "内部错误！",
    "INTIMIDATING_HELMET": "骇人头盔",
    "INVALID_SAVE_POPUP": "失效存档！",
    "INVOKE": "唤起",
    "IRONCLAD": "铁甲战士",
    "IRONCLAD2_EPOCH": "铁甲战士",
    "IRONCLAD3_EPOCH": "赶尽杀绝",
    "IRONCLAD4_EPOCH": "契约",
    "IRONCLAD5_EPOCH": "火光冲天",
    "IRONCLAD6_EPOCH": "熄灭",
    "IRONCLAD7_EPOCH": "我的脸",
    "IRONCLAD_ASCENSION10": "恶魔",
    "IRONCLAD_WIN": "铁甲战士",
    "IRON_CLUB": "铁棒",
    "IRON_WAVE": "铁斩波",
    "ITERATION": "迭代",
    "ITERATION_POWER": "迭代",
    "IVORY_TILE": "象牙麻将牌",
    "I_AM_INVINCIBLE": "所向无敌",
    "JACKPOT": "大奖",
    "JACK_OF_ALL_TRADES": "花样百出",
    "JEWELED_MASK": "宝石面具",
    "JEWELRY_BOX": "珠宝盒",
    "JOIN": "加入",
    "JOIN_FRIENDS_MENU": "选择你要加入的好友",
    "JOSS_PAPER": "金纸",
    "JUGGERNAUT": "势不可当",
    "JUGGERNAUT_POWER": "势不可当",
    "JUGGLING": "杂耍",
    "JUGGLING_POWER": "杂耍",
    "JUNGLE_MAZE_ADVENTURE": "丛林迷宫奇遇",
    "JUZU_BRACELET": "佛珠手链",
    "KACHING": "感谢惠顾！",
    "KAISER_CRAB_BOSS": "帝皇蟹",
    "KALEIDOSCOPE": "万花筒",
    "KIFUDA": "木札",
    "KINGLY_KICK": "王者之踢",
    "KINGLY_PUNCH": "王者之拳",
    "KINGS_COURAGE": "王之勇气",
    "KNIFE_TRAP": "刀刃陷阱",
    "KNIGHTS_ELITE": "骑士团伙",
    "KNOCKDOWN": "击倒",
    "KNOCKDOWN_POWER": "击倒",
    "KNOCKOUT_BLOW": "决胜一击",
    "KNOWLEDGE_DEMON_BOSS": "知识恶魔",
    "KNOW_THY_PLACE": "何人僭越",
    "KUNAI": "苦无",
    "KUSARIGAMA": "锁镰",
    "LAGAVULIN_MATRIARCH_BOSS": "乐加维林族母",
    "LANTERN": "灯笼",
    "LANTERN_KEY": "灯火钥匙",
    "LARGESSE": "慷慨捐助",
    "LARGE_CAPSULE": "巨大扭蛋",
    "LASTING_CANDY": "吃不完的糖",
    "LAVA_LAMP": "熔岩灯",
    "LAVA_ROCK": "熔岩石",
    "LEADERBOARDS": "排行榜",
    "LEADERSHIP_POWER": "领袖气质",
    "LEADING_STRIKE": "先制打击",
    "LEAD_PAPERWEIGHT": "铅制镇纸",
    "LEAFY_POULTICE": "树叶药膏",
    "LEAP": "飞跃",
    "LEES_WAFFLE": "李家华夫饼",
    "LEGEND_ANCIENT": "先古之民",
    "LEGEND_ELITE": "精英",
    "LEGEND_ENEMY": "敌人",
    "LEGEND_MERCHANT": "商人",
    "LEGEND_REST": "休息",
    "LEGEND_TREASURE": "宝箱",
    "LEGEND_UNKNOWN": "未知",
    "LEGION_OF_BONE": "骸骨军团",
    "LEG_SWEEP": "扫腿",
    "LETHALITY": "致死性",
    "LETHALITY_POWER": "致死性",
    "LETTER_OPENER": "开信刀",
    "LEVEL_00": "无进阶",
    "LEVEL_01": "精英蜂拥",
    "LEVEL_02": "旅途劳顿",
    "LEVEL_03": "贫穷",
    "LEVEL_04": "收紧腰带",
    "LEVEL_05": "进阶之灾",
    "LEVEL_06": "通货膨胀",
    "LEVEL_07": "稀缺",
    "LEVEL_08": "强韧敌人",
    "LEVEL_09": "致命敌人",
    "LEVEL_10": "双重Boss",
    "LIFT": "托举",
    "LIGHTNING_ORB": "闪电",
    "LIGHTNING_ROD": "引雷针",
    "LIGHTNING_ROD_POWER": "引雷针",
    "LINKED_REWARDS": "相连奖励",
    "LIQUID_BRONZE": "流动铜液",
    "LIQUID_MEMORIES": "液态记忆",
    "LIVING_FOG_NORMAL": "邪恶气体",
    "LIZARD_TAIL": "蜥蜴尾巴",
    "LOCKED": "锁定",
    "LOOMING_FRUIT": "布质果实",
    "LOOP": "循环",
    "LOOP_POWER": "循环",
    "LORDS_PARASOL": "领主阳伞",
    "LOST_COFFER": "失物盒",
    "LOST_WISP": "迷失鬼火",
    "LOUSE_PROGENITOR_NORMAL": "虱虫之祖",
    "LUCKY_FYSH": "招财异鱼",
    "LUCKY_TONIC": "幸运补剂",
    "LUMINESCE": "冷光",
    "LUMINOUS_CHOIR": "冷光合唱团",
    "LUNAR_BLAST": "月面射击",
    "LUNAR_PASTRY": "月亮糕点",
    "MACHINE_LEARNING": "机器学习",
    "MACHINE_LEARNING_POWER": "机器学习",
    "MAD_SCIENCE": "疯狂科学",
    "MAGIC_BOMB_POWER": "魔法炸弹",
    "MAKE_IT_SO": "如此甚好",
    "MALAISE": "萎靡",
    "MANGLE": "凌虐",
    "MANGO": "芒果",
    "MANIFEST_AUTHORITY": "君权自授",
    "MAP": "地图{Hotkey:choose(None):|（{}）}",
    "MASSIVE_SCROLL": "巨大卷轴",
    "MASTER_OF_STRATEGY": "战略大师",
    "MASTER_PLANNER": "谋划专家",
    "MASTER_PLANNER_POWER": "谋划专家",
    "MAUL": "撕咬",
    "MAWLER_NORMAL": "蛮兽",
    "MAW_BANK": "巨口储蓄罐",
    "MAYHEM": "乱战",
    "MAYHEM_POWER": "乱战",
    "MAZALETHS_GIFT": "马萨雷斯的赠礼",
    "MEAL_TICKET": "餐券",
    "MEAT_CLEAVER": "切肉刀",
    "MEAT_ON_THE_BONE": "带骨肉",
    "MECHA_KNIGHT_ELITE": "机甲骑士",
    "MELANCHOLY": "忧郁",
    "MELANCHOLY_POWER": "忧郁",
    "MEMBERSHIP_CARD": "会员卡",
    "MEMENTO_MORI": "铭记死亡",
    "MERCURY_HOURGLASS": "水银沙漏",
    "METAMORPHOSIS": "羽化",
    "METEOR_SHOWER": "流星雨",
    "METEOR_STRIKE": "陨石打击",
    "METRONOME": "节拍器",
    "MIDAS": "点金手",
    "MIMIC": "拟态",
    "MIND_BLAST": "心灵震慑",
    "MIND_ROT": "心灵腐化",
    "MIND_ROT_POWER": "心灵腐化",
    "MINIATURE_CANNON": "微型大炮",
    "MINIATURE_TENT": "微型帐篷",
    "MINION_DIVE_BOMB": "仆从俯冲",
    "MINION_POWER": "爪牙",
    "MINION_SACRIFICE": "仆从捐躯",
    "MINION_STRIKE": "仆从打击",
    "MINI_REGENT": "迷你储君",
    "MIRAGE": "蜃景",
    "MISERY": "苦难",
    "MOCK_ATTACK_CARD": "模拟攻击牌",
    "MOCK_CLONE_CARDS_ON_PLAY_POWER": "テスト用クローンカード",
    "MOCK_COMBAT_CLEANUP_ORB": "テスト用戦闘クリーンナップ",
    "MOCK_CURSE_CARD": "模拟诅咒牌",
    "MOCK_DISCARD_AND_ADD_SHIVS_POTION": "试用弃牌与小刀药水",
    "MOCK_DO_NOT_SCALE_IN_MULTIPLAYER_POWER": "テスト用マルチプレイスケール停止",
    "MOCK_EVENT_MODEL": "样品事件",
    "MOCK_EXTRA_TURN_POWER": "テスト用追加ターン",
    "MOCK_FREE_CARDS_POWER": "テスト用フリーカード",
    "MOCK_FREE_ENCHANTMENT": "临时免费附魔",
    "MOCK_GAIN_BLOCK_ON_ATTACK_POWER": "テスト用アタックブロック",
    "MOCK_INVINCIBLE_ON_DEATH_POWER": "テスト用死亡時無敵",
    "MOCK_MODIFY_ENERGY_COST_POWER": "テスト用エナジーコスト変更",
    "MOCK_MODIFY_STAR_COST_POWER": "テスト用スターコスト変更",
    "MOCK_NO_UNPLAYABLE_AFFLICTION": "临时不可打出苦痛",
    "MOCK_PHASE_OBSERVER_POWER": "テスト用フェーズ監視",
    "MOCK_POWER_CARD": "模拟能力牌",
    "MOCK_PREVENT_DEATH_POWER": "テスト用死亡回避",
    "MOCK_QUEST_CARD": "临时任务牌",
    "MOCK_REMOVE_DRAWN_CARDS_FROM_COMBAT_POWER": "テスト用戦闘中に引いたカードを削除",
    "MOCK_RESET_COMBAT_ON_SHUFFLE_POWER": "テスト用シャッフル時戦闘リセット",
    "MOCK_REVIVE_POWER": "テスト用復活",
    "MOCK_SCALE_IN_MULTIPLAYER_POWER": "テスト用マルチプレイスケール",
    "MOCK_SELF_DAMAGE_AFFLICTION": "临时自伤苦痛",
    "MOCK_SKILL_CARD": "模拟技能牌",
    "MOCK_STATUS_CARD": "模拟状态牌",
    "MOCK_TEMPORARY_STRENGTH_LOSS": "テスト用一時的筋力低下",
    "MOCK_UNHITTABLE_POWER": "テスト用命中回避",
    "MOCK_USELESS_AFFLICTION": "临时无用苦痛",
    "MODDED": "模组改造",
    "MODDING_POPUP": "是否要加载模组？",
    "MOD_NOT_LOADED_POPUP": "模组尚未完全加载",
    "MOLTEN_EGG": "熔火之蛋",
    "MOLTEN_FIST": "熔融之拳",
    "MOMENTUM": "动量",
    "MOMENTUM_STRIKE": "趁势打击",
    "MONARCHS_GAZE": "王之凝视",
    "MONARCHS_GAZE_POWER": "王之凝视",
    "MONEY_POUCH": "金币",
    "MONOLOGUE": "独白",
    "MONOLOGUE_POWER": "独白",
    "MORPHIC_GROVE": "变形灵林谷",
    "MP_ABANDON": "放弃多人存档",
    "MP_LOAD": "读档多人游戏",
    "MR_STRUGGLES": "抱抱先生",
    "MULTIPLAYER_WARNING_POPUP": "检测到第一局游戏！",
    "MULTI_CAST": "多重释放",
    "MUMMIFIED_HAND": "干瘪之手",
    "MURDER": "谋杀",
    "MURDEROUS": "嗜杀成性",
    "MUSIC_BOX": "音乐盒",
    "MYSTERIOUS_COCOON": "谜之茧",
    "MYSTERIOUS_KNIGHT_EVENT_ENCOUNTER": "神秘骑士",
    "MYSTERY_MACHINE": "寻谜问道",
    "MYSTIC_LIGHTER": "神秘打火机",
    "MYTES_NORMAL": "一群异螨",
    "NECROBINDER": "亡灵契约师",
    "NECROBINDER1_EPOCH": "生与死",
    "NECROBINDER2_EPOCH": "十二人",
    "NECROBINDER3_EPOCH": "徒劳",
    "NECROBINDER4_EPOCH": "生于高塔",
    "NECROBINDER5_EPOCH": "巨人",
    "NECROBINDER6_EPOCH": "覆灭",
    "NECROBINDER7_EPOCH": "杀戮",
    "NECROBINDER_ASCENSION10": "巫妖",
    "NECROBINDER_WIN": "亡灵契约师",
    "NECRO_MASTERY": "亡灵精通",
    "NECRO_MASTERY_POWER": "亡灵精通",
    "NEGATIVE_PULSE": "负能量脉冲",
    "NEMESIS_POWER": "天罚",
    "NEOW": "涅奥",
    "NEOWS_BONES": "涅奥骨骰",
    "NEOWS_FURY": "涅奥之怒",
    "NEOWS_TALISMAN": "涅奥的护符",
    "NEOWS_TORMENT": "涅奥的苦痛",
    "NEOW_EPOCH": "涅奥",
    "NETWORK_PROBLEM_CLIENT": "连接不佳！",
    "NETWORK_PROBLEM_HOST": "连接不佳！",
    "NEUROSURGE": "精神过载",
    "NEUROSURGE_POWER": "精神过载",
    "NEUTRALIZE": "中和",
    "NEUTRON_AEGIS": "中子护盾",
    "NEW_LEAF": "新叶",
    "NIBBITS_NORMAL": "两只小啃兽",
    "NIBBITS_WEAK": "一只落单的小啃兽",
    "NIGHTMARE": "夜魇",
    "NIGHTMARE_POWER": "夜魇",
    "NIGHT_TERRORS": "暗夜惊魂",
    "NIMBLE": "灵巧",
    "NINJA_SCROLL": "忍术卷轴",
    "NONUPEIPE": "诺奴佩普",
    "NORMALITY": "凡庸",
    "NOSTALGIA": "怀旧",
    "NOSTALGIA_POWER": "怀旧",
    "NOT_YET": "时候未到",
    "NOXIOUS_FUMES": "毒雾",
    "NOXIOUS_FUMES_POWER": "毒雾",
    "NO_BLOCK_POWER": "不可格挡",
    "NO_DRAW_POWER": "不可抽牌",
    "NO_ENERGY_GAIN_POWER": "无法获得能量",
    "NO_ESCAPE": "无处可逃",
    "NO_ESCAPE_POWER": "无处可逃",
    "NO_RELIC_WIN": "身无杂物",
    "NULL": "空值",
    "NUNCHAKU": "双截棍",
    "NUTRITIOUS_OYSTER": "营养牡蛎",
    "NUTRITIOUS_SOUP": "营养汤",
    "OBLIVION": "湮灭",
    "OBLIVION_POWER": "湮灭",
    "ODDLY_SMOOTH_STONE": "意外光滑的石头",
    "OFFERING": "祭品",
    "OFF_BY_ONE": "一线之差",
    "OLD_COIN": "古钱币",
    "OMNISLICE": "万向斩",
    "ONE_TWO_PUNCH": "连环拳",
    "ONE_TWO_PUNCH_POWER": "连环拳",
    "OPEN_PROFILE_SCREEN": "存档{Id}",
    "ORANGE_DOUGH": "橙色团块",
    "ORBIT": "环绕轨道",
    "ORBIT_POWER": "环绕轨道",
    "ORB_GENERATOR": "充能球生成器",
    "ORICHALCUM": "奥利哈钢",
    "ORNAMENTAL_FAN": "精致折扇",
    "OROBAS": "欧洛巴斯",
    "OROBAS_EPOCH": "欧洛巴斯",
    "OROBIC_ACID": "欧洛巴斯之酸",
    "ORRERY": "星系仪",
    "OUTBREAK": "毒性爆发",
    "OUTBREAK_POWER": "毒性爆发",
    "OUTMANEUVER": "抢占先机",
    "OVERCLOCK": "超频",
    "OVERGROWTH": "密林",
    "OVERGROWTH_CRAWLERS": "密林爬虫",
    "OVERGROWTH_WILDLIFE": "密林野生生物",
    "OVICOPTER_NORMAL": "直飞产卵虫",
    "OWL_MAGISTRATE_NORMAL": "猫头鹰法官",
    "PACTS_END": "契约终结",
    "PAEL": "佩尔",
    "PAELS_BLOOD": "佩尔之血",
    "PAELS_CLAW": "佩尔之爪",
    "PAELS_EYE": "佩尔之眼",
    "PAELS_FLESH": "佩尔之肉",
    "PAELS_GROWTH": "佩尔的增生组织",
    "PAELS_HORN": "佩尔之角",
    "PAELS_LEGION": "佩尔的士兵",
    "PAELS_STRIKE": "佩尔的打击",
    "PAELS_TEARS": "佩尔之泪",
    "PAELS_TOOTH": "佩尔之牙",
    "PAELS_WING": "佩尔之翼",
    "PAGESTORM": "书页风暴",
    "PAGESTORM_POWER": "书页风暴",
    "PAINFUL_STABS_POWER": "疼痛戳刺",
    "PALE_BLUE_DOT": "暗淡蓝点",
    "PALE_BLUE_DOT_POWER": "暗淡蓝点",
    "PANACHE": "神气制胜",
    "PANACHE_POWER": "神气制胜",
    "PANDORAS_BOX": "潘多拉魔盒",
    "PANIC_BUTTON": "应急按钮",
    "PANTOGRAPH": "缩放仪",
    "PAPER_CUTS_POWER": "纸伤难愈",
    "PAPER_KRANE": "纸鹤",
    "PAPER_PHROG": "纸蛙",
    "PARRY": "招架",
    "PARRYING_SHIELD": "招架盾",
    "PARRY_POWER": "招架",
    "PARSE": "领会",
    "PARTICLE_WALL": "粒子墙",
    "PATTER": "星星点点",
    "PEAR": "梨子",
    "PECK": "啄击",
    "PENDULUM": "摆动球",
    "PEN_NIB": "钢笔尖",
    "PERFECTED_STRIKE": "完美打击",
    "PERFECT_FIT": "完美契合",
    "PERMAFROST": "永冻冰晶",
    "PERSONAL_HIVE_POWER": "人体蜂房",
    "PETRIFIED_TOAD": "石化蟾蜍",
    "PHANTASMAL_GARDENERS_ELITE": "花园幽灵鳗",
    "PHANTOM_BLADES": "幻影之刃",
    "PHANTOM_BLADES_POWER": "幻影之刃",
    "PHIAL_HOLSTER": "药瓶皮套",
    "PHILOSOPHERS_STONE": "贤者之石",
    "PHOTON_CUT": "光子切割",
    "PHROG_PARASITE_ELITE": "异蛙寄生虫",
    "PHYLACTERY_UNBOUND": "无界命匣",
    "PIERCING_WAIL": "尖啸",
    "PILLAGE": "劫掠",
    "PILLAR_OF_CREATION": "创世之柱",
    "PILLAR_OF_CREATION_POWER": "创世之柱",
    "PINPOINT": "精密瞄准",
    "PITY": "怜悯",
    "PLANISPHERE": "活动星图",
    "PLASMA_ORB": "等离子",
    "PLATING_POWER": "覆甲",
    "PLAY20_CARDS_SINGLE_TURN": "打牌真开心啊",
    "PLOW_POWER": "横冲直撞",
    "POCKETWATCH": "怀表",
    "POISONED_STAB": "带毒刺击",
    "POISON_POTION": "毒药水",
    "POISON_POWER": "中毒",
    "POKE": "戳击",
    "POLLINOUS_CORE": "花粉核心",
    "POMANDER": "橙型香盒",
    "POMMEL_STRIKE": "剑柄打击",
    "POOR_SLEEP": "睡眠不佳",
    "POSSESS_SPEED_POWER": "抢夺速度",
    "POSSESS_STRENGTH_POWER": "抢夺力量",
    "POTION1_EPOCH": "开放",
    "POTION2_EPOCH": "宁静",
    "POTION_BELT": "药水腰带",
    "POTION_COURIER": "药水快递员",
    "POTION_OF_BINDING": "缚魂药水",
    "POTION_OF_CAPACITY": "扩容药水",
    "POTION_OF_DOOM": "灾厄药水",
    "POTION_SHAPED_ROCK": "药水形状的石头",
    "POTION_SLOT": "药水栏位",
    "POT_OF_GHOULS": "尸鬼瓮",
    "POUNCE": "猛扑",
    "POWDERED_DEMISE": "消亡粉末",
    "POWER_CELL": "能量电池",
    "POWER_POTION": "能力药水",
    "PRAYER_WHEEL": "转经轮",
    "PRECARIOUS_SHEARS": "松动羊毛剪",
    "PRECISE_CUT": "精确切击",
    "PRECISE_SCISSORS": "精准剪刀",
    "PREDATOR": "猎杀者",
    "PREPARED": "早有准备",
    "PREP_TIME": "准备时间",
    "PREP_TIME_POWER": "准备时间",
    "PRESERVED_FOG": "腌制活雾",
    "PRIMAL_FORCE": "原始力量",
    "PRISMATIC_GEM": "棱彩宝石",
    "PROCEED": "继续",
    "PRODUCTION": "生产制造",
    "PROLONG": "延伸",
    "PROPHESIZE": "预言",
    "PROTECTOR": "守护者",
    "PROWESS": "非凡技艺",
    "PULL_AGGRO": "吸引仇恨",
    "PULL_FROM_BELOW": "亡魂牵引",
    "PUMPKIN_CANDLE": "南瓜蜡烛",
    "PUNCH_CONSTRUCT_NORMAL": "拳击构装体",
    "PUNCH_DAGGER": "拳刃",
    "PUNCH_OFF": "重拳出击",
    "PUNCH_OFF_EVENT_ENCOUNTER": "拳击构装体",
    "PURITY": "净化",
    "PUTREFY": "腐败",
    "PYRE": "薪火之源",
    "PYRE_POWER": "薪火之源",
    "QUADCAST": "四重释放",
    "QUASAR": "类星体",
    "QUEEN_BOSS": "女王",
    "QUICK_SLASH": "快斩",
    "RADIANCE_POWER": "明耀",
    "RADIANT_PEARL": "发光珍珠",
    "RADIANT_TINCTURE": "明耀酊剂",
    "RADIATE": "辐射",
    "RAGE": "狂怒",
    "RAGE_POWER": "狂怒",
    "RAINBOW": "彩虹",
    "RAINBOW_RING": "彩虹戒指",
    "RALLY": "集结",
    "RAMPAGE": "暴走",
    "RAMPART_POWER": "盾墙",
    "RANDOM_CHARACTER": "随机",
    "RANWID_THE_ELDER": "长者兰伟德",
    "RATTLE": "猛晃",
    "RAVENOUS_POWER": "饥饿",
    "RAZOR_TOOTH": "剃刀牙",
    "READ_THE_BONES_POWER": "识骨知数",
    "REANIMATE": "死者苏生",
    "REAP": "收割",
    "REAPER_FORM": "死神形态",
    "REAPER_FORM_POWER": "死神形态",
    "REATTACH_POWER": "接续",
    "REAVE": "剥夺",
    "REBOOT": "重启",
    "REBOUND": "弹回",
    "REBOUND_POWER": "弹回",
    "RED_MASK": "红面具",
    "RED_SKULL": "红头骨",
    "RED_VINE_TEA": "赤藤茶",
    "REFINE_BLADE": "淬炼刀刃",
    "REFLECT": "倒映",
    "REFLECTIONS": "镜中倒影  影倒中镜",
    "REFLECTIVE_FORTRESS_POWER": "逆反要塞",
    "REFLECT_POWER": "倒映",
    "REFLEX": "本能反应",
    "REFRACT": "折射",
    "REGALITE": "君王矿石",
    "REGAL_PILLOW": "皇家枕头",
    "REGENT": "储君",
    "REGENT1_EPOCH": "异常行星",
    "REGENT2_EPOCH": "我已到来",
    "REGENT3_EPOCH": "我们需要一个英雄！",
    "REGENT4_EPOCH": "大战略",
    "REGENT5_EPOCH": "朋友",
    "REGENT6_EPOCH": "不满",
    "REGENT7_EPOCH": "小小的王",
    "REGENT_ASCENSION10": "帝王",
    "REGENT_WIN": "储君",
    "REGEN_POTION": "再生药水",
    "REGEN_POWER": "再生",
    "REGRET": "悔恨",
    "RELAX": "放松",
    "RELIC1_EPOCH": "涂鸦",
    "RELIC2_EPOCH": "先古之民",
    "RELIC3_EPOCH": "文明",
    "RELIC4_EPOCH": "后果",
    "RELIC5_EPOCH": "醒来",
    "RELIC_TRADER": "遗物交换商",
    "REND": "撕碎",
    "REPLAY_DYNAMIC": "重放",
    "REPLAY_STATIC": "重放",
    "REPTILE_TRINKET": "爬行动物饰品",
    "RESERVES": "储备",
    "RESERVES_POWER": "储备",
    "RESONANCE": "共鸣",
    "RESTFUL": "休养生息",
    "RESTLESS": "战斗不息",
    "RESTLESSNESS": "心神不宁",
    "RESTORE_DEXTERITY": "敏捷恢复",
    "RESTORE_FOCUS": "集中恢复",
    "RETAIN": "保留",
    "RETAIN_HAND_POWER": "保留手牌",
    "RICOCHET": "连续反弹",
    "RIGHT_HAND_HAND": "得力助手",
    "RINGING": "昏眩",
    "RINGING_POWER": "昏眩",
    "RINGING_TRIANGLE": "三角铃鼓",
    "RING_OF_THE_DRAKE": "长蛇戒指",
    "RING_OF_THE_SNAKE": "蛇之戒指",
    "RIPPLE_BASIN": "波纹水盆",
    "RIP_AND_TEAR": "狂乱撕扯",
    "RITUAL_POWER": "仪式",
    "ROCKET_PUNCH": "火箭飞拳",
    "ROLLING_BOULDER": "滚石",
    "ROLLING_BOULDER_POWER": "滚石",
    "ROOM_ANCIENT": "先古之民",
    "ROOM_ELITE": "精英",
    "ROOM_ENEMY": "敌人",
    "ROOM_EVENT": "事件",
    "ROOM_FULL_OF_CHEESE": "满屋芝士",
    "ROOM_MAP": "前厅",
    "ROOM_MERCHANT": "商店",
    "ROOM_REST": "休息处",
    "ROOM_TREASURE": "宝箱房",
    "ROOM_UNKNOWN_ELITE": "未知 - 精英",
    "ROOM_UNKNOWN_ENEMY": "未知 - 敌人",
    "ROOM_UNKNOWN_EVENT": "未知 - 事件",
    "ROOM_UNKNOWN_MERCHANT": "未知 - 商人",
    "ROOM_UNKNOWN_TREASURE": "未知 - 财宝",
    "ROUND_TEA_PARTY": "圆桌茶会",
    "ROYALLY_APPROVED": "王室认证",
    "ROYALTIES": "王国资产",
    "ROYALTIES_POWER": "王国资产",
    "ROYAL_GAMBLE": "胜券在王",
    "ROYAL_POISON": "王室猛毒",
    "ROYAL_STAMP": "王室印章",
    "RUBY_RAIDERS_NORMAL": "红宝石劫掠者",
    "RUINED_HELMET": "损毁头盔",
    "RUNIC_CAPACITOR": "符文电容器",
    "RUNIC_PYRAMID": "符文金字塔",
    "RUN_HISTORY": "历史记录",
    "RUPTURE": "撕裂",
    "RUPTURE_POWER": "撕裂",
    "SACRIFICE": "牺牲",
    "SAI": "钗",
    "SALVO": "箭雨",
    "SANDPIT_POWER": "沙坑",
    "SAND_CASTLE": "沙堡",
    "SAPPHIRE_SEED": "蓝宝石种子",
    "SCARE": "恫吓",
    "SCAVENGE": "内存清理",
    "SCOURGE": "鞭打",
    "SCRAPE": "刮削",
    "SCRAWL": "潦草急就",
    "SCREAMING_FLAGON": "尖叫酒壶",
    "SCROLLS_OF_BITING_NORMAL": "许多咬人卷轴",
    "SCROLLS_OF_BITING_WEAK": "咬人卷轴",
    "SCROLL_BOXES": "卷轴箱",
    "SCULPTING_STRIKE": "雕琢打击",
    "SEALED_DECK": "现开套牌",
    "SEAL_OF_GOLD": "黄金印",
    "SEANCE": "降灵",
    "SEAPUNK_NORMAL": "暗港野生动物",
    "SEAPUNK_WEAK": "海洋混混",
    "SEA_GLASS": "海玻璃",
    "SECOND_WIND": "重振精神",
    "SECRET_TECHNIQUE": "秘密技法",
    "SECRET_WEAPON": "秘密武器",
    "SEEKER_STRIKE": "探寻打击",
    "SEEKING_EDGE": "追踪之刃",
    "SEEKING_EDGE_POWER": "追踪之刃",
    "SELF_FORMING_CLAY": "自成型黏土",
    "SELF_FORMING_CLAY_POWER": "自成型黏土",
    "SELF_HELP_BOOK": "自助指南",
    "SENTRY_MODE": "哨卫模式",
    "SENTRY_MODE_POWER": "哨卫模式",
    "SERE_TALON": "原初之爪",
    "SERPENT_FORM": "群蛇形态",
    "SERPENT_FORM_POWER": "群蛇形态",
    "SETTINGS": "设置（ESC）",
    "SETUP_STRIKE": "预备打击",
    "SEVEN_STARS": "七星",
    "SEVERANCE": "切断",
    "SEWER_CLAM_NORMAL": "下水道蚌",
    "SHACKLING_POTION": "镣铐药水",
    "SHADOWMELD": "融入暗影",
    "SHADOWMELD_POWER": "融入暗影",
    "SHADOW_SHIELD": "暗影之盾",
    "SHADOW_STEP": "暗影步",
    "SHADOW_STEP_POWER": "暗影步",
    "SHAME": "羞耻",
    "SHARED_FATE": "命运同担",
    "SHARP": "锋利",
    "SHARP_EDGE": "锋利边缘",
    "SHARP_EDGE_POWER": "锋利边缘",
    "SHATTER": "打碎",
    "SHINING_STRIKE": "明耀打击",
    "SHIP_IN_A_BOTTLE": "瓶中船",
    "SHIV": "小刀",
    "SHOCKWAVE": "震荡波",
    "SHOVEL": "铲子",
    "SHRIEK_POWER": "尖叫",
    "SHRINKER_BEETLE_WEAK": "缩小甲虫",
    "SHRINK_POWER": "缩小",
    "SHROUD": "厄运之衣",
    "SHROUD_POWER": "厄运之衣",
    "SHRUG_IT_OFF": "耸肩无视",
    "SHURIKEN": "手里剑",
    "SIC_EM": "紧追不放",
    "SIC_EM_POWER": "紧追不放",
    "SIGNAL_BOOST": "信号增强",
    "SIGNAL_BOOST_POWER": "信号增强",
    "SIGNET_RING": "图章戒指",
    "SILENT": "静默猎手",
    "SILENT1_EPOCH": "迷雾压境",
    "SILENT2_EPOCH": "更大的猎物",
    "SILENT3_EPOCH": "疾病",
    "SILENT4_EPOCH": "战利品",
    "SILENT5_EPOCH": "下毒者",
    "SILENT6_EPOCH": "天罚",
    "SILENT7_EPOCH": "汤",
    "SILENT_ASCENSION10": "幽魂",
    "SILENT_WIN": "静默猎手",
    "SILKEN_TRESS": "华美发束",
    "SILVER_CRUCIBLE": "白银熔炉",
    "SINGLE_PLAYER_CUSTOM": "自定义",
    "SINGLE_PLAYER_DAILY_RUN": "每日挑战",
    "SINGLE_PLAYER_STANDARD": "标准",
    "SKEWER": "串刺",
    "SKILL_POTION": "技能药水",
    "SKIM": "快速检索",
    "SKITTISH_POWER": "胆小",
    "SKULKING_COLONY_ELITE": "鬼祟珊瑚群",
    "SLEEP": "沉睡",
    "SLEIGHT_OF_FLESH": "血肉戏法",
    "SLEIGHT_OF_FLESH_POWER": "血肉戏法",
    "SLICE": "切割",
    "SLIMED": "黏液",
    "SLIMED_BERSERKER_NORMAL": "史莱姆狂战士",
    "SLIMES_NORMAL": "一大群史莱姆",
    "SLIMES_WEAK": "一群史莱姆",
    "SLING_OF_COURAGE": "勇气投石索",
    "SLIPPERY_BRIDGE": "滑脚木桥",
    "SLIPPERY_POWER": "滑溜",
    "SLITHER": "蛇行",
    "SLITHERING_STRANGLER_NORMAL": "扼杀者与伙伴",
    "SLOTH": "懒惰",
    "SLOTH_POWER": "懒惰",
    "SLOW_POWER": "缓慢",
    "SLUDGE_SPINNER_WEAK": "淤泥旋螺",
    "SLUMBERING_BEETLE_NORMAL": "熟睡派对",
    "SLUMBERING_ESSENCE": "沉眠精华",
    "SLUMBER_POWER": "熟睡",
    "SLY": "奇巧",
    "SMALL_CAPSULE": "小型扭蛋",
    "SMOG": "烟雾",
    "SMOGGY_POWER": "烟雾弥漫",
    "SMOKESTACK": "烟囱",
    "SMOKESTACK_POWER": "烟囱",
    "SNAKEBITE": "蛇咬",
    "SNAP": "响指",
    "SNAPPING_JAXFRUIT_NORMAL": "密林植物",
    "SNEAKY": "鬼祟",
    "SNEAKY_POWER": "鬼祟",
    "SNECKO_EYE": "异蛇之眼",
    "SNECKO_OIL": "异蛇之油",
    "SNECKO_SKULL": "异蛇头骨",
    "SOAR_POWER": "翱翔",
    "SOLAR_STRIKE": "太阳打击",
    "SOLDIERS_STEW": "士兵炖汤",
    "SOOT": "煤灰",
    "SOUL": "灵魂",
    "SOULS_POWER": "灵魂之力",
    "SOUL_FYSH_BOSS": "灵魂异鱼",
    "SOUL_NEXUS_ELITE": "灵魂枢纽",
    "SOUL_STORM": "灵魂风暴",
    "SOUL_WITHER": "灵魂枯萎",
    "SOVEREIGN_BLADE": "君王之剑",
    "SOW": "播种",
    "SOWN": "播种",
    "SOW_POWER": "播种",
    "SOZU": "添水",
    "SPARKLING_ROUGE": "闪亮口红",
    "SPECIALIZED": "专精",
    "SPECTRUM_SHIFT": "光谱偏移",
    "SPECTRUM_SHIFT_POWER": "光谱偏移",
    "SPEEDSTER": "速行者",
    "SPEEDSTER_POWER": "速行者",
    "SPEED_POTION": "速度药水",
    "SPIKED": "扎刺",
    "SPIKED_GAUNTLETS": "带刺手甲",
    "SPINNER": "旋转工艺",
    "SPINNER_POWER": "旋转工艺",
    "SPINY_TOAD_NORMAL": "棘刺蟾蜍",
    "SPIRAL": "涡旋",
    "SPIRALING_WHIRLPOOL": "螺旋漩涡",
    "SPIRIT_GRAFTER": "灵魂嫁接者",
    "SPIRIT_OF_ASH": "灰烬之灵",
    "SPIRIT_OF_ASH_POWER": "灰烬之灵",
    "SPITE": "怨恨",
    "SPLASH": "飞溅",
    "SPOILS_MAP": "藏宝图",
    "SPOILS_OF_BATTLE": "战利品",
    "SPORE_MIND": "孢子心灵",
    "SPUR": "增生",
    "SQUASH": "压扁",
    "SQUEEZE": "榨取",
    "STABLE_SERUM": "稳定血清",
    "STACK": "堆栈",
    "STAMPEDE": "惊逃",
    "STAMPEDE_POWER": "惊逃",
    "STANDARD": "标准模式",
    "STANDARD_MP": "标准模式",
    "STARDUST": "星尘",
    "STARTUP_ERROR": "致命错误！",
    "STAR_BLAST": "星光爆射",
    "STAR_COUNT": "辉星",
    "STAR_NEXT_TURN_POWER": "下回合辉星",
    "STAR_POTION": "星星药水",
    "STAR_SHIELD": "星之盾",
    "STATISTICS": "角色数据",
    "STATUS": "策略",
    "STEADY": "稳定",
    "STEAM_ERUPTION_POWER": "蒸汽喷发",
    "STEAM_INIT_ERROR": "Steam错误！",
    "STEAM_STOPPED_ERROR": "Steam错误！",
    "STOCK_POWER": "库存",
    "STOKE": "添柴",
    "STOMP": "踩踏",
    "STONE_ARMOR": "岩石铠甲",
    "STONE_CALENDAR": "历石",
    "STONE_CRACKER": "碎石钻",
    "STONE_HUMIDIFIER": "石炉加湿器",
    "STONE_OF_ALL_TIME": "永恒之石",
    "STORM": "雷暴",
    "STORM_OF_STEEL": "钢铁风暴",
    "STORM_POWER": "雷暴",
    "STORYBOOK": "故事书",
    "STRANGLE": "紧勒",
    "STRANGLE_POWER": "紧勒",
    "STRATAGEM": "计策",
    "STRATAGEM_POWER": "计策",
    "STRAWBERRY": "草莓",
    "STRENGTH_POTION": "力量药水",
    "STRENGTH_POWER": "力量",
    "STRIKE_DEFECT": "打击",
    "STRIKE_DUMMY": "打击木偶",
    "STRIKE_IRONCLAD": "打击",
    "STRIKE_NECROBINDER": "打击",
    "STRIKE_REGENT": "打击",
    "STRIKE_SILENT": "打击",
    "STUN": "击晕",
    "STURDY_CLAMP": "坚固钳子",
    "SUBROUTINE": "子程序",
    "SUBROUTINE_POWER": "子程序",
    "SUCKER_PUNCH": "突然一拳",
    "SUCK_POWER": "吮吸",
    "SUMMON": "召唤",
    "SUMMON_DYNAMIC": "召唤{Summon}",
    "SUMMON_FORTH": "征召上前",
    "SUMMON_NEXT_TURN_POWER": "下回合召唤",
    "SUMMON_STATIC": "召唤",
    "SUNDER": "分离",
    "SUNKEN_STATUE": "沉没雕像",
    "SUNKEN_TREASURY": "淹水金库",
    "SUPERCRITICAL": "超临界态",
    "SUPERMASSIVE": "超质量体",
    "SUPPRESS": "压制",
    "SURPRISE_POWER": "意外",
    "SURROUNDED_POWER": "遭到包围",
    "SURVIVOR": "生存者",
    "SWEEPING_BEAM": "扫荡射线",
    "SWEEPING_GAZE": "扫荡凝视",
    "SWIFT": "迅速",
    "SWIFT_POTION": "迅捷药水",
    "SWIFT_STRIKE": "迅捷打击",
    "SWIPE_POWER": "顺走",
    "SWORD_BOOMERANG": "飞剑回旋镖",
    "SWORD_OF_JADE": "玉之剑",
    "SWORD_OF_STONE": "石之剑",
    "SWORD_SAGE": "剑圣",
    "SWORD_SAGE_POWER": "剑圣",
    "SYMBIOTE": "共生体",
    "SYMBIOTIC_VIRUS": "共生病毒",
    "SYNCHRONIZE": "同步",
    "SYNTHESIS": "人工合成",
    "TABLET": "真理石板",
    "TABLET_OF_TRUTH": "真理石板",
    "TACTICIAN": "战术大师",
    "TAG_TEAM": "双打组合",
    "TAG_TEAM_POWER": "多人组队",
    "TAINTED": "污染",
    "TAINTED_POWER": "污染",
    "TANGLED_POWER": "缠结",
    "TANK": "肉盾",
    "TANK_POWER": "肉盾",
    "TANX": "坦克斯",
    "TANXS_WHISTLE": "坦克斯的哨子",
    "TAUNT": "挑衅",
    "TAUNT_POWER": "挑衅",
    "TEAM_PLAYER": "团队型选手",
    "TEAR_ASUNDER": "扯碎",
    "TEA_MASTER": "茶艺大师",
    "TEA_OF_DISCOURTESY": "无礼之茶",
    "TEMPEST": "暴风雨",
    "TENDER_POWER": "柔嫩",
    "TERMINAL": "身患绝症",
    "TERRAFORMING": "地形改造",
    "TERRITORIAL_POWER": "领地意识",
    "TERROR_EEL_ELITE": "骇鳗",
    "TESLA_COIL": "特斯拉线圈",
    "TEST_SUBJECT_BOSS": "实验体",
    "TEZCATARA": "特兹卡塔拉",
    "TEZCATARAS_BLIGHT": "特兹卡塔拉的荒疫",
    "TEZCATARAS_EMBER": "特兹卡塔拉的余烬",
    "TEZCATARAS_MIGHT": "特兹卡塔拉之力",
    "THE_ABACUS": "算盘",
    "THE_ARCHITECT": "建筑师",
    "THE_ARCHITECT_EVENT_ENCOUNTER": "建筑师",
    "THE_BOMB": "炸弹",
    "THE_BOMB_POWER": "炸弹",
    "THE_BOOT": "发条靴",
    "THE_COURIER": "送货员",
    "THE_FUTURE_OF_POTIONS": "药水的未来？",
    "THE_GAMBIT": "孤注一掷",
    "THE_GAMBIT_POWER": "孤注一掷",
    "THE_HUNT": "狩猎",
    "THE_HUNT_POWER": "狩猎",
    "THE_INSATIABLE_BOSS": "无厌沙虫",
    "THE_KIN_BOSS": "同族小队",
    "THE_LANTERN_KEY": "灯火钥匙",
    "THE_LEGENDS_WERE_TRUE": "传说是真的",
    "THE_LOST_AND_FORGOTTEN_NORMAL": "失落与遗忘之物",
    "THE_OBSCURA_NORMAL": "胧光怪",
    "THE_SCYTHE": "巨镰",
    "THE_SEALED_THRONE": "封印王座",
    "THE_SEALED_THRONE_POWER": "封印王座",
    "THE_SMITH": "铸剑者",
    "THIEVERY_POWER": "偷窃",
    "THIEVING_HOPPER_WEAK": "偷窃草蜢",
    "THINKING_AHEAD": "深谋远虑",
    "THIS_OR_THAT": "这个还是那个？",
    "THORNS_POWER": "荆棘",
    "THRASH": "痛殴",
    "THROWING_AXE": "投斧",
    "THRUMMING_HATCHET": "无休手斧",
    "THUNDER": "雷霆",
    "THUNDERCLAP": "闪电霹雳",
    "THUNDER_POWER": "雷霆",
    "TIMEOUT_OVERLAY": "连接已中断",
    "TIMES_UP": "大限已至",
    "TINGSHA": "铜钹",
    "TINKER_TIME": "打造时间",
    "TINY_MAILBOX": "小邮箱",
    "TOADPOLES_WEAK": "蟾蜍蝌蚪",
    "TOASTY_MITTENS": "烘焙手套",
    "TOOLBOX": "工具箱",
    "TOOLS_OF_THE_TRADE": "必备工具",
    "TOOLS_OF_THE_TRADE_POWER": "必备工具",
    "TORIC_TOUGHNESS": "坚韧之环",
    "TORIC_TOUGHNESS_POWER": "坚韧之环",
    "TORN": "破裂",
    "TOUCH_OF_INSANITY": "癫狂之触",
    "TOUCH_OF_OROBAS": "欧洛巴斯之触",
    "TOUGH_BANDAGES": "结实绷带",
    "TOXIC": "毒素",
    "TOXIC_EGG": "毒素之蛋",
    "TOY_BOX": "玩具盒",
    "TRACKING": "跟踪",
    "TRACKING_POWER": "跟踪",
    "TRAINING_STRIKE": "训练打击",
    "TRANSFIGURE": "重构",
    "TRANSFORM": "变化",
    "TRASH_HEAP": "垃圾堆",
    "TRASH_TO_TREASURE": "化废为宝",
    "TRASH_TO_TREASURE_POWER": "化废为宝",
    "TREMBLE": "战栗",
    "TRIAL": "审判",
    "TRI_BOOMERANG": "三刃回旋镖",
    "TRUE_GRIT": "坚毅",
    "TUNGSTEN_ROD": "钨合金棍",
    "TUNING_FORK": "音叉",
    "TUNNELER_NORMAL": "地道组合",
    "TUNNELER_WEAK": "地道虫",
    "TURBO": "内核加速",
    "TURRET_OPERATOR_WEAK": "高塔炮手",
    "TWIN_STRIKE": "双重打击",
    "TWISTED_FUNNEL": "扭曲漏斗",
    "TWO_TAILED_RATS_NORMAL": "双尾鼠",
    "TYRANNY": "暴政",
    "TYRANNY_POWER": "暴政",
    "ULTIMATE_DEFEND": "究极防御",
    "ULTIMATE_STRIKE": "究极打击",
    "UNCEASING_TOP": "不休陀螺",
    "UNDEATH": "不死",
    "UNDERDOCKS": "暗港",
    "UNDERDOCKS_EPOCH": "暗港",
    "UNDYING_SIGIL": "不死符文",
    "UNKNOWN": "未知",
    "UNLEASH": "出击",
    "UNMOVABLE": "坚定不移",
    "UNMOVABLE_POWER": "不动",
    "UNPLAYABLE": "不能被打出",
    "UNRELENTING": "无情猛攻",
    "UNREST_SITE": "无休之处",
    "UNSETTLING_LAMP": "不安油灯",
    "UNSTABLE": "不稳定",
    "UNSTEADY": "脚下不稳",
    "UNTOUCHABLE": "触不可及",
    "UPPERCUT": "上勾拳",
    "UPROAR": "骚动",
    "UP_MY_SLEEVE": "袖里乾坤",
    "VAJRA": "金刚杵",
    "VAKUU": "瓦库",
    "VAMBRACE": "臂甲",
    "VANTOM_BOSS": "墨影幻灵",
    "VEILPIERCER": "刺破帷幕",
    "VEILPIERCER_POWER": "刺破帷幕",
    "VELVET_CHOKER": "天鹅绒颈圈",
    "VENERABLE_TEA_SET": "古茶具套装",
    "VENERATE": "崇拜",
    "VERY_HOT_COCOA": "烫嘴可可",
    "VETERAN": "老兵",
    "VEXING_PUZZLEBOX": "烦人机关盒",
    "VICIOUS": "凶恶",
    "VICIOUS_POWER": "凶恶",
    "VIGOROUS": "活力",
    "VIGOR_POWER": "活力",
    "VINE_BRACELET": "藤蔓手镯",
    "VINE_SHAMBLER_NORMAL": "藤蔓蹒跚者",
    "VINTAGE": "上等好货",
    "VISIONS_OF_GRANDEUR": "伟大愿景",
    "VISUAL_ONLY": "仅视觉",
    "VITAL_SPARK_POWER": "活力火花",
    "VITRUVIAN_MINION": "维特鲁威仆从",
    "VOID": "虚空",
    "VOID_FORM": "虚空形态",
    "VOID_FORM_POWER": "虚空形态",
    "VOLLEY": "连射",
    "VOLTAIC": "电流相生",
    "VULNERABLE_POTION": "易伤药水",
    "VULNERABLE_POWER": "易伤",
    "WARP_SPACE": "折跃空间",
    "WAR_HAMMER": "战锤",
    "WAR_HISTORIAN_REPY": "战史学家 付袭",
    "WAR_PAINT": "战纹涂料",
    "WASTE_AWAY": "衰朽",
    "WASTE_AWAY_POWER": "衰朽",
    "WATERFALL_GIANT_BOSS": "瀑布巨兽",
    "WATERLOGGED_SCRIPTORIUM": "水漫缮写室",
    "WEAK_POTION": "虚弱药水",
    "WEAK_POWER": "虚弱",
    "WELCOME_TO_WONGOS": "欢迎来到旺购百货",
    "WELLSPRING": "泉水",
    "WELL_LAID_PLANS": "计划妥当",
    "WELL_LAID_PLANS_POWER": "计划妥当",
    "WHETSTONE": "磨刀石",
    "WHIRLWIND": "旋风斩",
    "WHISPERING_EARRING": "低语耳环",
    "WHISPERING_HOLLOW": "低语空谷",
    "WHISTLE": "吹哨",
    "WHITE_BEAST_STATUE": "白兽雕像",
    "WHITE_NOISE": "白噪声",
    "WHITE_STAR": "白星",
    "WHOMPER": "势大力沉",
    "WINGED_BOOTS": "羽翼之靴",
    "WINGED_JUMP_POWER": "翱翔之跃",
    "WING_CHARM": "羽翼护符",
    "WISH": "许愿",
    "WISP": "鬼火",
    "WITHER": "凋萎",
    "WITHERING_PRESENCE_POWER": "凋萎存在",
    "WONGOS_MYSTERY_TICKET": "旺购神秘券",
    "WONGO_CUSTOMER_APPRECIATION_BADGE": "旺购客户感恩徽章",
    "WOOD_CARVINGS": "木雕",
    "WOUND": "伤口",
    "WRAITH_FORM": "幽魂形态",
    "WRAITH_FORM_POWER": "幽魂形态",
    "WRITHE": "苦恼",
    "WROUGHT_IN_WAR": "战火铸就",
    "YUMMY_COOKIE": "美味饼干",
    "ZAP": "电击",
    "ZEN_WEAVER": "修禅织网者",
}


def camel_to_snake(card_id: str) -> str:
    """Convert CamelCase card ID to UPPER_SNAKE_CASE.

    Examples:
        BloodWall -> BLOOD_WALL
        DefendIronclad -> DEFEND_IRONCLAD
        ExpectAFight -> EXPECT_A_FIGHT
        BASH -> BASH
        STRIKE_IRONCLAD -> STRIKE_IRONCLAD (already snake_case)
    """
    # If already has underscores, just uppercase
    if '_' in card_id:
        return card_id.upper()
    # Insert underscore at word boundaries:
    # - Before uppercase letter preceded by lowercase (camelCase → camel_Case)
    # - Before uppercase letter followed by lowercase and preceded by uppercase (ABc → A_Bc)
    result = []
    for i, c in enumerate(card_id):
        if i > 0 and c.isupper():
            prev = card_id[i-1]
            if prev.islower():
                result.append('_')  # lowercase→UPPERCASE transition
            elif i + 1 < len(card_id) and card_id[i+1].islower():
                result.append('_')  # UPPERCASE→Uppercase→lower transition (acronym start)
        result.append(c.upper())
    return ''.join(result)


def get_chinese_name(card_id: str) -> str:
    """Get Chinese name for a card, with fallback.

    Tries multiple lookup strategies:
    1. Direct UPPERCASE match
    2. CamelCase → UPPER_SNAKE_CASE conversion
    """
    # Strategy 1: Direct uppercase
    upper = card_id.upper()
    if upper in CHINESE_NAMES:
        return CHINESE_NAMES[upper]

    # Strategy 2: Try CamelCase → SNAKE_CASE conversion
    snake = camel_to_snake(card_id)
    if snake != upper and snake in CHINESE_NAMES:
        return CHINESE_NAMES[snake]

    # Fallback: Generate a readable name with [待翻译] marker
    readable = card_id.replace("_", " ").title()
    return f"[待翻译] {readable}"


# ═══════════════════════════════════════════════════════════════
# Card type detection
# ═══════════════════════════════════════════════════════════════

# Powers (known from game data)
KNOWN_POWERS = _upper_set(
    # Ironclad
    "AGGRESSION", "BARRICADE", "BERSERK", "BRUTALITY", "COMBUST", "CORRUPTION",
    "DARK_EMBRACE", "DEMON_FORM", "EVOLVE", "FEEL_NO_PAIN", "FIRE_BREATHING",
    "INFLAME", "JUGGERNAUT", "RUPTURE",
    # Silent
    "ACCURACY", "AFTERIMAGE", "ENVENOM", "FOOTWORK", "INFINITE_BLADES",
    "KNIFE_TRAP", "NOXIOUS_FUMES", "SERPENT_FORM", "THOUSAND_CUTS",
    "TOOLS_OF_THE_TRADE", "WELL_LAID_PLANS", "WRAITH_FORM",
    # Defect
    "BIASED_COGNITION", "BUFFER", "CAPACITOR", "CREATIVE_AI", "DEFRAGMENT",
    "ECHO_FORM", "ELECTRODYNAMICS", "HEATSINKS", "LOOP", "MACHINE_LEARNING",
    "SELF_REPAIR", "STATIC_DISCHARGE", "STORM",
    # Necrobinder
    "DEATH_MARCH", "DEMESNE", "EIDOLON", "END_OF_DAYS", "FORBIDDEN_GRIMOIRE",
    "LEGION_OF_BONE", "LETHALITY", "NECRO_MASTERY", "REAPER_FORM",
    "SENTRY_MODE", "SOULBOUND", "SPIRIT_OF_ASH",
    # Regent
    "ARSENAL", "CHILD_OF_THE_STARS", "DIVINE_AEGIS", "FURNACE",
    "HALLOWED_GROUND", "MARTYRDOM", "THE_SEALED_THRONE", "VOID_FORM",
    "GENESIS",
)

# Rare cards (partial list from CharacterConfigs MaxCopies)
KNOWN_RARES = _upper_set(
    "DEMON_FORM", "DARK_EMBRACE", "CORRUPTION", "BARRICADE", "FEED", "REAPER",
    "OFFERING", "BERSERK", "IMPERVIOUS", "FIEND_FIRE", "BLUDGEON", "JUGGERNAUT",
    "WRAITH_FORM", "NIGHTMARE", "GRAND_FINALE", "ADRENALINE", "BURST",
    "AFTERIMAGE", "ENVENOM", "THOUSAND_CUTS", "TOOLS_OF_THE_TRADE",
    "ECHO_FORM", "CREATIVE_AI", "ELECTRODYNAMICS", "RAINBOW", "METEOR_STRIKE",
    "HYPERBEAM", "SUNDER", "ALL_FOR_ONE", "MULTI_CAST", "FISSION",
    "NECRO_MASTERY", "REAPER_FORM", "SPIRIT_OF_ASH", "LETHALITY", "FRIENDSHIP",
    "INVOKE", "DEATH_MARCH", "END_OF_DAYS",
    "CHILD_OF_THE_STARS", "ARSENAL", "THE_SEALED_THRONE", "VOID_FORM", "GENESIS",
)

KNOWN_UNCOMMONS = _upper_set(
    "BURNING_PACT", "BATTLE_TRANCE", "HEADBUTT", "SHRUG_IT_OFF", "TRUE_GRIT",
    "SWORD_BOOMERANG", "TWIN_STRIKE", "UPPERCUT", "THUNDERCLAP", "INFLAME",
    "SPOT_WEAKNESS", "LIMIT_BREAK", "RUPTURE", "SECOND_WIND", "HAVOC",
    "CALCULATED_GAMBLE", "EXPERTISE", "ACROBATICS", "BACKFLIP", "LEG_SWEEP",
    "MALAISE", "CORPSE_EXPLOSION", "DIE_DIE_DIE", "ALL_OUT_ATTACK",
    "DEFRAGMENT", "BIASED_COGNITION", "GLACIER", "CHILL", "DARKNESS",
    "TURBO", "DOUBLE_ENERGY", "RECYCLE", "OVERCLOCK", "SEEK", "HOLOGRAM",
    "DREDGE", "FETCH", "PARSE", "CALCIFY", "DEFILE",
    "CHARGE", "HAMMER_TIME", "SANCTIFY", "PURIFY",
)

BASIC_CARDS = _upper_set(
    "STRIKE_IRONCLAD", "DEFEND_IRONCLAD", "BASH",
    "STRIKE_SILENT", "DEFEND_SILENT", "NEUTRALIZE", "SURVIVOR",
    "STRIKE_DEFECT", "DEFEND_DEFECT", "ZAP", "DUALCAST",
    "STRIKE_NECROBINDER", "DEFEND_NECROBINDER",
    "STRIKE_REGENT", "DEFEND_REGENT",
)


def detect_card_type(card_id: str) -> str:
    """Detect card type (Attack/Skill/Power) from card ID."""
    uid = card_id.upper()
    if uid in KNOWN_POWERS:
        return "Power"
    # Heuristic: cards with these suffixes are typically Skills
    skill_indicators = [
        "DEFEND", "BLOCK", "BARRIER", "WALL", "ARMOR", "SHIELD",
        "DRAW", "TRANCE", "PLAN", "STEP", "FORM",
        "OFFERING", "BLOODLETTING", "ADRENALINE", "TACTICIAN",
        "TURBO", "ENERGY", "CHARGE", "RECYCLE",
        "SEEK", "HOLOGRAM", "SECRET", "FETCH", "PREPARED",
        "SURVIVOR", "ESCAPE", "CALCULATED", "EXPERTISE",
        "CONCENTRATE", "NIGHTMARE", "BURST", "DOUBLE",
        "DODGE", "BACKFLIP", "ACROBATICS", "FOOTWORK",
        "FINESSE", "IMPATIENCE", "PURITY",
        "ARMAMENTS", "APOTHEOSIS",
        "SENTINEL", "ENTRENCH", "FEEL_NO_PAIN",
        "TRUE_GRIT", "SECOND_WIND", "SHRUG_IT_OFF",
        "BATTLE_TRANCE", "BURNING_PACT", "EXHUME",
    ]
    for indicator in skill_indicators:
        if indicator in uid:
            return "Skill"
    # Default to Attack
    return "Attack"


def detect_rarity(card_id: str) -> str:
    """Detect card rarity."""
    uid = card_id.upper()
    if uid in BASIC_CARDS:
        return "Basic"
    if uid in KNOWN_RARES:
        return "Rare"
    if uid in KNOWN_UNCOMMONS:
        return "Uncommon"
    return "Common"


def detect_cost(card_id: str) -> int:
    """Best-guess energy cost. 0 for X-cost, otherwise heuristic."""
    uid = card_id.upper()
    # Known 0-cost
    zero_cost = _upper_set(
        "CLAW", "ZAP", "DUALCAST", "FTL", "GO_FOR_THE_EYES", "BEAM_CELL",
        "STEAM_BARRIER", "NEUTRALIZE", "SLICE", "DEFLECT", "ESCAPE_PLAN",
        "PREPARED", "REFLEX", "TACTICIAN", "CONCENTRATE", "ADRENALINE",
        "BLOOD_FOR_BLOOD", "SWIFT_STRIKE", "SECRET_TECHNIQUE", "SECRET_WEAPON",
        "ANGER", "WARCRY", "HAVOC",
        "FLASH_OF_STEEL", "FINESSE", "IMPATIENCE", "PANACHE", "PANIC_BUTTON",
        "MADNESS", "ENLIGHTENMENT", "PURITY", "THINKING_AHEAD",
    )
    if uid in zero_cost:
        return 0
    # Known 3-cost
    three_cost = _upper_set(
        "BLUDGEON", "IMPERVIOUS", "FIEND_FIRE", "DEMON_FORM",
        "WRAITH_FORM", "ECHO_FORM", "CREATIVE_AI", "METEOR_STRIKE",
        "VOID_FORM", "THE_SEALED_THRONE", "BOMBARDMENT",
        "END_OF_DAYS", "LEGION_OF_BONE",
    )
    if uid in three_cost:
        return 3
    # Known 2-cost
    two_cost = _upper_set(
        "BASH", "UPPERCUT", "CLOTHESLINE", "SHOCKWAVE", "THUNDERCLAP",
        "BATTLE_TRANCE", "BURNING_PACT", "RAGE", "FLAME_BARRIER",
        "BARRICADE", "CORRUPTION", "JUGGERNAUT", "DARK_EMBRACE",
        "OFFERING", "SPOT_WEAKNESS", "BLOODLETTING", "HEADBUTT",
        "SHRUG_IT_OFF", "ARMAMENTS", "TRUE_GRIT", "HEMOKINESIS",
        "DASH", "LEG_SWEEP", "MALAISE", "CORPSE_EXPLOSION",
        "EXPERTISE", "NIGHTMARE", "BURST", "AFTERIMAGE",
        "BIASED_COGNITION", "GLACIER", "DARKNESS", "ELECTRODYNAMICS",
        "CHARGE", "HAMMER_TIME", "FURNACE", "CHILD_OF_THE_STARS",
        "ARSENAL", "NECRO_MASTERY", "SPIRIT_OF_ASH", "LETHALITY",
        "REAPER_FORM", "DEATH_MARCH", "CALCIFY", "INVOKE",
        "DEFRAGMENT", "CONSUME", "LOOP", "CAPACITOR",
    )
    if uid in two_cost:
        return 2
    # Known X-cost
    x_cost = _upper_set(
        "WHIRLWIND", "TEMPEST", "MALAISE", "SKEWER", "MULTI_CAST",
        "REINFORCED_BODY",
    )
    if uid in x_cost:
        return -1  # X-cost marker
    # Default to 1
    return 1


# ═══════════════════════════════════════════════════════════════
# Card effect data (key cards only — will be expanded)
# ═══════════════════════════════════════════════════════════════

def get_effects(card_id: str) -> dict:
    """Return known effect data for a card."""
    uid = card_id.upper()
    # Ironclad basics
    if uid == "BASH":
        return {"damage": 8, "vulnerable": 2}
    if uid == "STRIKE_IRONCLAD":
        return {"damage": 6}
    if uid == "DEFEND_IRONCLAD":
        return {"block": 5}
    if uid == "WHIRLWIND":
        return {"damage": 5, "aoe": True, "x_cost": True, "multi_hit": True}
    if uid == "DEMON_FORM":
        return {"strength_per_turn": 3}
    if uid == "INFLAME":
        return {"strength": 2}
    if uid == "UPPERCUT":
        return {"damage": 13, "weak": 1, "vulnerable": 1}
    if uid == "THUNDERCLAP":
        return {"damage": 4, "vulnerable": 1, "aoe": True}
    if uid == "IRON_WAVE":
        return {"damage": 5, "block": 5}
    if uid == "POMMEL_STRIKE":
        return {"damage": 9, "draw": 1}
    if uid == "SHRUG_IT_OFF":
        return {"block": 8, "draw": 1}
    if uid == "TRUE_GRIT":
        return {"block": 7, "exhaust": 1}
    if uid == "HEADBUTT":
        return {"damage": 9, "top_deck": 1}
    if uid == "SWORD_BOOMERANG":
        return {"damage": 3, "hits": 3, "multi_hit": True}
    if uid == "TWIN_STRIKE":
        return {"damage": 5, "hits": 2, "multi_hit": True}
    if uid == "BATTLE_TRANCE":
        return {"draw": 3}
    if uid == "OFFERING":
        return {"draw": 3, "energy": 2, "hp_loss": 4}
    if uid == "FEED":
        return {"damage": 10, "max_hp": 4}
    if uid == "REAPER":
        return {"damage": 4, "heal": True, "aoe": True}
    if uid == "BLOODLETTING":
        return {"energy": 2, "hp_loss": 3}
    if uid == "ARMAMENTS":
        return {"block": 5, "upgrade_hand": 1}
    if uid == "CORRUPTION":
        return {"skills_cost_zero": True}
    if uid == "DARK_EMBRACE":
        return {"exhaust_draw": 1}
    if uid == "BARRICADE":
        return {"block_retain": True}
    if uid == "FEEL_NO_PAIN":
        return {"exhaust_block": 4}
    if uid == "JUGGERNAUT":
        return {"block_damage": 5}
    if uid == "LIMIT_BREAK":
        return {"double_strength": True}
    if uid == "SPOT_WEAKNESS":
        return {"strength_if_attack": 3}
    if uid == "RUPTURE":
        return {"hp_loss_strength": 1}
    if uid == "BURNING_PACT":
        return {"draw": 3, "exhaust": 1}
    if uid == "SECOND_WIND":
        return {"block_per_exhaust": 5}
    if uid == "FIEND_FIRE":
        return {"damage_per_card": 7, "exhaust_all": True}
    if uid == "HAVOC":
        return {"play_top": 1}
    if uid == "HEMOKINESIS":
        return {"damage": 14, "hp_loss": 2}
    if uid == "RAGE":
        return {"block_per_attack": 4}
    if uid == "ANGER":
        return {"damage": 6, "copy": 1}
    if uid == "PERFECTED_STRIKE":
        return {"damage": 6, "per_strike": 3}
    if uid == "BODY_SLAM":
        return {"damage": 0, "damage_equals_block": True}
    if uid == "FLAME_BARRIER":
        return {"block": 12, "thorns": 4}
    if uid == "IMPERVIOUS":
        return {"block": 30}
    if uid == "BLUDGEON":
        return {"damage": 32}
    if uid == "CLOTHESLINE":
        return {"damage": 12, "weak": 2}
    if uid == "SHOCKWAVE":
        return {"weak": 1, "vulnerable": 1, "aoe": True}

    # Silent basics
    if uid == "NEUTRALIZE":
        return {"damage": 3, "weak": 2}
    if uid == "SURVIVOR":
        return {"block": 8, "discard": 1}
    if uid == "STRIKE_SILENT":
        return {"damage": 6}
    if uid == "DEFEND_SILENT":
        return {"block": 5}
    if uid == "DEADLY_POISON":
        return {"poison": 5}
    if uid == "BOUNCING_FLASK":
        return {"poison": 3, "hits": 3}
    if uid == "NOXIOUS_FUMES":
        return {"poison_per_turn": 2}
    if uid == "CATALYST":
        return {"poison_multiply": 2}
    if uid == "CORPSE_EXPLOSION":
        return {"poison": 6, "aoe_on_death": True}
    if uid == "DAGGER_SPRAY":
        return {"damage": 4, "hits": 2, "aoe": True}
    if uid == "DAGGER_THROW":
        return {"damage": 9, "draw": 1, "discard": 1}
    if uid == "BACKFLIP":
        return {"block": 5, "draw": 2}
    if uid == "ACROBATICS":
        return {"draw": 3, "discard": 1}
    if uid == "CALCULATED_GAMBLE":
        return {"discard_all": True, "draw_equal": True}
    if uid == "EXPERTISE":
        return {"draw_to": 6}
    if uid == "ADRENALINE":
        return {"energy": 1, "draw": 2}
    if uid == "FOOTWORK":
        return {"dexterity": 2}
    if uid == "AFTERIMAGE":
        return {"block_per_card": 1}
    if uid == "ACCURACY":
        return {"shiv_damage": 4}
    if uid == "INFINITE_BLADES":
        return {"shiv_per_turn": 1}
    if uid == "CLOAK_AND_DAGGER":
        return {"block": 6, "shiv": 1}
    if uid == "BLADE_DANCE":
        return {"shiv": 2}
    if uid == "FINISHER":
        return {"damage_per_attack": 6}
    if uid == "LEG_SWEEP":
        return {"block": 11, "weak": 2}
    if uid == "MALAISE":
        return {"weak": 1, "strength_down": 1, "x_cost": True}
    if uid == "PIERCING_WAIL":
        return {"strength_down": 6, "aoe": True}
    if uid == "BURST":
        return {"double_next_skill": 1}
    if uid == "NIGHTMARE":
        return {"copy_card": 3}
    if uid == "WRAITH_FORM":
        return {"intangible": 3}
    if uid == "ENVENOM":
        return {"poison_per_attack": 1}
    if uid == "DASH":
        return {"damage": 10, "block": 10}
    if uid == "SKEWER":
        return {"damage": 7, "x_cost": True}
    if uid == "BACKSTAB":
        return {"damage": 11, "innate": True}
    if uid == "DIE_DIE_DIE":
        return {"damage": 13, "aoe": True}
    if uid == "GRAND_FINALE":
        return {"damage": 50, "aoe": True, "empty_draw": True}
    if uid == "TOOLS_OF_THE_TRADE":
        return {"discard_per_turn": 1}
    if uid == "WELL_LAID_PLANS":
        return {"retain": 1}
    if uid == "THOUSAND_CUTS":
        return {"damage_per_card": 1}

    # Defect basics
    if uid == "ZAP":
        return {"channel_lightning": 1}
    if uid == "DUALCAST":
        return {"evoke_twice": True}
    if uid == "STRIKE_DEFECT":
        return {"damage": 6}
    if uid == "DEFEND_DEFECT":
        return {"block": 5}
    if uid == "BALL_LIGHTNING":
        return {"damage": 7, "channel_lightning": 1}
    if uid == "COLD_SNAP":
        return {"damage": 6, "channel_frost": 1}
    if uid == "GLACIER":
        return {"block": 7, "channel_frost": 2}
    if uid == "CHILL":
        return {"channel_frost_per_enemy": 1}
    if uid == "DEFRAGMENT":
        return {"focus": 1}
    if uid == "BIASED_COGNITION":
        return {"focus": 4, "focus_decay": 1}
    if uid == "CONSUME":
        return {"focus": 2, "orb_slot": -1}
    if uid == "CAPACITOR":
        return {"orb_slots": 2}
    if uid == "LOOP":
        return {"bonus_passive": 1}
    if uid == "ECHO_FORM":
        return {"double_first_card": 1}
    if uid == "ELECTRODYNAMICS":
        return {"lightning_aoe": True}
    if uid == "CREATIVE_AI":
        return {"random_power_per_turn": 1}
    if uid == "HEATSINKS":
        return {"power_draw": 1}
    if uid == "TEMPEST":
        return {"channel_lightning_x": True, "x_cost": True}
    if uid == "DARKNESS":
        return {"channel_dark": 1}
    if uid == "RAINBOW":
        return {"channel_all_orbs": 1}
    if uid == "CHAOS":
        return {"channel_random": 2}
    if uid == "FUSION":
        return {"channel_plasma": 1}
    if uid == "TURBO":
        return {"energy": 1, "void": 1}
    if uid == "DOUBLE_ENERGY":
        return {"double_energy": True}
    if uid == "AGGREGATE":
        return {"energy_per_x_cards": 1}
    if uid == "RECYCLE":
        return {"exhaust_for_energy": True}
    if uid == "SKIM":
        return {"draw": 3}
    if uid == "COMPILE_DRIVER":
        return {"damage": 7, "draw_per_orb": 1}
    if uid == "COOLHEADED":
        return {"channel_frost": 1, "draw": 1}
    if uid == "OVERCLOCK":
        return {"draw": 2, "burn": 1}
    if uid == "HOLOGRAM":
        return {"block": 3, "retrieve": 1}
    if uid == "SEEK":
        return {"tutor": 2}
    if uid == "CLAW":
        return {"damage": 3, "self_scaling": 2}
    if uid == "BARRAGE":
        return {"damage_per_orb_slot": 4}
    if uid == "MULTI_CAST":
        return {"evoke_x": True}
    if uid == "ALL_FOR_ONE":
        return {"damage": 10, "retrieve_zero_cost": True}
    if uid == "METEOR_STRIKE":
        return {"damage": 24, "channel_plasma": 3}
    if uid == "HYPERBEAM":
        return {"damage": 26, "aoe": True, "focus_down": 3}
    if uid == "SUNDER":
        return {"damage": 24, "refund_on_kill": True}
    if uid == "SWEEPING_BEAM":
        return {"damage": 6, "aoe": True, "draw": 1}
    if uid == "FTL":
        return {"damage": 5, "draw": 1}
    if uid == "GO_FOR_THE_EYES":
        return {"damage": 3, "weak": 1}
    if uid == "BEAM_CELL":
        return {"damage": 3, "vulnerable": 1}
    if uid == "CHARGE_BATTERY":
        return {"block": 7, "next_turn_energy": 1}
    if uid == "LEAP":
        return {"block": 9}
    if uid == "SCRAPE":
        return {"damage": 5, "draw_zero_cost": 3}
    if uid == "REBOOT":
        return {"shuffle_draw": 6}
    if uid == "REBOUND":
        return {"damage": 9, "play_next_turn": 1}
    if uid == "BUFFER":
        return {"negate_next_hit": 1}

    # Necrobinder basics
    if uid == "STRIKE_NECROBINDER":
        return {"damage": 6}
    if uid == "DEFEND_NECROBINDER":
        return {"block": 5}
    if uid == "NECRO_MASTERY":
        return {"osty_attack_defend": True}
    if uid == "REAPER_FORM":
        return {"damage_doubler": True}
    if uid == "SPIRIT_OF_ASH":
        return {"ethereal_block": 4}
    if uid == "LETHALITY":
        return {"first_attack_bonus": True}
    if uid == "FRIENDSHIP":
        return {"attack_energy": 1}
    if uid == "DEATH_MARCH":
        return {"souls_damage": True}
    if uid == "INVOKE":
        return {"osty_growth": True, "energy": 1}
    if uid == "CALCIFY":
        return {"osty_damage_growth": True}
    if uid == "DREDGE":
        return {"draw": 2}
    if uid == "FETCH":
        return {"draw": 1}
    if uid == "PARSE":
        return {"draw": 3}
    if uid == "ENFEEBLING_TOUCH":
        return {"weak": 2}
    if uid == "PUTREFY":
        return {"vulnerable": 2}
    if uid == "DEFILE":
        return {"aoe_debuff": True}
    if uid == "THE_SCYTHE":
        return {"damage": 20}
    if uid == "REAP":
        return {"damage": 8, "heal": True}
    if uid == "GRAVEBLAST":
        return {"damage": 7, "aoe": True}
    if uid == "BORROWED_TIME":
        return {"energy": 1, "draw": 1}
    if uid == "BODYGUARD":
        return {"block": 10, "osty": 1}
    if uid == "BONE_SHARDS":
        return {"block": 7, "damage": 2}

    # Regent basics
    if uid == "STRIKE_REGENT":
        return {"damage": 6}
    if uid == "DEFEND_REGENT":
        return {"block": 5}
    if uid == "CHILD_OF_THE_STARS":
        return {"star_block": True}
    if uid == "ARSENAL":
        return {"created_strength": 1}
    if uid == "VOID_FORM":
        return {"cost_reduction": 2}
    if uid == "THE_SEALED_THRONE":
        return {"star_cost_free": True}
    if uid == "GENESIS":
        return {"star_generation": True}
    if uid == "SANCTIFY":
        return {"energy": 1, "draw": 1}
    if uid == "CHARGE":
        return {"waste_to_power": True, "draw": 1}
    if uid == "HAMMER_TIME":
        return {"forge_engine": True}
    if uid == "FURNACE":
        return {"auto_forge": True}
    if uid == "SMITE":
        return {"damage": 9}
    if uid == "HOLY_BLADE":
        return {"damage": 12}

    # Generic fallback
    return {}


# ═══════════════════════════════════════════════════════════════
# Character Configs Priority Extraction
# ═══════════════════════════════════════════════════════════════

# Extracted from CharacterConfigs.cs CardPriorities dictionaries
# Format: card_id → tier_value (int)
# Tier values from the C# constants

# Tier constants as integer values
TIER_VALUES = {
    -5: "PRIORITY_MULTIPLAYER_ALLY",
    -4: "PRIORITY_MULTIPLAYER_TEAM",
    -3: "PRIORITY_FREE_ENERGY",
    -2: "PRIORITY_TUTOR",
    -1: "PRIORITY_UPGRADE_HAND",
    0:  "PRIORITY_MULTIPLAYER_SELF",  # also DECK_THINNER
    1:  "PRIORITY_SETUP",
    2:  "PRIORITY_ENERGY_DRAW",
    3:  "PRIORITY_POWER_S",
    4:  "PRIORITY_EXHAUST_DRAW",
    5:  "PRIORITY_STRENGTH_DEX",
    6:  "PRIORITY_VULNERABLE",
    7:  "PRIORITY_DOUBLER",
    8:  "PRIORITY_POWER",
    9:  "PRIORITY_DRAW_FILTER",
    10: "PRIORITY_BUFF",
    11: "PRIORITY_ATTACK",
    12: "PRIORITY_BLOCK",
    13: "PRIORITY_FLEX",
    14: "PRIORITY_LAST",
}

# Character-specific card priorities extracted from CharacterConfigs.cs
CHARACTER_PRIORITIES = {
    "IRONCLAD": {
        # PRIORITY_FREE_ENERGY (-3)
        "OFFERING": -3, "BLOODLETTING": -3,
        # PRIORITY_TUTOR (-2)
        "SEEK": -2, "HOLOGRAM": -2, "SECRET_TECHNIQUE": -2, "SECRET_WEAPON": -2,
        "HEADBUTT": -2, "WARCRY": -2, "EXHUME": -2,
        # PRIORITY_UPGRADE_HAND (-1)
        "ARMAMENTS": -1, "APOTHEOSIS": -1,
        # DECK_THINNER (0)
        "BURNING_PACT": 0, "TRUE_GRIT": 0, "SEVER_SOUL": 0, "SECOND_WIND": 0,
        # PRIORITY_SETUP (1)
        "CORRUPTION": 1, "DARK_EMBRACE": 1, "BARRICADE": 1,
        # PRIORITY_ENERGY_DRAW (2)
        "BATTLE_TRANCE": 2, "POMMEL_STRIKE": 2,
        # PRIORITY_POWER_S (3)
        "DEMON_FORM": 3, "FEEL_NO_PAIN": 3,
        # PRIORITY_STRENGTH_DEX (5)
        "INFLAME": 5, "SPOT_WEAKNESS": 5, "LIMIT_BREAK": 5, "RUPTURE": 5, "BRAND": 5,
        # PRIORITY_VULNERABLE (6)
        "BASH": 6, "UPPERCUT": 6, "THUNDERCLAP": 6, "TREMBLE": 6,
        "CLOTHESLINE": 6, "SHOCKWAVE": 6, "TAUNT": 6, "INTIMIDATE": 6,
        # PRIORITY_DOUBLER (7)
        "DOUBLE_TAP": 7, "MOLTEN_FIST": 7,
        # PRIORITY_POWER (8)
        "JUGGERNAUT": 8, "FIRE_BREATHING": 8, "EVOLVE": 8, "BRUTALITY": 8, "BERSERK": 8,
        "RAGE": 8, "FLAME_BARRIER": 8,
        # PRIORITY_DRAW_FILTER (9)
        "SHRUG_IT_OFF": 9, "HAVOC": 9,
        # PRIORITY_ATTACK (11)
        "ANGER": 11, "IRON_WAVE": 11, "TWIN_STRIKE": 11, "SWORD_BOOMERANG": 11,
        "PERFECTED_STRIKE": 11, "HEMOKINESIS": 11, "BLOOD_WALL": 11,
        # PRIORITY_BLOCK (12)
        "IMPERVIOUS": 12, "UNMOVABLE": 12, "SENTINEL": 12,
    },
    "SILENT": {
        # PRIORITY_FREE_ENERGY (-3)
        "ADRENALINE": -3, "TACTICIAN": -3, "CONCENTRATE": -3,
        # PRIORITY_SETUP (1)
        "WRAITH_FORM": 1,
        # PRIORITY_ENERGY_DRAW (2)
        "EXPERTISE": 2, "CALCULATED_GAMBLE": 2,
        # PRIORITY_POWER_S (3)
        "FOOTWORK": 3, "NOXIOUS_FUMES": 3, "ACCURACY": 3, "AFTERIMAGE": 3,
        "ENVENOM": 3, "THOUSAND_CUTS": 3,
        # PRIORITY_STRENGTH_DEX (5)
        "DODGE_AND_ROLL": 5,
        # PRIORITY_VULNERABLE (6)
        "TERROR": 6,
        # PRIORITY_DOUBLER (7)
        "BURST": 7, "NIGHTMARE": 7, "PHANTASMAL_KILLER": 7,
        # PRIORITY_POWER (8)
        "INFINITE_BLADES": 8, "TOOLS_OF_THE_TRADE": 8, "WELL_LAID_PLANS": 8,
        # PRIORITY_DRAW_FILTER (9)
        "BACKFLIP": 9, "DAGGER_THROW": 9, "ACROBATICS": 9, "PREPARED": 9,
        "ESCAPE_PLAN": 9,
        # PRIORITY_ATTACK (11)
        "BLADE_DANCE": 11, "DAGGER_SPRAY": 11, "DIE_DIE_DIE": 11,
        "ALL_OUT_ATTACK": 11, "SKEWER": 11, "BACKSTAB": 11,
        "POISONED_STAB": 11, "DASH": 11, "SUCKER_PUNCH": 11,
        # PRIORITY_BLOCK (12)
        "DEFLECT": 12, "BLUR": 12, "LEG_SWEEP": 12, "PIERCING_WAIL": 12,
        "CLOAK_AND_DAGGER": 12,
        # PRIORITY_FLEX (13)
        "MALAISE": 13, "PANIC_BUTTON": 13,
    },
    "DEFECT": {
        # PRIORITY_FREE_ENERGY (-3)
        "TURBO": -3, "DOUBLE_ENERGY": -3, "AGGREGATE": -3, "RECYCLE": -3,
        # PRIORITY_TUTOR (-2)
        "SEEK": -2, "HOLOGRAM": -2,
        # PRIORITY_SETUP (1)
        "ECHO_FORM": 1,
        # PRIORITY_ENERGY_DRAW (2)
        "SKIM": 2, "COMPILE_DRIVER": 2, "OVERCLOCK": 2,
        # PRIORITY_POWER_S (3)
        "DEFRAGMENT": 3, "BIASED_COGNITION": 3, "CREATIVE_AI": 3,
        "ELECTRODYNAMICS": 3, "HEATSINKS": 3, "LOOP": 3,
        # PRIORITY_STRENGTH_DEX (5)
        "CONSUME": 5, "CAPACITOR": 5,
        # PRIORITY_VULNERABLE (6)
        "BEAM_CELL": 6,
        # PRIORITY_DOUBLER (7)
        "AMPLIFY": 7,
        # PRIORITY_POWER (8)
        "STORM": 8, "MACHINE_LEARNING": 8, "BUFFER": 8,
        "STATIC_DISCHARGE": 8, "DARKNESS": 8, "RAINBOW": 8, "CHAOS": 8,
        # PRIORITY_DRAW_FILTER (9)
        "COOLHEADED": 9, "FTL": 9, "SCRAPE": 9,
        # PRIORITY_ATTACK (11)
        "BALL_LIGHTNING": 11, "COLD_SNAP": 11, "SWEEPING_BEAM": 11,
        "CLAW": 11, "BARRAGE": 11, "SUNDER": 11, "HYPERBEAM": 11,
        "METEOR_STRIKE": 11, "ALL_FOR_ONE": 11,
        # PRIORITY_BLOCK (12)
        "GLACIER": 12, "CHILL": 12, "CHARGE_BATTERY": 12, "LEAP": 12,
        # Special
        "GO_FOR_THE_EYES": 6,  # Vulnerable-priority 0-cost weak
    },
    "NECROBINDER": {
        # PRIORITY_SETUP (1)
        "REAPER_FORM": 1,
        # PRIORITY_ENERGY_DRAW (2)
        "FRIENDSHIP": 2, "BORROWED_TIME": 2, "DRAIN_POWER": 2,
        "SOUL_STORM": 2, "PAGESTORM": 2,
        # PRIORITY_POWER_S (3)
        "NECRO_MASTERY": 3, "SPIRIT_OF_ASH": 3, "LETHALITY": 3,
        # PRIORITY_EXHAUST_DRAW (4)
        "INVOKE": 4, "CALCIFY": 4, "DEATH_MARCH": 4,
        # PRIORITY_STRENGTH_DEX (5)
        "UNLEASH": 5, "FORBIDDEN_GRIMOIRE": 5,
        # PRIORITY_VULNERABLE (6)
        "ENFEEBLING_TOUCH": 6, "PUTREFY": 6, "DEBILITATE": 6, "DEFILE": 6,
        # PRIORITY_POWER (8)
        "DEMESNE": 8, "EIDOLON": 8, "END_OF_DAYS": 8, "LEGION_OF_BONE": 8,
        # PRIORITY_DRAW_FILTER (9)
        "DREDGE": 9, "FETCH": 9, "PARSE": 9, "GLIMPSE_BEYOND": 9,
        # PRIORITY_BUFF (10)
        "BODYGUARD": 10, "BONE_SHARDS": 10, "DEATHS_DOOR": 10,
        "GRAVE_WARDEN": 10, "PROTECTOR": 10, "SHROUD": 10, "SENTRY_MODE": 10,
        # PRIORITY_ATTACK (11)
        "THE_SCYTHE": 11, "REAP": 11, "GRAVEBLAST": 11, "BLIGHT_STRIKE": 11,
        "ERADICATE": 11, "SCULPTING_STRIKE": 11, "SEVERANCE": 11, "REAVE": 11,
    },
    "REGENT": {
        # PRIORITY_FREE_ENERGY (-3)
        "OFFERING": -3,
        # PRIORITY_SETUP (1)
        "VOID_FORM": 1, "THE_SEALED_THRONE": 1,
        # PRIORITY_ENERGY_DRAW (2)
        "SANCTIFY": 2, "GENESIS": 2,
        # PRIORITY_POWER_S (3)
        "CHILD_OF_THE_STARS": 3, "ARSENAL": 3,
        # PRIORITY_EXHAUST_DRAW (4)
        "CHARGE": 4, "HAMMER_TIME": 4, "FURNACE": 4,
        # PRIORITY_STRENGTH_DEX (5)
        "BLESSING_OF_HUNTING": 5, "APOTHEOSIS": 5,
        "SACRED_OATH": 5, "MAKE_IT_SO": 5,
        # PRIORITY_VULNERABLE (6)
        "PURIFY": 6, "OATH": 6, "AWE": 6,
        # PRIORITY_DOUBLER (7)
        "DIVINE_LANCE": 7, "RECLAMATION": 7,
        # PRIORITY_POWER (8)
        "DIVINE_AEGIS": 8, "HALLOWED_GROUND": 8, "MARTYRDOM": 8,
        # PRIORITY_DRAW_FILTER (9)
        "GRACE": 9, "CONFESS": 9,
        # PRIORITY_BUFF (10)
        "ROYALTIES": 10, "BOMBARDMENT": 10,
        # PRIORITY_ATTACK (11)
        "CHAMPIONS_BLOW": 11, "CLEAVING_STRIKE": 11, "HOLY_BLADE": 11,
        "RETRIBUTION": 11, "SMITE": 11, "ZEALOUS_STRIKE": 11,
        # PRIORITY_BLOCK (12)
        "ABSOLVE": 12, "BLESSED_SHIELD": 12, "DIVINE_PROTECTION": 12,
        "HOLY_ARMOR": 12, "PENANCE": 12,
    },
}

# Multiplayer priorities (added to ALL characters by AddMultiplayerPriorities)
MP_PRIORITIES = {
    # PRIORITY_MULTIPLAYER_ALLY (-5)
    "BELIEVE_IN_YOU": -5, "COORDINATE": -5, "INTERCEPT": -5,
    "LARGESSE": -5, "LIFT": -5, "MIMIC": -5,
    "DEMONIC_SHIELD": -5, "IGNITION": -5,
    # PRIORITY_MULTIPLAYER_TEAM (-4)
    "ENERGY_SURGE": -4, "HUDDLE_UP": -4, "RALLY": -4,
    "GLIMPSE_BEYOND": -4, "LEGION_OF_BONE": -4,
    # PRIORITY_MULTIPLAYER_SELF (0)
    "BEACON_OF_HOPE": 0, "HAMMER_TIME": 0, "TANK": 0, "SNEAKY": 0,
    # PRIORITY_VULNERABLE / PRIORITY_ATTACK
    "FLANKING": 6, "TAG_TEAM": 6,
    "GANG_UP": 11, "KNOCKDOWN": 11,
}


# ═══════════════════════════════════════════════════════════════
# Card lists (from STS2_Cards_by_Class.md)
# ═══════════════════════════════════════════════════════════════

CHARACTER_CARDS = {
    "Ironclad": [
        "Aggression", "Anger", "Armaments", "AshenStrike", "Barricade", "Bash",
        "BattleTrance", "BloodWall", "Bloodletting", "Bludgeon", "BodySlam",
        "Brand", "Break", "Breakthrough", "Bully", "BurningPact", "Cascade",
        "Cinder", "Colossus", "Conflagration", "Corruption", "CrimsonMantle",
        "Cruelty", "DarkEmbrace", "DefendIronclad", "DemonForm", "DemonicShield",
        "Dismantle", "Dominate", "DrumOfBattle", "EvilEye", "ExpectAFight",
        "Feed", "FeelNoPain", "FiendFire", "FightMe", "FlameBarrier",
        "ForgottenRitual", "Havoc", "Headbutt", "Hellraiser", "Hemokinesis",
        "HowlFromBeyond", "Impervious", "InfernalBlade", "Inferno", "Inflame",
        "IronWave", "Juggernaut", "Juggling", "Mangle", "MoltenFist", "NotYet",
        "Offering", "OneTwoPunch", "PactsEnd", "PerfectedStrike", "Pillage",
        "PommelStrike", "PrimalForce", "Pyre", "Rage", "Rampage", "Rupture",
        "SecondWind", "SetupStrike", "ShrugItOff", "Spite", "Stampede", "Stoke",
        "Stomp", "StoneArmor", "StrikeIronclad", "SwordBoomerang", "Tank", "Taunt",
        "TearAsunder", "Thrash", "Thunderclap", "Tremble", "TrueGrit", "TwinStrike",
        "Unmovable", "Unrelenting", "Uppercut", "Vicious", "Whirlwind",
    ],
    "Silent": [
        "Abrasive", "Accelerant", "Accuracy", "Acrobatics", "Adrenaline",
        "Afterimage", "Anticipate", "Assassinate", "Backflip", "Backstab",
        "BladeDance", "BladeOfInk", "Blur", "BouncingFlask", "BubbleBubble",
        "BulletTime", "Burst", "CalculatedGamble", "CloakAndDagger",
        "CorrosiveWave", "DaggerSpray", "DaggerThrow", "Dash", "DeadlyPoison",
        "DefendSilent", "Deflect", "DodgeAndRoll", "EchoingSlash", "Envenom",
        "EscapePlan", "Expertise", "Expose", "FanOfKnives", "Finisher", "Flanking",
        "Flechettes", "FlickFlack", "Footwork", "GrandFinale", "HandTrick", "Haze",
        "HiddenDaggers", "InfiniteBlades", "KnifeTrap", "LeadingStrike", "LegSweep",
        "Malaise", "MasterPlanner", "MementoMori", "Mirage", "Murder", "Neutralize",
        "Nightmare", "NoxiousFumes", "Outbreak", "PhantomBlades", "PiercingWail",
        "Pinpoint", "PoisonedStab", "Pounce", "PreciseCut", "Predator", "Prepared",
        "Reflex", "Ricochet", "Scare", "SerpentForm", "ShadowStep", "Shadowmeld",
        "Skewer", "Slice", "Snakebite", "Sneaky", "Speedster", "StormOfSteel",
        "Strangle", "StrikeSilent", "SuckerPunch", "Suppress", "Survivor",
        "Tactician", "TheHunt", "ToolsOfTheTrade", "Tracking", "Untouchable",
        "UpMySleeve", "WellLaidPlans", "WraithForm",
    ],
    "Defect": [
        "AdaptiveStrike", "AllForOne", "BallLightning", "Barrage", "BeamCell",
        "BiasedCognition", "BoostAway", "BootSequence", "Buffer", "BulkUp",
        "Capacitor", "Chaos", "ChargeBattery", "Chill", "Claw", "ColdSnap",
        "Compact", "CompileDriver", "ConsumingShadow", "Coolant", "Coolheaded",
        "CreativeAi", "Darkness", "DefendDefect", "Defragment", "DoubleEnergy",
        "Dualcast", "EchoForm", "EnergySurge", "Feral", "FightThrough", "FlakCannon",
        "FocusedStrike", "Ftl", "Fusion", "GeneticAlgorithm", "Glacier", "Glasswork",
        "GoForTheEyes", "GunkUp", "Hailstorm", "HelixDrill", "Hologram", "Hotfix",
        "Hyperbeam", "IceLance", "Ignition", "Iteration", "Leap", "LightningRod",
        "Loop", "MachineLearning", "MeteorStrike", "Modded", "MomentumStrike",
        "MultiCast", "Null", "Overclock", "Quadcast", "Rainbow", "Reboot", "Refract",
        "RocketPunch", "Scavenge", "Scrape", "ShadowShield", "Shatter", "SignalBoost",
        "Skim", "Smokestack", "Spinner", "Storm", "StrikeDefect", "Subroutine",
        "Sunder", "Supercritical", "SweepingBeam", "Synchronize", "Synthesis",
        "Tempest", "TeslaCoil", "Thunder", "TrashToTreasure", "Turbo", "Uproar",
        "Voltaic", "WhiteNoise", "Zap",
    ],
    "Necrobinder": [
        "Afterlife", "BansheesCry", "BlightStrike", "Bodyguard", "BoneShards",
        "BorrowedTime", "Bury", "Calcify", "CallOfTheVoid", "CaptureSpirit",
        "Cleanse", "Countdown", "DanseMacabre", "DeathMarch", "Deathbringer",
        "DeathsDoor", "Debilitate", "DefendNecrobinder", "Defile", "Defy", "Delay",
        "Demesne", "DevourLife", "Dirge", "DrainPower", "Dredge", "Eidolon",
        "EndOfDays", "EnfeeblingTouch", "Eradicate", "Fear", "Fetch",
        "Flatten", "ForbiddenGrimoire", "Friendship", "GlimpseBeyond", "GraveWarden",
        "Graveblast", "Hang", "Haunt", "HighFive", "Invoke", "LegionOfBone",
        "Lethality", "Melancholy", "Misery", "NecroMastery", "NegativePulse",
        "Neurosurge", "NoEscape", "Oblivion", "Pagestorm", "Parse", "Poke",
        "Protector", "PullAggro", "PullFromBelow", "Putrefy", "Rattle", "Reanimate",
        "Reap", "ReaperForm", "Reave", "RightHandHand", "Sacrifice", "Scourge",
        "SculptingStrike", "Seance", "SentryMode", "Severance", "SharedFate",
        "Shroud", "SicEm", "SleightOfFlesh", "Snap", "SoulStorm", "Sow",
        "SpiritOfAsh", "Spur", "Squeeze", "StrikeNecrobinder", "TheScythe",
        "TimesUp", "Transfigure", "Undeath", "Unleash", "Veilpiercer", "Wisp",
    ],
    "Regent": [
        "Absolve", "Apothesis", "Arsenal", "Awe", "BlessedShield",
        "BlessingOfHunting", "Bombardment", "ChampionsBlow", "Charge",
        "ChildOfTheStars", "CleavingStrike", "Confess", "DivineAegis",
        "DivineLance", "DivineProtection", "Furnace", "Genesis", "Grace",
        "HallowedGround", "HammerTime", "HolyArmor", "HolyBlade", "MakeItSo",
        "Martyrdom", "Oath", "Offering", "Penance", "Purify", "Reclamation",
        "Retribution", "Royalties", "SacredOath", "Sanctify", "Smite",
        "TheSealedThrone", "VoidForm", "ZealousStrike",
    ],
}


def generate_card_entry(card_id: str, character: str) -> dict:
    """Generate a complete card JSON entry."""
    uid = card_id.upper()

    # Get type and rarity
    card_type = detect_card_type(card_id)
    rarity = detect_rarity(card_id)
    cost = detect_cost(card_id)

    # Get Chinese name
    name_cn = get_chinese_name(card_id)
    name_en = card_id  # Display name derived from ID

    # Get play_priority from CharacterConfigs tier
    tier_value = None
    char_priorities = CHARACTER_PRIORITIES.get(character.upper(), {})
    if uid in char_priorities:
        tier_value = char_priorities[uid]
    elif uid in MP_PRIORITIES:
        tier_value = MP_PRIORITIES[uid]

    if tier_value is not None:
        tier_name = TIER_VALUES.get(tier_value, "PRIORITY_ATTACK")
        play_priority = TIER_TO_PLAY_PRIORITY.get(tier_name, 50)
    else:
        # Default: compute from card type and categories
        play_priority = _default_play_priority(card_id, card_type)

    # Get play_order from card categories
    tags = classify_card(card_id)
    play_order = compute_play_order(card_id, tags)

    # Get effects
    effects = get_effects(card_id)

    entry = {
        "id": card_id,
        "name_cn": name_cn,
        "name_en": name_en,
        "type": card_type,
        "cost": cost,
        "rarity": rarity,
        "play_priority": play_priority,
        "play_order": play_order,
        "effects": effects,
        "character": character,
    }

    # Add categories tags for reference
    if tags:
        entry["categories"] = tags

    # Add tier info for debugging
    if tier_value is not None:
        entry["_tier"] = TIER_VALUES.get(tier_value, f"UNKNOWN_{tier_value}")

    return entry


def _default_play_priority(card_id: str, card_type: str) -> int:
    """Compute default play_priority when no tier is defined."""
    uid = card_id.upper()

    # Power cards are generally high priority
    if card_type == "Power":
        return 65

    # Check for key categories
    if uid in AOE_CARDS:
        return 72
    if uid in DRAW_CARDS:
        return 55
    if uid in VULNERABLE_CARDS:
        return 60
    if uid in WEAK_CARDS:
        return 58
    if uid in STRENGTH_CARDS:
        return 75
    if uid in SCALING_CARDS:
        return 68
    if uid in POISON_CARDS:
        return 65
    if uid in ORB_CARDS:
        return 52
    if uid in ENERGY_CARDS:
        return 78
    if uid in FREE_ENERGY_CARDS:
        return 100
    if uid in PREMIUM_POWERS:
        return 80
    if uid in TUTOR_CARDS:
        return 95
    if uid in EXHAUST_CARDS:
        return 42
    if uid in DISCARD_CARDS:
        return 44
    if uid in MULTI_HIT_CARDS:
        return 50
    if uid in FINISHER_CARDS:
        return 38
    if uid in SELF_DAMAGE_CARDS:
        return 40

    # Block cards: medium priority
    if "DEFEND" in uid or "BLOCK" in uid or "BARRIER" in uid or "WALL" in uid:
        return 65
    if "SHIELD" in uid or "ARMOR" in uid:
        return 65

    # Attack cards: high priority (they end fights)
    if card_type == "Attack":
        return 85

    # Default
    return 50


# ═══════════════════════════════════════════════════════════════
# Main generation
# ═══════════════════════════════════════════════════════════════

def generate_all(mod_dir: str) -> dict[str, list[dict]]:
    """Generate card databases for all characters. Returns {char_name: [cards]}."""
    results = {}
    for character, card_ids in CHARACTER_CARDS.items():
        cards = []
        seen = set()
        for card_id in card_ids:
            uid = card_id.upper()
            if uid in seen:
                continue
            seen.add(uid)
            try:
                entry = generate_card_entry(card_id, character)
                cards.append(entry)
            except Exception as e:
                print(f"  ERROR generating {card_id} ({character}): {e}", file=sys.stderr)

        # Sort: Powers first, then Skills, then Attacks, then by play_priority descending
        type_order = {"Power": 0, "Skill": 1, "Attack": 2}
        cards.sort(key=lambda c: (type_order.get(c["type"], 3), -c["play_priority"], c["id"]))
        results[character] = cards
    return results


def write_json_files(results: dict[str, list[dict]], mod_dir: str) -> list[str]:
    """Write card JSON files to Cards/ directory. Returns list of file paths."""
    cards_dir = os.path.join(mod_dir, "Cards")
    os.makedirs(cards_dir, exist_ok=True)

    written = []
    for character, cards in results.items():
        filepath = os.path.join(cards_dir, f"{character}Cards.json")
        output = {
            "character": character,
            "version": "1.0",
            "generated_by": "scripts/generate_card_db.py",
            "total_cards": len(cards),
            "cards": cards,
        }
        with open(filepath, "w", encoding="utf-8") as f:
            json.dump(output, f, ensure_ascii=False, indent=2)
        written.append(filepath)

    return written


def print_stats(results: dict[str, list[dict]]):
    """Print summary statistics."""
    print("\n=== Card Database Generation Summary ===\n")
    total = 0
    total_with_cn = 0
    total_needs_translation = 0

    for character, cards in results.items():
        n = len(cards)
        total += n
        powers = sum(1 for c in cards if c["type"] == "Power")
        skills = sum(1 for c in cards if c["type"] == "Skill")
        attacks = sum(1 for c in cards if c["type"] == "Attack")
        has_priority = sum(1 for c in cards if "_tier" in c)
        needs_trans = sum(1 for c in cards if c["name_cn"].startswith("[待翻译]"))
        total_with_cn += (n - needs_trans)
        total_needs_translation += needs_trans

        print(f"  {character}: {n} cards")
        print(f"    Powers={powers}, Skills={skills}, Attacks={attacks}")
        print(f"    With priority tier: {has_priority}/{n}")
        print(f"    Needs Chinese translation: {needs_trans}/{n}")

    print(f"\n  TOTAL: {total} cards across 5 characters")
    print(f"  Chinese names: {total_with_cn}/{total} complete")
    print(f"  Needs translation: {total_needs_translation}")

    # Show cards needing translation
    if total_needs_translation > 0:
        print(f"\n  Cards needing Chinese translation:")
        for character, cards in results.items():
            for c in cards:
                if c["name_cn"].startswith("[待翻译]"):
                    print(f"    [{character}] {c['id']}")


# ═══════════════════════════════════════════════════════════════
# CLI Entry Point
# ═══════════════════════════════════════════════════════════════

def main():
    # Find mod directory
    script_dir = os.path.dirname(os.path.abspath(__file__))
    mod_dir = os.path.dirname(script_dir)  # scripts/ → mod root

    # Verify we're in the right place
    if not os.path.isdir(os.path.join(mod_dir, "src")):
        print(f"ERROR: Cannot find mod src/ directory from {mod_dir}", file=sys.stderr)
        print("Run this script from the TokenSpire2 mod directory.", file=sys.stderr)
        sys.exit(1)

    print(f"Mod directory: {mod_dir}")
    print(f"Generating card databases...")

    results = generate_all(mod_dir)
    written = write_json_files(results, mod_dir)

    print(f"\nWrote {len(written)} files:")
    for fp in written:
        size_kb = os.path.getsize(fp) / 1024
        print(f"  {fp} ({size_kb:.1f} KB)")

    print_stats(results)
    print("\nDone!")


if __name__ == "__main__":
    main()
