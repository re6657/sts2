# Host Manual Control, Turn Safety, and Launcher Diagnostics Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Guarantee that multiplayer hosts remain fully manual, prevent bots from ending a turn before turn-start state is stable, and provide a side-navigation launcher with per-instance automatic diagnostics.

**Architecture:** Add pure policy and readiness components that can be tested without Godot, then make `AutoSlayNode` consume them at the existing integration boundaries. Add a per-instance JSONL diagnostic writer shared by the mod and launcher, while the WinForms launcher reads those files into a dedicated diagnostics page. Preserve the existing solver and multiplayer action queue behavior except where the new gate explicitly blocks premature solving or ending.

**Tech Stack:** C# 13, .NET 9, Godot 4.5, BaseLib `ICardSelector`, xUnit, WinForms .NET 8, JSON Lines.

## Global Constraints

- Multiplayer Host must never register or invoke automatic card selection.
- Bot decisions must remain deterministic across lockstep instances.
- No `System.Random`, wall-clock decision timeout, `Thread.Sleep`, or per-frame file writes may affect gameplay decisions.
- Existing uncommitted edits in `src/AutoSlayNode.cs` and `src/Solver/DecisionEngine.cs` must be preserved.
- Diagnostic failures must never block the Godot main thread or change game state.
- The launcher may read logs but must not control a running game instance.

---

## File Structure

- Create `src/Core/AutomationPolicy.cs`: pure authorization rules for host, bot, and solo instances.
- Create `src/Core/TurnReadinessGate.cs`: pure snapshot-based state machine for solve/end-turn readiness.
- Create `src/Diagnostics/DiagnosticEvent.cs`: JSONL event contract and fixed event type names.
- Create `src/Diagnostics/DiagnosticWriter.cs`: role-specific text/JSONL writer with deduplication.
- Create `tests/TokenSpire2.Core.Tests/TokenSpire2.Core.Tests.csproj`: isolated test project linking pure source files.
- Create `tests/TokenSpire2.Core.Tests/AutomationPolicyTests.cs`: host/manual policy tests.
- Create `tests/TokenSpire2.Core.Tests/TurnReadinessGateTests.cs`: readiness and end-turn regression tests.
- Create `tests/TokenSpire2.Core.Tests/DiagnosticWriterTests.cs`: per-role path and JSONL tests.
- Modify `src/AutoSlayNode.cs`: policy integration, selector lifecycle, readiness gate, structured events.
- Modify `src/Core/AppConfig.cs`: stable instance role (`host`, `bot1`…`bot3`, `solo`) and session ID.
- Modify `tools/Launcher/MainForm.cs`: side-navigation workspace and diagnostics viewer.
- Create `tools/Launcher/DiagnosticLogReader.cs`: parse/filter JSONL and load surrounding text context.
- Create `tools/Launcher/LauncherTheme.cs`: centralized colors, fonts, spacing, and control styling.
- Modify `tools/Launcher/TokenSpire2Launcher.csproj`: compile shared diagnostic event contract by link.
- Modify `TokenSpire2.sln`: include the pure test project.

---

### Task 1: Pure Automation Policy

**Files:**
- Create: `src/Core/AutomationPolicy.cs`
- Create: `tests/TokenSpire2.Core.Tests/TokenSpire2.Core.Tests.csproj`
- Create: `tests/TokenSpire2.Core.Tests/AutomationPolicyTests.cs`
- Modify: `TokenSpire2.sln`

**Interfaces:**
- Produces: `AutomationPolicy(bool multiplayer, bool isHost, bool autoNavigate, bool autoBattle, bool autoEvent)`
- Produces: `bool Allows(AutomationAction action)`
- Produces: `AutomationAction` enum covering selector registration, combat, overlays, map, event, shop, rest, rewards, LLM, and fallback.

