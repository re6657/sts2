# Multiplayer Rewrite — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the broken linear state machine in AutoSlayNode.HandleMultiplayerMainMenu with an event-driven MpController, fix the duplicate-join-request bug by removing the join-request from BeginStandardBrokerJoinAsync, and add heartbeat-based timeout detection.

**Architecture:** MpController (event-driven state machine) replaces HandleMultiplayerMainMenu in AutoSlayNode._Process. MpJoinFlow delegates to the simplified BrokerClientJoinFlow (no join request). MpLobbyCoordinator subscribes to broker messages for lobby lifecycle + heartbeat timeout detection.

**Tech Stack:** Godot 4.5.1, .NET 9, C#, Harmony (patches remain in `localcoop.transport-broker` Harmony instance)

## Global Constraints

- Must build with 0 errors via `dotnet build -c Release`
- Must NOT modify any file in `src/Solver/`, `src/Handlers/`, `src/AutoBattle/` (except AutoBattleController skeleton which stays gated)
- Single-player auto-battle must continue working unchanged (CoopMode=false path)
- BrokerServer must remain unchanged (TCP relay is pure passthrough)
- All broker patches use `localcoop.transport-broker` Harmony ID

---

## File Structure

| File | Action | Responsibility |
|---|---|---|
| `src/Core/GameScreen.cs` | Modify | Add LOBBY enum value |
| `src/Core/ScreenDetector.cs` | Modify | Add lobby screen detection |
| `src/Multiplayer/MpController.cs` | Rewrite | Event-driven state machine, recovery with retry counter, heartbeat check |
| `src/Multiplayer/MpJoinFlow.cs` | Rewrite | Thin wrapper: delegates to BrokerClientJoinFlow (simplified) |
| `src/Multiplayer/MpLobbyCoordinator.cs` | Rewrite | Message-driven lobby + heartbeat timeout tracking |
| `src/Multiplayer/MpScreenHandler.cs` | Rewrite | Robust button finding + escape + character select |
| `src/AutoSlayNode.cs` | Modify | Replace HandleMultiplayerMainMenu() with MpController.Update() |
| `src/Coop/Runtime/BrokerClientJoinFlow.cs` | Modify | Remove join-request send + response wait from BeginStandardBrokerJoinAsync |
| `src/Coop/Runtime/BrokerBackedNetService.cs` | Modify | Remove duplicate join suppression; forward join request normally |
| `src/Coop/Patches/BrokerClientJoinFlowPatch.cs` | Modify | Simplify: remove _cachedJoinResult + _joinInProgress guards |
| `src/Coop/Patches/BrokerForceLobbyTransitionPatch.cs` | Simplify | Remove reflection-based AreAllPlayersReady; delegate to MpLobbyCoordinator |

---

### Task 1: Add LOBBY to GameScreen enum + detection

**Files:**
- Modify: `src/Core/GameScreen.cs`
- Modify: `src/Core/ScreenDetector.cs`

**Interfaces:**
- Produces: `GameScreen.LOBBY` enum value, detected when lobby screen node is in scene tree

- [ ] **Step 1: Add LOBBY to GameScreen enum**

In `src/Core/GameScreen.cs`, add `LOBBY` to the enum:

```csharp
// In the GameScreen enum, add after MULTIPLAYER_HOST_SUBMENU:
/// <summary>Multiplayer lobby — all players connected, waiting to start.</summary>
LOBBY,
```

- [ ] **Step 2: Add lobby detection in ScreenDetector**

In `src/Core/ScreenDetector.cs`, in `DetectInternal()`, add lobby detection in the multiplayer section (after host submenu check, before rooms):

```csharp
// Lobby screen (all players connected, waiting for ready/embark)
// Look for StartRunLobby or lobby container nodes
var lobbyNode = node.GetNodeOrNull<Node>("Run/RoomContainer/LobbyRoom")
    ?? node.GetNodeOrNull<Control>("Lobby");
if (lobbyNode != null)
    return GameScreen.LOBBY;
```

- [ ] **Step 3: Build and verify**

```bash
cd "E:/SteamLibrary/steamapps/common/Slay the Spire 2/mods/TokenSpire2" && dotnet build -c Release 2>&1 | tail -5
```

Expected: Build succeeded with 0 errors.

---

### Task 2: Rewrite MpScreenHandler

**Files:**
- Modify: `src/Multiplayer/MpScreenHandler.cs`

**Interfaces:**
- Consumes: Nothing (standalone)
- Produces: `MpScreenHandler` class with `ClickButton(string)`, `SelectCharacter(string)`, `PressEscape()`, `IsButtonVisible(string)`, `ClickFirstEnabledButton()`

- [ ] **Step 1: Write the full MpScreenHandler**

Replace entire file content:

