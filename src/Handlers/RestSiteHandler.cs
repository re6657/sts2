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
            .Where(b => b.Option?.IsEnabled == true)
            .ToList();
        if (btns.Count == 0) return 0.5;

        // Try to find a REST option (heals ~30% HP) — prefer it when HP is low.
        // Use multiple detection strategies to avoid fragile string matching on class names.
        NRestSiteButton? restBtn = btns.FirstOrDefault(b =>
        {
            var optType = b.Option?.GetType();
            if (optType == null) return false;
            string typeName = optType.Name;
            // Strategy 1: type name contains "Rest" (most common)
            if (typeName.Contains("Rest", StringComparison.OrdinalIgnoreCase))
                return true;
            // Strategy 2: full type name contains Rest (qualified name check)
            if (optType.FullName?.Contains("Rest", StringComparison.OrdinalIgnoreCase) == true)
                return true;
            // Strategy 3: check base type hierarchy for Rest
            var baseType = optType.BaseType;
            while (baseType != null)
            {
                if (baseType.Name.Contains("Rest", StringComparison.OrdinalIgnoreCase))
                    return true;
                baseType = baseType.BaseType;
            }
            return false;
        });
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
