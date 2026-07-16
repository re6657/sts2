using System.Text.Json;
using TokenSpire2.Diagnostics;
using Xunit;

namespace TokenSpire2.Core.Tests;

public sealed class DiagnosticWriterTests
{
    [Fact]
    public async Task DifferentRolesWriteDifferentFiles()
    {
        var root = CreateTempDirectory();
        await using var host = new DiagnosticWriter(root, "host", "session-a");
        await using var bot = new DiagnosticWriter(root, "bot1", "session-a");

        host.Write(Event("host", DiagnosticEventTypes.HostAutomationBlocked));
        bot.Write(Event("bot1", DiagnosticEventTypes.ActionQueueStalled));
        await host.FlushAsync();
        await bot.FlushAsync();

        Assert.NotEqual(host.JsonlPath, bot.JsonlPath);
        Assert.True(File.Exists(host.JsonlPath));
        Assert.True(File.Exists(bot.JsonlPath));
    }

    [Fact]
    public async Task JsonlContainsOneValidObjectPerEvent()
    {
        var root = CreateTempDirectory();
        await using var writer = new DiagnosticWriter(root, "bot2", "session-b");

        writer.Write(Event("bot2", DiagnosticEventTypes.TurnSkippedWithPlayableCards));
        await writer.FlushAsync();

        var lines = await File.ReadAllLinesAsync(writer.JsonlPath);
        Assert.Single(lines);
        var parsed = JsonSerializer.Deserialize<DiagnosticEvent>(lines[0]);
        Assert.Equal(DiagnosticEventTypes.TurnSkippedWithPlayableCards, parsed?.EventType);
        Assert.Equal("bot2", parsed?.InstanceRole);
    }

    [Fact]
    public async Task DuplicateKeyIsWrittenOnlyOnce()
    {
        var root = CreateTempDirectory();
        await using var writer = new DiagnosticWriter(root, "bot3", "session-c");
        var evt = Event("bot3", DiagnosticEventTypes.OverlayStuck);

        writer.Write(evt, "room-4-overlay");
        writer.Write(evt, "room-4-overlay");
        await writer.FlushAsync();

        Assert.Single(await File.ReadAllLinesAsync(writer.JsonlPath));
    }

    private static DiagnosticEvent Event(string role, string type) => new()
    {
        TimestampUtc = DateTimeOffset.UtcNow,
        SessionId = "test-session",
        InstanceRole = role,
        EventType = type,
        Message = "test",
        TurnNumber = 2,
        Energy = 3,
        Hand = ["STRIKE", "DEFEND"],
        PlayableCards = ["STRIKE"],
    };

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "TokenSpire2Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
