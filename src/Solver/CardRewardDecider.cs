using System;
using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;

using TokenSpire2.Core;

namespace TokenSpire2.Solver;

/// <summary>
/// Card reward evaluation with strategic depth matching experienced player heuristics.
///
/// Core principles (from STS strategy guide):
///   1. Don't pick every reward — skip 40-60% of offers. A thinner deck is more consistent.
///   2. Act-aware: Act 1 needs frontload damage, Act 2 needs AOE/block, Act 3 needs scaling.
///   3. Deck synergy: strength scaling → multi-hit bonus; exhaust → exhaust card bonus.
///   4. Redundancy penalty: third copies of unique cards have diminishing returns.
///   5. Cost curve balance: too many high-cost cards → prioritize low-cost.
///   6. Card rarity ≠ card quality — common Pommel Strike can beat rare Demon Form.
///   7. Dynamic skip threshold: the larger/better the deck, the pickier we get.
/// </summary>
public static class CardRewardDecider
{
    // ── Helper sets ─────────────────────────────────────────────────────────

    /// <summary>Cards that draw additional cards (synergy with deck velocity).</summary>
    internal static readonly HashSet<string> DrawCardSet = new(StringComparer.OrdinalIgnoreCase)
    {
        "POMMEL_STRIKE", "SHRUG_IT_OFF", "BURNING_PACT", "BATTLE_TRANCE",
        "OFFERING", "WARCRY", "BACKFLIP", "DAGGER_THROW", "ESCAPE_PLAN",
        "ACROBATICS", "CALCULATED_GAMBLE", "EXPERTISE", "QUICK_SLASH",
        "HEEL_HOOK", "DROP_KICK", "SKIM", "COMPILE_DRIVER", "COOLHEADED",
        "OVERCLOCK", "REBOUND", "FTL", "SCRAPE", "DREDGE", "FETCH", "PARSE",
        "PILLAGE", "EXPECT_A_FIGHT", "SPITE", "STOKE", "HEADBUTT",
        "GRACE", "CONFESS",
    };

    /// <summary>Multi-hit attacks that scale exceptionally well with Strength.</summary>
    private static readonly HashSet<string> MultiHitSet = new(StringComparer.OrdinalIgnoreCase)
    {
        "TWIN_STRIKE", "SWORD_BOOMERANG", "HEAVY_BLADE", "PUMMEL",
        "RIDDLE_WITH_HOLES", "BARRAGE", "TEMPEST", "WHIRLWIND",
        "SKEWER", "FLEA", "SLICE", "ENDLESS_AGONY",
    };

    /// <summary>Cards with exhaust mechanics (synergy with Feel No Pain / Dark Embrace).</summary>
    private static readonly HashSet<string> ExhaustCardSet = new(StringComparer.OrdinalIgnoreCase)
    {
        "TRUE_GRIT", "BURNING_PACT", "SEVER_SOUL", "SECOND_WIND",
        "FIEND_FIRE", "HAVOC", "SENTINEL", "PURITY",
        "RECYCLE", "TURBO", "OFFERING", "EXHUME", "CORRUPTION",
    };

    /// <summary>Self-damage / HP-loss cards (synergy with Rupture).</summary>
    private static readonly HashSet<string> SelfDamageSet = new(StringComparer.OrdinalIgnoreCase)
    {
        "HEMOKINESIS", "BLOODLETTING", "OFFERING", "BRUTALITY",
        "COMBUST", "RUPTURE", "BLOOD_WALL",
    };

    /// <summary>Poison-applying cards (synergy with Silent poison engine).</summary>
    private static readonly HashSet<string> PoisonCardSet = new(StringComparer.OrdinalIgnoreCase)
    {
        "DEADLY_POISON", "BOUNCING_FLASK", "NOXIOUS_FUMES", "CATALYST",
        "CORPSE_EXPLOSION", "ENVENOM", "CORROSIVE_WAVE", "POISONED_STAB",
        "FLASK", "VENOMOLOGY",
    };

    /// <summary>Discard-synergy cards (Silent engine).</summary>
    private static readonly HashSet<string> DiscardCardSet = new(StringComparer.OrdinalIgnoreCase)
    {
        "TACTICIAN", "REFLEX", "PREPARED", "CALCULATED_GAMBLE",
        "CONCENTRATE", "TOOLS_OF_THE_TRADE", "ACROBATICS",
        "DAGGER_THROW", "SURVIVOR", "EXPERTISE", "STORM_OF_STEEL",
    };

    /// <summary>Orb-channelling cards (Defect synergy).</summary>
    private static readonly HashSet<string> OrbCardSet = new(StringComparer.OrdinalIgnoreCase)
    {
        "GLACIER", "CHILL", "COLD_SNAP", "BALL_LIGHTNING",
        "DARKNESS", "RAINBOW", "CHAOS", "FUSION", "ZAP",
        "ELECTRODYNAMICS", "TEMPEST", "COOLHEADED",
    };

    /// <summary>Focus-scaling cards (Defect).</summary>
    private static readonly HashSet<string> FocusCardSet = new(StringComparer.OrdinalIgnoreCase)
    {
        "DEFRAGMENT", "BIASED_COGNITION", "CONSUME",
    };

    /// <summary>Star-synergy cards (Necrobinder/Regent).</summary>
    private static readonly HashSet<string> StarCardSet = new(StringComparer.OrdinalIgnoreCase)
    {
        "CHILD_OF_THE_STARS", "ARSENAL", "GENESIS", "THE_SEALED_THRONE",
        "NECRO_MASTERY", "REAPER_FORM", "SPIRIT_OF_ASH", "VOID_FORM",
        "SANCTIFY", "CHARGE", "HAMMER_TIME", "FURNACE",
        "FRIENDSHIP", "INVOKE", "CALCIFY", "DEATH_MARCH",
        "LETHALITY", "BORROWED_TIME", "SOUL_STORM", "PAGESTORM",
    };

    /// <summary>Cards that are perfectly fine in multiples (stackable).</summary>
    private static readonly HashSet<string> StackableCards = new(StringComparer.OrdinalIgnoreCase)
    {
        "ANGER", "CLAW", "SHRUG_IT_OFF", "POMMEL_STRIKE",
        "SLICE", "DEFLECT", "ESCAPE_PLAN", "STEAM_BARRIER",
        "DODGE_AND_ROLL", "CLOAK_AND_DAGGER", "COLD_SNAP",
        "COOLHEADED", "BALL_LIGHTNING", "TURBO", "OVERCLOCK",
        "FLAME_BARRIER", "FLICK_FLACK", "RAGE", "INFLAME",
    };

    /// <summary>Premium Act 1 attack cards that trivialize early elites.</summary>
    private static readonly HashSet<string> PremiumAct1Attacks = new(StringComparer.OrdinalIgnoreCase)
    {
        "CARNAGE", "IMMOLATE", "BLUDGEON", "UPPERCUT", "HEMOKINESIS",
        "POMMEL_STRIKE", "IRON_WAVE", "TWIN_STRIKE", "BASH",
        "DASH", "GLASS_KNIFE", "PREDATOR", "SUNDER", "DOOM_AND_GLOOM",
        "BALL_LIGHTNING", "COLD_SNAP", "COMPILE_DRIVER",
    };

    // ── Phase 15-22 classification sets ──────────────────────────────────────

