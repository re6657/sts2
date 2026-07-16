namespace TokenSpire2.Core;

/// <summary>Allows at most one multiplayer map vote per map-open cycle.</summary>
public sealed class MapVoteCycleGate
{
    private bool _multiplayerVoteSubmitted;

    public bool CanVote(bool isMultiplayer) => !isMultiplayer || !_multiplayerVoteSubmitted;

    public void MarkVoteSubmitted(bool isMultiplayer)
    {
        if (isMultiplayer) _multiplayerVoteSubmitted = true;
    }

    public void ObserveMapVisibility(bool isOpen)
    {
        if (!isOpen) _multiplayerVoteSubmitted = false;
    }

    public void Reset() => _multiplayerVoteSubmitted = false;
}
