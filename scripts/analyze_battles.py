#!/usr/bin/env python3
"""
Analyze TokenSpire2 battle logs to identify issues and suggest optimizations.

Usage:
    python scripts/analyze_battles.py [--dir PATH] [--verbose] [--output PATH]

Reads all battle_*.json files in the specified directory (default: llm_data/battles/)
and produces a detailed analysis including:
    1. Aggregate statistics (win rate, avg turns, avg HP loss)
    2. Card-level analysis (cards that frequently fail, cards with bad estimates)
    3. Solver performance (crash rate, states explored, greedy fallback frequency)
    4. Per-encounter breakdown
    5. Optimization suggestions for card priorities and evaluation weights
"""

import argparse
import json
import sys
from collections import defaultdict
from datetime import datetime
from pathlib import Path


# ── Known card baselines (from CardEffectReader.FallbackEstimate) ─────────────
# Used to compare solver estimates against actual outcomes

CARD_DAMAGE = {
    # Ironclad (STS2)
    "STRIKE_IRONCLAD": 6, "DEFEND_IRONCLAD": 0, "BASH": 8, "TREMBLE": 0,
    "HAVOC": 4, "INFLAME": 0, "FASTEN": 0, "GREED": 7, "CLUMSY": 0,
    "DOUBT": 0, "DISMANTLE": 5, "SNAKEBITE": 3, "PECK": 3, "FLICK_FLACK": 3,
    # Ironclad (STS1 aliases)
    "STRIKE_R": 6, "DEFEND_R": 0, "ANGER": 6, "HEADBUTT": 9,
    "POMMEL_STRIKE": 8, "SWORD_BOOMERANG": 3, "TWIN_STRIKE": 5,
    "WILD_STRIKE": 17, "PERFECTED_STRIKE": 12, "HEAVY_BLADE": 14,
    "CLOTHESLINE": 12, "UPPERCUT": 13, "WHIRLWIND": 5,
    "BLOOD_FOR_BLOOD": 18, "CLASH": 14, "DROP_KICK": 5,
    "IMMOLATE": 21, "RAMPAGE": 8, "SEARING_BLOW": 12,
    "SEVER_SOUL": 16, "THUNDERCLAP": 7, "REAPER": 4,
    "BODY_SLAM": 0,  # based on block
    "FEED": 10, "RECKLESS_CHARGE": 7, "BLOODLETTING": 0,
    "HEMOKINESIS": 15, "OFFERING": 0, "PUMMEL": 2,
    "BURNING_PACT": 0, "BATTLE_TRANCE": 0, "SHRUG_IT_OFF": 0,
    # Silent
    "NEUTRALIZE": 3, "DEADLY_POISON": 5, "BACKSTAB": 11,
    "DAGGER_SPRAY": 4, "BOUNCING_FLASK": 3, "EVISCERATE": 7,
    "FLEETING_STRIKE": 9, "DAGGER_THROW": 9, "SKEWER": 7,
    "UNLOAD": 14, "CRIPPLING_CLOUD": 4, "CORPSE_EXPLOSION": 6,
    "ALL_OUT_ATTACK": 10, "RIDDLE_WITH_HOLES": 3,
    "GRAND_FINALE": 50, "STORM_OF_STEEL": 4,
    "ENDLESS_AGONY": 4, "HEEL_HOOK": 5, "SUCKER_PUNCH": 7,
    # Defect
    "BALL_LIGHTNING": 7, "CLAW": 3, "COMPILE_DRIVER": 7,
    "BARRAGE": 4, "BLIZZARD": 0, "MELTDOWN": 0,
    "STREAMLINE": 15, "SUNDER": 24, "DOOM_AND_GLOOM": 10,
    "THUNDER_STRIKE": 7, "REBOUND": 9, "SWEEPING_BEAM": 6,
    "COLD_SNAP": 6, "DARK_SHACKLES": 0,
    # Necrobinder
    "THE_SCYTHE": 18, "REAP": 14, "FLESH_OFFERING": 0,
    "BONE_ARMOR": 0, "GRAVE_DIG": 0, "RITUAL_DAGGER": 15,
}

