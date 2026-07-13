using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TokenSpire2.Solver;

/// <summary>
/// Root parameter container loaded from params.json at runtime.
/// Modified by the optimizer without recompilation.
/// Thread-safe via lazy reload: call SolverParams.Load() to refresh.
/// </summary>
public class SolverParams
{
    private static SolverParams? _instance;
    private static readonly object _lock = new();
    private static string? _lastPath;

    public static SolverParams Instance
    {
        get
        {
            if (_instance == null)
                Load();
            return _instance!;
        }
    }

    /// <summary>Load or reload params from the given path (or auto-detect).</summary>
    public static void Load(string? path = null)
    {
        lock (_lock)
        {
            path ??= _lastPath ?? FindParamsFile();
            if (path == null)
            {
                _instance = new SolverParams(); // default values
                return;
            }

            try
            {
                var json = File.ReadAllText(path);
                _instance = JsonSerializer.Deserialize<SolverParams>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                _lastPath = path;
                if (_instance != null)
                    MainFile.Logger.Info($"[SolverParams] Loaded {path} (v{_instance.Meta?.Version ?? 0})");
            }
            catch (Exception ex)
            {
                MainFile.Logger.Error($"[SolverParams] Failed to load {path}: {ex.Message}");
                _instance = new SolverParams();
            }
        }
    }

