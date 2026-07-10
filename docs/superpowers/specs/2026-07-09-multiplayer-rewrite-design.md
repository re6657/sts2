# TokenSpire2 Multiplayer Rewrite — Design Spec

**Date**: 2026-07-09
**Status**: Approved
**Scope**: Multiplayer orchestration layer only (MpController / MpJoinFlow / MpLobbyCoordinator / MpScreenHandler)

---

## 1. Background

The existing TokenSpire2 mod has 6 documented fundamental architecture problems (COMPREHENSIVE_README.md §7):

1. **Linear state machine** in `HandleMultiplayerMainMenu` — assumes "click button → next screen appears", causing infinite click-loops when transitions take time
2. **Duplicate join requests** — `ClientLobbyJoinRequestMessage` sent from TWO code paths → 3 players in lobby
3. **Scattered `!IsCoopMode` guards** — copypasted across AutoSlayNode (~12 locations)
4. **Fragile reflection** — `BrokerForceLobbyTransitionPatch.AreAllPlayersReady` uses reflection, silently breaking on game updates
5. **Timing dependencies** — Host waits hard-coded 60s for client with no heartbeat
6. **4200-line monolithic controller** — AutoSlayNode.cs does everything

This spec covers a rewrite of the **multiplayer orchestration layer only**. The single-player auto-battle solver algorithms (IroncladSolver, CardRewardDecider, MapDecider, etc.) are battle-tested and NOT modified.

## 2. Architecture

### 2.1 Integration Point

```
AutoSlayNode._Process (delta)
  ├── if (CoopMode) → MpController.Update()        ← NEW: replaces HandleMultiplayerMainMenu()
  ├── else → existing single-player logic            ← UNCHANGED
```

`AutoBattleController._Process` remains gated behind `NewArchitectureEnabled = false` and does not participate in this rewrite.

### 2.2 Component Diagram

```
┌─────────────────────────────────────────────────────┐
│ MpController (event-driven state machine)            │
│                                                      │
│  _Process:                                           │
│    1. ScreenDetector.Detect() → current GameScreen   │
│    2. GameScreen → inferred MpState                  │
│    3. If state changed → reset timeout               │
│    4. If state timeout → recover (backtrack, retry)  │
│    5. Dispatch to state handler                      │
│    6. Heartbeat check                                │
│                                                      │
│  Uses:                                               │
│    ├── MpScreenHandler  (UI button clicking)         │
│    ├── MpJoinFlow       (single join path)           │
│    └── MpLobbyCoordinator (message-driven lobby)     │
└─────────────────────────────────────────────────────┘
```

### 2.3 MpState Enum

```
Inactive → MainMenu → EnteringMultiplayer → HostSubmenu/Joining
  → CharacterSelect → InLobby → WaitingForGame → InGame
```

State is **inferred from actual screen detection**, not from "what we just clicked".

## 3. Component Designs

### 3.1 MpController — Event-Driven State Machine

```csharp
public class MpController
{
    MpState _state;
    double _stateEnteredAt;
    int _recoveryAttempts;

    public double Update(double delta)
    {
        var screen = ScreenDetector.Detect();
        var actualState = ScreenToMpState(screen);

        // State change? Reset timeout
        if (actualState != _state)
        {
            _state = actualState;
            _stateEnteredAt = 0; // reset timer
            _recoveryAttempts = 0;
        }

        // Timeout? Recover
        if (TimedOut(_state, _stateEnteredAt))
        {
            if (_recoveryAttempts >= 3)
            {
                Log("Max recovery attempts reached. Returning to main menu.");
                return RecoverToMainMenu();
            }
            _recoveryAttempts++;
            return Recover(_state);
        }

        return _state switch
        {
            MpState.MainMenu => ClickMultiplayer(),
            MpState.EnteringMultiplayer => Wait(1.0),
            MpState.HostSubmenu => _isHost ? ClickHostGame() : Wait(0.5),
            MpState.Joining => ClickJoinFriends(),
            MpState.CharacterSelect => SelectCharacter(),
            MpState.InLobby => HandleLobby(),
            MpState.WaitingForGame => WaitWithHeartbeat(),
            MpState.InGame => 0.0, // combat handler takes over
            _ => 0.5
        };
    }

    MpState ScreenToMpState(GameScreen screen) { /* pure mapping */ }
    bool TimedOut(MpState state, double elapsed);
    double Recover(MpState state);
}
```

**Key difference from v1**: Never assumes "click → next state". Always detects actual screen first.

### 3.2 MpJoinFlow — Single Join Path

`BeginStandardBrokerJoinAsync` is simplified to ONLY handle broker connection setup:

```
Responsibilities:
  1. Create TCP connection to BrokerServer (127.0.0.1:9999)
  2. Create BrokerEnvelopeTransport on top of the connection
  3. Wait for InitialGameInfoMessage (lobby metadata)
  4. Register BrokerBackedNetService in BrokerRegistry
  5. Return JoinResult with { accepted = true }
     (true = broker handshake complete, NOT lobby join complete)

DOES NOT SEND ClientLobbyJoinRequestMessage.
```