CARD_BLOCK = {
    # Ironclad (STS2)
    "DEFEND_IRONCLAD": 5, "STRIKE_IRONCLAD": 0,
    "TREMBLE": 0, "HAVOC": 0, "INFLAME": 0, "FASTEN": 5,
    "CLUMSY": 0, "DOUBT": 0, "GREED": 0, "DISMANTLE": 0,
    "SNAKEBITE": 0, "PECK": 0, "FLICK_FLACK": 0,
    # Ironclad (STS1 aliases)
    "DEFEND_R": 5, "SHRUG_IT_OFF": 8, "TRUE_GRIT": 7,
    "ARMAMENTS": 5, "IRON_WAVE": 5, "FLAME_BARRIER": 12,
    "GHOSTLY_ARMOR": 10, "IMPERVIOUS": 30, "POWER_THROUGH": 15,
    "SENTINEL": 5, "SECOND_WIND": 0, "ENTRENCH": 0,
    "JUGGERNAUT": 0, "METALLICIZE": 0, "RAGE": 0,
    "DEFEND_G": 5, "BACKFLIP": 5, "SURVIVOR": 8,
    "BLUR": 5, "DODGE_AND_ROLL": 4, "DEFLECT": 4,
    "ESCAPE_PLAN": 0, "LEG_SWEEP": 0,
    "DEFEND_B": 5, "CHARGE_BATTERY": 7, "GLACIER": 7,
    "BOOT_SEQUENCE": 0, "REINFORCED_BODY": 7, "AUTO_SHIELDS": 0,
    "GENETIC_ALGORITHM": 0, "STEAM_BARRIER": 6,
    "FORCEFIELD": 0, "EQUILIBRIUM": 13,
    "DEFEND_N": 5, "BONE_WALL": 6,
    "DEFEND_RG": 5,
}


def load_battles(battle_dir: Path) -> list[dict]:
    """Load all battle JSON files from a directory."""
    battles = []
    for f in sorted(battle_dir.glob("battle_*.json")):
        try:
            with open(f, "r", encoding="utf-8") as fh:
                battles.append(json.load(fh))
        except Exception as e:
            print(f"  WARN: Failed to load {f.name}: {e}", file=sys.stderr)
    return battles


# ── Aggregate statistics ─────────────────────────────────────────────────────

def compute_aggregates(battles: list[dict]) -> dict:
    """Compute aggregate statistics from all battles."""
    if not battles:
        return {"total": 0}

    total = len(battles)
    wins = sum(1 for b in battles if b.get("Victory"))
    losses = total - wins
    all_turns = [b.get("TotalTurns", 0) for b in battles]
    all_dmg_taken = [b.get("TotalDamageTaken", 0) for b in battles]
    all_dmg_dealt = [b.get("TotalDamageDealt", 0) for b in battles]
    all_crashes = [b.get("SolverCrashes", 0) for b in battles]
    durations = [b.get("DurationSeconds", 0) for b in battles]

    return {
        "total": total,
        "wins": wins,
        "losses": losses,
        "win_rate": wins / total if total > 0 else 0,
        "avg_turns": sum(all_turns) / total if total > 0 else 0,
        "avg_damage_taken": sum(all_dmg_taken) / total if total > 0 else 0,
        "avg_damage_dealt": sum(all_dmg_dealt) / total if total > 0 else 0,
        "total_solver_crashes": sum(all_crashes),
        "crash_rate": sum(1 for c in all_crashes if c > 0) / total if total > 0 else 0,
        "avg_duration": sum(durations) / total if total > 0 else 0,
        "min_turns": min(all_turns) if all_turns else 0,
        "max_turns": max(all_turns) if all_turns else 0,
    }


# ── Per-encounter breakdown ──────────────────────────────────────────────────

def encounter_breakdown(battles: list[dict]) -> list[dict]:
    """Aggregate stats per encounter type."""
    by_encounter = defaultdict(list)
    for b in battles:
        name = b.get("EncounterName", "Unknown")
        by_encounter[name].append(b)

    results = []
    for name, group in sorted(by_encounter.items()):
        total = len(group)
        wins = sum(1 for b in group if b.get("Victory"))
        results.append({
            "encounter": name,
            "count": total,
            "wins": wins,
            "losses": total - wins,
            "win_rate": wins / total if total > 0 else 0,
            "avg_turns": sum(b.get("TotalTurns", 0) for b in group) / total if total > 0 else 0,
            "avg_damage_taken": sum(b.get("TotalDamageTaken", 0) for b in group) / total if total > 0 else 0,
        })
    return results


