using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace TokenSpire2.Diagnostics;

/// <summary>
/// Non-blocking per-instance diagnostics. Gameplay code only enqueues work;
/// one background reader performs append-only writes with shared read access.
/// </summary>
public sealed class DiagnosticWriter : IAsyncDisposable
{
    private const int MaxDedupeKeys = 2048;
    private readonly Channel<WriteRequest> _channel;
    private readonly ConcurrentDictionary<string, byte> _dedupe = new(StringComparer.Ordinal);
    private readonly Task _worker;
    private readonly Action<Exception>? _onError;
    private readonly JsonSerializerOptions _jsonOptions = new();

    public DiagnosticWriter(
        string modDirectory,
        string instanceRole,
        string sessionId,
        Action<Exception>? onError = null)
    {
        InstanceRole = SanitizeRole(instanceRole);
        SessionId = string.IsNullOrWhiteSpace(sessionId) ? "local" : sessionId.Trim();
        _onError = onError;

        var logDirectory = Path.Combine(modDirectory, "logs");
        Directory.CreateDirectory(logDirectory);
        TextPath = Path.Combine(logDirectory, $"{InstanceRole}.log");
        JsonlPath = Path.Combine(logDirectory, $"{InstanceRole}.events.jsonl");

        _channel = Channel.CreateUnbounded<WriteRequest>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
        _worker = Task.Run(ProcessQueueAsync);
    }

    public string InstanceRole { get; }
    public string SessionId { get; }
    public string TextPath { get; }
    public string JsonlPath { get; }

    public bool Write(DiagnosticEvent evt, string? dedupeKey = null)
    {
        if (!string.IsNullOrEmpty(dedupeKey))
        {
            if (_dedupe.Count >= MaxDedupeKeys)
                _dedupe.Clear();
            if (!_dedupe.TryAdd(dedupeKey, 0))
                return false;
        }

        evt.InstanceRole = InstanceRole;
        evt.SessionId = SessionId;
        if (evt.TimestampUtc == default)
            evt.TimestampUtc = DateTimeOffset.UtcNow;
        return _channel.Writer.TryWrite(new WriteRequest(evt, null));
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await _channel.Writer.WriteAsync(new WriteRequest(null, completion), cancellationToken);
        await completion.Task.WaitAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        try { await _worker.ConfigureAwait(false); }
        catch (Exception ex) { _onError?.Invoke(ex); }
    }

    private async Task ProcessQueueAsync()
    {
        await foreach (var request in _channel.Reader.ReadAllAsync())
        {
            if (request.Barrier != null)
            {
                request.Barrier.TrySetResult();
                continue;
            }

            if (request.Event == null)
                continue;

            try
            {
                await AppendEventAsync(request.Event).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _onError?.Invoke(ex);
            }
        }
    }

    private async Task AppendEventAsync(DiagnosticEvent evt)
    {
        var json = JsonSerializer.Serialize(evt, _jsonOptions);
        var text = $"[{evt.TimestampUtc:O}] [{evt.InstanceRole}] [{evt.EventType}] " +
                   $"turn={evt.TurnNumber} energy={evt.Energy} {evt.Message}";

        await AppendLineAsync(JsonlPath, json).ConfigureAwait(false);
        await AppendLineAsync(TextPath, text).ConfigureAwait(false);
    }

    private static async Task AppendLineAsync(string path, string line)
    {
        await using var stream = new FileStream(
            path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite,
            bufferSize: 4096, useAsync: true);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        await writer.WriteLineAsync(line).ConfigureAwait(false);
    }

    private static string SanitizeRole(string role)
    {
        if (string.IsNullOrWhiteSpace(role))
            return "solo";
        var chars = role.Trim().ToLowerInvariant()
            .Where(c => char.IsLetterOrDigit(c) || c is '-' or '_')
            .ToArray();
        return chars.Length == 0 ? "solo" : new string(chars);
    }

    private sealed record WriteRequest(
        DiagnosticEvent? Event,
        TaskCompletionSource? Barrier);
}
