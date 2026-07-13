using Godot;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens;

namespace TokenSpire2.Handlers;

public static class ChooseARelicHandler
{
    public static double Handle(NChooseARelicSelection screen, System.Random rng)
    {
        if (!GodotObject.IsInstanceValid(screen)) return 0;

        // Filter to relic-specific clickables — skip generic buttons (skip, back, etc.)
        // Relic buttons typically have "Relic" in their node name.
        var allClickables = AutoSlayHelpers.FindAll<NClickableControl>(screen);
        var relicClickables = allClickables
            .Where(c => c.Name?.ToString()?.Contains("Relic", System.StringComparison.OrdinalIgnoreCase) == true
                     || c.GetType().Name.Contains("Relic", System.StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Fallback: if filtering removed everything, use all clickables (screen might use different naming)
        if (relicClickables.Count == 0)
            relicClickables = allClickables;

        if (relicClickables.Count == 0) return 0.5;

        var pick = relicClickables[rng.Next(relicClickables.Count)];
        MainFile.Logger.Info($"[AutoSlay] Selecting relic ({relicClickables.Count} options)");
        pick.ForceClick();
        return 1.0;
    }
}
