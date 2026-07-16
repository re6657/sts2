namespace TokenSpire2.Core;

public enum TurnReadinessKind
{
    Wait,
    Solve,
    AllowEndTurn,
}

public readonly record struct TurnSnapshot(
    int TurnNumber,
    int Energy,
    int HandCount,
    int DrawCount,
    int DiscardCount,
    int PlayableCount,
    bool HasPositiveCostCards,
    bool HasActedThisTurn,
    bool PlayerActionsDisabled,
    bool ActionQueueIdle,
    bool DrawComplete,
    bool EndTurnRequested);

public readonly record struct TurnReadinessDecision(
    TurnReadinessKind Kind,
    string Reason,
    int StableSamples);

/// <summary>
/// Requires several identical observations before the solver may act. This
/// prevents a client from solving between turn-start draw and energy-refresh
/// actions while still allowing a player that spent all energy to end turn.
/// </summary>
public sealed class TurnReadinessGate
{
    private readonly int _requiredStableSamples;
    private TurnSnapshot? _lastSnapshot;
    private int _stableSamples;

    public TurnReadinessGate(int requiredStableSamples = 3)
    {
        if (requiredStableSamples < 1)
            throw new ArgumentOutOfRangeException(nameof(requiredStableSamples));
        _requiredStableSamples = requiredStableSamples;
    }

    public TurnReadinessDecision Observe(TurnSnapshot snapshot)
    {
        if (_lastSnapshot is { } previous && previous == snapshot)
            _stableSamples++;
        else
        {
            _lastSnapshot = snapshot;
            _stableSamples = 1;
        }

        if (snapshot.EndTurnRequested)
            return Wait("end-turn-already-requested");
        if (snapshot.PlayerActionsDisabled)
            return Wait("player-actions-disabled");
        if (!snapshot.ActionQueueIdle)
            return Wait("action-queue-busy");
        if (!snapshot.DrawComplete)
            return Wait("draw-incomplete");
        if (!snapshot.HasActedThisTurn && snapshot.Energy == 0 && snapshot.HasPositiveCostCards)
            return Wait("waiting-for-energy-refresh");
        if (_stableSamples < _requiredStableSamples)
            return Wait("state-not-stable");

        if (snapshot.PlayableCount > 0)
            return new TurnReadinessDecision(TurnReadinessKind.Solve, "stable-playable-hand", _stableSamples);

        return new TurnReadinessDecision(TurnReadinessKind.AllowEndTurn, "stable-no-playable-cards", _stableSamples);
    }

    public void Reset()
    {
        _lastSnapshot = null;
        _stableSamples = 0;
    }

    private TurnReadinessDecision Wait(string reason) =>
        new(TurnReadinessKind.Wait, reason, _stableSamples);
}