    /// <summary>Cards that generate energy (for Phase 15 gap diagnosis + Phase 18 engine closure).</summary>
    private static readonly HashSet<string> EnergyCardSet = new(StringComparer.OrdinalIgnoreCase)
    {
        "OFFERING", "BLOODLETTING", "SEEING_RED", "SENTINEL",
        "ADRENALINE", "TACTICIAN", "CONCENTRATE",
        "TURBO", "DOUBLE_ENERGY", "RECYCLE", "AGGREGATE", "FUSION",
        "CHARGE", "HAMMER_TIME", "MANUFACTURING", "AUTOMATION",
        "CORRUPTION", "BERSERK", "DEVA_FORM",
    };

    /// <summary>Cards that deal AOE damage (for Phase 15 gap diagnosis).</summary>
    private static readonly HashSet<string> AoeCardSet = new(StringComparer.OrdinalIgnoreCase)
    {
        "CLEAVE", "WHIRLWIND", "IMMOLATE", "THUNDERCLAP", "COMBUST",
        "DIE_DIE_DIE", "CORPSE_EXPLOSION", "ALL_OUT_ATTACK", "NOXIOUS_FUMES",
        "ELECTRODYNAMICS", "DOOM_AND_GLOOM", "HYPER_BEAM", "TEMPEST",
        "CONFLUENCE", "BOMBARDMENT", "STAR_EXTINGUISH", "DEFILE",
        "FLEA", "SCRAPE", "SWEEPING_BEAM", "DAZZLING_ENTRANCE",
    };

    /// <summary>Cards that provide scaling (strength, dex, focus, poison, etc.) for Phase 15.</summary>
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

    /// <summary>"High-potential" future-investment cards — weak now, strong later. For Phase 16.</summary>
    private static readonly HashSet<string> HighPotentialCardSet = new(StringComparer.OrdinalIgnoreCase)
    {
        "DEMON_FORM", "BARRICADE", "CORRUPTION", "DARK_EMBRACE", "EVOLVE",
        "JUGGERNAUT", "BERSERK", "RUPTURE", "FEEL_NO_PAIN", "FIRE_BREATHING",
        "WRAITH_FORM", "ENVENOM", "NOXIOUS_FUMES", "AFTERIMAGE", "ACCURACY",
        "TOOLS_OF_THE_TRADE", "WELL_LAID_PLANS", "NIGHTMARE", "BURST",
        "ECHO_FORM", "CREATIVE_AI", "ELECTRODYNAMICS", "DEFRAGMENT",
        "BIASED_COGNITION", "CONSUME", "CAPACITOR", "LOOP", "HEATSINKS",
        "NECRO_MASTERY", "SPIRIT_OF_ASH", "LETHALITY", "DEATH_MARCH",
        "CHILD_OF_THE_STARS", "ARSENAL", "VOID_FORM", "THE_SEALED_THRONE",
        "CORROSIVE_WAVE", "COMPRESS", "SCATTER_CANNON",
    };

    /// <summary>Transition cards — good early, fall off late. For Phase 17.</summary>
    private static readonly HashSet<string> TransitionCardSet = new(StringComparer.OrdinalIgnoreCase)
    {
        "CARNAGE", "IMMOLATE", "BLUDGEON", "HEMOKINESIS", "IRON_WAVE",
        "TWIN_STRIKE", "BASH", "CLEAVE", "THUNDERCLAP", "SEVER_SOUL",
        "DASH", "GLASS_KNIFE", "PREDATOR", "ALL_OUT_ATTACK", "DIE_DIE_DIE",
        "SUNDER", "DOOM_AND_GLOOM", "HYPER_BEAM", "BALL_LIGHTNING",
        "COLD_SNAP", "COMPILE_DRIVER", "SWEEPING_BEAM",
        "FLYING_SWORD", "RAPID_FIRE", "DEFILE", "BONE_SHARDS",
        "STAR_EXTINGUISH", "BOMBARDMENT", "FLEA", "SLICE",
    };

    /// <summary>Win-condition cards — scale into late-game dominance. For Phase 17.</summary>
    private static readonly HashSet<string> WinConditionCardSet = new(StringComparer.OrdinalIgnoreCase)
    {
        "DEMON_FORM", "BARRICADE", "CORRUPTION", "DARK_EMBRACE",
        "WRAITH_FORM", "ENVENOM", "NOXIOUS_FUMES", "AFTERIMAGE", "NIGHTMARE",
        "ECHO_FORM", "CREATIVE_AI", "ELECTRODYNAMICS", "DEFRAGMENT",
        "BIASED_COGNITION", "CONSUME",
        "NECRO_MASTERY", "SPIRIT_OF_ASH", "LETHALITY", "REAPER_FORM",
        "CHILD_OF_THE_STARS", "ARSENAL", "VOID_FORM", "THE_SEALED_THRONE",
        "CORROSIVE_WAVE", "COMPRESS", "SCATTER_CANNON",
        "JUGGERNAUT", "FEEL_NO_PAIN", "LIMIT_BREAK",
    };

    /// <summary>Hybrid cards — good throughout the game. For Phase 17.</summary>
    private static readonly HashSet<string> HybridCardSet = new(StringComparer.OrdinalIgnoreCase)
    {
        "POMMEL_STRIKE", "SHRUG_IT_OFF", "BURNING_PACT", "BATTLE_TRANCE",
        "OFFERING", "TRUE_GRIT", "SECOND_WIND", "ARMAMENTS", "FLAME_BARRIER",
        "BACKFLIP", "ACROBATICS", "CALCULATED_GAMBLE", "EXPERTISE",
        "CLOAK_AND_DAGGER", "DODGE_AND_ROLL", "ESCAPE_PLAN",
        "COOLHEADED", "SKIM", "COMPILE_DRIVER", "TURBO", "OVERCLOCK",
        "DREDGE", "FETCH", "PARSE", "PILLAGE", "SPITE", "STOKE",
        "CHARGE", "HAMMER_TIME", "FURNACE", "ROYALTIES", "GENESIS",
        "GRACE", "CONFESS",
    };

    /// <summary>Cards whose upgrade is transformative ("must-upgrade"). For Phase 19.</summary>
    private static readonly HashSet<string> TransformativeUpgradeSet = new(StringComparer.OrdinalIgnoreCase)
    {
        "ARMAMENTS", "BURNING_PACT", "TRUE_GRIT", "BATTLE_TRANCE",
        "CALCULATED_GAMBLE", "WELL_LAID_PLANS", "TOOLS_OF_THE_TRADE",
        "DEFRAGMENT", "BIASED_COGNITION", "CONSUME",
        "NECRO_MASTERY", "SPIRIT_OF_ASH", "LETHALITY",
        "CHILD_OF_THE_STARS", "ARSENAL", "VOID_FORM",
    };

    /// <summary>
    /// Cards that should NEVER be picked under any circumstances.
    /// These are player-specified hard bans — the bot will never select
    /// these cards from rewards, shops, or any screen.
    /// </summary>
    private static readonly HashSet<string> HardBannedCards = new(StringComparer.OrdinalIgnoreCase)
    {
        "PACTS_END",          // 契约终结 — user request: never pick
        "EXPECT_A_FIGHT",     // 跃跃欲试 — user request: never pick
        "HOWL_FROM_BEYOND",   // 彼岸咆哮 — user request: never pick
    };

    /// <summary>Colorless premium cards with fixed bonus values. For Phase 21.</summary>
    private static readonly Dictionary<string, double> ColorlessPremiumScores = new(StringComparer.OrdinalIgnoreCase)
    {
        ["FLYING_SWORD"] = 22.0,
        ["GRAND_PRIZE"] = 20.0,
        ["GEMSTONE"] = 18.0,
        ["RAPID_FIRE"] = 12.0,
        ["FLASH_OF_STEEL"] = 10.0,
        ["DAZZLING_ENTRANCE"] = 12.0,
        ["ENDLESS_PUNISHMENT"] = 11.0,
        ["DISCOVERY"] = 10.0,
        ["MANUFACTURING"] = 14.0,
        ["AUTOMATION"] = 13.0,
    };

