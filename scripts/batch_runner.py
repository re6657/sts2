#!/usr/bin/env python3
"""
Batch test runner for TokenSpire2 solver optimization.

Runs N games with different seeds, collects battle/decision logs,
and generates an aggregate analysis report. Designed to be run
unattended — launch it, come back after coffee, check the report.

Usage:
    python scripts/batch_runner.py --runs 10 [--character IRONCLAD] [--hp 1.0]
                                   [--max-time 600] [--stuck-timeout 300]

Workflow per run:
    1. Generate a unique seed (timestamp + counter)
    2. Write batch_config.json with seed/character/hpMultiplier
    3. Build the mod (optional, --no-build to skip)
    4. Launch STS2 via Steam
    5. Wait for game process to start
    6. Monitor BepInEx log for run completion (GameOver/MainMenu/Victory/Death)
    7. Detect stuck runs (no new battles for N seconds)
    8. Kill game, collect logs, archive to batch_runs/run_NNN/
    9. Repeat

Output:
    batch_runs/
    ├── run_001/
    │   ├── battles/       # battle_*.json from this run
    │   ├── decisions/     # decisions_run_*.json from this run
    │   └── summary.json   # per-run summary
    ├── run_002/
    │   └── ...
    ├── aggregate_report.md  # combined analysis
    └── batch_summary.json   # machine-readable summary of all runs
"""

import argparse
import json
import os
import signal
import subprocess
import sys
import time
import shutil
import hashlib
import json
from datetime import datetime
from pathlib import Path


# ── Paths ──────────────────────────────────────────────────────────────────────

STS2_DIR = Path(__file__).parent.parent.parent.parent.resolve()
MOD_DIR = STS2_DIR / "mods" / "TokenSpire2"
MOD_CSPROJ = MOD_DIR / "TokenSpire2.csproj"
LLM_DATA = MOD_DIR / "llm_data"
BATTLE_DIR = LLM_DATA / "battles"
DECISION_DIR = LLM_DATA / "decisions"
BATCH_CONFIG = MOD_DIR / "batch_config.json"
RUN_COMPLETE_SIGNAL = MOD_DIR / "run_complete.txt"
STUCK_DIAGNOSTICS = MOD_DIR / "stuck_diagnostics.json"
BATCH_RUNS_DIR = MOD_DIR / "batch_runs"

RUNNING = True

CHARACTER_ROTATION = ["IRONCLAD", "SILENT", "DEFECT", "REGENT", "NECROBINDER"]


# ── Helpers ────────────────────────────────────────────────────────────────────

def log(msg: str) -> None:
    ts = datetime.now().strftime("%H:%M:%S")
    print(f"[{ts}] {msg}", flush=True)


def generate_seed(run_num: int) -> str:
    """Generate a truly unique seed for each run using multiple entropy sources."""
    ts = datetime.now().strftime("%Y%m%d%H%M%S%f")  # microseconds for uniqueness
    rand_bytes = os.urandom(4).hex()  # system entropy
    unique = hashlib.md5(f"{ts}-{run_num}-{os.getpid()}-{rand_bytes}".encode()).hexdigest()[:12]
    return f"B{run_num:03d}{unique}".upper()


def write_batch_config(seed: str, character: str, hp_mult: float, run_number: int = 1) -> None:
    """Write batch_config.json for the mod to read."""
    config = {
        "Seed": seed,
        "Character": character,
        "HpMultiplier": hp_mult,
        "RunNumber": str(run_number),
    }
    BATCH_CONFIG.write_text(json.dumps(config, indent=2), encoding="utf-8")
    log(f"Wrote batch_config: seed={seed} char={character} hp={hp_mult} run={run_number}")


# ── Build ──────────────────────────────────────────────────────────────────────

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
        lines = (result.stdout + result.stderr).splitlines()
        for line in lines[-20:]:
            print(f"  {line}")
        return False


# ── Game process management ────────────────────────────────────────────────────

def find_game_process() -> bool:
    """Check if SlayTheSpire2.exe is actually running (not just the Steam DRM zombie).

    The Steam DRM stub is ~36K with 0 CPU time. The real game process uses
    500MB+ of memory. We filter out processes with < 10MB to avoid the zombie.
    """
    try:
        result = subprocess.run(
            ["tasklist", "/fi", "IMAGENAME eq SlayTheSpire2.exe", "/fo", "csv", "/nh"],
            capture_output=True, text=True, timeout=10,
        )
        lower_stdout = result.stdout.lower()
        if "slaythespire2.exe" not in lower_stdout:
            return False
        # Parse memory usage — real game uses 500MB+, zombie is ~36K
        for line in result.stdout.strip().split("\n"):
            if "slaythespire2.exe" in line.lower():
                parts = line.replace('"', '').split(",")
                if len(parts) >= 5:
                    mem_str = parts[4].strip().replace(" K", "").replace(",", "")
                    try:
                        mem_kb = int(mem_str)
                        if mem_kb < 10000:  # < 10MB = zombie, not real game
                            continue
                        return True
                    except ValueError:
                        return True  # Can't parse — assume it's real
        return False
    except Exception:
        return False


