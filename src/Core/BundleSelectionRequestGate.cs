namespace TokenSpire2.Core;

public enum BundleSelectionInput
{
    None,
    Clicked,
    HitboxFallback,
    Exhausted,
}

/// <summary>
/// Bounds bundle selection recovery to three cycles. Each tick returns at most
/// one input and ages fallback from the request that started the current cycle.
/// </summary>
public sealed class BundleSelectionRequestGate
{
    public const int HitboxFallbackFrame = 6;
    public const int MaxCycles = 3;

    private bool _cycleStartPending;

    public bool Attempted { get; private set; }
    public bool Accepted { get; private set; }
    public int RequestAge { get; private set; } = -1;
    public bool FallbackSent { get; private set; }
    public int CycleCount { get; private set; }
    public bool Exhausted { get; private set; }

    public BundleSelectionInput Tick(
        bool hasValidHitbox,
        bool timeoutRecoveryRequested,
        bool canIssueClicked = true)
    {
        if (Exhausted)
            return BundleSelectionInput.None;

        if (!Attempted || _cycleStartPending)
        {
            if (!canIssueClicked)
                return BundleSelectionInput.None;

            _cycleStartPending = false;
            return StartCycle();
        }

        RequestAge++;

        if (!FallbackSent && hasValidHitbox && RequestAge >= HitboxFallbackFrame)
        {
            FallbackSent = true;
            return BundleSelectionInput.HitboxFallback;
        }

        if (timeoutRecoveryRequested)
        {
            if (!canIssueClicked)
            {
                _cycleStartPending = true;
                return BundleSelectionInput.None;
            }

            return StartCycle();
        }

        return BundleSelectionInput.None;
    }

    public void RecordClickedResult(bool accepted)
    {
        if (!Attempted)
            throw new InvalidOperationException("Cannot record a result before a request");

        Accepted = accepted;
    }

    public void ReportInputFailed()
    {
        if (!FallbackSent || Exhausted)
            return;

        FallbackSent = false;
        _cycleStartPending = true;
    }

    public void Reset()
    {
        Attempted = false;
        Accepted = false;
        RequestAge = -1;
        FallbackSent = false;
        CycleCount = 0;
        Exhausted = false;
        _cycleStartPending = false;
    }

    private BundleSelectionInput StartCycle()
    {
        if (CycleCount >= MaxCycles)
        {
            Exhausted = true;
            return BundleSelectionInput.Exhausted;
        }

        CycleCount++;
        Attempted = true;
        Accepted = false;
        RequestAge = 0;
        FallbackSent = false;
        return BundleSelectionInput.Clicked;
    }
}
