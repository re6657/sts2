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
/// Uses a simple state machine — no frame counting.
/// Overlay dispatch (AutoSlayNode) handles NChooseARelicSelection separately.
/// </summary>
public static class TreasureDecider
{
    private enum State { Idle, ChestOpened, Picking }
    private static State _state = State.Idle;
    private static int _stuckFrames;
    private const int STUCK_TIMEOUT = 300; // 5 seconds at 60fps

    public static void Reset()
    {
        _state = State.Idle;
        _stuckFrames = 0;
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
            var relics = AutoSlayHelpers.FindAll<NTreasureRoomRelicHolder>(room)
                .Where(r => GodotObject.IsInstanceValid(r) && r.IsEnabled && r.Visible)
                .ToList();

            // Fallback: search for any clickable that looks like a relic
            if (relics.Count == 0 && _stuckFrames > 60)
            {
                relics = AutoSlayHelpers.FindAll<NTreasureRoomRelicHolder>(room)
                    .Where(r => GodotObject.IsInstanceValid(r))
                    .ToList();
            }

            if (relics.Count > 0)
            {
                try
                {
                    MainFile.Logger.Info($"[TreasureDecider] Picking up relic ({relics.Count} available, stuckFrames={_stuckFrames})");
                    relics[0].ForceClick();
                    _state = State.Picking;
                    _stuckFrames = 0;
                    DecisionLogger.LogDecision(GameScreen.TREASURE, "TreasurePickup",
                        new List<DecisionLogger.OptionScore>(), 0, "TAKE_RELIC",
                        $"Taking treasure relic ({relics.Count} available)");
                }
                catch (Exception ex)
                {
                    MainFile.Logger.Error($"[TreasureDecider] Relic click failed: {ex.Message}");
                    // If clicking fails repeatedly, try proceeding anyway
                    if (_stuckFrames > 90)
                    {
                        MainFile.Logger.Error("[TreasureDecider] Relic click failing repeatedly — skipping to Proceed");
                        _state = State.Picking; // fall through to Proceed check
                    }
                }
                return;
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

        // ── Stuck detection — reset if nothing works ───────────────────
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
