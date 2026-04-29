namespace Armada.Test.Unit.Suites.Services
{
    using System.IO;
    using Armada.Test.Common;

    public class StartupScriptTests : TestSuite
    {
        public override string Name => "Startup Scripts";

        protected override async Task RunTestsAsync()
        {
            await RunTest("Script Layout Exists", () =>
            {
                string root = FindRepositoryRoot();
                string[] files =
                {
                    Path.Combine("scripts", "common", "publish-server.sh"),
                    Path.Combine("scripts", "common", "healthcheck-server.sh"),
                    Path.Combine("scripts", "windows", "publish-server.bat"),
                    Path.Combine("scripts", "windows", "healthcheck-server.bat"),
                    Path.Combine("scripts", "windows", "start-armada-server.ps1"),
                    Path.Combine("scripts", "windows", "stop-armada-server.ps1"),
                    Path.Combine("scripts", "linux", "install.sh"),
                    Path.Combine("scripts", "linux", "publish-server.sh"),
                    Path.Combine("scripts", "linux", "healthcheck-server.sh"),
                    Path.Combine("scripts", "macos", "install.sh"),
                    Path.Combine("scripts", "macos", "publish-server.sh"),
                    Path.Combine("scripts", "macos", "healthcheck-server.sh"),
                    Path.Combine("scripts", "windows", "install-windows-task.bat"),
                    Path.Combine("scripts", "windows", "update-windows-task.bat"),
                    Path.Combine("scripts", "windows", "remove-windows-task.bat"),
                    Path.Combine("scripts", "linux", "install-systemd-user.sh"),
                    Path.Combine("scripts", "linux", "update-systemd-user.sh"),
                    Path.Combine("scripts", "linux", "remove-systemd-user.sh"),
                    Path.Combine("scripts", "macos", "install-launchd-agent.sh"),
                    Path.Combine("scripts", "macos", "update-launchd-agent.sh"),
                    Path.Combine("scripts", "macos", "remove-launchd-agent.sh"),
                };

                foreach (string relativePath in files)
                {
                    AssertTrue(
                        File.Exists(Path.Combine(root, relativePath)),
                        relativePath + " should exist");
                }
            });

            await RunTest("Scripts Root Has No Bat Or Sh Files", () =>
            {
                string scriptsRoot = Path.Combine(FindRepositoryRoot(), "scripts");

                AssertEqual(0, Directory.GetFiles(scriptsRoot, "*.bat", SearchOption.TopDirectoryOnly).Length, "scripts/ root should not contain .bat files");
                AssertEqual(0, Directory.GetFiles(scriptsRoot, "*.sh", SearchOption.TopDirectoryOnly).Length, "scripts/ root should not contain .sh files");
            });

            await RunTest("Startup Docs Reference Scripted Workflow", () =>
            {
                string root = FindRepositoryRoot();
                string readmeContents = File.ReadAllText(Path.Combine(root, "README.md"));
                string gettingStartedContents = File.ReadAllText(Path.Combine(root, "GETTING_STARTED.md"));
                string startupDocContents = File.ReadAllText(Path.Combine(root, "docs", "RUN_ON_STARTUP.md"));

                AssertContains("docs/RUN_ON_STARTUP.md", readmeContents, "README should link to the run-on-startup guide");
                AssertContains("scripts/linux/install-systemd-user.sh", readmeContents, "README should reference the Linux local deployment installer");
                AssertContains("scripts/macos/install-launchd-agent.sh", readmeContents, "README should reference the macOS local deployment installer");
                AssertContains("scripts/windows/install-windows-task.bat", readmeContents, "README should reference the Windows local deployment installer");
                AssertContains("scripts/linux/update-systemd-user.sh", readmeContents, "README should reference the Linux local deployment updater");
                AssertContains("scripts/macos/update-launchd-agent.sh", readmeContents, "README should reference the macOS local deployment updater");
                AssertContains("scripts/windows/update-windows-task.bat", readmeContents, "README should reference the Windows local deployment updater");
                AssertContains("scripts/linux/healthcheck-server.sh", readmeContents, "README should reference the Linux health-check helper");
                AssertContains("scripts/macos/healthcheck-server.sh", readmeContents, "README should reference the macOS health-check helper");
                AssertContains("scripts/windows/healthcheck-server.bat", readmeContents, "README should reference the Windows health-check helper");
                AssertContains("scripts/linux/install-systemd-user.sh", gettingStartedContents, "Getting Started should reference the Linux local deployment installer");
                AssertContains("scripts/macos/install-launchd-agent.sh", gettingStartedContents, "Getting Started should reference the macOS local deployment installer");
                AssertContains("scripts/windows/install-windows-task.bat", gettingStartedContents, "Getting Started should reference the Windows local deployment installer");
                AssertContains("scripts/linux/update-systemd-user.sh", gettingStartedContents, "Getting Started should reference the Linux local deployment updater");
                AssertContains("scripts/macos/update-launchd-agent.sh", gettingStartedContents, "Getting Started should reference the macOS local deployment updater");
                AssertContains("scripts/windows/update-windows-task.bat", gettingStartedContents, "Getting Started should reference the Windows local deployment updater");
                AssertContains("scripts/linux/healthcheck-server.sh", gettingStartedContents, "Getting Started should reference the Linux health-check helper");
                AssertContains("scripts/macos/healthcheck-server.sh", gettingStartedContents, "Getting Started should reference the macOS health-check helper");
                AssertContains("scripts/windows/healthcheck-server.bat", gettingStartedContents, "Getting Started should reference the Windows health-check helper");
                AssertContains("scripts/windows/install-windows-task.bat", startupDocContents, "Startup guide should reference the Windows installer script");
                AssertContains("scripts/linux/install-systemd-user.sh", startupDocContents, "Startup guide should reference the Linux installer script");
                AssertContains("scripts/macos/install-launchd-agent.sh", startupDocContents, "Startup guide should reference the macOS installer script");
                AssertContains("scripts/common/publish-server.sh", startupDocContents, "Startup guide should reference the shared shell publish helper");
                AssertContains("scripts/windows/healthcheck-server.bat", startupDocContents, "Startup guide should reference the Windows health-check helper");
            });

            await RunTest("Windows Startup Registration Is User Scoped", () =>
            {
                string root = FindRepositoryRoot();
                string installTaskContents = File.ReadAllText(Path.Combine(root, "scripts", "windows", "install-windows-task.bat"));
                string startupDocContents = File.ReadAllText(Path.Combine(root, "docs", "RUN_ON_STARTUP.md"));

                AssertContains(@"CurrentVersion\Run", installTaskContents, "Windows installer should register a current-user Run entry");
                AssertFalse(installTaskContents.Contains("schtasks /create"), "Windows installer should not depend on schtasks task creation");
                AssertContains("does not require elevation", startupDocContents, "Startup guide should document the non-elevated Windows task flow");
            });
        }

        private static string FindRepositoryRoot()
        {
            DirectoryInfo? current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current != null)
            {
                if (Directory.Exists(Path.Combine(current.FullName, "src")) &&
                    Directory.Exists(Path.Combine(current.FullName, "test")))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }

            throw new DirectoryNotFoundException("Could not locate repository root from test base directory.");
        }
    }
}
