using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Events;
using MegaCrit.Sts2.Core.Nodes.Events.Custom.CrystalSphere;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.RestSite;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;
using MegaCrit.Sts2.Core.Runs;
using TokenSpire2.Core;

namespace TokenSpire2.Solver;

/// <summary>
/// Single entry point for ALL non-combat decisions.
/// Routes GameScreen to the appropriate decider heuristic.
/// NEVER uses random — every decision has a reason.
/// </summary>
public static class DecisionEngine
{
    private static readonly RunState _runState = new();

    // ── Card selection context tracking ────────────────────────────────────
    // When the solver plays a card that triggers a card selection screen
    // (Headbutt, True Grit, Armaments, etc.), we record the context here.
    // This lets ChooseCardDecider and SimpleSelectDecider make intelligent
    // choices instead of using generic "best card" scoring.
    //
    // Context values:
    //   "PUT_ON_TOP"  — Headbutt, Warcry: pick BEST card to draw next turn
    //   "EXHAUST"     — True Grit, Burning Pact, Recycle: pick WORST card
    //   "UPGRADE"     — Armaments: pick best upgrade candidate
    //   "RETRIEVE"    — Exhume, Hologram: pick BEST exhausted/discarded card
    //   "FETCH_SKILL" — Secret Technique: pick best Skill from draw pile
    //   "FETCH_ATTACK"— Secret Weapon: pick best Attack from draw pile
    //   ""            — Unknown context (default: use ScoreCard)

    public static string PendingCardSelectContext { get; set; } = "";
    public static string PendingCardSelectCardId { get; set; } = "";

    /// <summary>Called when the solver plays a card that triggers card selection.</summary>
    public static void SetPendingCardSelect(string cardId)
    {
        PendingCardSelectCardId = cardId;
        string upper = cardId.ToUpperInvariant();
        if (upper is "HEADBUTT" or "WARCRY")
            PendingCardSelectContext = "PUT_ON_TOP";
        else if (upper is "TRUE_GRIT" or "BURNING_PACT" or "RECYCLE")
            PendingCardSelectContext = "EXHAUST";
        else if (upper is "ARMAMENTS")
            PendingCardSelectContext = "UPGRADE";
        else if (upper is "EXHUME" or "HOLOGRAM")
            PendingCardSelectContext = "RETRIEVE";
        else if (upper is "SECRET_TECHNIQUE")
            PendingCardSelectContext = "FETCH_SKILL";
        else if (upper is "SECRET_WEAPON")
            PendingCardSelectContext = "FETCH_ATTACK";
        else
            PendingCardSelectContext = "";
        MainFile.Logger.Info($"[DecisionEngine] CardSelect context: {PendingCardSelectContext} (card={cardId})");
    }

    /// <summary>Clear the card select context after the selection screen is handled.</summary>
    public static void ClearPendingCardSelect()
    {
        PendingCardSelectContext = "";
        PendingCardSelectCardId = "";
    }

    /// <summary>Initialize logging and reset state for a new run.</summary>
    public static void OnNewRun()
    {
        DecisionLogger.NewRun();
        _runState.Refresh();
        MapDecider.Reset();
        ShopDecider.Reset();
        TreasureDecider.Reset();
        StateStabilityDetector.Reset();
        // Set character class for OP.GG stats lookup
        string ch = _runState.Character?.ToLower() ?? "ironclad";
        StatsDatabase.CurrentClass = ch switch
        {
            "silent" => "silent",
            "defect" => "defect",
            "necrobinder" => "necrobinder",
            "regent" => "regent",
            _ => "ironclad",
        };
        StatsDatabase.EnsureLoaded();
    }

    /// <summary>
    /// Make a decision for the given screen. Returns true if a decision was made
    /// (vs. the screen being in a mechanical state like "waiting for preview").
    ///
    /// Delta time is used by StateStabilityDetector to ensure game state is
    /// stable before making decisions (preventing premature actions during
    /// transitions — key CommunicationMod pattern).
    /// </summary>
    public static bool Decide(GameScreen screen, double delta = 0.0)
    {
        // ── Stability gate (CommunicationMod pattern) ──────────────────
        // Only allow decisions when game state is stable.
        // During transitions, animations, or pending actions, skip.
        if (!StateStabilityDetector.IsStableForDecision(screen, delta))
        {
            // Still refresh run state to track changes
            try { _runState.Refresh(); } catch { }
            return false; // not stable yet, wait
        }

        try
        {
            _runState.Refresh();
        }
        catch (Exception ex)
        {
            MainFile.Logger.Error($"[DecisionEngine] RunState.Refresh() threw: {ex.Message}\n{ex.StackTrace}");
            // Continue with stale state — better than crashing
        }

        try
        {
            switch (screen)
            {
                case GameScreen.MAP:
                    return MapDecider.Decide(_runState);

                case GameScreen.EVENT:
                    return EventDecider.Decide(_runState);

                case GameScreen.REST:
                    return RestDecider.Decide(_runState);

                case GameScreen.SHOP:
                    return ShopDecider.Decide(_runState);

                case GameScreen.TREASURE:
                    TreasureDecider.Decide();
                    return true;

                case GameScreen.OVERLAY_CARD_REWARD:
                    return CardRewardDecider.Decide(_runState);

                case GameScreen.OVERLAY_CHOOSE_CARD:
                    return ChooseCardDecider.Decide(_runState);

                case GameScreen.OVERLAY_CHOOSE_BUNDLE:
                    BundleDecider.Decide();
                    return true;

                case GameScreen.OVERLAY_CHOOSE_RELIC:
                    return RelicDecider.Decide(_runState);

                case GameScreen.OVERLAY_DECK_GRID:
                    return CardGridDecider.Decide(_runState);

                case GameScreen.OVERLAY_SIMPLE_SELECT:
                    return SimpleSelectDecider.Decide(_runState);

                case GameScreen.OVERLAY_CRYSTAL_SPHERE:
                    CrystalSphereDecider.Decide();
                    return true;

                default:
                    return false;
            }
        }
        catch (Exception ex)
        {
            MainFile.Logger.Error($"[DecisionEngine] CRASH in {screen}: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            return false;
        }
    }
}
