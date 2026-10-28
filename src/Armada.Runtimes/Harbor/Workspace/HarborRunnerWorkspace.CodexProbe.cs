namespace Armada.Runtimes.Harbor
{
    using System;
    using System.Collections.Generic;
    using System.ComponentModel;
    using System.Diagnostics;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Harbor;

    /// <summary>Runs one allowlisted Codex inventory command on this runner host.</summary>
    public sealed partial class HarborRunnerWorkspace
    {
        private async Task ProbeCodexInventoryAsync(
            HarborWorkspaceOperationRequest request,
            HarborWorkspaceOperationResult result,
            CancellationToken token)
        {
            if (!CodexInventoryCommand.TryParse(request.InventoryCommand, out IReadOnlyList<string> arguments))
                throw Failure("workspace_codex_probe_arguments_invalid", "The Codex inventory command is not allowlisted.");

            ProcessStartInfo start = new ProcessStartInfo
            {
                FileName = CodexInventoryCommand.ExecutableName(),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false)
            };
            foreach (string argument in arguments) start.ArgumentList.Add(argument);
            ScrubProcessEnvironment(start, false);

            int limit = Math.Clamp(request.MaxBytes, 1, 1024 * 1024);
            TimeSpan timeout = TimeSpan.FromSeconds(Math.Clamp(request.TimeoutSeconds, 1, 30));
            HarborWorkspaceProcessResult process;
            try
            {
                process = await RunProcessAsync(start, timeout, limit, token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is InvalidOperationException || exception is Win32Exception)
            {
                result.Accepted = false;
                result.Reason = "codex_probe_start_failed";
                return;
            }

            result.StandardOutput = process.StandardOutput;
            result.StandardError = process.StandardError;
            result.ExitCode = process.ExitCode;
            result.Accepted = process.ExitCode.HasValue;
            if (!result.Accepted) result.Reason = "codex_probe_incomplete";
        }
    }
}
