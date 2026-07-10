using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.RestSite;
using MegaCrit.Sts2.Core.Nodes.Rooms;

namespace TokenSpire2.Handlers;

public static class RestSiteHandler
{
    /// <summary>Handle rest site choice. Prefer REST to heal HP.</summary>
    public static double Handle(NRestSiteRoom room, System.Random rng)
    {
        if (!GodotObject.IsInstanceValid(room)) return 0;

        // Check for proceed button (after choosing an option)
        var proceed = room.ProceedButton;
        if (proceed?.IsEnabled == true)
        {
            MainFile.Logger.Info("[AutoSlay] Clicking rest site proceed");
            proceed.ForceClick();
            return 1.5;
        }

        var btns = AutoSlayHelpers.FindAll<NRestSiteButton>(room)
            .Where(b => b.Option.IsEnabled)
            .ToList();
        if (btns.Count == 0) return 0.5;

        // Try to find a REST option (heals ~30% HP) — prefer it when HP is low
        NRestSiteButton? restBtn = btns.FirstOrDefault(b =>
            b.Option.GetType().Name.Contains("Rest", StringComparison.OrdinalIgnoreCase));
        if (restBtn != null)
        {
            MainFile.Logger.Info("[AutoSlay] Resting to heal HP");
            restBtn.ForceClick();
            return 1.5;
        }

        // No explicit REST button found; pick the first option (usually rest is first)
        btns[0].ForceClick();
        return 1.5;
    }
}
