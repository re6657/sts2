using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TokenSpire2.Solver;

/// <summary>
/// Structured JSON battle logger. Records every combat turn with full state,
/// solver decisions, and actual outcomes for later analysis and optimization.
/// </summary>
public static class BattleLogger
{
    private static BattleRecord? _currentBattle;
    private static TurnRecord? _currentTurn;
    private static string? _logDir;
    private static bool _enabled;

    public static void Enable()
    {
        if (_enabled) return;
        _enabled = true;
        try
        {
            var asmDir = Path.GetDirectoryName(
                System.Reflection.Assembly.GetExecutingAssembly().Location) ?? ".";
            _logDir = Path.Combine(asmDir, "llm_data", "battles");
            Directory.CreateDirectory(_logDir);
            MainFile.Logger.Info($"[BattleLog] Enabled, writing to {_logDir}");
        }
        catch (Exception ex)
        {
            MainFile.Logger.Info($"[BattleLog] Failed to init: {ex.Message}");
            _enabled = false;
        }
    }

    /// <summary>Start a new battle record.</summary>
    public static void StartBattle(string encounterName, int floor, string character, int ascension)
    {
        if (!_enabled) return;
        _currentBattle = new BattleRecord
        {
            EncounterName = encounterName,
            Floor = floor,
            Character = character,
            AscensionLevel = ascension,
            StartTime = DateTime.Now,
        };
    }

    /// <summary>Record the pre-turn state before solver runs.</summary>
    public static void StartTurn(
        int turnNumber,
        int energy, int block, int hp, int maxHp,
        List<string> handCardIds, List<int> handCardCosts,
        int drawPileCount, int discardPileCount,
        List<string> enemyIds, List<int> enemyHps, List<int> enemyMaxHps,
        List<int> enemyBlocks, List<int> enemyIntentDamages, List<string> enemyIntentTypes,
        List<int> enemyVulnStacks, List<int> enemyWeakStacks, List<int> enemyStrStacks,
        List<int> enemyDexStacks, List<List<PowerSnapshot>>? enemyAllPowers,
        int strength, int dexterity,
        int weakOnPlayer, int frailOnPlayer, int vulnerableOnPlayer,
        int orbSlots, int lightningOrbs, int frostOrbs,
        int darkOrbs, int plasmaOrbs, int focus, int stars,
        List<PowerSnapshot>? playerAllPowers = null,
        List<string>? potionIds = null)
    {
        if (!_enabled || _currentBattle == null) return;

        var cards = new List<CardSnapshot>();
        for (int i = 0; i < handCardIds.Count; i++)
            cards.Add(new CardSnapshot {
                Id = handCardIds[i],
                Cost = i < handCardCosts.Count ? handCardCosts[i] : -1
            });

        var enemies = new List<EnemySnapshot>();
        for (int i = 0; i < enemyIds.Count; i++)
        {
            enemies.Add(new EnemySnapshot
            {
                Index = i,
                Id = enemyIds[i],
                Hp = i < enemyHps.Count ? enemyHps[i] : 0,
                MaxHp = i < enemyMaxHps.Count ? enemyMaxHps[i] : 0,
                Block = i < enemyBlocks.Count ? enemyBlocks[i] : 0,
                IntentDamage = i < enemyIntentDamages.Count ? enemyIntentDamages[i] : 0,
                IntentType = i < enemyIntentTypes.Count ? enemyIntentTypes[i] : "?",
                VulnerableStacks = i < enemyVulnStacks.Count ? enemyVulnStacks[i] : 0,
                WeakStacks = i < enemyWeakStacks.Count ? enemyWeakStacks[i] : 0,
                StrengthStacks = i < enemyStrStacks.Count ? enemyStrStacks[i] : 0,
                DexterityStacks = i < enemyDexStacks.Count ? enemyDexStacks[i] : 0,
                Powers = (enemyAllPowers != null && i < enemyAllPowers.Count)
                    ? enemyAllPowers[i] : new List<PowerSnapshot>(),
            });
        }

        _currentTurn = new TurnRecord
        {
            TurnNumber = turnNumber,
            Energy = energy,
            Block = block,
            Hp = hp,
            MaxHp = maxHp,
            Hand = cards,
            DrawPileCount = drawPileCount,
            DiscardPileCount = discardPileCount,
            Enemies = enemies,
            PlayerPowers = new PlayerPowerSnapshot
            {
                Strength = strength,
                Dexterity = dexterity,
                Weak = weakOnPlayer,
                Frail = frailOnPlayer,
                Vulnerable = vulnerableOnPlayer,
                AllPowers = playerAllPowers ?? new List<PowerSnapshot>(),
            },
            Orbs = new OrbSnapshot
            {
                OrbSlots = orbSlots,
                Lightning = lightningOrbs,
                Frost = frostOrbs,
                Dark = darkOrbs,
                Plasma = plasmaOrbs,
                Focus = focus,
                Stars = stars,
            },
            PotionIds = potionIds ?? new List<string>(),
            PotionCount = (potionIds ?? new List<string>()).Count,
        };
    }

