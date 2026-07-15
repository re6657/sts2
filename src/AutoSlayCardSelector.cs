using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TokenSpire2.Llm;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.TestSupport;

namespace TokenSpire2;

public class AutoSlayCardSelector : ICardSelector
{
    private readonly System.Random? _rng;
    private readonly LlmClient? _llm;
    public bool IsPendingLlm { get; private set; }

    /// <summary>
    /// Premium starter cards that should NEVER be removed or transformed.
    /// These define each character's core identity and are among the best
    /// cards in the game when upgraded/enchanted.
    /// </summary>
    private static readonly HashSet<string> PremiumStarters = new(StringComparer.OrdinalIgnoreCase)
    {
        "BASH",           // Ironclad: 2-cost, 8 dmg + 2 Vulnerable — premium debuff
        "NEUTRALIZE",     // Silent: 0-cost, 3 dmg + 1 Weak — free debuff
        "SURVIVOR",       // Silent: 1-cost, 8 block + discard 1 — deck manipulation
        "ZAP",            // Defect: 0-cost, Channel 1 Lightning — orb engine starter
        "DUALCAST",       // Defect: 0-cost, Evoke 1 orb — burst damage scaling
    };

    public AutoSlayCardSelector(System.Random? rng = null, LlmClient? llm = null)
    {
        _rng = rng;
        _llm = llm;
    }

    public async Task<IEnumerable<CardModel>> GetSelectedCards(IEnumerable<CardModel> options, int minSelect, int maxSelect)
    {
        IsPendingLlm = true;
        try { return await GetSelectedCardsInner(options, minSelect, maxSelect); }
        finally { IsPendingLlm = false; }
    }

