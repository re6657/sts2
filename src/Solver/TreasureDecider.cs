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
/// Treasure room: open chest, wait for animation, pick up relic, proceed.
/// Uses frame-based delays to avoid "Attempted to pick relic while relic
/// picking is not active!" errors caused by acting before the chest
/// animation completes.
/// </summary>
public static class TreasureDecider
{
    private static bool _chestOpened;
    private static int _postChestFrames;   // frames waited since chest opened
    private static int _pickupFailures;     // consecutive pickup failures
    private const int CHEST_ANIM_DELAY = 45; // frames (~0.75s at 60fps) for chest open animation
    private const int MAX_PICKUP_RETRIES = 10;

    public static void Reset()
    {
        _chestOpened = false;
        _postChestFrames = 0;
        _pickupFailures = 0;
    }

    public static void Decide()
    {
        var room = GetTreasureRoom();
        if (room == null) return;

        // ── Guard: if a relic selection overlay is showing, let the overlay
        // handler (DispatchOverlay → RelicDecider) deal with it. Don't try
        // to click NTreasureRoomRelicHolder while the overlay is active. ──
        if (NOverlayStack.Instance?.Peek() is NChooseARelicSelection)
        {
            // Overlay is handling the relic choice — wait for it to dismiss
            return;
        }

        // ── Step 1: Open chest ──────────────────────────────────────────
        if (!_chestOpened)
        {
            var chest = room.GetNodeOrNull<NClickableControl>("Chest");
            if (chest != null && GodotObject.IsInstanceValid(chest) && chest.IsEnabled)
            {
                MainFile.Logger.Info("[TreasureDecider] Opening chest");
                chest.ForceClick();
                _chestOpened = true;
                _postChestFrames = 0;
                _pickupFailures = 0;
                DecisionLogger.LogDecision(GameScreen.TREASURE, "TreasureOpen",
                    new List<DecisionLogger.OptionScore>(), 0, "OPEN_CHEST",
                    "Opening treasure chest");
            }
            return;
        }

        // ── Wait for chest animation to finish ─────────────────────────
        _postChestFrames++;
        if (_postChestFrames < CHEST_ANIM_DELAY)
            return;

        // ── Step 2: Pick up relic(s) ────────────────────────────────────
        var relics = AutoSlayHelpers.FindAll<NTreasureRoomRelicHolder>(room)
            .Where(r => r.IsEnabled && r.Visible)
            .ToList();

        if (relics.Count > 0)
        {
            try
            {
                MainFile.Logger.Info($"[TreasureDecider] Picking up relic ({relics.Count} available, waited {_postChestFrames}f)");
                relics[0].ForceClick();
                _pickupFailures = 0;
                DecisionLogger.LogDecision(GameScreen.TREASURE, "TreasurePickup",
                    new List<DecisionLogger.OptionScore>(), 0, "TAKE_RELIC",
                    $"Taking treasure relic ({relics.Count} available)");
                return;
            }
            catch (System.InvalidOperationException ex)
            {
                // "Attempted to pick relic while relic picking is not active!"
                // The chest animation hasn't finished yet, or the game state
                // isn't ready. Wait more frames and retry.
                _pickupFailures++;
                if (_pickupFailures >= MAX_PICKUP_RETRIES)
                {
                    // Reset and try opening chest again from scratch
                    MainFile.Logger.Error($"[TreasureDecider] Pickup failed {_pickupFailures}x — resetting chest state (last error: {ex.Message})");
                    _chestOpened = false;
                    _postChestFrames = 0;
                    _pickupFailures = 0;
                }
                return;
            }
        }

        // ── Step 3: Proceed (only when no relics remain to pick up) ─────
        // After the relic is picked up OR the NChooseARelicSelection overlay
        // has been handled and dismissed, the Proceed button becomes enabled.
        var proceed = room.ProceedButton;
        if (proceed?.IsEnabled == true)
        {
            MainFile.Logger.Info("[TreasureDecider] Clicking treasure room proceed");
            proceed.ForceClick();
            Reset();
            DecisionLogger.LogDecision(GameScreen.TREASURE, "TreasureProceed",
                new List<DecisionLogger.OptionScore>(), 0, "PROCEED",
                "Leaving treasure room");
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