- [ ] **Step 1: Create the isolated test project**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
    <PackageReference Include="xunit" Version="2.9.3" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.1.1" PrivateAssets="all" />
    <Compile Include="..\..\src\Core\AutomationPolicy.cs" Link="Core\AutomationPolicy.cs" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Write failing host/manual policy tests**

```csharp
[Fact]
public void MultiplayerHostRejectsEveryAutomationAction()
{
    var policy = new AutomationPolicy(true, true, true, true, true);
    foreach (var action in Enum.GetValues<AutomationAction>())
        Assert.False(policy.Allows(action));
}

[Fact]
public void MultiplayerBotAllowsConfiguredAutomation()
{
    var policy = new AutomationPolicy(true, false, true, true, true);
    Assert.True(policy.Allows(AutomationAction.RegisterCardSelector));
    Assert.True(policy.Allows(AutomationAction.Combat));
    Assert.True(policy.Allows(AutomationAction.CardGrid));
}
```

- [ ] **Step 3: Run the tests and verify RED**

Run: `dotnet test tests/TokenSpire2.Core.Tests/TokenSpire2.Core.Tests.csproj --filter AutomationPolicyTests`

Expected: FAIL because `AutomationPolicy` and `AutomationAction` do not exist.

- [ ] **Step 4: Implement the minimal policy**

```csharp
namespace TokenSpire2.Core;

public enum AutomationAction
{
    RegisterCardSelector, Combat, CardGrid, Map, Event, Shop, Rest,
    Rewards, LlmSelection, TimeoutFallback
}

public sealed class AutomationPolicy
{
    private readonly bool _multiplayer;
    private readonly bool _isHost;
    private readonly bool _autoNavigate;
    private readonly bool _autoBattle;
    private readonly bool _autoEvent;

    public AutomationPolicy(bool multiplayer, bool isHost,
        bool autoNavigate, bool autoBattle, bool autoEvent)
        => (_multiplayer, _isHost, _autoNavigate, _autoBattle, _autoEvent)
            = (multiplayer, isHost, autoNavigate, autoBattle, autoEvent);

    public bool IsFullyManualHost => _multiplayer && _isHost;

    public bool Allows(AutomationAction action)
    {
        if (IsFullyManualHost) return false;
        return action switch
        {
            AutomationAction.Combat => _autoBattle,
            AutomationAction.Map or AutomationAction.Shop or AutomationAction.Rest => _autoNavigate,
            AutomationAction.Event or AutomationAction.Rewards or AutomationAction.CardGrid => _autoEvent,
            AutomationAction.RegisterCardSelector => _autoBattle || _autoEvent,
            AutomationAction.LlmSelection or AutomationAction.TimeoutFallback => _autoEvent,
            _ => false,
        };
    }
}
```

- [ ] **Step 5: Run tests and verify GREEN**

Run: `dotnet test tests/TokenSpire2.Core.Tests/TokenSpire2.Core.Tests.csproj --filter AutomationPolicyTests`

Expected: PASS.

- [ ] **Step 6: Add the test project to the solution and commit**

Run: `dotnet sln TokenSpire2.sln add tests/TokenSpire2.Core.Tests/TokenSpire2.Core.Tests.csproj`

Commit: `git commit -m "test: define centralized automation policy"`

---

### Task 2: Turn Readiness State Machine

**Files:**
- Create: `src/Core/TurnReadinessGate.cs`
- Create: `tests/TokenSpire2.Core.Tests/TurnReadinessGateTests.cs`
- Modify: `tests/TokenSpire2.Core.Tests/TokenSpire2.Core.Tests.csproj`

**Interfaces:**
- Consumes: immutable `TurnSnapshot` values from `AutoSlayNode`.
- Produces: `TurnReadinessDecision Observe(TurnSnapshot snapshot)`.
- Produces: `TurnReadinessDecision` values `Wait`, `Solve`, and `AllowEndTurn` with a stable reason string.

- [ ] **Step 1: Write failing readiness tests**

