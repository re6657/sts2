using System.Reflection;
using HarmonyLib;
using LocalCoop.Mod.Runtime;
using MegaCrit.Sts2.Core.Multiplayer.Connection;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using TokenSpire2;

namespace LocalCoop.Mod.Patches;

/// <summary>
/// Intercepts the Join button on the multiplayer submenu.
/// In broker client mode: creates a JoinFlow instance and calls Begin()
/// directly — bypassing the empty Steam friend list.
/// BrokerClientJoinFlowPatch.Prefix intercepts JoinFlow.Begin and performs
/// the TCP broker handshake → scene transition to Character Select.
///
/// In normal mode: lets OpenJoinFriendsScreen run (shows Steam friend list).
/// </summary>
[HarmonyPatch]
public static class BrokerJoinFriendScreenPatch
{
    public static MethodBase? TargetMethod()
    {
        try
        {
            var type = AccessTools.TypeByName(
                "MegaCrit.Sts2.Core.Nodes.Screens.MainMenu.NMultiplayerSubmenu");
            if (type is null)
            {
                Log("[BrokerJoinFriendScreenPatch] NMultiplayerSubmenu type NOT FOUND.");
                return null;
            }

            Log($"[BrokerJoinFriendScreenPatch] NMultiplayerSubmenu type found: {type.FullName}");

            var method = AccessTools.Method(type, "OpenJoinFriendsScreen");
            if (method is null)
            {
                Log("[BrokerJoinFriendScreenPatch] OpenJoinFriendsScreen NOT FOUND.");
                return null;
            }

            var parms = method.GetParameters();
            Log($"[BrokerJoinFriendScreenPatch] Found OpenJoinFriendsScreen(" +
                $"{string.Join(", ", parms.Select(p => $"{p.ParameterType.Name} {p.Name}"))}).");
            return method;
        }
        catch (Exception ex)
        {
            Log($"[BrokerJoinFriendScreenPatch] TargetMethod error: {ex.Message}");
            return null;
        }
    }

    public static bool Prefix()
    {
        if (!ShouldIntercept())
            return true; // normal flow: show friend list

        MainFile.Logger.Info(
            "[BrokerJoinFriendScreenPatch] Broker client detected. " +
            "Skipping friend list, triggering broker join via JoinFlow.Begin...");

        try
        {
            // Resolve types via AccessTools (cross-assembly-load-context safe)
            var joinFlowType = AccessTools.TypeByName(
                "MegaCrit.Sts2.Core.Multiplayer.Game.JoinFlow");
            var initializerType = AccessTools.TypeByName(
                "MegaCrit.Sts2.Core.Multiplayer.Connection.IClientConnectionInitializer");
            var sceneTreeType = AccessTools.TypeByName("Godot.SceneTree");

            if (joinFlowType is null || initializerType is null || sceneTreeType is null)
            {
                MainFile.Logger.Error(
                    "[BrokerJoinFriendScreenPatch] Types not found: " +
                    $"JoinFlow={joinFlowType != null}, " +
                    $"IClientConnectionInitializer={initializerType != null}, " +
                    $"SceneTree={sceneTreeType != null}");
                return false; // skip friend list even on failure (no friends to show)
            }

            var beginMethod = AccessTools.Method(
                joinFlowType, "Begin", new[] { initializerType, sceneTreeType });
            if (beginMethod is null)
            {
                MainFile.Logger.Error(
                    "[BrokerJoinFriendScreenPatch] JoinFlow.Begin NOT FOUND.");
                return false;
            }

            // Create JoinFlow INSTANCE (Begin is an instance method)
            var initializer = new BrokerClientJoinFlow
                .PlaceholderClientConnectionInitializer();
            var sceneTree = (Godot.SceneTree)Godot.Engine.GetMainLoop();

            object joinFlowInstance;
            try
            {
                joinFlowInstance = Activator.CreateInstance(joinFlowType);
            }
            catch (MissingMethodException)
            {
                MainFile.Logger.Error(
                    "[BrokerJoinFriendScreenPatch] JoinFlow has no parameterless " +
                    "constructor. Cannot create instance.");
                return false;
            }

            // Invoke — BrokerClientJoinFlowPatch.Prefix intercepts this call,
            // performs TCP broker handshake, stores service in registry.
            // The game's multiplayer state machine detects the stored service
            // and transitions to Character Select.
            beginMethod.Invoke(joinFlowInstance,
                new object[] { initializer, sceneTree });

            MainFile.Logger.Info(
                "[BrokerJoinFriendScreenPatch] JoinFlow.Begin invoked — " +
                "broker handshake in progress.");
        }
        catch (Exception ex)
        {
            MainFile.Logger.Error(
                $"[BrokerJoinFriendScreenPatch] Failed: {ex.Message}");
        }

        return false; // skip original OpenJoinFriendsScreen
    }

    private static bool ShouldIntercept()
    {
        try
        {
            if (!TokenSpire2.Core.AppConfig.IsInitialized)
                return false;
            var cfg = TokenSpire2.Core.AppConfig.Instance;
            return cfg.BrokerEnabled && !cfg.IsHost;
        }
        catch { return false; }
    }

    private static void Log(string msg)
    {
        try { System.Console.WriteLine(msg); } catch { }
    }
}
