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
    private const int STUCK_TIMEOUT = 180; // 3 seconds at 60fps

    public static void Reset()
    {
        _stuckFrames = 0;
    }

    public static void Decide()
    {
        var screen = NOverlayStack.Instance?.Peek() as NChooseABundleSelectionScreen;
        if (screen == null || !GodotObject.IsInstanceValid(screen))
        {
            _stuckFrames = 0;
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
            return;
        }

        // ── Step 2: Select a bundle ───────────────────────────────────────────
        var bundles = AutoSlayHelpers.FindAll<NCardBundle>(screen)
            .Where(b => GodotObject.IsInstanceValid(b))
            .ToList();

        if (bundles.Count == 0)
        {
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
                catch { }
                _stuckFrames = 0;
            }
            return;
        }

        // Pick the first bundle (deterministic)
        var pick = bundles[0];
        string label = pick.Name ?? "Bundle#1";
        bool hasHitbox = pick.Hitbox != null && GodotObject.IsInstanceValid(pick.Hitbox);

        MainFile.Logger.Info($"[BundleDecider] Selecting bundle: {label} (bundles={bundles.Count}, hasHitbox={hasHitbox}, frame={_stuckFrames})");

        DecisionLogger.LogDecision(
            GameScreen.OVERLAY_CHOOSE_BUNDLE, "BundleChoice",
            bundles.Select((b, i) => new DecisionLogger.OptionScore
            {
                Index = i, Label = b.Name ?? $"Bundle#{i + 1}", Score = bundles.Count - i
            }).ToList(),
            0, label, "Picking first bundle (deterministic)");

        // Try to click the bundle's hitbox. NCardBundle itself doesn't have
        // ForceClick() — the selection is triggered through its Hitbox child.
        try
        {
            if (hasHitbox)
            {
                pick.Hitbox!.ForceClick();
                MainFile.Logger.Info($"[BundleDecider] Clicked Hitbox on '{label}'");
            }
            else
            {
                MainFile.Logger.Warn($"[BundleDecider] Bundle '{label}' has no Hitbox — cannot click");
            }
        }
        catch (Exception ex)
        {
            MainFile.Logger.Error($"[BundleDecider] Click failed for '{label}': {ex.Message}");
        }

        // Reset stuck frames on successful attempt (we'll detect if it didn't work
        // when confirm never appears on subsequent calls)
        if (_stuckFrames > STUCK_TIMEOUT)
        {
            MainFile.Logger.Error($"[BundleDecider] STUCK after {_stuckFrames} frames on '{label}' — forcing reset");
            _stuckFrames = 0;
            // Emergency: try clicking ANY bundle's hitbox
            foreach (var b in bundles)
            {
                try
                {
                    if (b.Hitbox != null && GodotObject.IsInstanceValid(b.Hitbox))
                        b.Hitbox.ForceClick();
                }
                catch { }
            }
        }
    }
}
