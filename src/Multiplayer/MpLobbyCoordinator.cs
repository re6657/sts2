using System;

namespace TokenSpire2.Multiplayer;

/// <summary>
/// Coordinates the multiplayer lobby lifecycle.
/// Replaces BrokerForceLobbyTransitionPatch's reflection-based
/// AreAllPlayersReady with message-driven detection.
///
/// Heartbeat: Client sends heartbeat every 5s via broker.
/// Host checks every 10s. If no heartbeat for 60s → disconnect.
/// </summary>
public class MpLobbyCoordinator
{
    private LobbyPhase _phase = LobbyPhase.Disconnected;
    private int _playerCount;
    private int _readyPlayerCount;
    private DateTime _phaseEnteredAt = DateTime.UtcNow;

    private DateTime _lastHeartbeatUtc = DateTime.MinValue;
    private const double HEARTBEAT_WARN_SECONDS = 30.0;
    private const double HEARTBEAT_TIMEOUT_SECONDS = 60.0;
    private bool _heartbeatTimedOut;

    public event Action<LobbyPhase, LobbyPhase>? OnPhaseChanged;
    public event Action<int>? OnPlayerCountChanged;
    public event Action<bool>? OnAllPlayersReady;
    public event Action? OnHeartbeatTimeout;

    public LobbyPhase CurrentPhase => _phase;
    public int PlayerCount => _playerCount;
    public int ReadyPlayerCount => _readyPlayerCount;
    public bool AllPlayersReady => _readyPlayerCount >= _playerCount && _playerCount >= 2;
    public TimeSpan TimeInPhase => DateTime.UtcNow - _phaseEnteredAt;
    public bool IsHeartbeatAlive => !_heartbeatTimedOut;

    /// <summary>Call when a heartbeat message arrives from a peer.</summary>
    public void RecordHeartbeat()
    {
        _lastHeartbeatUtc = DateTime.UtcNow;
        _heartbeatTimedOut = false;
    }

    /// <summary>
    /// Check heartbeat health. Returns true if peer is alive.
    /// Fires OnHeartbeatTimeout when peer exceeds timeout.
    /// </summary>
    public bool CheckHeartbeat()
    {
        if (_lastHeartbeatUtc == DateTime.MinValue)
            return true;

        var elapsed = (DateTime.UtcNow - _lastHeartbeatUtc).TotalSeconds;

        if (elapsed > HEARTBEAT_TIMEOUT_SECONDS && !_heartbeatTimedOut)
        {
            _heartbeatTimedOut = true;
            Log($"[MpLobbyCoordinator] Heartbeat TIMEOUT after {elapsed:F0}s — peer is dead.");
            OnHeartbeatTimeout?.Invoke();
            return false;
        }

        if (elapsed > HEARTBEAT_WARN_SECONDS)
        {
            Log($"[MpLobbyCoordinator] Heartbeat WARNING: {elapsed:F0}s since last heartbeat.");
        }

        return !_heartbeatTimedOut;
    }

    public void OnEnteredLobby(int playerCount)
    {
        TransitionTo(LobbyPhase.InLobby);
        _playerCount = playerCount;
        _readyPlayerCount = 0;
        OnPlayerCountChanged?.Invoke(_playerCount);
    }

    public void OnPlayerReadyChanged(int readyCount)
    {
        _readyPlayerCount = readyCount;
        OnAllPlayersReady?.Invoke(AllPlayersReady);
        Log($"[MpLobbyCoordinator] Ready: {_readyPlayerCount}/{_playerCount}");
    }

    public void OnRunStarted()
    {
        TransitionTo(LobbyPhase.InGame);
    }

    public void OnDisconnected(string? reason = null)
    {
        TransitionTo(LobbyPhase.Disconnected);
        Log($"[MpLobbyCoordinator] Disconnected: {reason ?? "unknown"}");
    }

    public void Reset()
    {
        _phase = LobbyPhase.Disconnected;
        _playerCount = 0;
        _readyPlayerCount = 0;
        _phaseEnteredAt = DateTime.UtcNow;
        _lastHeartbeatUtc = DateTime.MinValue;
        _heartbeatTimedOut = false;
    }

    private void TransitionTo(LobbyPhase newPhase)
    {
        if (newPhase == _phase) return;
        var old = _phase;
        _phase = newPhase;
        _phaseEnteredAt = DateTime.UtcNow;
        Log($"[MpLobbyCoordinator] Phase: {old} → {newPhase}");
        OnPhaseChanged?.Invoke(old, newPhase);
    }

    private static void Log(string msg)
    {
        try { MainFile.Logger?.Info(msg); }
        catch { }
    }
}

public enum LobbyPhase
{
    Disconnected,
    InLobby,
    InGame,
}
