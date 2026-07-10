using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;

using TokenSpire2.Core;

namespace TokenSpire2.Solver;

/// <summary>
/// Card bundle selection (e.g., "choose a card bundle" events).
/// Simple heuristic: pick the first bundle (they're usually visually ordered by value).
/// </summary>
public static class BundleDecider
{
    public static void Decide()
    {
        var screen = NOverlayStack.Instance?.Peek() as NChooseABundleSelectionScreen;
        if (screen == null) return;

        // If confirm is enabled, click it
        var confirm = AutoSlayHelpers.FindFirst<NConfirmButton>(screen);
        if (confirm?.IsEnabled == true)
        {
            MainFile.Logger.Info("[BundleDecider] Confirming bundle selection");
            confirm.ForceClick();
            return;
        }

        var bundles = AutoSlayHelpers.FindAll<NCardBundle>(screen);
        if (bundles.Count == 0) return;

        // Pick the first bundle (deterministic)
        var pick = bundles[0];
        string label = pick.Name ?? "Bundle#1";
        MainFile.Logger.Info($"[BundleDecider] Selecting bundle: {label}");

        DecisionLogger.LogDecision(
            GameScreen.OVERLAY_CHOOSE_BUNDLE, "BundleChoice",
            bundles.Select((b, i) => new DecisionLogger.OptionScore
            {
                Index = i, Label = b.Name ?? $"Bundle#{i + 1}", Score = bundles.Count - i
            }).ToList(),
            0, label, "Picking first bundle (deterministic)");

        pick.Hitbox?.ForceClick();
    }
}
