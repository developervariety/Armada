namespace Armada.Runtimes.Harbor
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>Scrubs child environments and confirms process-tree stops.</summary>
    public sealed partial class HarborRunnerWorkspace
    {
        private static void ScrubProcessEnvironment(ProcessStartInfo start, bool verification)
        {
            HashSet<string> keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "PATH", "SYSTEMROOT", "WINDIR", "COMSPEC", "HOME", "TMP", "TEMP", "TMPDIR", "DOTNET_ROOT"
            };
            Dictionary<string, string> safe = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, string?> entry in start.Environment)
            {
                if (!keep.Contains(entry.Key) || String.IsNullOrEmpty(entry.Value)) continue;
                safe[entry.Key] = entry.Value;
            }
            start.Environment.Clear();
            foreach (KeyValuePair<string, string> entry in safe) start.Environment[entry.Key] = entry.Value;
            if (!start.Environment.ContainsKey("PATH")) start.Environment["PATH"] = String.Empty;
            if (verification) start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        }

        private static void KillProcessTree(Process process)
        {
            if (process.HasExited) return;
            try { process.Kill(true); }
            catch (InvalidOperationException)
            {
                throw Failure("workspace_process_kill_failed", "The local process state changed before the stop request.");
            }
            catch (System.ComponentModel.Win32Exception)
            {
                throw Failure("workspace_process_kill_failed", "The local process could not be stopped.");
            }
        }

        private static async Task ConfirmProcessExitAsync(Process process)
        {
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            catch (TimeoutException) { throw Failure("workspace_process_stop_unconfirmed", "The local process did not stop after a bounded kill request."); }
        }
    }
}
