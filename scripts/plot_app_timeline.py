#!/usr/bin/env python3
"""
App 活跃时间线图 —— 横轴是一天的时间，纵轴是 Top N App，
每个 App 一行，活跃时段用色块标出，颜色按语境分类。

依赖：matplotlib
  pip install matplotlib -i https://pypi.tuna.tsinghua.edu.cn/simple

Usage:
  python scripts/plot_app_timeline.py
  python scripts/plot_app_timeline.py --date 2026-07-06 --top 10
  python scripts/plot_app_timeline.py --date 2026-07-08 --top 8 --output timeline.png
"""

import argparse
import os
import sqlite3
import sys
from collections import Counter, defaultdict
from datetime import date, datetime, timedelta, time, timezone

try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass

# ── Context classification (same as analyze_context_switches.py) ──────────

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
BROWSER_ENTERTAINMENT = [
    "youtube", "bilibili", "哔哩哔哩", "xiaohongshu", "小红书", "migu",
    "咪咕", "weibo", "微博", "zhiboba", "直播吧", "netflix", "twitch",
    "douyin", "抖音", "视频", "直播", "游戏",
]
BROWSER_COMM = [
    "gmail", "outlook", "mail", "teams", "slack", "discord", "wechat", "微信", "飞书",
]
BROWSER_DEV = [
    "github", "gitlab", "stack overflow", "stackoverflow", "microsoft learn",
    "docs", "documentation", "api", "nuget", "npm", "localhost",
    "openai", "codex", "developer", "devdocs", "copilot",
]

CONTEXT_COLORS = {
    "开发": "#0F766E",
    "研究": "#3B82F6",
    "沟通": "#A855F7",
    "娱乐": "#EF4444",
    "系统": "#94A3B8",
    "效率": "#F59E0B",
    "其他": "#64748B",
}


def contains_any(text: str, tokens: list[str]) -> bool:
    t = text.lower()
    return any(tok in t for tok in tokens)


def normalize(name: str) -> str:
    n = name.strip().lower()
    return n[:-4] if n.endswith(".exe") else n


def short_name(process: str) -> str:
    """Short app name with friendly display overrides."""
    n = normalize(process)
    for suf in ["-win64", "-x64", "-x86", ".exe"]:
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


def classify_browser_title(title: str) -> str:
    if contains_any(title, BROWSER_ENTERTAINMENT):
        return "娱乐"
    if contains_any(title, BROWSER_COMM):
        return "沟通"
    if contains_any(title, BROWSER_DEV):
        return "开发"
    return "研究"


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


# ── Database ───────────────────────────────────────────────────────────────

def find_db_path() -> str:
    candidates = [
        r"D:\WUJI\WindowsAgent\data\quantified_self_windows.db",
        os.path.expandvars(
            r"%LOCALAPPDATA%\WUJI\WindowsAgent\data\quantified_self_windows.db"
        ),
    ]
    for p in candidates:
        if os.path.exists(p):
            return p
    raise FileNotFoundError("找不到数据库，请用 --db 指定路径。")


def read_active_samples(db_path: str, target_date: date) -> list[dict]:
    """Read Active foreground_samples for the given date."""
    local_start = datetime.combine(target_date, time.min)
    local_end = datetime.combine(target_date, time.max)
    utc_start = local_start.astimezone(timezone.utc).isoformat()
    utc_end = local_end.astimezone(timezone.utc).isoformat()

    conn = sqlite3.connect(f"file:{db_path}?mode=ro", uri=True)
    rows = conn.execute(
        """
        SELECT sample_time_utc, process_name,
               COALESCE(window_title, '') AS window_title, activity_state
        FROM foreground_samples
        WHERE sample_time_utc >= ? AND sample_time_utc <= ?
          AND activity_state = 'Active'
        ORDER BY sample_time_utc ASC
        """,
        (utc_start, utc_end),
    ).fetchall()
    conn.close()

    samples = []
    for r in rows:
        t = datetime.fromisoformat(r[0])
        if t.tzinfo is None:
            t = t.replace(tzinfo=timezone.utc)
        samples.append({
            "time_utc": t,
            "process": r[1],
            "title": r[2],
            "short_name": short_name(r[1]),
        })
    return samples


# ── Build timeline segments ────────────────────────────────────────────────

def build_timeline(samples: list[dict], top_n: int = 10) -> tuple[list[str], dict[str, list[dict]]]:
    """
    For each of the top N apps, build a list of time segments
    where the app was continuously in the foreground.
    A segment ends when another app appears or gap > 5 min.
    """
    # Rank apps by total sample count
    app_counts = Counter(s["short_name"] for s in samples)
    top_apps = [app for app, _ in app_counts.most_common(top_n)]

    # Build continuous segments per app
    max_gap = timedelta(minutes=5)
    app_segments: dict[str, list[dict]] = defaultdict(list)

    # Group consecutive samples by app
    current_app = None
    seg_start = None
    seg_end = None
    seg_titles = []

    for s in samples:
        app = s["short_name"]
        if app not in top_apps:
            continue

        t = s["time_utc"]

        if app != current_app:
            # Close previous segment
            if current_app and seg_start:
                ctx = classify_context(current_app, " · ".join(seg_titles[:3]))
                app_segments[current_app].append({
                    "start": seg_start,
                    "end": seg_end,
                    "context": ctx,
                })
            current_app = app
            seg_start = t
            seg_end = t
            seg_titles = [s["title"]]
        else:
            gap = t - seg_end
            if gap > max_gap:
                # Gap too large, close and start new
                if seg_start:
                    ctx = classify_context(current_app, " · ".join(seg_titles[:3]))
                    app_segments[current_app].append({
                        "start": seg_start,
                        "end": seg_end,
                        "context": ctx,
                    })
                seg_start = t
                seg_titles = [s["title"]]
            seg_end = t
            seg_titles.append(s["title"])

    # Close last segment
    if current_app and seg_start:
        ctx = classify_context(current_app, " · ".join(seg_titles[:3]))
        app_segments[current_app].append({
            "start": seg_start,
            "end": seg_end,
            "context": ctx,
        })

    return top_apps, app_segments


