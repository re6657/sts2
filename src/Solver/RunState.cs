using System;
using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Runs;

namespace TokenSpire2.Solver;

/// <summary>
/// Tracks persistent run state across decisions.
/// Updated after every decision (card picked, relic obtained, shop purchase, etc.).
/// Provides context for all deciders to make informed, non-random choices.
/// </summary>
public class RunState
{
    public string Character { get; private set; } = "IRONCLAD";
    public int CurrentHp { get; private set; }
    public int MaxHp { get; private set; }
    public int Gold { get; private set; }
    public int Floor { get; private set; }
    public int Act { get; private set; }

    // Deck tracking
    public List<string> DeckCardIds { get; private set; } = new();
    public int AttackCount { get; private set; }
    public int SkillCount { get; private set; }
    public int PowerCount { get; private set; }
    public int TotalCardCount => DeckCardIds.Count;

    // Cost curve analysis
    public int HighCostCardCount { get; private set; }     // Cards costing 2+
    public int ZeroCostCardCount { get; private set; }     // Cards costing 0 (non-X)
    public int DrawCardCount { get; private set; }         // Cards that draw additional cards

    // Relics
    public List<string> RelicIds { get; private set; } = new();

    // Potions
    public int PotionSlotCount { get; private set; }
    public int PotionCount { get; private set; }
    public List<string> PotionIds { get; private set; } = new();

    // Run-level flags
    public bool HasEnergyRelic { get; private set; }
    public bool HasStrengthScaling { get; private set; }
    public bool HasDexterityScaling { get; private set; }
    public bool HasExhaustSynergy { get; private set; }    // Feel No Pain, Dark Embrace, Corruption
    public bool HasBlockSynergy { get; private set; }      // Barricade, Entrench, Body Slam
    public bool HasSustainRelic { get; private set; }      // Burning Blood, Meat on the Bone, etc.
    public bool HasSelfDamageSynergy { get; private set; } // Rupture, Hemokinesis, etc.

    // ── Non-Ironclad synergy flags ───────────────────────────────────────
    public bool HasPoisonSynergy { get; private set; }     // Noxious Fumes, Catalyst, Envenom (Silent)
    public bool HasDiscardSynergy { get; private set; }    // Tactician, Reflex, Tools of the Trade (Silent)
    public bool HasOrbSynergy { get; private set; }        // Electrodynamics, Loop, Capacitor (Defect)
    public bool HasFocusScaling { get; private set; }      // Defragment, Biased Cognition, Consume (Defect)
    public int OrbCount { get; private set; }              // Current number of channeled orbs (Defect)
    public int FocusStat { get; private set; }             // Current Focus value (Defect)
    public bool HasStarSynergy { get; private set; }       // Genesis, Child of the Stars, Arsenal (Necro/Regent)

    // ── New diagnostic fields (Phases 15-22) ─────────────────────────────────

    /// <summary>Number of cards that generate energy.</summary>
    public int CountEnergyCards { get; private set; }

    /// <summary>Number of cards that deal AOE damage.</summary>
    public int CountAoeCards { get; private set; }

    /// <summary>Number of cards that provide ongoing scaling (powers, strength, etc.).</summary>
    public int CountScalingCards { get; private set; }

    /// <summary>Number of basic Strike-type cards remaining.</summary>
    public int CountBasicStrikes { get; private set; }

    /// <summary>Number of basic Defend-type cards remaining.</summary>
    public int CountBasicDefends { get; private set; }

    /// <summary>Number of upgraded cards in the deck.</summary>
    public int CountUpgradedCards { get; private set; }

    /// <summary>True if any basic-named card has extra effects (enchanted).</summary>
    public bool HasEnchantedBasicCard { get; private set; }

    /// <summary>Estimated remaining campfires in the current act.</summary>
    public int EstimatedRemainingCampfires { get; private set; }

    /// <summary>Detected act boss name (empty if unknown).</summary>
    public string ActBoss { get; private set; } = "";

    // ── Computed diagnostics ─────────────────────────────────────────────────

    /// <summary>Total number of basic cards (Strikes + Defends).</summary>
    public int TotalBasicCards => CountBasicStrikes + CountBasicDefends;

    /// <summary>Ratio of basic cards to total deck.</summary>
    public float BasicCardRatio => TotalCardCount > 0
        ? (float)TotalBasicCards / TotalCardCount : 0f;

    /// <summary>Draw card density: ratio of draw cards to total deck.</summary>
    public float DrawDensity => TotalCardCount > 0
        ? (float)DrawCardCount / TotalCardCount : 0f;

    /// <summary>Energy card density: ratio of energy cards to total deck.</summary>
    public float EnergyDensity => TotalCardCount > 0
        ? (float)CountEnergyCards / TotalCardCount : 0f;

