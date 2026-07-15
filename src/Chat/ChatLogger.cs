using System;
using System.IO;
using System.Linq;

namespace TokenSpire2.Chat;

/// <summary>
/// Appends AI chat messages to bot-count-specific log files for player review.
///
/// Three separate log files:
///   ai_chat_1bot.log  — solo bot chatting to itself
///   ai_chat_2bot.log  — two bots talking to each other
///   ai_chat_3bot.log  — three bots in a group chat
///
/// Log format:
///   [HH:mm:ss] CharacterName | message text
///   === SESSION START [Run #N] ===
///   --- SESSION END ---
///
/// Bot roster: each bot touches a marker file in .bot_roster/ on startup.
/// Counting those files gives the active bot count.
///
/// Thread-safe and process-safe: uses FileStream with
/// FileShare.ReadWrite to allow multiple game instances to write
/// to the same log concurrently.
/// </summary>
public static class ChatLogger
{
    private static string? _modDirectory;
    private static readonly object _lock = new();
    private static int _runNumber;

    /// <summary>
    /// Initialize the logger. Call once after AppConfig is ready.
    /// Cleans up stale bot roster files from previous sessions.
    /// </summary>
    public static void Initialize(string modDirectory)
    {
        lock (_lock)
        {
            _modDirectory = modDirectory;
            // Clean up stale roster so bot count is accurate for this session
            try
            {
                var rosterDir = Path.Combine(modDirectory, ".bot_roster");
                if (Directory.Exists(rosterDir))
                {
                    foreach (var f in Directory.GetFiles(rosterDir, "*.txt"))
                        File.Delete(f);
                }
            }
            catch { /* best effort */ }
        }
    }

    /// <summary>Register this bot in the roster and return the total active bot count.</summary>
    public static int RegisterBot(string characterName)
    {
        if (_modDirectory == null) return 1;
        try
        {
            var rosterDir = Path.Combine(_modDirectory, ".bot_roster");
            Directory.CreateDirectory(rosterDir);
            // Each bot writes a marker file named after its character.
            // Same character name → same file (overwrite), so duplicates don't inflate count.
            var markerPath = Path.Combine(rosterDir, $"{SanitizeFileName(characterName)}.txt");
            File.WriteAllText(markerPath, DateTime.Now.ToString("O"));
            // Count unique bots
            return Directory.GetFiles(rosterDir, "*.txt").Length;
        }
        catch { return 1; }
    }

    /// <summary>Get current bot count from the roster (without re-registering).</summary>
    public static int GetBotCount()
    {
        if (_modDirectory == null) return 1;
        try
        {
            var rosterDir = Path.Combine(_modDirectory, ".bot_roster");
            if (!Directory.Exists(rosterDir)) return 1;
            return Directory.GetFiles(rosterDir, "*.txt").Length;
        }
        catch { return 1; }
    }

    private static string GetLogPath(int botCount)
    {
        if (_modDirectory == null) return "";
        var fileName = botCount switch
        {
            2 => "ai_chat_2bot.log",
            3 => "ai_chat_3bot.log",
            _ => "ai_chat_1bot.log",
        };
        return Path.Combine(_modDirectory, fileName);
    }

    /// <summary>
    /// Mark the start of a new game session in all relevant logs.
    /// Call when combat begins (first combat = new run).
    /// </summary>
    public static void MarkSessionStart()
    {
        if (_modDirectory == null) return;
        _runNumber++;

        var botCounts = new[] { 1, 2, 3 };
        foreach (var bc in botCounts)
        {
            try
            {
                var path = GetLogPath(bc);
                var marker = $"{Environment.NewLine}=== SESSION START [Run #{_runNumber}] {DateTime.Now:yyyy-MM-dd HH:mm:ss} ==={Environment.NewLine}";
                using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using var sw = new StreamWriter(fs);
                sw.Write(marker);
            }
            catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Mark the end of a game session.
    /// </summary>
    public static void MarkSessionEnd()
    {
        if (_modDirectory == null) return;

        var botCounts = new[] { 1, 2, 3 };
        foreach (var bc in botCounts)
        {
            try
            {
                var path = GetLogPath(bc);
                var marker = $"--- SESSION END [Run #{_runNumber}] {DateTime.Now:yyyy-MM-dd HH:mm:ss} ---{Environment.NewLine}";
                using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using var sw = new StreamWriter(fs);
                sw.Write(marker);
            }
            catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Append a chat message to the bot-count-appropriate history log.
    /// Safe to call from any thread or process.
    /// </summary>
    /// <param name="characterName">Display name of the character persona</param>
    /// <param name="message">The chat message text</param>
    /// <param name="botCount">Number of active bots (1/2/3). If 0, auto-detects from roster.</param>
    public static void Log(string characterName, string message, int botCount = 0)
    {
        if (string.IsNullOrEmpty(_modDirectory)) return;
        if (string.IsNullOrEmpty(message)) return;

        if (botCount <= 0)
            botCount = GetBotCount();

        var path = GetLogPath(botCount);
        if (string.IsNullOrEmpty(path)) return;

        string timestamp = DateTime.Now.ToString("HH:mm:ss");
        string line = $"[{timestamp}] {characterName} | {message}{Environment.NewLine}";

        try
        {
            // Use FileStream with FileShare.ReadWrite to allow multiple instances to write.
            using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            using var sw = new StreamWriter(fs);
            sw.Write(line);
        }
        catch
        {
            // Best effort — never crash the game for logging failure
        }
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }
}
