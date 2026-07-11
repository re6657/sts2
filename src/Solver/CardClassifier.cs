using System;
using System.Collections.Generic;

namespace TokenSpire2.Solver;

/// <summary>
/// Shared card classification utilities for the solver system.
///
/// Centralizes card category checks that were previously duplicated across
/// IroncladSolver.cs, CharacterConfigs.cs, and CardRewardDecider.cs.
///
/// All methods use OrdinalIgnoreCase comparison for robustness.
/// </summary>
public static class CardClassifier
{
    // ═══════════════════════════════════════════════════════════════
    // Category: Draw
    // ═══════════════════════════════════════════════════════════════

    private static readonly HashSet<string> DrawCardSet = new(StringComparer.OrdinalIgnoreCase)
    {
        "POMMEL_STRIKE", "SHRUG_IT_OFF", "BURNING_PACT", "BATTLE_TRANCE",
        "OFFERING", "WARCRY", "BACKFLIP", "DAGGER_THROW", "ESCAPE_PLAN",
        "ACROBATICS", "CALCULATED_GAMBLE", "EXPERTISE", "QUICK_SLASH",
        "HEEL_HOOK", "DROP_KICK", "SKIM", "COMPILE_DRIVER", "COOLHEADED",
        "OVERCLOCK", "REBOUND", "FTL", "SCRAPE", "DREDGE", "FETCH", "PARSE",
        "PILLAGE", "EXPECT_A_FIGHT", "SPITE", "STOKE", "HEADBUTT",
        "GRACE", "CONFESS",
    };

    public static bool IsDrawCard(string cardId) => DrawCardSet.Contains(cardId);

    // ═══════════════════════════════════════════════════════════════
    // Category: Energy generation
    // ═══════════════════════════════════════════════════════════════

    private static readonly HashSet<string> EnergyCardSet = new(StringComparer.OrdinalIgnoreCase)
    {
        "OFFERING", "BLOODLETTING", "SEEING_RED", "SENTINEL",
        "ADRENALINE", "TACTICIAN", "CONCENTRATE",
        "TURBO", "DOUBLE_ENERGY", "RECYCLE", "AGGREGATE", "FUSION",
        "CHARGE", "HAMMER_TIME", "MANUFACTURING", "AUTOMATION",
        "CORRUPTION", "BERSERK", "DEVA_FORM",
        "FRIENDSHIP", "GENESIS", "SANCTIFY",
    };

    public static bool IsEnergyCard(string cardId) => EnergyCardSet.Contains(cardId);

    // ═══════════════════════════════════════════════════════════════
    // Category: AoE
    // ═══════════════════════════════════════════════════════════════

    private static readonly HashSet<string> AoeCardSet = new(StringComparer.OrdinalIgnoreCase)
    {
        "CLEAVE", "WHIRLWIND", "IMMOLATE", "THUNDERCLAP", "COMBUST",
        "DIE_DIE_DIE", "CORPSE_EXPLOSION", "ALL_OUT_ATTACK", "NOXIOUS_FUMES",
        "ELECTRODYNAMICS", "DOOM_AND_GLOOM", "HYPER_BEAM", "TEMPEST",
        "CONFLUENCE", "BOMBARDMENT", "STAR_EXTINGUISH", "DEFILE",
        "FLEA", "SCRAPE", "SWEEPING_BEAM", "DAZZLING_ENTRANCE",
        "CONFLAGRATION", "INFERNO",
    };

    public static bool IsAoeCard(string cardId) => AoeCardSet.Contains(cardId);

    // ═══════════════════════════════════════════════════════════════
    // Category: Multi-hit (Strength scaling)
    // ═══════════════════════════════════════════════════════════════

    private static readonly HashSet<string> MultiHitSet = new(StringComparer.OrdinalIgnoreCase)
    {
        "TWIN_STRIKE", "SWORD_BOOMERANG", "HEAVY_BLADE", "PUMMEL",
        "RIDDLE_WITH_HOLES", "BARRAGE", "TEMPEST", "WHIRLWIND",
        "SKEWER", "FLEA", "SLICE", "ENDLESS_AGONY",
    };

    public static bool IsMultiHitCard(string cardId) => MultiHitSet.Contains(cardId);

    // ═══════════════════════════════════════════════════════════════
    // Category: Vulnerable application
    // ═══════════════════════════════════════════════════════════════