    /// <summary>Reload from last known path (call after optimizer updates params.json).</summary>
    public static void Reload()
    {
        lock (_lock)
        {
            if (_lastPath != null && File.Exists(_lastPath))
            {
                try
                {
                    var json = File.ReadAllText(_lastPath);
                    _instance = JsonSerializer.Deserialize<SolverParams>(json,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
                catch { /* keep old instance on failure */ }
            }
        }
    }

    private static string? FindParamsFile()
    {
        try
        {
            var asmDir = Path.GetDirectoryName(
                System.Reflection.Assembly.GetExecutingAssembly().Location);
            if (asmDir == null) return null;
            var path = Path.Combine(asmDir, "params.json");
            return File.Exists(path) ? path : null;
        }
        catch { return null; }
    }

    // ═══════════════════════════════════════════════════════════════
    // Meta
    // ═══════════════════════════════════════════════════════════════

    [JsonPropertyName("_meta")]
    public MetaSection? Meta { get; set; }

    public class MetaSection
    {
        public int Version { get; set; } = 1;
        public string Description { get; set; } = "";
    }

    // ═══════════════════════════════════════════════════════════════
    // Combat Solver
    // ═══════════════════════════════════════════════════════════════

    [JsonPropertyName("combat_solver")]
    public CombatSolverSection CombatSolver { get; set; } = new();

    public class CombatSolverSection
    {
        public int MaxSearchStates { get; set; } = 5000;
        public int MaxCardsPerTurn { get; set; } = 15;
        public CombatScoringSection Scoring { get; set; } = new();
        public CombatSafetySection Safety { get; set; } = new();
    }

    public class CombatScoringSection
    {
        public double KillBase { get; set; } = 1000;
        public double KillPerMaxHp { get; set; } = 5;
        public double KillWeight { get; set; } = 1.0;
        public double DamagePerPoint { get; set; } = 10;
        public double DamageWeight { get; set; } = 1.0;
        public double BlockPerNeededPoint { get; set; } = 7;
        public double BlockPerExcessPoint { get; set; } = 1;
        public double BlockOverIncoming { get; set; } = 1;
        public double BlockWeight { get; set; } = 1.0;
        public double ExistingBlockPerPoint { get; set; } = 2;
        public double HealthPenaltyLowHpThreshold { get; set; } = 0.5;
        public double HealthPenaltyLowHpMultiplier { get; set; } = 80;
        public double HealthPenaltyNormalMultiplier { get; set; } = 40;
        public double HealthPenaltyWeight { get; set; } = 1.0;
        public double LowHpRatioPenaltyMultiplier { get; set; } = 50;
        public double VulnerablePerStack { get; set; } = 25;
        public double VulnerableWeight { get; set; } = 1.0;
        public double WeakPerStack { get; set; } = 15;
        public double WeakWeight { get; set; } = 1.0;
        public double StrengthPerPoint { get; set; } = 20;
        public double StrengthWeight { get; set; } = 1.0;
        public double DexterityPerPoint { get; set; } = 18;
        public double DexterityWeight { get; set; } = 1.0;
        public double PowerPerPlayed { get; set; } = 50;
        public double PowerWeight { get; set; } = 1.0;
        public double CardSelectPerPlayed { get; set; } = 35;
        public double EnergyPerPoint { get; set; } = 5;
        public double NetEnergyPositiveBonus { get; set; } = 8;
        public double FreeEnergyCardBonus { get; set; } = 35;
        public double PoisonPerStack { get; set; } = 15;
        public double PoisonWeight { get; set; } = 1.0;
        public double OrbLightningMult { get; set; } = 12;
        public double OrbFrostMult { get; set; } = 10;
        public double OrbDarkMult { get; set; } = 8;
        public double OrbFocusPerPoint { get; set; } = 40;
        public double OrbValueWeight { get; set; } = 1.0;
        public double StarPerStack { get; set; } = 10;
        public double StarWeight { get; set; } = 1.0;

        // ── Focus-fire strategy ──
        // Rewards concentrating damage on one enemy instead of spreading it.
        // Bonus = damageOnFocusTarget × hpPctGap × FocusFireMultiplier
        public double FocusFireMultiplier { get; set; } = 15.0;
        // Extra bonus when the focus target is the boss (highest MaxHp enemy)
        public double BossPriorityMultiplier { get; set; } = 8.0;
        // Penalty per point of damage dealt to minions while boss HP > 50%
        public double MinionDamagePenalty { get; set; } = 3.0;
        // Block weight multiplier vs elites (0.75 = 25% less block value)
        public double EliteBlockMultiplier { get; set; } = 0.75;
        // Block weight multiplier vs bosses (0.6 = 40% less block value)
        public double BossBlockMultiplier { get; set; } = 0.6;
    }

    public class CombatSafetySection
    {
        public int MinHpAfterHpCost { get; set; } = 5;
    }

    // ═══════════════════════════════════════════════════════════════
    // Combat Sequencing (Optimization 2 & 5)
    // ═══════════════════════════════════════════════════════════════

    [JsonPropertyName("combat_sequencing")]
    public CombatSequencingSection CombatSequencing { get; set; } = new();

    public class CombatSequencingSection
    {
        /// <summary>Bonus per setup card (priority <= SETUP) played in the first N cards.</summary>
        public double EarlySetupBonus { get; set; } = 30.0;
        /// <summary>Bonus per S-tier power (priority <= POWER_S) played in the first N cards.</summary>
        public double EarlyPowerBonus { get; set; } = 20.0;
        /// <summary>Penalty per setup card played in the LAST N cards.</summary>
        public double LateSetupPenalty { get; set; } = -25.0;
        /// <summary>"Early" window = first N cards in the action sequence.</summary>
        public int SetupWindowSize { get; set; } = 3;
        /// <summary>Extra bonus when a 0-cost energy-gain card is the FIRST action.</summary>
        public double FirstActionEnergyBonus { get; set; } = 50.0;
        /// <summary>Whether hard BEFORE/AFTER sequencing rules are enforced.</summary>
        public bool HardSequencingRulesEnabled { get; set; } = true;
        /// <summary>Bonus when card is played in the correct BEFORE position.</summary>
        public double CardOrderingBoost { get; set; } = 15.0;
        /// <summary>Penalty when a BEFORE rule is violated.</summary>
        public double CardOrderingPenalty { get; set; } = -20.0;

        // ── Two-dimensional ordering (Phase 2 redesign) ──

        /// <summary>Enable the two-dimensional ordering score in EvaluateState.</summary>
        public bool TwoDimensionalOrderingEnabled { get; set; } = true;

        /// <summary>Score per point of correct order difference (higher = earlier card played first).</summary>
        public double TwoDimOrderingScorePerPoint { get; set; } = 2.0;

        /// <summary>Penalty per point of incorrect order difference (lower-order card played before higher).</summary>
        public double TwoDimOrderingPenaltyPerPoint { get; set; } = 3.0;
    }

    // ═══════════════════════════════════════════════════════════════
    // Future Value Estimation (Optimization 1)
    // ═══════════════════════════════════════════════════════════════

    [JsonPropertyName("future_value")]
    public FutureValueSection FutureValue { get; set; } = new();

    public class FutureValueSection
    {
        /// <summary>Max turns into the future to estimate.</summary>
        public int MaxTurns { get; set; } = 8;
        /// <summary>Discount rate: each turn, value *= discount. 0.5 = 50% decay per turn.</summary>
        public double DiscountRate { get; set; } = 0.5;
        /// <summary>Default per-turn value for powers not in the explicit map.</summary>
        public double PowerPerTurnBaseValue { get; set; } = 10.0;
    }

    // ═══════════════════════════════════════════════════════════════
    // Deck Quality (Optimization 3)
    // ═══════════════════════════════════════════════════════════════

    [JsonPropertyName("deck_quality")]
    public DeckQualitySection DeckQuality { get; set; } = new();

    public class DeckQualitySection
    {
        /// <summary>Score gained per basic card exhausted during combat.</summary>
        public double DeckThinningValue { get; set; } = 25.0;
        /// <summary>Bonus when deck quality (non-basic ratio) improves from baseline.</summary>
        public double ImprovedDrawQualityBonus { get; set; } = 100.0;
        /// <summary>Bonus per card under 10 remaining in draw+discard (encourages infinite).</summary>
        public double ThinDeckBonus { get; set; } = 15.0;
        /// <summary>Multiplier for draw quality ratio in scoring.</summary>
        public double DrawQualityRatioWeight { get; set; } = 12.0;
    }

    // ═══════════════════════════════════════════════════════════════
    // Beam Search (Optimization 7)
    // ═══════════════════════════════════════════════════════════════

    [JsonPropertyName("beam_search")]
    public BeamSearchSection BeamSearch { get; set; } = new();

    public class BeamSearchSection
    {
        /// <summary>Whether beam-search prefix is enabled (P3, after P0-P2 stabilize).</summary>
        public bool Enabled { get; set; } = false;
        /// <summary>Number of states to keep per beam layer.</summary>
        public int BeamWidth { get; set; } = 50;
        /// <summary>Number of initial layers to beam-search before switching to DFS.</summary>
        public int BeamDepth { get; set; } = 3;
        /// <summary>Multiplier on setup-card score during beam phase (encourages early setup).</summary>
        public double EarlySetupBeamBoost { get; set; } = 1.5;
    }

    // ═══════════════════════════════════════════════════════════════
    // Card Reward
    // ═══════════════════════════════════════════════════════════════

    [JsonPropertyName("card_reward")]
    public CardRewardSection CardReward { get; set; } = new();

    public class CardRewardSection
    {
        public CardRewardStageWeights StageWeights { get; set; } = new();
        public CardRewardSkipThreshold SkipThreshold { get; set; } = new();
        public CardRewardRedundancyPenalty RedundancyPenalty { get; set; } = new();
        public CardRewardCurveBalance CurveBalance { get; set; } = new();
        public CardRewardTypeBalance TypeBalance { get; set; } = new();
        public DeckGapSection DeckGap { get; set; } = new();
        public FutureInvestmentSection FutureInvestment { get; set; } = new();
        public CardRoleSection CardRole { get; set; } = new();
        public EngineClosureSection EngineClosure { get; set; } = new();
        public UpgradePressureSection UpgradePressure { get; set; } = new();
        public Dictionary<string, double> ColorlessPremium { get; set; } = new();
        public BossCounterSection BossCounter { get; set; } = new();
    }

    public class CardRewardStageWeights
    {
        public double RawEfficiencyDamagePerEnergy { get; set; } = 6;
        public double RawEfficiencyBlockPerEnergy { get; set; } = 5;
        public double ZeroCostDamagePerPoint { get; set; } = 6;
        public double ZeroCostBlockPerPoint { get; set; } = 5;
        public double CardTypePower { get; set; } = 20;
        public double CardTypeZeroCost { get; set; } = 22;
        public double CardTypeOneCost { get; set; } = 5;
        public double XCostFlexibility { get; set; } = 15;
        public double DebuffVulnerable { get; set; } = 12;
        public double DebuffWeak { get; set; } = 10;
        public double DebuffPoison { get; set; } = 10;
        public double BuffEnergy { get; set; } = 25;
        public double BuffStrength { get; set; } = 18;
        public double BuffDexterity { get; set; } = 16;
        public double AoeBonus { get; set; } = 14;
        public double DrawDetection { get; set; } = 18;
        public double HpCostWithSynergyPerPoint { get; set; } = 5;
        public double HpCostWithoutSynergyPerPoint { get; set; } = -4;
        public double Act1DamagePerEnergyExtra { get; set; } = 4;
        public double Act1PremiumAttackBonus { get; set; } = 15;
        public double Act1TwoCost12DmgBonus { get; set; } = 10;
        public double Act1SlowPowerPenalty { get; set; } = -5;
        // ── Phase 7 mod: small deck bonus ──
        public double SmallDeckBonusPerCard { get; set; } = 3.0;     // bonus per card under 12
        public double ModerateDeckBonusPerCard { get; set; } = 1.5;  // bonus per card under 15
        // ── Phase 13 mod: conditional stat weight ──
        public double StatsWeightEarlyLargeDeck { get; set; } = 0.4; // Act1 large deck stat discount
        public double StatsWeightMid { get; set; } = 0.7;            // medium stat weight
        public double Act1PureBlockPenalty { get; set; } = -5;
        public double Act2AoeBonus { get; set; } = 22;
        public double Act2BlockBonus { get; set; } = 6;
        public double Act2WeakBonus { get; set; } = 8;
        public double Act2BadAttackPenalty { get; set; } = -5;
        public double Act3PowerBonus { get; set; } = 12;
        public double Act3StrengthExtra { get; set; } = 10;
        public double Act3BadAttackPenalty { get; set; } = -12;
        public double Act3VanillaAttackPenalty { get; set; } = -6;
        public double UpgradeBonus { get; set; } = 20;
        public double DeckSizeOver20PerCard { get; set; } = -2;
        public double DeckSizeOver25PerCard { get; set; } = -4;
        public double DeckSizeOver30PerCard { get; set; } = -8;
        public double StrengthSynergyMultiHit { get; set; } = 15;
        public double ExhaustSynergyBonus { get; set; } = 18;
        public double BlockSynergyBonus { get; set; } = 12;
        public double SelfDamageSynergyBonus { get; set; } = 18;
        public double EnergyRelicHighCostBonus { get; set; } = 8;
        public double PoisonSynergyBonus { get; set; } = 18;
        public double DiscardSynergyBonus { get; set; } = 18;
        public double OrbSynergyBonus { get; set; } = 18;
        public double FocusSynergyBonus { get; set; } = 22;
        public double StarSynergyBonus { get; set; } = 18;
        public double ComboSynergyMultiplier { get; set; } = 12;
        public double StatsWrImpactMultiplier { get; set; } = 500;
        public double StatsWrAbove30Multiplier { get; set; } = 100;
    }

    public class CardRewardSkipThreshold
    {
        public double Base { get; set; } = 25;
        public double PerDeckSizeAbove12 { get; set; } = 2.5;
        public double PerDeckSizeAbove20 { get; set; } = 5.0;
        public double PerAct { get; set; } = 5;
        public double RelativeThresholdMultiplier { get; set; } = 0.85;
        public double EmergencyLowHpReduction { get; set; } = 12;
        public double EmergencyAct1NoAttackReduction { get; set; } = 10;
        public double EmergencyNoBlockReduction { get; set; } = 5;
        public double HasStrengthSynergyBonus { get; set; } = 5;
        public double HasExhaustSynergyBonus { get; set; } = 5;
        public double HasBlockSynergyBonus { get; set; } = 5;
        public double MinThreshold { get; set; } = 10;
    }

    public class CardRewardRedundancyPenalty
    {
        public double OneCopy { get; set; } = -18;
        public double TwoOrMoreCopies { get; set; } = -28;
    }

    public class CardRewardCurveBalance
    {
        public double HighCostRatioHigh { get; set; } = 0.35;
        public double HighCostRatioMid { get; set; } = 0.25;
        public double LowCostUrgentBonus { get; set; } = 10;
        public double LowCostModerateBonus { get; set; } = 4;
        public double HighCostPenalty { get; set; } = -8;
        public double ExpensiveCardSmallDeckPenalty { get; set; } = -8;
        public double ZeroCostLargeDeckBonus { get; set; } = 6;
    }

    public class CardRewardTypeBalance
    {
        public double SkillAttackRatioThreshold { get; set; } = 2.0;
        public double TooManySkillsPenalty { get; set; } = -22;
        public double MissingAttackBonus { get; set; } = 18;
        public int MissingAttackThreshold { get; set; } = 4;
        public double MissingBlockBonus { get; set; } = 10;
        public int MissingBlockThreshold { get; set; } = 3;
        public int MissingBlockMinSkill { get; set; } = 3;
    }

    // ═══════════════════════════════════════════════════════════════
    // Phase 15: Deck Gap Diagnosis (端口化缺口诊断)
    // ═══════════════════════════════════════════════════════════════

    public class DeckGapSection
    {
        /// <summary>Bonus for picking a damage card when damage gap is detected.</summary>
        public double DamageBonus { get; set; } = 25.0;
        /// <summary>Bonus for picking a block card when block gap is detected.</summary>
        public double BlockBonus { get; set; } = 25.0;
        /// <summary>Bonus for picking a draw card when draw gap is detected.</summary>
        public double DrawBonus { get; set; } = 30.0;
        /// <summary>Bonus for picking an energy card when energy gap is detected.</summary>
        public double EnergyBonus { get; set; } = 30.0;
        /// <summary>Bonus for picking an AOE card when AOE gap is detected.</summary>
        public double AoeBonus { get; set; } = 28.0;
        /// <summary>Bonus for picking a scaling card when scaling gap is detected.</summary>
        public double ScalingBonus { get; set; } = 22.0;
        /// <summary>Draw card density below this value triggers a draw gap.</summary>
        public double DrawDensityThreshold { get; set; } = 0.08;
        /// <summary>Energy card density below this value triggers an energy gap.</summary>
        public double EnergyDensityThreshold { get; set; } = 0.05;
        /// <summary>Average damage per card below this triggers a damage gap (high urgency).</summary>
        public double AvgDamageThresholdLow { get; set; } = 8.0;
        /// <summary>Average damage per card below this triggers a damage gap (medium urgency).</summary>
        public double AvgDamageThresholdMid { get; set; } = 10.0;
        /// <summary>Average block per card below this triggers a block gap.</summary>
        public double AvgBlockThresholdLow { get; set; } = 6.0;
        /// <summary>Average block per card below this triggers a block gap (medium urgency).</summary>
        public double AvgBlockThresholdMid { get; set; } = 8.0;
        /// <summary>Gap urgency multiplier for high-severity gaps.</summary>
        public double GapUrgencyHigh { get; set; } = 0.9;
        /// <summary>Gap urgency multiplier for medium-severity gaps.</summary>
        public double GapUrgencyMid { get; set; } = 0.5;
        /// <summary>Gap urgency multiplier for low-severity gaps.</summary>
        public double GapUrgencyLow { get; set; } = 0.3;
        /// <summary>AOE gap urgency in Act 2+.</summary>
        public double AoeGapUrgencyAct2 { get; set; } = 0.9;
        /// <summary>Scaling gap urgency in Act 3+.</summary>
        public double ScalingGapUrgencyAct3 { get; set; } = 0.85;
    }

    // ═══════════════════════════════════════════════════════════════
    // Phase 16: Future Investment Feasibility (站未来可行性)
    // ═══════════════════════════════════════════════════════════════

    public class FutureInvestmentSection
    {
        /// <summary>Bonus when the card has 2+ strong synergies with existing deck.</summary>
        public double StrongSynergyBonus { get; set; } = 18.0;
        /// <summary>Bonus when the card has at least 1 weak synergy.</summary>
        public double WeakSynergyBonus { get; set; } = 5.0;
        /// <summary>Penalty when synergy is barely detectable.</summary>
        public double BarelySynergyPenalty { get; set; } = -8.0;
        /// <summary>Heavy penalty when there is zero synergy — pure "wound".</summary>
        public double NoSynergyPenalty { get; set; } = -30.0;
        /// <summary>Synergy score threshold for "strong" classification.</summary>
        public double MinSynergyThresholdStrong { get; set; } = 0.5;
        /// <summary>Synergy score threshold for "weak" classification.</summary>
        public double MinSynergyThresholdWeak { get; set; } = 0.2;
        /// <summary>HP ratio below which future investment is penalized (too risky).</summary>
        public double HpRatioSafeThreshold { get; set; } = 0.6;
    }

    // ═══════════════════════════════════════════════════════════════
    // Phase 17: Transition vs Win Condition (过渡牌vs终端牌)
    // ═══════════════════════════════════════════════════════════════

    public class CardRoleSection
    {
        /// <summary>Bonus for transition cards in Act 1.</summary>
        public double TransitionAct1Bonus { get; set; } = 15.0;
        /// <summary>Penalty for pure transition cards in Act 2+.</summary>
        public double TransitionLatePenalty { get; set; } = -10.0;
        /// <summary>Bonus for a win-condition card in Act 1 when we have a solid base.</summary>
        public double WinConditionEarlyWithBase { get; set; } = 8.0;
        /// <summary>Bonus for win-condition cards in Act 2+.</summary>
        public double WinConditionAct2Plus { get; set; } = 12.0;
        /// <summary>Bonus for hybrid (good all-game) cards.</summary>
        public double HybridAlwaysGood { get; set; } = 8.0;
        /// <summary>Minimum transition cards needed to consider "base is solid".</summary>
        public int MinTransitionForBase { get; set; } = 2;
    }

    // ═══════════════════════════════════════════════════════════════
    // Phase 18: Engine Closure Detection (运转闭合检测)
    // ═══════════════════════════════════════════════════════════════

    public class EngineClosureSection
    {
        /// <summary>Bonus for draw card when engine is already closed.</summary>
        public double ClosedDrawSynergy { get; set; } = 20.0;
        /// <summary>Bonus for energy card when engine is already closed.</summary>
        public double ClosedEnergySynergy { get; set; } = 20.0;
        /// <summary>Bonus for energy cards when draw exists but energy is missing.</summary>
        public double MissingEnergy { get; set; } = 28.0;
        /// <summary>Bonus for draw cards when energy exists but draw is missing.</summary>
        public double MissingDraw { get; set; } = 28.0;
        /// <summary>Bonus when both draw and energy are missing (starting from scratch).</summary>
        public double MissingBoth { get; set; } = 22.0;
    }

    // ═══════════════════════════════════════════════════════════════
    // Phase 19: Upgrade Pressure (敲位压力评估)
    // ═══════════════════════════════════════════════════════════════

    public class UpgradePressureSection
    {
        /// <summary>Penalty when no upgrade slots remain for a card that needs upgrade.</summary>
        public double NoUpgradeSlot { get; set; } = -25.0;
        /// <summary>Penalty when only 1 upgrade slot remains.</summary>
        public double TightUpgradeSlot { get; set; } = -10.0;
        /// <summary>Bonus when upgrade slots are plentiful (3+).</summary>
        public double PlentyUpgradeSlot { get; set; } = 5.0;
        /// <summary>Bonus for transformative-upgrade card when slots available.</summary>
        public double TransformativeUpgradeBonus { get; set; } = 10.0;
        /// <summary>Penalty for transformative-upgrade card when no slots.</summary>
        public double TransformativeButNoSlot { get; set; } = -15.0;
        /// <summary>Number of cards needing upgrade above which slot pressure is "tight".</summary>
        public int CardsNeedingUpgradeThreshold { get; set; } = 3;
    }

    // ═══════════════════════════════════════════════════════════════
    // Phase 22: Boss Counter (Act感知Boss对策)
    // ═══════════════════════════════════════════════════════════════

    public class BossCounterSection
    {
        /// <summary>Bonus for multi-hit cards vs Vantom (breaks Slippery Shield).</summary>
        public double MultiHit { get; set; } = 15.0;
        /// <summary>Bonus for AOE cards vs multi-target bosses.</summary>
        public double Aoe { get; set; } = 18.0;
        /// <summary>Extra AOE bonus vs The Kin (3 targets).</summary>
        public double AoeKin { get; set; } = 22.0;
        /// <summary>Bonus for poison/orb cards vs Lagavulin (bypasses strength reduction).</summary>
        public double PoisonOrb { get; set; } = 15.0;
        /// <summary>Bonus for fast damage cards vs Insatiable (sand-pit countdown).</summary>
        public double FastDamage { get; set; } = 18.0;
        /// <summary>Bonus for exhaust-capable cards vs Soul Fysh/Aeonglass.</summary>
        public double Exhaust { get; set; } = 15.0;
        /// <summary>Bonus for high-cost big-impact cards vs Knowledge Demon.</summary>
        public double BigCards { get; set; } = 10.0;
        /// <summary>Bonus for scaling cards vs Test Subject (600 HP endurance fight).</summary>
        public double Scaling { get; set; } = 20.0;
        /// <summary>Penalty for poison cards vs Test Subject (resets on phase change).</summary>
        public double TrapPoison { get; set; } = -25.0;
        /// <summary>Bonus for 0-1 cost high-damage cards vs Insatiable.</summary>
        public double FastDamageMinDamage { get; set; } = 10.0;
        /// <summary>Bonus for high-cost high-impact cards vs Aeonglass.</summary>
        public double BigCardsAeonglass { get; set; } = 10.0;
        /// <summary>Min damage for FastDamage boss counter to trigger.</summary>
        public double FastDamageThreshold { get; set; } = 10.0;
    }

    // ═══════════════════════════════════════════════════════════════
    // Map
    // ═══════════════════════════════════════════════════════════════

    [JsonPropertyName("map")]
    public MapSection Map { get; set; } = new();

    public class MapSection
    {
        public MapNodeScores NodeScores { get; set; } = new();
        public MapPathBonuses PathBonuses { get; set; } = new();
    }

    public class MapNodeScores
    {
        public double CampfireBase { get; set; } = 8.0;
        public double CampfireLowHp { get; set; } = 6.0;
        public double CampfireHighHpUpgrade { get; set; } = 4.0;
        public double CampfireBeforeBossLowHp { get; set; } = 3.0;
        public double ShopBase { get; set; } = 2.0;
        public double ShopGold200 { get; set; } = 5.0;
        public double ShopGold100 { get; set; } = 3.0;
        public double ShopGoldLowPenalty { get; set; } = -1.0;
        public double ShopAct1Bonus { get; set; } = 1.5;
        public double ShopAct3Penalty { get; set; } = -1.0;
        public double EliteBase { get; set; } = -100.0;
        public double EliteStrengthModifier { get; set; } = 10.0;
        public double ElitePowerModifier { get; set; } = 10.0;
        public double EliteFrontloadModifier { get; set; } = 15.0;
        public double EliteHighHpModifier { get; set; } = 10.0;
        public double UnknownBase { get; set; } = 3.0;
        public double UnknownAct1Bonus { get; set; } = 2.0;
        public double UnknownLowHpPenalty { get; set; } = -2.0;
        public double TreasureBase { get; set; } = 2.0;
        public double BossBase { get; set; } = 0.0;
        public double MonsterBase { get; set; } = 1.0;
        public double MonsterAct1EarlyBonus { get; set; } = 2.0;
        public double MonsterAct3LargeDeckPenalty { get; set; } = -1.0;
    }

    public class MapPathBonuses
    {
        public double OneCampfire { get; set; } = 3.0;
        public double TwoPlusCampfire { get; set; } = 2.0;
        public double ThreePlusEliteLowHp { get; set; } = -8.0;
        public double ThreePlusEliteNoStrength { get; set; } = -4.0;
        public double ShopWithGold { get; set; } = 3.0;
        public double ShopGold200Extra { get; set; } = 2.0;
        public double PathLengthPerNode { get; set; } = 0.1;
    }

    // ═══════════════════════════════════════════════════════════════
    // Rest
    // ═══════════════════════════════════════════════════════════════

    [JsonPropertyName("rest")]
    public RestSection Rest { get; set; } = new();

    public class RestSection
    {
        public double RestLowHpThreshold { get; set; } = 0.65;
        public double RestLowHpScore { get; set; } = 320;
        public double RestMediumHpMax { get; set; } = 0.80;
        public double RestMediumHpScore { get; set; } = 200;
        public double RestMediumNoSustainBonus { get; set; } = 40;
        public double RestHighHpScore { get; set; } = 5;
        public double RestAct3BossBonus { get; set; } = 50;
        public double SmithHpThreshold { get; set; } = 0.65;
        public double SmithHighHpScore { get; set; } = 220;
        public double SmithAct1Bonus { get; set; } = 40;
        public double SmithAct3LowHpPenalty { get; set; } = -80;
        public double SmithLowHpScore { get; set; } = 10;
        public double TokeBaseScore { get; set; } = 80;
        public double TokeLargeDeckBonus { get; set; } = 100;
        public double TokeHighHpBonus { get; set; } = 30;
        public double TokeStrikeCountBonus { get; set; } = 40;
        public double RecallScore { get; set; } = 70;
        public double LiftScore { get; set; } = 90;
        public double DigScore { get; set; } = 95;
    }

    // ═══════════════════════════════════════════════════════════════
    // Event
    // ═══════════════════════════════════════════════════════════════

    [JsonPropertyName("event")]
    public EventSection Event { get; set; } = new();

    public class EventSection
    {
        public double HpCostHardBlockThreshold { get; set; } = 0.50;
        public double HpCostHardBlockScore { get; set; } = -200;
        public double HpCostWarningThreshold { get; set; } = 0.70;
        public double HpCostWarningScore { get; set; } = -120;
        public double HpCostNormalScore { get; set; } = -60;
        public double CurseNoSynergyPenalty { get; set; } = -120;
        public double CurseWithSynergyPenalty { get; set; } = -40;
        public double StatusCardPenalty { get; set; } = -30; // M10: Wound/Burn/Slimed/Dazed/Void — milder than curses
        public double RepeatHardBlock { get; set; } = -500;
        public double RepeatPenalty2 { get; set; } = -150;
        public double TabletPenalty { get; set; } = -200;
        public EventKeywords Keywords { get; set; } = new();
    }

    public class EventKeywords
    {
        public double HealLowHp { get; set; } = 250;
        public double HealHighHp { get; set; } = 100;
        public double Relic { get; set; } = 100;
        public double Card { get; set; } = 60;
        public double Gold { get; set; } = 40;
        public double Upgrade { get; set; } = 70;
        public double Transform { get; set; } = 50;
        public double Remove { get; set; } = 60;
        public double Strength { get; set; } = 50;
        public double MaxHp { get; set; } = 40;
        public double Proceed { get; set; } = -30;
        public double Sacrifice { get; set; } = -40;
    }

    // ═══════════════════════════════════════════════════════════════
    // Shop
    // ═══════════════════════════════════════════════════════════════

    [JsonPropertyName("shop")]
    public ShopSection Shop { get; set; } = new();

    public class ShopSection
    {
        public int MinGoldReserve { get; set; } = 30;
        public double MinScoreToBuyLowGold { get; set; } = 100;
        public double BaseRelicValue { get; set; } = 80;
        public double EnergyRelicBonus { get; set; } = 200;
        public double StrengthRelicBonus { get; set; } = 100;
        public double DefenseRelicBonus { get; set; } = 60;
        public double RelicCostHighPenalty { get; set; } = -30;
        public double RelicCostLowBonus { get; set; } = 20;
        public double RelicWrMultiplier { get; set; } = 500;
        public double RelicBossWrMultiplier { get; set; } = 300;
        public double BaseCardValue { get; set; } = 40;
        public double PremiumCardBonus { get; set; } = 120;
        public double GoodCardBonus { get; set; } = 60;
        public double BasicCardPenalty { get; set; } = -100;
        public double CardCostHighPenalty { get; set; } = -20;
        public double CardCostLowBonus { get; set; } = 10;
        public double PotionBaseValue { get; set; } = 20;
        public double RemoveCardLargeDeckScore { get; set; } = 300;
        public double RemoveCardNormalScore { get; set; } = 150;
        public int RemoveMinDeckSize { get; set; } = 10;
    }

    // ═══════════════════════════════════════════════════════════════
    // Card Tiers
    // ═══════════════════════════════════════════════════════════════

    [JsonPropertyName("card_tiers")]
    public Dictionary<string, double> CardTiers { get; set; } = new()
    {
        ["FREE_ENERGY"] = 65,
        ["TUTOR"] = 60,
        ["UPGRADE_HAND"] = 55,
        ["DECK_THINNER"] = 50,
        ["ENERGY_DRAW"] = 50,
        ["STRENGTH"] = 45,
        ["PREMIUM_ATTACK"] = 40,
        ["PREMIUM_BLOCK"] = 35,
        ["AOE"] = 30,
        ["DEBUFF"] = 25,
        ["DRAW"] = 20,
        ["POWER"] = 20,
        ["BASIC_BLOCK"] = 15,
        ["BASIC_ATTACK"] = 10,
        ["FLEX"] = 5,
    };

    // ═══════════════════════════════════════════════════════════════
    // Potion
    // ═══════════════════════════════════════════════════════════════

    [JsonPropertyName("potion")]
    public PotionSection Potion { get; set; } = new();

    public class PotionSection
    {
        public double UseWhenHpBelowRatio { get; set; } = 0.4;
        public bool UseWhenEnemyElite { get; set; } = true;
        public bool UseBeforeDeath { get; set; } = true;
        public double MinHpToSavePotion { get; set; } = 0.70;
        public int FirePotionDamageEstimate { get; set; } = 20;
        public int BlockPotionBlockEstimate { get; set; } = 12;
        public double StrengthPotionValue { get; set; } = 40;
        public double WeakPotionValue { get; set; } = 30;
        public int RegenPotionHealEstimate { get; set; } = 15;
    }
}