def delete_stale_saves() -> None:
    """Delete current_run.save to prevent 'continue run?' prompt on launch.
    No save file → no stale run → game starts fresh directly."""
    save_dir = Path(os.environ.get('APPDATA', '')) / 'SlayTheSpire2' / 'steam'
    if not save_dir.exists():
        return
    for save_file in save_dir.rglob('current_run.save*'):
        try:
            save_file.unlink()
            log(f"Deleted stale save: {save_file}")
        except Exception as e:
            log(f"WARNING: could not delete {save_file}: {e}")


def kill_game() -> None:
    """Forcefully kill ALL SlayTheSpire2.exe processes — no survivors."""
    log("Killing ALL game processes...")
    killed_any = False

    # Method 1: cmd taskkill /f (most reliable on Windows)
    for attempt in range(3):
        try:
            result = subprocess.run(
                ["cmd", "/c", "taskkill /f /im SlayTheSpire2.exe"],
                capture_output=True, text=True, timeout=15,
            )
            if "SUCCESS" in result.stdout:
                killed_any = True
        except Exception:
            pass
        time.sleep(1)

    # Method 2: PowerShell Stop-Process
    try:
        subprocess.run(
            ["powershell", "-Command",
             "Get-Process -Name SlayTheSpire2 -ErrorAction SilentlyContinue | Stop-Process -Force"],
            capture_output=True, timeout=15,
        )
    except Exception:
        pass

    # Method 3: wmic teardown (nuclear option for zombies)
    try:
        subprocess.run(
            ["cmd", "/c", "wmic process where \"name='SlayTheSpire2.exe'\" delete 2>nul"],
            capture_output=True, timeout=15,
        )
    except Exception:
        pass

    # Verify cleanup
    time.sleep(2)
    if find_game_process():
        log("WARNING: SlayTheSpire2.exe still running after kill attempts!")
    else:
        log("All game processes terminated.")


def find_steam() -> Path | None:
    """Find Steam.exe path. Checks common install locations."""
    candidates = [
        Path("E:/Steam/steam.exe"),
        Path("C:/Program Files (x86)/Steam/steam.exe"),
        Path("D:/Steam/steam.exe"),
    ]
    for p in candidates:
        if p.exists():
            return p
    # Try registry
    try:
        import winreg
        for hkey in (winreg.HKEY_LOCAL_MACHINE, winreg.HKEY_CURRENT_USER):
            for subkey in (r"SOFTWARE\Valve\Steam", r"SOFTWARE\Wow6432Node\Valve\Steam"):
                try:
                    with winreg.OpenKey(hkey, subkey) as key:
                        val, _ = winreg.QueryValueEx(key, "InstallPath")
                        steam_exe = Path(val) / "steam.exe"
                        if steam_exe.exists():
                            return steam_exe
                except OSError:
                    pass
    except Exception:
        pass
    return None


def steam_is_running() -> bool:
    """Check if Steam.exe is currently running."""
    try:
        result = subprocess.run(
            ["tasklist", "/fi", "IMAGENAME eq Steam.exe", "/fo", "csv", "/nh"],
            capture_output=True, text=True, timeout=10,
        )
        return "steam.exe" in result.stdout.lower()
    except Exception:
        return False

def game_is_running() -> bool:
    """Check if SlayTheSpire2.exe is currently running."""
    try:
        result = subprocess.run(
            ["tasklist", "/fi", "IMAGENAME eq SlayTheSpire2.exe", "/fo", "csv", "/nh"],
            capture_output=True, text=True, timeout=10,
        )
        return "slaythespire2.exe" in result.stdout.lower()
    except Exception:
        return False


def launch_game() -> bool:
    """Launch STS2 via Steam. Returns True if launch was attempted."""
    # Verify Steam is running before attempting launch
    if not steam_is_running():
        log("Steam is NOT running. Attempting to start Steam first...")
        steam_path = find_steam()
        if steam_path:
            log(f"Starting Steam: {steam_path}")
            subprocess.Popen(
                [str(steam_path)],
                stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
            )
            log("Waiting for Steam to initialize (15s)...")
            time.sleep(15)
            if not steam_is_running():
                log("ERROR: Steam failed to start!")
                return False
        else:
            log("ERROR: Cannot find Steam.exe!")
            return False

    log("Launching STS2 via Steam...")
    # Method 1: os.startfile (reliable — opens steam:// via Windows shell)
    try:
        os.startfile("steam://rungameid/2868840")
        return True
    except Exception as e:
        log(f"  os.startfile failed: {e}")

    # Method 2: steam.exe -applaunch (fallback)
    steam_path = find_steam()
    if steam_path:
        log(f"  Fallback: {steam_path} -applaunch 2868840")
        try:
            subprocess.Popen(
                [str(steam_path), "-applaunch", "2868840"],
                stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
            )
            return True
        except Exception as e:
            log(f"  steam -applaunch failed: {e}")

    # Method 3: cmd /c start (last resort)
    try:
        subprocess.Popen(
            ["cmd", "/c", "start", "", "steam://rungameid/2868840"],
            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
        )
        return True
    except Exception as e:
        log(f"Launch failed: {e}")
        return False
    except Exception as e:
        log(f"Launch failed: {e}")
        return False


