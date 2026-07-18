# Card Bundle Selection Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make bots reliably select and confirm an `NCardBundle` without infinite retries or multiplayer-nondeterministic input.

**Architecture:** Keep deterministic first-bundle selection in `BundleDecider`, but replace the invalid generic `"pressed"` signal with the game-generated `EmitSignalClicked()` API. Track attempts by frame count, wait for observable UI state changes, and use `Hitbox.ForceClick()` only as a delayed compatibility fallback.

**Tech Stack:** C# 13, .NET 9, Godot 4.5, Slay the Spire 2 node APIs, xUnit

## Global Constraints

- Always choose the first valid bundle deterministically.
- Never use `System.Random`.
- Never treat “signal emission did not throw” as proof that selection succeeded.
- Do not modify unrelated working-tree changes in `src/Solver/EventDecider.cs` or the untracked `6.0` file.
- Build and deploy through the existing `TokenSpire2.csproj` Release target.

---

### Task 1: Correct Bundle Selection Input

**Files:**
- Modify: `src/Solver/BundleDecider.cs`
- Test: `tests/TokenSpire2.Core.Tests/AutoSlayIntegrationTests.cs`

**Interfaces:**
- Consumes: `NCardBundle.EmitSignalClicked()`, `NCardBundle.Hitbox`, `NConfirmButton.IsEnabled`
- Produces: deterministic bundle-selection requests with delayed hitbox fallback

- [ ] **Step 1: Write the failing regression test**

Add this test to `AutoSlayIntegrationTests`:

```csharp
[Fact]
public void BundleSelectionUsesNativeClickedSignalInsteadOfPressed()
{
    var source = File.ReadAllText(
        FindRepoFile("src", "Solver", "BundleDecider.cs"));

    Assert.Contains("pick.EmitSignalClicked();", source);
    Assert.DoesNotContain("pick.EmitSignal(\"pressed\")", source);
}
```

- [ ] **Step 2: Run the focused test and verify RED**

Run:

```powershell
dotnet test tests\TokenSpire2.Core.Tests\TokenSpire2.Core.Tests.csproj `
  --no-restore --filter BundleSelectionUsesNativeClickedSignalInsteadOfPressed
```

Expected: FAIL because `BundleDecider` still contains
`pick.EmitSignal("pressed")` and does not contain `pick.EmitSignalClicked()`.

- [ ] **Step 3: Implement the minimal native-signal fix**

In `BundleDecider.Decide()`, replace the three speculative click approaches
with a state-aware request:

```csharp
private const int HitboxFallbackFrame = 6;
private static bool _selectionRequested;
```

Reset `_selectionRequested` in `Reset()` and whenever the screen is missing.
After detecting an enabled confirm button, log confirmation, click it, and
reset both state fields.

For selection:

```csharp
if (!_selectionRequested)
{
    pick.EmitSignalClicked();
    _selectionRequested = true;
    MainFile.Logger.Info(
        $"[BundleDecider] Selection requested via Clicked: {label}");
    return;
}

if (_stuckFrames == HitboxFallbackFrame && hasHitbox)
{
    pick.Hitbox!.ForceClick();
    MainFile.Logger.Warn(
        $"[BundleDecider] No state change after {HitboxFallbackFrame} frames; " +
        $"using hitbox fallback: {label}");
}
```

Remove all `EmitSignal("pressed")` calls and misleading `Approach N OK`
messages. Preserve the existing timeout diagnostic, but make its recovery use
`EmitSignalClicked()` followed by a hitbox fallback.

- [ ] **Step 4: Run the focused test and verify GREEN**

Run the same filtered command.

Expected: 1 passed, 0 failed.

- [ ] **Step 5: Run all tests**

Run:

```powershell
dotnet test tests\TokenSpire2.Core.Tests\TokenSpire2.Core.Tests.csproj --no-restore
```

Expected: all tests pass.

- [ ] **Step 6: Build and verify deployment**

Run:

```powershell
dotnet build TokenSpire2.csproj -c Release --no-restore
Get-Item .\TokenSpire2.dll | Format-List FullName,Length,LastWriteTime
```

Expected: build exits with 0 errors and the root mod DLL has a new timestamp.

- [ ] **Step 7: Commit only the bundle fix**

```powershell
git add -- src/Solver/BundleDecider.cs `
  tests/TokenSpire2.Core.Tests/AutoSlayIntegrationTests.cs
git commit -m "fix: use native clicked signal for card bundles"
```

Expected: the commit contains exactly the production file and regression test.