# ── Card-level analysis ──────────────────────────────────────────────────────

def card_analysis(battles: list[dict]) -> dict:
    """Analyze card performance across all battles."""
    card_played = defaultdict(int)       # times card was played
    card_failed = defaultdict(int)       # times card failed to play
    card_in_hand = defaultdict(int)      # times card appeared in hand
    card_estimated_dmg = defaultdict(list)  # estimated damage values
    card_estimated_block = defaultdict(list)  # estimated block values

    for b in battles:
        for turn in b.get("Turns", []):
            # Cards in hand
            for card in turn.get("Hand", []):
                cid = card.get("Id", "?")
                card_in_hand[cid] += 1

            # Solver plan estimates
            plan = turn.get("SolverPlan")
            if plan:
                # We can't easily map individual estimates to cards from the string representation
                pass

            # Action results
            for action in turn.get("ActionResults", []):
                cid = action.get("CardId", "?") or "?"
                if action.get("Success"):
                    card_played[cid] += 1
                    if action.get("ActualDamage"):
                        card_estimated_dmg[cid].append(action["ActualDamage"])
                    if action.get("ActualBlock"):
                        card_estimated_block[cid].append(action["ActualBlock"])
                else:
                    card_failed[cid] += 1

    return {
        "played": dict(card_played),
        "failed": dict(card_failed),
        "in_hand": dict(card_in_hand),
        "avg_damage": {k: sum(v) / len(v) for k, v in card_estimated_dmg.items() if v},
        "avg_block": {k: sum(v) / len(v) for k, v in card_estimated_block.items() if v},
    }


# ── Solver performance ───────────────────────────────────────────────────────

def solver_performance(battles: list[dict]) -> dict:
    """Analyze solver performance across all turns."""
    turn_count = 0
    crash_turns = 0
    greedy_turns = 0
    empty_turns = 0
    energy_wasted_turns = 0
    total_energy_wasted = 0
    avg_actions = []
    avg_estimated_dmg = []
    avg_estimated_block = []
    states_explored = []

    for b in battles:
        for turn in b.get("Turns", []):
            turn_count += 1
            plan = turn.get("SolverPlan")
            outcome = turn.get("Outcome", {})

            # Check for energy waste
            energy_rem = outcome.get("EnergyRemaining", 0)
            playable_not_played = outcome.get("PlayableCardsNotPlayed", 0)
            if energy_rem > 0 and playable_not_played > 0:
                energy_wasted_turns += 1
                total_energy_wasted += energy_rem

            # Check for issues from the new logging
            issue_list = outcome.get("Issues", [])
            for issue in issue_list:
                if "ENERGY_WASTED" in issue:
                    energy_wasted_turns += 1

            if plan:
                actions = plan.get("Actions", [])
                has_crash = any("CRASH" in a for a in actions)
                has_greedy = any("greedy" in a.lower() for a in actions)
                is_empty = len(actions) == 0 or all(a == "END_TURN" for a in actions)

                if has_crash:
                    crash_turns += 1
                if has_greedy:
                    greedy_turns += 1
                if is_empty:
                    empty_turns += 1

                avg_actions.append(len([a for a in actions if a != "END_TURN"]))
                avg_estimated_dmg.append(plan.get("EstimatedDamage", 0))
                avg_estimated_block.append(plan.get("EstimatedBlock", 0))
                states = plan.get("StatesExplored", 0)
                if states > 0:
                    states_explored.append(states)

    return {
        "total_turns": turn_count,
        "crash_turns": crash_turns,
        "crash_rate": crash_turns / turn_count if turn_count > 0 else 0,
        "greedy_turns": greedy_turns,
        "greedy_rate": greedy_turns / turn_count if turn_count > 0 else 0,
        "empty_turns": empty_turns,
        "empty_rate": empty_turns / turn_count if turn_count > 0 else 0,
        "energy_wasted_turns": energy_wasted_turns,
        "energy_wasted_rate": energy_wasted_turns / turn_count if turn_count > 0 else 0,
        "total_energy_wasted": total_energy_wasted,
        "avg_actions_per_turn": sum(avg_actions) / len(avg_actions) if avg_actions else 0,
        "avg_estimated_damage": sum(avg_estimated_dmg) / len(avg_estimated_dmg) if avg_estimated_dmg else 0,
        "avg_estimated_block": sum(avg_estimated_block) / len(avg_estimated_block) if avg_estimated_block else 0,
        "avg_states_explored": sum(states_explored) / len(states_explored) if states_explored else 0,
        "max_states_explored": max(states_explored) if states_explored else 0,
    }


