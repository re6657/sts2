using System;
using System.IO;
using System.Text.Json;

namespace TokenSpire2.Chat;

/// <summary>
/// Global AI chat configuration loaded from aichat_config.json.
/// Contains API key, model, endpoint, and generation parameters.
/// Singleton — loaded once at mod startup.
/// </summary>
public class AiChatConfig
{
    private static readonly object _lock = new();
    private static AiChatConfig? _instance;

    public static AiChatConfig Instance
    {
        get
        {
            if (_instance == null)
                throw new InvalidOperationException("AiChatConfig not initialized. Call Initialize(modDirectory) first.");
            return _instance;
        }
    }

    public static bool IsInitialized
    {
        get { lock (_lock) return _instance != null; }
    }

    // ── Config fields ──────────────────────────────────────────────

    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "deepseek-chat";
    public string BaseUrl { get; set; } = "https://api.deepseek.com/v1";
    public int IntervalSeconds { get; set; } = 5;
    public int MaxTokens { get; set; } = 50;
    public double Temperature { get; set; } = 0.9;

    // ── Initialization ─────────────────────────────────────────────

    public static void Initialize(string modDirectory)
    {
        lock (_lock)
        {
            if (_instance != null) return;
            _instance = Load(modDirectory);
        }
    }

    private static AiChatConfig Load(string modDirectory)
    {
        var config = new AiChatConfig();
        var path = Path.Combine(modDirectory, "aichat_config.json");

        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var loaded = JsonSerializer.Deserialize<AiChatConfig>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (loaded != null)
                {
                    config = loaded;
                }
            }
            else
            {
                // Write default config file for user to edit
                var defaultJson = JsonSerializer.Serialize(config,
                    new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(path, defaultJson);
            }
        }
        catch (Exception ex)
        {
            MainFile.Logger?.Info($"[AiChatConfig] Error loading: {ex.Message}");
        }

        MainFile.Logger?.Info($"[AiChatConfig] Initialized. Model={config.Model}, " +
            $"Interval={config.IntervalSeconds}s, MaxTokens={config.MaxTokens}");
        return config;
    }
}
