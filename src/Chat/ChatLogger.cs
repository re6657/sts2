using System;
using System.IO;

namespace TokenSpire2.Chat;

/// <summary>
/// Appends all AI chat messages to a single log file for player review.
///
/// Log format:
///   [HH:mm:ss] CharacterName | message text
///
/// File location: {modDir}/ai_chat_history.log
///
/// Thread-safe and process-safe: uses File.AppendAllText with
/// FileShare.Read to allow multiple game instances to write
/// to the same log concurrently.
/// </summary>
public static class ChatLogger
{
    private static string? _logPath;
    private static readonly object _lock = new();

    /// <summary>
    /// Initialize the logger. Call once after AppConfig is ready.
    /// </summary>
    public static void Initialize(string modDirectory)
    {
        lock (_lock)
        {
            _logPath = Path.Combine(modDirectory, "ai_chat_history.log");
        }
    }

    /// <summary>
    /// Append a chat message to the history log.
    /// Safe to call from any thread or process.
    /// </summary>
    /// <param name="characterName">Display name of the character persona (e.g. "德丽莎·月下初拥")</param>
    /// <param name="message">The chat message text</param>
    public static void Log(string characterName, string message)
    {
        if (string.IsNullOrEmpty(_logPath)) return;
        if (string.IsNullOrEmpty(message)) return;

        string timestamp = DateTime.Now.ToString("HH:mm:ss");
        string line = $"[{timestamp}] {characterName} | {message}{Environment.NewLine}";

        try
        {
            // File.AppendAllText opens with FileShare.Read, allowing
            // concurrent writes from multiple processes on Windows.
            // Short lines make interleaving at the byte level unlikely,
            // and if it does happen it's cosmetic only.
            File.AppendAllText(_logPath, line);
        }
        catch
        {
            // Best effort — never crash the game for logging failure
        }
    }
}
