using TokenSpire2.Core;
using Xunit;

namespace TokenSpire2.Core.Tests;

public sealed class TurnReadinessGateTests
{
    [Fact]
    public void ZeroEnergyBeforeAnyActionWaitsForTurnRefresh()
    {
        var gate = new TurnReadinessGate(requiredStableSamples: 3);
        var snapshot = Snapshot(energy: 0, playable: 0, hasPositiveCostCards: true);

        for (var i = 0; i < 5; i++)
            Assert.Equal(TurnReadinessKind.Wait, gate.Observe(snapshot).Kind);
    }

    [Fact]
    public void StableFullEnergyPlayableHandCanSolve()
    {
        var gate = new TurnReadinessGate(3);
        var snapshot = Snapshot(energy: 3, playable: 4, hasPositiveCostCards: true);

        Assert.Equal(TurnReadinessKind.Wait, gate.Observe(snapshot).Kind);
        Assert.Equal(TurnReadinessKind.Wait, gate.Observe(snapshot).Kind);
        Assert.Equal(TurnReadinessKind.Solve, gate.Observe(snapshot).Kind);
    }

    [Fact]
    public void SpentEnergyAfterSuccessfulActionCanEndTurn()
    {
        var gate = new TurnReadinessGate(3);
        var snapshot = Snapshot(
            energy: 0,
            playable: 0,
            hasPositiveCostCards: true,
            hasActedThisTurn: true);

        gate.Observe(snapshot);
        gate.Observe(snapshot);
        Assert.Equal(TurnReadinessKind.AllowEndTurn, gate.Observe(snapshot).Kind);
    }

    [Fact]
    public void TrulyUnplayableZeroCostHandCanEndTurn()
    {
        var gate = new TurnReadinessGate(3);
        var snapshot = Snapshot(energy: 0, playable: 0, hasPositiveCostCards: false);

        gate.Observe(snapshot);
        gate.Observe(snapshot);
        Assert.Equal(TurnReadinessKind.AllowEndTurn, gate.Observe(snapshot).Kind);
    }

    [Fact]
    public void EnergyChangeRestartsStabilityCount()
    {
        var gate = new TurnReadinessGate(3);
        var zero = Snapshot(energy: 0, playable: 0, hasPositiveCostCards: true);
        var ready = Snapshot(energy: 3, playable: 3, hasPositiveCostCards: true);

        gate.Observe(zero);
        gate.Observe(zero);
        Assert.Equal(TurnReadinessKind.Wait, gate.Observe(ready).Kind);
        Assert.Equal(TurnReadinessKind.Wait, gate.Observe(ready).Kind);
        Assert.Equal(TurnReadinessKind.Solve, gate.Observe(ready).Kind);
    }

    [Fact]
    public void SubmittedEndTurnNeverSolvesAgain()
    {
        var gate = new TurnReadinessGate(1);
        var snapshot = Snapshot(
            energy: 3,
            playable: 4,
            hasPositiveCostCards: true,
            endTurnRequested: true);

        Assert.Equal(TurnReadinessKind.Wait, gate.Observe(snapshot).Kind);
    }

    private static TurnSnapshot Snapshot(
        int energy,
        int playable,
        bool hasPositiveCostCards,
        bool hasActedThisTurn = false,
        bool endTurnRequested = false) => new(
            TurnNumber: 1,
            Energy: energy,
            HandCount: 5,
            DrawCount: 5,
            DiscardCount: 0,
            PlayableCount: playable,
            HasPositiveCostCards: hasPositiveCostCards,
            HasActedThisTurn: hasActedThisTurn,
            PlayerActionsDisabled: false,
            ActionQueueIdle: true,
            DrawComplete: true,
            EndTurnRequested: endTurnRequested);
}
