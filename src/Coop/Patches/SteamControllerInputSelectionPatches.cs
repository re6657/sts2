using System.Reflection;
using HarmonyLib;
using LocalCoop.Mod.Runtime;

namespace LocalCoop.Mod.Patches;

[HarmonyPatch]
public static class SteamControllerInputSelectionPatches
{
    public static MethodBase? TargetMethod()
    {
        var type = AccessTools.TypeByName("MegaCrit.Sts2.Core.ControllerInput.SteamControllerInputStrategy");
        return type is null ? null : AccessTools.Method(type, "UpdateControllerConnections");
    }

    public static bool Prefix(object __instance)
    {
        var settings = LoadSettings();
        if (!ShouldReplaceNativeUpdateControllerConnections(settings))
        {
            return true;
        }

        LocalCoopInputRouter.ApplyControllerSelection(
            __instance,
            LocalCoopInputRouter.ResolveAssignment(settings.Config!),
            message => new BrokerEventLog(settings.EventLogPath).Write(message));
        return false;
    }

    public static bool ShouldReplaceNativeUpdateControllerConnectionsForTesting(BrokerModeSettings settings)
    {
        return ShouldReplaceNativeUpdateControllerConnections(settings);
    }

    private static bool ShouldReplaceNativeUpdateControllerConnections(BrokerModeSettings settings)
    {
        return settings.Enabled
            && settings.Config is not null
            && LocalCoopInputRouter.ResolveAssignment(settings.Config).ControllerDevice.IsConfigured;
    }

    private static BrokerModeSettings LoadSettings()
    {
        var modDirectory = Path.GetDirectoryName(typeof(LocalCoopMod).Assembly.Location);
        return string.IsNullOrWhiteSpace(modDirectory)
            ? new BrokerModeSettings(false, null, "client-0", "localcoop-events.txt", "mod directory unavailable")
            : BrokerModeSettings.LoadFromDirectory(modDirectory);
    }
}
