using System;
using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;

using TokenSpire2.Core;

namespace TokenSpire2.Solver;

/// <summary>
/// Context-aware choose-a-card screen decisions.
///
/// NOT just a generic "pick the best card" — the context matters critically:
///   - Headbutt/Warcry: pick the BEST card to put on top (you'll draw it next)
///   - True Grit/Burning Pact: pick the WORST card to exhaust
///   - Armaments: pick the best UPGRADE candidate (unupgraded, high impact)
///   - Exhume/Hologram: pick the BEST card to retrieve
///   - Secret Technique/Weapon: pick the best Skill/Attack from draw
/// </summary>
public static class ChooseCardDecider
{
    public static bool Decide(RunState state)
    {
        var screen = NOverlayStack.Instance?.Peek() as NChooseACardSelectionScreen;
        if (screen == null) return false;

        var holders = AutoSlayHelpers.FindAll<NCardHolder>(screen);
        if (holders.Count == 0) return false;

        string context = DecisionEngine.PendingCardSelectContext;
        string triggerCardId = DecisionEngine.PendingCardSelectCardId;

        var scored = holders.Select((h, i) =>
        {
            var card = h.CardModel;
            double score = ScoreForContext(card, state, context);
            string label = $"{card?.Id.Entry ?? "?"} (cost={CardCost(card)})";
            return (index: i, score, label, holder: h);
        }).OrderByDescending(x => x.score).ToList();

        var best = Tiebreaker.PickBestFromSorted(scored, x => x.score);
        string reason = $"Chose {best.label} score={best.score:F0} context={context} trigger={triggerCardId} from {holders.Count} options";

        DecisionLogger.LogDecision(
            GameScreen.OVERLAY_CHOOSE_CARD, "ChooseCard",
            scored.Select(s => new DecisionLogger.OptionScore
            {
                Index = s.index, Label = s.label, Score = s.score
            }).ToList(),
            best.index, best.label, reason);

        MainFile.Logger.Info($"[ChooseCardDecider] {reason}");
        best.holder.EmitSignal(NCardHolder.SignalName.Pressed, best.holder);

        // Clear context after handling (avoid stale context on next screen)
        DecisionEngine.ClearPendingCardSelect();
        return true;
    }

    /// <summary>
    /// Score a card for selection, ADJUSTED FOR CONTEXT.
    ///
    /// EXHAUST context: prefer Strike/Defend/Curse/Status (lowest value)
    /// PUT_ON_TOP context: prefer high-impact cards (damage, power, upgraded)
    /// UPGRADE context: prefer unupgraded cards with upgrade potential
    /// RETRIEVE context: prefer high-value cards
    /// FETCH_SKILL/ATTACK: prefer best matching type
    /// </summary>
    private static double ScoreForContext(CardModel? card, RunState state, string context)
    {
        if (card == null) return -1000;

        string cardId = card.Id.Entry ?? "";
        string cardIdUpper = cardId.ToUpperInvariant();
        int cost = CardCost(card);

        switch (context)
        {
            case "EXHAUST":
                // Prefer to exhaust the WORST cards — Strike, Defend, Curse, Status
                return ScoreForExhaust(card, state);

            case "PUT_ON_TOP":
                // Prefer high-impact cards to replay next turn — high damage, powers, upgraded
                return ScoreForPutOnTop(card, state);

            case "UPGRADE":
                // Prefer unupgraded cards with the most upgrade potential
                return ScoreForUpgrade(card, state);

            case "RETRIEVE":
                // Prefer the best card — same as PUT_ON_TOP
                return ScoreForPutOnTop(card, state);

            case "FETCH_SKILL":
                // Prefer best Skill card
                if (card.Type != CardType.Skill) return -500;
                return ScoreForPutOnTop(card, state);

            case "FETCH_ATTACK":
                // Prefer best Attack card
                if (card.Type != CardType.Attack) return -500;
                return ScoreForPutOnTop(card, state);

            default:
                // Unknown context — default to "pick best" (safe for most cases)
                // But apply heavy penalty to strikes/defends if there are better options
                return CardRewardDecider.ScoreCard(card, state);
        }
    }

