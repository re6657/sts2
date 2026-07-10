#!/usr/bin/env python3
"""
功能完整性审计系统 — TokenSpire2

核心逻辑:
  对于每种游戏状态类型，检查 AI 是否考虑了所有合法动作。
  如果某个动作类型从未被考虑过 → 能力缺失（代码缺陷，优化器修不了）。
  如果被考虑但从未被选择 → 可能是参数问题（优化器可以修）。
  如果从未被考虑 → 一定是代码问题（优化器修不了）。

用法:
    python scripts/capability_auditor.py --batch-dir batch_runs/
    python scripts/capability_auditor.py --decisions-dir llm_data/decisions/
"""

import json
from pathlib import Path
from collections import defaultdict, Counter
from typing import Dict, List, Optional, Any
from dataclasses import dataclass, field


MOD_DIR = Path(r"E:\SteamLibrary\steamapps\common\Slay the Spire 2\mods\TokenSpire2")


@dataclass
class GapReport:
    status: str  # "missing", "never_chosen", "suspicious", "ok", "no_data"
    severity: str  # "CRITICAL", "WARNING", "INFO"
    state_type: str
    action: str
    times_available: int = 0
    times_considered: int = 0
    times_chosen: int = 0
    consideration_rate: float = 0.0
    selection_rate: float = 0.0
    message: str = ""
    fix_type: str = ""  # "code_change", "parameter_tuning", "needs_review"
    likely_cause: List[str] = field(default_factory=list)
    related_params: List[str] = field(default_factory=list)


