using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Events;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;

using TokenSpire2.Core;

namespace TokenSpire2.Solver;

/// <summary>
/// Event risk/reward evaluation.
/// Known events get hardcoded strategies. Unknown events parse text keywords.
/// Prefers reward options (cards, relics, gold, HP) over HP-cost options.
/// </summary>
public static class EventDecider
{
    // Track how many times we've selected each option label to prevent repeat-loop deaths
    private static Dictionary<string, int> _repeatCounts = new();
    private static string _lastEventId = "";

    public static bool Decide(RunState state)
    {
        var eventRoom = GetEventRoom();
        if (eventRoom == null) return false;

        // Detect event identity change → reset repeat counters
        string currentEventId = GetCurrentEventId(eventRoom);
        if (currentEventId != _lastEventId)
        {
            _lastEventId = currentEventId;
            _repeatCounts.Clear();
        }

        // Proceed button first (some events show it after completion)
        var proceedBtn = AutoSlayHelpers.FindFirst<NProceedButton>(eventRoom);
        if (proceedBtn?.IsEnabled == true)
        {
            MainFile.Logger.Info("[EventDecider] Clicking proceed button");
            proceedBtn.ForceClick();
            DecisionLogger.LogDecision(GameScreen.EVENT, "EventProceed",
                new List<DecisionLogger.OptionScore>(), 0, "PROCEED",
                "Proceed button is enabled");
            return true;
        }

        var options = AutoSlayHelpers.FindAll<NEventOptionButton>(eventRoom)
            .Where(o => !o.Option.IsLocked)
            .ToList();

        if (options.Count == 0)
        {
            // Try dialogue hitbox (Ancient event)
            var dialogueBtn = eventRoom.GetNodeOrNull<NButton>("%DialogueHitbox");
            if (dialogueBtn != null && dialogueBtn.Visible && dialogueBtn.IsEnabled)
            {
                MainFile.Logger.Info("[EventDecider] Clicking Ancient event dialogue");
                dialogueBtn.EmitSignal(NClickableControl.SignalName.Released, dialogueBtn);
                DecisionLogger.LogDecision(GameScreen.EVENT, "EventDialogue",
                    new List<DecisionLogger.OptionScore>(), 0, "DIALOGUE",
                    "Ancient event dialogue hitbox clicked");
                return true;
            }
            return false;
        }

        // Score and pick the best option
        var scored = options.Select((o, i) =>
        {
            double score = ScoreEventOption(o, state);
            string label = OptionLabel(o);
            return (index: i, score, label, option: o);
        }).OrderByDescending(x => x.score).ToList();

        // ── Fallback: if ALL options are repeat-blocked, reset counters ──
        // Without this, we'd soft-lock on events where every option has been
        // picked 3+ times (e.g. infinite-loop events like Scrap Ooze).
        if (scored.Count > 0 && scored.All(x => x.score <= SolverParams.Instance.Event.RepeatHardBlock))
        {
            MainFile.Logger.Info($"[EventDecider] ALL {scored.Count} options repeat-blocked! Resetting counters for event {currentEventId}");
            _repeatCounts.Clear();
            // Re-score without repeat penalties
            scored = options.Select((o, i) =>
            {
                double score = ScoreEventOption(o, state);
                string label = OptionLabel(o);
                return (index: i, score, label, option: o);
            }).OrderByDescending(x => x.score).ToList();
        }

        var best = Tiebreaker.PickBestFromSorted(scored, x => x.score);
        string reason = $"Chose event option {best.index + 1}/{options.Count}: {best.label} score={best.score:F0}";

        // Track repeat count for this option
        // M7: key by content label, not just index — some events randomize option order
        string repeatKey = $"{currentEventId}:{best.label}";
        _repeatCounts.TryGetValue(repeatKey, out int count);
        _repeatCounts[repeatKey] = count + 1;

        DecisionLogger.LogDecision(
            GameScreen.EVENT, "EventChoice",
            scored.Select(s => new DecisionLogger.OptionScore
            {
                Index = s.index, Label = s.label, Score = s.score
            }).ToList(),
            best.index, best.label, reason);

        MainFile.Logger.Info($"[EventDecider] {reason}");

        // ── Set card-select context BEFORE clicking ──────────────────────
        // AutoSlayCardSelector (global ICardSelector) uses this context to
        // decide which card to pick. Must be set before the game opens the
        // card grid and calls ICardSelector.GetSelectedCards().
        string optLower = best.label.ToLowerInvariant();
        if (optLower.Contains("remove") || optLower.Contains("purge")
            || optLower.Contains("toke") || optLower.Contains("cleanse"))
            DecisionEngine.PendingCardSelectContext = "REMOVE";
        else if (optLower.Contains("transform") || optLower.Contains("change"))
            DecisionEngine.PendingCardSelectContext = "TRANSFORM";
        else if (optLower.Contains("upgrade") || optLower.Contains("smith")
            || optLower.Contains("forge") || optLower.Contains("improve"))
            DecisionEngine.PendingCardSelectContext = "UPGRADE";

        best.option.ForceClick();
        return true;
    }

