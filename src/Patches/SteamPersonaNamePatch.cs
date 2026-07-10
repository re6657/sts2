using HarmonyLib;
using TokenSpire2.Core;

namespace TokenSpire2.Patches;

/// <summary>
/// Harmony patch that overrides the Steam persona name to allow
/// different display names per game instance in LAN multiplayer mode.
///
/// When MultiplayerMode is enabled and SteamPersonaName is set in
/// batch_config.json, this patch intercepts GetPersonaName() and
/// returns the configured name instead of the real Steam name.
///
/// Host and client instances use different batch_config.json files,
/// so each gets its own display name (e.g., "Player" vs "Bot").
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
