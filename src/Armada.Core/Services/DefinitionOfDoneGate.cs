namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using System.Text;
    using System.Text.RegularExpressions;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>
    /// Evaluates whether a Worker mission's in-dock build and unit-test commands pass before
    /// the mission is accepted as complete. Missions that carry the configured doc-only opt-out
    /// marker are skipped without running any commands. Non-Worker personas are also skipped
    /// unless the settings explicitly list them under AppliedPersonas.
    /// </summary>
    public class DefinitionOfDoneGate
    {
        #region Private-Members

        private readonly string _Header = "[DefinitionOfDoneGate] ";
        private readonly DefinitionOfDoneSettings _Settings;
        private readonly DatabaseDriver _Database;
        private readonly LoggingModule _Logging;

        private static readonly Regex _SecretLikePattern = new Regex(
            @"(?:password|passwd|secret|token|key|credential|auth|api_key|apikey|access_key|private_key)\s*[=:]\s*\S+",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate with required dependencies.
        /// </summary>
        /// <param name="settings">Gate configuration.</param>
        /// <param name="database">Database driver for resolving vessel and workflow profile data.</param>
        /// <param name="logging">Logging module.</param>
        public DefinitionOfDoneGate(
            DefinitionOfDoneSettings settings,
            DatabaseDriver database,
            LoggingModule logging)
        {
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Evaluate the definition-of-done gate for the specified mission and dock.
        /// Returns a skipped result when the gate does not apply; returns a passing result
        /// when all required commands succeed; returns a failing result with the command
        /// label, exit code, and output tail when any command fails.
        /// </summary>
        /// <param name="mission">The mission being completed.</param>
        /// <param name="dock">The captain's dock, used to locate the worktree.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>A <see cref="DefinitionOfDoneResult"/> describing the gate outcome.</returns>
        public async Task<DefinitionOfDoneResult> EvaluateAsync(
            Mission mission,
            Dock dock,
            CancellationToken token = default)
        {
            if (mission == null) throw new ArgumentNullException(nameof(mission));
            if (dock == null) throw new ArgumentNullException(nameof(dock));

            if (!_Settings.Enabled)
                return DefinitionOfDoneResult.Skipped("DoD gate is disabled");

            if (!IsPersonaApplicable(mission.Persona))
                return DefinitionOfDoneResult.Skipped("persona '" + (mission.Persona ?? "(none)") + "' is not in AppliedPersonas");

            if (HasDocOnlyMarker(mission.Description))
                return DefinitionOfDoneResult.Skipped("mission description contains doc-only opt-out marker");

            string? worktreePath = dock.WorktreePath;
            if (String.IsNullOrWhiteSpace(worktreePath))
                return DefinitionOfDoneResult.Fail("dock-setup", -1, "Dock has no WorktreePath; cannot run in-dock checks.");

            WorkflowProfile? profile = await ResolveProfileAsync(mission, token).ConfigureAwait(false);
            string? buildCommand = profile?.BuildCommand;
            string? testCommand = profile?.UnitTestCommand;

            if (String.IsNullOrWhiteSpace(buildCommand) && String.IsNullOrWhiteSpace(testCommand))
            {
                return DefinitionOfDoneResult.Fail(
                    "missing-commands",
                    -1,
                    "No BuildCommand or UnitTestCommand is configured on the vessel's workflow profile. " +
                    "Add a workflow profile for this vessel, or add '" + _Settings.DocOnlyMarker +
                    "' to the mission description to opt out of in-dock verification.");
            }

            if (_Settings.RunRestoreBeforeBuild && !String.IsNullOrWhiteSpace(_Settings.RestoreCommand))
            {
                DefinitionOfDoneResult restoreResult = await RunCommandAsync("restore", _Settings.RestoreCommand, worktreePath, token).ConfigureAwait(false);
                if (!restoreResult.Passed) return restoreResult;
            }

            if (!String.IsNullOrWhiteSpace(buildCommand))
            {
                DefinitionOfDoneResult buildResult = await RunCommandAsync("build", buildCommand, worktreePath, token).ConfigureAwait(false);
                if (!buildResult.Passed)
                    return buildResult;
            }

            if (!String.IsNullOrWhiteSpace(testCommand))
            {
                DefinitionOfDoneResult testResult = await RunCommandAsync("unit-test", testCommand, worktreePath, token).ConfigureAwait(false);
                if (!testResult.Passed)
                    return testResult;
            }

            return DefinitionOfDoneResult.Pass();
        }

        #endregion

        #region Private-Methods

        private bool IsPersonaApplicable(string? persona)
        {
            if (_Settings.AppliedPersonas == null || _Settings.AppliedPersonas.Count == 0)
                return false;
            return _Settings.AppliedPersonas.Exists(p =>
                String.Equals(p, persona, StringComparison.OrdinalIgnoreCase));
        }

        private bool HasDocOnlyMarker(string? description)
        {
            if (String.IsNullOrWhiteSpace(description)) return false;
            if (String.IsNullOrWhiteSpace(_Settings.DocOnlyMarker)) return false;
            return description.Contains(_Settings.DocOnlyMarker, StringComparison.OrdinalIgnoreCase);
        }

        private async Task<WorkflowProfile?> ResolveProfileAsync(Mission mission, CancellationToken token)
        {
            string? vesselId = mission.VesselId;
            if (String.IsNullOrWhiteSpace(vesselId)) return null;

            Vessel? vessel = !String.IsNullOrWhiteSpace(mission.TenantId)
                ? await _Database.Vessels.ReadAsync(mission.TenantId, vesselId, token).ConfigureAwait(false)
                : await _Database.Vessels.ReadAsync(vesselId, token).ConfigureAwait(false);

            if (vessel == null) return null;

            WorkflowProfileQuery query = new WorkflowProfileQuery
            {
                TenantId = mission.TenantId,
                Active = true,
                PageNumber = 1,
                PageSize = 1000
            };

            List<WorkflowProfile> candidates = await _Database.WorkflowProfiles.EnumerateAllAsync(query, token).ConfigureAwait(false);
            if (candidates.Count == 0) return null;

            WorkflowProfile? match = ChooseBestFromScope(
                candidates.Where(p => p.Scope == WorkflowProfileScopeEnum.Vessel
                    && String.Equals(p.VesselId, vesselId, StringComparison.Ordinal)).ToList());
            if (match != null) return match;

            if (!String.IsNullOrWhiteSpace(vessel.FleetId))
            {
                match = ChooseBestFromScope(
                    candidates.Where(p => p.Scope == WorkflowProfileScopeEnum.Fleet
                        && String.Equals(p.FleetId, vessel.FleetId, StringComparison.Ordinal)).ToList());
                if (match != null) return match;
            }

            return ChooseBestFromScope(candidates.Where(p => p.Scope == WorkflowProfileScopeEnum.Global).ToList());
        }

        private WorkflowProfile? ChooseBestFromScope(List<WorkflowProfile> candidates)
        {
            if (candidates.Count == 0) return null;
            WorkflowProfile? defaultProfile = candidates.FirstOrDefault(p => p.IsDefault);
            return defaultProfile ?? candidates[0];
        }

        private async Task<DefinitionOfDoneResult> RunCommandAsync(
            string label,
            string command,
            string workingDir,
            CancellationToken token)
        {
            _Logging.Info(_Header + "running " + label + " command in " + workingDir + ": " + command);

            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = GetShell(),
                Arguments = GetShellArgs(command),
                WorkingDirectory = workingDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using CancellationTokenSource timeoutCts = new CancellationTokenSource(
                TimeSpan.FromSeconds(_Settings.CommandTimeoutSeconds));
            using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token, timeoutCts.Token);

            // Declare outside the try block so the catch block can kill the process on timeout.
            using Process process = new Process { StartInfo = startInfo };
            try
            {
                process.Start();

                // Read both streams concurrently with the linked token so a hanging process
                // is interrupted when the timeout fires, and a full stderr pipe cannot
                // deadlock the stdout read.
                Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(linkedCts.Token);
                Task<string> stderrTask = process.StandardError.ReadToEndAsync(linkedCts.Token);
                await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);

                await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);

                string combined = stdoutTask.Result;
                if (!String.IsNullOrEmpty(stderrTask.Result))
                    combined += "\n--- STDERR ---\n" + stderrTask.Result;

                int exitCode = process.ExitCode;
                _Logging.Info(_Header + label + " command exited " + exitCode + " for mission in " + workingDir);

                if (exitCode == 0)
                    return DefinitionOfDoneResult.Pass();

                return DefinitionOfDoneResult.Fail(label, exitCode, TailAndRedact(combined));
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                TryKillProcess(process);
                string message = label + " command timed out after " + _Settings.CommandTimeoutSeconds + " seconds.";
                _Logging.Warn(_Header + message + " workingDir=" + workingDir);
                return DefinitionOfDoneResult.Fail(label, -1, message);
            }
        }

        private void TryKillProcess(Process process)
        {
            try
            {
                process.Kill(true);
            }
            catch (Exception killEx)
            {
                _Logging.Warn(_Header + "could not kill timed-out process: " + killEx.Message);
            }
        }

        private string TailAndRedact(string output)
        {
            if (String.IsNullOrEmpty(output)) return "";

            string[] lines = output.Split('\n');
            int tailCount = Math.Min(lines.Length, _Settings.OutputTailLines);
            int startIndex = lines.Length - tailCount;

            StringBuilder sb = new StringBuilder();
            for (int i = startIndex; i < lines.Length; i++)
            {
                string redacted = _SecretLikePattern.Replace(lines[i], m =>
                {
                    int eqIdx = m.Value.IndexOfAny(new char[] { '=', ':' });
                    if (eqIdx < 0) return m.Value;
                    return m.Value.Substring(0, eqIdx + 1) + " [REDACTED]";
                });
                sb.AppendLine(redacted);
            }

            return sb.ToString().TrimEnd();
        }

        private string GetShell()
        {
            if (OperatingSystem.IsWindows()) return "cmd.exe";
            return "/bin/sh";
        }

        private string GetShellArgs(string command)
        {
            if (OperatingSystem.IsWindows()) return "/c " + command;
            return "-c \"" + command.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        #endregion
    }
}
