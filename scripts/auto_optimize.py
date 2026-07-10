#!/usr/bin/env python3
"""
自动优化主循环 — TokenSpire2

用法:
    python auto_optimize.py --character IRONCLAD --target 0.7 --max-hours 48

流程:
    1. 从 params.json 加载当前参数
    2. 初始化遗传算法优化器
    3. 每代: 评估(10局) → 分析 → 进化 → 新参数
    4. 保存历史 + 最佳参数快照
    5. 重复直到 Act1 Boss 击败率 ≥ 目标 或 超时
"""

import argparse, time, json, sys
from datetime import datetime
from pathlib import Path
from optimizer import GeneticOptimizer, SensitivityAnalyzer, GENE_SPACE


MOD_DIR = Path(r"E:\SteamLibrary\steamapps\common\Slay the Spire 2\mods\TokenSpire2")
PARAMS_PATH = MOD_DIR / "params.json"


def main():
    parser = argparse.ArgumentParser(description="TokenSpire2 自动优化")
    parser.add_argument("--character", default="IRONCLAD",
                       choices=["IRONCLAD", "SILENT", "DEFECT", "REGENT", "NECROBINDER"],
                       help="角色 (默认: IRONCLAD)")
    parser.add_argument("--target", type=float, default=0.7,
                       help="目标 Act1 Boss 击败率 (默认: 0.7)")
    parser.add_argument("--max-hours", type=float, default=48,
                       help="最大运行时间/小时 (默认: 48)")
    parser.add_argument("--runs-per-eval", type=int, default=10,
                       help="每组参数评估局数 (默认: 10)")
    parser.add_argument("--population", type=int, default=20,
                       help="种群大小 (默认: 20)")
    parser.add_argument("--generations", type=int, default=50,
                       help="最大代数 (默认: 50)")
    parser.add_argument("--sensitivity", action="store_true",
                       help="先运行敏感性分析再优化")
    parser.add_argument("--sensitivity-only", action="store_true",
                       help="只运行敏感性分析")

    args = parser.parse_args()

    # ── Validate params.json exists ──
    if not PARAMS_PATH.exists():
        print(f"ERROR: params.json not found at {PARAMS_PATH}")
        print("Please create params.json first.")
        sys.exit(1)

    print(f"""
╔══════════════════════════════════════════════════╗
║     TokenSpire2 自动优化系统 v1.0               ║
╠══════════════════════════════════════════════════╣
║  角色:       {args.character:<33s} ║
║  目标胜率:   {args.target:.0%}{'':<32s} ║
║  最大时间:   {args.max_hours:.0f}h{'':<32s} ║
║  种群大小:   {args.population:<33d} ║
║  每轮局数:   {args.runs_per_eval:<33d} ║
║  基因数量:   {len(GENE_SPACE):<33d} ║
║  params.json: 已找到{'':<28s} ║
╚══════════════════════════════════════════════════╝
    """)

    # ── Sensitivity analysis (optional) ──
    if args.sensitivity or args.sensitivity_only:
        print("\n=== 敏感性分析 ===\n")
        sa = SensitivityAnalyzer(n_runs=3, character=args.character)
        results = sa.analyze()

        print(f"{'影响':<8s} {'参数':<65s} {'灵敏度':>10s}")
        print("-" * 85)
        for r in results:
            print(f"[{r['impact']:<6s}] {r['param']:<65s} {r['sensitivity']:>+10.4f}")

        # Save sensitivity report
        report_path = MOD_DIR / "optimization_sessions" / f"sensitivity_{args.character}_{datetime.now().strftime('%Y%m%d_%H%M%S')}.json"
        report_path.parent.mkdir(parents=True, exist_ok=True)
        with open(report_path, "w", encoding="utf-8") as f:
            json.dump(results, f, indent=2, ensure_ascii=False)
        print(f"\n敏感性报告已保存: {report_path}")

        if args.sensitivity_only:
            print("\n仅运行敏感性分析 — 完成。")
            return

    # ── Initialize optimizer ──
    opt = GeneticOptimizer(
        population_size=args.population,
        elite_count=max(4, args.population // 5),
        runs_per_evaluation=args.runs_per_eval,
        mutation_rate=0.2,
        mutation_scale=0.3,
        crossover_rate=0.7,
        character=args.character,
    )

    # ── Main loop ──
    start_time = time.time()
    deadline = start_time + args.max_hours * 3600

    try:
        opt.run(target_score=args.target, max_generations=args.generations)
    except KeyboardInterrupt:
        print("\n⚠️  用户中断 — 保存当前进度...")
    finally:
        opt._save_history()

        elapsed = (time.time() - start_time) / 3600
        print(f"\n总耗时: {elapsed:.1f} 小时")
        if opt.best_ever_params:
            print(f"最佳胜率: {opt.best_ever_score:.1%}")
        print(f"结果目录: {opt.session_dir}")


if __name__ == "__main__":
    main()
