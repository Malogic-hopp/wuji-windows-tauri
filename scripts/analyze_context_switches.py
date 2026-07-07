#!/usr/bin/env python3
"""
Context Switch & Focus Root-Cause Analyzer
==========================================
Reads today's foreground_samples from the QuantifiedSelf SQLite database and
explains:
  1. Why context switching is high — which apps cause interruptions, switch
     patterns, time distribution.
  2. Why no continuous focus session was detected — what broke potential
     focus blocks, and which interrupter apps are most responsible.

Usage:
  python scripts/analyze_context_switches.py
  python scripts/analyze_context_switches.py --db "D:\\WUJI\\WindowsAgent\\data\\quantified_self_windows.db"
  python scripts/analyze_context_switches.py --date 2026-07-06
  python scripts/analyze_context_switches.py --top 15 --min-focus-min 10

Dependencies: Python 3.9+ (stdlib only — sqlite3, argparse, collections, datetime)
"""

from __future__ import annotations

import argparse
import os
import sqlite3
import sys
from collections import Counter, defaultdict
from dataclasses import dataclass, field
from datetime import datetime, timezone, timedelta, date, time
from typing import Optional

# Ensure UTF-8 output on Windows consoles
try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass

# ── Constants (mirror FocusMetricsCalculator thresholds) ──────────────────

DEFAULT_MIN_FOCUS_MINUTES = 10
DEFAULT_MAX_GAP_MINUTES = 3
DEFAULT_MAX_SWITCHES_PER_FOCUS = 3
SAMPLE_INTERVAL_SECONDS = 60  # typical agent polling interval

# ── Activity context classification (mirrors FocusMetricsCalculator) ─────

DEV_TOKENS = [
    "code", "codex", "devenv", "rider", "webstorm", "pycharm", "idea",
    "terminal", "windowsterminal", "powershell", "pwsh", "cmd", "git",
    "github", "dotnet", "quantifiedself", "wuji", "cursor",
]

COMM_TOKENS = [
    "wechat", "weixin", "teams", "slack", "outlook", "mail", "discord",
    "feishu", "lark",
]

ENTERTAINMENT_TOKENS = [
    "steam", "netease", "spotify", "music", "video", "player",
    "bilibili", "youtube",
]

BROWSER_TOKENS = ["msedge", "edge", "chrome", "firefox", "browser"]

PRODUCTIVITY_TOKENS = [
    "word", "excel", "powerpoint", "onenote", "notion", "obsidian",
    "zotero", "typora", "wps",
]

SYSTEM_TOKENS = ["explorer", "taskmgr", "settings", "control"]

# Browser title → context classification
BROWSER_ENTERTAINMENT_TITLES = [
    "youtube", "bilibili", "哔哩哔哩", "xiaohongshu", "小红书", "migu",
    "咪咕", "weibo", "微博", "zhiboba", "直播吧", "netflix", "twitch",
    "douyin", "抖音", "视频", "直播", "游戏",
]
BROWSER_COMM_TITLES = [
    "gmail", "outlook", "mail", "teams", "slack", "discord", "wechat",
    "微信", "飞书",
]
BROWSER_DEV_TITLES = [
    "github", "gitlab", "stack overflow", "stackoverflow", "microsoft learn",
    "docs", "documentation", "api", "nuget", "npm", "localhost",
    "openai", "codex", "developer", "devdocs", "copilot",
]


# ── Models ─────────────────────────────────────────────────────────────────

@dataclass
class Sample:
    id: int
    sample_time_utc: datetime
    process_name: str
    window_title: str
    activity_state: str
    context: str = ""  # classified ActivityContext


@dataclass
class SwitchEvent:
    """A single context switch between two active samples."""
    from_app: str
    to_app: str
    from_title: str
    to_title: str
    from_context: str
    to_context: str
    time_utc: datetime
    is_meaningful: bool  # True if context changed


@dataclass
class FocusSegment:
    """A potential focus block (continuous active samples with gaps < max_gap)."""
    start_utc: datetime
    end_utc: datetime
    app_counts: Counter = field(default_factory=Counter)
    duration_min: float = 0.0
    switch_count: int = 0
    dominant_app: str = ""
    is_fragmented: bool = False
    break_reason: str = ""  # why it ended (if not a valid focus session)


# ── Database helpers ───────────────────────────────────────────────────────


