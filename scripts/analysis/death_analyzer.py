#!/usr/bin/env python3
"""
死亡原因自动分类器

分析战斗日志，自动分类每局死亡原因，生成可操作的建议。
与 optimizer.py 集成：每代评估后自动运行分析，结果反馈到参数更新。
"""

import json
from pathlib import Path
from collections import Counter
from typing import Dict, List, Optional
from dataclasses import dataclass, field


MOD_DIR = Path(r"E:\SteamLibrary\steamapps\common\Slay the Spire 2\mods\TokenSpire2")


@dataclass
class DeathCategory:
    name: str
    count: int = 0
    sample_run_ids: List[int] = field(default_factory=list)
    suggestion: str = ""
    target_param: str = ""


class DeathAnalyzer:
    """自动分类死亡原因"""

    def __init__(self):
        pass

    def analyze_battle_log(self, battle: dict) -> List[str]:
        """Analyze a single battle log and return applicable death categories."""
        categories = []

        if battle.get("Victory", False):
            return categories  # No death, no analysis needed

        turns = battle.get("Turns", [])
        if not turns:
            return ["unknown"]

        last_turn = turns[-1] if turns else {}

        # 1. Unused potions
        potions = last_turn.get("PotionIds", []) or []
        if potions:
            categories.append("unused_potions")

        # 2. Energy wasted (played too few cards)
        outcome = last_turn.get("Outcome", {}) or {}
        if outcome:
            energy_remaining = outcome.get("EnergyRemaining", 0)
            playable_not_played = outcome.get("PlayableCardsNotPlayed", 0)
            if energy_remaining > 0 and playable_not_played > 0:
                categories.append("played_too_few_cards")

        # 3. Over-blocking detected
        solver_plan = last_turn.get("SolverPlan", {}) or {}
        if solver_plan:
            est_block = solver_plan.get("EstimatedBlock", 0)
            est_damage = solver_plan.get("EstimatedDamage", 0)
            if est_block > 15 and est_damage < 5:
                categories.append("over_blocking")

        # 4. Missed lethal
        enemies = last_turn.get("Enemies", []) or []
        if enemies:
            # Check if any enemy was near death
            for enemy in enemies:
                hp = enemy.get("Hp", 999)
                max_hp = enemy.get("MaxHp", 999)
                if hp > 0 and hp < max_hp * 0.15:
                    categories.append("missed_lethal")
                    break

        # 5. Elite encounter
        encounter = battle.get("EncounterName", "")
        is_elite = any(e in encounter.lower() for e in
                      ["gremlin nob", "lagavulin", "sentry", "tri-sentry"])

        # 6. Took burst damage (single hit > 30% HP)
        hp_before = last_turn.get("Hp", 100)
        damage_taken = outcome.get("DamageTaken", 0) if outcome else 0
        max_hp = last_turn.get("MaxHp", 100)
        if max_hp > 0 and damage_taken > max_hp * 0.3:
            categories.append("no_block_vs_burst")

        if not categories:
            categories.append("attrition")  # Died slowly, deck isn't good enough

        return categories

    def analyze_run(self, battle_logs: List[dict], floor_threshold: int = 2) -> dict:
        """Analyze all battles in a run and classify the likely cause of death/failure."""
        if not battle_logs:
            return {"primary_cause": "no_battles", "categories": []}

        # Find losses
        losses = [b for b in battle_logs if not b.get("Victory", False)]
        if not losses:
            return {"primary_cause": "all_wins", "categories": []}

        # Aggregate causes across losses
        all_causes = []
        for loss in losses:
            all_causes.extend(self.analyze_battle_log(loss))

        cause_counts = Counter(all_causes)
        primary = cause_counts.most_common(1)[0][0] if cause_counts else "unknown"

        return {
            "primary_cause": primary,
            "categories": dict(cause_counts),
            "total_battles": len(battle_logs),
            "total_losses": len(losses),
            "floors_reached": max((b.get("Floor", 0) for b in battle_logs), default=0),
        }

    def batch_analyze(self, batch_runs_dir: Optional[Path] = None) -> dict:
        """Analyze all runs in a batch_runs directory. Returns aggregate categories."""
        batch_dir = batch_runs_dir or (MOD_DIR / "batch_runs")
        if not batch_dir.exists():
            return {"error": "batch_runs directory not found"}

        results = {
            "total_runs": 0,
            "death_categories": Counter(),
            "sample_runs": {},
        }

        for run_dir in sorted(batch_dir.iterdir()):
            if not run_dir.is_dir() or not run_dir.name.startswith("run_"):
                continue

            battles_dir = run_dir / "battles"
            if not battles_dir.exists():
                continue

            battles = []
            for f in sorted(battles_dir.glob("battle_*.json")):
                try:
                    battles.append(json.loads(f.read_text(encoding="utf-8")))
                except Exception:
                    pass

            if battles:
                results["total_runs"] += 1
                analysis = self.analyze_run(battles)

                for category, count in analysis.get("categories", {}).items():
                    results["death_categories"][category] += count
                    if category not in results["sample_runs"]:
                        results["sample_runs"][category] = []
                    results["sample_runs"][category].append(run_dir.name)

        return results

    # ── Suggestion mapping ────────────────────────────────────────────────

    SUGGESTIONS = {
        "unused_potions": {
            "suggestion": "药水未使用就死亡。降低药水使用阈值",
            "target_param": "potion.use_when_hp_below_ratio",
            "direction": "increase",  # Increase threshold to use potions sooner
        },
        "played_too_few_cards": {
            "suggestion": "结束回合过早，剩余能量未利用",
            "target_param": "combat_solver.scoring.energy_per_point",
            "direction": "increase",
        },
        "over_blocking": {
            "suggestion": "获得过多不必要格挡，可能错过击杀窗口",
            "target_param": "combat_solver.scoring.block_per_excess_point",
            "direction": "decrease",
        },
        "missed_lethal": {
            "suggestion": "有击杀机会但未选择正确出牌顺序",
            "target_param": "combat_solver.scoring.kill_weight",
            "direction": "increase",
        },
        "no_block_vs_burst": {
            "suggestion": "面对爆发伤害回合没有足够格挡",
            "target_param": "combat_solver.scoring.health_penalty_low_hp_multiplier",
            "direction": "increase",
        },
        "attrition": {
            "suggestion": "卡组质量不足，被慢慢消耗致死",
            "target_param": "card_reward.skip_threshold.base",
            "direction": "decrease",  # Be less picky
        },
    }

    def get_parameter_suggestions(self, death_categories: Dict[str, int]) -> List[dict]:
        """Convert death categories into parameter adjustment suggestions."""
        suggestions = []
        total_deaths = sum(death_categories.values())
        if total_deaths == 0:
            return suggestions

        for category, count in death_categories.items():
            if category in self.SUGGESTIONS:
                info = self.SUGGESTIONS[category]
                suggestions.append({
                    "category": category,
                    "frequency": count / total_deaths,
                    "count": count,
                    "suggestion": info["suggestion"],
                    "target_param": info["target_param"],
                    "direction": info["direction"],
                })

        return sorted(suggestions, key=lambda s: s["frequency"], reverse=True)


