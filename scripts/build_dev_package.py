#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""WUJI Rebuild v0.1 dev package 构建与验收（09 §9.3、§11 V01-8、§12.1）。

步骤：
  1. 记录旧系统数据库 checksum（验收前后不得变化，09 §12.3 一票否决；
     缺失的旧库如实记录 missing，不伪造"已验证"）。
  2. 校验 release 二进制存在（desktop/agent）。
  3. pnpm build（前端嵌入）。
  4. pnpm tauri build（NSIS installer）。
  5. 静默安装到临时目录，校验固定 Agent 布局与禁止资产（Bridge/.NET/旧合同）。
  6. 安装版 Desktop 在 package-smoke test channel 中经 AgentController
     拉起安装目录 Agent（普通 Desktop 启动语义不变）。
  7. 生成 dev package manifest（Desktop/Agent version + SHA-256 + 安装版启动证据）。
  8. 复检旧库 checksum 不变。

用法：python scripts/build_dev_package.py [--skip-build]
"""

import argparse
import ctypes
import hashlib
import json
import os
import shutil
import sqlite3
import subprocess
import sys
import tempfile
import time
from ctypes import wintypes
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[1]
DESKTOP_DIR = REPO_ROOT / "apps" / "desktop"
DESKTOP_EXE = REPO_ROOT / "target" / "release" / "wuji-rebuild-desktop-v01.exe"
AGENT_EXE = REPO_ROOT / "target" / "release" / "wuji-rebuild-agent-v01.exe"
MANIFEST_OUT = REPO_ROOT / "dist" / "dev-package-manifest.json"
INSTALLER_GLOB_DIR = REPO_ROOT / "target" / "release" / "bundle" / "nsis"

# 09 §12.3：旧系统数据库（prod 与既有 dev channel），全程只读。
# 脱敏标签：证据中只出现 prod/dev，不输出本机绝对路径（审核 R06）。
OLD_DB_CANDIDATES = {
    "prod": Path(os.environ.get("LOCALAPPDATA", "")) / "WUJI" / "WindowsAgent" / "data" / "quantified_self_windows.db",
    "dev": Path(os.environ.get("LOCALAPPDATA", "")) / "WUJI-Dev" / "WindowsAgent" / "data" / "quantified_self_windows.db",
}

# 09 §12.1：rebuild dev 包禁止出现的资产。
FORBIDDEN_PATTERNS = [
    "bridge",
    "quantifiedself",
    "coreclr",
    "hostfxr",
    ".ni.dll",
    ".deps.json",
    ".runtimeconfig.json",
    "wuji-bridge",
]
REQUIRED_LAYOUT = [
    "wuji-rebuild-desktop-v01.exe",
    "Agent/wuji-rebuild-agent-v01.exe",
]

PROCESS_TERMINATE = 0x0001
SYNCHRONIZE = 0x00100000
PROCESS_QUERY_LIMITED_INFORMATION = 0x1000
WAIT_OBJECT_0 = 0x00000000
WAIT_TIMEOUT = 0x00000102
WAIT_FAILED = 0xFFFFFFFF

_KERNEL32 = ctypes.WinDLL("kernel32", use_last_error=True)
_OPEN_PROCESS = _KERNEL32.OpenProcess
_OPEN_PROCESS.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
_OPEN_PROCESS.restype = wintypes.HANDLE
_QUERY_FULL_PROCESS_IMAGE_NAME = _KERNEL32.QueryFullProcessImageNameW
_QUERY_FULL_PROCESS_IMAGE_NAME.argtypes = [
    wintypes.HANDLE,
    wintypes.DWORD,
    wintypes.LPWSTR,
    ctypes.POINTER(wintypes.DWORD),
]
_QUERY_FULL_PROCESS_IMAGE_NAME.restype = wintypes.BOOL
_TERMINATE_PROCESS = _KERNEL32.TerminateProcess
_TERMINATE_PROCESS.argtypes = [wintypes.HANDLE, wintypes.UINT]
_TERMINATE_PROCESS.restype = wintypes.BOOL
_WAIT_FOR_SINGLE_OBJECT = _KERNEL32.WaitForSingleObject
_WAIT_FOR_SINGLE_OBJECT.argtypes = [wintypes.HANDLE, wintypes.DWORD]
_WAIT_FOR_SINGLE_OBJECT.restype = wintypes.DWORD
_CLOSE_HANDLE = _KERNEL32.CloseHandle
_CLOSE_HANDLE.argtypes = [wintypes.HANDLE]
_CLOSE_HANDLE.restype = wintypes.BOOL


def normalized_windows_path(path: str | Path) -> str:
    """Windows 路径身份比较：绝对化、分隔符归一化并忽略大小写。"""
    return os.path.normcase(os.path.abspath(os.fspath(path)))


class VerifiedProcessHandle:
    """已验证映像路径的稳定进程对象句柄。

    句柄绑定具体进程对象而非裸 PID；即使进程退出后 PID 被系统复用，后续
    `terminate_and_wait` 也绝不会作用于新进程。
    """

    def __init__(self, pid: int, handle, image_path: str):
        self.pid = pid
        self._handle = handle
        self.image_path = image_path

    @classmethod
    def open_verified(cls, pid: int, expected_exe: Path):
        access = PROCESS_TERMINATE | SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION
        handle = _OPEN_PROCESS(access, False, pid)
        if not handle:
            raise ctypes.WinError(ctypes.get_last_error())
        try:
            length = wintypes.DWORD(32768)
            buffer = ctypes.create_unicode_buffer(length.value)
            if not _QUERY_FULL_PROCESS_IMAGE_NAME(handle, 0, buffer, ctypes.byref(length)):
                raise ctypes.WinError(ctypes.get_last_error())
            actual = buffer.value
            if normalized_windows_path(actual) != normalized_windows_path(expected_exe):
                raise RuntimeError(
                    f"PID {pid} 映像身份不符，拒绝终止 package-smoke 进程"
                )
            return cls(pid, handle, actual)
        except BaseException:
            _CLOSE_HANDLE(handle)
            raise

    def terminate_and_wait(self, timeout_ms: int = 10_000) -> None:
        """通过稳定句柄终止并确认退出；查询/等待失败一律不当成已退出。"""
        if self._handle is None:
            raise RuntimeError("进程句柄已关闭")
        terminated = bool(_TERMINATE_PROCESS(self._handle, 1))
        terminate_error = ctypes.get_last_error() if not terminated else 0
        wait_result = _WAIT_FOR_SINGLE_OBJECT(self._handle, timeout_ms)
        if wait_result == WAIT_OBJECT_0:
            return
        if wait_result == WAIT_TIMEOUT:
            raise RuntimeError(f"PID {self.pid} 在 {timeout_ms}ms 内未退出")
        if wait_result == WAIT_FAILED:
            raise ctypes.WinError(ctypes.get_last_error())
        if not terminated:
            raise ctypes.WinError(terminate_error)
        raise RuntimeError(f"PID {self.pid} 等待结果异常：{wait_result}")

    def close(self) -> None:
        if self._handle is not None:
            _CLOSE_HANDLE(self._handle)
            self._handle = None


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with open(path, "rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def old_db_checksums() -> dict:
    """每个旧库候选记录 present/sha256；缺失如实记录，不伪造 checksum（审核 R06）。"""
    result = {}
    for label, candidate in OLD_DB_CANDIDATES.items():
        if candidate.exists():
            result[label] = {"present": True, "sha256": sha256_file(candidate)}
        else:
            result[label] = {"present": False, "sha256": None}
    return result


def old_db_stable(before: dict, after: dict) -> bool:
    for label in OLD_DB_CANDIDATES:
        b, a = before[label], after[label]
        if b["present"] != a["present"]:
            return False
        if b["present"] and b["sha256"] != a["sha256"]:
            return False
    return True


def processes_by_name(image_name: str) -> list[dict]:
    """按映像名列出进程（ProcessId/ExecutablePath/CommandLine）。"""
    out = subprocess.check_output(
        [
            "powershell", "-NoProfile", "-Command",
            f"Get-CimInstance Win32_Process -Filter \"Name='{image_name}'\" "
            "| Select-Object ProcessId,ExecutablePath,CommandLine | ConvertTo-Json -Compress",
        ],
        text=True,
    ).strip()
    if not out:
        return []
    data = json.loads(out)
    return data if isinstance(data, list) else [data]


def process_matches_smoke_identity(process: dict, expected_exe: Path, channel: str) -> bool:
    """CIM 交付候选时同时证明固定映像路径与隔离 channel。"""
    executable = process.get("ExecutablePath")
    command_line = process.get("CommandLine") or ""
    if not executable:
        return False
    if normalized_windows_path(executable) != normalized_windows_path(expected_exe):
        return False
    tokens = command_line.replace('"', "").split()
    return any(
        token == "--channel" and index + 1 < len(tokens) and tokens[index + 1] == channel
        for index, token in enumerate(tokens)
    )


def acquire_smoke_agent_handles(expected_exe: Path, channel: str) -> list[VerifiedProcessHandle]:
    """将 CIM 的 path+channel 候选升级为稳定进程句柄。

    打开句柄后再次查询 CIM，关闭“候选查询→OpenProcess”之间的身份变化窗口；
    句柄自身再校验映像路径，之后 PID 复用不影响清理目标。
    """
    handles = []
    for process in processes_by_name("wuji-rebuild-agent-v01.exe"):
        if not process_matches_smoke_identity(process, expected_exe, channel):
            continue
        pid = int(process["ProcessId"])
        try:
            handle = VerifiedProcessHandle.open_verified(pid, expected_exe)
        except (OSError, RuntimeError):
            continue
        try:
            confirmed = any(
                int(current["ProcessId"]) == pid
                and process_matches_smoke_identity(current, expected_exe, channel)
                for current in processes_by_name("wuji-rebuild-agent-v01.exe")
            )
        except BaseException:
            handle.close()
            raise
        if confirmed:
            handles.append(handle)
        else:
            handle.close()
    return handles


def read_smoke_state(db_path: Path):
    """只读查询 smoke channel 数据库的最新 runtime 与 Observation 数。

    WAL 并发读：Agent（写者）运行中仍可安全查询。返回
    (process_state, capture_state) 与 Observation 计数；库尚不可读返回 (None, None)。
    """
    try:
        con = sqlite3.connect(f"file:{db_path}?mode=ro", uri=True)
        try:
            row = con.execute(
                "SELECT process_state, capture_state FROM agent_runtime "
                "ORDER BY rowid DESC LIMIT 1"
            ).fetchone()
            observations = con.execute(
                "SELECT COUNT(*) FROM foreground_observations"
            ).fetchone()[0]
            return row, observations
        finally:
            con.close()
    except sqlite3.Error:
        return None, None


def wait_for_smoke_stopped(
    read_state,
    *,
    ready_attempts: int = 20,
    stable_samples: int = 5,
    sample_interval_seconds: float = 1.0,
    sleep=time.sleep,
) -> dict:
    """等待 Agent 完成启动，并证明一个稳定窗口内从未开始采集。

    `read_state` 可注入，测试无需真实休眠；生产默认在首次看到
    Running/Stopped/0 后继续观察 4 秒（共 5 个样本）。
    """
    if ready_attempts < 1 or stable_samples < 1:
        raise ValueError("ready_attempts 与 stable_samples 必须为正数")

    ready_state = None
    last_state = None
    for attempt in range(ready_attempts):
        row, observations = read_state()
        if row is not None:
            last_state = {
                "processState": row[0],
                "captureState": row[1],
                "observationCount": observations,
            }
            if row[1] != "stopped" or observations != 0:
                raise RuntimeError(
                    "package-smoke 检测到意外采集："
                    f"{last_state}"
                )
            if row[0] == "running":
                ready_state = last_state
                break
        if attempt + 1 < ready_attempts:
            sleep(sample_interval_seconds)
    if ready_state is None:
        raise RuntimeError(
            "package-smoke 未进入 Running/Stopped/0 就绪态："
            f"{last_state}"
        )

    for _ in range(stable_samples - 1):
        sleep(sample_interval_seconds)
        row, observations = read_state()
        current = None if row is None else {
            "processState": row[0],
            "captureState": row[1],
            "observationCount": observations,
        }
        if current != ready_state:
            raise RuntimeError(
                "package-smoke 稳定观察窗口内状态发生变化："
                f"expected={ready_state}, actual={current}"
            )

    return {
        **ready_state,
        "stableSamples": stable_samples,
        "stableWindowSeconds": sample_interval_seconds * (stable_samples - 1),
    }


def verify_installed_launch(install_dir: Path) -> dict:
    """安装版 Desktop 启动并拉起安装版 Agent（审核 R06：并入自动验收）。

    以隔离 test channel 启动安装目录中的 Desktop；等待该 channel 数据库出现
    （证明 Agent 已启动并完成 bootstrap），并校验运行中的 Agent 进程
    确实来自安装目录。随后**真实断言** smoke 只拉起不自动开录（09 §9.3）：
    最新 runtime 的 capture_state 必须保持 `stopped` 且 Observation 数为 0，
    而不是仅靠说明文字。返回脱敏证据；失败抛 SystemExit。
    """
    suffix = ("pkg" + hex(time.time_ns())[2:]).ljust(26, "0")[:26]
    channel = f"rebuild-v01-test-{suffix}"
    env = dict(
        os.environ,
        WUJI_REBUILD_CHANNEL=channel,
        WUJI_REBUILD_PACKAGE_SMOKE_AUTOSTART="1",
    )
    db_expected = (
        Path(os.environ["LOCALAPPDATA"])
        / "WUJI-Rebuild-V01"
        / channel
        / "data"
        / "wuji-rebuild-v0.1.db"
    )
    channel_root = db_expected.parents[1]
    desktop = subprocess.Popen(
        [str(install_dir / "wuji-rebuild-desktop-v01.exe")],
        env=env,
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
    )
    expected_agent_exe = install_dir / "Agent" / "wuji-rebuild-agent-v01.exe"
    agent_handles: list[VerifiedProcessHandle] = []
    try:
        for _ in range(45):
            if db_expected.exists():
                break
            time.sleep(1)
        if not db_expected.exists():
            raise SystemExit(
                "安装版 Desktop package-smoke 启动后 45 秒内未创建 channel 数据库"
                "（AgentController 未拉起安装目录 Agent）"
            )
        # 运行中的 Agent 必须来自安装目录（错版/混版检测）。
        for _ in range(15):
            agent_handles = acquire_smoke_agent_handles(expected_agent_exe, channel)
            if agent_handles:
                break
            time.sleep(1)
        if not agent_handles:
            raise SystemExit("未发现来自安装目录的 Agent 进程（可能拉起了错误位置的 Agent）")

        # smoke 只拉起、不自动开录（09 §9.3）：真实断言最新 runtime 保持
        # capture_state=stopped 且零 Observation。若安装版 Desktop 意外走了
        # 自动开始记录，capture_state 会变为 running 且 Observation 增长——
        # 必须先进入 process=running，再在稳定窗口内连续保持 stopped/0；不能在
        # bootstrap 与异步自动开录之间抢读一个瞬时 stopped 就提前通过。
        try:
            smoke_state = wait_for_smoke_stopped(
                lambda: read_smoke_state(db_expected),
            )
        except RuntimeError as error:
            raise SystemExit(str(error)) from error
        return {
            "performed": True,
            "installedAgentSpawned": True,
            "databaseCreated": True,
            "captureStayedStopped": True,
            "observationCount": smoke_state["observationCount"],
            "stableSamples": smoke_state["stableSamples"],
            "stableWindowSeconds": smoke_state["stableWindowSeconds"],
            "note": (
                "安装版 Desktop 通过 package-smoke test channel 调用 AgentController，"
                "拉起安装目录内 Agent 并完成 bootstrap；smoke 只验证拉起链路，"
                "自动开始记录由 Desktop 本地偏好（09 §9.4）决定；实测最新 runtime "
                f"process_state=running、capture_state=stopped、Observation 数 "
                f"{smoke_state['observationCount']}，连续稳定观察 "
                f"{smoke_state['stableWindowSeconds']} 秒"
            ),
        }
    finally:
        desktop.kill()
        try:
            desktop.wait(timeout=10)
        except subprocess.TimeoutExpired:
            pass
        # Agent 设计上脱离 Desktop 生命周期存活。补抓早期失败窗口中可能已启动
        # 但尚未登记的同 path+channel 进程；随后只通过稳定句柄终止，绝不裸 PID。
        cleanup_discovery_error = None
        try:
            for process in acquire_smoke_agent_handles(expected_agent_exe, channel):
                # 即使 PID 数值相同也保留新句柄：旧进程退出后 PID 可能复用，
                # 两个句柄分别绑定各自进程对象；重复终止同一对象也是安全幂等的。
                agent_handles.append(process)
        except (OSError, subprocess.SubprocessError, RuntimeError) as error:
            cleanup_discovery_error = str(error)

        cleanup_errors = []
        for process in agent_handles:
            try:
                process.terminate_and_wait()
            except (OSError, RuntimeError) as error:
                cleanup_errors.append(f"PID {process.pid}: {error}")
            finally:
                process.close()
        if cleanup_discovery_error is not None or cleanup_errors:
            details = []
            if cleanup_discovery_error is not None:
                details.append(f"身份确认失败: {cleanup_discovery_error}")
            details.extend(cleanup_errors)
            raise SystemExit(
                "package-smoke Agent 未确认退出，保留 channel 目录："
                + "; ".join(details)
            )
        shutil.rmtree(channel_root, ignore_errors=True)


def run(command: list[str], cwd: Path | None = None) -> None:
    resolved = shutil.which(command[0]) or shutil.which(command[0] + ".cmd") or command[0]
    print(f"+ {' '.join([resolved, *command[1:]])}", flush=True)
    subprocess.run([resolved, *command[1:]], cwd=cwd, check=True)


def find_installer() -> Path:
    candidates = sorted(
        INSTALLER_GLOB_DIR.glob("*.exe"),
        key=lambda path: path.stat().st_mtime,
        reverse=True,
    )
    if not candidates:
        raise SystemExit(f"未找到 NSIS installer：{INSTALLER_GLOB_DIR}")
    return candidates[0]


def scan_install_tree(root: Path) -> list[str]:
    violations = []
    for path in root.rglob("*"):
        if not path.is_file():
            continue
        relative = path.relative_to(root).as_posix().lower()
        for pattern in FORBIDDEN_PATTERNS:
            if pattern in relative:
                violations.append(relative)
                break
    return violations


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--skip-build", action="store_true", help="跳过 cargo/pnpm 构建，只验收现有产物")
    args = parser.parse_args()

    print("== 1/8 旧库 checksum（验收前） ==")
    before = old_db_checksums()
    for label, status in before.items():
        if status["present"]:
            print(f"  {status['sha256'][:16]}…  old-db[{label}]")
        else:
            print(f"  old-db[{label}]: missing（如实记录，不伪造 checksum）")

    if not args.skip_build:
        print("== 2/8 cargo release 构建 ==")
        run(["cargo", "build", "--release", "--workspace"], cwd=REPO_ROOT)
        print("== 3/8 前端构建 ==")
        run(["pnpm", "build"], cwd=DESKTOP_DIR)
        print("== 4/8 tauri bundle ==")
        run(["pnpm", "tauri", "build"], cwd=DESKTOP_DIR)
    else:
        print("== 2-4/8 跳过构建（--skip-build） ==")

    for exe in (DESKTOP_EXE, AGENT_EXE):
        if not exe.exists():
            raise SystemExit(f"缺少 release 二进制：{exe.name}")

    installer = find_installer()
    print(f"installer: {installer.name}")

    print("== 5/8 静默安装与资产校验 ==")
    install_dir = Path(tempfile.mkdtemp(prefix="wuji-v01-install-"))
    try:
        run([str(installer), "/S", f"/D={install_dir}"])
        for _ in range(30):
            if all((install_dir / rel).exists() for rel in REQUIRED_LAYOUT):
                break
            time.sleep(1)
        missing = [rel for rel in REQUIRED_LAYOUT if not (install_dir / rel).exists()]
        if missing:
            raise SystemExit(f"安装目录缺少固定布局文件：{missing}")
        print("  固定布局 OK：desktop + Agent/wuji-rebuild-agent-v01.exe")

        violations = scan_install_tree(install_dir)
        if violations:
            raise SystemExit(f"包内出现禁止资产（Bridge/.NET/旧合同）：{violations}")
        print("  禁止资产扫描 OK（无 Bridge/.NET/旧合同）")

        # Agent 二进制 byte 级一致（未被 bundler 处理）；desktop 记录双侧 hash：
        # bundler 会对自身 exe 写入 bundle 类型元数据，磁盘工件与安装副本允许不同。
        if sha256_file(install_dir / "Agent/wuji-rebuild-agent-v01.exe") != sha256_file(AGENT_EXE):
            raise SystemExit("Agent 安装副本与 release 构建不一致（错版）")
        print("  Agent 二进制一致性 OK")
        desktop_note = "desktop exe 经 bundler 写入 bundle 元数据，记录双侧 hash（09 §9.3 错版校验以 Agent 为准）"
        print(f"  {desktop_note}")

        print("== 6/8 安装版启动校验（Desktop 拉起安装目录 Agent） ==")
        installed_launch = verify_installed_launch(install_dir)
        print(f"  {installed_launch['note']}")
    finally:
        uninstaller = next(install_dir.glob("uninst*.exe"), None)
        if uninstaller:
            subprocess.run([str(uninstaller), "/S"], check=False)
        shutil.rmtree(install_dir, ignore_errors=True)

    print("== 7/8 生成 dev package manifest ==")
    MANIFEST_OUT.parent.mkdir(parents=True, exist_ok=True)
    manifest = {
        "channel": "rebuild-v01-dev",
        "productName": "吾迹 Rebuild v0.1（开发）",
        "createdAtUtc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
        "desktop": {
            "name": DESKTOP_EXE.name,
            "version": "0.1.0",
            "sha256": sha256_file(DESKTOP_EXE),
        },
        "agent": {
            "name": AGENT_EXE.name,
            "version": "0.1.0",
            "sha256": sha256_file(AGENT_EXE),
        },
        "installer": {
            "name": installer.name,
            "sha256": sha256_file(installer),
        },
        "validation": {
            "layout": REQUIRED_LAYOUT,
            "forbiddenAssetScan": "pass",
            "installedLaunch": installed_launch,
            "oldDatabases": {
                label: ({"present": True, "sha256": status["sha256"]} if status["present"] else {"present": False})
                for label, status in before.items()
            },
            "note": "dev-only 校验 hash，不充当生产身份认证（09 §9.3）；desktop exe 经 bundler 写入 bundle 元数据，错版校验以 Agent 为准",
        },
    }
    MANIFEST_OUT.write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8"
    )
    print("  manifest: dist/dev-package-manifest.json")

    print("== 8/8 旧库 checksum（验收后） ==")
    after = old_db_checksums()
    if not old_db_stable(before, after):
        raise SystemExit("旧库 checksum 发生变化（一票否决）")
    if any(s["present"] for s in before.values()):
        print("  旧库 checksum 不变 OK（verified_stable）")
    else:
        print("  旧库均不存在（not_verifiable_no_old_db_present，如实记录）")

    print("\nDEV PACKAGE OK")
    print(f"  installer: {installer.name}")
    print("  manifest:  dist/dev-package-manifest.json")
    return 0


if __name__ == "__main__":
    sys.exit(main())