class CapabilityAuditor:
    """
    自动检测 AI 是否遗漏了游戏中的可用功能。

    对于每种游戏状态类型，检查 AI 是否考虑了所有合法动作。
    - missing: AI 从未考虑此选项 → 代码缺陷
    - never_chosen: AI 考虑但从不选择 → 参数问题
    - suspicious: 选择率异常低 → 需要人工判断
    """

    # ─── 所有应该被覆盖的动作类型 ───
    EXPECTED_CAPABILITIES = {
        "REST": {
            "REST":       {"check": "always_available", "critical": False},
            "SMITH":      {"check": "always_available", "critical": True},
            "TOKE":       {"check": "if_available",     "critical": False},
            "LIFT":       {"check": "if_available",     "critical": False},
            "DIG":        {"check": "if_available",     "critical": False},
            "RECALL":     {"check": "if_available",     "critical": False},
        },
        "COMBAT": {
            "PLAY_CARD":           {"check": "always_available", "critical": True},
            "END_TURN":            {"check": "always_available", "critical": True},
            "USE_POTION":          {"check": "if_has_potions",   "critical": True},
            "USE_POTION_TARGETED": {"check": "if_has_targeted_potion", "critical": True},
            "DISCARD_POTION":      {"check": "if_potions_full",  "critical": False},
        },
        "SHOP": {
            "BUY_CARD":    {"check": "if_available", "critical": False},
            "BUY_RELIC":   {"check": "if_available", "critical": False},
            "BUY_POTION":  {"check": "if_available", "critical": False},
            "PURGE_CARD":  {"check": "if_available", "critical": True},
            # LEAVE_SHOP is handled via ShopDecider state machine:
            # When no good purchases, sets _shopLeaving=true, then clicks proceed.
            # Not an explicit option, but always handled correctly.
        },
        "CARD_REWARD": {
            "PICK_CARD":  {"check": "always_available", "critical": True},
            # SKIP_CARD: CardRewardDecider always has skip logic and logs it
            # when skipping. When it picks, SKIP isn't in options but the
            # capability IS present. Checked via skip threshold code presence.
            "BOWL_CARD":  {"check": "if_bowl_available", "critical": False},
        },
        "EVENT": {
            "CHOOSE_OPTION":  {"check": "always_available", "critical": True},
            # LEAVE_EVENT is handled via state machine (proceed button).
            # The EventDecider checks for NProceedButton before scoring options.
            # Not an explicit option, but always handled correctly.
        },
        "MAP": {
            "CHOOSE_NODE":    {"check": "always_available", "critical": True},
            "CHOOSE_BOSS":    {"check": "if_boss_available", "critical": True},
            # SCROLL_MAP is a UI action, not strategic — non-critical
        },
        "BOSS_REWARD": {
            "PICK_RELIC":    {"check": "always_available", "critical": True},
            "SKIP_RELIC":    {"check": "if_skip_allowed",  "critical": False},
        },
        "GRID_SELECT": {
            "SELECT_CARD":   {"check": "always_available", "critical": True},
            "CONFIRM":       {"check": "always_available", "critical": True},
            "CANCEL":        {"check": "if_cancel_allowed", "critical": False},
        },
        "COMBAT_REWARD": {
            "PICK_GOLD":     {"check": "always_available", "critical": True},
            "OPEN_CHEST":    {"check": "if_chest_available", "critical": False},
        },
    }

    # ─── 诊断知识库 ───
    DIAGNOSES = {
        ("REST", "SMITH"): [
            "RestDecider 没有枚举 SMITH 选项",
            "RestDecider.Decide() 中缺少对 RestOption.SMITH 的处理分支",
            "状态检测时未读取 available_rest_options 列表",
        ],
        ("COMBAT", "USE_POTION"): [
            "CombatSolver 没有在动作空间中包含药水使用",
            "Solve() 的枚举循环中缺少 TryUsePotions 步骤",
            "药水槽位状态读取错误（已知bug：HasOpenPotionSlots返回false）",
        ],
        ("COMBAT", "USE_POTION_TARGETED"): [
            "CombatSolver 没有处理需要目标的药水",
            "药水目标选择逻辑缺失",
        ],
        ("COMBAT", "DISCARD_POTION"): [
            "药水满时未考虑丢弃选项",
            "缺少对 PotionSlotFull 状态的处理",
        ],
        ("SHOP", "PURGE_CARD"): [
            "ShopDecider 没有检测 purge_available 标志",
            "删牌逻辑被错误的条件分支跳过",
            "cardRemoval?.IsStocked 可能为 false",
        ],
        ("REST", "TOKE"): [
            "RestDecider 未检测 Toke/Remove 选项",
            "RestOption 枚举中未包含 TOKE 类型",
        ],
        ("REST", "LIFT"): [
            "RestDecider 未检测 Lift/Girya 选项",
        ],
        ("REST", "DIG"): [
            "RestDecider 未检测 Dig/Excavate 选项",
        ],
        ("REST", "RECALL"): [
            "RestDecider 未检测 Recall/Key 选项",
        ],
    }

    # ─── 参数建议映射 ───
    PARAM_SUGGESTIONS = {
        ("REST", "SMITH"): [
            "rest.smith_high_hp_score",
            "rest.smith_hp_threshold",
            "rest.rest_low_hp_threshold",
        ],
        ("COMBAT", "USE_POTION"): [
            "potion.use_when_hp_below_ratio",
            "potion.min_damage_value",
        ],
        ("SHOP", "PURGE_CARD"): [
            "shop.remove_min_deck_size",
            "shop.remove_card_large_deck_score",
            "shop.remove_card_normal_score",
        ],
        ("REST", "TOKE"): [
            "rest.toke_base_score",
            "rest.toke_large_deck_bonus",
            "rest.toke_high_hp_bonus",
        ],
        ("REST", "LIFT"): [
            "rest.lift_score",
        ],
        ("REST", "DIG"): [
            "rest.dig_score",
        ],
        ("REST", "RECALL"): [
            "rest.recall_score",
        ],
    }

    # ─── 主分析入口 ───
    def audit(self, batch_dir: Optional[Path] = None,
              decisions_dir: Optional[Path] = None) -> dict:
        """
        输入：N局的决策日志 + 战斗日志
        输出：能力缺口报告
        """
        batch_dir = batch_dir or (MOD_DIR / "batch_runs")
        decisions_dir = decisions_dir or (MOD_DIR / "llm_data" / "decisions")

        # 收集所有状态记录
        state_records: Dict[str, List[dict]] = defaultdict(list)
        self._collect_from_decisions(decisions_dir, state_records)
        self._collect_from_battles(batch_dir, state_records)

        report = {
            "missing_capabilities": [],
            "never_chosen_capabilities": [],
            "suspicious_patterns": [],
            "all_results": [],
            "score": 0,
            "generated_at": "",
            "fix_guide_markdown": "",
        }

        for state_type, expected_actions in self.EXPECTED_CAPABILITIES.items():
            for action_name, config in expected_actions.items():
                result = self._check_action_coverage(
                    state_records, state_type, action_name, config
                )
                report["all_results"].append(result)

                if result.status == "missing":
                    report["missing_capabilities"].append(result)
                elif result.status == "never_chosen":
                    report["never_chosen_capabilities"].append(result)
                elif result.status == "suspicious":
                    report["suspicious_patterns"].append(result)

        # 计算完整度评分
        total_expected = sum(
            len(actions) for actions in self.EXPECTED_CAPABILITIES.values()
        )
        # 只统计有数据的
        total_with_data = sum(
            1 for r in report["all_results"]
            if r.status not in ("no_data", "not_applicable")
        )
        detected_missing = len(
            [r for r in report["missing_capabilities"] if r.severity == "CRITICAL"]
        )
        if total_with_data > 0:
            report["score"] = 100 * (1 - detected_missing / total_with_data)
        else:
            report["score"] = 100  # No data yet

        # 生成修复指南
        report["fix_guide_markdown"] = self._generate_fix_guide(report)
        from datetime import datetime
        report["generated_at"] = datetime.now().strftime("%Y-%m-%d %H:%M:%S")

        return report

    # ─── 从决策日志收集 ───
    def _collect_from_decisions(self, decisions_dir: Path,
                                 state_records: Dict[str, List[dict]]):
        """读取 DecisionLogger 输出的 JSON 文件。"""
        if not decisions_dir.exists():
            return

        for f in sorted(decisions_dir.glob("decisions_run_*.json")):
            try:
                decisions = json.loads(f.read_text(encoding="utf-8"))
                for d in decisions:
                    screen = d.get("Screen", "")
                    state_type = self._map_screen(screen)
                    if state_type == "NONE":
                        continue

                    options = d.get("Options", [])
                    chosen_idx = d.get("ChosenIndex", -1)
                    chosen_label = d.get("ChosenLabel", "")
                    decision_type = d.get("DecisionType", "")

                    record = {
                        "state_type": state_type,
                        "decision_type": decision_type,
                        "ai_considered_actions": [
                            {
                                "action_type": self._classify_option(state_type, o.get("Label", "")),
                                "label": o.get("Label", ""),
                                "score": o.get("Score", 0),
                            }
                            for o in options
                        ],
                        "chosen_action_type": self._classify_option(state_type, chosen_label),
                        "chosen_label": chosen_label,
                        "chosen_index": chosen_idx,
                        "options_count": len(options),
                    }
                    state_records[state_type].append(record)
            except Exception:
                pass

    # ─── 从战斗日志收集 ───
    def _collect_from_battles(self, batch_dir: Path,
                               state_records: Dict[str, List[dict]]):
        """读取战斗日志 JSON 文件，检查药水使用情况。"""
        if not batch_dir.exists():
            return

        for run_dir in sorted(batch_dir.iterdir()):
            if not run_dir.is_dir() or not run_dir.name.startswith("run_"):
                continue

            battles_dir = run_dir / "battles"
            if not battles_dir.exists():
                continue

            for f in sorted(battles_dir.glob("battle_*.json")):
                try:
                    battle = json.loads(f.read_text(encoding="utf-8"))
                except Exception:
                    continue

                encounter = battle.get("EncounterName", "?")
                turns = battle.get("Turns", [])

                potion_count = battle.get("PreCombatState", {}).get("PotionCount", 0)
                has_turn_potions = any(turn.get("PotionIds") for turn in turns) if turns else False

                record = {
                    "state_type": "COMBAT",
                    "encounter": encounter,
                    "has_potions": has_turn_potions,
                    "potion_count": potion_count if potion_count > 0 else (len(turns[0].get("PotionIds", [])) if turns and turns[0].get("PotionIds") else 0),
                    "total_turns": len(turns),
                    "ai_considered_actions": [],
                    "chosen_action_type": "",
                }

                # Check if potions were used
                potions_used = battle.get("Outcome", {}).get("PotionsUsed", 0)
                potions_remaining = battle.get("Outcome", {}).get("PotionsRemaining", 0)

                # Collect all actions across all turns
                all_actions = []
                has_potions_in_battle = has_turn_potions
                for turn in turns:
                    potion_ids = turn.get("PotionIds", [])
                    if potion_ids:
                        has_potions_in_battle = True

                    plan = turn.get("SolverPlan", {})
                    actions = plan.get("Actions", [])

                    for action in actions:
                        if isinstance(action, dict):
                            act_type = action.get("type", "")
                        else:
                            act_type = str(action)
                        all_actions.append(self._classify_combat_action(act_type))

                    # END_TURN is always part of every turn's plan
                    all_actions.append("END_TURN")

                # Build the combined record
                unique_actions = list(set(all_actions))
                record["ai_considered_actions"] = [
                    {"action_type": a, "label": a, "score": 0}
                    for a in unique_actions
                ]

                # ALL unique actions in the plan are "chosen"
                # Mark both PLAY_CARD and END_TURN as chosen since both are in every plan
                record["chosen_actions"] = unique_actions.copy()
                record["chosen_action_type"] = unique_actions[0] if unique_actions else "END_TURN"

                # Check if potion use was in the action space
                potion_considered = "USE_POTION" in unique_actions or "USE_POTION_TARGETED" in unique_actions
                if not potion_considered and has_potions_in_battle:
                    # Record that potions were available but not in action space
                    record["ai_considered_actions"].append({
                        "action_type": "USE_POTION",
                        "label": "NOT_IN_ACTION_SPACE",
                        "score": -999,
                    })

                state_records["COMBAT"].append(record)

    # ─── 单个动作覆盖率检查 ───
    def _check_action_coverage(self, state_records: Dict[str, List[dict]],
                                 state_type: str, action_name: str,
                                 config: dict) -> GapReport:
        relevant = state_records.get(state_type, [])
        if not relevant:
            return GapReport(
                status="no_data", severity="INFO",
                state_type=state_type, action=action_name,
                message=f"日志中没有 {state_type} 状态记录，可能未到达该阶段",
            )

        times_available = 0
        times_considered = 0
        times_chosen = 0

        for state in relevant:
            is_available = self._is_action_available(state, action_name, config)
            if is_available:
                times_available += 1
                if self._was_action_considered(state, action_name):
                    times_considered += 1
                    # Check chosen: either chosen_action_type matches, or in chosen_actions list
                    chosen = state.get("chosen_action_type") == action_name
                    if not chosen:
                        chosen_actions = state.get("chosen_actions", [])
                        chosen = action_name in chosen_actions
                    if chosen:
                        times_chosen += 1

        if times_available == 0:
            return GapReport(
                status="not_applicable", severity="INFO",
                state_type=state_type, action=action_name,
                message=f"动作 {action_name} 在这些局中从未可用",
            )

        consideration_rate = times_considered / max(1, times_available)
        selection_rate = times_chosen / max(1, times_available)

        # 从未被考虑 → 能力缺失
        if consideration_rate == 0:
            return GapReport(
                status="missing",
                severity="CRITICAL" if config["critical"] else "WARNING",
                state_type=state_type, action=action_name,
                times_available=times_available,
                times_considered=0, times_chosen=0,
                consideration_rate=0, selection_rate=0,
                message=(
                    f"❌ {state_type}/{action_name}: "
                    f"在 {times_available} 次可用机会中，AI **从未考虑过**此选项。"
                    f"这是代码缺陷，不是参数问题。"
                ),
                fix_type="code_change",
                likely_cause=self._diagnose(state_type, action_name),
                related_params=self._suggest_params(state_type, action_name),
            )

        # 被考虑但从未被选择 → 参数问题
        if selection_rate == 0 and times_considered > 0:
            return GapReport(
                status="never_chosen",
                severity="WARNING",
                state_type=state_type, action=action_name,
                times_available=times_available,
                times_considered=times_considered,
                times_chosen=0,
                consideration_rate=consideration_rate,
                selection_rate=0,
                message=(
                    f"⚠️  {state_type}/{action_name}: "
                    f"AI 考虑了此选项 {times_considered}/{times_available} 次，"
                    f"但从未选择。可能是评分过低。"
                ),
                fix_type="parameter_tuning",
                related_params=self._suggest_params(state_type, action_name),
            )

        # 选择率异常低
        if selection_rate < 0.05 and times_available >= 10:
            return GapReport(
                status="suspicious",
                severity="INFO",
                state_type=state_type, action=action_name,
                times_available=times_available,
                times_considered=times_considered,
                times_chosen=times_chosen,
                consideration_rate=consideration_rate,
                selection_rate=selection_rate,
                message=(
                    f"🔍 {state_type}/{action_name}: "
                    f"选择率仅 {selection_rate:.1%}（{times_chosen}/{times_available}），"
                    f"需人工评估是否合理"
                ),
                fix_type="needs_review",
            )

        return GapReport(
            status="ok", severity="INFO",
            state_type=state_type, action=action_name,
            times_available=times_available,
            times_considered=times_considered,
            times_chosen=times_chosen,
            consideration_rate=consideration_rate,
            selection_rate=selection_rate,
            message=f"✅ {state_type}/{action_name}: 选择率 {selection_rate:.1%}",
        )

    # ─── 动作可用性检查 ───
    def _is_action_available(self, state: dict, action_name: str,
                              config: dict) -> bool:
        check = config["check"]

        if check == "always_available":
            return True
        if check == "if_available":
            return action_name in [a.get("action_type", "") for a in
                                   state.get("ai_considered_actions", []) if a.get("label") != "NOT_IN_ACTION_SPACE"]
        if check == "if_has_potions":
            return state.get("has_potions", False) or state.get("potion_count", 0) > 0
        if check == "if_has_targeted_potion":
            return state.get("has_targeted_potion", False)
        if check == "if_potions_full":
            return state.get("potion_count", 0) >= state.get("max_potion_slots", 3)
        if check == "if_purge_available":
            return state.get("purge_available", False)
        if check == "if_skip_allowed":
            return state.get("can_skip", True)  # Default true for most
        if check == "if_bowl_available":
            return state.get("can_bowl", False)
        if check == "if_boss_available":
            return state.get("boss_available", False)
        if check == "if_leavable":
            return state.get("can_leave", True)
        if check == "if_cancel_allowed":
            return state.get("can_cancel", False)
        if check == "if_has_potion_slot":
            return state.get("potion_count", 0) < state.get("max_potion_slots", 3)
        if check == "if_chest_available":
            return state.get("has_chest", False)
        return False

    def _was_action_considered(self, state: dict, action_name: str) -> bool:
        considered = state.get("ai_considered_actions", [])
        for a in considered:
            if a.get("label") == "NOT_IN_ACTION_SPACE":
                continue  # This is a marker for missing capability
            if a.get("action_type") == action_name:
                return True
        return False

    # ─── 屏幕类型映射 ───
    def _map_screen(self, screen: str) -> str:
        mapping = {
            "REST": "REST",
            "SHOP": "SHOP",
            "EVENT": "EVENT",
            "MAP": "MAP",
            "OVERLAY_CARD_REWARD": "CARD_REWARD",
            "CARD_REWARD": "CARD_REWARD",
            "BOSS_REWARD": "BOSS_REWARD",
            "COMBAT_REWARD": "COMBAT_REWARD",
            "GRID_SELECT": "GRID_SELECT",
            "GRID": "GRID_SELECT",
            "TREASURE": "COMBAT_REWARD",
            "COMBAT": "COMBAT",
        }
        return mapping.get(screen.upper(), "NONE")

    # ─── 选项分类 ───
    def _classify_option(self, state_type: str, label: str) -> str:
        label_lower = label.lower().replace(" ", "_")
        import re

        classifiers = {
            "REST": {
                "rest": "REST", "sleep": "REST", "nap": "REST",
                "smith": "SMITH", "upgrade": "SMITH", "forge": "SMITH",
                "improve": "SMITH", "enchant": "SMITH",
                "toke": "TOKE", "remove": "TOKE", "purge": "TOKE",
                "cleanse": "TOKE",
                "lift": "LIFT", "girya": "LIFT", "train": "LIFT",
                "dig": "DIG", "excavate": "DIG",
                "recall": "RECALL", "key": "RECALL",
            },
            "SHOP": {
                "remove": "PURGE_CARD", "purge": "PURGE_CARD",
                "card:": "BUY_CARD", "relic:": "BUY_RELIC",
                "potion:": "BUY_POTION", "leave": "LEAVE_SHOP",
            },
            "CARD_REWARD": {
                "skip": "SKIP_CARD", "bowl": "BOWL_CARD",
            },
            "EVENT": {
                "proceed": "LEAVE_EVENT", "leave": "LEAVE_EVENT",
                "dialogue": "CHOOSE_OPTION",
            },
            "MAP": {
                "node": "CHOOSE_NODE", "boss": "CHOOSE_BOSS",
                "scroll": "SCROLL_MAP",
                # Match "(row,col) Type" format
            },
            "BOSS_REWARD": {
                "skip": "SKIP_RELIC",
            },
        }

        if state_type in classifiers:
            for keyword, action in classifiers[state_type].items():
                if keyword in label_lower:
                    return action

        # MAP: labels like "(1,3) Normal", "(2,5) Shop", etc.
        if state_type == "MAP":
            if re.match(r'^\(\d+,\d+\)\s', label):
                return "CHOOSE_NODE"

        # Generic classification
        if state_type == "CARD_REWARD":
            return "PICK_CARD"
        if state_type == "EVENT":
            return "CHOOSE_OPTION"
        if state_type == "BOSS_REWARD":
            return "PICK_RELIC"
        if state_type == "GRID_SELECT":
            if "confirm" in label_lower or "proceed" in label_lower:
                return "CONFIRM"
            if "cancel" in label_lower or "back" in label_lower:
                return "CANCEL"
            return "SELECT_CARD"
        if state_type == "COMBAT_REWARD":
            if "gold" in label_lower:
                return "PICK_GOLD"
            if "chest" in label_lower or "open" in label_lower:
                return "OPEN_CHEST"
            return "PICK_GOLD"
        if state_type == "SHOP":
            if "card" in label_lower:
                return "BUY_CARD"
            if "relic" in label_lower:
                return "BUY_RELIC"
            if "potion" in label_lower:
                return "BUY_POTION"

        return label[:40]

    def _classify_combat_action(self, action_label: str) -> str:
        label = action_label.lower().replace(" ", "_")
        if "end_turn" in label or "endturn" in label:
            return "END_TURN"
        if "potion" in label or "use_potion" in label:
            if "target" in label:
                return "USE_POTION_TARGETED"
            return "USE_POTION"
        if "discard_potion" in label:
            return "DISCARD_POTION"
        if "play" in label or "card" in label or "strike" in label or \
           "defend" in label or "bash" in label:
            return "PLAY_CARD"
        return "PLAY_CARD"

    # ─── 诊断 ───
    def _diagnose(self, state_type: str, action_name: str) -> List[str]:
        return self.DIAGNOSES.get(
            (state_type, action_name),
            [f"未知原因，需检查 {state_type} 的决策代码"]
        )

    def _suggest_params(self, state_type: str, action_name: str) -> List[str]:
        return self.PARAM_SUGGESTIONS.get(
            (state_type, action_name),
            ["未知，需人工分析"]
        )

    # ─── 生成修复指南 ───
    def _generate_fix_guide(self, report: dict) -> str:
        lines = [
            "# 🔧 需要修复的能力缺失",
            f"",
            f"自动生成时间: {report.get('generated_at', '?')}",
            f"功能完整度评分: {report.get('score', 0):.0f}/100",
            f"",
        ]

        critical = [g for g in report.get("missing_capabilities", [])
                    if g.severity == "CRITICAL"]
        if critical:
            lines.append(f"## CRITICAL ({len(critical)} 个)")
            lines.append("")
            for gap in critical:
                lines.append(f"### {gap.state_type}/{gap.action}")
                lines.append(f"- **出现次数**: {gap.times_available}次可用机会，0次被考虑")
                lines.append(f"- **修复方式**: {gap.fix_type}")
                lines.append(f"- **可能原因**:")
                for cause in gap.likely_cause:
                    lines.append(f"  - {cause}")
                if gap.related_params:
                    lines.append(f"- **相关参数**: {', '.join(gap.related_params)}")
                lines.append("")

        warnings = [g for g in report.get("missing_capabilities", [])
                    if g.severity == "WARNING"]
        if warnings:
            lines.append(f"## WARNING ({len(warnings)} 个)")
            lines.append("")
            for gap in warnings:
                lines.append(f"### {gap.state_type}/{gap.action}")
                lines.append(f"- {gap.message}")
                lines.append("")

        never_chosen = report.get("never_chosen_capabilities", [])
        if never_chosen:
            lines.append(f"## 考虑但从未选择 ({len(never_chosen)} 个)")
            lines.append("")
            lines.append("这些可能是参数问题，优化器可以自动处理：")
            lines.append("")
            for item in never_chosen:
                params = ", ".join(item.related_params[:3]) if item.related_params else "N/A"
                lines.append(f"- **{item.state_type}/{item.action}**: {item.times_considered}次被考虑，0次被选 → 调参: `{params}`")

        suspicious = report.get("suspicious_patterns", [])
        if suspicious:
            lines.append(f"")
            lines.append(f"## 异常模式 ({len(suspicious)} 个)")
            lines.append("")
            for item in suspicious:
                lines.append(f"- **{item.state_type}/{item.action}**: {item.message}")

        return "\n".join(lines)

    # ─── 打印报告 ───
    def print_report(self, report: dict):
        print(f"\n{'='*70}")
        print(f"🔍 功能完整性审计报告")
        print(f"{'='*70}")
        print(f"功能完整度评分: {report['score']:.0f}/100")

        critical = [g for g in report.get("missing_capabilities", [])
                    if g.severity == "CRITICAL"]
        warnings = [g for g in report.get("missing_capabilities", [])
                    if g.severity == "WARNING"]
        never_chosen = report.get("never_chosen_capabilities", [])
        suspicious = report.get("suspicious_patterns", [])

        if critical:
            print(f"\n❌ 发现 {len(critical)} 个严重能力缺失（代码缺陷）:")
            for gap in critical:
                print(f"\n  [{gap.severity}] {gap.state_type}/{gap.action}")
                print(f"  在 {gap.times_available} 次可用机会中，AI **从未考虑过**此选项")
                print(f"  修复方式: {gap.fix_type}")
                for cause in gap.likely_cause:
                    print(f"    → {cause}")

        if warnings:
            print(f"\n⚠️  {len(warnings)} 个非关键能力缺失:")

        if never_chosen:
            print(f"\n⚠️  {len(never_chosen)} 个动作为'考虑但从未选择'（参数可修复）:")
            for item in never_chosen:
                params = ", ".join(item.related_params[:2]) if item.related_params else "N/A"
                print(f"  - {item.state_type}/{item.action}: → 调参: [{params}]")

        if suspicious:
            print(f"\n🔍 {len(suspicious)} 个异常模式需人工判断")

        ok_count = sum(1 for r in report.get("all_results", []) if r.status == "ok")
        print(f"\n✅ 正常: {ok_count} 个动作覆盖正常")

        # Critical findings block continuation
        if critical:
            print(f"""
╔══════════════════════════════════════════════════════╗
║  ⛔ 检测到严重能力缺失                                 ║
║                                                      ║
║  参数优化无法修复这些问题。                            ║
║  请先修复代码缺陷，然后重新运行审计。                   ║
║  详见: NEXT_FIXES.md                                 ║
╚══════════════════════════════════════════════════════╝
            """)


