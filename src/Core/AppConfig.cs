using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TokenSpire2.Core;

/// <summary>
/// Unified application configuration.
///
/// Replaces: CoopManager.cs + BrokerModeSettings + BrokerClientConfig
///
/// Thread-safe: all public state reads use a reader-writer lock.
/// Loaded once at startup from coop_config.json and broker marker files.
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
    /// Reads coop_config.json and broker marker files.
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
    // Co-op / Role settings
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Whether we're in co-op multiplayer mode (from coop_config.json).</summary>
    public bool CoopMode { get; set; }

    /// <summary>
    /// Whether this instance is the networking HOST.
    /// Read from TOKENSPIRE2_ROLE env var. "client" → false, anything else → true (default).
    /// </summary>
    public bool IsHost { get; private set; } = true;

    /// <summary>Whether this instance is the networking CLIENT.</summary>
    public bool IsClient => !IsHost;

    /// <summary>Bot = client (auto-joins, auto-plays, auto-readies).</summary>
    public bool IsBot => IsClient;

    /// <summary>Human = host (creates room, chooses character manually).</summary>
    public bool IsHumanPlayer => IsHost;

    // ═══════════════════════════════════════════════════════════════
    // Auto-battle settings (from coop_config.json)
    // ═══════════════════════════════════════════════════════════════

    public bool AutoBattleEnabled { get; set; } = true;
    public bool AutoBattlePaused { get; set; }
    public int AutoBattleScope { get; set; } = 1; // 0=Combat, 1=Full
    public int BotPlayerSlot { get; set; }
    public bool AutoStartEnabled { get; set; } = true;

    // ═══════════════════════════════════════════════════════════════
    // Broker settings (from marker file)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Whether broker mode is enabled (marker file found).</summary>
    public bool BrokerEnabled { get; private set; }

    /// <summary>Broker host address.</summary>
    public string BrokerHost { get; private set; } = "127.0.0.1";

    /// <summary>Broker port.</summary>
    public int BrokerPort { get; private set; } = 9999;

    /// <summary>Client identifier for broker registration.</summary>
    public string ClientId { get; private set; } = "client-0";

    /// <summary>Client index (0 for host, 1 for client).</summary>
    public int ClientIndex { get; private set; }

    /// <summary>Session ID for broker routing.</summary>
    public string SessionId { get; private set; } = "";

    /// <summary>Path to event log file.</summary>
    public string EventLogPath { get; private set; } = "";

    // ═══════════════════════════════════════════════════════════════
    // Batch mode
    // ═══════════════════════════════════════════════════════════════

    public bool BatchMode { get; set; }
    public string? Seed { get; set; }
    public string Character { get; set; } = "IRONCLAD";

    // ═══════════════════════════════════════════════════════════════
    // Loading
    // ═══════════════════════════════════════════════════════════════

    private static AppConfig Load(string modDirectory)
    {
        var config = new AppConfig();

        // 1. Load coop_config.json
        var coopPath = Path.Combine(modDirectory, "coop_config.json");
        if (File.Exists(coopPath))
        {
            try
            {
                var json = File.ReadAllText(coopPath);
                var coop = JsonSerializer.Deserialize<CoopConfigFile>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (coop != null)
                {
                    config.CoopMode = coop.CoopMode;
                    config.AutoBattleEnabled = coop.AutoBattleEnabled;
                    config.AutoBattlePaused = coop.AutoBattlePaused;
                    config.AutoBattleScope = coop.AutoBattleScope;
                    config.BotPlayerSlot = coop.BotPlayerSlot;
                    config.AutoStartEnabled = coop.AutoStartEnabled;
                }
            }
            catch (Exception ex)
            {
                Log($"[AppConfig] Failed to load coop_config.json: {ex.Message}");
            }
        }

        // 2. Resolve role from TOKENSPIRE2_ROLE env var
        var role = Environment.GetEnvironmentVariable("TOKENSPIRE2_ROLE");
        config.IsHost = string.IsNullOrEmpty(role)
            || !role.Equals("client", StringComparison.OrdinalIgnoreCase);

        // 3. Set event log path (even without broker — needed for MpController diagnostics)
        var roleSuffix = config.IsHost ? "host" : "client";
        config.EventLogPath = Path.Combine(modDirectory, $"localcoop-{roleSuffix}-{config.ClientIndex}-events.txt");

        // 4. Load broker marker file (overrides EventLogPath if broker enabled)
        config.LoadBrokerMarker(modDirectory);

        // 5. Enforce CoopMode and AutoBattleEnabled for multiplayer modes.
        //    In broker mode: marker files are the source of truth.
        //    In SteamFix64/CoopMode: bot must always have auto-battle enabled,
        //    otherwise the turn never advances in multiplayer combat.
        if (config.BrokerEnabled || config.CoopMode)
        {
            if (!config.CoopMode)
            {
                Log("[AppConfig] WARNING: CoopMode was false but broker marker exists — forcing CoopMode=true.");
                config.CoopMode = true;
            }
            if (!config.AutoBattleEnabled && config.IsBot)
            {
                Log("[AppConfig] AutoBattleEnabled was false for bot — forcing AutoBattleEnabled=true.");
                config.AutoBattleEnabled = true;
            }
        }

        // 5. Load batch_config.json if present
        var batchPath = Path.Combine(modDirectory, "batch_config.json");
        if (File.Exists(batchPath))
        {
            try
            {
                var json = File.ReadAllText(batchPath);
                var batch = JsonSerializer.Deserialize<BatchConfigFile>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (batch != null)
                {
                    config.BatchMode = true;
                    config.Seed = batch.Seed;
                    config.Character = batch.Character ?? "IRONCLAD";
                }
            }
            catch { /* ignore batch config errors */ }
        }

        Log($"[AppConfig] Initialized. CoopMode={config.CoopMode}, " +
            $"IsHost={config.IsHost}, Broker={config.BrokerEnabled}, " +
            $"ClientId={config.ClientId}");

        return config;
    }

    private void LoadBrokerMarker(string modDirectory)
    {
        // Try per-instance marker file based on role
        var roleSuffix = IsHost ? "host" : "client";
        var markerPath = Path.Combine(modDirectory, $"enable-local-broker-{roleSuffix}.txt");

        if (!File.Exists(markerPath))
        {
            // Fallback: try shared marker
            markerPath = Path.Combine(modDirectory, "enable-local-broker.txt");
        }

        if (!File.Exists(markerPath))
        {
            BrokerEnabled = false;
            return;
        }

        try
        {
            var content = File.ReadAllText(markerPath).Trim();
            // Parse key=value lines
            foreach (var line in content.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim();
                var eqIdx = trimmed.IndexOf('=');
                if (eqIdx < 0) continue;
                var key = trimmed[..eqIdx].Trim();
                var value = trimmed[(eqIdx + 1)..].Trim();

                // ── Case-insensitive key matching ──────────────────
                // The launch script uses lowercase keys (clientIndex, sessionId,
                // endpoint). Match both to avoid silent parse failures.
                var keyLower = key.ToLowerInvariant();
                switch (keyLower)
                {
                    case "host":
                    case "endpoint":
                        var colonIdx = value.LastIndexOf(':');
                        if (colonIdx > 0)
                        {
                            BrokerHost = value[..colonIdx];
                            if (int.TryParse(value[(colonIdx + 1)..], out var port))
                                BrokerPort = port;
                        }
                        else
                        {
                            BrokerHost = value;
                        }
                        break;
                    case "port":
                        if (int.TryParse(value, out var p)) BrokerPort = p;
                        break;
                    case "clientindex":
                        if (int.TryParse(value, out var ci)) ClientIndex = ci;
                        break;
                    case "sessionid":
                        SessionId = value;
                        break;
                }
            }

            ClientId = $"client-{ClientIndex}";
            EventLogPath = Path.Combine(modDirectory, $"localcoop-{roleSuffix}-{ClientIndex}-events.txt");
            BrokerEnabled = true;
        }
        catch (Exception ex)
        {
            Log($"[AppConfig] Failed to load broker marker: {ex.Message}");
            BrokerEnabled = false;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Config persistence
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Save current config to coop_config.json.</summary>
    public void Save()
    {
        lock (_lock)
        {
            if (_modDirectory == null) return;
            try
            {
                // ── Broker mode: force critical fields to true ──────
                // When broker is enabled, CoopMode and AutoBattleEnabled
                // must always be true. Otherwise, the host instance skips
                // auto-battle and the turn never advances in multiplayer.
                if (BrokerEnabled)
                {
                    CoopMode = true;
                    // AutoBattleEnabled: respect user's preference.
                    // The bot has it forced on at Load() time;
                    // the host (human) can toggle freely via T-key.
                }

                var coop = new CoopConfigFile
                {
                    CoopMode = CoopMode,
                    AutoBattleEnabled = AutoBattleEnabled,
                    AutoBattlePaused = AutoBattlePaused,
                    AutoBattleScope = AutoBattleScope,
                    BotPlayerSlot = BotPlayerSlot,
                    AutoStartEnabled = AutoStartEnabled
                };
                var json = JsonSerializer.Serialize(coop, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(Path.Combine(_modDirectory, "coop_config.json"), json);
            }
            catch (Exception ex)
            {
                Log($"[AppConfig] Failed to save config: {ex.Message}");
            }
        }
    }

    /// <summary>Toggle auto-battle pause (T-key). Returns new paused state.</summary>
    public bool TogglePause()
    {
        AutoBattlePaused = !AutoBattlePaused;
        Save();
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

    private class CoopConfigFile
    {
        public bool AutoBattleEnabled { get; set; } = true;
        public bool AutoBattlePaused { get; set; }
        public int AutoBattleScope { get; set; } = 1;
        public bool CoopMode { get; set; }
        public int BotPlayerSlot { get; set; }
        public bool AutoStartEnabled { get; set; } = true;
    }

    private class BatchConfigFile
    {
        public string? Seed { get; set; }
        public string? Character { get; set; }
    }
}
