using System.Threading;
using HarmonyLib;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Multiplayer.Transport.ENet;
using TokenSpire2.Core;

namespace TokenSpire2.Patches;

/// <summary>
/// All bot instances on the same machine start with NetId=1000 (the
/// --fastmp default). The ENet host rejects connections from duplicate
/// peer IDs.
///
/// Previous approach (reflection override in MainFile.Initialize) failed
/// because it ran too early — FastMpJoin() later reset NetId back to
/// 1000. A getter patch on LocalContext.NetId was rejected because it
/// corrupts card/relic logic (task #330).
///
/// THIS patch intercepts ENetClient.ConnectToHost — the exact call site
/// where NetId becomes the ENet peer ID. We modify the netId parameter
/// in-place, so the handshake uses a unique ID per bot without touching
/// LocalContext at all.
///
/// Bot naming → NetId mapping (FNV-1a hash, deterministic):
///   Host → 1000 (unchanged — patch skips hosts)
///   Bot1 → ~1001+
///   Bot2 → ~1002+
///   Bot3 → ~1003+
/// </summary>

[HarmonyPatch(typeof(ENetClient), "ConnectToHost")]
public static class ENetClientNetIdPatch
{
    static void Prefix(ref ulong netId)
    {
        // Only active in --fastmp mode and only for non-host bots
        if (!CommandLineHelper.HasArg("fastmp"))
            return;

        try
        {
            var cfg = AppConfig.Instance;
            if (cfg == null) return;
            if (cfg.IsMultiplayerHost) return; // host keeps original NetId

            var name = cfg.SteamPersonaName ?? "Bot";
            uint hash = 0x811C9DC5;
            foreach (char c in name) { hash ^= c; hash *= 0x01000193; }
            ulong newNetId = 1000 + (hash % 9000);

            MainFile.Logger?.Info($"[ENetClientNetId] Overriding netId: {netId} → {newNetId} for {name}");
            netId = newNetId;
        }
        catch (System.Exception ex)
        {
            MainFile.Logger?.Info($"[ENetClientNetId] Error: {ex.Message}");
        }
    }
}
