#!/usr/bin/env python3
"""
遗传算法优化器 — TokenSpire2 自动调参系统

基因 = params.json 中的权重值
适应度 = Act1 Boss 击败率 (每局若到达并击败Act1 Boss则计为1)

用法:
    from optimizer import GeneticOptimizer
    opt = GeneticOptimizer(population_size=20, runs_per_evaluation=10)
    opt.run(target_score=0.7, max_generations=50)
"""

import json, random, copy, subprocess, time, os, sys, string
from dataclasses import dataclass, field
from pathlib import Path
from typing import Dict, List, Optional, Tuple
import numpy as np


# ── Paths ──────────────────────────────────────────────────────────────────────

MOD_DIR = Path(r"E:\SteamLibrary\steamapps\common\Slay the Spire 2\mods\TokenSpire2")
PARAMS_PATH = MOD_DIR / "params.json"
BATCH_RUNNER = MOD_DIR / "scripts" / "batch_runner.py"
SESSIONS_DIR = MOD_DIR / "optimization_sessions"


# ── Gene space definition ─────────────────────────────────────────────────────
# Maps param paths to (min, max) ranges

GENE_SPACE: Dict[str, Tuple[float, float]] = {
    # Combat solver scoring weights
    "combat_solver.scoring.kill_weight":               (0.5, 5.0),
    "combat_solver.scoring.damage_weight":             (0.1, 5.0),
    "combat_solver.scoring.block_weight":              (0.1, 5.0),
    "combat_solver.scoring.health_penalty_weight":     (0.5, 5.0),
    "combat_solver.scoring.power_weight":              (0.1, 5.0),
    "combat_solver.scoring.strength_weight":           (0.1, 5.0),
    "combat_solver.scoring.vulnerable_weight":         (0.1, 5.0),
    "combat_solver.scoring.weak_weight":               (0.1, 5.0),
    "combat_solver.scoring.poison_weight":             (0.1, 5.0),
    "combat_solver.scoring.orb_value_weight":          (0.1, 5.0),
    "combat_solver.scoring.energy_per_point":          (1, 10),
    "combat_solver.scoring.block_per_excess_point":    (0, 5),
    "combat_solver.scoring.kill_per_max_hp":           (1, 20),
    "combat_solver.scoring.damage_per_point":          (3, 30),
    "combat_solver.scoring.block_per_needed_point":    (2, 20),
    "combat_solver.scoring.health_penalty_low_hp_multiplier": (20, 200),
    "combat_solver.scoring.health_penalty_normal_multiplier": (10, 100),
    "combat_solver.scoring.card_select_per_played":    (10, 80),

    # Card reward
    "card_reward.stage_weights.raw_efficiency_damage_per_energy": (2, 15),
    "card_reward.stage_weights.buff_strength":         (5, 40),
    "card_reward.stage_weights.aoe_bonus":             (5, 30),
    "card_reward.stage_weights.card_type_power":       (5, 40),
    "card_reward.stage_weights.debuff_vulnerable":     (5, 30),
    "card_reward.stage_weights.draw_detection":        (5, 35),
    "card_reward.skip_threshold.base":                 (10, 50),
    "card_reward.skip_threshold.per_deck_size_above_12": (1, 6),
    "card_reward.skip_threshold.per_act":             (2, 10),
    "card_reward.redundancy_penalty.one_copy":         (-40, -5),
    "card_reward.redundancy_penalty.two_or_more_copies": (-50, -10),

    # Map
    "map.node_scores.elite_base":                      (-200, -20),
    "map.node_scores.campfire_base":                   (3, 20),
    "map.node_scores.shop_base":                       (0, 10),
    "map.node_scores.unknown_base":                    (0, 10),
    "map.node_scores.monster_base":                    (-2, 5),
    "map.path_bonuses.one_campfire":                   (0, 10),
    "map.path_bonuses.path_length_per_node":           (0, 0.5),

    # Rest
    "rest.rest_low_hp_threshold":                      (0.3, 0.7),
    "rest.smith_hp_threshold":                         (0.3, 0.7),
    "rest.rest_low_hp_score":                          (100, 500),
    "rest.smith_high_hp_score":                        (100, 400),

    # Event
    "event.hp_cost_hard_block_threshold":              (0.3, 0.7),
    "event.hp_cost_hard_block_score":                  (-400, -50),
    "event.hp_cost_normal_score":                      (-150, -10),
    "event.keywords.heal_low_hp":                      (100, 400),

    # Shop
    "shop.min_gold_reserve":                           (10, 80),
    "shop.remove_min_deck_size":                       (5, 20),
}


