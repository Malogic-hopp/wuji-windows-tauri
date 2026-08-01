import importlib.util
import subprocess
import sys
import unittest
from pathlib import Path


SCRIPT = Path(__file__).resolve().parents[1] / "build_dev_package.py"
SPEC = importlib.util.spec_from_file_location("build_dev_package", SCRIPT)
PACKAGE = importlib.util.module_from_spec(SPEC)
assert SPEC.loader is not None
SPEC.loader.exec_module(PACKAGE)


class SequenceReader:
    def __init__(self, values):
        self.values = iter(values)
        self.calls = 0

    def __call__(self):
        self.calls += 1
        return next(self.values)


class SmokeStoppedTests(unittest.TestCase):
    def test_requires_running_then_full_stable_window(self):
        reader = SequenceReader(
            [
                (("starting", "stopped"), 0),
                (("running", "stopped"), 0),
                (("running", "stopped"), 0),
                (("running", "stopped"), 0),
            ]
        )
        sleeps = []

        result = PACKAGE.wait_for_smoke_stopped(
            reader,
            ready_attempts=3,
            stable_samples=3,
            sample_interval_seconds=1.0,
            sleep=sleeps.append,
        )

        self.assertEqual(result["processState"], "running")
        self.assertEqual(result["captureState"], "stopped")
        self.assertEqual(result["observationCount"], 0)
        self.assertEqual(result["stableSamples"], 3)
        self.assertEqual(result["stableWindowSeconds"], 2.0)
        self.assertEqual(reader.calls, 4)
        self.assertEqual(sleeps, [1.0, 1.0, 1.0])

    def test_async_recording_after_bootstrap_cannot_false_pass(self):
        reader = SequenceReader(
            [
                (("running", "stopped"), 0),
                (("running", "running"), 0),
            ]
        )

        with self.assertRaisesRegex(RuntimeError, "稳定观察窗口内状态发生变化"):
            PACKAGE.wait_for_smoke_stopped(
                reader,
                stable_samples=2,
                sleep=lambda _: None,
            )

    def test_observation_growth_fails_even_when_capture_reports_stopped(self):
        reader = SequenceReader([(("running", "stopped"), 1)])

        with self.assertRaisesRegex(RuntimeError, "检测到意外采集"):
            PACKAGE.wait_for_smoke_stopped(
                reader,
                stable_samples=1,
                sleep=lambda _: None,
            )

    def test_starting_stopped_is_not_accepted_as_ready(self):
        reader = SequenceReader(
            [
                (("starting", "stopped"), 0),
                (("starting", "stopped"), 0),
            ]
        )

        with self.assertRaisesRegex(RuntimeError, "未进入 Running/Stopped/0"):
            PACKAGE.wait_for_smoke_stopped(
                reader,
                ready_attempts=2,
                stable_samples=1,
                sleep=lambda _: None,
            )


class ProcessIdentityTests(unittest.TestCase):
    def test_requires_exact_exe_and_exact_channel_argument(self):
        expected = Path(r"C:\Program Files\WUJI\Agent\wuji-rebuild-agent-v01.exe")
        process = {
            "ExecutablePath": str(expected).lower(),
            "CommandLine": f'"{expected}" --channel rebuild-v01-test-abc',
        }
        self.assertTrue(
            PACKAGE.process_matches_smoke_identity(
                process, expected, "rebuild-v01-test-abc"
            )
        )
        self.assertFalse(
            PACKAGE.process_matches_smoke_identity(
                process, expected, "rebuild-v01-test-ab"
            )
        )
        self.assertFalse(
            PACKAGE.process_matches_smoke_identity(
                {**process, "ExecutablePath": str(expected.parent / "other.exe")},
                expected,
                "rebuild-v01-test-abc",
            )
        )

    def test_verified_handle_terminates_exact_spawned_process_object(self):
        child = subprocess.Popen(
            [sys.executable, "-c", "import time; time.sleep(60)"],
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
        )
        handle = None
        try:
            handle = PACKAGE.VerifiedProcessHandle.open_verified(
                child.pid, Path(sys.executable)
            )
            handle.terminate_and_wait(timeout_ms=10_000)
            child.wait(timeout=10)
            self.assertIsNotNone(child.returncode)
        finally:
            if handle is not None:
                handle.close()
            if child.poll() is None:
                child.kill()
                child.wait(timeout=10)


if __name__ == "__main__":
    unittest.main()