def find_db_path() -> str:
    """Auto-detect the SQLite database path."""
    candidates = [
        r"D:\WUJI\WindowsAgent\data\quantified_self_windows.db",
        os.path.expandvars(
            r"%LOCALAPPDATA%\WUJI\WindowsAgent\data\quantified_self_windows.db"
        ),
    ]
    for p in candidates:
        if os.path.exists(p):
            return p
    raise FileNotFoundError(
        "Cannot find quantified_self_windows.db. "
        "Use --db to specify the path manually."
    )


def read_samples(db_path: str, target_date: date) -> list[Sample]:
    """Read all foreground_samples for the given local date, sorted by time."""
    local_start = datetime.combine(target_date, time.min)
    local_end = datetime.combine(target_date, time.max)
    utc_start = local_start.astimezone(timezone.utc).isoformat()
    utc_end = local_end.astimezone(timezone.utc).isoformat()

    conn = sqlite3.connect(f"file:{db_path}?mode=ro", uri=True)
    conn.row_factory = sqlite3.Row
    rows = conn.execute(
        """
        SELECT id, sample_time_utc, process_name,
               COALESCE(window_title, '') AS window_title,
               idle_seconds, activity_state
        FROM foreground_samples
        WHERE sample_time_utc >= ? AND sample_time_utc <= ?
        ORDER BY sample_time_utc ASC
        """,
        (utc_start, utc_end),
    ).fetchall()
    conn.close()

    samples = []
    for r in rows:
        t = datetime.fromisoformat(r["sample_time_utc"])
        if t.tzinfo is None:
            t = t.replace(tzinfo=timezone.utc)
        samples.append(
            Sample(
                id=r["id"],
                sample_time_utc=t,
                process_name=r["process_name"],
                window_title=r["window_title"],
                activity_state=r["activity_state"],
            )
        )
    return samples


# ── Context classification ─────────────────────────────────────────────────


def normalize(name: str) -> str:
    n = name.strip().lower()
    return n[: -4] if n.endswith(".exe") else n


def contains_any(text: str, tokens: list[str]) -> bool:
    t = text.lower()
    return any(tok in t for tok in tokens)


def classify_context(process: str, title: str) -> str:
    p = normalize(process)

    if contains_any(p, BROWSER_TOKENS) or "browser" in p:
        return classify_browser_title(title)

    if contains_any(p, DEV_TOKENS):
        return "Development"
    if contains_any(p, COMM_TOKENS):
        return "Communication"
    if contains_any(p, ENTERTAINMENT_TOKENS):
        return "Entertainment"
    if contains_any(p, SYSTEM_TOKENS):
        return "System"
    if contains_any(p, PRODUCTIVITY_TOKENS):
        return "Productivity"
    return "Other"


def classify_browser_title(title: str) -> str:
    if contains_any(title, BROWSER_ENTERTAINMENT_TITLES):
        return "Entertainment"
    if contains_any(title, BROWSER_COMM_TITLES):
        return "Communication"
    if contains_any(title, BROWSER_DEV_TITLES):
        return "Development"
    return "Research"


def short_name(process: str) -> str:
    """Human-readable short name for a process."""
    n = normalize(process)
    # Remove common suffixes
    for suf in [".exe", "-win64", "-x64", "-x86"]:
        if n.endswith(suf):
            n = n[: -len(suf)]
    return n


# ── Switch analysis ────────────────────────────────────────────────────────


def detect_switches(active_samples: list[Sample]) -> list[SwitchEvent]:
    """Detect all context switches between consecutive active samples."""
    switches = []
    for i in range(1, len(active_samples)):
        prev = active_samples[i - 1]
        cur = active_samples[i]
        app_changed = prev.process_name.lower() != cur.process_name.lower()
        title_changed = prev.window_title != cur.window_title
        if not app_changed and not title_changed:
            continue  # no switch
        switches.append(
            SwitchEvent(
                from_app=prev.process_name,
                to_app=cur.process_name,
                from_title=prev.window_title,
                to_title=cur.window_title,
                from_context=prev.context,
                to_context=cur.context,
                time_utc=cur.sample_time_utc,
                is_meaningful=prev.context != cur.context,
            )
        )
    return switches


def build_switch_pairs(switches: list[SwitchEvent]) -> Counter:
    """Count (from_context → to_context) direction pairs."""
    pairs = Counter()
    for s in switches:
        key = (s.from_context, s.to_context)
        if s.from_context != s.to_context:
            pairs[key] += 1
    return pairs


