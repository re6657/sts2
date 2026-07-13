#!/usr/bin/env python3
"""
Auto-launch STS2, monitor the mod, and generate error reports.

Usage:
    python scripts/run_and_watch.py [--runs N] [--watch] [--restart-on-crash]

Workflow:
    1. Build the mod (dotnet build)
    2. Launch STS2
    3. Monitor mod logs + battle logs directory
    4. Detect errors (solver crashes, stuck states, etc.)
    5. Generate per-run error reports
    6. Optional: restart on fatal errors, run multiple times
"""

import argparse
import json
import os
import re
import signal
import subprocess
import sys
import time
import threading
import glob
from datetime import datetime
from pathlib import Path


# ── Configuration ────────────────────────────────────────────────────────────

STS2_DIR = Path(__file__).parent.parent.parent.parent.resolve()
MOD_DIR = STS2_DIR / "mods" / "TokenSpire2"
MOD_SRC = MOD_DIR / "src"
MOD_CSPROJ = MOD_DIR / "TokenSpire2.csproj"
MOD_DLL = MOD_DIR / "TokenSpire2.dll"
LOG_DIR = MOD_DIR / "llm_data"
BATTLE_LOG_DIR = LOG_DIR / "battles"
GAME_EXE = STS2_DIR / "SlayTheSpire2.exe"
# STS2 is a Godot game — logs go to AppData/Roaming, not BepInEx
GAME_LOG = Path(os.environ.get("APPDATA", "")) / "SlayTheSpire2" / "logs" / "godot.log"
# Fallback for if APPDATA isn't set
if not GAME_LOG.exists():
    GAME_LOG = Path(os.environ.get("USERPROFILE", "")) / "AppData" / "Roaming" / "SlayTheSpire2" / "logs" / "godot.log"

# Mod log is the same Godot log (MainFile.Logger writes to godot.log)
BEPINEX_LOG = GAME_LOG

ERROR_PATTERNS = [
    (re.compile(r"CRASH", re.IGNORECASE), "crash"),
    (re.compile(r"NullReferenceException", re.IGNORECASE), "null_reference"),
    (re.compile(r"InvalidOperationException", re.IGNORECASE), "invalid_operation"),
    (re.compile(r"KeyNotFoundException", re.IGNORECASE), "key_not_found"),
    (re.compile(r"IndexOutOfRangeException", re.IGNORECASE), "index_out_of_range"),
    (re.compile(r"StackOverflowException", re.IGNORECASE), "stack_overflow"),
    (re.compile(r"Solver CRASH", re.IGNORECASE), "solver_crash"),
    (re.compile(r"TryManualPlay FAILED", re.IGNORECASE), "play_failed"),
    (re.compile(r"Card selection failed", re.IGNORECASE), "card_select_failed"),
    (re.compile(r"LLM fail.*consecutive", re.IGNORECASE), "llm_consecutive_fail"),
    (re.compile(r"heuristic card selection", re.IGNORECASE), "heuristic_fallback"),
]

STUCK_PATTERNS = [
    (re.compile(r"Stuck|stuck|STUCK"), "explicit_stuck"),
]

RUNNING = True


def log(msg: str) -> None:
    """Timestamped log."""
    ts = datetime.now().strftime("%H:%M:%S")
    print(f"[{ts}] {msg}", flush=True)


# ── Build ────────────────────────────────────────────────────────────────────

def build_mod() -> bool:
    """dotnet build the mod. Returns True on success."""
    log("Building mod...")
    result = subprocess.run(
        ["dotnet", "build", str(MOD_CSPROJ), "-c", "Debug"],
        capture_output=True, text=True, cwd=str(MOD_DIR), timeout=120,
    )
    if result.returncode == 0:
        log("Build OK")
        return True
    else:
        log(f"Build FAILED (exit {result.returncode})")
        # Print last 20 lines of output
        lines = (result.stdout + result.stderr).splitlines()
        for line in lines[-20:]:
            print(f"  {line}")
        return False


# ── Launch ───────────────────────────────────────────────────────────────────

def launch_game() -> subprocess.Popen | None:
    """Launch STS2 via Steam and return the process handle."""
    log("Launching STS2 via Steam...")
    try:
        # Steam URL protocol — launches the game through Steam client
        proc = subprocess.Popen(
            ["cmd", "/c", "start", "", "steam://rungameid/2868840"],
            cwd=str(STS2_DIR),
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
        )
        # Steam protocol returns immediately; game takes ~10-20s to start
        # Return a dummy process that we can poll — actual monitoring is via logs
        return proc
    except Exception as e:
        log(f"Launch failed: {e}")
        return None


