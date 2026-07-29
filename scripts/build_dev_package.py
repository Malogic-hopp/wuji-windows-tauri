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
import hashlib
import json
import os
import shutil
import subprocess
import sys
import tempfile
import time
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


def verify_installed_launch(install_dir: Path) -> dict:
    """安装版 Desktop 启动并拉起安装版 Agent（审核 R06：并入自动验收）。

    以隔离 test channel 启动安装目录中的 Desktop；等待该 channel 数据库出现
    （证明 Agent 已启动并完成 bootstrap），并校验运行中的 Agent 进程
    确实来自安装目录。返回脱敏证据；失败抛 SystemExit。
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
    agent_pids: list[int] = []
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
            agent_pids = [
                int(p["ProcessId"])
                for p in processes_by_name("wuji-rebuild-agent-v01.exe")
                if p.get("ExecutablePath") and str(install_dir) in str(p["ExecutablePath"])
            ]
            if agent_pids:
                break
            time.sleep(1)
        if not agent_pids:
            raise SystemExit("未发现来自安装目录的 Agent 进程（可能拉起了错误位置的 Agent）")
        return {
            "performed": True,
            "installedAgentSpawned": True,
            "databaseCreated": True,
            "note": (
                "安装版 Desktop 通过 package-smoke test channel 调用 AgentController，"
                "拉起安装目录内 Agent 并完成 bootstrap；普通启动不自动拉起 Agent"
            ),
        }
    finally:
        desktop.kill()
        try:
            desktop.wait(timeout=10)
        except subprocess.TimeoutExpired:
            pass
        # Agent 设计上脱离 Desktop 生命周期存活；按 PID 结束并清理 test channel 数据。
        for pid in agent_pids:
            subprocess.run(["taskkill", "/F", "/PID", str(pid)], check=False,
                           stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        time.sleep(2)
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