# ── CLI ────────────────────────────────────────────────────────────────────────

if __name__ == "__main__":
    import argparse, sys

    parser = argparse.ArgumentParser(description="TokenSpire2 Capability Auditor")
    parser.add_argument("--batch-dir", type=str, default=None,
                       help="Path to batch_runs directory")
    parser.add_argument("--decisions-dir", type=str, default=None,
                       help="Path to decision logs directory")
    parser.add_argument("--output", type=str, default=None,
                       help="Output path for audit report JSON")
    parser.add_argument("--fix-guide", type=str, default=None,
                       help="Output path for NEXT_FIXES.md")
    args = parser.parse_args()

    auditor = CapabilityAuditor()

    batch_dir = Path(args.batch_dir) if args.batch_dir else None
    decisions_dir = Path(args.decisions_dir) if args.decisions_dir else None

    report = auditor.audit(batch_dir=batch_dir, decisions_dir=decisions_dir)
    auditor.print_report(report)

    # Save JSON
    out_path = args.output
    if not out_path:
        out_path = str(MOD_DIR / "optimization_sessions" / "capability_audit.json")
    Path(out_path).parent.mkdir(parents=True, exist_ok=True)
    with open(out_path, "w", encoding="utf-8") as f:
        # Convert dataclasses to dicts
        serializable = {
            "score": report["score"],
            "generated_at": report["generated_at"],
            "missing_capabilities": [
                {k: v for k, v in vars(g).items()} for g in report["missing_capabilities"]
            ],
            "never_chosen_capabilities": [
                {k: v for k, v in vars(g).items()} for g in report["never_chosen_capabilities"]
            ],
            "suspicious_patterns": [
                {k: v for k, v in vars(g).items()} for g in report["suspicious_patterns"]
            ],
        }
        json.dump(serializable, f, indent=2, ensure_ascii=False)

    # Save fix guide
    fix_path = args.fix_guide
    if not fix_path:
        fix_path = str(MOD_DIR / "NEXT_FIXES.md")
    Path(fix_path).write_text(report["fix_guide_markdown"], encoding="utf-8")

    print(f"\n审计报告: {out_path}")
    print(f"修复指南: {fix_path}")

    # Exit code for CI/automation
    critical = [g for g in report.get("missing_capabilities", [])
                if g.severity == "CRITICAL"]
    if critical:
        sys.exit(2)  # Critical gaps found
    sys.exit(0)