# ── Log monitoring ───────────────────────────────────────────────────────────

def find_log_file() -> Path | None:
    """Find the Godot log file to monitor."""
    if GAME_LOG.exists():
        return GAME_LOG
    return None


class LogMonitor:
    """Tails a log file and runs pattern matching."""

    def __init__(self, path: Path):
        self.path = path
        self.pos = 0
        self.errors: list[dict] = []
        self.line_count = 0

    def seek_end(self) -> None:
        """Start monitoring from the current end of file."""
        try:
            if self.path.exists():
                self.pos = self.path.stat().st_size
        except Exception:
            self.pos = 0

    def poll(self) -> list[str]:
        """Read new lines since last poll. Returns list of new lines."""
        try:
            if not self.path.exists():
                return []
            size = self.path.stat().st_size
            if size < self.pos:
                # File truncated/rotated
                self.pos = 0
            if size == self.pos:
                return []
            with open(self.path, "r", encoding="utf-8", errors="replace") as f:
                f.seek(self.pos)
                new_data = f.read()
                self.pos = f.tell()
            lines = new_data.splitlines()
            self.line_count += len(lines)
            return lines
        except Exception:
            return []

    def scan_lines(self, lines: list[str]) -> list[dict]:
        """Scan lines for error patterns. Returns list of error dicts."""
        found = []
        for line in lines:
            for pattern, error_type in ERROR_PATTERNS:
                m = pattern.search(line)
                if m:
                    found.append({
                        "type": error_type,
                        "line": line.strip()[:200],
                        "time": datetime.now().isoformat(),
                    })
                    break  # one error type per line
            for pattern, stuck_type in STUCK_PATTERNS:
                if pattern.search(line):
                    found.append({
                        "type": stuck_type,
                        "line": line.strip()[:200],
                        "time": datetime.now().isoformat(),
                    })
                    break
        self.errors.extend(found)
        return found


# ── Battle log watching ──────────────────────────────────────────────────────

class BattleWatcher:
    """Watch for new battle JSON files appearing on disk."""

    def __init__(self, watch_dir: Path):
        self.watch_dir = watch_dir
        self.seen: set[str] = set()
        self.battles: list[dict] = []

    def discover_existing(self) -> None:
        """Mark all existing battle logs as seen."""
        if not self.watch_dir.exists():
            return
        for f in self.watch_dir.glob("battle_*.json"):
            self.seen.add(f.name)

    def poll(self) -> list[dict]:
        """Check for new battle logs. Returns list of parsed battle dicts."""
        if not self.watch_dir.exists():
            return []
        new_battles = []
        for f in sorted(self.watch_dir.glob("battle_*.json")):
            if f.name in self.seen:
                continue
            self.seen.add(f.name)
            try:
                with open(f, "r", encoding="utf-8") as fh:
                    data = json.load(fh)
                new_battles.append(data)
                self.battles.append(data)
            except Exception as e:
                log(f"Failed to parse {f.name}: {e}")
        return new_battles


# ── Report generation ────────────────────────────────────────────────────────

def generate_report(
    monitor: LogMonitor, watcher: BattleWatcher,
    run_num: int, start_time: datetime, crashed: bool = False,
) -> str:
    """Generate a Markdown error report."""
    elapsed = time.time() - start_time
    lines = []
    lines.append(f"# Run #{run_num} Report")
    lines.append(f"**Time:** {datetime.now().isoformat()}")
    lines.append(f"**Duration:** {elapsed:.1f}s")
    lines.append(f"**Status:** {'CRASHED' if crashed else 'Completed'}")
    lines.append("")

    # Errors
    lines.append("## Errors Detected")
    if monitor.errors:
        lines.append(f"**Total errors:** {len(monitor.errors)}")
        lines.append("")
        # Group by type
        by_type: dict[str, int] = {}
        for e in monitor.errors:
            t = e["type"]
            by_type[t] = by_type.get(t, 0) + 1
        for t, count in sorted(by_type.items(), key=lambda x: -x[1]):
            lines.append(f"- **{t}**: {count}")
        lines.append("")
        lines.append("### Details")
        for e in monitor.errors[:50]:  # cap at 50
            lines.append(f"- `[{e['type']}]` {e['line']}")
    else:
        lines.append("No errors detected.")
    lines.append("")

    # Battles
    lines.append("## Battles")
    if watcher.battles:
        total = len(watcher.battles)
        wins = sum(1 for b in watcher.battles if b.get("Victory"))
        crashes = sum(b.get("SolverCrashes", 0) for b in watcher.battles)
        lines.append(f"- **Total battles:** {total}")
        lines.append(f"- **Wins:** {wins} ({100*wins/total:.0f}%)" if total > 0 else "")
        lines.append(f"- **Losses:** {total - wins}")
        lines.append(f"- **Solver crashes:** {crashes}")
        lines.append("")
        for b in watcher.battles:
            name = b.get("EncounterName", "?")
            turns = b.get("TotalTurns", 0)
            dmg_taken = b.get("TotalDamageTaken", 0)
            dmg_dealt = b.get("TotalDamageDealt", 0)
            win = "WIN" if b.get("Victory") else "LOSS"
            lines.append(
                f"- [{win}] **{name}** — {turns} turns, "
                f"{dmg_dealt} dealt, {dmg_taken} taken"
            )
    else:
        lines.append("No battles recorded.")
    lines.append("")

    report = "\n".join(lines)
    return report


