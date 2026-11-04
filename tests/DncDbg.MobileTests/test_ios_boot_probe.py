"""App-free CoreSimulator diagnostic contracts; no Apple tools run in these tests."""
import importlib.util
import json
import os
from pathlib import Path
import plistlib
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

SPEC = importlib.util.spec_from_file_location("ios_boot_probe", Path(__file__).with_name("probe_ios_boot.py"))
probe = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(probe)


class FakeProbe(probe.Probe):
    def __init__(self, output, mode="native"):
        super().__init__(output, mode, {"HOME": str(output / "home"), "TMPDIR": "/native/tmp/",
                                      "PATH": "/usr/bin:/bin", "LANG": "en_US.UTF-8"})
        self.calls = []
        self.fail_boot = False
        self.fail_cleanup = False
        self.bad_identity = False
        self.booted_state = 3
        self.now = 0

    def run(self, label, command, timeout=30, check=True, environment=None):
        self.calls.append((label, command, timeout))
        text = ""
        if command[:4] == ["xcrun", "simctl", "list", "devices"]:
            devices = [] if label == "cleanup-verify" else [{
                "udid": self.state["device"], "name": "unrelated" if self.bad_identity else self.state["name"],
                "state": "Booted", "deviceTypeIdentifier": probe.DEVICE_TYPE}]
            text = json.dumps({"devices": {probe.RUNTIME: devices}})
        elif command[:3] == ["xcrun", "simctl", "create"]:
            device = "A541A718-9232-4C79-8E2A-4F915096C5CB"
            folder = self.device_set / device
            folder.mkdir(parents=True)
            (folder / "device.plist").write_bytes(plistlib.dumps({
                "UDID": device, "name": command[3], "deviceType": probe.DEVICE_TYPE,
                "runtime": probe.RUNTIME, "state": 1}))
            text = device + "\n"
        elif command[:3] == ["xcrun", "simctl", "bootstatus"]:
            if self.fail_boot:
                self.now += 240
                raise TimeoutError("WaitingOnSystemApp exceeded 240s")
            folder = self.device_set / self.state["device"]
            value = plistlib.loads((folder / "device.plist").read_bytes()) | {"state": self.booted_state}
            (folder / "device.plist").write_bytes(plistlib.dumps(value))
            text = "Status=4294967295, isTerminal=YES\nFinished\n"
        elif command[:3] == ["xcrun", "simctl", "shutdown"]:
            self.now += 19
            if self.fail_cleanup:
                raise TimeoutError("shutdown exceeded remaining cleanup budget")
        elif command[:3] == ["xcrun", "simctl", "delete"]:
            self.now += 2
            (self.device_set / self.state["device"] / "device.plist").unlink()
            (self.device_set / self.state["device"]).rmdir()
        return subprocess.CompletedProcess(command, 0, text, "")

    def diagnostics(self):
        self.calls.append(("diagnostics", [], 180))
        self.state["diagnosticsComplete"] = True

    def preflight(self):
        self.host_verified = True
        self.calls.append(("preflight", [], 30))

    def observe(self, stop):
        stop.wait()


class BootProbeTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.addCleanup(self.temp.cleanup)

    def test_pins_are_exact_and_main_ios_tuple_is_not_changed(self):
        ios = json.loads(Path(__file__).with_name("tuple.json").read_text())["ios"]
        self.assertEqual(probe.XCODE, "/Applications/Xcode_26.6.app/Contents/Developer")
        self.assertEqual(probe.XCODE_VERSION, f"Xcode {ios['xcode']}\nBuild version {ios['xcodeBuild']}")
        self.assertEqual(probe.RUNTIME, ios["runtime"])
        self.assertEqual(probe.RUNTIME_BUILD, ios["runtimeBuild"])
        self.assertEqual(probe.DEVICE_TYPE, ios["deviceType"])
        self.assertEqual(probe.RID, "iossimulator-x64")
        self.assertEqual(probe.BOOT_SECONDS, 240)
        self.assertEqual(probe.CLEANUP_SECONDS, 30)

    def test_harness_preserves_native_home_tmp_locale_and_device_set(self):
        p = FakeProbe(self.root / "probe", "harness")
        for key in ("HOME", "TMPDIR", "LANG", "PATH"):
            self.assertEqual(p.env[key], p.native[key])
        self.assertEqual(p.env["DEVELOPER_DIR"], probe.XCODE)
        self.assertEqual(p.env["DOTNET_CLI_USE_MSBUILD_SERVER"], "false")
        self.assertEqual(p.env["MSBUILDUSESERVER"], "0")
        self.assertNotIn("SIMULATOR_DEVICE_SET_PATH", p.env)
        self.assertEqual(p.device_set, Path(p.native["HOME"]) / "Library/Developer/CoreSimulator/Devices")

    def test_exact_runtime_and_architecture_are_required(self):
        runtime = {"identifier": probe.RUNTIME, "buildversion": probe.RUNTIME_BUILD, "version": "26.5",
                   "isAvailable": True, "supportedArchitectures": ["x86_64", "arm64"],
                   "supportedDeviceTypes": [{"identifier": probe.DEVICE_TYPE}]}
        self.assertEqual(probe.exact_runtime({"runtimes": [runtime]}), runtime)
        for mutation in ({"buildversion": "other"}, {"version": "26.6"}, {"isAvailable": False},
                         {"supportedArchitectures": ["arm64"]}, {"supportedDeviceTypes": []}):
            with self.subTest(mutation=mutation), self.assertRaises(RuntimeError):
                probe.exact_runtime({"runtimes": [runtime | mutation]})
        with self.assertRaises(RuntimeError):
            probe.exact_runtime({"runtimes": [runtime, runtime]})

    def test_boot_creates_fresh_owned_device_without_a_supplied_udid(self):
        p = FakeProbe(self.root / "probe")
        p.boot()
        create = next(command for label, command, _ in p.calls if label == "create")
        self.assertEqual(create[4:], [probe.DEVICE_TYPE, probe.RUNTIME])
        self.assertTrue(create[3].startswith("dncdbg-intel-"))
        bootstatus = next((command, budget) for label, command, budget in p.calls if label == "bootstatus")
        self.assertEqual(bootstatus[0], ["xcrun", "simctl", "bootstatus", p.state["device"], "-b"])
        self.assertLessEqual(bootstatus[1], 240)
        self.assertTrue(p.state["systemAppReady"])
        with self.assertRaises(RuntimeError):
            p.boot()

    def test_boot_timeout_stays_red_and_diagnostics_precede_cleanup(self):
        p = FakeProbe(self.root / "probe")
        p.fail_boot = True
        with patch.object(probe.time, "monotonic", side_effect=lambda: p.now):
            self.assertEqual(p.execute(), 1)
        labels = [label for label, _, _ in p.calls]
        self.assertLess(labels.index("diagnostics"), labels.index("cleanup-inventory"))
        self.assertFalse(p.state["passed"])
        self.assertFalse(p.state["systemAppReady"])
        self.assertIn("240s", p.state["error"])
        self.assertTrue(p.state["cleanupDone"])

    def test_cleanup_has_one_30_second_wall_clock_budget(self):
        p = FakeProbe(self.root / "probe")
        p.boot()
        with patch.object(probe.time, "monotonic", side_effect=lambda: p.now):
            p.cleanup()
        budgets = {label: budget for label, _, budget in p.calls if label.startswith("cleanup-")}
        self.assertEqual(budgets["cleanup-inventory"], 30)
        self.assertEqual(budgets["cleanup-shutdown"], 30)
        self.assertEqual(budgets["cleanup-delete"], 11)
        self.assertEqual(budgets["cleanup-verify"], 9)
        self.assertTrue(p.state["cleanupDone"])
        self.assertEqual(p.state["cleanupSeconds"], 21)
        count = len(p.calls)
        p.cleanup()
        self.assertEqual(len(p.calls), count)

    def test_zero_bootstatus_exit_requires_independent_booted_state(self):
        p = FakeProbe(self.root / "probe")
        p.booted_state = 1
        with self.assertRaises(RuntimeError):
            p.boot()
        self.assertFalse(p.state["systemAppReady"])

    def test_diagnostics_do_not_make_late_boot_readiness_green(self):
        p = FakeProbe(self.root / "probe")
        p.fail_boot = True
        def diagnose():
            p.state["systemAppReady"] = True
        p.diagnostics = diagnose
        self.assertEqual(p.execute(), 1)
        self.assertFalse(p.state["passed"])

    def test_full_success_requires_boot_cleanup_and_preserves_manifest_hashes(self):
        p = FakeProbe(self.root / "probe")
        self.assertEqual(p.execute(), 0)
        self.assertTrue(p.state["systemAppReady"])
        self.assertTrue(p.state["cleanupDone"])
        self.assertTrue(p.state["passed"])
        manifest = json.loads((p.evidence.path / "manifest.json").read_text())
        for name, digest in manifest["files"].items():
            self.assertEqual(probe.hashlib.sha256((p.evidence.path / name).read_bytes()).hexdigest(), digest)

    def test_workflow_is_isolated_exact_two_fresh_vms_without_app_installation(self):
        root = Path(__file__).resolve().parents[2]
        workflow = (root / ".github/workflows/intel-ios-boot.yml").read_text()
        self.assertIn("branches: [mattleibow-intel-boot-diagnostics]", workflow)
        self.assertIn("runs-on: macos-26-intel", workflow)
        self.assertIn("mode: [native, harness]", workflow)
        self.assertIn("fail-fast: false", workflow)
        self.assertIn("ref: ${{ github.sha }}", workflow)
        self.assertIn("persist-credentials: false", workflow)
        self.assertIn("if: always()", workflow)
        self.assertIn("path: build/intel-ios-${{ matrix.mode }}/evidence/", workflow)
        for forbidden in ("continue-on-error", "Simulator.app",
                          "killall", "erase all", "--all-logs", "--data-container"):
            self.assertNotIn(forbidden, workflow)

    def test_tool_install_boundary_is_harness_only_and_preserves_pr20_exact_order(self):
        root = Path(__file__).resolve().parents[2]
        workflow = (root / ".github/workflows/intel-ios-boot.yml").read_text()
        setup = workflow.index("uses: actions/setup-dotnet@v4")
        inventory = workflow.index("--inventory-only")
        install = workflow.index("dotnet workload install maui-ios")
        boot = workflow.index("name: Reproduce exact cold boot")
        self.assertLess(setup, inventory)
        self.assertLess(inventory, install)
        self.assertLess(install, boot)
        self.assertEqual(workflow.count("if: matrix.mode == 'harness'"), 3)
        self.assertIn("10.0.401", workflow)
        self.assertIn("11.0.100-rc.1.26425.128", workflow)
        self.assertIn("Microsoft.Maui.Cli --version 0.1.0-preview.12.26421.1", workflow)

    def test_inventory_only_never_boots_and_does_not_claim_readiness_success(self):
        p = FakeProbe(self.root / "probe")
        self.assertEqual(p.execute(inventory_only=True), 0)
        self.assertTrue(p.state["inventoryPassed"])
        self.assertFalse(p.state["systemAppReady"])
        self.assertFalse(p.state["passed"])
        self.assertNotIn("device", p.state)
        self.assertFalse(any(label == "create" for label, _, _ in p.calls))

    def test_scoped_diagnose_has_external_and_internal_bounds_without_data_containers(self):
        p = FakeProbe(self.root / "probe")
        p.boot()
        p.host_snapshot = lambda _: None
        p.copy_text = lambda *args, **kwargs: None
        calls = []
        def command(label, argv, **kwargs):
            calls.append((label, argv, kwargs))
            return subprocess.CompletedProcess(argv, 0, "", "")
        p.run = command
        probe.Probe.diagnostics(p)
        _, argv, options = next(c for c in calls if c[0] == "diagnose")
        self.assertIn("--udid=" + p.state["device"], argv)
        self.assertIn("--timeout=60", argv)
        self.assertIn("--no-archive", argv)
        self.assertIn("-b", argv)
        self.assertLessEqual(options["timeout"], 75)
        self.assertEqual(options["payload_path"], p.private / "diagnose")
        for forbidden in ("--all-logs", "--data-container", "-X"):
            self.assertNotIn(forbidden, argv)

    def test_raw_diagnose_growth_is_stopped_and_recorded(self):
        p = probe.Probe(self.root / "probe", "harness", dict(os.environ))
        payload = p.private / "diagnose"
        payload.mkdir()
        (payload / "diagnostic.log").write_text("a" * 100)
        with patch.object(probe, "RAW_DIAGNOSE_BYTES", 32), self.assertRaises(RuntimeError):
            p.run("payload", [sys.executable, "-c", "import threading; threading.Event().wait(20)"],
                  timeout=2, payload_path=payload)
        status = json.loads((p.evidence.path / "payload.status.json").read_text())
        self.assertTrue(status["rawDiagnoseLimitExceeded"])
        self.assertFalse(status["passed"])

    def test_scoped_diagnose_runs_before_potentially_slow_text_exports(self):
        p = FakeProbe(self.root / "probe")
        p.boot()
        actions = []
        p.host_snapshot = lambda _: None
        p.copy_text = lambda prefix, *args, **kwargs: actions.append(prefix)
        def command(label, argv, **kwargs):
            actions.append(label)
            return subprocess.CompletedProcess(argv, 0, "", "")
        p.run = command
        probe.Probe.diagnostics(p)
        self.assertLess(actions.index("diagnose"), actions.index("device-logs"))
        self.assertLess(actions.index("diagnose"), actions.index("user-crashes"))

    def test_redaction_long_non_email_text_is_bounded(self):
        program = ("import importlib.util; "
                   f"s=importlib.util.spec_from_file_location('probe', {str(SPEC.origin)!r}); "
                   "p=importlib.util.module_from_spec(s); s.loader.exec_module(p); "
                   "r=p.Redactor({}); assert r('a'*100000 + ' user@example.test').endswith('<EMAIL>')")
        try:
            result = subprocess.run([sys.executable, "-c", program], capture_output=True, text=True, timeout=2)
        except subprocess.TimeoutExpired:
            self.fail("Redaction exceeded 2s for 100KB non-email text; diagnostics can starve before diagnose.")
        self.assertEqual(result.returncode, 0, result.stderr)

    def test_cleanup_failure_cannot_be_green_or_lose_ownership(self):
        p = FakeProbe(self.root / "probe")
        p.boot()
        p.fail_cleanup = True
        with self.assertRaises(TimeoutError):
            p.cleanup()
        self.assertFalse(p.state["cleanupDone"])
        self.assertIn("device", p.state)
        self.assertTrue((p.output / "state.json").is_file())

    def test_unrelated_device_is_never_deleted(self):
        p = FakeProbe(self.root / "probe")
        p.boot()
        p.bad_identity = True
        with self.assertRaises(RuntimeError):
            p.cleanup()
        self.assertFalse(any(label == "cleanup-delete" for label, _, _ in p.calls))

    def test_device_plist_identity_mismatch_is_rejected(self):
        p = FakeProbe(self.root / "probe")
        p.boot()
        path = p.device_set / p.state["device"] / "device.plist"
        value = plistlib.loads(path.read_bytes()) | {"name": "someone-else"}
        path.write_bytes(plistlib.dumps(value))
        with self.assertRaises(RuntimeError):
            p.owned_plist()

    def test_redaction_and_evidence_limits(self):
        p = FakeProbe(self.root / "probe")
        p.redactor = probe.Redactor(p.native | {"SOME_TOKEN": "owned-secret-123456"})
        text = (f"{p.native['HOME']} owned-secret-123456 "
                "Authorization: Bearer abcdefghijklmnop ghp_abcdefghijklmnopqrstuvwxyz123456 "
                "https://example.test/path?token=private&other=value someone@example.test")
        clean = p.redactor(text)
        for secret in (p.native["HOME"], "owned-secret-123456", "abcdefghijklmnop",
                       "ghp_abcdefghijklmnopqrstuvwxyz123456", "token=private", "someone@example.test"):
            self.assertNotIn(secret, clean)
        p.evidence.limit = 32
        p.evidence.total_limit = 64
        p.evidence.write("one.log", "a" * 40)
        self.assertLessEqual((p.evidence.path / "one.log").stat().st_size, 32)
        self.assertTrue(p.evidence.omissions)
        p.evidence.write("two.log", "b" * 50)
        self.assertLessEqual(sum(f.stat().st_size for f in p.evidence.path.iterdir()), 64)

    def test_command_timeout_preserves_partial_output_and_failure_status(self):
        p = probe.Probe(self.root / "probe", "harness", dict(os.environ))
        with self.assertRaises(TimeoutError):
            p.run("timeout", [sys.executable, "-u", "-c",
                              "import threading; print('WaitingOnSystemApp'); threading.Event().wait(20)"],
                  timeout=0.1)
        self.assertIn("WaitingOnSystemApp", (p.evidence.path / "timeout.log").read_text())
        status = json.loads((p.evidence.path / "timeout.status.json").read_text())
        self.assertTrue(status["timedOut"])
        self.assertFalse(status["passed"])
        self.assertLess(status["seconds"], 3)

    def test_unqualified_host_never_reads_host_logs(self):
        p = probe.Probe(self.root / "probe", "native", dict(os.environ))
        with patch.object(probe.platform, "machine", return_value="arm64"), \
                patch.object(p, "diagnostics") as diagnostics:
            self.assertEqual(p.execute(), 1)
        diagnostics.assert_not_called()
        self.assertFalse(p.state["passed"])


if __name__ == "__main__":
    unittest.main()
