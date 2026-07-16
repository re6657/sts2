using TokenSpire2.Core;
using Xunit;

namespace TokenSpire2.Core.Tests;

public sealed class MapVoteCycleGateTests
{
    [Fact]
    public void Multiplayer_vote_stays_locked_while_same_map_remains_open()
    {
        var gate = new MapVoteCycleGate();
        Assert.True(gate.CanVote(isMultiplayer: true));
        gate.MarkVoteSubmitted(isMultiplayer: true);
        gate.ObserveMapVisibility(isOpen: true);
        Assert.False(gate.CanVote(isMultiplayer: true));
    }

    [Fact]
    public void Multiplayer_vote_unlocks_only_after_map_was_closed()
    {
        var gate = new MapVoteCycleGate();
        gate.MarkVoteSubmitted(isMultiplayer: true);
        gate.ObserveMapVisibility(isOpen: false);
        gate.ObserveMapVisibility(isOpen: true);
        Assert.True(gate.CanVote(isMultiplayer: true));
    }

    [Fact]
    public void Singleplayer_is_not_latched()
    {
        var gate = new MapVoteCycleGate();
        gate.MarkVoteSubmitted(isMultiplayer: false);
        Assert.True(gate.CanVote(isMultiplayer: false));
    }
}
