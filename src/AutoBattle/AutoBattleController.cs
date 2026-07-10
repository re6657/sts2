using System;
using Godot;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Settings;
using TokenSpire2.AutoBattle.Handlers;
using TokenSpire2.Core;
using Environment = System.Environment;

namespace TokenSpire2.AutoBattle;

/// <summary>
/// Top-level auto-battle controller. Attached to the scene tree root.
/// Replaces the monolithic AutoSlayNode with a clean delegation model:
///
///   1. Detect current screen via <see cref="ScreenDetector"/>
///   2. Check stuck detection via <see cref="StuckDetector"/>
///   3. Dispatch to the appropriate <see cref="IScreenHandler"/>
///      via <see cref="ScreenDispatcher"/>
///
/// Pause toggle (T-key), seed override, FTUE skip, and other
/// one-time setup are handled here or in specialized handlers.
/// </summary>
public partial class AutoBattleController : Node
{
    // ═══════════════════════════════════════════════════════════════
    // Dependencies
    // ═══════════════════════════════════════════════════════════════

    private readonly ScreenDispatcher _dispatcher = new();
    private readonly StuckDetector _stuckDetector = new();
    private readonly RunContext _runContext = new();

    // ═══════════════════════════════════════════════════════════════
    // State
    // ═══════════════════════════════════════════════════════════════

    private double _nextActionAt;
    private bool _ftueDisabled;
    private bool _hpBoosted;
    private string? _seed;
    private string? _character;
    private double _logTimer;

    /// <summary>
    /// Set to true once AutoSlayNode is retired and the new architecture
    /// takes over. Until then, screen dispatch is disabled to prevent
    /// double-click conflicts with the old controller.
    /// </summary>
    public static bool NewArchitectureEnabled { get; set; }

    // ═══════════════════════════════════════════════════════════════
    // Godot lifecycle
    // ═══════════════════════════════════════════════════════════════

    public override void _Ready()
    {
        Name = "AutoBattleController";

        // Configure stuck detector
        var cfg = AppConfig.Instance;
        _stuckDetector.IsHumanPlayer = cfg.IsHumanPlayer;
        _stuckDetector.NeverKill = cfg.CoopMode;
        _stuckDetector.OnBeforeKill += OnBeforeStuckKill;

        // Load seed/character from config
        _seed = cfg.Seed;
        _character = cfg.Character;

        // Register screen handlers (populated in later phases)
        RegisterHandlers();

        Log("[AutoBattleController] Ready.");
    }

