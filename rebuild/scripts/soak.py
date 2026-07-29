#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""WUJI Rebuild v0.1 soak 验收（09 §12.2：持续运行无 crash、死锁或明显无界内存/WAL 增长）。

做法：
  1. 记录旧库 checksum（soak 前后不得变化；两个候选各自记录 存在/缺失，
     缺失时绝不声称"已验证 checksum 不变"）。
  2. 在隔离 test channel 以 --capture-on-start 启动 release Agent。
  3. 周期采样：进程 RSS、DB/WAL 大小、agent_runtime 心跳/drop 计数/writer 状态。
  4. 结束时先 hello 再 agent_shutdown_dev，校验响应 ok 且进程限时以 exit code 0
     退出；任何强杀都判失败。
  5. 复检：
     - 无 crash（进程全程存活，单实例）
     - RSS 增长有界（< 64 MiB 且 < 50%）
     - WAL 趋势收敛（见 WAL_TREND 判据）
     - 心跳严格单调推进（全程采样）
     - writer 全程不曾 faulted
     - DB quick_check 通过、旧库 checksum 不变（仅对真实存在的旧库判定）
  6. 输出脱敏、可提交的 soak-report.json：不含用户名与本机绝对路径。

用法：python rebuild/scripts/soak.py --minutes 480 [--interval 60] [--channel rebuild-v01-test-<ulid>]
"""

import argparse
import ctypes
import hashlib
import json
import os
import platform
import random
import sqlite3
import subprocess
import sys
import time
from datetime import datetime, timezone
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
REBUILD_DIR = REPO_ROOT / "rebuild"
AGENT_EXE = REBUILD_DIR / "target" / "release" / "wuji-rebuild-agent-v01.exe"
AGENT_EXE_LABEL = "rebuild/target/release/wuji-rebuild-agent-v01.exe"
REPORT_OUT = REBUILD_DIR / "dist" / "soak-report.json"
CARGO_LOCK = REBUILD_DIR / "Cargo.lock"

# 脱敏标签：旧库只以 prod/dev 标签出现在证据中，不输出本机绝对路径（审核 R06）。
OLD_DB_CANDIDATES = {
    "prod": Path(os.environ.get("LOCALAPPDATA", "")) / "WUJI" / "WindowsAgent" / "data" / "quantified_self_windows.db",
    "dev": Path(os.environ.get("LOCALAPPDATA", "")) / "WUJI-Dev" / "WindowsAgent" / "data" / "quantified_self_windows.db",
}

MAX_RSS_GROWTH_BYTES = 64 * 1024 * 1024
MAX_RSS_GROWTH_RATIO = 0.5
WAL_END_MAX_BYTES = 4 * 1024 * 1024
SHUTDOWN_GRACE_SECONDS = 20

CRITERIA = [
    "no_crash: 进程全程存活，exit code 0 优雅退出，不允许强杀",
    "rss_bounded: RSS 增长 < 64 MiB 且 < 50%",
    "wal_trend: 结束时 WAL <= 4 MiB，且末段均值 <= 前段均值*2 + 1 MiB（无趋势性增长）",
    "heartbeat_monotonic: 全程心跳采样严格单调递增",
    "writer_never_faulted: 任一采样点 writer_state 不得为 faulted",
    "quick_check: PRAGMA quick_check = ok",
    "old_db_stable: 存在的旧库 checksum 前后不变；不存在的旧库如实记录 missing",
    "observations: >= 1 分钟 soak 必须有 Observation 落库",
]


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with open(path, "rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def old_db_status() -> dict:
    """每个旧库候选记录 present/sha256；缺失如实记录，不伪造 checksum（审核 R06）。"""
    status = {}
    for label, path in OLD_DB_CANDIDATES.items():
        if path.exists():
            status[label] = {"present": True, "sha256": sha256_file(path)}
        else:
            status[label] = {"present": False, "sha256": None}
    return status


def old_db_stable(before: dict, after: dict) -> bool:
    """只对真实存在的旧库判定稳定性；缺失→缺失不算"已验证"。"""
    for label in OLD_DB_CANDIDATES:
        b, a = before[label], after[label]
        if b["present"] != a["present"]:
            return False
        if b["present"] and b["sha256"] != a["sha256"]:
            return False
    return True


def ulid() -> str:
    """ULID（26 位 Crockford Base32）：48 位毫秒时间 + 80 位随机。"""
    alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"
    value = ((int(time.time() * 1000) & ((1 << 48) - 1)) << 80) | random.getrandbits(80)
    return "".join(alphabet[(value >> (5 * (25 - i))) & 31] for i in range(26))


def current_user_scope() -> str:
    """与 wuji-core runtime_names::user_scope 相同的派生。"""
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
            "FROM agent_runtime ORDER BY started_at_utc_ms DESC, runtime_id DESC LIMIT 1"
        ).fetchone()
        conn.close()
        if row:
            stats.update(
                heartbeat=row[0], writerState=row[1], dropped=row[2], epoch=row[3]
            )
    except sqlite3.Error:
        stats.update(heartbeat=None, writerState="query_failed", dropped=-1)
    return stats


def _pipe_roundtrip(pipe, request: dict) -> dict:
    pipe.write((json.dumps(request) + "\n").encode("utf-8"))
    buffer = b""
    while b"\n" not in buffer:
        chunk = pipe.read(4096)
        if not chunk:
            raise OSError("pipe 被对端关闭")
        buffer += chunk
        if len(buffer) > 64 * 1024 + 4096:
            raise OSError("响应超过上限")
    return json.loads(buffer.split(b"\n", 1)[0].decode("utf-8"))


def ipc_graceful_shutdown(channel: str) -> tuple[bool, str]:
    """先 hello 再 agent_shutdown_dev，解析两次响应（审核 R06）。

    返回 (成功与否, 说明)。只证明服务端接受了退出命令；退出码由调用方另行校验。
    """
    name = pipe_name(channel)
    try:
        with open(name, "r+b", buffering=0) as pipe:
            hello = _pipe_roundtrip(pipe, {
                "protocolVersion": 1,
                "requestId": ulid(),
                "command": "hello",
                "sentAtUtcMs": str(int(time.time() * 1000)),
                "payload": {
                    "desktopVersion": "soak",
                    "protocolVersion": 1,
                    "channel": channel,
                },
            })
            if not hello.get("ok"):
                return False, f"hello 被拒绝: {hello.get('error')}"
            shutdown = _pipe_roundtrip(pipe, {
                "protocolVersion": 1,
                "requestId": ulid(),
                "command": "agent_shutdown_dev",
                "sentAtUtcMs": str(int(time.time() * 1000)),
                "payload": {},
            })
            if not shutdown.get("ok"):
                return False, f"shutdown 被拒绝: {shutdown.get('error')}"
            if shutdown.get("result", {}).get("willExit") is not True:
                return False, f"shutdown 响应缺少 willExit: {shutdown}"
        return True, "hello + shutdown 均被接受"
    except (OSError, json.JSONDecodeError) as error:
        return False, f"IPC 传输失败: {error}"


def git_commit() -> str:
    try:
        return subprocess.check_output(
            ["git", "rev-parse", "HEAD"], cwd=REPO_ROOT, text=True
        ).strip()
    except (subprocess.CalledProcessError, OSError):
        return "unknown"


def wal_trend_ok(wals: list[int]) -> bool:
    """WAL 趋势判据（显式）：末段均值不得相对前段均值趋势性增长。"""
    if not wals:
        return True
    if wals[-1] > WAL_END_MAX_BYTES:
        return False
    if len(wals) < 3:
        return True
    third = max(1, len(wals) // 3)
    first_mean = sum(wals[:third]) / third
    last_mean = sum(wals[-third:]) / third
    return last_mean <= first_mean * 2 + 1024 * 1024


def heartbeats_monotonic(heartbeats: list[int]) -> bool:
    return all(later > earlier for earlier, later in zip(heartbeats, heartbeats[1:]))


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
        raise SystemExit(f"缺少 release Agent：{AGENT_EXE_LABEL}")

    print(f"soak channel: {channel}")
    print(f"duration: {args.minutes} min, interval: {args.interval}s")

    before = old_db_status()
    db_path = data_db(channel)
    started_at_utc = datetime.now(timezone.utc).isoformat()

    agent = subprocess.Popen(
        [str(AGENT_EXE), "--channel", channel, "--capture-on-start"],
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
    )
    started_at = time.time()
    samples = []
    crashes = 0
    forced_kill = False
    shutdown_note = "未执行（Agent 已提前退出）"

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

        # 优雅退出：hello → agent_shutdown_dev → 校验响应与 exit code 0；强杀判失败。
        if agent.poll() is None:
            ok, note = ipc_graceful_shutdown(channel)
            shutdown_note = note
            if not ok:
                print(f"优雅退出失败：{note}", flush=True)
            try:
                agent.wait(timeout=SHUTDOWN_GRACE_SECONDS)
            except subprocess.TimeoutExpired:
                agent.kill()
                forced_kill = True
    finally:
        if agent.poll() is None:
            agent.kill()
            forced_kill = True

    elapsed = time.time() - started_at
    after = old_db_status()
    exit_code = agent.returncode

    rss_values = [s["rssBytes"] for s in samples if "rssBytes" in s]
    rss_growth = (max(rss_values) - rss_values[0]) if rss_values else 0
    rss_growth_ratio = (rss_growth / rss_values[0]) if rss_values and rss_values[0] else 0
    wal_series = [s["walBytes"] for s in samples if "walBytes" in s]
    heartbeats = [s["heartbeat"] for s in samples if s.get("heartbeat") is not None]
    writer_faulted_ever = any(s.get("writerState") == "faulted" for s in samples)
    wal_max = max(wal_series, default=0)
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

    stable = old_db_stable(before, after)
    any_old_db_present = any(s["present"] for s in before.values())

    failures = []
    if crashes:
        failures.append(f"Agent 在 soak 中退出（code={exit_code}）")
    if forced_kill:
        failures.append("Agent 未在限时内优雅退出，发生强杀")
    if not crashes and exit_code != 0:
        failures.append(f"Agent exit code 非 0：{exit_code}")
    # S2-07：RSS 增长超过任一上限即失败（OR 非 AND）。
    if rss_growth >= MAX_RSS_GROWTH_BYTES or rss_growth_ratio >= MAX_RSS_GROWTH_RATIO:
        failures.append(f"RSS 无界增长迹象：+{rss_growth // 1024 // 1024} MiB ({rss_growth_ratio:.0%})")
    if writer_faulted_ever:
        failures.append("writer 在 soak 期间曾处于 faulted")
    if not heartbeats_monotonic(heartbeats):
        failures.append("心跳未严格单调推进")
    if len(heartbeats) < 2 and args.minutes >= 1:
        failures.append("有效心跳采样不足（< 2）")
    if not wal_trend_ok(wal_series):
        failures.append("WAL 趋势未收敛（末段超限或趋势性增长）")
    if not stable:
        failures.append("旧库 checksum 发生变化")
    if quick_check != "ok":
        failures.append(f"quick_check 未通过：{quick_check}")
    if observations <= 0 and args.minutes >= 1:
        failures.append("soak 期间没有任何 Observation 落库")

    old_db_evidence = {
        label: (
            {"present": True, "sha256Before": before[label]["sha256"], "sha256After": after[label]["sha256"]}
            if before[label]["present"]
            else {"present": False}
        )
        for label in OLD_DB_CANDIDATES
    }

    # 脱敏证据（审核 R06）：无用户名、无本机绝对路径；可提交到 evidence 目录。
    report = {
        "schema": "wuji-rebuild-soak-report/2",
        "channel": channel,
        "agentExe": AGENT_EXE_LABEL,
        "agentSha256": sha256_file(AGENT_EXE),
        "durationSeconds": int(elapsed),
        "samples": len(samples),
        "gracefulShutdown": {
            "note": shutdown_note,
            "exitCode": exit_code,
            "forcedKill": forced_kill,
        },
        "rssStartBytes": rss_values[0] if rss_values else 0,
        "rssMaxBytes": max(rss_values) if rss_values else 0,
        "rssGrowthBytes": rss_growth,
        "walMaxBytes": wal_max,
        "walEndBytes": wal_series[-1] if wal_series else 0,
        "dbBytes": last.get("dbBytes", 0),
        "droppedTotal": last.get("dropped", -1),
        "continuityEpoch": last.get("epoch", -1),
        "heartbeatSamples": len(heartbeats),
        "observations": observations,
        "quickCheck": quick_check,
        "oldDatabases": old_db_evidence,
        # 缺失旧库时明确为 not_verifiable，绝不写"两库 checksum 不变"（审核 R06）。
        "oldDbChecksumStatus": (
            "verified_stable" if stable and any_old_db_present
            else "changed" if not stable
            else "not_verifiable_no_old_db_present"
        ),
        "evidence": {
            "gitCommit": git_commit(),
            "cargoLockSha256": sha256_file(CARGO_LOCK) if CARGO_LOCK.exists() else None,
            "os": platform.platform(),
            "python": platform.python_version(),
            "command": f"rebuild/scripts/soak.py --minutes {args.minutes} --interval {args.interval}",
            "startedAtUtc": started_at_utc,
            "criteria": CRITERIA,
        },
        "failures": failures,
        "verdict": "pass" if not failures else "fail",
    }
    REPORT_OUT.parent.mkdir(parents=True, exist_ok=True)
    REPORT_OUT.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")

    print(json.dumps(report, ensure_ascii=False, indent=2))
    return 0 if not failures else 1


if __name__ == "__main__":
    sys.exit(main())
