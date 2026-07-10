using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;

using TokenSpire2.Core;

namespace TokenSpire2.Solver;

/// <summary>
/// Relic quality evaluation for relic selection screens (boss chest, events).
/// Scores: Energy relics > Strength/Dex scaling > Defensive > Niche > Cursed.
/// </summary>
public static class RelicDecider
{
    public static bool Decide(RunState state)
    {
        var screen = NOverlayStack.Instance?.Peek() as NChooseARelicSelection;
        if (screen == null) return false;

        var clickables = AutoSlayHelpers.FindAll<NClickableControl>(screen);
        if (clickables.Count == 0) return false;

        var scored = clickables.Select((c, i) =>
        {
            string label = RelicLabel(c, i);
            double score = ScoreRelicByName(label, state);
            return (index: i, score, label, clickable: c);
        }).OrderByDescending(x => x.score).ToList();

        var best = Tiebreaker.PickBestFromSorted(scored, x => x.score);
        string reason = $"Chose relic {best.label} score={best.score:F0}";

        DecisionLogger.LogDecision(
            GameScreen.OVERLAY_CHOOSE_RELIC, "RelicChoice",
            scored.Select(s => new DecisionLogger.OptionScore
            {
                Index = s.index, Label = s.label, Score = s.score
            }).ToList(),
            best.index, best.label, reason);

        MainFile.Logger.Info($"[RelicDecider] {reason}");
        best.clickable.ForceClick();
        return true;
    }

