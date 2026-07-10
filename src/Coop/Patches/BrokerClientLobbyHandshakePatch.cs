using System.Reflection;
using HarmonyLib;

namespace LocalCoop.Mod.Patches;

/// <summary>
/// DISABLED: This patch previously sent a ClientLobbyJoinRequestMessage as a
/// Postfix on NCharacterSelectScreen.InitializeMultiplayerAsClient. That caused
/// duplicate player creation (the 3+ player bug) because BrokerClientJoinFlowPatch
/// already handles the join flow, and the game's own InitializeMultiplayerAsClient
/// code sends the join request through the substituted broker service.
///
/// The class is retained as a skeleton so it can be re-enabled if needed, but
/// Postfix is a no-op. TargetMethods is preserved so Harmony doesn't error
/// if this class is somehow auto-discovered.
/// </summary>
[HarmonyPatch]
public static class BrokerClientLobbyHandshakePatch
{
    public static IEnumerable<MethodBase> TargetMethods()
    {
        var type = AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect.NCharacterSelectScreen");
        if (type is null)
        {
            yield break;
        }

        foreach (var method in AccessTools.GetDeclaredMethods(type).Where(method => method.Name == "InitializeMultiplayerAsClient"))
        {
            yield return method;
        }
    }

    public static void Postfix(object __instance, object[] __args)
    {
        // Intentional no-op — see class doc.
    }
}