    /// <summary>Record the solver's plan for this turn.</summary>
    public static void LogSolverPlan(IroncladSolver.SolveResult result, int statesExplored)
    {
        if (!_enabled || _currentTurn == null) return;

        _currentTurn.SolverPlan = new SolverPlanSnapshot
        {
            Actions = result.Actions.Select(a => a.ToString() ?? "?").ToList(),
            EstimatedDamage = result.EstimatedDamage,
            EstimatedBlock = result.EstimatedBlock,
            Score = result.DebugInfo,
            StatesExplored = statesExplored,
        };
    }

    /// <summary>Record a single action execution result.</summary>
    public static void LogAction(string cardId, bool success, string? failReason = null,
        int? actualDamage = null, int? actualBlock = null, string? target = null)
    {
        if (!_enabled || _currentTurn == null) return;

        _currentTurn.ActionResults.Add(new ActionResultSnapshot
        {
            CardId = cardId,
            Success = success,
            FailReason = failReason,
            ActualDamage = actualDamage,
            ActualBlock = actualBlock,
            Target = target,
        });
    }

    /// <summary>Record the post-turn outcome.</summary>
    public static void EndTurn(
        int hpAfter, int blockAfter,
        int damageTaken, int enemiesKilled,
        int energyRemaining,
        int playableCardsNotPlayed = 0, int totalPlayableInHand = 0)
    {
        if (!_enabled || _currentTurn == null) return;

        var issues = new List<string>();
        if (energyRemaining > 0 && playableCardsNotPlayed > 0)
            issues.Add($"ENERGY_WASTED: {energyRemaining} energy left, {playableCardsNotPlayed}/{totalPlayableInHand} playable cards not used");
        else if (energyRemaining > 0 && totalPlayableInHand == 0)
            issues.Add($"NO_PLAYABLE: {energyRemaining} energy left, 0 playable cards");
        else if (playableCardsNotPlayed > 0 && energyRemaining == 0)
            issues.Add($"CARDS_UNPLAYED: {playableCardsNotPlayed} playable cards left at 0 energy");

        _currentTurn.Outcome = new TurnOutcomeSnapshot
        {
            HpAfter = hpAfter,
            BlockAfter = blockAfter,
            DamageTaken = damageTaken,
            EnemiesKilled = enemiesKilled,
            EnergyRemaining = energyRemaining,
            PlayableCardsNotPlayed = playableCardsNotPlayed,
            TotalPlayableInHand = totalPlayableInHand,
            Issues = issues,
        };

        _currentBattle!.Turns.Add(_currentTurn);
        _currentTurn = null;
    }

    /// <summary>End the current battle and save to disk.</summary>
    public static void EndBattle(bool victory, int hpRemaining, string? killedBy = null)
    {
        if (!_enabled || _currentBattle == null) return;

        _currentBattle.Victory = victory;
        _currentBattle.EndHp = hpRemaining;
        _currentBattle.KilledBy = killedBy;
        _currentBattle.EndTime = DateTime.Now;
        _currentBattle.DurationSeconds = (_currentBattle.EndTime - _currentBattle.StartTime).TotalSeconds;

        _currentBattle.TotalTurns = _currentBattle.Turns.Count;
        _currentBattle.TotalDamageDealt = _currentBattle.Turns
            .Sum(t => t.ActionResults.Sum(a => a.ActualDamage ?? 0));
        _currentBattle.TotalDamageTaken = _currentBattle.Turns
            .Sum(t => t.Outcome?.DamageTaken ?? 0);
        _currentBattle.TotalEnemiesKilled = _currentBattle.Turns
            .Sum(t => t.Outcome?.EnemiesKilled ?? 0);
        _currentBattle.SolverCrashes = _currentBattle.Turns
            .Count(t => t.SolverPlan?.Actions.Any(a => a.Contains("CRASH") == true) == true);

        SaveBattle();
        _currentBattle = null;
    }

