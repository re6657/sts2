using System.Collections.Generic;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.TreasureRoomRelic;

using TokenSpire2.Core;

namespace TokenSpire2.Solver;

/// <summary>
/// Treasure room: open chest, pick up relic, proceed. No random decisions needed.
/// </summary>
public static class TreasureDecider
{
    private static bool _chestOpened;

    public static void Reset() => _chestOpened = false;

    public static void Decide()
    {
        var room = GetTreasureRoom();
        if (room == null) return;

        // Step 1: Open chest
        if (!_chestOpened)
        {
            var chest = room.GetNodeOrNull<NClickableControl>("Chest");
            if (chest != null && GodotObject.IsInstanceValid(chest) && chest.IsEnabled)
            {
                MainFile.Logger.Info("[TreasureDecider] Opening chest");
                chest.ForceClick();
                _chestOpened = true;
                DecisionLogger.LogDecision(GameScreen.TREASURE, "TreasureOpen",
                    new List<DecisionLogger.OptionScore>(), 0, "OPEN_CHEST",
                    "Opening treasure chest");
            }
            return;
        }

        // Step 2: Pick up relic(s)
        var relics = AutoSlayHelpers.FindAll<NTreasureRoomRelicHolder>(room)
            .Where(r => r.IsEnabled && r.Visible)
            .ToList();
        if (relics.Count > 0)
        {
            MainFile.Logger.Info($"[TreasureDecider] Picking up relic ({relics.Count} available)");
            relics[0].ForceClick();
            DecisionLogger.LogDecision(GameScreen.TREASURE, "TreasurePickup",
                new List<DecisionLogger.OptionScore>(), 0, "TAKE_RELIC",
                $"Taking treasure relic ({relics.Count} available)");
            return;
        }

        // Step 3: Proceed
        var proceed = room.ProceedButton;
        if (proceed?.IsEnabled == true)
        {
            MainFile.Logger.Info("[TreasureDecider] Clicking treasure room proceed");
            proceed.ForceClick();
            _chestOpened = false;
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
