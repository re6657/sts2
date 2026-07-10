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
/// Card grid decisions: Upgrade, Transform, Enchant, Remove.
/// Upgrade: highest damage/block increase card.
/// Transform: weakest Strike/Defend.
/// Remove: Strike (if >3) > Defend (if >3) > Curses/Statuses.
/// </summary>
public static class CardGridDecider
{
    // Known preview container names
    private static readonly string[] PreviewNames =
    {
        "%PreviewContainer", "%UpgradeSinglePreviewContainer",
        "%UpgradeMultiPreviewContainer", "%EnchantSinglePreviewContainer",
        "%EnchantMultiPreviewContainer",
    };

    public static bool Decide(RunState state)
    {
        var screen = NOverlayStack.Instance?.Peek() as Node;
        if (screen == null) return false;

        // Phase 3: Preview visible — confirm it
        var visiblePreview = FindVisiblePreview(screen);
        if (visiblePreview != null)
        {
            var previewConfirm = visiblePreview.GetNodeOrNull<NConfirmButton>("Confirm")
                ?? visiblePreview.GetNodeOrNull<NConfirmButton>("%PreviewConfirm")
                ?? AutoSlayHelpers.FindFirst<NConfirmButton>(visiblePreview);
            if (previewConfirm?.IsEnabled == true)
            {
                MainFile.Logger.Info("[CardGridDecider] Clicking preview confirm");
                previewConfirm.ForceClick();
                return true;
            }
            return true; // waiting for confirm to enable
        }

        // Phase 2: Main confirm enabled — click to show preview
        var mainConfirm = screen.GetNodeOrNull<NConfirmButton>("Confirm")
            ?? screen.GetNodeOrNull<NConfirmButton>("%Confirm");
        if (mainConfirm?.IsEnabled == true)
        {
            MainFile.Logger.Info("[CardGridDecider] Clicking main confirm to show preview");
            mainConfirm.ForceClick();
            return true;
        }

        // Phase 1: Select a card using heuristics
        var cards = AutoSlayHelpers.FindAll<NGridCardHolder>(screen);
        if (cards.Count == 0) return false;

        string screenType = screen.GetType().Name;
        NGridCardHolder pick;

        // ── Set card-select context BEFORE picking ───────────────────────
        // Ensures AutoSlayCardSelector (global ICardSelector) uses the
        // correct scoring even if called later by the game engine.
        if (screenType.Contains("Upgrade"))
        {
            DecisionEngine.PendingCardSelectContext = "UPGRADE";
            pick = PickForUpgrade(cards, state);
        }
        else if (screenType.Contains("Transform"))
        {
            DecisionEngine.PendingCardSelectContext = "TRANSFORM";
            pick = PickForTransform(cards, state);
        }
        else if (screenType.Contains("Remove") || screenType.Contains("CardSelect"))
        {
            DecisionEngine.PendingCardSelectContext = "REMOVE";
            pick = PickForRemove(cards, state);
        }
        else if (screenType.Contains("Enchant"))
        {
            DecisionEngine.PendingCardSelectContext = "UPGRADE"; // Enchant ≈ upgrade
            pick = PickForEnchant(cards, state);
        }
        else
        {
            pick = PickForUpgrade(cards, state); // default: treat like upgrade
        }

        string cardId = pick.CardModel?.Id.Entry ?? "?";
        string reason = $"Selected {cardId} for {screenType} (deck={state.TotalCardCount})";

        DecisionLogger.LogDecision(
            GameScreen.OVERLAY_DECK_GRID, "CardGrid",
            cards.Select((c, i) => new DecisionLogger.OptionScore
            {
                Index = i,
                Label = c.CardModel?.Id.Entry ?? $"?",
                Score = ScoreCardForUpgrade(c.CardModel, state)
            }).ToList(),
            cards.IndexOf(pick), cardId, reason);

        MainFile.Logger.Info($"[CardGridDecider] {reason}");

        var grid = AutoSlayHelpers.FindFirst<NCardGrid>(screen);
        if (grid != null)
            grid.EmitSignal(NCardGrid.SignalName.HolderPressed, pick);
        return true;
    }

    /// <summary>Pick the card that benefits most from upgrade (highest damage/block increase).</summary>
    private static NGridCardHolder PickForUpgrade(List<NGridCardHolder> cards, RunState state)
    {
        // Prefer high-value cards; enchanted basics get priority bonus
        return Tiebreaker.PickBest(cards, c => ScoreCardForUpgrade(c.CardModel, state));
    }

