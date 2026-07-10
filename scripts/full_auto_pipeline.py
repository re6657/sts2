#!/usr/bin/env python3
"""
全自动双层优化流水线 — TokenSpire2

外层循环: 能力审计 → 发现缺失 → 生成修复方案 → 等待修复（或自动修复）
内层循环: 参数优化 → 遗传算法 → 目标收敛

用法:
    python scripts/full_auto_pipeline.py --character IRONCLAD --target 0.7 --max-hours 48

与 auto_optimize.py 的区别:
    auto_optimize.py: 只做参数优化（假设所有能力已完整）
    full_auto_pipeline.py: 先审计能力完整性 → 再参数优化 → 循环
"""

import argparse, time, json, sys
from datetime import datetime
from pathlib import Path
from optimizer import GeneticOptimizer, SensitivityAnalyzer, GENE_SPACE
from capability_auditor import CapabilityAuditor


MOD_DIR = Path(r"E:\SteamLibrary\steamapps\common\Slay the Spire 2\mods\TokenSpire2")
PARAMS_PATH = MOD_DIR / "params.json"


def main():
    parser = argparse.ArgumentParser(description="TokenSpire2 Full Auto Pipeline")
    parser.add_argument("--character", default="IRONCLAD",
                       choices=["IRONCLAD", "SILENT", "DEFECT", "REGENT", "NECROBINDER"])
    parser.add_argument("--target", type=float, default=0.7)
    parser.add_argument("--max-hours", type=float, default=48)
    parser.add_argument("--runs-per-eval", type=int, default=10)
    parser.add_argument("--population", type=int, default=20)
    parser.add_argument("--generations", type=int, default=50)
    parser.add_argument("--skip-audit", action="store_true",
                       help="Skip capability audit and go straight to optimization")
    parser.add_argument("--audit-only", action="store_true",
                       help="Only run the capability audit, no optimization")
    args = parser.parse_args()

    if not PARAMS_PATH.exists():
        print(f"ERROR: params.json not found at {PARAMS_PATH}")
        sys.exit(1)

    session_id = datetime.now().strftime("%Y%m%d_%H%M%S")
    session_dir = MOD_DIR / "optimization_sessions" / f"{args.character}_{session_id}"
    session_dir.mkdir(parents=True, exist_ok=True)

    print(f"""
╔══════════════════════════════════════════════════╗
║  TokenSpire2 全自动双层优化流水线 v1.0          ║
╠══════════════════════════════════════════════════╣
║  角色:       {args.character:<33s} ║
║  目标胜率:   {args.target:.0%}{'':<32s} ║
║  最大时间:   {args.max_hours:.0f}h{'':<32s} ║
║  会话目录:   {str(session_dir):<33s} ║
╚══════════════════════════════════════════════════╝
    """)

    # ── Save pipeline config ──
    config = vars(args).copy()
    config["session_id"] = session_id
    (session_dir / "pipeline_config.json").write_text(
        json.dumps(config, indent=2, ensure_ascii=False), encoding="utf-8")

    # ═══════════════════════════════════════════════════════════════════════
    # STEP 1: CAPABILITY AUDIT
    # ═══════════════════════════════════════════════════════════════════════

    if not args.skip_audit:
        print(f"\n{'='*70}")
        print(f"🔍 STEP 1: 功能完整性审计")
        print(f"{'='*70}\n")

        auditor = CapabilityAuditor()
        report = auditor.audit()
        auditor.print_report(report)

        # Save audit
        audit_path = session_dir / "capability_audit.json"
        with open(audit_path, "w", encoding="utf-8") as f:
            serializable = {
                "score": report["score"],
                "generated_at": report["generated_at"],
                "missing_capabilities": [
                    {k: str(v) if not isinstance(v, (str, int, float, bool, list, dict, type(None)))
                       else v for k, v in vars(g).items()}
                    for g in report["missing_capabilities"]
                ],
                "never_chosen_capabilities": [
                    {k: str(v) if not isinstance(v, (str, int, float, bool, list, dict, type(None)))
                       else v for k, v in vars(g).items()}
                    for g in report["never_chosen_capabilities"]
                ],
                "suspicious_patterns": [
                    {k: str(v) if not isinstance(v, (str, int, float, bool, list, dict, type(None)))
                       else v for k, v in vars(g).items()}
                    for g in report["suspicious_patterns"]
                ],
            }
            json.dump(serializable, f, indent=2, ensure_ascii=False, default=str)

        # Save fix guide to mod root
        fix_path = MOD_DIR / "NEXT_FIXES.md"
        fix_path.write_text(report["fix_guide_markdown"], encoding="utf-8")

        # Check for critical gaps
        critical = [g for g in report.get("missing_capabilities", [])
                    if g.severity == "CRITICAL"]

        if critical:
            print(f"\n{'='*70}")
            print(f"⛔ 发现 {len(critical)} 个严重能力缺失")
            print(f"{'='*70}")
            print(f"\n参数优化无法修复这些问题。请先修复代码缺陷。")
            print(f"\n修复指南: {fix_path}")
            print(f"\n修复完成后运行:")
            print(f"  python scripts/full_auto_pipeline.py --character {args.character}")
            print(f"\n或跳过审计直接优化（不推荐）:")
            print(f"  python scripts/full_auto_pipeline.py --character {args.character} --skip-audit")

            if args.audit_only:
                print(f"\n审计完成（--audit-only 模式）。")
                return

            # Don't proceed to optimization with critical gaps
            print(f"\n❌ 优化未启动 — 请先修复能力缺失。")
            return

        print(f"\n✅ 审计通过 — 无严重能力缺失")
        print(f"   功能完整度: {report['score']:.0f}/100")

    if args.audit_only:
        print(f"\n审计完成（--audit-only 模式）。")
        return

    # ═══════════════════════════════════════════════════════════════════════
    # STEP 2: SENSITIVITY ANALYSIS (optional but recommended)
    # ═══════════════════════════════════════════════════════════════════════

    print(f"\n{'='*70}")
    print(f"📊 STEP 2: 参数敏感性分析")
    print(f"{'='*70}\n")

    try:
        sa = SensitivityAnalyzer(n_runs=3, character=args.character)
        sens_results = sa.analyze()

        high_impact = [r for r in sens_results if r["impact"] == "HIGH"]
        print(f"高影响参数 ({len(high_impact)} 个):")
        for r in high_impact[:10]:
            direction = "↑" if r["sensitivity"] > 0 else "↓"
            print(f"  {direction} {r['param']:<50s} sens={r['sensitivity']:+.4f}")

        # Save sensitivity
        sens_path = session_dir / "sensitivity.json"
        with open(sens_path, "w", encoding="utf-8") as f:
            json.dump(sens_results, f, indent=2, ensure_ascii=False)
    except Exception as e:
        print(f"⚠️  敏感性分析跳过（错误: {e}）")

    # ═══════════════════════════════════════════════════════════════════════
    # STEP 3: GENETIC OPTIMIZATION
    # ═══════════════════════════════════════════════════════════════════════

    print(f"\n{'='*70}")
    print(f"🧬 STEP 3: 遗传算法参数优化")
    print(f"{'='*70}\n")

    opt = GeneticOptimizer(
        population_size=args.population,
        elite_count=max(4, args.population // 5),
        runs_per_evaluation=args.runs_per_eval,
        mutation_rate=0.2,
        mutation_scale=0.3,
        crossover_rate=0.7,
        character=args.character,
        session_id=session_id,
    )

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

            # Restore best params
            with open(PARAMS_PATH, "w", encoding="utf-8") as f:
                json.dump(opt.best_ever_params, f, indent=2, ensure_ascii=False)
            print(f"最佳参数已恢复到: {PARAMS_PATH}")

        print(f"结果目录: {session_dir}")

        # ═══════════════════════════════════════════════════════════════════
        # STEP 4: RE-AUDIT
        # ═══════════════════════════════════════════════════════════════════

        if not args.skip_audit:
            print(f"\n{'='*70}")
            print(f"🔍 STEP 4: 优化后重新审计")
            print(f"{'='*70}\n")

            auditor = CapabilityAuditor()
            report2 = auditor.audit()
            auditor.print_report(report2)

            # Compare scores
            new_score = report2.get("score", 0)

            # If still below target and no critical gaps, suggest more runs
            if opt.best_ever_score < args.target:
                print(f"\n⚠️  最佳胜率 {opt.best_ever_score:.1%} < 目标 {args.target:.0%}")
                print(f"建议: 增加 --runs-per-eval 或 --generations 重新运行")
                print(f"继续命令: python scripts/full_auto_pipeline.py --character {args.character} "
                      f"--target {args.target} --runs-per-eval {args.runs_per_eval + 5} "
                      f"--generations {args.generations + 10}")
            else:
                print(f"\n🎉 目标达成! {opt.best_ever_score:.1%} >= {args.target:.0%}")


if __name__ == "__main__":
    main()
