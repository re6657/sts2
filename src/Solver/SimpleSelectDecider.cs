using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;

namespace TokenSpire2.Solver;

/// <summary>
/// Simple card select screen (e.g., Headbutt/Armaments mid-combat, events,
/// multi-card selection, transforms, etc.).
///
/// KEY IMPROVEMENT over original:
///   1. Context-aware scoring — knows whether we're exhausting, putting on top, upgrading, etc.
///   2. Only clicks confirm AFTER enough cards are selected (not before).
///   3. Stuck detection with intelligent fallback — doesn't re-select the same card forever.
///   4. Avoids already-selected cards using NGridCardHolder.Selected property (game-native).
/// </summary>
public static class SimpleSelectDecider
{
    private static Node? _lastScreen;
    private static readonly HashSet<string> _selectedCardIds = new();
    private static int _selectAttempts;
    private static int _stuckResetCount;

    public static bool Decide(RunState state)
    {
        var screen = NOverlayStack.Instance?.Peek() as Node;
        if (screen == null)
        {
            ResetTracking();
            return false;
        }

        // Reset tracking when screen changes
        if (screen != _lastScreen)
        {
            ResetTracking();
            _lastScreen = screen;
        }

        // ── Determine how many cards need to be selected ──────────────
        int maxSelect = GetMaxSelectCount(screen);
        int selectedCount = CountSelectedCards(screen);
        string context = DecisionEngine.PendingCardSelectContext;

        MainFile.Logger.Info(
            $"[SimpleSelect] maxSelect={maxSelect} selected={selectedCount} " +
            $"attempts={_selectAttempts} context={context}");

        // ── Priority 1: click confirm IF enough cards are selected ───
        if (selectedCount >= maxSelect)
        {
            var confirmBtn = FindConfirmButton(screen);
            if (confirmBtn != null)
            {
                MainFile.Logger.Info($"[SimpleSelectDecider] {selectedCount}/{maxSelect} selected, clicking confirm: {confirmBtn.GetType().Name}");
                confirmBtn.ForceClick();
                ResetTracking();
                DecisionEngine.ClearPendingCardSelect();
                return true;
            }
        }

        // ── Priority 2: select unselected cards ──────────────────────
        var cards = AutoSlayHelpers.FindAll<NGridCardHolder>(screen);
        if (cards.Count > 0)
        {
            // Stuck detection: if we've tried too many times, something is wrong
            if (_selectAttempts > Math.Max(cards.Count * 4 + 10, 20) + _stuckResetCount * cards.Count)
            {
                MainFile.Logger.Error(
                    $"[SimpleSelectDecider] STUCK after {_selectAttempts} attempts " +
                    $"(cards={cards.Count}, maxSelect={maxSelect}, selected={selectedCount})! " +
                    $"Resetting tracking and clearing context.");
                // Clear context — it might be wrong and causing bad selections
                DecisionEngine.ClearPendingCardSelect();
                ResetTracking();
                _stuckResetCount++;
                _lastScreen = screen; // Keep same screen to avoid infinite reset loop
                return false; // Skip this frame, try fresh next frame
            }

            // Pick the best card, avoiding already-selected ones
            var best = PickBestCard(cards, state, context);
            NGridCardHolder pick;
            if (best.HasValue)
            {
                pick = best.Value.holder;
                string cardId = pick.CardModel?.Id.Entry ?? "";
                _selectedCardIds.Add(cardId);
                _selectAttempts++;

                MainFile.Logger.Info(
                    $"[SimpleSelectDecider] Selecting {best.Value.label} " +
                    $"score={best.Value.score:F0} " +
                    $"(attempt {_selectAttempts}, {_selectedCardIds.Count} tracked, " +
                    $"context={context})");

                DecisionLogger.LogDecision(
                    GameScreen.OVERLAY_SIMPLE_SELECT, "SimpleSelect",
                    new List<DecisionLogger.OptionScore>
                    {
                        new() { Index = 0, Label = best.Value.label, Score = best.Value.score }
                    },
                    0, best.Value.label,
                    $"Card select #{_selectAttempts}: {best.Value.label} context={context}");

                var grid = AutoSlayHelpers.FindFirst<NCardGrid>(screen);
                if (grid != null)
                    grid.EmitSignal(NCardGrid.SignalName.HolderPressed, pick);
                return true;
            }
            else
            {
                // All cards already selected, but we still need more.
                // This can happen if _selectedCardIds is stale due to card transformations.
                // Reset tracking and try again.
                MainFile.Logger.Info(
                    $"[SimpleSelectDecider] All {cards.Count} cards already tracked selected! " +
                    $"Need {maxSelect} but have {selectedCount}. Resetting tracked set.");
                _selectedCardIds.Clear();
                _selectAttempts++;
                return false;
            }
        }

        return false;
    }

