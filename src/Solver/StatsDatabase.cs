using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace TokenSpire2.Solver;

/// <summary>
/// Loads OP.GG statistics for cards, relics, and boss relics.
/// Provides win-rate and pick-rate lookups to supplement heuristic decisions.
/// Data source: E:\STS2_OPGG_Stats_Report.md → extracted to JSON via extract_opgg_stats.py
/// </summary>
public static class StatsDatabase
{
    private const string STATS_DIR = "llm_data/opgg_stats";

    // cardId (normalized) → stats
    private static Dictionary<string, CardStat> _cards = new();
    // relicId (normalized) → stats
    private static Dictionary<string, RelicStat> _relics = new();
    // relicId (normalized) → boss relic swap stats
    private static Dictionary<string, RelicStat> _bossRelics = new();

    private static bool _loaded;
    private static string _currentClass = "ironclad"; // default; updated on character select

    public static string CurrentClass
    {
        get => _currentClass;
        set
        {
            if (_currentClass != value)
            {
                _currentClass = value;
                _cards.Clear();
                _relics.Clear();
                _bossRelics.Clear();
                _loaded = false;
            }
        }
    }

    // ── Loading ────────────────────────────────────────────────────────

    public static void EnsureLoaded()
    {
        if (_loaded) return;
        Load();
        _loaded = true;
    }

    private static void Load()
    {
        try
        {
            string baseDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, STATS_DIR);
            if (!Directory.Exists(baseDir))
            {
                MainFile.Logger.Info($"[StatsDatabase] Stats dir not found: {baseDir}");
                return;
            }

            // Load cards
            string cardPath = Path.Combine(baseDir, "card_stats.json");
            if (File.Exists(cardPath))
            {
                var json = File.ReadAllText(cardPath);
                var all = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, CardStat>>>(json);
                if (all != null && all.TryGetValue(_currentClass, out var classCards))
                {
                    foreach (var (id, stat) in classCards)
                        _cards[NormalizeId(id)] = stat;
                }
                MainFile.Logger.Info($"[StatsDatabase] Loaded {_cards.Count} card stats for {_currentClass}");
            }

            // Load relics
            string relicPath = Path.Combine(baseDir, "relic_stats.json");
            if (File.Exists(relicPath))
            {
                var json = File.ReadAllText(relicPath);
                var all = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, RelicStat>>>(json);
                if (all != null && all.TryGetValue(_currentClass, out var classRelics))
                {
                    foreach (var (id, stat) in classRelics)
                        _relics[NormalizeId(id)] = stat;
                }
                MainFile.Logger.Info($"[StatsDatabase] Loaded {_relics.Count} relic stats for {_currentClass}");
            }

            // Load boss relics
            string bossPath = Path.Combine(baseDir, "boss_relic_stats.json");
            if (File.Exists(bossPath))
            {
                var json = File.ReadAllText(bossPath);
                var all = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, RelicStat>>>(json);
                if (all != null && all.TryGetValue(_currentClass, out var classBoss))
                {
                    foreach (var (id, stat) in classBoss)
                        _bossRelics[NormalizeId(id)] = stat;
                }
                MainFile.Logger.Info($"[StatsDatabase] Loaded {_bossRelics.Count} boss relic stats for {_currentClass}");
            }
        }
        catch (Exception ex)
        {
            MainFile.Logger.Error($"[StatsDatabase] Load failed: {ex.Message}");
        }
    }

    // ── Lookup ─────────────────────────────────────────────────────────

    /// <summary>Get card win-rate impact (positive = good to pick). Returns 0 if unknown.</summary>
    public static double GetCardWrImpact(string? cardId)
    {
        if (string.IsNullOrEmpty(cardId)) return 0;
        EnsureLoaded();
        var key = NormalizeId(cardId);
        if (_cards.TryGetValue(key, out var stat))
            return stat.wrImpact;
        return 0;
    }

    /// <summary>Get card absolute win rate. Returns 0.20 (baseline) if unknown.</summary>
    public static double GetCardWinRate(string? cardId)
    {
        if (string.IsNullOrEmpty(cardId)) return 0.20;
        EnsureLoaded();
        var key = NormalizeId(cardId);
        if (_cards.TryGetValue(key, out var stat))
            return stat.winRate;
        return 0.20;
    }

    /// <summary>Get relic win rate. Returns 0.20 (baseline) if unknown.</summary>
    public static double GetRelicWinRate(string? relicId)
    {
        if (string.IsNullOrEmpty(relicId)) return 0.20;
        EnsureLoaded();
        var key = NormalizeId(relicId);
        if (_relics.TryGetValue(key, out var stat))
            return stat.winRate;
        if (_bossRelics.TryGetValue(key, out var bstat))
            return bstat.winRate;
        return 0.20;
    }

    /// <summary>Get boss relic swap win rate. Returns 0.20 if unknown.</summary>
    public static double GetBossRelicWinRate(string? relicId)
    {
        if (string.IsNullOrEmpty(relicId)) return 0.20;
        EnsureLoaded();
        var key = NormalizeId(relicId);
        if (_bossRelics.TryGetValue(key, out var stat))
            return stat.winRate;
        return 0.20;
    }

    /// <summary>Rank a boss relic for initial swap: higher = better.</summary>
    public static double GetBossRelicSwapScore(string? relicId)
    {
        double wr = GetBossRelicWinRate(relicId);
        // Scale: 20% win rate = score 20, 38% = score 38. Normal range: 8-40.
        return wr * 100.0;
    }

    // ── ID Normalization ───────────────────────────────────────────────

    /// <summary>
    /// Normalize card/relic IDs for lookup.
    /// OP.GG uses UPPER_SNAKE_CASE; game may use PascalCase or snake_case.
    /// </summary>
    private static string NormalizeId(string id)
    {
        return id.ToUpperInvariant()
            .Replace(" ", "_")
            .Replace("-", "_");
    }

    // ── Data types ─────────────────────────────────────────────────────

    private class CardStat
    {
        public string id { get; set; } = "";
        public double winRate { get; set; }
        public double wrImpact { get; set; }
        public int offered { get; set; }
        public int picked { get; set; }
    }

    private class RelicStat
    {
        public string id { get; set; } = "";
        public int count { get; set; }
        public double winRate { get; set; }
    }
}