def build_app_switch_matrix(switches: list[SwitchEvent]) -> Counter:
    """Count (from_app → to_app) pairs for the top switching apps."""
    pairs = Counter()
    for s in switches:
        pairs[(short_name(s.from_app), short_name(s.to_app))] += 1
    return pairs


# ── Focus segment detection (mirrors FocusMetricsCalculator) ──────────────


def detect_focus_segments(
    active_samples: list[Sample],
    min_focus_min: int,
    max_gap_min: int,
    max_switches: int,
) -> list[FocusSegment]:
    """Partition active samples into segments separated by gaps > max_gap_min."""
    if not active_samples:
        return []

    max_gap = timedelta(minutes=max_gap_min)
    segments: list[FocusSegment] = []
    seg = FocusSegment(
        start_utc=active_samples[0].sample_time_utc,
        end_utc=active_samples[0].sample_time_utc,
        app_counts=Counter(),
    )
    seg.app_counts[short_name(active_samples[0].process_name)] += 1
    last_context = active_samples[0].context
    last_app = active_samples[0].process_name
    last_title = active_samples[0].window_title

    for i in range(1, len(active_samples)):
        s = active_samples[i]
        gap = s.sample_time_utc - seg.end_utc

        if gap > max_gap:
            # Gap too large → finalize current segment, start new one
            seg.dominant_app = seg.app_counts.most_common(1)[0][0] if seg.app_counts else ""
            dur = (seg.end_utc - seg.start_utc).total_seconds() / 60
            seg.duration_min = round(dur, 1)
            seg.is_fragmented = seg.switch_count > max_switches
            if dur < min_focus_min:
                seg.break_reason = f"duration too short ({seg.duration_min:.0f}m < {min_focus_min}m)"
            elif seg.is_fragmented:
                seg.break_reason = f"too many switches ({seg.switch_count} > {max_switches})"
            segments.append(seg)

            seg = FocusSegment(
                start_utc=s.sample_time_utc,
                end_utc=s.sample_time_utc,
                app_counts=Counter(),
            )
            seg.app_counts[short_name(s.process_name)] += 1
            last_context = s.context
            last_app = s.process_name
            last_title = s.window_title
            continue

        # Same segment: extend
        seg.end_utc = s.sample_time_utc
        seg.app_counts[short_name(s.process_name)] += 1

        if s.context != last_context:
            seg.switch_count += 1
        last_context = s.context
        last_app = s.process_name
        last_title = s.window_title

    # Finalize last segment
    seg.dominant_app = seg.app_counts.most_common(1)[0][0] if seg.app_counts else ""
    dur = (seg.end_utc - seg.start_utc).total_seconds() / 60
    seg.duration_min = round(dur, 1)
    seg.is_fragmented = seg.switch_count > max_switches
    if dur < min_focus_min:
        seg.break_reason = f"duration too short ({seg.duration_min:.0f}m < {min_focus_min}m)"
    elif seg.is_fragmented:
        seg.break_reason = f"too many switches ({seg.switch_count} > {max_switches})"
    segments.append(seg)
    return segments


# ── Interrupter analysis ──────────────────────────────────────────────────


def find_interrupters(
    switches: list[SwitchEvent], work_contexts: set[str]
) -> list[dict]:
    """
    Find which apps pull the user away from work contexts.
    Returns ranked list of interrupter apps with counts and destination contexts.
    """
    interrupter_counts: dict[str, Counter] = defaultdict(Counter)
    interrupter_details: dict[str, list[str]] = defaultdict(list)

    for s in switches:
        if s.from_context in work_contexts and s.to_context not in work_contexts:
            to_app = short_name(s.to_app)
            interrupter_counts[to_app][s.to_context] += 1
            interrupter_details[to_app].append(s.to_app)

    # Sort by total pull count descending
    ranked: list[tuple[str, Counter]] = sorted(
        interrupter_counts.items(),
        key=lambda kv: sum(kv[1].values()),
        reverse=True,
    )
    result = []
    for app, ctx_counter in ranked:
        result.append(
            {
                "app": app,
                "total_pulls": sum(ctx_counter.values()),
                "destinations": ctx_counter.most_common(),
            }
        )
    return result