# ── Main loop ────────────────────────────────────────────────────────────────

def signal_handler(sig, frame):
    global RUNNING
    RUNNING = False
    log("Shutting down...")


def find_game_process() -> subprocess.Popen | None:
    """Check if SlayTheSpire2.exe is running. Returns True if found."""
    try:
        result = subprocess.run(
            ["tasklist", "/fi", "IMAGENAME eq SlayTheSpire2.exe", "/fo", "csv", "/nh"],
            capture_output=True, text=True, timeout=10,
        )
        return "SlayTheSpire2.exe" in result.stdout
    except Exception:
        return False


def kill_game():
    """Kill SlayTheSpire2.exe if running."""
    try:
        subprocess.run(
            ["taskkill", "/f", "/im", "SlayTheSpire2.exe"],
            capture_output=True, timeout=15,
        )
    except Exception:
        pass


def run_session(run_num: int, restart_on_crash: bool) -> dict:
    """Run one session: launch, monitor, report. Returns summary dict."""
    global RUNNING

    # Build
    if not build_mod():
        return {"success": False, "reason": "build_failed"}

    # Discover existing battle logs (avoid re-reporting)
    BATTLE_LOG_DIR.mkdir(parents=True, exist_ok=True)
    watcher = BattleWatcher(BATTLE_LOG_DIR)
    watcher.discover_existing()

    # Launch game via Steam
    log("Launching STS2 via Steam...")
    try:
        subprocess.Popen(
            ["cmd", "/c", "start", "", "steam://rungameid/2868840"],
            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
        )
    except Exception as e:
        log(f"Launch failed: {e}")
        return {"success": False, "reason": "launch_failed"}

    start_time = time.time()
    monitor = LogMonitor(BEPINEX_LOG)

    # Wait for game process to actually start (up to 30s)
    game_started = False
    for _ in range(30):
        time.sleep(1)
        if find_game_process():
            game_started = True
            break

    if not game_started:
        log("ERROR: Game process never appeared after launch")
        return {"success": False, "reason": "game_not_started"}

    log("Game process detected, monitoring...")
    time.sleep(5)  # Let BepInEx initialize
    monitor.seek_end()

    # Monitoring loop
    last_activity = time.time()
    stuck_timeout = 300  # 5 minutes without any log output = stuck
    max_session = 3600   # 1 hour max per run
    crashed = False

    while RUNNING:
        now = time.time()

        # Check game still running (poll every iteration)
        if not find_game_process():
            log("Game process exited")
            break

        # Check session timeout
        if now - start_time > max_session:
            log("Session timeout reached")
            kill_game()
            time.sleep(3)
            break

        # Poll logs
        new_lines = monitor.poll()
        if new_lines:
            last_activity = now
            errors = monitor.scan_lines(new_lines)
            for e in errors:
                log(f"⚠ [{e['type']}] {e['line'][:120]}")

            # Detect game over / run completion from log patterns
            for line in new_lines:
                # "GameOver" is definitive — run is done.
                if "GameOver" in line:
                    log(f"Detected run completion (GameOver): {line.strip()[:100]}")
                    time.sleep(3)
                    RUNNING = False
                    break
                # "MainMenu" can fire on initial game load via
                # "Preloading 'MainMenuEssentials'" — skip those.
                # IMPORTANT: STS2 briefly shows MainMenu during combat→rewards
                # transitions. We must wait and confirm MainMenu is STABLE before
                # treating it as run completion. Otherwise we kill the game during
                # a normal transition (the user sees this as a crash/"闪退").
                if "MainMenu" in line and "MainMenuEssentials" not in line:
                    if len(watcher.battles) > 0:
                        log(f"MainMenu signal detected — waiting 8s to confirm stable...")
                        pre_menu_battles = len(watcher.battles)
                        confirmed = True
                        for i in range(16):  # 16 × 0.5s = 8s
                            time.sleep(0.5)
                            # Check for new battles (game continuing)
                            watcher.poll()
                            if len(watcher.battles) > pre_menu_battles:
                                log(f"MainMenu was transient (new battle detected) — ignoring")
                                confirmed = False
                                break
                            # Check recent log lines for game activity
                            recent = monitor.poll()
                            for rline in recent:
                                if any(kw in rline for kw in [
                                    'Dispatching rewards', 'Dispatching OVERLAY',
                                    '⚔ Battle:', 'InCombat: playerDisabled=False',
                                    'NRewardsScreen', 'NCardRewardSelectionScreen',
                                    'overlays=1', 'overlays=2',
                                ]):
                                    log(f"MainMenu was transient (game activity detected) — ignoring")
                                    confirmed = False
                                    break
                            if not confirmed:
                                break
                        if confirmed:
                            log(f"MainMenu confirmed stable after 8s — run ended")
                            time.sleep(3)
                            RUNNING = False
                            break
                    else:
                        log(f"Skipping MainMenu (no battles yet): {line.strip()[:100]}")
            if not RUNNING:
                break

        # Watch for new battles
        new_battles = watcher.poll()
        for b in new_battles:
            log(f"⚔ Battle: {b.get('EncounterName', '?')} — "
                f"{'WIN' if b.get('Victory') else 'LOSS'} "
                f"({b.get('TotalTurns', 0)} turns)")

        # Stuck detection
        if now - last_activity > stuck_timeout:
            log("Game appears stuck (no log output for 5 min)")
            crashed = True
            kill_game()
            time.sleep(3)
            break

        time.sleep(2)

    # Ensure game is killed
    kill_game()
    time.sleep(1)

    # Final poll for any remaining battles
    watcher.poll()

    # Generate report
    report = generate_report(monitor, watcher, run_num, start_time, crashed)
    report_path = LOG_DIR / f"report_run{run_num:03d}.md"
    LOG_DIR.mkdir(parents=True, exist_ok=True)
    report_path.write_text(report, encoding="utf-8")
    log(f"Report saved: {report_path}")

    return {
        "success": not crashed,
        "run_num": run_num,
        "errors": len(monitor.errors),
        "battles": len(watcher.battles),
        "wins": sum(1 for b in watcher.battles if b.get("Victory")),
        "duration": time.time() - start_time,
        "report": str(report_path),
    }


