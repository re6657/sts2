using System;
using System.IO;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;

namespace TokenSpire2.Core;

/// <summary>
/// Detects when the auto-battle bot is stuck and needs recovery.
/// Extracted from AutoSlayNode stuck-detection logic (lines 318-408).
///
/// Three-tier detection:
///   1. Combat inactivity (45s default)
///   2. Same-screen timeout (45s non-combat, 90s combat-adjacent)
///   3. Per-frame tick threshold (prevents false positives from lag)
///
/// Co-op mode instances are NEVER killed — both must stay alive.
/// Human players are NEVER killed — only bot instances self-terminate.
/// </summary>
public class StuckDetector
{
    // ═══════════════════════════════════════════════════════════════
    // Configuration (overridable via AppConfig)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Seconds of combat inactivity before declaring stuck.</summary>
    public double CombatStuckTimeoutSeconds { get; set; } = 45.0;

    /// <summary>Seconds on same non-combat screen before declaring stuck.</summary>
    public double NonCombatStuckTimeoutSeconds { get; set; } = 45.0;

    /// <summary>Seconds on same combat-adjacent screen before declaring stuck.</summary>
    public double CombatAdjacentStuckTimeoutSeconds { get; set; } = 90.0;

    /// <summary>Minimum tick count before non-combat stuck can fire (prevents lag false positives).</summary>
    public int MinStuckTicks { get; set; } = 30;

    /// <summary>When true, the detector will NEVER kill the process (co-op mode).</summary>
    public bool NeverKill { get; set; }

    /// <summary>When true, the detector will NEVER kill the process (human player).</summary>
    public bool IsHumanPlayer { get; set; }

    // ═══════════════════════════════════════════════════════════════
    // State
    // ═══════════════════════════════════════════════════════════════

    private double _combatInactivityTimer = -1; // -1 = not in combat
    private double _sameScreenDuration;
    private int _sameScreenTickCount;
    private string _lastScreenId = "";
    private bool _activityOccurredThisFrame;

    // ═══════════════════════════════════════════════════════════════
    // Events
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Fired when stuck is detected. Handlers can attempt recovery before process kill.</summary>
    public event Action<StuckSeverity, string>? OnStuckDetected;

    /// <summary>Fired just before the process is killed (last chance to save state).</summary>
    public event Action<string>? OnBeforeKill;

    // ═══════════════════════════════════════════════════════════════
    // Public API
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Call every frame with the current screen and delta time.
    /// Returns a recovery action if stuck was detected.
    /// </summary>
    public StuckResult Update(double delta)
    {
        var screen = ScreenDetector.DetectRaw();
        bool inCombat = CombatManager.Instance?.IsInProgress == true;

        // ── Tier 1: Combat inactivity ──────────────────────────
        if (inCombat)
        {
            if (_combatInactivityTimer < 0) _combatInactivityTimer = 0;

            if (true) // single-player: always accumulate timer
            {
                if (_activityOccurredThisFrame)
                    _combatInactivityTimer = 0;
                else
                    _combatInactivityTimer += delta;
            }

            if (!ShouldSkipKill() && _combatInactivityTimer > CombatStuckTimeoutSeconds)
            {
                var msg = $"Combat inactive for {_combatInactivityTimer:F0}s";
                OnStuckDetected?.Invoke(StuckSeverity.Critical, msg);
                OnBeforeKill?.Invoke(msg);
                return StuckResult.KillProcess;
            }
        }
        else
        {
            _combatInactivityTimer = -1; // reset
        }

        // ── Tier 2: Same-screen timeout ────────────────────────
        string screenId = screen.ToString();
        try
        {
            if (NOverlayStack.Instance?.ScreenCount > 0)
            {
                var top = NOverlayStack.Instance.Peek();
                screenId += "/" + (top?.GetType().Name ?? "?");
            }
        }
        catch { screenId += "/DETECT_FAILED"; }

        if (screenId == _lastScreenId && screenId != "NONE")
        {
            _sameScreenDuration += delta;
            _sameScreenTickCount++;
        }
        else
        {
            _lastScreenId = screenId;
            _sameScreenDuration = 0;
            _sameScreenTickCount = 0;
        }

        double effectiveTimeout = _lastScreenId?.StartsWith("COMBAT") == true
            ? CombatAdjacentStuckTimeoutSeconds
            : NonCombatStuckTimeoutSeconds;

        if (!ShouldSkipKill()
            && _sameScreenDuration > effectiveTimeout
            && _sameScreenTickCount > MinStuckTicks)
        {
            var msg = $"Screen stuck: {_lastScreenId} for {_sameScreenDuration:F0}s ({_sameScreenTickCount} ticks)";
            OnStuckDetected?.Invoke(StuckSeverity.Critical, msg);
            OnBeforeKill?.Invoke(msg);
            return StuckResult.KillProcess;
        }

        // ── Warning threshold (50% of timeout) ─────────────────
        if (_sameScreenDuration > effectiveTimeout * 0.5 && _sameScreenTickCount > MinStuckTicks / 2)
        {
            OnStuckDetected?.Invoke(StuckSeverity.Warning,
                $"Screen may be stuck: {_lastScreenId} for {_sameScreenDuration:F0}s");
        }

        // Reset per-frame flag
        _activityOccurredThisFrame = false;

        return StuckResult.None;
    }

