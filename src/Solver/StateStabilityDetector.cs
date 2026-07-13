using System;
using System.Collections.Generic;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Runs;
using TokenSpire2.Core;

namespace TokenSpire2.Solver;

/// <summary>
/// State stability detector — patterned after CommunicationMod's
/// GameStateListener.hasDungeonStateChanged().
///
/// Prevents decisions during game transitions, animations, and
/// intermediate states. Only allows decisions when the game
/// is in a "stable" state ready to accept input.
///
/// Key pattern from CommunicationMod: check multiple conditions
/// (action queues, screen transitions, combat phase, overlay changes)
/// before declaring state stable.
/// </summary>
public static class StateStabilityDetector
{
    // Previous frame state for change detection
    private static GameScreen _previousScreen = GameScreen.NONE;
    private static int _previousOverlayCount;
    private static string _previousOverlayType = "";
    private static int _previousGold = -1;
    private static bool _previousCombatInProgress;
    private static string _previousCombatPhase = "";
    private static int _previousFloor;

    // Stability tracking
    private static bool _waitOneUpdate;
    private static int _stabilityCounter; // frames since last state change
    private static int _stabilityRequired = 2; // frames of stability needed before decision

    // Block mechanism — like CommunicationMod's blockStateUpdate
    private static bool _blocked;
    private static string _blockReason = "";

    // Screen tracking
    private static string _lastOverlayType = "";
    private static double _sameOverlayDuration;
    private static int _sameOverlayDecisions;

    /// <summary>Reset all state trackers for a new run.</summary>
    public static void Reset()
    {
        _previousScreen = GameScreen.NONE;
        _previousOverlayCount = 0;
        _previousOverlayType = "";
        _previousGold = -1;
        _previousCombatInProgress = false;
        _previousCombatPhase = "";
        _previousFloor = 0;
        _waitOneUpdate = false;
        _stabilityCounter = 0;
        _blocked = false;
        _blockReason = "";
        _lastOverlayType = "";
        _sameOverlayDuration = 0;
        _sameOverlayDecisions = 0;
    }

    /// <summary>
    /// Block state updates during async operations.
    /// Pattern: CommunicationMod's blockStateUpdate() used during
    /// relic equipping, campfire effects, shop transitions.
    /// </summary>
    public static void Block(string reason)
    {
        _blocked = true;
        _blockReason = reason;
        MainFile.Logger.Info($"[StabilityDetector] BLOCKED: {reason}");
    }

    /// <summary>Resume state updates after async operation completes.</summary>
    public static void Resume()
    {
        if (_blocked)
        {
            MainFile.Logger.Info($"[StabilityDetector] RESUMED (was blocked: {_blockReason})");
            _blocked = false;
            _blockReason = "";
            _waitOneUpdate = true; // need one frame of transition after resume
        }
    }

    /// <summary>Check if currently blocked.</summary>
    public static bool IsBlocked => _blocked;

