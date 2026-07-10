using System;
using HarmonyLib;

namespace TokenSpire2.Patches;

/// <summary>
/// Central patch registry.
///
/// Single-player only. Harmony patches are installed via
/// harmony.PatchAll(assembly) in MainFile.Initialize().
///
/// This registry provides utilities for managing patches at runtime.
/// </summary>
public static class PatchRegistry
{
    public const string ModHarmonyId = "TokenSpire2";

    private static bool _installed;

    /// <summary>Install all patches from the assembly.</summary>
    public static void InstallAll()
    {
        if (_installed) return;
        _installed = true;

        var harmony = new Harmony(ModHarmonyId);
        harmony.PatchAll(typeof(PatchRegistry).Assembly);
        Log("[PatchRegistry] All patches installed.");
    }

    /// <summary>Uninstall all patches.</summary>
    public static void UninstallAll()
    {
        var harmony = new Harmony(ModHarmonyId);
        harmony.UnpatchAll(ModHarmonyId);
        _installed = false;
        Log("[PatchRegistry] All patches uninstalled.");
    }

    /// <summary>Install a single patch class via CreateClassProcessor.</summary>
    public static void PatchAll(Type patchClass, string? harmonyId = null)
    {
        var harmony = new Harmony(harmonyId ?? ModHarmonyId);
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
