using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Connection;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Lobby;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

namespace LocalCoop.Mod.Runtime;

/// <summary>
/// Client-side broker join logic.
///
/// Called from BrokerClientJoinFlowPatch.Prefix when the game invokes
/// JoinFlow.Begin on the client instance. Performs the broker handshake:
///   1. Create broker transport + service
///   2. Wait for host's InitialGameInfoMessage
///   3. Send ClientLobbyJoinRequestMessage (the ONE real join request)
///   4. Wait for ClientLobbyJoinResponseMessage
///   5. Stash response for duplicate suppression
///   6. Store service in BrokerPendingNetGameServiceRegistry
///   7. Return JoinResult with the real join response
///
/// The game may later call InitializeMultiplayerAsClient, which tries to send
/// a duplicate ClientLobbyJoinRequestMessage. BrokerBackedNetService drops it
/// and replays the stashed response — preventing the 3-player bug.
///
/// This is the SINGLE code path for broker client joins.
/// </summary>
public static class BrokerClientJoinFlow
{
    public static bool ShouldUseBrokerJoin(BrokerModeSettings settings)
    {
        return settings.Enabled
            && settings.Config is not null;
    }

    public static ClientLobbyJoinRequestMessage CreateJoinRequest(
        int maxAscensionUnlocked,
        SerializableUnlockState unlockState)
    {
        return new ClientLobbyJoinRequestMessage
        {
            maxAscensionUnlocked = maxAscensionUnlocked,
            unlockState = unlockState
        };
    }

