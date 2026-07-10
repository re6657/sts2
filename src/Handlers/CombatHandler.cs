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

    public static void BoostHpIfNeeded()
    {
        // DISABLED — normal HP for real runs
        _hpBoosted = true;
        return;
        var runState = RunManager.Instance?.DebugOnlyGetState();
        var player = runState != null ? LocalContext.GetMe(runState) : null;
        if (player?.Creature == null) return;

        player.Creature.SetMaxHpInternal(9999);
        player.Creature.SetCurrentHpInternal(9999);
        MainFile.Logger.Info("[AutoSlay] Boosted player HP to 9999.");
        _hpBoosted = true;
    }

    public static void UsePotionsIfNeeded(System.Random rng)
    {
        if (_potionsUsed) return;
        var cm = CombatManager.Instance;
        if (cm == null || !cm.IsInProgress || cm.PlayerActionsDisabled) return;

        var runState = RunManager.Instance?.DebugOnlyGetState();
        var player = runState != null ? LocalContext.GetMe(runState) : null;
        if (player == null) return;

        var potions = player.Potions.ToList();
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

        var playable = PileType.Hand.GetPile(player).Cards
            .Where(c => !_attemptedCards.Contains(c) && c.CanPlay(out _, out _))
            .ToList();

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
            var enemies = card.CombatState?.HittableEnemies.ToList() ?? new List<Creature>();
            if (enemies.Count > 0) target = enemies[rng.Next(enemies.Count)];
        }

        // ── Co-op diagnostic ──────────────────────────────────────────
        if (Coop.CoopManager.IsCoopMode)
        {
            try
            {
                var rs = RunManager.Instance?.DebugOnlyGetState();
                var pl = rs != null ? LocalContext.GetMe(rs) : null;
                int preEnergy = pl?.PlayerCombatState?.Energy ?? -1;
                MainFile.Logger.Info($"[CombatHandler/COOP] Pre-play: card={card.Id.Entry} energy={preEnergy}");
            }
            catch { }
        }
        card.TryManualPlay(target);
        if (Coop.CoopManager.IsCoopMode)
        {
            try
            {
                var rs = RunManager.Instance?.DebugOnlyGetState();
                var pl = rs != null ? LocalContext.GetMe(rs) : null;
                int postEnergy = pl?.PlayerCombatState?.Energy ?? -1;
                MainFile.Logger.Info($"[CombatHandler/COOP] Post-play: card={card.Id.Entry} energy={postEnergy}");
            }
            catch { }
        }
        return 0.4;
    }

    /// <summary>
    /// End turn via UI button in co-op mode (network-synced), or direct API in single-player.
    /// CRITICAL: In co-op mode, NEVER falls back to local-only PlayerCmd.EndTurn to prevent state divergence.
    /// </summary>
    private static void EndTurnViaUiOrApi(Player player)
    {
        if (TokenSpire2.Coop.CoopManager.IsCoopMode)
        {
            try
            {
                var handler = new TokenSpire2.Multiplayer.MpScreenHandler();
                if (handler.ClickEndTurnButton())
                {
                    MainFile.Logger.Info("[CombatHandler] End turn via UI button click (network-synced)");
                    return;
                }

                // Retry once after a short delay
                System.Threading.Thread.Sleep(100);
                if (handler.ClickEndTurnButton())
                {
                    MainFile.Logger.Info("[CombatHandler] End turn via UI button click (retry succeeded)");
                    return;
                }

                MainFile.Logger.Error(
                    "[CombatHandler] CRITICAL: UI EndTurn button not found in co-op mode. " +
                    "NOT falling back to local-only PlayerCmd.EndTurn to prevent desync!");
                return;
            }
            catch (Exception ex)
            {
                MainFile.Logger.Error(
                    $"[CombatHandler] CRITICAL: UI EndTurn error in co-op mode: {ex.Message}. " +
                    "NOT falling back to prevent desync!");
                return;
            }
        }

        // Single-player: direct API call (safe — no other instance to desync with)
        PlayerCmd.EndTurn(player, canBackOut: false);
    }
}
