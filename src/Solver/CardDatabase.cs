using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TokenSpire2.Solver;

/// <summary>
/// Thread-safe singleton that loads per-character card definitions from JSON files
/// and provides query APIs for the two-dimensional auto-battle scoring system.
///
/// Pattern: Same as SolverParams — Initialize() once, then access Instance.
///
/// JSON files are in the Cards/ directory relative to the mod root:
///   Cards/IroncladCards.json, Cards/SilentCards.json, etc.
///
/// Each card has: id, name_cn, name_en, type, cost, rarity,
///   play_priority (0-100, whether to play), play_order (0-100, when to play),
///   effects (damage, block, vulnerable, weak, etc.)
/// </summary>
public class CardDatabase
{
    // ── Singleton ──────────────────────────────────────────────────

    private static readonly object _lock = new();
    private static CardDatabase? _instance;
    private static string _modDirectory = "";

    public static CardDatabase Instance
    {
        get
        {
            if (_instance == null)
                throw new InvalidOperationException(
                    "CardDatabase not initialized. Call CardDatabase.Initialize(modDir) first.");
            return _instance;
        }
    }

    /// <summary>Initialize from the mod's root directory (e.g. "mods/TokenSpire2/").</summary>
    public static void Initialize(string modDirectory)
    {
        lock (_lock)
        {
            if (_instance != null)
            {
                MainFile.Logger.Warn("[CardDatabase] Already initialized, skipping.");
                return;
            }
            _modDirectory = modDirectory;
            _instance = new CardDatabase();
            _instance.LoadAll();
        }
    }

    // ── Card Data ──────────────────────────────────────────────────