    private static readonly HashSet<string> VulnerableCardSet = new(StringComparer.OrdinalIgnoreCase)
    {
        "BASH", "UPPERCUT", "THUNDERCLAP", "TREMBLE", "CLOTHESLINE",
        "SHOCKWAVE", "TAUNT", "INTIMIDATE",
        "TERROR", "BEAM_CELL", "GO_FOR_THE_EYES",
        "ENFEEBLING_TOUCH", "PURIFY",
        "AGGRESSION", // Power that applies Vulnerable
    };

    public static bool IsVulnerableCard(string cardId) => VulnerableCardSet.Contains(cardId);

    // ═══════════════════════════════════════════════════════════════
    // Category: Weak application
    // ═══════════════════════════════════════════════════════════════

    private static readonly HashSet<string> WeakCardSet = new(StringComparer.OrdinalIgnoreCase)
    {
        "CLOTHESLINE", "UPPERCUT", "SHOCKWAVE", "INTIMIDATE",
        "NEUTRALIZE", "SUCKER_PUNCH", "MALAISE", "LEG_SWEEP",
        "GO_FOR_THE_EYES", "BEAM_CELL", "PURIFY", "ENFEEBLING_TOUCH",
    };

    public static bool IsWeakCard(string cardId) => WeakCardSet.Contains(cardId);

    // ═══════════════════════════════════════════════════════════════
    // Category: Strength gain
    // ═══════════════════════════════════════════════════════════════

    private static readonly HashSet<string> StrengthCardSet = new(StringComparer.OrdinalIgnoreCase)
    {
        "INFLAME", "SPOT_WEAKNESS", "LIMIT_BREAK", "FLEX", "DEMON_FORM",
        "RUPTURE", "BRAND", "DOMINATE", "SETUP_STRIKE", "FIGHT_ME",
        "AGGRESSION", "JUGGERNAUT", "VICIOUS",
    };

    public static bool IsStrengthCard(string cardId) => StrengthCardSet.Contains(cardId);

    // ═══════════════════════════════════════════════════════════════
    // Category: Dexterity gain
    // ═══════════════════════════════════════════════════════════════

    private static readonly HashSet<string> DexterityCardSet = new(StringComparer.OrdinalIgnoreCase)
    {
        "FOOTWORK", "DODGE_AND_ROLL", "AFTERIMAGE",
        "REPROGRAM", // Defect: Str+Dex
    };

    public static bool IsDexterityCard(string cardId) => DexterityCardSet.Contains(cardId);

    // ═══════════════════════════════════════════════════════════════
    // Category: Poison
    // ═══════════════════════════════════════════════════════════════

    private static readonly HashSet<string> PoisonCardSet = new(StringComparer.OrdinalIgnoreCase)
    {
        "DEADLY_POISON", "BOUNCING_FLASK", "NOXIOUS_FUMES", "CATALYST",
        "CORPSE_EXPLOSION", "ENVENOM", "CORROSIVE_WAVE", "POISONED_STAB",
        "FLASK", "VENOMOLOGY",
    };

    public static bool IsPoisonCard(string cardId) => PoisonCardSet.Contains(cardId);

    // ═══════════════════════════════════════════════════════════════
    // Category: Orb-related (Defect)
    // ═══════════════════════════════════════════════════════════════

    private static readonly HashSet<string> OrbCardSet = new(StringComparer.OrdinalIgnoreCase)
    {
        "GLACIER", "CHILL", "COLD_SNAP", "BALL_LIGHTNING",
        "DARKNESS", "RAINBOW", "CHAOS", "FUSION", "ZAP",
        "ELECTRODYNAMICS", "TEMPEST", "COOLHEADED",
        "DUALCAST", "MULTI_CAST", "RECURSION", "FISSION",
    };

    public static bool IsOrbCard(string cardId) => OrbCardSet.Contains(cardId);

    // ═══════════════════════════════════════════════════════════════
    // Category: Focus (Defect)
    // ═══════════════════════════════════════════════════════════════

    private static readonly HashSet<string> FocusCardSet = new(StringComparer.OrdinalIgnoreCase)
    {
        "DEFRAGMENT", "BIASED_COGNITION", "CONSUME",
    };

    public static bool IsFocusCard(string cardId) => FocusCardSet.Contains(cardId);

    // ═══════════════════════════════════════════════════════════════
    // Category: Star-related (Necrobinder/Regent)
    // ═══════════════════════════════════════════════════════════════

