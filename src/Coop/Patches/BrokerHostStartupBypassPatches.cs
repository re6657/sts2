using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Multiplayer;

namespace LocalCoop.Mod.Patches;

/// <summary>
/// Bypasses native Steam/ENet host startup when running in broker mode.
/// v2: Uses AppConfig.Instance.BrokerEnabled directly (same as client bypass patches).
/// </summary>

[HarmonyPatch]
public static class BrokerHostSteamStartupBypassPatch
{
    private const string NativeMethodName = "StartSteamHost";

    public static MethodBase? TargetMethod()
    {
        return ResolveNativeHostMethod(NativeMethodName);
    }

    public static bool Prefix(ref Task<NetErrorInfo?> __result)
    {
        if (!BrokerClientStartupBypassHelper.ShouldBypass($"Host {NativeMethodName}"))
        {
            return true; // run native P2P
        }

        __result = Task.FromResult<NetErrorInfo?>(null);
        return false; // skip native P2P
    }

    internal static MethodBase? ResolveNativeHostMethod(string methodName)
    {
        var type = AccessTools.TypeByName("MegaCrit.Sts2.Core.Multiplayer.NetHostGameService");
        return type is null ? null : AccessTools.Method(type, methodName);
    }
}

[HarmonyPatch]
public static class BrokerHostENetStartupBypassPatch
{
    private const string NativeMethodName = "StartENetHost";

    public static MethodBase? TargetMethod()
    {
        return BrokerHostSteamStartupBypassPatch.ResolveNativeHostMethod(NativeMethodName);
    }

    public static bool Prefix(ref NetErrorInfo? __result)
    {
        if (!BrokerClientStartupBypassHelper.ShouldBypass($"Host {NativeMethodName}"))
        {
            return true; // run native P2P
        }

        __result = null;
        return false; // skip native P2P
    }
}
