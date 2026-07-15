using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;

using TokenSpire2.Core;

namespace TokenSpire2.Solver;

/// <summary>
/// Strategic shop purchasing.
/// Priority: Card Removal > Relic > Card > Potion
/// Buffer: keep at least 50 gold after purchase.
/// Leaves when nothing good remains or gold is too low.
/// </summary>
public static class ShopDecider
{
    private static bool _shopStarted;
    private static bool _shopLeaving;
    private static int _lastKnownGold = -1; // B17: track gold to confirm purchases
    private static string? _lastPurchaseItemId; // B17: track failed purchase to skip retry

    public static void Reset()
    {
        _shopStarted = false;
        _shopLeaving = false;
        _lastKnownGold = -1;
        _lastPurchaseItemId = null;
    }

    public static bool Decide(RunState state)
    {
        var shopRoom = GetShopRoom();
        if (shopRoom == null)
        {
            Reset();
            return false;
        }

        // Leaving state: click proceed when available
        if (_shopLeaving)
        {
            if (shopRoom.ProceedButton?.IsEnabled == true)
            {
                MainFile.Logger.Info("[ShopDecider] Leaving shop");
                shopRoom.ProceedButton.ForceClick();
                Reset();
                return true;
            }
            // Close inventory
            AutoSlayHelpers.FindFirst<NBackButton>(shopRoom)?.ForceClick();
            return true;
        }

        // Open inventory
        if (!_shopStarted)
        {
            shopRoom.OpenInventory();
            _shopStarted = true;
            return true;
        }

        // Execute one purchase per call (cooperative multi-tick)
        ExecuteBestPurchase(shopRoom, state);
        return true;
    }

    private static void ExecuteBestPurchase(NMerchantRoom room, RunState state)
    {
        var inv = room.Inventory?.Inventory;
        if (inv == null)
        {
            LeaveShop();
            return;
        }

        // B17: Verify previous purchase succeeded
        if (_lastKnownGold >= 0 && _lastPurchaseItemId != null)
        {
            // M8: use > instead of >= — only flag failure if gold strictly increased
            // (>= would falsely flag zero-cost purchases or simultaneous gold gains)
            if (state.Gold > _lastKnownGold)
            {
                MainFile.Logger.Warn($"[ShopDecider] B17: Purchase of '{_lastPurchaseItemId}' may have FAILED — gold unchanged at {state.Gold}");
                // Purchase was rejected — the item will naturally be excluded
                // on re-evaluation if it's no longer affordable or stocked
            }
            else
            {
                MainFile.Logger.Info($"[ShopDecider] B17: Purchase confirmed — gold {_lastKnownGold} → {state.Gold} (spent {_lastKnownGold - state.Gold})");
            }
            _lastKnownGold = -1;
            _lastPurchaseItemId = null;
        }

        // Build item lists
        var relics = new List<(MerchantEntry entry, string label, double score)>();
        var potions = new List<(MerchantEntry entry, string label, double score)>();
        MerchantEntry? cardRemoval = inv.CardRemovalEntry;

        foreach (var e in inv.RelicEntries)
        {
            if (e.IsStocked && e.EnoughGold)
            {
                double score = ScoreRelicPurchase(e, state);
                string label = $"Relic: {GetItemId(e) ?? e.GetType().Name} ({e.Cost}g)";
                relics.Add((e, label, score));
            }
        }
        // ── Cards are NEVER purchased per user requirement ──
        // Card purchasing is completely disabled — we only remove basics, buy relics, and buy potions.
        foreach (var e in inv.PotionEntries)
        {
            if (e.IsStocked && e.EnoughGold && state.OpenPotionSlots > 0)
            {
                double score = 20; // potions are lowest priority
                string label = $"Potion: {GetItemId(e) ?? e.GetType().Name} ({e.Cost}g)";
                potions.Add((e, label, score));
            }
        }

        // Check card removal — prioritize when deck has Strike/Defend basics
        // Defend prioritized over Strike for removal (matching CardGridDecider)
        var sp = SolverParams.Instance.Shop;
        int basicCount = state.CountBasicStrikes + state.CountBasicDefends;
        double removeScore;
        bool canRemove = cardRemoval?.IsStocked == true && cardRemoval.EnoughGold
            && state.TotalCardCount > sp.RemoveMinDeckSize;

        if (basicCount > 0)
        {
            // Massive priority boost: always remove Strike/Defend first
            removeScore = 500 + basicCount * 20;
            // Allow removal even in smaller decks when basics exist
            if (!canRemove && cardRemoval?.IsStocked == true && cardRemoval.EnoughGold
                && state.TotalCardCount > 5)
                canRemove = true;
            MainFile.Logger.Info($"[ShopDecider] Removal priority boosted: {basicCount} basic cards (score={removeScore:F0})");
        }
        else
        {
            removeScore = state.IsDeckLarge ? sp.RemoveCardLargeDeckScore : sp.RemoveCardNormalScore;
        }

        var allOptions = new List<(MerchantEntry entry, string label, double score, int priority)>();

        if (canRemove && cardRemoval != null)
            allOptions.Add((cardRemoval, $"REMOVE Strike/Defend ({cardRemoval.Cost}g)", removeScore, 0));

        foreach (var r in relics)
            allOptions.Add((r.entry, r.label, r.score, 1));
        // NOTE: Cards are never purchased — priority 2 skipped entirely
        foreach (var p in potions)
            allOptions.Add((p.entry, p.label, p.score, 2));

        if (allOptions.Count == 0)
        {
            MainFile.Logger.Info("[ShopDecider] No good purchases, leaving shop");
            LeaveShop();
            return;
        }

        // Sort by priority first, then score within same priority.
        // Ties broken by original list order (deterministic — no Random for multiplayer lockstep).
        var sortedOptions = allOptions
            .OrderBy(x => x.priority)
            .ThenByDescending(x => x.score)
            .ToList();

        int minReserve = SolverParams.Instance.Shop.MinGoldReserve;
        double minScore = SolverParams.Instance.Shop.MinScoreToBuyLowGold;

        // Find the best purchaseable item — skip items that would drain gold too much
        (MerchantEntry entry, string label, double score, int priority)? best = null;
        int bestGoldAfter = 0;

        foreach (var opt in sortedOptions)
        {
            int cost = opt.entry.Cost;
            int goldAfter = state.Gold - cost;
            if (goldAfter < minReserve && opt.score < minScore)
            {
                MainFile.Logger.Info($"[ShopDecider] Skipping {opt.label} (would leave only {goldAfter}g, score={opt.score:F0} < {minScore})");
                continue; // try next best option instead of leaving shop
            }
            best = opt;
            bestGoldAfter = goldAfter;
            break;
        }

        if (best == null)
        {
            MainFile.Logger.Info("[ShopDecider] No affordable purchases, leaving shop");
            LeaveShop();
            return;
        }

        string reason = $"Buy {best.Value.label} score={best.Value.score:F0} goldLeft={bestGoldAfter}";
        DecisionLogger.LogDecision(
            GameScreen.SHOP, "ShopPurchase",
            allOptions.Select((o, i) => new DecisionLogger.OptionScore
            {
                Index = i, Label = o.label, Score = o.score
            }).ToList(),
            0, best.Value.label, reason);

        MainFile.Logger.Info($"[ShopDecider] {reason}");
        // B17: Track gold before purchase for confirmation
        int goldBefore = state.Gold;
        string itemId = GetItemId(best.Value.entry) ?? best.Value.label;
        TaskHelper.RunSafely(best.Value.entry.OnTryPurchaseWrapper(inv));

        // B17: Verify purchase succeeded — wait one tick for gold to update
        // Gold is updated asynchronously; check on next Decide() call
        _lastKnownGold = goldBefore;
        _lastPurchaseItemId = itemId;

        // After purchase, re-evaluate next tick
    }