    private static void SaveBattle()
    {
        if (_currentBattle == null || _logDir == null) return;
        try
        {
            var ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var name = (_currentBattle.EncounterName ?? "Unknown").Replace(" ", "_")
                .Replace(":", "").Replace("/", "_");
            var filename = $"battle_{name}_{ts}.json";
            var path = Path.Combine(_logDir, filename);

            var json = JsonSerializer.Serialize(_currentBattle, new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            });
            File.WriteAllText(path, json);
            MainFile.Logger.Info($"[BattleLog] Saved: {filename} ({json.Length} bytes)");
        }
        catch (Exception ex)
        {
            MainFile.Logger.Info($"[BattleLog] Save failed: {ex.Message}");
        }
    }

    // ── Internal data types ─────────────────────────────────────────────────

    private class BattleRecord
    {
        public string? EncounterName { get; set; }
        public int Floor { get; set; }
        public string? Character { get; set; }
        public int AscensionLevel { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public double DurationSeconds { get; set; }
        public bool Victory { get; set; }
        public int EndHp { get; set; }
        public string? KilledBy { get; set; }
        public int TotalTurns { get; set; }
        public int TotalDamageDealt { get; set; }
        public int TotalDamageTaken { get; set; }
        public int TotalEnemiesKilled { get; set; }
        public int SolverCrashes { get; set; }
        public List<TurnRecord> Turns { get; set; } = new();
    }

    private class CardSnapshot
    {
        public string Id { get; set; } = "";
        public int Cost { get; set; } = -1;
    }

    private class EnemySnapshot
    {
        public int Index { get; set; }
        public string Id { get; set; } = "";
        public int Hp { get; set; }
        public int MaxHp { get; set; }
        public int Block { get; set; }
        public int IntentDamage { get; set; }
        public string IntentType { get; set; } = "";
        public int VulnerableStacks { get; set; }
        public int WeakStacks { get; set; }
        public int StrengthStacks { get; set; }
        public int DexterityStacks { get; set; }
        public List<PowerSnapshot> Powers { get; set; } = new();
    }

    public class PowerSnapshot
    {
        public string Name { get; set; } = "";
        public int Amount { get; set; }
    }

    private class TurnRecord
    {
        public int TurnNumber { get; set; }
        public int Energy { get; set; }
        public int Block { get; set; }
        public int Hp { get; set; }
        public int MaxHp { get; set; }
        public int DrawPileCount { get; set; }
        public int DiscardPileCount { get; set; }
        public List<CardSnapshot> Hand { get; set; } = new();
        public List<EnemySnapshot> Enemies { get; set; } = new();
        public PlayerPowerSnapshot PlayerPowers { get; set; } = new();
        public OrbSnapshot Orbs { get; set; } = new();
        public List<string>? PotionIds { get; set; }
        public int PotionCount { get; set; }
        public SolverPlanSnapshot? SolverPlan { get; set; }
        public List<ActionResultSnapshot> ActionResults { get; set; } = new();
        public TurnOutcomeSnapshot? Outcome { get; set; }
    }

    private class PlayerPowerSnapshot
    {
        public int Strength { get; set; }
        public int Dexterity { get; set; }
        public int Weak { get; set; }
        public int Frail { get; set; }
        public int Vulnerable { get; set; }
        public List<PowerSnapshot> AllPowers { get; set; } = new();
    }

    private class OrbSnapshot
    {
        public int OrbSlots { get; set; }
        public int Lightning { get; set; }
        public int Frost { get; set; }
        public int Dark { get; set; }
        public int Plasma { get; set; }
        public int Focus { get; set; }
        public int Stars { get; set; }
    }

    private class SolverPlanSnapshot
    {
        public List<string> Actions { get; set; } = new();
        public int EstimatedDamage { get; set; }
        public int EstimatedBlock { get; set; }
        public string? Score { get; set; }
        public int StatesExplored { get; set; }
    }

    private class ActionResultSnapshot
    {
        public string? CardId { get; set; }
        public bool Success { get; set; }
        public string? FailReason { get; set; }
        public int? ActualDamage { get; set; }
        public int? ActualBlock { get; set; }
        public string? Target { get; set; }
    }

    private class TurnOutcomeSnapshot
    {
        public int HpAfter { get; set; }
        public int BlockAfter { get; set; }
        public int DamageTaken { get; set; }
        public int EnemiesKilled { get; set; }
        public int EnergyRemaining { get; set; }
        public int PlayableCardsNotPlayed { get; set; }
        public int TotalPlayableInHand { get; set; }
        public List<string> Issues { get; set; } = new();
    }
}
