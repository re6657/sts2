# LAN Multiplayer Rewrite — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Rewrite LAN multiplayer join flow: remove 17 broken/noise patches, replace MpController reflection with a single clean patch, add 5 combat stability fixes.

**Architecture:** TCP Broker transport is proven. The fix is: (1) rewrite `BrokerJoinFriendScreenPatch` (currently decommissioned) to correctly create a JoinFlow instance and call the instance method `Begin()`, (2) strip 80 lines of reflection from `MpController`, (3) remove virtual friend and diagnostic patches, (4) apply Fixes A-E in `AutoSlayNode.cs`.

**Tech Stack:** C# 12, Harmony 2.x, Godot 4.x Mono, .NET 8

## Global Constraints

- Single machine, two game instances (one human host + one bot client)
- Bot auto-plays with T-key toggle (`AutoBattleEnabled` in `coop_config.json`)
- Marker files (`enable-local-broker-host.txt`, `enable-local-broker-client.txt`) distinguish instances
- `TOKENSPIRE2_ROLE` env var: `host` or `client`
- BrokerServer.exe on 127.0.0.1:9999
- All broker patches use Harmony ID `localcoop.transport-broker`
- Mod directory: `E:\SteamLibrary\steamapps\common\Slay the Spire 2\mods\TokenSpire2`
- Build: `dotnet build -c Release` in mod directory
- Deploy: copy `bin/Release/net8.0/TokenSpire2.dll` to both `mods/TokenSpire2.dll` and `mods/TokenSpire2/TokenSpire2.dll`

---

## File Structure

```
src/
├── Coop/
│   ├── Patches/
│   │   ├── BrokerJoinFriendScreenPatch.cs    ← REWRITE (was decommissioned, now reactivated)
│   │   ├── BrokerVirtualFriendSteamPatch.cs  ← DELETE
│   │   ├── BrokerClientLobbyHandshakePatch.cs ← DELETE
│   │   ├── CharacterSelectInputDiagnosticsPatches.cs ← DELETE
│   │   ├── LobbyLifecycleDiagnosticsPatches.cs       ← DELETE
│   │   ├── NetServiceDiagnosticsPatches.cs           ← DELETE
│   │   ├── RewardDiagnosticsPatches.cs               ← DELETE
│   │   ├── RunIdentityDiagnosticsPatches.cs          ← DELETE
│   │   ├── RunIdentityLocalUiDiagnosticsPatches.cs   ← DELETE
│   │   ├── BrokerClientJoinFlowPatch.cs    ← KEEP (unchanged)
│   │   ├── BrokerHostStartupBypassPatches.cs ← KEEP (unchanged)
│   │   ├── BrokerClientStartupBypassPatches.cs ← KEEP (unchanged)
│   │   ├── BrokerLobbyServiceSubstitutionPatch.cs ← KEEP (unchanged)
│   │   ├── BrokerForceLobbyTransitionPatch.cs ← KEEP (unchanged)
│   │   ├── BrokerBeginRunPatch.cs          ← KEEP (unchanged)
│   │   └── (all DualRole patches)          ← KEEP (unchanged)
│   └── Runtime/
│       └── LocalCoopPatchInstaller.cs      ← MODIFY (remove deleted types, add reactivated one)
├── Multiplayer/
│   └── MpController.cs                     ← MODIFY (remove reflection, simplify client path)
└── AutoSlayNode.cs                         ← MODIFY (Fixes A-E watchdogs)
```

---

### Task 1: Rewrite BrokerJoinFriendScreenPatch — intercept Join button, trigger broker join

**Files:**
- Modify: `src/Coop/Patches/BrokerJoinFriendScreenPatch.cs` (complete rewrite)

**Interfaces:**
- Consumes: `BrokerClientJoinFlow.PlaceholderClientConnectionInitializer` (from `BrokerClientJoinFlow.cs`), `AccessTools.TypeByName`, `AccessTools.Method` (Harmony), `AppConfig.Instance.BrokerEnabled`, `AppConfig.Instance.IsHost`
- Produces: `BrokerJoinFriendScreenPatch.Prefix()` — intercepts `NMultiplayerSubmenu.OpenJoinFriendsScreen`, returns `false` (skip friend list) in broker client mode