    /// <summary>True if the engine (draw + energy) is "closed" — both present.</summary>
    public bool IsEngineClosed => DrawCardCount > 0 && CountEnergyCards > 0;

    /// <summary>Average base damage per card in the deck (approximate).</summary>
    public float AvgDamagePerCard { get; private set; }

    /// <summary>Average base block per card in the deck (approximate).</summary>
    public float AvgBlockPerCard { get; private set; }

    // ── Draw card detection set ─────────────────────────────────────────────

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

    // ── Energy card detection set ────────────────────────────────────────────

    private static readonly HashSet<string> _energyCardIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "OFFERING", "BLOODLETTING", "SEEING_RED", "SENTINEL",
        "ADRENALINE", "TACTICIAN", "CONCENTRATE",
        "TURBO", "DOUBLE_ENERGY", "RECYCLE", "AGGREGATE", "FUSION",
        "CHARGE", "HAMMER_TIME", "MANUFACTURING", "AUTOMATION",
        "CORRUPTION", "BERSERK", "DEVA_FORM",
    };

    // ── AOE card detection set ───────────────────────────────────────────────

    private static readonly HashSet<string> _aoeCardIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "CLEAVE", "WHIRLWIND", "IMMOLATE", "THUNDERCLAP", "COMBUST",
        "DIE_DIE_DIE", "CORPSE_EXPLOSION", "ALL_OUT_ATTACK", "NOXIOUS_FUMES",
        "ELECTRODYNAMICS", "DOOM_AND_GLOOM", "HYPER_BEAM", "TEMPEST",
        "CONFLUENCE", "BOMBARDMENT", "STAR_EXTINGUISH", "DEFILE",
        "FLEA", "SCRAPE", "SWEEPING_BEAM", "DAZZLING_ENTRANCE",
    };

    // ── Scaling card detection set (powers that provide ongoing combat value) ─

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

    // ── Basic card name patterns ─────────────────────────────────────────────

    /// <summary>
    /// Non-Strike/Defend starter cards for every character.
    /// These are the unique starter cards each character begins with.
    /// Strike/Defend variants are handled by prefix matching — do NOT add them here.
    /// Used by CardGridDecider.IsBasicCard, GetEnchantedBonus, and IroncladSolver.IsBasicCardByName.
    /// </summary>
    public static readonly HashSet<string> NonStrikeDefendStarters = new(StringComparer.OrdinalIgnoreCase)
    {
        // Ironclad
        "BASH",
        // Silent
        "NEUTRALIZE", "SURVIVOR",
        // Defect
        "ZAP", "DUALCAST",
        // Necrobinder — TODO: verify starter card IDs from game data
        // "NECRO_MASTERY",  // likely but unconfirmed
        // Regent — TODO: verify starter card IDs from game data
        // "GENESIS",         // likely but unconfirmed
    };

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
    /// This is the single source of truth for basic card detection across all characters.
    /// </summary>
    public static bool IsAnyBasicCardId(string cardId)
    {
        if (string.IsNullOrEmpty(cardId)) return false;
        return IsBasicStrikeName(cardId) || IsBasicDefendName(cardId)
            || NonStrikeDefendStarters.Contains(cardId);
    }

    /// <summary>
    /// Returns true if a basic-named card has extra effects beyond its vanilla stats,
    /// indicating it has been enchanted or is a special variant.
    /// </summary>
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

        // ── Compute Act from floor ──────────────────────────────────────
        // STS2 floor ranges (approximate — adjusted from STS1):
        // Act 1: 1–16, Act 2: 17–33, Act 3: 34+
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

                // Cost curve analysis
                HighCostCardCount = cardList.Count(c =>
                    !c.EnergyCost.CostsX && c.EnergyCost.Canonical >= 2);
                ZeroCostCardCount = cardList.Count(c =>
                    !c.EnergyCost.CostsX && c.EnergyCost.Canonical == 0);
                DrawCardCount = cardList.Count(c =>
                    _drawCardIds.Contains(c.Id.Entry));

                // ── New diagnostics (Phases 15-22) ──────────────────────
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

                // Estimate remaining campfires in current act
                EstimatedRemainingCampfires = EstimateRemainingCampfires();

                // Try to detect act boss from map
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

        // ── Synergy detection from relics + deck ────────────────────────
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
            id.Contains("Rupture")) // also check deck for Rupture
            || DeckCardIds.Any(id =>
                id.Contains("RUPTURE", StringComparison.OrdinalIgnoreCase) ||
                id.Contains("HEMOKINESIS", StringComparison.OrdinalIgnoreCase) ||
                id.Contains("BLOODLETTING", StringComparison.OrdinalIgnoreCase) ||
                id.Contains("OFFERING", StringComparison.OrdinalIgnoreCase) ||
                id.Contains("BRUTALITY", StringComparison.OrdinalIgnoreCase) ||
                id.Contains("COMBUST", StringComparison.OrdinalIgnoreCase));

        // ── Non-Ironclad synergy detection ────────────────────────────
        HasPoisonSynergy = RelicIds.Any(id =>
            id.Contains("Snecko") && id.Contains("Skull")) // Snecko Skull
            || DeckCardIds.Any(id =>
                id.Contains("NOXIOUS_FUMES", StringComparison.OrdinalIgnoreCase) ||
                id.Contains("CATALYST", StringComparison.OrdinalIgnoreCase) ||
                id.Contains("ENVENOM", StringComparison.OrdinalIgnoreCase) ||
                id.Contains("CORPSE_EXPLOSION", StringComparison.OrdinalIgnoreCase) ||
                id.Contains("DEADLY_POISON", StringComparison.OrdinalIgnoreCase) ||
                id.Contains("BOUNCING_FLASK", StringComparison.OrdinalIgnoreCase));

        HasDiscardSynergy = RelicIds.Any(id =>
            id.Contains("Bandages") || id.Contains("Tingsha")) // Tough Bandages, Tingsha
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
            id.Contains("Data")) // Data Disk
            || DeckCardIds.Any(id =>
                id.Contains("DEFRAGMENT", StringComparison.OrdinalIgnoreCase) ||
                id.Contains("BIASED_COGNITION", StringComparison.OrdinalIgnoreCase) ||
                id.Contains("CONSUME", StringComparison.OrdinalIgnoreCase));

        // ── Orb count & Focus (Defect) — read from active combat state ──
        OrbCount = 0;
        FocusStat = 0;
        try
        {
            var creature = player.Creature;
            if (creature != null)
            {
                // Read Focus from powers
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
                // Read orb count from orb queue
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

    /// <summary>HP ratio: 0.0 = dead, 1.0 = full.</summary>
    public float HpRatio => MaxHp > 0 ? (float)CurrentHp / MaxHp : 0f;

    /// <summary>True if HP is critically low.</summary>
    public bool IsHpLow => HpRatio < 0.4f;

    /// <summary>True if HP is near full.</summary>
    public bool IsHpHigh => HpRatio > 0.70f;

    /// <summary>True if the deck is large (bad for consistency).</summary>
    public bool IsDeckLarge => TotalCardCount > 25;

    /// <summary>True if we have enough gold for a meaningful purchase.</summary>
    public bool HasGoldForShop => Gold >= 100;

    /// <summary>True if the deck desperately needs attack density.</summary>
    public bool NeedsAttacks => AttackCount < 4 && TotalCardCount > 5;

    /// <summary>True if the deck desperately needs block.</summary>
    public bool NeedsBlock => SkillCount < 3 && TotalCardCount > 5;

    /// <summary>Ratio of high-cost cards (2+) in deck.</summary>
    public float HighCostRatio => TotalCardCount > 0
        ? (float)HighCostCardCount / TotalCardCount : 0f;

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

    /// <summary>Number of open potion slots.</summary>
    public int OpenPotionSlots => Math.Max(0, PotionSlotCount - PotionCount);

    // ── Diagnostic helpers ────────────────────────────────────────────────────

    /// <summary>
    /// Rough estimate of remaining campfires in the current act.
    /// Assumes ~1 campfire per 5 floors.
    /// </summary>
    private int EstimateRemainingCampfires()
    {
        int floorInAct = Act switch
        {
            1 => Floor,
            2 => Floor - 16,
            3 => Floor - 33,
            _ => Floor
        };
        int actEndFloor = Act switch
        {
            1 => 16,
            2 => 33,
            3 => 50, // approximate
            _ => 50
        };
        int remainingFloors = Math.Max(0, actEndFloor - floorInAct);
        // Rough: ~1 campfire per 5 floors
        return Math.Max(0, remainingFloors / 5);
    }

    /// <summary>
    /// Try to detect the act boss from the map.
    /// Returns empty string if unknown.
    /// </summary>
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
                    // Check the Point's type for "Boss" (same pattern as MapDecider.NodeTypeName)
                    var ptProp = point.GetType().GetProperty("Point");
                    if (ptProp == null) continue;
                    var ptValue = ptProp.GetValue(point);
                    if (ptValue == null) continue;

                    string typeName = ptValue.GetType().Name;
                    if (!typeName.Contains("Boss", StringComparison.OrdinalIgnoreCase))
                        continue;

                    // Try to extract the boss encounter ID
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