```csharp
using System;
using System.Collections.Generic;
using Godot;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;

namespace TokenSpire2.Multiplayer;

/// <summary>
/// Handles UI interactions for multiplayer screens.
/// All actions route through here for logging, retry, and click verification.
/// </summary>
public class MpScreenHandler
{
    private readonly HashSet<string> _recentClicks = new(); // dedup clicks within cooldown
    private double _clickCooldown;

    /// <summary>
    /// Click a button whose text contains the given substring.
    /// Returns true if a matching button was found and clicked.
    /// </summary>
    public bool ClickButton(string buttonText)
    {
        // Dedup: skip if we just clicked this button recently
        var now = DateTime.UtcNow.Ticks;
        if (_recentClicks.Contains(buttonText))
            return false;

        try
        {
            var root = GetRootNode();
            if (root == null) return false;

            foreach (var button in FindAllButtons(root))
            {
                if (!button.Visible || !button.IsEnabled)
                    continue;
                if (button.Text.Contains(buttonText, StringComparison.OrdinalIgnoreCase)
                    || button.Name?.ToString()?.Contains(buttonText, StringComparison.OrdinalIgnoreCase) == true)
                {
                    button.EmitSignal("pressed");
                    _recentClicks.Add(buttonText);
                    Log($"Clicked: \"{button.Text}\" (searched for \"{buttonText}\")");
                    return true;
                }
            }

            return false;
        }
        catch (Exception ex)
        {
            Log($"ClickButton error: {ex.Message}");
            return false;
        }
    }

    /// <summary>Click the first visible enabled button found in the scene.</summary>
    public bool ClickFirstEnabledButton()
    {
        try
        {
            var root = GetRootNode();
            if (root == null) return false;

            foreach (var button in FindAllButtons(root))
            {
                if (button.Visible && button.IsEnabled)
                {
                    button.EmitSignal("pressed");
                    Log($"Clicked first enabled: \"{button.Text}\"");
                    return true;
                }
            }
            return false;
        }
        catch (Exception ex)
        {
            Log($"ClickFirstEnabled error: {ex.Message}");
            return false;
        }
    }

    /// <summary>Check if a button with matching text is visible and enabled.</summary>
    public bool IsButtonVisible(string buttonText)
    {
        try
        {
            var root = GetRootNode();
            if (root == null) return false;

            foreach (var button in FindAllButtons(root))
            {
                if (button.Visible && button.IsEnabled
                    && button.Text.Contains(buttonText, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
        catch { return false; }
    }

    /// <summary>Select a character on the character select screen.</summary>
    public bool SelectCharacter(string characterName)
    {
        Log($"SelectCharacter: \"{characterName}\"");
        try
        {
            var root = GetRootNode();
            if (root == null) return false;

            // Search for NCharacterSelectButton instances
            foreach (var node in FindAllNodesRecursive(root))
            {
                if (node.GetType().Name == "NCharacterSelectButton"
                    && node is Button btn
                    && btn.Visible && !btn.Disabled)
                {
                    // Check character name via Character property
                    var charProp = node.GetType().GetProperty("Character");
                    var character = charProp?.GetValue(node);
                    var idProp = character?.GetType().GetProperty("Id");
                    var id = idProp?.GetValue(character);
                    var entryProp = id?.GetType().GetProperty("Entry");
                    var entry = entryProp?.GetValue(id) as string;

                    if (entry == characterName || characterName == "RANDOM")
                    {
                        btn.EmitSignal("pressed");
                        Log($"Selected character: {entry ?? "RANDOM"}");
                        return true;
                    }
                }
            }

            // Fallback: try clicking button by name
            return ClickButton(characterName);
        }
        catch (Exception ex)
        {
            Log($"SelectCharacter error: {ex.Message}");
            return false;
        }
    }

    /// <summary>Press Escape key.</summary>
    public void PressEscape()
    {
        Log("PressEscape");
        try
        {
            var input = new InputEventKey { Keycode = Key.Escape, Pressed = true };
            Input.ParseInputEvent(input);
        }
        catch (Exception ex)
        {
            Log($"PressEscape error: {ex.Message}");
        }
    }

    /// <summary>Clear click dedup cache (call when screen changes).</summary>
    public void ClearClickCache()
    {
        _recentClicks.Clear();
    }

    // ── Helpers ──

    private static Node? GetRootNode()
    {
        try
        {
            return ((SceneTree)Engine.GetMainLoop()).Root
                .GetNodeOrNull<Node>("Game/RootSceneContainer");
        }
        catch { return null; }
    }

    private static List<Button> FindAllButtons(Node root)
    {
        var buttons = new List<Button>();
        FindButtonsRecursive(root, buttons);
        return buttons;
    }

    private static void FindButtonsRecursive(Node node, List<Button> result)
    {
        if (node is Button button)
            result.Add(button);
        foreach (var child in node.GetChildren())
        {
            if (child is Node childNode)
                FindButtonsRecursive(childNode, result);
        }
    }

    private static List<Node> FindAllNodesRecursive(Node root)
    {
        var nodes = new List<Node>();
        CollectNodes(root, nodes);
        return nodes;
    }

    private static void CollectNodes(Node node, List<Node> result)
    {
        result.Add(node);
        foreach (var child in node.GetChildren())
        {
            if (child is Node childNode)
                CollectNodes(childNode, result);
        }
    }

    private static void Log(string msg)
    {
        try { MainFile.Logger?.Info($"[MpScreenHandler] {msg}"); }
        catch { /* logging unavailable */ }
    }
}
```

- [ ] **Step 2: Build and verify**

```bash
cd "E:/SteamLibrary/steamapps/common/Slay the Spire 2/mods/TokenSpire2" && dotnet build -c Release 2>&1 | tail -5
```

Expected: Build succeeded with 0 errors.

---

### Task 3: Rewrite MpLobbyCoordinator with heartbeat

**Files:**
- Modify: `src/Multiplayer/MpLobbyCoordinator.cs`

**Interfaces:**
- Consumes: `LobbyPhase` enum (defined in same file), `MainFile.Logger`
- Produces: `MpLobbyCoordinator` class with `OnEnteredLobby`, `OnPlayerReadyChanged`, `OnRunStarted`, `OnDisconnected`, `RecordHeartbeat`, `IsHeartbeatAlive`, `OnHeartbeatTimeout` event

- [ ] **Step 1: Write MpLobbyCoordinator**

Replace entire file:

