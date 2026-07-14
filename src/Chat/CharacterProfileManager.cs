using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace TokenSpire2.Chat;

/// <summary>
/// Per-character metadata parsed from the .md file header.
///
/// Each .md file can declare metadata via HTML comments at the top:
///   <!-- kaomoji: tsundere -->
///   <!-- display_name: 自定义名称 -->
///
/// If no metadata is found, sensible defaults are used.
/// </summary>
public sealed class CharacterMeta
{
    /// <summary>Character ID (filename without .md extension).</summary>
    public string Id { get; init; } = "";

    /// <summary>Display name shown in UI and chat headers.</summary>
    public string DisplayName { get; init; } = "";

    /// <summary>
    /// Kaomoji personality archetype: "tsundere", "gentle", "sweet", "balanced".
    /// New archetypes can be added in PromptLibrary.Kaomoji.GetProfileForArchetype().
    /// Defaults to "balanced" if not specified.
    /// </summary>
    public string KaomojiArchetype { get; init; } = "balanced";

    /// <summary>Raw .md file content (the persona prompt).</summary>
    public string PersonaPrompt { get; init; } = "";
}

/// <summary>
/// Loads and caches character persona prompts and metadata from markdown
/// files in the characters/ directory.
///
/// Each .md file is a full persona prompt. The filename without extension
/// is the character ID (e.g. "delilah", "seele", "elysia").
///
/// Metadata is declared via HTML comment lines at the top of the file:
///   <!-- kaomoji: tsundere -->
///   <!-- display_name: 德丽莎·月下初拥 -->
///
/// Users can add new characters by dropping a .md file into characters/ —
/// no code changes needed. The .md file is the SINGLE source of truth
/// for everything about a character.
/// </summary>
public static class CharacterProfileManager
{
    private static readonly object _lock = new();
    private static readonly Dictionary<string, CharacterMeta> _metas = new(StringComparer.OrdinalIgnoreCase);
    private static string? _charactersDir;
    private static bool _initialized;

