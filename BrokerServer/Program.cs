using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

// ═══════════════════════════════════════════════════════════════════════════════
// TokenSpire2 TCP Broker Server
// ═══════════════════════════════════════════════════════════════════════════════
// Relays network messages between STS2 game instances in LAN co-op mode.
//
// Protocol (matching BrokerClientConnection):
//   - 4-byte big-endian length prefix + UTF-8 JSON payload
//   - Kind enum: 0=Registration, 1=RegistrationAccepted, 2=Envelope, 3=PeerRegistered
//   - camelCase property names (matches System.Text.Json JsonSerializerDefaults.Web)
//
// Usage: BrokerServer.exe [--port PORT] [--session-id ID]
// ═══════════════════════════════════════════════════════════════════════════════

var port = 9999;
var sessionId = $"coop-{Guid.NewGuid():N}"[..12];

for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--port" && i + 1 < args.Length)
        port = int.Parse(args[++i]);
    else if (args[i] == "--session-id" && i + 1 < args.Length)
        sessionId = args[++i];
}

Console.WriteLine("+----------------------------------------------+");
Console.WriteLine("|  TokenSpire2 TCP Broker Server               |");
Console.WriteLine("+----------------------------------------------+");
Console.WriteLine($"  Port:      {port}");
Console.WriteLine($"  Session:   {sessionId}");
Console.WriteLine("+----------------------------------------------+");
Console.WriteLine();

var server = new BrokerServer(port, sessionId);
await server.RunAsync();

// ═══════════════════════════════════════════════════════════════════════════════

internal sealed class BrokerServer
{
    // Kind enum values (must match BrokerClientConnection.BrokerTransportMessageKind)
    private const int KindRegistration = 0;
    private const int KindRegistrationAccepted = 1;
    private const int KindEnvelope = 2;
    private const int KindPeerRegistered = 3;

    private readonly int _port;
    private readonly string _sessionId;
    private readonly object _clientsGate = new();
    private readonly List<ConnectedClient> _clients = [];
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    public BrokerServer(int port, string sessionId)
    {
        _port = port;
        _sessionId = sessionId;
    }