    public static ClientLobbyJoinRequestMessage CreateJoinRequestFromCurrentSave(Action<string>? log)
    {
        var maxAscensionUnlocked = 0;
        var unlockState = new SerializableUnlockState();

        try
        {
            var saveManagerType = ResolveType("MegaCrit.Sts2.Core.Saves.SaveManager");
            var saveManager = saveManagerType?.GetProperty("Instance", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)?.GetValue(null);
            if (saveManager is null)
            {
                log?.Invoke("Broker client lobby handshake: SaveManager unavailable; using default join request progress.");
                return CreateJoinRequest(maxAscensionUnlocked, unlockState);
            }

            var progress = saveManager.GetType().GetProperty("Progress", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)?.GetValue(saveManager);
            var maxAscension = progress?.GetType().GetProperty("MaxMultiplayerAscension", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)?.GetValue(progress);
            if (maxAscension is int parsedMaxAscension)
            {
                maxAscensionUnlocked = parsedMaxAscension;
            }

            var generatedUnlockState = saveManager.GetType()
                .GetMethod("GenerateUnlockStateFromProgress", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                ?.Invoke(saveManager, null);
            var serializableUnlockState = generatedUnlockState?.GetType()
                .GetMethod("ToSerializable", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                ?.Invoke(generatedUnlockState, null);
            if (serializableUnlockState is SerializableUnlockState parsedUnlockState)
            {
                unlockState = parsedUnlockState;
            }
        }
        catch (Exception exception)
        {
            log?.Invoke($"Broker client lobby handshake: failed to read save progress; using default join request progress: {exception.GetType().Name}: {exception.Message}");
        }

        return CreateJoinRequest(maxAscensionUnlocked, unlockState);
    }

    /// <summary>
    /// Executes the broker join setup:
    ///   1. Create broker transport + service
    ///   2. Wait for host's InitialGameInfoMessage
    ///   3. Send ClientLobbyJoinRequestMessage
    ///   4. Wait for ClientLobbyJoinResponseMessage
    ///   5. Stash the response for duplicate suppression
    ///   6. Store service in BrokerPendingNetGameServiceRegistry
    ///   7. Return JoinResult with the real join response
    ///
    /// InitializeMultiplayerAsClient may send a duplicate join request
    /// through the substituted service, which is suppressed with a
    /// replayed stashed response — only 2 players in lobby.
    /// </summary>
    public static async Task<JoinResult> BeginStandardBrokerJoinAsync(
        BrokerModeSettings settings,
        Func<IBrokerEnvelopeTransport> createTransport,
        Action<string>? log,
        CancellationToken cancellationToken,
        Func<ClientLobbyJoinRequestMessage>? createJoinRequest = null)
    {
        if (!ShouldUseBrokerJoin(settings))
        {
            throw new InvalidOperationException("Broker join was requested while broker mode is disabled.");
        }

        var inner = BrokerNetServiceFactory.TryCreate(
            settings,
            createTransport(),
            log,
            BrokerClientRole.Client) ?? throw new InvalidOperationException("Broker client service could not be created.");
        var service = new BrokerNetGameService(inner, NetGameType.Client);
        var initialInfoSource = new TaskCompletionSource<InitialGameInfoMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var responseSource = new TaskCompletionSource<ClientLobbyJoinResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        Action<InitialGameInfoMessage> initialInfoHandler = initialInfo => initialInfoSource.TrySetResult(initialInfo);
        Action<ClientLobbyJoinResponseMessage> responseHandler = response => responseSource.TrySetResult(response);
        using var cancellationRegistration = cancellationToken.Register(
            static state =>
            {
                var (info, resp, token) = ((TaskCompletionSource<InitialGameInfoMessage>, TaskCompletionSource<ClientLobbyJoinResponseMessage>, CancellationToken))state!;
                info.TrySetCanceled(token);
                resp.TrySetCanceled(token);
            },
            (initialInfoSource, responseSource, cancellationToken));

        inner.RegisterMessageHandler(initialInfoHandler);
        inner.RegisterMessageHandler(responseHandler);
        try
        {
            log?.Invoke($"Broker client join flow: waiting for host initial game info clientId={settings.ClientId}.");
            while (!initialInfoSource.Task.IsCompleted)
            {
                service.Update();
                await Task.Delay(16, cancellationToken).ConfigureAwait(false);
            }

            var initialInfo = await initialInfoSource.Task.ConfigureAwait(false);
            ThrowIfInitialGameInfoRejected(initialInfo);
            log?.Invoke($"Broker client join flow: received host initial game info clientId={settings.ClientId}.");

            // Send the SINGLE real join request.
            // If InitializeMultiplayerAsClient later tries to send a duplicate,
            // BrokerBackedNetService drops it and replays the stashed response.
            service.Update();
            var joinRequest = createJoinRequest?.Invoke() ?? CreateJoinRequestFromCurrentSave(log);
            log?.Invoke($"Broker client join flow: sending real lobby join request clientId={settings.ClientId}.");
            service.SendMessage(joinRequest);

            log?.Invoke($"Broker client join flow: waiting for lobby join response clientId={settings.ClientId}.");
            while (!responseSource.Task.IsCompleted)
            {
                service.Update();
                await Task.Delay(16, cancellationToken).ConfigureAwait(false);
            }

            var joinResponse = await responseSource.Task.ConfigureAwait(false);
            log?.Invoke($"Broker client join flow: received lobby join response clientId={settings.ClientId}.");

            // Stash the response so any duplicate join request from
            // InitializeMultiplayerAsClient is suppressed and replayed.
            service.Update();
            inner.StashJoinResponse(joinResponse);

            BrokerPendingNetGameServiceRegistry.Store(settings.ClientId, service);
            log?.Invoke($"Broker client join flow: join complete, service stored for substitution clientId={settings.ClientId}.");

            return new JoinResult
            {
                gameMode = initialInfo.gameMode,
                sessionState = initialInfo.sessionState,
                joinResponse = joinResponse
            };
        }
        catch
        {
            service.Dispose();
            throw;
        }
        finally
        {
            inner.UnregisterMessageHandler(initialInfoHandler);
            inner.UnregisterMessageHandler(responseHandler);
        }
    }

    private static void ThrowIfInitialGameInfoRejected(InitialGameInfoMessage initialInfo)
    {
        if (!initialInfo.connectionFailureReason.HasValue)
        {
            return;
        }

        throw new ClientConnectionFailedException(
            "Got connection failure from host",
            new NetErrorInfo(initialInfo.connectionFailureReason.Value, default));
    }

    private static Type? ResolveType(string fullName)
    {
        return Type.GetType($"{fullName}, sts2", throwOnError: false)
            ?? AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(assembly => string.Equals(assembly.GetName().Name, "sts2", StringComparison.OrdinalIgnoreCase))
                ?.GetType(fullName, throwOnError: false);
    }

    /// <summary>
    /// Minimal IClientConnectionInitializer that reports success immediately.
    /// Used as a placeholder when invoking JoinGame.Invoke — the real broker
    /// connection is created by BrokerLobbyServiceSubstitutionPatch.
    /// </summary>
    public sealed class PlaceholderClientConnectionInitializer : IClientConnectionInitializer
    {
        public Task<NetErrorInfo?> Connect(NetClientGameService service, CancellationToken cancelToken)
        {
            return Task.FromResult<NetErrorInfo?>(null);
        }
    }
}