    private static readonly HashSet<string> StarCardSet = new(StringComparer.OrdinalIgnoreCase)
    {
        "CHILD_OF_THE_STARS", "ARSENAL", "GENESIS", "THE_SEALED_THRONE",
        "NECRO_MASTERY", "REAPER_FORM", "SPIRIT_OF_ASH", "VOID_FORM",
        "SANCTIFY", "CHARGE", "HAMMER_TIME", "FURNACE",
        "FRIENDSHIP", "INVOKE", "CALCIFY", "DEATH_MARCH",
        "LETHALITY", "BORROWED_TIME", "SOUL_STORM", "PAGESTORM",
    };

    public static bool IsStarCard(string cardId) => StarCardSet.Contains(cardId);

    // ═══════════════════════════════════════════════════════════════
    // Category: Scaling cards (broad — any persistent growth)
    // ═══════════════════════════════════════════════════════════════

    private static readonly HashSet<string> ScalingCardSet = new(StringComparer.OrdinalIgnoreCase)
    {
        "DEMON_FORM", "RUPTURE", "INFLAME", "SPOT_WEAKNESS", "LIMIT_BREAK",
        "FOOTWORK", "NOXIOUS_FUMES", "ACCURACY", "ENVENOM", "AFTERIMAGE",
        "DEFRAGMENT", "BIASED_COGNITION", "CONSUME", "CAPACITOR", "LOOP",
        "ECHO_FORM", "CREATIVE_AI", "HEATSINKS", "ELECTRODYNAMICS",
        "NECRO_MASTERY", "SPIRIT_OF_ASH", "LETHALITY", "REAPER_FORM",
        "CHILD_OF_THE_STARS", "ARSENAL", "VOID_FORM", "THE_SEALED_THRONE",
        "DARK_EMBRACE", "FEEL_NO_PAIN", "CORRUPTION", "BARRICADE",
        "JUGGERNAUT", "EVOLVE", "FIRE_BREATHING",
    };

    public static bool IsScalingCard(string cardId) => ScalingCardSet.Contains(cardId);

    // ═══════════════════════════════════════════════════════════════
    // Category: Exhaust-related
    // ═══════════════════════════════════════════════════════════════

    private static readonly HashSet<string> ExhaustCardSet = new(StringComparer.OrdinalIgnoreCase)
    {
        "TRUE_GRIT", "BURNING_PACT", "SEVER_SOUL", "SECOND_WIND",
        "FIEND_FIRE", "HAVOC", "SENTINEL", "PURITY",
        "RECYCLE", "TURBO", "OFFERING", "EXHUME", "CORRUPTION",
        "PANIC_BUTTON",
    };

    public static bool IsExhaustCard(string cardId) => ExhaustCardSet.Contains(cardId);

    // ═══════════════════════════════════════════════════════════════
    // Category: Self-damage (Rupture synergy)
    // ═══════════════════════════════════════════════════════════════

    private static readonly HashSet<string> SelfDamageSet = new(StringComparer.OrdinalIgnoreCase)
    {
        "HEMOKINESIS", "BLOODLETTING", "OFFERING", "BRUTALITY",
        "COMBUST", "RUPTURE", "BLOOD_WALL",
    };

    public static bool IsSelfDamageCard(string cardId) => SelfDamageSet.Contains(cardId);

    // ═══════════════════════════════════════════════════════════════
    // Category: Power cards (type-based, not the general "IsPower")
    // Key powers that define win conditions (S-tier and A-tier)
    // ═══════════════════════════════════════════════════════════════

    private static readonly HashSet<string> PremiumPowerSet = new(StringComparer.OrdinalIgnoreCase)
    {
        // Ironclad
        "DEMON_FORM", "DARK_EMBRACE", "CORRUPTION", "BARRICADE",
        "FEEL_NO_PAIN", "JUGGERNAUT", "RUPTURE", "INFLAME", "BERSERK",
        // Silent
        "WRAITH_FORM", "FOOTWORK", "NOXIOUS_FUMES", "AFTERIMAGE",
        "ENVENOM", "ACCURACY", "THOUSAND_CUTS", "INFINITE_BLADES",
        // Defect
        "ECHO_FORM", "DEFRAGMENT", "BIASED_COGNITION", "CREATIVE_AI",
        "ELECTRODYNAMICS", "HEATSINKS", "LOOP", "CONSUME", "CAPACITOR",
        // Necrobinder
        "NECRO_MASTERY", "REAPER_FORM", "SPIRIT_OF_ASH", "LETHALITY",
        "FRIENDSHIP", "DEATH_MARCH",
        // Regent
        "CHILD_OF_THE_STARS", "ARSENAL", "VOID_FORM", "THE_SEALED_THRONE",
        "GENESIS",
    };