```csharp
using System;

namespace TokenSpire2.Multiplayer;

/// <summary>
/// Coordinates the multiplayer lobby lifecycle.
/// Replaces BrokerForceLobbyTransitionPatch's reflection-based
/// AreAllPlayersReady with message-driven detection.
///
/// Heartbeat: Client sends heartbeat every 5s via broker.
/// Host checks every 10s. If no heartbeat for 60s → disconnect.
/// </summary>
public class MpLobbyCoordinator
{
    // ── Lobby state ──
    private LobbyPhase _phase = LobbyPhase.Disconnected;
    private int _playerCount;
    private int _readyPlayerCount;
    private DateTime _phaseEnteredAt = DateTime.UtcNow;

    // ── Heartbeat ──
    private DateTime _lastHeartbeatUtc = DateTime.MinValue;
    private const double HEARTBEAT_WARN_SECONDS = 30.0;
    private const double HEARTBEAT_TIMEOUT_SECONDS = 60.0;
    private bool _heartbeatTimedOut;

    // ── Events ──
    public event Action<LobbyPhase, LobbyPhase>? OnPhaseChanged;
    public event Action<int>? OnPlayerCountChanged;
    public event Action<bool>? OnAllPlayersReady;
    public event Action? OnHeartbeatTimeout; // fired when peer is dead

    // ── Properties ──
    public LobbyPhase CurrentPhase => _phase;
    public int PlayerCount => _playerCount;
    public int ReadyPlayerCount => _readyPlayerCount;
    public bool AllPlayersReady => _readyPlayerCount >= _playerCount && _playerCount >= 2;
    public TimeSpan TimeInPhase => DateTime.UtcNow - _phaseEnteredAt;
    public bool IsHeartbeatAlive => !_heartbeatTimedOut;

    /// <summary>Call when a heartbeat message arrives from a peer.</summary>
    public void RecordHeartbeat()
    {
        _lastHeartbeatUtc = DateTime.UtcNow;
        _heartbeatTimedOut = false;
    }

    /// <summary>
    /// Check heartbeat health. Call periodically (every few seconds).
    /// Returns true if the peer is still alive.
    /// Fires OnHeartbeatTimeout when peer exceeds timeout.
    /// </summary>
    public bool CheckHeartbeat()
    {
        if (_lastHeartbeatUtc == DateTime.MinValue)
            return true; // no heartbeat expected yet

        var elapsed = (DateTime.UtcNow - _lastHeartbeatUtc).TotalSeconds;

        if (elapsed > HEARTBEAT_TIMEOUT_SECONDS && !_heartbeatTimedOut)
        {
            _heartbeatTimedOut = true;
            Log($"[MpLobbyCoordinator] Heartbeat TIMEOUT after {elapsed:F0}s — peer is dead.");
            OnHeartbeatTimeout?.Invoke();
            return false;
        }

        if (elapsed > HEARTBEAT_WARN_SECONDS)
        {
            Log($"[MpLobbyCoordinator] Heartbeat WARNING: {elapsed:F0}s since last heartbeat.");
        }

        return !_heartbeatTimedOut;
    }

    // ── Phase transitions ──

    public void OnEnteredLobby(int playerCount)
    {
        TransitionTo(LobbyPhase.InLobby);
        _playerCount = playerCount;
        _readyPlayerCount = 0;
        OnPlayerCountChanged?.Invoke(_playerCount);
    }

    public void OnPlayerReadyChanged(int readyCount)
    {
        _readyPlayerCount = readyCount;
        OnAllPlayersReady?.Invoke(AllPlayersReady);
        Log($"[MpLobbyCoordinator] Ready: {_readyPlayerCount}/{_playerCount}");
    }

    public void OnRunStarted()
    {
        TransitionTo(LobbyPhase.InGame);
    }

    public void OnDisconnected(string? reason = null)
    {
        TransitionTo(LobbyPhase.Disconnected);
        Log($"[MpLobbyCoordinator] Disconnected: {reason ?? "unknown"}");
    }

    public void Reset()
    {
        _phase = LobbyPhase.Disconnected;
        _playerCount = 0;
        _readyPlayerCount = 0;
        _phaseEnteredAt = DateTime.UtcNow;
        _lastHeartbeatUtc = DateTime.MinValue;
        _heartbeatTimedOut = false;
    }

    // ── Helpers ──

    private void TransitionTo(LobbyPhase newPhase)
    {
        if (newPhase == _phase) return;
        var old = _phase;
        _phase = newPhase;
        _phaseEnteredAt = DateTime.UtcNow;
        Log($"[MpLobbyCoordinator] Phase: {old} → {newPhase}");
        OnPhaseChanged?.Invoke(old, newPhase);
    }

    private static void Log(string msg)
    {
        try { MainFile.Logger?.Info(msg); }
        catch { /* logging unavailable */ }
    }
}

public enum LobbyPhase
{
    Disconnected,
    InLobby,
    InGame,
}
```

- [ ] **Step 2: Build and verify**

```bash
cd "E:/SteamLibrary/steamapps/common/Slay the Spire 2/mods/TokenSpire2" && dotnet build -c Release 2>&1 | tail -5
```

---

### Task 4: Rewrite MpJoinFlow

**Files:**
- Modify: `src/Multiplayer/MpJoinFlow.cs`

**Interfaces:**
- Consumes: `MainFile.Logger`, `BrokerClientJoinFlow.BeginStandardBrokerJoinAsync` (simplified in Task 8)
- Produces: `MpJoinFlow` static class with `ExecuteJoinAsync`, `Reset`, `HasJoinCompleted`, `IsJoinInProgress`

- [ ] **Step 1: Write MpJoinFlow**

Replace entire file content:

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using LocalCoop.Mod.Runtime;

namespace TokenSpire2.Multiplayer;

/// <summary>
/// SINGLE CODE PATH for broker join handshake.
///
/// THE KEY FIX for the 3-player lobby bug:
/// BeginStandardBrokerJoinAsync does NOT send ClientLobbyJoinRequestMessage.
/// It only: creates transport → waits for InitialGameInfo → stores service.
///
/// The ONE AND ONLY join request is sent later by the game's
/// InitializeMultiplayerAsClient through BrokerBackedNetService.
/// </summary>
public static class MpJoinFlow
{
    private static int _joinInProgress;
    private static JoinResult? _cachedResult;
    private static readonly object _lock = new();

