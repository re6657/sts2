using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.RestSite;
using MegaCrit.Sts2.Core.Nodes.Rooms;

using TokenSpire2.Core;

namespace TokenSpire2.Solver;

/// <summary>
/// Rest site decision: upgrade when HP >= 50%, rest when below.
/// Act-aware: Act 1 upgrade more aggressively (boss heals), Act 3 rest more conservatively.
/// ForgottenArbiter pattern: REST if HP < 50%, else SMITH (upgrade).
/// </summary>
public static class RestDecider
{
    // M9: removed unused REST_THRESHOLD constant — actual thresholds come from SolverParams
    private static int _stuckFrames;
    private const int MaxStuckFrames = 90; // ~3 seconds before force-proceed

    public static void ResetStuckCounter() { _stuckFrames = 0; }

    public static bool Decide(RunState state)
    {
        var restRoom = GetRestRoom();
        if (restRoom == null)
        {
            _stuckFrames = 0;
            return false;
        }

        // Check proceed first (after making a choice)
        var proceed = restRoom.ProceedButton;
        if (proceed?.IsEnabled == true)
        {
            MainFile.Logger.Info("[RestDecider] Clicking proceed");
            proceed.ForceClick();
            _stuckFrames = 0;
            DecisionLogger.LogDecision(GameScreen.REST, "RestProceed",
                new List<DecisionLogger.OptionScore>(), 0, "PROCEED",
                "Proceed button enabled after rest site choice");
            return true;
        }

        var btns = AutoSlayHelpers.FindAll<NRestSiteButton>(restRoom)
            .Where(b => b.Option.IsEnabled)
            .ToList();

        // ── No options and Proceed not enabled — might be loading.
        //    Wait a few frames, then force-proceed as fallback. ────────────
        if (btns.Count == 0)
        {
            if (_stuckFrames < MaxStuckFrames)
            {
                _stuckFrames++;
                if (_stuckFrames % 30 == 0) // Log every ~1 second
                    MainFile.Logger.Info($"[RestDecider] Waiting for rest options (frame {_stuckFrames}/{MaxStuckFrames})");
                return false; // keep waiting for buttons to appear
            }
            // Timeout: force-click Proceed even if not enabled
            MainFile.Logger.Info($"[RestDecider] No rest options after {_stuckFrames} frames, force-clicking Proceed");
            _stuckFrames = 0;
            proceed?.ForceClick();
            return true;
        }
        _stuckFrames = 0;

        var scored = btns.Select((b, i) =>
        {
            double score = ScoreRestOption(b, state);
            string label = RestOptionLabel(b);
            return (index: i, score, label, button: b);
        }).OrderByDescending(x => x.score).ToList();

        var best = Tiebreaker.PickBestFromSorted(scored, x => x.score);
        string reason = $"Chose {best.label} score={best.score:F0} (HP={state.CurrentHp}/{state.MaxHp} ratio={state.HpRatio:F1}% Act={state.Act})";

        DecisionLogger.LogDecision(
            GameScreen.REST, "RestChoice",
            scored.Select(s => new DecisionLogger.OptionScore
            {
                Index = s.index, Label = s.label, Score = s.score
            }).ToList(),
            best.index, best.label, reason);

        MainFile.Logger.Info($"[RestDecider] {reason}");

        // ── Set card-select context BEFORE clicking ──────────────────────
        // AutoSlayCardSelector (global ICardSelector) must know whether this
        // is an upgrade, removal, or transform operation so it picks the
        // right card. Must be set before the game opens the card grid.
        string optionLower = (best.button.Option?.GetType()?.Name ?? "").ToLowerInvariant();
        if (optionLower.Contains("smith") || optionLower.Contains("upgrade")
            || optionLower.Contains("forge") || optionLower.Contains("improve"))
            DecisionEngine.PendingCardSelectContext = "UPGRADE";
        else if (optionLower.Contains("toke") || optionLower.Contains("remove")
            || optionLower.Contains("purge") || optionLower.Contains("cleanse"))
            DecisionEngine.PendingCardSelectContext = "REMOVE";
        else if (optionLower.Contains("transform") || optionLower.Contains("change"))
            DecisionEngine.PendingCardSelectContext = "TRANSFORM";
        // Note: "rest"/"heal" and other options don't trigger card selection,
        // so context doesn't matter for them.

        best.button.ForceClick();
        _stuckFrames = 0; // B14: reset counter after clicking to avoid stale state
        return true;
    }