    public static bool IsPremiumPower(string cardId) => PremiumPowerSet.Contains(cardId);

    // ═══════════════════════════════════════════════════════════════
    // Category: Discard synergy (Silent)
    // ═══════════════════════════════════════════════════════════════

    private static readonly HashSet<string> DiscardCardSet = new(StringComparer.OrdinalIgnoreCase)
    {
        "TACTICIAN", "REFLEX", "PREPARED", "CALCULATED_GAMBLE",
        "CONCENTRATE", "TOOLS_OF_THE_TRADE", "ACROBATICS",
        "DAGGER_THROW", "SURVIVOR", "EXPERTISE", "STORM_OF_STEEL",
    };

    public static bool IsDiscardCard(string cardId) => DiscardCardSet.Contains(cardId);

    // ═══════════════════════════════════════════════════════════════
    // Category: Doubler cards (play before doubled cards)
    // ═══════════════════════════════════════════════════════════════

    private static readonly HashSet<string> DoublerCardSet = new(StringComparer.OrdinalIgnoreCase)
    {
        "DOUBLE_TAP", "BURST", "AMPLIFY", "ECHO_FORM",
        "NIGHTMARE", "PHANTASMAL_KILLER", "REAPER_FORM",
        "ONE_TWO_PUNCH", "MOLTEN_FIST",
    };

    public static bool IsDoublerCard(string cardId) => DoublerCardSet.Contains(cardId);

    // ═══════════════════════════════════════════════════════════════
    // Category: Tutor/search cards
    // ═══════════════════════════════════════════════════════════════

    private static readonly HashSet<string> TutorCardSet = new(StringComparer.OrdinalIgnoreCase)
    {
        "SEEK", "HOLOGRAM", "SECRET_TECHNIQUE", "SECRET_WEAPON",
        "HEADBUTT", // Pseudo-tutor: puts card from discard on top of draw
        "WARCRY", "EXHUME",
    };

    public static bool IsTutorCard(string cardId) => TutorCardSet.Contains(cardId);

    // ═══════════════════════════════════════════════════════════════
    // Category: Finisher / Lethal cards (play last)
    // ═══════════════════════════════════════════════════════════════

    private static readonly HashSet<string> FinisherCardSet = new(StringComparer.OrdinalIgnoreCase)
    {
        "FEED", "REAPER", "RITUAL_DAGGER", "HAND_OF_GREED",
        "FINISHER", "GRAND_FINALE",
    };

    public static bool IsFinisherCard(string cardId) => FinisherCardSet.Contains(cardId);

    // ═══════════════════════════════════════════════════════════════
    // Category: Free energy cards (0-cost that gains energy)
    // ═══════════════════════════════════════════════════════════════

    private static readonly HashSet<string> FreeEnergyCardSet = new(StringComparer.OrdinalIgnoreCase)
    {
        "OFFERING", "BLOODLETTING", "ADRENALINE",
        "TURBO", "DOUBLE_ENERGY", "AGGREGATE", "RECYCLE",
        "CONCENTRATE", "TACTICIAN",
        "FRIENDSHIP", "GENESIS", "SANCTIFY",
    };

    public static bool IsFreeEnergyCard(string cardId) => FreeEnergyCardSet.Contains(cardId);

    // ═══════════════════════════════════════════════════════════════
    // Category: Hand upgrade cards
    // ═══════════════════════════════════════════════════════════════

    private static readonly HashSet<string> HandUpgradeCardSet = new(StringComparer.OrdinalIgnoreCase)
    {
        "ARMAMENTS", "APOTHEOSIS",
    };

    public static bool IsHandUpgradeCard(string cardId) => HandUpgradeCardSet.Contains(cardId);

    // ═══════════════════════════════════════════════════════════════
    // Category: Basic cards (Strike / Defend variants)
    // ═══════════════════════════════════════════════════════════════

    public static bool IsBasicCard(string cardId)
    {
        if (string.IsNullOrEmpty(cardId)) return false;
        string upper = cardId.ToUpperInvariant();
        return upper.Contains("STRIKE") && (upper.Contains("IRONCLAD") || upper.Contains("SILENT") ||
            upper.Contains("DEFECT") || upper.Contains("NECROBINDER") || upper.Contains("REGENT") ||
            upper == "STRIKE")
            || upper.Contains("DEFEND") && (upper.Contains("IRONCLAD") || upper.Contains("SILENT") ||
            upper.Contains("DEFECT") || upper.Contains("NECROBINDER") || upper.Contains("REGENT") ||
            upper == "DEFEND")
            || upper is "BASH" or "NEUTRALIZE" or "SURVIVOR" or "ZAP" or "DUALCAST";
    }

