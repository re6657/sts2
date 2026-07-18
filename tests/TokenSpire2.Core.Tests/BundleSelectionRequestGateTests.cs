using TokenSpire2.Core;
using Xunit;

namespace TokenSpire2.Core.Tests;

public sealed class BundleSelectionRequestGateTests
{
    [Fact]
    public void FirstTickRequestsClickedOnlyOnce()
    {
        var gate = new BundleSelectionRequestGate();

        Assert.Equal(BundleSelectionInput.Clicked, gate.Tick(false, false));
        Assert.True(gate.Attempted);
        Assert.False(gate.Accepted);
        gate.RecordClickedResult(true);
        Assert.True(gate.Accepted);

        Assert.Equal(BundleSelectionInput.None, gate.Tick(false, false));
        Assert.Equal(BundleSelectionInput.None, gate.Tick(false, false));
    }

    [Fact]
    public void NonOkClickedResultIsNotRetried()
    {
        var gate = new BundleSelectionRequestGate();

        Assert.Equal(BundleSelectionInput.Clicked, gate.Tick(false, false));
        gate.RecordClickedResult(false);
        Assert.True(gate.Attempted);
        Assert.False(gate.Accepted);

        for (var i = 0; i < 5; i++)
            Assert.Equal(BundleSelectionInput.None, gate.Tick(false, false));
    }

    [Fact]
    public void FallbackHappensAfterSixWaitingTicks()
    {
        var gate = new BundleSelectionRequestGate();

        Assert.Equal(BundleSelectionInput.Clicked, gate.Tick(false, false));
        gate.RecordClickedResult(false);

        for (var i = 0; i < 5; i++)
            Assert.Equal(BundleSelectionInput.None, gate.Tick(true, false));

        Assert.Equal(BundleSelectionInput.HitboxFallback, gate.Tick(true, false));
    }

    [Fact]
    public void LateBundleDoesNotResetRequestAge()
    {
        var gate = new BundleSelectionRequestGate();

        Assert.Equal(BundleSelectionInput.Clicked, gate.Tick(false, false));
        gate.RecordClickedResult(false);

        for (var i = 0; i < 6; i++)
            Assert.Equal(BundleSelectionInput.None, gate.Tick(false, false));

        Assert.Equal(BundleSelectionInput.HitboxFallback, gate.Tick(true, false));
    }

    [Fact]
    public void FallbackIsSentOnlyOnce()
    {
        var gate = new BundleSelectionRequestGate();

        Assert.Equal(BundleSelectionInput.Clicked, gate.Tick(false, false));
        gate.RecordClickedResult(false);

        for (var i = 0; i < 6; i++)
            gate.Tick(true, false);

        Assert.Equal(BundleSelectionInput.None, gate.Tick(true, false));
    }

    [Fact]
    public void TimeoutRecoveryProducesAtMostOneInput()
    {
        var gate = new BundleSelectionRequestGate();

        Assert.Equal(BundleSelectionInput.Clicked, gate.Tick(true, true));
        gate.RecordClickedResult(false);

        Assert.Equal(BundleSelectionInput.HitboxFallback, gate.Tick(true, true));
        Assert.Equal(BundleSelectionInput.None, gate.Tick(true, true));
    }
}
