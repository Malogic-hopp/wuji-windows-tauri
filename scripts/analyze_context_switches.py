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
        return "开发"
    if contains_any(p, COMM_TOKENS):
        return "沟通"
    if contains_any(p, ENTERTAINMENT_TOKENS):
        return "娱乐"
    if contains_any(p, SYSTEM_TOKENS):
        return "系统"
    if contains_any(p, PRODUCTIVITY_TOKENS):
        return "效率"
    return "其他"


def classify_browser_title(title: str) -> str:
    if contains_any(title, BROWSER_ENTERTAINMENT_TITLES):
        return "娱乐"
    if contains_any(title, BROWSER_COMM_TITLES):
        return "沟通"
    if contains_any(title, BROWSER_DEV_TITLES):
        return "开发"
    return "研究"


def short_name(process: str) -> str:
    """Human-readable short name for a process with friendly display overrides."""
    n = normalize(process)
    # Remove common suffixes
    for suf in [".exe", "-win64", "-x64", "-x86"]:
        if n.endswith(suf):
            n = n[: -len(suf)]
    overrides = {
        "quantifiedself.windows.app": "WUJI",
        "quantifiedself.windows.agent": "WUJI Agent",
        "applicationframehost": "AppFrameHost",
        "shellexperiencehost": "ShellExperience",
        "searchhost": "SearchHost",
    }
    return overrides.get(n.lower(), n)


# ── Switch analysis ────────────────────────────────────────────────────────


