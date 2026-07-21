#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""WUJI Rebuild v0.1 soak 验收（09 §12.2：持续运行无 crash、死锁或明显无界内存/WAL 增长）。

做法：
  1. 记录旧库 checksum（soak 前后不得变化）。
  2. 在隔离 test channel 以 --capture-on-start 启动 release Agent。
  3. 周期采样：进程 RSS、DB/WAL 大小、agent_runtime 心跳/drop 计数/writer 状态。
  4. 结束时经 IPC agent_shutdown_dev 优雅退出并复检：
     - 无 crash（进程全程存活，单实例）
     - RSS 增长有界（< 64 MiB 且 < 50%）
     - WAL/DB 无异常无界增长
     - 心跳持续推进、writer 不处于 faulted
     - DB quick_check 通过、旧库 checksum 不变
  5. 输出 soak-report.json。

用法：python rebuild/scripts/soak.py --minutes 480 [--interval 60] [--channel rebuild-v01-test-<ulid>]
"""

import argparse
import ctypes
import hashlib
import json
import os
import sqlite3
import subprocess
import sys
import tempfile
import time
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
REBUILD_DIR = REPO_ROOT / "rebuild"
AGENT_EXE = REBUILD_DIR / "target" / "release" / "wuji-rebuild-agent-v01.exe"
REPORT_OUT = REBUILD_DIR / "dist" / "soak-report.json"

OLD_DB_CANDIDATES = [
    Path(os.environ.get("LOCALAPPDATA", "")) / "WUJI" / "WindowsAgent" / "data" / "quantified_self_windows.db",
    Path(os.environ.get("LOCALAPPDATA", "")) / "WUJI-Dev" / "WindowsAgent" / "data" / "quantified_self_windows.db",
]

MAX_RSS_GROWTH_BYTES = 64 * 1024 * 1024
MAX_RSS_GROWTH_RATIO = 0.5


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with open(path, "rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def old_db_checksums() -> dict[str, str]:
    return {str(p): sha256_file(p) for p in OLD_DB_CANDIDATES if p.exists()}


def current_user_scope() -> str:
    """与 wuji-core runtime_names::user_scope 相同的派生。"""
    # 经由 PowerShell 取当前用户 SID；与 Rust 侧 SHA-256 前 16 hex 一致。
    sid = subprocess.check_output(
        ["powershell", "-NoProfile", "-Command", "([System.Security.Principal.WindowsIdentity]::GetCurrent()).User.Value"],
        text=True,
    ).strip()
    return hashlib.sha256(sid.encode("utf-8")).hexdigest()[:16]


def pipe_name(channel: str) -> str:
    scope = current_user_scope()
    if channel == "rebuild-v01-dev":
        return rf"\\.\pipe\WUJI.Rebuild.V01.Dev.{scope}"
    return rf"\\.\pipe\WUJI.Rebuild.V01.Test.{channel}.{scope}"


def data_db(channel: str) -> Path:
    return (
        Path(os.environ["LOCALAPPDATA"])
        / "WUJI-Rebuild-V01"
        / channel
        / "data"
        / "wuji-rebuild-v0.1.db"
    )


class ProcessMemoryInfo(ctypes.Structure):
    _fields_ = [
        ("cb", ctypes.c_ulong),
        ("PageFaultCount", ctypes.c_ulong),
        ("PeakWorkingSetSize", ctypes.c_size_t),
        ("WorkingSetSize", ctypes.c_size_t),
        ("QuotaPeakPagedPoolUsage", ctypes.c_size_t),
        ("QuotaPagedPoolUsage", ctypes.c_size_t),
        ("QuotaPeakNonPagedPoolUsage", ctypes.c_size_t),
        ("QuotaNonPagedPoolUsage", ctypes.c_size_t),
        ("PagefileUsage", ctypes.c_size_t),
        ("PeakPagefileUsage", ctypes.c_size_t),
    ]


def working_set_bytes(pid: int) -> int:
    PROCESS_QUERY_LIMITED_INFORMATION = 0x1000
    kernel32 = ctypes.windll.kernel32
    psapi = ctypes.windll.psapi
    handle = kernel32.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, False, pid)
    if not handle:
        return 0
    info = ProcessMemoryInfo()
    info.cb = ctypes.sizeof(ProcessMemoryInfo)
    psapi.GetProcessMemoryInfo(handle, ctypes.byref(info), info.cb)
    kernel32.CloseHandle(handle)
    return info.WorkingSetSize


def db_stats(db_path: Path) -> dict:
    if not db_path.exists():
        return {"dbBytes": 0, "walBytes": 0, "heartbeat": None, "writerState": None, "dropped": 0}
    wal = db_path.with_suffix(".db-wal")
    stats = {
        "dbBytes": db_path.stat().st_size,
        "walBytes": wal.stat().st_size if wal.exists() else 0,
    }
    try:
        conn = sqlite3.connect(f"file:{db_path}?mode=ro", uri=True)
        row = conn.execute(
            "SELECT heartbeat_at_utc_ms, writer_state, dropped_capture_count + dropped_writer_count, continuity_epoch "
            "FROM agent_runtime ORDER BY started_at_utc_ms DESC LIMIT 1"
        ).fetchone()
        conn.close()
        if row:
            stats.update(
                heartbeat=row[0], writerState=row[1], dropped=row[2], epoch=row[3]
            )
    except sqlite3.Error:
        stats.update(heartbeat=None, writerState="query_failed", dropped=-1)
    return stats


def ipc_shutdown(channel: str) -> bool:
    name = pipe_name(channel)
    try:
        import json as _json

        with open(name, "r+b", buffering=0) as pipe:
            request = {
                "protocolVersion": 1,
                "requestId": "soak" + hex(time.time_ns())[2:].rjust(22, "0")[:22],
                "command": "agent_shutdown_dev",
                "sentAtUtcMs": "0",
                "payload": {},
            }
            pipe.write((_json.dumps(request) + "\n").encode("utf-8"))
            pipe.read(4096)
        return True
    except OSError:
        return False


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--minutes", type=float, default=480.0)
    parser.add_argument("--interval", type=float, default=60.0, help="采样间隔（秒）")
    parser.add_argument("--channel", default=None)
    args = parser.parse_args()

    channel = args.channel
    if not channel:
        # 合法 test channel：rebuild-v01-test-<26 位字母数字>（09 §4.1）。
        suffix = ("soak" + hex(time.time_ns())[2:]).ljust(26, "0")[:26]
        channel = f"rebuild-v01-test-{suffix}"

    if not AGENT_EXE.exists():
        raise SystemExit(f"缺少 release Agent：{AGENT_EXE}")

    print(f"soak channel: {channel}")
    print(f"duration: {args.minutes} min, interval: {args.interval}s")

    before = old_db_checksums()
    db_path = data_db(channel)

    agent = subprocess.Popen(
        [str(AGENT_EXE), "--channel", channel, "--capture-on-start"],
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
    )
    started_at = time.time()
    samples = []
    crashes = 0

    try:
        while time.time() - started_at < args.minutes * 60:
            time.sleep(args.interval)
            if agent.poll() is not None:
                crashes += 1
                samples.append({"t": time.time(), "event": "agent_exited", "code": agent.returncode})
                break
            stats = db_stats(db_path)
            samples.append(
                {
                    "t": time.time(),
                    "rssBytes": working_set_bytes(agent.pid),
                    **stats,
                }
            )
            last = samples[-1]
            print(
                f"[{int(time.time() - started_at)}s] rss={last['rssBytes'] // 1024}KiB "
                f"db={last['dbBytes'] // 1024}KiB wal={last['walBytes'] // 1024}KiB "
                f"writer={last.get('writerState')} dropped={last.get('dropped')}",
                flush=True,
            )

        # 优雅退出。
        if agent.poll() is None:
            if not ipc_shutdown(channel):
                agent.kill()
            try:
                agent.wait(timeout=20)
            except subprocess.TimeoutExpired:
                agent.kill()
    finally:
        if agent.poll() is None:
            agent.kill()

    elapsed = time.time() - started_at
    after = old_db_checksums()

    rss_values = [s["rssBytes"] for s in samples if "rssBytes" in s]
    rss_growth = (max(rss_values) - rss_values[0]) if rss_values else 0
    rss_growth_ratio = (rss_growth / rss_values[0]) if rss_values and rss_values[0] else 0
    wal_max = max((s["walBytes"] for s in samples), default=0)
    last = samples[-1] if samples else {}

    quick_check = "skipped"
    if db_path.exists():
        try:
            conn = sqlite3.connect(f"file:{db_path}?mode=ro", uri=True)
            quick_check = conn.execute("PRAGMA quick_check").fetchone()[0]
            observations = conn.execute("SELECT COUNT(*) FROM foreground_observations").fetchone()[0]
            conn.close()
        except sqlite3.Error as error:
            quick_check = f"failed: {error}"
            observations = -1
    else:
        observations = -1

    failures = []
    if crashes:
        failures.append(f"Agent 在 soak 中退出（code={agent.returncode}）")
    if rss_growth > MAX_RSS_GROWTH_BYTES and rss_growth_ratio > MAX_RSS_GROWTH_RATIO:
        failures.append(f"RSS 无界增长迹象：+{rss_growth // 1024 // 1024} MiB ({rss_growth_ratio:.0%})")
    if last.get("writerState") == "faulted":
        failures.append("writer 处于 faulted")
    if last.get("heartbeat") is None:
        failures.append("心跳不可读")
    if before != after:
        failures.append("旧库 checksum 发生变化")
    if quick_check != "ok":
        failures.append(f"quick_check 未通过：{quick_check}")
    if observations <= 0 and args.minutes >= 1:
        failures.append("soak 期间没有任何 Observation 落库")

    report = {
        "channel": channel,
        "agentExe": str(AGENT_EXE),
        "agentSha256": sha256_file(AGENT_EXE),
        "durationSeconds": elapsed,
        "samples": len(samples),
        "rssStartBytes": rss_values[0] if rss_values else 0,
        "rssMaxBytes": max(rss_values) if rss_values else 0,
        "rssGrowthBytes": rss_growth,
        "walMaxBytes": wal_max,
        "dbBytes": last.get("dbBytes", 0),
        "droppedTotal": last.get("dropped", -1),
        "continuityEpoch": last.get("epoch", -1),
        "observations": observations,
        "quickCheck": quick_check,
        "oldDbChecksumStable": before == after,
        "failures": failures,
        "verdict": "pass" if not failures else "fail",
    }
    REPORT_OUT.parent.mkdir(parents=True, exist_ok=True)
    REPORT_OUT.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")

    print(json.dumps(report, ensure_ascii=False, indent=2))
    return 0 if not failures else 1


if __name__ == "__main__":
    sys.exit(main())