```csharp
[Fact]
public void ZeroEnergyWithPositiveCostCardsWaitsForTurnRefresh()
{
    var gate = new TurnReadinessGate(requiredStableSamples: 3);
    var snapshot = Snapshot(energy: 0, hand: 5, playable: 0,
        hasPositiveCostCards: true, queueIdle: true, drawComplete: true);
    Assert.Equal(TurnReadinessKind.Wait, gate.Observe(snapshot).Kind);
}

[Fact]
public void StableFullEnergyPlayableHandCanSolve()
{
    var gate = new TurnReadinessGate(3);
    var snapshot = Snapshot(energy: 3, hand: 5, playable: 4,
        hasPositiveCostCards: true, queueIdle: true, drawComplete: true);
    gate.Observe(snapshot); gate.Observe(snapshot);
    Assert.Equal(TurnReadinessKind.Solve, gate.Observe(snapshot).Kind);
}

[Fact]
public void StableTrulyUnplayableHandCanEndTurn()
{
    var gate = new TurnReadinessGate(3);
    var snapshot = Snapshot(energy: 0, hand: 2, playable: 0,
        hasPositiveCostCards: false, queueIdle: true, drawComplete: true);
    gate.Observe(snapshot); gate.Observe(snapshot);
    Assert.Equal(TurnReadinessKind.AllowEndTurn, gate.Observe(snapshot).Kind);
}
```

- [ ] **Step 2: Run tests and verify RED**

Run: `dotnet test tests/TokenSpire2.Core.Tests/TokenSpire2.Core.Tests.csproj --filter TurnReadinessGateTests`

Expected: FAIL because readiness types do not exist.

- [ ] **Step 3: Implement snapshot identity and the three-state gate**

Implement immutable records for snapshot and decision. Snapshot equality must include turn number, energy, hand/draw/discard counts, playable count, queue/draw state, and `EndTurnRequested`. Reset stable samples whenever any included value changes. `EndTurnRequested`, disabled actions, active queue, or incomplete draw always returns `Wait`.

- [ ] **Step 4: Add regression tests for state changes**

Add tests proving that energy changes from 0 to 3 reset stability, a turn-number change resets stability, and a submitted end-turn request never returns `Solve` or `AllowEndTurn`.

- [ ] **Step 5: Run the complete pure test suite and commit**

Run: `dotnet test tests/TokenSpire2.Core.Tests/TokenSpire2.Core.Tests.csproj`

Expected: PASS.

Commit: `git commit -m "feat: gate solver on stable turn state"`

---

### Task 3: Per-Instance Structured Diagnostics

**Files:**
- Modify: `src/Core/AppConfig.cs`
- Create: `src/Diagnostics/DiagnosticEvent.cs`
- Create: `src/Diagnostics/DiagnosticWriter.cs`
- Create: `tests/TokenSpire2.Core.Tests/DiagnosticWriterTests.cs`
- Modify: `tests/TokenSpire2.Core.Tests/TokenSpire2.Core.Tests.csproj`

**Interfaces:**
- Produces: `AppConfig.InstanceRole` (`host`, `bot1`, `bot2`, `bot3`, or `solo`).
- Produces: `DiagnosticWriter.Initialize(modDirectory, instanceRole, sessionId)`.
- Produces: `DiagnosticWriter.Write(DiagnosticEvent evt, string? dedupeKey = null)`.

- [ ] **Step 1: Write failing path and serialization tests**

Test that host and bot writers resolve different `.log` and `.events.jsonl` paths, JSONL contains exactly one JSON object per line, and repeating the same dedupe key does not append a second event.

- [ ] **Step 2: Run tests and verify RED**

Run: `dotnet test tests/TokenSpire2.Core.Tests/TokenSpire2.Core.Tests.csproj --filter DiagnosticWriterTests`

Expected: FAIL because diagnostic types do not exist.

- [ ] **Step 3: Implement fixed event types and append-only writer**