# ── Data structures ───────────────────────────────────────────────────────────

@dataclass
class ParamSet:
    """一组参数及其性能评估"""
    id: int
    params: dict
    score: float = 0.0
    secondary_scores: dict = field(default_factory=dict)
    generation: int = 0

    # Secondary metrics
    act1_boss_kills: int = 0
    total_runs: int = 0
    valid_runs: int = 0        # runs that weren't STUCK/LAUNCH_FAILED
    stuck_runs: int = 0        # runs killed by stuck detection
    avg_damage_per_energy: float = 0.0
    avg_damage_per_battle: float = 0.0  # HP loss per battle (primary metric with debugMaxHp)
    avg_block_efficiency: float = 0.0
    avg_floor_reached: float = 0.0
    death_categories: dict = field(default_factory=dict)
    interruption_categories: dict = field(default_factory=dict)  # exit_reason → count


# ── Nested dict helpers ───────────────────────────────────────────────────────

def _get_nested(d: dict, path: str) -> float:
    keys = path.split(".")
    for k in keys:
        d = d[k]
    return float(d)


def _set_nested(d: dict, path: str, value: float):
    keys = path.split(".")
    for k in keys[:-1]:
        d = d[k]
    d[keys[-1]] = value


def _extract_gene_values(params: dict) -> dict:
    """Extract just the gene-space values from a full params dict."""
    result = {}
    for gene_path in GENE_SPACE:
        try:
            result[gene_path] = _get_nested(params, gene_path)
        except (KeyError, TypeError):
            result[gene_path] = None
    return result


# ═══════════════════════════════════════════════════════════════════════════════
# GENETIC OPTIMIZER
# ═══════════════════════════════════════════════════════════════════════════════

