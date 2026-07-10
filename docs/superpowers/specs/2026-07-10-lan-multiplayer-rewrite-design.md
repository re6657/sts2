# LAN Multiplayer Rewrite — Simplified Broker Design

**Date**: 2026-07-10
**Status**: Design approved — awaiting implementation plan
**Scope**: Single machine, dual instance, human host + bot client, T-key auto-battle toggle

---

## 1. Architecture Overview

### Core Insight

TCP Broker transport layer (BrokerServer.exe on 127.0.0.1:9999) is proven reliable. The problems are:
- Too many Harmony patches (29) fighting each other
- Client join flow bypasses the friend list with fragile reflection
- Virtual friend injection (Direction D) doesn't work — the game's Join button never enumerates SteamFriends API

### Approach

Delete 17 unnecessary patches (59%), keep 20 core patches, add 1 new patch to bypass the friend list, and rewrite the client join path in MpController to remove 80 lines of reflection.

### Patch Inventory

| Category | Count | Action |
|----------|-------|--------|
| Transport bypass (Steam/ENet startup) | 4 | Keep |
| Lobby lifecycle (service substitution, transition, begin run) | 3 | Keep |
| Client join intercept (JoinFlow.Begin) | 1 | Keep |
| Dual-instance UI/input isolation | 12 | Keep |
| Virtual friend injection (Direction D) | 4 | **Delete** |
| Diagnostic logging (noise) | ~12 | **Delete** |
| Dead no-op code | 1 | **Delete** |
| **New: Skip friend list** | 1 | **Add** |

---

## 2. Client Join Flow

### Problem

Current `MpController.TriggerJoinFlowBegin()` (80 lines) does complex reflection: find `JoinFlow` type → find `Begin` method → `Activator.CreateInstance` → fallback to scene tree search → `Invoke`. The `BrokerClientJoinFlowPatch` already intercepts `JoinFlow.Begin` correctly — but getting there requires bypassing the empty friend list.

### Solution: BrokerSkipFriendListPatch

A single new Harmony patch that intercepts the Join button handler:

```
Bot clicks "Join" in multiplayer submenu
  → Game calls NMultiplayerSubmenu.OpenJoinFriendsScreen()
  → BrokerSkipFriendListPatch.Prefix intercepts
  → If broker client mode:
      new JoinFlow().Begin(PlaceholderClientConnectionInitializer, SceneTree)
      → BrokerClientJoinFlowPatch.Prefix intercepts Begin()
      → TCP handshake with host via BrokerServer
      → Returns JoinResult → game transitions to Character Select
  → Else: normal friend list (unchanged)
```

### MpController Simplification

`HandleEnteringMultiplayer` client path reduces to button clicks only:

```csharp
// All that's needed:
if (_ui.ClickButton("Join"))     return 2.0;
if (_ui.ClickButton("加入"))     return 2.0;
```

### Code Removed

- `TriggerJoinFlowBegin()` — 80 lines of reflection
- `FindJoinFlowInstance()` — static property + scene tree search
- `FindNodeOfTypeRecursive()` — recursive Godot node search
- `_brokerJoinTriggered`, `_brokerJoinDone`, `_brokerJoinFlowInvoked` state flags

---

## 3. Combat Stability (Fixes A-E)

Five watchdog fixes in `AutoSlayNode.cs` to prevent deadlocks:

| Fix | Deadlock State | Recovery |
|-----|---------------|----------|
| A | `_combatTurnRequested=true` but `PlayerActionsDisabled` stays false | 5s timeout → retry EndTurn, reset state |
| B | Zero progress for 120s in co-op (safety net was disabled) | Co-op-safe stuck detection → force re-solve |
| C | `TryManualPlay` causes RNG divergence (local-first, sync-later) | Replace with UI ForceClick → game input pipeline |
| D | `ClickEndTurnButton` succeeds but turn doesn't end | Post-click verification, let Fix A handle failure |
| E | `PlayerActionsDisabled=true` persists >45s (waiting forever) | Watchdog → click End Turn to advance round |

No new Harmony patches needed — all fixes are in `AutoSlayNode.cs`.

---

## 4. Files Changed

### Deleted (9 files)
- `src/Coop/Patches/BrokerVirtualFriendSteamPatch.cs`
- `src/Coop/Patches/BrokerClientLobbyHandshakePatch.cs`
- `src/Coop/Patches/CharacterSelectInputDiagnosticsPatches.cs`
- `src/Coop/Patches/LobbyLifecycleDiagnosticsPatches.cs`
- `src/Coop/Patches/NetServiceDiagnosticsPatches.cs`
- `src/Coop/Patches/RewardDiagnosticsPatches.cs`
- `src/Coop/Patches/RunIdentityDiagnosticsPatches.cs`
- `src/Coop/Patches/RunIdentityLocalUiDiagnosticsPatches.cs`
- Other diagnostic patch files

### Added (1 file)
- `src/Coop/Patches/BrokerSkipFriendListPatch.cs` — intercepts Join button, redirects to broker join

### Modified (3 files)
- `src/Multiplayer/MpController.cs` — remove reflection, simplify to button clicks
- `src/Coop/Runtime/LocalCoopPatchInstaller.cs` — remove deleted patch registrations
- `src/AutoSlayNode.cs` — Fixes A-E combat stability watchdogs

### Unchanged (13 files)
- All transport bypass, lobby lifecycle, join intercept, and dual-role isolation patches

---

## 5. Verification

1. `dotnet build -c Release` — compile
2. Delete old logs: `rm localcoop-*.txt`
3. `.\launch_lan.ps1` — launch dual instances
4. Human operates Host: Multiplayer → Host Game → pick character → Ready
5. Bot auto-joins: clicks Join → skips friend list → broker handshake → Character Select → picks character → Ready
6. Combat: press T for auto-battle, observe 10+ rounds without freeze
7. Check logs: no `DEADLOCK DETECTED`, no `StateDivergenceMessage`

### Fallback

If `BrokerSkipFriendListPatch` fails, restore `TriggerJoinFlowBegin` (kept in a comment block). No other patches affected.