# ── Battle tracking ────────────────────────────────────────────────────────────

def count_battles_in_dir(directory: Path) -> int:
    """Count battle JSON files in a directory."""
    if not directory.exists():
        return 0
    return len(list(directory.glob("battle_*.json")))


# ── Run archival ───────────────────────────────────────────────────────────────

def archive_run(run_num: int, seed: str, character: str) -> Path:
    """Copy battle and decision logs to batch_runs/run_NNN/."""
    archive_dir = BATCH_RUNS_DIR / f"run_{run_num:03d}"
    archive_dir.mkdir(parents=True, exist_ok=True)

    # Copy battle logs
    battle_archive = archive_dir / "battles"
    battle_archive.mkdir(exist_ok=True)
    battle_count = 0
    if BATTLE_DIR.exists():
        for f in BATTLE_DIR.glob("battle_*.json"):
            dest = battle_archive / f.name
            shutil.copy2(f, dest)
            battle_count += 1

    # Copy decision logs
    decision_archive = archive_dir / "decisions"
    decision_archive.mkdir(exist_ok=True)
    decision_count = 0
    if DECISION_DIR.exists():
        for f in DECISION_DIR.glob("decisions_run_*.json"):
            dest = decision_archive / f.name
            shutil.copy2(f, dest)
            decision_count += 1

    log(f"Archived {battle_count} battles + {decision_count} decision logs to {archive_dir.name}")

    # Also copy the latest decision log (decisions_run_NNN.json)
    return archive_dir


def clear_live_logs() -> None:
    """Clear llm_data between runs so each run has isolated logs."""
    if BATTLE_DIR.exists():
        for f in BATTLE_DIR.glob("battle_*.json"):
            f.unlink()
    if DECISION_DIR.exists():
        for f in DECISION_DIR.glob("decisions_run_*.json"):
            f.unlink()


# ── Summary ────────────────────────────────────────────────────────────────────

def classify_run_termination(ended_normally: bool, battles: list[dict],
                              stuck_diagnostics: dict | None) -> str:
    """
    Classify HOW the run ended. This is critical for the optimization loop
    to distinguish between:
      - NORMAL_VICTORY: run completed with clear battles
      - NORMAL_DEATH: player died in combat
      - STUCK_COMBAT: mod killed game (30s no activity in combat)
      - STUCK_NONCOMBAT: mod killed game (45s same screen)
      - TIMEOUT_NO_BATTLES: no battles recorded (game stuck on menu)
      - TIMEOUT_STUCK: batch runner killed game (stuck_timeout no activity)
      - TIMEOUT_MAX: max_time reached
      - PROCESS_DIED: game process exited unexpectedly
      - LAUNCH_FAILED: game never started
      - ZERO_BATTLES: game ran but no battles (Steam launch failure)
    """
    if stuck_diagnostics:
        stuck_type = stuck_diagnostics.get("stuck_type", "UNKNOWN")
        if stuck_type == "COMBAT_STUCK":
            # Provide rich context for debugging
            hand_count = stuck_diagnostics.get("hand_count", 0)
            energy = stuck_diagnostics.get("player_energy", 0)
            alive_enemies = stuck_diagnostics.get("alive_enemy_count", 0)
            turn = stuck_diagnostics.get("combat_turn_number", 0)
            has_plan = stuck_diagnostics.get("has_combat_plan", False)
            empty_retries = stuck_diagnostics.get("empty_solve_retries", 0)
            top_overlay = stuck_diagnostics.get("top_overlay", "none")
            return (f"STUCK_COMBAT (hand={hand_count} energy={energy} "
                    f"enemies={alive_enemies} turn={turn} "
                    f"plan={has_plan} retries={empty_retries} "
                    f"overlay={top_overlay})")
        elif stuck_type == "NONCOMBAT_STUCK":
            screen = stuck_diagnostics.get("detected_screen", "?")
            last_screen = stuck_diagnostics.get("last_screen_type", "?")
            overlay = stuck_diagnostics.get("top_overlay", "none")
            return f"STUCK_NONCOMBAT (screen={screen} last={last_screen} overlay={overlay})"
        else:
            return f"STUCK_{stuck_type}"

    if not ended_normally:
        if not battles:
            return "TIMEOUT_NO_BATTLES"
        # Check if we have battles but game was killed by batch_runner stuck detection
        return "PROCESS_DIED"

    if not battles:
        return "ZERO_BATTLES"

    # Normal termination: check if death or victory ended it
    last_battle = battles[-1] if battles else {}
    if last_battle.get("Victory"):
        return "NORMAL_VICTORY"
    else:
        return "NORMAL_DEATH"


