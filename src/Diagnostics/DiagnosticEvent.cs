namespace TokenSpire2.Diagnostics;

public static class DiagnosticEventTypes
{
    public const string TurnSkippedWithPlayableCards = "TURN_SKIPPED_WITH_PLAYABLE_CARDS";
    public const string HostAutomationBlocked = "HOST_AUTOMATION_BLOCKED";
    public const string StateDivergence = "STATE_DIVERGENCE";
    public const string ActionQueueStalled = "ACTION_QUEUE_STALLED";
    public const string OverlayStuck = "OVERLAY_STUCK";
    public const string TurnReadinessWait = "TURN_READINESS_WAIT";
    public const string TurnPlan = "TURN_PLAN";
}

public sealed class DiagnosticEvent
{
    public DateTimeOffset TimestampUtc { get; set; } = DateTimeOffset.UtcNow;
    public string SessionId { get; set; } = "";
    public string InstanceRole { get; set; } = "solo";
    public string Character { get; set; } = "";
    public ulong NetId { get; set; }
    public string Room { get; set; } = "";
    public int TurnNumber { get; set; }
    public string EventType { get; set; } = "";
    public string Severity { get; set; } = "info";
    public string Message { get; set; } = "";
    public int Energy { get; set; }
    public IReadOnlyList<string> Hand { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> PlayableCards { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> Plan { get; set; } = Array.Empty<string>();
    public string EndTurnReason { get; set; } = "";
    public string ActionQueueState { get; set; } = "";
    public Dictionary<string, string> Details { get; set; } = new();
}
