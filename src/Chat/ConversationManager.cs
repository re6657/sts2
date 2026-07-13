using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace TokenSpire2.Chat;

/// <summary>
/// Thread-safe shared conversation log for multi-bot AI chat.
///
/// All bot processes read/write the same file (.ai_chat_conversation.txt)
/// in the mod directory. Each line records one message:
///   德丽莎: 那个精英怪好恶心……
///   希儿: 大哥哥小心！
///
/// This enables:
///   1. Turn-taking: bots know who spoke last, avoid double-talking
///   2. Context: each bot sees recent history before generating
///   3. Debugging: human-readable log of the full conversation
/// </summary>
public static class ConversationManager
{
    private static string? _logPath;
    private static readonly object _lock = new();
    private const int MaxLines = 30; // keep last 30 lines

    /// <summary>Call once on startup to set the log file path.</summary>
    public static void Initialize(string modDirectory)
    {
        _logPath = Path.Combine(modDirectory, ".ai_chat_conversation.txt");
    }

    /// <summary>
    /// Append a message to the shared conversation log.
    /// Thread-safe — uses file lock to prevent torn writes across processes.
    /// </summary>
    public static void Append(string characterName, string text)
    {
        if (_logPath == null) return;
        if (string.IsNullOrWhiteSpace(characterName) || string.IsNullOrWhiteSpace(text)) return;

        var line = $"{characterName}: {text}";

        lock (_lock)
        {
            try
            {
                // Read existing lines
                var lines = new List<string>();
                if (File.Exists(_logPath))
                    lines.AddRange(File.ReadAllLines(_logPath));

                // Append new line
                lines.Add(line);

                // Trim to max lines
                while (lines.Count > MaxLines)
                    lines.RemoveAt(0);

                // Write atomically (write to temp, then rename)
                var tmpPath = _logPath + ".tmp";
                File.WriteAllLines(tmpPath, lines);
                File.Move(tmpPath, _logPath, overwrite: true);
            }
            catch
            {
                // Best-effort: direct write as fallback
                try { File.AppendAllText(_logPath, line + "\n"); }
                catch { /* give up */ }
            }
        }
    }

    /// <summary>Get the last N messages from the shared log.</summary>
    public static List<(string Character, string Text)> GetRecent(int count = 8)
    {
        var result = new List<(string, string)>();
        if (_logPath == null || !File.Exists(_logPath)) return result;

        try
        {
            var lines = File.ReadAllLines(_logPath);
            int start = Math.Max(0, lines.Length - count);
            for (int i = start; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (string.IsNullOrEmpty(line)) continue;

                var colonIdx = line.IndexOf(':');
                if (colonIdx > 0 && colonIdx < line.Length - 2)
                {
                    var name = line[..colonIdx].Trim();
                    var text = line[(colonIdx + 1)..].Trim();
                    if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(text))
                        result.Add((name, text));
                }
            }
        }
        catch { /* best effort */ }

        return result;
    }

    /// <summary>Get the name of the character who spoke last, or null.</summary>
    public static string? GetLastSpeaker()
    {
        var recent = GetRecent(1);
        return recent.Count > 0 ? recent[0].Character : null;
    }

    /// <summary>
    /// Build a conversation context string suitable for the AI prompt.
    /// Returns empty string if no recent conversation.
    /// </summary>
    public static string BuildContextString(int count = 6)
    {
        var recent = GetRecent(count);
        if (recent.Count == 0) return "";

        var lines = recent.Select(r => $"{r.Character}: {r.Text}");
        return "【最近对话】\n" + string.Join("\n", lines);
    }

    /// <summary>Clear the conversation log (e.g., on run end).</summary>
    public static void Clear()
    {
        if (_logPath == null) return;
        try { File.WriteAllText(_logPath, ""); }
        catch { /* best effort */ }
    }

    /// <summary>Check if a given character already spoke in the last N messages.</summary>
    public static bool SpokeRecently(string characterName, int window = 1)
    {
        var recent = GetRecent(window);
        return recent.Any(r =>
            string.Equals(r.Character, characterName, StringComparison.OrdinalIgnoreCase));
    }
}
