#!/usr/bin/env python3
"""
战斗效率指标计算器

从 battle_*.json 提取关键效率指标:
  - 每点能量造成的伤害
  - 格挡利用率 (用到多少 vs 产生了多少)
  - 过度击杀比例
  - 药水使用/浪费
  - 回合数 / 搜索状态数 / 决策耗时
"""

import json
from pathlib import Path
from typing import Dict, List, Optional
from collections import defaultdict


MOD_DIR = Path(r"E:\SteamLibrary\steamapps\common\Slay the Spire 2\mods\TokenSpire2")


class CombatAnalyzer:
    """战斗效率分析"""

    def compute_metrics(self, battle: dict) -> dict:
        """Compute efficiency metrics from a single battle log."""
        metrics = {
            "encounter": battle.get("EncounterName", "?"),
            "floor": battle.get("Floor", 0),
            "victory": battle.get("Victory", False),
            "total_turns": battle.get("TotalTurns", 0),
            "total_damage_dealt": battle.get("TotalDamageDealt", 0),
            "total_damage_taken": battle.get("TotalDamageTaken", 0),
            "total_enemies_killed": battle.get("TotalEnemiesKilled", 0),
            "solver_crashes": battle.get("SolverCrashes", 0),
        }

        turns = battle.get("Turns", [])
        if not turns:
            return metrics

        # Per-turn analysis
        total_energy = 0
        total_energy_used = 0
        total_block_gained = 0
        total_block_used = 0
        total_overkill = 0
        total_actions = 0
        total_states = 0
        total_cards_playable = 0
        total_cards_played = 0
        potions_used = 0

        for turn in turns:
            energy = turn.get("Energy", 3)
            total_energy += energy

            plan = turn.get("SolverPlan", {}) or {}
            total_states += plan.get("StatesExplored", 0)

            actions = plan.get("Actions", []) or []
            total_actions += len([a for a in actions if not a.startswith("END_TURN")])

            # Energy used = energy - remaining
            outcome = turn.get("Outcome", {}) or {}
            remaining = outcome.get("EnergyRemaining", 0)
            total_energy_used += (energy - remaining)

            # Block efficiency
            est_block = plan.get("EstimatedBlock", 0)
            total_block_gained += est_block
            damage_taken = outcome.get("DamageTaken", 0)
            # Block used = block_gained - block wasted (if we took damage, block was used)
            # Simplified: block used = min(block_gained, incoming_damage that was blocked)

            # Playable cards
            playable = outcome.get("TotalPlayableInHand", 0)
            not_played = outcome.get("PlayableCardsNotPlayed", 0)
            total_cards_playable += playable
            total_cards_played += (playable - not_played)

        metrics["total_energy"] = total_energy
        metrics["total_energy_used"] = total_energy_used
        metrics["energy_efficiency"] = total_energy_used / max(1, total_energy)
        metrics["total_block_gained"] = total_block_gained
        metrics["total_actions"] = total_actions
        metrics["avg_states_explored"] = total_states / max(1, len(turns))
        metrics["damage_per_energy"] = metrics["total_damage_dealt"] / max(1, total_energy_used)
        metrics["block_per_energy"] = total_block_gained / max(1, total_energy_used)
        metrics["cards_played_pct"] = total_cards_played / max(1, total_cards_playable)
        metrics["actions_per_turn"] = total_actions / max(1, len(turns))

        # Wasted energy flag
        issues = []
        for turn in turns:
            outcome = turn.get("Outcome", {}) or {}
            for issue in outcome.get("Issues", []):
                if issue not in issues:
                    issues.append(issue)
        metrics["issues"] = issues
        metrics["issue_count"] = len(issues)

        return metrics

    def batch_analyze(self, batch_runs_dir: Optional[Path] = None) -> dict:
        """Aggregate combat metrics across all batch runs."""
        batch_dir = batch_runs_dir or (MOD_DIR / "batch_runs")
        if not batch_dir.exists():
            return {"error": "batch_runs directory not found"}

        all_metrics = []
        encounter_stats = defaultdict(list)
        issue_counter = defaultdict(int)

        for run_dir in sorted(batch_dir.iterdir()):
            if not run_dir.is_dir() or not run_dir.name.startswith("run_"):
                continue

            battles_dir = run_dir / "battles"
            if not battles_dir.exists():
                continue

            for f in sorted(battles_dir.glob("battle_*.json")):
                try:
                    battle = json.loads(f.read_text(encoding="utf-8"))
                    m = self.compute_metrics(battle)
                    all_metrics.append(m)
                    encounter_stats[m["encounter"]].append(m)
                    for issue in m["issues"]:
                        issue_counter[issue] += 1
                except Exception:
                    pass

        if not all_metrics:
            return {"error": "no battles found"}

        # Aggregate
        wins = [m for m in all_metrics if m["victory"]]
        losses = [m for m in all_metrics if not m["victory"]]

        def avg(lst, key):
            vals = [m[key] for m in lst if m.get(key, 0) != 0]
            return sum(vals) / max(1, len(vals))

        aggregate = {
            "total_battles": len(all_metrics),
            "wins": len(wins),
            "losses": len(losses),
            "win_rate": len(wins) / max(1, len(all_metrics)),

            "avg_damage_per_energy": avg(all_metrics, "damage_per_energy"),
            "avg_energy_efficiency": avg(all_metrics, "energy_efficiency"),
            "avg_actions_per_turn": avg(all_metrics, "actions_per_turn"),
            "avg_states_explored": avg(all_metrics, "avg_states_explored"),
            "avg_cards_played_pct": avg(all_metrics, "cards_played_pct"),
            "avg_damage_dealt_per_battle": avg(all_metrics, "total_damage_dealt"),
            "avg_damage_taken_per_battle": avg(all_metrics, "total_damage_taken"),
            "avg_turns_per_battle": avg(all_metrics, "total_turns"),

            "total_issues": dict(issue_counter.most_common(10)),

            # Per-encounter breakdown
            "encounter_stats": {},
        }

        for name, metrics_list in sorted(encounter_stats.items()):
            aggregate["encounter_stats"][name] = {
                "count": len(metrics_list),
                "wins": sum(1 for m in metrics_list if m["victory"]),
                "win_rate": sum(1 for m in metrics_list if m["victory"]) / len(metrics_list),
                "avg_damage_per_energy": avg(metrics_list, "damage_per_energy"),
                "avg_damage_taken": avg(metrics_list, "total_damage_taken"),
            }

        return aggregate

    def print_report(self, aggregate: dict):
        """Print a human-readable report."""
        print("\n=== COMBAT EFFICIENCY REPORT ===\n")
        print(f"Battles: {aggregate.get('total_battles', 0)} "
              f"({aggregate.get('wins', 0)}W / {aggregate.get('losses', 0)}L) "
              f"WR={aggregate.get('win_rate', 0):.0%}")

        print(f"\nEfficiency Metrics:")
        print(f"  Damage/Energy:     {aggregate.get('avg_damage_per_energy', 0):.1f}")
        print(f"  Energy Efficiency: {aggregate.get('avg_energy_efficiency', 0):.1%}")
        print(f"  Actions/Turn:      {aggregate.get('avg_actions_per_turn', 0):.1f}")
        print(f"  Cards Played %:    {aggregate.get('avg_cards_played_pct', 0):.1%}")
        print(f"  States Explored:   {aggregate.get('avg_states_explored', 0):.0f}")
        print(f"  Avg Damage/Battle: {aggregate.get('avg_damage_dealt_per_battle', 0):.0f}")
        print(f"  Avg Damage Taken:  {aggregate.get('avg_damage_taken_per_battle', 0):.0f}")
        print(f"  Avg Turns/Battle:  {aggregate.get('avg_turns_per_battle', 0):.1f}")

        issues = aggregate.get("total_issues", {})
        if issues:
            print(f"\nTop Issues:")
            for issue, count in list(issues.items())[:5]:
                print(f"  [{count}x] {issue}")

        encounter_stats = aggregate.get("encounter_stats", {})
        if encounter_stats:
            print(f"\nEncounter Breakdown ({len(encounter_stats)} types):")
            print(f"  {'Encounter':<30s} {'Count':>5s} {'WR':>6s} {'Dmg/E':>6s} {'DmgTaken':>8s}")
            for name in sorted(encounter_stats.keys()):
                s = encounter_stats[name]
                flag = " ⚠️" if s["count"] >= 2 and s["win_rate"] < 0.5 else ""
                print(f"  {name:<30s} {s['count']:>5d} {s['win_rate']:>5.0%} "
                      f"{s['avg_damage_per_energy']:>5.1f} {s['avg_damage_taken']:>7.0f}{flag}")


# ── CLI ────────────────────────────────────────────────────────────────────────

if __name__ == "__main__":
    import argparse, sys
    parser = argparse.ArgumentParser(description="TokenSpire2 Combat Analyzer")
    parser.add_argument("--batch-dir", type=str, default=None,
                       help="Path to batch_runs directory")
    parser.add_argument("--output", type=str, default=None,
                       help="Output JSON file for results")
    args = parser.parse_args()

    analyzer = CombatAnalyzer()
    batch_dir = Path(args.batch_dir) if args.batch_dir else None
    results = analyzer.batch_analyze(batch_dir)

    analyzer.print_report(results)

    if args.output:
        Path(args.output).write_text(json.dumps(results, indent=2, ensure_ascii=False), encoding="utf-8")
        print(f"\nResults saved to: {args.output}")
