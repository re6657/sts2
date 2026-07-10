using System;
using System.Threading;
using System.Threading.Tasks;
using LocalCoop.Mod.Runtime;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Connection;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Entities.Multiplayer;

namespace TokenSpire2.Multiplayer;

/// <summary>
/// SINGLE CODE PATH for broker join handshake.
///
/// THE KEY FIX for the 3-player lobby bug:
/// BeginStandardBrokerJoinAsync does NOT send ClientLobbyJoinRequestMessage.
/// It only: creates transport → waits for InitialGameInfo → stores service.
///
/// The ONE AND ONLY join request is sent later by the game's
/// InitializeMultiplayerAsClient through BrokerBackedNetService.
/// </summary>
public static class MpJoinFlow
{
    private static int _joinInProgress;
    private static JoinResult? _cachedResult;
    private static readonly object _lock = new();

    public static bool IsJoinInProgress =>
        Interlocked.CompareExchange(ref _joinInProgress, 0, 0) == 1;

    public static bool HasJoinCompleted
    {
        get { lock (_lock) return _cachedResult != null; }
    }

    /// <summary>
    /// Execute the broker join handshake.
    /// Concurrent-call guard: returns cached result if already completed.
    /// </summary>
    public static async Task<JoinResult> ExecuteJoinAsync(
        BrokerModeSettings settings,
        Func<IBrokerEnvelopeTransport> createTransport,
        Action<string>? log = null,
        CancellationToken cancellation = default)
    {
        lock (_lock)
        {
            if (_cachedResult != null)
            {
                Log("[MpJoinFlow] Join already completed, returning cached result.");
                return _cachedResult!.Value;
            }
        }

        if (Interlocked.CompareExchange(ref _joinInProgress, 1, 0) != 0)
        {
            Log("[MpJoinFlow] Join in progress — waiting for completion.");
            for (int i = 0; i < 300 && _cachedResult == null; i++)
                await Task.Delay(100, cancellation);
            lock (_lock)
            {
                if (_cachedResult != null)
                    return _cachedResult!.Value;
            }
            throw new TimeoutException("Timed out waiting for concurrent join.");
        }

        try
        {
            Log("[MpJoinFlow] Starting broker handshake (no join request sent)...");
            var result = await BrokerClientJoinFlow.BeginStandardBrokerJoinAsync(
                settings, createTransport, log, cancellation);

            lock (_lock) { _cachedResult = result; }
            Log("[MpJoinFlow] Broker handshake complete (JoinRequest NOT sent here).");
            return result;
        }
        catch (Exception ex)
        {
            Log($"[MpJoinFlow] ERROR: {ex.Message}");
            throw;
        }
        finally
        {
            Interlocked.Exchange(ref _joinInProgress, 0);
        }
    }

    public static void Reset()
    {
        lock (_lock) { _cachedResult = null; }
        Interlocked.Exchange(ref _joinInProgress, 0);
        Log("[MpJoinFlow] Reset.");
    }

    private static void Log(string msg)
    {
        try { MainFile.Logger?.Info(msg); }
        catch { }
    }
}