class GeneticOptimizer:
    """遗传算法优化器"""

    def __init__(self,
                 population_size: int = 20,
                 elite_count: int = 4,
                 mutation_rate: float = 0.2,
                 mutation_scale: float = 0.3,
                 crossover_rate: float = 0.7,
                 runs_per_evaluation: int = 10,
                 character: str = "IRONCLAD",
                 session_id: Optional[str] = None):
        self.population_size = population_size
        self.elite_count = max(elite_count, population_size // 5)
        self.mutation_rate = mutation_rate
        self.mutation_scale = mutation_scale
        self.crossover_rate = crossover_rate
        self.runs_per_evaluation = runs_per_evaluation
        self.character = character
        self.session_id = session_id or datetime.now().strftime("%Y%m%d_%H%M%S")

        self.population: List[ParamSet] = []
        self.generation = 0
        self.history: List[dict] = []
        self.best_ever_score = 0.0
        self.best_ever_params: Optional[dict] = None
        self.degradation_streak = 0
        self.MAX_DEGRADATION = 3

        # Session directory
        self.session_dir = SESSIONS_DIR / f"{character}_{self.session_id}"
        self.session_dir.mkdir(parents=True, exist_ok=True)

    # ── Initialization ─────────────────────────────────────────────────────

    def initialize(self, seed_params_path: Optional[Path] = None):
        """Initialize population from base params + random mutations."""
        path = seed_params_path or PARAMS_PATH
        with open(path, "r", encoding="utf-8") as f:
            base_params = json.load(f)

        for i in range(self.population_size):
            params = copy.deepcopy(base_params)
            if i > 0:  # Keep first individual as baseline
                self._mutate_random_genes(params, scale=1.0)
            self.population.append(ParamSet(id=i, params=params, generation=0))

        self._log(f"Initialized population of {self.population_size}")

    # ── Mutation ───────────────────────────────────────────────────────────

    def _mutate_random_genes(self, params: dict, scale: float = 0.3):
        """Randomly mutate ~30% of genes with Gaussian noise."""
        genes = list(GENE_SPACE.keys())
        n_mutate = max(1, int(len(genes) * 0.3))
        for gene_path in random.sample(genes, n_mutate):
            current = _get_nested(params, gene_path)
            lo, hi = GENE_SPACE[gene_path]
            sigma = (hi - lo) * scale * 0.25
            new_val = current + np.random.normal(0, sigma)
            new_val = max(lo, min(hi, new_val))
            _set_nested(params, gene_path, new_val)

    # ── Crossover ──────────────────────────────────────────────────────────

    def _crossover(self, params_a: dict, params_b: dict) -> dict:
        """Uniform crossover: each gene 50% from parent A, 50% from parent B."""
        result = copy.deepcopy(params_a)
        for gene_path in GENE_SPACE:
            if random.random() < 0.5:
                _set_nested(result, gene_path, _get_nested(params_b, gene_path))
        return result

    # ── Selection ──────────────────────────────────────────────────────────

    def _tournament_select(self, k: int = 3) -> ParamSet:
        candidates = random.sample(self.population, min(k, len(self.population)))
        return max(candidates, key=lambda p: p.score)

    # ── Evaluation ─────────────────────────────────────────────────────────

    def evaluate(self, param_set: ParamSet):
        """Run N games with these params, return fitness score."""
        self._log(f"Evaluating individual #{param_set.id} (gen {param_set.generation})...")

        # Write params (no rebuild needed — params.json is loaded at runtime)
        self._write_params(param_set.params)

        # Run batch test
        results = self._run_batch(self.runs_per_evaluation)

        # ── Interruption analysis ──
        param_set.stuck_runs = results.get("stuck_runs", 0)
        param_set.valid_runs = results.get("valid_runs", 0)
        param_set.interruption_categories = results.get("interruption_categories", {})

        # Log interruptions prominently
        if param_set.stuck_runs > 0:
            self._log(f"  ⚠️  {param_set.stuck_runs}/{self.runs_per_evaluation} runs INTERRUPTED:")
            for cat, count in sorted(param_set.interruption_categories.items(),
                                      key=lambda x: -x[1]):
                self._log(f"       {cat}: {count}")

        # Compute fitness: HP-loss-based (lower damage/battle = higher fitness)
        # With _debugMaxHp=999, every run reaches the boss — so we optimize for
        # minimal HP loss per battle instead of win/loss.
        # Formula: 0 dmg/battle = 1.0, 20 dmg/battle ≈ 0.5, 100 dmg/battle ≈ 0.09
        param_set.total_runs = self.runs_per_evaluation
        if param_set.valid_runs > 0:
            param_set.act1_boss_kills = results.get("act1_boss_kills", 0)
            avg_dmg = results.get("avg_damage_per_battle", 99)
            param_set.avg_damage_per_battle = avg_dmg
            param_set.score = 1.0 / (1.0 + avg_dmg / 20.0)
        else:
            # All runs were interrupted — severe penalty
            param_set.score = 0.0
            self._log(f"  🔴 ALL runs interrupted! Fitness = 0. Check game/mod stability.")

        param_set.avg_floor_reached = results.get("avg_floor", 0.0)
        param_set.death_categories = results.get("death_categories", {})

        avg_dmg_str = f"{param_set.avg_damage_per_battle:.1f}"
        self._log(f"  Fitness: {param_set.score:.4f} (avg {avg_dmg_str} dmg/battle, "
                  f"{param_set.act1_boss_kills}/{param_set.valid_runs} boss kills) "
                  f"avg_floor={param_set.avg_floor_reached:.1f} "
                  f"stuck={param_set.stuck_runs}")

        return param_set

    def _write_params(self, params: dict):
        """Write params to params.json (also save backup)."""
        with open(PARAMS_PATH, "w", encoding="utf-8") as f:
            json.dump(params, f, indent=2, ensure_ascii=False)

    def _build_mod(self) -> bool:
        """Build the mod DLL. Returns True on success."""
        result = subprocess.run(
            ["dotnet", "build", str(MOD_DIR / "TokenSpire2.csproj"), "-c", "Debug", "-o", str(MOD_DIR)],
            capture_output=True, text=True, cwd=str(MOD_DIR), timeout=120,
        )
        return result.returncode == 0

    def _run_batch(self, n_runs: int) -> dict:
        """Run N single-run batch tests, analyzing interruptions between each.
        Returns summary dict with interruption analysis."""
        from collections import Counter
        import random, string

        all_per_run = []
        interruption_counter = Counter()
        stuck_count = 0
        valid_run_count = 0
        act1_boss_kills = 0
        total_dmg_taken = 0
        total_valid_battles = 0
        all_floors = []

        for i in range(n_runs):
            # Generate a unique seed for this run
            seed = ''.join(random.choice(string.hexdigits.upper()) for _ in range(16))
            run_num = i + 1

            self._log(f"  Run {run_num}\{n_runs} (seed={seed[:12]}...)...")

            # Clean stale batch_summary.json before launching
            batch_summary_path = MOD_DIR / "batch_runs" / "batch_summary.json"
            if batch_summary_path.exists():
                try:
                    batch_summary_path.unlink()
                except Exception:
                    pass

            try:
                result = subprocess.run(
                    [sys.executable, str(BATCH_RUNNER),
                     "--character", self.character,
                     "--run-num", str(run_num),
                     "--seed", seed,
                     "--max-time", "600",
                     "--stuck-timeout", "200",
                     "--no-build"],
                    capture_output=True, text=True, cwd=str(MOD_DIR),
                    timeout=900,  # 15 min max per run
                )

                # Read the per-run summary — prefer batch_summary.json if
                # batch_runner wrote it, otherwise fall back to the archive's
                # summary.json.
                run_summary = None
                if batch_summary_path.exists():
                    try:
                        data = json.loads(batch_summary_path.read_text(encoding="utf-8"))
                        per_run = data.get("per_run", [])
                        if per_run:
                            run_summary = per_run[0]
                    except Exception as e:
                        self._log(f"    Warning: failed to parse batch_summary: {e}")

                if run_summary is None:
                    # Fall back to per-run summary in the archive directory
                    archive_summary = MOD_DIR / "batch_runs" / f"run_{run_num:03d}" / "summary.json"
                    if archive_summary.exists():
                        try:
                            run_summary = json.loads(archive_summary.read_text(encoding="utf-8"))
                        except Exception as e:
                            self._log(f"    Warning: failed to parse archive summary: {e}")

                if run_summary is not None:
                    all_per_run.append(run_summary)
                    termination = run_summary.get("termination", "?")

                    # Classify
                    if termination.startswith("STUCK_"):
                        stuck_count += 1
                        interruption_counter[termination] += 1
                        self._log(f"    ⚠️  STUCK: {termination}")
                    elif termination in ("TIMEOUT_NO_BATTLES", "TIMEOUT_STUCK",
                                         "TIMEOUT_MAX", "PROCESS_DIED",
                                         "LAUNCH_FAILED", "ZERO_BATTLES"):
                        stuck_count += 1
                        interruption_counter[termination] += 1
                        self._log(f"    ⚠️  Interrupted: {termination}")
                    else:
                        valid_run_count += 1
                        # Count boss kills
                        encounters = run_summary.get("encounters", [])
                        for e in encounters:
                            if "boss" in e.lower() or "guardian" in e.lower() or \
                               "slime" in e.lower() or "hexaghost" in e.lower():
                                act1_boss_kills += 1
                                break
                        # Track floors and HP loss
                        all_floors.append(run_summary.get("total_battles", 0))
                        battles = run_summary.get("total_battles", 0)
                        dmg = run_summary.get("total_damage_taken", 0)
                        total_dmg_taken += dmg
                        total_valid_battles += max(1, battles)
                        wins = run_summary.get("wins", 0)
                        self._log(f"    OK: {wins}W/{battles-wins}L ({battles} battles) "
                                  f"dmg={dmg} "
                                  f"term={termination[:30]}")
                else:
                    self._log(f"    Warning: no summary found for run {run_num} (subprocess may have failed)")
                    stuck_count += 1
                    interruption_counter["BATCH_RUNNER_FAILED"] += 1

            except subprocess.TimeoutExpired:
                self._log(f"    ⚠️  Run {run_num} timed out (900s)")
                stuck_count += 1
                interruption_counter["OPTIMIZER_TIMEOUT"] += 1
            except Exception as e:
                self._log(f"    ⚠️  Run {run_num} error: {e}")
                stuck_count += 1
                interruption_counter["OPTIMIZER_ERROR"] += 1

            # Brief cooldown between runs (let Steam/game fully exit)
            if i < n_runs - 1:
                time.sleep(5)

        # ── Build aggregate summary ──
        avg_dmg_per_battle = total_dmg_taken / max(1, total_valid_battles)
        summary = {
            "total_runs": n_runs,
            "act1_boss_kills": act1_boss_kills,
            "avg_floor": sum(all_floors) / len(all_floors) if all_floors else 0.0,
            "death_categories": {},
            "interruption_categories": dict(interruption_counter),
            "stuck_runs": stuck_count,
            "valid_runs": valid_run_count,
            "total_damage_taken": total_dmg_taken,
            "total_valid_battles": total_valid_battles,
            "avg_damage_per_battle": avg_dmg_per_battle,
        }

        # Death analysis for normally-terminated runs
        for run in all_per_run:
            if run.get("termination") == "NORMAL_DEATH":
                boss_name = run.get("death_boss", "unknown")
                summary["death_categories"][boss_name] = \
                    summary["death_categories"].get(boss_name, 0) + 1

        return summary

    # ── Evolution ──────────────────────────────────────────────────────────

    def evolve_one_generation(self):
        """Run one generation of evolution."""
        self.generation += 1
        self._log(f"\n{'='*60}")
        self._log(f"GENERATION {self.generation}")
        self._log(f"{'='*60}")

        # Evaluate unevaluated individuals
        for p in self.population:
            if p.score == 0.0 and p.total_runs == 0:
                self.evaluate(p)

        # Sort by fitness
        self.population.sort(key=lambda p: p.score, reverse=True)
        best = self.population[0]

        # Record history
        avg_score = np.mean([p.score for p in self.population if p.total_runs > 0])
        total_stuck = sum(p.stuck_runs for p in self.population)
        total_valid = sum(p.valid_runs for p in self.population)
        total_runs_all = sum(p.total_runs for p in self.population)

        self.history.append({
            "generation": self.generation,
            "best_score": best.score,
            "avg_score": float(avg_score),
            "total_stuck_runs": total_stuck,
            "total_valid_runs": total_valid,
            "total_runs": total_runs_all,
            "stuck_rate": total_stuck / max(1, total_runs_all),
            "best_genes": _extract_gene_values(best.params),
        })

        self._log(f"Best: {best.score:.1%}  Avg: {avg_score:.1%}  "
                  f"Stuck: {total_stuck}/{total_runs_all} ({total_stuck/max(1,total_runs_all):.0%})")

        # ── Interruption health check ──
        stuck_rate = total_stuck / max(1, total_runs_all)
        if stuck_rate > 0.5:
            self._log(f"🔴 CRITICAL: {stuck_rate:.0%} of all runs are interrupted!")
            self._log(f"   Check stuck_diagnostics.json files in batch_runs/run_*/")
            self._log(f"   Common causes: overlay not dismissed, solver infinite loop, "
                      f"animation lock, Steam disconnect")
        elif stuck_rate > 0.25:
            self._log(f"🟡 WARNING: {stuck_rate:.0%} interruption rate — monitor closely")

        # Safety: check for degradation
        if best.score > self.best_ever_score:
            self.best_ever_score = best.score
            self.best_ever_params = copy.deepcopy(best.params)
            self.degradation_streak = 0
            self._save_best_params(best)
        elif best.score < self.best_ever_score * 0.7:
            self.degradation_streak += 1
            if self.degradation_streak >= self.MAX_DEGRADATION:
                self._log("⚠️  Degradation detected — rolling back to best params")
                best.params = copy.deepcopy(self.best_ever_params)
                self.degradation_streak = 0

        # Create next generation
        new_population = [copy.deepcopy(self.population[i])
                         for i in range(self.elite_count)]

        while len(new_population) < self.population_size:
            parent1 = self._tournament_select()
            parent2 = self._tournament_select()

            if random.random() < self.crossover_rate:
                child_params = self._crossover(parent1.params, parent2.params)
            else:
                child_params = copy.deepcopy(parent1.params)

            if random.random() < self.mutation_rate:
                self._mutate_random_genes(child_params, self.mutation_scale)

            new_population.append(ParamSet(
                id=len(new_population),
                params=child_params,
                generation=self.generation,
            ))

        self.population = new_population

    # ── Main loop ──────────────────────────────────────────────────────────

    def run(self, target_score: float = 0.7, max_generations: int = 50):
        """Main optimization loop."""
        # ── Pre-flight checks ──
        dll_path = MOD_DIR / "TokenSpire2.dll"
        if not dll_path.exists():
            self._log("🔴 TokenSpire2.dll not found! Building now...")
            if not self._build_mod():
                self._log("🔴 Build failed! Please check compilation errors.")
                self._log("   Run: dotnet build TokenSpire2.csproj -c Debug -o .")
                return
            self._log("✅ Build succeeded.")

        # Check if DLL is writable (not locked by Steam)
        try:
            with open(dll_path, "ab") as f:
                pass  # Test append (doesn't modify content)
        except PermissionError:
            self._log("🔴 TokenSpire2.dll is LOCKED by another process (Steam/game).")
            self._log("   Please close the game and restart Steam, then re-run.")
            self._log("   If the game is not running, restart Steam to release the file lock.")
            return

        if not BATCH_RUNNER.exists():
            self._log(f"🔴 Batch runner not found: {BATCH_RUNNER}")
            return

        self._log("✅ Pre-flight checks passed.")
        self._log(f"   DLL: {dll_path}")
        self._log(f"   Batch runner: {BATCH_RUNNER}")

        self.initialize()
        self._save_session_config()

        while self.generation < max_generations:
            self.evolve_one_generation()

            best = self.population[0]
            if best.score >= target_score:
                self._log(f"\n✅ Target reached! {best.score:.1%} >= {target_score:.1%}")
                self._save_best_params(best)
                break

        self._save_history()
        self._log(f"\nOptimization complete. Best score: {self.best_ever_score:.1%}")
        self._log(f"Results saved to: {self.session_dir}")

    # ── Persistence ────────────────────────────────────────────────────────

    def _save_session_config(self):
        config = {
            "character": self.character,
            "session_id": self.session_id,
            "population_size": self.population_size,
            "runs_per_evaluation": self.runs_per_evaluation,
            "elite_count": self.elite_count,
            "mutation_rate": self.mutation_rate,
            "mutation_scale": self.mutation_scale,
            "crossover_rate": self.crossover_rate,
            "target_score": 0.7,
            "gene_space": {k: list(v) for k, v in GENE_SPACE.items()},
        }
        (self.session_dir / "config.json").write_text(
            json.dumps(config, indent=2, ensure_ascii=False), encoding="utf-8")

    def _save_best_params(self, param_set: ParamSet):
        path = self.session_dir / f"best_params_gen{self.generation}_score{param_set.score:.3f}.json"
        with open(path, "w", encoding="utf-8") as f:
            json.dump({
                "generation": self.generation,
                "score": param_set.score,
                "genes": _extract_gene_values(param_set.params),
                "full_params": param_set.params,
            }, f, indent=2, ensure_ascii=False)
        self._log(f"Best params saved: {path.name}")

    def _save_history(self):
        path = self.session_dir / "history.json"
        with open(path, "w", encoding="utf-8") as f:
            json.dump(self.history, f, indent=2, ensure_ascii=False)

    def _log(self, msg: str):
        ts = datetime.now().strftime("%H:%M:%S")
        print(f"[{ts}] {msg}", flush=True)


# ═══════════════════════════════════════════════════════════════════════════════
# SENSITIVITY ANALYZER
# ═══════════════════════════════════════════════════════════════════════════════

class SensitivityAnalyzer:
    """One-at-a-time parameter sensitivity analysis."""

    def __init__(self, n_runs: int = 3, character: str = "IRONCLAD"):
        self.n_runs = n_runs
        self.character = character

    def analyze(self, base_params_path: Optional[Path] = None) -> List[dict]:
        path = base_params_path or PARAMS_PATH
        with open(path, "r", encoding="utf-8") as f:
            base_params = json.load(f)

        results = []
        for gene_path, (lo, hi) in GENE_SPACE.items():
            base_val = _get_nested(base_params, gene_path)
            scores = {}
            for label, test_val in [("low", lo), ("base", base_val), ("high", hi)]:
                params = copy.deepcopy(base_params)
                _set_nested(params, gene_path, test_val)
                self._write_params(params)
                self._build_mod()
                score = self._evaluate_fast(self.n_runs)
                scores[label] = score

            sensitivity = (scores["high"] - scores["low"]) / (hi - lo) if hi != lo else 0
            results.append({
                "param": gene_path,
                "sensitivity": sensitivity,
                "scores": scores,
                "impact": "HIGH" if abs(sensitivity) > 0.05 else "MEDIUM" if abs(sensitivity) > 0.01 else "LOW",
            })

        # Restore base params
        self._write_params(base_params)
        return sorted(results, key=lambda r: abs(r["sensitivity"]), reverse=True)

    def _write_params(self, params: dict):
        with open(PARAMS_PATH, "w", encoding="utf-8") as f:
            json.dump(params, f, indent=2, ensure_ascii=False)

    def _build_mod(self) -> bool:
        result = subprocess.run(
            ["dotnet", "build", str(MOD_DIR / "TokenSpire2.csproj"), "-c", "Debug", "-o", str(MOD_DIR)],
            capture_output=True, text=True, cwd=str(MOD_DIR), timeout=120,
        )
        return result.returncode == 0

    def _evaluate_fast(self, n_runs: int) -> float:
        """Quick evaluation - just check Act1 boss encounter presence."""
        try:
            result = subprocess.run(
                [sys.executable, str(BATCH_RUNNER),
                 "--runs", str(n_runs), "--character", self.character,
                 "--max-time", "600", "--stuck-timeout", "200", "--no-build"],
                capture_output=True, text=True, cwd=str(MOD_DIR), timeout=n_runs * 900,
            )
            # Count Act1 boss mentions in output
            output = result.stdout + result.stderr
            boss_hints = output.lower().count("guardian") + output.lower().count("slime boss") + \
                        output.lower().count("hexaghost") + output.lower().count("boss")
            floor_hints = output.count("floor 16") + output.count("floor 17")
            return min(1.0, (boss_hints * 0.3 + floor_hints * 0.2) / max(1, n_runs))
        except Exception:
            return 0.0


# ── CLI ────────────────────────────────────────────────────────────────────────

from datetime import datetime

if __name__ == "__main__":
    import argparse
    parser = argparse.ArgumentParser(description="TokenSpire2 Genetic Optimizer")
    parser.add_argument("--character", default="IRONCLAD")
    parser.add_argument("--target", type=float, default=0.7)
    parser.add_argument("--generations", type=int, default=50)
    parser.add_argument("--population", type=int, default=20)
    parser.add_argument("--runs-per-eval", type=int, default=10)
    parser.add_argument("--sensitivity", action="store_true", help="Run sensitivity analysis instead")
    args = parser.parse_args()

    if args.sensitivity:
        sa = SensitivityAnalyzer(n_runs=3, character=args.character)
        results = sa.analyze()
        print("\n=== SENSITIVITY ANALYSIS ===\n")
        for r in results:
            print(f"  [{r['impact']:6s}] {r['param']:<60s} sensitivity={r['sensitivity']:+.4f}")
        print()
    else:
        opt = GeneticOptimizer(
            population_size=args.population,
            runs_per_evaluation=args.runs_per_eval,
            character=args.character,
        )
        opt.run(target_score=args.target, max_generations=args.generations)
