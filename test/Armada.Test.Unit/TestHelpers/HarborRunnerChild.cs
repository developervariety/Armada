namespace Armada.Test.Unit.TestHelpers
{
    using System;
    using System.Diagnostics;
    using System.IO;
    using System.Text;
    using System.Threading.Tasks;

    /// <summary>Small real child process used by Harbor supervisor component tests.</summary>
    internal static class HarborRunnerChild
    {
        /// <summary>Run one explicitly selected child behavior before the unit runner starts.</summary>
        /// <param name="args">Arguments after the child-mode marker.</param>
        /// <returns>Child exit code.</returns>
        internal static async Task<int> RunAsync(string[] args)
        {
            if (args == null || args.Length == 0) return 64;
            if (String.Equals(args[0], "inspect", StringComparison.Ordinal))
            {
                for (int index = 1; index < args.Length; index++)
                    Console.Out.WriteLine("ARG:" + Encode(args[index]));
                Console.Out.WriteLine("CWD:" + Encode(Environment.CurrentDirectory));
                Console.Out.WriteLine("ENV:" + Encode(Environment.GetEnvironmentVariable("MAX_THINKING_TOKENS") ?? String.Empty));
                string prompt = await Console.In.ReadToEndAsync().ConfigureAwait(false);
                Console.Out.WriteLine("PROMPT:" + Encode(prompt));
                Console.Out.Write(new string('a', 4095) + "😀tail\n");
                Console.Out.Flush();
                Console.Error.WriteLine("stderr-marker");
                Console.Error.Flush();
                return 23;
            }
            if (String.Equals(args[0], "hold", StringComparison.Ordinal))
            {
                Console.Out.WriteLine("child-ready");
                Console.Out.Flush();
                Console.Error.WriteLine("child-stderr-ready");
                Console.Error.Flush();
                await Task.Delay(TimeSpan.FromMinutes(5)).ConfigureAwait(false);
                return 0;
            }
            if (String.Equals(args[0], "large", StringComparison.Ordinal))
            {
                Console.Out.Write(new string('x', 4096));
                Console.Out.Flush();
                return 0;
            }
            if (String.Equals(args[0], "verify-large", StringComparison.Ordinal))
            {
                if (args.Length != 2 || !Int32.TryParse(args[1], out int count) || count < 1 || count > 8 * 1024 * 1024) return 64;
                Console.Out.Write(new string('x', count));
                Console.Out.Flush();
                Console.Error.Write(new string('e', count));
                Console.Error.Flush();
                return 0;
            }
            if (String.Equals(args[0], "verify-hold", StringComparison.Ordinal))
            {
                await File.WriteAllTextAsync(Path.Combine(Environment.CurrentDirectory, ".harbor-verification-ready"), "ready").ConfigureAwait(false);
                Console.Out.WriteLine("verification-ready");
                Console.Out.Flush();
                await Task.Delay(TimeSpan.FromMinutes(5)).ConfigureAwait(false);
                return 0;
            }
            if (String.Equals(args[0], "verify-fail", StringComparison.Ordinal))
            {
                Console.Out.WriteLine("  Failed Namespace.TestCase [1 ms]");
                Console.Out.WriteLine("Failed!  - Failed: 1, Passed: 0, Total: 1");
                Console.Out.Flush();
                return 1;
            }
            if (String.Equals(args[0], "verify-rerun", StringComparison.Ordinal))
            {
                Console.Out.WriteLine(Environment.GetEnvironmentVariable("ARMADA_RERUN_TESTS") ?? "missing-rerun-selection");
                Console.Out.Flush();
                return 0;
            }
            if (String.Equals(args[0], "release", StringComparison.Ordinal))
            {
                if (args.Length != 3 && args.Length != 4) return 64;
                string releasePath = args[1];
                string counterPath = args[2];
                await File.AppendAllTextAsync(counterPath, "started\n", new UTF8Encoding(false)).ConfigureAwait(false);
                await Console.In.ReadToEndAsync().ConfigureAwait(false);
                Console.Out.WriteLine("link-secret-present=" + (Environment.GetEnvironmentVariable("UNRELATED_RUNNER_AUTH") == null ? "False" : "True"));
                if (args.Length == 4) WriteMissionEnvironmentMarkers(args[3]);
                Console.Out.WriteLine("before offline");
                Console.Out.Flush();
                Stopwatch timer = Stopwatch.StartNew();
                while (!File.Exists(releasePath) && timer.Elapsed < TimeSpan.FromSeconds(30))
                    await Task.Delay(TimeSpan.FromMilliseconds(50)).ConfigureAwait(false);
                if (!File.Exists(releasePath)) return 75;
                Console.Out.WriteLine("offline output");
                Console.Out.Flush();
                return 0;
            }
            if (String.Equals(args[0], "owned-commit", StringComparison.Ordinal))
            {
                if (args.Length != 5) return 64;
                string releasePath = args[1];
                string counterPath = args[2];
                string briefName = args[3];
                await File.AppendAllTextAsync(counterPath, "started\n", new UTF8Encoding(false)).ConfigureAwait(false);
                await Console.In.ReadToEndAsync().ConfigureAwait(false);
                Console.Out.WriteLine("link-secret-present=" + (Environment.GetEnvironmentVariable("UNRELATED_RUNNER_AUTH") == null ? "False" : "True"));
                Console.Out.WriteLine("mission-token-present=" + (!String.IsNullOrEmpty(Environment.GetEnvironmentVariable("ARMADA_MCP_TOKEN"))));
                Console.Out.WriteLine("brief-present=" + (File.Exists(Path.Combine(Environment.CurrentDirectory, briefName))));
                WriteMissionEnvironmentMarkers(args[4]);
                Console.Out.WriteLine("before offline");
                Console.Out.Flush();
                Stopwatch timer = Stopwatch.StartNew();
                while (!File.Exists(releasePath) && timer.Elapsed < TimeSpan.FromSeconds(30))
                    await Task.Delay(TimeSpan.FromMilliseconds(50)).ConfigureAwait(false);
                if (!File.Exists(releasePath)) return 75;

                string brief = await File.ReadAllTextAsync(Path.Combine(Environment.CurrentDirectory, briefName)).ConfigureAwait(false);
                await File.WriteAllTextAsync("owned-result.txt", "brief-has-mission=" + brief.Contains("Mission Instructions", StringComparison.Ordinal), new UTF8Encoding(false)).ConfigureAwait(false);
                if (await RunGitAsync("add", "owned-result.txt").ConfigureAwait(false) != 0) return 76;
                if (await RunGitAsync("commit", "-m", "Add Harbor-owned mission result").ConfigureAwait(false) != 0) return 77;
                Console.Out.WriteLine("offline output");
                Console.Out.WriteLine("owned-change-committed");
                Console.Out.Flush();
                return 0;
            }
            return 64;
        }

        private static void WriteMissionEnvironmentMarkers(string expectedDirectory)
        {
            string? home = Environment.GetEnvironmentVariable("HOME");
            string? userProfile = Environment.GetEnvironmentVariable("USERPROFILE");
            string? openCode = Environment.GetEnvironmentVariable("OPENCODE_CONFIG");
            Console.Out.WriteLine("mission-token-present=" + (!String.IsNullOrEmpty(Environment.GetEnvironmentVariable("ARMADA_MCP_TOKEN"))));
            Console.Out.WriteLine("scoped-home=" + (SamePath(home, expectedDirectory) && SamePath(userProfile, expectedDirectory)));
            Console.Out.WriteLine("opencode-config=" + SamePath(openCode, Path.Combine(expectedDirectory, "opencode.json")));
        }

        private static bool SamePath(string? first, string second)
        {
            if (String.IsNullOrWhiteSpace(first)) return false;
            StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return String.Equals(Path.GetFullPath(first), Path.GetFullPath(second), comparison);
        }

        private static async Task<int> RunGitAsync(params string[] arguments)
        {
            ProcessStartInfo start = new ProcessStartInfo("git") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string argument in arguments) start.ArgumentList.Add(argument);
            using (Process process = Process.Start(start) ?? throw new InvalidOperationException("The fixture Git process did not start."))
            {
                await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
                await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
                await process.WaitForExitAsync().ConfigureAwait(false);
                return process.ExitCode;
            }
        }

        private static string Encode(string value)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
        }
    }
}
