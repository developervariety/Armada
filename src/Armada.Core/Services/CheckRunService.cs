namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using SyslogLogging;

    /// <summary>
    /// Executes structured check runs using workflow profiles and persists the results.
    /// </summary>
    public class CheckRunService
    {
        private readonly string _Header = "[CheckRunService] ";
        private readonly DatabaseDriver _Database;
        private readonly WorkflowProfileService _WorkflowProfiles;
        private readonly LoggingModule _Logging;
        private readonly TimeSpan _DefaultTimeout = TimeSpan.FromMinutes(30);

        /// <summary>
        /// Instantiate.
        /// </summary>
        public CheckRunService(DatabaseDriver database, WorkflowProfileService workflowProfiles, LoggingModule logging)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _WorkflowProfiles = workflowProfiles ?? throw new ArgumentNullException(nameof(workflowProfiles));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
        }

        /// <summary>
        /// Execute a check run synchronously and persist the result.
        /// </summary>
        public async Task<CheckRun> RunAsync(AuthContext auth, CheckRunRequest request, CancellationToken token = default)
        {
            if (auth == null) throw new ArgumentNullException(nameof(auth));
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (String.IsNullOrWhiteSpace(request.VesselId)) throw new ArgumentNullException(nameof(request.VesselId));

            Vessel vessel = await ReadAccessibleVesselAsync(auth, request.VesselId, token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Vessel not found or not accessible.");

            if (String.IsNullOrWhiteSpace(vessel.WorkingDirectory) || !Directory.Exists(vessel.WorkingDirectory))
                throw new InvalidOperationException("This vessel does not have a usable working directory.");

            WorkflowProfile? profile = await _WorkflowProfiles.ResolveForVesselAsync(auth, vessel, request.WorkflowProfileId, token).ConfigureAwait(false);
            if (profile == null && String.IsNullOrWhiteSpace(request.CommandOverride))
                throw new InvalidOperationException("No active workflow profile could be resolved for this vessel.");

            string command = !String.IsNullOrWhiteSpace(request.CommandOverride)
                ? request.CommandOverride!.Trim()
                : _WorkflowProfiles.ResolveCommand(profile!, request.Type, request.EnvironmentName)
                    ?? throw new InvalidOperationException("No command is configured for " + request.Type + ".");

            CheckRun run = new CheckRun
            {
                TenantId = vessel.TenantId,
                UserId = auth.UserId,
                WorkflowProfileId = profile?.Id,
                VesselId = vessel.Id,
                MissionId = request.MissionId,
                VoyageId = request.VoyageId,
                Label = request.Label,
                Type = request.Type,
                Status = CheckRunStatusEnum.Running,
                EnvironmentName = request.EnvironmentName,
                Command = command,
                WorkingDirectory = vessel.WorkingDirectory,
                BranchName = request.BranchName,
                CommitHash = request.CommitHash,
                StartedUtc = DateTime.UtcNow,
                CreatedUtc = DateTime.UtcNow,
                LastUpdateUtc = DateTime.UtcNow
            };

            run = await _Database.CheckRuns.CreateAsync(run, token).ConfigureAwait(false);

            Stopwatch sw = Stopwatch.StartNew();
            CommandExecutionResult execution;

            try
            {
                execution = await ExecuteCommandAsync(run.Command, run.WorkingDirectory!, _DefaultTimeout, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                execution = new CommandExecutionResult
                {
                    ExitCode = -1,
                    Output = ex.Message
                };
            }

            sw.Stop();

            run.ExitCode = execution.ExitCode;
            run.Output = execution.Output;
            run.DurationMs = Convert.ToInt64(Math.Round(sw.Elapsed.TotalMilliseconds));
            run.CompletedUtc = DateTime.UtcNow;
            run.LastUpdateUtc = DateTime.UtcNow;
            run.Status = execution.ExitCode == 0 ? CheckRunStatusEnum.Passed : CheckRunStatusEnum.Failed;
            run.Artifacts = CollectArtifacts(run.WorkingDirectory!, profile?.ExpectedArtifacts);
            run.Summary = BuildSummary(run, profile);

            run = await _Database.CheckRuns.UpdateAsync(run, token).ConfigureAwait(false);
            return run;
        }

        /// <summary>
        /// Retry a previously completed check run.
        /// </summary>
        public async Task<CheckRun> RetryAsync(AuthContext auth, string id, CancellationToken token = default)
        {
            if (auth == null) throw new ArgumentNullException(nameof(auth));
            if (String.IsNullOrWhiteSpace(id)) throw new ArgumentNullException(nameof(id));

            CheckRunQuery scope = BuildScopeQuery(auth);
            CheckRun? prior = await _Database.CheckRuns.ReadAsync(id, scope, token).ConfigureAwait(false);
            if (prior == null) throw new InvalidOperationException("Check run not found.");

            return await RunAsync(auth, new CheckRunRequest
            {
                VesselId = prior.VesselId ?? String.Empty,
                WorkflowProfileId = prior.WorkflowProfileId,
                MissionId = prior.MissionId,
                VoyageId = prior.VoyageId,
                Type = prior.Type,
                EnvironmentName = prior.EnvironmentName,
                Label = prior.Label,
                BranchName = prior.BranchName,
                CommitHash = prior.CommitHash,
                CommandOverride = prior.Command
            }, token).ConfigureAwait(false);
        }

        private CheckRunQuery BuildScopeQuery(AuthContext auth)
        {
            return new CheckRunQuery
            {
                TenantId = auth.IsAdmin ? null : auth.TenantId,
                UserId = auth.IsAdmin || auth.IsTenantAdmin ? null : auth.UserId
            };
        }

        private async Task<Vessel?> ReadAccessibleVesselAsync(AuthContext auth, string vesselId, CancellationToken token)
        {
            if (auth.IsAdmin)
                return await _Database.Vessels.ReadAsync(vesselId, token).ConfigureAwait(false);
            if (auth.IsTenantAdmin)
                return await _Database.Vessels.ReadAsync(auth.TenantId!, vesselId, token).ConfigureAwait(false);
            return await _Database.Vessels.ReadAsync(auth.TenantId!, auth.UserId!, vesselId, token).ConfigureAwait(false);
        }

        private async Task<CommandExecutionResult> ExecuteCommandAsync(string command, string workingDirectory, TimeSpan timeout, CancellationToken token)
        {
            bool isWindows = OperatingSystem.IsWindows();

            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = isWindows ? "cmd.exe" : "/bin/sh",
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            if (isWindows)
            {
                startInfo.ArgumentList.Add("/c");
                startInfo.ArgumentList.Add(command);
            }
            else
            {
                startInfo.ArgumentList.Add("-lc");
                startInfo.ArgumentList.Add(command);
            }

            using Process process = new Process
            {
                StartInfo = startInfo
            };

            if (!process.Start())
                throw new InvalidOperationException("Failed to start check command.");

            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
            Task<string> stderrTask = process.StandardError.ReadToEndAsync();

            using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeoutCts.CancelAfter(timeout);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                try
                {
                    if (!process.HasExited)
                        process.Kill(true);
                }
                catch
                {
                }

                throw new TimeoutException("Check command timed out after " + timeout.TotalMinutes.ToString("0") + " minutes.");
            }

            string stdout = await stdoutTask.ConfigureAwait(false);
            string stderr = await stderrTask.ConfigureAwait(false);
            string output = CombineOutput(stdout, stderr);

            _Logging.Debug(_Header + "command exited with code " + process.ExitCode + ": " + FirstNonEmptyLine(stderr, stdout));

            return new CommandExecutionResult
            {
                ExitCode = process.ExitCode,
                Output = output
            };
        }

        private static string CombineOutput(string stdout, string stderr)
        {
            string trimmedStdout = stdout?.Trim() ?? String.Empty;
            string trimmedStderr = stderr?.Trim() ?? String.Empty;

            if (String.IsNullOrWhiteSpace(trimmedStderr)) return trimmedStdout;
            if (String.IsNullOrWhiteSpace(trimmedStdout)) return trimmedStderr;
            return trimmedStdout + Environment.NewLine + Environment.NewLine + "--- STDERR ---" + Environment.NewLine + trimmedStderr;
        }

        private static string FirstNonEmptyLine(string? primary, string? secondary)
        {
            foreach (string source in new[] { primary ?? String.Empty, secondary ?? String.Empty })
            {
                foreach (string line in source.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string trimmed = line.Trim();
                    if (!String.IsNullOrWhiteSpace(trimmed))
                        return trimmed;
                }
            }

            return String.Empty;
        }

        private static List<CheckRunArtifact> CollectArtifacts(string workingDirectory, List<string>? expectedArtifacts)
        {
            List<CheckRunArtifact> results = new List<CheckRunArtifact>();
            if (String.IsNullOrWhiteSpace(workingDirectory) || expectedArtifacts == null) return results;

            string root = Path.GetFullPath(workingDirectory);
            foreach (string relativePath in expectedArtifacts.Where(path => !String.IsNullOrWhiteSpace(path)))
            {
                try
                {
                    string fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
                    if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!File.Exists(fullPath)) continue;

                    FileInfo info = new FileInfo(fullPath);
                    results.Add(new CheckRunArtifact
                    {
                        Path = relativePath.Replace('\\', '/'),
                        SizeBytes = info.Length,
                        LastWriteUtc = info.LastWriteTimeUtc
                    });
                }
                catch
                {
                }
            }

            return results;
        }

        private static string BuildSummary(CheckRun run, WorkflowProfile? profile)
        {
            string label = !String.IsNullOrWhiteSpace(run.Label)
                ? run.Label!
                : run.Type.ToString();

            if (run.Status == CheckRunStatusEnum.Passed)
            {
                if (run.Artifacts.Count > 0)
                    return label + " passed and collected " + run.Artifacts.Count + " artifact(s).";
                return label + " passed.";
            }

            string details = FirstNonEmptyLine(run.Output, null);
            if (String.IsNullOrWhiteSpace(details))
                details = "Exit code " + (run.ExitCode?.ToString() ?? "unknown");

            return label + " failed. " + details;
        }

        private sealed class CommandExecutionResult
        {
            public int ExitCode { get; set; } = -1;
            public string Output { get; set; } = String.Empty;
        }
    }
}
