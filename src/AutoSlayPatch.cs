using System;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes;

namespace TokenSpire2;

// Attach AutoPlayController to the game tree when NGame is ready.
[HarmonyPatch(typeof(NGame), "_Ready")]
public static class AttachAutoSlayNodePatch
{
    static void Postfix(NGame __instance)
    {
        try
        {
            Console.WriteLine("[AutoSlay] Harmony patch fired! Attaching AutoPlayController to NGame...");
            var node = new AutoPlayController();
            node.Name = "AutoPlayController";
            __instance.AddChild(node);
            Console.WriteLine("[AutoSlay] AutoPlayController added to NGame successfully.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AutoSlay] Patch FAILED: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
