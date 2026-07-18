using Xunit;

namespace TokenSpire2.Core.Tests;

public sealed class AutoSlayIntegrationTests
{
    [Fact]
    public void CardSelectorRegistrationIsControlledByAutomationPolicy()
    {
        var source = File.ReadAllText(FindRepoFile("src", "AutoSlayNode.cs"));

        Assert.Contains("Allows(AutomationAction.RegisterCardSelector)", source);
        Assert.Contains("Global card selector NOT registered; synchronized overlays only", source);
    }

    [Fact]
    public void SolverEntryUsesTurnReadinessGate()
    {
        var source = File.ReadAllText(FindRepoFile("src", "AutoSlayNode.cs"));

        Assert.Contains("_turnReadinessGate.Observe", source);
        Assert.Contains("waiting-for-energy-refresh", source);
    }

    [Fact]
    public void EmptyCombatPlanIsNotTreatedAsBusyActionQueue()
    {
        var source = File.ReadAllText(FindRepoFile("src", "AutoSlayNode.cs"));

        Assert.Contains("_combatPlan == null || _combatPlan.Count == 0", source);
    }

    [Fact]
    public void BundleSelectionUsesNativeClickedSignalInsteadOfPressed()
    {
        var source = File.ReadAllText(
            FindRepoFile("src", "Solver", "BundleDecider.cs"));

        Assert.Contains("pick.EmitSignalClicked();", source);
        Assert.DoesNotContain("pick.EmitSignal(\"pressed\")", source);
    }

    private static string FindRepoFile(params string[] parts)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            var candidate = Path.Combine([current.FullName, .. parts]);
            if (File.Exists(candidate))
                return candidate;
            current = current.Parent;
        }
        throw new FileNotFoundException(string.Join(Path.DirectorySeparatorChar, parts));
    }
}
