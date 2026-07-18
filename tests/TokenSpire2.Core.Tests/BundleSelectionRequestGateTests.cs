using TokenSpire2.Core;
using Xunit;

namespace TokenSpire2.Core.Tests;

public sealed class BundleSelectionRequestGateTests
{
    [Fact]
    public void ExhaustedGateDoesNotIssueFurtherRecoveryInputs()
    {
        var gate = new BundleSelectionRequestGate();

        Assert.Equal(BundleSelectionInput.Clicked, gate.Tick(false, false));
        gate.RecordClickedResult(false);
        Assert.Equal(BundleSelectionInput.HitboxFallback, AdvanceToHitbox(gate));
        Assert.Equal(BundleSelectionInput.None, gate.Tick(false, false));

        Assert.Equal(BundleSelectionInput.Clicked, gate.Tick(false, true));
        gate.RecordClickedResult(false);
        Assert.Equal(BundleSelectionInput.HitboxFallback, AdvanceToHitbox(gate));
        Assert.Equal(BundleSelectionInput.None, gate.Tick(false, false));

        Assert.Equal(BundleSelectionInput.Clicked, gate.Tick(false, true));
        gate.RecordClickedResult(false);
        Assert.Equal(BundleSelectionInput.HitboxFallback, AdvanceToHitbox(gate));

        Assert.Equal(BundleSelectionInput.Exhausted, gate.Tick(false, true));
        Assert.Equal(BundleSelectionInput.None, gate.Tick(true, true));
        Assert.Equal(BundleSelectionInput.None, gate.Tick(true, true));
        Assert.Equal(BundleSelectionInput.None, gate.Tick(false, false));
        Assert.True(gate.Exhausted);
        Assert.Equal(3, gate.CycleCount);
    }

    [Fact]
    public void HitboxFailureStartsControlledNextCycle()
    {
        var gate = new BundleSelectionRequestGate();

        Assert.Equal(BundleSelectionInput.Clicked, gate.Tick(false, false));
        gate.RecordClickedResult(false);
        Assert.Equal(BundleSelectionInput.HitboxFallback, AdvanceToHitbox(gate));

        gate.ReportInputFailed();

        Assert.Equal(BundleSelectionInput.Clicked, gate.Tick(true, false));
        gate.RecordClickedResult(false);
        Assert.Equal(BundleSelectionInput.None, gate.Tick(false, false));
    }

    [Fact]
    public void FirstClickNearTimeoutLeavesTheNextTickInTheCurrentCycle()
    {
        var gate = new BundleSelectionRequestGate();

        Assert.Equal(BundleSelectionInput.Clicked, gate.Tick(false, false));
        gate.RecordClickedResult(false);

        Assert.Equal(BundleSelectionInput.None, gate.Tick(true, false));
        Assert.Equal(1, gate.CycleCount);
    }

    [Fact]
    public void LateBundleStillUsesRequestAgeAndFallbackOnlyOnce()
    {
        var gate = new BundleSelectionRequestGate();

        Assert.Equal(BundleSelectionInput.Clicked, gate.Tick(false, false));
        gate.RecordClickedResult(false);

        for (var i = 0; i < 6; i++)
            Assert.Equal(BundleSelectionInput.None, gate.Tick(false, false));

        Assert.Equal(BundleSelectionInput.HitboxFallback, gate.Tick(true, false));
        Assert.Equal(BundleSelectionInput.None, gate.Tick(true, false));
        Assert.Equal(BundleSelectionInput.Clicked, gate.Tick(true, true));
        gate.RecordClickedResult(false);
    }

    [Fact]
    public void TimeoutTickReturnsOnlyClickedBeforeNextFallbackWindow()
    {
        var gate = new BundleSelectionRequestGate();

        Assert.Equal(BundleSelectionInput.Clicked, gate.Tick(true, false));
        gate.RecordClickedResult(false);

        Assert.Equal(BundleSelectionInput.Clicked, gate.Tick(true, true));
        gate.RecordClickedResult(false);
        Assert.Equal(BundleSelectionInput.None, gate.Tick(false, false));
    }

    private static BundleSelectionInput AdvanceToHitbox(BundleSelectionRequestGate gate)
    {
        for (var i = 0; i < BundleSelectionRequestGate.HitboxFallbackFrame - 1; i++)
            Assert.Equal(BundleSelectionInput.None, gate.Tick(true, false));

        return gate.Tick(true, false);
    }
}
