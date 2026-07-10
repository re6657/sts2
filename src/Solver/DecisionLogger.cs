using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using TokenSpire2.Core;
using TokenSpire2.Solver;

namespace TokenSpire2.Solver;

/// <summary>
/// Logs every non-combat decision made by DecisionEngine for debugging and optimization.
/// Writes JSON files to llm_data/decisions/ under the mod assembly directory.
/// </summary>
public static class DecisionLogger
{
    private static readonly List<DecisionRecord> _currentRun = new();
    private static string? _outputDir;
    private static bool _enabled;
    private static int _runNumber;

    public static void Enable()
    {
        _enabled = true;
        _outputDir = Path.Combine(
            Path.GetDirectoryName(typeof(DecisionLogger).Assembly.Location) ?? ".",
            "llm_data", "decisions");
        Directory.CreateDirectory(_outputDir!);
        // Count existing runs to increment
        try
        {
            var files = Directory.GetFiles(_outputDir, "decisions_run_*.json");
            _runNumber = files.Length + 1;
        }
        catch { _runNumber = 1; }
    }

    public static void LogDecision(
        GameScreen screen,
        string decisionType,
        List<OptionScore> options,
        int chosenIndex,
        string chosenLabel,
        string reason)
    {
        if (!_enabled) return;

        _currentRun.Add(new DecisionRecord
        {
            Screen = screen.ToString(),
            DecisionType = decisionType,
            Options = options,
            ChosenIndex = chosenIndex,
            ChosenLabel = chosenLabel,
            Reason = reason,
            Timestamp = DateTime.UtcNow.ToString("O"),
        });

        // Auto-save after each decision so logs appear during the run
        SaveRun();
    }

    public static void NewRun()
    {
        if (_currentRun.Count > 0)
            SaveRun();
        _currentRun.Clear();
        _runNumber++;
    }

    public static void SaveRun()
    {
        if (!_enabled || _outputDir == null || _currentRun.Count == 0) return;
        try
        {
            var path = Path.Combine(_outputDir, $"decisions_run_{_runNumber:D3}.json");
            var json = JsonSerializer.Serialize(_currentRun, new JsonSerializerOptions
            {
                WriteIndented = true,
            });
            File.WriteAllText(path, json);
        }
        catch { /* logging should never crash */ }
    }

    public class DecisionRecord
    {
        public string Screen { get; set; } = "";
        public string DecisionType { get; set; } = "";
        public List<OptionScore> Options { get; set; } = new();
        public int ChosenIndex { get; set; }
        public string ChosenLabel { get; set; } = "";
        public string Reason { get; set; } = "";
        public string Timestamp { get; set; } = "";
    }

    public class OptionScore
    {
        public int Index { get; set; }
        public string Label { get; set; } = "";
        public double Score { get; set; }
        public string Notes { get; set; } = "";
    }
}
