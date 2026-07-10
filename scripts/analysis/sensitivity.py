#!/usr/bin/env python3
"""
参数敏感性分析模块

每次改变一个参数(one-at-a-time)，运行N局测试，计算参数变化对胜率的影响斜率。
与 optimizer.py 集成，也可独立运行。

用法:
    python analysis/sensitivity.py --n-runs 3 --output sensitivity_report.json
"""

import json, copy, subprocess, sys
from pathlib import Path
from typing import Dict, List, Optional, Tuple
from collections import defaultdict

# Import from sibling optimizer
sys.path.insert(0, str(Path(__file__).parent.parent))
from optimizer import (
    MOD_DIR, PARAMS_PATH, BATCH_RUNNER,
    GENE_SPACE, _get_nested, _set_nested
)


class SensitivityAnalyzer:
    """One-at-a-time sensitivity analysis with detailed reporting."""

    def __init__(self, n_runs: int = 3, character: str = "IRONCLAD"):
        self.n_runs = n_runs
        self.character = character
        self.MOD_DIR = MOD_DIR
        self.PARAMS_PATH = PARAMS_PATH

    def analyze(self, params_to_test: Optional[List[str]] = None) -> List[dict]:
        """Run sensitivity analysis on specified params (or all)."""
        with open(self.PARAMS_PATH, "r", encoding="utf-8") as f:
            base_params = json.load(f)

        genes_to_test = params_to_test or list(GENE_SPACE.keys())
        results = []

        print(f"Analyzing {len(genes_to_test)} parameters with {self.n_runs} runs each...")

        for i, gene_path in enumerate(genes_to_test):
            if gene_path not in GENE_SPACE:
                print(f"  Skipping {gene_path} (not in GENE_SPACE)")
                continue

            lo, hi = GENE_SPACE[gene_path]
            base_val = _get_nested(base_params, gene_path)

            # Quick evaluation at 3 points
            scores = {}
            runs = {}
            for label, test_val in [("min", lo), ("base", base_val), ("max", hi)]:
                if label != "base":
                    params = copy.deepcopy(base_params)
                    _set_nested(params, gene_path, test_val)
                    self._write_params(params)
                    self._build_mod()

                label_clean = label
                score, detail = self._evaluate_fast(self.n_runs)
                scores[label_clean] = score
                runs[label_clean] = detail

                print(f"  [{i+1}/{len(genes_to_test)}] {gene_path} @ {label}={test_val:.3f} → score={score:.3f}")

            # Sensitivity = (max_score - min_score) / range_size
            sensitivity = (scores["max"] - scores["min"]) / (hi - lo) if hi != lo else 0
            # Change sensitivity = (max_score - base_score)
            sensitivity_vs_base = (scores["max"] - scores["base"]) / (hi - base_val) if hi != base_val else 0

            if abs(sensitivity_vs_base) > abs(sensitivity) * 5:
                best_sensitivity = sensitivity_vs_base
            else:
                best_sensitivity = sensitivity

            result = {
                "param": gene_path,
                "range": [lo, hi],
                "base_value": base_val,
                "sensitivity": best_sensitivity,
                "sensitivity_vs_base": sensitivity_vs_base,
                "scores_at_min": scores["min"],
                "scores_at_base": scores["base"],
                "scores_at_max": scores["max"],
                "abs_impact": abs(scores["max"] - scores["min"]),
                "impact": "HIGH" if abs(scores["max"] - scores["min"]) > 0.15 else
                          "MEDIUM" if abs(scores["max"] - scores["min"]) > 0.05 else "LOW",
                "direction": "increase" if sensitivity > 0 else "decrease",
                "detail": runs,
            }
            results.append(result)

        # Restore base params
        self._write_params(base_params)

        return sorted(results, key=lambda r: abs(r["sensitivity"]), reverse=True)

    def generate_report(self, results: List[dict]) -> str:
        """Generate a markdown report string."""
        lines = [
            f"# 参数敏感性分析报告",
            f"",
            f"**角色**: {self.character}",
            f"**每组参数测试局数**: {self.n_runs}",
            f"**分析参数数**: {len(results)}",
            f"",
            f"## 结果概览",
            f"",
            f"| 影响 | 参数 | 灵敏度 | 方向 | 建议范围 |",
            f"|------|------|--------|------|----------|",
        ]

        for r in results[:20]:  # Top 20
            param_short = r["param"].split(".")[-1]
            direction = "↑" if r["sensitivity"] > 0 else "↓"
            suggested = f"[{r['base_value']:.2f}→{r['base_value']*1.3:.2f}]" if r["sensitivity"] > 0 else f"[{r['base_value']*0.7:.2f}→{r['base_value']:.2f}]"
            lines.append(f"| {r['impact']} | {param_short} | {r['sensitivity']:+.4f} | {direction} | {suggested} |")

        lines.extend([
            f"",
            f"## 高影响参数",
        ])

        high = [r for r in results if r["impact"] == "HIGH"]
        if high:
            for r in high:
                lines.append(f"### {r['param']}")
                lines.append(f"- **灵敏度**: {r['sensitivity']:+.4f}")
                lines.append(f"- **范围**: [{r['range'][0]:.2f}, {r['range'][1]:.2f}]")
                lines.append(f"- **基线值**: {r['base_value']:.2f}")
                lines.append(f"- **分数变化**: min={r['scores_at_min']:.2f}, base={r['scores_at_base']:.2f}, max={r['scores_at_max']:.2f}")
                lines.append(f"- **建议**: {'增大' if r['sensitivity'] > 0 else '减小'}该参数")
                lines.append(f"")
        else:
            lines.append("无高影响参数。")

        return "\n".join(lines)

    def _write_params(self, params: dict):
        with open(self.PARAMS_PATH, "w", encoding="utf-8") as f:
            json.dump(params, f, indent=2, ensure_ascii=False)

    def _build_mod(self) -> bool:
        result = subprocess.run(
            ["dotnet", "build",
             str(self.MOD_DIR / "TokenSpire2.csproj"),
             "-c", "Debug", "-o", str(self.MOD_DIR)],
            capture_output=True, text=True,
            cwd=str(self.MOD_DIR), timeout=120,
        )
        return result.returncode == 0

    def _evaluate_fast(self, n_runs: int) -> Tuple[float, dict]:
        """Quick evaluation using batch_runner. Returns (score, detail)."""
        detail = {"runs_attempted": n_runs, "runs_completed": 0, "boss_encounters": 0}
        try:
            result = subprocess.run(
                [sys.executable, str(BATCH_RUNNER),
                 "--runs", str(n_runs),
                 "--character", self.character,
                 "--max-time", "600",
                 "--stuck-timeout", "200",
                 "--no-build"],
                capture_output=True, text=True,
                cwd=str(self.MOD_DIR), timeout=n_runs * 900,
            )

            output = result.stdout + result.stderr
            detail["runs_completed"] = output.count("Run ") - output.count("Run completed")
            detail["boss_encounters"] = (
                output.lower().count("guardian") +
                output.lower().count("slime boss") +
                output.lower().count("hexaghost")
            )

            # Score = boss encounter rate (proxy for Act1 success)
            boss_score = min(1.0, detail["boss_encounters"] / max(1, n_runs))

            # Also try to read batch summary
            summary_path = self.MOD_DIR / "batch_runs" / "batch_summary.json"
            if summary_path.exists():
                try:
                    data = json.loads(summary_path.read_text(encoding="utf-8"))
                    if "act1_boss_kills" in data:
                        return data["act1_boss_kills"] / max(1, data.get("total_runs", n_runs)), detail
                except Exception:
                    pass

            return boss_score, detail

        except Exception as e:
            detail["error"] = str(e)
            return 0.0, detail


