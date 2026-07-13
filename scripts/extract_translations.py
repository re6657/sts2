"""Extract Chinese card name translations from SlayTheSpire2.pck."""
import re
import json
import os
from pathlib import Path

PCK_PATH = str(Path(__file__).parent.parent.parent.parent / "SlayTheSpire2.pck")

# All character card IDs from STS2_Cards_by_Class.md
IRONCLAD = [
    "AGGRESSION", "ANGER", "ARMAMENTS", "ASHEN_STRIKE", "BARRICADE", "BASH", "BATTLE_TRANCE",
    "BLOOD_WALL", "BLOODLETTING", "BLUDGEON", "BODY_SLAM", "BRAND", "BREAK", "BREAKTHROUGH",
    "BULLY", "BURNING_PACT", "CASCADE", "CINDER", "COLOSSUS", "CONFLAGRATION", "CORRUPTION",
    "CRIMSON_MANTLE", "CRUELTY", "DARK_EMBRACE", "DEFEND_IRONCLAD", "DEMON_FORM", "DEMONIC_SHIELD",
    "DISMANTLE", "DOMINATE", "DRUM_OF_BATTLE", "EVIL_EYE", "EXPECT_A_FIGHT", "FEED",
    "FEEL_NO_PAIN", "FIEND_FIRE", "FIGHT_ME", "FLAME_BARRIER", "FORGOTTEN_RITUAL", "HAVOC",
    "HEADBUTT", "HELLRAISER", "HEMOKINESIS", "HOWL_FROM_BEYOND", "IMPERVIOUS", "INFERNAL_BLADE",
    "INFERNO", "INFLAME", "IRON_WAVE", "JUGGERNAUT", "JUGGLING", "MANGLE", "MOLTEN_FIST",
    "NOT_YET", "OFFERING", "ONE_TWO_PUNCH", "PACTS_END", "PERFECTED_STRIKE", "PILLAGE",
    "POMMEL_STRIKE", "PRIMAL_FORCE", "PYRE", "RAGE", "RAMPAGE", "RUPTURE", "SECOND_WIND",
    "SETUP_STRIKE", "SHRUG_IT_OFF", "SPITE", "STAMPEDE", "STOKE", "STOMP", "STONE_ARMOR",
    "STRIKE_IRONCLAD", "SWORD_BOOMERANG", "TANK", "TAUNT", "TEAR_ASUNDER", "THRASH",
    "THUNDERCLAP", "TREMBLE", "TRUE_GRIT", "TWIN_STRIKE", "UNMOVABLE", "UNRELENTING",
    "UPPERCUT", "VICIOUS", "WHIRLWIND"
]

SILENT = [
    "ABRASIVE", "ACCELERANT", "ACCURACY", "ACROBATICS", "ADRENALINE", "AFTERIMAGE", "ANTICIPATE",
    "ASSASSINATE", "BACKFLIP", "BACKSTAB", "BLADE_DANCE", "BLADE_OF_INK", "BLUR", "BOUNCING_FLASK",
    "BUBBLE_BUBBLE", "BULLET_TIME", "BURST", "CALCULATED_GAMBLE", "CLOAK_AND_DAGGER", "CORROSIVE_WAVE",
    "DAGGER_SPRAY", "DAGGER_THROW", "DASH", "DEADLY_POISON", "DEFEND_SILENT", "DEFLECT",
    "DODGE_AND_ROLL", "ECHOING_SLASH", "ENVENOM", "ESCAPE_PLAN", "EXPERTISE", "EXPOSE",
    "FAN_OF_KNIVES", "FINISHER", "FLANKING", "FLECHETTES", "FLICK_FLACK", "FOOTWORK",
    "GRAND_FINALE", "HAND_TRICK", "HAZE", "HIDDEN_DAGGERS", "INFINITE_BLADES", "KNIFE_TRAP",
    "LEADING_STRIKE", "LEG_SWEEP", "MALAISE", "MASTER_PLANNER", "MEMENTO_MORI", "MIRAGE",
    "MURDER", "NEUTRALIZE", "NIGHTMARE", "NOXIOUS_FUMES", "OUTBREAK", "PHANTOM_BLADES",
    "PIERCING_WAIL", "PINPOINT", "POISONED_STAB", "POUNCE", "PRECISE_CUT", "PREDATOR",
    "PREPARED", "REFLEX", "RICOCHET", "SCARE", "SERPENT_FORM", "SHADOW_STEP", "SHADOWMELD",
    "SKEWER", "SLICE", "SNAKEBITE", "SNEAKY", "SPEEDSTER", "STORM_OF_STEEL", "STRANGLE",
    "STRIKE_SILENT", "SUCKER_PUNCH", "SUPPRESS", "SURVIVOR", "TACTICIAN", "THE_HUNT",
    "TOOLS_OF_THE_TRADE", "TRACKING", "UNTOUCHABLE", "UP_MY_SLEEVE", "WELL_LAID_PLANS", "WRAITH_FORM"
]

