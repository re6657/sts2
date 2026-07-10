using System;
using HarmonyLib;

namespace TokenSpire2.Patches;

/// <summary>
/// Central patch registry — PLACEHOLDER for future consolidation.
///
/// CURRENT STATE (v2 rewrite):
///   All broker patches (25+ types) are installed by
///   LocalCoopPatchInstaller.Install() called from
///   AutoSlayPatch.TokenSpire2ModuleInit [ModuleInitializer].
///
///   Generic patches are installed by the fallback
///   new Harmony("TokenSpire2").PatchAll() in MainFile.Initialize().
///
/// FUTURE:
///   When the patch files are migrated from src/Coop/Patches/
///   into src/Patches/ (see PatchStubs.cs migration notes),
///   this registry becomes the SINGLE install point, replacing
///   both the ModuleInitializer path and the fallback scan.
/// </summary>
public static class PatchRegistry
{
    public const string BrokerHarmonyId = "localcoop.transport-broker";
    public const string ModHarmonyId = "TokenSpire2";

    private static bool _installed;

    /// <summary>
    /// Install all patches. Currently a no-op — real broker patches
    /// are installed by the ModuleInitializer. This method exists
    /// as the planned entry point once patches are consolidated.
    /// </summary>
    public static void InstallAll()
    {
        if (_installed) return;
        _installed = true;

        Log("[PatchRegistry] Deferred — broker patches installed by ModuleInitializer.");

        // Future: migrate patch classes from src/Coop/Patches/ into
        // src/Patches/ and call PatchAll(typeof(...)) for each group.
    }

    /// <summary>Uninstall all broker patches.</summary>
    public static void UninstallAll()
    {
        var harmony = new Harmony(BrokerHarmonyId);
        harmony.UnpatchAll(BrokerHarmonyId);
        _installed = false;
        Log("[PatchRegistry] All broker patches uninstalled.");
    }

    /// <summary>Install a single patch class via CreateClassProcessor.</summary>
    public static void PatchAll(Type patchClass, string? harmonyId = null)
    {
        var harmony = new Harmony(harmonyId ?? BrokerHarmonyId);
        try
        {
            harmony.CreateClassProcessor(patchClass).Patch();
            Log($"[PatchRegistry]   ✓ {patchClass.Name}");
        }
        catch (Exception ex)
        {
            Log($"[PatchRegistry]   ✗ {patchClass.Name}: {ex.Message}");
            throw;
        }
    }

    private static void Log(string msg)
    {
        try { MainFile.Logger?.Info(msg); }
        catch { /* logging unavailable */ }
    }
}