    /// <summary>Get a stable identifier for the current event from room/scene metadata.</summary>
    private static string GetCurrentEventId(Node eventRoom)
    {
        try
        {
            // Try to get the event model ID
            var eventModelProp = eventRoom.GetType().GetProperty("EventModel");
            if (eventModelProp != null)
            {
                var model = eventModelProp.GetValue(eventRoom);
                if (model != null)
                {
                    var idProp = model.GetType().GetProperty("Id");
                    if (idProp != null)
                    {
                        var id = idProp.GetValue(model);
                        if (id != null) return id.ToString() ?? "?";
                    }
                }
            }
        }
        catch { }
        return eventRoom.GetType().Name; // fallback: room class name
    }

    private static double ScoreEventOption(NEventOptionButton option, RunState state)
    {
        double score = 50;
        var ep = SolverParams.Instance.Event;
        var kw = ep.Keywords;

        string godotText = ReadButtonGodotText(option);
        string locText = "";
        try { locText = (option.Option?.Title?.ToString() ?? "") + " " + (option.Option?.Description?.ToString() ?? ""); }
        catch { }

        string text = !string.IsNullOrWhiteSpace(godotText) ? godotText : locText;
        text = text.ToLower();
        if (string.IsNullOrWhiteSpace(text) || text.StartsWith("locstring"))
            text = ExtractKeyFromLocString(locText).ToLower();

        // ── REPEAT DETECTION ──
        int repeatCount = 0;
        var options = AutoSlayHelpers.FindAll<NEventOptionButton>(option.GetParentOrNull<Node>()).ToList();
        int idx = options.IndexOf(option);
        if (idx >= 0)
        {
            // M7: key by content label + index — content-based for random-order events,
            // index for distinguishing identical-text options (e.g. two "Leave" buttons)
            string label = OptionLabel(option);
            string repeatKey = $"{_lastEventId}:{label}:{idx}";
            _repeatCounts.TryGetValue(repeatKey, out repeatCount);
        }

        if (repeatCount >= 3)
        {
            MainFile.Logger.Info($"[EventDecider] BLOCKED option {idx + 1} — already picked {repeatCount} times");
            return ep.RepeatHardBlock;
        }
        if (repeatCount >= 2)
            score += ep.RepeatPenalty2;

        // ── KNOWN REPEATABLE EVENTS ──
        if (text.Contains("tablet") || text.Contains("truth"))
        {
            MainFile.Logger.Info("[EventDecider] Detected Tablet of Truth — penalizing heavily");
            score += ep.TabletPenalty;
        }

        // ── HP-COST DETECTION ──
        // Supports both English and Chinese (中文) keywords
        bool costsHp = text.Contains("lose") && (text.Contains("hp") || text.Contains("health"));
        costsHp |= text.Contains("sacrifice") && (text.Contains("hp") || text.Contains("health"));
        costsHp |= text.Contains("pay") && (text.Contains("hp") || text.Contains("health") || text.Contains("blood"));
        costsHp |= text.Contains("blood") && (text.Contains("sacrifice") || text.Contains("offer"));
        costsHp |= (text.Contains("damage") || text.Contains("hurt")) && text.Contains("take");
        costsHp |= text.Contains("offer") && (text.Contains("hp") || text.Contains("health") || text.Contains("blood"));
        costsHp |= text.Contains("hp") && (text.Contains("spend") || text.Contains("drain") || text.Contains("cost"));
        // Chinese: 失去/损失/牺牲 + 生命/血量/HP
        costsHp |= (text.Contains("失去") || text.Contains("损失")) && (text.Contains("生命") || text.Contains("血量") || text.Contains("hp"));
        costsHp |= (text.Contains("牺牲") || text.Contains("献祭")) && (text.Contains("生命") || text.Contains("血量"));
        costsHp |= text.Contains("受到") && text.Contains("伤害");
        costsHp |= text.Contains("扣") && (text.Contains("血") || text.Contains("生命"));

        if (costsHp)
        {
            // ── Absolute HP floor: never reduce HP or max HP below this ──
            // Ratio-based checks can be too permissive at high max HP.
            // e.g., 30% of 200 HP = 60 — ratio says "safe" but absolute HP is 60.
            // This hard-blocks ANY HP-cost option that would drop HP too low.
            if (state.CurrentHp < ep.EventAbsoluteHpFloor)
            {
                MainFile.Logger.Info($"[EventDecider] Hard-blocking HP-cost option: HP={state.CurrentHp} < floor={ep.EventAbsoluteHpFloor}");
                return ep.HpCostHardBlockScore;
            }

            if (state.HpRatio < ep.HpCostHardBlockThreshold)
                return ep.HpCostHardBlockScore;
            if (state.HpRatio < ep.HpCostWarningThreshold)
                score += ep.HpCostWarningScore;
            else
                score += ep.HpCostNormalScore;
        }

        // ── Curse detection ──
        // NOTE: "wound" and "burn" are status cards (removed after combat), NOT curses.
        // Including them causes false positives for events that give temporary status cards.
        // Chinese: 诅咒牌名称 (pain=痛苦, regret=悔恨, shame=羞耻, doubt=怀疑, normality=凡庸, decay=腐朽)
        bool givesCurse = text.Contains("curse") || text.Contains("pain") || text.Contains("regret")
            || text.Contains("shame") || text.Contains("doubt") || text.Contains("normality")
            || text.Contains("decay")
            || text.Contains("诅咒") || text.Contains("痛苦") || text.Contains("悔恨")
            || text.Contains("羞耻") || text.Contains("怀疑") || text.Contains("凡庸")
            || text.Contains("腐朽");
        if (givesCurse)
            score += state.HasExhaustSynergy ? ep.CurseWithSynergyPenalty : ep.CurseNoSynergyPenalty;

        // ── STATUS CARD DETECTION ──────────────────────────────────────────
        // M10: Wound, Burn, Slimed, Void, Dazed are status cards (removed after combat).
        // They're less harmful than curses but still clog the deck during combat.
        // Apply a mild penalty for events that add status cards.
        bool givesStatus = text.Contains("wound") || text.Contains("burn")
            || text.Contains("slimed") || text.Contains("dazed") || text.Contains("void")
            || text.Contains("伤口") || text.Contains("灼伤") || text.Contains("粘液")
            || text.Contains("眩晕") || text.Contains("虚空");
        if (givesStatus)
            score += ep.StatusCardPenalty;

        // ── Positive keywords (English + Chinese 中文) ──
        if (text.Contains("heal") || text.Contains("restore") || text.Contains("治疗") || text.Contains("回复") || text.Contains("恢复"))
            score += state.IsHpLow ? kw.HealLowHp : kw.HealHighHp;
        if (text.Contains("relic") || text.Contains("artifact") || text.Contains("遗物") || text.Contains("圣物")) score += kw.Relic;
        if ((text.Contains("card") || text.Contains("牌")) && !text.Contains("curse") && !text.Contains("诅咒")) score += kw.Card;
        if (text.Contains("gold") || text.Contains("money") || text.Contains("金币") || text.Contains("钱")) score += kw.Gold;
        if (text.Contains("upgrade") || text.Contains("smith") || text.Contains("升级") || text.Contains("锻造")) score += kw.Upgrade;
        if (text.Contains("transform") || text.Contains("变换") || text.Contains("转化")) score += kw.Transform + (state.TotalBasicCards * 6);
        if (text.Contains("remove") || text.Contains("purge") || text.Contains("移除") || text.Contains("删除") || text.Contains("剔除")) score += kw.Remove + (state.TotalBasicCards * 8);
        if (text.Contains("strength") || text.Contains("power") || text.Contains("力量") || text.Contains("能力")) score += kw.Strength;
        if (text.Contains("max hp") || text.Contains("max health") || text.Contains("最大生命") || text.Contains("生命上限")) score += kw.MaxHp;
        if (text.Contains("proceed") || text.Contains("leave") || text.Contains("continue") || text.Contains("离开") || text.Contains("继续")) score += kw.Proceed;

        if (text.Contains("ancient") || text.Contains("excavation") || text.Contains("远古") || text.Contains("挖掘"))
        { if (text.Contains("relic") || text.Contains("遗物")) score += 50; }
        if (text.Contains("sacrifice") || text.Contains("blood") || text.Contains("牺牲") || text.Contains("献祭") || text.Contains("献血")) score += kw.Sacrifice;

        // ── Known event-specific strategies ────────────────────────────────
        score += GetKnownEventBonus(text, state);

        // ── Deck-aware modifiers ──────────────────────────────────────────
        // Large deck → remove/transform is more valuable
        if (state.IsDeckLarge)
        {
            if (text.Contains("remove") || text.Contains("purge") || text.Contains("移除") || text.Contains("删除")) score += 20;
            if (text.Contains("transform") || text.Contains("变换") || text.Contains("转化")) score += 15;
        }
        // Many basic cards → transform is more valuable (replaces bad cards)
        if (state.TotalBasicCards >= 5 && (text.Contains("transform") || text.Contains("变换") || text.Contains("转化"))) score += 20;
        // Act 1 → avoid curses more strongly (early curse ruins run)
        if (state.Act == 1 && givesCurse) score -= 20;

        // ── POSITION-BASED HEURISTIC ──────────────────────────────────────
        // When text extraction fails completely (score still near 50 baseline),
        // use option position as a weak signal. In most STS events:
        //   Index 0: positive/reward option (accept, take, proceed)
        //   Last index: leave/decline/skip option
        // This is a TIEBREAKER only — keyword bonuses (up to ±50+) dominate.
        int totalOptions = options.Count;
        if (idx == 0)
        {
            // First option is typically the "active" choice — take the reward
            score += 5;
            // If HP is good, prefer the active option more
            if (!state.IsHpLow) score += 3;
        }
        else if (idx == totalOptions - 1 && totalOptions >= 2)
        {
            // Last option is typically "leave" — prefer when HP is low
            if (state.IsHpLow) score += 10;
            else score -= 5;
        }
        // Middle options get no positional bonus/penalty

        // ── OPTION TEXT QUALITY CHECK ──────────────────────────────────────
        // Log a warning when text extraction produced poor results
        if (string.IsNullOrWhiteSpace(text) || text.Length < 3 || text == "?" || text.StartsWith("locstring"))
        {
            MainFile.Logger.Info($"[EventDecider] WARNING: Poor text for option {idx + 1}/{totalOptions}: " +
                $"godotText='{godotText}' locText='{locText}' — using position heuristic");
            // Reward option: index 0 in a 2-3 option event is usually positive
            if (idx == 0 && totalOptions <= 3) score += 15;
            // Leave option: last in a multi-option event
            if (idx == totalOptions - 1 && totalOptions >= 2) score -= 5;
        }

        return score;
    }