def write_run_summary(archive_dir: Path, run_num: int, seed: str,
                       character: str, hp_mult: float,
                       duration: float, ended_normally: bool,
                       battles: list[dict]) -> dict:
    """Write per-run summary and return it."""
    # Read stuck diagnostics if present
    stuck_diag = None
    if STUCK_DIAGNOSTICS.exists():
        try:
            stuck_diag = json.loads(STUCK_DIAGNOSTICS.read_text(encoding="utf-8"))
        except Exception:
            pass

    termination = classify_run_termination(ended_normally, battles, stuck_diag)

    wins = sum(1 for b in battles if b.get("Victory"))
    losses = len(battles) - wins
    summary = {
        "run_num": run_num,
        "seed": seed,
        "character": character,
        "hp_multiplier": hp_mult,
        "duration_seconds": round(duration, 1),
        "ended_normally": ended_normally,
        "termination": termination,
        "total_battles": len(battles),
        "wins": wins,
        "losses": losses,
        "win_rate": wins / len(battles) if battles else 0,
        "total_damage_taken": sum(b.get("TotalDamageTaken", 0) for b in battles),
        "total_turns": sum(b.get("TotalTurns", 0) for b in battles),
        "encounters": [b.get("EncounterName", "?") for b in battles],
        "timestamp": datetime.now().isoformat(),
        "stuck_diagnostics": stuck_diag,  # include full diagnostics for analysis
    }
    summary_path = archive_dir / "summary.json"
    summary_path.write_text(json.dumps(summary, indent=2), encoding="utf-8")
    return summary


# ── Character rotation helpers ──────────────────────────────────────────────────

def _check_act1_boss_victory(summary: dict) -> int:
    """
    Check if a run summary indicates the Act 1 boss was beaten.
    Returns the boss floor number if found, 0 otherwise.
    Act 1 boss is typically at floor 16-17.
    """
    encounters = summary.get("encounters", [])
    # The summary's "wins" field tells us total wins, but we need to know
    # if any win was at floor >= 16 (Act 1 boss range).
    # Check the archived battle files for a victory at high floor.
    # Since summary only has encounter names, check the archived battles.
    run_num = summary.get("run_num", 0)
    archive_dir = BATCH_RUNS_DIR / f"run_{run_num:03d}"
    battles_dir = archive_dir / "battles"
    if not battles_dir.exists():
        return 0

    for battle_file in sorted(battles_dir.glob("battle_*.json")):
        try:
            b = json.loads(battle_file.read_text(encoding="utf-8"))
            floor = b.get("Floor", 0)
            victory = b.get("Victory", False)
            encounter = b.get("Encounter", "")
            # Act 1 boss floor is 16-17. Check for victory at boss floor.
            if floor >= 16 and victory:
                # Verify it's actually a boss encounter
                if "boss" in encounter.lower() or "guardian" in encounter.lower() or \
                   "slime" in encounter.lower() or "hexaghost" in encounter.lower():
                    return floor
        except Exception:
            pass
    return 0


# ── Run one session ────────────────────────────────────────────────────────────

