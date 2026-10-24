namespace Armada.Runtimes.Harbor
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Services;

    /// <summary>Starts configured Git and verification commands.</summary>
    public sealed partial class HarborRunnerWorkspace
    {
        private static async Task<HarborWorkspaceProcessResult> RunGitAsync(
            string workingDirectory,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            int outputLimit,
            CancellationToken token)
        {
            ProcessStartInfo start = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false)
            };
            foreach (string argument in arguments) start.ArgumentList.Add(argument);
            ScrubProcessEnvironment(start, false);
            start.Environment["GIT_TERMINAL_PROMPT"] = "0";
            return await RunProcessAsync(start, timeout, outputLimit, token).ConfigureAwait(false);
        }

        private static async Task<HarborWorkspaceProcessResult> RunVerificationProcessAsync(
            string workingDirectory,
            string command,
            TimeSpan timeout,
            int outputLimit,
            IReadOnlyDictionary<string, string> extraEnvironment,
            CancellationToken token)
        {
            ProcessStartInfo start;
            if (OperatingSystem.IsWindows())
            {
                string shell = Environment.GetEnvironmentVariable("ComSpec") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
                start = new ProcessStartInfo(shell);
                start.ArgumentList.Add("/d");
                start.ArgumentList.Add("/s");
                start.ArgumentList.Add("/c");
                start.ArgumentList.Add(command);
            }
            else
            {
                start = new ProcessStartInfo("/bin/sh");
                start.ArgumentList.Add("-c");
                start.ArgumentList.Add(command);
            }
            start.WorkingDirectory = workingDirectory;
            start.UseShellExecute = false;
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
            start.CreateNoWindow = true;
            start.StandardOutputEncoding = new UTF8Encoding(false);
            start.StandardErrorEncoding = new UTF8Encoding(false);
            ScrubProcessEnvironment(start, true);
            foreach (KeyValuePair<string, string> entry in extraEnvironment) start.Environment[entry.Key] = entry.Value;
            return await RunProcessAsync(start, timeout, outputLimit, token).ConfigureAwait(false);
        }
    }
}
