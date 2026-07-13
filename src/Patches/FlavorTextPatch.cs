using System.Collections.Generic;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Vfx;

namespace TokenSpire2.Patches;

/// <summary>
/// Harmony patch that overrides the text of end-turn-ping speech bubbles
/// to "喵喵喵" (meow). The bot calls FlavorSynchronizer.SendEndTurnPing()
/// every 5 seconds, which sends an EndTurnPingMessage over the network.
/// Each peer creates the speech bubble locally via CreateEndTurnPingDialogueIfNecessary.
///
/// This patch replaces the localized text with "喵喵喵" so the host
/// sees a cute cat nudge instead of the default "Hurry up!" text.
///
/// Since EndTurnPingMessage is a zero-size struct with no fields,
/// we cannot distinguish bot pings from human pings — all pings
/// get "喵喵喵" text. This is fine because:
///   1. Humans rarely use the ping button
///   2. "喵喵喵" is still a valid nudge
/// </summary>
[HarmonyPatch(typeof(FlavorSynchronizer), "CreateEndTurnPingDialogueIfNecessary")]
public static class FlavorTextPatch
{
    /// <summary>
    /// Prefix replaces the entire method body.
    /// ____endTurnPingDialogues is injected by Harmony (3-underscore prefix
    /// for private field access, plus the field's own leading underscore).
    /// </summary>
    static bool Prefix(
        Player player,
        Dictionary<Player, NSpeechBubbleVfx?> ____endTurnPingDialogues)
    {
        // Guard: don't create speech bubbles outside of a run
        if (NRun.Instance == null)
            return false; // skip original

        // If there's an existing bubble for this player, remove it
        if (____endTurnPingDialogues.TryGetValue(player, out var existing)
            && existing != null
            && GodotObject.IsInstanceValid(existing))
        {
            existing.QueueFree();
        }

        // Create speech bubble with "喵喵喵" text instead of localized text
        string text = player.Creature.IsDead ? "喵喵喵 (dead)" : "喵喵喵";
        var bubble = NSpeechBubbleVfx.Create(text, player.Creature, 1.5,
            player.Character.SpeechBubbleColor);
        NCombatRoom.Instance?.CombatVfxContainer.AddChild(bubble);
        ____endTurnPingDialogues[player] = bubble;

        return false; // skip original method entirely
    }
}
