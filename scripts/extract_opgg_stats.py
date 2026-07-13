#!/usr/bin/env python3
"""
Parse E:\STS2_OPGG_Stats_Report.md and extract card/relic/boss-relic stats
into JSON files that the C# mod can load at runtime.
"""
import json, re, os
from pathlib import Path

REPORT = Path(__file__).parent.parent.parent.parent / "STS2_OPGG_Stats_Report.md"
OUT_DIR = Path(__file__).parent.parent / "llm_data" / "opgg_stats"
OUT_DIR.mkdir(parents=True, exist_ok=True)

# Class name mapping: report header → short key
CLASS_MAP = {
    "Ironclad": "ironclad",
    "Silent": "silent",
    "Defect": "defect",
    "Necrobinder": "necrobinder",
    "Regent": "regent",
    "Colorless": "colorless",
}

def parse_pct(s: str) -> float:
    """Parse '34.2%' → 0.342, '+0.043' → 0.043, '-0.065' → -0.065"""
    s = s.strip().rstrip("%")
    return float(s) / 100.0 if not s.startswith(("+", "-")) else float(s)

def parse_card_table(lines, start_idx):
    """Parse a card markdown table. Returns list of dicts.
    Format varies:
      Pick Rate sorted: | # | Card | Type | Cost | Rarity | Pick Rate | Win Rate | WR Impact | Offered | Picked |
      Win Rate sorted:  | # | Card | Type | Cost | Rarity | Win Rate | Pick Rate | WR Impact | Offered |
    """
    cards = []
    in_table = False
    is_winrate_sorted = False
    for i in range(start_idx, len(lines)):
        line = lines[i].strip()
        if line.startswith("| # | Card | Type"):
            in_table = True
            # Detect column order from header
            is_winrate_sorted = "Win Rate" in line.split("|")[4] if len(line.split("|")) > 4 else False
            continue
        if in_table:
            if not line.startswith("|"):
                break
            parts = [p.strip() for p in line.split("|")[1:-1]]
            if len(parts) < 7:
                continue
            try:
                card_id = parts[1]
                if is_winrate_sorted:
                    # Cols: #, Card, Type, Cost, Rarity, Win Rate, Pick Rate, WR Impact, Offered
                    win_rate = parse_pct(parts[5])
                    pick_rate = parse_pct(parts[6])
                    wr_impact = parse_pct(parts[7])
                    offered = int(parts[8].replace(",", "")) if len(parts) >= 9 else 0
                    picked = 0
                else:
                    # Cols: #, Card, Type, Cost, Rarity, Pick Rate, Win Rate, WR Impact, Offered, Picked
                    pick_rate = parse_pct(parts[5])
                    win_rate = parse_pct(parts[6])
                    wr_impact = parse_pct(parts[7])
                    offered = int(parts[8].replace(",", "")) if len(parts) >= 9 else 0
                    picked = int(parts[9].replace(",", "")) if len(parts) >= 10 else 0

                cards.append({
                    "id": card_id,
                    "winRate": round(win_rate, 4),
                    "wrImpact": round(wr_impact, 4),
                    "offered": offered,
                    "picked": picked,
                })
            except (ValueError, IndexError) as e:
                continue
    return cards

def parse_relic_table(lines, start_idx):
    """Parse a relic markdown table. Returns list of dicts."""
    relics = []
    in_table = False
    for i in range(start_idx, len(lines)):
        line = lines[i].strip()
        if line.startswith("| # | Relic | Count"):
            in_table = True
            continue
        if line.startswith("| # | Boss Relic | Count"):
            in_table = True
            continue
        if in_table:
            if not line.startswith("|"):
                break
            parts = [p.strip() for p in line.split("|")[1:-1]]
            if len(parts) < 4:
                continue
            try:
                relic_id = parts[1]
                count = int(parts[2].replace(",", ""))
                win_rate = parse_pct(parts[3])
                relics.append({
                    "id": relic_id,
                    "count": count,
                    "winRate": round(win_rate, 4),
                })
            except (ValueError, IndexError):
                continue
    return relics

