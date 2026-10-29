"""Exercise app replacement and rollback without touching login items or the user's Applications folder."""
import pathlib
import shutil
import subprocess
import tempfile
import unittest


@unittest.skipUnless(shutil.which("bash"), "bash is required")
class HarborAppInstallTests(unittest.TestCase):
    def run_install(self, failure):
        script = (pathlib.Path(__file__).resolve().parents[2] / "macos/build-harbor-app.sh").read_text()
        start = script.index("install_native_app() (")
        end = script.index('\n\nif [ "$FAILURES"', start)
        function = script[start:end]
        with tempfile.TemporaryDirectory(prefix="harbor-install-test-") as root:
            root = pathlib.Path(root)
            fixture = root / "fixture" / "Armada Harbor.app"
            resource = fixture / "Contents/Resources/Pi/armada-permission-gate.ts"
            resource.parent.mkdir(parents=True)
            resource.write_text("// new gate")
            apps = root / "Applications"
            previous = apps / "Armada Harbor.app"
            previous.mkdir(parents=True)
            (previous / "obsolete.txt").write_text("preserve on failure")
            harness = r'''
OUTPUT=unused VERSION=test HOST_RID=osx-arm64
install_test_apps="$1"
install_test_fixture="$2"
install_test_failure="$3"
hdiutil() {
  if [ "$1" = attach ]; then
    while [ "$#" -gt 0 ]; do
      if [ "$1" = -mountpoint ]; then
        shift
        command cp -R "$install_test_fixture/Armada Harbor.app" "$1/"
        return
      fi
      shift
    done
    return 1
  fi
  # Emulate unmounting the fixture's files, leaving an empty mount point for rmdir.
  command rm -rf "$3/Armada Harbor.app"
}
ditto() { command cp -R "$1" "$2"; }
codesign() { [ "$install_test_failure" != signature ]; }
mv() {
  if [ "$install_test_failure" = promote ] && [ "$2" = "$install_test_apps/Armada Harbor.app" ] \
     && [ "$1" != "${1%/previous.app}" ]; then command mv "$@"; return; fi
  if [ "$install_test_failure" = promote ] && [ "$2" = "$install_test_apps/Armada Harbor.app" ]; then return 1; fi
  command mv "$@"
}
'''
            result = subprocess.run(
                ["bash", "-c", harness + function + '\ninstall_native_app "$install_test_apps"',
                 "--", str(apps), str(fixture.parent), failure], capture_output=True, text=True)
            if failure:
                self.assertNotEqual(0, result.returncode, result.stdout + result.stderr)
                self.assertEqual("preserve on failure", (previous / "obsolete.txt").read_text())
            else:
                self.assertEqual(0, result.returncode, result.stdout + result.stderr)
                self.assertFalse((previous / "obsolete.txt").exists(), "Replacement must not merge stale bundle files")
                self.assertEqual("// new gate", (previous / "Contents/Resources/Pi/armada-permission-gate.ts").read_text())
            self.assertEqual([], list(apps.glob(".armada-harbor-install.*")), "Staging directories are cleaned")

    def test_replaces_complete_bundle(self):
        self.run_install("")

    def test_signature_failure_preserves_previous_app(self):
        self.run_install("signature")

    def test_promotion_failure_restores_previous_app(self):
        self.run_install("promote")


if __name__ == "__main__":
    unittest.main()