def is_dev_tool_app(process_name: str) -> bool:
    """Apps that are part of the development workflow.
    Switching to these from 开发 should not count as a context change."""
    n = normalize(process_name)
    return n in (
        "explorer", "windowsterminal", "terminal", "powershell", "pwsh", "cmd"
    )


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

        # 开发 ↔ dev_tool（Explorer/Terminal 等）双向豁免，同一工作流
        context_changed = prev.context != cur.context
        if context_changed and (
            (prev.context == "开发" and is_dev_tool_app(cur.process_name))
            or (cur.context == "开发" and is_dev_tool_app(prev.process_name))
        ):
            context_changed = False

        switches.append(
            SwitchEvent(
                from_app=prev.process_name,
                to_app=cur.process_name,
                from_title=prev.window_title,
                to_title=cur.window_title,
                from_context=prev.context,
                to_context=cur.context,
                time_utc=cur.sample_time_utc,
                is_meaningful=context_changed,
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


def build_app_switch_matrix(switches: list[SwitchEvent]) -> tuple[Counter, Counter]:
    """
    Count (from_app → to_app) pairs.
    Returns:
      cross_app:  different-app switches (App 间切换)
      same_app:   same-app window/title changes (App 内窗口切换)
    """
    cross_app = Counter()
    same_app = Counter()
    for s in switches:
        a = short_name(s.from_app)
        b = short_name(s.to_app)
        if a == b:
            same_app[(a, b)] += 1
        else:
            cross_app[(a, b)] += 1
    return cross_app, same_app


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
                seg.break_reason = f"时长不足（{seg.duration_min:.0f}m < {min_focus_min}m）"
            elif seg.is_fragmented:
                seg.break_reason = f"切换过多（{seg.switch_count} > {max_switches}）"
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
            # 开发 ↔ dev_tool（Explorer/Terminal 等）双向豁免
            if (last_context == "开发" and is_dev_tool_app(s.process_name)) \
               or (s.context == "开发" and is_dev_tool_app(last_app)):
                pass  # same workflow, keep last_context unchanged
            else:
                seg.switch_count += 1
                last_context = s.context
        else:
            last_context = s.context
        last_app = s.process_name
        last_title = s.window_title

    # Finalize last segment
    seg.dominant_app = seg.app_counts.most_common(1)[0][0] if seg.app_counts else ""
    dur = (seg.end_utc - seg.start_utc).total_seconds() / 60
    seg.duration_min = round(dur, 1)
    seg.is_fragmented = seg.switch_count > max_switches
    if dur < min_focus_min:
        seg.break_reason = f"时长不足（{seg.duration_min:.0f}m < {min_focus_min}m）"
    elif seg.is_fragmented:
        seg.break_reason = f"切换过多（{seg.switch_count} > {max_switches}）"
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


# ── Browser title analysis ─────────────────────────────────────────────────


def build_browser_title_details(
    switches: list[SwitchEvent], top_n: int = 15
) -> list[dict]:
    """
    Extract browser window titles from switches, grouped by title + context.
    Shows which specific pages the user was looking at in the browser.
    """
    title_counts: dict[tuple[str, str], int] = defaultdict(int)

    for s in switches:
        # When switching TO a browser, record the destination title
        if contains_any(normalize(s.to_app), BROWSER_TOKENS):
            key = (s.to_title[:120], s.to_context)
            title_counts[key] += 1
        # Also record when switching FROM a browser (browser tab change)
        if contains_any(normalize(s.from_app), BROWSER_TOKENS):
            key = (s.from_title[:120], s.from_context)
            title_counts[key] += 1

    # Remove empty-title entries and deduplicate
    result = []
    seen_titles = set()
    for (title, ctx), count in sorted(
        title_counts.items(), key=lambda x: -x[1]
    ):
        t = title.strip()
        if not t or t in seen_titles:
            continue
        seen_titles.add(t)
        result.append({"title": t, "context": ctx, "count": count})

    return result[:top_n]


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
    print_header("📊 任务切换与专注分析")

    active_min = sum(
        (s.end_utc - s.start_utc).total_seconds() / 60 for s in segments
    )
    print(f"\n  今日活跃样本数：  {len(active_samples):,}")
    print(f"  今日总样本数：    {len(samples):,}")
    print(f"  预估活跃时长：    {format_duration(active_min * 60)}")
    print(
        f"  工具跳转（原始）：{raw_switches:,}  "
        f"（活跃样本间每次 App 或窗口标题变更）"
    )
    print(
        f"  任务切换：{meaningful_switches:,}  "
        f"（跨语境级别切换，如开发→沟通）"
    )
    print(
        f"  专注阈值：        连续 ≥{args.min_focus_min} 分钟，"
        f"任务切换 ≤{args.max_switches} 次，"
        f"采样间隔 ≤{args.max_gap_min} 分钟"
    )
    print(f"  有效专注段：      {len(valid_focus)}")
    print(f"  碎片化段：        {sum(1 for s in segments if s.is_fragmented)}")

    # ── 2. Why So Many Switches ───────────────────────────────────────
    print_header("🔀 为什么任务切换频繁")

    # 2a. Context-level switch breakdown
    context_pairs = build_switch_pairs(switches)
    meaningful_pairs = {
        (f, t): c for (f, t), c in context_pairs.items() if f != t
    }

    # Build example map: (from_ctx, to_ctx) → list of concrete switch descriptions
    def _make_example(sw: SwitchEvent) -> str:
        """Build a concrete description of a switch, using window titles."""
        from_label = short_name(sw.from_app)
        to_label = short_name(sw.to_app)
        from_title = sw.from_title.strip()[:50] if sw.from_title.strip() else ""
        to_title = sw.to_title.strip()[:50] if sw.to_title.strip() else ""

        # For same-app switches, the title change IS the story
        if sw.from_app == sw.to_app:
            return f"{from_label}: 「{from_title}」→「{to_title}」"

        # Cross-app: show the destination title if it has substance
        if to_title and to_title.lower() != to_label.lower():
            return f"{from_label} → {to_label}「{to_title}」"
        return f"{from_label} → {to_label}"

    ctx_examples: dict[tuple[str, str], Counter] = defaultdict(Counter)
    for s in switches:
        if s.is_meaningful:
            key = (s.from_context, s.to_context)
            example = _make_example(s)
            ctx_examples[key][example] += 1

    if meaningful_pairs:
        print_subheader("任务切换方向（按次数排序）")
        for (f, t), count in sorted(
            meaningful_pairs.items(), key=lambda x: -x[1]
        ):
            pct = count / meaningful_switches * 100 if meaningful_switches else 0
            bar = "█" * max(1, int(pct / 2))
            print(f"  {f:>6} → {t:<6}  {count:>4}  ({pct:5.1f}%)  {bar}")
            # Show top 3 by frequency for this direction
            top_examples = ctx_examples.get((f, t), Counter()).most_common(3)
            if top_examples:
                items = [f"{ex}（{n}次）" for ex, n in top_examples]
                print(f"         次数前三：{'、'.join(items)}")

    # 2b. Top app-switch pairs
    cross_app, same_app = build_app_switch_matrix(switches)
    same_total = sum(same_app.values())

    print_subheader("App 间切换 Top 对（不同 App 之间）")
    for (a, b), count in cross_app.most_common(args.top):
        pct = count / total_switches * 100 if total_switches else 0
        print(f"  {a:>20} → {b:<20}  {count:>4}  ({pct:5.1f}%)")

    if same_total > 0:
        print(
            f"\n  （另有 {same_total} 次为 App 内窗口/标签切换，"
            f"如 Terminal 切目录、Code 切文件、Edge 切标签页）"
        )

    # 2c. Interrupters
    work_contexts = {"开发", "效率", "研究"}
    interrupters = find_interrupters(switches, work_contexts)
    if interrupters:
        print_subheader("主要中断来源（将你从工作语境拉走的 App）")
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
                f"  {item['app']:>20}  →  {item['total_pulls']:>4} 次  "
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

    print_subheader("切换类型分布")
    print(
        f"  浏览器相关切换：  {browser_switches:>4}  "
        f"({browser_switches / total_switches * 100:5.1f}%)"
        if total_switches
        else ""
    )
    print(
        f"  纯桌面应用切换：  {desktop_switches:>4}  "
        f"({desktop_switches / total_switches * 100:5.1f}%)"
        if total_switches
        else ""
    )

    # 2e. Browser title details
    browser_titles = build_browser_title_details(switches)
    if browser_titles:
        print_subheader("浏览器标签页标题详情（Top 15）")
        # Group by context
        by_ctx: dict[str, list[dict]] = defaultdict(list)
        for item in browser_titles:
            by_ctx[item["context"]].append(item)
        for ctx in ["娱乐", "沟通", "开发", "研究", "效率", "系统", "其他"]:
            items = by_ctx.get(ctx, [])
            if not items:
                continue
            print(f"\n  ▸ {ctx}")
            for item in items[:6]:
                title_preview = item["title"][:96]
                print(f"    [{item['count']:>3}×] {title_preview}")

    # ── 3. Time Distribution ──────────────────────────────────────────
    print_header("⏰ 各时段切换分布")

    hourly = hourly_switch_distribution(switches)
    max_h = max((h["total"] for h in hourly.values()), default=1)
    for h in range(24):
        d = hourly[h]
        if d["total"] == 0:
            continue
        bar_w = max(1, int(d["total"] / max(max_h, 1) * 30))
        bar = "█" * bar_w
        print(
            f"  {h:02d}:00  总计:{d['total']:>4}  "
            f"有意义:{d['meaningful']:>4}  {bar}"
        )

    # Peak switch hour
    peak_hour = max(hourly.items(), key=lambda x: x[1]["total"])
    print(
        f"\n  切换高峰时段：{peak_hour[0]:02d}:00 "
        f"（{peak_hour[1]['total']} 次切换）"
    )

    # ── 4. Why No Continuous Focus ────────────────────────────────────
    print_header("🎯 为什么缺少连续专注")

    long_segments = [s for s in segments if s.duration_min >= 3]
    focus_candidates = [
        s
        for s in long_segments
        if s.dominant_app in work_contexts
        or contains_any(normalize(s.dominant_app), DEV_TOKENS)
    ]

    if valid_focus:
        print_subheader("今日有效专注段")
        for fs in valid_focus:
            print(
                f"  {fs.dominant_app:<20}  "
                f"{fs.duration_min:>6.1f}m  "
                f"{fs.switch_count} 次切换  "
                f"{fs.start_utc.astimezone().strftime('%H:%M')} – "
                f"{fs.end_utc.astimezone().strftime('%H:%M')}"
            )

    blockers = find_potential_focus_blockers(segments, args.min_focus_min)
    if blockers:
        print_subheader("未能形成专注的段")
        for b in blockers[:12]:
            other_info = ", ".join(f"{a}({n})" for a, n in b["other_apps"][:3])
            print(f"  ⚠ {b['time_range']}  {b['dominant_app']:<16}  "
                  f"{b['duration_min']:>5.1f}m  "
                  f"切换:{b['switch_count']}")
            print(f"     原因: {b['reason']}")
            if other_info:
                print(f"     同时出现: {other_info}")
    else:
        print_subheader("分析")
        if len(segments) <= 1:
            print(
                "  整个活跃时段是一个连续段——"
                "切换可能均匀分布在全天。"
            )
        if active_min < args.min_focus_min:
            print(
                f"  总活跃时间（{active_min:.0f}m）低于"
                f"最小专注阈值（{args.min_focus_min}m）。"
            )

    # ── 5. Key Apps Summary ───────────────────────────────────────────
    print_header("📱 App 活跃度概览")

    app_counter = Counter()
    for s in active_samples:
        app_counter[short_name(s.process_name)] += 1

    print_subheader("按样本数排序的 Top App")
    for app, count in app_counter.most_common(args.top):
        bar_w = max(1, int(count / max(app_counter.values()) * 30))
        bar = "█" * bar_w
        print(f"  {app:>24}  {count:>5}  {bar}")

    # ── 6. Recommendations ────────────────────────────────────────────
    print_header("💡 建议")

    if meaningful_switches >= 20:
        print("  • 任务切换频繁 — 可以考虑以下具体行动：")
        if interrupters:
            top_interrupter = interrupters[0]
            print(
                f"    - 最大中断源：'{top_interrupter['app']}' "
                f"（{top_interrupter['total_pulls']} 次）。"
                f"试试在工作块期间关闭或静音它的通知。"
            )
        peak_h = peak_hour[0]
        print(
            f"    - 切换最严重时段：{peak_h:02d}:00。"
            f"在这个时段前后安排一个 25 分钟的专注块。"
        )
        if browser_switches > desktop_switches:
            print(
                "    - 浏览器标签切换占主导。"
                "考虑分组整理研究类标签，批量查阅。"
            )

    if not valid_focus:
        print("  • 今日未检测到有效专注段：")
        if blockers:
            worst = blockers[0]
            print(
                f"    - 最近候选：'{worst['dominant_app']}' "
                f"持续 {worst['duration_min']:.0f}m，但 {worst['reason']}。"
            )
        print(
            f"    - 试试：选择一个 App（IDE 或文档编辑器），关掉其他一切，"
            f"设定 {args.min_focus_min} 分钟计时器。"
        )
    else:
        print(
            f"  ✅ 检测到 {len(valid_focus)} 段有效专注 — "
            f"继续保持这个节奏。"
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
        print(f"错误：找不到数据库文件 {db_path}", file=sys.stderr)
        sys.exit(1)
    print(f"📁 数据库：{db_path}")

    # Resolve date
    target_date = (
        date.fromisoformat(args.date) if args.date else date.today()
    )
    print(f"📅 日期：   {target_date.isoformat()}")

    # Read samples
    samples = read_samples(db_path, target_date)
    if not samples:
        print("\n⚠ 该日期没有找到任何前台样本数据。")
        print(
            "  请确保 QuantifiedSelf Agent 已运行并在采集数据。"
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
        print("\n⚠ 活跃样本太少，无法分析切换。")
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
