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
/// Thread-safe and process-safe: uses FileStream with
/// FileShare.ReadWrite to allow multiple game instances to write
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
            // H24: File.AppendAllText uses FileShare.Read which blocks concurrent writes.
            // Use FileStream with FileShare.ReadWrite to allow multiple instances to write.
            using var fs = new FileStream(_logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            using var sw = new StreamWriter(fs);
            sw.Write(line);
        }
        catch
        {
            // Best effort — never crash the game for logging failure
        }
    }
}