    /// <summary>
    /// Returns true if the card is a basic, unmodified starter card
    /// (Strike, Defend, Bash, Neutralize, etc.) that has NOT been enchanted
    /// or otherwise modified. Cards that have been enchanted gain non-standard
    /// effects and should be treated as premium cards, not removal fodder.
    /// </summary>
    private static bool IsBasicCard(CardModel? card)
    {
        if (card == null) return false;
        if (card.IsUpgraded) return false; // Upgraded = not basic
        string id = (card.Id.Entry ?? "").ToLowerInvariant();

        // Check if the ID matches a basic starter card pattern
        // Uses the shared RunState list — single source of truth for all characters.
        bool isBasicById = RunState.IsAnyBasicCardId(id);
        if (!isBasicById) return false;

        // Now check effects: a basic card should have minimal effects.
        // If a "Strike" has vulnerable, block, strength gain, etc., it's been
        // enchanted or is a special variant → NOT basic.
        var fx = CardEffectReader.ReadEffects(card);

        // Basic Strike: only BaseDamage (6), nothing else
        if (id == "strike" || id.StartsWith("strike_"))
        {
            bool hasExtraEffects = fx.BaseBlock > 0 || fx.VulnerableStacks > 0
                || fx.WeakStacks > 0 || fx.GrantsStrength || fx.GrantsDexterity
                || fx.EnergyGain > 0 || fx.StrengthAmount > 0 || fx.DexterityAmount > 0
                || fx.HpCost > 0 || fx.IsAoe || fx.IsPower || fx.IsXCost;
            return !hasExtraEffects;
        }

        // Basic Defend: only BaseBlock (5), nothing else
        if (id == "defend" || id.StartsWith("defend_"))
        {
            bool hasExtraEffects = fx.BaseDamage > 0 || fx.VulnerableStacks > 0
                || fx.WeakStacks > 0 || fx.GrantsStrength || fx.GrantsDexterity
                || fx.EnergyGain > 0 || fx.StrengthAmount > 0 || fx.DexterityAmount > 0
                || fx.HpCost > 0 || fx.IsAoe || fx.IsPower || fx.IsXCost;
            return !hasExtraEffects;
        }

        // For other starter cards (Bash, Neutralize, etc.), they always have
        // special effects, so they're never treated as "basic" removal fodder.
        // But we still block upgrading them unless we have good reason.
        return false;
    }

    /// <summary>
    /// Returns a bonus score for cards that appear to be enchanted.
    /// An enchanted card is a basic card that has gained non-standard effects.
    /// These should be prioritized for upgrades.
    /// </summary>
    private static double GetEnchantedBonus(CardModel? card)
    {
        if (card == null) return 0;
        string id = (card.Id.Entry ?? "").ToLowerInvariant();

        // Check if this was originally a basic card that now has extra effects
        bool wasBasic = RunState.IsAnyBasicCardId(id);
        if (!wasBasic) return 0;

        var fx = CardEffectReader.ReadEffects(card);
        int extraEffectCount = 0;
        if (fx.BaseBlock > 0) extraEffectCount++;
        if (fx.VulnerableStacks > 0) extraEffectCount++;
        if (fx.WeakStacks > 0) extraEffectCount++;
        if (fx.GrantsStrength) extraEffectCount++;
        if (fx.GrantsDexterity) extraEffectCount++;
        if (fx.EnergyGain > 0) extraEffectCount++;
        if (fx.HpCost > 0) extraEffectCount++;
        if (fx.IsAoe) extraEffectCount++;
        if (fx.StrengthAmount > 0) extraEffectCount++;
        if (fx.DexterityAmount > 0) extraEffectCount++;

        // If this basic-named card has extra effects, it's likely enchanted.
        // Enchanted cards are premium upgrade targets — the enchant scales
        // multiplicatively with the upgrade, making them far more valuable
        // than a vanilla card upgrade.
        if (extraEffectCount > 0)
            return 150 + extraEffectCount * 30; // Very strong: beats any non-enchanted card
        return 0;
    }

    /// <summary>Public accessor for external callers (e.g. rest site inline handling).</summary>
    public static double ScoreCardForUpgradePublic(CardModel? card, RunState state)
        => ScoreCardForUpgrade(card, state);