    public static bool IsJoinInProgress =>
        Interlocked.CompareExchange(ref _joinInProgress, 0, 0) == 1;

    public static bool HasJoinCompleted
    {
        get { lock (_lock) return _cachedResult != null; }
    }

    /// <summary>
    /// Execute the broker join handshake.
    /// Concurrent-call guard: returns cached result if already completed.
    /// </summary>
    public static async Task<JoinResult> ExecuteJoinAsync(
        BrokerModeSettings settings,
        Func<IBrokerEnvelopeTransport> createTransport,
        Action<string>? log = null,
        CancellationToken cancellation = default)
    {
        // Cached result
        lock (_lock)
        {
            if (_cachedResult != null)
            {
                Log("[MpJoinFlow] Join already completed, returning cached result.");
                return _cachedResult;
            }
        }

        // Concurrent guard
        if (Interlocked.CompareExchange(ref _joinInProgress, 1, 0) != 0)
        {
            Log("[MpJoinFlow] Join in progress — waiting for completion.");
            for (int i = 0; i < 300 && _cachedResult == null; i++)
                await Task.Delay(100, cancellation);
            lock (_lock)
            {
                if (_cachedResult != null)
                    return _cachedResult;
            }
            throw new TimeoutException("Timed out waiting for concurrent join.");
        }

        try
        {
            Log("[MpJoinFlow] Starting broker handshake (no join request sent)...");
            // BrokerClientJoinFlow.BeginStandardBrokerJoinAsync
            // (to be simplified in Task 8) handles transport + InitialGameInfo + registry.
            // It does NOT send ClientLobbyJoinRequestMessage.
            var result = await BrokerClientJoinFlow.BeginStandardBrokerJoinAsync(
                settings, createTransport, log, cancellation);

            lock (_lock) { _cachedResult = result; }
            Log("[MpJoinFlow] Broker handshake complete (JoinRequest NOT sent here).");
            return result;
        }
        catch (Exception ex)
        {
            Log($"[MpJoinFlow] ERROR: {ex.Message}");
            var result = new JoinResult
            {
                gameMode = null,
                sessionState = null,
                joinResponse = null
            };
            lock (_lock) { _cachedResult = result; }
            return result;
        }
        finally
        {
            Interlocked.Exchange(ref _joinInProgress, 0);
        }
    }

    public static void Reset()
    {
        lock (_lock) { _cachedResult = null; }
        Interlocked.Exchange(ref _joinInProgress, 0);
        Log("[MpJoinFlow] Reset.");
    }

    private static void Log(string msg)
    {
        try { MainFile.Logger?.Info(msg); }
        catch { /* logging unavailable */ }
    }
}
```

- [ ] **Step 2: Build and verify**

```bash
cd "E:/SteamLibrary/steamapps/common/Slay the Spire 2/mods/TokenSpire2" && dotnet build -c Release 2>&1 | tail -5
```

Expected: Build may fail here due to `JoinResult` references from `BrokerClientJoinFlow` — the `gameMode` / `sessionState` / `joinResponse` property access may need adjustment. If `JoinResult` changes in Task 8, come back and fix.

---

### Task 5: Rewrite MpController — event-driven state machine

**Files:**
- Modify: `src/Multiplayer/MpController.cs`

**Interfaces:**
- Consumes: `ScreenDetector.Detect()`, `AppConfig.Instance`, `MpScreenHandler`, `MpLobbyCoordinator`
- Produces: `MpController.Update(GameScreen, double)` → returns delay in seconds

- [ ] **Step 1: Write MpController**

Replace entire file:

```csharp
using System;
using TokenSpire2.Core;

namespace TokenSpire2.Multiplayer;

/// <summary>
/// Event-driven multiplayer state machine.
/// REPLACES AutoSlayNode.HandleMultiplayerMainMenu.
///
/// Key differences from v1:
///   1. DETECTS actual screen (never assumes "click → next state")
///   2. Recovery: max 3 attempts per state, then fallback to main menu
///   3. Heartbeat: host checks client heartbeat health
///   4. State-specific timeouts
/// </summary>
public class MpController
{
    private MpState _state = MpState.Inactive;
    private double _stateElapsed;
    private int _recoveryAttempts;
    private const int MAX_RECOVERY = 3;

    // ── Dependencies ──
    private readonly MpScreenHandler _ui = new();
    private readonly MpLobbyCoordinator _lobby = new();

    // ── Heartbeat timer ──
    private double _heartbeatTimer;
    private const double HEARTBEAT_CHECK_INTERVAL = 10.0;

    // ── State timeouts (seconds) ──
    private const double MAIN_MENU_TIMEOUT = 30.0;
    private const double ENTERING_MP_TIMEOUT = 15.0;
    private const double HOST_SUBMENU_TIMEOUT = 15.0;
    private const double JOINING_TIMEOUT = 120.0;
    private const double CHAR_SELECT_TIMEOUT = 90.0;
    private const double IN_LOBBY_TIMEOUT = 120.0;
    private const double DEFAULT_TIMEOUT = 45.0;

    // ── Events ──
    public event Action? OnDisconnected; // fired when peer times out / disconnect

