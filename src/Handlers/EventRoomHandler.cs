using System;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Events;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;

namespace TokenSpire2.Handlers;

public static class EventRoomHandler
{
    public static double Handle(Node eventRoom, System.Random rng)
    {
        if (!GodotObject.IsInstanceValid(eventRoom)) return 0;

        // Look for proceed button first (some events have it after completion)
        var proceedBtn = AutoSlayHelpers.FindFirst<NProceedButton>(eventRoom);
        if (proceedBtn?.IsEnabled == true)
        {
            MainFile.Logger.Info("[AutoSlay] Clicking event proceed button");
            proceedBtn.ForceClick();
            return 1.0;
        }

        // Try unlocked event options
        var options = AutoSlayHelpers.FindAll<NEventOptionButton>(eventRoom)
            .Where(o => o.Option?.IsLocked == false)
            .ToList();

        if (options.Count > 0)
        {
            var pick = options[rng.Next(options.Count)];
            MainFile.Logger.Info("[AutoSlay] Selecting event option");
            pick.ForceClick();
            return 1.0;
        }

        // Try clicking dialogue hitbox (Ancient event, etc.)
        // Search by name with multiple type attempts — NButton, NClickableControl, or generic Node
        var dialogueBtn = eventRoom.GetNodeOrNull<Node>("%DialogueHitbox");
        if (dialogueBtn != null && GodotObject.IsInstanceValid(dialogueBtn))
        {
            // Check visibility/is-enabled via duck-typing to handle multiple possible types
            try
            {
                bool visible = dialogueBtn is CanvasItem ci ? ci.Visible : true;
                if (visible)
                {
                    MainFile.Logger.Info("[AutoSlay] Clicking event dialogue hitbox");
                    if (dialogueBtn is NButton nb) nb.ForceClick();
                    else if (dialogueBtn is NClickableControl ncc) ncc.ForceClick();
                    else dialogueBtn.EmitSignal("pressed");
                    return 0.5;
                }
            }
            catch (Exception ex)
            {
                MainFile.Logger.Warn($"[AutoSlay] DialogueHitbox click failed: {ex.Message}");
            }
        }

        return 0.5;
    }
}