This replaces the decommissioned patch that had the static-call bug (`Invoke(null, ...)` on an instance method). The fix: create a JoinFlow instance with `Activator.CreateInstance`, then call the instance method.

- [ ] **Step 1: Write the replacement BrokerJoinFriendScreenPatch**

Overwrite `src/Coop/Patches/BrokerJoinFriendScreenPatch.cs`:

```csharp
using System.Reflection;
using HarmonyLib;
using LocalCoop.Mod.Runtime;
using MegaCrit.Sts2.Core.Multiplayer.Connection;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using TokenSpire2;

namespace LocalCoop.Mod.Patches;

/// <summary>
/// Intercepts the Join button on the multiplayer submenu.
/// In broker client mode: creates a JoinFlow instance and calls Begin()
/// directly — bypassing the empty Steam friend list.
/// BrokerClientJoinFlowPatch.Prefix intercepts JoinFlow.Begin and performs
/// the TCP broker handshake → scene transition to Character Select.
///
/// In normal mode: lets OpenJoinFriendsScreen run (shows Steam friend list).
/// </summary>
[HarmonyPatch]
public static class BrokerJoinFriendScreenPatch
{
    public static MethodBase? TargetMethod()
    {
        try
        {
            var type = AccessTools.TypeByName(
                "MegaCrit.Sts2.Core.Nodes.Screens.MainMenu.NMultiplayerSubmenu");
            if (type is null)
            {
                Log("[BrokerJoinFriendScreenPatch] NMultiplayerSubmenu type NOT FOUND.");
                return null;
            }

            Log($"[BrokerJoinFriendScreenPatch] NMultiplayerSubmenu type found: {type.FullName}");

            var method = AccessTools.Method(type, "OpenJoinFriendsScreen");
            if (method is null)
            {
                Log("[BrokerJoinFriendScreenPatch] OpenJoinFriendsScreen NOT FOUND.");
                return null;
            }

            var parms = method.GetParameters();
            Log($"[BrokerJoinFriendScreenPatch] Found OpenJoinFriendsScreen(" +
                $"{string.Join(", ", parms.Select(p => $"{p.ParameterType.Name} {p.Name}"))}).");
            return method;
        }
        catch (Exception ex)
        {
            Log($"[BrokerJoinFriendScreenPatch] TargetMethod error: {ex.Message}");
            return null;
        }
    }

    public static bool Prefix()
    {
        if (!ShouldIntercept())
            return true; // normal flow: show friend list

        MainFile.Logger.Info(
            "[BrokerJoinFriendScreenPatch] Broker client detected. " +
            "Skipping friend list, triggering broker join via JoinFlow.Begin...");

        try
        {
            // Resolve types via AccessTools (cross-assembly-load-context safe)
            var joinFlowType = AccessTools.TypeByName(
                "MegaCrit.Sts2.Core.Multiplayer.Game.JoinFlow");
            var initializerType = AccessTools.TypeByName(
                "MegaCrit.Sts2.Core.Multiplayer.Connection.IClientConnectionInitializer");
            var sceneTreeType = AccessTools.TypeByName("Godot.SceneTree");

            if (joinFlowType is null || initializerType is null || sceneTreeType is null)
            {
                MainFile.Logger.Error(
                    "[BrokerJoinFriendScreenPatch] Types not found: " +
                    $"JoinFlow={joinFlowType != null}, " +
                    $"IClientConnectionInitializer={initializerType != null}, " +
                    $"SceneTree={sceneTreeType != null}");
                return false; // skip friend list even on failure (no friends to show)
            }

            var beginMethod = AccessTools.Method(
                joinFlowType, "Begin", new[] { initializerType, sceneTreeType });
            if (beginMethod is null)
            {
                MainFile.Logger.Error(
                    "[BrokerJoinFriendScreenPatch] JoinFlow.Begin NOT FOUND.");
                return false;
            }

            // Create JoinFlow INSTANCE (Begin is an instance method)
            var initializer = new BrokerClientJoinFlow
                .PlaceholderClientConnectionInitializer();
            var sceneTree = (Godot.SceneTree)Godot.Engine.GetMainLoop();

            object joinFlowInstance;
            try
            {
                joinFlowInstance = Activator.CreateInstance(joinFlowType);
            }
            catch (MissingMethodException)
            {
                MainFile.Logger.Error(
                    "[BrokerJoinFriendScreenPatch] JoinFlow has no parameterless " +
                    "constructor. Cannot create instance.");
                return false;
            }

            // Invoke — BrokerClientJoinFlowPatch.Prefix intercepts this call,
            // performs TCP broker handshake, stores service in registry.
            // The game's multiplayer state machine detects the stored service
            // and transitions to Character Select.
            beginMethod.Invoke(joinFlowInstance,
                new object[] { initializer, sceneTree });

            MainFile.Logger.Info(
                "[BrokerJoinFriendScreenPatch] JoinFlow.Begin invoked — " +
                "broker handshake in progress.");
        }
        catch (Exception ex)
        {
            MainFile.Logger.Error(
                $"[BrokerJoinFriendScreenPatch] Failed: {ex.Message}");
        }

        return false; // skip original OpenJoinFriendsScreen
    }

    private static bool ShouldIntercept()
    {
        try
        {
            if (!TokenSpire2.Core.AppConfig.IsInitialized)
                return false;
            var cfg = TokenSpire2.Core.AppConfig.Instance;
            return cfg.BrokerEnabled && !cfg.IsHost;
        }
        catch { return false; }
    }

    private static void Log(string msg)
    {
        try { System.Console.WriteLine(msg); } catch { }
    }
}
```