    /// <summary>Score a relic by name-based heuristics. Higher = better.</summary>
    public static double ScoreRelicByName(string name, RunState state)
    {
        double score = 50; // baseline
        string idLower = name.ToLower();

        // ── S-Tier: Energy relics ──
        if (idLower.Contains("prison") || idLower.Contains("sozu") ||
            idLower.Contains("coffee") || idLower.Contains("dripper") ||
            idLower.Contains("ectoplasm") || idLower.Contains("cursed") ||
            idLower.Contains("philosopher") || idLower.Contains("battery") ||
            idLower.Contains("core") || idLower.Contains("nuclear"))
            score += 250;

        // ── A-Tier: Strong scaling ──
        if (idLower.Contains("vajra") || idLower.Contains("girya") ||
            idLower.Contains("duvu") || idLower.Contains("shuriken") ||
            idLower.Contains("kunai") || idLower.Contains("fan") ||
            idLower.Contains("clock") || idLower.Contains("wrist"))
            score += 150;

        if (idLower.Contains("smooth") || idLower.Contains("oddly") ||
            idLower.Contains("mummified") || idLower.Contains("charon"))
            score += 120;

        if (idLower.Contains("top") || idLower.Contains("sundial") ||
            idLower.Contains("pocket") || idLower.Contains("violet"))
            score += 130;

        // ── B-Tier: Good defense ──
        if (idLower.Contains("anchor") || idLower.Contains("horn") ||
            idLower.Contains("cleat") || idLower.Contains("boat") ||
            idLower.Contains("thread") || idLower.Contains("needle") ||
            idLower.Contains("helix") || idLower.Contains("tori"))
            score += 90;

        if (idLower.Contains("blood") && idLower.Contains("vial"))
            score += 100;

        if (idLower.Contains("meat") || idLower.Contains("bone") ||
            idLower.Contains("mango") || idLower.Contains("pear") ||
            idLower.Contains("strawberry") || idLower.Contains("waffle"))
            score += state.IsHpLow ? 120 : 60;

        // ── C-Tier: Niche/Situational ──
        if (idLower.Contains("darkstone") || idLower.Contains("maw") ||
            idLower.Contains("cauldron") || idLower.Contains("bottle"))
            score += 30;

        // ── Boss relic special handling ──
        if (idLower.Contains("crown") && state.TotalCardCount < 15)
            score -= 80;

        if (idLower.Contains("hammer"))
            score -= 20;

        if (idLower.Contains("bell"))
            score -= 30;

        if (idLower.Contains("tiny") && idLower.Contains("house"))
            score += 40;

        // ── Character-specific relic bonuses ──────────────────────────
        // Different characters value relics differently based on their mechanics.
        string charId = state.Character.ToUpperInvariant();
        switch (charId)
        {
            case "IRONCLAD":
                // Ironclad loves: strength, self-damage synergy, exhaust synergy
                if (idLower.Contains("vajra") || idLower.Contains("girya") ||
                    idLower.Contains("duvu") || idLower.Contains("stone"))
                    score += 30; // Strength relics
                if (idLower.Contains("charon") || idLower.Contains("dead") ||
                    idLower.Contains("branch"))
                    score += 40; // Exhaust relics
                if (idLower.Contains("rupture") || idLower.Contains("pain"))
                    score += 25; // Self-damage relics
                if (idLower.Contains("burning") || idLower.Contains("blood"))
                    score += 20; // Sustain — Ironclad starter relic already heals
                if (idLower.Contains("calipers"))
                    score += 25; // Block retention for Barricade
                break;

            case "SILENT":
                // Silent loves: dexterity, discard, poison, shiv
                if (idLower.Contains("kunai") || idLower.Contains("fan") ||
                    idLower.Contains("smooth") || idLower.Contains("oddly") ||
                    idLower.Contains("wrist"))
                    score += 35; // Dexterity relics
                if (idLower.Contains("bandages") || idLower.Contains("tingsha"))
                    score += 30; // Discard relics
                if (idLower.Contains("snecko") && idLower.Contains("skull"))
                    score += 30; // Poison relic
                if (idLower.Contains("shuriken"))
                    score += 25; // Strength on attacks (shiv synergy)
                if (idLower.Contains("top"))
                    score += 15; // 0-cost shiv synergy
                if (idLower.Contains("specimen"))
                    score += 20; // Poison spread
                break;

            case "DEFECT":
                // Defect loves: focus, orb slots, power synergy
                if (idLower.Contains("data") || idLower.Contains("inserter") ||
                    idLower.Contains("capacitor") || idLower.Contains("cables"))
                    score += 40; // Orb/focus relics
                if (idLower.Contains("core") || idLower.Contains("battery"))
                    score += 45; // Energy + orb synergy
                if (idLower.Contains("mummified") || idLower.Contains("bird"))
                    score += 20; // Power synergy
                if (idLower.Contains("emotion") || idLower.Contains("chip"))
                    score += 20; // Frost/lightning buffs
                if (idLower.Contains("gold") && idLower.Contains("plated"))
                    score += 15; // Defense for setup turns
                if (idLower.Contains("symbiotic"))
                    score += 15; // Orb generation
                break;

            case "NECROBINDER":
                // Necrobinder loves: star generation, ethereal synergy, curse synergy
                if (idLower.Contains("star") || idLower.Contains("celestial"))
                    score += 35; // Star relics
                if (idLower.Contains("duvu") || idLower.Contains("darkstone") ||
                    idLower.Contains("cursed") && !idLower.Contains("key"))
                    score += 30; // Curse synergy
                if (idLower.Contains("vajra") || idLower.Contains("girya"))
                    score += 20; // Strength for Osty scaling
                if (idLower.Contains("clock") || idLower.Contains("mummified"))
                    score += 15; // Scaling/sustain
                if (idLower.Contains("stone") || idLower.Contains("shuriken"))
                    score += 20; // Multi-attack scaling
                break;

            case "REGENT":
                // Regent loves: star generation, strength, card creation
                if (idLower.Contains("star") || idLower.Contains("celestial"))
                    score += 35; // Star relics
                if (idLower.Contains("vajra") || idLower.Contains("girya") ||
                    idLower.Contains("stone"))
                    score += 30; // Strength — Arsenal synergy
                if (idLower.Contains("dead") || idLower.Contains("branch"))
                    score += 25; // Card creation engine
                if (idLower.Contains("pocket") || idLower.Contains("violet"))
                    score += 15; // Energy for forge/card creation
                if (idLower.Contains("sundial") || idLower.Contains("flower"))
                    score += 15; // Energy/regen for setup turns
                break;
        }

        // ── OP.GG statistical weight ──────────────────────────────────
        // Relic win rate: higher WR = better relic
        // Scale: 35% WR → +75, 25% WR → +25, 15% WR → -25
        double wr = StatsDatabase.GetRelicWinRate(name);
        if (wr > 0)
        {
            score += (wr - 0.20) * 500;  // baseline 20% = 0; 35% → +75; 15% → -25
        }
        // Boss relic swap rating for boss relics
        double bossWr = StatsDatabase.GetBossRelicWinRate(name);
        if (bossWr > 0 && bossWr != 0.20)
        {
            // This is a boss relic — also incorporate its swap win rate
            score += (bossWr - 0.20) * 300;
        }

        return score;
    }

    private static string RelicLabel(NClickableControl clickable, int index)
    {
        try
        {
            // Try reflection to get relic name from model
            var type = clickable.GetType();
            var relicProp = type.GetProperty("Relic") ?? type.GetProperty("RelicModel");
            if (relicProp != null)
            {
                var relic = relicProp.GetValue(clickable);
                if (relic != null)
                {
                    var idProp = relic.GetType().GetProperty("Id");
                    if (idProp != null)
                    {
                        var idObj = idProp.GetValue(relic);
                        var entryProp = idObj?.GetType().GetProperty("Entry");
                        var entryVal = entryProp?.GetValue(idObj) as string;
                        if (!string.IsNullOrEmpty(entryVal)) return entryVal;
                    }
                }
            }
        }
        catch { }
        return clickable.Name ?? $"Relic#{index + 1}";
    }
}
