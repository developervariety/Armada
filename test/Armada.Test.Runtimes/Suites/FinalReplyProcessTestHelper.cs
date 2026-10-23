namespace Armada.Test.Runtimes.Suites
{
    using System.IO;
    using System.Text;
    using Armada.Core.Models;
    using Armada.Runtimes;

    /// <summary>
    /// Starts a runtime against a small executable that writes protocol lines to stdout.
    /// </summary>
    internal static class FinalReplyProcessTestHelper
    {
        /// <summary>
        /// Create a POSIX executable that drains prompt input, writes protocol lines, and exits.
        /// </summary>
        public static string CreateExecutable(string directory, IEnumerable<string> outputLines, int exitCode, IEnumerable<string>? errorLines = null)
        {
            string path = Path.Combine(directory, "agent-stub.sh");
            StringBuilder script = new StringBuilder();
            script.AppendLine("#!/bin/sh");
            script.AppendLine("cat >/dev/null");
            script.AppendLine("cat <<'ARMADA_FINAL_REPLY_EOF'");
            foreach (string line in outputLines) script.AppendLine(line);
            script.AppendLine("ARMADA_FINAL_REPLY_EOF");
            if (errorLines != null)
            {
                script.AppendLine("cat <<'ARMADA_FINAL_REPLY_STDERR_EOF' >&2");
                foreach (string line in errorLines) script.AppendLine(line);
                script.AppendLine("ARMADA_FINAL_REPLY_STDERR_EOF");
            }
            script.AppendLine("exit " + exitCode);
            File.WriteAllText(path, script.ToString(), new UTF8Encoding(false));
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return path;
        }

        /// <summary>
        /// Start one process and wait until BaseAgentRuntime has drained its output and handled exit.
        /// </summary>
        public static async Task<int?> StartAndWaitAsync(BaseAgentRuntime runtime, string workingDirectory, string finalMessageFilePath)
        {
            TaskCompletionSource<int?> exited = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously);
            Action<int, int?> handler = (_, code) => exited.TrySetResult(code);
            runtime.OnProcessExited += handler;
            int processId = 0;
            try
            {
                processId = await runtime.StartAsync(workingDirectory, "final reply test prompt", finalMessageFilePath: finalMessageFilePath).ConfigureAwait(false);
                try
                {
                    return await exited.Task.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                }
                catch (TimeoutException timeout)
                {
                    // Stop through the runtime so cleanup keeps the recorded process-identity guard.
                    AgentStopResult stopped = await runtime.StopAsync(processId)
                        .WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                    if (stopped.IsRefused)
                        throw new InvalidOperationException("The stub process stop was refused: " + stopped, timeout);
                    await exited.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                    throw new TimeoutException("The runtime did not finish handling the stub process exit within 30 seconds.", timeout);
                }
            }
            finally
            {
                runtime.OnProcessExited -= handler;
            }
        }
    }
}
