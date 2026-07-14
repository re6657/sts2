using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

namespace TokenSpire2.Handlers;

public static class CombatHandler
{
    private static bool _hpBoosted;
    private static bool _potionsUsed;
    private static readonly HashSet<CardModel> _attemptedCards = new();

    public static void OnCombatEnded()
    {
        _hpBoosted = false;
        _potionsUsed = false;
        _attemptedCards.Clear();
        TreasureRoomHandler.Reset(); // prevent stale chest state across runs
    }

    public static void OnNonPlayPhase()
    {
        _attemptedCards.Clear();
    }

    /// <summary>HP boosting is disabled — set to 9999 via params if needed.</summary>
    public static void BoostHpIfNeeded()
    {
        _hpBoosted = true; // L13: removed dead code; HP boost disabled for normal runs
    }

    public static void UsePotionsIfNeeded(System.Random rng)
    {
        if (_potionsUsed) return;
        var cm = CombatManager.Instance;
        if (cm == null || !cm.IsInProgress || cm.PlayerActionsDisabled) return;

        var runState = RunManager.Instance?.DebugOnlyGetState();
        var player = runState != null ? LocalContext.GetMe(runState) : null;
        if (player == null) return;

        var potions = player.Potions?.ToList() ?? new List<PotionModel>();
        if (potions.Count == 0) { _potionsUsed = true; return; }

        var combatState = player.Creature.CombatState;

        foreach (var potion in potions)
        {
            if (cm.PlayerActionsDisabled || !cm.IsInProgress) break;

            Creature? target = PotionHelper.GetTarget(potion, combatState as CombatState, rng);
            if (target == null && potion.TargetType.IsSingleTarget())
            {
                MainFile.Logger.Info($"[AutoSlay] Skipping potion {potion.Id.Entry}: no valid target");
                continue;
            }

            MainFile.Logger.Info($"[AutoSlay] Using potion: {potion.Id.Entry}");
            potion.EnqueueManualUse(target);
        }
        _potionsUsed = true;
    }

    public static double PlayOneCard(CombatManager cm, System.Random rng)
    {
        var runState = RunManager.Instance?.DebugOnlyGetState();
        var player = runState != null ? LocalContext.GetMe(runState) : null;
        if (player == null) return 0;

        var pile = PileType.Hand.GetPile(player);
        var playable = pile?.Cards
            ?.Where(c => !_attemptedCards.Contains(c) && c.CanPlay(out _, out _))
            .ToList() ?? new List<CardModel>();

        if (playable.Count == 0)
        {
            if (!cm.PlayerActionsDisabled && cm.IsInProgress)
            {
                _attemptedCards.Clear();
                EndTurnViaUiOrApi(player);
                return 0.5;
            }
            return 0;
        }

        var card = playable[rng.Next(playable.Count)];
        _attemptedCards.Add(card);

        Creature? target = null;
        if (card.TargetType == TargetType.AnyEnemy)
        {
            var combatState = player.Creature?.CombatState;
            var enemies = combatState?.HittableEnemies.ToList() ?? new List<Creature>();
            if (enemies.Count > 0) target = enemies[rng.Next(enemies.Count)];
        }
        else if (card.TargetType == TargetType.AnyAlly || card.TargetType == TargetType.AnyPlayer)
        {
            // Multiplayer player-targeting cards (DEMONIC_SHIELD, BELIEVE_IN_YOU, etc.)
            // Target the first alive other player, or self as fallback.
            var combatState = player.Creature?.CombatState as CombatState;
            if (combatState != null && combatState.PlayerCreatures.Count > 0)
            {
                target = combatState.PlayerCreatures
                    .FirstOrDefault(c => c.IsAlive && c != player.Creature)
                    ?? combatState.PlayerCreatures.FirstOrDefault(c => c.IsAlive);
            }
        }

        card.TryManualPlay(target);
        return 0.4;
    }

    /// <summary>
    /// End turn via direct API call (single-player safe).
    /// </summary>
    private static void EndTurnViaUiOrApi(Player player)
    {
        PlayerCmd.EndTurn(player, canBackOut: false);
    }
}
