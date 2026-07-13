using System.Collections.Generic;
using System.IO;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Vfx;
using TokenSpire2.Core;

namespace TokenSpire2.Patches;

/// <summary>
/// Harmony patch that overrides the text of end-turn-ping speech bubbles.
/// The bot calls FlavorSynchronizer.SendEndTurnPing() which sends an
/// EndTurnPingMessage over the network. Each peer creates the speech
/// bubble locally via CreateEndTurnPingDialogueIfNecessary.
///
/// Text resolution order:
///   1. Thread-static OverrideText — set by bot before calling SendEndTurnPing()
///      (works on the bot's own process, consumed immediately).
///   2. Shared file .ai_chat_current.txt — written by bot before ping,
///      read by ALL peers (host + other bots). This is how the host sees
///      the AI-generated text from the bot.
///   3. Fallback "喵喵喵" — if neither override is available.
/// </summary>
[HarmonyPatch(typeof(FlavorSynchronizer), "CreateEndTurnPingDialogueIfNecessary")]
public static class FlavorTextPatch
{
    /// <summary>
    /// Thread-static override for speech bubble text.
    /// Set before calling FlavorSynchronizer.SendEndTurnPing(),
    /// consumed once and cleared. Only visible on the calling thread/process.
    /// </summary>
    [ThreadStatic]
    public static string? OverrideText;

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

        // ── Resolve text ───────────────────────────────────────────
        string text = ResolveText(player);
        if (string.IsNullOrWhiteSpace(text))
            return false; // nothing to say

        var bubble = NSpeechBubbleVfx.Create(text, player.Creature, 1.5,
            player.Character.SpeechBubbleColor);
        NCombatRoom.Instance?.CombatVfxContainer.AddChild(bubble);
        ____endTurnPingDialogues[player] = bubble;

        return false; // skip original method entirely
    }

    /// <summary>
    /// Resolve speech bubble text with this priority:
    /// 1. Thread-static OverrideText (bot's own process)
    /// 2. Shared file .ai_chat_current.txt (cross-process, any peer)
    /// 3. Fallback "喵喵喵"
    /// </summary>
    private static string ResolveText(Player player)
    {
        // Priority 1: thread-static override (bot process only)
        if (OverrideText != null)
        {
            var t = OverrideText;
            OverrideText = null;
            return t;
        }

        // Priority 2: shared file (works cross-process for host/other bots)
        try
        {
            if (AppConfig.IsInitialized)
            {
                var filePath = Path.Combine(AppConfig.ModDirectory, ".ai_chat_current.txt");
                if (File.Exists(filePath))
                {
                    var text = File.ReadAllText(filePath).Trim();
                    if (!string.IsNullOrEmpty(text) && text.Length <= 20)
                    {
                        // Don't delete — the file serves as a cache for all peers
                        return text;
                    }
                }
            }
        }
        catch { /* fall through to meow */ }

        // Priority 3: fallback
        return player.Creature.IsDead ? "喵喵喵 (dead)" : "喵喵喵";
    }
}
