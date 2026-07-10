using System.Reflection;
using HarmonyLib;
using LocalCoop.Mod.Runtime;
using MegaCrit.Sts2.Core.Multiplayer.Connection;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using TokenSpire2;

namespace LocalCoop.Mod.Patches;

/// <summary>
/// [DIRECTION-D: DECOMMISSIONED]
///
/// This patch previously intercepted OpenJoinFriendsScreen on NMultiplayerSubmenu
/// and skipped the Steam friend list, calling JoinFlow.Begin directly.
/// This was ARCHITECTURALLY BROKEN: JoinFlow.Begin's JoinResult was received
/// by the patch code, not by the game's JoinGameAsync → no character select
/// transition ever occurred.
///
/// REPLACED BY: BrokerVirtualFriendSteamPatch which injects "人机一号" into
/// the Steam friend list at the API level. The game's ShowFriends method sees
/// the virtual friend and creates a real NJoinFriendButton. Player clicks it →
/// normal JoinGameAsync → JoinFlow.Begin → BrokerClientJoinFlowPatch intercepts
/// → JoinResult → scene transition. Chain is unbroken.
///
/// This file is kept as a no-op for reference. The patch is NOT installed
/// (removed from BrokerNetworkPatchTypes in LocalCoopPatchInstaller.cs).
/// </summary>
[HarmonyPatch]
public static class BrokerJoinFriendScreenPatch
{
    /// <summary>
    /// Resolve OpenJoinFriendsScreen on NMultiplayerSubmenu.
    /// NOTE: The full type name is MegaCrit.Sts2.Core.Nodes.Screens.MainMenu.NMultiplayerSubmenu
    /// (includes "MainMenu" namespace segment).
    /// Using AccessTools.Method without parameter types to match any overload.
    /// </summary>
    public static MethodBase? TargetMethod()
    {
        try
        {
            var type = AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.Screens.MainMenu.NMultiplayerSubmenu");
            if (type is null)
            {
                Log("[BrokerJoinFriendScreenPatch] NMultiplayerSubmenu type NOT FOUND. " +
                    "Tried: MegaCrit.Sts2.Core.Nodes.Screens.MainMenu.NMultiplayerSubmenu");
                return null;
            }

            Log($"[BrokerJoinFriendScreenPatch] NMultiplayerSubmenu type found: {type.FullName}");

            // Use AccessTools.Method (like BrokerHostStartupBypassPatch does) —
            // simpler and avoids iterating all declared methods which can fail
            // if any method has an unloadable parameter type.
            var method = AccessTools.Method(type, "OpenJoinFriendsScreen");
            if (method is null)
            {
                Log("[BrokerJoinFriendScreenPatch] OpenJoinFriendsScreen method NOT FOUND on NMultiplayerSubmenu.");
                return null;
            }

            var parms = method.GetParameters();
            Log($"[BrokerJoinFriendScreenPatch] Found OpenJoinFriendsScreen({string.Join(", ", parms.Select(p => $"{p.ParameterType.Name} {p.Name}"))}).");
            return method;
        }
        catch (Exception ex)
        {
            Log($"[BrokerJoinFriendScreenPatch] TargetMethod threw {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Skip the Steam friend list and trigger broker join via JoinFlow.Begin.
    /// BrokerClientJoinFlowPatch intercepts JoinFlow.Begin and performs the
    /// broker handshake.
    /// </summary>
    public static bool Prefix()
    {
        if (!ShouldUseBrokerJoin())
        {
            // Let the original OpenJoinFriendsScreen run (normal Steam friend list)
            return true;
        }

        MainFile.Logger.Info("[BrokerJoinFriendScreenPatch] Broker mode + client detected. Skipping Steam friend list, triggering broker join via JoinFlow.Begin...");

        try
        {
            // Create a placeholder initializer — the broker doesn't need
            // Steam/ENet P2P because TCP broker handles all communication.
            var initializer = new BrokerClientJoinFlow.PlaceholderClientConnectionInitializer();
            var sceneTree = (Godot.SceneTree)Godot.Engine.GetMainLoop();

            // Call JoinFlow.Begin via reflection — BrokerClientJoinFlowPatch.Prefix
            // intercepts this and performs the TCP broker handshake. The caller
            // (game code that invoked OpenJoinFriendsScreen) receives the JoinResult
            // and transitions to the lobby.
            var joinFlowType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Multiplayer.Game.JoinFlow");
            var initializerType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Multiplayer.Connection.IClientConnectionInitializer");
            var sceneTreeType = AccessTools.TypeByName("Godot.SceneTree");
            var beginMethod = joinFlowType is not null && initializerType is not null && sceneTreeType is not null
                ? AccessTools.Method(joinFlowType, "Begin", [initializerType, sceneTreeType])
                : null;

            if (beginMethod is null)
            {
                MainFile.Logger.Error("[BrokerJoinFriendScreenPatch] JoinFlow.Begin(IClientConnectionInitializer, SceneTree) NOT FOUND via reflection.");
                return false;
            }

            // Invoke — this is intercepted by BrokerClientJoinFlowPatch.Prefix
            // which replaces the result with the broker join task.
            var joinTask = (System.Threading.Tasks.Task<JoinResult>)beginMethod.Invoke(null, [initializer, sceneTree]);

            // Log completion or failure
            joinTask.ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    MainFile.Logger.Error(
                        $"[BrokerJoinFriendScreenPatch] JoinFlow.Begin FAILED: {t.Exception?.InnerException?.Message}");
                }
                else if (t.IsCompletedSuccessfully)
                {
                    MainFile.Logger.Info(
                        $"[BrokerJoinFriendScreenPatch] JoinFlow.Begin completed successfully. " +
                        $"gameMode={t.Result.gameMode}. Service stored in registry.");
                }
            });
        }
        catch (Exception ex)
        {
            MainFile.Logger.Error(
                $"[BrokerJoinFriendScreenPatch] Failed to trigger JoinFlow.Begin: {ex.Message}");
        }

        // Return false to skip the original OpenJoinFriendsScreen
        // (don't show the empty Steam friend list)
        return false;
    }

    private static bool ShouldUseBrokerJoin()
    {
        try
        {
            if (!TokenSpire2.Core.AppConfig.IsInitialized)
                return false;

            var appConfig = TokenSpire2.Core.AppConfig.Instance;
            if (!appConfig.BrokerEnabled)
                return false;

            // Only intercept on the CLIENT — host uses the normal flow
            if (appConfig.IsHost)
                return false;

            return true;
        }
        catch (Exception ex)
        {
            MainFile.Logger?.Info($"[BrokerJoinFriendScreenPatch] ShouldUseBrokerJoin check failed: {ex.Message}");
            return false;
        }
    }

    private static void Log(string msg)
    {
        try { System.Console.WriteLine(msg); } catch { }
    }
}