# ── Problem detection ────────────────────────────────────────────────────────

def detect_problems(card_stats: dict, solver_stats: dict, encounters: list[dict]) -> list[dict]:
    """Detect specific problems and suggest optimizations."""
    problems = []

    # 1. Cards that frequently fail to play
    for cid, fail_count in sorted(card_stats["failed"].items(), key=lambda x: -x[1]):
        played = card_stats["played"].get(cid, 0)
        total = played + fail_count
        if total >= 3 and fail_count / total > 0.3:
            problems.append({
                "severity": "high",
                "type": "card_frequently_fails",
                "card": cid,
                "detail": f"{cid}: {fail_count}/{total} attempts failed ({100*fail_count/total:.0f}%)",
                "suggestion": f"Check {cid}'s mana cost, target requirements, or play conditions",
            })

    # 2. Cards with no estimated values
    for cid in card_stats["in_hand"]:
        if cid not in CARD_DAMAGE and cid not in CARD_BLOCK and cid not in (
            "DEFEND_R", "DEFEND_G", "DEFEND_B", "DEFEND_N", "DEFEND_RG",
            "STRIKE_R", "STRIKE_G", "STRIKE_B", "STRIKE_N", "STRIKE_RG",
        ):
            if not cid.startswith("?"):
                problems.append({
                    "severity": "low",
                    "type": "missing_card_estimate",
                    "card": cid,
                    "detail": f"{cid} has no known baseline damage/block values",
                    "suggestion": f"Add {cid} to CardEffectReader.FallbackEstimate()",
                })

    # 3. Solver crash rate too high
    if solver_stats["crash_rate"] > 0.05:
        problems.append({
            "severity": "critical",
            "type": "high_solver_crash_rate",
            "detail": f"Solver crash rate: {100*solver_stats['crash_rate']:.1f}% ({solver_stats['crash_turns']}/{solver_stats['total_turns']} turns)",
            "suggestion": "Investigate crash callstacks in mod logs. Check CardEffectReader for cards causing exceptions.",
        })

    # 4. Greedy fallback too frequent
    if solver_stats["greedy_rate"] > 0.1:
        problems.append({
            "severity": "high",
            "type": "frequent_greedy_fallback",
            "detail": f"Greedy fallback rate: {100*solver_stats['greedy_rate']:.1f}%",
            "suggestion": "Solver is crashing or returning empty plans frequently. Improve CardEffectReader coverage.",
        })

    # 5. Empty turn rate
    if solver_stats["empty_rate"] > 0.15:
        problems.append({
            "severity": "medium",
            "type": "high_empty_turn_rate",
            "detail": f"Empty turn rate: {100*solver_stats['empty_rate']:.1f}%",
            "suggestion": "Solver is often finding nothing playable. Check energy estimation and card priorities.",
        })

    # 5b. Energy waste rate
    if solver_stats["energy_wasted_rate"] > 0.1:
        problems.append({
            "severity": "high",
            "type": "energy_wasted",
            "detail": f"Energy wasted on {solver_stats['energy_wasted_turns']}/{solver_stats['total_turns']} turns ({100*solver_stats['energy_wasted_rate']:.1f}%), total {solver_stats['total_energy_wasted']} energy unused",
            "suggestion": "Solver is ending turns with playable cards and unused energy. Check card priority scoring and DFS search depth.",
        })

    # 6. Low win rate encounters
    for enc in encounters:
        if enc["count"] >= 3 and enc["win_rate"] < 0.5:
            problems.append({
                "severity": "high",
                "type": "low_win_rate_encounter",
                "encounter": enc["encounter"],
                "detail": f"{enc['encounter']}: {enc['wins']}/{enc['count']} wins ({100*enc['win_rate']:.0f}%)",
                "suggestion": f"Record more data for {enc['encounter']}. Consider tuning defensive card weights or targeting priority.",
            })

    # 7. High damage taken
    high_dmg_encounters = [e for e in encounters if e["avg_damage_taken"] > 20]
    for enc in high_dmg_encounters:
        problems.append({
            "severity": "medium",
            "type": "high_damage_encounter",
            "encounter": enc["encounter"],
            "detail": f"{enc['encounter']}: avg {enc['avg_damage_taken']:.0f} damage taken",
            "suggestion": "Increase block card priority or add defensive heuristics for this encounter.",
        })

    return problems