    /// <summary>Apply known event-specific strategy bonuses based on event text content.</summary>
    private static double GetKnownEventBonus(string text, RunState state)
    {
        double bonus = 0;

        // ── SCRAP OOZE ────────────────────────────────────────────────────
        // Options: [Reach In] (take damage for relic) vs [Leave]
        // Strategy: Keep reaching until accumulated damage ≥ 18, then leave.
        // Each successful reach gives a relic for ~4-7 damage (avg 5.5).
        if (text.Contains("ooze") || text.Contains("scrap"))
        {
            if (text.Contains("reach"))
                bonus += 40; // Reach in — relic for small HP cost is good
            if (text.Contains("leave") || text.Contains("go"))
                bonus += 10; // Leaving is fine but not optimal early
        }

        // ── THE LIBRARY ────────────────────────────────────────────────────
        // Options: [Read] (choose 1 of 3 cards) vs [Sleep] (heal 20%)
        // Strategy: Read when HP > 50%, Sleep when low.
        if (text.Contains("library") || text.Contains("read"))
        {
            if (text.Contains("read") || text.Contains("book") || text.Contains("study"))
                bonus += state.HpRatio > 0.5 ? 30 : -10;
            if (text.Contains("sleep") || text.Contains("rest"))
                bonus += state.IsHpLow ? 30 : -15;
        }

        // ── CURSED TOME ────────────────────────────────────────────────────
        // Options: [Take Tome] (-21 HP, get Enchiridion/Nilry's Codex/Necronomicon)
        // Strategy: Take if HP > 40 (can safely lose 21 HP).
        if (text.Contains("tome") || text.Contains("book") && text.Contains("curse"))
        {
            if (text.Contains("take") || text.Contains("pick") || text.Contains("grab"))
                bonus += state.CurrentHp > 40 ? 35 : -50;
            if (text.Contains("leave") || text.Contains("walk away"))
                bonus += state.CurrentHp <= 40 ? 20 : -10;
        }

        // ── THE MOAI HEAD ──────────────────────────────────────────────────
        // Options: [Enter] (heal to full, lose 8 max HP) vs [Leave]
        // Strategy: Always heal unless near full HP.
        if (text.Contains("moai") || text.Contains("statue") || text.Contains("head"))
        {
            if (text.Contains("enter") || text.Contains("heal"))
                bonus += state.HpRatio < 0.85 ? 50 : -15;
            if (text.Contains("leave"))
                bonus += state.HpRatio > 0.85 ? 20 : -30;
        }

        // ── VAMPIRES ───────────────────────────────────────────────────────
        // Options: [Accept] (lose 30% max HP, get 5 Bites) vs [Refuse]
        // Strategy: Usually refuse. Bites dilute the deck. Only accept with
        // Blood Vial relic or if desperately needing sustain.
        if (text.Contains("vampire") || text.Contains("bite"))
        {
            if (text.Contains("accept") || text.Contains("yes") || text.Contains("become"))
                bonus += (state.HasSustainRelic || state.IsHpLow) ? 10 : -50;
            if (text.Contains("refuse") || text.Contains("no") || text.Contains("decline"))
                bonus += state.HasSustainRelic ? -5 : 30;
        }

        // ── GHOST COUNCIL ──────────────────────────────────────────────────
        // Options: [Accept] (lose 50% max HP, get 5 Apparitions) vs [Refuse]
        // Strategy: Apparitions are very strong. Accept if HP > 50%.
        if (text.Contains("ghost") || text.Contains("apparition") || text.Contains("spectral"))
        {
            if (text.Contains("accept") || text.Contains("take") || text.Contains("offer"))
                bonus += state.HpRatio > 0.5 ? 40 : -30;
            if (text.Contains("refuse") || text.Contains("decline"))
                bonus += state.HpRatio <= 0.5 ? 25 : -20;
        }

        // ── BONFIRE SPIRITS ────────────────────────────────────────────────
        // Options: [Offer] (give a card, get 5 max HP + full heal) vs [Leave]
        // Strategy: Offer a basic Strike/Defend — always good unless deck is tiny.
        if (text.Contains("bonfire") || text.Contains("spirit"))
        {
            if (text.Contains("offer") || text.Contains("give"))
                bonus += state.TotalCardCount > 8 ? 35 : -10;
            if (text.Contains("leave"))
                bonus += state.TotalCardCount <= 8 ? 10 : -20;
        }

        // ── MYSTERIOUS SPHERE ──────────────────────────────────────────────
        // Options: [Open] (get rare relic) vs [Leave]
        // Strategy: Always open — free rare relic.
        if (text.Contains("sphere") || text.Contains("mysterious"))
        {
            if (text.Contains("open") || text.Contains("touch"))
                bonus += 40;
            if (text.Contains("leave"))
                bonus -= 30;
        }

        // ── THE JOUST ──────────────────────────────────────────────────────
        // Options: [Bet on Owner] (win: 100 gold, lose: lose 50 gold or nothing)
        //          [Bet on Murderer] (win: 200 gold, lose: lose 50 gold)
        // Strategy: Owner wins ~70% of the time. Bet Owner unless desperate.
        if (text.Contains("joust") || text.Contains("bet"))
        {
            if (text.Contains("owner"))
                bonus += 30;
            if (text.Contains("murderer") || text.Contains("killer"))
                bonus += state.Gold < 50 ? 5 : -15;
        }

        // ── BIG FISH ───────────────────────────────────────────────────────
        // Options: [Donate] (lose all gold, gain max HP) vs [Banana] (heal) vs [Relic]
        // Strategy: Donate when low on gold (<80). Banana when injured.
        if (text.Contains("fish") || text.Contains("donate"))
        {
            if (text.Contains("donate") || text.Contains("offer"))
                bonus += state.Gold < 80 ? 30 : -10;
            if (text.Contains("banana"))
                bonus += state.IsHpLow ? 25 : -5;
            if (text.Contains("relic"))
                bonus += 15;
        }

        // ── SSSSERPENT ─────────────────────────────────────────────────────
        // Options: [Agree] (gain 175 gold, get Doubt curse) vs [Disagree]
        // Strategy: Agree only if exhaust synergy or desperate for gold.
        if (text.Contains("serpent") || text.Contains("snake") || text.Contains("doubt"))
        {
            if (text.Contains("agree") || text.Contains("yes"))
                bonus += state.HasExhaustSynergy ? 25 : -25;
            if (text.Contains("disagree") || text.Contains("no"))
                bonus += state.HasExhaustSynergy ? -5 : 20;
        }

        // ── WINDING HALLS ───────────────────────────────────────────────────
        // Options: [Embrace Madness] (get 2 Madness) vs [Press On] (take Writhe curse, heal 25%)
        //           vs [Retreat] (lose 5% max HP)
        // Strategy: Madness is great with high-cost deck. Writhe is terrible.
        if (text.Contains("wind") || text.Contains("hall") || text.Contains("madness"))
        {
            if (text.Contains("madness") || text.Contains("embrace"))
                bonus += state.HighCostCardCount >= 3 ? 30 : 5;
            if (text.Contains("press on") || text.Contains("writhe"))
                bonus -= 40; // Writhe is bad
            if (text.Contains("retreat"))
                bonus -= 5;
        }

        // ── THE CLERIC ─────────────────────────────────────────────────────
        // Options: [Remove] (35 gold to remove) vs [Heal] (35 gold to heal 25%)
        //           vs [Leave]
        // Strategy: Remove if have basics, Heal if low HP.
        if (text.Contains("cleric"))
        {
            if (text.Contains("remove") || text.Contains("purge"))
                bonus += state.TotalBasicCards > 2 ? 30 : 5;
            if (text.Contains("heal") || text.Contains("restore"))
                bonus += state.IsHpLow ? 30 : -10;
        }

        // ── LIVING WALL ────────────────────────────────────────────────────
        // Options: [Forget] (remove) vs [Change] (transform) vs [Grow] (upgrade)
        // Strategy: Remove if basics, Transform if basics but need cards,
        // Upgrade only if have strong unupgraded cards.
        if (text.Contains("wall") || text.Contains("living"))
        {
            if (text.Contains("remove") || text.Contains("forget"))
                bonus += state.TotalBasicCards >= 3 ? 30 : 0;
            if (text.Contains("transform") || text.Contains("change"))
                bonus += state.TotalBasicCards >= 3 ? 25 : 0;
            if (text.Contains("upgrade") || text.Contains("grow"))
                bonus += state.CountUpgradedCards < 3 ? 15 : 5;
        }

        return bonus;
    }