# ── CLI ────────────────────────────────────────────────────────────────────────

if __name__ == "__main__":
    import argparse, sys
    parser = argparse.ArgumentParser(description="TokenSpire2 Death Analyzer")
    parser.add_argument("--batch-dir", type=str, default=None,
                       help="Path to batch_runs directory")
    parser.add_argument("--output", type=str, default=None,
                       help="Output JSON file for results")
    args = parser.parse_args()

    analyzer = DeathAnalyzer()

    batch_dir = Path(args.batch_dir) if args.batch_dir else None
    results = analyzer.batch_analyze(batch_dir)

    suggestions = analyzer.get_parameter_suggestions(dict(results.get("death_categories", {})))

    print("\n=== DEATH ANALYSIS ===\n")
    print(f"Runs analyzed: {results.get('total_runs', 0)}")
    print(f"\nDeath categories:")

    for cat, count in results.get("death_categories", {}).most_common():
        pct = count / max(1, results.get("total_runs", 1)) * 100
        print(f"  {cat:<25s}: {count:>3d} ({pct:.0f}%)")

    print(f"\nParameter suggestions:")
    for s in suggestions:
        print(f"  [{s['frequency']:.0%}] {s['category']}: {s['suggestion']}")
        print(f"         → adjust {s['target_param']} ({s['direction']})")

    if args.output:
        output = {
            "results": {"total_runs": results.get("total_runs", 0),
                        "death_categories": dict(results.get("death_categories", {}))},
            "suggestions": suggestions,
        }
        Path(args.output).write_text(json.dumps(output, indent=2, ensure_ascii=False), encoding="utf-8")
        print(f"\nResults saved to: {args.output}")
