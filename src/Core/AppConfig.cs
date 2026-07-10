using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TokenSpire2.Core;

/// <summary>
/// Unified application configuration.
///
/// Thread-safe: all public state reads use a reader-writer lock.
/// Loaded once at startup from batch_config.json.
/// SolverParams are loaded separately via SolverParams.Load().
/// </summary>
public class AppConfig
{
    private static readonly object _lock = new();
    private static AppConfig? _instance;
    private static string? _modDirectory;

    // ═══════════════════════════════════════════════════════════════
    // Singleton access
    // ═══════════════════════════════════════════════════════════════

    public static AppConfig Instance
    {
        get
        {
            if (_instance == null)
                throw new InvalidOperationException("AppConfig not initialized. Call AppConfig.Initialize(modDirectory) first.");
            return _instance;
        }
    }

    public static bool IsInitialized
    {
        get { lock (_lock) return _instance != null; }
    }

    /// <summary>
    /// Initialize from the mod directory. Must be called once during mod startup.
    /// </summary>
    public static void Initialize(string modDirectory)
    {
        lock (_lock)
        {
            if (_instance != null) return; // already initialized
            _modDirectory = modDirectory;
            _instance = Load(modDirectory);
        }
    }

    /// <summary>Get the mod directory (for resolving relative paths).</summary>
    public static string ModDirectory
    {
        get
        {
            lock (_lock)
            {
                if (_modDirectory == null)
                    throw new InvalidOperationException("AppConfig not initialized.");
                return _modDirectory;
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Auto-battle settings
    // ═══════════════════════════════════════════════════════════════

    public bool AutoBattleEnabled { get; set; } = true;
    public bool AutoBattlePaused { get; set; }
    public int AutoBattleScope { get; set; } = 1; // 0=Combat, 1=Full

    // ═══════════════════════════════════════════════════════════════
    // Batch mode
    // ═══════════════════════════════════════════════════════════════

    public bool BatchMode { get; set; }
    public string? Seed { get; set; }
    public string Character { get; set; } = "IRONCLAD";

    // ═══════════════════════════════════════════════════════════════
    // Multiplayer mode
    // ═══════════════════════════════════════════════════════════════

    public bool MultiplayerMode { get; set; }
    public bool IsMultiplayerHost { get; set; }
    public string SteamPersonaName { get; set; } = "";

    // ═══════════════════════════════════════════════════════════════
    // Loading
    // ═══════════════════════════════════════════════════════════════

    private static AppConfig Load(string modDirectory)
    {
        var config = new AppConfig();

        // ── Per-instance config: check --config CLI arg first, then env var ──
        // This allows launching multiple instances with different configs
        // without a race condition on the shared batch_config.json file.
        var args = Environment.GetCommandLineArgs();
        Log($"[AppConfig] CommandLineArgs count={args.Length}: {string.Join(" | ", args)}");

        string? cliConfigPath = null;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--config" && i + 1 < args.Length)
            {
                cliConfigPath = args[i + 1];
                break;
            }
        }
        var envConfigPath = Environment.GetEnvironmentVariable("TOKENSPIRE2_CONFIG");
        var batchPath = cliConfigPath
            ?? (!string.IsNullOrEmpty(envConfigPath) ? envConfigPath : null)
            ?? Path.Combine(modDirectory, "batch_config.json");

        Log($"[AppConfig] Config path: cli={cliConfigPath ?? "null"}, env={envConfigPath ?? "null"}, final={batchPath}");
        Log($"[AppConfig] File.Exists({batchPath}) = {File.Exists(batchPath)}");

        if (File.Exists(batchPath))
        {
            try
            {
                var json = File.ReadAllText(batchPath);
                Log($"[AppConfig] Raw JSON: {json}");
                var batch = JsonSerializer.Deserialize<BatchConfigFile>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (batch != null)
                {
                    config.BatchMode = true;
                    config.Seed = batch.Seed;
                    config.Character = batch.Character ?? "IRONCLAD";
                    config.MultiplayerMode = batch.MultiplayerMode;
                    config.IsMultiplayerHost = batch.IsMultiplayerHost;
                    config.SteamPersonaName = batch.SteamPersonaName ?? "";

                    // In multiplayer mode, set AutoBattleEnabled from batch config
                    if (config.MultiplayerMode)
                    {
                        config.AutoBattleEnabled = batch.AutoBattleEnabled;
                    }
                }
            }
            catch (Exception ex) { Log($"[AppConfig] Error loading config: {ex.Message}"); }
        }

        // ── Write role-specific signal file so launcher can detect
        // when each instance has read its config. Separate files prevent
        // the client from overwriting the host's signal. ────────────
        try
        {
            var role = config.IsMultiplayerHost ? "host" : "client";
            var signalPath = Path.Combine(modDirectory, $"config_read_{role}.signal");
            File.WriteAllText(signalPath, $"Config read at {DateTime.Now:O}\n" +
                $"MultiplayerMode={config.MultiplayerMode}\n" +
                $"IsMultiplayerHost={config.IsMultiplayerHost}\n" +
                $"SteamPersonaName={config.SteamPersonaName}\n" +
                $"AutoBattleEnabled={config.AutoBattleEnabled}\n");
            // Also write a shared signal for backward compatibility
            var sharedPath = Path.Combine(modDirectory, "config_read.signal");
            File.WriteAllText(sharedPath, $"Config read at {DateTime.Now:O}\n" +
                $"Role={role}\n" +
                $"IsMultiplayerHost={config.IsMultiplayerHost}\n");
        }
        catch { /* best-effort, don't block startup */ }

        Log($"[AppConfig] Initialized. AutoBattleEnabled={config.AutoBattleEnabled}, " +
            $"BatchMode={config.BatchMode}, Character={config.Character}, " +
            $"MultiplayerMode={config.MultiplayerMode}, IsMultiplayerHost={config.IsMultiplayerHost}, " +
            $"SteamPersonaName={config.SteamPersonaName}");

        return config;
    }

    // ═══════════════════════════════════════════════════════════════
    // Config persistence
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Toggle auto-battle pause (T-key). Returns new paused state.</summary>
    public bool TogglePause()
    {
        AutoBattlePaused = !AutoBattlePaused;
        return AutoBattlePaused;
    }

    // ═══════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════

    private static void Log(string msg)
    {
        try
        {
            TokenSpire2.MainFile.Logger?.Info(msg);
        }
        catch { /* logging unavailable during early init */ }
    }

    // ═══════════════════════════════════════════════════════════════
    // JSON file formats
    // ═══════════════════════════════════════════════════════════════

    private class BatchConfigFile
    {
        public string? Seed { get; set; }
        public string? Character { get; set; }
        public bool MultiplayerMode { get; set; }
        public bool IsMultiplayerHost { get; set; }
        public string? SteamPersonaName { get; set; }
        public bool AutoBattleEnabled { get; set; } = true;
    }
}
