using System;
using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Runs;
using TokenSpire2.Solver;

namespace TokenSpire2.Core;

/// <summary>
/// Tracks persistent run state across decisions.
/// Updated after every decision (card picked, relic obtained, shop purchase, etc.).
/// Provides context for all deciders to make informed, non-random choices.
///
/// Migrated from Solver.RunState — logic preserved, namespace updated.
/// </summary>
public class RunContext
{
    // ═══════════════════════════════════════════════════════════════
    // Basic state
    // ═══════════════════════════════════════════════════════════════

    public string Character { get; private set; } = "IRONCLAD";
    public int CurrentHp { get; private set; }
    public int MaxHp { get; private set; }
    public int Gold { get; private set; }
    public int Floor { get; private set; }
    public int Act { get; private set; }

    // ═══════════════════════════════════════════════════════════════
    // Deck tracking
    // ═══════════════════════════════════════════════════════════════

    public List<string> DeckCardIds { get; private set; } = new();
    public int AttackCount { get; private set; }
    public int SkillCount { get; private set; }
    public int PowerCount { get; private set; }
    public int TotalCardCount => DeckCardIds.Count;

    // Cost curve
    public int HighCostCardCount { get; private set; }     // Cards costing 2+
    public int ZeroCostCardCount { get; private set; }     // Cards costing 0 (non-X)
    public int DrawCardCount { get; private set; }         // Cards that draw additional cards

    // ═══════════════════════════════════════════════════════════════
    // Relics & Potions
    // ═══════════════════════════════════════════════════════════════

    public List<string> RelicIds { get; private set; } = new();
    public int PotionSlotCount { get; private set; }
    public int PotionCount { get; private set; }
    public List<string> PotionIds { get; private set; } = new();

    // ═══════════════════════════════════════════════════════════════
    // Synergy flags — Ironclad
    // ═══════════════════════════════════════════════════════════════

    public bool HasEnergyRelic { get; private set; }
    public bool HasStrengthScaling { get; private set; }
    public bool HasDexterityScaling { get; private set; }
    public bool HasExhaustSynergy { get; private set; }    // Feel No Pain, Dark Embrace, Corruption
    public bool HasBlockSynergy { get; private set; }      // Barricade, Entrench, Body Slam
    public bool HasSustainRelic { get; private set; }      // Burning Blood, Meat on the Bone, etc.
    public bool HasSelfDamageSynergy { get; private set; } // Rupture, Hemokinesis, etc.

    // ═══════════════════════════════════════════════════════════════
    // Synergy flags — Non-Ironclad
    // ═══════════════════════════════════════════════════════════════

    public bool HasPoisonSynergy { get; private set; }     // Noxious Fumes, Catalyst, Envenom (Silent)
    public bool HasDiscardSynergy { get; private set; }    // Tactician, Reflex, Tools of the Trade (Silent)
    public bool HasOrbSynergy { get; private set; }        // Electrodynamics, Loop, Capacitor (Defect)
    public bool HasFocusScaling { get; private set; }      // Defragment, Biased Cognition, Consume (Defect)
    public int OrbCount { get; private set; }              // Current number of channeled orbs (Defect)
    public int FocusStat { get; private set; }             // Current Focus value (Defect)
    public bool HasStarSynergy { get; private set; }       // Genesis, Child of the Stars, Arsenal (Necro/Regent)

    // ═══════════════════════════════════════════════════════════════
    // Diagnostic counters
    // ═══════════════════════════════════════════════════════════════

    public int CountEnergyCards { get; private set; }
    public int CountAoeCards { get; private set; }
    public int CountScalingCards { get; private set; }
    public int CountBasicStrikes { get; private set; }
    public int CountBasicDefends { get; private set; }
    public int CountUpgradedCards { get; private set; }
    public bool HasEnchantedBasicCard { get; private set; }
    public int EstimatedRemainingCampfires { get; private set; }
    public string ActBoss { get; private set; } = "";

    // ═══════════════════════════════════════════════════════════════
    // Computed diagnostics
    // ═══════════════════════════════════════════════════════════════

