using TokenSpire2.Core;
using Xunit;

namespace TokenSpire2.Core.Tests;

public sealed class AutomationPolicyTests
{
    [Fact]
    public void MultiplayerHostRejectsEveryAutomationAction()
    {
        var policy = new AutomationPolicy(
            multiplayer: true,
            isHost: true,
            autoNavigate: true,
            autoBattle: true,
            autoEvent: true);

        foreach (var action in Enum.GetValues<AutomationAction>())
            Assert.False(policy.Allows(action), $"Host unexpectedly allowed {action}");
    }

    [Fact]
    public void MultiplayerBotAllowsConfiguredAutomation()
    {
        var policy = new AutomationPolicy(true, false, true, true, true);

        Assert.True(policy.Allows(AutomationAction.RegisterCardSelector));
        Assert.True(policy.Allows(AutomationAction.Combat));
        Assert.True(policy.Allows(AutomationAction.CardGrid));
        Assert.True(policy.Allows(AutomationAction.Map));
    }

    [Fact]
    public void SoloTogglesRemainIndependent()
    {
        var policy = new AutomationPolicy(false, false, true, false, true);

        Assert.False(policy.Allows(AutomationAction.Combat));
        Assert.True(policy.Allows(AutomationAction.Map));
        Assert.True(policy.Allows(AutomationAction.Event));
    }
}
