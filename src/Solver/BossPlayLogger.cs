using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TokenSpire2.Solver;

/// <summary>
/// Records detailed card-by-card play sequence for boss fights.
/// Writes JSON logs to E:\STS2_BossPlays\{Character}\.
///
/// Each log includes:
///   - Run number, character, boss name, seed, timestamp
///   - Per-turn card plays with card effects (damage, block, buffs, debuffs)
///   - Per-turn state (HP, energy, enemy status)
///   - Final outcome (victory/defeat, HP remaining, total turns)
/// </summary>
public static class BossPlayLogger
{
    private static BossPlayRecord? _current;
    private static int _currentTurnNumber = 0;
    private static string _outputRoot = @"E:\BOSS战总结";
    private static bool _enabled;

    // Set by AutoSlayNode before combat starts
    public static string RunNumber { get; set; } = "?";
    public static string Character { get; set; } = "?";
    public static string Seed { get; set; } = "?";

    /// <summary>
    /// Call at the START of a boss fight. If not a boss, this is a no-op.
    /// </summary>
    public static void StartBossFight(string encounterId, int floor,
        string character, string runNumber, string seed)
    {
        // Only track actual boss fights
        if (!IsBossEncounter(encounterId))
        {
            _enabled = false;
            return;
        }

        _enabled = true;
        RunNumber = runNumber;
        Character = character;
        Seed = seed;

        _current = new BossPlayRecord
        {
            RunNumber = runNumber,
            Character = character,
            Seed = seed,
            BossName = SanitizeBossName(encounterId),
            EncounterId = encounterId,
            Floor = floor,
            StartTime = DateTime.Now,
        };

        _currentTurnNumber = 0;

        try
        {
            var dir = GetOutputDir();
            Directory.CreateDirectory(dir);
        }
        catch (Exception ex)
        {
            MainFile.Logger.Info($"[BossPlayLog] Failed to create output dir: {ex.Message}");
            _enabled = false;
        }
    }

    /// <summary>
    /// Call at the START of each turn in a boss fight.
    /// </summary>
    public static void StartTurn(int turnNumber, int energy, int playerHp, int playerMaxHp,
        int playerBlock, int strength, int dexterity,
        List<EnemyStateSnapshot> enemies)
    {
        if (!_enabled || _current == null) return;
        _currentTurnNumber = turnNumber;

        _current.Turns.Add(new BossTurnRecord
        {
            TurnNumber = turnNumber,
            StartEnergy = energy,
            StartHp = playerHp,
            StartMaxHp = playerMaxHp,
            StartBlock = playerBlock,
            StartStrength = strength,
            StartDexterity = dexterity,
            EnemiesAtTurnStart = enemies,
        });
    }

    /// <summary>
    /// Call for EACH card played during a boss fight.
    /// </summary>
    public static void LogCardPlay(int turnNumber, string cardId, CardEffectReader.CardEffects effects,
        string? targetEnemyId = null, int actualDamage = 0, int actualBlock = 0,
        int energyCost = -1)
    {
        if (!_enabled || _current == null) return;

        // Find the current turn record (last added)
        var turnRecord = _current.Turns.LastOrDefault();
        if (turnRecord == null) return;

        turnRecord.CardPlays.Add(new CardPlayDetail
        {
            CardId = cardId,
            EnergyCost = energyCost,
            TargetEnemyId = targetEnemyId,
            ActualDamage = actualDamage,
            ActualBlock = actualBlock,
            // Card effects
            BaseDamage = effects.BaseDamage,
            BaseBlock = effects.BaseBlock,
            VulnerableStacks = effects.VulnerableStacks,
            WeakStacks = effects.WeakStacks,
            GrantsStrength = effects.GrantsStrength,
            StrengthAmount = effects.StrengthAmount,
            GrantsDexterity = effects.GrantsDexterity,
            DexterityAmount = effects.DexterityAmount,
            EnergyGain = effects.EnergyGain,
            HpCost = effects.HpCost,
            IsAoe = effects.IsAoe,
            IsPower = effects.IsPower,
            IsXCost = effects.IsXCost,
        });
    }

    /// <summary>
    /// Call at the END of the boss fight. Writes the log to disk.
    /// </summary>
    public static void EndBossFight(bool victory, int hpRemaining, int totalTurns,
        int totalDamageDealt, int totalDamageTaken)
    {
        if (!_enabled || _current == null) return;

        _current.Victory = victory;
        _current.EndHp = hpRemaining;
        _current.TotalTurns = totalTurns;
        _current.TotalDamageDealt = totalDamageDealt;
        _current.TotalDamageTaken = totalDamageTaken;
        _current.EndTime = DateTime.Now;
        _current.DurationSeconds = (_current.EndTime - _current.StartTime).TotalSeconds;

        SaveToDisk();
        _current = null;
        _enabled = false;
    }

