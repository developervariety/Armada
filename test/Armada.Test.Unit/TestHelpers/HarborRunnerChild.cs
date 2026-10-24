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
            if (String.Equals(args[0], "release", StringComparison.Ordinal))
            {
                if (args.Length != 3) return 64;
                string releasePath = args[1];
                string counterPath = args[2];
                await File.AppendAllTextAsync(counterPath, "started\n", new UTF8Encoding(false)).ConfigureAwait(false);
                await Console.In.ReadToEndAsync().ConfigureAwait(false);
                Console.Out.WriteLine("link-secret-present=" + (Environment.GetEnvironmentVariable("UNRELATED_RUNNER_AUTH") == null ? "False" : "True"));
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
            return 64;
        }

        private static string Encode(string value)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
        }
    }
}
