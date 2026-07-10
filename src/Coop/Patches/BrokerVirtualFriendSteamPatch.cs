using System.Reflection;
using HarmonyLib;
using TokenSpire2;
using TokenSpire2.Core;

namespace LocalCoop.Mod.Patches;

/// <summary>
/// Shared state and helpers for virtual friend injection patches.
/// Patches Steamworks.NET methods to inject "人机一号" into the Steam
/// friend list at the API level so the game's ShowFriends creates a
/// real NJoinFriendButton. Player clicks → JoinGameAsync → JoinFlow.Begin
/// → BrokerClientJoinFlowPatch intercepts → broker handshake.
/// </summary>
internal static class VirtualFriendHelper
{
    public const ulong VirtualSteamIdValue = 0x1100001DEAD0001UL;

    /// <summary>
    /// Original (uninflated) friend count, set by BrokerVFPatch_GetFriendCount
    /// before it adds +1.
    /// </summary>
    public static int OriginalFriendCount;

    private static object? _virtualSteamId;

    public static object GetVirtualSteamId()
    {
        if (_virtualSteamId != null) return _virtualSteamId;
        try
        {
            var csteamIdType = AccessTools.TypeByName("Steamworks.CSteamID");
            if (csteamIdType != null)
            {
                var ctor = csteamIdType.GetConstructor([typeof(ulong)]);
                if (ctor != null)
                {
                    _virtualSteamId = ctor.Invoke([VirtualSteamIdValue]);
                }
            }
        }
        catch { }
        return _virtualSteamId ?? Activator.CreateInstance(typeof(ulong), [(object)VirtualSteamIdValue]);
    }

    public static bool IsVirtualFriend(object steamIdObj)
    {
        if (steamIdObj == null) return false;
        try
        {
            var field = steamIdObj.GetType().GetField("m_SteamID",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (field != null)
            {
                var value = field.GetValue(steamIdObj);
                return value is ulong ul && ul == VirtualSteamIdValue;
            }
        }
        catch { }
        return false;
    }

    public static bool ShouldInject()
    {
        try
        {
            if (!AppConfig.IsInitialized) return false;
            var cfg = AppConfig.Instance;
            return cfg.BrokerEnabled && cfg.IsClient;
        }
        catch { return false; }
    }

    public static void Log(string msg)
    {
        try { MainFile.Logger?.Info(msg); }
        catch { }
    }
}

// ══════════════════════════════════════════════════════════════════════════
// Patch 1: GetFriendCount — add 1 for our virtual friend
// ══════════════════════════════════════════════════════════════════════════

[HarmonyPatch]
public static class BrokerVFPatch_GetFriendCount
{
    public static MethodBase? TargetMethod()
    {
        var type = AccessTools.TypeByName("Steamworks.SteamFriends");
        var method = type != null ? AccessTools.Method(type, "GetFriendCount") : null;
        VirtualFriendHelper.Log(
            method != null
                ? $"[BrokerVFPatch] GetFriendCount TargetMethod: FOUND {method.DeclaringType?.FullName}.{method.Name}"
                : "[BrokerVFPatch] GetFriendCount TargetMethod: NOT FOUND");
        return method;
    }

    public static void Postfix(ref int __result)
    {
        if (!VirtualFriendHelper.ShouldInject()) return;

        VirtualFriendHelper.OriginalFriendCount = __result;
        __result += 1;
        VirtualFriendHelper.Log(
            $"[BrokerVFPatch] GetFriendCount: {VirtualFriendHelper.OriginalFriendCount} → {__result}");
    }
}

// ══════════════════════════════════════════════════════════════════════════
// Patch 2: GetFriendByIndex — for the extra index, return virtual CSteamID
// ══════════════════════════════════════════════════════════════════════════

[HarmonyPatch]
public static class BrokerVFPatch_GetFriendByIndex
{
    public static MethodBase? TargetMethod()
    {
        var type = AccessTools.TypeByName("Steamworks.SteamFriends");
        var method = type != null ? AccessTools.Method(type, "GetFriendByIndex") : null;
        VirtualFriendHelper.Log(
            method != null
                ? $"[BrokerVFPatch] GetFriendByIndex TargetMethod: FOUND {method.DeclaringType?.FullName}.{method.Name}"
                : "[BrokerVFPatch] GetFriendByIndex TargetMethod: NOT FOUND");
        return method;
    }