    /// <summary>All card definitions keyed by card ID (case-insensitive).</summary>
    public Dictionary<string, CardDefinition> AllCards { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Cards grouped by character for iteration.</summary>
    public Dictionary<string, List<CardDefinition>> CardsByCharacter { get; } = new(StringComparer.OrdinalIgnoreCase);

    private void LoadAll()
    {
        string cardsDir = Path.Combine(_modDirectory, "Cards");
        if (!Directory.Exists(cardsDir))
        {
            MainFile.Logger.Error($"[CardDatabase] Cards/ directory not found: {cardsDir}. " +
                "Run scripts/generate_card_db.py first or create card JSON files manually.");
            return;
        }

        string[] characters = { "Ironclad", "Silent", "Defect", "Necrobinder", "Regent" };
        int totalLoaded = 0;

        foreach (string character in characters)
        {
            string filePath = Path.Combine(cardsDir, $"{character}Cards.json");
            if (!File.Exists(filePath))
            {
                MainFile.Logger.Warn($"[CardDatabase] Card file not found: {filePath} — run scripts/generate_card_db.py?");
                continue;
            }

            try
            {
                string json = File.ReadAllText(filePath);
                var fileData = JsonSerializer.Deserialize<CardFileData>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (fileData?.Cards == null || fileData.Cards.Count == 0)
                {
                    MainFile.Logger.Warn($"[CardDatabase] {character}Cards.json is empty or invalid.");
                    continue;
                }

                var charList = new List<CardDefinition>();
                foreach (var card in fileData.Cards)
                {
                    // Validate and fill defaults
                    if (string.IsNullOrEmpty(card.Id))
                    {
                        MainFile.Logger.Warn($"[CardDatabase] Skipping card with empty ID in {character}");
                        continue;
                    }

                    card.Character ??= character;
                    card.NameCn ??= card.Id;
                    card.NameEn ??= card.Id;
                    card.Type ??= "Attack";
                    card.Rarity ??= "Common";
                    card.Effects ??= new Dictionary<string, object>();

                    // Clamp scores to valid range
                    card.PlayPriority = Math.Clamp(card.PlayPriority, 0, 100);
                    card.PlayOrder = Math.Clamp(card.PlayOrder, 0, 100);

                    AllCards[card.Id] = card;
                    charList.Add(card);
                }

                CardsByCharacter[character] = charList;
                totalLoaded += charList.Count;
                MainFile.Logger.Info($"[CardDatabase] Loaded {charList.Count} {character} cards from {filePath}");
            }
            catch (Exception ex)
            {
                MainFile.Logger.Error($"[CardDatabase] Failed to load {filePath}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // Load Colorless cards if available
        string colorlessPath = Path.Combine(cardsDir, "ColorlessCards.json");
        if (File.Exists(colorlessPath))
        {
            try
            {
                string json = File.ReadAllText(colorlessPath);
                var fileData = JsonSerializer.Deserialize<CardFileData>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (fileData?.Cards != null)
                {
                    var colorlessList = new List<CardDefinition>();
                    foreach (var card in fileData.Cards)
                    {
                        card.Character ??= "Colorless";
                        card.NameCn ??= card.Id;
                        card.NameEn ??= card.Id;
                        card.Type ??= "Skill";
                        card.Rarity ??= "Common";
                        card.Effects ??= new Dictionary<string, object>();
                        card.PlayPriority = Math.Clamp(card.PlayPriority, 0, 100);
                        card.PlayOrder = Math.Clamp(card.PlayOrder, 0, 100);
                        AllCards[card.Id] = card;
                        colorlessList.Add(card);
                    }
                    CardsByCharacter["Colorless"] = colorlessList;
                    totalLoaded += colorlessList.Count;
                    MainFile.Logger.Info($"[CardDatabase] Loaded {colorlessList.Count} Colorless cards");
                }
            }
            catch (Exception ex)
            {
                MainFile.Logger.Warn($"[CardDatabase] Failed to load ColorlessCards.json: {ex.Message}");
            }
        }

        MainFile.Logger.Info($"[CardDatabase] Total cards loaded: {totalLoaded} across {CardsByCharacter.Count} pools");

        if (totalLoaded == 0)
        {
            MainFile.Logger.Error("[CardDatabase] ZERO cards loaded! " +
                "Auto-battle two-dimensional scoring will fall back to CardClassifier defaults.");
        }
    }

    // ── Query API ──────────────────────────────────────────────────

    /// <summary>Get the full card definition, or null if not found.</summary>
    public CardDefinition? GetCard(string cardId)
    {
        if (string.IsNullOrEmpty(cardId)) return null;
        AllCards.TryGetValue(cardId, out var card);
        return card;
    }

    /// <summary>Get play_priority (0-100). Higher = more essential to play this turn.</summary>
    public int GetPlayPriority(string cardId)
    {
        var card = GetCard(cardId);
        if (card != null) return card.PlayPriority;

        // Fallback: compute from CardClassifier
        int fallback = CardClassifier.GetDefaultPlayPriority(cardId);
        MainFile.Logger.Debug($"[CardDatabase] PlayPriority fallback for '{cardId}': {fallback}");
        return fallback;
    }

    /// <summary>Get play_order (0-100). Higher = should be played earlier in turn.</summary>
    public int GetPlayOrder(string cardId)
    {
        var card = GetCard(cardId);
        if (card != null) return card.PlayOrder;

        // Fallback: compute from CardClassifier
        int fallback = CardClassifier.GetDefaultPlayOrder(cardId);
        MainFile.Logger.Debug($"[CardDatabase] PlayOrder fallback for '{cardId}': {fallback}");
        return fallback;
    }

    /// <summary>Get the Chinese display name for a card, or the card ID if unavailable.</summary>
    public string GetChineseName(string cardId)
    {
        var card = GetCard(cardId);
        if (card != null && !string.IsNullOrEmpty(card.NameCn))
            return card.NameCn;
        return cardId;
    }

    /// <summary>Get the English display name for a card.</summary>
    public string GetEnglishName(string cardId)
    {
        var card = GetCard(cardId);
        if (card != null && !string.IsNullOrEmpty(card.NameEn))
            return card.NameEn;
        // Format SNAKE_CASE → Title Case
        return System.Globalization.CultureInfo.InvariantCulture.TextInfo
            .ToTitleCase(cardId.Replace('_', ' ').ToLowerInvariant());
    }

    /// <summary>Get all cards for a specific character.</summary>
    public IReadOnlyList<CardDefinition> GetCardsForCharacter(string character)
    {
        if (CardsByCharacter.TryGetValue(character, out var list))
            return list;
        return Array.Empty<CardDefinition>();
    }

    /// <summary>Get card effect data.</summary>
    public IReadOnlyDictionary<string, object> GetEffects(string cardId)
    {
        var card = GetCard(cardId);
        return card?.Effects ?? new Dictionary<string, object>();
    }

    /// <summary>Get a specific effect value, or 0 if not found.</summary>
    public int GetEffectInt(string cardId, string effectKey)
    {
        var card = GetCard(cardId);
        if (card?.Effects == null) return 0;
        if (card.Effects.TryGetValue(effectKey, out var val))
        {
            if (val is long l) return (int)l;
            if (val is int i) return i;
            if (val is double d) return (int)d;
        }
        return 0;
    }

    /// <summary>Get a specific effect bool, or false if not found.</summary>
    public bool GetEffectBool(string cardId, string effectKey)
    {
        var card = GetCard(cardId);
        if (card?.Effects == null) return false;
        if (card.Effects.TryGetValue(effectKey, out var val) && val is bool b)
            return b;
        return false;
    }

    /// <summary>Check if a card definition exists in the database.</summary>
    public bool HasCard(string cardId)
    {
        return AllCards.ContainsKey(cardId);
    }

    /// <summary>Total number of cards loaded.</summary>
    public int Count => AllCards.Count;

    /// <summary>Number of characters with loaded card data.</summary>
    public int CharacterCount => CardsByCharacter.Count;
}

// ═══════════════════════════════════════════════════════════════════
// Data Transfer Objects (match JSON structure)
// ═══════════════════════════════════════════════════════════════════

/// <summary>Root structure of a Cards/*.json file.</summary>
public class CardFileData
{
    [JsonPropertyName("character")]
    public string? Character { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("total_cards")]
    public int TotalCards { get; set; }

    [JsonPropertyName("cards")]
    public List<CardDefinition>? Cards { get; set; }
}

/// <summary>
/// Complete card definition for the two-dimensional auto-battle scoring system.
/// Mirrors the JSON schema in Cards/*.json.
/// </summary>
public class CardDefinition
{
    /// <summary>Unique card ID (e.g. "BASH", "STRIKE_IRONCLAD").</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    /// <summary>Chinese display name (e.g. "痛击"). Used for manual editing.</summary>
    [JsonPropertyName("name_cn")]
    public string? NameCn { get; set; }

    /// <summary>English display name.</summary>
    [JsonPropertyName("name_en")]
    public string? NameEn { get; set; }

    /// <summary>Card type: "Attack", "Skill", or "Power".</summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    /// <summary>Energy cost. -1 for X-cost.</summary>
    [JsonPropertyName("cost")]
    public int Cost { get; set; }

    /// <summary>Card rarity: "Basic", "Common", "Uncommon", or "Rare".</summary>
    [JsonPropertyName("rarity")]
    public string? Rarity { get; set; }

    /// <summary>
    /// Play Priority (出牌优先级): 0-100.
    /// How essential it is to play this card this turn.
    /// Higher = more important to play (determines whether to include in plan).
    /// </summary>
    [JsonPropertyName("play_priority")]
    public int PlayPriority { get; set; } = 50;

    /// <summary>
    /// Play Order (出牌顺序): 0-100.
    /// How early this card should be played in the turn sequence.
    /// Higher = play earlier (determines where in the sequence).
    /// </summary>
    [JsonPropertyName("play_order")]
    public int PlayOrder { get; set; } = 50;

    /// <summary>
    /// Card effects: damage, block, vulnerable, weak, strength, poison, draw, etc.
    /// Values can be int, bool, or string.
    /// </summary>
    [JsonPropertyName("effects")]
    public Dictionary<string, object>? Effects { get; set; }

    /// <summary>Which character this card belongs to.</summary>
    [JsonPropertyName("character")]
    public string? Character { get; set; }

    /// <summary>Category tags for reference (e.g. ["DRAW", "VULNERABLE"]).</summary>
    [JsonPropertyName("categories")]
    public List<string>? Categories { get; set; }

    public override string ToString()
    {
        return $"[{Character}] {Id} ({NameCn}) pri={PlayPriority} ord={PlayOrder} {Type} {Cost}E";
    }
}

/// <summary>
/// Extension methods for CardDatabase in combat context.
/// </summary>
public static class CardDatabaseExtensions
{
    /// <summary>
    /// Compute the combined "what to play next" score for a card
    /// using the current combat context.
    ///
    /// Formula: combinedScore = context.selectionWeight × play_priority
    ///                         + context.orderWeight × play_order
    /// </summary>
    public static double GetCombinedScore(
        this CardDatabase db,
        string cardId,
        double selectionWeight = 0.60,
        double orderWeight = 0.40)
    {
        int priority = db.GetPlayPriority(cardId);
        int order = db.GetPlayOrder(cardId);
        return selectionWeight * priority + orderWeight * order;
    }

    /// <summary>
    /// Get context weights based on combat state.
    /// Returns (selectionWeight, orderWeight).
    ///
    /// Context-dependent weights:
    ///   Energy tight (≤1):      (0.80, 0.20) — prioritize card VALUE
    ///   Energy abundant (≥3):   (0.40, 0.60) — prioritize card ORDER
    ///   Early turn (cards≤2):   (0.35, 0.65) — setup/buff/debuff first
    ///   Late turn (cards≥6):    (0.85, 0.15) — play remaining value
    ///   Lethal detected:        (1.00, 0.00) — just kill
    ///   Boss fight:             (0.50, 0.50) — both matter equally
    /// </summary>
    public static (double selectionWeight, double orderWeight) GetContextWeights(
        int currentEnergy,
        int cardsPlayedThisTurn,
        bool isBossFight,
        bool lethalDetected)
    {
        // Lethal overrides everything
        if (lethalDetected)
            return (1.00, 0.00);

        // Boss fights: both dimensions matter equally
        if (isBossFight)
            return (0.50, 0.50);

        // Energy-based weighting
        if (currentEnergy <= 1)
            return (0.80, 0.20);  // energy tight → play the RIGHT cards

        // Turn position-based weighting
        if (cardsPlayedThisTurn < 2 && currentEnergy >= 3)
            return (0.35, 0.65);  // early turn with energy → set up properly

        if (currentEnergy >= 3)
            return (0.40, 0.60);  // energy abundant → optimal ordering

        // Balanced default
        return (0.60, 0.40);
    }
}