---

### Task 2: Simplify MpController — remove reflection, keep button clicks only

**Files:**
- Modify: `src/Multiplayer/MpController.cs`

**Interfaces:**
- Consumes: `AppConfig.Instance.IsHost`, `AppConfig.Instance.BrokerEnabled`
- Produces: `HandleEnteringMultiplayer()` — simplified client path (only button clicks)
- Removes: `TriggerJoinFlowBegin()`, `FindJoinFlowInstance()`, `FindNodeOfTypeRecursive()`, state flags `_brokerJoinTriggered`, `_brokerJoinDone`, `_brokerJoinFlowInvoked`

- [ ] **Step 1: Remove unused using directives**

At line 1-3, remove `System.Linq` and `System.Reflection` and `HarmonyLib` (no longer needed after removing reflection code):

```csharp
using System;
using TokenSpire2.Core;
```

- [ ] **Step 2: Simplify HandleEnteringMultiplayer — client path**

Replace lines 162-231 (the entire `HandleEnteringMultiplayer` method) with:

```csharp
    private bool _hostButtonInvoked;

    private double HandleEnteringMultiplayer()
    {
        var cfg = AppConfig.Instance;
        if (cfg.IsHost)
        {
            if (_ui.ClickButton("Host"))     return 2.0;

            // If button click doesn't navigate, directly invoke OnHostPressed
            if (!_hostButtonInvoked)
            {
                _hostButtonInvoked = true;
                if (TryInvokeSteamHostButtonPressed())
                {
                    Log("[MpController] Directly invoked OnHostPressed.");
                    return 3.0;
                }
            }

            if (_ui.ClickButton("Create"))   return 2.0;
            if (_ui.ClickButton("创建"))     return 2.0;
            if (_ui.ClickButton("主持"))     return 2.0;
        }
        else // IsClient — broker mode
        {
            // Simply click the Join button. BrokerJoinFriendScreenPatch
            // intercepts OpenJoinFriendsScreen, creates a JoinFlow, and
            // calls Begin() → BrokerClientJoinFlowPatch handles TCP handshake.
            // No reflection needed here.
            if (_ui.ClickButton("Join"))     return 2.0;
            if (_ui.ClickButton("加入"))     return 2.0;
            if (_ui.ClickButton("参与"))     return 2.0;
        }

        // One-time button dump on first failure
        if (!_mainMenuDumped)
        {
            _mainMenuDumped = true;
            _ui.DumpVisibleButtons();
            Log("[MpController] EnteringMultiplayer: no matching button — dumped all.");
        }
        return 2.0;
    }
```

- [ ] **Step 3: Remove `_brokerJoinTriggered` / `_brokerJoinDone` field declarations**

