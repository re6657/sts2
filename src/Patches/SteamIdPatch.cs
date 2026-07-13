using HarmonyLib;
using Steamworks;
using TokenSpire2.Core;

namespace TokenSpire2.Patches;

/// <summary>
/// Gives each bot instance a UNIQUE fake CSteamID derived from its
/// SteamPersonaName config value (Bot1, Bot2, Bot3).
///
/// PROBLEM: All bots share the same real Steam ID (same Steam account
/// on the same machine). When Bot2 tries to join the lobby, the host
/// sees "Steam ID X is already in the lobby" and rejects it.
///
/// FIX: Each bot gets a deterministic fake ID based on its persona name.
/// In --fastmp ENet mode, Steam networking is NOT used at all — ENet
/// peer IDs handle actual message routing. The Steam ID is only used
/// for lobby identity checks, so faking it is safe.
///
/// COMPATIBILITY: We only override for non-host bot instances in
/// multiplayer mode. The host and singleplayer keep their real ID.
/// This is a Prefix patch on the static method SteamUser.GetSteamID(),
/// NOT a property getter patch — avoids the card/relic corruption
/// issues that plagued the previous NetId getter patch attempt.
/// </summary>
[HarmonyPatch(typeof(SteamUser), nameof(SteamUser.GetSteamID))]
public static class SteamIdPatch
{
    private static CSteamID _fakeId;
    private static bool _initialized;

    // Standard SteamID64 template for individual accounts:
    // Universe=1(Public), Type=1(Individual), Instance=1
    // Format: 0x1100001XXXXXXXX (account ID in lower 32 bits)
    private const ulong STEAMID64_TEMPLATE = 0x110000100000000UL;

    static bool Prefix(ref CSteamID __result)
    {
        if (!AppConfig.IsInitialized) return true;
        var cfg = AppConfig.Instance;
        if (!cfg.MultiplayerMode) return true;

        // Host keeps real Steam ID — only one host, no conflict
        if (cfg.IsMultiplayerHost) return true;

        __result = GetFakeId();
        return false; // skip original
    }

    private static CSteamID GetFakeId()
    {
        if (_initialized) return _fakeId;
        _initialized = true;

        var name = AppConfig.Instance.SteamPersonaName ?? "Bot";

        // Deterministic hash of persona name → unique account ID
        uint hash = 0x811C9DC5; // FNV-1a offset basis
        foreach (char c in name)
        {
            hash ^= c;
            hash *= 0x01000193; // FNV-1a prime
        }

        // Ensure account ID is in a reasonable range (1 .. 0x7FFFFFFF)
        // and never zero (Steam uses 32-bit account IDs)
        uint accountId = (hash & 0x7FFFFFFF);
        if (accountId == 0) accountId = 1;

        _fakeId = new CSteamID(STEAMID64_TEMPLATE | accountId);

        MainFile.Logger?.Info(
            $"[SteamId] Fake ID for '{name}': {_fakeId} (accountId=0x{accountId:X8})");

        return _fakeId;
    }
}