    private static double ScoreCardForUpgrade(CardModel? card, RunState state)
    {
        if (card == null) return -1000;
        if (card.IsUpgraded) return -500; // already upgraded — very low priority
        if (card.Type == CardType.Curse || card.Type == CardType.Status) return -800;

        var fx = CardEffectReader.ReadEffects(card);

        // ── Enchanted card bonus: cards that were enchanted gain priority ──
        double enchantedBonus = GetEnchantedBonus(card);

        // ── Upgrade delta: estimated benefit from upgrading ─────────────
        double upgradeDelta = 0;

        // Direct stat increases (typical upgrade is +3-4 damage or +3-4 block)
        int damageGain = fx.BaseDamage > 0 ? Math.Max(1, (int)(fx.BaseDamage * 0.3)) : 0;
        int blockGain = fx.BaseBlock > 0 ? Math.Max(1, (int)(fx.BaseBlock * 0.3)) : 0;
        upgradeDelta += damageGain * 8;
        upgradeDelta += blockGain * 7;

        // Cost reduction on upgrade is premium
        if (card.EnergyCost.CostsX)
            upgradeDelta += 30;
        // Cards we play often (low cost) benefit more from upgrades
        int cost = CardCost(card);
        if (cost <= 1) upgradeDelta += 15;
        if (cost >= 3) upgradeDelta -= 10;

        // ── Card quality: how good the card is in our deck ─────────────
        // Blend with CardRewardDecider scoring for consistency
        double cardQuality = 0;
        string cardIdLower = card.Id.Entry?.ToLowerInvariant() ?? "";

        // Powers are high-value upgrades
        if (card.Type == CardType.Power || fx.IsPower)
            cardQuality += 50;

        // Energy generation cards are premium upgrades
        if (fx.EnergyGain > 0)
            cardQuality += fx.EnergyGain * 30;

        // Strength/Dexterity granting cards
        if (fx.GrantsStrength)
            cardQuality += fx.StrengthAmount * 25;
        if (fx.GrantsDexterity)
            cardQuality += fx.DexterityAmount * 20;

        // AOE cards scale well with upgrades
        if (fx.IsAoe)
            cardQuality += 20;

        // Vulnerable/Weak application — debuff upgrades are valuable
        if (fx.VulnerableStacks > 0)
            cardQuality += fx.VulnerableStacks * 15;
        if (fx.WeakStacks > 0)
            cardQuality += fx.WeakStacks * 12;

        // Draw cards — more draw = more consistency
        if (CardRewardDecider.DrawCardSet.Contains(card.Id.Entry?.ToUpperInvariant() ?? ""))
            cardQuality += 22;

        // High-impact attacks get priority
        if (fx.BaseDamage >= 12 && cost <= 2)
            cardQuality += 18;

        // High-impact block cards
        if (fx.BaseBlock >= 8 && cost <= 1)
            cardQuality += 15;

        // ── Hard block: NEVER upgrade basic Strike or Defend ──────────
        // But ALLOW enchanted variants (detected by IsBasicCard)
        if (IsBasicCard(card))
            return -2000;

        // ── Act-aware adjustments ──────────────────────────────────────
        if (state.Act == 1 && fx.BaseDamage >= 12)
            cardQuality += 10; // Early game: prioritize attack upgrades
        if (state.Act >= 3 && card.Type == CardType.Power)
            cardQuality += 10; // Late game: power upgrades scale well

        // ── Defect: DUALCAST is the highest-priority upgrade ─────────
        // Dualcast upgrades from 0-cost evoke once → 0-cost evoke twice,
        // effectively doubling its damage output. Combined with Focus scaling,
        // this is the single most impactful upgrade for Defect.
        if (state.Character == "DEFECT" && cardIdLower == "dualcast")
        {
            double dualcastBonus = 250; // Huge bonus — highest upgrade priority
            // Even higher if we have strong orbs or focus
            if (state.OrbCount >= 2) dualcastBonus += 50;
            if (state.FocusStat > 0) dualcastBonus += state.FocusStat * 20;
            upgradeDelta += dualcastBonus;
        }

        // ── Defect: ZAP is the second-highest upgrade priority ─────
        // Zap upgrades from 0-cost Channel 1 Lightning → 0-cost Channel 2 Lightning,
        // doubling orb output for free. Second only to Dualcast for Defect.
        if (state.Character == "DEFECT" && cardIdLower == "zap")
        {
            double zapBonus = 180; // Very high — second only to Dualcast
            // Even better with orb slots available or focus
            if (state.OrbCount >= 3) zapBonus += 30;
            if (state.FocusStat > 0) zapBonus += state.FocusStat * 15;
            upgradeDelta += zapBonus;
        }

        // ── Combine: upgrade delta weighted higher, card quality as tiebreaker ──
        // Enchanted bonus is added on top
        return upgradeDelta + cardQuality * 0.3 + enchantedBonus;
    }

    /// <summary>Transform uses the same priority as removal: Strike/Defend first.</summary>
    private static NGridCardHolder PickForTransform(List<NGridCardHolder> cards, RunState state)
    {
        // Same logic as card removal: transform Strikes > Defends > curses > statuses
        // Good cards (upgraded, enchanted, high value) are never transformed
        return Tiebreaker.PickBestAscending(cards, c => ScoreCardForRemove(c.CardModel, state));
    }