# ── CLI ────────────────────────────────────────────────────────────────────────

if __name__ == "__main__":
    import argparse
    from datetime import datetime

    parser = argparse.ArgumentParser(description="TokenSpire2 Sensitivity Analyzer")
    parser.add_argument("--character", default="IRONCLAD")
    parser.add_argument("--n-runs", type=int, default=3)
    parser.add_argument("--output", type=str, default=None)
    parser.add_argument("--params", type=str, nargs="*", default=None,
                       help="Specific params to test (dot-separated paths)")
    parser.add_argument("--json-only", action="store_true",
                       help="Output JSON only, no markdown report")
    args = parser.parse_args()

    analyzer = SensitivityAnalyzer(n_runs=args.n_runs, character=args.character)

    if args.params:
        # Filter GENE_SPACE to only matching params
        test_params = [p for p in GENE_SPACE if any(arg in p for arg in args.params)]
        if not test_params:
            print(f"No params matched filters: {args.params}")
            sys.exit(1)
        print(f"Filtered to {len(test_params)} parameters: {args.params}")
    else:
        test_params = None

    results = analyzer.analyze(params_to_test=test_params)

    if args.json_only:
        json_output = [{
            "param": r["param"],
            "sensitivity": r["sensitivity"],
            "impact": r["impact"],
            "direction": r["direction"],
            "scores": {"min": r["scores_at_min"], "base": r["scores_at_base"], "max": r["scores_at_max"]},
        } for r in results]
        print(json.dumps(json_output, indent=2, ensure_ascii=False))
    else:
        report = analyzer.generate_report(results)
        print(report)

    # Save to file
    out_path = args.output
    if not out_path:
        ts = datetime.now().strftime("%Y%m%d_%H%M%S")
        out_path = str(MOD_DIR / "optimization_sessions" / f"sensitivity_{args.character}_{ts}.json")

    Path(out_path).parent.mkdir(parents=True, exist_ok=True)
    with open(out_path, "w", encoding="utf-8") as f:
        json.dump(results, f, indent=2, ensure_ascii=False)
    print(f"\n报告已保存: {out_path}")