    private static double ScoreRelicPurchase(MerchantEntry entry, RunState state)
    {
        var sp = SolverParams.Instance.Shop;
        double score = sp.BaseRelicValue;
        string typeName = entry.GetType().Name.ToLower();
        string? itemId = GetItemId(entry);

        if (typeName.Contains("prison") || typeName.Contains("sozu") || typeName.Contains("coffee") ||
            typeName.Contains("dripper") || typeName.Contains("ectoplasm") || typeName.Contains("key") ||
            typeName.Contains("philosopher"))
            score += sp.EnergyRelicBonus;

        if (typeName.Contains("vajra") || typeName.Contains("girya") || typeName.Contains("duvu") ||
            typeName.Contains("shuriken"))
            score += sp.StrengthRelicBonus;

        if (typeName.Contains("anchor") || typeName.Contains("horn") || typeName.Contains("cleat") ||
            typeName.Contains("boat") || typeName.Contains("thread") || typeName.Contains("needle"))
            score += sp.DefenseRelicBonus;

        double wr = StatsDatabase.GetRelicWinRate(itemId);
        if (wr > 0) score += (wr - 0.20) * sp.RelicWrMultiplier;
        double bossWr = StatsDatabase.GetBossRelicWinRate(itemId);
        if (bossWr > 0 && bossWr != 0.20) score += (bossWr - 0.20) * sp.RelicBossWrMultiplier;

        int cost = entry.Cost;
        if (cost > 200) score += sp.RelicCostHighPenalty;
        if (cost < 100) score += sp.RelicCostLowBonus;
        return score;
    }

    private static void LeaveShop()
    {
        _shopLeaving = true;
        MainFile.Logger.Info("[ShopDecider] Setting shop to leave state");
    }

    /// <summary>Extract item ID from a merchant entry via reflection.</summary>
    private static string? GetItemId(MerchantEntry entry)
    {
        try
        {
            var t = entry.GetType();
            // Try common property names for the underlying item
            foreach (var propName in new[] { "Card", "CardModel", "Relic", "RelicModel",
                "Potion", "PotionModel", "Item", "ItemModel", "Model" })
            {
                var prop = t.GetProperty(propName);
                if (prop == null) continue;
                var val = prop.GetValue(entry);
                if (val == null) continue;

                // Try to get Id.Entry
                var idProp = val.GetType().GetProperty("Id");
                if (idProp != null)
                {
                    var id = idProp.GetValue(val);
                    var entryProp = id?.GetType().GetProperty("Entry");
                    if (entryProp != null)
                    {
                        var entryVal = entryProp.GetValue(id) as string;
                        if (!string.IsNullOrEmpty(entryVal)) return entryVal;
                    }
                    if (id is string idStr && !string.IsNullOrEmpty(idStr)) return idStr;
                }

                // Fallback: use ToString on the item
                var itemStr = val.ToString();
                if (!string.IsNullOrEmpty(itemStr) && !itemStr.Contains("Merchant"))
                    return itemStr.Length > 40 ? itemStr[..40] : itemStr;
            }
        }
        catch { }
        return null;
    }

    private static NMerchantRoom? GetShopRoom()
    {
        try
        {
            var root = ((SceneTree)Engine.GetMainLoop()).Root;
            return root.GetNodeOrNull<NMerchantRoom>(
                "Game/RootSceneContainer/Run/RoomContainer/MerchantRoom");
        }
        catch { return null; }
    }
}