    /// <summary>Read the actual displayed text from the button's Godot Label nodes.</summary>
    private static string ReadButtonGodotText(NEventOptionButton option)
    {
        try
        {
            var texts = new List<string>();

            // Walk all child nodes looking for Labels
            void CollectLabels(Node node)
            {
                foreach (var child in node.GetChildren())
                {
                    if (child is Label label && label.Visible)
                    {
                        string t = label.Text ?? "";
                        if (!string.IsNullOrWhiteSpace(t))
                            texts.Add(t);
                    }
                    CollectLabels(child);
                }
            }

            CollectLabels(option);
            return string.Join(" ", texts);
        }
        catch
        {
            return "";
        }
    }

    /// <summary>Extract meaningful key text from a LocString debug representation.</summary>
    private static string ExtractKeyFromLocString(string locStr)
    {
        // LocString debug format: "LocString table <table> entry <entry>.<key>"
        // Extract the last parts which contain the actual key name
        if (string.IsNullOrWhiteSpace(locStr)) return "";
        var parts = locStr.Split(' ', '.');
        // Return the last few segments which are most likely to contain meaningful info
        var meaningful = parts.Where(p =>
            !string.IsNullOrWhiteSpace(p) &&
            !p.Equals("LocString", StringComparison.OrdinalIgnoreCase) &&
            !p.Equals("table", StringComparison.OrdinalIgnoreCase) &&
            !p.Equals("entry", StringComparison.OrdinalIgnoreCase) &&
            p.Length > 1
        ).ToList();

        if (meaningful.Count == 0) return locStr;
        // Skip the first segment (it's usually the LocString type name)
        // Return the remaining key parts joined
        return string.Join(" ", meaningful.Skip(1));
    }

