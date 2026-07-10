using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Godot;
using HarmonyLib;
using LocalCoop.Mod.Runtime;
using MegaCrit.Sts2.Core.Modding;
using TokenSpire2.Core;
using TokenSpire2.Coop;
using TokenSpire2.AutoBattle;
using TokenSpire2.Multiplayer;

namespace TokenSpire2;

/// <summary>
/// Entry point for the TokenSpire2 mod.
///
/// Architecture (v2 rewrite):
///   AppConfig          — single source of truth for all config
///   CoopManager        — backward-compat shim (delegates to AppConfig)
///   PatchRegistry      — centralized Harmony patch installer
///   AutoBattleController — new event-driven controller (skeleton, co-exists with old)
///   MpController       — multiplayer state machine (active in CoopMode)
///
/// Old AutoSlayNode is still attached for backward compatibility
/// until all handlers are migrated to IScreenHandler implementations.
/// </summary>
[ModInitializer(nameof(Initialize))]
public partial class MainFile : Node
{
    public const string ModId = "TokenSpire2";

    /// <summary>The game's built-in logger.</summary>
    public static MegaCrit.Sts2.Core.Logging.Logger? Logger { get; private set; }

    /// <summary>Whether the console window is available for debug output.</summary>
    public static bool ConsoleEnabled { get; private set; }

    public static void Initialize()
    {
        Logger = new(ModId, MegaCrit.Sts2.Core.Logging.LogType.Generic);
        ConsoleEnabled = TryAllocConsole();

        // ── Resolve mod directory ──────────────────────────────────────
        var modDirectory = Path.GetDirectoryName(typeof(MainFile).Assembly.Location) ?? ".";
        Logger.Info($"[TokenSpire2] Mod directory: {modDirectory}");

        // ── Step 1: Load unified config (coop_config.json + broker markers) ─
        AppConfig.Initialize(modDirectory);
        Logger.Info($"[TokenSpire2] AppConfig loaded. CoopMode={AppConfig.Instance.CoopMode}, " +
            $"IsHost={AppConfig.Instance.IsHost}, Broker={AppConfig.Instance.BrokerEnabled}");

        // ── Step 2: Init CoopManager as backward-compat shim ───────────
        CoopManager.Initialize(modDirectory);
        Logger.Info("[TokenSpire2] CoopManager initialized (compat shim).");

        // ── Step 3: Harmony patches ───────────────────────────────
        //
        // Install ALL patches via LocalCoopPatchInstaller.
        // This replaces both the old ModuleInitializer approach (which
        // wasn't called by the game's mod loader) and the fallback
        // PatchAll (which fails on broker patches with missing target
        // methods).
        //
        // Patch order: broker networking patches first, then dual-role
        // isolation patches.
        var assembly = typeof(MainFile).Assembly;
        var cfg = AppConfig.Instance;

        if (cfg.CoopMode && cfg.BrokerEnabled)
        {
            // ── Broker Mode: install ALL patches ──────────────────
            Logger.Info("[TokenSpire2] Broker mode — installing ALL patches.");
            var result = LocalCoopPatchInstaller.Install(assembly, msg =>
            {
                if (msg.Contains("failed", StringComparison.OrdinalIgnoreCase))
                    Logger.Error($"[TokenSpire2] {msg}");
                else
                    Logger.Info($"[TokenSpire2] {msg}");
            });

            if (result.Success)
                Logger.Info($"[TokenSpire2] All broker+dual patches installed successfully.");
            else
                Logger.Warn($"[TokenSpire2] {result.Failures.Count} patch(es) failed — continuing.");
            foreach (var f in result.Failures)
                Logger.Error($"[TokenSpire2] Patch failure: {f}");
        }
        else
        {
            // ── Single-player / Dual-instance mode: dual-role only ─
            Logger.Info("[TokenSpire2] Non-broker mode — installing dual-role patches only.");
            var result = LocalCoopPatchInstaller.InstallDualRoleOnly(assembly, msg =>
            {
                if (msg.Contains("failed", StringComparison.OrdinalIgnoreCase))
                    Logger.Error($"[TokenSpire2] {msg}");
                else
                    Logger.Info($"[TokenSpire2] {msg}");
            });

            if (result.Success)
                Logger.Info($"[TokenSpire2] Dual-role patches installed successfully.");
            else
                Logger.Warn($"[TokenSpire2] {result.Failures.Count} patch(es) failed — continuing.");
            foreach (var f in result.Failures)
                Logger.Error($"[TokenSpire2] Patch failure: {f}");
        }

        // ── Step 4: Attach runtime nodes to scene tree ─────────────────
        AttachNodes();

        if (ConsoleEnabled)
            Console.WriteLine("[TokenSpire2] Initialized successfully.\n");

        Logger.Info("[TokenSpire2] Initialization complete.");
    }

    /// <summary>
    /// Attach runtime nodes to the Godot scene tree.
    /// Old AutoSlayNode still runs the show. New controllers are
    /// attached as no-op skeletons until handlers are fully migrated.
    ///
    /// NOTE: There's also a Harmony patch (AttachAutoSlayNodePatch)
    /// that attaches AutoSlayNode to NGame._Ready. We check for
    /// existing instances to prevent double-attachment.
    /// </summary>
    private static void AttachNodes()
    {
        try
        {
            var mainLoop = Engine.GetMainLoop();
            if (mainLoop is not SceneTree sceneTree) return;
            var root = sceneTree.Root;
            if (root == null) return;

            // ── Legacy: AutoSlayNode (only if not already attached by Harmony patch) ─
            bool alreadyHasAutoSlay = false;
            foreach (var child in root.GetChildren())
            {
                if (child is AutoSlayNode)
                {
                    alreadyHasAutoSlay = true;
                    Logger?.Info("[TokenSpire2] AutoSlayNode already present (from Harmony patch).");
                    break;
                }
            }

            if (!alreadyHasAutoSlay)
            {
                var autoSlay = new AutoSlayNode();
                autoSlay.Name = "AutoSlayNode";
                root.AddChild(autoSlay);
                Logger?.Info("[TokenSpire2] AutoSlayNode attached directly (fallback).");
            }

            // ── New: AutoBattleController (no-op skeleton, ready for handler migration) ─
            var autoBattle = new AutoBattleController();
            autoBattle.Name = "AutoBattleController";
            root.AddChild(autoBattle);
            Logger?.Info("[TokenSpire2] AutoBattleController attached (new architecture skeleton).");

            // ── Config UI (in-game overlay for toggling settings) ──────
            var configUI = new CoopConfigUI();
            configUI.Name = "CoopConfigUI";
            root.AddChild(configUI);
            Logger?.Info("[TokenSpire2] CoopConfigUI attached.");

            Logger?.Info($"[TokenSpire2] {root.GetChildCount()} nodes attached to scene root.");
        }
        catch (Exception ex)
        {
            Logger?.Error($"[TokenSpire2] Failed to attach nodes: {ex.Message}");
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Console allocation (for debug output)
    // ═══════════════════════════════════════════════════════════════

    private static bool TryAllocConsole()
    {
        try
        {
            if (!AllocConsole()) return false;
            Console.OutputEncoding = Encoding.UTF8;
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), Encoding.UTF8) { AutoFlush = true });
            Console.SetError(new StreamWriter(Console.OpenStandardError(), Encoding.UTF8) { AutoFlush = true });
            return true;
        }
        catch { return false; }
    }

    [DllImport("kernel32.dll")]
    private static extern bool AllocConsole();
}
