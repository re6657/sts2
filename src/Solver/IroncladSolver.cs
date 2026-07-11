using System;
using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;

namespace TokenSpire2.Solver;

/// <summary>
/// Multi-character combat solver using DFS state-space search with heuristic
/// evaluation. Supports all 5 character classes via CharacterConfig.
/// </summary>
public static class IroncladSolver
{
    // ── Solver constants (loaded from params.json at runtime) ──────────────
    // MAX_SEARCH_STATES and MAX_CARDS_PER_TURN are now read from SolverParams.Instance

    // ── Current config (set per Solve call) ────────────────────────────────

    private static CharacterConfig _cfg = null!;

    // ── Public types ───────────────────────────────────────────────────────

    public class SolveResult
    {
        public List<SolveAction> Actions = new();
        public int EstimatedDamage;
        public int EstimatedBlock;
        public int StatesExplored;
        public string DebugInfo = "";
    }

    public class SolveAction
    {
        public CardModel? Card;
        public Creature? Target;
        public bool IsEndTurn;
        public bool IsPotion;
        public string? PotionId;

        public override string ToString() =>
            IsEndTurn ? "END_TURN" :
            IsPotion ? $"USE_POTION {PotionId}" :
            Card != null ? $"PLAY {Card.Id.Entry} -> {Target?.Monster?.Id.Entry ?? "self"}" :
            "?";
    }

    /// <summary>Represents a potion's combat effects for the solver.</summary>
    public class PotionEffect
    {
        public string Id;
        public int Damage;
        public bool IsAoe;
        public int VulnerableStacks;
        public int WeakStacks;
        public int StrengthGain;
        public int DexterityGain;
        public int BlockGain;
        public int EnergyGain;
        public int HealAmount;
        public int PoisonStacks;
        public bool Used;
    }

    // ── Internal state ────────────────────────────────────────────────────

    /// <summary>Action record for sequencing tracking.</summary>
    private class ActionRecord
    {
        public string CardId = "";
        public int Priority;
        public int OrderPriority;     // 0-100, two-dimensional: when to play (higher = earlier)
        public bool IsZeroCostEnergy; // 0-cost card that gives energy
        public int EnergyGain;
        public bool IsSetupCard;      // priority <= PRIORITY_SETUP
        public bool IsPowerCard;      // priority <= PRIORITY_POWER_S
        public bool IsBasic;          // IsBasicCard equivalent check
    }

    private class SearchState
    {
        public int Energy;
        public int Block;
        public int Hp;
        public int MaxHp;
        public int Strength;
        public int Dexterity;
        public int VulnerableOnPlayer;
        public int WeakOnPlayer;              // Player has Weak (damage * 0.75)
        public int FrailOnPlayer;             // Player has Frail (block * 0.75)
        public int PlayerDOT;                 // DOT on player (Constrict, etc.) — unblockable end-of-turn damage
        public List<EnemyProxy> Enemies = new();
        public List<CardEntry> Hand = new();
        public List<CardModel> DrawPile = new(); // Remaining draw pile (for draw simulation)
        public List<CardModel> DiscardPile = new(); // Discard pile (for reshuffle simulation)
        public int CardsPlayed;
        public int PowersPlayed;
        public int StrengthGained;
        public int DexterityGained;
        public int TotalDamageDealt;
        public int TotalBlockGained;
        public int CardSelectsPlayed;         // Cards that trigger card selection (upgrade/exhaust)

        // ── Sequencing tracking (Optimization 2) ──
        public List<ActionRecord> ActionHistory = new();
        public int TotalEnergyGained;         // Total energy gained from cards
        public int TotalEnergySpent;          // Total energy spent on cards
        public int ZeroCostEnergyCardsPlayed; // Count of 0-cost energy-gain cards played
        public int ExhaustedBasicCount;       // Basic cards exhausted during combat
        public double InitialDeckQuality;     // Non-basic ratio at combat start

        // ── Active powers tracking (Optimization 1) ──
        public List<string> ActivePowers = new(); // IDs of powers played this combat

        // ── Character-specific state ──
        public int PoisonOnEnemies;       // Total poison stacks (Silent)
        public int OrbSlots;              // Number of orb slots (Defect)
        public int LightningOrbs;         // Lightning (deal 3+Focus damage passively, evoke 8+Focus)
        public int FrostOrbs;             // Frost (gain 2+Focus block passively, evoke 5+Focus)
        public int DarkOrbs;              // Dark (accumulate damage — passive +6/turn, evoke accumulated)
        public int PlasmaOrbs;            // Plasma (gain 1 energy at TURN START, evoke 2 energy)
        public int Focus;                 // Focus stat (Defect) — +1 to passive and evoke per point
        public int LoopCount;             // Loop power stacks — triggers first orb passive N extra times
        // ── Orb position tracking (for evoke simulation) ──
        // Orb queue: index 0 = rightmost (next to be evoked when channeling into full slots)
        // Index N-1 = leftmost (newest orb). This matches the game's FIFO evoke order.
        public List<string> OrbQueue = new(); // e.g. ["Lightning","Frost","Dark"]
        // Accumulated damage on dark orbs (grows by 6 each end-of-turn passive)
        public int BaseDarkOrbDamage = 6;   // Starting value when dark orb is channeled
        public int TotalDarkOrbDamage = 0;  // Sum of accumulated damage across all dark orbs
        public int Stars;                 // Current stars (Necrobinder)
        public int StarsSpent;
        public List<PotionEffect> Potions = new();   // Available potions
        public int PanicButtonTurns;    // Remaining turns of "no block from cards" debuff from PANIC_BUTTON
    }

    private class EnemyProxy
    {
        public Creature Creature = null!;
        public int Index;
        public int Vulnerable;
        public int Weak;
        public int Strength;           // Enemy Strength increases damage output
        public int Poison;
        public int Hp;
        public int MaxHp;
        public int Block;
        public int IntentDamage;
        public bool IsAlive;
        public int Intangible;        // Nemesis/Snecko/etc: cap damage per hit (1 = max 1 dmg)
        public int Buffer;            // Negates the next instance of HP loss
        public int Thorns;            // Deals damage back to attacker when hit by attack damage
    }

