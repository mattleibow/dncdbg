"""Portable regression gates for mobile orchestration, isolation and evidence (no device required)."""
import json
import io
import errno
import queue
import os
import runpy
from pathlib import Path
import shutil
import signal
import socket
import selectors
import warnings
import subprocess
import sys
import time
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
import run_suite
from run_device import verify_ui, Ui
from run_direct import unavailable_broker_environment, FixtureApp
from assert_artifacts import verify
from fake_adb import FakeAdbDaemon
from test_mobile_processes import alive
import process_tools


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

    def test_target_artifact_upload_preserves_hidden_inventory_entries(self):
        workflow = (ROOT / ".github/workflows/remote.yml").read_text()
        upload = workflow.split("name: Upload explicit target deployment package", 1)[1].split("\n      - ", 1)[0]
        self.assertIn("include-hidden-files: true", upload,
                      "Runtime .version and hidden payload files must survive upload with their integrity inventory.")

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

    def test_forward_commit_deadline_starts_after_package_admission_not_before_it(self):
        daemon = FakeAdbDaemon.__new__(FakeAdbDaemon)
        daemon.state = self.output
        clock, ticks = [0.0], [0]
        def advance(_):
            ticks[0] += 1
            if ticks[0] == 1:
                clock[0] = 10.0
                (self.output / "forward-cli-started").touch()
            else:
                clock[0] = 11.0
                (self.output / "daemon-committed.json").write_text('{"pid":123,"child":124}')
        with patch("fake_adb.time.monotonic", side_effect=lambda: clock[0]), patch("fake_adb.time.sleep", side_effect=advance):
            self.assertEqual(daemon.committed(), {"pid": 123, "child": 124})
        self.assertEqual(ticks[0], 2)

    def test_mobile_sdk_uses_explicit_in_process_msbuild_not_noisy_server_fallback(self):
        self.assertEqual(self.suite.env.get("DOTNET_CLI_USE_MSBUILD_SERVER"), "false")
        self.assertEqual(self.suite.env.get("MSBUILDUSESERVER"), "0")

    def test_suite_environment_removes_python_injection_overrides(self):
        from run_suite import sdk_environment
        injected = {"PATH": "/tools", "TMPDIR": "/native-temp/", "DNCDBG_PYTHON": "/selected/python",
                    "PYTHONPATH": "/foreign", "PYTHONHOME": "/foreign", "PYTHONUSERBASE": "/foreign",
                    "PYTHONSTARTUP": "/foreign/start.py", "PYTHONINSPECT": "1", "PYTHONOPTIMIZE": "2",
                    "__PYVENV_LAUNCHER__": "/foreign/python"}
        clean = sdk_environment(injected)
        for key in injected.keys() - {"PATH", "TMPDIR"}:
            self.assertNotIn(key, clean)
        self.assertEqual(clean["TMPDIR"], injected["TMPDIR"])
        self.assertEqual(clean["PYTHONNOUSERSITE"], "1")
        self.assertEqual(clean["PYTHONUTF8"], "1")

    @unittest.skipUnless(os.name == "posix", "Test-only executable selector")
    def test_common_facade_scrubs_overrides_before_starting_the_selected_python(self):
        foreign = self.output / "foreign"
        foreign.mkdir()
        marker = foreign / "imported"
        (foreign / "jsonschema.py").write_text(f"from pathlib import Path\nPath({str(marker)!r}).touch()\n")
        interpreter = self.output / "selected-python"
        interpreter.write_text("#!" + sys.executable + "\nimport os,json,sys,jsonschema,rpds\n"
                               "keys=['PYTHONPATH','PYTHONHOME','PYTHONUSERBASE','PYTHONSTARTUP','PYTHONINSPECT',"
                               "'PYTHONOPTIMIZE','__PYVENV_LAUNCHER__','DNCDBG_PYTHON']\n"
                               "print(json.dumps({'env':{k:os.environ.get(k) for k in keys},"
                               "'schema':jsonschema.__file__,'rpds':rpds.__file__,'prefix':sys.prefix}))\n")
        interpreter.chmod(0o700)
        environment = dict(os.environ) | {"DNCDBG_PYTHON": str(interpreter), "PYTHONPATH": str(foreign),
                                          "PYTHONHOME": str(foreign), "PYTHONUSERBASE": str(foreign),
                                          "PYTHONSTARTUP": str(foreign / "start.py"), "PYTHONINSPECT": "1",
                                          "PYTHONOPTIMIZE": "2", "__PYVENV_LAUNCHER__": str(foreign / "python")}
        result = subprocess.run(["dotnet", str(ROOT / "tests/DncDbg.MobileTests/bin/Release/net10.0/DncDbg.MobileTests.dll"),
                                 "--platform", "contracts"], env=environment, capture_output=True, text=True, timeout=15)
        self.assertEqual(result.returncode, 0, result.stderr)
        report = json.loads(result.stdout)
        self.assertTrue(all(value is None for value in report["env"].values()), report)
        self.assertFalse(marker.exists(), "Foreign code must not execute before provenance checks.")
        for name in ("schema", "rpds"):
            self.assertTrue(Path(report[name]).resolve().is_relative_to(Path(report["prefix"]).resolve()), report)

    def test_package_provenance_rejects_duplicate_distribution_names(self):
        import python_environment
        actual = list(python_environment.metadata.distributions())
        from types import SimpleNamespace
        duplicate = SimpleNamespace(metadata={"Name": "jsonschema"}, version="999",
                                    locate_file=lambda _: self.output / "foreign")
        with patch("python_environment.metadata.distributions", return_value=actual + [duplicate]), \
                self.assertRaisesRegex(RuntimeError, "Duplicate.*jsonschema"):
            python_environment.verify_environment(ROOT / "tests/DncDbg.RemoteTests/requirements.txt")

    def test_package_provenance_rejects_foreign_pythonpath_before_import(self):
        helper = ROOT / "tests/DncDbg.RemoteTests/python_environment.py"
        self.assertTrue(helper.is_file())
        foreign = self.output / "foreign-package"
        foreign.mkdir()
        marker = foreign / "imported"
        (foreign / "jsonschema.py").write_text(f"from pathlib import Path\nPath({str(marker)!r}).touch()\n")
        environment = dict(os.environ) | {"PYTHONPATH": str(foreign)}
        result = subprocess.run([sys.executable, str(helper)], env=environment,
                                capture_output=True, text=True, timeout=15)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("jsonschema", result.stderr)
        self.assertIn("outside", result.stderr)
        self.assertFalse(marker.exists())

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

    def test_ios_preserves_runner_native_temporary_environment_for_system_services(self):
        for architecture, host in (("arm64", "arm64"), ("x64", "x86_64")):
            output = self.output / architecture
            suite = Suite(Namespace(platform="ios", output=output, xcode_dir=None, ios_architecture=architecture))
            native = dict(os.environ) | {"TMPDIR": "/runner/native-user-temp/"}
            def verify(*args, **kwargs):
                self.assertEqual(suite.env["TMPDIR"], native["TMPDIR"])
                self.assertEqual(suite.env["DOTNET_CLI_USE_MSBUILD_SERVER"], "false")
                raise RuntimeError("native environment verified")
            with self.subTest(architecture=architecture), \
                    patch("run_suite.platform.system", return_value="Darwin"), \
                    patch("run_suite.platform.machine", return_value=host), \
                    patch("run_suite.tool_environment", return_value=native), \
                    patch.object(suite, "run", side_effect=verify):
                with self.assertRaisesRegex(RuntimeError, "native environment verified"):
                    suite.preflight()

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

    def test_ios_x64_tuple_is_explicit_and_does_not_replace_arm64(self):
        hosted = Suite(Namespace(platform="ios", output=self.output, xcode_dir=None, ios_architecture="x64"))
        local = Suite(Namespace(platform="ios", output=self.output, xcode_dir=None))
        self.assertEqual(hosted.ios["rid"], "iossimulator-x64")
        self.assertEqual(hosted.ios["hostArchitecture"], "x86_64")
        self.assertEqual(local.ios["rid"], "iossimulator-arm64")
        self.assertEqual(local.ios["hostArchitecture"], "arm64")
        for key in ("xcode", "xcodeBuild", "runtime", "runtimeBuild", "deviceType"):
            self.assertEqual(hosted.ios[key], local.ios[key])
        with patch("run_suite.platform.system", return_value="Darwin"), \
                patch("run_suite.platform.machine", return_value="arm64"), \
                self.assertRaisesRegex(RuntimeError, "Darwin/x86_64"):
            hosted.preflight()

    def test_parallel_dap_contracts_run_every_test_once_and_propagate_failures(self):
        import test_dap
        expected = {"MobileDapTests." + name for name in unittest.defaultTestLoader.getTestCaseNames(test_dap.MobileDapTests)}
        with patch.object(sys, "argv", ["test_dap.py", "--parallel"]), \
                patch("subprocess.Popen") as start:
            start.return_value.wait.side_effect = [0, 1]
            with self.assertRaises(SystemExit) as result:
                runpy.run_path(str(ROOT / "tests/DncDbg.RemoteTests/test_dap.py"), run_name="__main__")
            self.assertEqual(result.exception.code, 1)
            commands = [call.args[0] for call in start.call_args_list]
            self.assertEqual(len(commands), 2)
            self.assertTrue(all(command[2] == "-v" for command in commands),
                            "Names and failures must be emitted before the aggregate deadline, not only in a final summary.")
            tests = [name for command in commands for name in command[3:]]
            self.assertEqual(set(tests), expected)
            self.assertEqual(len(tests), len(expected))
            self.assertTrue(all(command[:2] == [sys.executable, str(ROOT / "tests/DncDbg.RemoteTests/test_dap.py")]
                                for command in commands))
        source = (ROOT / "tests/DncDbg.MobileTests/run_suite.py").read_text()
        self.assertIn('timeout=180', source)
        self.assertIn('["--parallel"]', source)

    def test_ios_pinned_inventory_is_checked_in_the_runner_environment_before_workloads(self):
        source = (ROOT / ".github/workflows/mobile.yml").read_text()
        inventory = source.index("name: Verify pinned simulator inventory in the runner environment")
        self.assertLess(inventory, source.index("name: Install exact workload and DevFlow CLI"))
        probe = source[inventory:source.index("name: Install exact workload and DevFlow CLI")]
        self.assertIn('run_owned("xcrun", "simctl", "list", "runtimes", "-j"', probe)
        self.assertIn('timeout=180', probe)
        self.assertNotIn('["TMPDIR"] =', probe)
        self.assertIn('["runtimeBuild"]', probe)

    def test_desktop_contract_facade_uses_explicit_python_instead_of_xcode_python(self):
        source = (ROOT / ".github/workflows/ci.yml").read_text().split("\n  docker:", 1)[0]
        self.assertIn("uses: actions/setup-python@v5", source)
        self.assertIn('python-version: "3.13.15"', source)
        contracts = source.split("name: Check native target ownership contracts (no mobile runtime)", 1)[1]
        contracts = contracts.split("\n      - ", 1)[0]
        self.assertIn("DNCDBG_PYTHON: ${{ github.workspace }}/build/contract-python/bin/python", contracts)
        facade = (ROOT / "tests/CommonRunner.cs").read_text()
        self.assertIn('Environment.GetEnvironmentVariable("DNCDBG_PYTHON")', facade)

    def test_portable_dependencies_are_hash_locked_in_an_isolated_environment(self):
        source = (ROOT / ".github/workflows/ci.yml").read_text().split("\n  docker:", 1)[0]
        install = source.split("name: Install portable contract dependencies", 1)[1].split("\n      - ", 1)[0]
        self.assertIn("-m venv build/contract-python", install)
        self.assertIn("--require-hashes --no-deps --only-binary=:all:", install)
        self.assertIn("build/contract-python/bin/python -I -m pip", install)
        lock = (ROOT / "tests/DncDbg.RemoteTests/requirements.txt").read_text()
        expected = {"attrs", "jsonschema", "jsonschema-specifications", "referencing", "rpds-py"}
        import re
        entries = re.split(r"(?m)^(?=[a-z][a-z0-9-]*==)", lock)
        pinned = {}
        for entry in entries:
            if not re.match(r"[a-z][a-z0-9-]*==", entry):
                continue
            name, version = entry.splitlines()[0].split()[0].split("==")
            self.assertNotIn(name, pinned)
            pinned[name] = version
            hashes = re.findall(r"--hash=sha256:([a-f0-9]+)", entry)
            self.assertTrue(hashes, name)
            self.assertTrue(all(len(digest) == 64 for digest in hashes), name)
        self.assertEqual(set(pinned), expected)
        from python_environment import verify_environment
        report = verify_environment(ROOT / "tests/DncDbg.RemoteTests/requirements.txt")
        installed = {d["name"]: d["version"] for d in report["distributions"]
                     if d["name"] not in {"pip", "setuptools", "wheel"}}
        self.assertEqual(installed, pinned, "Portable test packages must be the exact locked closure, never global extras.")

    def test_fake_tool_processes_do_not_reload_the_entire_unittest_driver(self):
        helper = ROOT / "tests/DncDbg.RemoteTests/fake_mobile_tools.py"
        self.assertTrue(helper.is_file(), "Tool startup must not import every DAP test and server dependency.")
        source = (ROOT / "tests/DncDbg.RemoteTests/test_dap.py").read_text()
        self.assertIn('with_name("fake_mobile_tools.py")', source)
        namespace = runpy.run_path(str(helper))
        self.assertTrue(callable(namespace["tool"]))
        self.assertNotIn("unittest", namespace)
        self.assertNotIn("FakeAdbDaemon", namespace)

    def test_public_launch_schema_accepts_both_simulator_rids_and_rejects_unknown(self):
        from jsonschema import Draft202012Validator
        schema = json.loads((ROOT / "docs/remote-launch.schema.json").read_text())
        Draft202012Validator.check_schema(schema)
        validate = Draft202012Validator(schema)
        for rid in ("iossimulator-arm64", "iossimulator-x64"):
            options = {"assetsPath": "/fixture/symbols", "outputPath": "/fixture/run",
                       "target": {"provider": "appleSimulator", "apple": {
                           "applicationId": "com.dncdbg.mobiletests", "deviceId": "owned-simulator",
                           "appPath": "/fixture/Fixture.app", "runtimeIdentifier": rid}}}
            with self.subTest(rid=rid):
                validate.validate(options)
            for unknown in ("iossimulator-x86", "iossimulator-unknown", "ios-arm64", "android-x64", "", None, 42):
                options["target"]["apple"]["runtimeIdentifier"] = unknown
                with self.subTest(unknown=unknown):
                    self.assertFalse(validate.is_valid(options))
            del options["target"]["apple"]["runtimeIdentifier"]
            validate.validate(options)
        self.assertEqual(schema["$defs"]["apple"]["properties"]["runtimeIdentifier"]["default"], "iossimulator-arm64")

    def test_android_reclaims_only_unused_hosted_payloads_before_pinned_install(self):
        source = (ROOT / ".github/workflows/mobile.yml").read_text()
        start = source.index("name: Reclaim unused hosted SDK payloads")
        end = source.index("name: Provision immutable checksummed Android emulator and image")
        self.assertLess(start, end)
        cleanup = source[start:end]
        self.assertIn('test "$RUNNER_ENVIRONMENT" = github-hosted', cleanup)
        self.assertIn('test "${ANDROID_HOME%/}" = /usr/local/lib/android/sdk', cleanup)
        self.assertIn("sudo rm -rf -- /usr/local/lib/android/sdk/system-images /usr/local/lib/android/sdk/ndk /usr/local/lib/android/sdk/emulator", cleanup)
        self.assertNotIn("sdk/cmdline-tools", cleanup)
        self.assertNotIn("sdk/platform-tools", cleanup)
        self.assertNotIn('rm -rf "$ANDROID_HOME"', cleanup)

    def test_ios_x64_build_and_every_proof_use_selected_rid(self):
        suite = Suite(Namespace(platform="ios", output=self.output, xcode_dir=None, ios_architecture="x64"))
        suite.state = {"uiPort": 12345, "name": "ownedrun", "device": "owned-simulator"}
        evaluated = {"Properties": {"MauiVersion": PIN["maui"], "DefineConstants": ""},
                     "Items": {key: [] for key in ("NativeReference", "AndroidNativeLibrary", "PackageReference", "ProjectReference")}}
        with patch.object(suite, "json", return_value=evaluated), patch.object(suite, "run") as run, \
                patch("run_suite.Path.exists", return_value=True):
            app, assets = suite.build("Release")
            self.assertIn("iossimulator-x64", str(app))
            build = next(call for call in run.call_args_list if call.args[:2] == ("dotnet", "build"))
            self.assertIn("-p:RuntimeIdentifier=iossimulator-x64", build.args)
            for helper in ("run_device.py", "run_faults.py"):
                suite.proof(helper, app, assets, helper)
                self.assertIn("iossimulator-x64", run.call_args.args)
            suite.fixture_proof(app, assets, "dap", "direct")
            self.assertIn("iossimulator-x64", run.call_args.args)

    def test_intel_ci_routes_matching_rids_without_renaming_protected_contexts(self):
        ci = (ROOT / ".github/workflows/ci.yml").read_text()
        remote = (ROOT / ".github/workflows/remote.yml").read_text()
        mobile = (ROOT / ".github/workflows/mobile.yml").read_text()
        self.assertIn("name: Debugger (${{ matrix.os }})", ci)
        self.assertIn("runner: macos-26-intel", ci)
        self.assertIn("rid: osx-x64", ci)
        self.assertIn("name: Remote native host (${{ matrix.os }})", remote)
        self.assertIn("runner: macos-26-intel", remote)
        self.assertIn("rid: osx-x64", remote)
        self.assertIn("name: Mobile iOS simulator (pinned)", mobile)
        self.assertIn("runs-on: macos-26-intel", mobile)
        self.assertEqual(mobile.count("--ios-architecture x64"), 2)

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
        with patch("run_suite.time.monotonic", return_value=100), \
                patch.object(self.suite, "run", return_value=subprocess.CompletedProcess([], 0, '{"devices":{}}', "")) as run:
            self.suite.cleanup()
        run.assert_called_once_with("xcrun", "simctl", "list", "devices", "-j", timeout=60, deadline=160)
        self.assertTrue(self.suite.state["cleanupDone"])

    def test_ios_boot_budget_requires_exact_owned_booted_device(self):
        self.suite.args.platform = "ios"
        device = "12345678-1234-1234-1234-123456789abc"
        commands = []

        def run(*command, **kwargs):
            commands.append((command, kwargs))
            text = device if command[2] == "create" else json.dumps({"devices": {
                self.suite.ios["runtime"]: [{"udid": device, "name": self.suite.state["name"], "state": "Booted"}]}})
            return subprocess.CompletedProcess(command, 0, text, "")

        with patch("run_suite.time.monotonic", return_value=100), patch.object(self.suite, "run", side_effect=run):
            self.suite.boot()
        self.assertIn((("xcrun", "simctl", "bootstatus", device, "-b"), {"timeout": 600, "deadline": 700}), commands)
        self.assertEqual(commands[-1][0], ("xcrun", "simctl", "list", "devices", "-j"))
        report = json.loads((self.output / "ios-provisioning.json").read_text())
        self.assertEqual(report["timeoutSeconds"], 600)
        self.assertEqual(report["device"], device)
        self.assertTrue(report["ready"])
        self.assertFalse(report["complete"])

    def test_ios_bootstatus_success_without_owned_readiness_is_not_qualification(self):
        self.suite.args.platform = "ios"
        device = "12345678-1234-1234-1234-123456789abc"
        for field, value in (("state", "Shutdown"), ("name", "another-owner"), ("udid", "another-device")):
            def run(*command, **kwargs):
                entry = {"udid": device, "name": self.suite.state["name"], "state": "Booted"} | {field: value}
                text = device if command[2] == "create" else json.dumps({"devices": {self.suite.ios["runtime"]: [entry]}})
                return subprocess.CompletedProcess(command, 0, text, "")
            with self.subTest(field=field), patch.object(self.suite, "run", side_effect=run):
                with self.assertRaisesRegex(RuntimeError, "owned simulator.*Booted"):
                    self.suite.boot()
            self.assertFalse(json.loads((self.output / "ios-provisioning.json").read_text())["ready"])

    def test_ios_boot_rejects_success_after_aggregate_provisioning_deadline(self):
        self.suite.args.platform = "ios"
        clock = [100]

        def run(*command, **kwargs):
            if command[2] == "bootstatus":
                clock[0] += 601
            return subprocess.CompletedProcess(command, 0, "owned-device", "")

        with patch("run_suite.time.monotonic", side_effect=lambda: clock[0]), \
                patch.object(self.suite, "run", side_effect=run):
            with self.assertRaisesRegex(TimeoutError, "provisioning.*600"):
                self.suite.boot()
        self.assertFalse(json.loads((self.output / "ios-provisioning.json").read_text())["ready"])

    def test_ios_creation_does_not_reset_the_aggregate_provisioning_clock(self):
        self.suite.args.platform = "ios"
        clock = [100]
        commands = []

        def run(*command, **kwargs):
            commands.append(command)
            if command[2] == "create":
                clock[0] += 601
                return subprocess.CompletedProcess(command, 0, "owned-device", "")
            raise AssertionError("Expired provisioning must not start another ordinary command.")

        with patch("run_suite.time.monotonic", side_effect=lambda: clock[0]), patch.object(self.suite, "run", side_effect=run):
            with self.assertRaisesRegex(TimeoutError, "provisioning.*600"):
                self.suite.boot()
        self.assertEqual(len(commands), 1)
        self.assertEqual(self.suite.state["device"], "owned-device")
        report = json.loads((self.output / "ios-provisioning.json").read_text())
        self.assertFalse(report["ready"])
        self.assertEqual(report["seconds"], 601)

    def test_ios_cleanup_uses_one_owned_sixty_second_budget(self):
        self.suite.args.platform = "ios"
        clock = [100]
        commands = []

        def run(*command, **kwargs):
            commands.append((command, kwargs))
            entry = {"udid": self.suite.state["device"], "name": self.suite.state["name"], "state": "Booted"}
            clock[0] += 40
            return subprocess.CompletedProcess(command, 0, json.dumps({"devices": {"runtime": [entry]}}), "")

        with patch("run_suite.time.monotonic", side_effect=lambda: clock[0]), \
                patch.object(self.suite, "run", side_effect=run):
            with self.assertRaisesRegex(RuntimeError, "cleanup.*60"):
                self.suite.cleanup()
        self.assertEqual([kwargs["timeout"] for _, kwargs in commands], [60, 20])
        self.assertFalse(self.suite.state["cleanupDone"])

    def ios_deadline_helper(self, mode):
        helper = self.output / "xcrun"
        helper.write_text(f"""#!{sys.executable}
import os, sys
from pathlib import Path
sys.path.insert(0, {str(ROOT / "tests/DncDbg.RemoteTests")!r})
from test_mobile_processes import waiting_parent
if sys.argv[2] == "create":
    print("12345678-1234-1234-1234-123456789abc")
elif sys.argv[2] == "bootstatus" or {mode!r} == "cleanup":
    waiting_parent(os.environ["DNCDBG_TEST_PROCESS_RECORD"], ignore_term=True)
""")
        helper.chmod(0o700)
        self.suite.args.platform = "ios"
        self.suite.env["PATH"] = str(self.output) + os.pathsep + os.environ["PATH"]
        record = self.output / "owned-processes.json"
        self.suite.env["DNCDBG_TEST_PROCESS_RECORD"] = str(record)
        return record

    def assert_ios_deadline_owned_processes_gone(self, record):
        self.assertTrue(record.is_file(), "The real parent/child must reach the timeout path.")
        owned = json.loads(record.read_text())
        self.assertFalse(alive(owned["parent"]), "Timed-out group leader must be reaped.")
        self.assertFalse(alive(owned["child"]), "Timed-out child must be reaped, not orphaned.")
        with socket.socket() as connection:
            self.assertNotEqual(connection.connect_ex(("127.0.0.1", owned["port"])), 0)
        self.assertTrue(any("owned helper ready" in p.read_text() for p in self.output.glob("*.log")))

    def wait_for_ios_helper_record(self, record, deadline):
        while time.monotonic() < deadline:
            try:
                owned = json.loads(record.read_text())
                self.assertEqual(set(owned), {"parent", "child", "port"})
                return
            except (FileNotFoundError, json.JSONDecodeError):
                time.sleep(0.005)
        self.fail("Real helper did not become ready within the existing operation deadline.")

    @unittest.skipUnless(os.name == "posix", "POSIX command ownership")
    def test_real_ios_boot_timeout_includes_term_kill_drain_and_reap(self):
        record = self.ios_deadline_helper("boot")
        budget = 1.5
        started = time.monotonic()
        try:
            with patch("run_suite.IOS_BOOT_TIMEOUT", budget), self.assertRaises(TimeoutError):
                self.suite.boot()
            self.assertLessEqual(time.monotonic() - started, budget + 0.25)
            self.assert_ios_deadline_owned_processes_gone(record)
        finally:
            if record.exists():
                owned = json.loads(record.read_text())
                for pid in (owned["parent"], owned["child"]):
                    if alive(pid):
                        os.kill(pid, signal.SIGKILL)

    @unittest.skipUnless(os.name == "posix", "POSIX command ownership")
    def test_real_ios_cleanup_timeout_includes_term_kill_drain_and_reap(self):
        record = self.ios_deadline_helper("cleanup")
        budget = 1.5
        started = time.monotonic()
        with patch("run_suite.IOS_CLEANUP_TIMEOUT", budget), self.assertRaisesRegex(RuntimeError, "Cleanup failed"):
            self.suite.cleanup()
        self.assertLessEqual(time.monotonic() - started, budget + 0.25)
        self.assert_ios_deadline_owned_processes_gone(record)
        self.assertFalse(self.suite.state["cleanupDone"])

    @unittest.skipUnless(os.name == "posix", "POSIX command ownership")
    def test_real_ios_cancellation_still_settles_owned_group_within_deadline(self):
        record = self.ios_deadline_helper("cleanup")
        from process_tools import start_owned
        communicate = []

        def start(*args, **kwargs):
            process = start_owned(*args, **kwargs)
            original = process.communicate

            def cancel(*args, **kwargs):
                if not communicate:
                    ready = time.monotonic() + 1
                    while not record.exists() and time.monotonic() < ready:
                        time.sleep(0.01)
                    communicate.append(True)
                    raise KeyboardInterrupt()
                return original(*args, **kwargs)
            process.communicate = cancel
            return process

        started = time.monotonic()
        with patch("process_tools.start_owned", side_effect=start), self.assertRaises(KeyboardInterrupt):
            self.suite.run("xcrun", "simctl", "list", "devices", "-j",
                           timeout=1.5, deadline=started + 1.5)
        self.assertLessEqual(time.monotonic() - started, 1.75)
        self.assert_ios_deadline_owned_processes_gone(record)

    @unittest.skipUnless(os.name == "posix", "POSIX command ownership")
    def test_real_late_cancellation_and_error_preserve_cause_and_settle_owned_group(self):
        from process_tools import start_owned
        for original in (KeyboardInterrupt("late cancellation"), ValueError("late tool failure")):
            with self.subTest(error=type(original).__name__):
                record = self.ios_deadline_helper("cleanup")
                record.unlink(missing_ok=True)
                processes = []
                injected = []
                started = time.monotonic()
                deadline = started + 1.5

                def start(*args, **kwargs):
                    process = start_owned(*args, **kwargs)
                    processes.append(process)
                    communicate = process.communicate

                    def late(*args, **kwargs):
                        if not injected:
                            while time.monotonic() <= deadline:
                                time.sleep(0.005)
                            injected.append(True)
                            raise original
                        return communicate(*args, **kwargs)
                    process.communicate = late
                    return process

                try:
                    with patch("process_tools.start_owned", side_effect=start):
                        try:
                            self.suite.run("xcrun", "simctl", "list", "devices", "-j",
                                           timeout=1.5, deadline=deadline)
                        except BaseException as error:
                            self.assertTrue(error is original or error.__cause__ is original,
                                            "Late settlement must preserve the original error, not replace it with timeout.")
                            if error is not original:
                                self.assertIn("settlement", str(error).lower())
                        else:
                            self.fail("Late cancellation/error cannot return success.")
                    self.assertLessEqual(time.monotonic() - started, 6.75)
                    self.assert_ios_deadline_owned_processes_gone(record)
                    self.assertIsNotNone(processes[0].returncode)
                    reports = [json.loads(p.read_text()) for p in self.output.glob("*.settlement.json")]
                    self.assertTrue(any(p["pid"] == processes[0].pid and p["leaderReaped"] and p["groupAbsent"]
                                        and p["drainComplete"] and not p["resourceJournalClean"] and p["deadlineExceeded"]
                                        and p["primaryError"] == type(original).__name__ for p in reports))
                finally:
                    for process in processes:
                        if process.poll() is None:
                            process.kill()
                            process.wait(timeout=1)

    @unittest.skipUnless(os.name == "posix", "POSIX command ownership")
    def test_full_ios_cleanup_late_cancellation_preserves_identity_and_stops_commands(self):
        record = self.ios_deadline_helper("cleanup")
        original = KeyboardInterrupt("cleanup interrupted")
        started = time.monotonic()
        deadline = started + 0.8
        real_start = process_tools.start_owned
        commands = []

        def start(args, **kwargs):
            commands.append(args)
            process = real_start(args, **kwargs)
            communicate = process.communicate
            injected = []

            def late(*args, **kwargs):
                if not injected:
                    while time.monotonic() <= deadline:
                        time.sleep(0.005)
                    injected.append(True)
                    raise original
                return communicate(*args, **kwargs)
            process.communicate = late
            return process

        with patch("run_suite.IOS_CLEANUP_TIMEOUT", 0.8), patch("process_tools.start_owned", side_effect=start):
            try:
                self.suite.cleanup()
            except BaseException as error:
                self.assertIs(error, original)
            else:
                self.fail("Full cleanup must re-raise the original cancellation.")
        self.assertEqual(len(commands), 1, "Cancellation must stop ordinary cleanup commands.")
        self.assertFalse(self.suite.state["cleanupDone"])
        self.assertFalse(self.suite.state["resourceJournalClean"])
        self.assert_ios_deadline_owned_processes_gone(record)
        self.assertTrue(all(r.leader_reaped for r in self.suite.process_recoveries))

    def test_mobile_main_records_evidence_then_preserves_original_cancellation(self):
        destination = self.output / "cancelled-main"
        original = KeyboardInterrupt("top-level cancellation")
        previous = signal.getsignal(signal.SIGTERM)
        try:
            with patch.object(sys, "argv", ["run_suite.py", "--platform", "ios", "--output", str(destination)]), \
                    patch.object(Suite, "execute", side_effect=original):
                try:
                    run_suite.main()
                except BaseException as error:
                    self.assertIs(error, original)
                else:
                    self.fail("Top-level cancellation cannot become an ordinary status or success.")
        finally:
            signal.signal(signal.SIGTERM, previous)
        manifest = json.loads((destination / "evidence/manifest.json").read_text())
        self.assertFalse(manifest["passed"])
        self.assertIn("top-level cancellation", manifest["error"])
        self.assertIn("KeyboardInterrupt", (destination / "failure.log").read_text())

    def test_mobile_failure_evidence_uses_the_python39_traceback_contract(self):
        formatter = run_suite.traceback.format_exception

        def legacy_format(exception_type, exception, traceback):
            return formatter(exception_type, exception, traceback)

        previous = signal.getsignal(signal.SIGTERM)
        try:
            for primary in (RuntimeError("controlled startup failure"), KeyboardInterrupt("controlled cancellation"),
                            SystemExit("controlled shutdown")):
                with self.subTest(exception=type(primary).__name__):
                    destination = self.output / type(primary).__name__
                    with patch.object(sys, "argv", ["run_suite.py", "--platform", "ios", "--output", str(destination)]), \
                            patch.object(Suite, "execute", side_effect=primary), patch.object(Suite, "cleanup"), \
                            patch.object(run_suite.traceback, "format_exception", legacy_format):
                        if isinstance(primary, (KeyboardInterrupt, SystemExit)):
                            with self.assertRaises(type(primary)) as raised:
                                run_suite.main()
                            self.assertIs(raised.exception, primary)
                        else:
                            self.assertEqual(run_suite.main(), 1)
                    manifest = json.loads((destination / "evidence/manifest.json").read_text())
                    self.assertFalse(manifest["passed"])
                    self.assertIn(str(primary), manifest["error"])
                    self.assertIn(type(primary).__name__, (destination / "failure.log").read_text())
        finally:
            signal.signal(signal.SIGTERM, previous)

    @unittest.skipUnless(os.name == "posix", "POSIX command ownership")
    def test_deadline_recovery_separates_reaped_leader_from_failed_partial_drain(self):
        log = self.output / "drain.log"
        real_start = process_tools.start_owned

        def start(*args, **kwargs):
            process = real_start(*args, **kwargs)
            communicate = process.communicate
            calls = []

            def drain(*args, **kwargs):
                if not calls:
                    calls.append(True)
                    communicate(*args, **kwargs)
                raise subprocess.TimeoutExpired(process.args, 0, output=b"partial\xff", stderr=b"stderr\xfe")
            process.communicate = drain
            return process

        with patch("process_tools.start_owned", side_effect=start):
            with self.assertRaises(process_tools.OwnedProcessSettlementError) as caught:
                self.suite.run(sys.executable, "-c", "pass", timeout=1, deadline=time.monotonic() + 1)
        recovery = caught.exception.recovery
        self.assertTrue(recovery.leader_reaped)
        self.assertTrue(recovery.group_absent)
        self.assertFalse(recovery.drain_complete)
        self.assertIn("partial\\xff", recovery.stdout_text)
        self.assertIn("stderr\\xfe", recovery.stderr_text)
        self.assertFalse(recovery.complete)
        with self.assertRaises(RuntimeError):
            self.suite.cleanup()
        self.assertFalse(self.suite.state["cleanupDone"])

    @unittest.skipUnless(os.name == "posix", "POSIX command ownership")
    def test_deadline_recovery_reaps_when_first_wait_exhausts_admission_deadline(self):
        record = self.ios_deadline_helper("cleanup")
        real_start = process_tools.start_owned
        deadline = time.monotonic() + 1

        def start(*args, **kwargs):
            process = real_start(*args, **kwargs)
            wait = process.wait
            calls = []

            def delayed(*args, **kwargs):
                if not calls:
                    calls.append(True)
                    while time.monotonic() <= deadline:
                        time.sleep(0.005)
                    raise subprocess.TimeoutExpired(process.args, 0)
                return wait(*args, **kwargs)
            process.wait = delayed
            return process

        with patch("process_tools.start_owned", side_effect=start):
            with self.assertRaises(process_tools.OwnedProcessSettlementError) as caught:
                self.suite.run("xcrun", "simctl", "list", "devices", "-j", timeout=1, deadline=deadline)
        self.assertTrue(caught.exception.recovery.complete)
        self.assert_ios_deadline_owned_processes_gone(record)

    def test_deadline_runner_rejects_known_daemon_modes_without_resource_journal(self):
        for flag in ("--daemon", "--daemonize", "--background"):
            with self.subTest(flag=flag), patch("process_tools.start_owned") as start:
                with self.assertRaisesRegex(ValueError, "resource journal"):
                    self.suite.run("unowned-tool", flag, deadline=time.monotonic() + 1)
                start.assert_not_called()

    def test_restarted_cleanup_cannot_hide_unverified_process_recovery_journal(self):
        self.suite.args.platform = "ios"
        self.suite.state["processRecovery"] = [{
            "pid": 123456, "processGroup": 123456, "leaderReaped": False,
            "groupAbsent": False, "drainComplete": False}]
        with patch.object(self.suite, "run", return_value=subprocess.CompletedProcess([], 0, '{"devices":{}}', "")):
            with self.assertRaisesRegex(RuntimeError, "process recovery journal"):
                self.suite.cleanup()
        self.assertFalse(self.suite.state["cleanupDone"])
        self.assertFalse(self.suite.state["resourceJournalClean"])

    def test_stop_and_second_drain_errors_remain_primary_after_mandatory_recovery(self):
        for phase in ("stop", "drain"):
            for original in (KeyboardInterrupt("stop/drain cancellation"), SystemExit(17), ValueError("stop/drain error")):
                with self.subTest(phase=phase, error=type(original).__name__):
                    real_start, real_stop = process_tools.start_owned, process_tools.stop_owned
                    calls = []

                    def stop(*args, **kwargs):
                        if phase == "stop" and not calls:
                            calls.append(True)
                            raise original
                        return real_stop(*args, **kwargs)

                    def start(*args, **kwargs):
                        process = real_start(*args, **kwargs)
                        communicate = process.communicate
                        count = []

                        def drain(*args, **kwargs):
                            count.append(True)
                            if phase == "drain" and len(count) == 2:
                                raise original
                            return communicate(*args, **kwargs)
                        process.communicate = drain
                        return process

                    with patch("process_tools.start_owned", side_effect=start), \
                            patch("process_tools.stop_owned", side_effect=stop):
                        try:
                            self.suite.run(sys.executable, "-c", "print('mandatory recovery output')",
                                           deadline=time.monotonic() + 2)
                        except BaseException as error:
                            self.assertTrue(error is original or error.__cause__ is original)
                        else:
                            self.fail("Stop/drain failure cannot return success.")
                    self.assertTrue(self.suite.process_recoveries[-1].complete)
                    self.assertIs(self.suite.process_recoveries[-1].primary_error, original)

    def test_evidence_write_failure_never_loses_owner_or_original_cancellation(self):
        original = KeyboardInterrupt("cancel with full disk")
        real_start = process_tools.start_owned

        def start(*args, **kwargs):
            process = real_start(*args, **kwargs)
            communicate = process.communicate
            calls = []

            def cancel(*args, **kwargs):
                if not calls:
                    calls.append(True)
                    raise original
                return communicate(*args, **kwargs)
            process.communicate = cancel
            return process

        with patch("process_tools.start_owned", side_effect=start), \
                patch.object(Path, "write_text", side_effect=OSError("evidence disk full")):
            try:
                self.suite.run(sys.executable, "-c", "print('output before cancellation')",
                               deadline=time.monotonic() + 2)
            except BaseException as error:
                self.assertTrue(error is original or error.__cause__ is original)
            else:
                self.fail("Cancellation cannot become success when evidence is unwritable.")
            self.assertTrue(self.suite.process_recoveries)
            recovery = self.suite.process_recoveries[-1]
            self.assertTrue(recovery.complete)
            self.assertTrue(recovery.record_errors)
            with self.assertRaises(KeyboardInterrupt) as caught:
                self.suite.cleanup()
            self.assertIs(caught.exception, original)
            self.assertFalse(self.suite.state["cleanupDone"])

    def test_incomplete_recovery_owner_survives_unwritable_logs_and_state(self):
        real_start = process_tools.start_owned

        def start(*args, **kwargs):
            process = real_start(*args, **kwargs)
            communicate = process.communicate
            calls = []

            def drain(*args, **kwargs):
                if not calls:
                    calls.append(True)
                    communicate(*args, **kwargs)
                raise subprocess.TimeoutExpired(process.args, 0, output=b"unwritten partial")
            process.communicate = drain
            return process

        with patch("process_tools.start_owned", side_effect=start), \
                patch.object(Path, "write_text", side_effect=OSError("unwritable evidence")):
            with self.assertRaises(process_tools.OwnedProcessSettlementError) as caught:
                self.suite.run(sys.executable, "-c", "pass", deadline=time.monotonic() + 1)
            self.assertIn(caught.exception.recovery, self.suite.process_recoveries)
            self.assertTrue(caught.exception.recovery.leader_reaped)
            self.assertFalse(caught.exception.recovery.drain_complete)
            self.assertTrue(caught.exception.recovery.record_errors)
            with self.assertRaises(RuntimeError):
                self.suite.cleanup()
            self.assertFalse(self.suite.state["cleanupDone"])

    def test_completed_process_does_not_make_cancelled_resource_cleanup_successful(self):
        original = KeyboardInterrupt("cancelled before resource cleanup")
        real_start = process_tools.start_owned

        def start(*args, **kwargs):
            process = real_start(*args, **kwargs)
            communicate = process.communicate
            calls = []

            def cancel(*args, **kwargs):
                if not calls:
                    calls.append(True)
                    raise original
                return communicate(*args, **kwargs)
            process.communicate = cancel
            return process

        with patch("process_tools.start_owned", side_effect=start), self.assertRaises(KeyboardInterrupt):
            self.suite.run(sys.executable, "-c", "pass", deadline=time.monotonic() + 2)
        self.assertTrue(self.suite.process_recoveries[-1].complete)
        with patch.object(self.suite, "run") as run, self.assertRaises(KeyboardInterrupt):
            self.suite.cleanup()
        run.assert_not_called()
        self.assertFalse(self.suite.state["resourceJournalClean"])
        self.assertFalse(self.suite.state["cleanupDone"])

    def test_owner_callback_failure_still_reaps_and_preserves_original_cause(self):
        for retained in (False, True):
            for original in (ValueError("callback failed"), MemoryError("retention failed"), KeyboardInterrupt("callback cancelled")):
                with self.subTest(retained=retained, error=type(original).__name__):
                    record = self.ios_deadline_helper("cleanup")
                    record.unlink(missing_ok=True)
                    processes, owners = [], []

                    def callback(owner):
                        processes.append(owner.process)
                        if retained:
                            owners.append(owner)
                        ready = time.monotonic() + 1
                        while not record.exists() and time.monotonic() < ready:
                            time.sleep(0.005)
                        raise original

                    try:
                        with self.assertRaises(process_tools.OwnedProcessSettlementError) as caught:
                            process_tools.run_owned("xcrun", "simctl", "list", "devices", "-j",
                                env=self.suite.env, deadline=time.monotonic() + 2,
                                owner_callback=callback, log=self.output / "callback.log")
                        self.assertIs(caught.exception.__cause__, original)
                        self.assertTrue(caught.exception.recovery.complete)
                        self.assert_ios_deadline_owned_processes_gone(record)
                    finally:
                        for process in processes:
                            if process.poll() is None:
                                process_tools.stop_owned(process)

    def test_final_recovery_pass_recomputes_new_cancellation_before_cleanup_result(self):
        self.suite.run(sys.executable, "-c", "pass", deadline=time.monotonic() + 2)
        recovery = self.suite.process_recoveries[-1]
        recovery.drain_complete = False
        original = SystemExit(23)
        calls = []

        def finish():
            calls.append(True)
            if len(calls) == 1:
                return False
            recovery.preserve_error(original)
            recovery.drain_complete = True
            return True

        with patch.object(recovery, "finish", side_effect=finish), patch.object(self.suite, "run") as run:
            with self.assertRaises(SystemExit) as caught:
                self.suite.cleanup()
        self.assertIs(caught.exception, original)
        run.assert_not_called()
        self.assertFalse(self.suite.state["cleanupDone"])

    def test_main_evidence_failures_cannot_replace_the_first_cancellation(self):
        for later in (OSError("evidence full"), KeyboardInterrupt("later interrupt"), SystemExit(29)):
            with self.subTest(later=type(later).__name__):
                destination = self.output / uuid.uuid4().hex
                original = KeyboardInterrupt("first cancellation")
                mkdir = Path.mkdir

                def unavailable(path, *args, **kwargs):
                    if path.name == "evidence":
                        raise OSError("fallback mkdir unavailable")
                    return mkdir(path, *args, **kwargs)

                previous = signal.getsignal(signal.SIGTERM)
                try:
                    with patch.object(sys, "argv", ["run_suite.py", "--platform", "ios", "--output", str(destination)]), \
                            patch.object(Suite, "execute", side_effect=original), \
                            patch.object(Suite, "evidence", side_effect=later), patch.object(Path, "mkdir", unavailable):
                        with self.assertRaises(KeyboardInterrupt) as caught:
                            run_suite.main()
                        self.assertIs(caught.exception, original)
                finally:
                    signal.signal(signal.SIGTERM, previous)

    def test_suite_retains_owner_before_report_or_decode_even_if_notification_fails(self):
        class Notifications(list):
            def append(self, value):
                raise ValueError("notification failed before retaining")

        for phase in ("report", "decode"):
            with self.subTest(phase=phase):
                self.suite.process_recoveries = Notifications()
                observed = []

                def unavailable(owner):
                    observed.append(self.suite.recovery_fallback is owner)
                    raise MemoryError("report allocation failed")

                original_record = process_tools.OwnedProcessRecovery.record
                if phase == "report":
                    replacement = unavailable
                else:
                    def replacement(owner):
                        observed.append(self.suite.recovery_fallback is owner)
                        return original_record(owner)
                try:
                    with patch.object(process_tools.OwnedProcessRecovery, "record", replacement):
                        if phase == "decode":
                            with patch.object(process_tools.OwnedProcessRecovery, "stdout_text",
                                              new_callable=unittest.mock.PropertyMock, side_effect=MemoryError("decode failed")):
                                self.suite.run(sys.executable, "-c", "print('retained owner')",
                                               deadline=time.monotonic() + 2)
                        else:
                            self.suite.run(sys.executable, "-c", "print('retained owner')",
                                           deadline=time.monotonic() + 2)
                except BaseException:
                    pass
                else:
                    self.fail("Failed notification/evidence cannot report success.")
                self.assertTrue(observed and all(observed), "Mandatory retention must precede all report/decode operations.")
                self.assertIsNotNone(self.suite.recovery_fallback)
                self.assertTrue(self.suite.recovery_fallback.complete)

    def test_main_outer_perimeter_preserves_first_cancel_when_bookkeeping_itself_fails(self):
        for phase in ("repr", "append"):
            for later in (KeyboardInterrupt("bookkeeping interrupt"), SystemExit(31), MemoryError("bookkeeping allocation")):
                with self.subTest(phase=phase, later=type(later).__name__):
                    destination = self.output / uuid.uuid4().hex
                    original = KeyboardInterrupt("first protected cancellation")

                    class Unformattable(Exception):
                        def __repr__(self):
                            raise later

                    class Unrecordable(list):
                        def append(self, value):
                            raise later

                    def evidence(suite, *args):
                        if phase == "append":
                            suite.state["evidenceErrors"] = Unrecordable()
                            raise OSError("evidence failed")
                        raise Unformattable()

                    previous = signal.getsignal(signal.SIGTERM)
                    try:
                        with patch.object(sys, "argv", ["run_suite.py", "--platform", "ios", "--output", str(destination)]), \
                                patch.object(Suite, "execute", side_effect=original), patch.object(Suite, "evidence", evidence):
                            with self.assertRaises(KeyboardInterrupt) as caught:
                                run_suite.main()
                            self.assertIs(caught.exception, original)
                    finally:
                        signal.signal(signal.SIGTERM, previous)

    @unittest.skipUnless(os.name == "posix", "POSIX publication signal mask")
    def test_publication_fault_waits_for_ready_helper_within_existing_operation_deadline(self):
        helper = self.ios_deadline_helper

        def delayed(mode):
            record = helper(mode)
            record.unlink(missing_ok=True)
            executable = self.output / "xcrun"
            executable.write_text(executable.read_text().replace(
                "import os, sys", "import os, sys, time\ntime.sleep(1.1)", 1))
            return record

        with patch.object(self, "ios_deadline_helper", side_effect=delayed):
            for scenario in (self.test_system_exit_after_launch_return_keeps_published_owner_and_reaps,
                             self.test_real_signal_during_launch_is_deferred_until_owner_publication):
                with self.subTest(scenario=scenario.__name__):
                    scenario()

    @unittest.skipUnless(os.name == "posix", "POSIX publication signal mask")
    def test_real_signal_during_launch_is_deferred_until_owner_publication(self):
        import threading
        record = self.ios_deadline_helper("cleanup")
        popen = subprocess.Popen
        processes = []
        original = KeyboardInterrupt("signal at publication")
        deadline = time.monotonic() + 3

        def interrupted(*_):
            raise original

        def launch(*args, **kwargs):
            if processes:
                return popen(*args, **kwargs)
            process = popen(*args, **kwargs)
            processes.append(process)
            self.wait_for_ios_helper_record(record, deadline)
            main_id = threading.get_ident()
            sender = threading.Thread(target=lambda: signal.pthread_kill(main_id, signal.SIGINT))
            sender.start()
            sender.join()
            return process

        previous = signal.signal(signal.SIGINT, interrupted)
        try:
            with patch("process_tools.subprocess.Popen", side_effect=launch):
                with self.assertRaises(BaseException) as caught:
                    self.suite.run("xcrun", "simctl", "list", "devices", "-j",
                                   deadline=deadline)
            self.assertTrue(caught.exception is original or caught.exception.__cause__ is original,
                            f"Expected publication cancellation, got {caught.exception!r}")
            self.assertIsNotNone(self.suite.recovery_fallback.process)
            self.assertTrue(self.suite.recovery_fallback.complete)
            self.assert_ios_deadline_owned_processes_gone(record)
        finally:
            signal.signal(signal.SIGINT, previous)
            for process in processes:
                if process.poll() is None:
                    process_tools.stop_owned(process)

    @unittest.skipUnless(os.name == "posix", "POSIX publication ownership")
    def test_system_exit_after_launch_return_keeps_published_owner_and_reaps(self):
        record = self.ios_deadline_helper("cleanup")
        start = process_tools.start_owned
        processes = []
        original = SystemExit(37)
        deadline = time.monotonic() + 3

        def launched(*args, **kwargs):
            process = start(*args, **kwargs)
            processes.append(process)
            self.wait_for_ios_helper_record(record, deadline)
            raise original

        try:
            with patch("process_tools.start_owned", side_effect=launched), self.assertRaises(BaseException) as caught:
                self.suite.run("xcrun", "simctl", "list", "devices", "-j", deadline=deadline)
            self.assertTrue(caught.exception is original or caught.exception.__cause__ is original,
                            f"Expected publication cancellation, got {caught.exception!r}")
            self.assertIsNotNone(self.suite.recovery_fallback.process)
            self.assertTrue(self.suite.recovery_fallback.complete)
            self.assert_ios_deadline_owned_processes_gone(record)
        finally:
            for process in processes:
                if process.poll() is None:
                    process_tools.stop_owned(process)

    @unittest.skipUnless(os.name == "posix", "POSIX publication signal mask")
    def test_launch_failure_restores_mask_and_has_no_stale_owned_process(self):
        before = signal.pthread_sigmask(signal.SIG_BLOCK, set())
        with patch("process_tools.subprocess.Popen", side_effect=FileNotFoundError("launch failed")):
            with self.assertRaises(FileNotFoundError):
                self.suite.run("missing-command", deadline=time.monotonic() + 1)
        self.assertIsNone(self.suite.recovery_fallback.process)
        self.assertEqual(list(self.suite.owned_recoveries()), [])
        self.assertEqual(signal.pthread_sigmask(signal.SIG_BLOCK, set()), before)
        with patch("process_tools.os.name", "nt"), patch("process_tools.subprocess.Popen") as popen:
            with self.assertRaisesRegex(RuntimeError, "POSIX"):
                process_tools.start_owned(["not-launched"])
            popen.assert_not_called()

    @unittest.skipUnless(os.name == "posix", "POSIX publication signal mask")
    def test_owned_exec_restores_the_original_child_signal_mask(self):
        expected = sorted(int(value) for value in signal.pthread_sigmask(signal.SIG_BLOCK, set()))
        result = self.suite.run(sys.executable, "-c",
            "import json,signal;print(json.dumps(sorted(int(v) for v in signal.pthread_sigmask(signal.SIG_BLOCK,set()))))",
            deadline=time.monotonic() + 2)
        self.assertEqual(json.loads(result.stdout), expected)

    def test_state_handler_bookkeeping_cannot_replace_original_notification_cancellation(self):
        original = KeyboardInterrupt("first notification cancellation")

        class Notifications(list):
            def append(self, value):
                raise original

        for later in (MemoryError("snapshot formatting"), KeyboardInterrupt("later state cancellation"), SystemExit(41)):
            with self.subTest(later=type(later).__name__):
                class Unformattable(OSError):
                    def __str__(self):
                        raise later

                self.suite.process_recoveries = Notifications()
                with patch.object(self.suite, "save", side_effect=Unformattable()), \
                        self.assertRaises(BaseException) as caught:
                    self.suite.run(sys.executable, "-c", "pass", deadline=time.monotonic() + 2)
                self.assertTrue(caught.exception is original or caught.exception.__cause__ is original)
                self.assertTrue(self.suite.recovery_fallback.complete)
                self.assertIs(self.suite.recovery_fallback.primary_error, original)

    def test_owned_exec_preserves_real_popen_oserror_class_errno_and_filename(self):
        denied = self.output / "not-executable"
        denied.write_text("not executable")
        denied.chmod(0o600)
        invalid = self.output / "invalid-format"
        invalid.write_text("not an executable format")
        invalid.chmod(0o700)
        for target in (str(self.output / "missing"), str(denied), str(self.output), str(invalid)):
            with self.subTest(target=target):
                try:
                    subprocess.Popen([target])
                except OSError as expected:
                    expected_type, expected_errno, expected_filename = type(expected), expected.errno, expected.filename
                else:
                    self.fail("Invalid executable unexpectedly launched.")
                with self.assertRaises(expected_type) as caught:
                    self.suite.run(target, deadline=time.monotonic() + 2)
                self.assertEqual(caught.exception.errno, expected_errno)
                self.assertEqual(caught.exception.filename, expected_filename)
                owner = self.suite.recovery_fallback
                self.assertTrue(owner.complete)
                self.assertIsNone(owner.exec_error_read)
                self.assertIsNone(owner.exec_error_write)
                self.assertNotIn("Traceback", owner.stderr_text)

    def test_owned_exec_restores_popen_native_sigpipe_disposition(self):
        command = ("/bin/sh", "-c", "kill -PIPE $$; printf survived")
        expected = process_tools.run_owned(*command, check=False)
        actual = self.suite.run(*command, check=False, deadline=time.monotonic() + 2)
        self.assertEqual((actual.returncode, actual.stdout), (expected.returncode, expected.stdout))
        self.assertEqual(actual.returncode, -signal.SIGPIPE)

    def test_exec_error_handshake_handles_partial_malformed_and_timeout_records(self):
        import threading
        for payload, expected in ((b'{"version":1,"errno":2}', FileNotFoundError),
                                  (b"{malformed", RuntimeError), (b"x" * 300, RuntimeError), (None, subprocess.TimeoutExpired)):
            with self.subTest(payload=payload):
                read, write = os.pipe()
                owner = process_tools.OwnedProcessRecovery(None, self.output / "handshake.log", time.monotonic() + 1)
                owner.exec_error_read = read
                done = threading.Event()

                def send():
                    try:
                        if payload is None:
                            done.wait(timeout=2)
                        else:
                            os.write(write, payload[:5])
                            time.sleep(0.01)
                            os.write(write, payload[5:])
                    finally:
                        os.close(write)

                sender = threading.Thread(target=send)
                sender.start()
                try:
                    with self.assertRaises(expected):
                        owner.await_exec(("missing-target",), time.monotonic() + 0.1)
                    self.assertIsNone(owner.exec_error_read)
                finally:
                    done.set()
                    sender.join(timeout=2)
                    if owner.exec_error_read is not None:
                        os.close(owner.exec_error_read)
                    self.assertFalse(sender.is_alive())

    @unittest.skipUnless(os.name == "posix", "POSIX exec-error pipe")
    def test_signal_during_real_exec_handshake_settles_retained_process_group(self):
        import threading
        record = self.ios_deadline_helper("cleanup")
        owner = process_tools.OwnedProcessRecovery(None, self.output / "handshake-cancel.log", time.monotonic() + 2)
        self.assertTrue(callable(getattr(owner, "await_exec", None)))
        read, write = os.pipe()
        owner.exec_error_read = read
        owner.process = process_tools.start_owned(("xcrun", "simctl", "list", "devices", "-j"),
            env=self.suite.env, pass_fds=(write,), stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        os.close(write)
        ready = time.monotonic() + 1
        while not record.exists() and time.monotonic() < ready:
            time.sleep(0.005)
        original = KeyboardInterrupt("exec handshake cancelled")

        def interrupted(*_):
            raise original

        main_id = threading.get_ident()
        sender = threading.Thread(target=lambda: (time.sleep(0.02), signal.pthread_kill(main_id, signal.SIGINT)))
        previous = signal.signal(signal.SIGINT, interrupted)
        sender.start()
        try:
            with self.assertRaises(KeyboardInterrupt) as caught:
                owner.await_exec(("xcrun",), time.monotonic() + 1)
            self.assertIs(caught.exception, original)
        finally:
            sender.join(timeout=1)
            signal.signal(signal.SIGINT, previous)
            self.assertTrue(owner.finish())
            if owner.exec_error_read is not None:
                os.close(owner.exec_error_read)
        self.assert_ios_deadline_owned_processes_gone(record)

    @unittest.skipUnless(os.name == "posix", "POSIX high-FD exec handshake")
    def test_real_high_fd_exec_handshake_success_and_oserror(self):
        import resource
        previous = resource.getrlimit(resource.RLIMIT_NOFILE)
        if previous[1] != resource.RLIM_INFINITY and previous[1] < 1100:
            self.skipTest(f"Hard FD limit {previous[1]} cannot exercise FD_SETSIZE.")
        held = []
        try:
            try:
                resource.setrlimit(resource.RLIMIT_NOFILE,
                                   (1100 if previous[0] != resource.RLIM_INFINITY and previous[0] < 1100 else previous[0], previous[1]))
                while not held or held[-1] < 1050:
                    held.append(os.open(os.devnull, os.O_RDONLY))
            except OSError as error:
                if error.errno in (errno.EMFILE, errno.ENFILE, errno.EPERM):
                    self.skipTest(f"Host FD resource limit prevents high-FD handshake: {error}")
                raise
            for command in (shutil.which("true"), str(self.output / "missing-high-fd")):
                with self.subTest(command=command):
                    if command.endswith("missing-high-fd"):
                        with self.assertRaises(FileNotFoundError) as caught:
                            self.suite.run(command, deadline=time.monotonic() + 2)
                        self.assertEqual(caught.exception.errno, errno.ENOENT)
                    else:
                        self.assertEqual(self.suite.run(command, deadline=time.monotonic() + 2).returncode, 0)
                    self.assertTrue(self.suite.recovery_fallback.complete)
                    self.assertIsNone(self.suite.recovery_fallback.exec_error_read)
        finally:
            for fd in reversed(held):
                os.close(fd)
            resource.setrlimit(resource.RLIMIT_NOFILE, previous)

    def test_close_fds_false_keeps_default_semantics_but_owned_mode_rejects_before_spawn(self):
        read, write = os.pipe()
        os.set_inheritable(write, True)
        command = [sys.executable, "-c", f"import os;os.write({write},b'kept')"]
        owner = process_tools.OwnedProcessRecovery(None, self.output / "close-fds.log", time.monotonic() + 2)
        try:
            process = process_tools.start_owned(command, close_fds=False, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
            try:
                process.communicate(timeout=2)
                self.assertEqual(process.returncode, 0)
                self.assertEqual(os.read(read, 4), b"kept")
            finally:
                process_tools.stop_owned(process)
                process.stdout.close()
                process.stderr.close()
            with warnings.catch_warnings(record=True) as caught, patch("process_tools.subprocess.Popen") as launch:
                with self.assertRaisesRegex(ValueError, "close_fds=False"):
                    process_tools.start_owned(command, owner=owner, close_fds=False)
                launch.assert_not_called()
                self.assertEqual(caught, [])
        finally:
            owner.close_exec_error_pipe()
            os.close(read)
            os.close(write)

    def test_owned_launch_keeps_explicit_pass_fds_and_deduplicates_them(self):
        read, write = os.pipe()
        owner = process_tools.OwnedProcessRecovery(None, self.output / "pass-fds.log", time.monotonic() + 2)
        command = [sys.executable, "-c", f"import os;os.write({write},b'kept')"]
        try:
            with warnings.catch_warnings(record=True) as caught:
                process_tools.start_owned(command, owner=owner, pass_fds=(write, write),
                                          stdout=subprocess.PIPE, stderr=subprocess.PIPE)
                owner.await_exec(command, time.monotonic() + 2)
                with selectors.DefaultSelector() as ready:
                    ready.register(read, selectors.EVENT_READ)
                    self.assertTrue(ready.select(2), "Explicit caller FD did not survive exec.")
                self.assertEqual(os.read(read, 4), b"kept")
                self.assertEqual(caught, [])
        finally:
            if owner.process is not None:
                self.assertTrue(owner.finish())
            else:
                owner.close_exec_error_pipe()
            os.close(read)
            os.close(write)

    @unittest.skipUnless(os.name == "posix", "POSIX command ownership")
    def test_real_invalid_output_bytes_are_preserved_independently_of_reaping(self):
        result = self.suite.run(sys.executable, "-c",
            "import sys;sys.stdout.buffer.write(b'output\\xff');sys.stderr.buffer.write(b'error\\xfe')",
            deadline=time.monotonic() + 2)
        self.assertEqual(result.stdout, "output\\xff")
        self.assertEqual(result.stderr, "error\\xfe")
        report = json.loads(next(self.output.glob("*.settlement.json")).read_text())
        self.assertTrue(report["leaderReaped"] and report["groupAbsent"] and report["drainComplete"])
        self.assertFalse(report["resourceJournalClean"])
        self.assertEqual(report["ownershipScope"], "leader-and-original-process-group")

    @unittest.skipUnless(os.name == "posix", "POSIX command ownership")
    def test_escaped_pipe_holder_is_not_group_owned_or_a_clean_resource_journal(self):
        record = self.output / "escaped.json"
        nonce = uuid.uuid4().hex
        child = (
            "import os,socket,json;from pathlib import Path;"
            "s=socket.socket();s.bind(('127.0.0.1',0));s.listen();"
            f"Path({str(record)!r}).write_text(json.dumps({{'pid':os.getpid(),'port':s.getsockname()[1],'nonce':{nonce!r}}}));"
            "print('escaped resource ready',flush=True);c,_=s.accept();"
            f"assert c.recv(128)=={nonce.encode()!r};c.close();s.close()")
        parent = f"import subprocess,sys;subprocess.Popen([sys.executable,'-c',{child!r}],start_new_session=True)"
        owned = None
        try:
            with self.assertRaises(process_tools.OwnedProcessSettlementError) as caught:
                self.suite.run(sys.executable, "-c", parent, timeout=1, deadline=time.monotonic() + 1)
            owned = json.loads(record.read_text())
            recovery = caught.exception.recovery
            self.assertTrue(recovery.leader_reaped and recovery.group_absent)
            self.assertFalse(recovery.drain_complete)
            self.assertNotEqual(os.getpgid(owned["pid"]), recovery.process.pid)
            self.assertTrue(alive(owned["pid"]), "An escaped resource must not be killed by inferred ancestry.")
            with self.assertRaisesRegex(RuntimeError, "recovery incomplete"):
                self.suite.cleanup()
            self.assertFalse(self.suite.state["cleanupDone"])
            self.assertFalse(self.suite.state["resourceJournalClean"])
        finally:
            if record.exists():
                owned = json.loads(record.read_text())
                self.assertEqual(owned["nonce"], nonce)
                with socket.create_connection(("127.0.0.1", owned["port"]), timeout=2) as connection:
                    connection.sendall(nonce.encode())
                deadline = time.monotonic() + 5
                while alive(owned["pid"]) and time.monotonic() < deadline:
                    time.sleep(0.01)
                self.assertFalse(alive(owned["pid"]))
                with socket.socket() as connection:
                    self.assertNotEqual(connection.connect_ex(("127.0.0.1", owned["port"])), 0)

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

    @unittest.skipUnless(os.name == "posix", "POSIX build ownership")
    def test_progress_timeout_retains_primary_and_owner_when_settlement_is_denied(self):
        processes = []
        start = process_tools.start_owned

        def launch(*args, **kwargs):
            process = start(*args, **kwargs)
            processes.append(process)
            return process

        try:
            with patch("run_suite.start_owned", side_effect=launch), patch("process_tools.start_owned", side_effect=launch), \
                    patch("run_suite.stop_owned", side_effect=PermissionError(errno.EPERM, "controlled signal denial")), \
                    patch("process_tools.stop_owned", side_effect=PermissionError(errno.EPERM, "controlled signal denial")):
                with self.assertRaisesRegex(TimeoutError, "exceeded"):
                    self.suite.run(sys.executable, "-c", "import time; print('build ready',flush=True);time.sleep(30)",
                                   timeout=0.3, progress=True, progress_interval=0.05)
            owner = self.suite.recovery_fallback
            self.assertIsNotNone(owner)
            self.assertIs(owner.process, processes[0])
            self.assertFalse(owner.complete)
            status = json.loads(next(self.output.glob("*.status.json")).read_text())
            self.assertEqual(status["primaryError"], "TimeoutError")
            self.assertFalse(status["buildGroupSettled"])
            self.assertIn("PermissionError", status["recoveryErrors"])
            self.assertIn("build ready", next(self.output.glob("*.log")).read_text())
            with patch.object(self.suite, "run", side_effect=AssertionError("No ordinary cleanup after an unverified build")) as ordinary, \
                    patch("process_tools.stop_owned", side_effect=PermissionError(errno.EPERM, "controlled signal denial")):
                with self.assertRaises(RuntimeError):
                    self.suite.cleanup()
            ordinary.assert_not_called()
            self.assertFalse(self.suite.state["cleanupDone"])
            self.assertFalse(self.suite.state["buildGroupSettled"])
        finally:
            for process in processes:
                if process.poll() is None:
                    process_tools.stop_owned(process)
                process.communicate(timeout=5)

    @unittest.skipUnless(os.name == "posix", "POSIX build ownership")
    def test_progress_status_write_failure_cannot_replace_the_build_timeout(self):
        write = Path.write_text

        def fail_status(path, *args, **kwargs):
            if path.name.endswith(".status.json"):
                raise OSError("controlled status write failure")
            return write(path, *args, **kwargs)

        with patch.object(Path, "write_text", fail_status):
            with self.assertRaisesRegex(TimeoutError, "exceeded"):
                self.suite.run(sys.executable, "-c", "import time;print('build ready',flush=True);time.sleep(30)",
                               timeout=0.3, progress=True, progress_interval=0.05)
        owner = self.suite.recovery_fallback
        self.assertIsNotNone(owner)
        self.assertTrue(owner.complete, repr((owner.errors, owner.group_ownership.observations)))
        self.assertIsInstance(owner.primary_error, TimeoutError)
        self.assertTrue(owner.record_errors)
        self.assertIn("build ready", next(self.output.glob("*.log")).read_text())

    @unittest.skipUnless(os.name == "posix", "POSIX exact build ownership")
    def test_real_ready_build_kill_eperm_remains_owned_with_exact_identity_and_no_signal_retry(self):
        record = self.ios_deadline_helper("cleanup")
        send = os.killpg
        denied = []

        def guarded(group, value):
            if value == signal.SIGKILL:
                denied.append((group, value))
                raise PermissionError(errno.EPERM, "controlled live SIGKILL denial")
            return send(group, value)

        owner = None
        try:
            with patch("process_tools.os.killpg", side_effect=guarded):
                with self.assertRaisesRegex(TimeoutError, "exceeded"):
                    self.suite.run("xcrun", "simctl", "list", "devices", "-j",
                                   timeout=0.75, progress=True, progress_interval=0.1)
                owner = self.suite.recovery_fallback
                self.assertFalse(owner.complete)
                identity = owner.group_ownership.original
                self.assertEqual(identity["pid"], json.loads(record.read_text())["parent"])
                self.assertEqual(identity["pgid"], identity["pid"])
                self.assertEqual(identity["sid"], identity["pid"])
                self.assertEqual(identity["uid"], os.geteuid())
                self.assertTrue(identity["startIdentity"])
                attempts = owner.group_ownership.signals
                self.assertEqual(sum(a["signal"] == signal.SIGTERM for a in attempts), 1)
                self.assertEqual(sum(a["signal"] == signal.SIGKILL for a in attempts), 1)
                self.assertEqual(next(a for a in attempts if a["signal"] == signal.SIGKILL)["errno"], errno.EPERM)
                self.assertTrue(any(a["before"] and a["after"] for a in attempts if a["signal"] == signal.SIGKILL))
                owner.finish()
                self.assertEqual(len(denied), 1, "Final settlement must not retry a denied nonzero signal.")
                self.assertIsInstance(owner.primary_error, TimeoutError)
                with patch.object(self.suite, "run", side_effect=AssertionError("Unverified build forbids ordinary cleanup")):
                    with self.assertRaises(RuntimeError):
                        self.suite.cleanup()
                self.assertFalse(self.suite.state["cleanupDone"])
                self.assertFalse(self.suite.state["buildGroupSettled"])
                self.assertFalse(self.suite.state["simulatorJournalClean"])
        finally:
            if owner is not None and owner.process.poll() is None:
                current = process_tools.process_identity(owner.process.pid)
                self.assertTrue(owner.group_ownership.same(current, owner.group_ownership.original))
                send(owner.process.pid, signal.SIGKILL)
                owner.process.wait(timeout=5)

    @unittest.skipUnless(os.name == "posix", "POSIX build cancellation")
    def test_progress_cancel_keeps_first_identity_through_status_baseexceptions(self):
        start, write = process_tools.start_owned, Path.write_text
        for secondary in (SystemExit("status signal"), MemoryError("status allocation")):
            with self.subTest(secondary=type(secondary).__name__):
                original = KeyboardInterrupt("first build cancellation")

                def launch(*args, **kwargs):
                    process = start(*args, **kwargs)
                    communicate = process.communicate
                    calls = [0]

                    def interrupt(*args, **kwargs):
                        calls[0] += 1
                        if calls[0] == 1:
                            raise original
                        return communicate(*args, **kwargs)
                    process.communicate = interrupt
                    return process

                def fail_status(path, *args, **kwargs):
                    if path.name.endswith(".status.json"):
                        raise secondary
                    return write(path, *args, **kwargs)

                with patch("run_suite.start_owned", side_effect=launch), patch.object(Path, "write_text", fail_status):
                    with self.assertRaises(KeyboardInterrupt) as raised:
                        self.suite.run(sys.executable, "-c", "import time;time.sleep(30)",
                                       timeout=2, progress=True, progress_interval=0.1)
                self.assertIs(raised.exception, original)
                owner = self.suite.recovery_fallback
                self.assertTrue(owner.complete)
                self.assertIs(owner.primary_error, original)
                self.assertTrue(owner.record_errors)
                self.assertIsNotNone(owner.process.returncode)

    def run_ios_phase_contract(self, failure=None):
        destination = self.output / "phase-contract"
        suite = Suite(Namespace(platform="ios", output=destination, xcode_dir=None))
        calls = []

        def build(configuration="Debug", faults=False):
            calls.append(("build", configuration, faults))
            if configuration == "Release" and failure == "release":
                raise TimeoutError("controlled Release timeout")
            app, assets = destination / (configuration + "-app"), destination / (configuration + "-assets")
            app.mkdir(exist_ok=True)
            assets.mkdir(exist_ok=True)
            (app / "executable").write_bytes(b"fixture executable")
            for name in ("DncDbg.MauiTestApp.dll", "DncDbg.MauiTestApp.pdb", "DncDbg.ReloadFixture.dll",
                         "DncDbg.ReloadFixture.pdb", "dncdbg-agent.json", "dncdbg-package.json"):
                (assets / name).write_bytes(b"exact fixture identity")
            if configuration == "Release" and failure == "debug-mutated":
                (destination / "Debug-assets/DncDbg.MauiTestApp.pdb").write_bytes(b"changed by Release")
            return app, assets

        def fixture(app, assets, proof, name, disconnect=False):
            calls.append(("fixture", name, app.name, assets.name, disconnect))
            if name == "debugger-dap-broker-unavailable" and failure == "first-dap":
                raise RuntimeError("controlled first DAP failure")

        def cleanup():
            calls.append(("cleanup",))
            suite.state["cleanupDone"] = failure != "cleanup-unverified"
            suite.save()

        previous = signal.getsignal(signal.SIGTERM)
        try:
            with patch.object(sys, "argv", ["run_suite.py", "--platform", "ios", "--output", str(destination)]), \
                    patch("run_suite.Suite", return_value=suite), patch.object(suite, "preflight"), \
                    patch.object(suite, "boot"), patch.object(suite, "broker"), \
                    patch.object(suite, "build", side_effect=build), patch.object(suite, "fixture_proof", side_effect=fixture), \
                    patch.object(suite, "proof", side_effect=lambda helper, *a: calls.append(("proof", helper))), \
                    patch.object(suite, "cleanup", side_effect=cleanup):
                result = run_suite.main()
        finally:
            signal.signal(signal.SIGTERM, previous)
        manifest = json.loads((destination / "evidence/manifest.json").read_text())
        return result, calls, manifest

    def test_ios_first_dap_precedes_release_but_every_original_phase_remains_required(self):
        result, calls, manifest = self.run_ios_phase_contract()
        self.assertEqual(result, 0)
        self.assertEqual(calls[:3], [
            ("build", "Debug", False),
            ("fixture", "debugger-dap-broker-unavailable", "Debug-app", "Debug-assets", False),
            ("build", "Release", True)])
        self.assertIn(("fixture", "direct-continue", "Debug-app", "Debug-assets", False), calls)
        self.assertIn(("fixture", "direct-disconnect", "Debug-app", "Debug-assets", True), calls)
        self.assertIn(("fixture", "devflow-only", "Debug-app", "Debug-assets", False), calls)
        self.assertIn(("proof", "run_device.py"), calls)
        self.assertIn(("proof", "run_faults.py"), calls)
        self.assertEqual([call for call in calls if call[0] == "build"], [
            ("build", "Debug", False), ("build", "Release", True),
            ("build", "Debug", True), ("build", "Debug", False)])
        self.assertEqual(calls[-1], ("cleanup",))
        self.assertTrue(manifest["passed"] and manifest["cleanup"] and manifest["phases"]["complete"])
        self.assertEqual(manifest["phases"]["completed"], manifest["phases"]["required"])
        self.assertEqual(manifest["phases"]["completed"][:5], [
            "tuple-admission", "device-boot", "debug-build", "broker-independent-dap", "release-isolation"])

    def test_ios_release_failure_after_first_dap_never_qualifies_the_suite(self):
        result, calls, manifest = self.run_ios_phase_contract("release")
        self.assertEqual(result, 1)
        self.assertEqual([call[:2] for call in calls], [
            ("build", "Debug"), ("fixture", "debugger-dap-broker-unavailable"), ("build", "Release"), ("cleanup",)])
        self.assertFalse(manifest["passed"] or manifest["phases"]["complete"])
        self.assertTrue(manifest["cleanup"])
        self.assertIn("controlled Release timeout", manifest["error"])
        self.assertEqual(manifest["phases"]["completed"][-1], "broker-independent-dap")

    def test_ios_first_dap_failure_stops_later_builds_but_retains_cleanup_and_evidence(self):
        result, calls, manifest = self.run_ios_phase_contract("first-dap")
        self.assertEqual(result, 1)
        self.assertEqual([call[:2] for call in calls], [
            ("build", "Debug"), ("fixture", "debugger-dap-broker-unavailable"), ("cleanup",)])
        self.assertFalse(manifest["passed"] or manifest["phases"]["complete"])
        self.assertTrue(manifest["cleanup"])
        self.assertIn("controlled first DAP failure", manifest["error"])
        self.assertEqual(manifest["phases"]["completed"][-1], "debug-build")

    def test_ios_release_cannot_silently_change_the_early_dap_debug_artifacts(self):
        result, calls, manifest = self.run_ios_phase_contract("debug-mutated")
        self.assertEqual(result, 1)
        self.assertFalse(manifest["passed"] or manifest["phases"]["complete"])
        self.assertTrue(manifest["cleanup"])
        self.assertIn("Debug artifacts changed during Release", manifest["error"])
        self.assertEqual([call[:2] for call in calls], [
            ("build", "Debug"), ("fixture", "debugger-dap-broker-unavailable"), ("build", "Release"), ("cleanup",)])

    def test_completed_ios_phases_do_not_override_unverified_owned_cleanup(self):
        result, calls, manifest = self.run_ios_phase_contract("cleanup-unverified")
        self.assertEqual(result, 1)
        self.assertEqual(calls[-1], ("cleanup",))
        self.assertTrue(manifest["phases"]["complete"])
        self.assertFalse(manifest["passed"] or manifest["cleanup"])
        self.assertIn("cleanup", manifest["error"].lower())

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
