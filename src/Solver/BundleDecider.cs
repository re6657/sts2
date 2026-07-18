using System.Collections.Generic;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;

using TokenSpire2.Core;

namespace TokenSpire2.Solver;

/// <summary>
/// Card bundle selection (e.g., "Scroll Box" relic, "choose a card bundle" events).
/// Simple heuristic: pick the first bundle (they're usually visually ordered by value).
///
/// Handles the two-step flow:
///   1. Select a bundle by clicking it
///   2. Confirm the selection via NConfirmButton
///
/// Includes stuck detection to prevent infinite loops if clicks don't register.
/// </summary>
public static class BundleDecider
{
    private static int _stuckFrames;
    private const int HitboxFallbackFrame = BundleSelectionRequestGate.HitboxFallbackFrame;
    private const int STUCK_TIMEOUT = 180; // 3 seconds at 60fps
    private static readonly BundleSelectionRequestGate _selectionGate = new();

    public static void Reset()
    {
        _stuckFrames = 0;
        _selectionGate.Reset();
    }

    public static void Decide()
    {
        var screen = NOverlayStack.Instance?.Peek() as NChooseABundleSelectionScreen;
        if (screen == null || !GodotObject.IsInstanceValid(screen))
        {
            _stuckFrames = 0;
            _selectionGate.Reset();
            return;
        }

        _stuckFrames++;

        // ── Step 1: If confirm is enabled, click it (bundle already selected) ──
        var confirm = AutoSlayHelpers.FindFirst<NConfirmButton>(screen);
        if (confirm != null && GodotObject.IsInstanceValid(confirm) && confirm.IsEnabled)
        {
            MainFile.Logger.Info($"[BundleDecider] Confirming bundle selection (frame {_stuckFrames})");
            confirm.ForceClick();
            _stuckFrames = 0;
            _selectionGate.Reset();
            return;
        }

        // ── Step 2: Select a bundle ───────────────────────────────────────────
        var bundles = AutoSlayHelpers.FindAll<NCardBundle>(screen)
            .Where(b => GodotObject.IsInstanceValid(b))
            .ToList();

        if (bundles.Count == 0)
        {
            if (_selectionGate.Attempted)
                _selectionGate.Tick(false, false, false);

            // No bundles found — stuck or screen is in transition
            if (_stuckFrames > STUCK_TIMEOUT)
            {
                MainFile.Logger.Error($"[BundleDecider] STUCK: no bundles after {_stuckFrames} frames — trying emergency dismiss");
                // Try to dismiss the screen
                try
                {
                    var anyConfirm = AutoSlayHelpers.FindFirst<NConfirmButton>(screen);
                    if (anyConfirm != null)
                    {
                        MainFile.Logger.Info("[BundleDecider] Emergency: clicking confirm even if disabled");
                        anyConfirm.ForceClick();
                    }
                }
                catch (Exception ex)
                {
                    MainFile.Logger.Error($"[BundleDecider] Emergency confirm failed: {ex}");
                }
                _stuckFrames = 0;
            }
            return;
        }

        // Pick the first bundle (deterministic)
        var pick = bundles[0];
        string label = pick.Name ?? "Bundle#1";
        bool hasHitbox = pick.Hitbox != null && GodotObject.IsInstanceValid(pick.Hitbox);

        bool firstRequest = !_selectionGate.Attempted;
        if (firstRequest)
        {
            MainFile.Logger.Info($"[BundleDecider] Selecting bundle: {label} (bundles={bundles.Count}, hasHitbox={hasHitbox}, frame={_stuckFrames})");

            DecisionLogger.LogDecision(
                GameScreen.OVERLAY_CHOOSE_BUNDLE, "BundleChoice",
                bundles.Select((b, i) => new DecisionLogger.OptionScore
                {
                    Index = i, Label = b.Name ?? $"Bundle#{i + 1}", Score = bundles.Count - i
                }).ToList(),
                0, label, "Picking first bundle (deterministic)");
        }

        bool timeoutRecoveryRequested =
            _selectionGate.Attempted && _stuckFrames > STUCK_TIMEOUT;
        if (timeoutRecoveryRequested)
        {
            MainFile.Logger.Error(
                $"[BundleDecider] STUCK after {_stuckFrames} frames on '{label}' — emergency recovery");
        }

        var input = _selectionGate.Tick(hasHitbox, timeoutRecoveryRequested);
        if (input == BundleSelectionInput.Clicked)
        {
            Godot.Error selectionResult =
                pick.EmitSignal(NCardBundle.SignalName.Clicked, pick);
            _selectionGate.RecordClickedResult(selectionResult == Godot.Error.Ok);
            if (selectionResult == Godot.Error.Ok)
            {
                MainFile.Logger.Info(
                    $"[BundleDecider] Selection requested via Clicked: {label}");
            }
            else
            {
                MainFile.Logger.Warn(
                    $"[BundleDecider] Selection request failed for {label}: " +
                    $"{selectionResult}; waiting for hitbox fallback");
            }
            if (!firstRequest || timeoutRecoveryRequested)
                _stuckFrames = 0;
            return;
        }

        if (input == BundleSelectionInput.Exhausted)
        {
            MainFile.Logger.Error(
                $"[BundleDecider] BundleSelectionRecoveryExhausted cycles={_selectionGate.CycleCount} label={label}");
            _stuckFrames = 0;
            return;
        }

        if (input == BundleSelectionInput.HitboxFallback)
        {
            string reason = timeoutRecoveryRequested
                ? "timeout recovery"
                : $"after {HitboxFallbackFrame} waiting ticks";
            if (!TryHitboxFallback(pick, label, reason))
                _selectionGate.ReportInputFailed();
            if (timeoutRecoveryRequested)
                _stuckFrames = 0;
            return;
        }

        if (timeoutRecoveryRequested)
        {
            _stuckFrames = 0;
        }
    }

    private static bool TryHitboxFallback(NCardBundle pick, string label, string reason)
    {
        if (pick.Hitbox == null || !GodotObject.IsInstanceValid(pick.Hitbox))
        {
            MainFile.Logger.Warn(
                $"[BundleDecider] Hitbox fallback unavailable for {label}: invalid hitbox ({reason})");
            return false;
        }

        try
        {
            pick.Hitbox.ForceClick();
            MainFile.Logger.Warn(
                $"[BundleDecider] Using hitbox fallback for {label}: {reason}");
            return true;
        }
        catch (Exception ex)
        {
            MainFile.Logger.Error(
                $"[BundleDecider] Hitbox fallback failed for {label}: {ex}");
            return false;
        }
    }
}
