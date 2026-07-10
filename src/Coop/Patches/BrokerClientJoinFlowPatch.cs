using System.Reflection;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using LocalCoop.Mod.Runtime;
using MegaCrit.Sts2.Core.Multiplayer.Connection;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using TokenSpire2;

namespace LocalCoop.Mod.Patches;

[HarmonyPatch]
public static class BrokerClientJoinFlowPatch
{
    public static MethodBase? TargetMethod()
    {
        try
        {
            var type = AccessTools.TypeByName("MegaCrit.Sts2.Core.Multiplayer.Game.JoinFlow");
            if (type is null)
            {
                Log("[BrokerClientJoinFlowPatch] JoinFlow type NOT FOUND.");
                return null;
            }
            Log($"[BrokerClientJoinFlowPatch] JoinFlow type found: {type.FullName}");

            // Use TypeByName for ALL types to avoid JIT-time TypeLoadException
            // if the sts2 or Godot assemblies aren't fully loaded yet.
            var initializerType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Multiplayer.Connection.IClientConnectionInitializer");
            var sceneTreeType = AccessTools.TypeByName("Godot.SceneTree");
            if (initializerType is null || sceneTreeType is null)
            {
                Log($"[BrokerClientJoinFlowPatch] Parameter types not available: IClientConnectionInitializer={initializerType != null}, SceneTree={sceneTreeType != null}");
                return null;
            }

            var method = AccessTools.Method(type, "Begin", [initializerType, sceneTreeType]);
            if (method is null)
            {
                // Dump all methods on JoinFlow for debugging
                foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance))
                {
                    var parms = string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
                    Log($"[BrokerClientJoinFlowPatch] JoinFlow method: {m.ReturnType.Name} {m.Name}({parms})");
                }
                Log("[BrokerClientJoinFlowPatch] JoinFlow.Begin(IClientConnectionInitializer, SceneTree) NOT FOUND.");
                return null;
            }
            Log($"[BrokerClientJoinFlowPatch] JoinFlow.Begin found: {method.ReturnType.Name} {method.Name}({string.Join(", ", method.GetParameters().Select(p => p.ParameterType.Name))})");
            return method;
        }
        catch (Exception ex)
        {
            Log($"[BrokerClientJoinFlowPatch] TargetMethod threw {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static void Log(string msg)
    {
        try { System.Console.WriteLine(msg); } catch { }
    }

    public static bool Prefix(ref Task<JoinResult> __result)
    {
        var settings = LoadSettings();
        if (!BrokerClientJoinFlow.ShouldUseBrokerJoin(settings))
        {
            MainFile.Logger.Info("[BrokerClientJoinFlowPatch] ShouldUseBrokerJoin=false — letting original JoinFlow.Begin proceed.");
            return true;
        }

        if (settings.Config?.Role != BrokerClientRole.Client)
        {
            MainFile.Logger.Info($"[BrokerClientJoinFlowPatch] Not client (role={settings.Config?.Role}) — skipping.");
            return true;
        }

        MainFile.Logger.Info("[BrokerClientJoinFlowPatch] Intercepting JoinFlow.Begin for broker setup (no join request sent).");
        var log = new BrokerEventLog(settings.EventLogPath);
        var config = BrokerLobbyServiceSubstitution.CreateRegistrationConfig(settings, BrokerClientRole.Client);
        var transport = BrokerEnvelopeTransportConnector.ConnectBlocking(
            config,
            settings.ClientId,
            TimeSpan.FromSeconds(3),
            CancellationToken.None);

        __result = BrokerClientJoinFlow.BeginStandardBrokerJoinAsync(
            settings,
            () => transport,
            log.Write,
            CancellationToken.None);

        return false;
    }

    private static BrokerModeSettings LoadSettings()
    {
        var modDirectory = Path.GetDirectoryName(typeof(LocalCoopMod).Assembly.Location);
        return string.IsNullOrWhiteSpace(modDirectory)
            ? new BrokerModeSettings(false, null, "client-0", "localcoop-events.txt", "mod directory unavailable")
            : BrokerModeSettings.LoadFromDirectory(modDirectory);
    }
}
