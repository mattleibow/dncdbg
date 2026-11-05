"""Portable regression gates for mobile orchestration, isolation and evidence (no device required)."""
import json
import io
import queue
import os
import runpy
from pathlib import Path
import shutil
import signal
import socket
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
        self.assertLessEqual(time.monotonic() - started, 6.75)
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
                    self.assertLessEqual(time.monotonic() - started, 1.75)
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