# ── Optimization suggestions ─────────────────────────────────────────────────

def generate_suggestions(aggregates: dict, card_stats: dict, solver_stats: dict,
                         encounters: list[dict], problems: list[dict]) -> list[dict]:
    """Generate specific optimization suggestions."""
    suggestions = []

    # 1. Card priority adjustments
    frequently_failed = [p for p in problems if p["type"] == "card_frequently_fails"]
    if frequently_failed:
        suggestions.append({
            "area": "card_priority",
            "action": "Lower priority or check play conditions for frequently failing cards",
            "cards": [p["card"] for p in frequently_failed],
            "reason": "These cards fail to play >30% of the time, wasting solver states",
        })

    # 2. Missing card data
    missing = [p for p in problems if p["type"] == "missing_card_estimate"]
    if missing:
        suggestions.append({
            "area": "card_estimates",
            "action": "Add baseline damage/block estimates for missing cards",
            "cards": [p["card"] for p in missing[:10]],  # top 10
            "reason": "Without estimates, solver defaults to Attack=6/Skill=5, causing poor plans",
        })

    # 3. Energy efficiency tuning
    if solver_stats["avg_actions_per_turn"] < 1.5 and solver_stats["total_turns"] > 20:
        suggestions.append({
            "area": "energy_efficiency",
            "action": "Increase MAX_CARDS_PER_TURN or tune card cost evaluation",
            "reason": f"Only {solver_stats['avg_actions_per_turn']:.1f} actions/turn on average",
        })

    # 4. Defense tuning based on damage taken
    if aggregates.get("avg_damage_taken", 0) > 15:
        suggestions.append({
            "area": "defense_weights",
            "action": "Increase block evaluation weight in CharacterConfig",
            "reason": f"Average {aggregates['avg_damage_taken']:.0f} damage taken per battle",
        })

    # 5. Search depth
    if solver_stats.get("max_states_explored", 0) >= 1900:
        suggestions.append({
            "area": "search_depth",
            "action": "Increase MAX_SEARCH_STATES (currently 2000)",
            "reason": f"Solver frequently hits the state limit (max seen: {solver_stats['max_states_explored']})",
        })

    # 6. Draw simulation improvement
    draw_cards_played = sum(
        card_stats["played"].get(c, 0) for c in
        ["POMMEL_STRIKE", "BURNING_PACT", "BATTLE_TRANCE", "OFFERING",
         "SHRUG_IT_OFF", "BACKFLIP", "ACROBATICS", "CALCULATED_GAMBLE",
         "SKIM", "COOLHEADED", "COMPILE_DRIVER", "OVERCLOCK"]
    )
    if draw_cards_played > 0:
        suggestions.append({
            "area": "draw_simulation",
            "action": "Verify draw pile tracking accuracy in solver",
            "reason": f"{draw_cards_played} draw card plays detected — ensure draw simulation works correctly",
        })

    return suggestions


# ── Report generation ────────────────────────────────────────────────────────

