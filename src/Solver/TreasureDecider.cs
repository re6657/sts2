using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Nodes.Screens.TreasureRoomRelic;

using TokenSpire2.Core;

namespace TokenSpire2.Solver;

/// <summary>
/// Treasure room: open chest, pick up relic, proceed.
/// Uses a state machine with per-relic retry tracking to prevent
/// infinite clicking of the same relic that never gets picked up.
/// Overlay dispatch (AutoSlayNode) handles NChooseARelicSelection separately.
/// </summary>
public static class TreasureDecider
{
    private enum State { Idle, ChestOpened, Picking }
    private static State _state = State.Idle;
    private static int _stuckFrames;
    private const int STUCK_TIMEOUT = 300; // 5 seconds at 60fps
    private const int MAX_CLICKS_PER_RELIC = 3; // max attempts per relic before skipping

    // Track which relics have been clicked (by node path) and how many times.
    // This prevents the infinite loop where the same relic is clicked forever
    // because it never gets disabled/removed after ForceClick().
    private static readonly Dictionary<string, int> _relicClickCounts = new();
    private static int _totalRelicClicks; // global counter to eventually force-proceed

    public static void Reset()
    {
        _state = State.Idle;
        _stuckFrames = 0;
        _relicClickCounts.Clear();
        _totalRelicClicks = 0;
    }

    public static void Decide()
    {
        var room = GetTreasureRoom();
        if (room == null) return;

        // ── Guard: if a relic selection overlay is active, let the
        // overlay dispatcher (RelicDecider) handle it. Don't interact
        // with the room while the overlay is showing. ──
        if (NOverlayStack.Instance?.Peek() is NChooseARelicSelection)
            return;

        _stuckFrames++;

        // ── Step 1: Open chest ──────────────────────────────────────────
        if (_state == State.Idle)
        {
            var chest = room.GetNodeOrNull<NClickableControl>("Chest");
            if (chest != null && GodotObject.IsInstanceValid(chest) && chest.IsEnabled)
            {
                MainFile.Logger.Info("[TreasureDecider] Opening chest");
                chest.ForceClick();
                _state = State.ChestOpened;
                _stuckFrames = 0;
                DecisionLogger.LogDecision(GameScreen.TREASURE, "TreasureOpen",
                    new List<DecisionLogger.OptionScore>(), 0, "OPEN_CHEST",
                    "Opening treasure chest");
            }
            return;
        }

        // ── Step 2: Pick up relic(s) from the room ─────────────────────
        if (_state == State.ChestOpened || _state == State.Picking)
        {
            // Get all valid relic holders
            var allRelics = AutoSlayHelpers.FindAll<NTreasureRoomRelicHolder>(room)
                .Where(r => GodotObject.IsInstanceValid(r) && r.IsEnabled && r.Visible)
                .ToList();

            // Fallback: include disabled/invisible relics if stuck
            if (allRelics.Count == 0 && _stuckFrames > 60)
            {
                allRelics = AutoSlayHelpers.FindAll<NTreasureRoomRelicHolder>(room)
                    .Where(r => GodotObject.IsInstanceValid(r))
                    .ToList();
            }

            // Filter out relics we've already clicked too many times
            var unexhaustedRelics = allRelics
                .Where(r =>
                {
                    var path = r.GetPath().ToString();
                    var clicks = _relicClickCounts.GetValueOrDefault(path, 0);
                    return clicks < MAX_CLICKS_PER_RELIC;
                })
                .ToList();

            if (unexhaustedRelics.Count > 0)
            {
                try
                {
                    var relic = unexhaustedRelics[0];
                    var path = relic.GetPath().ToString();
                    var prevClicks = _relicClickCounts.GetValueOrDefault(path, 0);
                    var newClicks = prevClicks + 1;

                    MainFile.Logger.Info($"[TreasureDecider] Picking up relic ({allRelics.Count} visible, {unexhaustedRelics.Count} unexhausted, click #{newClicks} on {relic.Name}, stuckFrames={_stuckFrames})");
                    relic.ForceClick();
                    _state = State.Picking;

                    // Track this click — do NOT reset _stuckFrames so timeout can still fire
                    _relicClickCounts[path] = newClicks;
                    _totalRelicClicks++;

                    DecisionLogger.LogDecision(GameScreen.TREASURE, "TreasurePickup",
                        new List<DecisionLogger.OptionScore>(), 0, "TAKE_RELIC",
                        $"Taking treasure relic ({allRelics.Count} visible, click #{newClicks}/{MAX_CLICKS_PER_RELIC})");
                }
                catch (Exception ex)
                {
                    MainFile.Logger.Error($"[TreasureDecider] Relic click failed: {ex.Message}");
                }
                return;
            }

            // All relics have been clicked enough times — force proceed
            if (allRelics.Count > 0 && unexhaustedRelics.Count == 0)
            {
                MainFile.Logger.Warn($"[TreasureDecider] All {allRelics.Count} relics exhausted ({_totalRelicClicks} total clicks) — falling through to Proceed");
            }
        }

        // ── Step 3: Proceed (when relics collected or none remain) ─────
        var proceed = room.ProceedButton;
        if (proceed != null && GodotObject.IsInstanceValid(proceed) && proceed.IsEnabled)
        {
            MainFile.Logger.Info("[TreasureDecider] Clicking treasure room proceed");
            proceed.ForceClick();
            Reset();
            DecisionLogger.LogDecision(GameScreen.TREASURE, "TreasureProceed",
                new List<DecisionLogger.OptionScore>(), 0, "PROCEED",
                "Leaving treasure room");
            return;
        }

        // ── Stuck detection — force-reset if nothing works ─────────────
        if (_stuckFrames > STUCK_TIMEOUT)
        {
            MainFile.Logger.Error($"[TreasureDecider] STUCK in state {_state} for {_stuckFrames} frames — force-resetting");

            // Emergency: try clicking Proceed even if it appears disabled
            if (proceed != null && GodotObject.IsInstanceValid(proceed))
            {
                try { proceed.ForceClick(); }
                catch (Exception ex) { MainFile.Logger.Error($"[TreasureDecider] Emergency proceed failed: {ex.Message}"); }
            }

            Reset();
        }
    }

    private static NTreasureRoom? GetTreasureRoom()
    {
        try
        {
            var root = ((SceneTree)Engine.GetMainLoop()).Root;
            return root.GetNodeOrNull<NTreasureRoom>(
                "Game/RootSceneContainer/Run/RoomContainer/TreasureRoom");
        }
        catch { return null; }
    }
}
