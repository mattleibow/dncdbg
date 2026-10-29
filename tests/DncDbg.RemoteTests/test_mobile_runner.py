"""Portable regression gates for mobile orchestration, isolation and evidence (no device required)."""
import json
import io
import queue
import os
import runpy
from pathlib import Path
import shutil
import signal
import subprocess
import sys
import unittest
import xml.etree.ElementTree as ET
from unittest.mock import patch
import uuid
import zipfile

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tests/DncDbg.MobileTests"))
from argparse import Namespace
from mobile_tools import android_sdk
from run_suite import PIN, Suite
from run_device import verify_ui, Ui
from run_direct import unavailable_broker_environment, FixtureApp
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

    def test_windows_detach_checks_and_records_the_exact_reviewed_head(self):
        workflow = (ROOT / ".github/workflows/windows-detach.yml").read_text()
        expression = "${{ github.event.pull_request.head.sha || github.sha }}"
        self.assertIn("ref: " + expression, workflow)
        self.assertIn("EXPECTED_HEAD: " + expression, workflow)
        self.assertIn("git rev-parse HEAD", workflow)
        self.assertIn('$actual -ne $env:EXPECTED_HEAD', workflow)
        self.assertIn("source-head.txt", workflow)
        self.assertIn("windows-detach-${{ github.event.pull_request.head.sha || github.sha }}", workflow)

    def test_mobile_restore_is_scoped_to_the_selected_platform(self):
        self.suite.state |= {"uiPort": 9223, "name": "ownedrun"}
        with patch.object(self.suite, "json", side_effect=RuntimeError("evaluation only")) as evaluate:
            with self.assertRaisesRegex(RuntimeError, "evaluation only"):
                self.suite.build("Release")
        self.assertIn("-p:DncDbgMobileTestPlatform=android", evaluate.call_args.args)

    def test_android_sealing_uses_the_provisioned_exact_build_tools_not_the_sdk_default(self):
        project = ET.parse(ROOT / "tests/DncDbg.MauiTestApp/DncDbg.MauiTestApp.csproj").getroot()
        versions = [item.text for item in project.iter("AndroidSdkBuildToolsVersion")]
        self.assertEqual(versions, ["37.0.0"])
        for key in ("android", "androidX64"):
            self.assertEqual(PIN[key]["buildTools"], versions[0])
        self.suite.state |= {"uiPort": 9223, "name": "ownedrun"}
        evaluated = {"Properties": {"MauiVersion": PIN["maui"], "DefineConstants": "",
                                   "AndroidSdkBuildToolsVersion": versions[0]},
                     "Items": {key: [] for key in ("NativeReference", "AndroidNativeLibrary", "PackageReference", "ProjectReference")}}
        with patch.object(self.suite, "json", return_value=evaluated) as evaluate, \
                patch.object(self.suite, "run") as run, patch("run_suite.Path.exists", return_value=True):
            self.suite.build("Release")
        build = next(call for call in run.call_args_list if call.args[:2] == ("dotnet", "build"))
        self.assertIn("-p:AndroidSdkBuildToolsVersion=37.0.0", build.args)
        self.assertTrue(any("AndroidSdkBuildToolsVersion" in str(arg) and str(arg).startswith("-getProperty:")
                            for arg in evaluate.call_args.args))
        workflow = (ROOT / ".github/workflows/mobile.yml").read_text()
        self.assertIn('"build-tools;37.0.0"', workflow)

    def test_mobile_sdk_uses_explicit_in_process_msbuild_not_noisy_server_fallback(self):
        self.assertEqual(self.suite.env.get("DOTNET_CLI_USE_MSBUILD_SERVER"), "false")
        self.assertEqual(self.suite.env.get("MSBUILDUSESERVER"), "0")

    def test_preflight_preserves_sdk_isolation_after_platform_environment_refresh(self):
        def sdk(*args, **kwargs):
            self.assertEqual(self.suite.env["DOTNET_CLI_USE_MSBUILD_SERVER"], "false")
            self.assertEqual(self.suite.env["MSBUILDUSESERVER"], "0")
            raise RuntimeError("verified SDK environment")
        with patch("run_suite.platform.system", return_value=self.suite.android["hostSystem"]), \
                patch("run_suite.platform.machine", return_value=self.suite.android["hostArchitecture"]), \
                patch("run_suite.tool_environment", return_value=dict(os.environ) |
                      {"DOTNET_CLI_USE_MSBUILD_SERVER": "true", "MSBUILDUSESERVER": "1"}), \
                patch.object(self.suite, "run", side_effect=sdk):
            with self.assertRaisesRegex(RuntimeError, "verified SDK environment"):
                self.suite.preflight()

    def test_simulator_signing_does_not_require_a_developer_identity_or_device_profile(self):
        project = ET.parse(ROOT / "tests/DncDbg.MauiTestApp/DncDbg.MauiTestApp.csproj").getroot()
        groups = [group for group in project.findall("PropertyGroup")
                  if group.attrib.get("Condition") == "'$(RuntimeIdentifier)' == 'iossimulator-arm64'"]
        self.assertEqual(len(groups), 1)
        self.assertEqual(groups[0].findtext("CodesignKey"), "-")
        self.assertEqual(groups[0].findtext("CodesignRequireProvisioningProfile"), "false")
        self.assertNotEqual(groups[0].findtext("EnableCodeSigning"), "false")
        self.assertIsNone(groups[0].find("DisableCodesignVerification"))

    def test_release_build_has_its_own_budget_and_useful_diagnostics(self):
        self.suite.state |= {"uiPort": 9223, "name": "ownedrun"}
        evaluated = {"Properties": {"MauiVersion": PIN["maui"], "DefineConstants": "",
                                   "AndroidSdkBuildToolsVersion": PIN["android"]["buildTools"]},
                     "Items": {key: [] for key in ("NativeReference", "AndroidNativeLibrary", "PackageReference", "ProjectReference")}}
        with patch.object(self.suite, "json", return_value=evaluated), patch.object(self.suite, "run") as run, \
                patch("run_suite.Path.exists", return_value=True):
            self.suite.build("Release", faults=True)
        build = next(call for call in run.call_args_list if call.args[:2] == ("dotnet", "build"))
        self.assertEqual(build.kwargs["timeout"], 1200)
        self.assertIn("-v:minimal", build.args)
        self.assertTrue(any(str(arg).startswith("-bl:") and "ProjectImports=None" in str(arg) for arg in build.args))
        self.assertTrue(build.kwargs["progress"])

    def test_build_environment_does_not_publish_secret_environment_values(self):
        from run_suite import build_environment
        environment = {"PATH": "/tools", "ANDROID_HOME": "/sdk", "GITHUB_TOKEN": "secret",
                       "ACTIONS_RUNTIME_TOKEN": "secret", "NuGet_Credential": "secret", "MY_PASSWORD": "secret"}
        self.assertEqual(build_environment(environment), {"PATH": "/tools", "ANDROID_HOME": "/sdk"})

    def test_build_timeout_retains_live_progress_and_status(self):
        with self.assertRaisesRegex(TimeoutError, "partial output"), patch("run_suite.print") as progress:
            self.suite.run(sys.executable, "-c", "import time;print('reached linker',flush=True);time.sleep(30)",
                           timeout=0.3, progress=True, progress_interval=0.1)
        self.assertIn("reached linker", next(self.output.glob("*.log")).read_text())
        status = json.loads(next(self.output.glob("*.status.json")).read_text())
        self.assertTrue(status["timedOut"])
        self.assertEqual(status["timeoutSeconds"], 0.3)
        self.assertTrue(any("still running" in str(call) for call in progress.call_args_list))

    def test_mobile_catalog_copies_only_matching_fixture_symbols(self):
        from run_device import copy_debug_assets
        source = self.output / "input"
        source.mkdir()
        names = {f"{name}.{extension}" for name in ("DncDbg.MauiTestApp", "DncDbg.ReloadFixture")
                 for extension in ("dll", "pdb")}
        names |= {"dncdbg-agent.json", "dncdbg-package.json"}
        for name in names | {"DncDbg.Remote.dll", "DncDbg.Remote.pdb"}:
            (source / name).write_bytes(name.encode())
        destination = copy_debug_assets(source, self.output / "catalog")
        self.assertEqual({p.name for p in destination.iterdir()}, names)
        for name in names:
            self.assertEqual((destination / name).read_bytes(), (source / name).read_bytes())

    def test_direct_debugger_control_does_not_invoke_devflow_when_broker_is_unavailable(self):
        source = (ROOT / "tests/DncDbg.RemoteTests/MobileBreakpointProof.cs").read_text()
        self.assertNotIn("UiSession", source)
        self.assertIn("FixtureControlClient", source)

    def test_devflow_ui_proof_fails_honestly_when_broker_authority_is_unavailable(self):
        ui = Ui.__new__(Ui)
        ui.responses, ui.log = queue.Queue(), io.StringIO()
        ui.responses.put(json.dumps({"command": "MAUI tap --automationId reset-scenario", "exit_code": 1,
                                     "output": "The DevFlow broker lease authority is temporarily unavailable."}))
        with self.assertRaisesRegex(AssertionError, "lease authority is temporarily unavailable"):
            ui.receive()

    def test_unavailable_broker_regression_is_a_real_failing_cli_not_a_success_fallback(self):
        environment, trace = unavailable_broker_environment(self.output)
        result = subprocess.run(["maui", "devflow", "ui", "tree"], env=environment,
                                capture_output=True, text=True, timeout=10)
        self.assertEqual(result.returncode, 1)
        self.assertIn("lease authority is temporarily unavailable", result.stdout)
        self.assertTrue(trace.exists())

    def test_direct_native_cleanup_waits_for_the_same_exact_owner_fence_as_dap(self):
        app = FixtureApp(Namespace(platform="ios", output=self.output))
        finished = "\n".join(json.dumps({"Phase": phase, "HResult": "0x00000000"}, separators=(",", ":"))
                             for phase in ("cleanup-detach-return", "terminate-return", "owner-finished"))
        with patch.object(app, "read", side_effect=['{"Phase":"cleanup-stop-return"}', finished]) as read, \
                patch("run_direct.time.sleep"):
            app.native_cleanup()
        self.assertEqual(read.call_count, 2)
        with patch.object(app, "read", return_value=finished.replace("0x00000000", "0x80004005")):
            with self.assertRaises(AssertionError):
                app.native_cleanup()
    def test_emulator_ports_stay_inside_the_official_console_and_adb_range(self):
        from run_suite import ANDROID_CONSOLE_PORTS
        self.assertEqual(list(ANDROID_CONSOLE_PORTS), list(range(5554, 5586, 2)))

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

    def test_target_deployment_contains_linked_redistribution_notices(self):
        publisher = runpy.run_path(str(ROOT / "src/DncDbg.Remote.Target/publish.py"))
        publisher["copy_notices"](self.output)
        notices = (self.output / "THIRD-PARTY-NOTICES.md").read_text()
        self.assertNotIn("(src/licenses/", notices)
        for name in ("nlohmann-json.LICENSE", "Microsoft.MIT.LICENSE", "JaneySprings-clrdbg.LICENSE"):
            self.assertIn("(licenses/" + name + ")", notices)
            self.assertEqual((self.output / "licenses" / name).read_bytes(), (ROOT / "src/licenses" / name).read_bytes())

    def test_publisher_packages_complete_declared_publish_directory(self):
        publisher = runpy.run_path(str(ROOT / "src/DncDbg.Remote.Target/publish.py"))
        source = self.output / "published"
        files = {"Service.dll": b"managed", "Service.runtimeconfig.json": b"{}",
                 "runtimes/linux-x64/native/libvendor.so": b"native bytes",
                 "fr/Service.resources.dll": b"satellite", "content/device/config.bin": b"content",
                 "tools/vendor-helper": b"executable"}
        for name, value in files.items():
            path = source / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(value)
        destination = self.output / "payload"
        publisher["copy_payload"](source, destination)
        actual = {p.relative_to(destination).as_posix(): p.read_bytes() for p in destination.rglob("*") if p.is_file()}
        self.assertEqual(actual, files)
        publisher["write_inventory"](destination, "linux-x64")
        first = (destination / "integrity.json").read_bytes()
        publisher["write_inventory"](destination, "linux-x64")
        self.assertEqual((destination / "integrity.json").read_bytes(), first)
        inventory = json.loads(first)["Files"]
        self.assertEqual(list(inventory), sorted(files))

    def test_publisher_excludes_build_test_temp_and_secret_artifacts(self):
        publisher = runpy.run_path(str(ROOT / "src/DncDbg.Remote.Target/publish.py"))
        source = self.output / "published"
        for name in ("Service.dll", "bin/old.dll", "obj/cache.json", "tests/test.json",
                     "TestResults/run.trx", "temp/pending.json", "secrets.json", ".env",
                     "private.key", "signing.pfx", "app.binlog", "pending.tmp"):
            path = source / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(b"fixture")
        destination = self.output / "payload"
        publisher["copy_payload"](source, destination)
        self.assertEqual([p.relative_to(destination).as_posix() for p in destination.rglob("*") if p.is_file()],
                         ["Service.dll"])

    def test_publisher_rejects_symbolic_links_and_portable_path_substitution(self):
        publisher = runpy.run_path(str(ROOT / "src/DncDbg.Remote.Target/publish.py"))
        source = self.output / "published"
        source.mkdir()
        (self.output / "outside.dll").write_bytes(b"outside")
        (source / "linked.dll").symlink_to(self.output / "outside.dll")
        with self.assertRaisesRegex(RuntimeError, "symbolic"):
            publisher["copy_payload"](source, self.output / "payload-links")
        (source / "linked.dll").unlink()
        (source / "bad\\name.dll").write_bytes(b"ambiguous on Windows")
        with self.assertRaisesRegex(RuntimeError, "path"):
            publisher["copy_payload"](source, self.output / "payload-paths")

    def test_publisher_never_guesses_a_payload_closure_from_a_dll(self):
        publisher = runpy.run_path(str(ROOT / "src/DncDbg.Remote.Target/publish.py"))
        source = self.output / "Service.dll"
        source.write_bytes(b"not a standalone bundle")
        with self.assertRaisesRegex(RuntimeError, "published-output directory"):
            publisher["copy_payload"](source, self.output / "payload")

    def test_rejects_renamed_native_code(self):
        (self.output / "ordinary-binary").write_bytes(b"Mach-O\0_dncdbg_prepare\0")
        with self.assertRaisesRegex(AssertionError, "Target-native import/test hook"):
            verify(self.output)

    def test_rejects_native_and_debug_assets_inside_packages(self):
        for name in ("lib/arm64-v8a/libdncdbg_target.so", "Microsoft.Maui.DevFlow.Agent.dll"):
            package = self.output / "ordinary.nupkg"
            with zipfile.ZipFile(package, "w") as archive:
                archive.writestr(name, b"asset")
            with self.assertRaisesRegex(AssertionError, "Target-native/test-only asset"):
                verify(package)

    def test_native_release_build_refused_before_compilation(self):
        result = subprocess.run([sys.executable, str(ROOT / "src/DncDbg.Remote.Target/build.py"),
                                 "android-arm64", "--configuration", "Release", "--test-faults"], capture_output=True, text=True)
        self.assertEqual(result.returncode, 2)
        self.assertIn("test-only", result.stderr)

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

    def test_empty_console_reply_uses_exact_owned_pid_without_mutating_the_serial(self):
        self.suite.state["emulatorPid"] = 123456
        commands = []

        def run(*command, **kwargs):
            commands.append(command)
            text = ("emulator -avd owned-unique-avd -port 5566\n" if command[0] == "ps" else
                    "List of devices attached\n" if command[-1] == "devices" else "")
            return subprocess.CompletedProcess(command, 0, text, "")

        with patch("run_suite.adb_path", return_value="/sdk/adb"), patch.object(self.suite, "run", side_effect=run), \
                patch("run_suite.os.kill", side_effect=[None, ProcessLookupError()]) as kill:
            self.suite.cleanup()
        self.assertEqual(kill.call_args_list, [unittest.mock.call(123456, signal.SIGTERM),
                                               unittest.mock.call(123456, 0)])
        self.assertFalse(any("uninstall" in c or "force-stop" in c or "--remove" in c for c in commands))
        self.assertTrue(self.suite.state["cleanupDone"])

    def test_empty_console_reply_cannot_authorize_an_unrelated_pid(self):
        self.suite.state["emulatorPid"] = 123456

        def run(*command, **kwargs):
            return subprocess.CompletedProcess(command, 0,
                "emulator -avd another-owner -port 5566\n" if command[0] == "ps" else "", "")

        with patch("run_suite.adb_path", return_value="/sdk/adb"), patch.object(self.suite, "run", side_effect=run), \
                patch("run_suite.os.kill") as kill:
            with self.assertRaisesRegex(RuntimeError, "Cannot verify"):
                self.suite.cleanup()
        kill.assert_not_called()
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
                patch.object(self.suite, "independent_proofs"), patch.object(self.suite, "fixture_proof"), \
                patch.object(self.suite, "build", side_effect=build), patch.object(self.suite, "proof", side_effect=proof):
            with self.assertRaisesRegex(RuntimeError, "fault regression"):
                self.suite.execute()
        self.assertEqual(builds, [("Release", True), ("Debug", False), ("Debug", True), ("Debug", False)])

    def test_independent_debugger_proofs_run_before_broker_required_ui(self):
        order = []
        with patch.object(self.suite, "preflight"), patch.object(self.suite, "boot"), \
                patch.object(self.suite, "build", return_value=(Path("fixture"), Path("assets"))), \
                patch.object(self.suite, "independent_proofs", side_effect=lambda *a: order.append("debugger")), \
                patch.object(self.suite, "broker", side_effect=RuntimeError("broker unavailable")), \
                patch.object(self.suite, "fixture_proof") as ui:
            with self.assertRaisesRegex(RuntimeError, "broker unavailable"):
                self.suite.execute()
        self.assertEqual(order, ["debugger"])
        ui.assert_not_called()

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
