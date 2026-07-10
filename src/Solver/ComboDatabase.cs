using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TokenSpire2.Solver;

/// <summary>
/// Card combo synergy database loaded from card_combos.json.
/// When a card from a known combo is already in the deck, its partner cards
/// get a priority bonus in CardRewardDecider.
///
/// Thread-safe: loaded once at first access, reloadable via Load().
/// </summary>
public static class ComboDatabase
{
    private static Dictionary<string, List<(string PartnerCard, double SynergyScore)>>? _lookup;
    private static readonly object _lock = new();

    /// <summary>Card A → [(Partner Card B, synergy_score), ...]</summary>
    public static Dictionary<string, List<(string PartnerCard, double SynergyScore)>> Lookup
    {
        get
        {
            if (_lookup == null) Load();
            return _lookup!;
        }
    }

    /// <summary>Character name → lowercase key used in JSON.</summary>
    private static readonly Dictionary<string, string> CharJsonKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        { "IRONCLAD", "ironclad" },
        { "SILENT", "silent" },
        { "DEFECT", "defect" },
        { "NECROBINDER", "necrobinder" },
        { "REGENT", "regent" },
    };

    /// <summary>Load or reload the combo database from card_combos.json.</summary>
    public static void Load(string? path = null)
    {
        lock (_lock)
        {
            path ??= FindComboFile();
            if (path == null || !File.Exists(path))
            {
                MainFile.Logger.Info("[ComboDatabase] card_combos.json not found, synergy disabled");
                _lookup = new Dictionary<string, List<(string, double)>>(StringComparer.OrdinalIgnoreCase);
                return;
            }

            try
            {
                var json = File.ReadAllText(path);
                var raw = JsonSerializer.Deserialize<Dictionary<string, ComboCharacterData>>(json);
                _lookup = new Dictionary<string, List<(string, double)>>(StringComparer.OrdinalIgnoreCase);

                if (raw == null) return;

                int totalSynergies = 0;
                foreach (var (charKey, charData) in raw)
                {
                    if (charData?.TopSynergies == null) continue;

                    foreach (var syn in charData.TopSynergies)
                    {
                        if (string.IsNullOrEmpty(syn.CardA) || string.IsNullOrEmpty(syn.CardB))
                            continue;
                        if (syn.SynergyScore < 0.30) continue; // only strong synergies

                        AddSynergy(syn.CardA, syn.CardB, syn.SynergyScore);
                        AddSynergy(syn.CardB, syn.CardA, syn.SynergyScore);
                        totalSynergies++;
                    }
                }

                MainFile.Logger.Info($"[ComboDatabase] Loaded {totalSynergies} synergy pairs from {path}");
            }
            catch (Exception ex)
            {
                MainFile.Logger.Info($"[ComboDatabase] Failed to load: {ex.Message}");
                _lookup = new Dictionary<string, List<(string, double)>>(StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    private static void AddSynergy(string card, string partner, double score)
    {
        var key = card.ToUpperInvariant();
        if (!_lookup!.TryGetValue(key, out var list))
        {
            list = new List<(string, double)>();
            _lookup[key] = list;
        }
        list.Add((partner.ToUpperInvariant(), score));
    }

    /// <summary>
    /// Calculate the combo synergy bonus for picking a candidate card,
    /// given the current deck's card IDs.
    /// The bonus scales with how many combo partners are already in the deck.
    /// Returns the sum of (synergy_score × multiplier) for all matching partners.
    /// </summary>
    public static double GetSynergyBonus(string candidateCardId, List<string> deckCardIds,
        double multiplier = 12.0)
    {
        var lookup = Lookup;
        var candidateKey = candidateCardId.ToUpperInvariant();
        if (!lookup.TryGetValue(candidateKey, out var partners))
            return 0;

        double bonus = 0;
        foreach (var deckCard in deckCardIds)
        {
            var deckKey = deckCard.ToUpperInvariant();
            foreach (var (partnerCard, synergyScore) in partners)
            {
                if (deckKey == partnerCard)
                {
                    bonus += synergyScore * multiplier;
                    break; // each deck card only matches once per candidate
                }
            }
        }
        return bonus;
    }

    private static string? FindComboFile()
    {
        try
        {
            var asmDir = Path.GetDirectoryName(
                System.Reflection.Assembly.GetExecutingAssembly().Location);
            if (asmDir == null) return null;
            var path = Path.Combine(asmDir, "card_combos.json");
            return File.Exists(path) ? path : null;
        }
        catch { return null; }
    }

    // ── JSON deserialization types ──────────────────────────────────────

    private class ComboCharacterData
    {
        [JsonPropertyName("top_synergies")]
        public List<ComboEntry>? TopSynergies { get; set; }
    }

    private class ComboEntry
    {
        [JsonPropertyName("card_a")]
        public string? CardA { get; set; }

        [JsonPropertyName("card_b")]
        public string? CardB { get; set; }

        [JsonPropertyName("synergy_score")]
        public double SynergyScore { get; set; }
    }
}
