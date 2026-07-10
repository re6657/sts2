using System;
using TokenSpire2.Core;

namespace TokenSpire2.Coop;

/// <summary>
/// Backward-compatibility shim for legacy code that references CoopManager.
///
/// All state is delegated to <see cref="AppConfig.Instance"/>.
/// New code should use AppConfig directly. This class exists only so
/// AutoSlayNode.cs and other legacy files don't break during migration.
///
/// Thread-safe: delegates to AppConfig which has its own locking.
/// </summary>
public static class CoopManager
{
    private static bool _initialized;

    // ═══════════════════════════════════════════════════════════════
    // Config (delegates to AppConfig)
    // ═══════════════════════════════════════════════════════════════

    public static CoopConfig Config => new()
    {
        AutoBattleEnabled = AppConfig.Instance.AutoBattleEnabled,
        AutoBattlePaused = AppConfig.Instance.AutoBattlePaused,
        AutoBattleScope = (AutoBattleScope)AppConfig.Instance.AutoBattleScope,
        CoopMode = AppConfig.Instance.CoopMode,
        BotPlayerSlot = AppConfig.Instance.BotPlayerSlot,
        AutoStartEnabled = AppConfig.Instance.AutoStartEnabled,
    };

    public static string ModDirectory => AppConfig.ModDirectory;

    public static bool IsAutoBattleActive =>
        AppConfig.Instance.AutoBattleEnabled && !AppConfig.Instance.AutoBattlePaused;

    public static bool IsCoopMode => AppConfig.Instance.CoopMode;

    public static bool IsHost => AppConfig.Instance.IsHost;

    public static bool IsClient => AppConfig.Instance.IsClient;

    public static bool IsBot => AppConfig.Instance.IsBot;

    public static bool IsHumanPlayer => AppConfig.Instance.IsHumanPlayer;

    public static int BotPlayerSlot => AppConfig.Instance.BotPlayerSlot;

    // ═══════════════════════════════════════════════════════════════
    // Init (compat — actual loading was done by AppConfig)
    // ═══════════════════════════════════════════════════════════════

    public static void Initialize(string modDirectory)
    {
        if (_initialized) return;
        _initialized = true;

        // AppConfig.Initialize() was already called by MainFile.
        // If not (e.g. legacy call path), ensure it's initialized.
        if (!AppConfig.IsInitialized)
            AppConfig.Initialize(modDirectory);

        Log($"[CoopManager] Compat shim initialized. CoopMode={IsCoopMode}, " +
            $"IsHost={IsHost}, BotSlot={BotPlayerSlot}");
    }

    // ═══════════════════════════════════════════════════════════════
    // Setters (delegate to AppConfig + persist)
    // ═══════════════════════════════════════════════════════════════

    public static bool TogglePause()
    {
        return AppConfig.Instance.TogglePause();
    }

    public static void SetAutoBattleEnabled(bool enabled)
    {
        AppConfig.Instance.AutoBattleEnabled = enabled;
        AppConfig.Instance.AutoBattlePaused = false;
        AppConfig.Instance.Save();
    }

    public static void SetAutoBattleScope(AutoBattleScope scope)
    {
        AppConfig.Instance.AutoBattleScope = (int)scope;
        AppConfig.Instance.Save();
    }

    public static void SetCoopMode(bool enabled)
    {
        AppConfig.Instance.CoopMode = enabled;
        AppConfig.Instance.Save();
    }

    public static void SetBotPlayerSlot(int slot)
    {
        AppConfig.Instance.BotPlayerSlot = Math.Clamp(slot, 0, 3);
        AppConfig.Instance.Save();
    }

    public static bool ShouldAutoHandle(AutoBattleScope scope)
    {
        if (!AppConfig.Instance.AutoBattleEnabled || AppConfig.Instance.AutoBattlePaused)
            return false;
        return AppConfig.Instance.AutoBattleScope == (int)AutoBattleScope.Full
            || AppConfig.Instance.AutoBattleScope == (int)scope;
    }

    public static void SaveConfig()
    {
        AppConfig.Instance.Save();
    }

    // ═══════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════

    private static void Log(string msg)
    {
        try { MainFile.Logger?.Info(msg); }
        catch { /* logging unavailable */ }
    }
}

// ═══════════════════════════════════════════════════════════════
// Legacy types (unchanged for backward compatibility)
// ═══════════════════════════════════════════════════════════════

/// <summary>Configuration for co-op mode and auto-battle.</summary>
public class CoopConfig
{
    public bool AutoBattleEnabled { get; set; } = true;
    public bool AutoBattlePaused { get; set; }
    public AutoBattleScope AutoBattleScope { get; set; } = AutoBattleScope.Full;
    public bool CoopMode { get; set; }
    public int BotPlayerSlot { get; set; }
    public bool AutoStartEnabled { get; set; } = true;
}

/// <summary>What parts of the game the auto-battle system controls.</summary>
public enum AutoBattleScope
{
    Combat = 0,
    Full = 1
}
