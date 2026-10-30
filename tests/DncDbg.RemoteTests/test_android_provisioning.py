"""Immutable official Android archive contracts; no SDK, device or network required."""
import hashlib
import json
from pathlib import Path
import shutil
import stat
import sys
import unittest
import uuid
import xml.etree.ElementTree as ET
import zipfile

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tests/DncDbg.MobileTests"))
from provision_android import load_lock, verify_archive, install_archive


class AndroidProvisioningTests(unittest.TestCase):
    def setUp(self):
        self.output = ROOT / "build/android-provision-tests" / uuid.uuid4().hex
        self.output.mkdir(parents=True)

    def tearDown(self):
        shutil.rmtree(self.output)

    def archive(self, revision="37.2.12", extra=None):
        record = next(p.copy() for p in load_lock()["packages"] if p["id"] == "emulator")
        path = self.output / "fixture.zip"
        with zipfile.ZipFile(path, "w") as archive:
            archive.writestr("emulator/source.properties",
                             f"Pkg.Revision={revision}\nPkg.Path=emulator\nPkg.BuildId=16428233\n")
            executable = zipfile.ZipInfo("emulator/emulator")
            executable.external_attr = (stat.S_IFREG | 0o755) << 16
            archive.writestr(executable, b"fixture, never executed")
            if extra:
                archive.writestr(extra, b"not allowed")
        record |= {"size": path.stat().st_size, "sha256": hashlib.sha256(path.read_bytes()).hexdigest()}
        return record, path

    def test_exact_urls_checksums_revisions_and_tuple_are_locked(self):
        lock = load_lock()
        packages = {p["id"]: p for p in lock["packages"]}
        self.assertEqual(set(packages), {"emulator", "system-images;android-37.1;google_apis_ps16k;x86_64"})
        emulator = packages["emulator"]
        self.assertEqual(emulator["revision"], "37.2.12")
        self.assertEqual(emulator["url"], "https://dl.google.com/android/repository/emulator-linux_x64-16428233.zip")
        self.assertEqual(emulator["sha256"], "b08fc43d8608d2955f607f1b287beb525041e730086bdca3af152accac3af9c1")
        image = packages["system-images;android-37.1;google_apis_ps16k;x86_64"]
        self.assertEqual(image["revision"], "9")
        self.assertEqual(image["url"], "https://dl.google.com/android/repository/sys-img/google_apis/x86_64-ps16k-37.1_r09.zip")
        self.assertEqual(image["sha256"], "11b9682183827379e8d415db880a30b9ec84d4032650e08e6149b69b30d545a0")
        self.assertEqual(image["officialSha1"], "35c0da1cd298f3426a5b48233f59515097446a28")
        for package in packages.values():
            self.assertRegex(package["sha256"], r"^[0-9a-f]{64}$")
            self.assertGreater(package["size"], 100000000)

    def test_workflow_never_installs_floating_tuple_critical_package_ids(self):
        workflow = (ROOT / ".github/workflows/mobile.yml").read_text()
        self.assertNotIn('"emulator"', workflow)
        self.assertNotIn('"system-images;android-37.1;google_apis_ps16k;x86_64"', workflow)
        self.assertIn("provision_android.py", workflow)
        self.assertIn("build/android-sdk", workflow)

    def test_corrupt_archive_is_rejected_before_sdk_changes(self):
        record, path = self.archive()
        path.write_bytes(path.read_bytes() + b"corruption")
        with self.assertRaisesRegex(RuntimeError, "checksum|size"):
            install_archive(record, path, self.output / "sdk", self.output / "stage")
        self.assertFalse((self.output / "sdk/emulator").exists())

    def test_revision_mismatch_is_rejected_before_publication(self):
        record, path = self.archive(revision="99.0.0")
        with self.assertRaisesRegex(RuntimeError, "Pkg.Revision"):
            install_archive(record, path, self.output / "sdk", self.output / "stage")
        self.assertFalse((self.output / "sdk/emulator").exists())

    def test_zip_path_escape_is_rejected(self):
        record, path = self.archive(extra="emulator/../../escaped")
        with self.assertRaisesRegex(RuntimeError, "archive path"):
            verify_archive(record, path)
        self.assertFalse((self.output / "escaped").exists())

    def test_install_preserves_executable_and_local_sdk_metadata(self):
        record, path = self.archive()
        installed = install_archive(record, path, self.output / "sdk", self.output / "stage")
        self.assertTrue((installed / "emulator").stat().st_mode & stat.S_IXUSR)
        package = ET.parse(installed / "package.xml").getroot().find("localPackage")
        self.assertEqual(package.attrib["path"], "emulator")
        self.assertEqual([x.text for x in package.find("revision")], ["37", "2", "12"])

    def test_existing_sdk_package_is_not_overwritten(self):
        record, path = self.archive()
        existing = self.output / "sdk/emulator"
        existing.mkdir(parents=True)
        (existing / "unrelated").write_text("preserve")
        with self.assertRaisesRegex(RuntimeError, "already exists"):
            install_archive(record, path, self.output / "sdk", self.output / "stage")
        self.assertEqual((existing / "unrelated").read_text(), "preserve")


if __name__ == "__main__":
    unittest.main()