def run_one_session(run_num: int, character: str, hp_mult: float,
                     max_time: int, stuck_timeout: int,
                     seed: str = None) -> dict:
    """
    Run one complete game session.
    Returns a summary dict with results.
    """
    global RUNNING

    if seed is None:
        seed = generate_seed(run_num)
    log(f"\n{'='*60}")
    log(f"BATCH RUN {run_num} | Seed: {seed} | Character: {character}")
    log(f"{'='*60}")

    # Nuclear cleanup: kill zombies + delete stale saves
    log("Pre-launch cleanup...")
    kill_game()
    time.sleep(1)
    delete_stale_saves()
    time.sleep(2)

    # Write config for the mod
    write_batch_config(seed, character, hp_mult, run_num)

    # Clear previous run's live logs
    clear_live_logs()

    # Launch game
    if not launch_game():
        return {"run_num": run_num, "seed": seed, "error": "launch_failed"}

    # Wait for game process to start
    start_time = time.time()
    game_started = False
    for _ in range(45):  # up to 45 seconds
        time.sleep(1)
        if find_game_process():
            game_started = True
            break

    if not game_started:
        log("ERROR: Game process never appeared!")
        return {"run_num": run_num, "seed": seed, "error": "game_not_started"}

    log(f"Game started after {time.time() - start_time:.0f}s")
    time.sleep(5)  # Let BepInEx initialize and mod load

    # Delete any stale signals from previous run
    if RUN_COMPLETE_SIGNAL.exists():
        RUN_COMPLETE_SIGNAL.unlink()
    if STUCK_DIAGNOSTICS.exists():
        STUCK_DIAGNOSTICS.unlink()

    # Start monitoring — watch for run_complete.txt signal from the mod
    last_battle_count = 0
    last_activity = time.time()
    session_start = time.time()
    ended_normally = False
    exit_reason = "unknown"  # will be set to a specific reason
    # Track floor progression to detect death loops
    floor_battle_counts = {}  # floor -> count of battle files in current LIFE
    max_floor_seen = 0
    life_count = 0  # number of run restarts detected
    prev_battle_files = set()  # track known battle files to detect new ones

    while RUNNING:
        now = time.time()
        elapsed = now - session_start

        # Timeout
        if elapsed > max_time:
            log(f"Max time ({max_time}s) reached — ending run")
            exit_reason = "TIMEOUT_MAX"
            break

        # Check for stuck_diagnostics.json FIRST — if the mod killed itself
        # due to internal stuck detection, we want to capture that reason.
        if STUCK_DIAGNOSTICS.exists():
            try:
                diag = json.loads(STUCK_DIAGNOSTICS.read_text(encoding="utf-8"))
                stuck_type = diag.get("stuck_type", "UNKNOWN")
                log(f"STUCK DIAGNOSTICS: {stuck_type} — mod killed game internally")
            except Exception:
                log("STUCK DIAGNOSTICS: file found but unreadable")
            time.sleep(2)  # Let final logs write
            exit_reason = "STUCK_INTERNAL"
            break

        # Game still running?
        if not find_game_process():
            log("Game process exited")
            exit_reason = "PROCESS_EXITED"
            break

        # Check for run_complete.txt signal from the mod
        if RUN_COMPLETE_SIGNAL.exists():
            log("Run complete signal detected from mod")
            time.sleep(2)  # Let final logs write
            exit_reason = "RUN_COMPLETE_SIGNAL"
            ended_normally = True
            break

        # Battle progress tracking
        current_battles = count_battles_in_dir(BATTLE_DIR)
        if current_battles > last_battle_count:
            last_battle_count = current_battles
            last_activity = now
            log(f"  Battles recorded: {current_battles}")

            # Track floor progression to detect death loops
            try:
                current_files = set(BATTLE_DIR.glob("battle_*.json"))
                new_files = current_files - prev_battle_files
                prev_battle_files = current_files

                for f in sorted(new_files):
                    try:
                        b = json.loads(f.read_text(encoding="utf-8"))
                        floor = b.get("Floor", 0)

                        # Detect run restart: new battle on a floor we've already passed
                        # If floor goes back to 1-3 after we've seen higher floors,
                        # the bot died and restarted — reset counts for a new life.
                        if floor <= 3 and max_floor_seen > 5:
                            life_count += 1
                            log(f"  [LIFE {life_count}] Run restart detected (floor {floor} after max {max_floor_seen})")
                            floor_battle_counts.clear()
                            max_floor_seen = 0

                        floor_battle_counts[floor] = floor_battle_counts.get(floor, 0) + 1
                        if floor > max_floor_seen:
                            max_floor_seen = floor
                            log(f"  Max floor reached: {max_floor_seen}")
                    except Exception:
                        pass
            except Exception:
                pass

        # Stuck detection (no new battles for too long)
        if current_battles > 0 and (now - last_activity) > stuck_timeout:
            log(f"Stuck: no activity for {stuck_timeout}s ({current_battles} battles)")
            exit_reason = f"TIMEOUT_STUCK_{stuck_timeout}s"
            break

        # If no battles at all for a long time, something is wrong.
        # The game can take 60-120s to get past the main menu (abandon stale
        # save, select character, start run, Neow's blessing). Use a generous
        # timeout to avoid killing the game during legitimate startup.
        if current_battles == 0 and elapsed > 150:
            log("No battles recorded for 150s — game may be stuck on menu")
            exit_reason = "TIMEOUT_NO_BATTLES_150s"
            break

        # Death loop detection: if any floor fought >8 times in same life, kill it
        if floor_battle_counts and max(floor_battle_counts.values(), default=0) > 8:
            log(f"DEATH LOOP detected: floor(s) fought too many times: {dict(floor_battle_counts)}")
            exit_reason = "DEATH_LOOP"
            break

        time.sleep(3)

    # Kill game
    kill_game()
    time.sleep(2)

    # Collect battle results
    battles = []
    if BATTLE_DIR.exists():
        for f in sorted(BATTLE_DIR.glob("battle_*.json")):
            try:
                battles.append(json.loads(f.read_text(encoding="utf-8")))
            except Exception:
                pass

    # Archive
    archive_dir = archive_run(run_num, seed, character)
    summary = write_run_summary(archive_dir, run_num, seed, character,
                                 hp_mult, time.time() - session_start,
                                 ended_normally, battles)
    summary["exit_reason"] = exit_reason  # from monitoring loop

    # Print run results with termination classification
    wins = summary["wins"]
    total = summary["total_battles"]
    dmg = summary["total_damage_taken"]
    turns = summary["total_turns"]
    termination = summary.get("termination", "?")
    log(f"Run {run_num} done: {wins}W/{total-wins}L ({total} battles), "
        f"{dmg} dmg taken, {turns} turns, {summary['duration_seconds']:.0f}s")
    log(f"  Termination: {termination} | Exit: {exit_reason}")

    return summary


