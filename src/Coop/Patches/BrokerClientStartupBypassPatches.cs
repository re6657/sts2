using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Multiplayer;

namespace LocalCoop.Mod.Patches;

/// <summary>
/// Bypasses native Steam/ENet client P2P connection when running in broker mode.
///
/// The game has two IClientConnectionInitializer implementations:
///   SteamClientConnectionInitializer.Connect
///   ENetClientConnectionInitializer.Connect
///
/// These are called during the lobby→gameplay transition to establish a
/// Steam/ENet P2P connection between host and client. In broker mode, the
/// TCP broker already handles all communication — allowing native P2P to
/// run alongside the broker causes dual-network state divergence, which
/// triggers "Multiplayer data desync" checksum errors from
/// CombatStateSynchronizer.
///
/// These patches replace the old BrokerClientSteamStartupBypassPatch /
/// BrokerClientENetStartupBypassPatch / BrokerClientConnectBypassPatch
/// which targeted NetClientGameService methods that don't exist in the
/// current STS2 game version (StartSteamClient / StartENetClient are not
/// present on NetClientGameService).
///
/// v2: Uses AppConfig.Instance.BrokerEnabled directly instead of resolving
///     the mod directory from typeof(LocalCoopMod).Assembly.Location, which
///     can point to a temp/cache directory where marker files don't exist.
/// </summary>

[HarmonyPatch]
public static class BrokerClientSteamConnectBypassPatch
{
    private const string TargetTypeName = "MegaCrit.Sts2.Core.Multiplayer.Connection.SteamClientConnectionInitializer";
    private const string NativeMethodName = "Connect";

    public static MethodBase? TargetMethod()
    {
        try
        {
            var type = AccessTools.TypeByName(TargetTypeName);
            if (type is null)
            {
                Log($"[ClientBypass] {TargetTypeName} type not found.");
                return null;
            }

            foreach (var m in AccessTools.GetDeclaredMethods(type))
            {
                if (m.Name == NativeMethodName && !m.IsSpecialName)
                {
                    var parms = m.GetParameters();
                    if (parms.Length == 2
                        && parms[0].ParameterType.Name == "NetClientGameService"
                        && parms[1].ParameterType == typeof(CancellationToken))
                    {
                        Log($"[ClientBypass] Found Steam Connect({string.Join(", ", parms.Select(p => p.ParameterType.Name))}).");
                        return m;
                    }
                }
            }

            Log($"[ClientBypass] Method {NativeMethodName}(NetClientGameService, CancellationToken) not found on {TargetTypeName}.");
            return null;
        }
        catch (Exception ex)
        {
            Log($"[BrokerClientSteamConnectBypassPatch] TargetMethod threw {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    public static bool Prefix(ref Task<NetErrorInfo?> __result)
    {
        if (!BrokerClientStartupBypassHelper.ShouldBypass("Steam Client Connect"))
        {
            return true; // run native P2P
        }

        __result = Task.FromResult<NetErrorInfo?>(null);
        return false; // skip native P2P
    }

    private static void Log(string msg)
    {
        try { System.Console.WriteLine(msg); } catch { }
    }
}

[HarmonyPatch]
public static class BrokerClientENetConnectBypassPatch
{
    private const string TargetTypeName = "MegaCrit.Sts2.Core.Multiplayer.Connection.ENetClientConnectionInitializer";
    private const string NativeMethodName = "Connect";

    public static MethodBase? TargetMethod()
    {
        try
        {
            var type = AccessTools.TypeByName(TargetTypeName);
            if (type is null)
            {
                Log($"[ClientBypass] {TargetTypeName} type not found.");
                return null;
            }

            foreach (var m in AccessTools.GetDeclaredMethods(type))
            {
                if (m.Name == NativeMethodName && !m.IsSpecialName)
                {
                    var parms = m.GetParameters();
                    if (parms.Length == 2
                        && parms[0].ParameterType.Name == "NetClientGameService"
                        && parms[1].ParameterType == typeof(CancellationToken))
                    {
                        Log($"[ClientBypass] Found ENet Connect({string.Join(", ", parms.Select(p => p.ParameterType.Name))}).");
                        return m;
                    }
                }
            }

            Log($"[ClientBypass] Method {NativeMethodName}(NetClientGameService, CancellationToken) not found on {TargetTypeName}.");
            return null;
        }
        catch (Exception ex)
        {
            Log($"[BrokerClientENetConnectBypassPatch] TargetMethod threw {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    public static bool Prefix(ref Task<NetErrorInfo?> __result)
    {
        if (!BrokerClientStartupBypassHelper.ShouldBypass("ENet Client Connect"))
        {
            return true; // run native P2P
        }

        __result = Task.FromResult<NetErrorInfo?>(null);
        return false; // skip native P2P
    }

    private static void Log(string msg)
    {
        try { System.Console.WriteLine(msg); } catch { }
    }
}

/// <summary>
/// Shared bypass check used by all client-side startup bypass patches.
/// Uses AppConfig.Instance.BrokerEnabled (set at mod init from the correct
/// mod directory) instead of re-resolving marker files from assembly location
/// (which may point to a temp/cache directory).
/// </summary>
internal static class BrokerClientStartupBypassHelper
{
    public static bool ShouldBypass(string label)
    {
        try
        {
            if (!TokenSpire2.Core.AppConfig.IsInitialized)
            {
                TokenSpire2.MainFile.Logger?.Info(
                    $"[Bypass] {label} NOT suppressed — AppConfig not initialized yet.");
                return false;
            }

            if (!TokenSpire2.Core.AppConfig.Instance.BrokerEnabled)
            {
                TokenSpire2.MainFile.Logger?.Info(
                    $"[Bypass] {label} NOT suppressed — broker not enabled.");
                return false;
            }

            TokenSpire2.MainFile.Logger?.Info(
                $"[Bypass] {label} suppressed — broker mode active, native P2P bypassed.");
            return true;
        }
        catch
        {
            // If anything goes wrong, let native P2P run (safer than silently breaking).
            System.Console.WriteLine($"[Bypass] {label} check threw — allowing native P2P.");
            return false;
        }
    }
}