    public override void _Process(double delta)
    {
        // ── One-time setup ──────────────────────────────────────
        if (!_ftueDisabled)
        {
            DisableFtue();
            _ftueDisabled = true;
            return;
        }

        // ── Seed override ───────────────────────────────────────
        ApplySeedOverride();

        // ── HP boost (once per run) ─────────────────────────────
        if (!_hpBoosted)
        {
            ApplyHpBoost();
            _hpBoosted = true;
        }

        // ── Pause toggle (T-key) ────────────────────────────────
        if (Input.IsKeyPressed(Key.T) && !_tKeyWasDown)
        {
            bool paused = AppConfig.Instance.TogglePause();
            Log(paused ? "[AutoBattle] PAUSED" : "[AutoBattle] RESUMED");
        }
        _tKeyWasDown = Input.IsKeyPressed(Key.T);

        if (AppConfig.Instance.AutoBattlePaused)
            return;

        // ── Cooldown ────────────────────────────────────────────
        if (_nextActionAt > 0)
        {
            _nextActionAt -= delta;
            if (_nextActionAt > 0) return;
        }

        // ── Guard: don't dispatch until old AutoSlayNode is retired ──
        if (!NewArchitectureEnabled)
            return;

        // ── Screen detection ────────────────────────────────────
        var screen = ScreenDetector.Detect();

        // ── Stuck check ─────────────────────────────────────────
        if (!AppConfig.Instance.CoopMode && !AppConfig.Instance.IsHumanPlayer)
        {
            var stuckResult = _stuckDetector.Update(delta);
            if (stuckResult == StuckResult.KillProcess)
                return; // process will be killed
        }

        // ── Dispatch to handler ─────────────────────────────────
        double delay = _dispatcher.Dispatch(screen, delta);
        _nextActionAt = Math.Max(delay, 0.05); // minimum 50ms between actions

        // ── Periodic logging ────────────────────────────────────
        _logTimer -= delta;
        if (_logTimer <= 0)
        {
            _logTimer = 30.0;
            Log($"[AutoBattle] Screen={screen}, Handler={_dispatcher.ActiveHandler?.GetType().Name ?? "none"}");
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Handler registration (populated as handlers are migrated)
    // ═══════════════════════════════════════════════════════════════

    private void RegisterHandlers()
    {
        // ── Main menu (currently handled by legacy AutoSlayNode) ──
        _dispatcher.Register(new MainMenuHandler());

        // ── Room-level screens ────────────────────────────────────
        _dispatcher.Register(new CombatHandlerAdapter());
        _dispatcher.Register(new MapHandlerAdapter());
        _dispatcher.Register(new EventRoomHandlerAdapter());
        _dispatcher.Register(new TreasureRoomHandlerAdapter());
        _dispatcher.Register(new RestSiteHandlerAdapter());
        _dispatcher.Register(new ShopHandlerAdapter());

        // ── Overlay screens ───────────────────────────────────────
        _dispatcher.Register(new GameOverHandlerAdapter());
        _dispatcher.Register(new CombatVictoryHandler());
        _dispatcher.Register(new RewardsHandlerAdapter());
        _dispatcher.Register(new CardRewardHandlerAdapter());
        _dispatcher.Register(new ChooseCardHandlerAdapter());
        _dispatcher.Register(new BundleHandlerAdapter());
        _dispatcher.Register(new RelicHandlerAdapter());
        _dispatcher.Register(new CardGridHandlerAdapter());
        _dispatcher.Register(new SimpleCardSelectHandlerAdapter());
        _dispatcher.Register(new CrystalSphereHandlerAdapter());
    }

    // ═══════════════════════════════════════════════════════════════
    // One-time setup
    // ═══════════════════════════════════════════════════════════════

    private void DisableFtue()
    {
        try
        {
            SaveManager.Instance.SetFtuesEnabled(false);
            SaveManager.Instance.PrefsSave.FastMode = FastModeType.Fast;
            UnlockAll();
            Log("[AutoBattle] FTUEs disabled, fast mode enabled, all content unlocked.");
        }
        catch (Exception ex)
        {
            Log($"[AutoBattle] FTUE disable failed: {ex.Message}");
        }
    }

    private static void UnlockAll()
    {
        try
        {
            var progress = SaveManager.Instance.Progress;

            // Unlock all cards, relics, potions, events
            foreach (var card in ModelDb.AllCards)
                progress.MarkCardAsSeen(card.Id);
            foreach (var relic in ModelDb.AllRelics)
                progress.MarkRelicAsSeen(relic.Id);
            foreach (var potion in ModelDb.AllPotions)
                progress.MarkPotionAsSeen(potion.Id);
            foreach (var evt in ModelDb.AllEvents)
                progress.MarkEventAsSeen(evt.Id);

            // Unlock max ascension for all characters
            try
            {
                foreach (var character in ModelDb.AllCharacters)
                {
                    try
                    {
                        var stats = progress.GetOrCreateCharacterStats(character.Id);
                        stats.MaxAscension = 10;
                    }
                    catch (Exception ex)
                    {
                        Log($"[AutoBattle] Ascension unlock failed: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"[AutoBattle] Ascension unlock (non-critical): {ex.Message}");
            }

            SaveManager.Instance.SaveProgressFile();
            Log("[AutoBattle] All content unlocked: cards, relics, potions, events, ascension.");
        }
        catch (Exception ex)
        {
            Log($"[AutoBattle] UnlockAll failed: {ex.Message}");
        }
    }

    private void ApplySeedOverride()
    {
        if (string.IsNullOrEmpty(_seed)) return;
        try
        {
            if (NGame.Instance != null && NGame.Instance.DebugSeedOverride != _seed)
            {
                NGame.Instance.DebugSeedOverride = _seed;
                Log($"[AutoBattle] Seed override set to {_seed}");
            }
        }
        catch { /* NGame may not exist yet */ }
    }

    private void ApplyHpBoost()
    {
        // Placeholder — actual HP boost logic from AutoSlayNode
    }

    // ═══════════════════════════════════════════════════════════════
    // Stuck recovery
    // ═══════════════════════════════════════════════════════════════

    private void OnBeforeStuckKill(string reason)
    {
        _stuckDetector.WriteDiagnostics(reason, _stuckDetector.CombatStuckTimeoutSeconds);
        Log($"[AutoBattle] STUCK: {reason} — killing process");
        try { System.Diagnostics.Process.GetCurrentProcess().Kill(); }
        catch { Environment.Exit(1); }
    }

    // ═══════════════════════════════════════════════════════════════
    // State tracking (T-key debounce)
    // ═══════════════════════════════════════════════════════════════

    private bool _tKeyWasDown;

    // ═══════════════════════════════════════════════════════════════
    // Logging
    // ═══════════════════════════════════════════════════════════════

    private static void Log(string msg)
    {
        try { MainFile.Logger?.Info(msg); }
        catch { /* logging unavailable */ }
    }
}