    private async Task<IEnumerable<CardModel>> GetSelectedCardsInner(IEnumerable<CardModel> options, int minSelect, int maxSelect)
    {
        var list = options.ToList();
        if (list.Count == 0)
            return Array.Empty<CardModel>();

        int count = Math.Min(maxSelect, list.Count);
        if (count < minSelect)
            count = Math.Min(minSelect, list.Count);

        if (_llm != null && list.Count > 1)
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine(Llm.PromptStrings.Get("SelectCards", count));
                bool inCombat = NCombatRoom.Instance != null;
                sb.AppendLine(inCombat
                    ? Llm.PromptStrings.Get("SelectCardsIntro", count)
                    : Llm.PromptStrings.Get("SelectCardsNonCombat", count));
                for (int i = 0; i < list.Count; i++)
                {
                    var card = list[i];
                    var cost = card.EnergyCost.CostsX ? "X" : card.EnergyCost.Canonical.ToString();
                    var desc = Llm.GameStateSerializer.SafeGetCardDescription(card);
                    sb.AppendLine($"  [{i + 1}] {card.Id.Entry} ({card.Type}, {cost} {Llm.PromptStrings.Get("Energy")}) — {desc}");
                }
                sb.AppendLine();
                sb.AppendLine(Llm.PromptStrings.Get("ReplyChooseCount", count));

                MainFile.Logger.Info($"[AutoSlay/LLM] Asking LLM for card selection ({list.Count} options, pick {count})");
                var response = await _llm.SendAsync(sb.ToString());

                var llmChosen = ParseChoices(response, list.Count, count);
                if (llmChosen.Count > 0)
                {
                    var result = llmChosen.Select(idx => list[idx - 1]).ToList();
                    MainFile.Logger.Info($"[AutoSlay/LLM] Card selection: {string.Join(", ", result.Select(c => c.Id.Entry))}");
                    return result;
                }
            }
            catch (Exception ex)
            {
                MainFile.Logger.Info($"[AutoSlay/LLM] Card selection failed: {ex.Message}, falling back to random");
            }
        }

        // Fallback: context-aware heuristic-based selection.
        // The context matters critically — exhaust wants the WORST cards,
        // upgrade wants the BEST upgrade candidates, put-on-top wants the BEST cards.
        string context = "";
        try { context = Solver.DecisionEngine.GetPendingCardSelectContext(); } catch { }

        var sorted = list
            .OrderByDescending(c =>
            {
                try
                {
                    string cardId = c.Id.Entry?.ToUpperInvariant() ?? "";
                    int cost = c.EnergyCost.CostsX ? 3 : Math.Min(c.EnergyCost.Canonical, 5);
                    int upgraded = c.IsUpgraded ? 2 : 0;
                    // ── Correct basic card detection ─────────────────
                    // MUST use StartsWith not Contains — "STRIKE" in "POMMEL_STRIKE"
                    // is a false positive.  Also check for multi-word IDs like
                    // "STRIKE_IRONCLAD" and "DEFEND_IRONCLAD".
                    bool isBasicStrike = cardId == "STRIKE" || cardId.StartsWith("STRIKE_");
                    bool isBasicDefend = cardId == "DEFEND" || cardId.StartsWith("DEFEND_");
                    bool isCurse = c.Type == CardType.Curse;
                    bool isStatus = c.Type == CardType.Status;
                    bool isPower = c.Type == CardType.Power;
                    bool isPremium = PremiumStarters.Contains(cardId);

                    switch (context)
                    {
                        case "REMOVE":
                        case "TRANSFORM":
                            // Pick the WORST cards: Curse > Status > Strike > Defend > low-cost
                            if (isCurse) return 500;
                            if (isStatus) return 400;
                            if (isBasicStrike) return 300;
                            if (isBasicDefend) return 250;
                            // Premium starters: NEVER remove/transform
                            if (isPremium) return -500;
                            // For non-basic cards, prefer low-value
                            return 100 - cost * 10 - upgraded * 30;

                        case "EXHAUST":
                            // Pick the WORST cards to exhaust: Curse > Status > Strike > Defend > low-cost
                            if (isCurse) return 500;
                            if (isStatus) return 400;
                            if (isBasicStrike) return 300;
                            if (isBasicDefend) return 250;
                            // Premium starters: don't exhaust (they're valuable)
                            if (isPremium) return -200;
                            return 100 - cost * 10 - upgraded * 20;

                        case "UPGRADE":
                            // Pick the BEST upgrade candidate: unupgraded, high-impact
                            if (isCurse || isStatus) return -500;
                            if (c.IsUpgraded) return -200; // Already upgraded, skip
                            if (isBasicStrike) return -200; // NEVER upgrade basic Strike
                            if (isBasicDefend) return -200; // NEVER upgrade basic Defend
                            // Premium starters are EXCELLENT upgrade targets
                            if (isPremium) return 350 + cost * 5;
                            if (isPower) return 200 + cost * 5;
                            // Prefer high-cost cards (bigger upgrade impact)
                            return cost * 15 + upgraded * 10;

                        case "PUT_ON_TOP":
                        case "RETRIEVE":
                        case "FETCH_SKILL":
                        case "FETCH_ATTACK":
                            // Pick the BEST card to draw/retrieve
                            if (isCurse || isStatus) return -400;
                            if (isPremium) return 300 + cost * 5;
                            if (isPower) return 150 + cost * 5;
                            return cost * 15 + upgraded * 30;

                        default:
                            // ── UNKNOWN context: BE CONSERVATIVE ────────────
                            // Without knowing whether this is a "pick best" or
                            // "pick worst" operation, protect premium cards and
                            // prefer expendable basics. This is the SAFE default.
                            if (isCurse) return 100;  // Always fine to pick
                            if (isStatus) return 90;
                            if (isBasicStrike) return 80; // Expendable
                            if (isBasicDefend) return 70; // Expendable
                            // Premium starters: NEVER pick in unknown context
                            // (could be removal — safer to avoid)
                            if (isPremium) return -500;
                            // Non-basic, non-premium: prefer lower cost cards
                            // (less commitment if this is removal, less value
                            //  lost if it's transform)
                            return 50 - cost * 3;
                    }
                }
                catch { return 0; }
            })
            .ToList();

        var chosen = sorted.Take(count).ToList();
        MainFile.Logger.Info($"[AutoSlay] Context-aware card selection (ctx={context}): {count}/{list.Count}: {string.Join(", ", chosen.Select(c => c.Id.Entry))}");
        return chosen;
    }

    private static List<int> ParseChoices(string response, int max, int needed)
    {
        var choices = new List<int>();
        foreach (var line in response.Split('\n'))
        {
            var trimmed = line.Trim().ToUpper();
            if (trimmed.StartsWith("CHOOSE") && trimmed.Length > 6)
            {
                var after = trimmed.Substring(6).Trim();
                // M28: support comma-separated "1, 2, 3" as well as single "5"
                foreach (var part in after.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (int.TryParse(part.Trim(), out int idx) && idx >= 1 && idx <= max)
                        choices.Add(idx);
                }
            }
        }
        if (choices.Count >= needed)
            return choices.Take(needed).ToList();
        return choices;
    }

    public CardRewardSelection GetSelectedCardReward(IReadOnlyList<CardCreationResult> options, IReadOnlyList<CardRewardAlternative> alternatives)
    {
        if (options.Count == 0)
        {
            // M27: default struct has null .card — log warning, return first alternative (likely SKIP)
            MainFile.Logger.Warn("[AutoSlay] GetSelectedCardReward called with 0 card options");
            if (alternatives.Count > 0)
            {
                MainFile.Logger.Info("[AutoSlay] Selecting first alternative (0 card options)");
                return new CardRewardSelection { card = null, alternative = alternatives[0] };
            }
            return default;
        }

        // ── Determistic selection via Tiebreaker (FNV-1a based) ──────────
        // Instead of _rng.Next(), score each card by a basic heuristic
        // and use Tiebreaker for deterministic tiebreaking. This ensures
        // multiplayer lockstep — all instances pick the same card.
        CardCreationResult pick;
        try
        {
            pick = Solver.Tiebreaker.PickBest(options, o =>
            {
                try
                {
                    var card = o.Card;
                    if (card == null) return 0;
                    string id = card.Id?.Entry?.ToUpperInvariant() ?? "";
                    int cost = card.EnergyCost?.CostsX == true ? 2 : (card.EnergyCost?.Canonical ?? 1);
                    int rarityBonus = card.Rarity switch
                    {
                        MegaCrit.Sts2.Core.Entities.Cards.CardRarity.Rare => 30,
                        MegaCrit.Sts2.Core.Entities.Cards.CardRarity.Uncommon => 15,
                        _ => 0,
                    };
                    bool isUpgraded = card.IsUpgraded;
                    // Premium starter detection (same as GetSelectedCardsInner)
                    bool isPremium = PremiumStarters.Contains(id);
                    // Prefer: rarity > premium > upgraded > higher cost
                    return rarityBonus + (isPremium ? 20 : 0) + (isUpgraded ? 10 : 0) + cost;
                }
                catch { return 0; }
            });
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[AutoSlay] Tiebreaker.PickBest failed: {ex.Message}, falling back to first option");
            pick = options[0];
        }
        return new CardRewardSelection { card = pick.Card, alternative = null };
    }
}
