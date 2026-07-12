using HarmonyLib;
using TokenSpire2.Core;

namespace TokenSpire2.Patches;

/// <summary>
/// Harmony patch that overrides the Steam persona name to allow
/// different display names per game instance in LAN multiplayer mode.
///
/// When MultiplayerMode is enabled and SteamPersonaName is set in
/// the per-instance config, this patch intercepts GetPersonaName() and
/// returns the configured name instead of the real Steam name.
///
/// Host and client instances use different config files,
/// so each gets its own display name (e.g., "Player" vs "Bot").
///
/// IMPORTANT: We do NOT override GetSteamID() or set_NetId.
/// In --fastmp ENet mode, the game uses ENet peer IDs (not Steam IDs)
/// to track player identity. Overriding Steam IDs corrupts the game's
/// internal card/relic/player association, causing:
///   - Host cannot see hand cards
///   - Discard pile shows no cards
///   - Relic textures broken
///   - Bot UI only partial
/// </summary>
[HarmonyPatch(typeof(Steamworks.SteamFriends), nameof(Steamworks.SteamFriends.GetPersonaName))]
public static class SteamPersonaNamePatch
{
    static bool Prefix(ref string __result)
    {
        if (!AppConfig.IsInitialized) return true; // let original run
        var cfg = AppConfig.Instance;
        if (!cfg.MultiplayerMode || string.IsNullOrEmpty(cfg.SteamPersonaName))
            return true; // let original run

        __result = cfg.SteamPersonaName;
        return false; // skip original
    }
}
