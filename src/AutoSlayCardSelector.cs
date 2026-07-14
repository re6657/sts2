using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TokenSpire2.Llm;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.TestSupport;

namespace TokenSpire2;

public class AutoSlayCardSelector : ICardSelector
{
    private readonly System.Random _rng;
    private readonly LlmClient? _llm;
    private readonly bool _multiplayerMode;
    public bool IsPendingLlm { get; private set; }

    /// <summary>
    /// Reserved for future per-player manual mode control. Currently unused —
    /// ICardSelector always auto-picks valid cards for ALL players because
    /// returning empty from GetSelectedCards would bypass the manual UI
    /// (CardSelectCmd skips UI when a Selector is active) and cause
    /// StateDivergence in lockstep multiplayer.
    ///
    /// The host's manual mode is enforced at higher-level decision guards
    /// (RestDecider, CombatHandler, EventDecider, ShopDecider, MapDecider),
    /// NOT at the engine-level ICardSelector.
    /// </summary>
    public static HashSet<ulong> ManualPlayerNetIds { get; } = new HashSet<ulong>();

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

    public AutoSlayCardSelector(System.Random rng, LlmClient? llm = null, bool multiplayerMode = false)
    {
        _rng = rng;
        _llm = llm;
        _multiplayerMode = multiplayerMode;
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

        // ── Screen-type fallback for context detection ──────────────────
        // ICardSelector is called by the game engine during screen setup,
        // BEFORE CardGridDecider.Decide() runs. If the decider hasn't set
        // the context yet, infer it from the active overlay screen type.
        if (string.IsNullOrEmpty(context))
        {
            try
            {
                var overlay = NOverlayStack.Instance?.Peek();
                if (overlay != null)
                {
                    string typeName = overlay.GetType().Name;
                    if (typeName.Contains("Upgrade") || typeName.Contains("Enchant"))
                        context = "UPGRADE";
                    else if (typeName.Contains("Transform"))
                        context = "TRANSFORM";
                    else if (typeName.Contains("Remove") || typeName.Contains("CardSelect"))
                        context = "REMOVE";
                }
            }
            catch { }
        }

        var scored = list
            .Select(c =>
            {
                try
                {
                    string cardId = c.Id.Entry?.ToUpperInvariant() ?? "";
                    int cost = c.EnergyCost.CostsX ? 3 : Math.Min(c.EnergyCost.Canonical, 5);
                    int upgraded = c.IsUpgraded ? 2 : 0;
                    // ── Correct basic card detection ─────────────────
                    bool isBasicStrike = cardId == "STRIKE" || cardId.StartsWith("STRIKE_");
                    bool isBasicDefend = cardId == "DEFEND" || cardId.StartsWith("DEFEND_");
                    bool isCurse = c.Type == CardType.Curse;
                    bool isStatus = c.Type == CardType.Status;
                    bool isPower = c.Type == CardType.Power;
                    bool isPremium = PremiumStarters.Contains(cardId);

                    double score = context switch
                    {
                        "REMOVE" or "TRANSFORM" =>
                            isCurse ? 500 : isStatus ? 400 : isBasicStrike ? 300 :
                            isBasicDefend ? 250 : isPremium ? -500 : 100 - cost * 10 - upgraded * 30,

                        "EXHAUST" =>
                            isCurse ? 500 : isStatus ? 400 : isBasicStrike ? 300 :
                            isBasicDefend ? 250 : isPremium ? -200 : 100 - cost * 10 - upgraded * 20,

                        "UPGRADE" =>
                            isCurse || isStatus ? -500 : c.IsUpgraded ? -200 :
                            isBasicStrike ? -200 : isBasicDefend ? -200 :
                            isPremium ? 350 + cost * 5 : isPower ? 200 + cost * 5 :
                            cost * 15 + upgraded * 10,

                        "PUT_ON_TOP" or "RETRIEVE" or "FETCH_SKILL" or "FETCH_ATTACK" =>
                            isCurse || isStatus ? -400 : isPremium ? 300 + cost * 5 :
                            isPower ? 150 + cost * 5 : cost * 15 + upgraded * 30,

                        _ =>
                            isCurse || isStatus ? -50 : isBasicStrike ? -200 :
                            isBasicDefend ? -200 : isPremium ? -500 :
                            cost * 5 + upgraded * 10,
                    };
                    return (card: c, score);
                }
                catch { return (card: c, score: 0.0); }
            })
            .ToList();

        // ── Multiplayer deterministic tiebreaking ────────────────────────
        // When scores are equal, OrderByDescending preserves input order
        // (stable sort). But input order may differ across instances if a
        // prior non-deterministic action changed the game state.  Use FNV-1a
        // hash of each card's identity as a secondary sort key to guarantee
        // identical selection order across all lockstep instances.
        var sorted = _multiplayerMode
            ? scored
                .OrderByDescending(x => x.score)
                .ThenBy(x => HashCardIdentity(x.card))
                .Select(x => x.card)
                .ToList()
            : scored
                .OrderByDescending(x => x.score)
                .Select(x => x.card)
                .ToList();

        var chosen = sorted.Take(count).ToList();
        MainFile.Logger.Info($"[AutoSlay] Context-aware card selection (ctx={context}): {count}/{list.Count}: {string.Join(", ", chosen.Select(c => c.Id.Entry))}");
        return chosen;
    }

    /// <summary>
    /// FNV-1a hash of a card's identity string for deterministic secondary sort
    /// in multiplayer mode. When two cards have the same score in GetSelectedCardsInner,
    /// this ensures all lockstep instances pick them in the same order — preventing
    /// the StateDivergence that occurs when OrderByDescending's stable sort produces
    /// different results from differently-ordered input lists.
    /// </summary>
    private static uint HashCardIdentity(CardModel card)
    {
        uint hash = 2166136261;
        string s = card?.ToString() ?? "";
        foreach (char c in s)
            hash = unchecked((hash ^ c) * 16777619);
        return hash;
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

        int pickIndex;
        if (_multiplayerMode)
        {
            // MULTIPLAYER: use FNV-1a hash of ALL card option IDs to pick
            // deterministically. This ensures every player instance selects the
            // same card reward, preventing deck divergence and StateDivergence
            // disconnects caused by local System.Random producing different picks.
            uint hash = 2166136261;
            foreach (var opt in options)
            {
                string id = opt.Card?.Id?.Entry ?? "";
                foreach (char c in id)
                    hash = unchecked((hash ^ c) * 16777619);
            }
            pickIndex = (int)(hash % (uint)options.Count);
        }
        else
        {
            pickIndex = _rng.Next(options.Count);
        }

        var pick = options[pickIndex];
        return new CardRewardSelection { card = pick.Card, alternative = null };
    }
}