    // Regex to parse: <!-- key: value -->
    private static readonly Regex _metaRegex = new(
        @"<!--\s*(\w+)\s*:\s*(.+?)\s*-->",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>All discovered character metadata.</summary>
    public static IReadOnlyList<CharacterMeta> AllCharacters
    {
        get
        {
            lock (_lock)
            {
                return new List<CharacterMeta>(_metas.Values);
            }
        }
    }

    /// <summary>All discovered character IDs.</summary>
    public static IReadOnlyList<string> CharacterIds
    {
        get
        {
            lock (_lock)
            {
                return new List<string>(_metas.Keys);
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
            if (_metas.TryGetValue(characterId, out var meta))
                return meta.DisplayName;
            return characterId;
        }
    }

    /// <summary>
    /// Get the kaomoji archetype for a character.
    /// Returns "balanced" if the character has no archetype declared.
    /// </summary>
    public static string GetKaomojiArchetype(string characterId)
    {
        lock (_lock)
        {
            if (_metas.TryGetValue(characterId, out var meta))
                return meta.KaomojiArchetype;
            return "balanced";
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
            if (_metas.TryGetValue(characterId, out var cached))
                return cached.PersonaPrompt;
        }

        // Lazy-load on first request
        LoadCharacter(characterId);

        lock (_lock)
        {
            return _metas.GetValueOrDefault(characterId)?.PersonaPrompt;
        }
    }

    /// <summary>
    /// Get full metadata for a character. Returns null if not found.
    /// </summary>
    public static CharacterMeta? GetMeta(string characterId)
    {
        lock (_lock)
        {
            if (_metas.TryGetValue(characterId, out var cached))
                return cached;
        }

        LoadCharacter(characterId);

        lock (_lock)
        {
            return _metas.GetValueOrDefault(characterId);
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
                // Skip reference/documentation files (not character profiles)
                if (id.EndsWith("_memes") || id.EndsWith("_ref") || id.EndsWith("_doc"))
                    continue;
                LoadCharacterInternal(id, file);
            }

            MainFile.Logger?.Info($"[CharacterProfile] Loaded {_metas.Count} character(s): " +
                $"{string.Join(", ", _metas.Keys)}");
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
            if (_metas.ContainsKey(characterId)) return;
        }

        LoadCharacterInternal(characterId, path);
    }

    private static void LoadCharacterInternal(string id, string filePath)
    {
        try
        {
            var content = File.ReadAllText(filePath);

            // ── Parse metadata from HTML comments at top of file ──────
            string kaomoji = "balanced";
            string explicitDisplayName = "";

            // Only scan the first 30 lines (metadata must be at the top)
            var lines = content.Split('\n');
            int scanLimit = Math.Min(lines.Length, 30);
            for (int i = 0; i < scanLimit; i++)
            {
                var match = _metaRegex.Match(lines[i]);
                if (!match.Success) continue;

                var key = match.Groups[1].Value.Trim();
                var value = match.Groups[2].Value.Trim();

                switch (key.ToLowerInvariant())
                {
                    case "kaomoji":
                        kaomoji = value.ToLowerInvariant();
                        break;
                    case "display_name":
                        explicitDisplayName = value;
                        break;
                }
            }

            // ── Extract display name ──────────────────────────────────
            var displayName = explicitDisplayName.Length > 0
                ? explicitDisplayName
                : ExtractDisplayNameFromHeading(content, id);

            // ── Build cleaned persona prompt (strip metadata comments) ─
            var personaPrompt = StripMetadataComments(lines);
            if (string.IsNullOrWhiteSpace(personaPrompt))
                personaPrompt = content; // fallback: use raw content

            // ── Build metadata object ─────────────────────────────────
            var meta = new CharacterMeta
            {
                Id = id,
                DisplayName = displayName,
                KaomojiArchetype = kaomoji,
                PersonaPrompt = personaPrompt,
            };

            lock (_lock)
            {
                _metas[id] = meta;
            }

            MainFile.Logger?.Info(
                $"[CharacterProfile] Loaded: {id} → {displayName} " +
                $"(kaomoji: {kaomoji}, {content.Length} bytes)");
        }
        catch (Exception ex)
        {
            MainFile.Logger?.Info($"[CharacterProfile] Error loading {id}: {ex.Message}");
        }
    }

    /// <summary>
    /// Extract display name from the first heading line.
    /// Skips HTML comment metadata lines (e.g. &lt;!-- kaomoji: tsundere --&gt;)
    /// to find the actual heading. E.g.:
    /// "# 月下妖精 / 德丽莎·月下初拥（Delilah）——..." → "德丽莎·月下初拥"
    /// </summary>
    private static string ExtractDisplayNameFromHeading(string content, string fallbackId)
    {
        // Scan the first 10 lines, skipping HTML comment metadata lines
        var allLines = content.Split('\n');
        int scanLimit = Math.Min(allLines.Length, 10);
        string? headingLine = null;
        for (int i = 0; i < scanLimit; i++)
        {
            var candidate = allLines[i].Trim();
            // Skip empty lines and HTML comment metadata lines
            if (string.IsNullOrEmpty(candidate)) continue;
            if (candidate.StartsWith("<!--") && candidate.EndsWith("-->")) continue;
            // Found the first non-metadata, non-empty line
            headingLine = candidate;
            break;
        }

        if (headingLine == null)
            return fallbackId;

        var line = headingLine.AsSpan().Trim();
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
            return namePart.ToString();

        return fallbackId;
    }

    /// <summary>
    /// Remove metadata comment lines (e.g. &lt;!-- kaomoji: tsundere --&gt;)
    /// from the top of the persona prompt. These comments are parsed into
    /// CharacterMeta fields and should not be sent to the AI as part of
    /// the character persona.
    /// </summary>
    private static string StripMetadataComments(string[] lines)
    {
        var result = new System.Text.StringBuilder();
        bool foundContent = false;

        foreach (var raw in lines)
        {
            var trimmed = raw.Trim();
            // Skip metadata HTML comments at the top
            if (!foundContent && (string.IsNullOrEmpty(trimmed)
                || (trimmed.StartsWith("<!--") && trimmed.EndsWith("-->"))))
                continue;

            foundContent = true;
            result.AppendLine(raw);
        }

        return result.ToString().TrimEnd('\n', '\r');
    }
}
