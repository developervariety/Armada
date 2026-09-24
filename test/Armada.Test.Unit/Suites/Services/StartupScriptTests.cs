namespace Armada.Test.Unit.Suites.Services
{
    using System.Diagnostics;
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
                    Path.Combine("scripts", "autonomy", "spawn-helper.sh"),
                    Path.Combine("scripts", "autonomy", "test-spawn-helper.sh"),
                };

                foreach (string relativePath in files)
                {
                    AssertTrue(
                        File.Exists(Path.Combine(root, relativePath)),
                        relativePath + " should exist");
                }
            });

            await RunTest("Autonomy Helper Lifecycle Is Bounded And Tested", () =>
            {
                string root = FindRepositoryRoot();
                string helperPath = Path.Combine(root, "scripts", "autonomy", "spawn-helper.sh");
                string testPath = Path.Combine(root, "scripts", "autonomy", "test-spawn-helper.sh");
                string helperContents = File.ReadAllText(helperPath);

                AssertContains("AUTONOMY_MAX_HELPERS", helperContents, "helper launcher should enforce a concurrency cap");
                AssertContains("AUTONOMY_HELPER_TIMEOUT_MIN", helperContents, "helper launcher should enforce a timeout");
                AssertContains("armada_mark_signal_read", helperContents, "helper contract should acknowledge addressed wakes");
                AssertContains("Do not start a polling loop", helperContents, "helper contract should require a bounded exit");
                AssertContains("--mcp-config", helperContents, "Claude helpers in strict mode must receive an explicit MCP config");
                AssertContains("AUTONOMY_ARMADA_MCP_URL", helperContents, "helper launcher should expose the local Armada MCP endpoint setting");
                AssertContains("helper working directory", helperContents, "helper contract should state the file-sandbox boundary");

                if (OperatingSystem.IsWindows())
                {
                    return;
                }

                using Process process = Process.Start(new ProcessStartInfo
                {
                    FileName = "/bin/bash",
                    ArgumentList = { testPath },
                    WorkingDirectory = root,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                }) ?? throw new InvalidOperationException("Could not start autonomy helper contract test.");

                string standardOutput = process.StandardOutput.ReadToEnd();
                string standardError = process.StandardError.ReadToEnd();
                process.WaitForExit();

                AssertEqual(0, process.ExitCode, "autonomy helper contract test should pass: " + standardOutput + standardError);
                AssertContains("PASS: bounded helper lifecycle", standardOutput, "autonomy helper contract test should report completion");
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