    public async Task RunAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, _port);
        listener.Start();
        Console.WriteLine($"[SRV] Listening on 127.0.0.1:{_port}");

        try
        {
            while (true)
            {
                var tcpClient = await listener.AcceptTcpClientAsync();
                Console.WriteLine($"[SRV] New connection from {tcpClient.Client.RemoteEndPoint}");
                _ = HandleClientAsync(tcpClient);
            }
        }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[SRV] Fatal: {ex.Message}");
        }
    }

    private async Task HandleClientAsync(TcpClient tcpClient)
    {
        ConnectedClient? client = null;
        string clientLabel = "unknown";
        try
        {
            using (tcpClient)
            {
                var stream = tcpClient.GetStream();

                // ── Read registration message ─────────────────────────────
                var regDoc = await ReadJsonAsync(stream, CancellationToken.None);
                if (regDoc is null)
                {
                    Console.WriteLine($"[SRV] No registration from {tcpClient.Client.RemoteEndPoint}");
                    return;
                }

                var kind = GetInt(regDoc.Value, "kind");
                if (kind != KindRegistration || !regDoc.Value.TryGetProperty("registration", out var regNode))
                {
                    Console.WriteLine($"[SRV] Invalid registration: kind={kind}");
                    return;
                }

                var regClientId = GetString(regNode, "clientId") ?? "client-0";
                var regRole = GetString(regNode, "role") ?? "Host";
                var regIndex = GetInt(regNode, "clientIndex");
                clientLabel = regClientId;

                Console.WriteLine($"[SRV] Registered: {regClientId}  role={regRole}  index={regIndex}");

                client = new ConnectedClient(regClientId, regRole, regIndex, stream);

                // Get already-connected peers
                IReadOnlyList<object> connectedPeers;
                lock (_clientsGate)
                {
                    connectedPeers = _clients
                        .Select(c => (object)new Dictionary<string, object?>
                        {
                            ["clientId"] = c.ClientId,
                            ["role"] = c.Role,
                            ["clientIndex"] = c.ClientIndex
                        })
                        .ToArray();
                    _clients.Add(client);
                }

                // ── Send RegistrationAccepted (kind=1) ──────────────────
                await WriteJsonAsync(stream, new Dictionary<string, object?>
                {
                    ["kind"] = KindRegistrationAccepted,
                    ["registrationAccepted"] = new Dictionary<string, object?>
                    {
                        ["clientId"] = regClientId,
                        ["sessionId"] = _sessionId,
                        ["connectedPeers"] = connectedPeers
                    }
                });
                Console.WriteLine($"[SRV]  -> RegistrationAccepted  (peers={connectedPeers.Count})");

                // ── Notify existing peers about the new client (kind=3) ─
                if (connectedPeers.Count > 0)
                {
                    var peerNotify = new Dictionary<string, object?>
                    {
                        ["kind"] = KindPeerRegistered,
                        ["peerRegistration"] = new Dictionary<string, object?>
                        {
                            ["clientId"] = regClientId,
                            ["role"] = regRole,
                            ["clientIndex"] = regIndex
                        }
                    };
                    await BroadcastExceptAsync(regClientId, peerNotify);
                }

                Console.WriteLine($"[SRV] {regClientId} ready.  Clients online: {GetClientCount()}");

                // ── Message relay loop ───────────────────────────────────
                while (true)
                {
                    var msgDoc = await ReadJsonAsync(stream, CancellationToken.None);
                    if (msgDoc is null)
                    {
                        Console.WriteLine($"[SRV] {regClientId} disconnected (EOF)");
                        break;
                    }

                    var msgKind = GetInt(msgDoc.Value, "kind");
                    if (msgKind == KindEnvelope && msgDoc.Value.TryGetProperty("envelope", out var envNode))
                    {
                        var sourceId = GetString(envNode, "sourceClientId") ?? regClientId;
                        var targetId = GetString(envNode, "targetClientId");
                        var msgType = GetString(envNode, "messageType") ?? "?";

                        // Relay the raw JSON document as-is
                        var rawJson = msgDoc.Value.GetRawText();

                        if (targetId is not null)
                        {
                            await SendRawJsonAsync(targetId, rawJson);
                            Console.WriteLine($"[RELAY] {sourceId} -> {targetId}  [{msgType}]");
                        }
                        else
                        {
                            await BroadcastRawExceptAsync(sourceId, rawJson);
                            Console.WriteLine($"[RELAY] {sourceId} -> ALL  [{msgType}]");
                        }
                    }
                }
            }
        }
        catch (IOException)
        {
            Console.WriteLine($"[SRV] {clientLabel} connection lost (IO)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SRV] {clientLabel} error: {ex.Message}");
        }
        finally
        {
            if (client is not null)
            {
                lock (_clientsGate) { _clients.Remove(client); }
                Console.WriteLine($"[SRV] {clientLabel} removed.  Online: {GetClientCount()}");
            }
        }
    }

    // ── Send helpers ────────────────────────────────────────────────────

    private async Task SendRawJsonAsync(string targetClientId, string rawJson)
    {
        ConnectedClient? target;
        lock (_clientsGate)
            target = _clients.FirstOrDefault(c => c.ClientId == targetClientId);

        if (target is null) { Console.WriteLine($"[SRV] Target '{targetClientId}' not found"); return; }

        try { await WriteRawJsonAsync(target.Stream, rawJson); }
        catch (Exception ex) { Console.WriteLine($"[SRV] Send fail to {targetClientId}: {ex.Message}"); }
    }

    private async Task BroadcastExceptAsync(string exceptClientId, object message)
    {
        List<ConnectedClient> targets;
        lock (_clientsGate)
            targets = _clients.Where(c => c.ClientId != exceptClientId).ToList();

        foreach (var t in targets)
        {
            try { await WriteJsonAsync(t.Stream, message); }
            catch (Exception ex) { Console.WriteLine($"[SRV] Send fail to {t.ClientId}: {ex.Message}"); }
        }
    }

    private async Task BroadcastRawExceptAsync(string exceptClientId, string rawJson)
    {
        List<ConnectedClient> targets;
        lock (_clientsGate)
            targets = _clients.Where(c => c.ClientId != exceptClientId).ToList();

        foreach (var t in targets)
        {
            try { await WriteRawJsonAsync(t.Stream, rawJson); }
            catch (Exception ex) { Console.WriteLine($"[SRV] Send fail to {t.ClientId}: {ex.Message}"); }
        }
    }

    private int GetClientCount() { lock (_clientsGate) return _clients.Count; }

    // ── JSON helpers ────────────────────────────────────────────────────

    private static string? GetString(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int GetInt(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : -1;

    // ── Wire format: 4-byte big-endian length + UTF-8 JSON ──────────────

    private async Task<JsonElement?> ReadJsonAsync(NetworkStream stream, CancellationToken ct)
    {
        var lenBuf = new byte[4];
        if (await ReadExactlyAsync(stream, lenBuf, ct) < 4) return null;

        var len = BinaryPrimitives.ReadInt32BigEndian(lenBuf);
        if (len <= 0 || len > 1024 * 1024) throw new InvalidDataException($"Bad length: {len}");

        var payload = new byte[len];
        if (await ReadExactlyAsync(stream, payload, ct) < len) return null;

        return JsonSerializer.Deserialize<JsonElement>(payload, _jsonOptions);
    }

    private async Task WriteJsonAsync(NetworkStream stream, object message)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, _jsonOptions);
        var lenBuf = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(lenBuf, payload.Length);
        await stream.WriteAsync(lenBuf).ConfigureAwait(false);
        await stream.WriteAsync(payload).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);
    }

    private async Task WriteRawJsonAsync(NetworkStream stream, string rawJson)
    {
        var payload = System.Text.Encoding.UTF8.GetBytes(rawJson);
        var lenBuf = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(lenBuf, payload.Length);
        await stream.WriteAsync(lenBuf).ConfigureAwait(false);
        await stream.WriteAsync(payload).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);
    }

    private static async Task<int> ReadExactlyAsync(NetworkStream stream, byte[] buffer, CancellationToken ct)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            var r = await stream.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
            if (r == 0) return total;
            total += r;
        }
        return total;
    }

    // ── Internal types ──────────────────────────────────────────────────

    private sealed record ConnectedClient(
        string ClientId, string Role, int ClientIndex, NetworkStream Stream);
}