def find_potential_focus_blockers(
    segments: list[FocusSegment], min_focus_min: int
) -> list[dict]:
    """
    Analyze segments that failed to become focus sessions and identify why.
    """
    blockers = []
    for seg in segments:
        if seg.break_reason:
            # Find what apps appeared in this segment to disrupt it
            non_dominant = [
                (app, count)
                for app, count in seg.app_counts.most_common()
                if app != seg.dominant_app
            ]
            blockers.append(
                {
                    "dominant_app": seg.dominant_app,
                    "duration_min": seg.duration_min,
                    "switch_count": seg.switch_count,
                    "reason": seg.break_reason,
                    "other_apps": non_dominant[:5],
                    "time_range": (
                        seg.start_utc.astimezone().strftime("%H:%M")
                        + " – "
                        + seg.end_utc.astimezone().strftime("%H:%M")
                    ),
                }
            )
    return blockers


# ── Hourly analysis ──────────────────────────────────────────────────────


def hourly_switch_distribution(
    switches: list[SwitchEvent],
) -> dict[int, dict]:
    """Compute switch counts per hour of the day (local time)."""
    hours: dict[int, dict] = {}
    for h in range(24):
        hours[h] = {"total": 0, "meaningful": 0}
    for s in switches:
        local_hour = s.time_utc.astimezone().hour
        hours[local_hour]["total"] += 1
        if s.is_meaningful:
            hours[local_hour]["meaningful"] += 1
    return hours


# ── Report formatting ─────────────────────────────────────────────────────


