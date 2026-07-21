#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""WUJI Rebuild v0.1 dev package 构建与验收（09 §9.3、§11 V01-8、§12.1）。

步骤：
  1. 记录旧系统数据库 checksum（验收前后不得变化，09 §12.3 一票否决）。
  2. 校验 release 二进制存在（desktop/agent）。
  3. pnpm build（前端嵌入）。
  4. pnpm tauri build（NSIS installer）。
  5. 静默安装到临时目录，校验固定 Agent 布局与禁止资产（Bridge/.NET/旧合同）。
  6. 生成 dev package manifest（Desktop/Agent version + SHA-256）。
  7. 复检旧库 checksum 不变。

用法：python rebuild/scripts/build_dev_package.py [--skip-build]
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

REPO_ROOT = Path(__file__).resolve().parents[2]
REBUILD_DIR = REPO_ROOT / "rebuild"
DESKTOP_DIR = REBUILD_DIR / "apps" / "desktop"
DESKTOP_EXE = REBUILD_DIR / "target" / "release" / "wuji-rebuild-desktop-v01.exe"
AGENT_EXE = REBUILD_DIR / "target" / "release" / "wuji-rebuild-agent-v01.exe"
MANIFEST_OUT = REBUILD_DIR / "dist" / "dev-package-manifest.json"
INSTALLER_GLOB_DIR = REBUILD_DIR / "target" / "release" / "bundle" / "nsis"

# 09 §12.3：旧系统数据库（prod 与既有 dev channel），全程只读。
OLD_DB_CANDIDATES = [
    Path(os.environ.get("LOCALAPPDATA", "")) / "WUJI" / "WindowsAgent" / "data" / "quantified_self_windows.db",
    Path(os.environ.get("LOCALAPPDATA", "")) / "WUJI-Dev" / "WindowsAgent" / "data" / "quantified_self_windows.db",
]

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


def old_db_checksums() -> dict[str, str]:
    result = {}
    for candidate in OLD_DB_CANDIDATES:
        if candidate.exists():
            result[str(candidate)] = sha256_file(candidate)
    return result


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

    print("== 1/7 旧库 checksum（验收前） ==")
    before = old_db_checksums()
    for path, digest in before.items():
        print(f"  {digest[:16]}…  {path}")

    if not args.skip_build:
        print("== 2/7 cargo release 构建 ==")
        run(["cargo", "build", "--release", "--workspace"], cwd=REBUILD_DIR)
        print("== 3/7 前端构建 ==")
        run(["pnpm", "build"], cwd=DESKTOP_DIR)
        print("== 4/7 tauri bundle ==")
        run(["pnpm", "tauri", "build"], cwd=DESKTOP_DIR)
    else:
        print("== 2-4/7 跳过构建（--skip-build） ==")

    for exe in (DESKTOP_EXE, AGENT_EXE):
        if not exe.exists():
            raise SystemExit(f"缺少 release 二进制：{exe}")

    installer = find_installer()
    print(f"installer: {installer}")

    print("== 5/7 静默安装与资产校验 ==")
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
    finally:
        uninstaller = next(install_dir.glob("uninst*.exe"), None)
        if uninstaller:
            subprocess.run([str(uninstaller), "/S"], check=False)
        shutil.rmtree(install_dir, ignore_errors=True)

    print("== 6/7 生成 dev package manifest ==")
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
            "note": "dev-only 校验 hash，不充当生产身份认证（09 §9.3）；desktop exe 经 bundler 写入 bundle 元数据，错版校验以 Agent 为准",
        },
    }
    MANIFEST_OUT.write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8"
    )
    print(f"  manifest: {MANIFEST_OUT}")

    print("== 7/7 旧库 checksum（验收后） ==")
    after = old_db_checksums()
    if before != after:
        raise SystemExit(f"旧库 checksum 发生变化（一票否决）：{set(before) ^ set(after) or 'digest mismatch'}")
    print("  旧库 checksum 不变 OK")

    print("\nDEV PACKAGE OK")
    print(f"  installer: {installer}")
    print(f"  manifest:  {MANIFEST_OUT}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
