using System.Text.Json;
using TokenSpire2.Diagnostics;

namespace TokenSpire2.Launcher;

internal sealed class DiagnosticLogReader
{
    public IReadOnlyList<DiagnosticEvent> Read(
        string directory, string role = "全部", string eventType = "全部", int limit = 500)
    {
        if (!Directory.Exists(directory)) return Array.Empty<DiagnosticEvent>();

        var files = role == "全部"
            ? Directory.GetFiles(directory, "*.events.jsonl")
            : new[] { Path.Combine(directory, $"{role}.events.jsonl") };

        var result = new List<DiagnosticEvent>();
        foreach (var file in files.Where(File.Exists))
        {
            foreach (var line in ReadSharedLines(file))
            {
                try
                {
                    var evt = JsonSerializer.Deserialize<DiagnosticEvent>(line);
                    if (evt != null && (eventType == "全部" || evt.EventType == eventType))
                        result.Add(evt);
                }
                catch (JsonException) { }
            }
        }

        return result.OrderByDescending(x => x.TimestampUtc).Take(limit).ToList();
    }

    private static IEnumerable<string> ReadSharedLines(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
            if (!string.IsNullOrWhiteSpace(line)) yield return line;
    }
}