    /// <summary>Remove Strike (if >3), then Defend (if >3), then curses/statuses.</summary>
    private static NGridCardHolder PickForRemove(List<NGridCardHolder> cards, RunState state)
    {
        return Tiebreaker.PickBestAscending(cards, c => ScoreCardForRemove(c.CardModel, state));
    }

    /// <summary>Enchant: prefer cards we play often (low cost, high impact).</summary>
    private static NGridCardHolder PickForEnchant(List<NGridCardHolder> cards, RunState state)
    {
        return Tiebreaker.PickBest(cards, c => ScoreCardForEnchant(c.CardModel, state));
    }

    private static double ScoreCardForEnchant(CardModel? card, RunState state)
    {
        if (card == null) return -1000;
        if (card.IsUpgraded) return 200; // Already upgraded = even more value from enchant
        if (card.Type == CardType.Curse || card.Type == CardType.Status) return -800;

        var fx = CardEffectReader.ReadEffects(card);
        double score = 0;

        // Enchant benefits cards we play frequently
        int cost = CardCost(card);
        if (cost <= 1) score += 50;  // Low cost = played more = more enchant value
        if (cost == 2) score += 25;
        if (cost >= 3) score -= 15;

        // Multi-hit cards get more value from damage enchants
        if (fx.BaseDamage > 0 && fx.BaseDamage <= 4) score += 30; // Likely multi-hit
        if (fx.IsAoe) score += 25;

        // Powers are often enchanted for extra effects
        if (card.Type == CardType.Power || fx.IsPower) score += 20;

        // ── Hard block: NEVER enchant basic cards ─────────────────────
        // Use IsBasicCard to detect truly basic cards; enchanted variants pass through
        if (IsBasicCard(card))
            return -2000;

        // Cards with draw are great to enchant
        try
        {
            var drawProp = card.GetType().GetProperty("DrawCount");
            if (drawProp != null)
            {
                var val = drawProp.GetValue(card);
                if (val is int drawVal && drawVal > 0)
                    score += drawVal * 20;
            }
        }
        catch { }

        return score;
    }

    private static double ScoreCardForRemove(CardModel? card, RunState state)
    {
        if (card == null) return 0;
        string id = (card.Id.Entry ?? "").ToLowerInvariant();

        // ── NEVER remove upgraded cards ──────────────────────────────
        if (card.IsUpgraded)
            return 500; // very high: never pick for removal

        // ── NEVER remove enchanted cards ─────────────────────────────
        // If a card was originally basic but now has extra effects,
        // it's been enchanted — do NOT remove it.
        if (GetEnchantedBonus(card) > 0)
            return 500; // same as upgraded: never remove

        // Always remove curses
        if (card.Type == CardType.Curse)
            return -1000;

        // Statuses shouldn't be in deck but just in case
        if (card.Type == CardType.Status)
            return -900;

        // ── Strikes: ALWAYS prioritize for removal ─────────────────
        // Use ID matching instead of IsBasicCard to avoid CardEffectReader
        // false positives (e.g., reflection incorrectly detecting block on a
        // vanilla Strike).  Enchanted/upgraded Strikes are protected above.
        if (id == "strike" || id.StartsWith("strike_"))
        {
            // Double-check: if IsBasicCard says this Strike has extra effects
            // but GetEnchantedBonus returned 0 (not enough effects), it's
            // still a valid removal target — just lower priority.
            // Use CountBasicStrikes (prefix-matched, excludes Pommel/Twin/etc.)
            // NOT CountCardsById which uses Contains and matches non-basic Strikes.
            bool isTrulyBasic = IsBasicCard(card);
            int strikeCount = state.CountBasicStrikes;
            if (strikeCount > 3)
                return isTrulyBasic ? -500 : -450;
            else
                return isTrulyBasic ? -100 : -90;
        }

        // ── Defends: ALWAYS prioritize for removal ──────────────────
        if (id == "defend" || id.StartsWith("defend_"))
        {
            bool isTrulyBasic = IsBasicCard(card);
            // Use CountBasicDefends (prefix-matched) NOT CountCardsById.
            int defendCount = state.CountBasicDefends;
            if (defendCount > 3)
                return isTrulyBasic ? -400 : -350;
            else
                return isTrulyBasic ? -80 : -70;
        }

        // Keep good cards
        return 100;
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

    private static Control? FindVisiblePreview(Node screen)
    {
        foreach (var name in PreviewNames)
        {
            var container = screen.GetNodeOrNull<Control>(name);
            if (container?.Visible == true)
                return container;
        }
        return null;
    }
}
