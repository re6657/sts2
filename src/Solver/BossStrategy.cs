using System;
using System.Collections.Generic;

namespace TokenSpire2.Solver;

/// <summary>
/// Boss-specific combat strategy adjustments.
///
/// Each boss in STS2 has unique mechanics that demand different combat approaches.
/// This class provides per-boss multipliers for the solver's scoring dimensions,
/// applied on top of the existing elite/boss aggression bonuses.
///
/// Strategy reference: E:\STS2_Boss_Guide.md (complete boss manual)
/// </summary>
public static class BossStrategy
{
    /// <summary>Adjustments to apply to solver scoring for a specific boss.</summary>
    public class Adjustment
    {
        /// <summary>Multiplier on damage value (1.0 = normal). >1 = more aggressive.</summary>
        public double DamageMult = 1.0;

        /// <summary>Multiplier on block value. >1 = more defensive.</summary>
        public double BlockMult = 1.0;

        /// <summary>Multiplier on AoE damage bonus. >1 = value AoE more.</summary>
        public double AoeBonus = 0.0;

        /// <summary>Multiplier on strength gain value. <1 when boss steals strength.</summary>
        public double StrengthMult = 1.0;

        /// <summary>Multiplier on power/setup card value. >1 when free setup turns exist.</summary>
        public double PowerMult = 1.0;

        /// <summary>Extra damage-per-energy weight for multi-hit attacks (vs Slippery).</summary>
        public double MultiHitBonus = 0.0;

        /// <summary>Penalty per card played beyond threshold (for Aeonglass Wither).</summary>
        public double CardPlayPenalty = 0.0;

        /// <summary>Threshold after which card play penalty applies.</summary>
        public int CardPlayPenaltyThreshold = 99;

        /// <summary>
        /// Multiplier on the selection weight in the two-dimensional combined score.
        /// >1.0 = prioritize playing the RIGHT cards (card VALUE matters more).
        /// <1.0 = selection weight is less important.
        /// </summary>
        public double SelectionWeightMult = 1.0;

        /// <summary>
        /// Multiplier on the order weight in the two-dimensional combined score.
        /// >1.0 = card ORDER matters more (play setup/debuff cards first).
        /// <1.0 = order matters less (just play whatever).
        /// </summary>
        public double OrderWeightMult = 1.0;

        /// <summary>Description for debug logs.</summary>
        public string Description = "";
    }