The ONE AND ONLY join request is sent by the game's native flow:
```
InitializeMultiplayerAsClient
  → BrokerBackedNetService.SendMessageAsync(ClientLobbyJoinRequestMessage)
  → BrokerEnvelopeTransport.SendEnvelopeAsync
  → BrokerClientConnection (TCP → Broker → Host)
```

Concurrency guard:
```csharp
if (Interlocked.CompareExchange(ref _joinInProgress, 1, 0) != 0)
    return _cachedResult; // already joining, return cached
```

### 3.3 MpLobbyCoordinator — Message-Driven Lobby

Replaces `BrokerForceLobbyTransitionPatch`'s reflection-based AreAllPlayersReady.

Subscribes to broker message stream:
```
On ClientLobbyJoinResponseMessage (Client side):
  accepted=true  → transition to CharacterSelect
  accepted=false → retry after 1s

On LobbyPlayerSetReadyMessage (Host side):
  record player ready
  all slots ready? → StartRunLobby.BeginRunForAllPlayers()

On BeginRunForAllPlayers call (both sides):
  transition to InGame
```

Timeouts:
| Wait | Default | On Timeout |
|---|---|---|
| Host waiting for Client join | 120s | Return to MultiplayerSubmenu, re-host |
| Host waiting for Client ready | 60s | Send reminder; after +30s → return to lobby |
| Client waiting for Host embark | 120s | Return to lobby, re-join |

### 3.4 Heartbeat Protocol

New message type: `{"type":"heartbeat","ts":"<utc_ticks>","sender":"client"}`

```
Client: sends heartbeat every 5 seconds
Host:   checks every 10 seconds
        last heartbeat > 30s ago → warning log
        last heartbeat > 60s ago → cancel wait, return to lobby
```

BrokerServer is a pure TCP relay — it does not process heartbeat content.

Edge cases handled:
- Client crashes during character select → Host 60s timeout → return to lobby
- Client crashes mid-combat → Host continues solo (CoopMode keeps StuckDetector.NeverKill)
- Host crashes → Client heartbeat timeout → return to main menu

## 4. File Changes

| File | Action | Description |
|---|---|---|
| `src/Multiplayer/MpController.cs` | **Rewrite** | Event-driven state machine with timeout recovery |
| `src/Multiplayer/MpJoinFlow.cs` | **Rewrite** | Single join path; no join request in BeginStandardBrokerJoinAsync |
| `src/Multiplayer/MpLobbyCoordinator.cs` | **Rewrite** | Message-driven lobby lifecycle + heartbeat |
| `src/Multiplayer/MpScreenHandler.cs` | **Rewrite** | UI operations: ClickButton, SelectCharacter, PressEscape, WaitForSignal |
| `src/AutoSlayNode.cs` | **Modify** | Replace HandleMultiplayerMainMenu() body with MpController.Update() call |
| `src/Coop/Patches/BrokerClientJoinFlowPatch.cs` | **Modify** | Remove join-request send from BeginStandardBrokerJoinAsync |
| `src/Coop/Patches/BrokerForceLobbyTransitionPatch.cs` | **Simplify** | Remove reflection-based AreAllPlayersReady; delegate to MpLobbyCoordinator |

**NOT modified**:
- `src/Solver/*` — All solver algorithms preserved as-is
- `src/Handlers/*` — All single-player handlers preserved as-is
- `src/AutoBattle/*` — New architecture skeleton preserved as-is (still gated)
- `src/Core/*` — Foundation types preserved (minor additions for new message types OK)
- `BrokerServer/*` — TCP relay server unchanged

## 5. v1 Anti-Patterns → v2 Solutions Summary

| v1 Anti-Pattern | v2 Solution |
|---|---|
| Linear button-clicking (assumes click → next screen) | Event-driven: detect screen → infer state → act |
| Join request sent from 2 paths | MpJoinFlow: only InitializeMultiplayerAsClient sends join |
| Reflection-based ready detection | MpLobbyCoordinator: listen for LobbyPlayerSetReadyMessage |
| Hard-coded 60s delay | Heartbeat + configurable per-step timeouts |
| No error recovery (infinite loops) | 3-attempt recovery per state; fallback to main menu |
| 4200-line monolithic controller (AutoSlayNode mix of SP + MP) | MP extracted to focused MpController |

## 6. Verification

### Build
```
dotnet build -c Release   # must pass with 0 errors
```

### Single-player regression test
```
coop_config.json: {"CoopMode":false, "AutoBattleEnabled":true}
→ Verify: game starts, auto-battle works normally
→ Verify: AutoSlayNode._Process takes non-CoOp path (unchanged)
```

### Multiplayer LAN test
```powershell
.\launch_lan.ps1
→ Verify: BrokerServer starts
→ Verify: Host creates room, Client joins → EXACTLY 2 players (not 3)
→ Verify: Both players ready → game transitions to combat
→ Verify: Client heartbeat visible in logs
→ Verify: No "Join already completed" loop in client logs
→ Verify: Force-kill Client → Host detects heartbeat timeout → returns to lobby
```