Replace lines 374-376:

```csharp
    private bool _mainMenuDumped;
```

(Remove `_brokerJoinTriggered` and `_brokerJoinDone` — keep only `_mainMenuDumped`)

- [ ] **Step 4: Remove `_brokerJoinFlowInvoked` field**

Replace line 161:

```csharp
    private bool _hostButtonInvoked;
```

(Remove `_brokerJoinFlowInvoked` and `_joinButtonInvoked` — keep only `_hostButtonInvoked`)

- [ ] **Step 5: Update state-change reset block**

At lines 58-72, update the state-change reset (remove references to deleted fields):

```csharp
        if (detected != _state)
        {
            Log($"[MpController] {_state} → {detected} (screen={screen})");
            _state = detected;
            _stateElapsed = 0;
            _recoveryAttempts = 0;
            _ui.ClearClickCache();
            _mainMenuDumped = false;
            _hostButtonInvoked = false;
        }
```

- [ ] **Step 6: Delete TriggerJoinFlowBegin, FindJoinFlowInstance, FindNodeOfTypeRecursive**

Remove lines 463-598 (all three methods: `TriggerJoinFlowBegin`, `FindJoinFlowInstance`, `FindNodeOfTypeRecursive`).

- [ ] **Step 7: Remove TryInvokeJoinButtonPressed (no longer needed)**

Remove lines 285-322 (`TryInvokeJoinButtonPressed` method — was used for old friend-list-direct-invoke approach).

- [ ] **Step 8: Simplify HandleFriendList — remove broker virtual-friend clicking**

Replace lines 393-461 (the `HandleFriendList` method) with:

```csharp
    private double HandleFriendList()
    {
        // In broker mode, we should never reach this screen —
        // BrokerJoinFriendScreenPatch intercepts OpenJoinFriendsScreen
        // before the friend list opens. If we're here, press escape to go back.
        if (AppConfig.Instance.BrokerEnabled)
        {
            Log("[MpController] Unexpectedly on friend list in broker mode — escaping.");
            _ui.PressEscape();
            return 2.0;
        }

        // SteamFix64 mode (non-broker): click friend/lobby entries
        if (_ui.ClickButton("JoinFriend"))  return 2.0;
        if (_ui.ClickButton("Friend"))      return 2.0;
        if (_ui.ClickButton("Lobby"))       return 2.0;
        if (_ui.ClickButton("JoinGame"))    return 2.0;
        if (_ui.ClickButton("Refresh"))     return 2.0;
        if (_ui.ClickButton("刷新"))         return 2.0;

        if (_ui.ClickFirstEnabledButton())
        {
            Log("[MpController] SteamFix64: clicked first enabled entry in friend list.");
            return 2.0;
        }

        if (!_mainMenuDumped)
        {
            _mainMenuDumped = true;
            _ui.DumpVisibleButtons();
            Log("[MpController] FriendList dump complete.");
        }

        Log("[MpController] No entries in friend list — escaping to retry.");
        _ui.PressEscape();
        return 2.0;
    }
```

---

### Task 3: Update LocalCoopPatchInstaller — register reactivated patch, remove deleted types

**Files:**
- Modify: `src/Coop/Runtime/LocalCoopPatchInstaller.cs`

**Interfaces:**
- Consumes: `BrokerJoinFriendScreenPatch` type
- Produces: Updated `BrokerNetworkPatchTypes` array

- [ ] **Step 1: Update BrokerNetworkPatchTypes array**

Replace lines 15-40 (the `BrokerNetworkPatchTypes` array) with:

```csharp
    public static readonly Type[] BrokerNetworkPatchTypes =
    [
        typeof(BrokerClientJoinFlowPatch),
        // BrokerJoinFriendScreenPatch intercepts OpenJoinFriendsScreen on the
        // multiplayer submenu. In broker client mode, it creates a JoinFlow
        // instance and calls Begin() directly, bypassing the empty Steam friend
        // list. BrokerClientJoinFlowPatch intercepts Begin() → broker handshake.
        typeof(BrokerJoinFriendScreenPatch),
        typeof(BrokerHostSteamStartupBypassPatch),
        typeof(BrokerHostENetStartupBypassPatch),
        typeof(BrokerClientSteamConnectBypassPatch),
        typeof(BrokerClientENetConnectBypassPatch),
        typeof(BrokerForceLobbyTransitionPatch),
        typeof(BrokerLobbyServiceSubstitutionPatch),
        typeof(BrokerBeginRunPatch)
    ];
```

