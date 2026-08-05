# -*- coding: utf-8 -*-
"""soak.py 受控退出判定纯函数的回归测试（阶段 4.7 P2-03）。

覆盖：成功、IPC 失败（hello 拒 / shutdown 拒 / willExit 缺失）、未尝试、
超时强杀、提前 exit 0（不得通过）。运行：python -m unittest scripts.tests.test_soak_verdict
"""

import importlib.util
import unittest
from pathlib import Path


SCRIPT = Path(__file__).resolve().parents[1] / "soak.py"
SPEC = importlib.util.spec_from_file_location("soak", SCRIPT)
SOAK = importlib.util.module_from_spec(SPEC)
assert SPEC.loader is not None
SPEC.loader.exec_module(SOAK)


class ControlledExitFailuresTest(unittest.TestCase):
    def test_success_path_passes(self):
        failures = SOAK.controlled_exit_failures(
            shutdown_attempted=True,
            hello_ok=True,
            shutdown_ok=True,
            will_exit=True,
            exit_code=0,
            forced_kill=False,
            agent_exited_early=False,
        )
        self.assertEqual(failures, [])

    def test_hello_rejected_fails(self):
        failures = SOAK.controlled_exit_failures(
            shutdown_attempted=True,
            hello_ok=False,
            shutdown_ok=False,
            will_exit=False,
            exit_code=0,
            forced_kill=False,
            agent_exited_early=False,
        )
        self.assertIn("hello 响应未 ok", failures)

    def test_shutdown_rejected_fails(self):
        failures = SOAK.controlled_exit_failures(
            shutdown_attempted=True,
            hello_ok=True,
            shutdown_ok=False,
            will_exit=False,
            exit_code=0,
            forced_kill=False,
            agent_exited_early=False,
        )
        self.assertIn("shutdown 响应未 ok", failures)

    def test_will_exit_missing_fails(self):
        failures = SOAK.controlled_exit_failures(
            shutdown_attempted=True,
            hello_ok=True,
            shutdown_ok=True,
            will_exit=False,
            exit_code=0,
            forced_kill=False,
            agent_exited_early=False,
        )
        self.assertIn("shutdown 响应缺少 willExit=true", failures)

    def test_not_attempted_fails(self):
        # 受控路径未执行（Agent 存活但 shutdown 未发出）。
        failures = SOAK.controlled_exit_failures(
            shutdown_attempted=False,
            hello_ok=False,
            shutdown_ok=False,
            will_exit=False,
            exit_code=0,
            forced_kill=False,
            agent_exited_early=False,
        )
        self.assertIn("未尝试优雅退出（shutdown 命令未发出）", failures)

    def test_forced_kill_fails(self):
        failures = SOAK.controlled_exit_failures(
            shutdown_attempted=True,
            hello_ok=True,
            shutdown_ok=True,
            will_exit=True,
            exit_code=None,  # 强杀后 returncode 无意义
            forced_kill=True,
            agent_exited_early=False,
        )
        self.assertIn("未在限时内优雅退出，发生强杀", failures)

    def test_nonzero_exit_after_shutdown_fails(self):
        failures = SOAK.controlled_exit_failures(
            shutdown_attempted=True,
            hello_ok=True,
            shutdown_ok=True,
            will_exit=True,
            exit_code=1,
            forced_kill=False,
            agent_exited_early=False,
        )
        self.assertIn("Agent exit code 非 0：1", failures)

    def test_early_exit_zero_still_fails(self):
        # P2-03 核心：Agent 在 shutdown 前自行 exit 0 也不得通过。
        failures = SOAK.controlled_exit_failures(
            shutdown_attempted=False,
            hello_ok=False,
            shutdown_ok=False,
            will_exit=False,
            exit_code=0,
            forced_kill=False,
            agent_exited_early=True,
        )
        self.assertIn("Agent 在 shutdown 前提前退出（exit code=0，任何 code 均失败）", failures)

    def test_early_exit_nonzero_fails(self):
        failures = SOAK.controlled_exit_failures(
            shutdown_attempted=False,
            hello_ok=False,
            shutdown_ok=False,
            will_exit=False,
            exit_code=3,
            forced_kill=False,
            agent_exited_early=True,
        )
        self.assertIn("提前退出", failures[0])


if __name__ == "__main__":
    unittest.main()
