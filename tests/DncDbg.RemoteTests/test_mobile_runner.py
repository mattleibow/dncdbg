"""Portable regression gates for mobile orchestration, isolation and evidence (no device required)."""
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import unittest
from unittest.mock import patch
import uuid
import zipfile

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tests/DncDbg.MobileTests"))
from argparse import Namespace
from mobile_tools import android_sdk
from run_suite import PIN, Suite
from run_device import verify_ui
from assert_artifacts import verify


class MobileRunnerTests(unittest.TestCase):
    def setUp(self):
        self.output = ROOT / "build/mobile-runner-tests" / uuid.uuid4().hex
        self.output.mkdir(parents=True)
        self.suite = Suite(Namespace(platform="android", output=self.output, xcode_dir=None))
        self.suite.state = {"platform": "android", "name": "owned-unique-avd", "device": "emulator-5566"}

    def tearDown(self):
        shutil.rmtree(self.output)

    def test_android_tools_require_explicit_sdk(self):
        with patch.dict(os.environ, {}, clear=True), self.assertRaisesRegex(RuntimeError, "ANDROID_HOME"):
            android_sdk()

    def test_mobile_workload_install_consumes_the_fixture_global_pin(self):
        sdk = json.loads((ROOT / "tests/DncDbg.MauiTestApp/global.json").read_text())["sdk"]
        self.assertEqual(sdk["workloadVersion"], PIN["workload"])
        workflow = (ROOT / ".github/workflows/mobile.yml").read_text()
        self.assertNotIn("--version " + PIN["workload"], workflow)

    @unittest.skipUnless(os.name == "posix", "Portable no-op console helper")
    def test_remote_console_helper_exits_without_running_contracts(self):
        result = subprocess.run(["dotnet", str(ROOT / "tests/DncDbg.RemoteTests/bin/Release/net10.0/DncDbg.RemoteTests.dll"),
                                 "--console-utf8", "0"], capture_output=True, text=True, timeout=30)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(result.stdout, "")

    def test_android_x64_tuple_is_explicit_and_separate_from_local_arm64(self):
        args = Namespace(platform="android", output=self.output, xcode_dir=None, android_abi="x86_64")
        hosted = Suite(args)
        self.assertEqual(hosted.android["rid"], "android-x64")
        self.assertEqual(hosted.android["abi"], "x86_64")
        self.assertEqual(hosted.android["hostSystem"], "Linux")
        self.assertEqual(hosted.android["hostArchitecture"], "x86_64")
        self.assertTrue(hosted.android["systemImage"].endswith(";x86_64"))
        self.assertEqual(self.suite.android["rid"], "android-arm64")
        self.assertEqual(self.suite.android["hostSystem"], "Darwin")
        self.assertEqual(PIN["android"]["imageRevision"], "8")

    def test_android_tuple_rejects_nonmatching_host_instead_of_substituting_abi(self):
        with patch("run_suite.platform.system", return_value="Linux"), \
                patch("run_suite.platform.machine", return_value="x86_64"), \
                self.assertRaisesRegex(RuntimeError, "Darwin/arm64"):
            self.suite.preflight()

    def test_ui_rejects_another_app_platform_or_pid_before_any_tree_or_mutation(self):
        baseline = {"app": {"packageId": "com.dncdbg.mobiletests", "processId": 42}, "device": {"platform": "iOS"}}
        for field, value in (("app", {"packageId": "unrelated.app", "processId": 42}),
                             ("app", {"packageId": "com.dncdbg.mobiletests", "processId": 43}),
                             ("device", {"platform": "Android"})):
            status = baseline | {field: value}
            with self.subTest(status=status), patch("run_device.subprocess.run", return_value=subprocess.CompletedProcess(
                    [], 0, json.dumps(status), "")) as run:
                with self.assertRaises(AssertionError):
                    verify_ui(12345, "iosSimulator", 42)
                self.assertEqual(run.call_count, 1)
                self.assertEqual(run.call_args.args[0][-2:], ["agent", "status"])

    def test_ios_ui_uses_validated_broker_route_not_requested_build_port(self):
        from run_device import ios_ui_port
        registration = {"port": 10223, "processId": 42, "platform": "iOS", "sessionId": "ownedrun"}
        self.assertEqual(ios_ui_port(registration, 42, "ownedrun"), 10223)
        for field, value in (("processId", 43), ("platform", "Android"), ("sessionId", "another"),
                             ("port", 0), ("port", 65536)):
            with self.subTest(field=field), self.assertRaises(AssertionError):
                ios_ui_port(registration | {field: value}, 42, "ownedrun")

    def test_rejects_output_outside_checkout_build(self):
        with self.assertRaisesRegex(RuntimeError, "build/"):
            Suite(Namespace(platform="android", output=ROOT, xcode_dir=None))

    def test_rejects_empty_artifacts(self):
        empty = self.output / "empty"
        empty.mkdir()
        with self.assertRaisesRegex(AssertionError, "Empty artifact"):
            verify(empty)

    def test_allows_existing_stock_native_dependencies(self):
        (self.output / "libdbgshim.dylib").write_bytes(b"stock CLR dependency")
        self.assertEqual(verify(self.output), 1)

    def test_rejects_renamed_native_code(self):
        (self.output / "ordinary-binary").write_bytes(b"Mach-O\0_dncdbg_prepare\0")
        with self.assertRaisesRegex(AssertionError, "native code"):
            verify(self.output)

    def test_rejects_native_and_debug_assets_inside_packages(self):
        for name in ("lib/arm64-v8a/libdncdbg_target.so", "Microsoft.Maui.DevFlow.Agent.dll"):
            package = self.output / "ordinary.nupkg"
            with zipfile.ZipFile(package, "w") as archive:
                archive.writestr(name, b"asset")
            with self.assertRaisesRegex(AssertionError, "Debug-only asset"):
                verify(package)

    def test_native_release_build_refused_before_compilation(self):
        result = subprocess.run([sys.executable, str(ROOT / "src/DncDbg.Remote.Target/build.py"),
                                 "android-arm64", "--configuration", "Release"], capture_output=True, text=True)
        self.assertEqual(result.returncode, 2)
        self.assertIn("Debug-fixture-only", result.stderr)

    def test_cleanup_refuses_reused_emulator_serial(self):
        commands = []

        def run(*command, **kwargs):
            commands.append(command)
            return subprocess.CompletedProcess(command, 0, "another-avd\nOK\n", "")

        with patch("run_suite.adb_path", return_value="/sdk/adb"), patch.object(self.suite, "run", side_effect=run):
            with self.assertRaisesRegex(RuntimeError, "identity changed"):
                self.suite.cleanup()
        self.assertEqual(commands, [("/sdk/adb", "-s", "emulator-5566", "emu", "avd", "name"),
                                    ("/sdk/adb", "forward", "--list")])
        self.assertFalse(self.suite.state["cleanupDone"])

    def test_ios_cleanup_is_idempotent_after_partial_deletion(self):
        self.suite.args.platform = "ios"
        with patch.object(self.suite, "run", return_value=subprocess.CompletedProcess([], 0, '{"devices":{}}', "")) as run:
            self.suite.cleanup()
        run.assert_called_once_with("xcrun", "simctl", "list", "devices", "-j", timeout=30)
        self.assertTrue(self.suite.state["cleanupDone"])

    def test_ios_cleanup_refuses_another_device_identity(self):
        self.suite.args.platform = "ios"
        devices = {"devices": {"runtime": [{"udid": self.suite.state["device"], "name": "another-owner"}]}}
        with patch.object(self.suite, "run", return_value=subprocess.CompletedProcess([], 0, json.dumps(devices), "")) as run:
            with self.assertRaisesRegex(RuntimeError, "identity changed"):
                self.suite.cleanup()
        self.assertEqual(run.call_count, 1)

    def test_cleanup_removes_only_dedicated_emulator_forwards(self):
        commands = []

        def run(*command, **kwargs):
            commands.append(command)
            text = ""
            if command[-2:] == ("avd", "name"):
                text = "owned-unique-avd\nOK\n"
            elif command[-2:] == ("forward", "--list") and not any("kill" in c for c in commands):
                text = "emulator-5566 tcp:45678 tcp:12345\nemulator-5554 tcp:45679 tcp:9223\n"
            return subprocess.CompletedProcess(command, 0, text, "")

        with patch("run_suite.adb_path", return_value="/sdk/adb"), patch.object(self.suite, "run", side_effect=run):
            self.suite.cleanup()
        removed = [c for c in commands if "--remove" in c]
        self.assertEqual(removed, [("/sdk/adb", "-s", "emulator-5566", "forward", "--remove", "tcp:45678")])
        self.assertFalse(any("--remove-all" in c or "kill-server" in c for c in commands))
        self.assertTrue(self.suite.state["cleanupDone"])

    def test_recovery_log_numbering_preserves_original_evidence(self):
        (self.output / "012-dotnet.log").write_text("original")
        suite = Suite(self.suite.args)
        self.assertEqual(suite.number, 12)

    @unittest.skipUnless(os.name == "posix", "POSIX command ownership")
    def test_child_failure_is_nonzero_and_preserves_output(self):
        with self.assertRaisesRegex(RuntimeError, "exited 7"):
            self.suite.run(sys.executable, "-c", "print('exact failure evidence'); raise SystemExit(7)")
        self.assertIn("exact failure evidence", next(self.output.glob("*.log")).read_text())

    @unittest.skipUnless(os.name == "posix", "POSIX command ownership")
    def test_timeout_preserves_partial_output(self):
        with self.assertRaisesRegex(TimeoutError, "partial output"):
            self.suite.run(sys.executable, "-c", "import time; print('before timeout', flush=True); time.sleep(30)",
                           timeout=0.5)
        self.assertIn("before timeout", next(self.output.glob("*.log")).read_text())

    def test_fault_failure_still_restores_ordinary_native_helper(self):
        builds = []

        def build(configuration="Debug", faults=False):
            builds.append((configuration, faults))
            return Path("fixture"), Path("assets")

        def proof(helper, *args):
            if helper == "run_faults.py":
                raise RuntimeError("fault regression")

        with patch.object(self.suite, "preflight"), patch.object(self.suite, "boot"), patch.object(self.suite, "broker"), \
                patch.object(self.suite, "build", side_effect=build), patch.object(self.suite, "proof", side_effect=proof):
            with self.assertRaisesRegex(RuntimeError, "fault regression"):
                self.suite.execute()
        self.assertEqual(builds, [("Release", True), ("Debug", False), ("Debug", True), ("Debug", False)])

    def test_evidence_contains_head_and_hashes_but_not_credential_files(self):
        (self.output / "report.json").write_text('{"passed": true}')
        assets = self.output / "assets"
        assets.mkdir()
        (assets / "dncdbg-remote.json").write_text('{"Token": "secret"}')
        self.suite.evidence(True, None)
        manifest = json.loads((self.output / "evidence/manifest.json").read_text())
        self.assertEqual(len(manifest["head"]), 40)
        self.assertEqual(list(manifest["files"]), ["report.json"])
        self.assertEqual(manifest["pins"], PIN)

    def test_evidence_refuses_credentials_in_logs(self):
        (self.output / "report.json").write_text('{"Token": "secret"}')
        with self.assertRaisesRegex(RuntimeError, "Credential-shaped"):
            self.suite.evidence(False, "failure")

    def test_evidence_rejects_oversized_file(self):
        (self.output / "huge.log").write_bytes(b"x" * (8 * 1024 * 1024 + 1))
        with self.assertRaisesRegex(RuntimeError, "unbounded uploads"):
            self.suite.evidence(False, "failure")


if __name__ == "__main__":
    unittest.main()