def generate_report(aggregates: dict, encounters: list[dict], card_stats: dict,
                    solver_stats: dict, problems: list[dict],
                    suggestions: list[dict]) -> str:
    """Generate a Markdown analysis report."""
    lines = []
    lines.append("# TokenSpire2 Battle Analysis")
    lines.append(f"**Generated:** {datetime.now().isoformat()}")
    lines.append(f"**Battles analyzed:** {aggregates.get('total', 0)}")
    lines.append("")

    # ── Aggregate stats ──
    lines.append("## 1. Aggregate Statistics")
    lines.append("")
    if aggregates["total"] == 0:
        lines.append("No battles recorded.")
        return "\n".join(lines)

    lines.append(f"| Metric | Value |")
    lines.append(f"|--------|-------|")
    lines.append(f"| Total Battles | {aggregates['total']} |")
    lines.append(f"| Wins | {aggregates['wins']} |")
    lines.append(f"| Losses | {aggregates['losses']} |")
    lines.append(f"| Win Rate | {100*aggregates['win_rate']:.1f}% |")
    lines.append(f"| Avg Turns/Battle | {aggregates['avg_turns']:.1f} |")
    lines.append(f"| Avg Damage Dealt | {aggregates['avg_damage_dealt']:.0f} |")
    lines.append(f"| Avg Damage Taken | {aggregates['avg_damage_taken']:.0f} |")
    lines.append(f"| Total Solver Crashes | {aggregates['total_solver_crashes']} |")
    lines.append(f"| Crash Rate | {100*aggregates['crash_rate']:.1f}% |")
    lines.append(f"| Avg Duration | {aggregates['avg_duration']:.0f}s |")
    lines.append(f"| Turn Range | {aggregates['min_turns']}–{aggregates['max_turns']} |")
    lines.append("")

    # ── Encounter breakdown ──
    lines.append("## 2. Encounter Breakdown")
    lines.append("")
    if encounters:
        lines.append("| Encounter | Count | Wins | Losses | Win% | Avg Turns | Avg Dmg Taken |")
        lines.append("|-----------|-------|------|--------|------|-----------|---------------|")
        for e in encounters:
            lines.append(
                f"| {e['encounter']} | {e['count']} | {e['wins']} | {e['losses']} | "
                f"{100*e['win_rate']:.0f}% | {e['avg_turns']:.1f} | {e['avg_damage_taken']:.0f} |"
            )
    else:
        lines.append("No encounter data.")
    lines.append("")

    # ── Card performance ──
    lines.append("## 3. Card Performance")
    lines.append("")
    lines.append("### Most Played Cards")
    top_played = sorted(card_stats["played"].items(), key=lambda x: -x[1])[:20]
    if top_played:
        for cid, count in top_played:
            fail = card_stats["failed"].get(cid, 0)
            lines.append(f"- **{cid}**: {count} plays, {fail} fails")
    else:
        lines.append("No card play data.")
    lines.append("")

    # Frequently failing cards
    if card_stats["failed"]:
        lines.append("### Frequently Failing Cards")
        for cid, count in sorted(card_stats["failed"].items(), key=lambda x: -x[1])[:10]:
            played = card_stats["played"].get(cid, 0)
            lines.append(f"- **{cid}**: {count} fails / {played + count} attempts")
        lines.append("")

    # ── Solver performance ──
    lines.append("## 4. Solver Performance")
    lines.append("")
    lines.append(f"| Metric | Value |")
    lines.append(f"|--------|-------|")
    lines.append(f"| Total Turns | {solver_stats['total_turns']} |")
    lines.append(f"| Crash Turns | {solver_stats['crash_turns']} ({100*solver_stats['crash_rate']:.1f}%) |")
    lines.append(f"| Greedy Fallback Turns | {solver_stats['greedy_turns']} ({100*solver_stats['greedy_rate']:.1f}%) |")
    lines.append(f"| Empty Turns | {solver_stats['empty_turns']} ({100*solver_stats['empty_rate']:.1f}%) |")
    lines.append(f"| Energy Wasted Turns | {solver_stats['energy_wasted_turns']} ({100*solver_stats['energy_wasted_rate']:.1f}%), {solver_stats['total_energy_wasted']} energy unused |")
    lines.append(f"| Avg Actions/Turn | {solver_stats['avg_actions_per_turn']:.1f} |")
    lines.append(f"| Avg Est. Damage | {solver_stats['avg_estimated_damage']:.0f} |")
    lines.append(f"| Avg Est. Block | {solver_stats['avg_estimated_block']:.0f} |")
    lines.append(f"| Avg States Explored | {solver_stats['avg_states_explored']:.0f} |")
    lines.append(f"| Max States Explored | {solver_stats['max_states_explored']} |")
    lines.append("")

    # ── Problems ──
    lines.append("## 5. Detected Problems")
    lines.append("")
    if problems:
        severe = [p for p in problems if p["severity"] == "critical"]
        high = [p for p in problems if p["severity"] == "high"]
        medium = [p for p in problems if p["severity"] == "medium"]
        low = [p for p in problems if p["severity"] == "low"]

        if severe:
            lines.append("### 🔴 Critical")
            for p in severe:
                lines.append(f"- **{p['type']}**: {p['detail']}")
                lines.append(f"  → {p['suggestion']}")
            lines.append("")
        if high:
            lines.append("### 🟠 High")
            for p in high:
                lines.append(f"- **{p['type']}**: {p['detail']}")
                lines.append(f"  → {p['suggestion']}")
            lines.append("")
        if medium:
            lines.append("### 🟡 Medium")
            for p in medium:
                lines.append(f"- **{p['type']}**: {p['detail']}")
                lines.append(f"  → {p['suggestion']}")
            lines.append("")
        if low:
            lines.append("### 🔵 Low")
            for p in low:
                lines.append(f"- **{p['type']}**: {p['detail']}")
            lines.append("")
    else:
        lines.append("No problems detected.")
    lines.append("")

    # ── Suggestions ──
    lines.append("## 6. Optimization Suggestions")
    lines.append("")
    if suggestions:
        for i, s in enumerate(suggestions, 1):
            lines.append(f"### {i}. {s['area'].replace('_', ' ').title()}")
            lines.append(f"**Action:** {s['action']}")
            if "cards" in s:
                lines.append(f"**Cards:** {', '.join(s['cards'])}")
            lines.append(f"**Why:** {s['reason']}")
            lines.append("")
    else:
        lines.append("No specific suggestions yet — collect more data.")
    lines.append("")

    return "\n".join(lines)