# ── Plot ────────────────────────────────────────────────────────────────────

def plot_timeline(top_apps: list[str], app_segments: dict[str, list[dict]],
                  target_date: date, output_path: str | None = None):
    import matplotlib.pyplot as plt
    import matplotlib.patches as mpatches
    import matplotlib.dates as mdates

    # Support Chinese fonts
    plt.rcParams["font.sans-serif"] = ["Microsoft YaHei", "SimHei", "Noto Sans CJK SC", "DejaVu Sans"]
    plt.rcParams["axes.unicode_minus"] = False

    n = len(top_apps)
    fig, ax = plt.subplots(figsize=(16, max(6, n * 0.55)))

    # Use local time for the x-axis (naive datetimes for matplotlib)
    local_tz = datetime.now().astimezone().tzinfo
    day_start = datetime.combine(target_date, time.min)
    day_end = datetime.combine(target_date, time.max)

    ax.set_xlim(day_start, day_end)
    ax.set_ylim(-0.5, n - 0.5)
    ax.set_yticks(range(n))
    ax.set_yticklabels(top_apps, fontsize=11)
    ax.invert_yaxis()

    # Format x-axis as hours
    ax.xaxis.set_major_locator(mdates.HourLocator(interval=1))
    ax.xaxis.set_major_formatter(mdates.DateFormatter("%H:%M"))
    plt.setp(ax.get_xticklabels(), rotation=0, fontsize=9)
    ax.set_xlabel(f"时间 ({target_date.isoformat()})", fontsize=12)
    ax.grid(axis="x", alpha=0.3, linestyle="--")

    bar_height = 0.55

    for i, app in enumerate(top_apps):
        segments = app_segments.get(app, [])
        for seg in segments:
            # Convert UTC to local time
            start = seg["start"].astimezone(local_tz)
            end = seg["end"].astimezone(local_tz)
            # Strip tzinfo for matplotlib (it uses naive datetimes)
            start = start.replace(tzinfo=None)
            end = end.replace(tzinfo=None)
            if end <= start:
                continue
            ctx = seg["context"]
            color = CONTEXT_COLORS.get(ctx, "#64748B")
            rect = mpatches.Rectangle(
                (mdates.date2num(start), i - bar_height / 2),
                mdates.date2num(end) - mdates.date2num(start),
                bar_height,
                facecolor=color,
                edgecolor="none",
                alpha=0.85,
            )
            ax.add_patch(rect)

    # Legend
    legend_patches = [
        mpatches.Patch(color=c, label=ctx)
        for ctx, c in CONTEXT_COLORS.items()
    ]
    ax.legend(handles=legend_patches, loc="upper right", fontsize=9,
              ncol=7, framealpha=0.7)

    # Title
    total_active = sum(
        (seg["end"] - seg["start"]).total_seconds()
        for segs in app_segments.values()
        for seg in segs
    )
    h = int(total_active // 3600)
    m = int((total_active % 3600) // 60)
    ax.set_title(
        f"App 活跃时间线 — {target_date.isoformat()}（预估活跃 {h}h {m}m）",
        fontsize=14, fontweight="bold", pad=12,
    )

    plt.tight_layout()

    if output_path:
        plt.savefig(output_path, dpi=150, bbox_inches="tight")
        print(f"已保存至：{output_path}")
    else:
        plt.show()


# ── Main ────────────────────────────────────────────────────────────────────

def main():
    parser = argparse.ArgumentParser(description="绘制 App 活跃时间线图")
    parser.add_argument("--db", help="数据库路径（自动检测）")
    parser.add_argument("--date", default=None, help="日期 YYYY-MM-DD，默认今天")
    parser.add_argument("--top", type=int, default=10, help="展示 Top N App（默认 10）")
    parser.add_argument("--output", "-o", help="保存到文件，不指定则弹出窗口")
    args = parser.parse_args()

    db_path = args.db or find_db_path()
    target_date = date.fromisoformat(args.date) if args.date else date.today()

    print(f"读取 {target_date.isoformat()} 的数据...")
    samples = read_active_samples(db_path, target_date)
    if not samples:
        print("该日期没有活跃样本数据。")
        sys.exit(0)

    print(f"共 {len(samples)} 条活跃样本，构建时间线...")
    top_apps, app_segments = build_timeline(samples, args.top)

    print(f"Top {len(top_apps)} App：{', '.join(top_apps)}")
    print("绘图...")
    plot_timeline(top_apps, app_segments, target_date, args.output)


if __name__ == "__main__":
    main()