    public int TotalBasicCards => CountBasicStrikes + CountBasicDefends;
    public float BasicCardRatio => TotalCardCount > 0 ? (float)TotalBasicCards / TotalCardCount : 0f;
    public float DrawDensity => TotalCardCount > 0 ? (float)DrawCardCount / TotalCardCount : 0f;
    public float EnergyDensity => TotalCardCount > 0 ? (float)CountEnergyCards / TotalCardCount : 0f;
    public bool IsEngineClosed => DrawCardCount > 0 && CountEnergyCards > 0;
    public float AvgDamagePerCard { get; private set; }
    public float AvgBlockPerCard { get; private set; }

    // ═══════════════════════════════════════════════════════════════
    // Derived properties
    // ═══════════════════════════════════════════════════════════════

    public float HpRatio => MaxHp > 0 ? (float)CurrentHp / MaxHp : 0f;
    public bool IsHpLow => HpRatio < 0.4f;
    public bool IsHpHigh => HpRatio > 0.70f;
    public bool IsDeckLarge => TotalCardCount > 25;
    public bool HasGoldForShop => Gold >= 100;
    public bool NeedsAttacks => AttackCount < 4 && TotalCardCount > 5;
    public bool NeedsBlock => SkillCount < 3 && TotalCardCount > 5;
    public float HighCostRatio => TotalCardCount > 0 ? (float)HighCostCardCount / TotalCardCount : 0f;
    public int OpenPotionSlots => Math.Max(0, PotionSlotCount - PotionCount);

    // ═══════════════════════════════════════════════════════════════
    // Card detection sets (static — shared across all instances)
    // ═══════════════════════════════════════════════════════════════

