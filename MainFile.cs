using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;
using TokenSpire2.Core;

namespace TokenSpire2;

/// <summary>
/// Entry point for the TokenSpire2 mod.
///
/// Architecture:
///   AppConfig          — single source of truth for all config
///   AutoSlayNode       — main bot controller (_Process loop)
///   AutoBattleController — event-driven controller (skeleton)
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

        // ── Step 1: Load unified config ────────────────────────────────
        AppConfig.Initialize(modDirectory);
        Logger.Info($"[TokenSpire2] AppConfig loaded. AutoBattleEnabled={AppConfig.Instance.AutoBattleEnabled}");

        // ── Step 2: Initialize card database ─────────────────────────
        TokenSpire2.Solver.CardDatabase.Initialize(modDirectory);
        Logger.Info("[TokenSpire2] CardDatabase initialized.");

        // ── Step 2.5: Initialize AI chat systems ──────────────────────
        TokenSpire2.Chat.AiChatConfig.Initialize(modDirectory);
        TokenSpire2.Chat.CharacterProfileManager.Initialize(modDirectory);
        Logger.Info("[TokenSpire2] AI Chat systems initialized.");

        // ── Step 3: Install Harmony patches ────────────────────────────
        var harmony = new Harmony("TokenSpire2");
        harmony.PatchAll(typeof(MainFile).Assembly);
        Logger.Info("[TokenSpire2] Harmony patches installed.");

        // ── Step 4: Attach runtime nodes to scene tree ─────────────────
        AttachNodes();

        if (ConsoleEnabled)
            Console.WriteLine("[TokenSpire2] Initialized successfully.\n");

        Logger.Info("[TokenSpire2] Initialization complete.");
    }

    /// <summary>
    /// Attach runtime nodes to the Godot scene tree.
    /// </summary>
    private static void AttachNodes()
    {
        try
        {
            var mainLoop = Engine.GetMainLoop();
            if (mainLoop is not SceneTree sceneTree) return;
            var root = sceneTree.Root;
            if (root == null) return;

            // ── AutoSlayNode (only if not already attached by Harmony patch) ─
            bool alreadyHasController = false;
            foreach (var child in root.GetChildren())
            {
                if (child is AutoPlayController)
                {
                    alreadyHasController = true;
                    Logger?.Info("[TokenSpire2] AutoPlayController already present (from Harmony patch).");
                    break;
                }
            }

            if (!alreadyHasController)
            {
                var controller = new AutoPlayController();
                controller.Name = "AutoPlayController";
                root.AddChild(controller);
                Logger?.Info("[TokenSpire2] AutoPlayController attached directly (fallback).");
            }

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