def main():
    global RUNNING
    signal.signal(signal.SIGINT, signal_handler)
    signal.signal(signal.SIGTERM, signal_handler)

    parser = argparse.ArgumentParser(description="Auto-launch STS2 and monitor")
    parser.add_argument("--runs", type=int, default=1, help="Number of runs (default 1)")
    parser.add_argument("--watch", action="store_true", help="Single continuous watch session")
    parser.add_argument("--restart-on-crash", action="store_true", help="Restart on fatal errors")
    args = parser.parse_args()

    log("══ TokenSpire2 Auto-Watcher ══")
    log(f"Config: runs={args.runs} watch={args.watch} restart={args.restart_on_crash}")

    all_summaries = []
    for i in range(args.runs):
        if not RUNNING:
            break
        log(f"\n{'='*60}")
        log(f"Run {i+1}/{args.runs}")
        log(f"{'='*60}")
        summary = run_session(i + 1, args.restart_on_crash)
        all_summaries.append(summary)
        log(f"Run {i+1} done: success={summary['success']} battles={summary.get('battles', 0)}")

        if not summary["success"] and not args.restart_on_crash:
            log("Run failed and --restart-on-crash not set, stopping.")
            break

        # Brief cooldown between runs
        if i < args.runs - 1 and RUNNING:
            log("Cooling down 10s before next run...")
            time.sleep(10)

    # Final summary
    log(f"\n{'='*60}")
    log("ALL RUNS COMPLETE")
    log(f"{'='*60}")
    total_battles = sum(s.get("battles", 0) for s in all_summaries)
    total_wins = sum(s.get("wins", 0) for s in all_summaries)
    total_errors = sum(s.get("errors", 0) for s in all_summaries)
    log(f"Runs: {len(all_summaries)}")
    log(f"Battles: {total_battles}")
    log(f"Wins: {total_wins} ({100*total_wins/total_battles:.0f}%)" if total_battles > 0 else "Wins: 0")
    log(f"Errors: {total_errors}")
    log(f"Reports in: {LOG_DIR}")


if __name__ == "__main__":
    main()