# ── Main ─────────────────────────────────────────────────────────────────────

def main():
    parser = argparse.ArgumentParser(description="Analyze TokenSpire2 battle logs")
    parser.add_argument("--dir", type=str, default=None,
                        help="Battle log directory (default: auto-detect)")
    parser.add_argument("--verbose", "-v", action="store_true",
                        help="Print detailed card-level stats")
    parser.add_argument("--output", "-o", type=str, default=None,
                        help="Output report path (default: stdout)")
    args = parser.parse_args()

    # Auto-detect log directory
    if args.dir:
        battle_dir = Path(args.dir)
    else:
        # Try to find the mod directory relative to this script
        script_dir = Path(__file__).resolve().parent.parent
        battle_dir = script_dir / "llm_data" / "battles"
        if not battle_dir.exists():
            # Fallback to hardcoded path
            battle_dir = Path(
                r"E:\SteamLibrary\steamapps\common\Slay the Spire 2"
                r"\mods\TokenSpire2\llm_data\battles"
            )

    if not battle_dir.exists():
        print(f"ERROR: Battle log directory not found: {battle_dir}")
        print("Make sure the mod has been run at least once with BattleLogger enabled.")
        sys.exit(1)

    print(f"Loading battles from: {battle_dir}")
    battles = load_battles(battle_dir)
    print(f"Loaded {len(battles)} battle logs.")

    if not battles:
        print("No battles to analyze.")
        sys.exit(0)

    # Compute all stats
    aggregates = compute_aggregates(battles)
    encounters = encounter_breakdown(battles)
    card_stat = card_analysis(battles)
    solver_stat = solver_performance(battles)
    problems = detect_problems(card_stat, solver_stat, encounters)
    suggestions = generate_suggestions(aggregates, card_stat, solver_stat, encounters, problems)

    # Generate report
    report = generate_report(aggregates, encounters, card_stat, solver_stat,
                             problems, suggestions)

    if args.output:
        Path(args.output).write_text(report, encoding="utf-8")
        print(f"Report saved: {args.output}")
    else:
        print(report)

    if args.verbose:
        print("\n" + "="*60)
        print("VERBOSE CARD STATS")
        print("="*60)
        print("\nPlayed cards:")
        for cid, count in sorted(card_stat["played"].items(), key=lambda x: -x[1]):
            fail = card_stat["failed"].get(cid, 0)
            avg_dmg = card_stat["avg_damage"].get(cid)
            avg_blk = card_stat["avg_block"].get(cid)
            extras = []
            if avg_dmg is not None:
                extras.append(f"avg dmg={avg_dmg:.0f}")
            if avg_blk is not None:
                extras.append(f"avg blk={avg_blk:.0f}")
            extra_str = f" ({', '.join(extras)})" if extras else ""
            print(f"  {cid}: {count} plays, {fail} fails{extra_str}")


if __name__ == "__main__":
    main()