# ── Aggregate report ────────────────────────────────────────────────────────────

def write_aggregate_report(summaries: list[dict]) -> str:
    """Write the final aggregate report for all batch runs."""
    totals = [s for s in summaries if "error" not in s]
    errored = [s for s in summaries if "error" in s]

    lines = []
    lines.append("# TokenSpire2 Batch Test Report")
    lines.append(f"**Generated:** {datetime.now().isoformat()}")
    lines.append(f"**Total runs:** {len(summaries)} ({len(totals)} completed, {len(errored)} errors)")
    lines.append("")

    if not totals:
        lines.append("No successful runs to analyze.")
        return "\n".join(lines)

    # Aggregate stats
    total_battles = sum(s["total_battles"] for s in totals)
    total_wins = sum(s["wins"] for s in totals)
    total_losses = sum(s["losses"] for s in totals)
    total_dmg = sum(s["total_damage_taken"] for s in totals)
    total_turns = sum(s["total_turns"] for s in totals)
    avg_duration = sum(s["duration_seconds"] for s in totals) / len(totals)

    lines.append("## 1. Aggregate Statistics")
    lines.append("")
    lines.append(f"| Metric | Value |")
    lines.append(f"|--------|-------|")
    lines.append(f"| Completed Runs | {len(totals)} |")
    lines.append(f"| Errored Runs | {len(errored)} |")
    lines.append(f"| Total Battles | {total_battles} |")
    lines.append(f"| Wins | {total_wins} |")
    lines.append(f"| Losses | {total_losses} |")
    lines.append(f"| **Overall Win Rate** | **{100*total_wins/total_battles:.1f}%** |" if total_battles > 0 else "")
    lines.append(f"| Avg Battles/Run | {total_battles/len(totals):.1f} |")
    lines.append(f"| Total Damage Taken | {total_dmg} |")
    lines.append(f"| Avg Damage/Battle | {total_dmg/total_battles:.0f}" if total_battles > 0 else "")
    lines.append(f"| Total Turns | {total_turns} |")
    lines.append(f"| Avg Turns/Battle | {total_turns/total_battles:.1f}" if total_battles > 0 else "")
    lines.append(f"| Avg Run Duration | {avg_duration:.0f}s |")
    lines.append("")

    # Per-run breakdown
    lines.append("## 2. Per-Run Breakdown")
    lines.append("")
    lines.append("| Run | Seed | Battles | Wins | Losses | Win% | Dmg Taken | Turns | Duration | Termination |")
    lines.append("|-----|------|---------|------|--------|------|-----------|-------|----------|-------------|")
    for s in totals:
        b = s["total_battles"]
        wr = f"{100*s['wins']/b:.0f}%" if b > 0 else "N/A"
        term = s.get("termination", "?")
        # Truncate long termination strings for table
        term_short = term[:40] + "…" if len(term) > 40 else term
        lines.append(
            f"| {s['run_num']} | {s['seed'][:12]}... | {b} | {s['wins']} | {s['losses']} | "
            f"{wr} | {s['total_damage_taken']} | {s['total_turns']} | {s['duration_seconds']:.0f}s | "
            f"{term_short} |"
        )
    lines.append("")

    # ── Termination analysis ──
    if termination_counter:
        lines.append("## 3. Termination Analysis")
        lines.append("")
        lines.append("| Category | Count | % of Runs |")
        lines.append("|----------|-------|-----------|")
        for cat, count in termination_counter.most_common():
            pct = f"{100*count/len(summaries):.0f}%" if summaries else "N/A"
            flag = "⚠️" if cat.startswith("STUCK_") or cat in (
                "TIMEOUT_NO_BATTLES", "TIMEOUT_STUCK", "PROCESS_DIED",
                "LAUNCH_FAILED", "ZERO_BATTLES"
            ) else ""
            lines.append(f"| {cat} {flag} | {count} | {pct} |")
        lines.append("")

    # Encounter analysis
    lines.append("## 4. Encounter Analysis")
    lines.append("")
    from collections import Counter
    encounter_wins = Counter()
    encounter_total = Counter()
    encounter_dmg = Counter()
    encounter_turns = Counter()

    for s in totals:
        archive_dir = BATCH_RUNS_DIR / f"run_{s['run_num']:03d}" / "battles"
        if archive_dir.exists():
            for f in archive_dir.glob("battle_*.json"):
                try:
                    b = json.loads(f.read_text(encoding="utf-8"))
                    name = b.get("EncounterName", "?")
                    encounter_total[name] += 1
                    if b.get("Victory"):
                        encounter_wins[name] += 1
                    encounter_dmg[name] += b.get("TotalDamageTaken", 0)
                    encounter_turns[name] += b.get("TotalTurns", 0)
                except Exception:
                    pass

    if encounter_total:
        lines.append("| Encounter | Count | Wins | Losses | Win% | Avg Dmg | Avg Turns |")
        lines.append("|-----------|-------|------|--------|------|---------|-----------|")
        for name in sorted(encounter_total.keys()):
            total = encounter_total[name]
            wins = encounter_wins[name]
            losses = total - wins
            wr = f"{100*wins/total:.0f}%" if total > 0 else "N/A"
            avg_dmg = f"{encounter_dmg[name]/total:.0f}" if total > 0 else "?"
            avg_turns = f"{encounter_turns[name]/total:.1f}" if total > 0 else "?"
            flag = "⚠️" if (total >= 2 and wins / total < 0.5) else ""
            lines.append(
                f"| {name} | {total} | {wins} | {losses} | {wr} {flag} | {avg_dmg} | {avg_turns} |"
            )
    lines.append("")

    # Loss analysis
    lines.append("## 5. Loss Analysis")
    lines.append("")

    # Collect all loss battles
    loss_encounters = Counter()
    for s in totals:
        archive_dir = BATCH_RUNS_DIR / f"run_{s['run_num']:03d}" / "battles"
        if archive_dir.exists():
            for f in archive_dir.glob("battle_*.json"):
                try:
                    b = json.loads(f.read_text(encoding="utf-8"))
                    if not b.get("Victory"):
                        loss_encounters[b.get("EncounterName", "?")] += 1
                except Exception:
                    pass

    if loss_encounters:
        lines.append("### Enemies that caused losses:")
        for name, count in loss_encounters.most_common():
            lines.append(f"- **{name}**: {count} losses")
    else:
        lines.append("No losses! 🎉")
    lines.append("")

    # Recommendations
    lines.append("## 6. Recommendations")
    lines.append("")

    if total_battles > 0:
        wr = 100 * total_wins / total_battles
        if wr < 80:
            lines.append("- 🔴 **Win rate below 80%** — solver needs significant improvement")
            lines.append("  - Review loss battles and identify common patterns")
            lines.append("  - Check CardEffectReader coverage for new cards")
        elif wr < 90:
            lines.append("- 🟡 **Win rate {:.0f}%** — room for improvement".format(wr))
            lines.append("  - Focus on specific loss encounters")
        else:
            lines.append(f"- 🟢 **Win rate {wr:.0f}%** — solid performance")

    # Highlight problematic encounters
    problem_encounters = []
    for name in encounter_total:
        total = encounter_total[name]
        wins = encounter_wins[name]
        if total >= 2 and wins / total < 0.5:
            problem_encounters.append((name, 100*wins/total, total))
    if problem_encounters:
        lines.append("- 🔴 **Problem encounters (<50% win rate):**")
        for name, wr, count in sorted(problem_encounters, key=lambda x: x[1]):
            lines.append(f"  - {name}: {wr:.0f}% ({count} battles)")
            lines.append(f"    → Consider encounter-specific strategies or card priority tuning")

    lines.append("")
    lines.append("---")
    lines.append(f"*Report generated from {len(totals)} batch runs on {datetime.now().strftime('%Y-%m-%d %H:%M')}*")

    report = "\n".join(lines)
    report_path = BATCH_RUNS_DIR / "aggregate_report.md"
    report_path.write_text(report, encoding="utf-8")
    log(f"Aggregate report saved: {report_path}")

    # Termination statistics for interruption analysis
    from collections import Counter
    termination_counter = Counter()
    for s in totals:
        termination_counter[s.get("termination", "?")] += 1
    for s in errored:
        termination_counter["ERROR"] += 1

    # Also write machine-readable summary
    batch_summary = {
        "total_runs": len(summaries),
        "completed_runs": len(totals),
        "errored_runs": len(errored),
        "total_battles": total_battles,
        "total_wins": total_wins,
        "total_losses": total_losses,
        "win_rate": total_wins / total_battles if total_battles > 0 else 0,
        "total_damage_taken": total_dmg,
        "total_turns": total_turns,
        "avg_duration": avg_duration,
        "per_run": totals,
        "errors": errored,
        "termination_stats": dict(termination_counter),
        "encounter_stats": {
            name: {
                "total": encounter_total[name],
                "wins": encounter_wins[name],
                "losses": encounter_total[name] - encounter_wins[name],
                "avg_damage": encounter_dmg[name] / encounter_total[name] if encounter_total[name] > 0 else 0,
                "avg_turns": encounter_turns[name] / encounter_total[name] if encounter_total[name] > 0 else 0,
            }
            for name in encounter_total
        },
        "timestamp": datetime.now().isoformat(),
    }
    summary_path = BATCH_RUNS_DIR / "batch_summary.json"
    summary_path.write_text(json.dumps(batch_summary, indent=2), encoding="utf-8")

    return report