```csharp
public static class DiagnosticEventTypes
{
    public const string TurnSkippedWithPlayableCards = "TURN_SKIPPED_WITH_PLAYABLE_CARDS";
    public const string HostAutomationBlocked = "HOST_AUTOMATION_BLOCKED";
    public const string StateDivergence = "STATE_DIVERGENCE";
    public const string ActionQueueStalled = "ACTION_QUEUE_STALLED";
    public const string OverlayStuck = "OVERLAY_STUCK";
}
```

Open append streams with `FileShare.ReadWrite`; serialize outside the gameplay decision path; catch and report failures only through the existing logger. Keep a bounded in-memory dedupe set scoped to the current room/turn.

- [ ] **Step 4: Extend launcher JSON config with `InstanceRole` and `SessionId`**

Host config writes `InstanceRole:"host"`; bot configs write `bot1` through `bot3`; all configs from one launcher action share one GUID session ID. `AppConfig` reads both values with backwards-compatible defaults.

- [ ] **Step 5: Run tests and commit**

Run: `dotnet test tests/TokenSpire2.Core.Tests/TokenSpire2.Core.Tests.csproj`

Expected: PASS.

Commit: `git commit -m "feat: add per-instance structured diagnostics"`

---

### Task 4: Integrate Policy and Readiness into AutoSlayNode

**Files:**
- Modify: `src/AutoSlayNode.cs`
- Preserve without editing: `src/Solver/DecisionEngine.cs`; verify its existing deterministic pending-context change remains in the final diff.

**Interfaces:**
- Consumes: `AutomationPolicy`, `TurnReadinessGate`, and `DiagnosticWriter`.
- Preserves: existing solver calls and multiplayer `RequestEnqueue` path.

- [ ] **Step 1: Add a source-level regression test for selector registration**

Add a test that reads `src/AutoSlayNode.cs` and asserts selector registration is nested under `AutomationAction.RegisterCardSelector`, and that no unconditional `CardSelectCmd.UseSelector` statement remains.

- [ ] **Step 2: Run the test and verify RED**

Run: `dotnet test tests/TokenSpire2.Core.Tests/TokenSpire2.Core.Tests.csproj --filter AutoSlayIntegrationTests`

Expected: FAIL against the current unconditional registration.

- [ ] **Step 3: Replace the host guard and selector lifecycle with AutomationPolicy**

Construct one policy after configuration is loaded. Register `AutoSlayCardSelector` only when the policy allows `RegisterCardSelector`; otherwise leave selector and scope null and emit one `HOST_AUTOMATION_BLOCKED` event. Replace direct `IsHostManualMode` branches at overlay, map, event, treasure, rest, shop, LLM, and timeout fallback boundaries with policy checks.

- [ ] **Step 4: Insert TurnReadinessGate before solver invocation**

Build a snapshot from local player state. If the result is `Wait`, reset the stuck timer and return without mutating the plan. If `Solve`, enter the existing solver. If an empty plan requests end turn, obtain a fresh snapshot and require `AllowEndTurn`; otherwise clear the plan and wait.

- [ ] **Step 5: Emit actionable diagnostics**

Before submitting end turn, if a fresh hand contains any `CanPlay` card, cancel submission and emit `TURN_SKIPPED_WITH_PLAYABLE_CARDS` with energy, card IDs, turn number, and gate reason. Emit `ACTION_QUEUE_STALLED` from existing deadlock paths and `OVERLAY_STUCK` from forced overlay recovery.

- [ ] **Step 6: Run pure tests and build the mod**

Run: `dotnet test tests/TokenSpire2.Core.Tests/TokenSpire2.Core.Tests.csproj`

Run: `dotnet build TokenSpire2.csproj -c Release`

Expected: tests PASS; build completes with 0 errors.

- [ ] **Step 7: Commit integration**

Commit: `git commit -m "fix: keep host manual and prevent premature end turn"`

---

### Task 5: Launcher Side-Navigation Workspace