    // ── Main entry point ────────────────────────────────────────────────────

    public static bool Decide(RunState state)
    {
        var screen = NOverlayStack.Instance?.Peek() as NCardRewardSelectionScreen;
        if (screen == null) return false;

        var holders = AutoSlayHelpers.FindAll<NCardHolder>(screen);
        if (holders.Count == 0) return false;

        var skipBtn = AutoSlayHelpers.FindFirst<NCardRewardAlternativeButton>(screen);

        // Score all cards
        var scored = holders.Select((h, i) =>
        {
            var card = h.CardModel;
            double score = ScoreCard(card, state);
            string label = $"{card?.Id.Entry ?? "?"} (cost={CardCost(card)})";
            return (index: i, score, label, holder: h);
        }).OrderByDescending(x => x.score).ToList();

        var best = Tiebreaker.PickBestFromSorted(scored, x => x.score);

        // ── Dual-threshold system: Cards must pass BOTH checks to qualify ──
        //
        // CHECK 1 — Relative threshold (top-25% of best card's score):
        //   Card must score >= 75% of the best card in this offer.
        //   Prevents picking mediocre cards when one standout exists.
        //
        // CHECK 2 — Absolute threshold (deck-size + act-based):
        //   Card must score above a deck-contextual bar that rises as the
        //   deck grows larger and the run progresses. Prevents picking
        //   "best of a bad bunch" in strong late-game decks.
        //
        // A card must pass BOTH to qualify — the higher bar wins.
        var st = SolverParams.Instance.CardReward.SkipThreshold;
        double maxScore = scored.Count > 0 ? scored[0].score : 0;
        double relativeThreshold = maxScore * st.RelativeThresholdMultiplier;
        const double ABSOLUTE_FLOOR = 10.0;
        double effectiveRelThreshold = Math.Max(relativeThreshold, ABSOLUTE_FLOOR);

        // Revived absolute threshold: accounts for deck size, act, deck quality
        double absoluteThreshold = CalculateSkipThreshold(state);
        double finalThreshold = Math.Max(effectiveRelThreshold, absoluteThreshold);

        // Find all cards that pass the dual threshold
        var qualified = scored.Where(s => s.score >= finalThreshold).ToList();
        bool hasQualified = qualified.Count > 0;

        // ── Debug: log all card scores with both thresholds ──
        string scoreBreakdown = string.Join(" | ",
            scored.Take(5).Select(s => $"{s.label}:{s.score:F0}"));
        MainFile.Logger.Info(
            $"[CardReward] Deck={state.TotalCardCount}A/{state.AttackCount}S/{state.SkillCount}P " +
            $"Act={state.Act} HP={state.HpRatio:P0} MaxScore={maxScore:F0} " +
            $"RelThreshold={effectiveRelThreshold:F0} AbsThreshold={absoluteThreshold:F0} FinalThreshold={finalThreshold:F0} | {scoreBreakdown}");

        // Skip if no card qualifies
        if (!hasQualified && skipBtn != null)
        {
            string reason = $"SKIP all (best={best.label} score={best.score:F0} < " +
                           $"finalThreshold={finalThreshold:F0} [rel={effectiveRelThreshold:F0} abs={absoluteThreshold:F0}]), " +
                           $"deck={state.TotalCardCount} cards Act{state.Act}";
            DecisionLogger.LogDecision(
                GameScreen.OVERLAY_CARD_REWARD, "CardReward",
                scored.Select(s => new DecisionLogger.OptionScore
                {
                    Index = s.index, Label = s.label, Score = s.score
                }).ToList(),
                -1, "SKIP", reason);
            MainFile.Logger.Info($"[CardRewardDecider] {reason}");
            skipBtn.ForceClick();
            return true;
        }

        var bestQualified = qualified[0];
        string pickReason = $"PICK {bestQualified.label} score={bestQualified.score:F0} " +
                           $"(relThreshold={effectiveRelThreshold:F0} absThreshold={absoluteThreshold:F0} max={maxScore:F0}) " +
                           $"deck={state.TotalCardCount}C Act{state.Act} " +
                           $"({state.AttackCount}A/{state.SkillCount}S/{state.PowerCount}P)";
        DecisionLogger.LogDecision(
            GameScreen.OVERLAY_CARD_REWARD, "CardReward",
            scored.Select(s => new DecisionLogger.OptionScore
            {
                Index = s.index, Label = s.label, Score = s.score
            }).ToList(),
            bestQualified.index, bestQualified.label, pickReason);

        MainFile.Logger.Info($"[CardRewardDecider] {pickReason}");
        bestQualified.holder.EmitSignal(NCardHolder.SignalName.Pressed, bestQualified.holder);
        return true;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // DYNAMIC SKIP THRESHOLD
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Calculate the score threshold below which we skip all cards.
    /// Grows with deck size and act number — larger/better decks are pickier.
    ///
    /// Formula: baseThreshold + deckSizePenalty + actPenalty - emergencyRelief
    /// </summary>
    private static double CalculateSkipThreshold(RunState state)
    {
        var st = SolverParams.Instance.CardReward.SkipThreshold;

        double threshold = st.Base;

        // Deck size pressure
        if (state.TotalCardCount > 12)
            threshold += (state.TotalCardCount - 12) * st.PerDeckSizeAbove12;

        // Deck-size wall at 20: strong disincentive to grow beyond 20 cards
        if (state.TotalCardCount > 20)
            threshold += (state.TotalCardCount - 20) * st.PerDeckSizeAbove20;

        // Act-based selectivity
        threshold += state.Act * st.PerAct;

        // Emergency relief
        if (state.IsHpLow)
            threshold -= st.EmergencyLowHpReduction;

        if (state.Act == 1 && state.AttackCount < st.MinThreshold && state.TotalCardCount > 3)
            threshold -= st.EmergencyAct1NoAttackReduction;

        if (state.SkillCount < st.EmergencyNoBlockReduction && state.TotalCardCount > 5)
            threshold -= st.EmergencyNoBlockReduction;

        // Deck quality bonus
        if (state.HasStrengthScaling)
            threshold += st.HasStrengthSynergyBonus;
        if (state.HasExhaustSynergy)
            threshold += st.HasExhaustSynergyBonus;
        if (state.HasBlockSynergy)
            threshold += st.HasBlockSynergyBonus;

        return Math.Max(st.MinThreshold, threshold);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // CARD SCORING — 12-phase evaluation
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Score a card for deck building. Higher = better pick.
    /// Negative or below-threshold = probably skip.
    /// Curse = -1000 (never pick).
    /// </summary>
    public static double ScoreCard(CardModel? card, RunState state)
    {
        if (card == null) return -1000;

        string cardIdUpper = card.Id.Entry?.ToUpperInvariant() ?? "";

        // Curse/Status: NEVER pick
        if (card.Type == CardType.Curse || card.Type == CardType.Status)
            return -1000;

        // ── HARD BAN: player-specified never-pick cards ─────────────────
        if (HardBannedCards.Contains(cardIdUpper))
        {
            MainFile.Logger.Info($"[CardReward] HARD-BANNED {cardIdUpper}: player requested never pick");
            return -9999; // far below any threshold, guaranteed skip
        }

        // ── Read card effects early (needed for MAX_COPIES clamp + scoring) ──
        var fx = CardEffectReader.ReadEffects(card);
        string cardIdLower = card.Id.Entry?.ToLowerInvariant() ?? "";

        // ── MAX_COPIES hard cap (ForgottenArbiter pattern) ───────────────
        // If we already have the maximum recommended copies, hard-skip.
        // This prevents deck bloat with redundant copies of unique-effect cards.
        int currentCopies = state.CountCardsById(cardIdUpper);
        var cfg = CharacterConfig.Create(state.Character);
        int maxCopies = cfg.GetMaxCopies(cardIdUpper);

        // ── Clamp: non-draw, non-energy, non-exhaust → max 1 copy ──────
        // Cards that can't cycle, can't generate energy, and don't exhaust
        // are pure stat-sticks — a second copy just bloats the deck.
        // Exception: StackableCards (explicitly designed for multiples, e.g.
        // Anger self-duplicates, Claw scales with itself).
        if (!StackableCards.Contains(cardIdUpper))
        {
            bool canDraw = DrawCardSet.Contains(cardIdUpper);
            bool canGiveEnergy = EnergyCardSet.Contains(cardIdUpper) || fx.EnergyGain > 0;
            bool hasExhaust = ExhaustCardSet.Contains(cardIdUpper);
            if (!canDraw && !canGiveEnergy && !hasExhaust)
            {
                if (maxCopies > 1)
                {
                    MainFile.Logger.Info($"[CardReward] CLAMP-1 {cardIdUpper}: no draw/energy/exhaust → max 1 copy (was {maxCopies})");
                }
                maxCopies = Math.Min(maxCopies, 1);
            }
        }

        if (currentCopies >= maxCopies)
        {
            MainFile.Logger.Info($"[CardReward] HARD-SKIP {cardIdUpper}: already have {currentCopies}/{maxCopies} copies");
            return -500; // below any threshold, guaranteed skip
        }

        double score = 0;
        int cost = CardCost(card);
        var sw = SolverParams.Instance.CardReward.StageWeights;

        // ═══════════════════════════════════════════════════════════════════
        // PHASE 1: Raw stat efficiency (damage/block per energy)
        // ═══════════════════════════════════════════════════════════════════

        if (fx.BaseDamage > 0 && cost > 0)
            score += ((double)fx.BaseDamage / cost) * sw.RawEfficiencyDamagePerEnergy;
        else if (fx.BaseDamage > 0) // L4: cost must be 0 (already guarded in if)
            score += fx.BaseDamage * sw.ZeroCostDamagePerPoint;

        if (fx.BaseBlock > 0 && cost > 0)
            score += ((double)fx.BaseBlock / cost) * sw.RawEfficiencyBlockPerEnergy;
        else if (fx.BaseBlock > 0) // L4: cost must be 0 (already guarded in if)
            score += fx.BaseBlock * sw.ZeroCostBlockPerPoint;

        if (card.EnergyCost.CostsX) score += sw.XCostFlexibility;

        // ═══════════════════════════════════════════════════════════════════
        // PHASE 2: Card type baseline
        // ═══════════════════════════════════════════════════════════════════

        if (card.Type == CardType.Power || fx.IsPower)
            score += sw.CardTypePower;
        if (cost == 0)
            score += sw.CardTypeZeroCost;
        if (cost == 1)
            score += sw.CardTypeOneCost;

        // ═══════════════════════════════════════════════════════════════════
        // PHASE 3: Debuff / buff value
        // ═══════════════════════════════════════════════════════════════════

        if (fx.VulnerableStacks > 0)
            score += fx.VulnerableStacks * sw.DebuffVulnerable;
        if (fx.WeakStacks > 0)
            score += fx.WeakStacks * sw.DebuffWeak;
        if (fx.PoisonStacks > 0)
            score += fx.PoisonStacks * sw.DebuffPoison;
        if (fx.EnergyGain > 0)
            score += fx.EnergyGain * sw.BuffEnergy;
        if (fx.GrantsStrength)
            score += fx.StrengthAmount * sw.BuffStrength;
        if (fx.GrantsDexterity)
            score += fx.DexterityAmount * sw.BuffDexterity;
        if (fx.IsAoe)
            score += sw.AoeBonus;

        // ═══════════════════════════════════════════════════════════════════
        // PHASE 4: Card draw detection
        // ═══════════════════════════════════════════════════════════════════

        if (DrawCardSet.Contains(cardIdUpper))
            score += sw.DrawDetection;

        // ═══════════════════════════════════════════════════════════════════
        // PHASE 5: HP cost penalty / synergy
        // ═══════════════════════════════════════════════════════════════════

        if (fx.HpCost > 0)
        {
            if (state.HasSelfDamageSynergy || state.HasSustainRelic)
                score += fx.HpCost * sw.HpCostWithSynergyPerPoint;
            else
                score += fx.HpCost * sw.HpCostWithoutSynergyPerPoint;
        }

        // ═══════════════════════════════════════════════════════════════════
        // PHASE 6: Act-based adjustments
        // ═══════════════════════════════════════════════════════════════════

        switch (state.Act)
        {
            case 1:
                if (cost > 0 && fx.BaseDamage > 0)
                    score += ((double)fx.BaseDamage / cost) * sw.Act1DamagePerEnergyExtra; // M1: avoid integer division truncation
                if (PremiumAct1Attacks.Contains(cardIdUpper))
                    score += sw.Act1PremiumAttackBonus;
                if (cost == 2 && fx.BaseDamage >= 12)
                    score += sw.Act1TwoCost12DmgBonus;
                if (card.Type == CardType.Power && fx.BaseDamage == 0 && fx.BaseBlock == 0
                    && !fx.GrantsStrength && !fx.GrantsDexterity)
                    score += sw.Act1SlowPowerPenalty;
                if (card.Type == CardType.Skill && fx.BaseDamage == 0
                    && fx.BaseBlock > 0 && fx.VulnerableStacks == 0)
                    score += sw.Act1PureBlockPenalty;
                break;

            case 2:
                if (fx.IsAoe)
                    score += sw.Act2AoeBonus;
                if (fx.BaseBlock > 0)
                    score += sw.Act2BlockBonus;
                if (fx.WeakStacks > 0)
                    score += sw.Act2WeakBonus;
                if (card.Type == CardType.Attack && fx.BaseDamage < 10 && cost >= 2
                    && fx.VulnerableStacks == 0 && fx.WeakStacks == 0 && !fx.IsAoe)
                    score += sw.Act2BadAttackPenalty;
                break;

            case 3:
                if (card.Type == CardType.Power)
                    score += sw.Act3PowerBonus;
                if (fx.GrantsStrength)
                    score += fx.StrengthAmount * sw.Act3StrengthExtra;
                if (card.Type == CardType.Attack && fx.BaseDamage < 8 && cost >= 2
                    && fx.VulnerableStacks == 0 && fx.WeakStacks == 0)
                    score += sw.Act3BadAttackPenalty;
                if (card.Type == CardType.Attack && !fx.IsAoe
                    && fx.VulnerableStacks == 0 && fx.WeakStacks == 0
                    && !DrawCardSet.Contains(cardIdUpper) && !fx.GrantsStrength)
                    score += sw.Act3VanillaAttackPenalty;
                break;
        }

        // ═══════════════════════════════════════════════════════════════════
        // PHASE 7: Deck size pressure + small deck bonus
        // ═══════════════════════════════════════════════════════════════════

        int deckSize = state.TotalCardCount;
        if (deckSize > 20)
            score += (deckSize - 20) * sw.DeckSizeOver20PerCard;
        if (deckSize > 25)
            score += (deckSize - 25) * sw.DeckSizeOver25PerCard;
        if (deckSize > 30)
            score += (deckSize - 30) * sw.DeckSizeOver30PerCard;

        // ── Phase 7 mod: small deck bonus (精简牌库正向奖励) ──
        if (deckSize <= 12)
            score += (12 - deckSize) * sw.SmallDeckBonusPerCard;
        else if (deckSize <= 15)
            score += (15 - deckSize) * sw.ModerateDeckBonusPerCard;

        // ═══════════════════════════════════════════════════════════════════
        // PHASE 8: Deck synergy bonuses
        // ═══════════════════════════════════════════════════════════════════

        if (state.HasStrengthScaling && fx.BaseDamage > 0 && MultiHitSet.Contains(cardIdUpper))
            score += sw.StrengthSynergyMultiHit;
        if (state.HasExhaustSynergy && ExhaustCardSet.Contains(cardIdUpper))
            score += sw.ExhaustSynergyBonus;
        if (state.HasBlockSynergy && fx.BaseBlock > 0 && card.Type == CardType.Skill)
            score += sw.BlockSynergyBonus;
        if (state.HasSelfDamageSynergy && SelfDamageSet.Contains(cardIdUpper))
            score += sw.SelfDamageSynergyBonus;
        if (state.HasEnergyRelic && cost >= 2)
            score += sw.EnergyRelicHighCostBonus;

        // ── Non-Ironclad synergy bonuses ──────────────────────────────
        if (state.HasPoisonSynergy && PoisonCardSet.Contains(cardIdUpper))
            score += sw.PoisonSynergyBonus;
        if (state.HasDiscardSynergy && DiscardCardSet.Contains(cardIdUpper))
            score += sw.DiscardSynergyBonus;
        if (state.HasOrbSynergy && OrbCardSet.Contains(cardIdUpper))
            score += sw.OrbSynergyBonus;
        if (state.HasFocusScaling && FocusCardSet.Contains(cardIdUpper))
            score += sw.FocusSynergyBonus;
        if (state.HasStarSynergy && StarCardSet.Contains(cardIdUpper))
            score += sw.StarSynergyBonus;

        // ═══════════════════════════════════════════════════════════════════
        // PHASE 8b: Card combo synergy (from 250-win statistical analysis)
        // + community-verified combo boost (Phase 8b mod)
        // ═══════════════════════════════════════════════════════════════════

        double comboBonus = ComboDatabase.GetSynergyBonus(
            cardIdUpper, state.DeckCardIds, sw.ComboSynergyMultiplier);
        if (comboBonus > 0)
        {
            // Community-verified combo boost: "1+1>2" combos get extra multiplier
            double communityBoost = GetCommunityComboBoost(cardIdUpper, state.DeckCardIds);
            comboBonus *= communityBoost;
            score += comboBonus;
        }

        // ═══════════════════════════════════════════════════════════════════
        // PHASE 9: Redundancy penalty
        // ═══════════════════════════════════════════════════════════════════

        if (!StackableCards.Contains(cardIdUpper))
        {
            var rp = SolverParams.Instance.CardReward.RedundancyPenalty;
            int copies = state.CountCardsById(cardIdUpper);
            if (copies >= 1) score += rp.OneCopy;
            if (copies >= 2) score += rp.TwoOrMoreCopies;
        }

        // ═══════════════════════════════════════════════════════════════════
        // PHASE 10: Cost curve balance
        // ═══════════════════════════════════════════════════════════════════

        var cb = SolverParams.Instance.CardReward.CurveBalance;
        if (cost == 0 || cost == 1)
        {
            float highRatio = state.HighCostRatio;
            if (highRatio > cb.HighCostRatioHigh)
                score += cb.LowCostUrgentBonus;
            else if (highRatio > cb.HighCostRatioMid)
                score += cb.LowCostModerateBonus;
        }
        if (cost >= 2 && state.HighCostRatio > cb.HighCostRatioMid)
            score += cb.HighCostPenalty;
        if (cost >= 3 && !state.HasEnergyRelic && state.TotalCardCount < 15)
            score += cb.ExpensiveCardSmallDeckPenalty;
        if (cost == 0 && state.TotalCardCount > 22)
            score += cb.ZeroCostLargeDeckBonus;

        // ═══════════════════════════════════════════════════════════════════
        // PHASE 11: Type balance (attack/skill/power ratio)
        // ═══════════════════════════════════════════════════════════════════

        var tb = SolverParams.Instance.CardReward.TypeBalance;
        if (card.Type == CardType.Skill && state.SkillCount > state.AttackCount * tb.SkillAttackRatioThreshold)
            score += tb.TooManySkillsPenalty;
        if (card.Type == CardType.Attack && state.AttackCount < tb.MissingAttackThreshold && state.TotalCardCount > 4)
            score += tb.MissingAttackBonus;
        if (card.Type == CardType.Skill && fx.BaseBlock >= 7
            && state.SkillCount < tb.MissingBlockMinSkill && state.TotalCardCount > 5)
            score += tb.MissingBlockBonus;

        // ═══════════════════════════════════════════════════════════════════
        // PHASE 12: Character priority tier from CharacterConfig
        // ═══════════════════════════════════════════════════════════════════

        if (cfg.CardPriorities.TryGetValue(cardIdUpper, out int priority))
            score += (10 - priority) * 5;

        // ═══════════════════════════════════════════════════════════════════
        // PHASE 13: Stats Database weight (if available)
        // + conditional stat weight: discount stats for early large decks (Phase 13 mod)
        // ═══════════════════════════════════════════════════════════════════

        double statWeight = 1.0;
        if (state.Act == 1 && state.TotalCardCount > 15)
            statWeight = sw.StatsWeightEarlyLargeDeck;   // Act 1 large deck → low trust in stats
        else if (state.Act == 1 || state.TotalCardCount > 20)
            statWeight = sw.StatsWeightMid;              // medium trust
        // else: Act 2-3 with small/medium deck → full trust (1.0)

        double wrImpact = StatsDatabase.GetCardWrImpact(card.Id.Entry);
        score += wrImpact * sw.StatsWrImpactMultiplier * statWeight;

        double wr = StatsDatabase.GetCardWinRate(card.Id.Entry);
        if (wr > 0.30)
            score += (wr - 0.20) * sw.StatsWrAbove30Multiplier * statWeight;
        if (wr < 0.18 && wr > 0)
            score -= (0.20 - wr) * sw.StatsWrAbove30Multiplier * statWeight;

        // ═══════════════════════════════════════════════════════════════════
        // PHASE 14: Upgrade bonus
        // ═══════════════════════════════════════════════════════════════════

        if (card.IsUpgraded)
            score += sw.UpgradeBonus;

        // ═══════════════════════════════════════════════════════════════════
        // PHASE 15: Deck gap diagnosis (端口化缺口诊断)
        // ═══════════════════════════════════════════════════════════════════
        score += ScoreDeckGapDiagnosis(card, state, fx, cardIdUpper);

        // ═══════════════════════════════════════════════════════════════════
        // PHASE 16: Future investment feasibility (站未来可行性)
        // ═══════════════════════════════════════════════════════════════════
        score += ScoreFutureInvestment(card, state, cardIdUpper);

        // ═══════════════════════════════════════════════════════════════════
        // PHASE 17: Transition vs win condition (过渡牌 vs 终端牌)
        // ═══════════════════════════════════════════════════════════════════
        score += ScoreCardRole(card, state, cardIdUpper);

        // ═══════════════════════════════════════════════════════════════════
        // PHASE 18: Engine closure detection (运转闭合检测)
        // ═══════════════════════════════════════════════════════════════════
        score += ScoreEngineClosure(card, state, cardIdUpper);

        // ═══════════════════════════════════════════════════════════════════
        // PHASE 19: Upgrade pressure (敲位压力评估)
        // ═══════════════════════════════════════════════════════════════════
        score += ScoreUpgradePressure(card, state, cardIdUpper);

        // ═══════════════════════════════════════════════════════════════════
        // PHASE 20: Basic card removal progress (基础牌删除进度)
        // ═══════════════════════════════════════════════════════════════════
        score += ScoreBasicRemovalProgress(state);

        // ═══════════════════════════════════════════════════════════════════
        // PHASE 21: Colorless card quality (无色牌质量加成)
        // ═══════════════════════════════════════════════════════════════════
        score += ScoreColorlessPremium(card, cardIdUpper);

        // ═══════════════════════════════════════════════════════════════════
        // PHASE 22: Act boss counter (Act感知Boss对策)
        // ═══════════════════════════════════════════════════════════════════
        score += ScoreBossCounter(card, state, fx, cardIdUpper);

        // ═══════════════════════════════════════════════════════════════════
        // PHASE 23: Multiplayer card priority (多人模式卡牌优先)
        // In coop mode, multiplayer cards that benefit the human teammate
        // get a massive score boost to guarantee they're picked.
        // ═══════════════════════════════════════════════════════════════════
        if (MultiplayerCards.IsMultiplayerCard(cardIdUpper))
        {
            score += MultiplayerCards.PickBonus;
            string mpType = MultiplayerCards.NeedsAllyTarget(cardIdUpper) ? "Ally-target"
                : MultiplayerCards.IsAllAllies(cardIdUpper) ? "Team-wide"
                : MultiplayerCards.IsSelfTargetMP(cardIdUpper) ? "Self-buff"
                : "Enemy-debuff";
            MainFile.Logger.Info($"[CardReward] MP bonus +{MultiplayerCards.PickBonus:F0} for {cardIdUpper} ({mpType})");
        }

        return score;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // PHASE 15: Deck Gap Diagnosis (端口化缺口诊断)
    //
    // Diagnoses what the deck is missing and bonuses cards that fill those gaps.
    // Six gap types: DAMAGE, BLOCK, DRAW, ENERGY, AOE, SCALING
    // ═══════════════════════════════════════════════════════════════════════════

    private static double ScoreDeckGapDiagnosis(CardModel card, RunState state,
        CardEffectReader.CardEffects fx, string cardIdUpper)
    {
        var dg = SolverParams.Instance.CardReward.DeckGap;
        double bonus = 0;

        // 1. Damage gap — most important gap
        float avgDmg = state.AvgDamagePerCard;
        double damageUrgency = 0;
        if (avgDmg < dg.AvgDamageThresholdLow && avgDmg > 0)
            damageUrgency = dg.GapUrgencyHigh;
        else if (avgDmg < dg.AvgDamageThresholdMid && avgDmg > 0)
            damageUrgency = dg.GapUrgencyMid;

        if (damageUrgency > 0 && fx.BaseDamage > 0 && card.Type != CardType.Power)
            bonus += damageUrgency * dg.DamageBonus;

        // 2. Block gap
        float avgBlk = state.AvgBlockPerCard;
        double blockUrgency = 0;
        if (avgBlk < dg.AvgBlockThresholdLow && avgBlk > 0 && state.TotalCardCount > 10)
            blockUrgency = dg.GapUrgencyHigh;
        else if (avgBlk < dg.AvgBlockThresholdMid && avgBlk > 0)
            blockUrgency = dg.GapUrgencyMid;

        if (blockUrgency > 0 && fx.BaseBlock > 0 && card.Type != CardType.Power)
            bonus += blockUrgency * dg.BlockBonus;

        // 3. Draw gap — draw density too low
        float drawDensity = state.DrawDensity;
        double drawUrgency = 0;
        if (drawDensity < dg.DrawDensityThreshold)
            drawUrgency = dg.GapUrgencyHigh;
        else if (drawDensity < dg.DrawDensityThreshold * 2)
            drawUrgency = dg.GapUrgencyMid;

        if (drawUrgency > 0 && DrawCardSet.Contains(cardIdUpper))
            bonus += drawUrgency * dg.DrawBonus;

        // 4. Energy gap — energy density too low
        float energyDensity = state.EnergyDensity;
        double energyUrgency = 0;
        if (energyDensity < dg.EnergyDensityThreshold && state.TotalCardCount > 12)
            energyUrgency = dg.GapUrgencyHigh;
        else if (energyDensity < dg.EnergyDensityThreshold * 2)
            energyUrgency = dg.GapUrgencyMid;

        if (energyUrgency > 0 && EnergyCardSet.Contains(cardIdUpper))
            bonus += energyUrgency * dg.EnergyBonus;

        // 5. AOE gap — especially important in Act 2+
        int aoeCount = state.CountAoeCards;
        double aoeUrgency = 0;
        if (aoeCount == 0 && state.Act >= 2)
            aoeUrgency = dg.AoeGapUrgencyAct2;
        else if (aoeCount == 0 && state.Act == 1)
            aoeUrgency = dg.GapUrgencyLow;

        if (aoeUrgency > 0 && (fx.IsAoe || AoeCardSet.Contains(cardIdUpper)))
            bonus += aoeUrgency * dg.AoeBonus;

        // 6. Scaling gap — Act 3+ needs scaling
        int scalingCount = state.CountScalingCards;
        double scalingUrgency = 0;
        if (scalingCount == 0 && state.Act >= 3)
            scalingUrgency = dg.ScalingGapUrgencyAct3;
        else if (scalingCount <= 1 && state.Act >= 2)
            scalingUrgency = dg.GapUrgencyMid;

        if (scalingUrgency > 0 && ScalingCardSet.Contains(cardIdUpper))
            bonus += scalingUrgency * dg.ScalingBonus;

        return bonus;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // PHASE 16: Future Investment Feasibility (站未来可行性评估)
    //
    // Evaluates whether a "future card" (high potential, low immediate impact)
    // is worth taking based on synergy with the existing deck.
    // Replaces the overly simple Phase 6 slow-power -15 penalty.
    // ═══════════════════════════════════════════════════════════════════════════

    private static double ScoreFutureInvestment(CardModel card, RunState state, string cardIdUpper)
    {
        var fi = SolverParams.Instance.CardReward.FutureInvestment;

        // Only evaluate high-potential / future-investment cards
        if (!HighPotentialCardSet.Contains(cardIdUpper))
            return 0;

        // Calculate synergy score with existing deck
        double synergyScore = 0;

        // Check combo synergies with every card in the deck
        foreach (var existingId in state.DeckCardIds)
        {
            double s = ComboDatabase.GetSynergyBonus(
                cardIdUpper, new List<string> { existingId }, 1.0);
            synergyScore += s;
        }

        // Check relic synergies (simple keyword matching)
        foreach (var relicId in state.RelicIds)
        {
            string r = relicId.ToUpperInvariant();
            if (cardIdUpper.Contains("EXHAUST", StringComparison.OrdinalIgnoreCase) ||
                ExhaustCardSet.Contains(cardIdUpper))
            {
                if (r.Contains("CHARON") || r.Contains("DEAD") || r.Contains("BRANCH"))
                    synergyScore += 0.15;
            }
            if (ScalingCardSet.Contains(cardIdUpper) && card.Type == CardType.Power)
            {
                if (r.Contains("BIRD") || r.Contains("MUMMIFIED") || r.Contains("HAND"))
                    synergyScore += 0.15;
            }
        }

        // Classify synergy level and apply score
        if (synergyScore >= fi.MinSynergyThresholdStrong)
            return fi.StrongSynergyBonus;
        else if (synergyScore >= fi.MinSynergyThresholdWeak)
            return fi.WeakSynergyBonus;
        else if (synergyScore >= 0.05)
            return fi.BarelySynergyPenalty;
        else
            return fi.NoSynergyPenalty;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // PHASE 17: Transition vs Win Condition (过渡牌 vs 终端牌)
    //
    // Classifies cards into three roles and adjusts score based on game phase.
    // TRANSITION: good early, falls off late
    // WIN_CONDITION: weak early, dominates late
    // HYBRID: good throughout
    // ═══════════════════════════════════════════════════════════════════════════

    private static double ScoreCardRole(CardModel card, RunState state, string cardIdUpper)
    {
        var cr = SolverParams.Instance.CardReward.CardRole;

        if (TransitionCardSet.Contains(cardIdUpper))
        {
            if (state.Act == 1)
                return cr.TransitionAct1Bonus;
            else if (state.Act >= 2)
            {
                // Count how many transition cards we already have
                int transitionCount = state.DeckCardIds.Count(
                    id => TransitionCardSet.Contains(id));
                if (transitionCount < cr.MinTransitionForBase)
                    return cr.TransitionAct1Bonus * 0.33; // still can use a few
                else
                    return cr.TransitionLatePenalty;
            }
        }

        if (WinConditionCardSet.Contains(cardIdUpper))
        {
            if (state.Act == 1)
            {
                // Check if we have enough transition base to afford a win condition
                int transitionCount = state.DeckCardIds.Count(
                    id => TransitionCardSet.Contains(id));
                if (transitionCount >= cr.MinTransitionForBase && state.TotalCardCount <= 15)
                    return cr.WinConditionEarlyWithBase;
                // Otherwise, Phase 16 handles the penalty
                return 0;
            }
            else if (state.Act >= 2)
            {
                return cr.WinConditionAct2Plus;
            }
        }

        if (HybridCardSet.Contains(cardIdUpper))
            return cr.HybridAlwaysGood;

        return 0;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // PHASE 18: Engine Closure Detection (运转闭合检测)
    //
    // Detects whether the draw+energy engine is "closed" (self-sustaining).
    // When closed, bonus for complementary pieces. When open, bonus for
    // the missing half (draw or energy).
    // ═══════════════════════════════════════════════════════════════════════════

    private static double ScoreEngineClosure(CardModel card, RunState state, string cardIdUpper)
    {
        var ec = SolverParams.Instance.CardReward.EngineClosure;

        bool cardIsDraw = DrawCardSet.Contains(cardIdUpper);
        bool cardIsEnergy = EnergyCardSet.Contains(cardIdUpper);

        // Not an engine card — no score
        if (!cardIsDraw && !cardIsEnergy)
            return 0;

        bool hasDraw = state.DrawCardCount > 0;
        bool hasEnergy = state.CountEnergyCards > 0;
        bool hasBoth = hasDraw && hasEnergy;

        if (hasBoth)
        {
            // Engine is already closed — synergy bonus for complementary pieces
            if (cardIsDraw)
                return ec.ClosedDrawSynergy;
            if (cardIsEnergy)
                return ec.ClosedEnergySynergy;
        }

        // Engine is missing one half — urgent bonus for the missing piece
        if (hasDraw && !hasEnergy && cardIsEnergy)
            return ec.MissingEnergy;  // Highest priority!
        if (hasEnergy && !hasDraw && cardIsDraw)
            return ec.MissingDraw;    // Highest priority!

        // Both missing — bonus for either
        if (!hasDraw && !hasEnergy)
            return ec.MissingBoth;

        return 0;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // PHASE 19: Upgrade Pressure (敲位压力评估)
    //
    // Evaluates whether we can afford to take a card that needs an upgrade.
    // If campfire slots are tight, penalize "must-upgrade" cards.
    // ═══════════════════════════════════════════════════════════════════════════

    private static double ScoreUpgradePressure(CardModel card, RunState state, string cardIdUpper)
    {
        var up = SolverParams.Instance.CardReward.UpgradePressure;

        // Already upgraded — no pressure concern
        if (card.IsUpgraded)
            return 0;

        // Count cards that likely need upgrades (unupgraded key cards)
        int cardsNeedingUpgrade = state.DeckCardIds.Count(id =>
            (TransformativeUpgradeSet.Contains(id) || WinConditionCardSet.Contains(id))
            && !id.EndsWith("+", StringComparison.OrdinalIgnoreCase));

        int remainingCampfires = state.EstimatedRemainingCampfires;
        int upgradeSlotsAvailable = Math.Max(0, remainingCampfires - cardsNeedingUpgrade);

        bool cardNeedsUpgrade = TransformativeUpgradeSet.Contains(cardIdUpper);

        if (cardNeedsUpgrade)
        {
            if (upgradeSlotsAvailable <= 0)
                return up.NoUpgradeSlot;
            else if (upgradeSlotsAvailable == 1)
                return up.TightUpgradeSlot;
            else if (upgradeSlotsAvailable >= 3)
                return up.PlentyUpgradeSlot;
        }

        // Transformative upgrade card
        if (TransformativeUpgradeSet.Contains(cardIdUpper))
        {
            if (upgradeSlotsAvailable >= 1)
                return up.TransformativeUpgradeBonus;
            else
                return up.TransformativeButNoSlot;
        }

        return 0;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // PHASE 20: Basic Card Removal Progress (基础牌删除进度)
    //
    // Rewards or penalizes based on how many basic Strikes/Defends remain.
    // High basic ratio = deck is bloated → be more selective.
    // Low basic ratio = well-trimmed → confidently take good cards.
    // ═══════════════════════════════════════════════════════════════════════════

    private static double ScoreBasicRemovalProgress(RunState state)
    {
        float basicRatio = state.BasicCardRatio;

        // These thresholds are simple enough to inline
        if (basicRatio > 0.4f)
            return -8.0;  // TooManyBasics — deck bloated, be cautious
        else if (basicRatio > 0.25f)
            return -3.0;  // ManyBasics
        else if (basicRatio < 0.15f && state.TotalCardCount > 5)
            return 5.0;   // WellTrimmed — deck is lean, can confidently add

        return 0;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // PHASE 21: Colorless Card Quality (无色牌质量加成)
    //
    // Colorless cards are rare and some are extremely powerful.
    // Premium colorless cards get a fixed score bonus.
    // ═══════════════════════════════════════════════════════════════════════════

    private static double ScoreColorlessPremium(CardModel card, string cardIdUpper)
    {
        // Check against the known colorless premium list
        if (ColorlessPremiumScores.TryGetValue(cardIdUpper, out double bonus))
            return bonus;

        // Card ID contains common colorless indicators
        if (cardIdUpper.Contains("COLORLESS") || cardIdUpper.Contains("NEUTRAL"))
            return 5.0;

        return 0;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // PHASE 22: Act Boss Counter (Act感知Boss对策)
    //
    // Certain cards have extraordinary value against specific act bosses.
    // Based on the boss guide and BossStrategy parameters.
    // ═══════════════════════════════════════════════════════════════════════════

    private static double ScoreBossCounter(CardModel card, RunState state,
        CardEffectReader.CardEffects fx, string cardIdUpper)
    {
        var bc = SolverParams.Instance.CardReward.BossCounter;
        string boss = state.ActBoss?.ToUpperInvariant() ?? "";
        if (string.IsNullOrEmpty(boss))
            return 0; // No boss detected — skip

        double bonus = 0;

        // ── Vantom: multi-hit breaks Slippery Shield, Weak bypasses it ──
        if (boss.Contains("VANTOM"))
        {
            if (MultiHitSet.Contains(cardIdUpper) || fx.BaseDamage > 0 && cardIdUpper.Contains("STRIKE"))
                bonus += bc.MultiHit;
            if (fx.WeakStacks > 0)
                bonus += bc.MultiHit * 0.5;
        }

        // ── The Kin: AOE is king (3 targets) ──
        else if (boss.Contains("KIN"))
        {
            if (fx.IsAoe || AoeCardSet.Contains(cardIdUpper))
                bonus += bc.AoeKin;
        }

        // ── Lagavulin Matriarch: poison/orb damage bypasses strength reduction ──
        else if (boss.Contains("LAGAVULIN"))
        {
            if (fx.PoisonStacks > 0 || cardIdUpper.Contains("ORB") ||
                cardIdUpper.Contains("LIGHTNING") || cardIdUpper.Contains("FROST") ||
                cardIdUpper.Contains("DARK") || cardIdUpper.Contains("PLASMA"))
                bonus += bc.PoisonOrb;
        }

        // ── Soul Fysh: exhaust/transform handles Beckon ──
        else if (boss.Contains("SOUL") || boss.Contains("FYSH"))
        {
            if (ExhaustCardSet.Contains(cardIdUpper) || cardIdUpper.Contains("EXHAUST"))
                bonus += bc.Exhaust;
        }

        // ── Insatiable: fast damage before sand-pit kills you ──
        else if (boss.Contains("INSATIABLE"))
        {
            int cost = CardCost(card);
            if (cost <= 1 && fx.BaseDamage >= bc.FastDamageThreshold)
                bonus += bc.FastDamage;
        }

        // ── Knowledge Demon: high-cost high-impact (draw 1 fewer is fine) ──
        else if (boss.Contains("KNOWLEDGE") || boss.Contains("DEMON"))
        {
            int cost = CardCost(card);
            if (cost >= 2 && (fx.BaseDamage >= 15 || fx.BaseBlock >= 12))
                bonus += bc.BigCards;
        }

        // ── Kaiser Crab: AOE for both claws ──
        else if (boss.Contains("KAISER") || boss.Contains("CRAB"))
        {
            if (fx.IsAoe || AoeCardSet.Contains(cardIdUpper))
                bonus += bc.Aoe;
        }

        // ── Test Subject: scaling cards (600 HP endurance), poison TRAP ──
        else if (boss.Contains("TEST") || boss.Contains("SUBJECT"))
        {
            if (card.Type == CardType.Power && ScalingCardSet.Contains(cardIdUpper))
                bonus += bc.Scaling;
            if (fx.PoisonStacks > 0)
                bonus += bc.TrapPoison; // Poison resets on phase change!
        }

        // ── Aeonglass: exhaust handles Wither, high-cost big cards ──
        else if (boss.Contains("AEONGLASS"))
        {
            if (ExhaustCardSet.Contains(cardIdUpper) || cardIdUpper.Contains("EXHAUST"))
                bonus += bc.Exhaust;
            int cost = CardCost(card);
            if (cost >= 2 && fx.BaseDamage >= 12)
                bonus += bc.BigCardsAeonglass;
        }

        // ── Ceremonial Beast: powers are free during stun turns ──
        else if (boss.Contains("CEREMONIAL") || boss.Contains("BEAST"))
        {
            if (card.Type == CardType.Power)
                bonus += bc.Scaling * 0.5;
        }

        // ── Waterfall Giant: high defense needed for death explosion ──
        else if (boss.Contains("WATERFALL") || boss.Contains("GIANT"))
        {
            if (fx.BaseBlock >= 12)
                bonus += bc.BigCards;
        }

        return bonus;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Community Combo Boost (Phase 8b mod helper)
    //
    // Community-verified "1+1>2" card combos get an extra multiplier on top
    // of the statistical combo bonus.
    // ═══════════════════════════════════════════════════════════════════════════

    private static double GetCommunityComboBoost(string cardIdUpper, List<string> deckCardIds)
    {
        // Community combo boost lookup (bidirectional).
        // These are well-known synergies from STS2 community guides.
        // Boost is a multiplier: 1.0 = no change, 1.5 = +50%, 1.8 = +80%

        double boost = 1.0;

        foreach (var existingId in deckCardIds)
        {
            string e = existingId.ToUpperInvariant();

            // Ironclad
            if ((e.Contains("RUPTURE") && (cardIdUpper.Contains("BLOODLETTING") || cardIdUpper.Contains("BATH_IN_FIRE"))))
                boost = Math.Max(boost, 1.5);
            if ((e.Contains("CORRUPTION") && cardIdUpper.Contains("DARK_EMBRACE")) ||
                (e.Contains("DARK_EMBRACE") && cardIdUpper.Contains("CORRUPTION")))
                boost = Math.Max(boost, 1.8);
            if ((e.Contains("DARK_EMBRACE") && cardIdUpper.Contains("BURNING_PACT")) ||
                (e.Contains("BURNING_PACT") && cardIdUpper.Contains("DARK_EMBRACE")))
                boost = Math.Max(boost, 1.4);
            if ((e.Contains("FEEL_NO_PAIN") && cardIdUpper.Contains("SECOND_WIND")) ||
                (e.Contains("SECOND_WIND") && cardIdUpper.Contains("FEEL_NO_PAIN")))
                boost = Math.Max(boost, 1.3);

            // Silent
            if ((e.Contains("CORROSIVE_WAVE") && (cardIdUpper.Contains("ACROBATICS") || cardIdUpper.Contains("BACKPACK"))))
                boost = Math.Max(boost, 1.7);
            if ((e.Contains("ACCURACY") && cardIdUpper.Contains("FAN_OF_KNIVES")) ||
                (e.Contains("FAN_OF_KNIVES") && cardIdUpper.Contains("ACCURACY")))
                boost = Math.Max(boost, 1.4);
            if ((e.Contains("ENVENOM") && cardIdUpper.Contains("FAN_OF_KNIVES")) ||
                (e.Contains("FAN_OF_KNIVES") && cardIdUpper.Contains("ENVENOM")))
                boost = Math.Max(boost, 1.3);

            // Defect
            if ((e.Contains("COMPRESS") && (cardIdUpper.Contains("OVERCLOCK") || cardIdUpper.Contains("TURBO"))))
                boost = Math.Max(boost, 1.6);
            if ((e.Contains("SCATTER_CANNON") && cardIdUpper.Contains("OVERCLOCK")) ||
                (e.Contains("OVERCLOCK") && cardIdUpper.Contains("SCATTER_CANNON")))
                boost = Math.Max(boost, 1.4);

            // Necrobinder
            if ((e.Contains("ELEGY") && cardIdUpper.Contains("INVOKE")) ||
                (e.Contains("INVOKE") && cardIdUpper.Contains("ELEGY")))
                boost = Math.Max(boost, 1.5);
            if ((e.Contains("DEATH_MARCH") && cardIdUpper.Contains("CAPTURE_SOUL")) ||
                (e.Contains("CAPTURE_SOUL") && cardIdUpper.Contains("DEATH_MARCH")))
                boost = Math.Max(boost, 1.4);

            // Regent
            if ((e.Contains("FORGE_MASTER") && cardIdUpper.Contains("DECISION")) ||
                (e.Contains("DECISION") && cardIdUpper.Contains("FORGE_MASTER")))
                boost = Math.Max(boost, 1.6);
            if ((e.Contains("STAR_EXTINGUISH") && cardIdUpper.Contains("CONFLUENCE")) ||
                (e.Contains("CONFLUENCE") && cardIdUpper.Contains("STAR_EXTINGUISH")))
                boost = Math.Max(boost, 1.3);
        }

        return boost;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static int CardCost(CardModel? card)
    {
        if (card == null) return 99;
        try
        {
            if (card.EnergyCost.CostsX) return 1; // treat X-cost as 1 for scoring
            return card.EnergyCost.Canonical;
        }
        catch { return 99; }
    }
}