This removes the 4 VF patches (`BrokerVFPatch_GetFriendCount`, `BrokerVFPatch_GetFriendByIndex`, `BrokerVFPatch_GetFriendPersonaName`, `BrokerVFPatch_GetFriendGamePlayed`) and adds `BrokerJoinFriendScreenPatch`.

- [ ] **Step 2: Update RunIdentityDiagnosticsPatchTypes — empty the array**

Replace lines 69-83 with:

```csharp
    private static readonly Type[] RunIdentityDiagnosticsPatchTypes =
    [
        // Diagnostics patches removed — they produced log noise without
        // functional benefit. Re-add specific patches here if debugging
        // requires them.
    ];
```

---

### Task 4: Delete obsolete patch files

**Files:**
- Delete: `src/Coop/Patches/BrokerVirtualFriendSteamPatch.cs`
- Delete: `src/Coop/Patches/BrokerClientLobbyHandshakePatch.cs`
- Delete: `src/Coop/Patches/CharacterSelectInputDiagnosticsPatches.cs`
- Delete: `src/Coop/Patches/LobbyLifecycleDiagnosticsPatches.cs`
- Delete: `src/Coop/Patches/NetServiceDiagnosticsPatches.cs`
- Delete: `src/Coop/Patches/RewardDiagnosticsPatches.cs`
- Delete: `src/Coop/Patches/RunIdentityDiagnosticsPatches.cs`
- Delete: `src/Coop/Patches/RunIdentityLocalUiDiagnosticsPatches.cs`

- [ ] **Step 1: Delete all 8 files**

```bash
cd "E:\SteamLibrary\steamapps\common\Slay the Spire 2\mods\TokenSpire2"
rm src/Coop/Patches/BrokerVirtualFriendSteamPatch.cs
rm src/Coop/Patches/BrokerClientLobbyHandshakePatch.cs
rm src/Coop/Patches/CharacterSelectInputDiagnosticsPatches.cs
rm src/Coop/Patches/LobbyLifecycleDiagnosticsPatches.cs
rm src/Coop/Patches/NetServiceDiagnosticsPatches.cs
rm src/Coop/Patches/RewardDiagnosticsPatches.cs
rm src/Coop/Patches/RunIdentityDiagnosticsPatches.cs
rm src/Coop/Patches/RunIdentityLocalUiDiagnosticsPatches.cs
```

---

### Task 5: Apply combat stability Fixes A-E to AutoSlayNode.cs

**Files:**
- Modify: `src/AutoSlayNode.cs`

**Interfaces:**
- Adds fields: `_combatTurnRequestedDuration` (Fix A), `_waitingForTurnDuration` (Fix E)
- Modifies: combat update loop (lines 720-730 area), adds watchdog `else` branches

- [ ] **Step 1: Add new fields**

Find the existing field declarations near `_combatTurnRequested` (search for `private double _combatTurnRequestedDuration` or add after `_combatTurnRequested`). Add:

```csharp
    // Fix A: duration _combatTurnRequested has been true without PlayerActionsDisabled
    private double _combatTurnRequestedDuration;
    // Fix B: co-op safe stuck tracking — seconds since last meaningful action
    private double _lastCombatActivityCoop;
    // Fix E: duration stuck with PlayerActionsDisabled=true (waiting for other player)
    private double _waitingForTurnDuration;
```

- [ ] **Step 2: Add Fix A — _combatTurnRequested timeout watchdog**

Find the `if (!_combatTurnRequested)` block (search for that exact text). After the closing `}` of the `if (!_combatTurnRequested)` block but before any `return` at the end of the combat section, add the `else` watchdog. The exact location depends on the current file structure — insert after the block that handles the solver logic:

