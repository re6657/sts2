using System.Collections.Generic;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Events.Custom.CrystalSphere;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;

namespace TokenSpire2.Solver;

/// <summary>
/// Crystal Sphere event: reveal all hidden cells, then proceed.
/// Simple deterministic rule: click the first hidden cell each tick.
/// </summary>
public static class CrystalSphereDecider
{
    public static void Decide()
    {
        var screen = NOverlayStack.Instance?.Peek() as NCrystalSphereScreen;
        if (screen == null) return;

        // Defer to child overlay
        var topOverlay = NOverlayStack.Instance?.Peek();
        if (topOverlay != null && topOverlay != (IOverlayScreen)screen)
            return;

        // Try proceed
        var proceed = screen.GetNodeOrNull<NProceedButton>("%ProceedButton");
        if (proceed?.IsEnabled == true)
        {
            MainFile.Logger.Info("[CrystalSphereDecider] Clicking proceed");
            proceed.ForceClick();

            // Cleanup lingering screen
            if (NMapScreen.Instance?.IsOpen == true)
                NOverlayStack.Instance?.Remove(screen);

            DecisionLogger.LogDecision(GameScreen.OVERLAY_CRYSTAL_SPHERE,
                "CrystalProceed", new List<DecisionLogger.OptionScore>(),
                0, "PROCEED", "Crystal sphere complete, proceeding");
            return;
        }

        // Click first hidden cell (deterministic)
        var cells = screen.GetNodeOrNull<Control>("%Cells");
        if (cells == null) return;

        var hidden = AutoSlayHelpers.FindAll<NCrystalSphereCell>(cells)
            .Where(c => c.Visible && c.Entity.IsHidden)
            .ToList();

        if (hidden.Count == 0)
        {
            MainFile.Logger.Info("[CrystalSphereDecider] No hidden cells, waiting");
            return;
        }

        // Click first hidden cell — deterministic, will uncover everything eventually
        var pick = hidden[0];
        MainFile.Logger.Info($"[CrystalSphereDecider] Clicking cell, {hidden.Count} remaining");
        pick.EmitSignal(NClickableControl.SignalName.Released, pick);

        DecisionLogger.LogDecision(GameScreen.OVERLAY_CRYSTAL_SPHERE,
            "CrystalCell", new List<DecisionLogger.OptionScore>(),
            0, "CELL", $"Revealing cell ({hidden.Count} remaining)");
    }
}