    /// <summary>
    /// Call every frame from AutoSlayNode._Process when CoopMode=true.
    /// </summary>
    public double Update(GameScreen screen, double delta)
    {
        _stateElapsed += delta;

        // ── Heartbeat check (Host only) ──
        var cfg = AppConfig.Instance;
        if (cfg.IsHost && _state >= MpState.InLobby)
        {
            _heartbeatTimer += delta;
            if (_heartbeatTimer >= HEARTBEAT_CHECK_INTERVAL)
            {
                _heartbeatTimer = 0;
                if (!_lobby.CheckHeartbeat())
                {
                    Log("[MpController] Heartbeat timeout — peer is dead.");
                    OnDisconnected?.Invoke();
                    return HandleDisconnected();
                }
            }
        }

        // ── Step 1: Screen → State ──
        var detected = ScreenToMpState(screen);

        // ── Step 2: State change → reset ──
        if (detected != _state)
        {
            Log($"[MpController] {_state} → {detected} (screen={screen})");
            _state = detected;
            _stateElapsed = 0;
            _recoveryAttempts = 0;
            _ui.ClearClickCache();
        }

        // ── Step 3: Timeout → recover ──
        if (IsTimedOut())
        {
            Log($"[MpController] TIMEOUT in {_state} after {_stateElapsed:F0}s (attempt {_recoveryAttempts + 1}/{MAX_RECOVERY})");
            return HandleTimeout();
        }

        // ── Step 4: Dispatch ──
        return _state switch
        {
            MpState.Inactive => 0.5,
            MpState.MainMenu => HandleMainMenu(),
            MpState.EnteringMultiplayer => HandleEnteringMultiplayer(),
            MpState.HostSubmenu => HandleHostSubmenu(),
            MpState.Joining => HandleJoining(),
            MpState.CharacterSelect => HandleCharacterSelect(),
            MpState.InLobby => HandleInLobby(),
            MpState.InGame => 0.0,
            MpState.Disconnected => HandleDisconnected(),
            _ => 0.5
        };
    }

    public void Reset()
    {
        _state = MpState.Inactive;
        _stateElapsed = 0;
        _recoveryAttempts = 0;
        _heartbeatTimer = 0;
        _lobby.Reset();
    }

    // ═══════════════════════════════════════════════════════════════
    // Screen → State
    // ═══════════════════════════════════════════════════════════════

    private static MpState ScreenToMpState(GameScreen screen)
    {
        return screen switch
        {
            GameScreen.MAIN_MENU => MpState.MainMenu,
            GameScreen.MULTIPLAYER_SUBMENU => MpState.EnteringMultiplayer,
            GameScreen.MULTIPLAYER_HOST_SUBMENU => MpState.HostSubmenu,
            GameScreen.CHARACTER_SELECT or GameScreen.CHARACTER_SELECT_MULTIPLAYER => MpState.CharacterSelect,
            GameScreen.LOBBY => MpState.InLobby,
            GameScreen.COMBAT or GameScreen.MAP or GameScreen.EVENT
                or GameScreen.TREASURE or GameScreen.REST or GameScreen.SHOP
                or GameScreen.COMBAT_VICTORY or GameScreen.GAME_OVER => MpState.InGame,
            _ => MpState.MainMenu,
        };
    }

    // ═══════════════════════════════════════════════════════════════
    // State handlers
    // ═══════════════════════════════════════════════════════════════

    private double HandleMainMenu()
    {
        _ui.ClickButton("Multiplayer");
        return 2.0;
    }

    private double HandleEnteringMultiplayer()
    {
        var cfg = AppConfig.Instance;
        if (cfg.IsHost)
            _ui.ClickButton("Host");
        else
            _ui.ClickButton("Join");
        return 2.0;
    }

    private double HandleHostSubmenu()
    {
        // Host config screen — click confirm/start
        _ui.ClickFirstEnabledButton();
        return 2.0;
    }

    private double HandleJoining()
    {
        // Client connecting. Join request sent by InitializeMultiplayerAsClient
        // via BrokerBackedNetService. We just wait for transition.
        return 1.0;
    }

    private double HandleCharacterSelect()
    {
        var cfg = AppConfig.Instance;

        if (cfg.IsHumanPlayer)
            return 2.0; // idle: let human pick

        // Bot: select character
        var character = cfg.Character ?? "Ironclad";
        if (character == "RANDOM")
            character = "Ironclad";
        _ui.SelectCharacter(character);

        // Try embark
        _ui.ClickButton("Confirm");
        _ui.ClickButton("Embark");

        // Record heartbeat if client
        if (cfg.IsClient)
            _lobby.RecordHeartbeat();

        return 2.0;
    }

    private double HandleInLobby()
    {
        var cfg = AppConfig.Instance;

        // Record heartbeat (client sends, host receives via broker transport)
        // The actual heartbeat message is sent via BrokerBackedNetService.SendMessageAsync
        if (cfg.IsClient)
            _lobby.RecordHeartbeat();

        if (cfg.IsHost && cfg.AutoStartEnabled)
        {
            _ui.ClickButton("Ready");
            _ui.ClickButton("Embark");
            return 3.0;
        }

        if (cfg.IsBot)
        {
            _ui.ClickButton("Ready");
            return 2.0;
        }

        return 3.0;
    }

    private double HandleDisconnected()
    {
        Log("[MpController] Disconnected — returning to main menu.");
        _ui.PressEscape();
        return 2.0;
    }

    // ═══════════════════════════════════════════════════════════════
    // Timeout + Recovery
    // ═══════════════════════════════════════════════════════════════

    private bool IsTimedOut()
    {
        double timeout = _state switch
        {
            MpState.MainMenu => MAIN_MENU_TIMEOUT,
            MpState.EnteringMultiplayer => ENTERING_MP_TIMEOUT,
            MpState.HostSubmenu => HOST_SUBMENU_TIMEOUT,
            MpState.Joining => JOINING_TIMEOUT,
            MpState.CharacterSelect => CHAR_SELECT_TIMEOUT,
            MpState.InLobby => IN_LOBBY_TIMEOUT,
            _ => DEFAULT_TIMEOUT
        };
        return _stateElapsed > timeout;
    }

