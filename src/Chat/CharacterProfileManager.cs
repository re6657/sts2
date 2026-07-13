using System;
using System.Collections.Generic;
using System.IO;

namespace TokenSpire2.Chat;

/// <summary>
/// Loads and caches character persona prompts from markdown files
/// in the characters/ directory.
///
/// Each .md file is a full persona prompt. The filename without extension
/// is the character ID (e.g. "delilah", "seele", "elysia").
///
/// Users can add new characters by dropping a .md file into characters/.
/// </summary>
public static class CharacterProfileManager
{
    private static readonly object _lock = new();
    private static readonly Dictionary<string, string> _cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> _displayNames = new(StringComparer.OrdinalIgnoreCase);
    private static string? _charactersDir;
    private static bool _initialized;

    /// <summary>All discovered character IDs.</summary>
    public static IReadOnlyList<string> CharacterIds
    {
        get
        {
            lock (_lock)
            {
                return new List<string>(_cache.Keys);
            }
        }
    }

    /// <summary>
    /// Get a display-friendly name for a character ID.
    /// Falls back to the character ID itself.
    /// </summary>
    public static string GetDisplayName(string characterId)
    {
        lock (_lock)
        {
            if (_displayNames.TryGetValue(characterId, out var name))
                return name;
            return characterId;
        }
    }

    /// <summary>
    /// Get the full persona prompt for a character.
    /// Returns null if the character is not found.
    /// </summary>
    public static string? GetPersona(string characterId)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(characterId, out var cached))
                return cached;
        }

        // Lazy-load on first request
        LoadCharacter(characterId);

        lock (_lock)
        {
            return _cache.GetValueOrDefault(characterId);
        }
    }

    /// <summary>
    /// Scan the characters/ directory and load all .md files.
    /// Call once at startup.
    /// </summary>
    public static void Initialize(string modDirectory)
    {
        lock (_lock)
        {
            if (_initialized) return;
            _initialized = true;
            _charactersDir = Path.Combine(modDirectory, "characters");
        }

        try
        {
            if (!Directory.Exists(_charactersDir))
            {
                Directory.CreateDirectory(_charactersDir);
                MainFile.Logger?.Info($"[CharacterProfile] Created characters/ directory: {_charactersDir}");
                return;
            }

            var files = Directory.GetFiles(_charactersDir, "*.md");
            foreach (var file in files)
            {
                var id = Path.GetFileNameWithoutExtension(file);
                if (string.IsNullOrEmpty(id) || id.Equals("TEMPLATE", StringComparison.OrdinalIgnoreCase))
                    continue;
                LoadCharacterInternal(id, file);
            }

            MainFile.Logger?.Info($"[CharacterProfile] Loaded {_cache.Count} character(s): " +
                $"{string.Join(", ", _cache.Keys)}");
        }
        catch (Exception ex)
        {
            MainFile.Logger?.Info($"[CharacterProfile] Error scanning directory: {ex.Message}");
        }
    }

    private static void LoadCharacter(string characterId)
    {
        if (_charactersDir == null) return;
        var path = Path.Combine(_charactersDir, $"{characterId}.md");
        if (!File.Exists(path)) return;

        lock (_lock)
        {
            if (_cache.ContainsKey(characterId)) return;
        }

        LoadCharacterInternal(characterId, path);
    }

    private static void LoadCharacterInternal(string id, string filePath)
    {
        try
        {
            var content = File.ReadAllText(filePath);
            // Extract display name from first line: "# Name——..." or "# Name —..."
            var displayName = id;
            var firstLine = content.AsSpan(0, Math.Min(content.Length, 200));
            int newlineIdx = firstLine.IndexOf('\n');
            if (newlineIdx > 0)
                firstLine = firstLine[..newlineIdx];
            var line = firstLine.Trim();
            if (line.StartsWith("# "))
                line = line[2..].Trim();

            // Try to extract: "月下妖精 / 德丽莎·月下初拥（Delilah）——..."
            // Take the part after " / " if present, before "——" or "（"
            var namePart = line;
            var slashIdx = namePart.IndexOf('/');
            if (slashIdx >= 0)
                namePart = namePart[(slashIdx + 1)..].Trim();

            var dashIdx = namePart.IndexOf("——");
            if (dashIdx < 0) dashIdx = namePart.IndexOf("—");
            if (dashIdx >= 0)
                namePart = namePart[..dashIdx].Trim();

            var parenIdx = namePart.IndexOf('（');
            if (parenIdx >= 0)
                namePart = namePart[..parenIdx].Trim();

            if (namePart.Length > 0 && namePart.Length < 30)
                displayName = namePart.ToString();

            lock (_lock)
            {
                _cache[id] = content;
                _displayNames[id] = displayName;
            }

            MainFile.Logger?.Info($"[CharacterProfile] Loaded: {id} → {displayName} ({content.Length} bytes)");
        }
        catch (Exception ex)
        {
            MainFile.Logger?.Info($"[CharacterProfile] Error loading {id}: {ex.Message}");
        }
    }
}