**Files:**
- Create: `tools/Launcher/LauncherTheme.cs`
- Create: `tools/Launcher/DiagnosticLogReader.cs`
- Modify: `tools/Launcher/MainForm.cs`
- Modify: `tools/Launcher/TokenSpire2Launcher.csproj`
- Create: `tests/TokenSpire2.Core.Tests/DiagnosticLogReaderTests.cs`

**Interfaces:**
- Consumes: role-specific `*.events.jsonl` and `*.log` files.
- Produces: `DiagnosticLogReader.ReadEvents(path, filter)` and `ReadContext(textPath, timestamp, before, after)`.

- [ ] **Step 1: Write failing parser/filter tests**

Use temporary JSONL files containing all five event types. Assert invalid lines are skipped with a parse warning, role and type filters return only matching events, and context loading returns bounded lines around the selected timestamp.

- [ ] **Step 2: Run parser tests and verify RED**

Run: `dotnet test tests/TokenSpire2.Core.Tests/TokenSpire2.Core.Tests.csproj --filter DiagnosticLogReaderTests`

Expected: FAIL because the reader does not exist.

- [ ] **Step 3: Implement parser and centralized theme**

Keep parsing independent from WinForms controls. Theme constants use dark neutral backgrounds, amber primary accent, red errors, green healthy status, Microsoft YaHei UI for interface text, and Consolas for logs.

- [ ] **Step 4: Rebuild MainForm around a fixed sidebar**

Create sidebar buttons for 大厅, Bot, 对话, 诊断, 设置 and one right-side content host. Move existing controls into page panels without changing config semantics. Remove the Host auto checkbox and always write `AutoBattleEnabled:false` for the Host. Display a green “完全手动” badge on the Host card.

- [ ] **Step 5: Implement diagnostics page A**

Add instance tabs, five event filters, refresh, open-log-directory, event timeline, and raw context viewer. Poll only while the diagnostics page is visible and update UI on the WinForms thread. Do not hold files open between refreshes.

- [ ] **Step 6: Build launcher and run all tests**

Run: `dotnet test tests/TokenSpire2.Core.Tests/TokenSpire2.Core.Tests.csproj`

Run: `dotnet build tools/Launcher/TokenSpire2Launcher.csproj -c Release`

Expected: tests PASS; launcher build completes with 0 errors.

- [ ] **Step 7: Commit launcher**

Commit: `git commit -m "feat: redesign launcher with diagnostics workspace"`

---

### Task 6: Deployment and Multiplayer Verification

**Files:**
- Modify only if verification exposes a failing test or build error.

**Interfaces:**
- Consumes: Release mod DLL and Release launcher executable.
- Produces: deployed build ready for user testing.

- [ ] **Step 1: Run complete verification**

Run: `dotnet test tests/TokenSpire2.Core.Tests/TokenSpire2.Core.Tests.csproj -c Release`

Run: `dotnet build TokenSpire2.csproj -c Release`

Run: `dotnet build tools/Launcher/TokenSpire2Launcher.csproj -c Release`

Expected: all commands exit 0 with no build errors.

- [ ] **Step 2: Verify deployed artifacts**

Compare SHA-256 and timestamp of the build output and root `TokenSpire2.dll`; confirm the post-build target deployed the same bytes. Confirm the launcher executable timestamp is newer than its source changes.

- [ ] **Step 3: Launch only the launcher**

Start `tools/Launcher/bin/Release/net8.0-windows/TokenSpire2Launcher.exe` without launching the game automatically. Confirm all five pages render and diagnostics handles an empty log directory.

- [ ] **Step 4: Multiplayer acceptance test**

Using Host + 2 Bot, verify Host manually completes upgrade, remove, transform, enchant, reward, event, and combat card-selection interactions. Run three combats and inspect per-instance diagnostics for premature end-turn events and StateDivergence.

- [ ] **Step 5: Final review and commit**

Run: `git diff --check` and inspect `git status --short` to ensure only intended files remain.

Commit any verification-only fixes with: `git commit -m "test: verify multiplayer automation boundaries"`.