    public static bool Prefix(ref object __result, int iFriend, object iFriendFlags)
    {
        if (!VirtualFriendHelper.ShouldInject()) return true;

        try
        {
            if (iFriend >= VirtualFriendHelper.OriginalFriendCount)
            {
                __result = VirtualFriendHelper.GetVirtualSteamId();
                VirtualFriendHelper.Log(
                    $"[BrokerVFPatch] GetFriendByIndex({iFriend}): injected virtual friend (originalCount={VirtualFriendHelper.OriginalFriendCount}).");
                return false;
            }
        }
        catch (Exception ex)
        {
            VirtualFriendHelper.Log($"[BrokerVFPatch] GetFriendByIndex Prefix error: {ex.Message}");
        }

        return true;
    }
}

// ══════════════════════════════════════════════════════════════════════════
// Patch 3: GetFriendPersonaName — return "人机一号" for virtual friend
// ══════════════════════════════════════════════════════════════════════════

[HarmonyPatch]
public static class BrokerVFPatch_GetFriendPersonaName
{
    public static MethodBase? TargetMethod()
    {
        var type = AccessTools.TypeByName("Steamworks.SteamFriends");
        var method = type != null ? AccessTools.Method(type, "GetFriendPersonaName") : null;
        VirtualFriendHelper.Log(
            method != null
                ? $"[BrokerVFPatch] GetFriendPersonaName TargetMethod: FOUND {method.DeclaringType?.FullName}.{method.Name}"
                : "[BrokerVFPatch] GetFriendPersonaName TargetMethod: NOT FOUND");
        return method;
    }

    public static bool Prefix(ref string __result, object steamIDFriend)
    {
        if (!VirtualFriendHelper.ShouldInject()) return true;
        if (!VirtualFriendHelper.IsVirtualFriend(steamIDFriend)) return true;

        __result = "人机一号";
        VirtualFriendHelper.Log("[BrokerVFPatch] GetFriendPersonaName: returning '人机一号'.");
        return false;
    }
}

// ══════════════════════════════════════════════════════════════════════════
// Patch 4: GetFriendGamePlayed — report virtual friend is playing StS2
// ══════════════════════════════════════════════════════════════════════════

[HarmonyPatch]
public static class BrokerVFPatch_GetFriendGamePlayed
{
    public static MethodBase? TargetMethod()
    {
        var type = AccessTools.TypeByName("Steamworks.SteamFriends");
        var method = type != null ? AccessTools.Method(type, "GetFriendGamePlayed") : null;
        VirtualFriendHelper.Log(
            method != null
                ? $"[BrokerVFPatch] GetFriendGamePlayed TargetMethod: FOUND {method.DeclaringType?.FullName}.{method.Name}"
                : "[BrokerVFPatch] GetFriendGamePlayed TargetMethod: NOT FOUND");
        return method;
    }

    public static bool Prefix(ref object __result, object steamIDFriend)
    {
        if (!VirtualFriendHelper.ShouldInject()) return true;
        if (!VirtualFriendHelper.IsVirtualFriend(steamIDFriend)) return true;

        try
        {
            var friendGameInfoType = AccessTools.TypeByName("Steamworks.FriendGameInfo_t");
            if (friendGameInfoType == null)
            {
                VirtualFriendHelper.Log("[BrokerVFPatch] GetFriendGamePlayed: FriendGameInfo_t type not found.");
                return true;
            }

            var info = Activator.CreateInstance(friendGameInfoType);

            var cgameIdType = AccessTools.TypeByName("Steamworks.CGameID");
            if (cgameIdType != null)
            {
                var cgameIdCtor = cgameIdType.GetConstructor([typeof(uint)]);
                if (cgameIdCtor != null)
                {
                    var gameId = cgameIdCtor.Invoke([(uint)2868840]);
                    var gameIdField = friendGameInfoType.GetField("m_gameID",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    gameIdField?.SetValue(info, gameId);
                }
            }

            var lobbyIdField = friendGameInfoType.GetField("m_steamIDLobby",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (lobbyIdField != null && lobbyIdField.FieldType == VirtualFriendHelper.GetVirtualSteamId().GetType())
            {
                var csteamIdType = AccessTools.TypeByName("Steamworks.CSteamID");
                var lobbyCtor = csteamIdType?.GetConstructor([typeof(ulong)]);
                if (lobbyCtor != null)
                {
                    var dummyLobbyId = lobbyCtor.Invoke([0x1100001DEAD1001UL]);
                    lobbyIdField.SetValue(info, dummyLobbyId);
                }
            }

            // Wrap in Nullable<FriendGameInfo_t> since return type is FriendGameInfo_t?
            var nullableType = typeof(Nullable<>).MakeGenericType(friendGameInfoType);
            __result = Activator.CreateInstance(nullableType, [info]);
            VirtualFriendHelper.Log("[BrokerVFPatch] GetFriendGamePlayed: returning fake Nullable<FriendGameInfo_t>.");
            return false;
        }
        catch (Exception ex)
        {
            VirtualFriendHelper.Log($"[BrokerVFPatch] GetFriendGamePlayed error: {ex.Message}");
            return true;
        }
    }
}