    /// <summary>
    /// Check if the game state is STABLE enough to make a decision.
    /// This is the main gate — call before any decision.
    ///
    /// Returns true only when:
    /// 1. Not blocked by an async operation
    /// 2. The screen hasn't just changed (transition complete)
    /// 3. Combat actions are complete (if in combat)
    /// 4. At least N frames of stability have passed
    /// </summary>
    public static bool IsStableForDecision(GameScreen currentScreen, double delta)
    {
        // ── 1. Block mechanism ──────────────────────────────────────────
        if (_blocked)
        {
            // Still increment counter so we can detect stuck blocks
            _stabilityCounter++;
            return false;
        }

        // ── 2. Wait-one-update after state change ───────────────────────
        if (_waitOneUpdate)
        {
            _waitOneUpdate = false;
            _stabilityCounter = 0;
            MainFile.Logger.Info("[StabilityDetector] Wait-one-update consumed");
            // Fall through — check other conditions this frame
        }

        // ── 3. Screen change detection ──────────────────────────────────
        int currentOverlayCount = NOverlayStack.Instance?.ScreenCount ?? 0;
        string currentOverlayType = "";
        if (currentOverlayCount > 0)
        {
            var overlay = NOverlayStack.Instance!.Peek();
            currentOverlayType = overlay?.GetType().Name ?? "";
        }

        bool screenChanged = currentScreen != _previousScreen
            || currentOverlayCount != _previousOverlayCount
            || currentOverlayType != _previousOverlayType;

        // Check gold change (auxiliary signal like CommunicationMod)
        int currentGold = GetGold();
        bool goldChanged = currentGold >= 0 && _previousGold >= 0 && currentGold != _previousGold;

        // Check combat transition
        bool combatInProgress = CombatManager.Instance?.IsInProgress == true;
        bool combatChanged = combatInProgress != _previousCombatInProgress;

        if (screenChanged || combatChanged)
        {
            _stabilityCounter = 0;
            MainFile.Logger.Info($"[StabilityDetector] State changed: screen={currentScreen}(was {_previousScreen}) " +
                $"overlay={currentOverlayType}(cnt {currentOverlayCount}) combat={combatInProgress}");
        }

        // ── 4. Combat stability check ──────────────────────────────────
        if (combatInProgress)
        {
            // In combat, check if any actions are still pending
            // CommunicationMod checks: actions.isEmpty() && preTurnActions.isEmpty()
            //     && cardQueue.isEmpty() && !isFadingOut
            try
            {
                var cm = CombatManager.Instance;
                if (cm != null)
                {
                    // Check if actions are pending
                    bool actionsEmpty = true; // default: assume stable
                    try
                    {
                        var actionManager = cm.GetType().GetProperty("ActionManager")?.GetValue(cm);
                        if (actionManager != null)
                        {
                            var actionsProp = actionManager.GetType().GetProperty("Actions");
                            var phaseProp = actionManager.GetType().GetProperty("Phase");
                            if (actionsProp != null)
                            {
                                var actions = actionsProp.GetValue(actionManager);
                                if (actions is System.Collections.ICollection col)
                                    actionsEmpty = col.Count == 0;
                            }
                            if (phaseProp != null)
                            {
                                var phase = phaseProp.GetValue(actionManager)?.ToString() ?? "";
                                if (phase != _previousCombatPhase)
                                {
                                    _previousCombatPhase = phase;
                                    _stabilityCounter = 0;
                                }
                                // "WAITING_ON_USER" is the stable phase (from CommunicationMod)
                                if (!phase.Contains("WAITING") && !phase.Contains("Waiting"))
                                    actionsEmpty = false;
                            }
                        }
                    }
                    // M2: reflection may fail during scene transitions — assume unstable rather than stable
                    catch
                    {
                        _stabilityCounter = 0;
                        return false;
                    }

                    if (!actionsEmpty)
                    {
                        _stabilityCounter = 0;
                        return false;
                    }
                }
            }
            catch { /* combat status check failed, assume stable */ }
        }

        // ── 5. Gold change detection ────────────────────────────────────
        if (goldChanged && _stabilityCounter < 2)
        {
            // Gold changed — might still be in transition
            _stabilityCounter = 0;
        }

        // ── 6. Accumulate stability ─────────────────────────────────────
        _stabilityCounter++;

        // Update previous state
        _previousScreen = currentScreen;
        _previousOverlayCount = currentOverlayCount;
        _previousOverlayType = currentOverlayType;
        _previousGold = currentGold;
        _previousCombatInProgress = combatInProgress;

        // ── 7. Overlay stuck tracking ───────────────────────────────────
        if (currentOverlayType == _lastOverlayType && currentOverlayCount > 0)
        {
            _sameOverlayDuration += delta;
            _sameOverlayDecisions++;
        }
        else
        {
            _lastOverlayType = currentOverlayType;
            _sameOverlayDuration = 0;
            _sameOverlayDecisions = 0;
        }

        // Check for stuck overlay (CommunicationMod's stuck timeout equivalent)
        if (_sameOverlayDuration > 120.0 && _sameOverlayDecisions > 30)
        {
            MainFile.Logger.Error($"[StabilityDetector] OVERLAY STUCK: {currentOverlayType} for {_sameOverlayDuration:F0}s, {_sameOverlayDecisions} decisions! Resetting tracking — not permanently blocking.");
            // Reset stuck tracking to give it another chance
            _lastOverlayType = "";
            _sameOverlayDuration = 0;
            _sameOverlayDecisions = 0;
            _stabilityCounter = 0;
            return false; // Skip this frame, let it recover next frame
        }

        bool isStable = _stabilityCounter >= _stabilityRequired;
        if (!isStable && _stabilityCounter == 1)
        {
            // Log once when we first start waiting for stability (not on every frame)
            MainFile.Logger.Info($"[StabilityDetector] Waiting for stability: screen={currentScreen} counter={_stabilityCounter}/{_stabilityRequired}");
        }
        return isStable;
    }

    /// <summary>
    /// Check if this is a screen transition that needs a wait.
    /// Pattern from CommunicationMod: after screen changes, wait one frame
    /// for the game to finish transitioning.
    /// </summary>
    public static bool IsScreenTransition(GameScreen currentScreen)
    {
        if (currentScreen != _previousScreen)
            return true;
        int currentOverlayCount = NOverlayStack.Instance?.ScreenCount ?? 0;
        if (currentOverlayCount != _previousOverlayCount)
            return true;
        return false;
    }

    /// <summary>Get the number of frames since last state change.</summary>
    public static int StabilityFrames => _stabilityCounter;

    /// <summary>Get the duration the same overlay has been active.</summary>
    public static double SameOverlayDuration => _sameOverlayDuration;

    /// <summary>Get the number of decisions made on the current overlay.</summary>
    public static int SameOverlayDecisions => _sameOverlayDecisions;

    /// <summary>Mark a decision was attempted on this frame.</summary>
    public static void MarkDecisionAttempted()
    {
        // Handled by SameOverlayDecisions tracking
    }

    private static int GetGold()
    {
        try
        {
            var rs = RunManager.Instance?.DebugOnlyGetState();
            if (rs == null) return -1;
            var player = MegaCrit.Sts2.Core.Context.LocalContext.GetMe(rs);
            if (player == null) return -1;
            return player.Gold;
        }
        catch { return -1; }
    }
}