    private static void SaveToDisk()
    {
        if (_current == null) return;
        try
        {
            var dir = GetOutputDir();
            Directory.CreateDirectory(dir);

            var ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var bossName = (_current.BossName ?? "Unknown").Replace(" ", "_");
            var filename = $"run{_current.RunNumber}_{bossName}_{ts}.json";
            var path = Path.Combine(dir, filename);

            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            };
            var json = JsonSerializer.Serialize(_current, options);
            File.WriteAllText(path, json);

            MainFile.Logger.Info($"[BossPlayLog] Saved boss play log: {path} ({json.Length} bytes)");
        }
        catch (Exception ex)
        {
            MainFile.Logger.Info($"[BossPlayLog] Save failed: {ex.Message}");
        }
    }

    private static string GetOutputDir()
    {
        var character = (_current?.Character ?? Character ?? "Unknown");
        return Path.Combine(_outputRoot, character);
    }

    // ── Deck snapshot captured at boss fight start ──────────────────────
    public static void SetDeckSnapshot(List<string> deckCardIds, List<string> relicIds,
        List<string> potionIds, int attackCount, int skillCount, int powerCount,
        int upgradedCount, int basicStrikeCount, int basicDefendCount)
    {
        if (_current == null) return;
        _current.DeckSnapshot = new DeckSnapshot
        {
            CardIds = deckCardIds,
            RelicIds = relicIds,
            PotionIds = potionIds,
            AttackCount = attackCount,
            SkillCount = skillCount,
            PowerCount = powerCount,
            UpgradedCount = upgradedCount,
            BasicStrikeCount = basicStrikeCount,
            BasicDefendCount = basicDefendCount,
            TotalCardCount = deckCardIds.Count,
        };
    }

    private static bool IsBossEncounter(string encounterId)
    {
        if (string.IsNullOrWhiteSpace(encounterId)) return false;
        return encounterId.ToUpperInvariant().Contains("_BOSS");
    }

    private static string SanitizeBossName(string encounterId)
    {
        if (string.IsNullOrWhiteSpace(encounterId)) return "Unknown";
        // Extract just the boss name from encounter IDs like "SLIME_BOSS" or "GUARDIAN_BOSS"
        // L8: use RemoveEmptyEntries to avoid double spaces from consecutive underscores
        var name = encounterId.Replace("_BOSS", "").Replace("_", " ");
        return string.Join(" ", name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w =>
            char.ToUpper(w[0]) + w[1..].ToLower()));
    }

    // ── Data types ──────────────────────────────────────────────────────────

    private class BossPlayRecord
    {
        public string RunNumber { get; set; } = "";
        public string Character { get; set; } = "";
        public string Seed { get; set; } = "";
        public string BossName { get; set; } = "";
        public string EncounterId { get; set; } = "";
        public int Floor { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public double DurationSeconds { get; set; }
        public bool Victory { get; set; }
        public int EndHp { get; set; }
        public int TotalTurns { get; set; }
        public int TotalDamageDealt { get; set; }
        public int TotalDamageTaken { get; set; }
        public DeckSnapshot? DeckSnapshot { get; set; }
        public List<BossTurnRecord> Turns { get; set; } = new();
    }

    public class DeckSnapshot
    {
        public List<string> CardIds { get; set; } = new();
        public List<string> RelicIds { get; set; } = new();
        public List<string> PotionIds { get; set; } = new();
        public int AttackCount { get; set; }
        public int SkillCount { get; set; }
        public int PowerCount { get; set; }
        public int UpgradedCount { get; set; }
        public int BasicStrikeCount { get; set; }
        public int BasicDefendCount { get; set; }
        public int TotalCardCount { get; set; }
    }

    private class BossTurnRecord
    {
        public int TurnNumber { get; set; }
        public int StartEnergy { get; set; }
        public int StartHp { get; set; }
        public int StartMaxHp { get; set; }
        public int StartBlock { get; set; }
        public int StartStrength { get; set; }
        public int StartDexterity { get; set; }
        public List<EnemyStateSnapshot> EnemiesAtTurnStart { get; set; } = new();
        public List<CardPlayDetail> CardPlays { get; set; } = new();
    }
}

// ── Shared snapshot types (also used by BattleLogger) ──

public class EnemyStateSnapshot
{
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
}

public class CardPlayDetail
{
    public string CardId { get; set; } = "";
    public int EnergyCost { get; set; } = -1;
    public string? TargetEnemyId { get; set; }
    public int ActualDamage { get; set; }
    public int ActualBlock { get; set; }
    // Card effects (from CardEffectReader)
    public int BaseDamage { get; set; }
    public int BaseBlock { get; set; }
    public int VulnerableStacks { get; set; }
    public int WeakStacks { get; set; }
    public bool GrantsStrength { get; set; }
    public int StrengthAmount { get; set; }
    public bool GrantsDexterity { get; set; }
    public int DexterityAmount { get; set; }
    public int EnergyGain { get; set; }
    public int HpCost { get; set; }
    public bool IsAoe { get; set; }
    public bool IsPower { get; set; }
    public bool IsXCost { get; set; }
}