DEFECT = [
    "ADAPTIVE_STRIKE", "ALL_FOR_ONE", "BALL_LIGHTNING", "BARRAGE", "BEAM_CELL", "BIASED_COGNITION",
    "BOOST_AWAY", "BOOT_SEQUENCE", "BUFFER", "BULK_UP", "CAPACITOR", "CHAOS", "CHARGE_BATTERY",
    "CHILL", "CLAW", "COLD_SNAP", "COMPACT", "COMPILE_DRIVER", "CONSUMING_SHADOW", "COOLANT",
    "COOLHEADED", "CREATIVE_AI", "DARKNESS", "DEFEND_DEFECT", "DEFRAGMENT", "DOUBLE_ENERGY",
    "DUALCAST", "ECHO_FORM", "ENERGY_SURGE", "FERAL", "FIGHT_THROUGH", "FLAK_CANNON",
    "FOCUSED_STRIKE", "FTL", "FUSION", "GENETIC_ALGORITHM", "GLACIER", "GLASSWORK",
    "GO_FOR_THE_EYES", "GUNK_UP", "HAILSTORM", "HELIX_DRILL", "HOLOGRAM", "HOTFIX", "HYPERBEAM",
    "ICE_LANCE", "IGNITION", "ITERATION", "LEAP", "LIGHTNING_ROD", "LOOP", "MACHINE_LEARNING",
    "METEOR_STRIKE", "MODDED", "MOMENTUM_STRIKE", "MULTI_CAST", "NULL", "OVERCLOCK", "QUADCAST",
    "RAINBOW", "REBOOT", "REFRACT", "ROCKET_PUNCH", "SCAVENGE", "SCRAPE", "SHADOW_SHIELD",
    "SHATTER", "SIGNAL_BOOST", "SKIM", "SMOKESTACK", "SPINNER", "STORM", "STRIKE_DEFECT",
    "SUBROUTINE", "SUNDER", "SUPERCRITICAL", "SWEEPING_BEAM", "SYNCHRONIZE", "SYNTHESIS",
    "TEMPEST", "TESLA_COIL", "THUNDER", "TRASH_TO_TREASURE", "TURBO", "UPROAR", "VOLTAIC",
    "WHITE_NOISE", "ZAP"
]

NECROBINDER = [
    "AFTERLIFE", "BANSHEES_CRY", "BLIGHT_STRIKE", "BODYGUARD", "BONE_SHARDS", "BORROWED_TIME",
    "BURY", "CALCIFY", "CALL_OF_THE_VOID", "CAPTURE_SPIRIT", "CLEANSE", "COUNTDOWN",
    "DANSE_MACABRE", "DEATH_MARCH", "DEATHBRINGER", "DEATHS_DOOR", "DEBILITATE",
    "DEFEND_NECROBINDER", "DEFILE", "DEFY", "DELAY", "DEMESNE", "DEVOUR_LIFE", "DIRGE",
    "DRAIN_POWER", "DREDGE", "EIDOLON", "END_OF_DAYS", "ENFEEBLING_TOUCH", "ERADICATE",
    "FEAR", "FETCH", "FLATTEN", "FORBIDDEN_GRIMOIRE", "FRIENDSHIP", "GLIMPSE_BEYOND",
    "GRAVE_WARDEN", "GRAVEBLAST", "HANG", "HAUNT", "HIGH_FIVE", "INVOKE", "LEGION_OF_BONE",
    "LETHALITY", "MELANCHOLY", "MISERY", "NECRO_MASTERY", "NEGATIVE_PULSE", "NEUROSURGE",
    "NO_ESCAPE", "OBLIVION", "PAGESTORM", "PARSE", "POKE", "PROTECTOR", "PULL_AGGRO",
    "PULL_FROM_BELOW", "PUTREFY", "RATTLE", "REANIMATE", "REAP", "REAPER_FORM", "REAVE",
    "RIGHT_HAND_HAND", "SACRIFICE", "SCOURGE", "SCULPTING_STRIKE", "SEANCE", "SENTRY_MODE",
    "SEVERANCE", "SHARED_FATE", "SHROUD", "SIC_EM", "SLEIGHT_OF_FLESH", "SNAP", "SOUL_STORM",
    "SOW", "SPIRIT_OF_ASH", "SPUR", "SQUEEZE", "STRIKE_NECROBINDER", "THE_SCYTHE",
    "TIMES_UP", "TRANSFIGURE", "UNDEATH", "UNLEASH", "VEILPIERCER", "WISP"
]

