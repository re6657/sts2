using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Quality;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Multiplayer.Transport;

namespace LocalCoop.Mod.Runtime;

public sealed class BrokerNetGameService : INetHostGameService, IDisposable
{
    private static readonly AsyncLocal<int> NativeMessageHandlerDispatchDepth = new();

    private readonly BrokerBackedNetService _inner;
    private readonly NetGameType _type;
    private readonly CancellationTokenSource _receiveLoopCancellation = new();
    private readonly Task _receiveLoop;
    private readonly Dictionary<Delegate, Delegate> _registeredHandlers = new();
    private Action<NetErrorInfo>? _disconnected;
    private Action<ulong>? _clientConnected;
    private Action<ulong, NetErrorInfo>? _clientDisconnected;
    private HashSet<ulong> _lastConnectedPeerIds;
    private int _disposed;

    public BrokerNetGameService(BrokerBackedNetService inner, NetGameType type)
    {
        _inner = inner;
        _type = type;
        _lastConnectedPeerIds = [];
        _inner.PeerTracked += HandlePeerTracked;
        _receiveLoop = _inner.RunReceiveLoopAsync(_receiveLoopCancellation.Token);
    }

    public ulong NetId => _inner.NetId;

    public bool IsConnected => _inner.IsConnected;

    public bool IsGameLoading => _inner.IsGameLoading;

    public NetGameType Type => _type;

    public PlatformType Platform => PlatformType.None;

    internal static bool IsDispatchingNativeMessageHandler => NativeMessageHandlerDispatchDepth.Value > 0;

    public static bool IsDispatchingNativeMessageHandlerForTesting => IsDispatchingNativeMessageHandler;

    public IReadOnlyList<NetClientData> ConnectedPeers =>
        _inner.ConnectedPeers
            .Select(peer => new NetClientData
            {
                peerId = peer.PeerId,
                readyForBroadcasting = peer.ReadyForBroadcasting
            })
            .ToArray();

    public NetHost NetHost => null!;

    public event Action<NetErrorInfo>? Disconnected
    {
        add => _disconnected += value;
        remove => _disconnected -= value;
    }

    public event Action<ulong>? ClientConnected
    {
        add => _clientConnected += value;
        remove => _clientConnected -= value;
    }

    public event Action<ulong, NetErrorInfo>? ClientDisconnected
    {
        add => _clientDisconnected += value;
        remove => _clientDisconnected -= value;
    }

    public void SendMessage<T>(T message, ulong playerId)
        where T : INetMessage
    {
        _inner.SendMessage(message, playerId);
    }

    public void SendMessage<T>(T message)
        where T : INetMessage
    {
        _inner.SendMessage(message);
    }

    public void RegisterMessageHandler<T>(MessageHandlerDelegate<T> messageHandlerDelegate)
        where T : INetMessage
    {
        Action<T, ulong> adapter = (message, senderId) =>
            InvokeWithLocalContext(typeof(T), senderId, message, () => messageHandlerDelegate(message, senderId));
        _registeredHandlers[messageHandlerDelegate] = adapter;
        _inner.RegisterMessageHandler(adapter);
    }

    public void UnregisterMessageHandler<T>(MessageHandlerDelegate<T> messageHandlerDelegate)
        where T : INetMessage
    {
        if (!_registeredHandlers.Remove(messageHandlerDelegate, out var adapter))
        {
            return;
        }

        _inner.UnregisterMessageHandler((Action<T, ulong>)adapter);
    }

    public void Update()
    {
        var before = _lastConnectedPeerIds;
        _inner.Update();
        var after = _inner.ConnectedPeerIds.ToHashSet();
        foreach (var peerId in after.Except(before))
        {
            NotifyClientConnected(peerId);
        }

        foreach (var peerId in before.Except(after))
        {
            _clientDisconnected?.Invoke(peerId, default);
        }

        _lastConnectedPeerIds = after;
    }

    public void Disconnect(NetError reason, bool now)
    {
        _inner.Disconnect();
        Dispose();
    }

    public void DisconnectClient(ulong peerId, NetError reason, bool now)
    {
    }

    public void SetPeerReadyForBroadcasting(ulong peerId)
    {
        _inner.SetPeerReadyForBroadcasting(peerId);
    }

    public ConnectionStats GetStatsForPeer(ulong peerId)
    {
        return new ConnectionStats(peerId);
    }