def main():
    text = REPORT.read_text(encoding="utf-8")
    lines = text.splitlines()

    # Find section boundaries
    sections = []
    for i, line in enumerate(lines):
        if line.startswith("# Part "):
            sections.append((i, line.strip()))
        elif line.startswith("## ") and not line.startswith("### "):
            h = line[3:].strip()
            if h in CLASS_MAP or h.endswith(" Cards") or h in CLASS_MAP.values():
                sections.append((i, line.strip()))

    # --- Part 1: Cards ---
    card_data = {}
    current_class = None
    in_card_part = False
    for i, line in enumerate(lines):
        if line.startswith("# Part 1:"):
            in_card_part = True
            continue
        if line.startswith("# Part 2:"):
            in_card_part = False
            continue
        if not in_card_part:
            continue
        # Detect class section
        h_match = re.match(r"^## (.+) Cards$", line)
        if h_match:
            cls_name = h_match.group(1)
            if cls_name in CLASS_MAP:
                current_class = CLASS_MAP[cls_name]
                # Parse the next table (sorted by pick rate)
                cards = parse_card_table(lines, i + 2)
                if current_class not in card_data:
                    card_data[current_class] = {}
                for c in cards:
                    card_data[current_class][c["id"]] = c
        # Also handle the "Sorted by Win Rate" section - merge data
        if "Sorted by Win Rate" in line and current_class:
            wr_cards = parse_card_table(lines, i + 2)
            for c in wr_cards:
                if current_class in card_data and c["id"] in card_data[current_class]:
                    # Already have pick rate data, don't overwrite
                    pass
                else:
                    if current_class not in card_data:
                        card_data[current_class] = {}
                    card_data[current_class][c["id"]] = c

    out_card = OUT_DIR / "card_stats.json"
    out_card.write_text(json.dumps(card_data, indent=2), encoding="utf-8")
    print(f"Card stats: {out_card} ({sum(len(v) for v in card_data.values())} total across {len(card_data)} classes)")

    # --- Part 2: Relics ---
    relic_data = {}
    current_class = None
    in_relic_part = False
    for i, line in enumerate(lines):
        if line.startswith("# Part 2:"):
            in_relic_part = True
            continue
        if line.startswith("# Part 3:"):
            in_relic_part = False
            continue
        if not in_relic_part:
            continue
        h_match = re.match(r"^## (.+)$", line)
        if h_match:
            cls_name = h_match.group(1)
            if cls_name in CLASS_MAP:
                current_class = CLASS_MAP[cls_name]
                relics = parse_relic_table(lines, i + 2)
                relic_data[current_class] = {}
                for r in relics:
                    relic_data[current_class][r["id"]] = r

    out_relic = OUT_DIR / "relic_stats.json"
    out_relic.write_text(json.dumps(relic_data, indent=2), encoding="utf-8")
    print(f"Relic stats: {out_relic} ({sum(len(v) for v in relic_data.values())} total across {len(relic_data)} classes)")

    # --- Part 3: Boss Relic Swaps ---
    boss_relic_data = {}
    current_class = None
    in_boss_part = False
    for i, line in enumerate(lines):
        if line.startswith("# Part 3:"):
            in_boss_part = True
            continue
        if line.startswith("# Part 4:") or line.startswith("## Top 10"):
            in_boss_part = False
            continue
        if not in_boss_part:
            continue
        h_match = re.match(r"^## (.+)$", line)
        if h_match:
            cls_name = h_match.group(1)
            if cls_name in CLASS_MAP:
                current_class = CLASS_MAP[cls_name]
                relics = parse_relic_table(lines, i + 2)
                boss_relic_data[current_class] = {}
                for r in relics:
                    boss_relic_data[current_class][r["id"]] = r

    out_boss = OUT_DIR / "boss_relic_stats.json"
    out_boss.write_text(json.dumps(boss_relic_data, indent=2), encoding="utf-8")
    print(f"Boss relic stats: {out_boss} ({sum(len(v) for v in boss_relic_data.values())} total across {len(boss_relic_data)} classes)")

    # --- Write a combined index for easy C# loading ---
    index = {
        "totalRuns": 2249822,
        "overallWinRate": 0.2005,
        "classes": list(CLASS_MAP.values()),
        "files": {
            "cards": "card_stats.json",
            "relics": "relic_stats.json",
            "bossRelics": "boss_relic_stats.json",
        }
    }
    (OUT_DIR / "index.json").write_text(json.dumps(index, indent=2), encoding="utf-8")
    print(f"Index: {OUT_DIR / 'index.json'}")

if __name__ == "__main__":
    main()