    private static double ScoreRestOption(NRestSiteButton btn, RunState state)
    {
        double score = 10;
        var rp = SolverParams.Instance.Rest;
        string typeName = btn.Option?.GetType()?.Name ?? "";
        string lower = typeName.ToLower();

        // CRITICAL: check SPECIFIC options BEFORE generic "rest" because
        // ALL rest site option class names contain "Rest" (HealRestSiteOption,
        // SmithRestSiteOption, HatchRestSiteOption, etc.).
        // Use double HpRatio to avoid float < double precision issues.

        if (lower.Contains("smith") || lower.Contains("upgrade") ||
            lower.Contains("forge") || lower.Contains("improve"))
        {
            if (state.HpRatio >= rp.SmithHpThreshold)
            {
                score += rp.SmithHighHpScore;
                if (state.Act == 1) score += rp.SmithAct1Bonus;
                if (state.Act >= 3 && state.HpRatio < rp.RestMediumHpMax) score += rp.SmithAct3LowHpPenalty;
            }
            else
            {
                score += rp.SmithLowHpScore;
            }

            // ── Upgrade value awareness ────────────────────────────────
            // If every upgradeable card is a basic Strike/Defend, upgrading
            // is low value — prefer Rest or Toke instead.
            // EXCEPTION: enchanted basic cards (Strike/Defend with extra
            // effects) ARE worth upgrading because the enchant scales with
            // the upgrade, making them premium targets.
            bool hasEnchantedUpgradeCandidate = state.HasEnchantedBasicCard;
            int upgradeableNonBasic = state.TotalCardCount
                - state.CountUpgradedCards
                - state.CountBasicStrikes
                - state.CountBasicDefends;
            if (upgradeableNonBasic <= 0 && !hasEnchantedUpgradeCandidate)
            {
                score -= 40; // No worthwhile upgrades — heavily penalize Smith
                MainFile.Logger.Info("[RestDecider] Smith penalized: no non-basic or enchanted cards to upgrade");
            }
            else if (upgradeableNonBasic <= 1 && state.TotalCardCount > 10 && !hasEnchantedUpgradeCandidate)
            {
                score -= 10; // Only 1 worthwhile upgrade in a medium+ deck — mild penalty
            }
            // Bonus for having enchanted cards to upgrade
            if (hasEnchantedUpgradeCandidate)
            {
                score += 15;
                MainFile.Logger.Info("[RestDecider] Smith boosted: enchanted card upgrade candidate available");
            }
            // ── Defect: Dualcast upgrade is the single highest priority ──
            // Dualcast evokes the rightmost orb twice when upgraded (once when unupgraded).
            // With any orb scaling, this doubles its damage/block output.
            if (state.Character == "DEFECT")
            {
                bool hasDualcast = state.DeckCardIds.Any(id =>
                    id.Equals("DUALCAST", StringComparison.OrdinalIgnoreCase));
                bool hasZap = state.DeckCardIds.Any(id =>
                    id.Equals("ZAP", StringComparison.OrdinalIgnoreCase));
                if (hasDualcast)
                {
                    score += 35; // Major boost — Dualcast upgrade is premium
                    MainFile.Logger.Info("[RestDecider] Smith boosted: Defect Dualcast upgrade priority");
                    if (state.OrbCount >= 2) score += 10;
                    if (state.FocusStat > 0) score += state.FocusStat * 5;
                }
                if (hasZap)
                {
                    score += 8; // Minor boost — Zap is secondary priority
                }
            }
            // ── B16 fix: Silent upgrade priorities ──
            // Key Silent upgrades that dramatically improve deck performance
            if (state.Character == "SILENT")
            {
                bool hasNeutralize = state.DeckCardIds.Any(id =>
                    id.Equals("NEUTRALIZE", StringComparison.OrdinalIgnoreCase));
                bool hasSurvivor = state.DeckCardIds.Any(id =>
                    id.Equals("SURVIVOR", StringComparison.OrdinalIgnoreCase));
                bool hasBladeDance = state.DeckCardIds.Any(id =>
                    id.Equals("BLADE_DANCE", StringComparison.OrdinalIgnoreCase));
                bool hasCatalyst = state.DeckCardIds.Any(id =>
                    id.Equals("CATALYST", StringComparison.OrdinalIgnoreCase));
                bool hasEviscerate = state.DeckCardIds.Any(id =>
                    id.Equals("EVISCERATE", StringComparison.OrdinalIgnoreCase));
                bool hasCripplingCloud = state.DeckCardIds.Any(id =>
                    id.Equals("CRIPPLING_CLOUD", StringComparison.OrdinalIgnoreCase));

                if (hasNeutralize) { score += 25; MainFile.Logger.Info("[RestDecider] Smith boosted: Silent Neutralize upgrade"); }
                if (hasSurvivor) { score += 15; MainFile.Logger.Info("[RestDecider] Smith boosted: Silent Survivor upgrade"); }
                if (hasBladeDance) { score += 20; MainFile.Logger.Info("[RestDecider] Smith boosted: Silent Blade Dance upgrade"); }
                if (hasCatalyst && state.HasPoisonSynergy) { score += 30; MainFile.Logger.Info("[RestDecider] Smith boosted: Silent Catalyst upgrade"); }
                if (hasEviscerate && state.HasDiscardSynergy) { score += 20; MainFile.Logger.Info("[RestDecider] Smith boosted: Silent Eviscerate upgrade"); }
                if (hasCripplingCloud) { score += 15; MainFile.Logger.Info("[RestDecider] Smith boosted: Silent Crippling Cloud upgrade"); }
            }
            // Small bonus to break ties in favor of upgrade
            score += 0.5;
        }
        else if (lower.Contains("toke") || lower.Contains("remove") ||
                 lower.Contains("purge") || lower.Contains("cleanse"))
        {
            score += state.IsDeckLarge ? rp.TokeLargeDeckBonus : rp.TokeBaseScore;
            score += state.HpRatio >= rp.SmithHpThreshold ? rp.TokeHighHpBonus : 0;
            if (state.CountBasicStrikes >= 3) score += rp.TokeStrikeCountBonus;
        }
        else if (lower.Contains("recall") || lower.Contains("key") ||
                 lower.Contains("sapphire") || lower.Contains("ruby") || lower.Contains("emerald"))
        {
            score += rp.RecallScore;
        }
        else if (lower.Contains("lift") || lower.Contains("girya") ||
                 lower.Contains("strength") || lower.Contains("train"))
        {
            score += rp.LiftScore;
        }
        else if (lower.Contains("dig") || lower.Contains("excavate") || lower.Contains("relic"))
        {
            score += rp.DigScore;
        }
        else if (lower.Contains("rest") || lower.Contains("heal"))
        {
            // Generic rest/heal option — must be LAST since all options contain "Rest"
            if (state.HpRatio < rp.RestLowHpThreshold)
            {
                score += rp.RestLowHpScore;
                if (state.Act >= 3) score += rp.RestAct3BossBonus;
            }
            else if (state.HpRatio < rp.RestMediumHpMax)
            {
                score += rp.RestMediumHpScore;
                if (!state.HasSustainRelic) score += rp.RestMediumNoSustainBonus;
            }
            else
            {
                score += rp.RestHighHpScore;
            }
        }

        return score;
    }

    private static string RestOptionLabel(NRestSiteButton btn)
    {
        try
        {
            string name = btn.Option?.GetType()?.Name ?? "?";
            if (name.Length > 30) name = name[..30];
            return name;
        }
        catch { return "?"; }
    }

    private static NRestSiteRoom? GetRestRoom()
    {
        try
        {
            // Two-step path like GameStateDetector — more robust than single long path
            var root = ((SceneTree)Engine.GetMainLoop()).Root;
            var container = root.GetNodeOrNull<Node>("Game/RootSceneContainer");
            return container?.GetNodeOrNull<NRestSiteRoom>("Run/RoomContainer/RestSiteRoom");
        }
        catch { return null; }
    }
}