    public void SetGameLoading(bool isLoading)
    {
        _inner.SetGameLoading(isLoading);
    }

    public void SetBufferMessages(bool buffer)
    {
        // Added for compatibility with newer game versions (>v0.103.3)
        // The local broker doesn't need message buffering
    }

    public string GetRawLobbyIdentifier()
    {
        return _inner.GetRawLobbyIdentifier();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _inner.PeerTracked -= HandlePeerTracked;
        _receiveLoopCancellation.Cancel();
        _receiveLoopCancellation.Dispose();
    }

    public static IDisposable EnterNativeMessageHandlerDispatchForTesting()
    {
        return EnterNativeMessageHandlerDispatch();
    }

    private void HandlePeerTracked(ulong peerId)
    {
        NotifyClientConnected(peerId);
    }

    private void NotifyClientConnected(ulong peerId)
    {
        if (!_lastConnectedPeerIds.Add(peerId))
        {
            return;
        }

        _clientConnected?.Invoke(peerId);
    }

    private void InvokeWithLocalContext(Type messageType, ulong senderId, object? message, Action handler)
    {
        // ── StateDivergenceMessage diagnostic logging ──────────────────
        // When the game engine detects a checksum mismatch between instances,
        // it sends a StateDivergenceMessage. Log the current game state to
        // help diagnose what caused the divergence.
        if (messageType.Name.Contains("StateDivergence", StringComparison.Ordinal))
        {
            LogStateDivergence(NetId, senderId, messageType, message);
        }

        if (messageType.Name.Contains("MerchantCardRemovalMessage", StringComparison.Ordinal))
        {
            RunIdentityDiagnostics.StartCorrelation("shop-remove-message");
        }
        else if (messageType.Name.Contains("RewardObtainedMessage", StringComparison.Ordinal))
        {
            RunIdentityDiagnostics.StartCorrelation("reward-message");
        }
        else if (messageType.Name.Contains("PlayerChoiceMessage", StringComparison.Ordinal))
        {
            RunIdentityDiagnostics.EnsureCorrelation("player-choice-message");
        }

        RunIdentityDiagnostics.LogBrokerHandler("enter", NetId, Type, messageType, senderId, message);
        LocalContext.NetId = NetId;
        try
        {
            using var dispatchScope = EnterNativeMessageHandlerDispatch();
            handler();
        }
        finally
        {
            LocalContext.NetId = NetId;
            RunIdentityDiagnostics.LogBrokerHandler("exit", NetId, Type, messageType, senderId, message);
        }
    }

    /// <summary>
    /// Log detailed diagnostic information when a StateDivergenceMessage is received.
    /// This helps identify what caused the game state to diverge between instances.
    /// </summary>
    private static void LogStateDivergence(ulong localNetId, ulong senderId, Type messageType, object? message)
    {
        try
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"[DESYNC] StateDivergenceMessage received! senderId={senderId}, localNetId={localNetId}");

            // Log message details via reflection
            if (message != null)
            {
                sb.AppendLine($"[DESYNC] Message type: {messageType.FullName}");
                foreach (var prop in message.GetType().GetProperties(
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                {
                    try
                    {
                        var val = prop.GetValue(message);
                        sb.AppendLine($"[DESYNC]   {prop.Name} = {val}");
                    }
                    catch { }
                }
            }

            // Log what we can safely access without pulling in heavy game dependencies
            try
            {
                // Try to detect current screen via reflection
                var screenProp = message?.GetType().GetProperty("Screen");
                if (screenProp != null)
                {
                    sb.AppendLine($"[DESYNC] Reported screen: {screenProp.GetValue(message)}");
                }
            }
            catch { }

            // Write to both the mod logger and stderr
            try { TokenSpire2.MainFile.Logger?.Error(sb.ToString()); } catch { }
            try { System.Console.Error.WriteLine(sb.ToString()); } catch { }
        }
        catch { /* never let diagnostics crash the handler */ }
    }

    private static IDisposable EnterNativeMessageHandlerDispatch()
    {
        NativeMessageHandlerDispatchDepth.Value++;
        return new NativeMessageHandlerDispatchScope();
    }

    private sealed class NativeMessageHandlerDispatchScope : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            NativeMessageHandlerDispatchDepth.Value = Math.Max(0, NativeMessageHandlerDispatchDepth.Value - 1);
        }
    }
}