    private static void ResetTracking()
    {
        _lastScreen = null;
        _selectedCardIds.Clear();
        _selectAttempts = 0;
        _stuckResetCount = 0;
    }

    /// <summary>
    /// Find any confirm/proceed/choose button on the screen.
    /// Only returns it if ENOUGH cards are selected (checked by caller).
    /// </summary>
    private static NButton? FindConfirmButton(Node screen)
    {
        // Try standard confirm types
        var confirm = screen.GetNodeOrNull<NConfirmButton>("%Confirm");
        if (confirm?.IsEnabled == true) return confirm;

        confirm = screen.GetNodeOrNull<NConfirmButton>("Confirm");
        if (confirm?.IsEnabled == true) return confirm;

        var proceed = screen.GetNodeOrNull<NProceedButton>("%Proceed");
        if (proceed?.IsEnabled == true) return proceed;

        proceed = screen.GetNodeOrNull<NProceedButton>("Proceed");
        if (proceed?.IsEnabled == true) return proceed;

        // Try all clickable controls with confirm-like text (but NOT skip/cancel)
        var allClickables = AutoSlayHelpers.FindAll<NClickableControl>(screen);
        foreach (var c in allClickables)
        {
            if (!c.IsEnabled) continue;
            try
            {
                string name = (c.Name?.ToString() ?? "").ToLowerInvariant();
                // Only return if it's a confirm/proceed, NOT skip/cancel
                if ((name.Contains("confirm") || name.Contains("proceed") ||
                     name.Contains("choose") || name.Contains("done") ||
                     name.Contains("finish") || name.Contains("accept"))
                    && !name.Contains("skip") && !name.Contains("cancel"))
                {
                    if (c is NButton btn) return btn;
                }
                var childBtn = c.GetNodeOrNull<NButton>("%Confirm") ??
                               c.GetNodeOrNull<NButton>("%Proceed") ??
                               c.GetNodeOrNull<NButton>("%Choose");
                if (childBtn?.IsEnabled == true) return childBtn;
            }
            catch { }
        }

        // Last resort: any enabled NProceedButton or NConfirmButton
        foreach (var c in allClickables)
        {
            if ((c is NProceedButton || c is NConfirmButton) && c.IsEnabled)
                return c as NButton;
        }

        return null;
    }

    /// <summary>Count how many cards are currently selected on the screen.</summary>
    private static int CountSelectedCards(Node screen)
    {
        int count = 0;
        try
        {
            var cards = AutoSlayHelpers.FindAll<NGridCardHolder>(screen);
            foreach (var c in cards)
            {
                try
                {
                    // NGridCardHolder has a Selected property
                    var selectedProp = c.GetType().GetProperty("Selected");
                    if (selectedProp != null)
                    {
                        if (selectedProp.GetValue(c) is true) count++;
                    }
                }
                catch { }
            }
        }
        catch { }
        return count;
    }

    /// <summary>Get how many cards need to be selected.</summary>
    private static int GetMaxSelectCount(Node screen)
    {
        try
        {
            var t = screen.GetType();
            foreach (var propName in new[] { "MaxSelectCount", "NumCards", "CardsToSelect",
                "SelectCount", "RequiredCount", "CardsRequired" })
            {
                var prop = t.GetProperty(propName);
                if (prop != null)
                {
                    var val = prop.GetValue(screen);
                    if (val is int ival && ival > 0) return ival;
                }
            }
        }
        catch { }
        return 1; // default: single card select
    }

    /// <summary>Pick the best card, skipping already-selected ones.</summary>
    private static (NGridCardHolder holder, string label, double score)? PickBestCard(
        List<NGridCardHolder> cards, RunState state, string context)
    {
        var scored = cards
            .Where(c =>
            {
                // Skip already-selected cards (by ID tracking)
                string? id = c.CardModel?.Id.Entry;
                if (id != null && _selectedCardIds.Contains(id)) return false;

                // Also skip cards that the game says are already selected
                try
                {
                    var selectedProp = c.GetType().GetProperty("Selected");
                    if (selectedProp != null && selectedProp.GetValue(c) is true)
                        return false;
                }
                catch { }

                return true;
            })
            .Select(c =>
            {
                double score = ScoreForSimpleSelect(c.CardModel, state, context);
                string label = $"{c.CardModel?.Id.Entry ?? "?"}";
                return (holder: c, label, score);
            })
            .OrderByDescending(x => x.score)
            .ToList();

        if (scored.Count == 0) return null;
        return Tiebreaker.PickBestFromSorted(scored, x => x.score);
    }

