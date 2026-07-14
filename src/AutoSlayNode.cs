using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Events;
using MegaCrit.Sts2.Core.Nodes.Events.Custom.CrystalSphere;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Nodes.Screens.GameOverScreen;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;
using MegaCrit.Sts2.Core.Nodes.Screens.TreasureRoomRelic;
using MegaCrit.Sts2.Core.Nodes.RestSite;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Settings;
using MegaCrit.Sts2.Core.Timeline;
using MegaCrit.Sts2.Core.Unlocks;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Nodes.Vfx;
using TokenSpire2.Chat;
using TokenSpire2.Handlers;
using TokenSpire2.Llm;
using TokenSpire2.Solver;
using TokenSpire2.Core;
using TokenSpire2.Patches;
using System.Threading;

namespace TokenSpire2;

public partial class AutoSlayNode : Node
{
    private double _cooldown;
    private double _combatCardDelay;
    private double _logTimer;
    private bool _ftueDisabled;
    private bool _hpBoosted;
    private bool _debugMaxHp = false;  // set to true for optimizer: max HP 999, evaluate via HP loss not win/loss
    private bool _paused;
    private bool _tWasDown;
    // ── Feature toggles: F1/F2/F3 ──────────────────────────────────────
    private bool _autoNavigate;  // F1: auto map pathing + rest/shop decisions
    private bool _autoBattle;    // F2: auto combat (play cards + end turn)
    private bool _autoEvent;     // F3: auto event choices + card rewards/picks
    private bool _f1WasDown, _f2WasDown, _f3WasDown;

    // ── Unified host manual-mode detection ──────────────────────────────
    // When ANY toggle is OFF for the host in multiplayer, ALL auto-decisions
    // (card removal, upgrades, events, map navigation, etc.) must be manual.
    // This replaces scattered (_multiplayerMode && _isMultiplayerHost && !_autoBattle)
    // checks that previously only looked at the combat toggle.
    private bool IsFullAuto => _autoNavigate && _autoBattle && _autoEvent;
    private bool IsHostManualMode => _multiplayerMode && _isMultiplayerHost && !IsFullAuto;
    private IDisposable? _cardSelectorScope;
    private AutoSlayCardSelector? _cardSelector;
    private readonly System.Random _rng = new();
    private long _debugFrame;
    private double _dbgTimer;
	private double _delta;

    // Stuck detection: if no combat/card play for 60s, kill game
    private double _lastCombatActivity = -1;

    private const double STUCK_TIMEOUT = 45.0;       // kill if stuck >45s in combat (was 30s, too aggressive for long enemy turns)
    private const double NONCOMBAT_STUCK_TIMEOUT = 45.0; // kill if stuck >45s on same non-combat screen
    private const double COMBAT_NONCOMBAT_STUCK_TIMEOUT = 90.0; // kill if stuck >90s on combat screen (backup for combat stuck)
    private const double NON_COMBAT_DECISION_TIMEOUT = 30.0; // 30s timeout → fall back to random control

    // Non-combat stuck detection: track screen/overlay changes
    private string _lastScreenType = "";
    private double _sameScreenDuration;
    private int _sameScreenTickCount;

    // ── Empty solve retry safety net ─────────────────────────────────────
    private int _emptySolveRetries;         // consecutive empty solver results
    private const int MAX_EMPTY_SOLVE_RETRIES = 4; // retry before giving up

    // Static ctor runs when the DLL is first loaded — confirms code is live.
    static AutoSlayNode()
    {
        Console.WriteLine("[AutoSlay] DLL loaded (static ctor)");
    }

    private string _seed = "";
    private string _character = "SILENT";
    private float _hpMultiplier = 1.0f;
    private string _batchRunNumber = "1";
    private bool _batchMode;
    private bool _disableAutoPlay;
    private bool _runCompleteSignaled;
    private static readonly string[] ValidCharacters = { "IRONCLAD", "SILENT", "DEFECT", "REGENT", "NECROBINDER" };

    // ── Multi-seed rotation ─────────────────────────────────────────────────
    private List<string> _seeds = new();
    private int _currentSeedIndex;

    // LLM state
    private LlmClient? _llm;
    private Task<string>? _pendingLlm;
    private string? _pendingContext; // "combat", "map", "overlay:TypeName", "event", "restsite"
    private double _proceedTimer = -1; // timeout for audio proceed after LLM completes (-1 = not started)
    private int _llmFailCount; // consecutive LLM failures for current context
    private string? _lastFailedContext; // which context was failing
    private List<CombatAction>? _combatPlan;
    private int _combatPlanStep;
    private int _consecutiveRechecks; // track repeated end-turn cancellations to prevent loops
    private int _unknownOverlayRetries; // track repeated unknown overlay dismiss attempts
    private int _lastTurnEnergySpent; // actual energy spent this turn (for logging)
    private int _lastTurnActionCount; // actual cards played this turn (for logging)
    private int _lastTurnTotalPlayable; // total playable cards last turn
    private int _lastTurnPlayableNotPlayed; // playable cards not played last turn
    private bool _combatTurnRequested; // prevent re-requesting same turn
    private double _combatTurnRequestedDuration; // watchdog: seconds since _combatTurnRequested=true without PlayerActionsDisabled
    private bool _combatPlanEndTurn; // true if LLM explicitly said END_TURN after plays
    private bool _drawJustFinished; // hand was empty, now has cards — wait for full draw to stabilize
    private int _drawStableCount;   // consecutive frames with same handCount — draw is complete when >= 3
    private int _lastDrawHandCount; // handCount from previous frame
    private int _expectedDrawCount; // expected draw count (5 base, modified by powers/relics)
    private List<int>? _shopPlan; // list of item indices to buy
    private int _shopPlanStep;
    private bool _shopInventoryOpened;
    private bool _shopLeaving;
    private bool _gameOverReflected;
    private bool _abandonPending;   // true after clicking abandon, waiting for it to complete
    private bool _runEverStarted;   // true once we've successfully entered a run
    private int _rewardCardChoice; // card choice from rewards screen, -1 = skip
    private Queue<NRewardButton>? _rewardTakeQueue; // queued reward button refs to click
    private bool _rewardsLlmDone; // true after LLM plan has been executed for current rewards screen
    private bool _restSiteChoiceMade; // true after LLM rest site choice is executed
    private int _restStuckFrames;     // stuck detection for rest card grid / proceed loop
    private int _shopStuckFrames;    // stuck detection for shop leaving state
    private int _combatPlanStuckFrames; // stuck detection for combat plan step execution
    private int _turnPlansWithoutPlay;  // count plans created this turn without a successful card play
    // Battle logging
    private int _combatTurnNumber;
    private bool _wasInCombat;
    private bool _wasInRest;       // track rest site transitions for state reset
    private bool _wasInTreasure;  // track treasure room transitions for state reset
    private bool _postCombatCooldownLogged; // diagnostic: track if _cooldown is blocking post-combat nav
    private int _dismissProceedClicks;      // backoff counter: repeated Proceed clicks in TryClickDismissInModal
    private string? _lastDismissBtnNames;    // last button names seen in TryClickDismissInModal (for reset detection)
    private string? _lastHeartbeatScreen;    // only log heartbeat on screen change (log noise reduction)
    private string? _lastInCombatState;      // only log InCombat DBG on state change (log noise reduction)
    private string? _lastLogStateSummary;    // only log Tick/LogState on state change (log noise reduction)
    private HashSet<string> _recentLogs = new(); // last N unique messages for better dedup
    private bool _wasEnemyTurn;     // track enemy→player turn transitions for stuck timer
    private double _playerDisabledDuration; // MP watchdog: seconds spent with PlayerActionsDisabled=true
    private int _panicButtonTurnsRemaining; // PANIC_BUTTON debuff: turns of "no block from cards" remaining
    private int _lastTurnHp, _lastTurnBlock, _lastTurnMaxHp;
    private int _lastTurnAliveEnemyCount;

    // ── 5-second retry cycle for multiplayer combat ─────────────────────
    // After the bot ends its turn in MP, other players may still be playing.
    // The host might give the bot energy/cards via ally-targeting effects.
    // Every 5s, we cancel the end-turn, re-solve, and play any newly
    // available cards. If nothing is playable, we re-end the turn.
    private double _mpPostEndTurnTimer;            // seconds since last end-turn or retry
    private const double MP_END_TURN_RETRY = 5.0;  // retry interval in seconds
    private bool _mpEndTurnRetryActive;            // true when retry cycle is running
    private int _mpPostPlanEmptyCount;             // consecutive empty solver results after plan
    private const int MP_MAX_POST_PLAN_EMPTY = 3;  // end turn after this many empty re-solves

    // ── Multiplayer mode ────────────────────────────────────────────
    private bool _multiplayerMode;
    private bool _isMultiplayerHost;
    private bool _multiplayerJoined;
    private bool _multiplayerReady;

    // ── Auto-chat AI dialogue system (shared conversation) ──────────
    // Bots share one conversation via ConversationManager (file-based log).
    // Each bot polls the shared log, checks if it's their turn, and generates
    // 1-2 lines that continue the conversation naturally.
    private double _lastMeowTime;
    private ChatEngine? _chatEngine;
    private string _aiChatCharacter = "";
    private string _aiChatDisplayName = "";    // cached for logging
    private double _chatMyTurnCooldown = 3.0;  // seconds to wait AFTER my own message
    private double _chatPollInterval = 1.8;    // seconds between checking the shared log
    private bool _aiChatInitialized;
    private bool _conversationInitialized;

    public override void _Ready()
    {
        Console.WriteLine("[AutoSlay] _Ready() called! Node entering tree...");
        // Always activate — this is the whole point of the mod.
        // (was: bool active = CommandLineHelper.HasArg("autoslay"))
        bool active = true;
        SetProcess(active);

        // LLM permanently disabled — solver-only mode.
        // _llm stays null; all if(_llm!=null) guards fall through to handlers.

        // ── Nuclear: delete any stale save to prevent "continue?" prompts ──
        DeleteStaleSaveFile();

        // ── Batch config: read seed/character/hpMultiplier from JSON ──────
        LoadBatchConfig();

        // ── Multiplayer config: read from AppConfig ────────────────────
        if (AppConfig.IsInitialized)
        {
            var cfg = AppConfig.Instance;
            _multiplayerMode = cfg.MultiplayerMode;
            _isMultiplayerHost = cfg.IsMultiplayerHost;

            // ── Character override from AppConfig ────────────────────────
            // In multiplayer mode, the per-instance config (--config path)
            // specifies the character. This takes precedence over the old
            // batch_config.json loaded by LoadBatchConfig() above.
            if (_multiplayerMode && !string.IsNullOrEmpty(cfg.Character))
            {
                _character = cfg.Character;
                MainFile.Logger.Info($"[AutoSlay] Multiplayer character override: {_character}");
            }
            if (_multiplayerMode)
            {
                MainFile.Logger.Info($"[AutoSlay] Multiplayer mode: IsHost={_isMultiplayerHost}, PersonaName={cfg.SteamPersonaName}");
                // ── Feature toggle defaults ──────────────────────────────
                // Client (bot): all auto features ON
                // Host (human): all OFF, can toggle ON with F1/F2/F3
                if (_isMultiplayerHost)
                {
                    _autoNavigate = false;
                    _autoBattle = false;
                    _autoEvent = false;
                }
                else
                {
                    _autoNavigate = true;
                    _autoBattle = true;
                    _autoEvent = true;
                }
                MainFile.Logger.Info($"[AutoSlay] Toggles: Nav={_autoNavigate} Battle={_autoBattle} Event={_autoEvent} (F1/F2/F3 to change)");
            }
            else
            {
                // Singleplayer: all ON
                _autoNavigate = true;
                _autoBattle = true;
                _autoEvent = true;
            }

            // ── AI Chat initialization ───────────────────────────────────
            if (cfg.AiChatEnabled && !string.IsNullOrEmpty(cfg.AiChatCharacter))
            {
                _aiChatCharacter = cfg.AiChatCharacter;
                InitializeAiChat();
            }
        }

        // Register card selector for mid-combat card selections (e.g. Armaments).
        _cardSelector = new AutoSlayCardSelector(_rng, null);
        _cardSelectorScope = CardSelectCmd.UseSelector(_cardSelector);

        MainFile.Logger.Info("[AutoSlay] SOLVER-ONLY mode active. No LLM, no random play.");
        BattleLogger.Enable();
        DecisionLogger.Enable();
    }

    public override void _ExitTree()
    {
        _cardSelectorScope?.Dispose();
        _cardSelectorScope = null;
    }