def format_duration(seconds: float) -> str:
    m = int(seconds // 60)
    s = int(seconds % 60)
    if m >= 60:
        h = m // 60
        m = m % 60
        return f"{h}h {m}m"
    return f"{m}m {s}s"


def print_header(text: str) -> None:
    w = 72
    print(f"\n{'=' * w}")
    print(f"  {text}")
    print(f"{'=' * w}")


def print_subheader(text: str) -> None:
    print(f"\n── {text} ──")


def print_report(
    samples: list[Sample],
    active_samples: list[Sample],
    switches: list[SwitchEvent],
    segments: list[FocusSegment],
    args: argparse.Namespace,
) -> None:
    total_switches = len(switches)
    meaningful_switches = sum(1 for s in switches if s.is_meaningful)
    raw_switches = total_switches  # all switches are raw
    valid_focus = [s for s in segments if not s.break_reason]

    # ── 1. Executive Summary ──────────────────────────────────────────
    print_header("📊 Context Switch & Focus Analysis")

    active_min = sum(
        (s.end_utc - s.start_utc).total_seconds() / 60 for s in segments
    )
    print(f"\n  Active samples today:  {len(active_samples):,}")
    print(f"  Total samples today:   {len(samples):,}")
    print(f"  Est. active duration:  {format_duration(active_min * 60)}")
    print(
        f"  Raw tool switches:     {raw_switches:,}  "
        f"(every app or window title change between active samples)"
    )
    print(
        f"  Meaningful switches:   {meaningful_switches:,}  "
        f"(context-level: Dev→Comm, etc.)"
    )
    print(
        f"  Focus threshold:       ≥{args.min_focus_min}min continuous, "
        f"≤{args.max_switches} context switches, "
        f"gaps ≤{args.max_gap_min}min"
    )
    print(f"  Valid focus sessions:  {len(valid_focus)}")
    print(f"  Fragmented segments:   {sum(1 for s in segments if s.is_fragmented)}")

    # ── 2. Why So Many Switches ───────────────────────────────────────
    print_header("🔀 Why Context Switching Is High")

    # 2a. Context-level switch breakdown
    context_pairs = build_switch_pairs(switches)
    meaningful_pairs = {
        (f, t): c for (f, t), c in context_pairs.items() if f != t
    }
    if meaningful_pairs:
        print_subheader("Context-level switch directions (ranked)")
        for (f, t), count in sorted(
            meaningful_pairs.items(), key=lambda x: -x[1]
        ):
            pct = count / meaningful_switches * 100 if meaningful_switches else 0
            bar = "█" * max(1, int(pct / 2))
            print(f"  {f:>16} → {t:<16}  {count:>4}  ({pct:5.1f}%)  {bar}")

    # 2b. Top app-switch pairs
    print_subheader("Top app → app switch pairs (all switches)")
    app_pairs = build_app_switch_matrix(switches)
    for (a, b), count in app_pairs.most_common(args.top):
        pct = count / total_switches * 100 if total_switches else 0
        print(f"  {a:>20} → {b:<20}  {count:>4}  ({pct:5.1f}%)")

    # 2c. Interrupters
    work_contexts = {"Development", "Productivity", "Research"}
    interrupters = find_interrupters(switches, work_contexts)
    if interrupters:
        print_subheader("Top interrupter apps (pulling away from work)")
        for item in interrupters[: args.top]:
            pct = (
                item["total_pulls"] / meaningful_switches * 100
                if meaningful_switches
                else 0
            )
            dests = ", ".join(
                f"{ctx}({n})" for ctx, n in item["destinations"][:3]
            )
            print(
                f"  {item['app']:>20}  →  {item['total_pulls']:>4} pulls  "
                f"({pct:5.1f}%)  [{dests}]"
            )

    # 2d. Browser vs desktop breakdown
    browser_switches = 0
    desktop_switches = 0
    for s in switches:
        from_browser = contains_any(normalize(s.from_app), BROWSER_TOKENS)
        to_browser = contains_any(normalize(s.to_app), BROWSER_TOKENS)
        if from_browser or to_browser:
            browser_switches += 1
        else:
            desktop_switches += 1

    print_subheader("Switch type breakdown")
    print(
        f"  Browser-involved switches:  {browser_switches:>4}  "
        f"({browser_switches / total_switches * 100:5.1f}%)"
        if total_switches
        else ""
    )
    print(
        f"  Desktop-only switches:      {desktop_switches:>4}  "
        f"({desktop_switches / total_switches * 100:5.1f}%)"
        if total_switches
        else ""
    )

    # ── 3. Time Distribution ──────────────────────────────────────────
    print_header("⏰ Switch Distribution by Hour")

    hourly = hourly_switch_distribution(switches)
    max_h = max((h["total"] for h in hourly.values()), default=1)
    for h in range(24):
        d = hourly[h]
        if d["total"] == 0:
            continue
        bar_w = max(1, int(d["total"] / max(max_h, 1) * 30))
        bar = "█" * bar_w
        print(
            f"  {h:02d}:00  total:{d['total']:>4}  "
            f"meaningful:{d['meaningful']:>4}  {bar}"
        )

    # Peak switch hour
    peak_hour = max(hourly.items(), key=lambda x: x[1]["total"])
    print(
        f"\n  Peak switch hour: {peak_hour[0]:02d}:00 "
        f"({peak_hour[1]['total']} switches)"
    )

    # ── 4. Why No Continuous Focus ────────────────────────────────────
    print_header("🎯 Why Continuous Focus Sessions Are Missing")

    long_segments = [s for s in segments if s.duration_min >= 3]
    focus_candidates = [
        s
        for s in long_segments
        if s.dominant_app in work_contexts
        or contains_any(normalize(s.dominant_app), DEV_TOKENS)
    ]

    if valid_focus:
        print_subheader("Valid focus sessions today")
        for fs in valid_focus:
            print(
                f"  {fs.dominant_app:<20}  "
                f"{fs.duration_min:>6.1f}m  "
                f"{fs.switch_count} switches  "
                f"{fs.start_utc.astimezone().strftime('%H:%M')} – "
                f"{fs.end_utc.astimezone().strftime('%H:%M')}"
            )

    blockers = find_potential_focus_blockers(segments, args.min_focus_min)
    if blockers:
        print_subheader("Segments that FAILED to become focus sessions")
        for b in blockers[:12]:
            other_info = ", ".join(f"{a}({n})" for a, n in b["other_apps"][:3])
            print(f"  ⚠ {b['time_range']}  {b['dominant_app']:<16}  "
                  f"{b['duration_min']:>5.1f}m  "
                  f"switches:{b['switch_count']}")
            print(f"     Reason: {b['reason']}")
            if other_info:
                print(f"     Also present: {other_info}")
    else:
        print_subheader("Analysis")
        if len(segments) <= 1:
            print(
                "  The entire active period is one continuous segment — "
                "switches may be distributed across the whole day."
            )
        if active_min < args.min_focus_min:
            print(
                f"  Total active time ({active_min:.0f}m) is less than "
                f"the minimum focus threshold ({args.min_focus_min}m)."
            )

    # ── 5. Key Apps Summary ───────────────────────────────────────────
    print_header("📱 App Activity Summary")

    app_counter = Counter()
    for s in active_samples:
        app_counter[short_name(s.process_name)] += 1

    print_subheader("Top apps by sample count")
    for app, count in app_counter.most_common(args.top):
        bar_w = max(1, int(count / max(app_counter.values()) * 30))
        bar = "█" * bar_w
        print(f"  {app:>24}  {count:>5}  {bar}")

    # ── 6. Recommendations ────────────────────────────────────────────
    print_header("💡 Recommendations")

    if meaningful_switches >= 20:
        print("  • Context switching is high — consider these specific actions:")
        if interrupters:
            top_interrupter = interrupters[0]
            print(
                f"    - Biggest interrupter: '{top_interrupter['app']}' "
                f"({top_interrupter['total_pulls']} times). "
                f"Try closing or muting notifications from it during work blocks."
            )
        peak_h = peak_hour[0]
        print(
            f"    - Worst hour for switching: {peak_h:02d}:00. "
            f"Schedule a 25-minute focus block before or after this period."
        )
        if browser_switches > desktop_switches:
            print(
                "    - Browser tab switching dominates. "
                "Consider grouping research tabs and batch-checking them."
            )

    if not valid_focus:
        print("  • No valid focus sessions detected today:")
        if blockers:
            worst = blockers[0]
            print(
                f"    - Closest candidate: '{worst['dominant_app']}' "
                f"for {worst['duration_min']:.0f}m but {worst['reason']}."
            )
        print(
            f"    - Try: pick ONE app (IDE, doc editor), close everything else, "
            f"set a timer for {args.min_focus_min}min."
        )
    else:
        print(
            f"  ✅ {len(valid_focus)} focus sessions detected — "
            f"keep building on this pattern."
        )

    print()


# ── Main ──────────────────────────────────────────────────────────────────


def main() -> None:
    parser = argparse.ArgumentParser(
        description="Analyze context switches and focus gaps from QuantifiedSelf data.",
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    parser.add_argument(
        "--db",
        default=None,
        help="Path to quantified_self_windows.db (auto-detected if omitted).",
    )
    parser.add_argument(
        "--date",
        default=None,
        help="Date to analyze (YYYY-MM-DD). Default: today.",
    )
    parser.add_argument(
        "--top",
        type=int,
        default=10,
        help="Number of top items to show in each section (default: 10).",
    )
    parser.add_argument(
        "--min-focus-min",
        type=int,
        default=DEFAULT_MIN_FOCUS_MINUTES,
        help=f"Minimum focus session duration in minutes (default: {DEFAULT_MIN_FOCUS_MINUTES}).",
    )
    parser.add_argument(
        "--max-gap-min",
        type=int,
        default=DEFAULT_MAX_GAP_MINUTES,
        help=f"Maximum gap (minutes) that doesn't break a focus segment (default: {DEFAULT_MAX_GAP_MINUTES}).",
    )
    parser.add_argument(
        "--max-switches",
        type=int,
        default=DEFAULT_MAX_SWITCHES_PER_FOCUS,
        help=f"Maximum context switches within a focus block (default: {DEFAULT_MAX_SWITCHES_PER_FOCUS}).",
    )
    args = parser.parse_args()

    # Resolve database path
    db_path = args.db or find_db_path()
    if not os.path.exists(db_path):
        print(f"ERROR: Database not found at {db_path}", file=sys.stderr)
        sys.exit(1)
    print(f"📁 Database: {db_path}")

    # Resolve date
    target_date = (
        date.fromisoformat(args.date) if args.date else date.today()
    )
    print(f"📅 Date:     {target_date.isoformat()}")

    # Read samples
    samples = read_samples(db_path, target_date)
    if not samples:
        print("\n⚠ No foreground samples found for this date.")
        print(
            "  Make sure the QuantifiedSelf agent has been running and collecting data."
        )
        sys.exit(0)

    # Classify & filter
    for s in samples:
        s.context = classify_context(s.process_name, s.window_title)

    active_samples = [
        s
        for s in samples
        if s.activity_state.strip().lower() == "active"
    ]
    active_samples.sort(key=lambda s: s.sample_time_utc)

    if len(active_samples) < 2:
        print("\n⚠ Too few active samples to analyze switches.")
        sys.exit(0)

    # Detect switches
    switches = detect_switches(active_samples)

    # Detect focus segments
    segments = detect_focus_segments(
        active_samples,
        args.min_focus_min,
        args.max_gap_min,
        args.max_switches,
    )

    # Print report
    print_report(samples, active_samples, switches, segments, args)


if __name__ == "__main__":
    main()