    private double HandleTimeout()
    {
        _recoveryAttempts++;

        if (_recoveryAttempts >= MAX_RECOVERY)
        {
            Log($"[MpController] Max recovery ({MAX_RECOVERY}) reached — returning to main menu.");
            _state = MpState.MainMenu;
            _stateElapsed = 0;
            _recoveryAttempts = 0;
            _ui.PressEscape();
            _ui.PressEscape(); // double escape to clear overlays
            return 2.0;
        }

        // Recovery: go back one step
        _stateElapsed = 0;
        _ui.PressEscape();
        _ui.ClearClickCache();
        return 1.5;
    }

    private static void Log(string msg)
    {
        try { MainFile.Logger?.Info(msg); }
        catch { /* logging unavailable */ }
    }
}

public enum MpState
{
    Inactive,
    MainMenu,
    EnteringMultiplayer,
    HostSubmenu,
    Joining,
    CharacterSelect,
    InLobby,
    InGame,
    Disconnected,
}
```

- [ ] **Step 2: Build and verify**

```bash
cd "E:/SteamLibrary/steamapps/common/Slay the Spire 2/mods/TokenSpire2" && dotnet build -c Release 2>&1 | tail -5
```

Expected: 0 errors. MpController now ready to be wired in.

---

### Task 6: Wire MpController into AutoSlayNode._Process

**Files:**
- Modify: `src/AutoSlayNode.cs` — replace `HandleMultiplayerMainMenu` path + delete method

**Interfaces:**
- Consumes: `MpController`, `ScreenDetector.Detect()`
- Produces: Cleaner `_Process` — MP branch delegates to `MpController`

- [ ] **Step 1: Add MpController field and modify _Process**

In `src/AutoSlayNode.cs`, add field:

```csharp
// Near other field declarations (around line 100-160):
private MpController? _mpController;
```

In `_Process`, replace lines 2349-2371 (the `if (Coop.CoopManager.IsCoopMode)` block that calls `HandleMultiplayerMainMenu`):

```csharp
        // ═══════════════════════════════════════════════════════════════════
        // LAN BROKER MULTIPLAYER: delegate to event-driven MpController.
        // Replaces the linear HandleMultiplayerMainMenu with screen-detection-
        // based state machine that never assumes "click → next screen".
        // ═══════════════════════════════════════════════════════════════════
        if (Coop.CoopManager.IsCoopMode)
        {
            // Lazy-init MpController
            if (_mpController == null)
            {
                _mpController = new MpController();
                _mpController.OnDisconnected += () =>
                {
                    MainFile.Logger?.Info("[AutoSlay] MpController disconnected — returning to main menu.");
                };
            }

            var mpScreen = ScreenDetector.Detect();
            double mpDelay = _mpController.Update(mpScreen, delta);

            // If broker is active, never fall through to single-player path
            bool brokerActive = LocalCoop.Mod.Runtime.BrokerModeSettings.LoadFromDirectory(
                System.IO.Path.GetDirectoryName(typeof(TokenSpire2ModuleInit).Assembly.Location) ?? ".")
                .Enabled;
            if (brokerActive)
            {
                if (_debugFrame % 300 == 0)
                    MainFile.Logger?.Info($"[AutoSlay] MP State={_state}, Screen={mpScreen}, Delay={mpDelay}");
                return mpDelay > 0 ? mpDelay : 1.0;
            }

            return mpDelay > 0 ? mpDelay : 1.0;
        }