    /// <summary>
    /// Load batch test configuration from batch_config.json.
    /// Sets seed, character, and HP multiplier for the run.
    /// If the file doesn't exist or is invalid, uses defaults (random seed, IRONCLAD, 1x HP).
    /// </summary>
    private void LoadBatchConfig()
    {
        try
        {
            var asmDir = System.IO.Path.GetDirectoryName(
                System.Reflection.Assembly.GetExecutingAssembly().Location);
            if (asmDir == null) return;
            var path = System.IO.Path.Combine(asmDir, "batch_config.json");
            if (!System.IO.File.Exists(path))
            {
                MainFile.Logger.Info("[AutoSlay] No batch_config.json — using random seed, IRONCLAD, 1x HP");
                return;
            }
            var json = System.IO.File.ReadAllText(path);
            var cfg = System.Text.Json.JsonSerializer.Deserialize<BatchConfig>(json,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (cfg == null) return;

            if (!string.IsNullOrWhiteSpace(cfg.Seed))
            {
                _seed = cfg.Seed.Trim();
                _batchMode = true;
                MainFile.Logger.Info($"[AutoSlay] Batch seed: {_seed}");
            }
            if (!string.IsNullOrWhiteSpace(cfg.Character))
            {
                var ch = cfg.Character.Trim().ToUpperInvariant();
                if (ValidCharacters.Contains(ch))
                {
                    _character = ch;
                    MainFile.Logger.Info($"[AutoSlay] Batch character: {_character}");
                }
            }
            if (cfg.HpMultiplier > 0.1f && cfg.HpMultiplier <= 10f)
            {
                _hpMultiplier = cfg.HpMultiplier;
                MainFile.Logger.Info($"[AutoSlay] Batch HP multiplier: {_hpMultiplier}");
            }
            if (!string.IsNullOrWhiteSpace(cfg.RunNumber))
            {
                _batchRunNumber = cfg.RunNumber.Trim();
                BossPlayLogger.RunNumber = _batchRunNumber;
                MainFile.Logger.Info($"[AutoSlay] Batch run number: {_batchRunNumber}");
            }
            // Always sync character/seed for boss logging even in non-batch mode
            BossPlayLogger.Character = _character;
            BossPlayLogger.Seed = _seed;
            if (cfg.DisableAutoPlay)
            {
                _disableAutoPlay = true;
                MainFile.Logger.Info("[AutoSlay] Auto-play DISABLED — manual mode with all unlocks");
            }
        }
        catch (System.Exception ex)
        {
            MainFile.Logger.Info($"[AutoSlay] Failed to load batch_config.json: {ex.Message}");
        }

        // Fallback: check for DISABLE_AUTO_PLAY sentinel file (no JSON parsing needed)
        try
        {
            var asmDir2 = System.IO.Path.GetDirectoryName(
                System.Reflection.Assembly.GetExecutingAssembly().Location);
            if (asmDir2 != null)
            {
                var sentinel = System.IO.Path.Combine(asmDir2, "DISABLE_AUTO_PLAY");
                if (System.IO.File.Exists(sentinel))
                {
                    _disableAutoPlay = true;
                    MainFile.Logger.Info("[AutoSlay] DISABLE_AUTO_PLAY sentinel found — manual mode");
                }
            }
        }
        catch { }
    }

    private class BatchConfig
    {
        public string Seed { get; set; } = "";
        public string Character { get; set; } = "";
        public float HpMultiplier { get; set; } = 1.0f;
        public bool DisableAutoPlay { get; set; }
        public string RunNumber { get; set; } = "1";
    }

    public override void _Process(double delta)
    {
        _delta = delta;

        // ── Pause/Resume toggle (T key) ──────────────────────────────────────
        bool tDown = Input.IsKeyPressed(Key.T);
        if (tDown && !_tWasDown)
        {
            _paused = !_paused;
            if (_paused)
            {
                MainFile.Logger.Info("[AutoSlay] ⏸ PAUSED — press T to resume");
                if (MainFile.ConsoleEnabled) Console.WriteLine("\n⏸ [AutoSlay] PAUSED — AI bot stopped. Press T to resume.\n");
            }
            else
            {
                MainFile.Logger.Info("[AutoSlay] ▶ RESUMED — AI bot active");
                if (MainFile.ConsoleEnabled) Console.WriteLine("\n▶ [AutoSlay] RESUMED — AI bot active.\n");
            }
        }
        _tWasDown = tDown;

        // ── Feature toggles (F1/F2/F3) ──────────────────────────────────────
        bool f1Down = Input.IsKeyPressed(Key.F1);
        if (f1Down && !_f1WasDown)
        {
            _autoNavigate = !_autoNavigate;
            MainFile.Logger.Info($"[AutoSlay] F1: Auto-Navigate = {_autoNavigate}");
        }
        _f1WasDown = f1Down;

        bool f2Down = Input.IsKeyPressed(Key.F2);
        if (f2Down && !_f2WasDown)
        {
            _autoBattle = !_autoBattle;
            MainFile.Logger.Info($"[AutoSlay] F2: Auto-Battle = {_autoBattle}");
        }
        _f2WasDown = f2Down;

        bool f3Down = Input.IsKeyPressed(Key.F3);
        if (f3Down && !_f3WasDown)
        {
            _autoEvent = !_autoEvent;
            MainFile.Logger.Info($"[AutoSlay] F3: Auto-Event = {_autoEvent}");
        }
        _f3WasDown = f3Down;

        if (_paused) return;

        // ── Diagnostic heartbeat (every 5s, logs ONLY on screen change) ──
        _dbgTimer -= delta;
        if (_dbgTimer <= 0)
        {
            _dbgTimer = 5.0;
            try
            {
                var screen = GameStateDetector.Detect();
                string screenStr = screen.ToString();
                if (screenStr != _lastHeartbeatScreen)
                {
                    _lastHeartbeatScreen = screenStr;
                    MainFile.Logger?.Info($"[AutoSlay] ♥ heartbeat screen={screen}");
                }
            }
            catch (Exception ex)
            {
                MainFile.Logger?.Info($"[AutoSlay] ♥ heartbeat error: {ex.Message}");
            }
        }

        // Manual mode: run unlocks once then skip ALL auto-play logic
        if (_disableAutoPlay)
        {
            if (!_ftueDisabled && SaveManager.Instance != null)
            {
                SaveManager.Instance.SetFtuesEnabled(enabled: false);
                SaveManager.Instance.PrefsSave.FastMode = FastModeType.Fast;
                UnlockAll();
                _ftueDisabled = true;
                MainFile.Logger.Info("[AutoSlay] FTUEs disabled, fast mode enabled, all content unlocked.");
            }
            return;
        }

        // Wait for card selector's async LLM request to finish before doing anything
        if (_cardSelector?.IsPendingLlm == true)
            return;

        _cooldown -= delta;
        _combatCardDelay -= delta;
        _logTimer -= delta;

        // ── Multiplayer: dismiss any error popups (e.g. "Connection failed") ──
        // FastMpJoin() shows an error dialog if the host's ENet server isn't
        // ready yet. Dismiss it so the bot can retry.
        // IMPORTANT: respect _cooldown to avoid infinite click-loop on
        // NRewardsScreen ProceedButton in multiplayer (requires host sync).
        if (_multiplayerMode && !_isMultiplayerHost && _cooldown <= 0)
        {
            DismissContinuePrompt();
        }

        // ── Stuck detection: 60s without combat/play → kill game ──────
        bool inCombat = CombatManager.Instance?.IsInProgress == true;
        if (inCombat)
        {
            if (_lastCombatActivity < 0) _lastCombatActivity = 0; // init timer
            _lastCombatActivity += delta;
        }
        else
        {
            _lastCombatActivity = -1; // reset when not in combat
        }

        // Combat stuck detection — log and recover instead of killing game
        if (_lastCombatActivity > STUCK_TIMEOUT)
        {
            WriteStuckDiagnostics("COMBAT_STUCK", _lastCombatActivity);
            SignalRunComplete();
            MainFile.Logger.Error($"[AutoSlay] STUCK: {_lastCombatActivity:F0}s in combat without activity! Logging and recovering (game will NOT be killed)...");
            // ── Recovery: reset stuck timer and force-end current turn ──
            _lastCombatActivity = 0;
            _combatPlan = null;
            _combatTurnRequested = false;
            _combatTurnRequestedDuration = 0;
            _combatPlanStuckFrames = 0;
            try
            {
                var rs = RunManager.Instance?.DebugOnlyGetState();
                var pl = rs != null ? LocalContext.GetMe(rs) : null;
                if (CombatManager.Instance is { PlayerActionsDisabled: false } && pl != null)
                    EndTurnViaUiOrApi(pl);
            }
            catch { }
            _combatCardDelay = 0.5;
            return;
        }

        // ── Non-combat stuck detection ──────────────────────────────────────
        // Track the current screen/overlay type and detect if we're stuck
        // on the same screen for too long without progress.
        string currentScreenId = "";
        try
        {
            var screen = GameStateDetector.Detect();
            currentScreenId = screen.ToString();
            var ovs = NOverlayStack.Instance;
            if (ovs?.ScreenCount > 0)
            {
                var top = ovs.Peek();
                currentScreenId += "/" + (top?.GetType().Name ?? "?");
            }
        }
        catch { currentScreenId = "DETECT_FAILED"; }

        if (currentScreenId == _lastScreenType && currentScreenId != "NONE")
        {
            _sameScreenDuration += delta;
            _sameScreenTickCount++;
        }
        else
        {
            _lastScreenType = currentScreenId;
            _sameScreenDuration = 0;
            _sameScreenTickCount = 0;
        }

        // For COMBAT screens, use a longer timeout (90s vs 45s) because:
        // (1) combat has its own stuck detection via _lastCombatActivity (45s)
        // (2) boss fights can have genuinely long enemy turns
        // Non-combat screens use the standard 45s timeout.
        double effectiveNonCombatTimeout = _lastScreenType?.StartsWith("COMBAT") == true
            ? COMBAT_NONCOMBAT_STUCK_TIMEOUT : NONCOMBAT_STUCK_TIMEOUT;
        // ── Multiplayer guard: bot is expected to WAIT for human input ──
        // Don't kill the game when waiting at main menu for join.
        if (_multiplayerMode && !_isMultiplayerHost)
        {
            effectiveNonCombatTimeout = 300.0; // 5 minutes — human needs time
        }
        // Non-combat stuck detection — log and recover instead of killing game
        if (_sameScreenDuration > effectiveNonCombatTimeout && _sameScreenTickCount > 30)
        {
            WriteStuckDiagnostics("NONCOMBAT_STUCK", _sameScreenDuration);
            SignalRunComplete();
            MainFile.Logger.Error($"[AutoSlay] NON-COMBAT STUCK: {_lastScreenType} for {_sameScreenDuration:F0}s ({_sameScreenTickCount} ticks)! Logging and recovering (game will NOT be killed)...");
            // ── Recovery: reset stuck state and force a different action ──
            _lastScreenType = "";
            _sameScreenDuration = 0;
            _sameScreenTickCount = 0;
            _cooldown = 1.0; // give the game time to settle
            return;
        }

        // Keep seed override set (NGame.Instance may not exist at _Ready time)
        if (NGame.Instance != null && !string.IsNullOrEmpty(_seed) && NGame.Instance.DebugSeedOverride != _seed)
        {
            NGame.Instance.DebugSeedOverride = _seed;
            MainFile.Logger.Info($"[AutoSlay] Seed override set to {_seed}");
        }

        // Apply HP multiplier / debug max HP once per run
        if (!_hpBoosted)
        {
            var rs = RunManager.Instance?.DebugOnlyGetState();
            var p = rs != null ? LocalContext.GetMe(rs) : null;
            if (p?.Creature != null)
            {
                if (_debugMaxHp)
                {
                    p.Creature.SetMaxHpInternal(999);
                    p.Creature.SetCurrentHpInternal(999);
                    MainFile.Logger.Info($"[AutoSlay] DEBUG MaxHP set to 999 (all chars)");
                }
                else if (_hpMultiplier != 1.0f)
                {
                    int newMax = (int)(p.Creature.MaxHp * _hpMultiplier);
                    p.Creature.SetMaxHpInternal(newMax);
                    p.Creature.SetCurrentHpInternal(newMax);
                    MainFile.Logger.Info($"[AutoSlay] HP multiplied by {_hpMultiplier}: {newMax}");
                }
                _hpBoosted = true;
            }
        }

        // ── Check pending LLM call + wait for viewer proceed ─────────────────
        if (_pendingLlm != null)
        {
            if (!_pendingLlm.IsCompleted) return; // LLM still running

            if (_pendingLlm.IsFaulted)
            {
                var errMsg = _pendingLlm.Exception?.InnerException?.Message ?? "unknown";
                MainFile.Logger.Info($"[AutoSlay/LLM] Request failed: {errMsg}");
                var failedContext = _pendingContext; // Capture BEFORE nulling
                _pendingLlm = null;
                _pendingContext = null;
                _proceedTimer = -1;
                _combatTurnRequested = false; _combatTurnRequestedDuration = 0; // allow retry

                // Track consecutive failures to prevent infinite retry loop
                // (e.g. API key expired or insufficient balance)
                if (_lastFailedContext == failedContext)
                    _llmFailCount++;
                else
                    _llmFailCount = 1;
                _lastFailedContext = failedContext;

                if (_llmFailCount >= 3)
                {
                    MainFile.Logger.Info($"[AutoSlay/LLM] Too many failures ({_llmFailCount}) — disabling LLM for this session.");
                    _llm = null; // fall through to random/solver behavior
                    _llmFailCount = 0;
                    _cooldown = 0.5;
                }
                else
                {
                    _cooldown = 2.0;
                }
                return;
            }

            // LLM succeeded — reset failure count
            _llmFailCount = 0;
            _lastFailedContext = null;

            // LLM done — start proceed timer on first detection of completion
            if (_proceedTimer < 0)
            {
                _proceedTimer = 0.3;
                MainFile.Logger.Info("[AutoSlay/LLM] Response ready, waiting for viewer audio...");
            }

            // Wait for viewer audio proceed signal before executing
            _proceedTimer -= delta;
            if (CheckEnrichedProceed() || _proceedTimer <= 0)
            {
                if (_proceedTimer <= 0)
                    MainFile.Logger.Info("[AutoSlay] Audio wait timeout, proceeding anyway");
                try
                {
                    var response = _pendingLlm.Result;
                    var ctx = _pendingContext!;
                    _pendingLlm = null;
                    _pendingContext = null;
                    _proceedTimer = -1;
                    ExecuteLlmResult(ctx, response);
                }
                catch (Exception ex)
                {
                    MainFile.Logger.Info($"[AutoSlay/LLM] Error handling response: {ex.Message}");
                    _pendingLlm = null;
                    _pendingContext = null;
                    _proceedTimer = -1;
                    _cooldown = 1.0;
                }
            }
            else
            {
                _cooldown = 0.5;
            }
            return;
        }

        // ── Handle overlays that appear mid-combat (e.g. Headbutt card selection) ──
        var os3 = NOverlayStack.Instance;
        if (os3?.ScreenCount > 0 && CombatManager.Instance?.IsInProgress == true)
        {
            var overlay = os3.Peek();
            var overlayNode = overlay as Node;
            if (overlayNode != null)
            {
                // Reset stuck timer — we're handling an overlay, not stuck
                _lastCombatActivity = 0;
                if (!IsHostManualMode && _llm != null && TryRequestLlmForOverlay(overlayNode))
                    return;
                if (_cooldown <= 0)
                {
                    try { _cooldown = DispatchOverlay(overlayNode, delta); }
                    catch (Exception ex) { MainFile.Logger.Error($"[AutoSlay] Mid-combat DispatchOverlay CRASH: {ex.Message}"); _cooldown = 0.5; }
                    return;
                }
                return; // wait for cooldown before handling overlay
            }
        }

        // ── Execute combat plan steps ────────────────────────────────────────
        if (_combatPlan != null)
        {
            var cm2 = CombatManager.Instance;
            if (cm2 == null || !cm2.IsInProgress || cm2.PlayerActionsDisabled)
            {
                _combatPlan = null;
                _combatTurnRequested = false; _combatTurnRequestedDuration = 0;
                _combatPlanStuckFrames = 0;
                return;
            }
            if (_combatCardDelay > 0) return;

            ExecuteNextCombatStep();
            // Track every frame spent executing plan — if it runs too
            // long without the plan completing, force end turn.
            if (_combatPlan != null)
            {
                _combatPlanStuckFrames++;
                if (_combatPlanStuckFrames > 180 // ~6 seconds of plan execution
                    || _turnPlansWithoutPlay > 5) // or 6+ plans created without a successful card play
                {
                    MainFile.Logger.Error($"[AutoSlay] COMBAT PLAN STUCK: {_combatPlanStuckFrames} frames (step {_combatPlanStep}/{_combatPlan.Count}), {_turnPlansWithoutPlay} plans without play. Force-ending turn.");
                    _combatPlan = null;
                    _combatPlanStuckFrames = 0;
                    _turnPlansWithoutPlay = 0;  // reset — we forced progress
                    _combatTurnRequested = false; _combatTurnRequestedDuration = 0;
                    _lastCombatActivity = 0;     // actual progress
                    var rs = RunManager.Instance?.DebugOnlyGetState();
                    var pl = rs != null ? LocalContext.GetMe(rs) : null;
                    if (CombatManager.Instance is { PlayerActionsDisabled: false } && pl != null)
                        EndTurnViaUiOrApi(pl);
                    _combatCardDelay = 0.5;
                }
            }
            return;
        }
        _combatPlanStuckFrames = 0; // reset when no plan active

        // ── Combat (highest priority) ────────────────────────────────────────
        var cm = CombatManager.Instance;
        if (cm != null && cm.IsInProgress)
        {
            // ── Auto-chat: AI dialogue or meow every N seconds ───────────
            TrySendAiChat(delta);

            // Combat start detection
            if (!_wasInCombat)
            {
                _wasInCombat = true;
                _wasEnemyTurn = false; // reset enemy turn tracking for new combat
                _panicButtonTurnsRemaining = 0; // reset PANIC_BUTTON debuff for new combat
                _runEverStarted = true;  // confirmed: we entered a real run
                _combatTurnNumber = 0;
                _emptySolveRetries = 0;     // reset retry counter for new combat
                _consecutiveRechecks = 0;   // reset recheck counter for new combat
                _turnPlansWithoutPlay = 0;  // reset per-turn plan counter

                // ── Combat recorder: snapshot state for post-combat summary ──
                try { Chat.CombatRecorder.OnCombatStart(); } catch { }

                // ── Pre-generated dialogue: fire immediately at combat start ──
                var preGen = Chat.CombatRecorder.ConsumePreGeneratedDialogue();
                if (preGen != null && preGen.Count > 0)
                {
                    // Write directly to shared conversation log and fire pings
                    foreach (var line in preGen)
                    {
                        Chat.ConversationManager.Append(_aiChatDisplayName, line);
                        SendChatPing(line);
                    }
                    _lastMeowTime = 0; // start polling cycle
                    MainFile.Logger.Info($"[AutoSlay] Pre-generated dialogue: {preGen.Count} lines");
                }

                // DEBUG: heal to 999 at start of each combat (evaluate via HP loss)
                if (_debugMaxHp)
                {
                    try
                    {
                        var rs = RunManager.Instance?.DebugOnlyGetState();
                        var pl = rs != null ? LocalContext.GetMe(rs) : null;
                        if (pl?.Creature != null)
                        {
                            pl.Creature.SetMaxHpInternal(999);
                            pl.Creature.SetCurrentHpInternal(999);
                        }
                    }
                    catch { }
                }
                try
                {
                    var rs = RunManager.Instance?.DebugOnlyGetState();
                    var pl = rs != null ? LocalContext.GetMe(rs) : null;
                    int floor = rs?.TotalFloor ?? 0;
                    int asc = rs?.AscensionLevel ?? 0;
                    string encounter = cm.DebugOnlyGetState()?.Encounter?.Id.Entry ?? "Unknown";
                    BattleLogger.StartBattle(encounter, floor, _character, asc);
                    BossPlayLogger.StartBossFight(encounter, floor, _character, _batchRunNumber, _seed);

                    // ── Capture deck snapshot at boss fight start ─────
                    try
                    {
                        var deckState = new TokenSpire2.Solver.RunState();
                        deckState.Refresh();
                        BossPlayLogger.SetDeckSnapshot(
                            deckCardIds: deckState.DeckCardIds,
                            relicIds: deckState.RelicIds,
                            potionIds: deckState.PotionIds,
                            attackCount: deckState.AttackCount,
                            skillCount: deckState.SkillCount,
                            powerCount: deckState.PowerCount,
                            upgradedCount: deckState.CountUpgradedCards,
                            basicStrikeCount: deckState.CountBasicStrikes,
                            basicDefendCount: deckState.CountBasicDefends);
                    }
                    catch (Exception dkEx)
                    {
                        MainFile.Logger.Info($"[AutoSlay] Deck snapshot failed: {dkEx.Message}");
                    }

                    // New run detection: floor 0 means a fresh run just started
                    if (floor <= 1)
                        DecisionEngine.OnNewRun();
                }
                catch (Exception logEx)
                {
                    MainFile.Logger.Info($"[BattleLog] StartBattle failed: {logEx.Message}");
                }
            }

            // Rate-limited status every 0.5s
            _dbgTimer += delta;
            bool dbgLog = _dbgTimer >= 0.5;
            if (dbgLog)
            {
                _dbgTimer = 0.0;
                // In multiplayer, also log hand state to help diagnose card visibility issues
                string mpExtra = "";
                if (_multiplayerMode)
                {
                    try
                    {
                        var rsDbg = RunManager.Instance?.DebugOnlyGetState();
                        var plDbg = rsDbg != null ? LocalContext.GetMe(rsDbg) : null;
                        if (plDbg != null)
                        {
                            int handCnt = PileType.Hand.GetPile(plDbg)?.Cards?.Count ?? -1;
                            int drawCnt = PileType.Draw.GetPile(plDbg)?.Cards?.Count ?? -1;
                            int discardCnt = PileType.Discard.GetPile(plDbg)?.Cards?.Count ?? -1;
                            mpExtra = $" hand={handCnt} draw={drawCnt} discard={discardCnt}";
                        }
                    }
                    catch { }
                }
                // Only log on state change to reduce noise (was ~19K lines per session)
                string stateKey = $"pc={cm.PlayerActionsDisabled} d={_combatCardDelay:F1} tr={_combatTurnRequested} pl={_combatPlan != null}{mpExtra}";
                if (stateKey != _lastInCombatState)
                {
                    _lastInCombatState = stateKey;
                    MainFile.Logger.Info($"[DBG] InCombat: playerDisabled={cm.PlayerActionsDisabled} delay={_combatCardDelay:F2} turnReq={_combatTurnRequested} plan={_combatPlan != null} localNetId={LocalContext.NetId}{mpExtra}");
                }
            }

            // CombatHandler.BoostHpIfNeeded();

            if (!cm.PlayerActionsDisabled)
            {
                // ── We have an active turn — reset watchdogs
                _playerDisabledDuration = 0; // MP watchdog: we got our turn, reset disabled timer

                // ── Toggle guard: skip if auto-battle is OFF ──
                if (!_autoBattle)
                {
                    _lastCombatActivity = 0; // reset stuck detector — human is playing
                    return;
                }

                // Pause check: obey auto-battle toggle
                if (TokenSpire2.Core.AppConfig.Instance.AutoBattlePaused)
                {
                    return;
                }

                if (_combatCardDelay > 0)
                {
                    // Do NOT reset _lastCombatActivity here — the delay is
                    // very short (<1s) and resetting defeats the 30s combat
                    // stuck detection. Legitimate resets happen on card play
                    // (ExecuteNextCombatStep) and enemy turn (OnNonPlayPhase).
                    return;
                }

                if (!_combatTurnRequested)
                {
                    // Wait for draw animation to finish — CRITICAL for reshuffle correctness.
                    // After a reshuffle, cards arrive one by one during the draw animation.
                    // A fixed delay is unreliable; instead we wait until the hand count
                    // STABILIZES (same count for 3 consecutive frames) before running solver.
                    // This ensures ALL cards are present in hand, preventing energy waste
                    // and "card not in hand" failures on post-reshuffle turns.
                    var rs = RunManager.Instance?.DebugOnlyGetState();
                    var pl = rs != null ? LocalContext.GetMe(rs) : null;
                    try
                    {
                        if (pl != null)
                        {
                            int drawCount = PileType.Draw.GetPile(pl)?.Cards?.Count ?? -1;
                            int handCount = PileType.Hand.GetPile(pl)?.Cards?.Count ?? -1;
                            if (dbgLog || handCount > 0)
                            {
                                if (_drawStableCount <= 1) // Only log on first detection, not every frame
                                    MainFile.Logger.Info($"[DBG] DrawCheck pl=ok draw={drawCount} hand={handCount} stable={_drawStableCount}/{_lastDrawHandCount}");
                            }
                            if (handCount == 0)
                            {
                                // If draw pile is also empty, no cards will ever arrive — end turn.
                                if (drawCount == 0)
                                {
                                    MainFile.Logger.Info("[DBG] DrawCheck: hand=0 draw=0 — no cards to draw, ending turn");
                                    if (pl != null && CombatManager.Instance is { PlayerActionsDisabled: false })
                                        EndTurnViaUiOrApi(pl);
                                    _combatTurnRequested = true; _combatTurnRequestedDuration = 0;
                                    _combatCardDelay = 0.5;
                                    _lastCombatActivity = 0; // reset — waiting for end turn
                                    return;
                                }
                                // Reset stability tracking — draw hasn't started
                                _drawStableCount = 0;
                                _lastDrawHandCount = 0;
                                _drawJustFinished = false;
                                _combatCardDelay = 0.05; // poll faster during draw
                                _lastCombatActivity = 0; // reset — waiting for draw animation
                                return;
                            }
                            // ── Hand has cards — check if draw is stable ──
                            if (handCount == _lastDrawHandCount)
                            {
                                _drawStableCount++;
                            }
                            else
                            {
                                _drawStableCount = 1;
                                _lastDrawHandCount = handCount;
                            }
                            // Draw is complete when:
                            //   - we have 5+ cards (base draw without modifiers), OR
                            //   - hand is stable for 3+ frames AND draw pile is empty (nothing left)
                            //   - hand is stable for 8+ frames (slow animation safety — draw pile has cards but
                            //     game is animating slowly; give it more time before giving up)
                            bool drawComplete = handCount >= 5
                                || (_drawStableCount >= 3 && drawCount == 0)
                                || _drawStableCount >= 8;
                            if (!drawComplete && _drawStableCount >= 3 && drawCount > 0)
                            {
                                // Log when we'd have falsely declared complete under old logic
                                if (_drawStableCount == 3)
                                    MainFile.Logger.Info($"[DBG] DrawCheck extended wait: hand={handCount} draw={drawCount} — draw pile has cards, waiting longer");
                            }
                            if (!drawComplete)
                            {
                                _combatCardDelay = 0.05; // poll every 50ms during draw
                                // Don't log every frame — COMPLETE log below is sufficient
                                return;
                            }
                            // Draw complete — reset for next turn
                            _drawStableCount = 0;
                            _lastDrawHandCount = 0;
                            _drawJustFinished = true;
                            MainFile.Logger.Info($"[DBG] DrawCheck COMPLETE: hand={handCount} draw={drawCount}");
                        }
                        else
                        {
                            _drawStableCount = 0;
                            _lastDrawHandCount = 0;
                            _drawJustFinished = false;
                            if (dbgLog)
                                MainFile.Logger.Info("[DBG] DrawCheck pl=NULL — waiting for game state");
                            _combatCardDelay = 0.1;
                            return;
                        }
                    }
                    catch (Exception drawEx)
                    {
                        MainFile.Logger.Info($"[DBG] DrawCheck CRASH: {drawEx.GetType().Name}: {drawEx.Message}");
                    }

                    // ── ALGORITHMIC SOLVER (all characters) ─────────────
                    // Reset per-turn state for fresh solver invocation
                    _emptySolveRetries = 0;
                    _mpPostPlanEmptyCount = 0;
                    _mpEndTurnRetryActive = false;
                    _mpPostEndTurnTimer = 0;
                    MainFile.Logger.Info($"[DBG] >>> SOLVER START: character={_character}, pl={pl != null} <<<");
                    if (_character is "IRONCLAD" or "SILENT" or "DEFECT" or "NECROBINDER" or "REGENT")
                    {
                        try
                        {
                            var hand = PileType.Hand.GetPile(pl!).Cards.ToList();
                            MainFile.Logger.Info($"[DBG] Got hand: {hand.Count} cards");
                            var combatState = cm.DebugOnlyGetState();
                            var enemies = combatState?.Enemies.Where(e => e.IsAlive).ToList()
                                ?? new List<Creature>();
                            var pcs = pl!.PlayerCombatState;
                            int energy = pcs?.Energy ?? 0;
                            int block = pl.Creature.Block;
                            int hp = pl.Creature.CurrentHp;
                            int maxHp = pl.Creature.MaxHp;

                            // Read stats via power names (works for all classes)
                            int strength = 0, dexterity = 0, vulnerableOnPlayer = 0,
                                weakOnPlayer = 0, frailOnPlayer = 0;
                            try
                            {
                                strength = pl.Creature.Powers
                                    .Where(p => p.GetType().Name.Contains("Strength", StringComparison.OrdinalIgnoreCase))
                                    .Sum(p => p.Amount);
                                dexterity = pl.Creature.Powers
                                    .Where(p => p.GetType().Name.Contains("Dexterity", StringComparison.OrdinalIgnoreCase))
                                    .Sum(p => p.Amount);
                                vulnerableOnPlayer = pl.Creature.Powers
                                    .Where(p => p.GetType().Name.Contains("Vulnerable", StringComparison.OrdinalIgnoreCase))
                                    .Sum(p => p.Amount);
                                weakOnPlayer = pl.Creature.Powers
                                    .Where(p => p.GetType().Name.Contains("Weak", StringComparison.OrdinalIgnoreCase))
                                    .Sum(p => p.Amount);
                                frailOnPlayer = pl.Creature.Powers
                                    .Where(p => p.GetType().Name.Contains("Frail", StringComparison.OrdinalIgnoreCase))
                                    .Sum(p => p.Amount);
                            }
                            catch { /* powers may not be accessible */ }

                            // Player DOT (ConstrictPower, poison on player, etc.)
                            // These are unblockable end-of-turn damage effects applied to the player
                            int playerDOT = 0;
                            try
                            {
                                playerDOT = pl.Creature.Powers
                                    .Where(p => {
                                        var name = p.GetType().Name;
                                        return name.Contains("Constrict", StringComparison.OrdinalIgnoreCase)
                                            || name.Contains("PlayerPoison", StringComparison.OrdinalIgnoreCase);
                                    })
                                    .Sum(p => p.Amount);
                                if (playerDOT > 0)
                                    MainFile.Logger.Info($"[SolverDBG] Player DOT detected: {playerDOT} (Constrict/etc.)");
                            }
                            catch { }

                            // Poison on enemies (Silent)
                            int poisonOnEnemies = 0;
                            try
                            {
                                poisonOnEnemies = enemies.Sum(e => e.Powers
                                    .Where(p => p.GetType().Name.Contains("Poison", StringComparison.OrdinalIgnoreCase))
                                    .Sum(p => p.Amount));
                            }
                            catch { }

                            // Orbs (Defect) — enhanced collection with position, damage, loop
                            int orbSlots = 0, lightningOrbs = 0, frostOrbs = 0,
                                darkOrbs = 0, plasmaOrbs = 0, focus = 0;
                            int loopCount = 0, baseDarkOrbDamage = 6, totalDarkOrbDamage = 0;
                            var orbQueueList = new List<string>();
                            try
                            {
                                var orbQueue = pcs?.OrbQueue;
                                if (orbQueue != null && orbQueue.Capacity > 0)
                                {
                                    orbSlots = orbQueue.Capacity;
                                    // Read orb queue in order (index 0 = rightmost = next to evoke)
                                    foreach (var orb in orbQueue.Orbs)
                                    {
                                        var orbName = orb.GetType().Name;
                                        string orbType;
                                        if (orbName.Contains("Lightning")) { lightningOrbs++; orbType = "Lightning"; }
                                        else if (orbName.Contains("Frost")) { frostOrbs++; orbType = "Frost"; }
                                        else if (orbName.Contains("Dark"))
                                        {
                                            darkOrbs++; orbType = "Dark";
                                            // Try to read accumulated dark orb damage
                                            try
                                            {
                                                var dmgProp = orb.GetType().GetProperty("Damage")
                                                    ?? orb.GetType().GetProperty("PassiveAmount")
                                                    ?? orb.GetType().GetProperty("Amount");
                                                if (dmgProp != null)
                                                {
                                                    int dmg = Convert.ToInt32(dmgProp.GetValue(orb));
                                                    totalDarkOrbDamage += dmg;
                                                    if (dmg > baseDarkOrbDamage) baseDarkOrbDamage = dmg;
                                                }
                                            }
                                            catch { totalDarkOrbDamage += 6; }
                                        }
                                        else if (orbName.Contains("Plasma")) { plasmaOrbs++; orbType = "Plasma"; }
                                        else if (orbName.Contains("Glass")) { lightningOrbs++; orbType = "Lightning"; } // Glass ≈ Lightning
                                        else { orbType = "Lightning"; lightningOrbs++; } // Unknown — default
                                        orbQueueList.Add(orbType);
                                    }
                                    focus = pl.Creature.Powers
                                        .Where(p => p.GetType().Name.Contains("Focus", StringComparison.OrdinalIgnoreCase))
                                        .Sum(p => p.Amount);
                                    // Loop: read Loop power stacks
                                    loopCount = pl.Creature.Powers
                                        .Where(p => p.GetType().Name.Contains("Loop", StringComparison.OrdinalIgnoreCase))
                                        .Sum(p => p.Amount);
                                    if (loopCount > 0)
                                        MainFile.Logger.Info($"[SolverDBG] Loop detected: {loopCount} stacks");
                                }
                            }
                            catch (Exception orbEx)
                            {
                                MainFile.Logger.Info($"[SolverDBG] Orb collection failed: {orbEx.Message}");
                            }

                            // Stars (Necrobinder)
                            int stars = 0;
                            try { stars = pcs?.Stars ?? 0; } catch { }

                            MainFile.Logger.Info($"[SolverDBG] Hand={hand.Count} Enemies={enemies.Count} Energy={energy} Str={strength} Char={_character}");

                            // ── Battle logging: start of turn ─────────
                            _combatTurnNumber++;

                            // ── PANIC_BUTTON debuff tick: decrement each turn ──
                            if (_panicButtonTurnsRemaining > 0)
                            {
                                _panicButtonTurnsRemaining--;
                                MainFile.Logger.Info($"[AutoSlay] PANIC_BUTTON debuff: {_panicButtonTurnsRemaining} turns remaining");
                            }

                            // End previous turn (enemy turn damage is known now)
                            if (_combatTurnNumber > 1)
                            {
                                try
                                {
                                    int damageTaken = Math.Max(0, _lastTurnHp - hp);
                                    int enemiesKilled = Math.Max(0, _lastTurnAliveEnemyCount - enemies.Count);

                                    // Use saved values from plan creation (_combatPlan already consumed)
                                    int energyRemaining = Math.Max(0, energy - _lastTurnEnergySpent);

                                    BattleLogger.EndTurn(hp, block, damageTaken, enemiesKilled,
                                        energyRemaining, _lastTurnPlayableNotPlayed, _lastTurnTotalPlayable);
                                }
                                catch (Exception logEx)
                                {
                                    MainFile.Logger.Info($"[BattleLog] EndTurn failed: {logEx.Message}");
                                }
                            }

                            _lastTurnHp = hp;
                            _lastTurnBlock = block;
                            _lastTurnMaxHp = maxHp;
                            _lastTurnAliveEnemyCount = enemies.Count;

                            try
                            {
                                var turnCardIds = hand.Select(c =>
                                {
                                    try { return c.Id.Entry; } catch { return "?"; }
                                }).ToList();
                                var turnCardCosts = hand.Select(c =>
                                {
                                    try { return c.EnergyCost.CostsX ? -1 : c.EnergyCost.Canonical; }
                                    catch { return -1; }
                                }).ToList();
                                int drawPileCount = 0;
                                int discardPileCount = 0;
                                try
                                {
                                    drawPileCount = PileType.Draw.GetPile(pl!).Cards.Count;
                                    discardPileCount = PileType.Discard.GetPile(pl!).Cards.Count;
                                }
                                catch { }
                                var turnEnemyIds = enemies.Select(e =>
                                {
                                    try { return e.Monster?.Id.Entry ?? "?"; } catch { return "?"; }
                                }).ToList();
                                var turnEnemyHps = enemies.Select(e => e.CurrentHp).ToList();
                                var turnEnemyMaxHps = enemies.Select(e => e.MaxHp).ToList();
                                var turnEnemyBlocks = enemies.Select(e => e.Block).ToList();
                                var turnEnemyIntentDamages = enemies.Select(e =>
                                {
                                    try { return IroncladSolver.EstimateIntentDamageStatic(e); }
                                    catch { return 0; }
                                }).ToList();
                                var turnEnemyIntentTypes = enemies.Select(e =>
                                {
                                    try { return e.Monster?.NextMove?.Intents?.FirstOrDefault()?.IntentType.ToString() ?? "?"; }
                                    catch { return "?"; }
                                }).ToList();
                                var turnEnemyVuln = enemies.Select(e =>
                                {
                                    try { return IroncladSolver.GetVulnerableStacksStatic(e); } catch { return 0; }
                                }).ToList();
                                var turnEnemyWeak = enemies.Select(e =>
                                {
                                    try { return IroncladSolver.GetWeakStacksStatic(e); } catch { return 0; }
                                }).ToList();
                                var turnEnemyStr = enemies.Select(e =>
                                {
                                    try { return IroncladSolver.GetStrengthStacksStatic(e); } catch { return 0; }
                                }).ToList();
                                var turnEnemyDex = enemies.Select(e =>
                                {
                                    try { return IroncladSolver.GetDexterityStacksStatic(e); } catch { return 0; }
                                }).ToList();
                                // Gather ALL enemy powers for comprehensive logging
                                var turnEnemyAllPowers = enemies.Select(e =>
                                {
                                    try
                                    {
                                        return e.Powers.Select(p => new BattleLogger.PowerSnapshot
                                        {
                                            Name = p.GetType().Name,
                                            Amount = p.Amount
                                        }).ToList();
                                    }
                                    catch { return new List<BattleLogger.PowerSnapshot>(); }
                                }).ToList();
                                // Gather ALL player powers for comprehensive logging
                                var playerAllPowers = new List<BattleLogger.PowerSnapshot>();
                                try
                                {
                                    playerAllPowers = pl.Creature.Powers.Select(p =>
                                        new BattleLogger.PowerSnapshot { Name = p.GetType().Name, Amount = p.Amount }
                                    ).ToList();
                                }
                                catch { /* optional */ }
                                // Gather potion IDs
                                var potionIds = new List<string>();
                                try
                                {
                                    potionIds = pl.Potions.Where(p => !p.HasBeenRemovedFromState)
                                        .Select(p => p.Id.Entry).ToList();
                                }
                                catch { /* optional */ }

                                BattleLogger.StartTurn(
                                    _combatTurnNumber,
                                    energy, block, hp, maxHp,
                                    turnCardIds, turnCardCosts,
                                    drawPileCount, discardPileCount,
                                    turnEnemyIds, turnEnemyHps, turnEnemyMaxHps,
                                    turnEnemyBlocks, turnEnemyIntentDamages, turnEnemyIntentTypes,
                                    turnEnemyVuln, turnEnemyWeak, turnEnemyStr,
                                    turnEnemyDex, turnEnemyAllPowers,
                                    strength, dexterity,
                                    weakOnPlayer, frailOnPlayer, vulnerableOnPlayer,
                                    orbSlots, lightningOrbs, frostOrbs,
                                    darkOrbs, plasmaOrbs, focus, stars,
                                    playerAllPowers, potionIds);

                                // ── Boss play logging: record turn start state ──
                                var bossEnemySnapshots = enemies.Select(e =>
                                {
                                    try
                                    {
                                        return new EnemyStateSnapshot
                                        {
                                            Id = e.Monster?.Id.Entry ?? "?",
                                            Hp = e.CurrentHp,
                                            MaxHp = e.MaxHp,
                                            Block = e.Block,
                                            IntentDamage = IroncladSolver.EstimateIntentDamageStatic(e),
                                            IntentType = e.Monster?.NextMove?.Intents?.FirstOrDefault()?.IntentType.ToString() ?? "?",
                                            VulnerableStacks = IroncladSolver.GetVulnerableStacksStatic(e),
                                            WeakStacks = IroncladSolver.GetWeakStacksStatic(e),
                                            StrengthStacks = IroncladSolver.GetStrengthStacksStatic(e),
                                            DexterityStacks = IroncladSolver.GetDexterityStacksStatic(e),
                                        };
                                    }
                                    catch { return new EnemyStateSnapshot(); }
                                }).ToList();
                                BossPlayLogger.StartTurn(_combatTurnNumber, energy, hp, maxHp,
                                    block, strength, dexterity, bossEnemySnapshots);
                            }
                            catch (Exception logEx)
                            {
                                MainFile.Logger.Info($"[BattleLog] StartTurn failed: {logEx.Message}");
                            }

                            // ── Potion usage: now integrated into solver DFS ──
                            // TryUsePotions(pl, enemies, hp, maxHp, _combatTurnNumber);

                            var cfg = CharacterConfig.Create(_character);
                            var drawPile = PileType.Draw.GetPile(pl!).Cards.ToList();
                            var discardPile = PileType.Discard.GetPile(pl!).Cards.ToList();

                            // ── Detect elite/boss encounters for aggression adjustment ──
                            bool isElite = false;
                            bool isBoss = false;
                            string encounterId = "";
                            try
                            {
                                encounterId = cm.DebugOnlyGetState()?.Encounter?.Id.Entry ?? "";
                                string encounterUpper = encounterId.ToUpperInvariant();
                                // Boss encounters usually have "_BOSS" suffix
                                isBoss = encounterUpper.Contains("_BOSS");
                                // Elite encounters usually have "_ELITE" suffix
                                isElite = encounterUpper.Contains("_ELITE");
                                if (isBoss || isElite)
                                    MainFile.Logger.Info($"[SolverDBG] Encounter={encounterId} isElite={isElite} isBoss={isBoss}");
                            }
                            catch { }

                            var solveResult = IroncladSolver.Solve(
                                hand, enemies, energy, block, hp, maxHp,
                                strength, vulnerableOnPlayer, cfg,
                                dexterity, weakOnPlayer, frailOnPlayer, poisonOnEnemies,
                                orbSlots, lightningOrbs, frostOrbs,
                                darkOrbs, plasmaOrbs, focus,
                                loopCount, orbQueueList,
                                null, // darkOrbAccumulation — solver tracks internally
                                baseDarkOrbDamage, totalDarkOrbDamage,
                                stars,
                                drawPile, discardPile,
                                pl.Potions.Where(p => !((bool)p.HasBeenRemovedFromState)).Select(p => p.Id?.Entry ?? "?").ToList(),
                                isElite, isBoss,
                                encounterId,
                                playerDOT: playerDOT,
                                panicButtonTurns: _panicButtonTurnsRemaining);

                            MainFile.Logger.Info($"[Solver] {solveResult.DebugInfo}");
                            if (MainFile.ConsoleEnabled)
                                Console.WriteLine($"[Solver] {solveResult.DebugInfo}");

                            try
                            {
                                BattleLogger.LogSolverPlan(solveResult, solveResult.StatesExplored);
                            }
                            catch (Exception logEx)
                            {
                                MainFile.Logger.Info($"[BattleLog] LogSolverPlan failed: {logEx.Message}");
                            }

                            // Convert solver actions to combat plan
                            _combatPlan = solveResult.Actions
                                .Where(a => !a.IsEndTurn)
                                .Select(a => a.IsPotion
                                    ? new CombatAction(
                                        null,
                                        pl?.Potions?.FirstOrDefault(p =>
                                        {
                                            try { return p.Id?.Entry == a.PotionId; }
                                            catch { return false; }
                                        }),
                                        null)
                                    : new CombatAction(a.Card, null, a.Target))
                                .ToList();
                            _combatPlanEndTurn = solveResult.Actions.Any(a => a.IsEndTurn);
                            _combatPlanStep = 0;
                            _combatTurnRequested = true; _combatTurnRequestedDuration = 0;
                            _wasEnemyTurn = false; // player's turn starting — allow stuck timer to accumulate
                            _turnPlansWithoutPlay++; // track consecutive plans without a successful play

                            // ── PANIC_BUTTON tracking ──────────────────────────
                            // Check if this turn's plan includes PANIC_BUTTON
                            bool panicButtonInPlan = _combatPlan.Any(a =>
                            {
                                try { return a.Card?.Id?.Entry?.ToUpperInvariant() == "PANIC_BUTTON"; }
                                catch { return false; }
                            });
                            if (panicButtonInPlan)
                            {
                                _panicButtonTurnsRemaining = 2;
                                MainFile.Logger.Info("[AutoSlay] PANIC_BUTTON in plan — setting debuff to 2 turns");
                            }

                            // ── PANIC_BUTTON reorder: after playing, block cards play AFTER attacks ──
                            if (_panicButtonTurnsRemaining > 0)
                            {
                                // Move all block/skill cards to the end of the plan (after attacks)
                                var attacks = _combatPlan.Where(a =>
                                {
                                    try { return a.Card?.Type == MegaCrit.Sts2.Core.Entities.Cards.CardType.Attack; }
                                    catch { return false; }
                                }).ToList();
                                var nonAttacks = _combatPlan.Where(a =>
                                {
                                    try { return a.Card?.Type != MegaCrit.Sts2.Core.Entities.Cards.CardType.Attack; }
                                    catch { return true; } // potions stay in place
                                }).ToList();
                                // Reorder: attacks first, then everything else (block cards after attacks)
                                _combatPlan = attacks.Concat(nonAttacks).ToList();
                                MainFile.Logger.Info($"[AutoSlay] PANIC_BUTTON debuff active ({_panicButtonTurnsRemaining} turns left) — reordered: {attacks.Count} attacks first, {nonAttacks.Count} others after");
                            }

                            // Track planned energy/card count for logging
                            _lastTurnEnergySpent = _combatPlan.Sum(a =>
                            {
                                try
                                {
                                    if (a.Card?.EnergyCost.CostsX == true) return energy;
                                    return a.Card?.EnergyCost.Canonical ?? 0;
                                }
                                catch { return 0; }
                            });
                            _lastTurnActionCount = _combatPlan.Count;
                            _lastTurnTotalPlayable = hand.Count(c => c.CanPlay(out _, out _));
                            _lastTurnPlayableNotPlayed = Math.Max(0, _lastTurnTotalPlayable - _lastTurnActionCount);

                            MainFile.Logger.Info($"[SolverDBG] Plan: {_combatPlan.Count} actions, energySpent={_lastTurnEnergySpent}, endTurn={_combatPlanEndTurn}");
                        }
                        catch (Exception solverEx)
                        {
                            MainFile.Logger.Info($"[DBG] Solver CRASH: {solverEx.GetType().Name}: {solverEx.Message}\n{solverEx.StackTrace}");
                            if (MainFile.ConsoleEnabled)
                                Console.WriteLine($"[Solver] CRASH: {solverEx.Message}");
                            _combatPlan = null;
                            _combatTurnRequested = false; _combatTurnRequestedDuration = 0;
                        }

                        // If solver produced a plan, execute it
                        if (_combatPlan != null) return;

                        // ── FALLBACK: Solver crashed or returned empty ──────────
                        // Build a simple greedy plan so we don't waste the turn.
                        try
                        {
                            if (pl != null)
                            {
                                var fallbackHand = PileType.Hand.GetPile(pl).Cards.ToList();
                                int fallbackEnergy = pl.PlayerCombatState?.Energy ?? 0;

                                // Estimate incoming damage — if none, skip pure-block Skills
                                int fallbackIncomingDmg = 0;
                                try
                                {
                                    var fcs = CombatManager.Instance?.DebugOnlyGetState();
                                    if (fcs?.Enemies != null)
                                    {
                                        foreach (var en in fcs.Enemies.Where(e => e.IsAlive))
                                        {
                                            try { fallbackIncomingDmg += IroncladSolver.EstimateIntentDamageStatic(en); }
                                            catch { }
                                        }
                                    }
                                }
                                catch { }

                                var playable = fallbackHand
                                    .Where(c => c.CanPlay(out _, out _))
                                    .OrderByDescending(c =>
                                    {
                                        // Power: always play (long-term value)
                                        if (c.Type == CardType.Power) return 1000;
                                        // Attack: always play (deal damage)
                                        if (c.Type == CardType.Attack) return 500;
                                        // Skill: only play if enemy is attacking OR card is 0-cost (free)
                                        if (fallbackIncomingDmg <= 0)
                                        {
                                            int skillCost = c.EnergyCost.CostsX ? 1 : c.EnergyCost.Canonical;
                                            return skillCost == 0 ? 50 : -1000;
                                        }
                                        return 100;
                                    })
                                    .ThenByDescending(c =>
                                        Math.Min(fallbackEnergy,
                                            c.EnergyCost.CostsX ? fallbackEnergy : c.EnergyCost.Canonical))
                                    .ToList();

                                var greedyPlan = new List<CombatAction>();
                                foreach (var card in playable)
                                {
                                    // X-cost: use remaining energy (not 0), so the card actually does something
                                    int cost = card.EnergyCost.CostsX
                                        ? Math.Max(1, fallbackEnergy)
                                        : card.EnergyCost.Canonical;
                                    if (cost > fallbackEnergy) continue;
                                    // Skip cards with negative priority (pure-block Skills vs non-attacking enemy)
                                    if (fallbackIncomingDmg <= 0
                                        && card.Type == CardType.Skill
                                        && cost > 0
                                        && card.Type != CardType.Power)
                                        continue;

                                    Creature? greedyTarget = null;
                                    bool needsTarget = card.TargetType != TargetType.AllEnemies
                                        && card.TargetType != TargetType.RandomEnemy
                                        && card.TargetType != TargetType.None
                                        && card.TargetType != TargetType.Self;
                                    if (needsTarget)
                                    {
                                        try
                                        {
                                            var cs = CombatManager.Instance?.DebugOnlyGetState();
                                            greedyTarget = cs?.Enemies?.FirstOrDefault(e => e.IsAlive);
                                        }
                                        catch { }
                                    }

                                    greedyPlan.Add(new CombatAction(card, null, greedyTarget));
                                    fallbackEnergy -= cost;
                                }

                                if (greedyPlan.Count > 0)
                                {
                                    MainFile.Logger.Info($"[DBG] Greedy fallback: {greedyPlan.Count} cards");
                                    _combatPlan = greedyPlan;
                                    _combatPlanEndTurn = true;
                                    _combatPlanStep = 0;
                                    _combatTurnRequested = true; _combatTurnRequestedDuration = 0;
                                    _turnPlansWithoutPlay++; // track consecutive plans without a successful play
                                    int fallbackEnergyStart = pl?.PlayerCombatState?.Energy ?? 0;
                                    _lastTurnEnergySpent = fallbackEnergyStart - fallbackEnergy;
                                    _lastTurnActionCount = greedyPlan.Count;
                                    _lastTurnTotalPlayable = playable.Count;
                                    _lastTurnPlayableNotPlayed = Math.Max(0, playable.Count - greedyPlan.Count);

                                    // Log fallback plan
                                    try
                                    {
                                        var fallbackResult = new IroncladSolver.SolveResult
                                        {
                                            Actions = greedyPlan.Select(a => new IroncladSolver.SolveAction
                                            {
                                                Card = a.Card,
                                                Target = a.Target,
                                                IsEndTurn = false,
                                            }).ToList(),
                                            DebugInfo = "GREEDY_FALLBACK",
                                            StatesExplored = 0,
                                        };
                                        fallbackResult.Actions.Add(new IroncladSolver.SolveAction { IsEndTurn = true });
                                        BattleLogger.LogSolverPlan(fallbackResult, 0);
                                    }
                                    catch { }
                                    return;
                                }
                            }
                        }
                        catch (Exception fallbackEx)
                        {
                            MainFile.Logger.Info($"[DBG] Fallback plan CRASH: {fallbackEx.Message}");
                        }

                        // ── Solver returned empty — check if hand actually has playable cards ──
                        // Race condition: solver may run on stale state after a card play
                        // where the hand hasn't fully updated yet. Instead of immediately
                        // ending the turn, verify the hand is truly empty of playable cards.
                        try
                        {
                            var recheckHand = PileType.Hand.GetPile(pl!).Cards.ToList();
                            int recheckEnergy = pl!.PlayerCombatState?.Energy ?? 0;
                            var trulyPlayable = recheckHand
                                .Where(c =>
                                {
                                    try { return c.CanPlay(out _, out _); } catch { return false; }
                                })
                                .Where(c =>
                                {
                                    try
                                    {
                                        int cost = c.EnergyCost.CostsX ? 1 : c.EnergyCost.Canonical;
                                        return cost <= recheckEnergy;
                                    }
                                    catch { return false; }
                                })
                                .ToList();
                            int playableCount = trulyPlayable.Count;

                            if (playableCount > 0 && _emptySolveRetries < MAX_EMPTY_SOLVE_RETRIES)
                            {
                                _emptySolveRetries++;
                                string ids = string.Join(",", trulyPlayable.Select(c =>
                                {
                                    try { return c.Id.Entry; } catch { return "?"; }
                                }));
                                MainFile.Logger.Info($"[DBG] Solver empty BUT hand has {playableCount} playable cards [{ids}] (retry {_emptySolveRetries}/{MAX_EMPTY_SOLVE_RETRIES}) — waiting for state update");
                                // Wait longer and retry — game state may still be updating
                                _combatTurnRequested = false; _combatTurnRequestedDuration = 0;
                                _combatCardDelay = 0.6 + (_emptySolveRetries * 0.3);
                                _lastCombatActivity = 0; // reset — state update delay is legitimate wait
                                return;
                            }

                            if (playableCount > 0)
                            {
                                // Max retries hit but cards are playable — build greedy plan as last resort
                                MainFile.Logger.Info($"[DBG] Max retries ({MAX_EMPTY_SOLVE_RETRIES}) reached with {playableCount} playable — building greedy plan");
                                try
                                {
                                    // Estimate incoming damage to deprioritize block when enemy isn't attacking
                                    int retryIncomingDmg = 0;
                                    try
                                    {
                                        var rcs = CombatManager.Instance?.DebugOnlyGetState();
                                        if (rcs?.Enemies != null)
                                        {
                                            foreach (var en in rcs.Enemies.Where(e => e.IsAlive))
                                            {
                                                try { retryIncomingDmg += IroncladSolver.EstimateIntentDamageStatic(en); }
                                                catch { }
                                            }
                                        }
                                    }
                                    catch { }

                                    var greedyPlan = new List<CombatAction>();
                                    int greedyEnergy = recheckEnergy;
                                    foreach (var card in trulyPlayable.OrderByDescending(c =>
                                    {
                                        try
                                        {
                                            if (c.Type == CardType.Power) return 1000;
                                            if (c.Type == CardType.Attack) return 500;
                                            // Skill: only valuable if enemy is attacking or card is 0-cost
                                            if (retryIncomingDmg <= 0)
                                            {
                                                int sc = c.EnergyCost.CostsX ? 1 : c.EnergyCost.Canonical;
                                                return sc == 0 ? 50 : -1000;
                                            }
                                            return 100;
                                        }
                                        catch { return 0; }
                                    }))
                                    {
                                        int cost = card.EnergyCost.CostsX ? Math.Max(1, greedyEnergy) : card.EnergyCost.Canonical;
                                        if (cost > greedyEnergy) continue;
                                        // Skip pure-block Skills when enemy isn't attacking (cost > 0)
                                        if (retryIncomingDmg <= 0
                                            && card.Type == CardType.Skill
                                            && cost > 0)
                                            continue;
                                        Creature? greedyTarget = null;
                                        try
                                        {
                                            var gcs = CombatManager.Instance?.DebugOnlyGetState();
                                            greedyTarget = gcs?.Enemies?.FirstOrDefault(e => e.IsAlive);
                                        }
                                        catch { }
                                        greedyPlan.Add(new CombatAction(card, null, greedyTarget));
                                        greedyEnergy -= cost;
                                    }
                                    if (greedyPlan.Count > 0)
                                    {
                                        _combatPlan = greedyPlan;
                                        _combatPlanEndTurn = true;
                                        _combatPlanStep = 0;
                                        _combatTurnRequested = true; _combatTurnRequestedDuration = 0;
                                        _turnPlansWithoutPlay++; // track consecutive plans without a successful play
                                        _lastTurnEnergySpent = recheckEnergy - greedyEnergy;
                                        _lastTurnActionCount = greedyPlan.Count;
                                        _lastTurnTotalPlayable = playableCount;
                                        _lastTurnPlayableNotPlayed = playableCount - greedyPlan.Count;
                                        _emptySolveRetries = 0;
                                        return;
                                    }
                                }
                                catch (Exception greedyEx)
                                {
                                    MainFile.Logger.Info($"[DBG] Last-resort greedy plan CRASH: {greedyEx.Message}");
                                }
                            }

                            // Truly nothing playable — end turn
                            MainFile.Logger.Info($"[DBG] Solver empty, hand has {playableCount} playable — ending turn");
                            _emptySolveRetries = 0;
                        }
                        catch (Exception recheckEx)
                        {
                            MainFile.Logger.Info($"[DBG] Hand recheck CRASH: {recheckEx.Message} — ending turn");
                        }

                        _lastTurnEnergySpent = 0;
                        _lastTurnActionCount = 0;
                        _lastTurnTotalPlayable = 0;
                        _lastTurnPlayableNotPlayed = 0;
                        if (pl != null && CombatManager.Instance is { PlayerActionsDisabled: false })
                            EndTurnViaUiOrApi(pl);
                        _combatTurnRequested = true; _combatTurnRequestedDuration = 0;
                        _combatCardDelay = 0.5;
                        // ── Activate 5-second MP retry cycle ─────────────
                        if (_multiplayerMode && !_isMultiplayerHost)
                        {
                            _mpEndTurnRetryActive = true;
                            _mpPostEndTurnTimer = 0;
                        }
                    }
                    else
                    {
                        MainFile.Logger.Info($"[DBG] Character '{_character}' NOT in solver list! Falling through.");
                    }
                }
                else
                {
                    // ── Turn-requested watchdog (Fix A) ──────────────────────
                    // _combatTurnRequested=true means we asked to end our turn.
                    // PlayerActionsDisabled should become true shortly after.
                    // If it doesn't, the end-turn button click was silently
                    // rejected (button disabled, animation in progress, etc.).
                    //
                    // In multiplayer (client/bot), PlayerActionsDisabled stays
                    // false until ALL players have ended their turn — the bot
                    // must wait for the human host to finish playing. Use a much
                    // longer timeout and reset the stuck detector so we don't
                    // kill the game while waiting.
                    //
                    // ⚠️ We do NOT use Thread.Sleep — _Process runs on Godot's
                    // main thread. Sleeping blocks the engine and prevents the
                    // game from processing input events, creating a permanent
                    // green-screen freeze. Instead we use frame-based counting:
                    // spread retries across frames so the game can breathe.
                    _combatTurnRequestedDuration += _delta;
                    double deadlockTimeout = _multiplayerMode ? 120.0 : 5.0;
                    if (_combatTurnRequestedDuration > deadlockTimeout)
                    {
                        if (_multiplayerMode)
                        {
                            // In multiplayer: after 120s of waiting, the end-turn
                            // was probably silently rejected. Retry via action queue.
                            MainFile.Logger.Error(
                                $"[AutoSlay] MP DEADLOCK: _combatTurnRequested=true for " +
                                $"{_combatTurnRequestedDuration:F1}s, PlayerActionsDisabled=false (IsHost={_isMultiplayerHost}). " +
                                "Retrying end turn via ActionQueueSynchronizer...");
                            _combatTurnRequestedDuration = 0;
                            _combatTurnRequested = false;
                            _combatPlan = null;
                            _combatCardDelay = 0.2;
                            try
                            {
                                var rs2 = RunManager.Instance?.DebugOnlyGetState();
                                var pl2 = rs2 != null ? LocalContext.GetMe(rs2) : null;
                                if (pl2 != null && CombatManager.Instance is { PlayerActionsDisabled: false })
                                {
                                    int tn = pl2.PlayerCombatState.TurnNumber;
                                    RunManager.Instance?.ActionQueueSynchronizer?.RequestEnqueue(
                                        new MegaCrit.Sts2.Core.GameActions.EndPlayerTurnAction(pl2, tn));
                                    MainFile.Logger.Info($"[AutoSlay] MP Deadlock recovery: EndPlayerTurnAction turn#{tn} enqueued");
                                    _combatTurnRequested = true; _combatTurnRequestedDuration = 0;
                                    _combatCardDelay = 0.5;
                                }
                            }
                            catch (Exception deadlockEx)
                            {
                                MainFile.Logger.Error($"[AutoSlay] MP Deadlock recovery CRASH: {deadlockEx.Message}");
                            }
                        }
                        else
                        {
                            // Singleplayer: normal 5-second deadlock retry
                            MainFile.Logger.Error(
                                $"[AutoSlay] DEADLOCK DETECTED: _combatTurnRequested=true for " +
                                $"{_combatTurnRequestedDuration:F1}s, PlayerActionsDisabled=false. " +
                                "Retrying end turn via PlayerCmd...");
                            _combatTurnRequestedDuration = 0;
                            _combatTurnRequested = false;
                            _combatPlan = null;
                            _combatCardDelay = 0.2;
                            try
                            {
                                var rs2 = RunManager.Instance?.DebugOnlyGetState();
                                var pl2 = rs2 != null ? LocalContext.GetMe(rs2) : null;
                                if (pl2 != null && CombatManager.Instance is { PlayerActionsDisabled: false })
                                {
                                    PlayerCmd.EndTurn(pl2, canBackOut: false);
                                    MainFile.Logger.Info("[AutoSlay] Deadlock recovery: end turn sent via PlayerCmd");
                                    _combatTurnRequested = true; _combatTurnRequestedDuration = 0;
                                    _combatCardDelay = 0.5;
                                }
                            }
                            catch (Exception deadlockEx)
                            {
                                MainFile.Logger.Error($"[AutoSlay] Deadlock recovery CRASH: {deadlockEx.Message}");
                            }
                        }
                    }
                    else if (_multiplayerMode)
                    {
                        // Reset stuck detector — we're alive, just waiting for other player
                        _lastCombatActivity = 0;

                        // ── 5-second retry cycle ──────────────────────────
                        // After the bot ends its turn, the host may still be
                        // playing. The host could use ally-targeting cards
                        // (BELIEVE_IN_YOU, DEMONIC_SHIELD, etc.) that give
                        // the bot energy or cards. Check every 5s whether we
                        // have new playable cards, and if so, cancel end-turn
                        // and play them.
                        if (_mpEndTurnRetryActive && !_isMultiplayerHost)
                        {
                            _mpPostEndTurnTimer += _delta;
                            if (_mpPostEndTurnTimer >= MP_END_TURN_RETRY)
                            {
                                _mpPostEndTurnTimer = 0;
                                // Check if hand has playable cards
                                bool hasPlayable = false;
                                try
                                {
                                    var rs = RunManager.Instance?.DebugOnlyGetState();
                                    var me = rs != null ? LocalContext.GetMe(rs) : null;
                                    if (me != null)
                                    {
                                        var hand = PileType.Hand.GetPile(me).Cards.ToList();
                                        int curEnergy = me.PlayerCombatState?.Energy ?? 0;
                                        foreach (var c in hand)
                                        {
                                            try
                                            {
                                                if (c == null) continue;
                                                int cost = c.EnergyCost?.CostsX == true
                                                    ? Math.Max(1, curEnergy)
                                                    : (c.EnergyCost?.Canonical ?? 99);
                                                if (cost <= curEnergy && cost >= 0
                                                    && c.CanPlay(out _, out _))
                                                {
                                                    hasPlayable = true;
                                                    break;
                                                }
                                            }
                                            catch { }
                                        }
                                    }
                                }
                                catch { }

                                if (hasPlayable)
                                {
                                    MainFile.Logger.Info("[AutoSlay] MP retry: playable cards found after end-turn — canceling and re-solving");
                                    _mpEndTurnRetryActive = false;
                                    _mpPostEndTurnTimer = 0;
                                    _combatTurnRequested = false;
                                    _combatTurnRequestedDuration = 0;
                                    _combatPlan = null;
                                    _combatCardDelay = 0.3;
                                }
                                else
                                {
                                    MainFile.Logger.Info($"[AutoSlay] MP retry: no playable cards after {MP_END_TURN_RETRY}s — keeping end-turn");
                                }
                            }
                        }
                    }
                    return;
                }
                // If waiting, do nothing
            }
            else
            {
                CombatHandler.OnNonPlayPhase();
                _combatTurnRequested = false; _combatTurnRequestedDuration = 0;
                _drawJustFinished = false;
                _drawStableCount = 0;
                _lastDrawHandCount = 0;
                // Reset stuck timer ONCE per enemy turn — enemy turns can be long
                // (bosses with many minions, complex animations). The per-frame reset
                // was preventing the 30s combat stuck detector from ever firing.
                // Now we only reset on the FIRST frame of each enemy turn.
                if (!_wasEnemyTurn)
                {
                    _lastCombatActivity = 0;
                    _wasEnemyTurn = true;
                }
                // ── Multiplayer PlayerActionsDisabled watchdog ──────────────
                // In MP, PlayerActionsDisabled=true means it's another player's turn.
                // If the other player is stuck/dead, we'll wait here forever. Track
                // how long we've been disabled and force a recovery if it exceeds the
                // multiplayer stuck timeout (300s).
                if (_multiplayerMode)
                {
                    _playerDisabledDuration += delta;
                    double mpDisabledTimeout = _multiplayerMode ? 300.0 : 45.0;
                    if (_playerDisabledDuration > mpDisabledTimeout)
                    {
                        MainFile.Logger.Error(
                            $"[AutoSlay] MP DEADLOCK: PlayerActionsDisabled=true for " +
                            $"{_playerDisabledDuration:F0}s. Combat frozen — " +
                            "logging and signaling run complete to break out.");
                        WriteStuckDiagnostics("MP_PLAYER_DISABLED_STUCK", _playerDisabledDuration);
                        SignalRunComplete();
                        _playerDisabledDuration = 0;
                        _lastCombatActivity = 0;
                        _combatPlan = null;
                        _combatTurnRequested = false;
                        _combatTurnRequestedDuration = 0;
                        _combatPlanStuckFrames = 0;
                        // Try force-ending our own turn even though disabled
                        try
                        {
                            var rs3 = RunManager.Instance?.DebugOnlyGetState();
                            var pl3 = rs3 != null ? LocalContext.GetMe(rs3) : null;
                            if (pl3 != null)
                            {
                                int tn3 = pl3.PlayerCombatState.TurnNumber;
                                RunManager.Instance?.ActionQueueSynchronizer?.RequestEnqueue(
                                    new MegaCrit.Sts2.Core.GameActions.EndPlayerTurnAction(pl3, tn3));
                                MainFile.Logger.Info($"[AutoSlay] MP Disabled recovery: EndPlayerTurnAction turn#{tn3} enqueued");
                            }
                        }
                        catch (Exception disabledEx)
                        {
                            MainFile.Logger.Error($"[AutoSlay] MP Disabled recovery CRASH: {disabledEx.Message}");
                        }
                        _combatCardDelay = 0.5;
                        return;
                    }
                }
            }
            return;
        }
        // ── Post-combat one-time transition ──────────────────────────────
        // Only runs on the FIRST frame after combat ends, not every frame.
        // (_wasInCombat gate prevents repeated execution.)
        if (_wasInCombat)
        {
            _wasInCombat = false;
            _postCombatCooldownLogged = true;
            _cooldown = 0; // Clear any stale combat cooldown so post-combat navigation starts immediately
            MapDecider.InMultiplayerRun = _multiplayerMode; // Set early so flag is correct before MAP handler runs

            CombatHandler.OnCombatEnded();
            _combatTurnRequested = false; _combatTurnRequestedDuration = 0;
            _drawJustFinished = false;

            // ── Reset cached map path after combat ────────────────────────────
            // Combat changes room state. The cached path from before combat
            // may point to an already-visited node, causing auto-navigate to
            // stall. Resetting forces a fresh path-find on the next map open.
            MapDecider.Reset();

            // ── Post-combat: generate dialogue for next combat start ──
            try { Chat.CombatRecorder.OnCombatEnd(); } catch { }

            // ── Clear shared conversation log for fresh start next combat ──
            try { Chat.ConversationManager.Clear(); } catch { }

            // Combat end transition logging
            try
            {
                var rs = RunManager.Instance?.DebugOnlyGetState();
                var pl = rs != null ? LocalContext.GetMe(rs) : null;
                int hp = pl?.Creature?.CurrentHp ?? 0;
                int block = pl?.Creature?.Block ?? 0;

                // Close the last turn if it was never closed
                if (_combatTurnNumber > 0)
                {
                    int damageTaken = Math.Max(0, _lastTurnHp - hp);
                    BattleLogger.EndTurn(hp, block, damageTaken, 0, 0);
                }

                bool victory = hp > 0; // Player alive after combat = victory (all enemies killed)
                string? killedBy = null;
                if (!victory)
                    killedBy = cm?.DebugOnlyGetState()?.Encounter?.Id.Entry;
                BattleLogger.EndBattle(victory, hp, killedBy);

                // ── Boss play logging: write boss play record to disk ──
                try
                {
                    BossPlayLogger.EndBossFight(victory, hp, _combatTurnNumber,
                        totalDamageDealt: 0, totalDamageTaken: 0);
                }
                catch { }

                // In batch mode: if the player died, signal run complete immediately.
                // This is MORE RELIABLE than the overlay-based GameOver detection,
                // because the death transition may not use the overlay system.
                // CO-OP GUARD: never signal run complete in co-op mode.
                if (_batchMode && !victory)
                {
                    MainFile.Logger.Error($"[AutoSlay] PLAYER DIED in batch mode — signaling run complete");
                    SignalRunComplete();
                }
            }
            catch (Exception logEx)
            {
                MainFile.Logger.Info($"[BattleLog] EndBattle failed: {logEx.Message}");
            }
        }

        if (_cooldown > 0)
        {
            // ── Post-combat ghost cooldown diagnostic ──────────────────────
            // After combat ends, _cooldown should be 0 (combat uses
            // _combatCardDelay, not _cooldown). If _cooldown > 0 after
            // post-combat transition, something set it during combat and
            // it's blocking the map handler. Log it once per combat end.
            if (_postCombatCooldownLogged)
            {
                _postCombatCooldownLogged = false;
                MainFile.Logger.Info($"[AutoSlay] Post-combat: _cooldown={_cooldown:F1} delaying map navigation");
            }
            return;
        }
        _postCombatCooldownLogged = false; // reset flag once cooldown clears

        // ── Log state each tick ──────────────────────────────────────────────
        LogState();

        // ── Overlay screens ──────────────────────────────────────────────────
        var os2 = NOverlayStack.Instance;
        if (os2?.ScreenCount > 0)
        {
            var overlay = os2.Peek();
            var overlayNode = overlay as Node;
            // Skip rewards overlay when LLM is done with rewards — fall through to map
            if (overlayNode is NRewardsScreen rewardsForProceed && _rewardsLlmDone)
            {
                var proceed = AutoSlayHelpers.FindFirst<NProceedButton>(rewardsForProceed);
                if (proceed?.IsEnabled == true)
                {
                    proceed.ForceClick();
                    // ── MULTIPLAYER: Proceed on NRewardsScreen stays in Wait state until
                    // ALL players' rewards are fully synced. Clicking every 1s creates
                    // log spam but the click is silently ignored. Use a longer cooldown
                    // so we still eventually dismiss the screen once sync completes.
                    // The DismissContinuePrompt path has its own backoff (6 clicks → 10s).
                    _cooldown = _multiplayerMode ? 3.0 : 1.0;
                    return;
                }
                // fall through to map/room handling below
            }
            else if (overlayNode != null)
            {
                // ── 30-second decision timeout: fall back to random ──
                if (_sameScreenDuration > NON_COMBAT_DECISION_TIMEOUT && _sameScreenTickCount > 20)
                {
                    MainFile.Logger.Warn($"[AutoSlay] OVERLAY decision timeout ({_sameScreenDuration:F0}s, type={overlayNode.GetType().Name}) — falling back to random");
                    _lastScreenType = ""; _sameScreenDuration = 0; _sameScreenTickCount = 0;
                    RandomOverlayFallback(overlayNode);
                    return;
                }
                // HOST GUARD: when host has auto-battle OFF, skip LLM auto-handling
                // so the human player can interact with overlays manually.
                // Falls through to DispatchOverlay which also has the host guard.
                if (!IsHostManualMode && _llm != null && TryRequestLlmForOverlay(overlayNode))
                    return;
                try { _cooldown = DispatchOverlay(overlayNode, delta); }
                catch (Exception ex) { MainFile.Logger.Error($"[AutoSlay] DispatchOverlay CRASH on {overlayNode.GetType().Name}: {ex.Message}"); _cooldown = 0.5; }
                return;
            }
            else
            {
                return;
            }
        }
        _rewardsLlmDone = false; // reset once we're past overlays

        // ── Multiplayer: lobby screen (bot auto-ready after joining) ─────
        if (_multiplayerMode && !_isMultiplayerHost && _multiplayerJoined)
        {
            // Check for lobby screen
            var lobbyNode = GetNodeOrNull<Node>("/root/Game/RootSceneContainer/Run/RoomContainer/LobbyRoom")
                ?? GetNodeOrNull<Control>("/root/Game/RootSceneContainer/Lobby");
            if (lobbyNode != null)
            {
                _cooldown = HandleMultiplayerLobby();
                return;
            }
        }

        // ── Multiplayer: character select screen ────────────────────────
        if (_multiplayerMode && !_isMultiplayerHost && _multiplayerJoined)
        {
            var mpCharSelect = GetNodeOrNull<Node>("/root/Game/RootSceneContainer/Run/RoomContainer/CharacterSelectRoom")
                ?? GetNodeOrNull<Control>("/root/Game/RootSceneContainer/CharacterSelectScreen")
                ?? GetNodeOrNull<Control>("/root/Game/RootSceneContainer/CharacterSelect");
            if (mpCharSelect != null)
            {
                _cooldown = HandleMultiplayerCharacterSelect();
                return;
            }
        }

        // ── Multiplayer: character select for HOST ────────────────────────
        // Only auto-select the host's character when auto-battle is ON
        // (e.g. pure AI-vs-AI mode). When auto-battle is OFF, the human
        // player should manually pick their character and confirm.
        if (_multiplayerMode && _isMultiplayerHost && _multiplayerJoined && _autoBattle)
        {
            var mpCharSelect = GetNodeOrNull<Node>("/root/Game/RootSceneContainer/Run/RoomContainer/CharacterSelectRoom")
                ?? GetNodeOrNull<Control>("/root/Game/RootSceneContainer/CharacterSelectScreen")
                ?? GetNodeOrNull<Control>("/root/Game/RootSceneContainer/CharacterSelect");
            if (mpCharSelect != null)
            {
                _cooldown = HandleMultiplayerCharacterSelect();
                return;
            }
        }

        // ── Main Menu — auto-start run (HIGHEST priority before non-combat) ──
        var mainMenu = GetNodeOrNull<Control>("/root/Game/RootSceneContainer/MainMenu");
        if (mainMenu != null && mainMenu.IsVisibleInTree())
        {
            // ── Multiplayer bot: after joining, WAIT for screen transition ──
            // Do NOT fall through to single-player HandleMainMenu logic, which
            // would click "New Game" / "Continue" and cause the page to jump
            // back to main menu every time the screen briefly shows it during
            // transitions. The bot should do NOTHING on the main menu after
            // joining — just wait for LOBBY or CHARACTER_SELECT to appear.
            //
            // BUT: if we've been waiting too long (connection failed, error popup
            // dismissed, now back at main menu), reset and retry.
            if (_multiplayerMode && !_isMultiplayerHost && _multiplayerJoined)
            {
                double joinWaitElapsed = (_mpJoinClickedTime > 0)
                    ? Time.GetUnixTimeFromSystem() - _mpJoinClickedTime
                    : 0;

                // If we're back at the main menu with the Multiplayer button
                // visible AND we've waited >15s since clicking Join, the
                // connection likely failed. Reset and let HandleMultiplayerMenu
                // retry.
                var mpBtnCheck = mainMenu.GetNodeOrNull<NButton>("MainMenuTextButtons/MultiplayerButton");
                bool mainMenuReady = mpBtnCheck != null && mpBtnCheck.Visible;

                if (mainMenuReady && joinWaitElapsed > MP_JOIN_RETRY_DELAY)
                {
                    MainFile.Logger.Warn($"[AutoSlay] Join likely failed (back at main menu after {joinWaitElapsed:F0}s). Resetting for retry.");
                    _multiplayerJoined = false;
                    _mpButtonClicked = false; // re-navigate from scratch
                    _mpHostButtonClicked = false;
                    _mpStandardButtonClicked = false;
                    _mpSubmenuSeenTime = 0;
                    _cooldown = 1.0;
                    return;
                }

                if (_debugFrame % 120 == 0)
                    MainFile.Logger.Info($"[AutoSlay] Bot: waiting for lobby/character select ({joinWaitElapsed:F0}s elapsed)");
                _cooldown = 1.0;
                return;
            }

            _hpBoosted = false;
            _cooldown = HandleMainMenu(mainMenu);
            return;
        }

        RewardsHandler.ClearTried();

        // ── Room state reset: ensure "was in room" flags are cleared when
        // we've actually left the room. These resets MUST happen here (before
        // map/room handling) because the Map handler returns early and would
        // otherwise skip the per-room resets at the bottom of each block.
        // Bug: _wasInRest leaked across campfires when map opened during cooldown
        //      → next campfire skipped init → entered post-choice flow with
        //      stale _restSiteChoiceMade=true → force-proceed → entire campfire skipped.
        var currentNonCombatScreen = GameStateDetector.Detect();
        if (currentNonCombatScreen != GameScreen.REST)
        {
            if (_wasInRest)
            {
                MainFile.Logger.Info("[AutoSlay] State: leaving rest site (detected via screen change)");
                _wasInRest = false;
                _restSiteChoiceMade = false;
                _restStuckFrames = 0;
            }
        }
        if (currentNonCombatScreen != GameScreen.TREASURE)
        {
            // Reset treasure room state machine when we're not in a treasure room.
            // Prevents stale _state (ChestOpened/Picking) from leaking across rooms
            // if the user manually interacted or a transition was missed.
            if (_wasInTreasure)
            {
                MainFile.Logger.Info("[AutoSlay] State: leaving treasure room (detected via screen change)");
                _wasInTreasure = false;
                TokenSpire2.Solver.TreasureDecider.Reset();
            }
        }

        // ── Map ──────────────────────────────────────────────────────────────
        // Only handle map when no overlays are active — prevents expensive
        // path planning from running between card reward dismissals.
        var mapScreen = NMapScreen.Instance;
        if (mapScreen != null && mapScreen.IsOpen && NOverlayStack.Instance?.ScreenCount == 0)
        {
            // ── Toggle guard: skip if auto-navigate is OFF ──
            // HOST GUARD: also skip when host has auto-battle OFF (human is playing)
            if (!_autoNavigate || IsHostManualMode)
            {
                _cooldown = 3.0;
                return;
            }

            _gameOverReflected = false;
            RunSummaryLogger.Reset();
            if (_llm != null)
            {
                var prompt = GameStateSerializer.SerializeMap(mapScreen);
                _pendingLlm = _llm.SendAsync(prompt, "map");
                _pendingContext = "map";
                return;
            }
            // ── 30-second decision timeout: fall back to random ──
            if (_sameScreenDuration > NON_COMBAT_DECISION_TIMEOUT && _sameScreenTickCount > 20)
            {
                MainFile.Logger.Warn($"[AutoSlay] MAP decision timeout ({_sameScreenDuration:F0}s) — falling back to random");
                _lastScreenType = ""; _sameScreenDuration = 0; _sameScreenTickCount = 0;
                var pts = AutoSlayHelpers.FindAll<NMapPoint>(mapScreen).Where(p => p.IsEnabled).ToList();
                if (pts.Count > 0) { var pt = pts[_rng.Next(pts.Count)]; mapScreen.OnMapPointSelectedLocally(pt); }
                _cooldown = 1.0;
                return;
            }
            MainFile.Logger.Info("[AutoSlay] MAP screen detected, calling DecisionEngine");
            // In multiplayer, bots must wait for all players to select before
            // the map advances. Tell MapDecider so it doesn't mistake waiting
            // for a rejected click and retry infinitely.
            MapDecider.InMultiplayerRun = _multiplayerMode;
            try { DecisionEngine.Decide(GameScreen.MAP, delta); }
            catch (Exception ex) { MainFile.Logger.Error($"[AutoSlay] MAP DecisionEngine CRASH: {ex.Message}"); }
            _cooldown = 1.0;
            return;
        }

        // ── Event room ───────────────────────────────────────────────────────
        var eventRoom = GetNodeOrNull<Node>(
            "/root/Game/RootSceneContainer/Run/RoomContainer/EventRoom");
        if (eventRoom != null)
        {
            // ── Toggle guard: skip if auto-event is OFF ──
            // HOST GUARD: also skip when host has auto-battle OFF (human is playing)
            if (!_autoEvent || IsHostManualMode)
            {
                _cooldown = 3.0;
                return;
            }

            if (_llm != null)
            {
                var options = AutoSlayHelpers.FindAll<NEventOptionButton>(eventRoom)
                    .Where(o => !o.Option.IsLocked).ToList();
                if (options.Count == 1 && options[0].Option.IsProceed)
                {
                    // Only option is Proceed — auto-click without LLM
                    options[0].ForceClick();
                    _cooldown = 1.0;
                    return;
                }
                if (options.Count > 0)
                {
                    var prompt = GameStateSerializer.SerializeEvent(eventRoom);
                    _pendingLlm = _llm.SendAsync(prompt, "event");
                    _pendingContext = "event";
                    return;
                }
            }
            // ── 30-second decision timeout: fall back to random ──
            if (_sameScreenDuration > NON_COMBAT_DECISION_TIMEOUT && _sameScreenTickCount > 20)
            {
                MainFile.Logger.Warn($"[AutoSlay] EVENT decision timeout ({_sameScreenDuration:F0}s) — falling back to random");
                _lastScreenType = ""; _sameScreenDuration = 0; _sameScreenTickCount = 0;
                var eventOpts = AutoSlayHelpers.FindAll<NEventOptionButton>(eventRoom).Where(o => !o.Option.IsLocked).ToList();
                if (eventOpts.Count > 0) { eventOpts[_rng.Next(eventOpts.Count)].ForceClick(); }
                _cooldown = 1.0;
                return;
            }
            MainFile.Logger.Info("[AutoSlay] EVENT room detected, calling DecisionEngine");
            bool eventDecided = false;
            try { eventDecided = DecisionEngine.Decide(GameScreen.EVENT, delta); }
            catch (Exception ex) { MainFile.Logger.Error($"[AutoSlay] EVENT DecisionEngine CRASH: {ex.Message}"); }
            _cooldown = eventDecided ? 1.0 : 0.5;
            return;
        }

        // ── Treasure room ────────────────────────────────────────────────────
        var treasureRoom = GetNodeOrNull<NTreasureRoom>(
            "/root/Game/RootSceneContainer/Run/RoomContainer/TreasureRoom");
        if (treasureRoom != null)
        {
            // ── Toggle guard: skip if auto-event is OFF ──
            // HOST GUARD: also skip when host is in manual mode
            if (!_autoEvent || IsHostManualMode)
            {
                _cooldown = 3.0;
                return;
            }

            // ── Reset state machine when entering a NEW treasure room ──
            if (!_wasInTreasure)
            {
                _wasInTreasure = true;
                TokenSpire2.Solver.TreasureDecider.Reset();
                MainFile.Logger.Info("[AutoSlay] Entering new treasure room");
            }

            // ── 30-second decision timeout: fall back to random ──
            if (_sameScreenDuration > NON_COMBAT_DECISION_TIMEOUT && _sameScreenTickCount > 20)
            {
                MainFile.Logger.Warn($"[AutoSlay] TREASURE decision timeout ({_sameScreenDuration:F0}s) — falling back to random");
                _lastScreenType = ""; _sameScreenDuration = 0; _sameScreenTickCount = 0;
                // Try chest first, then relic, then proceed
                var tChest = treasureRoom.GetNodeOrNull<NClickableControl>("Chest");
                if (tChest != null && GodotObject.IsInstanceValid(tChest) && tChest.IsEnabled) { tChest.ForceClick(); }
                else { var tRelics = AutoSlayHelpers.FindAll<NTreasureRoomRelicHolder>(treasureRoom).Where(r => GodotObject.IsInstanceValid(r)).ToList(); if (tRelics.Count > 0) tRelics[0].ForceClick(); else treasureRoom.ProceedButton?.ForceClick(); }
                _cooldown = 1.0;
                return;
            }
            MainFile.Logger.Info("[AutoSlay] TREASURE room detected, calling DecisionEngine");
            try { DecisionEngine.Decide(GameScreen.TREASURE, delta); }
            catch (Exception ex) { MainFile.Logger.Error($"[AutoSlay] TREASURE DecisionEngine CRASH: {ex.Message}"); }
            _cooldown = 1.0;
            return;
        }

        // ── Rest site ────────────────────────────────────────────────────────
        var rsNode = ((SceneTree)Engine.GetMainLoop()).Root
            .GetNodeOrNull<Node>("Game/RootSceneContainer");
        var restRoom = rsNode?.GetNodeOrNull<NRestSiteRoom>("Run/RoomContainer/RestSiteRoom");
        // Guard: only interact with the rest room when the game is actually on
        // the REST screen. During REST→MAP transitions, NRestSiteRoom persists
        // in the scene tree for several frames after Proceed is clicked. Without
        // this guard, we'd re-enter rest handling during transitions, re-set
        // _wasInRest=true, and skip RestDecider on the next campfire.
        bool screenIsRest = GameStateDetector.Detect() == GameScreen.REST;
        if (restRoom != null && screenIsRest)
        {
            // ── Toggle guard: skip if auto-navigate is OFF ──
            // HOST GUARD: also skip when host is in manual mode
            if (!_autoNavigate || IsHostManualMode)
            {
                _cooldown = 3.0;
                return;
            }

            // ── Reset choice flag when entering a NEW rest site ──────────
            // SpeedX AutoProceed may have clicked Proceed on the previous rest site,
            // bypassing our reset at line 1654. Without this, _restSiteChoiceMade
            // stays true and the RestDecider is skipped on subsequent campfires.
            if (!_wasInRest)
            {
                _restSiteChoiceMade = false;
                _wasInRest = true;
                _restStuckFrames = 0;
            }

            // ── 30-second decision timeout: fall back to random ──
            if (!_restSiteChoiceMade && _sameScreenDuration > NON_COMBAT_DECISION_TIMEOUT && _sameScreenTickCount > 20)
            {
                MainFile.Logger.Warn($"[AutoSlay] REST decision timeout ({_sameScreenDuration:F0}s) — falling back to random");
                _lastScreenType = ""; _sameScreenDuration = 0; _sameScreenTickCount = 0;
                var rBtns = AutoSlayHelpers.FindAll<NRestSiteButton>(restRoom).Where(b => b.Option.IsEnabled).ToList();
                if (rBtns.Count > 0) { rBtns[_rng.Next(rBtns.Count)].ForceClick(); _restSiteChoiceMade = true; }
                else { restRoom.ProceedButton?.ForceClick(); _restSiteChoiceMade = true; }
                _cooldown = 1.5;
                return;
            }
            if (_llm != null && !_restSiteChoiceMade)
            {
                var btns = AutoSlayHelpers.FindAll<NRestSiteButton>(restRoom)
                    .Where(b => b.Option.IsEnabled).ToList();
                if (btns.Count > 0)
                {
                    var prompt = GameStateSerializer.SerializeRestSite(restRoom);
                    _pendingLlm = _llm.SendAsync(prompt, "restsite");
                    _pendingContext = "restsite";
                    return;
                }
            }
            if (_restSiteChoiceMade)
            {
                // Choice already made — wait for proceed or overlay (e.g. upgrade card select)

                // ── Handle card grid within the rest room ───────────────────
                // Rest site actions (Smith/Upgrade/Transform) show a card
                // selection that may be inside the rest room hierarchy,
                // NOT in NOverlayStack. Handle it inline.
                var cardGridHolders = AutoSlayHelpers.FindAll<NGridCardHolder>(restRoom);
                if (cardGridHolders.Count > 0)
                {
                    _restStuckFrames = 0; // reset stuck counter on activity
                    MainFile.Logger.Info($"[AutoSlay] REST: {cardGridHolders.Count} card grid holders found");

                    // Phase 3: Preview visible — confirm it
                    var visiblePreview = restRoom.GetNodeOrNull<Control>("%PreviewContainer")
                        ?? restRoom.GetNodeOrNull<Control>("%UpgradeSinglePreviewContainer")
                        ?? restRoom.GetNodeOrNull<Control>("%UpgradeMultiPreviewContainer")
                        ?? restRoom.GetNodeOrNull<Control>("%EnchantSinglePreviewContainer")
                        ?? restRoom.GetNodeOrNull<Control>("%EnchantMultiPreviewContainer");
                    if (visiblePreview?.Visible == true)
                    {
                        var previewConfirm = AutoSlayHelpers.FindFirst<NConfirmButton>(visiblePreview);
                        if (previewConfirm?.IsEnabled == true)
                        {
                            MainFile.Logger.Info("[AutoSlay] REST: Clicking preview confirm");
                            previewConfirm.ForceClick();
                            _cooldown = 0.5;
                            return;
                        }
                    }

                    // Phase 2: Main confirm enabled — click to show preview
                    var mainConfirm = restRoom.GetNodeOrNull<NConfirmButton>("Confirm")
                        ?? restRoom.GetNodeOrNull<NConfirmButton>("%Confirm");
                    if (mainConfirm?.IsEnabled == true)
                    {
                        MainFile.Logger.Info("[AutoSlay] REST: Clicking main confirm (preview will appear)");
                        mainConfirm.ForceClick();
                        _cooldown = 0.5;
                        return;
                    }

                    // Phase 1: Select a card using upgrade scoring
                    // Set context for AutoSlayCardSelector (global ICardSelector)
                    // so it uses UPGRADE scoring if called by the game engine.
                    TokenSpire2.Solver.DecisionEngine.PendingCardSelectContext = "UPGRADE";
                    var restRunState = new TokenSpire2.Solver.RunState();
                    restRunState.Refresh();
                    // Pick card with highest upgrade benefit
                    var cards = cardGridHolders;
                    int bestIdx = -1;
                    double bestScore = double.MinValue;
                    for (int i = 0; i < cards.Count; i++)
                    {
                        double s = CardGridDecider.ScoreCardForUpgradePublic(cards[i].CardModel, restRunState);
                        if (s > bestScore) { bestScore = s; bestIdx = i; }
                    }
                    if (bestIdx >= 0)
                    {
                        string cardId = cards[bestIdx].CardModel?.Id.Entry ?? "?";
                        MainFile.Logger.Info($"[AutoSlay] REST: Selecting {cardId} for upgrade (score={bestScore:F0})");
                        // Find the grid and emit the selection signal
                        var grid = AutoSlayHelpers.FindFirst<MegaCrit.Sts2.Core.Nodes.Cards.NCardGrid>(restRoom);
                        if (grid != null)
                            grid.EmitSignal(MegaCrit.Sts2.Core.Nodes.Cards.NCardGrid.SignalName.HolderPressed, cards[bestIdx]);
                        else
                            MainFile.Logger.Error("[AutoSlay] REST: NCardGrid not found in rest room!");
                    }
                    _cooldown = 0.5;
                    return;
                }

                // ── Handle overlay card grid (upgrade/transform screen in NOverlayStack) ──
                if (NOverlayStack.Instance?.ScreenCount > 0)
                {
                    _restStuckFrames = 0; // overlay is being handled
                    _cooldown = 0.5;
                    return; // let overlay handler deal with it
                }

                // ── Try proceed button ────────────────────────────────────
                var proceed = restRoom.ProceedButton;
                if (proceed?.IsEnabled == true)
                {
                    MainFile.Logger.Info("[AutoSlay] Clicking rest site proceed");
                    proceed.ForceClick();
                    _restSiteChoiceMade = false; // reset immediately after clicking
                    _restStuckFrames = 0;
                    _cooldown = 2.0;
                }
                else
                {
                    // ── Stuck detection: if no card grid, no overlay, no proceed ──
                    _restStuckFrames++;
                    if (_restStuckFrames > 90) // ~3 seconds stuck at rest site
                    {
                        MainFile.Logger.Error($"[AutoSlay] REST STUCK: {_restStuckFrames} frames with no card grid, no overlay, no proceed. Force-clicking proceed.");
                        proceed?.ForceClick();
                        _restSiteChoiceMade = false;
                        _restStuckFrames = 0;
                        _cooldown = 2.0;
                    }
                    else
                    {
                        _cooldown = 0.5;
                    }
                }
            }
            else
            {
                // ── Solver-based rest site decision (no LLM) ────────────────
                // RestDecider may fail if buttons aren't loaded yet.
                // We retry with a stuck counter; if too many failures,
                // force-click Proceed to avoid getting stuck.
                bool decided = false;
                try { decided = DecisionEngine.Decide(GameScreen.REST, delta); }
                catch (Exception ex) { MainFile.Logger.Error($"[AutoSlay] REST DecisionEngine CRASH: {ex.Message}"); }

                if (decided)
                {
                    _restSiteChoiceMade = true;
                    _restStuckFrames = 0;
                }
                else
                {
                    _restStuckFrames++;
                    if (_restStuckFrames > 10) // ~15s at 1.5s cooldown (was 15/~22s)
                    {
                        MainFile.Logger.Error($"[AutoSlay] REST: DecisionEngine failed {_restStuckFrames} times. Force-clicking proceed.");
                        var fallbackProceed = restRoom.ProceedButton;
                        fallbackProceed?.ForceClick();
                        _restSiteChoiceMade = true;
                        _restStuckFrames = 0;
                    }
                }
                _cooldown = 1.5;
            }
            return;
        }
        _restSiteChoiceMade = false; // reset when we leave the rest site
        _wasInRest = false;

        // ── Shop ─────────────────────────────────────────────────────────────
        var shopRoom = GetNodeOrNull<NMerchantRoom>(
            "/root/Game/RootSceneContainer/Run/RoomContainer/MerchantRoom");
        if (shopRoom != null)
        {
            // ── Toggle guard: skip if auto-navigate is OFF ──
            // HOST GUARD: also skip when host is in manual mode
            if (!_autoNavigate || IsHostManualMode)
            {
                _cooldown = 3.0;
                return;
            }

            // Leaving state: click proceed when available
            if (_shopLeaving)
            {
                if (shopRoom.ProceedButton?.IsEnabled == true)
                {
                    shopRoom.ProceedButton.ForceClick();
                    _shopLeaving = false;
                    _shopInventoryOpened = false;
                    _shopStuckFrames = 0;
                    _cooldown = 1.0;
                }
                else
                {
                    // Close inventory if still open
                    AutoSlayHelpers.FindFirst<NBackButton>(shopRoom)?.ForceClick();
                    // ── Stuck detection: if proceed never enables ──────────
                    _shopStuckFrames++;
                    if (_shopStuckFrames > 300) // ~10 seconds stuck
                    {
                        MainFile.Logger.Error($"[AutoSlay] SHOP STUCK: {_shopStuckFrames} frames waiting for proceed. Force-clicking.");
                        shopRoom.ProceedButton?.ForceClick();
                        _shopLeaving = false;
                        _shopInventoryOpened = false;
                        _shopStuckFrames = 0;
                        _cooldown = 1.0;
                    }
                    else
                    {
                        _cooldown = 0.5;
                    }
                }
                return;
            }
            _shopStuckFrames = 0; // reset when not in leaving state
            if (_llm != null && _pendingLlm == null && _shopPlan == null)
            {
                // Open inventory if not already open
                if (!_shopInventoryOpened)
                {
                    shopRoom.OpenInventory();
                    _shopInventoryOpened = true;
                    _cooldown = 0.5;
                    return;
                }
                LogOnce("Handling shop (LLM)");
                var prompt = GameStateSerializer.SerializeShop(shopRoom);
                _pendingLlm = _llm.SendAsync(prompt, "shop");
                _pendingContext = "shop";
                return;
            }
            if (_shopPlan != null)
            {
                ExecuteNextShopStep(shopRoom);
                return;
            }
            // ── 30-second decision timeout: fall back to random (leave shop) ──
            if (_sameScreenDuration > NON_COMBAT_DECISION_TIMEOUT && _sameScreenTickCount > 20)
            {
                MainFile.Logger.Warn($"[AutoSlay] SHOP decision timeout ({_sameScreenDuration:F0}s) — falling back to random (leaving shop)");
                _lastScreenType = ""; _sameScreenDuration = 0; _sameScreenTickCount = 0;
                shopRoom.ProceedButton?.ForceClick();
                _shopLeaving = true; _shopStuckFrames = 0;
                _cooldown = 1.0;
                return;
            }
            if (_llm == null)
            {
                MainFile.Logger.Info("[AutoSlay] SHOP detected, calling DecisionEngine");
                try { DecisionEngine.Decide(GameScreen.SHOP, delta); }
                catch (Exception ex) { MainFile.Logger.Error($"[AutoSlay] SHOP DecisionEngine CRASH: {ex.Message}"); }
                _cooldown = 0.5;
                return;
            }
            return;
        }
        else
        {
            _shopInventoryOpened = false;
            _shopLeaving = false;
        }

        // ── Victory proceed ──────────────────────────────────────────────────
        var combatRoom = NCombatRoom.Instance;
        if (combatRoom != null)
        {
            var proceed = combatRoom.ProceedButton;
            if (proceed != null && proceed.IsEnabled)
            {
                LogOnce("Clicking combat room proceed");
                proceed.ForceClick();
                _cooldown = 1.0;
                return;
            }
        }

        // ── Diagnostic: log once when main menu node is missing/invisible ──
        if (_debugFrame++ % 120 == 0)
        {
            var menuNode = GetNodeOrNull<Control>("/root/Game/RootSceneContainer/MainMenu");
            string status = menuNode == null ? "NULL" :
                menuNode.IsVisibleInTree() ? "VISIBLE" : "HIDDEN";
            var children = "";
            try
            {
                var root = ((SceneTree)Engine.GetMainLoop()).Root;
                var game = root.GetNodeOrNull<Node>("Game");
                var rsc = game?.GetNodeOrNull<Node>("RootSceneContainer");
                children = rsc != null
                    ? string.Join(",", rsc.GetChildren().Select(c => c.Name?.ToString() ?? "?"))
                    : "NO_RSC";
            }
            catch { children = "ERR"; }
            MainFile.Logger.Info($"[AutoSlay] MainMenu diag: status={status} runEver={_runEverStarted} children=[{children}]");
        }

        // ── Dismiss "Continue Run?" prompts ──────────────────────────────
        // Only runs when main menu is NOT visible (modal/overlay blocking it).
        DismissContinuePrompt();
        if (_cooldown > 0) return;

        // ── Nothing matched ──────────────────────────────────────────────────
        if (_logTimer <= 0)
        {
            var overlayCount = NOverlayStack.Instance?.ScreenCount ?? -1;
            var mapOpen = NMapScreen.Instance?.IsOpen ?? false;
            var cmInProgress = cm?.IsInProgress ?? false;
            MainFile.Logger.Info($"[AutoSlay] Idle: overlays={overlayCount} map={mapOpen} combat={cmInProgress}");
            _logTimer = 5.0;
        }
    }

    private bool CheckEnrichedProceed()
    {
        try
        {
            if (_llm == null) return true;
            // Derive enriched file path from history path
            var historyPath = _llm.HistoryPath;
            if (string.IsNullOrEmpty(historyPath)) return true;
            var enrichedPath = historyPath.Replace("llm_history_", "llm_enriched_");
            if (!System.IO.File.Exists(enrichedPath)) return false;

            var json = System.IO.File.ReadAllText(enrichedPath);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var runs = doc.RootElement;
            if (runs.GetArrayLength() == 0) return false;

            // Check last assistant message in last run
            var lastRun = runs[runs.GetArrayLength() - 1];
            var msgs = lastRun.GetProperty("messages");

            // Enriched file must have at least as many messages as the history —
            // otherwise the viewer hasn't caught up yet and we'd read stale should_proceed
            var expectedCount = _llm?.MessageCount ?? 0;
            if (expectedCount > 0 && msgs.GetArrayLength() < expectedCount)
                return false;

            for (int i = msgs.GetArrayLength() - 1; i >= 0; i--)
            {
                var msg = msgs[i];
                if (msg.GetProperty("role").GetString() == "assistant")
                {
                    if (msg.TryGetProperty("should_proceed", out var sp) && sp.GetBoolean())
                    {
                        MainFile.Logger.Info("[AutoSlay] Viewer audio proceed signal received");
                        return true;
                    }
                    return false;
                }
            }
            return false;
        }
        catch { return true; } // proceed on error to avoid permanent stall
    }

    // Safety counter for _abandonPending to prevent infinite loop
    private int _abandonPendingFrames;
    private bool _abandonConfirmed; // true after clicking "好的" — prevent re-clicking AbandonRunButton
    private int _abandonConfirmedFrames; // counter for post-confirm wait
    private int _mainMenuFrames; // counter for MainMenu confirmation before signaling run complete

    /// <summary>
    /// Nuclear option: delete the current_run.save file from disk.
    /// This is the most reliable way to prevent "continue run?" prompts —
    /// no save file = no stale run = fresh start every time.
    /// Called once in _Ready, before any UI interaction.
    /// </summary>
    private static void DeleteStaleSaveFile()
    {
        try
        {
            string appData = System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData);
            string savesRoot = System.IO.Path.Combine(appData, "SlayTheSpire2", "steam");
            if (!System.IO.Directory.Exists(savesRoot)) return;

            foreach (var saveFile in System.IO.Directory.GetFiles(savesRoot, "current_run.save*",
                System.IO.SearchOption.AllDirectories))
            {
                try
                {
                    System.IO.File.Delete(saveFile);
                    MainFile.Logger.Info($"[AutoSlay] Deleted stale save: {saveFile}");
                }
                catch (Exception ex)
                {
                    MainFile.Logger.Info($"[AutoSlay] Could not delete {saveFile}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            MainFile.Logger.Info($"[AutoSlay] DeleteStaleSaveFile error: {ex.Message}");
        }
    }

    /// <summary>
    /// Dismiss any "Continue Run?" prompt that appears as a modal or overlay.
    /// This ONLY handles popup dialogs — main menu handling is in HandleMainMenu.
    /// Does NOT scan the full root tree (too aggressive — would mis-click
    /// main menu buttons like "退出").
    /// </summary>
    private void DismissContinuePrompt()
    {
        try
        {
            // ── Check 1: NModalContainer ────────────────────────────────
            var modal = NModalContainer.Instance?.OpenModal;
            if (modal != null)
            {
                // MULTIPLAYER: NRewardsScreen won't dismiss until all players sync.
                // Skip — the main loop handles this with multiplayer guard.
                if (_multiplayerMode && modal is NRewardsScreen)
                    return;
                if (TryClickDismissInModal((Node)modal)) return;
            }

            // ── Check 2: NOverlayStack overlays ──────────────────────────
            try
            {
                var overlayStack2 = NOverlayStack.Instance;
                if (overlayStack2?.ScreenCount > 0)
                {
                    var overlay = overlayStack2.Peek() as Node;
                    // MULTIPLAYER: skip NRewardsScreen — main loop handles it
                    if (_multiplayerMode && overlay is NRewardsScreen)
                        return;
                    if (overlay != null && TryClickDismissInModal(overlay)) return;
                }
            }
            catch { }
        }
        catch (Exception ex)
        {
            MainFile.Logger.Info($"[AutoSlay] DismissContinuePrompt error: {ex.Message}");
        }
    }

    /// <summary>
    /// Try to find and click the "No/Abandon/Cancel" button in a modal/overlay.
    /// This is for dismissing "Continue Run?" prompts.
    /// Returns true if a button was clicked.
    /// </summary>
    private bool TryClickDismissInModal(Node container)
    {
        var allBtns = AutoSlayHelpers.FindAll<NButton>(container)
            .Where(b => {
                try { return b.IsEnabled && (b.Visible || b.IsVisibleInTree()); }
                catch { return false; }
            }).ToList();

        if (allBtns.Count == 0) return false;

        // Log for debugging
        var names = string.Join(", ", allBtns.Select(b => b.Name?.ToString() ?? "?"));
        LogOnce($"DismissModal: {allBtns.Count} buttons: [{names}]");

        // ── Backoff: if we keep clicking Proceed on the same modal, back off ──
        // Prevents infinite click-loop after combat rewards in multiplayer where
        // the Proceed button doesn't dismiss until host sync completes.
        if (names == _lastDismissBtnNames)
        {
            _dismissProceedClicks++;
        }
        else
        {
            _dismissProceedClicks = 0;
            _lastDismissBtnNames = names;
        }
        if (_dismissProceedClicks > 5)
        {
            // After 5 clicks with no change, we're in a loop. Skip this modal.
            LogOnce($"DismissModal BACKOFF: {_dismissProceedClicks} clicks on [{names}], giving up (will retry after 10s)");
            _cooldown = 10.0;  // Long cooldown — let MP sync or map transition happen
            return false;
        }
        if (_dismissProceedClicks >= 3)
        {
            // After 3 clicks, use longer cooldown to give multiplayer sync time
            LogOnce($"DismissModal throttling: {_dismissProceedClicks} clicks on [{names}], slowing down");
        }
        var _dismissCooldown = _dismissProceedClicks >= 3 ? 3.0 : 1.0;

        // Step 1: Look for dismiss-type buttons (No/Cancel/Abandon/Exit/不/取消/放弃/退出/不了/否)
        foreach (var btn in allBtns)
        {
            var n = (btn.Name?.ToString() ?? "").ToLowerInvariant();
            if (n.Contains("no") || n.Contains("cancel") || n.Contains("abandon")
                || n.Contains("不") || n.Contains("取消") || n.Contains("放弃")
                || n.Contains("退出") || n.Contains("不了") || n.Contains("否"))
            {
                LogOnce($"DismissModal: clicking {btn.Name}");
                btn.ForceClick();
                _cooldown = _dismissCooldown;
                return true;
            }
        }

        // Step 2: If exactly 2 buttons, prefer Proceed (dismiss without action),
        // then click the one that ISN'T Yes/Continue/Confirm/好的.
        // CRITICAL: In multiplayer, the potion-discard dialog has [PotionButton, ProceedButton].
        // Clicking the potion button triggers another potion reward → infinite loop!
        // Always prefer Proceed when it's one of the options.
        if (allBtns.Count == 2)
        {
            var proceedBtn = allBtns.FirstOrDefault(b => {
                var n = (b.Name?.ToString() ?? "").ToLowerInvariant();
                return n.Contains("proceed");
            });
            if (proceedBtn != null)
            {
                LogOnce($"DismissModal (2-btn w/Proceed): clicking Proceed to dismiss");
                proceedBtn.ForceClick();
                _cooldown = _dismissCooldown;
                return true;
            }

            var notYes = allBtns.FirstOrDefault(b => {
                var n = (b.Name?.ToString() ?? "").ToLowerInvariant();
                return !n.Contains("yes") && !n.Contains("continue") && !n.Contains("confirm")
                    && !n.Contains("resume") && !n.Contains("ok") && !n.Contains("proceed")
                    && !n.Contains("是") && !n.Contains("继续") && !n.Contains("确认")
                    && !n.Contains("好的") && !n.Contains("好") && !n.Contains("accept");
            });
            if (notYes != null)
            {
                LogOnce($"DismissModal (2-btn): clicking non-Yes {notYes.Name}");
                notYes.ForceClick();
                _cooldown = _dismissCooldown;
                return true;
            }
        }

        // Step 3: If only 1 button and it's Proceed, click it
        if (allBtns.Count == 1)
        {
            var only = allBtns[0];
            var name = (only.Name?.ToString() ?? "").ToLowerInvariant();
            if (name.Contains("proceed"))
            {
                LogOnce($"DismissModal (1-btn): clicking Proceed");
                only.ForceClick();
                _cooldown = _dismissCooldown;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Try to find and click the "Yes/Confirm/OK/好的" button in a modal/overlay.
    /// This is for CONFIRMING actions (like "Abandon Run?" → "好的").
    /// Returns true if a button was clicked.
    /// </summary>
    private bool TryClickConfirmInModal(Node container)
    {
        // Try known node paths first
        var yesBtn = container.GetNodeOrNull<NButton>("VerticalPopup/YesButton")
            ?? container.GetNodeOrNull<NButton>("%YesButton")
            ?? container.GetNodeOrNull<NButton>("YesButton");

        if (yesBtn != null && yesBtn.IsEnabled)
        {
            LogOnce($"ConfirmModal (path): clicking {yesBtn.Name}");
            yesBtn.ForceClick();
            return true;
        }

        // Fallback: scan all buttons for confirm-type patterns
        var allBtns = AutoSlayHelpers.FindAll<NButton>(container)
            .Where(b => {
                try { return b.IsEnabled && (b.Visible || b.IsVisibleInTree()); }
                catch { return false; }
            }).ToList();

        if (allBtns.Count == 0) return false;

        var names = string.Join(", ", allBtns.Select(b => b.Name?.ToString() ?? "?"));
        LogOnce($"ConfirmModal: {allBtns.Count} buttons: [{names}]");

        // Look for Yes/Confirm/OK/好的/是/确认
        var confirmBtn = allBtns.FirstOrDefault(b => {
            var n = (b.Name?.ToString() ?? "").ToLowerInvariant();
            return n.Contains("yes") || n.Contains("confirm") || n.Contains("ok")
                || n.Contains("是") || n.Contains("确认") || n.Contains("好的")
                || n.Contains("好") || n.Contains("accept");
        });
        if (confirmBtn != null)
        {
            LogOnce($"ConfirmModal (scan): clicking {confirmBtn.Name}");
            confirmBtn.ForceClick();
            return true;
        }

        // If exactly 2 buttons, click the one that ISN'T No/Cancel/不了/不
        if (allBtns.Count == 2)
        {
            var notNo = allBtns.FirstOrDefault(b => {
                var n = (b.Name?.ToString() ?? "").ToLowerInvariant();
                return !n.Contains("no") && !n.Contains("cancel")
                    && !n.Contains("不") && !n.Contains("取消") && !n.Contains("不了");
            });
            if (notNo != null)
            {
                LogOnce($"ConfirmModal (2-btn): clicking {notNo.Name}");
                notNo.ForceClick();
                return true;
            }
        }

        return false;
    }

    private double HandleMainMenu(Control mainMenu)
    {
        // ── Diagnostic: log menu state every 60 frames ──────────────────
        if (_debugFrame % 60 == 0)
        {
            var diagAbandon = mainMenu.GetNodeOrNull<NButton>("MainMenuTextButtons/AbandonRunButton");
            var diagSp = mainMenu.GetNodeOrNull<NButton>("MainMenuTextButtons/SingleplayerButton");
            var diagChar = mainMenu.GetNodeOrNull<Control>("Submenus/CharacterSelectScreen");
            var diagStandard = mainMenu.GetNodeOrNull<NButton>("Submenus/SingleplayerSubmenu/StandardButton");
            MainFile.Logger.Info($"[AutoSlay] HandleMainMenu: abandon={diagAbandon?.Visible}/{diagAbandon?.IsEnabled} " +
                $"sp={diagSp?.Visible}/{diagSp?.IsEnabled} " +
                $"charSelect={diagChar?.Visible} " +
                $"standard={diagStandard?.Visible}/{diagStandard?.IsEnabled} " +
                $"runEver={_runEverStarted} batch={_batchMode} signaled={_runCompleteSignaled}");
        }

        // ═══════════════════════════════════════════════════════════════════
        // RUN COMPLETE GUARD: if we've already signaled run completion,
        // do NOT auto-embark a new run. Just wait for the batch runner to
        // kill the game process. This prevents the mod from starting run
        // after run without stopping for parameter optimization.
        //
        // CO-OP GUARD: never freeze at main menu in co-op mode.
        // Both instances must stay alive for LAN multiplayer.
        // ═══════════════════════════════════════════════════════════════════
        if (_runCompleteSignaled)
        {
            // Run is done — freeze. Don't touch the main menu at all.
            // The batch runner will kill the game process shortly.
            return 2.0;
        }

        // ═══════════════════════════════════════════════════════════════════
        // MULTIPLAYER: route to multiplayer menu instead of singleplayer
        // Client (bot): navigate Multiplayer → Join, then wait for human
        // Host (human): autoBattle disabled, returns idle cooldown
        // ═══════════════════════════════════════════════════════════════════
        if (_multiplayerMode && !_multiplayerJoined)
            return HandleMultiplayerMenu(mainMenu);

        // ═══════════════════════════════════════════════════════════════════
        // Find AbandonRunButton
        // ═══════════════════════════════════════════════════════════════════
        var abandonBtn = mainMenu.GetNodeOrNull<NButton>("MainMenuTextButtons/AbandonRunButton");

        // ═══════════════════════════════════════════════════════════════════
        // BATCH MODE: auto-abandon stale saves
        // ═══════════════════════════════════════════════════════════════════
        if (_batchMode && abandonBtn != null && abandonBtn.Visible && abandonBtn.IsEnabled)
        {
            // ── Check if confirmation popup is open ───────────────────────
            var modal = NModalContainer.Instance?.OpenModal;
            if (modal != null)
            {
                var yesBtn = ((Node)modal).GetNodeOrNull<NButton>("VerticalPopup/YesButton");
                if (yesBtn != null && yesBtn.IsEnabled)
                {
                    LogOnce("Batch: confirming abandon (好的)");
                    yesBtn.ForceClick();
                    _abandonPending = false;
                    _abandonConfirmed = true; // prevent re-clicking AbandonRunButton
                    return 1.5;
                }
                // Modal open but YesButton not found yet — wait
                return 0.5;
            }

            // ── After confirmation, wait for AbandonRunButton to disappear ─
            if (_abandonConfirmed)
            {
                // Abandon confirmed, waiting for main menu to update.
                // If AbandonRunButton is still visible after confirming,
                // the game is still processing — just wait.
                _abandonConfirmedFrames++;
                if (_abandonConfirmedFrames > 20) // ~7s timeout
                {
                    LogOnce("Batch: abandon confirmed timeout — forcing continue");
                    _abandonConfirmed = false;
                    _abandonConfirmedFrames = 0;
                }
                return 1.0;
            }

            // ── If abandon pending (clicked AbandonRunButton, awaiting modal) ──
            if (_abandonPending)
            {
                _abandonPendingFrames++;
                if (_abandonPendingFrames > 20) // ~7s — modal should appear quickly
                {
                    LogOnce("Batch: abandon pending timeout (no modal appeared) — retry");
                    _abandonPending = false;
                    _abandonPendingFrames = 0;
                }
                return 1.0;
            }

            if (!_runEverStarted)
            {
                // ── Fresh launch, stale save: click AbandonRunButton ─────
                _abandonPending = true;
                _abandonPendingFrames = 0;
                LogOnce("Batch: clicking 放弃当前游戏 (Abandon Run)");
                abandonBtn.ForceClick();
                return 2.0;
            }

            // ── Run was started and game is now at MainMenu.
            //    This could be: (a) genuine run end (death/victory),
            //    (b) crash-to-menu (game threw an exception and reset).
            //    Wait a few seconds to confirm it's stable before signaling. ──
            _mainMenuFrames++;
            if (_mainMenuFrames < 60) // ~2 seconds confirmation delay
            {
                if (_mainMenuFrames == 1)
                    MainFile.Logger.Info("[AutoSlay] MainMenu detected after run — waiting to confirm (not a transient)...");
                return 1.0;
            }
            MainFile.Logger.Info($"[AutoSlay] MainMenu confirmed stable after {_mainMenuFrames} frames — run ended");
            _mainMenuFrames = 0;
            SignalRunComplete();
            return 3.0;
        }

        // Clear abandon state if AbandonRunButton is gone (abandon succeeded!)
        _abandonPending = false;
        _abandonPendingFrames = 0;
        _abandonConfirmed = false;
        _abandonConfirmedFrames = 0;
        _mainMenuFrames = 0; // reset MainMenu confirmation counter when not at MainMenu

        // ═══════════════════════════════════════════════════════════════════
        // Character select → embark
        // ═══════════════════════════════════════════════════════════════════

        // Check if character select is already open — select character and embark
        var charSelect = mainMenu.GetNodeOrNull<Control>("Submenus/CharacterSelectScreen");
        if (charSelect != null && charSelect.Visible)
        {
            // Try to click embark if already selected
            var embark = charSelect.GetNodeOrNull<NConfirmButton>("ConfirmButton");
            if (embark != null && embark.IsEnabled)
            {
                // Resolve RANDOM to a specific character
                var targetChar = _character;
                if (targetChar == "RANDOM")
                    targetChar = ValidCharacters[_rng.Next(ValidCharacters.Length)];

                var buttonContainer = charSelect.GetNodeOrNull<Node>("CharSelectButtons/ButtonContainer");
                if (buttonContainer != null)
                {
                    foreach (var btn in AutoSlayHelpers.FindAll<NCharacterSelectButton>(buttonContainer))
                    {
                        if (!btn.IsLocked && btn.Character?.Id.Entry == targetChar)
                        {
                            btn.Select();
                            MainFile.Logger.Info($"[AutoSlay] Selected character: {targetChar}");
                            break;
                        }
                    }
                }
                LogOnce("Clicking embark");
                embark.ForceClick();
                return 3.0;
            }
            return 0.5; // waiting for embark to become enabled
        }

        // Check if singleplayer submenu is open — click Standard
        var standardBtn = mainMenu.GetNodeOrNull<NButton>("Submenus/SingleplayerSubmenu/StandardButton");
        if (standardBtn != null && standardBtn.Visible && standardBtn.IsEnabled)
        {
            LogOnce("Clicking Standard run");
            standardBtn.ForceClick();
            return 1.0;
        }

        // Abandon existing run if needed (non-batch mode only)
        if (!_batchMode && abandonBtn != null && abandonBtn.Visible && abandonBtn.IsEnabled)
        {
            var modal = NModalContainer.Instance?.OpenModal;
            if (modal != null)
            {
                var yesBtn = ((Node)modal).GetNodeOrNull<NButton>("VerticalPopup/YesButton");
                if (yesBtn != null && yesBtn.IsEnabled)
                {
                    LogOnce("Confirming abandon run");
                    yesBtn.ForceClick();
                    return 1.5;
                }
                return 0.5;
            }
            LogOnce("Abandoning existing run");
            abandonBtn.ForceClick();
            return 1.0;
        }

        // Click singleplayer button
        var spBtn = mainMenu.GetNodeOrNull<NButton>("MainMenuTextButtons/SingleplayerButton");
        if (spBtn != null && spBtn.Visible && spBtn.IsEnabled)
        {
            LogOnce("Clicking Singleplayer");
            spBtn.ForceClick();
            return 1.0;
        }

        return 1.0; // waiting for menu to be ready
    }

    // ═══════════════════════════════════════════════════════════════════
    // ═══════════════════════════════════════════════════════════════════
    // Multiplayer menu navigation (--fastmp ENet mode)
    // ═══════════════════════════════════════════════════════════════════
    //
    // With --fastmp flag, the game uses ENet transport (127.0.0.1:33771)
    // instead of Steam matchmaking. No Steam lobby needed.
    //
    // Host: Human navigates Multiplayer → Host → Standard
    //   → Game creates ENet server on port 33771
    // Client: Bot clicks Multiplayer → Join
    //   → NJoinFriendScreen.OnSubmenuOpened detects --fastmp
    //   → Calls FastMpJoin() → auto-connects to 127.0.0.1:33771
    //
    // The client must only reach the Join screen AFTER the host has
    // created the ENet server. Timing is handled by the launcher.

    private bool _mpButtonClicked;
    private bool _mpHostButtonClicked;
    private bool _mpStandardButtonClicked;
    private double _mpSubmenuSeenTime;      // real time when Join button first appeared
    private int _mpJoinAttempt;             // how many times we've tried to join
    private double _mpJoinClickedTime;       // real time when we last clicked Join
    private const double MP_JOIN_DELAY = 8.0;       // seconds to wait before clicking Join (host auto-navigates in ~3s)
    private const double MP_JOIN_RETRY_DELAY = 12.0; // seconds to wait after failed join before retrying
    private const int MP_JOIN_MAX_ATTEMPTS = 5;

    // ── Multiplayer character select ──────────────────────────────────
    private bool _mpCharacterSelected;           // whether character was selected this screen
    private double _mpCharacterSelectedTime;     // real time when character was selected
    private const double MP_CHAR_SELECT_HOST_DELAY = 5.0; // seconds host waits before auto-confirm

    private double HandleMultiplayerMenu(Control mainMenu)
    {
        // ── Shared: Step 1 — Click Multiplayer button ONCE ────────────
        var mpBtn = mainMenu.GetNodeOrNull<NButton>("MainMenuTextButtons/MultiplayerButton");
        if (!_mpButtonClicked && mpBtn != null && mpBtn.Visible && mpBtn.IsEnabled)
        {
            MainFile.Logger.Info($"[AutoSlay] Clicking Multiplayer button... (isHost={_isMultiplayerHost})");
            mpBtn.ForceClick();
            _mpButtonClicked = true;
            _mpSubmenuSeenTime = 0;
            return 2.0;
        }

        // ── HOST: Step 2 — Click Host button, then Standard ──────────
        if (_isMultiplayerHost)
        {
            var buttonContainer = mainMenu.GetNodeOrNull<Control>("Submenus/MultiplayerSubmenu/ButtonContainer");
            if (buttonContainer != null)
            {
                // Step 2a: Click "Host" button
                if (!_mpHostButtonClicked)
                {
                    foreach (var child in buttonContainer.GetChildren())
                    {
                        if (child is NButton btn && btn.Visible && btn.IsEnabled)
                        {
                            var nm = btn.Name?.ToString() ?? "";
                            if (nm.Contains("Host", StringComparison.OrdinalIgnoreCase)
                                || nm.Contains("主持", StringComparison.OrdinalIgnoreCase))
                            {
                                MainFile.Logger.Info($"[AutoSlay] Host: clicking Host button ({nm})");
                                btn.ForceClick();
                                _mpHostButtonClicked = true;
                                return 2.0;
                            }
                        }
                    }
                }

                // Step 2b: Click "Standard" button (after Host submenu appears)
                if (_mpHostButtonClicked && !_mpStandardButtonClicked)
                {
                    // The Standard button may be in a nested submenu
                    var hostSubmenu = mainMenu.GetNodeOrNull<Control>("Submenus/HostSubmenu");
                    var container = hostSubmenu?.GetNodeOrNull<Control>("ButtonContainer") ?? buttonContainer;
                    foreach (var child in container.GetChildren())
                    {
                        if (child is NButton btn && btn.Visible && btn.IsEnabled)
                        {
                            var nm = btn.Name?.ToString() ?? "";
                            if (nm.Contains("Standard", StringComparison.OrdinalIgnoreCase)
                                || nm.Contains("标准", StringComparison.OrdinalIgnoreCase))
                            {
                                MainFile.Logger.Info($"[AutoSlay] Host: clicking Standard button ({nm})");
                                btn.ForceClick();
                                _mpStandardButtonClicked = true;
                                _multiplayerJoined = true;
                                return 3.0; // longer cooldown — game creates ENet server
                            }
                        }
                    }
                }
            }

            // Host auto-navigation done — ENet server should be up now
            if (_mpStandardButtonClicked && _debugFrame % 300 == 0)
                MainFile.Logger.Info("[AutoSlay] Host: waiting for lobby/character screen...");

            return 1.0;
        }

        // ── CLIENT (bot): Step 2 — Find and click Join button ─────────
        var mpButtonContainer = mainMenu.GetNodeOrNull<Control>("Submenus/MultiplayerSubmenu/ButtonContainer");
        NButton? joinBtn = null;

        if (mpButtonContainer != null)
        {
            foreach (var child in mpButtonContainer.GetChildren())
            {
                if (child is NButton btn && btn.Visible && btn.IsEnabled)
                {
                    var nm = btn.Name?.ToString() ?? "";
                    if (nm.Contains("Join", StringComparison.OrdinalIgnoreCase)
                        || nm.Contains("加入", StringComparison.OrdinalIgnoreCase))
                    {
                        joinBtn = btn;
                        break;
                    }
                }
            }
        }

        // ── Step 3: Wait for host ENet server before clicking Join ────
        // The host auto-navigates in ~3s. We wait MP_JOIN_DELAY (8s) to be safe.
        if (joinBtn != null && _mpJoinAttempt < MP_JOIN_MAX_ATTEMPTS)
        {
            double now = Time.GetUnixTimeFromSystem();

            if (_mpSubmenuSeenTime <= 0)
            {
                _mpSubmenuSeenTime = now;
                MainFile.Logger.Info($"[AutoSlay] Join button visible — waiting {MP_JOIN_DELAY}s for host ENet server...");
            }

            double elapsed = now - _mpSubmenuSeenTime;
            double timeSinceLastClick = (_mpJoinClickedTime > 0) ? (now - _mpJoinClickedTime) : 999.0;

            bool waitedEnough = elapsed >= MP_JOIN_DELAY;
            bool retryCooldownPassed = timeSinceLastClick >= MP_JOIN_RETRY_DELAY;

            if (waitedEnough && retryCooldownPassed)
            {
                _mpJoinAttempt++;
                MainFile.Logger.Info($"[AutoSlay] Clicking Join (attempt {_mpJoinAttempt}/{MP_JOIN_MAX_ATTEMPTS}) — " +
                    $"waited {elapsed:F0}s for host ENet server");
                joinBtn.ForceClick();
                _multiplayerJoined = true;
                _mpJoinClickedTime = now;
                _mpSubmenuSeenTime = 0;
                return 2.0;
            }
            else
            {
                if (_debugFrame % 300 == 0)
                {
                    double remaining = MP_JOIN_DELAY - elapsed;
                    double retryRemaining = MP_JOIN_RETRY_DELAY - timeSinceLastClick;
                    MainFile.Logger.Info($"[AutoSlay] Waiting to click Join: hostDelay={remaining:F0}s, retryCooldown={retryRemaining:F0}s");
                }
                return 1.0;
            }
        }

        if (joinBtn != null && _mpJoinAttempt >= MP_JOIN_MAX_ATTEMPTS)
        {
            if (_debugFrame % 300 == 0)
                MainFile.Logger.Error($"[AutoSlay] Max join attempts ({MP_JOIN_MAX_ATTEMPTS}) reached! Giving up.");
            return 5.0;
        }

        // ── Diagnostic logging ────────────────────────────────────────
        if (_debugFrame % 120 == 0)
        {
            MainFile.Logger.Info($"[AutoSlay] HandleMultiplayerMenu: mpClicked={_mpButtonClicked} " +
                $"joinBtnFound={joinBtn != null} mpBtnVisible={mpBtn?.Visible} attempt={_mpJoinAttempt}");
        }

        return 1.0;
    }

    private void DumpNodeTree(Node node, System.Text.StringBuilder sb, string indent, int depth, int maxDepth)
    {
        if (depth > maxDepth) return;
        foreach (var child in node.GetChildren())
        {
            try
            {
                var type = child.GetType().Name;
                var name = child.Name?.ToString() ?? "?";
                var visible = child is CanvasItem ci ? ci.Visible : true;
                sb.AppendLine($"{indent}[{type}] {name} vis={visible}");
                if (visible)
                    DumpNodeTree(child, sb, indent + "  ", depth + 1, maxDepth);
            }
            catch { }
        }
        // If we hit max depth but there are visible children, log a summary
        if (depth == maxDepth)
        {
            foreach (var child in node.GetChildren())
            {
                try
                {
                    if (child is CanvasItem ci && ci.Visible && child.GetChildCount() > 0)
                    {
                        var type = child.GetType().Name;
                        var name = child.Name?.ToString() ?? "?";
                        var childTypes = new System.Text.StringBuilder();
                        foreach (var gc in child.GetChildren())
                        {
                            if (gc is CanvasItem gci && gci.Visible)
                                childTypes.Append($"[{gc.GetType().Name}]{(gc.Name?.ToString() ?? "?")} ");
                        }
                        if (childTypes.Length > 0)
                            sb.AppendLine($"{indent}  → children of [{type}] {name}: {childTypes}");
                    }
                }
                catch { }
            }
        }
    }

    private void DumpVisibleButtons(Node node, System.Text.StringBuilder sb, string indent, int depth, int maxDepth)
    {
        if (depth > maxDepth) return;
        foreach (var child in node.GetChildren())
        {
            try
            {
                var type = child.GetType().Name;
                var name = child.Name?.ToString() ?? "?";
                if (child is NButton btn && btn.Visible)
                    sb.AppendLine($"{indent}[{type}] {name} enabled={btn.IsEnabled}");
                if (child is CanvasItem ci && ci.Visible)
                    DumpVisibleButtons(child, sb, indent + "  ", depth + 1, maxDepth);
            }
            catch { }
        }
    }

    /// <summary>
    /// Handle lobby screen — bot clicks Ready after joining.
    /// </summary>
    private double HandleMultiplayerLobby()
    {
        if (_multiplayerReady) return 2.0;

        var root = GetNodeOrNull<Node>("/root/Game/RootSceneContainer");
        if (root == null) return 1.0;

        // Search for Ready button — by node name (e.g. "ReadyButton", "Ready")
        // or look for ConfirmButton / any enabled button in the lobby
        foreach (var btn in AutoSlayHelpers.FindAll<NButton>(root))
        {
            if (!btn.Visible || !btn.IsEnabled) continue;
            var btnName = btn.Name?.ToString() ?? "";
            if (btnName.Contains("Ready", StringComparison.OrdinalIgnoreCase)
                || btnName.Contains("Confirm", StringComparison.OrdinalIgnoreCase)
                || btn is NConfirmButton)
            {
                LogOnce($"Clicking lobby button: {btnName}");
                btn.ForceClick();
                _multiplayerReady = true;
                return 1.0;
            }
        }

        if (_debugFrame % 120 == 0)
        {
            var lobbyBtns = new System.Text.StringBuilder();
            foreach (var btn in AutoSlayHelpers.FindAll<NButton>(root))
            {
                if (btn.Visible)
                    lobbyBtns.Append($"[{btn.Name}] ");
            }
            MainFile.Logger.Info($"[AutoSlay] Lobby buttons: {lobbyBtns}");
        }
        return 1.0;
    }

    /// <summary>
    /// Handle multiplayer character select — select configured character and confirm.
    ///
    /// Host: wait MP_CHAR_SELECT_HOST_DELAY seconds before auto-confirming so the
    ///        human has a chance to see what's happening and change character if desired.
    /// Bot:  confirm immediately after selection.
    /// </summary>
    private double HandleMultiplayerCharacterSelect()
    {
        var root = GetNodeOrNull<Node>("/root/Game/RootSceneContainer");
        if (root == null) return 0.5;

        // Find the character select screen
        var charSelect = root.GetNodeOrNull<Control>("Run/RoomContainer/CharacterSelectRoom")
            ?? (Control)root.GetNodeOrNull<Node>("CharacterSelectScreen")
            ?? (Control)root.GetNodeOrNull<Node>("CharacterSelect");

        if (charSelect == null) return 0.5;

        // Pick target character
        var targetChar = _character;
        if (targetChar == "RANDOM")
            targetChar = ValidCharacters[_rng.Next(ValidCharacters.Length)];

        // ── Step 1: Select the character ──────────────────────────────────
        if (!_mpCharacterSelected)
        {
            var buttonContainer = charSelect.GetNodeOrNull<Node>("CharSelectButtons/ButtonContainer");
            if (buttonContainer != null)
            {
                foreach (var btn in AutoSlayHelpers.FindAll<NCharacterSelectButton>(buttonContainer))
                {
                    if (!btn.IsLocked && btn.Character?.Id.Entry == targetChar)
                    {
                        btn.Select();
                        _mpCharacterSelected = true;
                        _mpCharacterSelectedTime = Time.GetUnixTimeFromSystem();
                        MainFile.Logger.Info($"[AutoSlay] MP: Selected character: {targetChar} (isHost={_isMultiplayerHost})");
                        break;
                    }
                }
            }
            // If we couldn't find the button, keep trying
            return 0.3;
        }

        // ── Step 2: Confirm (with host delay) ─────────────────────────────
        var confirmBtn = charSelect.GetNodeOrNull<NConfirmButton>("ConfirmButton");
        // Fallback: search by name if direct path fails
        if (confirmBtn == null)
        {
            foreach (var btn in AutoSlayHelpers.FindAll<NButton>(charSelect))
            {
                var btnName = btn.Name?.ToString() ?? "";
                if (btnName.Contains("Confirm", StringComparison.OrdinalIgnoreCase)
                    || btnName.Contains("Ready", StringComparison.OrdinalIgnoreCase)
                    || btn is NConfirmButton)
                {
                    if (btn.Visible && btn.IsEnabled)
                    {
                        confirmBtn = btn as NConfirmButton;
                        if (confirmBtn == null)
                        {
                            // Not an NConfirmButton, but click it directly
                            if (!_isMultiplayerHost)
                            {
                                LogOnce($"Multiplayer character select: confirming via fallback button [{btnName}] (bot)");
                                btn.ForceClick();
                                return 2.0;
                            }
                            // For host path below, we can't use this fallback easily
                            // because the delay logic expects confirmBtn
                        }
                        break;
                    }
                }
            }
        }
        if (confirmBtn == null || !confirmBtn.IsEnabled)
        {
            // Confirm button not ready yet — wait
            return 0.3;
        }

        if (!_isMultiplayerHost)
        {
            // Bot: confirm immediately — no need to wait.
            // Do NOT reset _mpCharacterSelected here — the character select
            // screen may still be visible during the transition animation,
            // and resetting would cause re-entry into step 1 (re-select).
            // The flag is reset in SignalRunComplete() when the run ends.
            LogOnce("Multiplayer character select: confirming (bot)");
            confirmBtn.ForceClick();
            return 2.0;
        }

        // ── Host: wait before auto-confirming ─────────────────────────────
        // Give the human time to see the selection and change character if desired.
        double elapsed = Time.GetUnixTimeFromSystem() - _mpCharacterSelectedTime;
        double remaining = MP_CHAR_SELECT_HOST_DELAY - elapsed;

        if (_debugFrame % 90 == 0 && remaining > 0)
        {
            MainFile.Logger.Info($"[AutoSlay] Host character auto-confirm in {remaining:F0}s... " +
                $"(press F1/F2/F3 to toggle auto-navigate/auto-battle/auto-event if you want manual control)");
        }

        if (elapsed >= MP_CHAR_SELECT_HOST_DELAY)
        {
            LogOnce("Multiplayer character select: auto-confirming (host)");
            confirmBtn.ForceClick();
            // Do NOT reset _mpCharacterSelected — same reason as bot path above.
            return 2.0;
        }

        return 0.5;
    }

    /// <summary>
    /// Write detailed diagnostics before killing the game due to stuck detection.
    /// This lets the batch runner and optimizer understand WHY the run was interrupted.
    /// </summary>
    private void WriteStuckDiagnostics(string stuckType, double stuckDuration)
    {
        try
        {
            var asmDir = System.IO.Path.GetDirectoryName(
                System.Reflection.Assembly.GetExecutingAssembly().Location);
            if (asmDir == null) return;
            var path = System.IO.Path.Combine(asmDir, "stuck_diagnostics.json");

            var diag = new Dictionary<string, object>
            {
                ["stuck_type"] = stuckType,
                ["stuck_duration_seconds"] = Math.Round(stuckDuration, 1),
                ["timestamp"] = DateTime.Now.ToString("o"),
                ["character"] = _character,
                ["seed"] = _seed,
                ["batch_mode"] = _batchMode,
            };

            // Current game state
            try
            {
                var cm = CombatManager.Instance;
                diag["in_combat"] = cm?.IsInProgress == true;
                diag["player_actions_disabled"] = cm?.PlayerActionsDisabled == true;

                if (cm?.IsInProgress == true)
                {
                    var rs = RunManager.Instance?.DebugOnlyGetState();
                    var pl = rs != null ? LocalContext.GetMe(rs) : null;
                    diag["player_hp"] = pl?.Creature?.CurrentHp ?? 0;
                    diag["player_max_hp"] = pl?.Creature?.MaxHp ?? 0;
                    diag["player_block"] = pl?.Creature?.Block ?? 0;
                    diag["player_energy"] = pl?.PlayerCombatState?.Energy ?? 0;
                    diag["floor"] = rs?.TotalFloor ?? 0;

                    // Hand cards
                    var handCards = new List<string>();
                    try
                    {
                        var hand = PileType.Hand.GetPile(pl!)?.Cards?.ToList();
                        if (hand != null)
                            foreach (var c in hand)
                                handCards.Add($"{c.Id.Entry}(cost={c.EnergyCost.Canonical})");
                    }
                    catch { }
                    diag["hand_cards"] = handCards;
                    diag["hand_count"] = handCards.Count;

                    // Draw pile
                    try
                    {
                        diag["draw_pile_count"] = PileType.Draw.GetPile(pl!)?.Cards?.Count ?? 0;
                        diag["discard_pile_count"] = PileType.Discard.GetPile(pl!)?.Cards?.Count ?? 0;
                    }
                    catch { }

                    // Enemies
                    var enemiesInfo = new List<Dictionary<string, object>>();
                    try
                    {
                        var cs = cm.DebugOnlyGetState();
                        if (cs?.Enemies != null)
                            foreach (var e in cs.Enemies)
                                enemiesInfo.Add(new Dictionary<string, object>
                                {
                                    ["id"] = e.Monster?.Id?.Entry ?? "?",
                                    ["hp"] = e.CurrentHp,
                                    ["max_hp"] = e.MaxHp,
                                    ["alive"] = e.IsAlive,
                                    ["block"] = e.Block,
                                    ["intent_damage"] = IroncladSolver.EstimateIntentDamageStatic(e),
                                });
                    }
                    catch { }
                    diag["enemies"] = enemiesInfo;
                    diag["alive_enemy_count"] = enemiesInfo.Count(e => (bool)(e["alive"]));

                    // Combat plan state
                    diag["has_combat_plan"] = _combatPlan != null;
                    diag["combat_plan_step"] = _combatPlanStep;
                    diag["combat_turn_requested"] = _combatTurnRequested;
                    diag["combat_card_delay"] = Math.Round(_combatCardDelay, 2);
                    diag["combat_turn_number"] = _combatTurnNumber;
                    diag["empty_solve_retries"] = _emptySolveRetries;
                    diag["draw_stable_count"] = _drawStableCount;
                }
            }
            catch (Exception stateEx)
            {
                diag["state_error"] = stateEx.Message;
            }

            // Overlay/screen info
            try
            {
                var screen = GameStateDetector.Detect();
                diag["detected_screen"] = screen.ToString();
                var overlayStack3 = NOverlayStack.Instance;
                if (overlayStack3?.ScreenCount > 0)
                {
                    var top = overlayStack3.Peek();
                    diag["top_overlay"] = top?.GetType().Name ?? "?";
                    diag["overlay_count"] = overlayStack3.ScreenCount;
                }
            }
            catch { }

            // Last screen type (for non-combat stuck tracking)
            diag["last_screen_type"] = _lastScreenType;

            var json = System.Text.Json.JsonSerializer.Serialize(diag,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            System.IO.File.WriteAllText(path, json);

            // Also write to mod directory so batch_runner can find it
            try
            {
                var modDir = System.IO.Path.GetDirectoryName(
                    System.Reflection.Assembly.GetExecutingAssembly().Location) ?? ".";
                var modPath = System.IO.Path.Combine(modDir, "stuck_diagnostics.json");
                if (modPath != path)
                    System.IO.File.WriteAllText(modPath, json);
            }
            catch { /* best-effort */ }

            MainFile.Logger.Info($"[AutoSlay] Stuck diagnostics written to {path}: {stuckType} for {stuckDuration:F0}s");
        }
        catch (Exception ex)
        {
            MainFile.Logger.Error($"[AutoSlay] Failed to write stuck diagnostics: {ex.Message}");
        }
    }

    /// <summary>Write a signal file so the batch runner knows the run is complete.</summary>
    private void SignalRunComplete()
    {
        if (_runCompleteSignaled) return;
        _runCompleteSignaled = true;

        // ── Reset multiplayer state for next run ────────────────────
        _multiplayerJoined = false;
        _multiplayerReady = false;
        _mpCharacterSelected = false;
        try
        {
            var asmDir = System.IO.Path.GetDirectoryName(
                System.Reflection.Assembly.GetExecutingAssembly().Location);
            if (asmDir == null) return;

            // Write to assembly directory (where the DLL is loaded from)
            var path = System.IO.Path.Combine(asmDir, "run_complete.txt");
            System.IO.File.WriteAllText(path, DateTime.Now.ToString("o"));

            // Also write to known mod directory (batch_runner looks here)
            // This ensures the signal is found regardless of which path the game loads from
            try
            {
                var modDir = System.IO.Path.GetDirectoryName(
                    System.Reflection.Assembly.GetExecutingAssembly().Location) ?? ".";
                var modPath = System.IO.Path.Combine(modDir, "run_complete.txt");
                if (modPath != path)
                    System.IO.File.WriteAllText(modPath, DateTime.Now.ToString("o"));
            }
            catch { /* best-effort */ }

            // ── Multi-seed rotation: write next seed to batch_config.json ──
            if (_seeds.Count > 1 && _currentSeedIndex + 1 < _seeds.Count)
            {
                try
                {
                    var modDir = System.IO.Path.GetDirectoryName(
                        System.Reflection.Assembly.GetExecutingAssembly().Location) ?? ".";
                    var cfgPath = System.IO.Path.Combine(modDir, "batch_config.json");
                    int nextIndex = _currentSeedIndex + 1;
                    string nextSeed = _seeds[nextIndex];
                    string nextJson = $"{{\n  \"Seed\": \"{nextSeed}\",\n  \"Character\": \"{_character}\",\n  \"HpMultiplier\": {_hpMultiplier.ToString(System.Globalization.CultureInfo.InvariantCulture)},\n  \"RunNumber\": \"{nextIndex + 1}\"\n}}";
                    System.IO.File.WriteAllText(cfgPath, nextJson);
                    MainFile.Logger.Info($"[AutoSlay] Seed rotation: {_currentSeedIndex + 1}/{_seeds.Count} → next seed: {nextSeed}");
                }
                catch (Exception seedEx)
                {
                    MainFile.Logger.Info($"[AutoSlay] Seed rotation write failed: {seedEx.Message}");
                }
            }

            MainFile.Logger.Info($"[AutoSlay] Run complete signal written to {path}. Waiting for batch runner to kill game.");
        }
        catch (Exception ex)
        {
            MainFile.Logger.Error($"[AutoSlay] Failed to write run_complete.txt: {ex.Message}");
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // LLM overlay request — returns true if LLM call was started
    // ─────────────────────────────────────────────────────────────────────────

    private bool TryRequestLlmForOverlay(Node overlayNode)
    {
        MainFile.Logger.Info($"[AutoSlay/DBG] TryRequestLlm overlay={overlayNode.GetType().Name} takeQ={(_rewardTakeQueue == null ? "null" : $"[{string.Join(", ", _rewardTakeQueue.Select(b => b.Reward?.GetType().Name ?? "?"))}]")} llmDone={_rewardsLlmDone} cardChoice={_rewardCardChoice}");

        if (overlayNode is NCardRewardSelectionScreen)
        {
            if (_rewardCardChoice != 0)
            {
                ApplyCardRewardChoice(_rewardCardChoice);
                _rewardCardChoice = 0;
                return true;
            }
            // Always ask LLM for card reward selections (supports multiple card rewards)
            var prompt = GameStateSerializer.SerializeCardReward((NCardRewardSelectionScreen)overlayNode);
            _pendingLlm = _llm!.SendAsync(prompt, "overlay:CardReward");
            _pendingContext = "overlay:CardReward";
            return true;
        }
        if (overlayNode is NRewardsScreen rewardsScreen)
        {
            // Process queued takes first
            if (_rewardTakeQueue != null && _rewardTakeQueue.Count > 0)
            {
                var btn = _rewardTakeQueue.Dequeue();
                if (GodotObject.IsInstanceValid(btn) && btn.IsEnabled)
                {
                    MainFile.Logger.Info($"[AutoSlay/LLM] Taking reward: {btn.Reward?.GetType().Name}");
                    btn.ForceClick();
                }
                _cooldown = 0.8;
                return true;
            }
            // Queue exhausted — proceed
            if (_rewardTakeQueue != null)
            {
                _rewardTakeQueue = null;
                _rewardsLlmDone = true;
                // Don't proceed yet — card reward overlay may still need processing
                _cooldown = 0.5;
                return true;
            }
            // No queue — check if there are actual rewards to choose from
            var availableBtns = AutoSlayHelpers.FindAll<NRewardButton>(rewardsScreen)
                .Where(b => b.IsEnabled).ToList();
            if (availableBtns.Count == 0)
            {
                // No rewards left — just proceed
                var proceed = AutoSlayHelpers.FindFirst<NProceedButton>(rewardsScreen);
                if (proceed?.IsEnabled == true)
                    proceed.ForceClick();
                else
                    NOverlayStack.Instance?.Remove(rewardsScreen);
                _cooldown = 1.0;
                return true;
            }
            // Ask LLM
            var prompt = GameStateSerializer.SerializeRewards(rewardsScreen);
            _pendingLlm = _llm!.SendAsync(prompt, "overlay:Rewards");
            _pendingContext = "overlay:Rewards";
            return true;
        }
        if (overlayNode is NDeckUpgradeSelectScreen or NDeckTransformSelectScreen
            or NDeckEnchantSelectScreen or NDeckCardSelectScreen)
        {
            // If LLM already chose, let DispatchOverlay -> CardGridHandler.Handle() apply it
            if (CardGridHandler.HasPendingLlmChoice)
                return false;

            // Only ask LLM in Phase 1 (no preview, no confirm enabled yet)
            var mainConfirm = overlayNode.GetNodeOrNull<NConfirmButton>("Confirm")
                ?? overlayNode.GetNodeOrNull<NConfirmButton>("%Confirm");
            if (mainConfirm?.IsEnabled != true)
            {
                var screenType = overlayNode switch
                {
                    NDeckUpgradeSelectScreen => "UPGRADE A CARD",
                    NDeckTransformSelectScreen => "TRANSFORM A CARD",
                    NDeckEnchantSelectScreen => "ENCHANT A CARD",
                    NDeckCardSelectScreen => "REMOVE A CARD",
                    _ => "CHOOSE A CARD"
                };
                var prompt = GameStateSerializer.SerializeCardGrid(overlayNode, screenType);
                _pendingLlm = _llm!.SendAsync(prompt, "overlay:CardGrid");
                _pendingContext = "overlay:CardGrid";
                return true;
            }
        }
        if (overlayNode is NSimpleCardSelectScreen simpleScreen)
        {
            // If LLM already chose, let DispatchOverlay -> SimpleCardSelectHandler.Handle() apply it
            if (SimpleCardSelectHandler.HasPendingLlmChoice)
                return false;

            // If confirm is already enabled, let DispatchOverlay handle it (card already selected or skip)
            var simpleConfirm = simpleScreen.GetNodeOrNull<NConfirmButton>("%Confirm");
            if (simpleConfirm?.IsEnabled == true)
                return false;

            // Ask LLM to choose a card
            var cards = AutoSlayHelpers.FindAll<NGridCardHolder>(overlayNode);
            if (cards.Count > 0)
            {
                var prompt = GameStateSerializer.SerializeCardGrid(overlayNode, "CHOOSE A CARD");
                _pendingLlm = _llm!.SendAsync(prompt, "overlay:SimpleCardSelect");
                _pendingContext = "overlay:SimpleCardSelect";
                return true;
            }
        }
        // Other overlays: use random handler (no strategic value)
        return false;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Execute LLM response based on context
    // ─────────────────────────────────────────────────────────────────────────

    private void ExecuteLlmResult(string context, string response)
    {
        switch (context)
        {
            case "combat":
                ParseAndStartCombatPlan(response);
                break;

            case "map":
                ExecuteMapChoice(response);
                break;

            case "event":
                ExecuteEventChoice(response);
                break;

            case "restsite":
                ExecuteRestSiteChoice(response);
                break;

            case "overlay:CardReward":
                ExecuteCardRewardChoice(response);
                break;

            case "overlay:Rewards":
                ExecuteRewardsChoice(response);
                break;

            case "overlay:CardGrid":
                ExecuteCardGridChoice(response);
                break;

            case "overlay:SimpleCardSelect":
                ExecuteSimpleCardSelectChoice(response);
                break;

            case "shop":
                ParseAndStartShopPlan(response);
                break;

            case "gameover_reflection":
                MainFile.Logger.Info($"[AutoSlay/LLM] Reflection: {response.Replace("\n", " | ")}");
                _llm?.SaveMemory(response);
                _llm?.ResetForNewRun();
                GameStateSerializer.ResetMapTracking();
                _gameOverReflected = true;
                _cooldown = 1.0;
                break;

            default:
                MainFile.Logger.Info($"[AutoSlay/LLM] Unknown context: {context}");
                _cooldown = 1.0;
                break;
        }
    }

    /// <summary>
    /// Use potions only in elite/boss fights, or when potion slots are full.
    /// Uses at most one potion per turn.
    /// </summary>
    private void TryUsePotions(dynamic player, List<Creature> enemies, int hp, int maxHp, int turn)
    {
        try
        {
            // Collect usable potions without LINQ lambdas (not supported with dynamic)
            var usablePotions = new List<dynamic>();
            var allPotions = new List<string>();
            int usedSlots = 0;
            foreach (var p in player.Potions)
            {
                try
                {
                    string pid = p.Id?.Entry ?? "?";
                    bool removed = (bool)p.HasBeenRemovedFromState;
                    bool queued = (bool)p.IsQueued;
                    allPotions.Add($"{pid}(removed={removed},queued={queued})");
                    if (!queued && !removed)
                        usablePotions.Add(p);
                }
                catch (Exception ex) { allPotions.Add($"err:{ex.Message}"); }
                try
                {
                    if (!((bool)p.HasBeenRemovedFromState))
                        usedSlots++;
                }
                catch { }
            }

            if (usablePotions.Count == 0)
            {
                // Log once per combat to understand why we never use potions
                if (_combatTurnNumber <= 2)
                    MainFile.Logger.Info($"[Potion] Turn {turn}: no usable potions. All: [{string.Join(", ", allPotions)}] usedSlots={usedSlots}");
                return;
            }

            int maxSlots = (int)player.MaxPotionSlots;
            bool slotsFull = usedSlots >= maxSlots;
            float hpRatio = maxHp > 0 ? (float)hp / maxHp : 1f;

            // Better elite/boss detection using monster IDs and known elite names
            bool isEliteOrBoss = false;
            foreach (var e in enemies)
            {
                try
                {
                    var id = (e.Monster?.Id?.Entry ?? "").ToUpperInvariant();
                    if (id.Contains("ELITE") || id.Contains("BOSS") || id.Contains("GREMLIN") ||
                        id.Contains("PHROG") || id.Contains("SENTRY") || id.Contains("LAGAVULIN") ||
                        id.Contains("SLAVER") || id.Contains("TASKMASTER") || id.Contains("COLLECTOR") ||
                        id.Contains("AUTOMATON") || id.Contains("CHAMP") || id.Contains("BRONZE") ||
                        id.Contains("NEMESIS") || id.Contains("REPTO") || id.Contains("GIANT") ||
                        id.Contains("GUARDIAN") || id.Contains("HEXAGHOST") || id.Contains("SLIME_BOSS"))
                    {
                        isEliteOrBoss = true;
                        break;
                    }
                }
                catch { }
            }

            // ── Decide whether to use potions ───────────────────────────
            // Only use in elite/boss fights, or when slots are full (to make room).
            // Normal fights: save potions for elites/bosses.
            bool emergencyHp = hpRatio < 0.25f;
            bool lowHp = hpRatio < 0.50f;
            bool shouldUse = isEliteOrBoss || slotsFull;
            if (!shouldUse)
            {
                // Log when we HAVE potions but choose not to use them
                if (_combatTurnNumber <= 2)
                    MainFile.Logger.Info($"[Potion] Turn {turn}: have {usablePotions.Count} potions but not using. " +
                        $"hpRatio={hpRatio:F2} isEliteOrBoss={isEliteOrBoss} slotsFull={slotsFull} " +
                        $"(policy: elite/boss only) " +
                        $"ids=[{string.Join(",", usablePotions.Select(p => { try { return p.Id?.Entry ?? "?"; } catch { return "?"; } }))}]");
                return;
            }

            // Pick the best potion to use
            dynamic? bestPotion = null;
            string reason = "";

            foreach (var p in usablePotions)
            {
                string id = "";
                try { id = p.Id?.Entry ?? ""; } catch { }

                // ── Priority 1: Full slots — use weakest potion first ──
                if (slotsFull)
                {
                    if (id.Contains("Block") || id.Contains("Explosive") || id.Contains("Fear") ||
                        id.Contains("Flex") || id.Contains("Speed") || id.Contains("Weak"))
                    {
                        bestPotion = p;
                        reason = $"slots full ({usedSlots}/{maxSlots}), using weak potion {id}";
                        break;
                    }
                }

                // ── Priority 2: Emergency HP (<25%) — use ANY healing/block potion ──
                if (emergencyHp && (id.Contains("Blood") || id.Contains("Regen") || id.Contains("Block") ||
                    id.Contains("Heart") || id.Contains("Fruit") || id.Contains("Essence") ||
                    id.Contains("Elixir") || id.Contains("Potion")))
                {
                    bestPotion = p;
                    reason = $"EMERGENCY HP {hp}/{maxHp} ({hpRatio*100:F0}%), using {id}";
                    break;
                }

                // ── Priority 3: Low HP (<50%) in any fight — use defensive potions ──
                if (lowHp && (id.Contains("Blood") || id.Contains("Regen") || id.Contains("Block") ||
                    id.Contains("Heart") || id.Contains("Fruit") || id.Contains("Essence") ||
                    id.Contains("Dexterity") || id.Contains("Artifact")))
                {
                    if (bestPotion == null)
                    {
                        bestPotion = p;
                        reason = $"low HP {hp}/{maxHp} ({hpRatio*100:F0}%), using {id}";
                    }
                }

                // ── Priority 4: Elite/Boss — use offensive potions ──
                if (isEliteOrBoss && (id.Contains("Fire") || id.Contains("Explosive") || id.Contains("Poison") ||
                    id.Contains("Steroid") || id.Contains("Power") || id.Contains("Cultist") ||
                    id.Contains("Fear") || id.Contains("Weak") || id.Contains("Vulnerable") ||
                    id.Contains("Strength") || id.Contains("Flex") || id.Contains("Speed")))
                {
                    if (bestPotion == null)
                    {
                        bestPotion = p;
                        reason = $"elite/boss fight, using {id}";
                    }
                }

                // ── Priority 5: Elite/Boss with any potion at all ──
                if (isEliteOrBoss && bestPotion == null)
                {
                    bestPotion = p;
                    reason = $"elite/boss fight, using any potion: {id}";
                }
            }

            if (bestPotion != null)
            {
                MainFile.Logger.Info($"[AutoSlay] Using potion: {reason}");
                var target = PotionHelper.GetTarget(bestPotion, player.Creature?.CombatState as CombatState);
                bestPotion.EnqueueManualUse(target);
                // Don't use more than one potion per turn
            }
        }
        catch (Exception ex)
        {
            MainFile.Logger.Info($"[AutoSlay] TryUsePotions failed: {ex.Message}");
        }
    }

    private void ParseAndStartCombatPlan(string response)
    {
        var plan = new List<CombatAction>();
        var runState = RunManager.Instance?.DebugOnlyGetState();
        var player = runState != null ? LocalContext.GetMe(runState) : null;
        if (player == null) { _cooldown = 0.5; return; }

        var hand = PileType.Hand.GetPile(player).Cards.ToList();
        var potions = player.Potions.Where(p => !p.IsQueued).ToList();
        var combatState = CombatManager.Instance?.DebugOnlyGetState();
        var enemies = combatState?.Enemies.Where(e => e.IsAlive).ToList() ?? new List<Creature>();
        bool hasEndTurn = false;

        foreach (var line in response.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("END_TURN", StringComparison.OrdinalIgnoreCase))
            {
                hasEndTurn = true;
                break;
            }

            if (trimmed.StartsWith("PLAY ", StringComparison.OrdinalIgnoreCase))
            {
                var parts = trimmed.Substring(5).Split("->", StringSplitOptions.TrimEntries);
                if (!int.TryParse(parts[0].Trim(), out int cardIdx)) continue;
                if (cardIdx < 1 || cardIdx > hand.Count) continue;

                var target = ParseEnemyTarget(parts, enemies);
                plan.Add(new CombatAction(hand[cardIdx - 1], null, target));
            }
            else if (trimmed.StartsWith("POTION ", StringComparison.OrdinalIgnoreCase))
            {
                var parts = trimmed.Substring(7).Split("->", StringSplitOptions.TrimEntries);
                var potionStr = parts[0].Trim().TrimStart('P', 'p');
                if (!int.TryParse(potionStr, out int potionIdx)) continue;
                if (potionIdx < 1 || potionIdx > potions.Count) continue;

                var target = ParseEnemyTarget(parts, enemies);
                plan.Add(new CombatAction(null, potions[potionIdx - 1], target));
            }
        }

        if (plan.Count == 0 && hasEndTurn)
        {
            MainFile.Logger.Info("[AutoSlay/LLM] LLM chose to end turn immediately");
            if (CombatManager.Instance is { PlayerActionsDisabled: false })
                EndTurnViaUiOrApi(player);
            _combatCardDelay = 0.5;
            return;
        }

        if (plan.Count == 0)
        {
            MainFile.Logger.Info("[AutoSlay/LLM] No valid actions parsed from combat response, ending turn");
            if (CombatManager.Instance is { PlayerActionsDisabled: false })
                EndTurnViaUiOrApi(player);
            _combatCardDelay = 0.5;
            return;
        }

        MainFile.Logger.Info($"[AutoSlay/LLM] Combat plan: {plan.Count} actions, endTurn={hasEndTurn}");
        _combatPlan = plan;
        _combatPlanEndTurn = hasEndTurn;
        _combatPlanStep = 0;
    }

    // FindHumanPlayerCreature removed — LAN multiplayer deleted

    private static Creature? ParseEnemyTarget(string[] parts, List<Creature> enemies)
    {
        if (parts.Length <= 1) return null;
        var targetStr = parts[1].Trim().ToUpperInvariant();
        if (targetStr.Length == 1 && targetStr[0] >= 'A')
        {
            int enemyIdx = targetStr[0] - 'A';
            if (enemyIdx >= 0 && enemyIdx < enemies.Count)
                return enemies[enemyIdx];
        }
        return null;
    }

    private void ExecuteNextCombatStep()
    {
        if (_combatPlan == null) return;

        // NOTE: _lastCombatActivity is NOT reset here — it only resets when
        // we make actual progress (card played, potion used, turn ended).
        // This way the 30s stuck timer can catch infinite step-execution loops.

        var runState = RunManager.Instance?.DebugOnlyGetState();
        var player = runState != null ? LocalContext.GetMe(runState) : null;
        if (player == null) { _combatPlan = null; return; }

        if (_combatPlanStep >= _combatPlan.Count)
        {
            int planStepCount = _combatPlanStep; // save before nulling
            _combatPlan = null;
            if (_combatPlanEndTurn)
            {
                // ── Double-confirm before ending turn ────────────────────
                // After playing cards, relics or card effects may have drawn
                // new playable cards. Scan the hand one more time before
                // committing to end the turn.
                //
                // IMPORTANT: Only recheck if we actually played cards (planStepCount > 0).
                // If the solver deliberately played 0 cards (enemy buffing, only Defend in hand,
                // no incoming damage), no new cards could have been drawn. Rechecking would
                // find the same "playable" cards the solver already rejected, creating an
                // infinite loop: solve→empty→recheck→re-solve→empty→...
                bool hasPlayableAfterPlan = false;
                if (planStepCount > 0)
                {
                    try
                    {
                        var currentHand = PileType.Hand.GetPile(player).Cards.ToList();
                        int currentEnergy = player?.PlayerCombatState?.Energy ?? 0;
                        foreach (var card in currentHand)
                        {
                            try
                            {
                                if (card == null) continue;
                                int cost = card.EnergyCost?.CostsX == true
                                    ? Math.Max(1, currentEnergy)
                                    : (card.EnergyCost?.Canonical ?? 99);
                                if (cost <= currentEnergy && cost >= 0)
                                {
                                    hasPlayableAfterPlan = true;
                                    break;
                                }
                            }
                            catch { }
                        }
                    }
                    catch (Exception recheckEx)
                    {
                        MainFile.Logger.Info($"[AutoSlay] End-turn recheck CRASH: {recheckEx.Message}");
                    }
                }

                if (hasPlayableAfterPlan)
                {
                    _consecutiveRechecks++;
                    // ── Deadlock protection ──────────────────────────────────
                    // If we've re-solved 3+ times in a row and the recheck
                    // still finds "playable" cards, the solver is deliberately
                    // choosing not to play them (or they keep failing to play).
                    // Force the end turn to break the potential infinite loop.
                    if (_consecutiveRechecks >= 10)
                    {
                        MainFile.Logger.Info($"[AutoSlay] END_TURN forced after {_consecutiveRechecks} consecutive rechecks — breaking potential loop");
                        _consecutiveRechecks = 0;
                        if (CombatManager.Instance is { PlayerActionsDisabled: false })
                            EndTurnViaUiOrApi(player);
                        _combatCardDelay = 0.5;
                        return;
                    }
                    // Cards appeared after plan execution — re-solve instead of ending turn
                    MainFile.Logger.Info($"[AutoSlay] END_TURN cancelled (#{_consecutiveRechecks}): new playable cards detected after plan, re-solving");
                    _combatTurnRequested = false; _combatTurnRequestedDuration = 0;
                    _combatCardDelay = 0.3;
                    return;
                }

                // Rechecks resolved — reset counter
                _consecutiveRechecks = 0;

                // ── Multiplayer post-plan re-check ───────────────────────
                // In multiplayer, don't end turn immediately after the first
                // plan completes. Relic effects or card-draw during plan
                // execution may have created new play opportunities.
                // Re-solve once more before committing to end turn.
                if (_multiplayerMode && !_isMultiplayerHost && _mpPostPlanEmptyCount < 1)
                {
                    _mpPostPlanEmptyCount++;
                    MainFile.Logger.Info($"[AutoSlay] MP post-plan: re-solving once more before ending turn");
                    _combatTurnRequested = false; _combatTurnRequestedDuration = 0;
                    _combatCardDelay = 0.5;
                    return;
                }

                // Solver/LLM explicitly said END_TURN — end the turn
                string reason = planStepCount > 0
                    ? "no new playable cards after plan"
                    : "solver deliberately played 0 cards (e.g. only Defend vs buffing enemy)";
                MainFile.Logger.Info($"[AutoSlay] Combat plan complete, ending turn ({reason})");
                _lastCombatActivity = 0; // actual progress
                if (CombatManager.Instance is { PlayerActionsDisabled: false })
                    EndTurnViaUiOrApi(player);
                _combatCardDelay = 0.5;
                // ── Activate 5-second MP retry cycle ─────────────────────
                // After ending turn in multiplayer, the bot waits for other
                // players. While waiting, the host may give us energy/cards.
                // Every 5s, cancel end-turn and re-solve.
                if (_multiplayerMode && !_isMultiplayerHost)
                {
                    _mpEndTurnRetryActive = true;
                    _mpPostEndTurnTimer = 0;
                }
            }
            else
            {
                // No END_TURN — re-evaluate hand (draw effects, etc.)
                _combatTurnRequested = false; _combatTurnRequestedDuration = 0;
                var overlayOpen = NOverlayStack.Instance?.ScreenCount > 0;
                _combatCardDelay = overlayOpen ? 2.0 : 0.5;
                MainFile.Logger.Info($"[AutoSlay] Combat plan complete, re-evaluating (overlay={overlayOpen})");
            }
            return;
        }

        var action = _combatPlan[_combatPlanStep];
        _combatPlanStep++;

        if (action.Potion != null)
        {
            // Use potion
            if (action.Potion.HasBeenRemovedFromState || action.Potion.IsQueued)
            {
                MainFile.Logger.Info($"[AutoSlay] Skipping potion {action.Potion.Id.Entry} (already used)");
                return;
            }
            var potionTarget = action.Target
                ?? PotionHelper.GetTarget(action.Potion, player?.Creature?.CombatState as CombatState);
            MainFile.Logger.Info($"[AutoSlay] Using potion {action.Potion.Id.Entry}{(action.Target != null ? $" -> {action.Target.Monster?.Id.Entry}" : "")}");
            _lastCombatActivity = 0; // actual progress
            action.Potion.EnqueueManualUse(potionTarget);
            // Re-solve after potion use to get optimal follow-up
            _combatPlan = null;
            _combatTurnRequested = false; _combatTurnRequestedDuration = 0;
            _combatCardDelay = 1.2;         // wait for potion effects to resolve
            return;
        }

        if (action.Card != null)
        {
            string cardId = action.Card.Id.Entry;
            string? targetId = action.Target?.Monster?.Id.Entry;

            // ── Set card select context BEFORE playing the card ──────
            // Cards like Headbutt, True Grit, Armaments trigger a card
            // selection overlay mid-combat. We pre-set the context so
            // ChooseCardDecider/SimpleSelectDecider know WHY we're selecting.
            bool triggersCardSelect = false;
            var cardIdUpper = cardId.ToUpperInvariant();
            if (cardIdUpper is "HEADBUTT" or "WARCRY" or "TRUE_GRIT" or "BURNING_PACT"
                or "ARMAMENTS" or "EXHUME" or "HOLOGRAM" or "RECYCLE"
                or "SECRET_TECHNIQUE" or "SECRET_WEAPON")
            {
                DecisionEngine.SetPendingCardSelect(cardId);
                triggersCardSelect = true;
            }

            // Play card — use ID-based matching, not reference equality.
            // After reshuffles, the game creates new CardModel objects with the same IDs.
            var hand = PileType.Hand.GetPile(player).Cards.ToList();
            var matchingCard = hand.FirstOrDefault(c => c.Id.Entry == action.Card.Id.Entry);
            if (matchingCard == null)
            {
                MainFile.Logger.Info($"[AutoSlay] Skipping {cardId} (no longer in hand)");
                try { BattleLogger.LogAction(cardId, false, "not in hand", target: targetId); } catch { }
                return;
            }
            if (!matchingCard.CanPlay(out var cantPlayReason, out _))
            {
                MainFile.Logger.Info($"[AutoSlay] Skipping {cardId} (not playable, reason={cantPlayReason})");
                try { BattleLogger.LogAction(cardId, false, $"not playable: {cantPlayReason}", target: targetId); } catch { }
                return;
            }
            try
            {
                // Skip target for AOE/Random/Self/None cards — they don't need one.
                // Pass target for AnyEnemy and any other single-enemy-targeting type.
                bool skipTarget = matchingCard.TargetType == TargetType.AllEnemies
                    || matchingCard.TargetType == TargetType.RandomEnemy
                    || matchingCard.TargetType == TargetType.None
                    || matchingCard.TargetType == TargetType.Self;
                var cardTarget = skipTarget ? null : action.Target;

                // ── Multiplayer player-targeting cards ────────────────────
                // AnyAlly / AnyPlayer cards (DEMONIC_SHIELD, BELIEVE_IN_YOU, etc.)
                // need a player target. The solver always returns null for these
                // (it only tracks enemy targets), so we must resolve a valid
                // player creature here. Without this, TryManualPlay(null) triggers
                // a player-selection UI that the bot can't handle → deadlock.
                if (cardTarget == null && (matchingCard.TargetType == TargetType.AnyAlly
                                        || matchingCard.TargetType == TargetType.AnyPlayer))
                {
                    try
                    {
                        var combatState = player?.Creature?.CombatState as CombatState;
                        if (combatState != null && combatState.PlayerCreatures.Count > 0)
                        {
                            // Target the first alive player that is NOT us (prefer host/human).
                            // If no other player is alive, target self.
                            var myCreature = player.Creature;
                            cardTarget = combatState.PlayerCreatures
                                .FirstOrDefault(c => c.IsAlive && c != myCreature)
                                ?? combatState.PlayerCreatures.FirstOrDefault(c => c.IsAlive);
                            MainFile.Logger.Info($"[AutoSlay] Resolved player target for {cardId} ({matchingCard.TargetType}): player={cardTarget?.Monster?.Id?.Entry ?? cardTarget?.GetType().Name ?? "?"}");
                        }
                    }
                    catch (Exception targetEx)
                    {
                        MainFile.Logger.Info($"[AutoSlay] Failed to resolve player target for {cardId}: {targetEx.Message}");
                    }
                }

                string targetDesc = cardTarget != null
                    ? (cardTarget.Monster?.Id?.Entry ?? cardTarget.GetType().Name)
                    : "";
                MainFile.Logger.Info($"[AutoSlay] Playing {cardId} (type={matchingCard.TargetType}, cost={matchingCard.EnergyCost.Canonical}){(cardTarget != null ? $" -> {targetDesc}" : "")}");
                if (!matchingCard.TryManualPlay(cardTarget))
                {
                    MainFile.Logger.Info($"[AutoSlay] TryManualPlay FAILED for {cardId}");
                    try { BattleLogger.LogAction(cardId, false, "TryManualPlay failed", target: targetId); } catch { }
                }
                else
                {
                    _combatPlanStuckFrames = 0; // reset — successful card play
                    _turnPlansWithoutPlay = 0;  // reset — successful card play
                    _consecutiveRechecks = 0;   // reset — successful card play broke any recheck cycle
                    try { BattleLogger.LogAction(cardId, true, target: targetId); } catch { }
                    // ── Record for post-combat dialogue generation ──
                    try
                    {
                        bool isAtk = matchingCard.Type == MegaCrit.Sts2.Core.Entities.Cards.CardType.Attack;
                        bool isSkl = matchingCard.Type == MegaCrit.Sts2.Core.Entities.Cards.CardType.Skill;
                        bool isPow = matchingCard.Type == MegaCrit.Sts2.Core.Entities.Cards.CardType.Power;
                        Chat.CombatRecorder.RecordCardPlayed(cardId, isAtk, isSkl, isPow);
                    }
                    catch { }
                    // ── Boss play logging: record card play with effects ──
                    try
                    {
                        var effects = CardEffectReader.ReadEffects(matchingCard, null);
                        BossPlayLogger.LogCardPlay(_combatTurnNumber, cardId, effects,
                            targetEnemyId: targetId,
                            energyCost: matchingCard.EnergyCost.CostsX ? -1 : matchingCard.EnergyCost.Canonical);
                    }
                    catch { }
                }
                if (triggersCardSelect)
                {
                    // Card triggers a selection overlay (Headbutt, True Grit, etc.)
                    // — discard plan so the overlay handler picks up card selection,
                    // then the main loop re-solves with the updated hand state.
                    _combatPlan = null;
                    _combatTurnRequested = false; _combatTurnRequestedDuration = 0;
                    _combatCardDelay = 1.5;
                }
                else
                {
                    // ── Post-card playability re-evaluation ─────────────────
                    // After playing a card, some cards in hand may become newly
                    // playable (e.g. Bully/Setup Strike cost→0 after an attack,
                    // Bloodletting enabling more plays, etc.). Check if any card
                    // that was NOT in the remaining plan is now playable —
                    // if so, discard plan and re-solve to capture the opportunity.
                    bool foundNewPlayable = false;
                    try
                    {
                        var handCards = PileType.Hand.GetPile(player).Cards.ToList();
                        int remainingEnergy = player?.PlayerCombatState?.Energy ?? 0;
                        var remainingPlanIds = new HashSet<string>(
                            _combatPlan.Skip(_combatPlanStep)
                                .Select(a => a.Card?.Id.Entry ?? "")
                                .Where(id => !string.IsNullOrEmpty(id)));

                        foreach (var card in handCards)
                        {
                            if (card == null) continue;
                            if (remainingPlanIds.Contains(card.Id.Entry)) continue;
                            int cost = card.EnergyCost?.CostsX == true
                                ? Math.Max(1, remainingEnergy)
                                : (card.EnergyCost?.Canonical ?? 99);
                            if (cost >= 0 && cost <= remainingEnergy && card.CanPlay(out _, out _))
                            {
                                MainFile.Logger.Info(
                                    $"[AutoSlay] New playable after card: {card.Id.Entry} cost={cost} energyLeft={remainingEnergy} → re-solving");
                                foundNewPlayable = true;
                                break;
                            }
                        }
                    }
                    catch (Exception recheckEx)
                    {
                        MainFile.Logger.Info($"[AutoSlay] Post-card recheck CRASH: {recheckEx.Message}");
                    }

                    if (foundNewPlayable)
                    {
                        _combatPlan = null;
                        _combatTurnRequested = false; _combatTurnRequestedDuration = 0;
                        _combatCardDelay = 0.3;
                    }
                    else
                    {
                        // Continue executing the FULL solver plan — do NOT re-solve
                        // after every card! Re-solving caused 4-5x turn inflation because
                        // the solver would find an optimal multi-card plan, we'd play ONE
                        // card, discarding the rest, and the re-solve on the weakened hand
                        // would return END_TURN — wasting 2-3 energy per turn.
                        // Instead, execute all plan cards sequentially; cards that become
                        // unplayable (out of hand / energy) are simply skipped.
                        _combatCardDelay = 0.25;
                    }
                }
            }
            catch (Exception playEx)
            {
                MainFile.Logger.Info($"[AutoSlay] Exception playing {cardId}: {playEx.GetType().Name}: {playEx.Message}");
                try { BattleLogger.LogAction(cardId, false, $"exception: {playEx.Message}", target: targetId); } catch { }
                _combatCardDelay = 0.2;
            }
        }
    }

    private void ExecuteMapChoice(string response)
    {
        int choice = ParseChoice(response);
        var mapScreen = NMapScreen.Instance;
        if (mapScreen?.IsOpen != true) { _cooldown = 1.0; return; }

        var points = AutoSlayHelpers.FindAll<NMapPoint>(mapScreen)
            .Where(p => p.IsEnabled).ToList();

        if (choice >= 1 && choice <= points.Count)
        {
            var point = points[choice - 1];
            MainFile.Logger.Info($"[AutoSlay/LLM] Map choice: {choice} -> ({point.Point.coord.row},{point.Point.coord.col})");
            mapScreen.OnMapPointSelectedLocally(point);
        }
        else if (points.Count > 0)
        {
            // Fallback to random
            var point = points[_rng.Next(points.Count)];
            mapScreen.OnMapPointSelectedLocally(point);
        }
        _cooldown = 2.0;
    }

    private void ExecuteEventChoice(string response)
    {
        int choice = ParseChoice(response);
        var eventRoom = GetNodeOrNull<Node>(
            "/root/Game/RootSceneContainer/Run/RoomContainer/EventRoom");
        if (eventRoom == null) { _cooldown = 1.0; return; }

        var options = AutoSlayHelpers.FindAll<NEventOptionButton>(eventRoom)
            .Where(o => !o.Option.IsLocked).ToList();

        if (choice >= 1 && choice <= options.Count)
        {
            MainFile.Logger.Info($"[AutoSlay/LLM] Event choice: {choice}");
            options[choice - 1].ForceClick();
        }
        else if (options.Count > 0)
        {
            options[_rng.Next(options.Count)].ForceClick();
        }
        _cooldown = 1.0;
    }

    private void ExecuteRestSiteChoice(string response)
    {
        int choice = ParseChoice(response);
        var restRoom = GetNodeOrNull<NRestSiteRoom>(
            "/root/Game/RootSceneContainer/Run/RoomContainer/RestSiteRoom");
        if (restRoom == null) { _cooldown = 1.0; return; }

        var btns = AutoSlayHelpers.FindAll<NRestSiteButton>(restRoom)
            .Where(b => b.Option.IsEnabled).ToList();

        if (choice >= 1 && choice <= btns.Count)
        {
            MainFile.Logger.Info($"[AutoSlay/LLM] Rest site choice: {choice}");
            btns[choice - 1].ForceClick();
        }
        else if (btns.Count > 0)
        {
            btns[_rng.Next(btns.Count)].ForceClick();
        }
        _restSiteChoiceMade = true;
        _cooldown = 1.5;
    }

    private void ExecuteCardRewardChoice(string response)
    {
        int choice = ParseChoice(response);
        MainFile.Logger.Info("666 ApplyCardRewardChoice from ExecuteCardRewardChoice");
        ApplyCardRewardChoice(choice);
    }

    private void ApplyCardRewardChoice(int choice)
    {
        var cardReward = NOverlayStack.Instance?.Peek() as NCardRewardSelectionScreen;
        if (cardReward == null) { _cooldown = 1.0; return; }

        if (choice <= 0)
        {
            // Skip — click the alternative/skip button
            var altBtn = AutoSlayHelpers.FindFirst<NCardRewardAlternativeButton>(cardReward);
            if (altBtn != null)
            {
                MainFile.Logger.Info("[AutoSlay/LLM] Clicking skip button on card reward");
                altBtn.ForceClick();
            }
            else
            {
                MainFile.Logger.Info("[AutoSlay/LLM] No skip button found, removing overlay");
                NOverlayStack.Instance?.Remove(cardReward);
            }
            _cooldown = 1.0;
            return;
        }

        var holders = AutoSlayHelpers.FindAll<NCardHolder>(cardReward);
        if (choice >= 1 && choice <= holders.Count)
        {
            MainFile.Logger.Info($"[AutoSlay/LLM] Card reward choice: {choice}");
            holders[choice - 1].EmitSignal(NCardHolder.SignalName.Pressed, holders[choice - 1]);
        }
        else
        {
            MainFile.Logger.Info("[AutoSlay/LLM] Invalid card choice, clicking skip");
            var altBtn = AutoSlayHelpers.FindFirst<NCardRewardAlternativeButton>(cardReward);
            if (altBtn != null) altBtn.ForceClick();
            else NOverlayStack.Instance?.Remove(cardReward);
        }
        _cooldown = 1.5;
    }

    private void ExecuteRewardsChoice(string response)
    {
        // Parse TAKE, CARD, and DONE commands
        var takeIndices = new List<int>();
        int cardChoice = -1;

        foreach (var line in response.Split('\n'))
        {
            var trimmed = line.Trim().ToUpper();
            if (trimmed.StartsWith("TAKE") && trimmed.Length > 4)
            {
                if (int.TryParse(trimmed.Substring(4).Trim(), out int idx))
                    takeIndices.Add(idx);
            }
            else if (trimmed.StartsWith("CARD") && trimmed.Length > 4)
            {
                if (int.TryParse(trimmed.Substring(4).Trim(), out int idx))
                    cardChoice = idx;
            }
        }

        MainFile.Logger.Info($"[AutoSlay/LLM] Reward plan: {takeIndices.Count} takes, card={cardChoice}");

        // Store card choice for when card reward screen opens
        _rewardCardChoice = cardChoice; // -1 = skip, >0 = pick that card

        // Resolve indices to actual button references NOW (before any buttons get disabled)
        var rewardsScreen = NOverlayStack.Instance?.Peek() as NRewardsScreen;
        var btns = rewardsScreen != null
            ? AutoSlayHelpers.FindAll<NRewardButton>(rewardsScreen).Where(b => b.IsEnabled).ToList()
            : new List<NRewardButton>();

        var queue = new Queue<NRewardButton>();
        foreach (var idx in takeIndices)
        {
            if (idx >= 1 && idx <= btns.Count)
            {
                var btn = btns[idx - 1];
                // Skip card reward buttons — handled separately below
                if (btn.Reward is MegaCrit.Sts2.Core.Rewards.CardReward)
                    continue;
                // Skip potion rewards when already have 3 potions (avoids bugs)
                if (btn.Reward is MegaCrit.Sts2.Core.Rewards.PotionReward)
                {
                    int potCount = 0;
                    try
                    {
                        var rs = RunManager.Instance?.DebugOnlyGetState();
                        var pl = rs != null ? LocalContext.GetMe(rs) : null;
                        if (pl != null)
                        {
                            foreach (var p in pl.Potions)
                            {
                                try { if (!((bool)p.HasBeenRemovedFromState) && !((bool)p.IsQueued)) potCount++; }
                                catch { }
                            }
                        }
                    }
                    catch { }
                    if (potCount >= 3)
                    {
                        MainFile.Logger.Info($"[AutoSlay/LLM] Skipping potion reward — already have {potCount} potions");
                        continue;
                    }
                }
                queue.Enqueue(btn);
            }
            else
                MainFile.Logger.Info($"[AutoSlay/LLM] Invalid reward index {idx} (max={btns.Count})");
        }

        // If LLM wants a card, enqueue the card reward button at the end
        if (cardChoice > 0)
        {
            var cardBtn = btns.FirstOrDefault(b => b.Reward is MegaCrit.Sts2.Core.Rewards.CardReward);
            if (cardBtn != null)
                queue.Enqueue(cardBtn);
        }

        _rewardTakeQueue = queue;
        _cooldown = 0.3;
    }

    private void ExecuteCardGridChoice(string response)
    {
        int choice = ParseChoice(response);
        MainFile.Logger.Info($"[AutoSlay/LLM] Card grid choice: {choice}");
        CardGridHandler.SetLlmChoice(choice);
        _cooldown = 0.5; // delay to let screen fully initialize before handler interacts
    }

    private void ExecuteSimpleCardSelectChoice(string response)
    {
        int choice = ParseChoice(response);
        MainFile.Logger.Info($"[AutoSlay/LLM] Simple card select choice: {choice}");
        SimpleCardSelectHandler.SetLlmChoice(choice);
        _cooldown = 0.5; // delay to let screen fully initialize before handler interacts
    }

    private void ParseAndStartShopPlan(string response)
    {
        var buys = new List<int>();
        foreach (var line in response.Split('\n'))
        {
            var trimmed = line.Trim().ToUpper();
            if (trimmed.StartsWith("BUY") && trimmed.Length > 3)
            {
                var numStr = trimmed.Substring(3).Trim();
                if (int.TryParse(numStr, out int idx))
                    buys.Add(idx);
            }
            if (trimmed == "LEAVE") break;
        }
        MainFile.Logger.Info($"[AutoSlay/LLM] Shop plan: {buys.Count} purchases");
        if (buys.Count == 0)
        {
            // Nothing to buy, leave immediately
            _shopPlan = null;
            LeaveShop();
            return;
        }
        _shopPlan = buys;
        _shopPlanStep = 0;
        _cooldown = 0.3;
    }

    private void ExecuteNextShopStep(NMerchantRoom shopRoom)
    {
        if (_shopPlan == null) return;

        if (_shopPlanStep >= _shopPlan.Count)
        {
            MainFile.Logger.Info("[AutoSlay/LLM] Shop plan complete, leaving");
            _shopPlan = null;
            LeaveShop();
            return;
        }

        var inv = shopRoom.Inventory?.Inventory;
        if (inv == null) { _shopPlan = null; _cooldown = 1.0; return; }

        // Build the same indexed list as serializer
        var items = new List<MerchantEntry>();
        foreach (var e in inv.CardEntries) if (e.IsStocked) items.Add(e);
        foreach (var e in inv.RelicEntries) if (e.IsStocked) items.Add(e);
        foreach (var e in inv.PotionEntries) if (e.IsStocked) items.Add(e);
        if (inv.CardRemovalEntry?.IsStocked == true) items.Add(inv.CardRemovalEntry);

        int idx = _shopPlan[_shopPlanStep];
        _shopPlanStep++;

        if (idx >= 1 && idx <= items.Count)
        {
            var entry = items[idx - 1];
            if (entry.IsStocked && entry.EnoughGold)
            {
                MainFile.Logger.Info($"[AutoSlay/LLM] Buying item {idx}: {entry.GetType().Name} ({entry.Cost}g)");
                TaskHelper.RunSafely(entry.OnTryPurchaseWrapper(inv));
                _cooldown = 1.5;
                return;
            }
            MainFile.Logger.Info($"[AutoSlay/LLM] Skipping item {idx}: not stocked or not enough gold");
        }
        _cooldown = 0.3;
    }

    private void LeaveShop()
    {
        MainFile.Logger.Info("[AutoSlay/LLM] Leaving shop");
        _shopLeaving = true;
        _cooldown = 1.5;
    }

    private static int ParseChoice(string response)
    {
        // Look for "CHOOSE <number>" pattern first
        foreach (var line in response.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("CHOOSE ", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(trimmed.Substring(7).Trim(), out int val))
                    return val;
            }
        }

        // Fallback: find first number in response (supports multi-digit)
        var match = System.Text.RegularExpressions.Regex.Match(response, @"\d+");
        if (match.Success && int.TryParse(match.Value, out int fallbackVal))
            return fallbackVal;
        return -1;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Random overlay dispatch (used when LLM is off or for non-strategic overlays)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 30-second timeout random fallback for overlay screens.
    /// Picks a random valid option for the current overlay type.
    /// </summary>
    private void RandomOverlayFallback(Node overlayNode)
    {
        try
        {
            // ── Multiplayer host guard: never auto-pick for the human host ──
            // If the host takes longer than 30s to decide, we still don't
            // interfere — just reset the timer and let them continue.
            if (IsHostManualMode)
            {
                MainFile.Logger.Info("[AutoSlay] Host manual mode: suppressing RandomOverlayFallback, resetting timeout");
                _sameScreenDuration = 0;
                _sameScreenTickCount = 0;
                _lastScreenType = "";
                _cooldown = 3.0;
                return;
            }

            // ── Card reward: pick random card or skip ──
            if (overlayNode is NCardRewardSelectionScreen cardReward)
            {
                var cards = AutoSlayHelpers.FindAll<NCardHolder>(cardReward)
                    .Where(c => GodotObject.IsInstanceValid(c)).ToList();
                if (cards.Count > 0)
                {
                    int pick = _rng.Next(cards.Count);
                    MainFile.Logger.Info($"[AutoSlay] Random overlay: picking card {pick}/{cards.Count}");
                    cards[pick].EmitSignal(NCardHolder.SignalName.Pressed, cards[pick]);
                }
                else
                {
                    var altBtn = AutoSlayHelpers.FindFirst<NCardRewardAlternativeButton>(cardReward);
                    if (altBtn != null) altBtn.ForceClick();
                    else NOverlayStack.Instance?.Remove(cardReward as IOverlayScreen);
                }
                _cooldown = 1.0;
                return;
            }
            // ── Relic selection: pick random relic (uses NClickableControl) ──
            if (overlayNode is NChooseARelicSelection chooseRelic)
            {
                var relics = AutoSlayHelpers.FindAll<NClickableControl>(chooseRelic)
                    .Where(r => GodotObject.IsInstanceValid(r)).ToList();
                if (relics.Count > 0)
                {
                    MainFile.Logger.Info($"[AutoSlay] Random overlay: picking relic {_rng.Next(relics.Count)}/{relics.Count}");
                    relics[_rng.Next(relics.Count)].ForceClick();
                }
                _cooldown = 1.0;
                return;
            }
            // ── Card grid (upgrade/transform/enchant/remove): pick first card then confirm ──
            if (overlayNode is NDeckUpgradeSelectScreen or NDeckTransformSelectScreen
                or NDeckEnchantSelectScreen or NDeckCardSelectScreen)
            {
                var grid = AutoSlayHelpers.FindFirst<NCardGrid>((Node)(object)overlayNode);
                if (grid != null)
                {
                    var gridCards = AutoSlayHelpers.FindAll<NGridCardHolder>((Node)(object)overlayNode);
                    if (gridCards.Count > 0)
                    {
                        MainFile.Logger.Info($"[AutoSlay] Random overlay: picking card grid item 0/{gridCards.Count}");
                        grid.EmitSignal(NCardGrid.SignalName.HolderPressed, gridCards[0]);
                    }
                }
                var confirm = AutoSlayHelpers.FindFirst<NConfirmButton>((Node)(object)overlayNode);
                if (confirm?.IsEnabled == true) confirm.ForceClick();
                _cooldown = 0.5;
                return;
            }
            // ── Simple card select: pick first card ──
            if (overlayNode is NSimpleCardSelectScreen simpleSelect)
            {
                var holders = AutoSlayHelpers.FindAll<NCardHolder>(simpleSelect)
                    .Where(h => GodotObject.IsInstanceValid(h)).ToList();
                if (holders.Count > 0)
                    holders[0].EmitSignal(NCardHolder.SignalName.Pressed, holders[0]);
                var btn = AutoSlayHelpers.FindFirst<NConfirmButton>(simpleSelect);
                if (btn?.IsEnabled == true) btn.ForceClick();
                _cooldown = 0.5;
                return;
            }
            // ── Choose card: pick first card then confirm ──
            if (overlayNode is NChooseACardSelectionScreen chooseCard)
            {
                var cards = AutoSlayHelpers.FindAll<NCardHolder>(chooseCard)
                    .Where(h => GodotObject.IsInstanceValid(h)).ToList();
                if (cards.Count > 0)
                    cards[0].EmitSignal(NCardHolder.SignalName.Pressed, cards[0]);
                var c = AutoSlayHelpers.FindFirst<NConfirmButton>(chooseCard);
                if (c?.IsEnabled == true) c.ForceClick();
                _cooldown = 0.5;
                return;
            }
            // ── Choose bundle: pick first then confirm (uses NCardBundle) ──
            if (overlayNode is NChooseABundleSelectionScreen chooseBundle)
            {
                // Step 1: Check if a bundle is already selected → confirm
                var confirm = AutoSlayHelpers.FindFirst<NConfirmButton>(chooseBundle);
                if (confirm?.IsEnabled == true)
                {
                    MainFile.Logger.Info("[AutoSlay] RandomOverlay: confirming bundle selection");
                    confirm.ForceClick();
                    _cooldown = 0.5;
                    return;
                }

                // Step 2: Select a bundle via Hitbox click
                var bundles = AutoSlayHelpers.FindAll<NCardBundle>(chooseBundle)
                    .Where(b => GodotObject.IsInstanceValid(b)).ToList();
                if (bundles.Count > 0)
                {
                    var pick = bundles[0];
                    MainFile.Logger.Info($"[AutoSlay] RandomOverlay: selecting bundle '{pick.Name}' (hitbox={(pick.Hitbox != null ? "yes" : "no")})");
                    if (pick.Hitbox != null)
                        pick.Hitbox.ForceClick();
                }
                _cooldown = 0.5;
                return;
            }
            // ── Rewards screen: click proceed ──
            if (overlayNode is NRewardsScreen rewards)
            {
                var proceed = AutoSlayHelpers.FindFirst<NProceedButton>(rewards);
                if (proceed?.IsEnabled == true)
                    proceed.ForceClick();
                else
                    NOverlayStack.Instance?.Remove(rewards as IOverlayScreen);
                _cooldown = 1.0;
                return;
            }
            // ── Game over: click continue ──
            if (overlayNode is NGameOverScreen gameOver)
            {
                GameOverHandler.Handle(gameOver);
                _cooldown = 1.0;
                return;
            }
            // ── Crystal sphere: click first hidden cell ──
            if (overlayNode is NCrystalSphereScreen crystal)
            {
                var cells = crystal.GetNodeOrNull<Control>("%Cells");
                if (cells != null)
                {
                    var hidden = AutoSlayHelpers.FindAll<NCrystalSphereCell>(cells)
                        .Where(c => GodotObject.IsInstanceValid(c) && c.Visible && c.Entity.IsHidden)
                        .ToList();
                    if (hidden.Count > 0)
                        hidden[0].EmitSignal(NClickableControl.SignalName.Released, hidden[0]);
                }
                _cooldown = 0.5;
                return;
            }
            // ── Unknown: try dismiss ──
            UiUtils.TryDismissUnknownOverlay(overlayNode);
            _cooldown = 0.5;
        }
        catch (Exception ex)
        {
            MainFile.Logger.Error($"[AutoSlay] RandomOverlayFallback CRASH for {overlayNode?.GetType().Name}: {ex.Message}");
            _cooldown = 0.5;
        }
    }

    private double DispatchOverlay(Node overlayNode, double delta)
    {
        try
        {
        // ── Multiplayer host guard: when host has auto-battle OFF, skip ALL auto-decisions ──
        // The human host should manually interact with all overlay screens.
        // Only game-over screen is truly non-interactive (just reports results).
        if (IsHostManualMode)
            {
                if (overlayNode is NGameOverScreen)
                {
                    // Game over: still auto-handle (just logs and shows stats)
                    MainFile.Logger.Info("[AutoSlay] Host manual mode: auto-handling game over screen");
                }
                else
                {
                    // Reset screen tracking so timeout doesn't accumulate while human is deciding
                    _sameScreenDuration = 0;
                    _sameScreenTickCount = 0;
                    _lastScreenType = "";
                    _unknownOverlayRetries = 0;
                    return 3.0; // long cooldown — let the human play
                }
            }

        if (overlayNode is NCardRewardSelectionScreen cardReward)
        {
            MainFile.Logger.Info("[AutoSlay] Dispatching OVERLAY_CARD_REWARD");
            DecisionEngine.Decide(GameScreen.OVERLAY_CARD_REWARD, delta);
            return 1.5;
        }
        if (overlayNode is NRewardsScreen rewardsScreen)
        {
            MainFile.Logger.Info("[AutoSlay] Dispatching rewards screen");
            return RewardsHandler.Handle(rewardsScreen);
        }
        if (overlayNode is NGameOverScreen gameOver)
        {
            MainFile.Logger.Info("[AutoSlay] Game over screen detected");
            RunSummaryLogger.TryLog(_llm);

            // ── Check for unused potions on death ──────────────────────
            // If the bot died with potions still available, it's a critical failure:
            // the potion-usage logic needs fixing. Log this prominently.
            try
            {
                var rs = RunManager.Instance?.DebugOnlyGetState();
                var player = rs != null ? LocalContext.GetMe(rs) : null;
                if (player != null)
                {
                    int unusedPotionCount = 0;
                    var unusedIds = new List<string>();
                    foreach (var p in player.Potions)
                    {
                        try
                        {
                            if (!((bool)p.HasBeenRemovedFromState) && !((bool)p.IsQueued))
                            {
                                unusedPotionCount++;
                                unusedIds.Add(p.Id?.Entry ?? "?");
                            }
                        }
                        catch { }
                    }
                    if (unusedPotionCount > 0)
                    {
                        MainFile.Logger.Error($"[AutoSlay] DIED WITH {unusedPotionCount} UNUSED POTIONS: [{string.Join(", ", unusedIds)}] — POTION LOGIC NEEDS FIXING!");
                    }
                    else
                    {
                        MainFile.Logger.Info($"[AutoSlay] Died with 0 unused potions — all potions were used or none available.");
                    }
                }
            }
            catch (Exception ex) { MainFile.Logger.Info($"[AutoSlay] Potion check on death failed: {ex.Message}"); }

            // In batch mode: signal run complete IMMEDIATELY — do NOT click continue.
            // Clicking continue goes back to main menu where HandleMainMenu falls through
            // to start a new run with the SAME seed. We must wait for the batch runner
            // to kill this process and launch a new one with a fresh seed.
            if (_batchMode)
            {
                SignalRunComplete();
                return 3.0; // Just wait — batch runner will kill the game
            }

            if (_llm != null && !_gameOverReflected)
            {
                if (_pendingLlm == null)
                {
                    MainFile.Logger.Info("[AutoSlay] Requesting game over reflection from LLM");
                    var stats = RunSummaryLogger.LastRunStats ?? "No stats available";
                    var currentMemory = _llm.Memory.Length > 0 ? _llm.Memory : "(empty — this is your first run)";
                    var prompt = PromptStrings.Get("GameOverReflection", stats, currentMemory);
                    _pendingLlm = _llm.SendAsync(prompt, "gameover_reflection");
                    _pendingContext = "gameover_reflection";
                    return 1.0;
                }
                return 0.5; // waiting for reflection response
            }
            MainFile.Logger.Info("[AutoSlay] Handling game over screen");
            return GameOverHandler.Handle(gameOver);
        }
        if (overlayNode is NChooseACardSelectionScreen chooseCard)
        {
            MainFile.Logger.Info("[AutoSlay] Dispatching OVERLAY_CHOOSE_CARD");
            DecisionEngine.Decide(GameScreen.OVERLAY_CHOOSE_CARD, delta);
            return 1.0;
        }
        if (overlayNode is NChooseABundleSelectionScreen chooseBundle)
        {
            MainFile.Logger.Info("[AutoSlay] Dispatching OVERLAY_CHOOSE_BUNDLE");
            DecisionEngine.Decide(GameScreen.OVERLAY_CHOOSE_BUNDLE, delta);
            return 0.5;
        }
        if (overlayNode is NChooseARelicSelection chooseRelic)
        {
            MainFile.Logger.Info("[AutoSlay] Dispatching OVERLAY_CHOOSE_RELIC");
            DecisionEngine.Decide(GameScreen.OVERLAY_CHOOSE_RELIC, delta);
            return 1.0;
        }
        if (overlayNode is NDeckUpgradeSelectScreen or NDeckTransformSelectScreen
            or NDeckEnchantSelectScreen or NDeckCardSelectScreen)
        {
            MainFile.Logger.Info($"[AutoSlay] Dispatching OVERLAY_DECK_GRID for {overlayNode.GetType().Name}");
            DecisionEngine.Decide(GameScreen.OVERLAY_DECK_GRID, delta);
            return 0.5;
        }
        if (overlayNode is NSimpleCardSelectScreen simpleSelect)
        {
            MainFile.Logger.Info("[AutoSlay] Dispatching OVERLAY_SIMPLE_SELECT");
            DecisionEngine.Decide(GameScreen.OVERLAY_SIMPLE_SELECT, delta);
            return 0.5;
        }
        if (overlayNode is NCrystalSphereScreen crystalSphere)
        {
            MainFile.Logger.Info("[AutoSlay] Dispatching OVERLAY_CRYSTAL_SPHERE");
            DecisionEngine.Decide(GameScreen.OVERLAY_CRYSTAL_SPHERE, delta);
            return 0.5;
        }

        // Unknown overlay — use UiUtils to try to dismiss it
        _unknownOverlayRetries++;
        MainFile.Logger.Info($"[AutoSlay] Unknown overlay: {overlayNode.GetType().Name} — trying UiUtils dismiss (attempt {_unknownOverlayRetries})");
        if (UiUtils.TryDismissUnknownOverlay(overlayNode))
        {
            _unknownOverlayRetries = 0;
            return 1.0;
        }
        // ── Deadlock protection ──────────────────────────────────────
        // If we've tried 10+ times to dismiss an unknown overlay and failed,
        // force-remove it from the overlay stack to break the loop.
        if (_unknownOverlayRetries >= 10)
        {
            MainFile.Logger.Error($"[AutoSlay] DEADLOCK: unknown overlay {overlayNode.GetType().Name} could not be dismissed after {_unknownOverlayRetries} attempts — force-removing");
            try
            {
                if (overlayNode is IOverlayScreen overlayScreen)
                    NOverlayStack.Instance?.Remove(overlayScreen);
                _unknownOverlayRetries = 0;
                return 1.0;
            }
            catch (Exception removeEx)
            {
                MainFile.Logger.Error($"[AutoSlay] Force-remove overlay failed: {removeEx.Message}");
            }
        }
        // Reset counter when overlay type changes (tracked elsewhere)
        return 0.5;
        }
        catch (Exception ex)
        {
            MainFile.Logger.Error($"[AutoSlay] DispatchOverlay CRASH for {overlayNode?.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            return 0.5;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Logging
    // ─────────────────────────────────────────────────────────────────────────

    private void LogState()
    {
        var stack = NOverlayStack.Instance;
        var overlayCount = stack?.ScreenCount ?? 0;
        var parts = new List<string>();
        parts.Add($"overlays={overlayCount}");
        if (overlayCount > 0 && stack != null)
        {
            var field = typeof(NOverlayStack).GetField("_overlays",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (field?.GetValue(stack) is System.Collections.IList overlays)
            {
                var names = new List<string>();
                foreach (var s in overlays)
                    names.Add(s?.GetType().Name ?? "null");
                parts.Add($"stack=[{string.Join(", ", names)}]");
            }
            else
            {
                var top = stack.Peek();
                parts.Add($"top={top?.GetType().Name ?? "null"}");
            }
        }
        var mapOpen = NMapScreen.Instance?.IsOpen ?? false;
        if (mapOpen) parts.Add("map=open");
        var cmActive = CombatManager.Instance?.IsInProgress ?? false;
        if (cmActive) parts.Add("combat=active");
        if (_llm != null) parts.Add($"llm_msgs={_llm.MessageCount}");
        string summary = string.Join(" ", parts);
        // Only log when state changes (was ~481 lines/session → ~50)
        if (summary != _lastLogStateSummary)
        {
            _lastLogStateSummary = summary;
            MainFile.Logger.Info($"[AutoSlay] Tick: {summary}");
        }
    }

    /// <summary>
    /// End the current player's turn. In multiplayer, the end-turn action MUST go
    /// through the network-synced action queue — ALWAYS, for both host and client.
    ///
    /// The game's NEndTurnButton uses:
    ///   ActionQueueSynchronizer.RequestEnqueue(new EndPlayerTurnAction(me, turnNumber))
    ///
    /// ActionQueueSynchronizer handles the role automatically:
    ///   - Host: enqueues directly into the synced queue
    ///   - Client: sends a message to host requesting enqueue
    ///
    /// PlayerCmd.EndTurn() is LOCAL-ONLY — it does NOT propagate to other peers.
    /// Using it in multiplayer causes the turn to never advance because the host
    /// never knows the other player is ready. Only use it in singleplayer.
    /// </summary>
    private void EndTurnViaUiOrApi(Player player)
    {
        try
        {
            if (_multiplayerMode)
            {
                // ── Multiplayer (host OR client): must use synced action queue ──
                int turnNumber = player.PlayerCombatState.TurnNumber;
                RunManager.Instance?.ActionQueueSynchronizer?.RequestEnqueue(
                    new MegaCrit.Sts2.Core.GameActions.EndPlayerTurnAction(player, turnNumber));
                MainFile.Logger.Info($"[AutoSlay] MP EndTurn: enqueued EndPlayerTurnAction turn#{turnNumber} (IsHost={_isMultiplayerHost})");
            }
            else
            {
                // ── Singleplayer: direct API is fine ──
                PlayerCmd.EndTurn(player, canBackOut: false);
            }
        }
        catch (Exception ex)
        {
            MainFile.Logger.Error($"[AutoSlay] EndTurnViaUiOrApi failed: {ex.Message}");
        }
    }

    private void LogOnce(string msg)
    {
        // Keep a sliding window of recent messages to handle alternating patterns
        // (e.g., "DismissModal: 1 buttons" ↔ "DismissModal (1-btn): clicking Proceed"
        //  previously defeated single-message dedup).
        // Increased from 5 to 20 to support the broader set of LogOnce calls.
        if (_recentLogs.Contains(msg)) return;
        if (_recentLogs.Count >= 20) _recentLogs.Clear();
        _recentLogs.Add(msg);
        MainFile.Logger.Info($"[AutoSlay] {msg}");
    }

    private static void UnlockAll()
    {
        try
        {
            var progress = SaveManager.Instance.Progress;

            // Unlock all epochs (characters, content tiers)
            foreach (var epochId in EpochModel.AllEpochIds)
            {
                SaveManager.Instance.ObtainEpochOverride(epochId, EpochState.Revealed);
            }

            // Unlock all cards, relics, potions, events
            foreach (var card in ModelDb.AllCards)
                progress.MarkCardAsSeen(card.Id);
            foreach (var relic in ModelDb.AllRelics)
                progress.MarkRelicAsSeen(relic.Id);
            foreach (var potion in ModelDb.AllPotions)
                progress.MarkPotionAsSeen(potion.Id);
            foreach (var evt in ModelDb.AllEvents)
                progress.MarkEventAsSeen(evt.Id);

            // All unlock/history nodes are handled via ObtainEpochOverride above.
            // Epochs ARE the progression tree nodes — revealing them unlocks everything.

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
                        MainFile.Logger.Info($"[AutoSlay] Ascension unlock failed: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                MainFile.Logger.Info($"[AutoSlay] Ascension unlock (non-critical): {ex.Message}");
            }

            SaveManager.Instance.SaveProgressFile();
            MainFile.Logger.Info("[AutoSlay] All content unlocked: cards, relics, potions, events, epochs, ascension.");
        }
        catch (Exception ex)
        {
            MainFile.Logger.Error($"[AutoSlay] UnlockAll failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Initialize the AI chat engine with the assigned character persona.
    /// Called once during _Ready when AiChatEnabled + AiChatCharacter are set.
    /// </summary>
    private void InitializeAiChat()
    {
        try
        {
            var persona = CharacterProfileManager.GetPersona(_aiChatCharacter);
            if (persona == null)
            {
                MainFile.Logger.Info($"[AutoSlay] AI Chat: character '{_aiChatCharacter}' not found — using meow fallback");
                return;
            }

            _chatEngine = new ChatEngine(persona, characterName: _aiChatCharacter);
            _aiChatInitialized = true;

            _aiChatDisplayName = CharacterProfileManager.GetDisplayName(_aiChatCharacter);
            MainFile.Logger.Info($"[AutoSlay] AI Chat initialized: {_aiChatDisplayName} ({_aiChatCharacter}), shared conversation mode");

            // Initialize shared conversation log (once per process)
            if (!_conversationInitialized && AppConfig.IsInitialized)
            {
                Chat.ConversationManager.Initialize(AppConfig.ModDirectory);
                _conversationInitialized = true;
            }

            // Initialize chat history logger
            Chat.ChatLogger.Initialize(AppConfig.ModDirectory);
        }
        catch (Exception ex)
        {
            MainFile.Logger.Info($"[AutoSlay] AI Chat init failed: {ex.Message} — using meow fallback");
        }
    }

    /// <summary>
    /// Shared multi-bot conversation with turn-taking.
    ///
    /// All bots read/write a shared conversation log (ConversationManager).
    /// Each bot polls every ~1.8s: if the last speaker isn't me, it's my turn.
    /// After speaking, the bot waits ~3s before checking again to let others respond.
    ///
    /// This produces a natural back-and-forth with minimal gaps — no explicit
    /// coordination needed beyond the shared file.
    /// </summary>
    private async void TrySendAiChat(double delta)
    {
        // Only in multiplayer mode, only as client (bot), only when auto-battle is on
        if (!_multiplayerMode || _isMultiplayerHost || !_autoBattle) return;

        _lastMeowTime += delta;

        // ── Determine polling interval ───────────────────────────────
        // Longer cooldown after my own message; shorter when waiting for others.
        var lastSpeaker = Chat.ConversationManager.GetLastSpeaker();
        var isMyTurn = lastSpeaker == null ||
                       !string.Equals(lastSpeaker, _aiChatDisplayName, StringComparison.OrdinalIgnoreCase);

        var pollInterval = isMyTurn ? _chatPollInterval : _chatMyTurnCooldown;

        if (_lastMeowTime < pollInterval) return;
        _lastMeowTime = 0;

        // ── Not my turn yet — skip ──────────────────────────────────
        if (!isMyTurn)
        {
            return;
        }

        // ── My turn — generate a response ───────────────────────────
        if (_aiChatInitialized && _chatEngine != null)
        {
            try
            {
                var state = GameStateExtractor.BuildContext();
                if (string.IsNullOrEmpty(state))
                {
                    return;
                }

                // Build conversation context: who else is talking, what was said
                var convoHistory = Chat.ConversationManager.BuildContextString(6);
                var otherNames = GetOtherBotNames();

                MainFile.Logger.Info($"[AutoSlay] My turn ({_aiChatDisplayName}) — generating response");

                var lines = await _chatEngine.SendConversationTurnAsync(
                    state, convoHistory, _aiChatDisplayName, otherNames);

                if (lines != null && lines.Length > 0)
                {
                    // Write each line as a separate message to the shared log
                    foreach (var line in lines)
                    {
                        Chat.ConversationManager.Append(_aiChatDisplayName, line);
                        SendChatPing(line);
                        // Tiny delay between multi-line responses so they don't stack
                        await Task.Delay(300);
                    }
                    MainFile.Logger.Info($"[AutoSlay] {_aiChatDisplayName} spoke: {string.Join(" | ", lines)}");
                }
            }
            catch (Exception ex)
            {
                MainFile.Logger.Info($"[AutoSlay] AI Chat turn error: {ex.Message}");
            }
        }
        else
        {
            // Fallback: occasional meow if AI isn't initialized
            SendChatPing(null);
        }
    }

    /// <summary>
    /// Collect the display names of all other bots currently in the conversation,
    /// formatted for the AI prompt.
    /// </summary>
    private string GetOtherBotNames()
    {
        // Check the conversation log for distinct speakers that aren't us
        var recent = Chat.ConversationManager.GetRecent(10);
        var otherNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, _) in recent)
        {
            if (!string.Equals(name, _aiChatDisplayName, StringComparison.OrdinalIgnoreCase))
                otherNames.Add(name);
        }
        return otherNames.Count > 0 ? string.Join("、", otherNames) : "";
    }

    /// <summary>
    /// Send a single chat ping. If text is null, falls back to "喵喵喵".
    /// Writes text to shared file for cross-process visibility,
    /// sets OverrideText for the bot's own process, then sends the ping.
    /// </summary>
    private void SendChatPing(string? text)
    {
        try
        {
            var flavorSync = RunManager.Instance?.FlavorSynchronizer;
            if (flavorSync == null) return;

            if (text != null)
            {
                // Set thread-static override (works on bot's own process)
                FlavorTextPatch.OverrideText = text;

                // Write to shared file (works on ALL processes — host + other bots)
                try
                {
                    if (AppConfig.IsInitialized)
                    {
                        var filePath = System.IO.Path.Combine(AppConfig.ModDirectory, ".ai_chat_current.txt");
                        System.IO.File.WriteAllText(filePath, text);
                    }
                }
                catch { /* best effort */ }
            }
            else
            {
                FlavorTextPatch.OverrideText = null;
                // Clear shared file so other peers also get meow
                try
                {
                    if (AppConfig.IsInitialized)
                    {
                        var filePath = System.IO.Path.Combine(AppConfig.ModDirectory, ".ai_chat_current.txt");
                        System.IO.File.WriteAllText(filePath, "");
                    }
                }
                catch { /* best effort */ }
            }

            flavorSync.SendEndTurnPing();

            // Log to chat history file (AI text only, not meow fallback)
            if (text != null && !string.IsNullOrEmpty(_aiChatDisplayName))
            {
                Chat.ChatLogger.Log(_aiChatDisplayName, text);
            }

            MainFile.Logger.Info($"[AutoSlay] Chat ping: \"{text ?? "喵喵喵"}\"");
        }
        catch (Exception ex)
        {
            MainFile.Logger.Info($"[AutoSlay] Chat ping failed: {ex.Message}");
        }
    }

    private record CombatAction(CardModel? Card, PotionModel? Potion, Creature? Target);

}
