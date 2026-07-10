using System;
using System.IO;
using HarmonyLib;
using Steamworks;
using TokenSpire2.Core;

namespace TokenSpire2.Patches;

/// <summary>
/// Injects the host instance as a virtual Steam friend on the client.
/// Both instances share the same machine/account, so the real friend list
/// is empty. These patches make the host appear as an online friend so the
/// client's "Join Friend" screen shows the host's lobby.
///
/// Flow:
/// 1. Host writes its Steam ID to host_steam_id.txt (via VirtualFriendData)
/// 2. Client reads it and patches GetFriendCount / GetFriendByIndex /
///    GetFriendPersonaName / GetFriendGamePlayed to inject the host.
/// 3. The join-friend screen shows "Player" as joinable.
/// </summary>

[HarmonyPatch(typeof(SteamFriends), nameof(SteamFriends.GetFriendCount))]
public static class VirtualFriend_GetFriendCountPatch
{
    /// <summary>Last real friend count (before we add 1). Read by GetFriendByIndex.</summary>
    public static int LastRealCount;

    static void Postfix(EFriendFlags iFriendFlags, ref int __result)
    {
        LastRealCount = __result;
        if (!VirtualFriendData.ShouldInject) return;
        __result += 1;
    }
}

[HarmonyPatch(typeof(SteamFriends), nameof(SteamFriends.GetFriendByIndex))]
public static class VirtualFriend_GetFriendByIndexPatch
{
    static bool Prefix(int iFriend, EFriendFlags iFriendFlags, ref CSteamID __result)
    {
        if (!VirtualFriendData.ShouldInject) return true;

        int realCount = VirtualFriend_GetFriendCountPatch.LastRealCount;
        if (iFriend == realCount)
        {
            // This is our injected virtual friend
            __result = VirtualFriendData.HostSteamId;
            return false;
        }
        if (iFriend < realCount)
            return true; // real friend — let original run

        // Out of range (shouldn't happen with correct GetFriendCount)
        __result = CSteamID.Nil;
        return false;
    }
}

[HarmonyPatch(typeof(SteamFriends), nameof(SteamFriends.GetFriendPersonaName))]
public static class VirtualFriend_GetFriendPersonaNamePatch
{
    static bool Prefix(CSteamID steamIDFriend, ref string __result)
    {
        if (!VirtualFriendData.ShouldInject) return true;
        if (steamIDFriend == VirtualFriendData.HostSteamId)
        {
            __result = VirtualFriendData.HostPersonaName;
            return false;
        }
        return true;
    }
}

[HarmonyPatch(typeof(SteamFriends), nameof(SteamFriends.GetFriendGamePlayed))]
public static class VirtualFriend_GetFriendGamePlayedPatch
{
    static bool Prefix(CSteamID steamIDFriend, ref FriendGameInfo_t pFriendGameInfo, ref bool __result)
    {
        if (!VirtualFriendData.ShouldInject) return true;
        if (steamIDFriend == VirtualFriendData.HostSteamId)
        {
            pFriendGameInfo = new FriendGameInfo_t
            {
                m_gameID = new CGameID(2868840), // STS2 AppID
                m_unGameIP = 0,
                m_usGamePort = 0,
                m_usQueryPort = 0,
                m_steamIDLobby = CSteamID.Nil
            };
            __result = true;
            return false;
        }
        return true;
    }
}

/// <summary>
/// Shared data for virtual friend injection.
/// </summary>
public static class VirtualFriendData
{
    public static CSteamID HostSteamId;
    public static string HostPersonaName = "Player";
    public static bool IsReady;

    public static bool ShouldInject
    {
        get
        {
            if (!AppConfig.IsInitialized) return false;
            var cfg = AppConfig.Instance;
            return cfg.MultiplayerMode && !cfg.IsMultiplayerHost && IsReady;
        }
    }

    private static string FilePath
    {
        get
        {
            try { return Path.Combine(AppConfig.ModDirectory, "host_steam_id.txt"); }
            catch { return Path.Combine(Environment.CurrentDirectory, "host_steam_id.txt"); }
        }
    }

    /// <summary>
    /// Host calls this after Steam is initialized to export its Steam ID.
    /// </summary>
    public static void ExportHostSteamId()
    {
        try
        {
            if (!AppConfig.IsInitialized) return;
            var cfg = AppConfig.Instance;
            if (!cfg.MultiplayerMode || !cfg.IsMultiplayerHost) return;

            var steamId = SteamUser.GetSteamID();
            var name = cfg.SteamPersonaName;
            var content = $"{steamId.m_SteamID}\n{name}";
            File.WriteAllText(FilePath, content);
            MainFile.Logger?.Info($"[VirtualFriend] HOST exported SteamID={steamId.m_SteamID}, Name={name}");
        }
        catch (Exception ex)
        {
            MainFile.Logger?.Error($"[VirtualFriend] Export failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Client calls this periodically to load the host's Steam ID.
    /// Returns true once the file is found and loaded.
    /// </summary>
    public static bool TryLoadHostSteamId()
    {
        if (IsReady) return true;
        try
        {
            if (!File.Exists(FilePath)) return false;
            var lines = File.ReadAllLines(FilePath);
            if (lines.Length < 1) return false;

            var idStr = lines[0].Trim();
            if (ulong.TryParse(idStr, out var id) && id != 0)
            {
                HostSteamId = new CSteamID(id);
                HostPersonaName = lines.Length >= 2 ? lines[1].Trim() : "Player";
                IsReady = true;
                MainFile.Logger?.Info($"[VirtualFriend] CLIENT loaded HostSteamID={id}, Name={HostPersonaName}");
                return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            MainFile.Logger?.Info($"[VirtualFriend] Load attempt failed: {ex.Message}");
            return false;
        }
    }
}