```

- [ ] **Step 2: Delete HandleMultiplayerMainMenu method**

Delete the entire `HandleMultiplayerMainMenu(Control mainMenu)` method (lines 2461-2684) and the associated `_mpHostCharSelectEnteredAt` field and `MP_HOST_EMBARK_DELAY` constant.

Remove these field declarations (search and delete):
```csharp
private DateTime _mpHostCharSelectEnteredAt = DateTime.MinValue;
private const double MP_HOST_EMBARK_DELAY = 60.0;
```

- [ ] **Step 3: Add using directive at top of file**

Add:
```csharp
using TokenSpire2.Multiplayer;
using TokenSpire2.Core;
```

- [ ] **Step 4: Build and verify**

```bash
cd "E:/SteamLibrary/steamapps/common/Slay the Spire 2/mods/TokenSpire2" && dotnet build -c Release 2>&1 | tail -5
```

Expected: 0 errors. AutoSlayNode._Process is now clean.

---

### Task 7: Simplify BrokerClientJoinFlow — remove join request from handshake

**Files:**
- Modify: `src/Coop/Runtime/BrokerClientJoinFlow.cs`

**Interfaces:**
- Consumes: `BrokerModeSettings`, `IBrokerEnvelopeTransport`, `BrokerNetGameService`, `BrokerPendingNetGameServiceRegistry`
- Produces: `BeginStandardBrokerJoinAsync` that does NOT send ClientLobbyJoinRequestMessage

- [ ] **Step 1: Rewrite BeginStandardBrokerJoinAsync**

In `src/Coop/Runtime/BrokerClientJoinFlow.cs`, replace the `BeginStandardBrokerJoinAsync` method (lines 104-193):

```csharp
    /// <summary>
    /// Executes the broker join SETUP only:
    ///   1. Create broker transport + service
    ///   2. Wait for host's InitialGameInfoMessage
    ///   3. Store service in BrokerPendingNetGameServiceRegistry
    ///   4. Return JoinResult
    ///
    /// DOES NOT send ClientLobbyJoinRequestMessage.
    /// The game's InitializeMultiplayerAsClient sends the ONE AND ONLY
    /// join request through BrokerBackedNetService.
    ///
    /// This is the SINGLE-CODE-PATH fix for the 3-player lobby bug.
    /// </summary>
    public static async Task<JoinResult> BeginStandardBrokerJoinAsync(
        BrokerModeSettings settings,
        Func<IBrokerEnvelopeTransport> createTransport,
        Action<string>? log,
        CancellationToken cancellationToken)
    {
        if (!ShouldUseBrokerJoin(settings))
        {
            throw new InvalidOperationException("Broker join was requested while broker mode is disabled.");
        }

        var inner = BrokerNetServiceFactory.TryCreate(
            settings,
            createTransport(),
            log,
            BrokerClientRole.Client) ?? throw new InvalidOperationException("Broker client service could not be created.");
        var service = new BrokerNetGameService(inner, NetGameType.Client);
        var initialInfoSource = new TaskCompletionSource<InitialGameInfoMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        Action<InitialGameInfoMessage> initialInfoHandler = initialInfo => initialInfoSource.TrySetResult(initialInfo);
        using var cancellationRegistration = cancellationToken.Register(
            state => ((TaskCompletionSource<InitialGameInfoMessage>)state!).TrySetCanceled(cancellationToken),
            initialInfoSource);

        inner.RegisterMessageHandler(initialInfoHandler);
        try
        {
            log?.Invoke($"Broker client join flow: waiting for host initial game info clientId={settings.ClientId}.");
            while (!initialInfoSource.Task.IsCompleted)
            {
                service.Update();
                await Task.Delay(16, cancellationToken).ConfigureAwait(false);
            }

            var initialInfo = await initialInfoSource.Task.ConfigureAwait(false);
            ThrowIfInitialGameInfoRejected(initialInfo);
            log?.Invoke($"Broker client join flow: received host initial game info clientId={settings.ClientId}.");

            // IMPORTANT: Do NOT send ClientLobbyJoinRequestMessage here.
            // The game's InitializeMultiplayerAsClient will send the ONE
            // join request through BrokerBackedNetService.
            // This prevents the 3-player lobby bug (duplicate join requests).

            service.Update();
            BrokerPendingNetGameServiceRegistry.Store(settings.ClientId, service);
            log?.Invoke($"Broker client join flow: setup complete, service stored. " +
                $"Join request will be sent by InitializeMultiplayerAsClient. clientId={settings.ClientId}.");

            return new JoinResult
            {
                gameMode = initialInfo.gameMode,
                sessionState = initialInfo.sessionState,
                joinResponse = null // join response comes later, from InitializeMultiplayerAsClient
            };
        }
        catch
        {
            service.Dispose();
            throw;
        }
        finally
        {
            inner.UnregisterMessageHandler(initialInfoHandler);
        }
    }
```

- [ ] **Step 2: Remove unused createJoinRequest parameter from method signature**

The `Func<ClientLobbyJoinRequestMessage>? createJoinRequest = null` parameter was already removed in the new signature above.

- [ ] **Step 3: Build and verify**

```bash
cd "E:/SteamLibrary/steamapps/common/Slay the Spire 2/mods/TokenSpire2" && dotnet build -c Release 2>&1 | tail -5
```

---

### Task 8: Simplify BrokerBackedNetService — remove duplicate join suppression

**Files:**
- Modify: `src/Coop/Runtime/BrokerBackedNetService.cs`

**Interfaces:**
- Consumes: Nothing changed externally
- Produces: `SendMessageAsync` forwards join requests normally (no duplicate suppression)

- [ ] **Step 1: Remove duplicate join suppression from SendMessageAsync**

In `src/Coop/Runtime/BrokerBackedNetService.cs`, replace the `SendMessageAsync` method (lines 128-165):

```csharp
    public async Task SendMessageAsync<T>(T message, ulong? targetPlayerId, CancellationToken cancellationToken)
    {
        // No more duplicate join suppression needed.
        // BeginStandardBrokerJoinAsync no longer sends the join request —
        // InitializeMultiplayerAsClient is the ONE AND ONLY path.
        var targetClientId = targetPlayerId is null ? null : PlayerIdToClientId(targetPlayerId.Value);
        var envelope = BrokerEnvelopeMessageSerializer.ToEnvelope(
            _sessionId,
            _clientId,
            targetClientId,
            message,
            Interlocked.Increment(ref _sequence));
        TrackKnownPeers(envelope);
        _log?.Invoke($"Broker outbound: sessionId={envelope.SessionId} source={envelope.SourceClientId} target={envelope.TargetClientId ?? "broadcast"} messageType={envelope.MessageType} sequence={envelope.Sequence}.");
        await _transport.SendEnvelopeAsync(envelope, cancellationToken);
    }
```

- [ ] **Step 2: Remove stale fields**

Remove the `_joinRequestSent` and `_stashedJoinResponse` fields (lines 22-23):

```csharp
// DELETE these lines:
private int _joinRequestSent;
private ClientLobbyJoinResponseMessage? _stashedJoinResponse;
```

- [ ] **Step 3: Remove StashJoinResponse method**

Delete `StashJoinResponse` method (lines 122-126).

- [ ] **Step 4: Remove stale cleanup in Disconnect**

In `Disconnect()` (line 108-113), remove the reset of `_joinRequestSent` and `_stashedJoinResponse`:

```csharp
    public void Disconnect()
    {
        IsConnected = false;
        // _joinRequestSent and _stashedJoinResponse removed — no longer needed
    }