    /// <summary>
    /// Call this whenever the bot successfully performs an action
    /// (plays a card, clicks a button, ends turn, etc.).
    /// Resets the combat inactivity timer.
    /// </summary>
    public void MarkActivity()
    {
        _activityOccurredThisFrame = true;
    }

    /// <summary>Reset all timers (e.g. after a scene transition).</summary>
    public void Reset()
    {
        _combatInactivityTimer = -1;
        _sameScreenDuration = 0;
        _sameScreenTickCount = 0;
        _lastScreenId = "";
        _activityOccurredThisFrame = false;
    }

    /// <summary>Write diagnostics to the mod directory for post-mortem analysis.</summary>
    public void WriteDiagnostics(string reason, double duration)
    {
        try
        {
            if (!AppConfig.IsInitialized) return;
            var path = Path.Combine(AppConfig.ModDirectory, "stuck_diagnostics.txt");
            var lines = new[]
            {
                $"=== Stuck Diagnostics ===",
                $"Time: {DateTime.UtcNow:O}",
                $"Reason: {reason}",
                $"Duration: {duration:F1}s",
                $"LastScreen: {_lastScreenId}",
                $"ScreenTicks: {_sameScreenTickCount}",
                $"CombatInactivity: {_combatInactivityTimer:F1}s",
                $"InCombat: {CombatManager.Instance?.IsInProgress}",
                $"CoopMode: false (single-player)",
                $"AutoBattleEnabled: {(AppConfig.IsInitialized ? AppConfig.Instance.AutoBattleEnabled : "unknown")}",
                $"AutoBattlePaused: {(AppConfig.IsInitialized ? AppConfig.Instance.AutoBattlePaused : "unknown")}",
                $"",
            };
            File.AppendAllLines(path, lines);
        }
        catch { /* best-effort diagnostics */ }
    }

    // ═══════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════

    private bool ShouldSkipKill()
    {
        // Never kill human player instances
        if (IsHumanPlayer) return true;
        // Never kill in co-op mode (both instances must stay alive)
        if (NeverKill) return true;
        // Single-player: never skip kill
        return false;
    }
}

// ═══════════════════════════════════════════════════════════════
// Supporting types
// ═══════════════════════════════════════════════════════════════

public enum StuckSeverity
{
    /// <summary>No issue.</summary>
    None,
    /// <summary>Screen has been stable longer than expected — log but don't act.</summary>
    Warning,
    /// <summary>Timeout exceeded — recovery required.</summary>
    Critical,
}

public enum StuckResult
{
    /// <summary>No action needed.</summary>
    None,
    /// <summary>Process should be killed (batch mode or unrecoverable).</summary>
    KillProcess,
}