    /// <summary>
    /// Score for EXHAUST context — pick the WORST card.
    /// Strikes, Defends, Curses, Statuses should be exhausted first.
    /// </summary>
    private static double ScoreForExhaust(CardModel? card, RunState state)
    {
        if (card == null) return 1000; // null cards don't exist, don't pick
        if (card.Type == CardType.Curse) return 1000; // ALWAYS exhaust curses
        if (card.Type == CardType.Status) return 900;  // Exhaust statuses

        string id = card.Id.Entry?.ToLowerInvariant() ?? "";
        var fx = CardEffectReader.ReadEffects(card);

        // Basic Defends: best exhaust targets (prefer over Strike — keep damage)
        if (id.Contains("defend"))
            return 800;

        // Basic Strikes: good exhaust targets
        if (id.Contains("strike") && !id.Contains("perfected") && !id.Contains("twin")
            && !id.Contains("pommel") && !id.Contains("wild"))
            return 700;

        // Cards that are dead in current fight (e.g., block cards when enemy isn't attacking)
        // Low-value cards with minimal impact
        double baseScore = CardRewardDecider.ScoreCard(card, state);

        // Invert: lower score = better exhaust target
        // Max score means "exhaust this first"
        return 300 - baseScore;
    }

    /// <summary>
    /// Score for PUT_ON_TOP context — pick the BEST card to draw next turn.
    /// High damage, powers, upgraded, energy generation are all premium.
    /// </summary>
    private static double ScoreForPutOnTop(CardModel? card, RunState state)
    {
        if (card == null) return -1000;
        if (card.Type == CardType.Curse || card.Type == CardType.Status)
            return -500; // Don't put curses/status back on top unless no choice

        double score = CardRewardDecider.ScoreCard(card, state);

        // Bonus for cards that give immediate impact next turn
        var fx = CardEffectReader.ReadEffects(card);
        int cost = CardCost(card);

        // You have exactly as much energy next turn as this turn
        // so affordable costs are good
        if (cost <= 1) score += 10;
        if (cost >= 2 && fx.BaseDamage >= 15) score += 15; // Worth the energy
        if (cost >= 2 && fx.BaseBlock >= 12) score += 12;

        // Powers are great to replay
        if (card.Type == CardType.Power || fx.IsPower) score += 25;

        // Upgraded cards are premium
        if (card.IsUpgraded) score += 20;

        // Energy gain cards let you play more
        if (fx.EnergyGain > 0) score += fx.EnergyGain * 15;

        return score;
    }

    /// <summary>
    /// Score for UPGRADE context — pick the best upgrade candidate.
    /// Prefer unupgraded cards with high upgrade potential.
    /// </summary>
    private static double ScoreForUpgrade(CardModel? card, RunState state)
    {
        if (card == null) return -1000;
        if (card.IsUpgraded) return -200; // Already upgraded, skip
        if (card.Type == CardType.Curse || card.Type == CardType.Status)
            return -800; // Don't upgrade curses

        var fx = CardEffectReader.ReadEffects(card);
        double score = 0;

        // Cards that gain significant damage/block from upgrade
        int damageGain = fx.BaseDamage > 0 ? Math.Max(3, (int)(fx.BaseDamage * 0.3)) : 0;
        int blockGain = fx.BaseBlock > 0 ? Math.Max(2, (int)(fx.BaseBlock * 0.3)) : 0;

        score += damageGain * 8;
        score += blockGain * 7;

        // Powers are often high value to upgrade
        if (card.Type == CardType.Power || fx.IsPower)
            score += 40;

        // Cards we play often (low cost) benefit more from upgrades
        int cost = CardCost(card);
        if (cost <= 1) score += 15;
        if (cost >= 3) score -= 10;

        // Cards with debuffs often get extra stacks on upgrade
        if (fx.VulnerableStacks > 0) score += 15;
        if (fx.WeakStacks > 0) score += 12;

        // Basic cards: lower priority for upgrade
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
