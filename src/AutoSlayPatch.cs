using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using LocalCoop.Mod.Runtime;
using MegaCrit.Sts2.Core.Nodes;

namespace TokenSpire2;

// Attach AutoSlayNode to the game tree when NGame is ready.
[HarmonyPatch(typeof(NGame), "_Ready")]
public static class AttachAutoSlayNodePatch
{
    private static bool _brokerInitialized;

    static void Postfix(NGame __instance)
    {
        try
        {
            Console.WriteLine("[AutoSlay] Harmony patch fired! Attaching node to NGame...");
            var node = new AutoSlayNode();
            node.Name = "AutoSlayNode";
            __instance.AddChild(node);
            Console.WriteLine("[AutoSlay] Node added to NGame successfully.");
        }
        catch (System.Exception ex)
        {
            Console.WriteLine($"[AutoSlay] Patch FAILED: {ex.GetType().Name}: {ex.Message}");
        }
    }
}

/// <summary>
/// Module initializer that runs when the assembly loads.
/// Installs all broker transport patches and starts broker mode if enabled.
/// This replaces the old MainFile.cs initialization that was removed.
/// </summary>
public static class TokenSpire2ModuleInit
{
    private static bool _initialized;

    /// <summary>
    /// Runs automatically when the .NET assembly is loaded.
    /// Installs all broker transport patches (the old MainFile.cs init path).
    /// This ensures broker patches are active regardless of which mod loader is used.
    /// </summary>
    [ModuleInitializer]
    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;

        try
        {
            var assembly = typeof(TokenSpire2ModuleInit).Assembly;
            var modDirectory = System.IO.Path.GetDirectoryName(assembly.Location);
            if (string.IsNullOrWhiteSpace(modDirectory)) return;

            // Log to console first (mod logger may not be available yet)
            Console.WriteLine($"[TokenSpire2] Module init: modDirectory={modDirectory}");

            // 1. Install assembly resolver for dependency loading
            LocalModAssemblyResolver.Install(assembly);

            // 2. Detect mode: SteamFix64 vs Broker vs Single-player
            bool isSteamFix64 = DetectSteamFix64Mode(modDirectory);
            var settings = BrokerModeSettings.LoadFromDirectory(modDirectory);
            var log = new BrokerEventLog(settings.EventLogPath);

            if (isSteamFix64)
            {
                // ── SteamFix64 Mode ──────────────────────────────────
                // The native DLL proxy (winmm.dll → SteamFix64.dll) handles
                // Steam API interception at the OS level. The game's built-in
                // multiplayer code runs unmodified. We only need dual-role
                // isolation patches — NO broker networking patches.
                log.Write("SteamFix64 mode: active. Installing dual-role patches only.");
                Console.WriteLine("[TokenSpire2] SteamFix64 mode: ACTIVE (dual-role patches only)");

                var patchResult = LocalCoopPatchInstaller.InstallDualRoleOnly(assembly, msg =>
                {
                    log.Write(msg);
                    Console.WriteLine($"[TokenSpire2] {msg}");
                });

                if (!patchResult.Success)
                {
                    foreach (var failure in patchResult.Failures)
                        Console.WriteLine($"[TokenSpire2] ERROR: {failure}");
                }

                Console.WriteLine($"[TokenSpire2] SteamFix64 init complete. Patches: success={patchResult.Success}");
            }
            else if (!settings.Enabled)
            {
                var reason = settings.FailureReason ?? "marker not present";
                log.Write($"Broker mode disabled: {reason}.");
                Console.WriteLine($"[TokenSpire2] Broker mode: DISABLED ({reason})");

                // Still install dual-role patches — they're harmless in
                // single-player and needed for potential dual-instance use.
                var patchResult = LocalCoopPatchInstaller.InstallDualRoleOnly(assembly, msg =>
                {
                    log.Write(msg);
                    Console.WriteLine($"[TokenSpire2] {msg}");
                });

                if (!patchResult.Success)
                {
                    foreach (var failure in patchResult.Failures)
                        Console.WriteLine($"[TokenSpire2] ERROR: {failure}");
                }

                Console.WriteLine($"[TokenSpire2] Module init complete (dual-role only). success={patchResult.Success}");
            }
            else
            {
                // ── Broker Mode ─────────────────────────────────────
                log.Write(
                    $"Broker mode enabled: clientId={settings.ClientId} role={settings.Config!.Role} " +
                    $"endpoint={settings.Config.Host}:{settings.Config.Port} sessionId={settings.Config.SessionId}.");
                Console.WriteLine($"[TokenSpire2] Broker mode: ENABLED role={settings.Config!.Role}");

                // Install ALL patches (broker networking + dual-role isolation)
                var patchResult = LocalCoopPatchInstaller.Install(
                    assembly,
                    msg =>
                    {
                        log.Write(msg);
                        Console.WriteLine($"[TokenSpire2] {msg}");
                    });

                if (!patchResult.Success)
                {
                    foreach (var failure in patchResult.Failures)
                        Console.WriteLine($"[TokenSpire2] ERROR: {failure}");
                }

                Console.WriteLine($"[TokenSpire2] Module init complete. Patches installed: success={patchResult.Success}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TokenSpire2] Module init FAILED: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Detect SteamFix64 mode: CoopMode=true in coop_config.json AND
    /// no broker marker files present. In this mode, the native SteamFix64
    /// DLL proxy handles all networking and we only need dual-role patches.
    /// </summary>
    private static bool DetectSteamFix64Mode(string modDirectory)
    {
        try
        {
            var coopPath = System.IO.Path.Combine(modDirectory, "coop_config.json");
            if (!System.IO.File.Exists(coopPath)) return false;

            var json = System.IO.File.ReadAllText(coopPath);
            // Quick check: does it contain "CoopMode": true?
            if (!json.Contains("\"CoopMode\": true", StringComparison.OrdinalIgnoreCase)
                && !json.Contains("\"CoopMode\":true", StringComparison.OrdinalIgnoreCase))
                return false;

            // Verify no broker marker files exist
            var hostMarker = System.IO.Path.Combine(modDirectory, "enable-local-broker-host.txt");
            var clientMarker = System.IO.Path.Combine(modDirectory, "enable-local-broker-client.txt");
            var sharedMarker = System.IO.Path.Combine(modDirectory, "enable-local-broker.txt");

            if (System.IO.File.Exists(hostMarker) || System.IO.File.Exists(clientMarker) || System.IO.File.Exists(sharedMarker))
                return false; // Broker markers exist → broker mode

            return true; // CoopMode=true + no broker markers → SteamFix64 mode
        }
        catch
        {
            return false;
        }
    }
}
