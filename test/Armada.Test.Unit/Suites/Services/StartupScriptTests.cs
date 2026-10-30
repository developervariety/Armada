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
            await RunTest("Autonomy Helper Lifecycle Is Bounded And Tested", () =>
            {
                string root = FindRepositoryRoot();
                string helperPath = Path.Combine(root, "scripts", "autonomy", "spawn-helper.sh");
                string testPath = Path.Combine(root, "scripts", "autonomy", "test-spawn-helper.sh");
                string helperContents = File.ReadAllText(helperPath);

                AssertContains("AUTONOMY_MAX_HELPERS", helperContents, "helper launcher should enforce a concurrency cap");
                AssertContains("AUTONOMY_HELPER_TIMEOUT_MIN", helperContents, "helper launcher should enforce a timeout");
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

            const string watcherTest = "Hub Watcher Reports A Refused Subscription And Reconnects";
            string? node = FindOnPath("node");
            if (OperatingSystem.IsWindows() || node == null)
            {
                SkipTest(watcherTest, OperatingSystem.IsWindows()
                    ? "the watcher self-check runs on Unix hosts"
                    : "node is not on PATH, so scripts/autonomy/test-watch-armada.mjs cannot run");
            }
            else
            {
                await RunTest(watcherTest, () =>
                {
                    string root = FindRepositoryRoot();
                    using Process process = Process.Start(new ProcessStartInfo
                    {
                        FileName = node,
                        ArgumentList = { Path.Combine(root, "scripts", "autonomy", "test-watch-armada.mjs") },
                        WorkingDirectory = root,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                    }) ?? throw new InvalidOperationException("Could not start the hub watcher self-check.");

                    Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
                    Task<string> standardError = process.StandardError.ReadToEndAsync();
                    if (!process.WaitForExit(60000))
                    {
                        process.Kill(true);
                        throw new InvalidOperationException("the hub watcher self-check did not finish in 60 s: a refused subscription left the watch silent");
                    }

                    string output = standardOutput.Result + standardError.Result;
                    AssertEqual(0, process.ExitCode, "the hub watcher self-check should pass: " + output);
                    AssertContains("RESULT: PASS", output, "the hub watcher self-check should report its result");
                });
            }
        }

        private static string? FindOnPath(string executable)
        {
            string? path = Environment.GetEnvironmentVariable("PATH");
            if (String.IsNullOrEmpty(path)) return null;
            foreach (string directory in path.Split(Path.PathSeparator))
            {
                if (String.IsNullOrWhiteSpace(directory)) continue;
                string candidate = Path.Combine(directory, executable);
                if (File.Exists(candidate)) return candidate;
            }
            return null;
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