    private static readonly HashSet<string> _drawCardIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "POMMEL_STRIKE", "SHRUG_IT_OFF", "BURNING_PACT", "BATTLE_TRANCE",
        "OFFERING", "WARCRY", "BACKFLIP", "DAGGER_THROW", "ESCAPE_PLAN",
        "ACROBATICS", "CALCULATED_GAMBLE", "EXPERTISE", "QUICK_SLASH",
        "HEEL_HOOK", "DROP_KICK", "SKIM", "COMPILE_DRIVER", "COOLHEADED",
        "OVERCLOCK", "REBOUND", "FTL", "SCRAPE", "DREDGE", "FETCH", "PARSE",
        "PILLAGE", "EXPECT_A_FIGHT", "SPITE", "STOKE", "HEADBUTT",
        "REFLEX", "TACTICIAN", "TOOLS_OF_THE_TRADE", "MACHINE_LEARNING",
        "DARK_EMBRACE", "EVOLVE", "GRACE", "CONFESS",
    };

    private static readonly HashSet<string> _energyCardIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "OFFERING", "BLOODLETTING", "SEEING_RED", "SENTINEL",
        "ADRENALINE", "TACTICIAN", "CONCENTRATE",
        "TURBO", "DOUBLE_ENERGY", "RECYCLE", "AGGREGATE", "FUSION",
        "CHARGE", "HAMMER_TIME", "MANUFACTURING", "AUTOMATION",
        "CORRUPTION", "BERSERK", "DEVA_FORM",
    };

    private static readonly HashSet<string> _aoeCardIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "CLEAVE", "WHIRLWIND", "IMMOLATE", "THUNDERCLAP", "COMBUST",
        "DIE_DIE_DIE", "CORPSE_EXPLOSION", "ALL_OUT_ATTACK", "NOXIOUS_FUMES",
        "ELECTRODYNAMICS", "DOOM_AND_GLOOM", "HYPER_BEAM", "TEMPEST",
        "CONFLUENCE", "BOMBARDMENT", "STAR_EXTINGUISH", "DEFILE",
        "FLEA", "SCRAPE", "SWEEPING_BEAM", "DAZZLING_ENTRANCE",
    };

    private static readonly HashSet<string> _scalingCardIds = new(StringComparer.OrdinalIgnoreCase)
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

    public static readonly HashSet<string> NonStrikeDefendStarters = new(StringComparer.OrdinalIgnoreCase)
    {
        "BASH",                         // Ironclad
        "NEUTRALIZE", "SURVIVOR",       // Silent
        "ZAP", "DUALCAST",              // Defect
    };

    // ═══════════════════════════════════════════════════════════════
    // Card classification (static helpers)
    // ═══════════════════════════════════════════════════════════════

    private static bool IsBasicStrikeName(string cardId)
    {
        return cardId.Equals("STRIKE", StringComparison.OrdinalIgnoreCase)
            || (cardId.StartsWith("STRIKE_", StringComparison.OrdinalIgnoreCase)
                && !cardId.Contains("POMMEL", StringComparison.OrdinalIgnoreCase)
                && !cardId.Contains("TWIN", StringComparison.OrdinalIgnoreCase)
                && !cardId.Contains("HEAVY", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsBasicDefendName(string cardId)
    {
        return cardId.Equals("DEFEND", StringComparison.OrdinalIgnoreCase)
            || (cardId.StartsWith("DEFEND_", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Returns true if the card ID is any basic starter card (Strike/Defend variants
    /// OR non-Strike/Defend unique starters like Bash, Neutralize, Zap, etc.).
    /// </summary>
    public static bool IsAnyBasicCardId(string cardId)
    {
        if (string.IsNullOrEmpty(cardId)) return false;
        return IsBasicStrikeName(cardId) || IsBasicDefendName(cardId)
            || NonStrikeDefendStarters.Contains(cardId);
    }

    private static bool HasExtraEffects(CardModel card)
    {
        if (card == null) return false;
        var fx = CardEffectReader.ReadEffects(card);
        string id = (card.Id.Entry ?? "").ToLowerInvariant();

        if (id == "strike")
            return fx.BaseBlock > 0 || fx.VulnerableStacks > 0 || fx.WeakStacks > 0
                || fx.GrantsStrength || fx.GrantsDexterity || fx.EnergyGain > 0
                || fx.StrengthAmount > 0 || fx.DexterityAmount > 0
                || fx.HpCost > 0 || fx.IsAoe || fx.IsPower || fx.IsXCost;

        if (id == "defend")
            return fx.BaseDamage > 0 || fx.VulnerableStacks > 0 || fx.WeakStacks > 0
                || fx.GrantsStrength || fx.GrantsDexterity || fx.EnergyGain > 0
                || fx.StrengthAmount > 0 || fx.DexterityAmount > 0
                || fx.HpCost > 0 || fx.IsAoe || fx.IsPower || fx.IsXCost;

        return false;
    }

    // ═══════════════════════════════════════════════════════════════
    // Refresh
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Count cards whose ID starts with the given fragment (case-insensitive).
    /// Uses StartsWith (not Contains) to avoid false matches:
    /// e.g., CountCardsById("Strike") matches STRIKE_IRONCLAD but NOT POMMEL_STRIKE.
    /// </summary>
    public int CountCardsById(string idFragment)
    {
        return DeckCardIds.Where(id =>
            id.StartsWith(idFragment, StringComparison.OrdinalIgnoreCase)).Count();
    }

    /// <summary>Refresh from current game state. Call before making decisions.</summary>
    public void Refresh()
    {
        var rs = RunManager.Instance?.DebugOnlyGetState();
        if (rs == null) return;

        var player = LocalContext.GetMe(rs);
        if (player == null) return;

        Character = player.Character?.Id.Entry ?? Character;
        CurrentHp = player.Creature?.CurrentHp ?? CurrentHp;
        MaxHp = player.Creature?.MaxHp ?? MaxHp;
        Gold = player.Gold;
        Floor = rs.TotalFloor;

        // Compute Act from floor
        Act = Floor <= 16 ? 1 : Floor <= 33 ? 2 : 3;

        // Deck composition
        try
        {
            var deck = player.Deck?.Cards;
            if (deck != null)
            {
                var cardList = deck.ToList();
                DeckCardIds = cardList.Select(c => c.Id.Entry).ToList();
                AttackCount = cardList.Count(c => c.Type == CardType.Attack);
                SkillCount = cardList.Count(c => c.Type == CardType.Skill);
                PowerCount = cardList.Count(c => c.Type == CardType.Power);

                HighCostCardCount = cardList.Count(c =>
                    !c.EnergyCost.CostsX && c.EnergyCost.Canonical >= 2);
                ZeroCostCardCount = cardList.Count(c =>
                    !c.EnergyCost.CostsX && c.EnergyCost.Canonical == 0);
                DrawCardCount = cardList.Count(c =>
                    _drawCardIds.Contains(c.Id.Entry));

                CountEnergyCards = cardList.Count(c =>
                    _energyCardIds.Contains(c.Id.Entry));
                CountAoeCards = cardList.Count(c =>
                    _aoeCardIds.Contains(c.Id.Entry));
                CountScalingCards = cardList.Count(c =>
                    _scalingCardIds.Contains(c.Id.Entry));
                CountBasicStrikes = cardList.Count(c =>
                    IsBasicStrikeName(c.Id.Entry));
                CountBasicDefends = cardList.Count(c =>
                    IsBasicDefendName(c.Id.Entry));
                CountUpgradedCards = cardList.Count(c => c.IsUpgraded);
                HasEnchantedBasicCard = cardList.Any(c =>
                    IsBasicStrikeName(c.Id.Entry) || IsBasicDefendName(c.Id.Entry)
                        ? HasExtraEffects(c) : false);

                // Approximate average damage/block per card
                try
                {
                    float totalDmg = 0; int dmgCards = 0;
                    float totalBlk = 0; int blkCards = 0;
                    foreach (var c in cardList)
                    {
                        var fx = CardEffectReader.ReadEffects(c);
                        if (fx.BaseDamage > 0) { totalDmg += fx.BaseDamage; dmgCards++; }
                        if (fx.BaseBlock > 0) { totalBlk += fx.BaseBlock; blkCards++; }
                    }
                    AvgDamagePerCard = dmgCards > 0 ? totalDmg / dmgCards : 0f;
                    AvgBlockPerCard = blkCards > 0 ? totalBlk / blkCards : 0f;
                }
                catch { AvgDamagePerCard = 0f; AvgBlockPerCard = 0f; }

                EstimatedRemainingCampfires = EstimateRemainingCampfires();
                ActBoss = DetectActBoss();
            }
        }
        catch { /* deck access may fail */ }

        // Relics
        try
        {
            var relics = player.Relics;
            if (relics != null)
            {
                RelicIds = relics.Select(r => r.Id.Entry).ToList();

                HasEnergyRelic = RelicIds.Any(id =>
                    id.Contains("Prison") || id.Contains("Sozu") || id.Contains("Ectoplasm") ||
                    id.Contains("Philosopher") || id.Contains("CursedKey") || id.Contains("Key") ||
                    id.Contains("Coffee") || id.Contains("Dripper") || id.Contains("Fusion") ||
                    id.Contains("Hammer") || id.Contains("Crown") || id.Contains("Bell") ||
                    id.Contains("Slaver") || id.Contains("Core") || id.Contains("Battery") ||
                    id.Contains("Lantern") || id.Contains("Tea") || id.Contains("Happy") ||
                    id.Contains("Blood") || id.Contains("Violet") || id.Contains("Boss"));

                HasStrengthScaling = RelicIds.Any(id =>
                    id.Contains("Vajra") || id.Contains("Girya") || id.Contains("DuVu") ||
                    id.Contains("Stone") || id.Contains("Shuriken") || id.Contains("Kunai") ||
                    id.Contains("Clock") || id.Contains("Fan") || id.Contains("Wrist"));

                HasDexterityScaling = RelicIds.Any(id =>
                    id.Contains("Kunai") || id.Contains("Fan") || id.Contains("Smooth") ||
                    id.Contains("Oddly") || id.Contains("Footwork") || id.Contains("Wrist"));

                HasSustainRelic = RelicIds.Any(id =>
                    id.Contains("Burning") || id.Contains("Blood") || id.Contains("Meat") ||
                    id.Contains("Feather") || id.Contains("Bird") || id.Contains("Urn") ||
                    id.Contains("Pillow") || id.Contains("Meal") || id.Contains("Mango") ||
                    id.Contains("Pear") || id.Contains("Berry") || id.Contains("Waffle") ||
                    id.Contains("Flower") || id.Contains("Pantograph"));
            }
        }
        catch { /* relics may fail */ }

        // Synergy detection from relics + deck
        HasExhaustSynergy = RelicIds.Any(id =>
            id.Contains("Charon") || id.Contains("Dead") || id.Contains("Branch"))
            || DeckCardIds.Any(id =>
                id.Contains("FEEL_NO_PAIN", StringComparison.OrdinalIgnoreCase) ||
                id.Contains("DARK_EMBRACE", StringComparison.OrdinalIgnoreCase) ||
                id.Contains("CORRUPTION", StringComparison.OrdinalIgnoreCase));

        HasBlockSynergy = RelicIds.Any(id =>
            id.Contains("Calipers") || id.Contains("Self"))
            || DeckCardIds.Any(id =>
                id.Contains("BARRICADE", StringComparison.OrdinalIgnoreCase) ||
                id.Contains("ENTRENCH", StringComparison.OrdinalIgnoreCase) ||
                id.Contains("BODY_SLAM", StringComparison.OrdinalIgnoreCase) ||
                id.Contains("JUGGERNAUT", StringComparison.OrdinalIgnoreCase));

        HasSelfDamageSynergy = RelicIds.Any(id =>
            id.Contains("Rupture"))
            || DeckCardIds.Any(id =>
                id.Contains("RUPTURE", StringComparison.OrdinalIgnoreCase) ||
                id.Contains("HEMOKINESIS", StringComparison.OrdinalIgnoreCase) ||
                id.Contains("BLOODLETTING", StringComparison.OrdinalIgnoreCase) ||
                id.Contains("OFFERING", StringComparison.OrdinalIgnoreCase) ||
                id.Contains("BRUTALITY", StringComparison.OrdinalIgnoreCase) ||
                id.Contains("COMBUST", StringComparison.OrdinalIgnoreCase));

        // Non-Ironclad synergy
        HasPoisonSynergy = RelicIds.Any(id =>
            id.Contains("Snecko") && id.Contains("Skull"))
            || DeckCardIds.Any(id =>
                id.Contains("NOXIOUS_FUMES", StringComparison.OrdinalIgnoreCase) ||
                id.Contains("CATALYST", StringComparison.OrdinalIgnoreCase) ||
                id.Contains("ENVENOM", StringComparison.OrdinalIgnoreCase) ||
                id.Contains("CORPSE_EXPLOSION", StringComparison.OrdinalIgnoreCase) ||
                id.Contains("DEADLY_POISON", StringComparison.OrdinalIgnoreCase) ||
                id.Contains("BOUNCING_FLASK", StringComparison.OrdinalIgnoreCase));

        HasDiscardSynergy = RelicIds.Any(id =>
            id.Contains("Bandages") || id.Contains("Tingsha"))
            || DeckCardIds.Any(id =>
                id.Contains("TACTICIAN", StringComparison.OrdinalIgnoreCase) ||
                id.Contains("REFLEX", StringComparison.OrdinalIgnoreCase) ||
                id.Contains("TOOLS_OF_THE_TRADE", StringComparison.OrdinalIgnoreCase) ||
                id.Contains("CALCULATED_GAMBLE", StringComparison.OrdinalIgnoreCase) ||
                id.Contains("PREPARED", StringComparison.OrdinalIgnoreCase) ||
                id.Contains("CONCENTRATE", StringComparison.OrdinalIgnoreCase));

        HasOrbSynergy = RelicIds.Any(id =>
            id.Contains("Data") || id.Contains("Inserter") || id.Contains("Capacitor") ||
            id.Contains("Cables") || id.Contains("Core"))
            || DeckCardIds.Any(id =>
                id.Contains("ELECTRODYNAMICS", StringComparison.OrdinalIgnoreCase) ||
                id.Contains("LOOP", StringComparison.OrdinalIgnoreCase) ||
                id.Contains("CAPACITOR", StringComparison.OrdinalIgnoreCase) ||
                id.Contains("STORM", StringComparison.OrdinalIgnoreCase) ||
                id.Contains("GLACIER", StringComparison.OrdinalIgnoreCase));

        HasFocusScaling = RelicIds.Any(id =>
            id.Contains("Data"))
            || DeckCardIds.Any(id =>
                id.Contains("DEFRAGMENT", StringComparison.OrdinalIgnoreCase) ||
                id.Contains("BIASED_COGNITION", StringComparison.OrdinalIgnoreCase) ||
                id.Contains("CONSUME", StringComparison.OrdinalIgnoreCase));

        // Orb count & Focus (Defect)
        OrbCount = 0;
        FocusStat = 0;
        try
        {
            var creature = player.Creature;
            if (creature != null)
            {
                var powers = creature.Powers;
                if (powers != null)
                {
                    foreach (var p in powers)
                    {
                        var pName = p.GetType().Name;
                        if (pName.Contains("Focus", StringComparison.OrdinalIgnoreCase))
                        {
                            try
                            {
                                var amountProp = p.GetType().GetProperty("Amount")
                                    ?? p.GetType().GetProperty("Stacks")
                                    ?? p.GetType().GetProperty("Count");
                                if (amountProp != null)
                                    FocusStat = Convert.ToInt32(amountProp.GetValue(p));
                            }
                            catch { FocusStat = 1; }
                            break;
                        }
                    }
                }
                try
                {
                    var orbQueueProp = creature.GetType().GetProperty("OrbQueue")
                        ?? creature.GetType().GetProperty("Orbs");
                    if (orbQueueProp != null)
                    {
                        var orbQueue = orbQueueProp.GetValue(creature);
                        if (orbQueue != null)
                        {
                            var countProp = orbQueue.GetType().GetProperty("Count")
                                ?? orbQueue.GetType().GetProperty("Length");
                            if (countProp != null)
                                OrbCount = Convert.ToInt32(countProp.GetValue(orbQueue));
                        }
                    }
                }
                catch { /* orb queue may not exist on non-Defect characters */ }
            }
        }
        catch { /* orbs only exist on Defect */ }

        HasStarSynergy = RelicIds.Any(id =>
            id.Contains("Star") || id.Contains("Celestial"))
            || DeckCardIds.Any(id =>
                id.Contains("CHILD_OF_THE_STARS", StringComparison.OrdinalIgnoreCase) ||
                id.Contains("ARSENAL", StringComparison.OrdinalIgnoreCase) ||
                id.Contains("GENESIS", StringComparison.OrdinalIgnoreCase) ||
                id.Contains("THE_SEALED_THRONE", StringComparison.OrdinalIgnoreCase) ||
                id.Contains("NECRO_MASTERY", StringComparison.OrdinalIgnoreCase) ||
                id.Contains("REAPER_FORM", StringComparison.OrdinalIgnoreCase) ||
                id.Contains("SPIRIT_OF_ASH", StringComparison.OrdinalIgnoreCase) ||
                id.Contains("VOID_FORM", StringComparison.OrdinalIgnoreCase));

        // Potions
        try
        {
            var potions = player.Potions;
            if (potions != null)
            {
                var potionList = potions.ToList();
                PotionSlotCount = potionList.Count;
                PotionCount = potionList.Count(p => !p.IsQueued);
                PotionIds = potionList.Where(p => !p.IsQueued).Select(p => p.Id.Entry).ToList();
            }
        }
        catch { /* potions may fail */ }
    }

    // ═══════════════════════════════════════════════════════════════
    // Private helpers
    // ═══════════════════════════════════════════════════════════════

    private int EstimateRemainingCampfires()
    {
        int floorInAct = Act switch { 1 => Floor, 2 => Floor - 16, 3 => Floor - 33, _ => Floor };
        int actEndFloor = Act switch { 1 => 16, 2 => 33, 3 => 50, _ => 50 };
        int remainingFloors = Math.Max(0, actEndFloor - floorInAct);
        return Math.Max(0, remainingFloors / 5);
    }

    private string DetectActBoss()
    {
        try
        {
            var mapScreen = NMapScreen.Instance;
            if (mapScreen == null) return "";

            var allPoints = AutoSlayHelpers.FindAll<NMapPoint>(mapScreen);
            foreach (var point in allPoints)
            {
                try
                {
                    var ptProp = point.GetType().GetProperty("Point");
                    if (ptProp == null) continue;
                    var ptValue = ptProp.GetValue(point);
                    if (ptValue == null) continue;

                    string typeName = ptValue.GetType().Name;
                    if (!typeName.Contains("Boss", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var idProp = ptValue.GetType().GetProperty("Id")
                        ?? ptValue.GetType().GetProperty("EncounterId")
                        ?? ptValue.GetType().GetProperty("ContentId");
                    if (idProp != null)
                    {
                        var idValue = idProp.GetValue(ptValue);
                        string? idStr = idValue?.ToString();
                        if (!string.IsNullOrEmpty(idStr) && idStr.Length > 3)
                            return idStr;
                    }
                    return "BOSS_ACT" + Act;
                }
                catch { continue; }
            }
        }
        catch { /* map access may fail at runtime */ }
        return "";
    }
}
