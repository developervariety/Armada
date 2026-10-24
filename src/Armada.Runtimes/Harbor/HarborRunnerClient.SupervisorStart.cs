namespace Armada.Runtimes.Harbor
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Harbor;
    using Armada.Core.Services;

    /// <summary>Starts one detached supervisor and sends its private launch envelope on stdin.</summary>
    public sealed partial class HarborRunnerClient
    {
        private async Task<Process> StartSupervisorAsync(HarborLaunchRequest request, CancellationToken token)
        {
            HarborSupervisorStart envelope = new HarborSupervisorStart(_Settings.Supervisor, request);
            string json = JsonSerializer.Serialize(envelope);
            if (Encoding.UTF8.GetByteCount(json) > MaximumFrameBytes)
                throw new HarborJobJournalException("supervisor_request_too_large", "The launch request exceeds its size limit.");
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = _SupervisorExecutable,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Environment.CurrentDirectory
            };
            foreach (string argument in _SupervisorArguments) startInfo.ArgumentList.Add(argument);
            startInfo.ArgumentList.Add("--supervise-job");
            ScrubLinkCredentialEnvironment(startInfo);

            Process supervisor = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            if (!supervisor.Start()) throw new InvalidOperationException("harbor_supervisor_start_failed");
            _ = DrainSupervisorStreamAsync(supervisor.StandardOutput);
            _ = DrainSupervisorStreamAsync(supervisor.StandardError);
            try
            {
                try
                {
                    await supervisor.StandardInput.WriteAsync(json.AsMemory(), token).ConfigureAwait(false);
                    await supervisor.StandardInput.FlushAsync(token).ConfigureAwait(false);
                }
                finally { supervisor.StandardInput.Close(); }
            }
            catch
            {
                supervisor.Dispose();
                throw;
            }
            return supervisor;
        }

        private void ScrubLinkCredentialEnvironment(ProcessStartInfo startInfo)
        {
            CaptainEnvironmentScrub.Scrub(startInfo.Environment, null);
            List<string> names = startInfo.Environment.Keys
                .Where(name => name.Contains("HARBOR", StringComparison.OrdinalIgnoreCase)
                    && (name.Contains("CREDENTIAL", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("API_KEY", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("BEARER_TOKEN", StringComparison.OrdinalIgnoreCase)))
                .ToList();
            foreach (string name in names) startInfo.Environment.Remove(name);
            List<string> aliases = startInfo.Environment
                .Where(entry => entry.Value != null && entry.Value.Contains(_Settings.Credential, StringComparison.Ordinal))
                .Select(entry => entry.Key)
                .ToList();
            foreach (string name in aliases) startInfo.Environment.Remove(name);
        }

        private async Task DrainSupervisorStreamAsync(StreamReader reader)
        {
            char[] buffer = new char[4096];
            try
            {
                int count = await reader.ReadAsync(buffer.AsMemory(), CancellationToken.None).ConfigureAwait(false);
                while (count > 0) count = await reader.ReadAsync(buffer.AsMemory(), CancellationToken.None).ConfigureAwait(false);
            }
            catch (IOException) { _Log("Harbor supervisor output drain failed: harbor_supervisor_stream_failed"); }
            catch (ObjectDisposedException) { _Log("Harbor supervisor output drain failed: harbor_supervisor_stream_closed"); }
        }
    }
}
