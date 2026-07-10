using System.Collections.Generic;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Runs;

namespace TokenSpire2.Handlers;

public static class RewardsHandler
{
    private static readonly HashSet<ulong> _triedRewards = new();
    private static int _stuckFrames;
    private const int MaxStuckFrames = 30; // ~1 second before force-remove (was 90)
    private static ulong _lastScreenId;
    private static int _sameScreenCount;
    private static int _forceRemoveCount;

    public static void ClearTried() { _triedRewards.Clear(); _stuckFrames = 0; }

    public static double Handle(NRewardsScreen screen)
    {
        if (!GodotObject.IsInstanceValid(screen)) return 0;

        var screenId = screen.GetInstanceId();

        // Track if this is the same screen persisting across calls
        if (screenId == _lastScreenId)
        {
            _sameScreenCount++;
        }
        else
        {
            _lastScreenId = screenId;
            _sameScreenCount = 0;
        }

        // Safety valve: if the same screen has been handled >300 times (~10s),
        // something is fundamentally broken. Force-remove and escalate.
        if (_sameScreenCount > 300)
        {
            MainFile.Logger.Error($"[AutoSlay] Rewards: CRITICAL — same screen {screenId} persisted {_sameScreenCount} ticks. Force-removing.");
            _triedRewards.Clear();
            _stuckFrames = 0;
            _sameScreenCount = 0;
            _forceRemoveCount++;
            NOverlayStack.Instance?.Remove(screen);
            return 2.0; // longer cooldown to let game stabilize
        }

        // Collect all clickable reward buttons first (gold, relic, potion).
        // Card rewards open a separate NCardRewardSelectionScreen handled elsewhere.
        var allBtns = AutoSlayHelpers.FindAll<NRewardButton>(screen);

        // ── Potion skip: when holding 3 potions, don't pick new potion rewards ──
        int currentPotionCount = 0;
        try
        {
            var runState = RunManager.Instance?.DebugOnlyGetState();
            var player = runState != null ? LocalContext.GetMe(runState) : null;
            if (player != null)
            {
                foreach (var p in player.Potions)
                {
                    try
                    {
                        if (!((bool)p.HasBeenRemovedFromState) && !((bool)p.IsQueued))
                            currentPotionCount++;
                    }
                    catch { }
                }
            }
        }
        catch { /* potion count check best-effort */ }

        var btn = allBtns.FirstOrDefault(b =>
        {
            if (!b.IsEnabled || _triedRewards.Contains(b.GetInstanceId()))
                return false;

            // Skip potion rewards when we already have 3 potions (avoids bugs)
            if (currentPotionCount >= 3 && b.Reward is PotionReward)
            {
                MainFile.Logger.Info($"[AutoSlay] Rewards: skipping potion reward — already have {currentPotionCount} potions");
                // NOT marking as _triedRewards — potion could become desirable if screen refreshes
                return false;
            }

            return true;
        });

        if (btn != null)
        {
            _triedRewards.Add(btn.GetInstanceId());
            _stuckFrames = 0;
            _sameScreenCount = 0;
            btn.ForceClick();
            return 0.3;
        }

        // Try the Proceed button — in STS2 this auto-collects remaining rewards
        var proceed = AutoSlayHelpers.FindFirst<NProceedButton>(screen);
        if (proceed?.IsEnabled == true)
        {
            MainFile.Logger.Info("[AutoSlay] Rewards: clicking Proceed");
            _triedRewards.Clear();
            _stuckFrames = 0;
            _sameScreenCount = 0;
            proceed.ForceClick();
            return 1.0;
        }

        // Proceed not ready yet — wait. STS2 often needs several frames after
        // the last reward animation finishes before enabling Proceed.
        // Also: SpeedX AutoProceed may have already clicked Proceed (now disabled),
        // causing us to wait for a screen transition that may never come.
        if (_stuckFrames < MaxStuckFrames)
        {
            _stuckFrames++;
            return 0.1; // fast poll
        }

        // Timed out — force-remove the overlay.
        // This handles both cases:
        // 1. STS2 animation delay (normal)
        // 2. SpeedX AutoProceed race (Proceed clicked but screen didn't close)
        _forceRemoveCount++;
        MainFile.Logger.Info($"[AutoSlay] Rewards: force-removing after {_stuckFrames} frames wait (screenId={screenId}, totalForceRemoves={_forceRemoveCount})");
        _triedRewards.Clear();
        _stuckFrames = 0;
        _sameScreenCount = 0;
        NOverlayStack.Instance?.Remove(screen);

        // If we're force-removing excessively, something is very wrong
        if (_forceRemoveCount > 10)
        {
            MainFile.Logger.Error($"[AutoSlay] Rewards: EXCESSIVE force-removes ({_forceRemoveCount}) — possible infinite reward loop!");
        }

        return 1.0;
    }
}