```csharp
            else
            {
                // ── Fix A: Turn-requested watchdog ──────────────────
                // We set _combatTurnRequested=true after playing cards.
                // PlayerActionsDisabled should become true shortly after.
                // If it doesn't (end-turn click failed silently), deadlock.
                _combatTurnRequestedDuration += _delta;
                if (_combatTurnRequestedDuration > 5.0)
                {
                    MainFile.Logger.Error(
                        $"[AutoSlay] DEADLOCK: _combatTurnRequested=true for " +
                        $"{_combatTurnRequestedDuration:F1}s. Retrying end turn...");
                    _combatTurnRequestedDuration = 0;
                    _combatTurnRequested = false;
                    _combatCardDelay = 0.2;
                    try
                    {
                        var rs = RunManager.Instance?.DebugOnlyGetState();
                        var pl = rs != null ? LocalContext.GetMe(rs) : null;
                        if (pl != null && CombatManager.Instance is { PlayerActionsDisabled: false })
                        {
                            var handler = new Multiplayer.MpScreenHandler();
                            for (int retry = 0; retry < 5; retry++)
                            {
                                if (handler.ClickEndTurnButton())
                                {
                                    MainFile.Logger.Info(
                                        $"[AutoSlay] Deadlock recovery: end turn clicked (retry #{retry})");
                                    _combatTurnRequested = true;
                                    _combatTurnRequestedDuration = 0;
                                    _combatCardDelay = 0.5;
                                    return;
                                }
                                System.Threading.Thread.Sleep(200);
                            }
                            MainFile.Logger.Error(
                                "[AutoSlay] Deadlock recovery FAILED — all retries exhausted!");
                        }
                    }
                    catch (Exception ex)
                    {
                        MainFile.Logger.Error(
                            $"[AutoSlay] Deadlock recovery CRASH: {ex.Message}");
                    }
                }
                return;
            }
```

- [ ] **Step 3: Add Fix B — co-op safe stuck detection**

Find the existing stuck-detection code guarded by `!Coop.CoopManager.IsCoopMode`. Replace the guard to add a co-op variant. Search for `IsCoopMode` in AutoSlayNode.cs to find the exact location. Replace with:

```csharp
            // Fix B: Co-op safe stuck detection (does NOT kill process)
            if (Coop.CoopManager.IsCoopMode
                && _lastCombatActivityCoop > 120.0)
            {
                MainFile.Logger.Error(
                    $"[AutoSlay] COOP STUCK: {_lastCombatActivityCoop:F0}s " +
                    "without activity. Forcing re-solve...");
                _lastCombatActivityCoop = 0;
                _combatTurnRequested = false;
                _combatPlan = null;
                _combatCardDelay = 1.0;
                _combatTurnRequestedDuration = 0;
                return;
            }
```

- [ ] **Step 4: Add Fix E — waiting-for-turn watchdog**

Find the `else` branch for `if (!cm.PlayerActionsDisabled)` (the branch where the bot is waiting). Add Fix E watchdog:

```csharp
            // Fix E: Watchdog for stuck-waiting state
            // PlayerActionsDisabled=true means it's not our turn.
            // If this persists >45s, try to advance the round.
            _waitingForTurnDuration += _delta;
            if (Coop.CoopManager.IsCoopMode && _waitingForTurnDuration > 45.0)
            {
                MainFile.Logger.Error(
                    $"[AutoSlay] STUCK-WAITING: {_waitingForTurnDuration:F0}s " +
                    "with PlayerActionsDisabled=true. Trying End Turn...");
                try
                {
                    var handler = new Multiplayer.MpScreenHandler();
                    if (handler.ClickEndTurnButton())
                    {
                        MainFile.Logger.Info(
                            "[AutoSlay] Stuck-waiting recovery: End Turn clicked.");
                        _waitingForTurnDuration = 0;
                    }
                    else
                    {
                        _waitingForTurnDuration = 30.0; // retry in ~15s
                    }
                }
                catch (Exception ex)
                {
                    MainFile.Logger.Error(
                        $"[AutoSlay] Stuck-waiting recovery error: {ex.Message}");
                    _waitingForTurnDuration = 30.0;
                }
            }
```

- [ ] **Step 5: Reset _waitingForTurnDuration in active turn**