    // ═══════════════════════════════════════════════════════════════
    // Play Order computation
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Compute the default play_order (0-100) for a card based on its category.
    /// Higher = should be played earlier in the turn sequence.
    /// This is a fallback when no per-card definition exists in CardDatabase.
    /// </summary>
    public static int GetDefaultPlayOrder(string cardId)
    {
        if (string.IsNullOrEmpty(cardId)) return 50;

        // Absolute first: free energy
        if (IsFreeEnergyCard(cardId)) return 100;
        // S-tier powers: scaling must start early
        if (IsPremiumPower(cardId)) return 98;
        // Tutors: find the right cards first
        if (IsTutorCard(cardId)) return 95;
        // Hand upgrades: upgrade before playing
        if (IsHandUpgradeCard(cardId)) return 93;
        // Debuffs: vulnerable/weak amplify all subsequent attacks
        if (IsVulnerableCard(cardId) || IsWeakCard(cardId)) return 92;
        // Strength/Dex buffs
        if (IsStrengthCard(cardId) || IsDexterityCard(cardId)) return 90;
        // Doublers
        if (IsDoublerCard(cardId)) return 88;
        // Focus (Defect)
        if (IsFocusCard(cardId)) return 85;
        // Poison (applies before Catalyst)
        if (IsPoisonCard(cardId)) return 80;
        // Other powers
        if (IsEnergyCard(cardId)) return 78;
        if (IsScalingCard(cardId)) return 75;
        if (IsStarCard(cardId)) return 72;
        // Orb channeling
        if (IsOrbCard(cardId)) return 65;
        // Draw cards: draw after setup
        if (IsDrawCard(cardId)) return 60;
        // AoE attacks
        if (IsAoeCard(cardId)) return 50;
        // Multi-hit (strength already applied if setup right)
        if (IsMultiHitCard(cardId)) return 45;
        // Standard attacks
        if (IsExhaustCard(cardId)) return 42;
        if (IsDiscardCard(cardId)) return 40;
        // Block cards: usually play last
        if (IsSelfDamageCard(cardId)) return 35;
        // Finishers
        if (IsFinisherCard(cardId)) return 20;

        return 50; // default mid-range
    }

    // ═══════════════════════════════════════════════════════════════
    // Play Priority computation (fallback from tier mapping)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Compute the default play_priority (0-100) for a card based on its category.
    /// Higher = more essential to play this turn.
    /// This is a fallback when no per-card definition exists in CardDatabase.
    /// </summary>
    public static int GetDefaultPlayPriority(string cardId)
    {
        if (string.IsNullOrEmpty(cardId)) return 50;

        // Highest: free energy
        if (IsFreeEnergyCard(cardId)) return 100;
        // Very high: S-tier powers
        if (IsPremiumPower(cardId)) return 80;
        if (IsTutorCard(cardId)) return 95;
        if (IsHandUpgradeCard(cardId)) return 90;
        // High: scaling and energy
        if (IsEnergyCard(cardId)) return 82;
        if (IsStrengthCard(cardId) || IsDexterityCard(cardId)) return 75;
        if (IsScalingCard(cardId)) return 70;
        if (IsStarCard(cardId)) return 70;
        if (IsFocusCard(cardId)) return 78;
        // Medium-high: AoE (important for multi-enemy)
        if (IsAoeCard(cardId)) return 72;
        // Medium: debuffs
        if (IsVulnerableCard(cardId)) return 60;
        if (IsWeakCard(cardId)) return 58;
        if (IsPoisonCard(cardId)) return 65;
        // Draw
        if (IsDrawCard(cardId)) return 55;
        // Medium-low
        if (IsOrbCard(cardId)) return 52;
        if (IsMultiHitCard(cardId)) return 50;
        if (IsDoublerCard(cardId)) return 48;
        if (IsExhaustCard(cardId)) return 42;
        if (IsDiscardCard(cardId)) return 44;
        if (IsSelfDamageCard(cardId)) return 40;
        // Finishers
        if (IsFinisherCard(cardId)) return 38;

        return 50;
    }
}