    /// <summary>
    /// Score a card for simple select, WITH CONTEXT AWARENESS.
    ///
    /// EXHAUST context: pick the WORST card (Strike, Defend, Curse)
    /// PUT_ON_TOP / RETRIEVE context: pick the BEST card
    /// UPGRADE context: pick best upgrade candidate
    /// Default: pick best card (safer than old random behavior)
    /// </summary>
    private static double ScoreForSimpleSelect(CardModel? card, RunState state, string context)
    {
        if (card == null) return -1000;

        switch (context)
        {
            case "EXHAUST":
                return ScoreForExhaust(card, state);

            case "PUT_ON_TOP":
            case "RETRIEVE":
                return ScoreForPutOnTop(card, state);

            case "UPGRADE":
                return ScoreForUpgrade(card, state);

            case "FETCH_SKILL":
                if (card.Type != CardType.Skill) return -200;
                return ScoreForPutOnTop(card, state);

            case "FETCH_ATTACK":
                if (card.Type != CardType.Attack) return -200;
                return ScoreForPutOnTop(card, state);

            default:
                // Unknown context: use the card reward scorer (safe default)
                // But still apply basic penalties to avoid dumb picks
                return ScoreForPutOnTop(card, state);
        }
    }

    // ── Context-specific scoring (reused from ChooseCardDecider logic) ──────

    private static double ScoreForExhaust(CardModel? card, RunState state)
    {
        if (card == null) return -1000;
        if (card.Type == CardType.Curse) return 2000; // ALWAYS exhaust curses first
        if (card.Type == CardType.Status) return 1500;

        string id = card.Id.Entry?.ToLowerInvariant() ?? "";
        var fx = CardEffectReader.ReadEffects(card);

        // Basic Defends: best exhaust targets (prefer over Strike — keep damage)
        if (id.Contains("defend"))
            return 1000;

        // Basic Strikes: good exhaust targets
        if (id.Contains("strike") && !id.Contains("perfected") && !id.Contains("twin")
            && !id.Contains("pommel") && !id.Contains("wild"))
            return 800;

        // Generic: lower-value cards are better exhaust targets
        double baseScore = CardRewardDecider.ScoreCard(card, state);
        return 500 - baseScore;
    }

    private static double ScoreForPutOnTop(CardModel? card, RunState state)
    {
        if (card == null) return -1000;
        if (card.Type == CardType.Curse || card.Type == CardType.Status)
            return -300;

        double score = CardRewardDecider.ScoreCard(card, state);

        var fx = CardEffectReader.ReadEffects(card);
        int cost = CardCost(card);

        // Affordable cards that give impact when drawn next turn
        if (cost <= 1) score += 8;
        if (cost >= 2 && (fx.BaseDamage >= 15 || fx.BaseBlock >= 12))
            score += 12;

        if (card.Type == CardType.Power || fx.IsPower) score += 20;
        if (card.IsUpgraded) score += 18;
        if (fx.EnergyGain > 0) score += fx.EnergyGain * 12;

        return score;
    }

    private static double ScoreForUpgrade(CardModel? card, RunState state)
    {
        if (card == null) return -1000;
        if (card.IsUpgraded) return -100;
        if (card.Type == CardType.Curse || card.Type == CardType.Status) return -500;

        var fx = CardEffectReader.ReadEffects(card);
        double score = 0;

        int damageGain = fx.BaseDamage > 0 ? Math.Max(2, (int)(fx.BaseDamage * 0.25)) : 0;
        int blockGain = fx.BaseBlock > 0 ? Math.Max(2, (int)(fx.BaseBlock * 0.25)) : 0;
        score += damageGain * 8 + blockGain * 7;

        if (card.Type == CardType.Power || fx.IsPower) score += 35;

        int cost = CardCost(card);
        if (cost <= 1) score += 12;

        string id = card.Id.Entry?.ToLowerInvariant() ?? "";
        if (id.Contains("strike") && !id.Contains("perfected") && !id.Contains("twin")
            && !id.Contains("pommel"))
            score -= 30;
        if (id.Contains("defend"))
            score -= 25;

        return score;
    }

    private static int CardCost(CardModel? card)
    {
        if (card == null) return 99;
        try
        {
            if (card.EnergyCost.CostsX) return 1;
            return card.EnergyCost.Canonical;
        }
        catch { return 99; }
    }
}