    private static string OptionLabel(NEventOptionButton option)
    {
        try
        {
            // Primary: read from Godot label
            string label = ReadButtonGodotText(option);
            if (!string.IsNullOrWhiteSpace(label))
            {
                if (label.Length > 50) label = label[..50];
                return label;
            }

            // Fallback: try LocString resolution
            var opt = option.Option;
            if (opt == null) return "?";

            string title = ResolveLocString(opt.Title) ?? "?";
            string desc = ResolveLocString(opt.Description) ?? "";
            if (string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(desc))
                title = desc;
            if (title.Length > 50) title = title[..50];
            return title;
        }
        catch { return "?"; }
    }

    /// <summary>Resolve a LocString to readable text using reflection.</summary>
    private static string? ResolveLocString(object? locStr)
    {
        if (locStr == null) return null;
        try
        {
            var t = locStr.GetType();

            // Try known property names for resolved text
            foreach (var propName in new[] { "Text", "Resolved", "LocalizedText",
                "DisplayString", "Value", "String", "Localized", "Translation",
                "RawText", "PlainText", "Message" })
            {
                var prop = t.GetProperty(propName);
                if (prop != null)
                {
                    var val = prop.GetValue(locStr);
                    if (val is string s && !string.IsNullOrWhiteSpace(s)
                        && !s.StartsWith("LocString"))
                        return s;
                }
            }

            // Try ToString — skip if it's just the debug representation
            var str = locStr.ToString();
            if (str != null && !str.StartsWith("LocString") && !str.StartsWith("MegaCrit"))
                return str;

            // Try the Entry/Key property chain: LocString -> Entry -> Key -> string
            var entryProp = t.GetProperty("Entry");
            if (entryProp != null)
            {
                var entry = entryProp.GetValue(locStr);
                if (entry == null) return str;
                if (entry is string entryStr) return entryStr;

                var entryType = entry.GetType();
                // Try .Key property
                var keyProp = entryType.GetProperty("Key");
                if (keyProp != null)
                {
                    var key = keyProp.GetValue(entry);
                    if (key is string keyStr && !string.IsNullOrWhiteSpace(keyStr))
                        return keyStr;
                }
                // Try .Value property
                var valueProp = entryType.GetProperty("Value");
                if (valueProp != null)
                {
                    var val = valueProp.GetValue(entry);
                    if (val is string valStr && !string.IsNullOrWhiteSpace(valStr))
                        return valStr;
                }
                // Try another .Entry
                var entryEntryProp = entryType.GetProperty("Entry");
                if (entryEntryProp != null)
                {
                    var entry2 = entryEntryProp.GetValue(entry);
                    if (entry2 is string s2) return s2;
                }
                // Try ToString on entry
                var entryStr2 = entry.ToString();
                if (entryStr2 != null && !entryStr2.StartsWith("LocString") && !entryStr2.StartsWith("MegaCrit"))
                    return entryStr2;
            }

            return str;
        }
        catch { return locStr.ToString(); }
    }

    private static Node? GetEventRoom()
    {
        try
        {
            var root = ((SceneTree)Engine.GetMainLoop()).Root;
            return root.GetNodeOrNull<Node>(
                "Game/RootSceneContainer/Run/RoomContainer/EventRoom");
        }
        catch { return null; }
    }
}