# ── Main ───────────────────────────────────────────────────────────────────────

def signal_handler(sig, frame):
    global RUNNING
    RUNNING = False
    log("\nShutting down gracefully... (press Ctrl+C again to force quit)")


def main():
    global RUNNING
    signal.signal(signal.SIGINT, signal_handler)
    signal.signal(signal.SIGTERM, signal_handler)

    parser = argparse.ArgumentParser(
        description="TokenSpire2 single-run test — ONE run per invocation. "
                    "The optimizer calls this repeatedly.")
    parser.add_argument("--character", type=str, default="IRONCLAD",
                        choices=["IRONCLAD", "SILENT", "DEFECT", "REGENT", "NECROBINDER"],
                        help="Character class (default: IRONCLAD)")
    parser.add_argument("--hp", type=float, default=1.0,
                        help="HP multiplier (default: 1.0)")
    parser.add_argument("--max-time", type=int, default=600,
                        help="Max seconds for the run (default: 600 = 10 min)")
    parser.add_argument("--stuck-timeout", type=int, default=180,
                        help="Seconds without new battle before declaring stuck (default: 180)")
    parser.add_argument("--no-build", action="store_true",
                        help="Skip building the mod before running")
    parser.add_argument("--run-num", type=int, default=1,
                        help="Run number label for output dir naming (default: 1)")
    parser.add_argument("--seed", type=str, default=None,
                        help="Specific seed to use (default: random)")
    args = parser.parse_args()

    log("╔══════════════════════════════════════════╗")
    log("║  TokenSpire2 Single Run Test             ║")
    log("╚══════════════════════════════════════════╝")
    log(f"Config: character={args.character} hp={args.hp}")
    log(f"Max time: {args.max_time}s  Stuck timeout: {args.stuck_timeout}s")
    log(f"Output: {BATCH_RUNS_DIR}")

    BATCH_RUNS_DIR.mkdir(parents=True, exist_ok=True)

    # Build once (if needed)
    if not args.no_build:
        if not build_mod():
            log("Build failed — aborting")
            sys.exit(1)

    # ── Run exactly ONE session ──
    max_retries = 3
    summary = None
    for attempt in range(max_retries):
        if attempt > 0:
            log(f"  ⚠️ Retry {attempt}/{max_retries-1}...")
            time.sleep(10)

        summary = run_one_session(
            run_num=args.run_num,
            character=args.character,
            hp_mult=args.hp,
            max_time=args.max_time,
            stuck_timeout=args.stuck_timeout,
            seed=args.seed,
        )

        # Check if this looks like a launch failure
        is_error = "error" in summary
        zero_battles = summary.get("total_battles", 0) == 0
        quick_exit = summary.get("duration_seconds", 0) < 60

        if is_error:
            log(f"  Run error: {summary.get('error', 'unknown')} — retrying...")
            continue
        elif zero_battles and quick_exit:
            log(f"  Run had 0 battles ({summary['duration_seconds']:.0f}s) — likely launch failure, retrying...")
            continue
        else:
            break

    if summary is None:
        log("FATAL: All retries exhausted")
        sys.exit(1)

    # ── Write single-run aggregate ──
    all_summaries = [summary]
    write_aggregate_report(all_summaries)

    # Print summary
    if "error" not in summary:
        b = summary["total_battles"]
        w = summary["wins"]
        l = summary["losses"]
        wr = f"{100*w/b:.0f}%" if b > 0 else "N/A"
        term = summary.get("termination", "?")
        log(f"\nRun complete: {w}W/{l}L ({b} battles) — {wr}")
        log(f"Termination: {term}")
    else:
        log(f"\nRun failed: {summary.get('error', 'unknown')}")

    # Exit with non-zero if the game never launched successfully
    if summary.get("total_battles", 0) == 0:
        sys.exit(1)


if __name__ == "__main__":
    main()