    /// <summary>Boss name pattern → strategy adjustment.</summary>
    private static readonly Dictionary<string, Adjustment> BossStrategies =
        new(StringComparer.OrdinalIgnoreCase)
    {
        // ═══════════════════════════════════════════════════════════════════
        // Act 1 — Overgrowth
        // ═══════════════════════════════════════════════════════════════════

        ["VANTOM"] = new Adjustment
        {
            // Slippery shield: first 9 attacks deal 1 damage.
            // Multi-hit attacks are essential. Single big hits are wasted.
            // Wound pollution every 4 turns — need to kill before deck clogs.
            DamageMult = 1.2,
            BlockMult = 0.9,
            MultiHitBonus = 25.0, // massive bonus for multi-hit
            Description = "Vantom: multi-hit to break Slippery, avoid single big hits"
        },

        ["CEREMONIAL_BEAST"] = new Adjustment
        {
            // Phase 1: Rush to break 150 HP threshold before strength stacks too high.
            // Phase 2: Big cards during Ringing (1 card/turn limit).
            // The stun turn after Plow is a free setup window.
            DamageMult = 1.5,  // aggressive — must break 150 HP FAST
            BlockMult = 0.7,   // don't over-block, prioritize damage
            PowerMult = 1.2,   // powers during stun turn are good
            Description = "Ceremonial Beast: DPS race to 150 HP, then big cards during Ringing"
        },

        ["THE_KIN"] = new Adjustment
        {
            // 3 targets: priest + 2 followers. AoE is king.
            // Constant incoming damage from staggered attacks.
            // Kill left minion first (it buffs itself turn 1).
            DamageMult = 1.3,
            BlockMult = 1.1,
            AoeBonus = 40.0, // massive AoE bonus — hitting all 3 is huge
            Description = "The Kin: AoE priority, 3 targets, constant damage"
        },

        // ═══════════════════════════════════════════════════════════════════
        // Act 1 — Underdocks
        // ═══════════════════════════════════════════════════════════════════

        ["LAGAVULIN"] = new Adjustment
        {
            // 3 free setup turns while sleeping. Then steals 2 str/dex every 4 turns.
            // Strength-dependent multi-hit builds are WEAK here.
            // Poison, orbs, Doom bypass strength drain.
            DamageMult = 1.2,
            BlockMult = 0.9,
            PowerMult = 2.0,   // free setup during sleep — powers are double value
            StrengthMult = 0.3, // strength gets stolen — don't over-value strength gain
            MultiHitBonus = -10.0, // multi-hits are BAD (each hit weakened by stolen str)
            Description = "Lagavulin: free setup during sleep, strength gets drained"
        },

        ["WATERFALL_GIANT"] = new Adjustment
        {
            // Steam Eruption: death explosion = total stacks. No need to rush.
            // Steady block + steady damage. Prepare for the explosion.
            // Weak reduces explosion damage.
            DamageMult = 0.9,  // don't rush, steady pace
            BlockMult = 1.5,   // prioritize block for the death explosion
            PowerMult = 0.8,   // slow powers not great — it dies on its own clock
            Description = "Waterfall Giant: steady block, prepare for death explosion"
        },

        ["SOUL_FYSH"] = new Adjustment
        {
            // Beckon cards deal 6 HP loss per turn if not played.
            // Intangible every 5 turns. Don't over-draw (pulls Beckons).
            // Big deck is an advantage.
            DamageMult = 1.1,
            BlockMult = 1.1,
            CardPlayPenalty = 2.0,
            CardPlayPenaltyThreshold = 6, // discourage excessive card play (Beckons)
            Description = "Soul Fysh: handle Beckons, avoid over-drawing, Intangible cycle"
        },

        // ═══════════════════════════════════════════════════════════════════
        // Act 2 — Hive
        // ═══════════════════════════════════════════════════════════════════

        ["INSATIABLE"] = new Adjustment
        {
            // Sandpit countdown = 4 → 0 = instant death. MUST play Frantic Escape.
            // Pure DPS race. Defensive/slow decks die.
            // Frantic Escape costs increase: 1→2→3→4... per use.
            DamageMult = 2.0,   // MAXIMUM aggression — it's a DPS timer
            BlockMult = 0.4,    // minimal block — every energy goes to damage
            PowerMult = 0.5,    // slow powers are terrible — need damage NOW
            Description = "Insatiable: MAX DPS race, sandpit timer = death"
        },

        ["KNOWLEDGE_DEMON"] = new Adjustment
        {
            // Curse choices every 4 turns. Heals 30 HP + gains 2-3 str per cycle.
            // Cumulative damage pressure from curses.
            DamageMult = 1.4,
            BlockMult = 0.8,
            CardPlayPenalty = 1.0,
            CardPlayPenaltyThreshold = 5, // curses limit what you can do
            Description = "Knowledge Demon: curse management, heal cycles, scaling threat"
        },

        ["KAISER_CRAB"] = new Adjustment
        {
            // Two claws: Crusher (left, 199 HP) + Rocket (right, 189 HP).
            // Focus Rocket first. AoE hits both. Facing system matters.
            // When one dies, other gets 99 block + 5 strength.
            DamageMult = 1.3,
            BlockMult = 1.0,
            AoeBonus = 35.0, // hitting both claws is very efficient
            Description = "Kaiser Crab: AoE for both claws, focus Rocket first"
        },

        // ═══════════════════════════════════════════════════════════════════
        // Act 3 — Glory
        // ═══════════════════════════════════════════════════════════════════

        ["QUEEN"] = new Adjustment
        {
            // Queen + Torch Head minion. Chains of Binding locks 3 cards.
            // Rush Queen (she buffs minion and gives it block each turn).
            // Poison bypasses block. Need heavy draw to deal with chains.
            DamageMult = 1.5,
            BlockMult = 0.8,
            PowerMult = 0.7, // no time for slow setup
            Description = "Queen: rush Queen directly, heavy draw needed, poison bypasses"
        },

        ["TEST_SUBJECT"] = new Adjustment
        {
            // 3 phases: 100→200→300 HP. Debuffs cleared between phases.
            // Phase 1: no skills (gives +2 str per skill).
            // Phase 2: full block all attacks (or wounds fill deck).
            // Phase 3: Intangible every other turn.
            // Need sustained scaling — Demon Form, dark orbs, stars.
            DamageMult = 1.3,
            BlockMult = 1.4,   // phase 2 demands heavy block
            PowerMult = 1.5,   // scaling powers are essential (600 total HP)
            StrengthMult = 1.3, // strength scaling is premium here
            Description = "Test Subject: sustained scaling, phase-specific, 600 total HP"
        },

        ["AEONGLASS"] = new Adjustment
        {
            // Wither cards: every 4 non-status cards → 1 Wither in hand.
            // Withers Retain and deal 2 HP loss each at turn end.
            // Control card play rate. Big cards > many small ones.
            // 3 artifact layers to strip first.
            DamageMult = 1.2,
            BlockMult = 1.1,
            CardPlayPenalty = 5.0,
            CardPlayPenaltyThreshold = 4, // heavy penalty for playing many cards
            Description = "Aeonglass: limit card plays to avoid Withers, big cards preferred"
        },
    };

    /// <summary>
    /// Get the boss strategy adjustment for a given encounter ID.
    /// Returns null if this isn't a boss encounter or no specific strategy exists.
    /// </summary>
    public static Adjustment? GetStrategy(string? encounterId)
    {
        if (string.IsNullOrEmpty(encounterId)) return null;

        var upper = encounterId.ToUpperInvariant();

        foreach (var (pattern, strategy) in BossStrategies)
        {
            if (upper.Contains(pattern))
                return strategy;
        }
        return null;
    }
}
