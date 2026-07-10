using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Multiplayer;

namespace TokenSpire2.Patches;

/// <summary>
/// Forces the host to use ENet transport when --fastmp is present.
///
/// The game's --fastmp flag only affects the JOIN side
/// (NJoinFriendScreen.FastMpJoin). The HOST side in
/// NMultiplayerHostSubmenu checks platformType independently —
/// if SteamFix64 makes SteamInitializer.Initialized == true, the host
/// will call StartSteamHost which creates a Steam lobby, not an ENet
/// server. The client then tries to connect to 127.0.0.1:33771 via
/// ENet, but nobody is listening there.
///
/// This patch intercepts StartSteamHost and redirects it to
/// StartENetHost(33771, maxClients) when --fastmp is active, so both
/// sides use the same transport.
/// </summary>

[HarmonyPatch(typeof(NetHostGameService), "StartSteamHost")]
public static class ForceENetHostPatch
{
    static bool Prefix(NetHostGameService __instance, int maxClients, ref Task<NetErrorInfo?> __result)
    {
        if (!CommandLineHelper.HasArg("fastmp"))
            return true; // use original Steam host

        try
        {
            MainFile.Logger?.Info("[ForceENetHost] --fastmp detected, routing StartSteamHost → StartENetHost(33771)");
        }
        catch { }

        var error = __instance.StartENetHost(33771, maxClients);
        __result = Task.FromResult<NetErrorInfo?>(error);
        return false; // skip original Steam host
    }
}
