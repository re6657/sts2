using System.Reflection;
using HarmonyLib;
using TokenSpire2;

namespace LocalCoop.Mod.Patches;

/// <summary>
/// Suppresses the Steam crash error dialog ("Steam应用已崩溃") that appears
/// when the game is launched without Steam running. In broker mode, we don't
/// use Steam at all — the TCP broker handles all networking — so the Steam
/// error popup is false-positive noise.
///
/// The game flow:
///   1. SteamInitializer.RunCallbacksAsync detects Steam is no longer running
///   2. SteamInitializer fires the SteamNoLongerRunning event
///   3. NGame.OnSteamNoLongerRunning is the event handler → shows error popup
///
/// This patch blocks step 2 by preventing the event subscription (step
/// between 1 and 3). We patch SteamInitializer.add_SteamNoLongerRunning
/// (a public static method) rather than NGame.OnSteamNoLongerRunning
/// (a private instance method) for more reliable method resolution.
/// </summary>
[HarmonyPatch]
public static class SteamCrashSuppressionPatch
{
    public static MethodBase? TargetMethod()
    {
        try
        {
            var type = AccessTools.TypeByName("MegaCrit.Sts2.Core.Platform.Steam.SteamInitializer");
            if (type is null)
            {
                Log("[SteamCrashSuppressionPatch] SteamInitializer type NOT FOUND.");
                return null;
            }

            // Use DeclaredMethod for exact name matching — avoids accidentally
            // matching event accessors or other special-name methods.
            var method = AccessTools.DeclaredMethod(type, "add_SteamNoLongerRunning");
            if (method is null)
            {
                Log("[SteamCrashSuppressionPatch] SteamInitializer.add_SteamNoLongerRunning NOT FOUND.");
                return null;
            }

            Log($"[SteamCrashSuppressionPatch] Targeting {type.FullName}.{method.Name}");
            return method;
        }
        catch (Exception ex)
        {
            Log($"[SteamCrashSuppressionPatch] TargetMethod threw {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static void Log(string msg)
    {
        try { System.Console.WriteLine(msg); } catch { }
    }

    /// <summary>
    /// Prefix that skips the original method entirely.
    /// This prevents NGame from subscribing to the SteamNoLongerRunning event,
    /// so the "Steam应用已崩溃" error popup never appears.
    /// </summary>
    public static bool Prefix()
    {
        MainFile.Logger.Info("[SteamCrashSuppressionPatch] Blocked SteamNoLongerRunning event subscription.");
        return false;
    }
}