COLORLESS = [
    "ALCHEMIZE", "ANOINTED", "AUTOMATION", "BEACON_OF_HOPE", "BEAT_DOWN", "BELIEVE_IN_YOU",
    "BOLAS", "CALAMITY", "CATASTROPHE", "COORDINATE", "DARK_SHACKLES", "DISCOVERY",
    "DRAMATIC_ENTRANCE", "ENTROPY", "EQUILIBRIUM", "ETERNAL_ARMOR", "FASTEN", "FINESSE",
    "FISTICUFFS", "FLASH_OF_STEEL", "GANG_UP", "GOLD_AXE", "HAND_OF_GREED", "HIDDEN_GEM",
    "HUDDLE_UP", "IMPATIENCE", "INTERCEPT", "JACK_OF_ALL_TRADES", "JACKPOT", "KNOCKDOWN",
    "LIFT", "MASTER_OF_STRATEGY", "MAYHEM", "MIMIC", "MIND_BLAST", "NOSTALGIA", "OMNISLICE",
    "PANACHE", "PANIC_BUTTON", "PREP_TIME", "PRODUCTION", "PROLONG", "PROWESS", "PURITY",
    "RALLY", "REND", "RESTLESSNESS", "ROLLING_BOULDER", "SALVO", "SCRAWL", "SECRET_TECHNIQUE",
    "SECRET_WEAPON", "SEEKER_STRIKE", "SHOCKWAVE", "SPLASH", "STRATAGEM", "TAG_TEAM",
    "THE_BOMB", "THE_GAMBIT", "THINKING_AHEAD", "THRUMMING_HATCHET", "ULTIMATE_DEFEND",
    "ULTIMATE_STRIKE", "VOLLEY"
]

REGENT = [
    "AMBUSH", "ANCIENT_STRIKE", "ARCANE_SHIELD", "AUDIENCE_CHAMBER", "BLOODLUST",
    "BOUND_BY_LAW", "CHAMPION", "COLD_DECREE", "CONQUER", "CORONATION",
    "CROWN_OF_THORNS", "CRUSADE", "DARK_SHIELD", "DEVASTATION", "DIPLOMACY",
    "DIVINE_RIGHT", "EDICT", "ETERNAL_REIGN", "FROST_SHIELD", "GOLDEN_ARMOR",
    "HOLY_SHIELD", "IRON_SHIELD", "JUDGMENT", "JUSTICE_STRIKE", "KING_SHIELD",
    "LIGHTNING_SHIELD", "MARTIAL_LAW", "NECROTIC_SHIELD", "NOBLE_SACRIFICE",
    "OBLITERATE", "OVERWHELM", "POISON_SHIELD", "RADIANT_SHIELD", "ROYAL_DECREE",
    "SHADOW_SHIELD", "SOVEREIGN_SHIELD", "SUPREMACY", "TAXATION", "THUNDER_SHIELD",
    "VINDICATION", "VOID_FORM", "WAR_BANNER", "WRATH_SHIELD", "ZENITH_STRIKE"
]


def main():
    print("Loading PCK file...")
    with open(PCK_PATH, 'rb') as f:
        data = f.read()

    print("Extracting translations...")
    title_pattern = re.compile(rb'"([A-Z][A-Z_0-9]*)\.title"\s*:\s*"([^"]*)"')

    translations = {}
    for m in title_pattern.finditer(data):
        key = m.group(1).decode('utf-8', errors='replace')
        value_bytes = m.group(2)
        try:
            value = value_bytes.decode('utf-8')
        except:
            continue
        has_cjk = any('一' <= c <= '鿿' or '㐀' <= c <= '䶿' for c in value)
        if has_cjk:
            translations[key] = value

    print(f"Found {len(translations)} translated entries with CJK characters")

    all_chars = {
        "Ironclad": IRONCLAD,
        "Silent": SILENT,
        "Defect": DEFECT,
        "Necrobinder": NECROBINDER,
        "Regent": REGENT,
        "Colorless": COLORLESS,
    }

    total_found = 0
    total_missing = 0

    for char_name, card_list in all_chars.items():
        found = 0
        missing = []
        print(f"\n{'='*60}")
        print(f"  {char_name}: ", end="")

        for card_id in card_list:
            if card_id in translations:
                found += 1
            else:
                missing.append(card_id)

        print(f"{found}/{len(card_list)} translated")
        if missing:
            print(f"  Missing ({len(missing)}): {', '.join(missing)}")

        total_found += found
        total_missing += len(missing)

    print(f"\n{'='*60}")
    print(f"TOTAL: {total_found} found, {total_missing} missing "
          f"({total_found+total_missing} cards)")

    # Generate Python dict output
    output_path = str(Path(__file__).parent / "chinese_names_extracted.py")
    with open(output_path, 'w', encoding='utf-8') as f:
        f.write("# Auto-extracted Chinese card name translations from SlayTheSpire2.pck\n")
        f.write(f"# Total entries: {len(translations)}\n\n")
        f.write("CHINESE_NAMES = {\n")
        for key, value in sorted(translations.items()):
            f.write(f'    "{key}": "{value}",\n')
        f.write("}\n")

    print(f"\nSaved all translations to: {output_path}")

    # Also output JSON for direct use
    json_path = str(Path(__file__).parent / "chinese_names.json")
    with open(json_path, 'w', encoding='utf-8') as f:
        json.dump(translations, f, ensure_ascii=False, indent=2)
    print(f"Saved translations JSON to: {json_path}")


if __name__ == "__main__":
    main()
