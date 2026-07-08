#!/usr/bin/env python3
"""
手动维护工具：按进程名或窗口标题关键词删除 QuantifiedSelf 历史数据。
操作直接 DELETE FROM + VACUUM，不可恢复。使用前建议先 --dry-run 预览。
建议在运行前备份数据库文件。

Usage:
  python scripts/clean_history.py --process explorer
  python scripts/clean_history.py --process weixin --process msedge
  python scripts/clean_history.py --title "世界杯" --title "bilibili"
  python scripts/clean_history.py --process msedge --title "bilibili"
  python scripts/clean_history.py --dry-run
  python scripts/clean_history.py
      (interactive: enter keywords one at a time, blank line to finish)
"""

import argparse
import os
import sqlite3
import sys

try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass


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
    raise FileNotFoundError("找不到 quantified_self_windows.db，请用 --db 指定路径。")


def query_count(conn, table: str, process_names: list[str], title_words: list[str]) -> int:
    """Count matching rows."""
    conditions = []
    params = []

    if process_names:
        placeholders = " OR ".join(["process_name LIKE ?"] * len(process_names))
        conditions.append(f"({placeholders})")
        params.extend(f"%{n}%" for n in process_names)

    if title_words and table == "foreground_samples":
        placeholders = " OR ".join(["window_title LIKE ?"] * len(title_words))
        conditions.append(f"({placeholders})")
        params.extend(f"%{w}%" for w in title_words)

    if not conditions:
        return 0

    sql = f"SELECT COUNT(*) FROM {table} WHERE {' AND '.join(conditions)}"
    return conn.execute(sql, params).fetchone()[0]


def preview_rows(conn, table: str, process_names: list[str], title_words: list[str], limit: int = 5):
    """Return the first N matching rows for preview."""
    conditions = []
    params = []

    if process_names:
        placeholders = " OR ".join(["process_name LIKE ?"] * len(process_names))
        conditions.append(f"({placeholders})")
        params.extend(f"%{n}%" for n in process_names)

    if title_words and table == "foreground_samples":
        placeholders = " OR ".join(["window_title LIKE ?"] * len(title_words))
        conditions.append(f"({placeholders})")
        params.extend(f"%{w}%" for w in title_words)

    if not conditions:
        return []

    columns = {
        "foreground_samples": "id, sample_time_utc, process_name, window_title, activity_state",
        "app_sessions": "id, started_at_utc, ended_at_utc, process_name, window_title, total_duration_seconds",
    }
    cols = columns.get(table, "*")
    sql = f"SELECT {cols} FROM {table} WHERE {' AND '.join(conditions)} ORDER BY id DESC LIMIT {limit}"
    return conn.execute(sql, params).fetchall()


def delete_from(conn, table: str, process_names: list[str], title_words: list[str]) -> int:
    """Delete matching rows, return count."""
    conditions = []
    params = []

    if process_names:
        placeholders = " OR ".join(["process_name LIKE ?"] * len(process_names))
        conditions.append(f"({placeholders})")
        params.extend(f"%{n}%" for n in process_names)

    if title_words and table == "foreground_samples":
        placeholders = " OR ".join(["window_title LIKE ?"] * len(title_words))
        conditions.append(f"({placeholders})")
        params.extend(f"%{w}%" for w in title_words)

    if not conditions:
        return 0

    sql = f"DELETE FROM {table} WHERE {' AND '.join(conditions)}"
    return conn.execute(sql, params).rowcount


def main():
    parser = argparse.ArgumentParser(
        description="按进程名或窗口标题关键词删除 QuantifiedSelf 历史数据。"
    )
    parser.add_argument("--db", help="数据库路径（自动检测）")
    parser.add_argument("--preview", type=int, default=0, metavar="N",
                        help="打印匹配的前 N 条记录详情（配合 --dry-run 使用）")
    parser.add_argument("--process", "-p", action="append",
                        help="进程名关键词（可多次指定），如 explorer、weixin")
    parser.add_argument("--title", "-t", action="append",
                        help="窗口标题关键词（可多次指定），如 世界杯、bilibili")
    parser.add_argument("--dry-run", action="store_true",
                        help="仅预览匹配到的记录数，不删除")
    args = parser.parse_args()

    db_path = args.db or find_db_path()
    if not os.path.exists(db_path):
        print(f"错误：找不到数据库 {db_path}", file=sys.stderr)
        sys.exit(1)

    process_names = args.process or []
    title_words = args.title or []

    # If no args provided, go interactive
    if not process_names and not title_words:
        print("输入要匹配的关键词（每行一个，空行结束）：")
        print("  进程名关键词（从前台样本 process_name 匹配）：")
        while True:
            kw = input("  进程 > ").strip()
            if not kw:
                break
            process_names.append(kw)

        print("  标题关键词（从窗口标题匹配，仅 foreground_samples）：")
        while True:
            kw = input("  标题 > ").strip()
            if not kw:
                break
            title_words.append(kw)

    if not process_names and not title_words:
        print("未指定任何关键词，退出。")
        sys.exit(0)

    print(f"\n匹配规则：")
    if process_names:
        print(f"  进程包含：{', '.join(process_names)}")
    if title_words:
        print(f"  标题包含：{', '.join(title_words)}")

    conn = sqlite3.connect(f"file:{db_path}", uri=True)
    conn.execute("PRAGMA journal_mode=WAL;")

    samples_count = query_count(conn, "foreground_samples", process_names, title_words)
    sessions_count = query_count(conn, "app_sessions", process_names, [])

    print(f"\n  匹配的 foreground_samples：{samples_count:,} 条")
    print(f"  匹配的 app_sessions：      {sessions_count:,} 条")

    if samples_count == 0 and sessions_count == 0:
        print("  没有匹配的记录。")
        conn.close()
        return

    if args.dry_run:
        n = args.preview or 5
        print(f"\n--- 前 {n} 条匹配的 foreground_samples ---")
        for row in preview_rows(conn, "foreground_samples", process_names, title_words, n):
            print(f"  [{row[0]}] {row[1][:19]} | {row[2]:<20} | {(row[3] or '')[:80]}")
        print(f"\n--- 前 {n} 条匹配的 app_sessions ---")
        for row in preview_rows(conn, "app_sessions", process_names, [], n):
            print(f"  [{row[0]}] {row[1][:19]} | {row[3]:<20} | {(row[4] or '')[:80]}")
        print(f"\n[预览模式] 未实际删除。去掉 --dry-run 执行真正的删除。")
        conn.close()
        return

    print(f"\n即将删除以上 {samples_count + sessions_count:,} 条记录。")
    confirm = input("确认删除？(y/N): ").strip().lower()
    if confirm != "y":
        print("已取消。")
        conn.close()
        return

    conn.execute("BEGIN;")
    deleted_samples = delete_from(conn, "foreground_samples", process_names, title_words)
    deleted_sessions = delete_from(conn, "app_sessions", process_names, [])
    conn.execute("COMMIT;")

    print(f"已删除 {deleted_samples + deleted_sessions:,} 条（samples: {deleted_samples:,}, sessions: {deleted_sessions:,}）")

    print("正在回收磁盘空间（VACUUM）...")
    conn.execute("VACUUM;")
    conn.close()

    print("清理完毕。重启 App 刷新即可。")


if __name__ == "__main__":
    main()
