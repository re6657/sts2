using System.IO;
using HarmonyLib;
using LocalCoop.Mod.Runtime;
using System.Reflection;

namespace LocalCoop.Mod.Patches;

[HarmonyPatch]
public static class RunIdentityLaunchPatch
{
    private const string TypeName = "MegaCrit.Sts2.Core.Runs.RunManager";
    private const string MethodName = "Launch";

    public static (string TypeName, string MethodName) TargetSignatureForTesting => (TypeName, MethodName);

    public static MethodBase? TargetMethod()
    {
        var type = AccessTools.TypeByName(TypeName);
        return type is null
            ? null
            : AccessTools.GetDeclaredMethods(type).SingleOrDefault(method =>
                method.Name == MethodName && method.GetParameters().Length == 0);
    }

    [HarmonyPriority(Priority.First)]
    public static void Postfix(object? __instance)
    {
        // Always align broker run identity during Launch, even in dual-instance
        // broker mode. LocalContext.NetId must be set BEFORE auto-battle or any
        // game code calls LocalContext.GetMe(runState) — otherwise GetMe returns
        // null and combat actions (card play, end turn) silently fail.
        //
        // AlignBrokerRun is idempotent and safe: if BrokerNetGameService isn't
        // found yet on the RunManager instance, it returns false and does nothing.
        // The alignment will then be completed reactively during the first message
        // dispatch via InvokeWithLocalContext. But calling it here ensures it runs
        // PROACTIVELY, before any game code that depends on LocalContext.NetId.
        AlignLocalContextForBrokerRunForTesting(__instance);
    }

    public static bool AlignLocalContextForBrokerRunForTesting(object? instance)
    {
        return RunIdentityAlignment.AlignBrokerRun(instance);
    }
}