```

- [ ] **Step 5: Build and verify**

```bash
cd "E:/SteamLibrary/steamapps/common/Slay the Spire 2/mods/TokenSpire2" && dotnet build -c Release 2>&1 | tail -5
```

---

### Task 9: Simplify BrokerClientJoinFlowPatch — remove guards

**Files:**
- Modify: `src/Coop/Patches/BrokerClientJoinFlowPatch.cs`

**Interfaces:**
- Consumes: `BrokerClientJoinFlow.BeginStandardBrokerJoinAsync` (simplified), `BrokerModeSettings`
- Produces: Simpler Prefix — no cached result, no concurrent guard

- [ ] **Step 1: Replace BrokerClientJoinFlowPatch.Prefix**

Replace the `Prefix` method + all static fields:

```csharp
    public static bool Prefix(ref Task<JoinResult> __result)
    {
        var settings = LoadSettings();
        if (!BrokerClientJoinFlow.ShouldUseBrokerJoin(settings))
        {
            MainFile.Logger.Info("[BrokerClientJoinFlowPatch] ShouldUseBrokerJoin=false — letting original JoinFlow.Begin proceed.");
            return true;
        }

        if (settings.Config?.Role != BrokerClientRole.Client)
        {
            MainFile.Logger.Info($"[BrokerClientJoinFlowPatch] Not client (role={settings.Config?.Role}) — skipping.");
            return true;
        }

        MainFile.Logger.Info("[BrokerClientJoinFlowPatch] Intercepting JoinFlow.Begin for broker setup (no join request sent).");
        var log = new BrokerEventLog(settings.EventLogPath);
        var transport = BrokerEnvelopeTransportConnector.ConnectBlocking(
            BrokerLobbyServiceSubstitution.CreateRegistrationConfig(settings, BrokerClientRole.Client),
            settings.ClientId,
            TimeSpan.FromSeconds(3),
            CancellationToken.None);

        __result = BrokerClientJoinFlow.BeginStandardBrokerJoinAsync(
            settings,
            () => transport,
            log.Write,
            CancellationToken.None);

        return false;
    }
```

- [ ] **Step 2: Remove old static fields**

Delete:
```csharp
private static int _joinInProgress;
private static JoinResult? _cachedJoinResult;
```

- [ ] **Step 3: Remove ResetJoinGuard method**

Delete the `ResetJoinGuard()` method entirely.

- [ ] **Step 4: Remove BrokerConnectTimeout field and CreateTransport method**

The `BrokerConnectTimeout` field and `CreateTransport` method are no longer needed (transport creation is now inline in Prefix).

- [ ] **Step 5: Build and verify**

```bash
cd "E:/SteamLibrary/steamapps/common/Slay the Spire 2/mods/TokenSpire2" && dotnet build -c Release 2>&1 | tail -5
```

---

### Task 10: Simplify BrokerForceLobbyTransitionPatch

**Files:**
- Modify: `src/Coop/Patches/BrokerForceLobbyTransitionPatch.cs`

**Interfaces:**
- Consumes: `BrokerModeSettings`
- Produces: Lightweight Postfix — calls BeginRunForAllPlayers only when Host, with reflection fallback

- [ ] **Step 1: Replace AreAllPlayersReady with simpler approach**

Replace the entire `AreAllPlayersReady` method (lines 126-186) and `ForceBeginRunForAllPlayers` (lines 188-247):

The reflection-based check is fragile. Replace with a simpler approach:

```csharp
    private static bool AreAllPlayersReady(object lobby, Action<string>? log)
    {
        // Try the common property names first
        var type = lobby.GetType();
        foreach (var name in new[] { "AreAllPlayersReady", "AllPlayersReady", "IsEveryoneReady", "AllReady" })
        {
            var prop = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (prop?.GetValue(lobby) is bool val)
            {
                log?.Invoke($"Broker lobby transition: {name} = {val}.");
                return val;
            }
            var method = type.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (method?.Invoke(lobby, null) is bool mVal)
            {
                log?.Invoke($"Broker lobby transition: {name}() = {mVal}.");
                return mVal;
            }
        }

        // Fallback: count ready players from _players field
        var playersField = FindField(type, "_players", "_lobbyPlayers", "_playerSlots", "_slots");
        var playersList = playersField?.GetValue(lobby);
        if (playersList is System.Collections.IEnumerable enumerable)
        {
            int total = 0, ready = 0;
            foreach (var player in enumerable)
            {
                if (player is null) continue;
                total++;
                var readyProp = player.GetType().GetProperty("IsReady",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (readyProp?.GetValue(player) is true) ready++;
            }
            log?.Invoke($"Broker lobby transition: {ready}/{total} players ready.");
            return total >= 2 && ready >= total;
        }

        log?.Invoke("Broker lobby transition: unable to determine ready state — returning false.");
        return false;
    }
```

- [ ] **Step 2: Build and verify**

```bash
cd "E:/SteamLibrary/steamapps/common/Slay the Spire 2/mods/TokenSpire2" && dotnet build -c Release 2>&1 | tail -5
```

---

### Task 11: Final build + deploy

- [ ] **Step 1: Full clean build**

```bash
cd "E:/SteamLibrary/steamapps/common/Slay the Spire 2/mods/TokenSpire2" && dotnet build -c Release 2>&1
```

Expected: 0 errors. Warnings may remain from pre-existing code.

- [ ] **Step 2: Verify DLL deployed**

```bash
ls -la "E:/SteamLibrary/steamapps/common/Slay the Spire 2/mods/TokenSpire2/TokenSpire2.dll"
```

- [ ] **Step 3: Verify single-player regression**

Set `coop_config.json` to `"CoopMode": false`. Launch game. Verify:
- Auto-battle clicks "Singleplayer" → picks character → embarks
- Combat cards are played
- No MpController log lines appear (CoopMode path not entered)

- [ ] **Step 4: Test multiplayer LAN flow**

Set up `coop_config.json` with `"CoopMode": true`, both marker files present. Launch via `launch_lan.ps1`. Verify:
- Host creates room
- Client joins → EXACTLY 2 players in lobby
- No "duplicate join request" in logs
- Both players ready → game transitions to combat