    // Cards that require selecting another card (upgrade, exhaust, retrieve, etc.)
    private static readonly HashSet<string> CardSelectIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "ARMAMENTS", "TRUE_GRIT", "BURNING_PACT", "HEADBUTT",
        "WARCRY", "EXHUME", "HOLOGRAM", "RECYCLE",
        "SECRET_TECHNIQUE", "SECRET_WEAPON",
    };

    // Cards that draw additional cards when played (estimated draw count)
    private static readonly Dictionary<string, int> DrawCardIds = new(StringComparer.OrdinalIgnoreCase)
    {
        {"POMMEL_STRIKE", 1}, {"BURNING_PACT", 2}, {"BATTLE_TRANCE", 3},
        {"OFFERING", 3}, {"SHRUG_IT_OFF", 1}, {"WARCRY", 1},
        {"BACKFLIP", 2}, {"DAGGER_THROW", 1}, {"ESCAPE_PLAN", 1},
        {"ACROBATICS", 3}, {"CALCULATED_GAMBLE", 3}, {"EXPERTISE", 3},
        {"QUICK_SLASH", 1}, {"HEEL_HOOK", 1}, {"DROP_KICK", 1},
        {"SKIM", 3}, {"COMPILE_DRIVER", 2}, {"COOLHEADED", 1},
        {"OVERCLOCK", 2}, {"REBOUND", 1}, {"FTL", 1}, {"SCRAPE", 2},
        {"DREDGE", 2}, {"FETCH", 1}, {"PARSE", 2},
    };

    /// <summary>Lightweight card reference for search.</summary>
    private class CardEntry
    {
        public CardModel Card;
        public int Priority;
        public int BaseDamage;
        public int BaseBlock;
        public bool IsAoe;
        public bool AppliesVulnerable;
        public int VulnerableStacks;
        public bool AppliesWeak;
        public int WeakStacks;
        public bool GrantsStrength;
        public int StrengthAmount;
        public bool GrantsDexterity;
        public int DexterityAmount;
        public int EnergyGain;
        public bool AppliesPoison;
        public int PoisonStacks;
        public bool IsPower;
        public bool CostsX;
        public int CanonicalCost;
        public bool CostsStars;
        public int StarCost;
        public string CardId;
        public string DebugInfo;
        public bool HasCardSelect;
        public int DrawCount;
        public int HpCost;
        public TargetType GameTargetType; // Actual game target type for correct targeting
        /// <summary>
        /// True if BaseDamage/BaseBlock were computed by the game engine's DynamicVar system
        /// and already include Strength/Dexterity/relic/enchant modifiers.
        /// When true, GetDamage/GetBlock should NOT add Strength/Dexterity again.
        /// </summary>
        public bool FromGameEngine;
        public bool IsOrbEvoke;   // Card evokes orbs (Dualcast, Multi-Cast, Recursion)
        public bool IsOrbChannel; // Card channels an orb (Zap, Ball Lightning, Cold Snap, etc.)
        /// <summary>Two-dimensional play order (0-100): higher = play earlier in sequence.</summary>
        public int OrderPriority;

        public CardEntry(CardModel card, CharacterConfig cfg)
        {
            Card = card;
            CardId = card.Id.Entry.ToUpperInvariant();
            GameTargetType = card.TargetType; // Use game's actual target type
            Priority = cfg.CardPriorities.GetValueOrDefault(CardId,
                card.Type == CardType.Power ? CharacterConfig.PRIORITY_POWER :
                card.Type == CardType.Skill ? CharacterConfig.PRIORITY_BLOCK :
                card.Type == CardType.Attack ? CharacterConfig.PRIORITY_ATTACK :
                CharacterConfig.PRIORITY_LAST);
            // Two-dimensional: load play_order from CardDatabase, fall back to CardClassifier
            try
            {
                OrderPriority = CardDatabase.Instance.GetPlayOrder(CardId);
                if (OrderPriority == 0 || OrderPriority == 50) // 50 = default, 0 = unset
                    OrderPriority = CardClassifier.GetDefaultPlayOrder(CardId);
            }
            catch
            {
                OrderPriority = CardClassifier.GetDefaultPlayOrder(CardId);
            }
            CostsX = card.EnergyCost.CostsX;
            CanonicalCost = CostsX ? 0 : Math.Max(0, card.EnergyCost.Canonical);
            IsPower = card.Type == CardType.Power;
            IsAoe = card.TargetType == TargetType.AllEnemies
                 || card.TargetType == TargetType.RandomEnemy;
            CostsStars = card.CanonicalStarCost > 0;
            StarCost = card.CanonicalStarCost;
            HasCardSelect = CardSelectIds.Contains(CardId);
            DrawCount = DrawCardIds.GetValueOrDefault(CardId, 0);

            // Use game engine values when available, fall back to hardcoded estimates
            try
            {
                var fx = CardEffectReader.ReadEffects(card);
                BaseDamage = fx.BaseDamage;
                BaseBlock = fx.BaseBlock;
                IsOrbEvoke = fx.IsOrbEvoke;

                // ── Fix: orb evoke cards have no direct damage DynamicVar ──
                // When reflection succeeds, BaseDamage is 0 for Dualcast/Multi-Cast/
                // Recursion because they don't deal direct damage — they evoke orbs.
                // The fallback values approximate the evoke damage (Lightning: 8+Focus).
                // Without this, the solver treats these cards as 0-damage and skips them.
                if (IsOrbEvoke && BaseDamage == 0)
                {
                    BaseDamage = CardId switch
                    {
                        "DUALCAST" => 16,   // Evoke rightmost orb twice: ~8×2
                        "MULTI_CAST" => 8,  // Evoke rightmost orb once per energy (X-cost)
                        "RECURSION" => 8,   // Evoke rightmost orb, re-channel it
                        _ => 8,             // Default: single evoke ≈ 8 damage
                    };
                }
                IsAoe = IsAoe || fx.IsAoe;
                AppliesVulnerable = fx.VulnerableStacks > 0;
                VulnerableStacks = fx.VulnerableStacks;
                AppliesWeak = fx.WeakStacks > 0;
                WeakStacks = fx.WeakStacks;
                GrantsStrength = fx.GrantsStrength;
                StrengthAmount = fx.StrengthAmount;
                GrantsDexterity = fx.GrantsDexterity;
                DexterityAmount = fx.DexterityAmount;
                EnergyGain = fx.EnergyGain;
                AppliesPoison = fx.PoisonStacks > 0;
                PoisonStacks = fx.PoisonStacks;
                HpCost = fx.HpCost;
                FromGameEngine = fx.FromGameEngine;
                DebugInfo = fx.DebugInfo;
            }
            catch (Exception ex)
            {
                // Fallback: use type-based defaults
                BaseDamage = card.Type == CardType.Attack ? 6 : 0;
                BaseBlock = card.Type == CardType.Skill ? 5 : 0;
                DebugInfo = $"cardentry_err: {ex.GetType().Name}";
                MainFile.Logger.Info($"[Solver] CardEntry crash for {CardId}: {ex.Message}");
            }
        }

        /// <summary>Get cost for a given energy spend (for X-cost cards).</summary>
        public int GetEnergyCost(int energySpent = -1)
        {
            if (!CostsX) return CanonicalCost;
            return Math.Max(0, energySpent);
        }

        /// <summary>Get actual damage in current state.</summary>
        public int GetDamage(SearchState state)
        {
            int dmg = BaseDamage;
            if (CardId == "BODY_SLAM")
            {
                // Body Slam: damage = current Block (Strength does NOT apply)
                dmg = state.Block;
            }
            else if (CardId == "PERFECTED_STRIKE")
            {
                // +2 per Strike-named card still in draw pile (undrawn deck)
                int strikeInDraw = state.DrawPile.Count(c => c.Id.Entry.ToUpperInvariant().Contains("STRIKE"));
                dmg += strikeInDraw * 2;
                // Strength: only add if game engine didn't already include it
                if (!FromGameEngine) dmg += state.Strength;
            }
            else
            {
                // Strength: game engine's DynamicVar PreviewValue already includes
                // Strength, relics, and enchants. Only add Strength for hardcoded fallback values.
                if (!FromGameEngine) dmg += state.Strength;
            }
            // Player Weak reduces damage by 25%
            if (state.WeakOnPlayer > 0)
                dmg = (int)Math.Floor(dmg * 0.75);
            return dmg;
        }

        /// <summary>Get actual block gain in current state.</summary>
        public int GetBlock(SearchState state)
        {
            int blk = BaseBlock;
            // Dexterity: game engine's DynamicVar already includes it. Only add for fallback.
            if (!FromGameEngine) blk += state.Dexterity;
            // Player Frail reduces block by 25%
            if (state.FrailOnPlayer > 0)
                blk = (int)Math.Floor(blk * 0.75);
            return blk;
        }
    }

    // ── Main entry point ──────────────────────────────────────────────────

    /// <summary>
    /// Solve this combat turn for any character class. Uses the provided
    /// CharacterConfig to determine card priorities and evaluation weights.
    /// </summary>
    /// <summary>Stored encounter type for scoring adjustments.</summary>
    private static bool _isEliteCombat;
    private static bool _isBossCombat;
    private static BossStrategy.Adjustment? _bossStrategy;

    public static SolveResult Solve(
        List<CardModel> hand,
        List<Creature> enemies,
        int energy,
        int currentBlock,
        int currentHp,
        int maxHp,
        int strength,
        int vulnerableOnPlayer,
        CharacterConfig? config = null,
        int dexterity = 0,
        int weakOnPlayer = 0,
        int frailOnPlayer = 0,
        int poisonOnEnemies = 0,
        int orbSlots = 0, int lightningOrbs = 0, int frostOrbs = 0,
        int darkOrbs = 0, int plasmaOrbs = 0, int focus = 0,
        int loopCount = 0, List<string>? orbQueue = null,
        int baseDarkOrbDamage = 6, int totalDarkOrbDamage = 0,
        int stars = 0,
        List<CardModel>? drawPile = null,
        List<CardModel>? discardPile = null,
        List<string>? potionIds = null,
        bool isElite = false,
        bool isBoss = false,
        string? encounterId = null,
        int playerDOT = 0,
        int panicButtonTurns = 0)
    {
        _cfg = config ?? CharacterConfig.Create("IRONCLAD");
        _isEliteCombat = isElite;
        _isBossCombat = isBoss;
        _bossStrategy = BossStrategy.GetStrategy(encounterId);
        if (_bossStrategy != null)
            MainFile.Logger.Info($"[Solver] Boss strategy active: {_bossStrategy.Description}");

        // ── Diagnostic: log raw hand ──────────────────────────────
        var handIds = string.Join(", ", hand.Select(c => $"{c.Id.Entry}(cost={c.EnergyCost.Canonical}, canPlay={CanPlayCard(c, energy)})"));
        MainFile.Logger.Info($"[SolverDBG] Raw hand ({hand.Count}): [{handIds}] energy={energy} enemies={enemies.Count}");

        // ── Pre-calculate incoming damage for PANIC_BUTTON filter ──
        int preTotalIncoming = enemies.Where(e => e.IsAlive).Sum(e => EstimateIntentDamage(e));

        // Build search state
        var state = new SearchState
        {
            Energy = energy,
            Block = currentBlock,
            Hp = currentHp,
            MaxHp = maxHp,
            Strength = strength,
            Dexterity = dexterity,
            VulnerableOnPlayer = vulnerableOnPlayer,
            WeakOnPlayer = weakOnPlayer,
            FrailOnPlayer = frailOnPlayer,
            PlayerDOT = playerDOT,
            Focus = focus,
            OrbSlots = orbSlots,
            LightningOrbs = lightningOrbs,
            FrostOrbs = frostOrbs,
            DarkOrbs = darkOrbs,
            PlasmaOrbs = plasmaOrbs,
            LoopCount = loopCount,
            OrbQueue = orbQueue != null ? new List<string>(orbQueue) : new List<string>(),
            BaseDarkOrbDamage = baseDarkOrbDamage,
            TotalDarkOrbDamage = totalDarkOrbDamage,
            Stars = stars,
            PanicButtonTurns = panicButtonTurns,
            Enemies = enemies.Select((e, i) => new EnemyProxy
            {
                Creature = e,
                Index = i,
                Vulnerable = GetVulnerableStacks(e),
                Weak = GetWeakStacks(e),
                Strength = GetStrengthFromPowers(e),
                Poison = GetPoisonStacks(e),
                Hp = e.CurrentHp,
                MaxHp = e.MaxHp,
                Block = e.Block,
                IntentDamage = EstimateIntentDamage(e),
                IsAlive = e.IsAlive,
                Intangible = GetIntangibleStacks(e),
                Buffer = GetBufferStacks(e),
                Thorns = GetThornsStacks(e),
            }).ToList(),
            Hand = hand
                .Where(c => CanPlayCard(c, energy))
                .Where(c =>
                {
                    // PANIC_BUTTON filter: don't play when defense sufficient or enemy not attacking
                    if (c.Id.Entry?.ToUpperInvariant() == "PANIC_BUTTON")
                    {
                        // Skip if enemy isn't attacking
                        if (preTotalIncoming <= 0) return false;
                        // Skip if current block already covers incoming damage
                        if (currentBlock >= preTotalIncoming) return false;
                    }
                    return true;
                })
                .Select(c => new CardEntry(c, _cfg))
                .OrderByDescending(ce => GetCombinedScore(ce, energy, 0,
                    _isBossCombat, false)) // initial sort: no cards played yet
                .ThenBy(ce => ce.CanonicalCost)  // cheaper cards as tiebreaker
                .ThenByDescending(ce => ce.Card.IsUpgraded ? 1 : 0) // 升级牌优先打出
                .ToList(),
            DrawPile = drawPile ?? new List<CardModel>(),
            DiscardPile = discardPile ?? new List<CardModel>(),
        };

        MainFile.Logger.Info($"[SolverDBG] Playable hand: {state.Hand.Count} cards (after CanPlayCard filter)");

        // Sum existing poison from enemies into state
        state.PoisonOnEnemies = state.Enemies.Sum(e => e.Poison);

        // ── Compute initial deck quality (Optimization 3) ────────────────
        var allDeckCards = state.DrawPile.Concat(state.DiscardPile).ToList();
        if (allDeckCards.Count > 0)
        {
            int nonBasic = allDeckCards.Count(c => !IsBasicCardByName(
                c.Id.Entry?.ToUpperInvariant() ?? ""));
            state.InitialDeckQuality = (double)nonBasic / allDeckCards.Count;
        }
        else
        {
            state.InitialDeckQuality = 0.5; // default for small decks
        }

        // ── Build potion effects from IDs ────────────────────────────────
        state.Potions = (potionIds ?? new List<string>())
            .Select(id => CreatePotionEffect(id))
            .Where(p => p != null)
            .Select(p => p!)
            .ToList();
        if (state.Potions.Count > 0)
            MainFile.Logger.Info($"[SolverDBG] Potions available: {string.Join(", ", state.Potions.Select(p => p.Id))}");

        // Include player DOT (Constrict, etc.) as unblockable incoming damage
        int totalIncoming = state.Enemies.Where(e => e.IsAlive).Sum(e => e.IntentDamage) + state.PlayerDOT;

        // ── Run DFS search ────────────────────────────────────────────────
        var results = new List<(double Score, List<SolveAction> Actions, SearchState State)>();
        int statesExplored = 0;

        double baselineScore = EvaluateState(state, totalIncoming);
        results.Add((baselineScore, new List<SolveAction>(), CloneState(state)));

        Search(state, new List<SolveAction>(), totalIncoming,
               results, ref statesExplored);

        var best = Tiebreaker.PickBest(results, r => r.Score);

        // ── WARNING: empty plan = no cards played, only END_TURN ──────
        if (best.Actions.Count == 0)
        {
            string handSummary = string.Join(", ", state.Hand.Select(h =>
                $"{h.CardId}(c={h.CanonicalCost},hp={h.HpCost})"));
            MainFile.Logger.Info(
                $"[Solver] ⚠️ EMPTY PLAN: 0 cards played! Hand=[{handSummary}] " +
                $"Energy={state.Energy} HP={state.Hp}/{state.MaxHp} Incoming={totalIncoming}");
        }

        var result = new SolveResult
        {
            Actions = best.Actions,
            EstimatedDamage = best.State.TotalDamageDealt,
            EstimatedBlock = best.State.TotalBlockGained,
            StatesExplored = statesExplored,
        };

        result.Actions.Add(new SolveAction { IsEndTurn = true });

        var firstEntry = state.Hand.FirstOrDefault();
        string source = firstEntry?.DebugInfo ?? "none";

        result.DebugInfo = $"Dmg={result.EstimatedDamage} Blk={result.EstimatedBlock} " +
                          $"In={totalIncoming} Cards={best.Actions.Count} " +
                          $"Score={best.Score:F0} States={statesExplored} " +
                          $"Char={_cfg.CharacterId} Src={source}";

        return result;
    }

    /// <summary>
    /// Compute the combined "what to play next" score for a card entry
    /// using the two-dimensional priority system.
    ///
    /// Formula: combinedScore = selectionWeight × Priority + orderWeight × OrderPriority
    ///
    /// Weights depend on combat context (energy, turn position, boss, lethal).
    /// This is the core of the two-dimensional auto-battle redesign.
    /// </summary>
    private static double GetCombinedScore(CardEntry entry, int currentEnergy,
        int cardsPlayedSoFar, bool isBossFight, bool lethalDetected)
    {
        var (selW, ordW) = CardDatabaseExtensions.GetContextWeights(
            currentEnergy, cardsPlayedSoFar, isBossFight, lethalDetected);

        // Boss-specific overrides
        if (_bossStrategy != null)
        {
            selW *= _bossStrategy.SelectionWeightMult;
            ordW *= _bossStrategy.OrderWeightMult;
        }

        // ── Upgraded card bonus ──────────────────────────────────────────
        // Upgraded cards are strictly better (higher damage, lower cost, extra effects).
        // Always prefer the upgraded version of the same card (e.g., Strike+ over Strike).
        // This bonus ensures upgraded cards score higher than their non-upgraded twins
        // even when everything else (priority, order, cost) is identical.
        double upgradedBonus = entry.Card.IsUpgraded ? 15.0 : 0;

        return selW * entry.Priority + ordW * entry.OrderPriority + upgradedBonus;
    }

    // ── DFS search ────────────────────────────────────────────────────────

    private static void Search(
        SearchState state,
        List<SolveAction> actions,
        int totalIncoming,
        List<(double Score, List<SolveAction> Actions, SearchState State)> results,
        ref int statesExplored)
    {
        int maxStates = SolverParams.Instance.CombatSolver.MaxSearchStates;
        int maxCards = SolverParams.Instance.CombatSolver.MaxCardsPerTurn;
        if (statesExplored >= maxStates) return;
        if (actions.Count >= maxCards) return;

        for (int i = 0; i < state.Hand.Count; i++)
        {
            var entry = state.Hand[i];
            int baseCost = entry.GetEnergyCost(0);

            if (baseCost > state.Energy) continue;
            if (entry.CostsStars && entry.StarCost > state.Stars) continue;
            // Safety: only block HP-cost cards when LETHAL (HP - cost <= 0).
            // Previously blocked at "critically low" HP (<=5), which caused empty turns
            // when the hand had no other playable cards. At low HP with 0 energy,
            // playing Bloodletting is the ONLY way to avoid guaranteed death.
            // Exception: STILL block if the HP cost would kill us.
            if (entry.HpCost > 0 && state.Hp - entry.HpCost <= 0) continue;

            var energyOptions = GetEnergyOptions(entry, state.Energy);

            foreach (int spentEnergy in energyOptions)
            {
                var targets = GetValidTargets(state, entry);

                foreach (var target in targets)
                {
                    statesExplored++;
                    if (statesExplored > maxStates) return;

                    var newState = CloneState(state);
                    newState.Energy -= spentEnergy;
                    if (entry.CostsStars) newState.Stars -= entry.StarCost;
                    newState.Hand.RemoveAt(i);
                    newState.CardsPlayed++;

                    ApplyCardEffects(newState, entry, target, spentEnergy);

                    // ── Record action history (Optimization 2) ──────────
                    newState.ActionHistory.Add(new ActionRecord
                    {
                        CardId = entry.CardId,
                        Priority = entry.Priority,
                        OrderPriority = entry.OrderPriority,
                        IsZeroCostEnergy = entry.CanonicalCost == 0 && entry.EnergyGain > 0,
                        EnergyGain = entry.EnergyGain,
                        IsSetupCard = entry.Priority <= CharacterConfig.PRIORITY_FREE_ENERGY
                                   || entry.Priority <= CharacterConfig.PRIORITY_SETUP,
                        IsPowerCard = entry.Priority <= CharacterConfig.PRIORITY_POWER_S,
                        IsBasic = IsBasicCardByName(entry.CardId),
                    });
                    // Track energy stats
                    newState.TotalEnergySpent += spentEnergy;

                    // ── Simulate draw if this card draws cards ──────────
                    if (entry.DrawCount > 0)
                    {
                        int remaining = entry.DrawCount;
                        // First draw from draw pile
                        while (remaining > 0 && newState.DrawPile.Count > 0)
                        {
                            try
                            {
                                var drawnCard = newState.DrawPile[0];
                                newState.DrawPile.RemoveAt(0);
                                newState.Hand.Add(new CardEntry(drawnCard, _cfg));
                                remaining--;
                            }
                            catch { break; }
                        }
                        // If draw pile is empty, reshuffle discard pile into draw pile
                        if (remaining > 0 && newState.DiscardPile.Count > 0)
                        {
                            // Shuffle discard into draw (simulate with order preservation —
                            // real game randomizes, we approximate)
                            newState.DrawPile.AddRange(newState.DiscardPile);
                            newState.DiscardPile.Clear();
                            // Continue drawing from reshuffled pile
                            while (remaining > 0 && newState.DrawPile.Count > 0)
                            {
                                try
                                {
                                    var drawnCard = newState.DrawPile[0];
                                    newState.DrawPile.RemoveAt(0);
                                    newState.Hand.Add(new CardEntry(drawnCard, _cfg));
                                    remaining--;
                                }
                                catch { break; }
                            }
                        }
                    }

                    var newActions = new List<SolveAction>(actions)
                    {
                        new SolveAction { Card = entry.Card, Target = target }
                    };

                    double score = EvaluateState(newState, totalIncoming);
                    results.Add((score, newActions, CloneState(newState)));

                    bool allDead = newState.Enemies.All(e => !e.IsAlive);
                    if (!allDead)
                    {
                        Search(newState, newActions, totalIncoming,
                               results, ref statesExplored);
                    }
                }
            }
        }

        // ── Potion usage actions (free, no energy cost) ──────────────────
        // Only use potions in Elite or Boss fights — save them for hard encounters.
        if (_isEliteCombat || _isBossCombat)
        for (int pi = 0; pi < state.Potions.Count; pi++)
        {
            var potion = state.Potions[pi];
            if (potion.Used) continue;

            statesExplored++;
            if (statesExplored > maxStates) return;

            var newState = CloneState(state);
            // Mark potion as used
            newState.Potions[pi].Used = true;

            // Apply potion effects
            ApplyPotionEffects(newState, potion);

            // Record potion in action history
            newState.ActionHistory.Add(new ActionRecord
            {
                CardId = $"POTION_{potion.Id}",
                Priority = 99, // potions are free actions
                IsZeroCostEnergy = potion.EnergyGain > 0,
                EnergyGain = potion.EnergyGain,
            });

            var newActions = new List<SolveAction>(actions)
            {
                new SolveAction { IsPotion = true, PotionId = potion.Id }
            };

            double score = EvaluateState(newState, totalIncoming);
            results.Add((score, newActions, CloneState(newState)));

            bool allDead = newState.Enemies.All(e => !e.IsAlive);
            if (!allDead)
            {
                Search(newState, newActions, totalIncoming,
                       results, ref statesExplored);
            }
        }
    }

    // ── Energy options ────────────────────────────────────────────────────

    private static List<int> GetEnergyOptions(CardEntry entry, int availableEnergy)
    {
        if (!entry.CostsX)
        {
            int cost = entry.CanonicalCost;
            return cost <= availableEnergy ? new List<int> { cost } : new List<int>();
        }

        // X-cost cards always consume ALL remaining energy when played via
        // TryManualPlay — the game UI defaults to max X.  We therefore only
        // consider X = availableEnergy so that plans are actually executable.
        // Cards placed after an X-cost play would have 0 energy and be
        // unplayable, so X-cost cards are effectively turn-enders.
        var options = new List<int>();
        if (availableEnergy > 0) options.Add(availableEnergy);
        return options;
    }

    // ── Valid targets ─────────────────────────────────────────────────────

    private static List<Creature?> GetValidTargets(SearchState state, CardEntry entry)
    {
        var alive = state.Enemies.Where(e => e.IsAlive).ToList();
        if (alive.Count == 0)
            return new List<Creature?> { null };

        // Use the game's actual TargetType — this is authoritative.
        // Cards like DARK_SHACKLES target AnyEnemy even though they have 0 damage
        // and don't apply Vulnerable/Weak (they apply strength reduction).
        var tt = entry.GameTargetType;
        bool targetsEnemy = tt == TargetType.AnyEnemy
                         || tt == TargetType.AllEnemies
                         || tt == TargetType.RandomEnemy;

        if (!targetsEnemy)
            return new List<Creature?> { null };

        if (entry.IsAoe || tt == TargetType.AllEnemies || tt == TargetType.RandomEnemy)
            return new List<Creature?> { alive[0].Creature };

        // Prioritize: vulnerable → lowest HP → rest
        var targets = new List<Creature?>();
        foreach (var e in alive.Where(e => e.Vulnerable > 0).OrderBy(e => e.Hp))
            targets.Add(e.Creature);
        foreach (var e in alive.Where(e => e.Vulnerable <= 0).OrderBy(e => e.Hp))
            targets.Add(e.Creature);

        return targets;
    }

    // ── Card effect application ───────────────────────────────────────────

    private static void ApplyCardEffects(SearchState state, CardEntry entry,
                                          Creature? target, int spentEnergy)
    {
        int dmg = entry.GetDamage(state);
        int blk = entry.GetBlock(state);

        // X-cost scaling: BaseDamage/BaseBlock * energy + stat (added once, not per energy)
        // Weak/Frail on player reduce output by 25%
        // IMPORTANT: When FromGameEngine, BaseDamage/BaseBlock already include modifiers.
        // When NOT FromGameEngine, we must add Strength/Dexterity manually.
        if (entry.CostsX)
        {
            // Damage: assume scales with X if BaseDamage >= 5 (Whirlwind=5/energy, Tempest=8, Skewer=7)
            if (entry.BaseDamage >= 5)
            {
                float xDmg = entry.BaseDamage * spentEnergy;
                if (!entry.FromGameEngine) xDmg += state.Strength;
                if (state.WeakOnPlayer > 0)
                    xDmg = (int)Math.Floor(xDmg * 0.75f);
                dmg = (int)xDmg;
            }
            else
            {
                dmg = entry.BaseDamage;
                if (!entry.FromGameEngine) dmg += state.Strength;
                if (state.WeakOnPlayer > 0) dmg = (int)Math.Floor(dmg * 0.75f);
            }

            // Block: assume scales with X if BaseBlock >= 5
            if (entry.BaseBlock >= 5)
            {
                float xBlk = entry.BaseBlock * spentEnergy;
                if (!entry.FromGameEngine) xBlk += state.Dexterity;
                if (state.FrailOnPlayer > 0)
                    xBlk = (int)Math.Floor(xBlk * 0.75f);
                blk = (int)xBlk;
            }
            else
            {
                blk = entry.BaseBlock;
                if (!entry.FromGameEngine) blk += state.Dexterity;
                if (state.FrailOnPlayer > 0) blk = (int)Math.Floor(blk * 0.75f);
            }
        }

        // Find target proxy
        EnemyProxy? targetEnemy = null;
        if (target != null)
            targetEnemy = state.Enemies.FirstOrDefault(e => e.Creature == target);

        // ── Apply HP cost (e.g. HEMOKINESIS, OFFERING) ──
        if (entry.HpCost > 0)
        {
            state.Hp -= entry.HpCost;
        }

        // ── Apply damage ──
        if (dmg > 0)
        {
            if (entry.IsAoe)
            {
                foreach (var e in state.Enemies.Where(e => e.IsAlive))
                {
                    int actualDmg = ApplyDamageModifiers(dmg, e);
                    // Cap damage at remaining HP (avoid overkill inflation)
                    actualDmg = Math.Min(actualDmg, e.Hp);
                    e.Hp -= actualDmg;
                    state.TotalDamageDealt += actualDmg;
                    // ── Thorns: enemy reflects damage when hit ──
                    if (e.Thorns > 0 && actualDmg > 0)
                        state.Hp -= e.Thorns;
                    if (e.Hp <= 0) e.IsAlive = false;
                }
            }
            else if (targetEnemy != null && targetEnemy.IsAlive)
            {
                int actualDmg = ApplyDamageModifiers(dmg, targetEnemy);
                targetEnemy.Hp -= actualDmg;
                state.TotalDamageDealt += actualDmg;
                // ── Thorns: enemy reflects damage when hit ──
                if (targetEnemy.Thorns > 0 && actualDmg > 0)
                    state.Hp -= targetEnemy.Thorns;
                if (targetEnemy.Hp <= 0) targetEnemy.IsAlive = false;
            }
        }

        // ── Apply block ──
        // PANIC_BUTTON debuff: no block from cards for 2 turns
        if (state.PanicButtonTurns > 0 && entry.CardId != "PANIC_BUTTON")
        {
            blk = 0; // Block from cards disabled by PANIC_BUTTON debuff
        }
        state.Block += blk;
        state.TotalBlockGained += blk;

        // ── PANIC_BUTTON (紧急按钮): 0-cost, 30 block, exhausts, no block from cards for 2 turns ──
        if (entry.CardId == "PANIC_BUTTON")
        {
            state.PanicButtonTurns = 2;
        }

        // ── Apply vulnerable ──
        if (entry.AppliesVulnerable && entry.VulnerableStacks > 0)
        {
            if (entry.IsAoe)
            {
                foreach (var e in state.Enemies.Where(e => e.IsAlive))
                    e.Vulnerable += entry.VulnerableStacks;
            }
            else if (targetEnemy != null && targetEnemy.IsAlive)
            {
                targetEnemy.Vulnerable += entry.VulnerableStacks;
            }
        }

        // ── Apply weak ──
        if (entry.AppliesWeak && entry.WeakStacks > 0)
        {
            if (entry.IsAoe)
            {
                foreach (var e in state.Enemies.Where(e => e.IsAlive))
                    e.Weak += entry.WeakStacks;
            }
            else if (targetEnemy != null && targetEnemy.IsAlive)
            {
                targetEnemy.Weak += entry.WeakStacks;
            }
        }

        // ── Apply strength gain ──
        if (entry.GrantsStrength && entry.StrengthAmount > 0)
        {
            state.Strength += entry.StrengthAmount;
            state.StrengthGained += entry.StrengthAmount;
        }

        // ── Apply dexterity gain ──
        if (entry.GrantsDexterity && entry.DexterityAmount > 0)
        {
            state.Dexterity += entry.DexterityAmount;
            state.DexterityGained += entry.DexterityAmount;
        }

        // ── Apply energy gain ──
        if (entry.EnergyGain > 0)
        {
            state.Energy += entry.EnergyGain;
            state.TotalEnergyGained += entry.EnergyGain;
        }

        // ── Track 0-cost energy cards (Optimization 6) ──
        if (entry.CanonicalCost == 0 && entry.EnergyGain > 0)
        {
            state.ZeroCostEnergyCardsPlayed++;
        }

        // ── Track exhausted basic cards (Optimization 3) ──
        // Exhaust-engine cards typically remove 1-2 basics from the deck.
        // SECOND_WIND exhausts ALL non-attacks → estimate 2 basics.
        if (entry.CardId == "SECOND_WIND")
            state.ExhaustedBasicCount += 2;
        else if (entry.CardId == "FIEND_FIRE")
            state.ExhaustedBasicCount += 2; // exhausts entire hand
        else if (entry.CardId == "BURNING_PACT" || entry.CardId == "TRUE_GRIT"
              || entry.CardId == "SEVER_SOUL" || entry.CardId == "HAVOC"
              || entry.CardId == "PURITY")
            state.ExhaustedBasicCount += 1;

        // ── Apply poison ──
        if (entry.AppliesPoison && entry.PoisonStacks > 0)
        {
            if (entry.IsAoe)
            {
                foreach (var e in state.Enemies.Where(e => e.IsAlive))
                {
                    e.Poison += entry.PoisonStacks;
                    state.PoisonOnEnemies += entry.PoisonStacks;
                }
            }
            else if (targetEnemy != null && targetEnemy.IsAlive)
            {
                targetEnemy.Poison += entry.PoisonStacks;
                state.PoisonOnEnemies += entry.PoisonStacks;
            }
        }

        // ── Orb channeling / evoking (Defect) ──────────────────────────
        // STS2 orb passive values per type:
        //   Lightning: 3+Focus damage to random enemy (passive), 8+Focus (evoke)
        //   Frost:     2+Focus block (passive), 5+Focus block (evoke)
        //   Dark:      +6 damage/turn (passive), accumulated damage (evoke)
        //   Plasma:    +1 energy at turn START (passive), +2 energy (evoke)
        //   Glass:     4-1/turn AOE damage (passive), current×2 AOE (evoke)
        // Evoke order: rightmost (index 0) is evoked first (FIFO).
        // When orb slots are full, channeling evokes the rightmost orb.
        int passiveLightningDmg = 3 + state.Focus;
        int passiveFrostBlk = 2 + state.Focus;
        int evokeLightningDmg = 8 + state.Focus;
        int evokeFrostBlk = 5 + state.Focus;

        // Apply per-card orb effects
        string upper = entry.CardId.ToUpperInvariant();
        switch (upper)
        {
            // ── Direct evoke cards ──
            case "DUALCAST":
                // DUALCAST evokes the SAME rightmost orb twice.
                if (state.OrbQueue.Count > 0)
                {
                    string dualOrbType = state.OrbQueue[0];
                    state.OrbQueue.RemoveAt(0);
                    // Decrement orb count
                    switch (dualOrbType)
                    {
                        case "Lightning": state.LightningOrbs--; if (state.LightningOrbs < 0) state.LightningOrbs = 0; break;
                        case "Frost": state.FrostOrbs--; if (state.FrostOrbs < 0) state.FrostOrbs = 0; break;
                        case "Dark": state.DarkOrbs--; if (state.DarkOrbs < 0) state.DarkOrbs = 0; break;
                        case "Plasma": state.PlasmaOrbs--; if (state.PlasmaOrbs < 0) state.PlasmaOrbs = 0; break;
                    }
                    // Apply evoke effects twice for the same orb
                    ApplyOrbEvokeEffect(state, dualOrbType, evokeLightningDmg, evokeFrostBlk, targetEnemy);
                    ApplyOrbEvokeEffect(state, dualOrbType, evokeLightningDmg, evokeFrostBlk, targetEnemy);
                }
                break;
            case "MULTI_CAST":
                // Evoke rightmost orb N times (N = energy spent, min 1)
                int multiCount = entry.CostsX ? Math.Max(1, spentEnergy) : 1;
                for (int m = 0; m < multiCount; m++)
                    EvokeRightmostOrb(state, evokeLightningDmg, evokeFrostBlk, targetEnemy);
                break;
            case "RECURSION":
                // Evoke rightmost orb, then re-channel it
                string? evokedType = EvokeRightmostOrb(state, evokeLightningDmg, evokeFrostBlk, targetEnemy);
                if (evokedType != null)
                    ChannelOrb(state, evokedType);
                break;

            // ── Channel cards ──
            case "ZAP":           ChannelOrb(state, "Lightning"); break;
            case "BALL_LIGHTNING": entry.BaseDamage = Math.Max(entry.BaseDamage, 7);
                                   ChannelOrb(state, "Lightning"); break;
            case "COLD_SNAP":     entry.BaseDamage = Math.Max(entry.BaseDamage, 6);
                                   ChannelOrb(state, "Frost"); break;
            case "CHILL":         for (int i = 0; i < state.Enemies.Count(e => e.IsAlive); i++)
                                       ChannelOrb(state, "Frost"); break;
            case "GLACIER":       entry.BaseBlock = Math.Max(entry.BaseBlock, 7);
                                   ChannelOrb(state, "Frost");
                                   ChannelOrb(state, "Frost"); break;
            case "DARKNESS":      ChannelOrb(state, "Dark"); break;
            case "FUSION":        ChannelOrb(state, "Plasma"); break;
            case "CHAOS":         ChannelOrb(state, "Lightning"); break; // random — use most common
            case "RAINBOW":       ChannelOrb(state, "Lightning");
                                   ChannelOrb(state, "Frost");
                                   ChannelOrb(state, "Dark"); break;

            // ── Focus / orb modifiers ──
            case "DEFRAGMENT":    state.Focus += entry.IsPower ? 1 : 0; break; // usually applied via power
            case "BIASED_COGNITION": state.Focus += 4; break;
            case "CONSUME":       state.Focus += 2; state.OrbSlots = Math.Max(1, state.OrbSlots - 1); break;
            case "LOOP":          state.LoopCount++; break;
            case "CAPACITOR":     state.OrbSlots += 2; break; // approximate
        }
        // Recalc damage after orb modification (cards like BLIZZARD scale with frost count)
        if (upper == "BLIZZARD")
            entry.BaseDamage = Math.Max(entry.BaseDamage, state.FrostOrbs * 6);

        // ── Track powers ──
        if (entry.IsPower)
        {
            state.PowersPlayed++;
            if (!state.ActivePowers.Contains(entry.CardId))
                state.ActivePowers.Add(entry.CardId);
        }

        // ── Track card selects ──
        if (entry.HasCardSelect)
            state.CardSelectsPlayed++;

        // ── Played card goes to discard pile (needed for reshuffle simulation) ──
        state.DiscardPile.Add(entry.Card);
    }

    // ── Orb helpers (Defect) ──────────────────────────────────────────────

    /// <summary>Channel an orb of the given type. Handles full-slot evoke.</summary>
    private static void ChannelOrb(SearchState state, string orbType)
    {
        // If orb slots are full, evoke the rightmost orb first
        int totalOrbs = state.LightningOrbs + state.FrostOrbs + state.DarkOrbs
                        + state.PlasmaOrbs;
        if (totalOrbs >= state.OrbSlots && state.OrbQueue.Count > 0)
        {
            // Evoke rightmost (index 0) — use default evoke values
            int evokeLDmg = 8 + state.Focus;
            int evokeFBlk = 5 + state.Focus;
            EvokeRightmostOrb(state, evokeLDmg, evokeFBlk, null);
        }

        // Add orb to the LEFT (end of queue — newest position)
        state.OrbQueue.Add(orbType);
        switch (orbType)
        {
            case "Lightning": state.LightningOrbs++; break;
            case "Frost":     state.FrostOrbs++; break;
            case "Dark":      state.DarkOrbs++; state.TotalDarkOrbDamage += state.BaseDarkOrbDamage; break;
            case "Plasma":    state.PlasmaOrbs++; break;
            case "Glass":     state.LightningOrbs++; break; // Glass approximated as Lightning
        }
    }

    /// <summary>
    /// Apply the evoke effect of an orb WITHOUT modifying the orb queue or counts.
    /// Used by DUALCAST (same orb evokes twice) and EvokeRightmostOrb (handles
    /// queue/count management separately).
    /// </summary>
    private static void ApplyOrbEvokeEffect(SearchState state, string orbType,
        int evokeLightningDmg, int evokeFrostBlk, EnemyProxy? targetEnemy)
    {
        switch (orbType)
        {
            case "Lightning":
                // Deal evoke damage to lowest HP enemy (approximating random target)
                if (targetEnemy == null)
                    targetEnemy = state.Enemies.Where(e => e.IsAlive)
                        .OrderBy(e => e.Hp).FirstOrDefault();
                if (targetEnemy != null && targetEnemy.IsAlive)
                {
                    int dmg = Math.Min(evokeLightningDmg, targetEnemy.Hp);
                    targetEnemy.Hp -= dmg;
                    state.TotalDamageDealt += dmg;
                    if (targetEnemy.Hp <= 0) targetEnemy.IsAlive = false;
                }
                break;

            case "Frost":
                state.Block += evokeFrostBlk;
                state.TotalBlockGained += evokeFrostBlk;
                break;

            case "Dark":
                // Evoke deals accumulated dark damage to lowest HP enemy
                int darkDmg = state.BaseDarkOrbDamage;
                if (targetEnemy == null)
                    targetEnemy = state.Enemies.Where(e => e.IsAlive)
                        .OrderBy(e => e.Hp).FirstOrDefault();
                if (targetEnemy != null && targetEnemy.IsAlive)
                {
                    int dmg = Math.Min(darkDmg, targetEnemy.Hp);
                    targetEnemy.Hp -= dmg;
                    state.TotalDamageDealt += dmg;
                    if (targetEnemy.Hp <= 0) targetEnemy.IsAlive = false;
                }
                break;

            case "Plasma":
                state.Energy += 2; // evoke: +2 energy
                state.TotalEnergyGained += 2;
                break;
        }
    }

    /// <summary>
    /// Evoke the rightmost orb (index 0 in OrbQueue). Returns the orb type evoked,
    /// or null if no orbs exist. Applies damage/block/energy to the state.
    /// </summary>
    private static string? EvokeRightmostOrb(SearchState state, int evokeLightningDmg,
        int evokeFrostBlk, EnemyProxy? targetEnemy)
    {
        if (state.OrbQueue.Count == 0) return null;

        string orbType = state.OrbQueue[0];
        state.OrbQueue.RemoveAt(0);

        // Decrement orb count
        switch (orbType)
        {
            case "Lightning":
                state.LightningOrbs--;
                if (state.LightningOrbs < 0) state.LightningOrbs = 0;
                break;
            case "Frost":
                state.FrostOrbs--;
                if (state.FrostOrbs < 0) state.FrostOrbs = 0;
                break;
            case "Dark":
                state.DarkOrbs--;
                if (state.DarkOrbs < 0) state.DarkOrbs = 0;
                break;
            case "Plasma":
                state.PlasmaOrbs--;
                if (state.PlasmaOrbs < 0) state.PlasmaOrbs = 0;
                break;
        }

        // Apply the actual evoke effect
        ApplyOrbEvokeEffect(state, orbType, evokeLightningDmg, evokeFrostBlk, targetEnemy);

        return orbType;
    }

    // ── Damage modifiers ─────────────────────────────────────────────────

    private static int ApplyDamageModifiers(int baseDamage, EnemyProxy enemy)
    {
        float dmg = baseDamage;

        // ── Buffer: negates one instance of HP loss completely ─────────
        if (enemy.Buffer > 0 && dmg > 0)
        {
            enemy.Buffer--;
            return 0; // No damage, no block consumed, no Vulnerable consumed
        }

        // ── Subtract enemy block first ──────────────────────────────────
        if (enemy.Block > 0)
        {
            int blocked = Math.Min((int)dmg, enemy.Block);
            enemy.Block -= blocked;
            dmg -= blocked;
        }

        // ── Intangible: cap damage per hit ──────────────────────────────
        // In STS2, Intangible reduces ALL damage and HP loss to 1 per instance.
        // e.g., Nemesis gains Intangible every other turn.
        if (enemy.Intangible > 0 && dmg > 1)
        {
            dmg = 1;
        }

        // ── Vulnerable on enemy: +50% unblocked damage ──────────────────
        if (enemy.Vulnerable > 0 && dmg > 0)
        {
            dmg *= 1.5f;
            enemy.Vulnerable--;
        }

        // Enemy Weak does NOT reduce player damage — it reduces enemy's OWN attack output.
        // This is tracked via intent damage reduction (scored separately).

        return Math.Max(0, (int)Math.Floor(dmg));
    }

    // ── Potion effect application ──────────────────────────────────────────

    /// <summary>Map known potion IDs to their combat effects.</summary>
    private static PotionEffect? CreatePotionEffect(string potionId)
    {
        var upper = potionId.ToUpperInvariant().Replace(" ", "_");

        // ── Damage potions ──
        if (upper.Contains("FIRE") || (upper.Contains("EXPLOSIVE") && !upper.Contains("AMPOULE")))
            return new PotionEffect { Id = upper, Damage = 20, IsAoe = false };
        if (upper.Contains("EXPLOSIVE_AMPOULE"))
            return new PotionEffect { Id = upper, Damage = 10, IsAoe = true };

        // ── Buff/Debuff potions ──
        if (upper.Contains("FEAR") || upper.Contains("ESSENCE_OF_FEAR"))
            return new PotionEffect { Id = upper, VulnerableStacks = 3 };
        if (upper.Contains("WEAK") || upper.Contains("ESSENCE_OF_WEAK"))
            return new PotionEffect { Id = upper, WeakStacks = 3 };
        if (upper.Contains("STRENGTH") || upper.Contains("FLEX"))
            return new PotionEffect { Id = upper, StrengthGain = 2 };
        if (upper.Contains("DEXTERITY") || upper.Contains("SPEED"))
            return new PotionEffect { Id = upper, DexterityGain = 2 };

        // ── Block/Defense potions ──
        if (upper.Contains("BLOCK") || upper.Contains("BARRIER") || upper.Contains("STEEL"))
            return new PotionEffect { Id = upper, BlockGain = 12 };
        if (upper.Contains("GHOST") || upper.Contains("INTANGIBLE"))
            return new PotionEffect { Id = upper, BlockGain = 999 };

        // ── Energy potions ──
        if (upper.Contains("ENERGY") || upper.Contains("BOTTLED_MIRACLE"))
            return new PotionEffect { Id = upper, EnergyGain = 2 };

        // ── Heal potions ──
        if (upper.Contains("BLOOD") || upper.Contains("HEAL") || upper.Contains("REGEN"))
            return new PotionEffect { Id = upper, HealAmount = 15 };
        if (upper.Contains("FAIRY") || upper.Contains("REVIVE"))
            return new PotionEffect { Id = upper, HealAmount = 30 };

        // ── Poison potions ──
        if (upper.Contains("POISON"))
            return new PotionEffect { Id = upper, PoisonStacks = 6 };

        // ── Other utility potions (approximate) ──
        if (upper.Contains("DISTILLED") || upper.Contains("ENTROPIC") ||
            upper.Contains("GAMBLER") || upper.Contains("SWIFT") || upper.Contains("POWER"))
            return new PotionEffect { Id = upper, EnergyGain = 1 };

        if (upper.Contains("DUPLICATION") || upper.Contains("LIQUID_MEMORIES"))
            return new PotionEffect { Id = upper, EnergyGain = 2 };

        if (upper.Contains("SNECKO") || upper.Contains("SMOKE"))
            return new PotionEffect { Id = upper };

        if (upper.Contains("FRUIT") || upper.Contains("JUICE"))
            return new PotionEffect { Id = upper, HealAmount = 5 };

        if (upper.Contains("HEART") || upper.Contains("IRON"))
            return new PotionEffect { Id = upper, BlockGain = 6 };

        if (upper.Contains("BRONZE") || upper.Contains("THORNS"))
            return new PotionEffect { Id = upper, Damage = 10, IsAoe = true };

        return new PotionEffect { Id = upper, EnergyGain = 1 };
    }

    private static void ApplyPotionEffects(SearchState state, PotionEffect potion)
    {
        if (potion.Damage > 0)
        {
            if (potion.IsAoe)
            {
                foreach (var e in state.Enemies.Where(e => e.IsAlive))
                {
                    int dmg = ApplyDamageModifiers(potion.Damage, e);
                    dmg = Math.Min(dmg, e.Hp);
                    e.Hp -= dmg;
                    state.TotalDamageDealt += dmg;
                    if (e.Hp <= 0) e.IsAlive = false;
                }
            }
            else
            {
                var target = state.Enemies.Where(e => e.IsAlive).OrderBy(e => e.Hp).FirstOrDefault();
                if (target != null)
                {
                    int dmg = ApplyDamageModifiers(potion.Damage, target);
                    dmg = Math.Min(dmg, target.Hp);
                    target.Hp -= dmg;
                    state.TotalDamageDealt += dmg;
                    if (target.Hp <= 0) target.IsAlive = false;
                }
            }
        }

        if (potion.VulnerableStacks > 0)
        {
            var target = state.Enemies.Where(e => e.IsAlive)
                .OrderByDescending(e => e.MaxHp).FirstOrDefault();
            if (target != null) target.Vulnerable += potion.VulnerableStacks;
        }

        if (potion.WeakStacks > 0)
        {
            var target = state.Enemies.Where(e => e.IsAlive)
                .OrderByDescending(e => e.IntentDamage).FirstOrDefault();
            if (target != null) target.Weak += potion.WeakStacks;
        }

        if (potion.StrengthGain > 0) { state.Strength += potion.StrengthGain; state.StrengthGained += potion.StrengthGain; }
        if (potion.DexterityGain > 0) { state.Dexterity += potion.DexterityGain; state.DexterityGained += potion.DexterityGain; }
        if (potion.BlockGain > 0) { state.Block += potion.BlockGain; state.TotalBlockGained += potion.BlockGain; }
        if (potion.EnergyGain > 0) state.Energy += potion.EnergyGain;
        if (potion.HealAmount > 0) state.Hp = Math.Min(state.MaxHp, state.Hp + potion.HealAmount);

        if (potion.PoisonStacks > 0)
        {
            var target = state.Enemies.Where(e => e.IsAlive)
                .OrderByDescending(e => e.MaxHp).FirstOrDefault();
            if (target != null) { target.Poison += potion.PoisonStacks; state.PoisonOnEnemies += potion.PoisonStacks; }
        }

        MainFile.Logger.Info($"[SolverDBG] Potion {potion.Id} applied: dmg={potion.Damage} vuln={potion.VulnerableStacks} weak={potion.WeakStacks} str={potion.StrengthGain} dex={potion.DexterityGain} blk={potion.BlockGain} en={potion.EnergyGain} heal={potion.HealAmount}");
    }

    // ── State evaluation ─────────────────────────────────────────────────

    private static double EvaluateState(SearchState state, int totalIncoming)
    {
        double score = 0;
        var cfg = _cfg;
        var p = SolverParams.Instance.CombatSolver.Scoring;

        // ── Elite/Boss aggression multiplier ─────────────────────────
        // Elites have more HP and deal more damage — playing defensively
        // just delays the inevitable. We increase damage/offense value
        // and slightly decrease excess block value to favor aggression.
        double eliteMult = 1.0;
        double bossMult = 1.0;
        if (_isBossCombat)
        {
            // Boss: heavy aggression — kill before the scaling overwhelms us
            eliteMult = 1.6;  // +60% damage value
            bossMult = 1.8;   // +80% kill/damage value
        }
        else if (_isEliteCombat)
        {
            // Elite: moderate aggression — elites have high HP pools
            eliteMult = 1.35; // +35% damage value
            bossMult = 1.0;
        }

        // ── Boss-specific strategy adjustments ────────────────────────
        var bs = _bossStrategy;
        double bsDamageMult = bs?.DamageMult ?? 1.0;
        double bsBlockMult = bs?.BlockMult ?? 1.0;
        double bsStrengthMult = bs?.StrengthMult ?? 1.0;
        double bsPowerMult = bs?.PowerMult ?? 1.0;
        double bsAoeBonus = bs?.AoeBonus ?? 0.0;

        // ── Estimated remaining turns (for delayed damage valuation) ──
        // sum enemy HP / (estimated damage per turn) → how many turns this fight will last
        double remainingEnemyHp = state.Enemies.Where(e => e.IsAlive).Sum(e => (double)Math.Max(1, e.Hp));
        double estDmgPerTurn = Math.Max(1.0,
            state.TotalDamageDealt / Math.Max(1, state.CardsPlayed) * Math.Max(1, state.Hand.Count));
        // Cap: minimum 1 turn (always at least 1 more turn), maximum 20 (prevent infinite scaling)
        double estimatedRemainingTurns = Math.Clamp(remainingEnemyHp / Math.Max(1.0, estDmgPerTurn), 1.0, 20.0);
        // For elites/bosses: fights naturally last longer, clamp higher
        if (_isBossCombat) estimatedRemainingTurns = Math.Max(estimatedRemainingTurns, 4.0);
        else if (_isEliteCombat) estimatedRemainingTurns = Math.Max(estimatedRemainingTurns, 2.5);
        // Persistent effect multiplier: how many turns this effect will be active
        // Decay factor: effects in later turns are worth less (enemies die, combat ends)
        double persistentMultiplier = Math.Min(estimatedRemainingTurns, 8.0); // Cap at 8x

        // Combine: elite/boss base × boss-specific
        double finalDamageMult = Math.Max(1.0, eliteMult) * bsDamageMult;
        // Elite/Boss block discount: blocking is less valuable vs elites/bosses
        // where aggression and damage racing are the correct strategy.
        double eliteBlockBase = 1.0;
        if (_isBossCombat) eliteBlockBase = p.BossBlockMultiplier;
        else if (_isEliteCombat) eliteBlockBase = p.EliteBlockMultiplier;
        double finalBlockMult = eliteBlockBase * bsBlockMult;
        double finalKillMult = Math.Max(eliteMult, bossMult) * bsDamageMult;

        // ── Enemy kills ──
        foreach (var e in state.Enemies.Where(e => !e.IsAlive))
            score += (p.KillBase + e.MaxHp * p.KillPerMaxHp) * cfg.KillWeight * p.KillWeight * finalKillMult;

        // ── Damage dealt ── (boosted for elites/bosses + boss-specific)
        score += state.TotalDamageDealt * p.DamagePerPoint * cfg.DamageWeight * p.DamageWeight * finalDamageMult;

        // ── Focus-fire strategy: reward concentrating damage on one enemy ──
        // The solver's TotalDamageDealt is aggregate — spending 20 dmg on one
        // enemy is identical to 10+10 on two enemies. This section adds a bonus
        // for damage concentration, which is the correct combat strategy:
        // kill one enemy at a time to remove its damage output quickly.
        var aliveList = state.Enemies.Where(e => e.IsAlive).ToList();
        if (aliveList.Count >= 2)
        {
            // Find the most damaged enemy (lowest HP percentage)
            var mostDamaged = aliveList.OrderBy(e => (double)e.Hp / Math.Max(1, e.MaxHp)).First();
            double mostDamagedHpPct = (double)mostDamaged.Hp / Math.Max(1, mostDamaged.MaxHp);

            var others = aliveList.Where(e => e != mostDamaged).ToList();
            double avgOthersHpPct = others.Average(e => (double)e.Hp / Math.Max(1, e.MaxHp));

            // Concentration gap: how much more damaged is the focus target vs others
            // Positive = damage is concentrated, ~0 = damage is spread evenly
            double concentrationGap = avgOthersHpPct - mostDamagedHpPct;

            if (concentrationGap > 0.05)
            {
                // Approximate damage dealt to the focus target
                double damageOnFocus = mostDamaged.MaxHp - mostDamaged.Hp;
                score += damageOnFocus * concentrationGap * p.FocusFireMultiplier * finalDamageMult * 0.1;
            }

            // ── Boss/minion priority: bonus for damaging the main enemy ──
            // In multi-enemy combats, the highest-MaxHp enemy is the primary target.
            // User directive: "优先击杀主怪" (kill the main enemy first, not minions).
            // Rationale: in normal combats, killing the main enemy kills all its minions.
            var maxHpEnemy = aliveList.OrderByDescending(e => e.MaxHp).First();
            var minHpEnemy = aliveList.OrderBy(e => e.MaxHp).First();
            bool hasMinions = maxHpEnemy != minHpEnemy && minHpEnemy.MaxHp > 0;

            if (hasMinions && maxHpEnemy.MaxHp >= minHpEnemy.MaxHp * 2)
            {
                // ── Elite/Boss combat: 2× HP gap confirms boss+minion setup ──
                double bossHpPct = (double)maxHpEnemy.Hp / Math.Max(1, maxHpEnemy.MaxHp);
                double bossDamageTaken = maxHpEnemy.MaxHp - maxHpEnemy.Hp;

                // Bonus: damage to the boss is worth more
                score += bossDamageTaken * p.BossPriorityMultiplier * finalDamageMult * 0.1;

                // Penalty: damaging minions while boss is still healthy (>50% HP)
                if (bossHpPct > 0.5)
                {
                    var minions = aliveList.Where(e => e != maxHpEnemy).ToList();
                    double minionDamage = minions.Sum(e => (double)Math.Max(0, e.MaxHp - e.Hp));
                    score -= minionDamage * p.MinionDamagePenalty * finalDamageMult * 0.1;
                }
            }
            else if (hasMinions && !_isEliteCombat && !_isBossCombat)
            {
                // ── Normal combat: ALWAYS penalize minion damage ──
                // In normal fights, killing the main enemy kills ALL its minions,
                // so any damage dealt to minions is completely wasted.
                // Apply a heavy penalty regardless of boss HP threshold.
                var minions = aliveList.Where(e => e != maxHpEnemy).ToList();
                double minionDamage = minions.Sum(e => (double)Math.Max(0, e.MaxHp - e.Hp));
                // Normal combat minion penalty is 3× stronger than elite/boss penalty
                // because minion damage here is truly wasted (they die with the main enemy)
                score -= minionDamage * p.MinionDamagePenalty * finalDamageMult * 0.3;

                // Also boost priority of damaging the main enemy
                double mainDamageTaken = maxHpEnemy.MaxHp - maxHpEnemy.Hp;
                score += mainDamageTaken * p.BossPriorityMultiplier * finalDamageMult * 0.15;
            }
        }

        // ── Block vs incoming (recalculate from alive enemies only) ──
        int realIncoming = state.Enemies.Where(e => e.IsAlive).Sum(e =>
        {
            int dmg = e.IntentDamage + e.Strength;
            if (e.Weak > 0) dmg = (int)Math.Floor(dmg * 0.75);
            return dmg;
        });
        int remainingIncoming = Math.Max(0, realIncoming - state.Block);
        double hpRatio = state.Hp / (double)Math.Max(1, state.MaxHp);
        double lowHpThreshold = p.HealthPenaltyLowHpThreshold;
        if (remainingIncoming > 0 && hpRatio < lowHpThreshold)
            score -= remainingIncoming * p.HealthPenaltyLowHpMultiplier * cfg.HealthPenaltyWeight * p.HealthPenaltyWeight;
        else if (remainingIncoming > 0)
            score -= remainingIncoming * p.HealthPenaltyNormalMultiplier * cfg.HealthPenaltyWeight * p.HealthPenaltyWeight;

        // Penalize low HP regardless of block
        if (hpRatio < lowHpThreshold)
            score -= (int)((lowHpThreshold - hpRatio) * state.MaxHp) * p.LowHpRatioPenaltyMultiplier * cfg.HealthPenaltyWeight * p.HealthPenaltyWeight;

        // ── Player DOT penalty (Constrict, etc.) ──────────────────────────
        // DOT is unblockable end-of-turn damage. It MUST be accounted for
        // or the solver will optimistically ignore it and die.
        if (state.PlayerDOT > 0)
        {
            // Base penalty: each point of DOT is as bad as unblocked incoming damage
            score -= state.PlayerDOT * p.HealthPenaltyNormalMultiplier * cfg.HealthPenaltyWeight * p.HealthPenaltyWeight * 2.0;

            // Lethality check: if DOT + remaining incoming > current HP, this state leads to death
            int totalUnavoidable = state.PlayerDOT + remainingIncoming;
            if (totalUnavoidable >= state.Hp)
            {
                // Massive penalty for lethal states — this should dominate all other considerations
                score -= totalUnavoidable * 200.0;
            }
        }

        // ── Block gained ──
        // For elites/bosses: reduce excess block value — don't over-block
        // Boss strategy can override: Waterfall Giant needs more block, Insatiable needs less
        int neededBlock = Math.Min(state.TotalBlockGained, realIncoming);
        score += neededBlock * p.BlockPerNeededPoint * cfg.BlockWeight * p.BlockWeight * finalBlockMult;
        int excessBlock = state.TotalBlockGained - neededBlock;
        if (excessBlock > 0)
        {
            double excessMultiplier = _isEliteCombat || _isBossCombat ? 0.5 : 1.0;
            excessMultiplier *= finalBlockMult;
            score += excessBlock * p.BlockPerExcessPoint * cfg.BlockWeight * p.BlockWeight * excessMultiplier;
        }
        // Only score pre-existing block (from before this turn), not block
        // gained this turn — TotalBlockGained already covers new block.
        // This prevents double-counting: playing 10 block should not give
        // both "block gained" AND "existing block" credit for the same 10.
        int existingBlock = Math.Max(0, state.Block - state.TotalBlockGained);
        if (existingBlock > 0)
            score += Math.Min(existingBlock, realIncoming) * p.ExistingBlockPerPoint * cfg.BlockWeight * p.BlockWeight * finalBlockMult;

        // ── Status effects on surviving enemies ── (boosted for elites/bosses)
        foreach (var e in state.Enemies.Where(e => e.IsAlive))
        {
            score += e.Vulnerable * p.VulnerablePerStack * cfg.VulnerableWeight * p.VulnerableWeight * finalDamageMult;
            score += e.Weak * p.WeakPerStack * cfg.WeakWeight * p.WeakWeight * finalDamageMult;
        }

        // ── Strength gained ── (persistent: affects ALL future attack damage)
        // Each point of Strength adds 1 damage to every attack for the rest of combat
        score += state.StrengthGained * p.StrengthPerPoint * cfg.StrengthWeight * p.StrengthWeight
            * Math.Max(1.0, eliteMult) * bsStrengthMult * persistentMultiplier;

        // ── Dexterity gained ── (persistent: affects ALL future block)
        score += state.DexterityGained * p.DexterityPerPoint * cfg.DexterityWeight * p.DexterityWeight * persistentMultiplier;

        // ── Powers played ── (persistent: powers affect ALL future turns)
        score += state.PowersPlayed * p.PowerPerPlayed * cfg.PowerWeight * p.PowerWeight * bsPowerMult * persistentMultiplier;

        // ── Card selects played ──
        score += state.CardSelectsPlayed * p.CardSelectPerPlayed * cfg.PowerWeight;

        // ── PANIC_BUTTON debuff penalty: each turn of "no block from cards" is costly ──
        if (state.PanicButtonTurns > 0)
            score -= state.PanicButtonTurns * 25.0; // Heavy penalty: can't block from cards

        // ── Energy efficiency ──
        score += state.Energy * p.EnergyPerPoint;

        // ── Poison value (Silent) — delayed damage: each stack deals 1/turn ──
        if (cfg.UsesPoison)
        {
            // Base: each poison stack = 1 damage per turn for remaining turns
            double poisonValue = state.PoisonOnEnemies * p.PoisonPerStack * cfg.PoisonDamageWeight * p.PoisonWeight * finalDamageMult;
            // Multiply by persistent turns — poison deals damage EVERY turn until combat ends
            poisonValue *= persistentMultiplier;
            score += poisonValue;
        }

        // ── Orb value (Defect) — delayed damage: passive effects trigger EVERY turn ──
        if (cfg.UsesOrbs)
        {
            // Passive: Lightning = 3+Focus damage/turn to random enemy
            // This deals damage at end of EVERY turn — highly valuable persistent damage
            double lightningPassiveVal = (3 + state.Focus) * p.OrbLightningMult * finalDamageMult;
            score += state.LightningOrbs * lightningPassiveVal * cfg.OrbValueWeight * p.OrbValueWeight * persistentMultiplier;

            // Passive: Frost = 2+Focus block/turn (applies at end of turn, carries to next)
            double frostPassiveVal = (2 + state.Focus) * p.OrbFrostMult * finalBlockMult;
            score += state.FrostOrbs * frostPassiveVal * cfg.OrbValueWeight * p.OrbValueWeight * persistentMultiplier;

            // Dark: accumulates 6/turn passively. Value = accumulated + future accumulation × turns
            double darkAccumulatePerTurn = 6.0;
            double darkCurrentAvg = state.DarkOrbs > 0
                ? (state.TotalDarkOrbDamage / Math.Max(1, state.DarkOrbs))
                : darkAccumulatePerTurn;
            // Current accumulated value + future growth over remaining turns
            double darkValue = darkCurrentAvg + darkAccumulatePerTurn * persistentMultiplier;
            score += state.DarkOrbs * darkValue * p.OrbDarkMult * cfg.OrbValueWeight * p.OrbValueWeight * finalDamageMult;

            // Plasma: +1 energy at turn START every turn → total energy = plasma × remaining turns
            score += state.PlasmaOrbs * p.EnergyPerPoint * cfg.OrbValueWeight * p.OrbValueWeight * persistentMultiplier;

            // Focus: each point improves ALL orb passive AND evoke effects for every remaining turn
            int totalOrbs = state.LightningOrbs + state.FrostOrbs + state.DarkOrbs + state.PlasmaOrbs;
            score += state.Focus * totalOrbs * p.OrbFocusPerPoint * cfg.OrbValueWeight * p.OrbValueWeight * persistentMultiplier;

            // Orb slots: empty slots have value (room to channel — future potential)
            int usedSlots = state.OrbQueue.Count;
            int emptySlots = Math.Max(0, state.OrbSlots - usedSlots);
            score += emptySlots * 15 * cfg.OrbValueWeight * p.OrbValueWeight;

            // Loop: triggers first orb passive N more times per turn — persistent effect
            if (state.LoopCount > 0 && state.OrbQueue.Count > 0)
            {
                string firstOrb = state.OrbQueue[0];
                double loopVal = state.LoopCount * (firstOrb switch
                {
                    "Lightning" => (3 + state.Focus) * p.OrbLightningMult * finalDamageMult,
                    "Frost" => (2 + state.Focus) * p.OrbFrostMult * finalBlockMult,
                    "Dark" => darkValue * p.OrbDarkMult * finalDamageMult,
                    "Plasma" => p.EnergyPerPoint,
                    _ => 5.0
                });
                score += loopVal * cfg.OrbValueWeight * p.OrbValueWeight * persistentMultiplier;
            }
        }

        // ── Star conservation (Necrobinder) ──
        if (cfg.UsesStars)
            score += state.Stars * p.StarPerStack * cfg.StarConservationWeight * p.StarWeight;

        // ── AoE bonus per alive enemy (boss-specific: The Kin, Kaiser Crab) ──
        int aliveCount = state.Enemies.Count(e => e.IsAlive);
        if (bsAoeBonus > 0 && aliveCount >= 2)
        {
            // Bonus proportional to AoE value — estimate from damage dealt to non-primary targets
            // Approximate: if we dealt damage with AoE cards, it's reflected in TotalDamageDealt
            // We add an extra bonus per enemy beyond the first
            score += state.TotalDamageDealt * (aliveCount - 1) * bsAoeBonus * 0.05;
        }

        // ── Card play penalty (boss-specific: Aeonglass, Soul Fysh, Knowledge Demon) ──
        if (bs?.CardPlayPenaltyThreshold < 99 && state.CardsPlayed > bs.CardPlayPenaltyThreshold)
        {
            int excessCards = state.CardsPlayed - bs.CardPlayPenaltyThreshold;
            score -= excessCards * bs.CardPlayPenalty * 5.0;
        }

        // ═══════════════════════════════════════════════════════════════════
        // NEW: Sequencing bonus (Optimization 2) — reward correct play order
        // ═══════════════════════════════════════════════════════════════════
        var seq = SolverParams.Instance.CombatSequencing;
        int windowSize = seq.SetupWindowSize;
        int historyCount = state.ActionHistory.Count;

        if (historyCount > 0)
        {
            // Count setup/power cards in the first N actions (early window)
            int earlySetupCount = 0;
            int earlyPowerCount = 0;
            int earlyWindow = Math.Min(windowSize, historyCount);
            for (int i = 0; i < earlyWindow; i++)
            {
                var act = state.ActionHistory[i];
                if (act.IsSetupCard) earlySetupCount++;
                if (act.IsPowerCard && !act.IsSetupCard) earlyPowerCount++;
            }
            score += earlySetupCount * seq.EarlySetupBonus;
            score += earlyPowerCount * seq.EarlyPowerBonus;

            // Penalize setup cards played late (last N actions)
            int lateSetupCount = 0;
            int lateWindow = Math.Min(windowSize, historyCount);
            for (int i = historyCount - lateWindow; i < historyCount; i++)
            {
                if (state.ActionHistory[i].IsSetupCard)
                    lateSetupCount++;
            }
            // Only penalize if they could have been played earlier
            // (i.e. there were earlier non-setup actions)
            if (lateSetupCount > 0 && historyCount > windowSize)
                score += lateSetupCount * seq.LateSetupPenalty;

            // First-action bonus: 0-cost energy card as very first action
            if (state.ActionHistory[0].IsZeroCostEnergy)
                score += seq.FirstActionEnergyBonus;
        }

        // ═══════════════════════════════════════════════════════════════════
        // NEW: Hard BEFORE/AFTER sequencing rules (Optimization 5)
        // ═══════════════════════════════════════════════════════════════════
        if (seq.HardSequencingRulesEnabled && historyCount >= 2)
        {
            foreach (var (cardA, categoryB) in CharacterConfig.BeforeRules)
            {
                // Find positions of cardA and any card matching categoryB
                int posA = -1, posB = -1;
                for (int i = 0; i < historyCount; i++)
                {
                    var act = state.ActionHistory[i];
                    if (posA < 0 && string.Equals(act.CardId, cardA, StringComparison.OrdinalIgnoreCase))
                        posA = i;
                    if (posB < 0 && MatchesCategory(act.CardId, categoryB))
                        posB = i;
                    if (posA >= 0 && posB >= 0) break;
                }

                if (posA >= 0 && posB >= 0)
                {
                    if (posA < posB)
                        score += seq.CardOrderingBoost;  // correct order: cardA BEFORE categoryB
                    else
                        score += seq.CardOrderingPenalty; // wrong order: cardA AFTER categoryB
                }
            }

            foreach (var (cardA, categoryB) in CharacterConfig.AfterRules)
            {
                int posA = -1, posB = -1;
                for (int i = 0; i < historyCount; i++)
                {
                    var act = state.ActionHistory[i];
                    if (posA < 0 && string.Equals(act.CardId, cardA, StringComparison.OrdinalIgnoreCase))
                        posA = i;
                    if (posB < 0 && MatchesCategory(act.CardId, categoryB))
                        posB = i;
                    if (posA >= 0 && posB >= 0) break;
                }

                if (posA >= 0 && posB >= 0)
                {
                    if (posB < posA)
                        score += seq.CardOrderingBoost;  // correct: categoryB BEFORE cardA
                    else
                        score += seq.CardOrderingPenalty; // wrong: cardA BEFORE categoryB
                }
            }
        }

        // ═══════════════════════════════════════════════════════════════════
        // NEW: Enhanced energy efficiency (Optimization 6)
        // ═══════════════════════════════════════════════════════════════════
        int netEnergy = state.TotalEnergyGained - state.TotalEnergySpent;
        if (netEnergy > 0)
            score += netEnergy * p.NetEnergyPositiveBonus;

        // Bonus for playing 0-cost energy cards
        if (state.ZeroCostEnergyCardsPlayed > 0)
            score += state.ZeroCostEnergyCardsPlayed * p.FreeEnergyCardBonus;

        // ═══════════════════════════════════════════════════════════════════
        // NEW: Future value estimation for active powers (Optimization 1)
        // ═══════════════════════════════════════════════════════════════════
        if (state.ActivePowers.Count > 0)
        {
            var fv = SolverParams.Instance.FutureValue;
            // Estimate remaining turns
            int totalEnemyHp = state.Enemies.Where(e => e.IsAlive).Sum(e => e.Hp);
            double dpr = state.TotalDamageDealt > 0 && state.CardsPlayed > 0
                ? (double)state.TotalDamageDealt / Math.Max(1, state.CardsPlayed) * 3.0 // ~3 cards/turn
                : 15.0; // default DPR
            if (dpr < 1) dpr = 15.0;
            int estRemainingTurns = Math.Max(1, Math.Min(fv.MaxTurns,
                (int)(totalEnemyHp / dpr) + 1));

            double futureValueBonus = 0;
            foreach (var powerId in state.ActivePowers)
            {
                double perTurnValue = CharacterConfig.PowerPerTurnValues
                    .GetValueOrDefault(powerId, fv.PowerPerTurnBaseValue);
                // Apply discount: value × Σ(discountRate^t) for t=1..remainingTurns
                double discountedSum = 0;
                double discount = 1.0;
                for (int t = 1; t <= estRemainingTurns; t++)
                {
                    discount *= fv.DiscountRate;
                    discountedSum += discount;
                }
                futureValueBonus += perTurnValue * discountedSum;
            }
            score += futureValueBonus;
        }

        // ═══════════════════════════════════════════════════════════════════
        // NEW: Deck quality scoring (Optimization 3)
        // ═══════════════════════════════════════════════════════════════════
        var dq = SolverParams.Instance.DeckQuality;

        // Reward exhausting basic cards
        if (state.ExhaustedBasicCount > 0)
            score += state.ExhaustedBasicCount * dq.DeckThinningValue;

        // Reward improved draw quality (non-basic ratio vs initial)
        int remainingDeck = state.DrawPile.Count + state.DiscardPile.Count;
        if (remainingDeck > 0)
        {
            int remainingNonBasic = state.DrawPile.Count(c =>
                !IsBasicCardByName(c.Id.Entry?.ToUpperInvariant() ?? ""))
                + state.DiscardPile.Count(c =>
                !IsBasicCardByName(c.Id.Entry?.ToUpperInvariant() ?? ""));
            double currentQuality = (double)remainingNonBasic / remainingDeck;
            double qualityImprovement = currentQuality - state.InitialDeckQuality;
            if (qualityImprovement > 0)
                score += qualityImprovement * dq.ImprovedDrawQualityBonus;

            // Thin deck bonus (encourages infinite loops)
            if (remainingDeck <= 10 && currentQuality > 0.6)
                score += (10 - remainingDeck) * dq.ThinDeckBonus;
        }

        // ── Multiplayer card play bonus ───────────────────────────────────
        // When a multiplayer card was played, add a large bonus to ensure
        // the solver strongly prefers playing MP cards over non-MP cards.
        // This applies to all 5 characters in coop mode.
        foreach (var action in state.ActionHistory)
        {
            if (MultiplayerCards.IsMultiplayerCard(action.CardId))
            {
                score += MultiplayerCards.PlayBonus;
                break; // bonus once per state, not per card
            }
        }

        // ═══════════════════════════════════════════════════════════════════
        // NEW: Two-dimensional ordering score (Phase 2 redesign)
        // ═══════════════════════════════════════════════════════════════════
        // Reward playing high-order cards before low-order cards.
        // This complements the BEFORE/AFTER hard rules above by adding
        // a continuous gradient: partially correct order gets partial reward.
        if (seq.TwoDimensionalOrderingEnabled && historyCount >= 2)
        {
            double orderingScore = 0;
            var actions = state.ActionHistory;

            for (int i = 0; i < actions.Count - 1; i++)
            {
                for (int j = i + 1; j < actions.Count; j++)
                {
                    int orderI = actions[i].OrderPriority;
                    int orderJ = actions[j].OrderPriority;
                    int orderDiff = orderI - orderJ;

                    // Position weight: earlier positions matter more for correct ordering
                    double positionWeight = 1.0 / (i + 1);

                    if (orderDiff >= 0)
                    {
                        // Card i (played earlier) has higher/equal order → correct
                        // Bonus proportional to the order gap
                        orderingScore += orderDiff * positionWeight
                            * seq.TwoDimOrderingScorePerPoint * 0.01;
                    }
                    else
                    {
                        // Card j should have been played before card i → wrong order
                        // Penalty proportional to the order gap
                        orderingScore -= (-orderDiff) * positionWeight
                            * seq.TwoDimOrderingPenaltyPerPoint * 0.01;
                    }
                }
            }

            // Normalize by number of pairs to prevent bias toward many-card turns
            int pairCount = historyCount * (historyCount - 1) / 2;
            if (pairCount > 0)
                orderingScore /= pairCount;

            score += orderingScore;
        }

        return score;
    }

    /// <summary>
    /// Check if a card matches a sequencing rule category.
    /// "*" matches everything, otherwise matches specific card types/effects.
    /// </summary>
    private static bool MatchesCategory(string cardId, string category)
    {
        if (category == "*") return true;

        string upper = cardId.ToUpperInvariant();
        return category.ToUpperInvariant() switch
        {
            "ATTACK" => !IsBasicCardByName(upper)
                && !DrawCardIds.ContainsKey(upper)
                && upper is not ("BASH" or "UPPERCUT" or "THUNDERCLAP" or "SHOCKWAVE"
                    or "TREMBLE" or "CLOTHESLINE" or "BEAM_CELL" or "GO_FOR_THE_EYES"),
            "SKILL" => true, // approximate — most non-attack, non-power cards
            "BLOCK" => !IsBasicCardByName(upper) && upper is not ("BASH" or "UPPERCUT"),
            "STRENGTH" => upper is ("INFLAME" or "SPOT_WEAKNESS" or "LIMIT_BREAK"
                or "FLEX" or "DEMON_FORM" or "RUPTURE"),
            "POISON" => upper is ("DEADLY_POISON" or "BOUNCING_FLASK" or "NOXIOUS_FUMES"
                or "CORROSIVE_WAVE" or "CATALYST" or "ENVENOM"),
            "ORB" => upper is ("DARKNESS" or "MULTI_CAST" or "DUALCAST"),
            "EXHAUST" => upper.Contains("EXHAUST") || upper is ("BURNING_PACT"
                or "SECOND_WIND" or "TRUE_GRIT" or "SEVER_SOUL" or "FIEND_FIRE"),
            _ => false,
        };
    }

    // ── State cloning ────────────────────────────────────────────────────

    private static SearchState CloneState(SearchState src)
    {
        return new SearchState
        {
            Energy = src.Energy,
            Block = src.Block,
            Hp = src.Hp,
            MaxHp = src.MaxHp,
            Strength = src.Strength,
            Dexterity = src.Dexterity,
            VulnerableOnPlayer = src.VulnerableOnPlayer,
            WeakOnPlayer = src.WeakOnPlayer,
            FrailOnPlayer = src.FrailOnPlayer,
            PlayerDOT = src.PlayerDOT,
            Focus = src.Focus,
            OrbSlots = src.OrbSlots,
            LightningOrbs = src.LightningOrbs,
            FrostOrbs = src.FrostOrbs,
            DarkOrbs = src.DarkOrbs,
            PlasmaOrbs = src.PlasmaOrbs,
            LoopCount = src.LoopCount,
            OrbQueue = new List<string>(src.OrbQueue),
            BaseDarkOrbDamage = src.BaseDarkOrbDamage,
            TotalDarkOrbDamage = src.TotalDarkOrbDamage,
            Stars = src.Stars,
            StarsSpent = src.StarsSpent,
            PanicButtonTurns = src.PanicButtonTurns,
            PoisonOnEnemies = src.PoisonOnEnemies,
            Enemies = src.Enemies.Select(e => new EnemyProxy
            {
                Creature = e.Creature,
                Index = e.Index,
                Vulnerable = e.Vulnerable,
                Weak = e.Weak,
                Strength = e.Strength,
                Poison = e.Poison,
                Hp = e.Hp,
                MaxHp = e.MaxHp,
                Block = e.Block,
                IntentDamage = e.IntentDamage,
                IsAlive = e.IsAlive,
                Intangible = e.Intangible,
                Buffer = e.Buffer,
                Thorns = e.Thorns,
            }).ToList(),
            Hand = new List<CardEntry>(src.Hand),
            DrawPile = new List<CardModel>(src.DrawPile),
            DiscardPile = new List<CardModel>(src.DiscardPile),
            CardsPlayed = src.CardsPlayed,
            CardSelectsPlayed = src.CardSelectsPlayed,
            PowersPlayed = src.PowersPlayed,
            StrengthGained = src.StrengthGained,
            DexterityGained = src.DexterityGained,
            TotalDamageDealt = src.TotalDamageDealt,
            TotalBlockGained = src.TotalBlockGained,
            // Sequencing tracking
            ActionHistory = src.ActionHistory.Select(a => new ActionRecord
            {
                CardId = a.CardId,
                Priority = a.Priority,
                IsZeroCostEnergy = a.IsZeroCostEnergy,
                EnergyGain = a.EnergyGain,
                IsSetupCard = a.IsSetupCard,
                IsPowerCard = a.IsPowerCard,
                IsBasic = a.IsBasic,
            }).ToList(),
            TotalEnergyGained = src.TotalEnergyGained,
            TotalEnergySpent = src.TotalEnergySpent,
            ZeroCostEnergyCardsPlayed = src.ZeroCostEnergyCardsPlayed,
            ExhaustedBasicCount = src.ExhaustedBasicCount,
            InitialDeckQuality = src.InitialDeckQuality,
            ActivePowers = new List<string>(src.ActivePowers),
            Potions = src.Potions.Select(p => new PotionEffect
            {
                Id = p.Id,
                Damage = p.Damage,
                IsAoe = p.IsAoe,
                VulnerableStacks = p.VulnerableStacks,
                WeakStacks = p.WeakStacks,
                StrengthGain = p.StrengthGain,
                DexterityGain = p.DexterityGain,
                BlockGain = p.BlockGain,
                EnergyGain = p.EnergyGain,
                HealAmount = p.HealAmount,
                PoisonStacks = p.PoisonStacks,
                Used = p.Used,
            }).ToList(),
        };
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Check if a card ID is a basic starter card (for deck quality).
    /// Delegates to RunState.IsAnyBasicCardId — the single source of truth
    /// for basic card detection across all characters.
    /// </summary>
    private static bool IsBasicCardByName(string cardId)
    {
        return RunState.IsAnyBasicCardId(cardId);
    }

    private static int GetCardCost(CardModel card)
    {
        if (card.EnergyCost.CostsX) return 0;
        return Math.Max(0, card.EnergyCost.Canonical);
    }

    private static bool CanPlayCard(CardModel card, int energy)
    {
        if (!card.CanPlay(out _, out _)) return false;
        int cost = GetCardCost(card);
        return cost <= energy || card.EnergyCost.CostsX;
    }

    private static int GetVulnerableStacks(Creature enemy)
    {
        try
        {
            return enemy.Powers
                .FirstOrDefault(p => p.GetType().Name.Contains("Vulnerable", StringComparison.OrdinalIgnoreCase))
                ?.Amount ?? 0;
        }
        catch { return 0; }
    }

    private static int GetWeakStacks(Creature enemy)
    {
        try
        {
            return enemy.Powers
                .FirstOrDefault(p => p.GetType().Name.Contains("Weak", StringComparison.OrdinalIgnoreCase))
                ?.Amount ?? 0;
        }
        catch { return 0; }
    }

    private static int GetStrengthFromPowers(Creature enemy)
    {
        try
        {
            return enemy.Powers
                .FirstOrDefault(p => p.GetType().Name.Contains("Strength", StringComparison.OrdinalIgnoreCase))
                ?.Amount ?? 0;
        }
        catch { return 0; }
    }

    private static int GetPoisonStacks(Creature enemy)
    {
        try
        {
            return enemy.Powers
                .FirstOrDefault(p => p.GetType().Name.Contains("Poison", StringComparison.OrdinalIgnoreCase))
                ?.Amount ?? 0;
        }
        catch { return 0; }
    }

    /// <summary>Get Intangible stacks on an enemy. Intangible caps damage to 1 per hit.</summary>
    private static int GetIntangibleStacks(Creature enemy)
    {
        try
        {
            return enemy.Powers
                .FirstOrDefault(p => p.GetType().Name.Contains("Intangible", StringComparison.OrdinalIgnoreCase))
                ?.Amount ?? 0;
        }
        catch { return 0; }
    }

    /// <summary>Get Buffer stacks on an enemy. Buffer negates one instance of HP loss.</summary>
    private static int GetBufferStacks(Creature enemy)
    {
        try
        {
            return enemy.Powers
                .FirstOrDefault(p => p.GetType().Name.Contains("Buffer", StringComparison.OrdinalIgnoreCase))
                ?.Amount ?? 0;
        }
        catch { return 0; }
    }

    /// <summary>Get Thorns stacks on an enemy. Thorns reflects damage back to attacker.</summary>
    private static int GetThornsStacks(Creature enemy)
    {
        try
        {
            return enemy.Powers
                .FirstOrDefault(p => p.GetType().Name.Contains("Thorns", StringComparison.OrdinalIgnoreCase)
                    || p.GetType().Name.Contains("Sharp", StringComparison.OrdinalIgnoreCase))
                ?.Amount ?? 0;
        }
        catch { return 0; }
    }

    private static int EstimateIntentDamage(Creature enemy)
    {
        try
        {
            var move = enemy.Monster?.NextMove;
            if (move?.Intents == null) return 0;
            int total = 0;
            foreach (var intent in move.Intents)
            {
                if (intent is MegaCrit.Sts2.Core.MonsterMoves.Intents.AttackIntent attack)
                {
                    var targets = enemy.CombatState?.PlayerCreatures?.ToList() ?? new List<Creature>();
                    total += attack.GetTotalDamage(targets, enemy);
                }
            }
            return total;
        }
        catch { return 0; }
    }

    /// <summary>Public static wrapper for BattleLogger use.</summary>
    public static int EstimateIntentDamageStatic(Creature enemy)
    {
        return EstimateIntentDamage(enemy);
    }

    /// <summary>Get Vulnerable stacks on an enemy.</summary>
    public static int GetVulnerableStacksStatic(Creature enemy)
    {
        try
        {
            return enemy.Powers
                .FirstOrDefault(p => p.GetType().Name.Contains("Vulnerable", StringComparison.OrdinalIgnoreCase))
                ?.Amount ?? 0;
        }
        catch { return 0; }
    }

    /// <summary>Get Weak stacks on an enemy.</summary>
    public static int GetWeakStacksStatic(Creature enemy)
    {
        try
        {
            return enemy.Powers
                .FirstOrDefault(p => p.GetType().Name.Contains("Weak", StringComparison.OrdinalIgnoreCase))
                ?.Amount ?? 0;
        }
        catch { return 0; }
    }

    /// <summary>Get Strength stacks on an enemy.</summary>
    public static int GetStrengthStacksStatic(Creature enemy)
    {
        try
        {
            return enemy.Powers
                .FirstOrDefault(p => p.GetType().Name.Contains("Strength", StringComparison.OrdinalIgnoreCase))
                ?.Amount ?? 0;
        }
        catch { return 0; }
    }

    public static int GetDexterityStacksStatic(Creature enemy)
    {
        try
        {
            return enemy.Powers
                .FirstOrDefault(p => p.GetType().Name.Contains("Dexterity", StringComparison.OrdinalIgnoreCase))
                ?.Amount ?? 0;
        }
        catch { return 0; }
    }
}