Find the block where `_combatTurnRequested` is set to `false` at `PlayerActionsDisabled=false`. Add reset:

```csharp
            _waitingForTurnDuration = 0; // Fix E: reset waiting timer
```

- [ ] **Step 6: Reset _combatTurnRequestedDuration wherever _combatTurnRequested is set to false**

Search for `_combatTurnRequested = false` and add `_combatTurnRequestedDuration = 0;` on the next line at each occurrence.

---

### Task 6: Build and deploy

- [ ] **Step 1: Kill any running game instances**

```bash
taskkill /f /im SlayTheSpire2.exe 2>/dev/null; taskkill /f /im BrokerServer.exe 2>/dev/null; echo "Cleaned"
```

- [ ] **Step 2: Build**

```bash
cd "E:\SteamLibrary\steamapps\common\Slay the Spire 2\mods\TokenSpire2" && dotnet build -c Release 2>&1
```

Expected: `Build succeeded.` with 0 errors.

- [ ] **Step 3: Fix any build errors**

If the build fails, check error messages. Common issues:
- Missing `using` directives in modified files
- Deleted types still referenced elsewhere (search entire codebase for deleted type names with `grep -r "BrokerVFPatch" src/`)
- `BrokerJoinFriendScreenPatch` namespace mismatch

- [ ] **Step 4: Deploy DLL to both locations**

```bash
cd "E:\SteamLibrary\steamapps\common\Slay the Spire 2\mods\TokenSpire2"
cp bin/Release/net8.0/TokenSpire2.dll ./TokenSpire2.dll
cp bin/Release/net8.0/TokenSpire2.dll mods/TokenSpire2/TokenSpire2.dll
echo "Deployed."
```

---

### Task 7: Test

- [ ] **Step 1: Delete old event logs**

```bash
cd "E:\SteamLibrary\steamapps\common\Slay the Spire 2\mods\TokenSpire2"
rm -f localcoop-*.txt
echo "Logs cleaned."
```

- [ ] **Step 2: Launch dual instances**

```bash
cd "E:\SteamLibrary\steamapps\common\Slay the Spire 2\mods\TokenSpire2"
powershell -ExecutionPolicy Bypass -File launch_lan.ps1
```

- [ ] **Step 3: Verify host starts**

Check `localcoop-host-0-events.txt`:
```
[OK] Broker markers: host+client, endpoint=127.0.0.1:9999
[HOST] Starting SlayTheSpire2.exe...
```

- [ ] **Step 4: Verify client starts**

Check `localcoop-client-1-events.txt`:
```
[CLIENT] Starting SlayTheSpire2.exe...
```

- [ ] **Step 5: Test client join**

In the client log (`localcoop-client-1-events.txt`), verify:
```
[BrokerJoinFriendScreenPatch] Broker client detected. Skipping friend list...
[BrokerJoinFriendScreenPatch] JoinFlow.Begin invoked — broker handshake in progress.
[BrokerClientJoinFlowPatch] Intercepting JoinFlow.Begin for broker setup...
```

And NOT:
```
[MpController] TriggerJoinFlowBegin: ... (should never appear — reflection removed)
```

- [ ] **Step 6: Verify character select**

Both instances should reach Character Select. Human picks character on host, bot auto-selects.

- [ ] **Step 7: Test combat + auto-battle**

1. Press T in host window to enable auto-battle
2. Observe 10+ rounds of combat
3. Check: no freeze, no stuck, turns advance normally
4. Check logs for absence of: `DEADLOCK DETECTED`, `COOP STUCK`, `StateDivergenceMessage`

---

## Verification Checklist

- [ ] Build: 0 errors
- [ ] All 8 obsolete files deleted
- [ ] `BrokerJoinFriendScreenPatch` installed successfully (logged at startup)
- [ ] No `BrokerVFPatch_*` in startup logs (VF patches removed)
- [ ] No diagnostic patch spam in logs
- [ ] Client joins WITHOUT friend list appearing
- [ ] Both players reach Character Select
- [ ] Both players reach lobby
- [ ] Combat starts
- [ ] T-key auto-battle works (10+ rounds without freeze)
- [ ] End turn works (verifiable in logs)
- [ ] No StateDivergence messages
