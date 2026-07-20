namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
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
        private readonly DefinitionOfDoneFailureClassifier _FailureClassifier = new DefinitionOfDoneFailureClassifier();

        private const int _MAX_DIAGNOSTIC_TEXT_CHARS = 16000;
        private const int _MAX_SECTION_CHARS = 7800;
        private const int _MAX_LINE_CHARS = 2000;

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
            {
                string diagnostic = BuildDiagnosticText("Dock has no WorktreePath; cannot run in-dock checks.");
                return DefinitionOfDoneResult.Fail(
                    "dock-setup",
                    -1,
                    diagnostic,
                    DefinitionOfDoneFailureClassEnum.Infra);
            }

            WorkflowProfile? profile = await ResolveProfileAsync(mission, token).ConfigureAwait(false);
            string? buildCommand = profile?.BuildCommand;
            string? testCommand = profile?.UnitTestCommand;

            if (String.IsNullOrWhiteSpace(buildCommand) && String.IsNullOrWhiteSpace(testCommand))
            {
                return DefinitionOfDoneResult.Fail(
                    "missing-commands",
                    -1,
                    BuildDiagnosticText("No BuildCommand or UnitTestCommand is configured on the vessel's workflow profile. " +
                    "Add a workflow profile for this vessel, or add '" + _Settings.DocOnlyMarker +
                    "' to the mission description to opt out of in-dock verification."),
                    DefinitionOfDoneFailureClassEnum.Infra);
            }

            if (!String.IsNullOrWhiteSpace(buildCommand))
            {
                string effectiveBuild = _Settings.RunRestoreBeforeBuild ? EnsureRestore(buildCommand) : buildCommand;
                DefinitionOfDoneResult buildResult = await RunCommandAsync("build", effectiveBuild, worktreePath, token).ConfigureAwait(false);
                if (!buildResult.Passed)
                    return buildResult;
            }

            if (!String.IsNullOrWhiteSpace(testCommand))
            {
                string effectiveTest = _Settings.RunRestoreBeforeBuild ? EnsureRestore(testCommand) : testCommand;
                DefinitionOfDoneResult testResult = await RunCommandAsync("unit-test", effectiveTest, worktreePath, token).ConfigureAwait(false);
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
            _Logging.Info(_Header + "running " + label + " command");

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

            Task<string>? stdoutTask = null;
            Task<string>? stderrTask = null;

            // Declare outside the try block so catch blocks can kill the process.
            using Process process = new Process { StartInfo = startInfo };
            try
            {
                if (!process.Start())
                    throw new InvalidOperationException("The command process did not start.");

                // Read both streams concurrently with the linked token so a hanging process
                // cannot fill either redirected pipe while the process is running.
                stdoutTask = process.StandardOutput.ReadToEndAsync();
                stderrTask = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
                await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);

                string combined = CombineOutput(stdoutTask.Result, stderrTask.Result);

                int exitCode = process.ExitCode;
                _Logging.Info(_Header + label + " command exited " + exitCode);

                if (exitCode == 0)
                    return DefinitionOfDoneResult.Pass();

                DefinitionOfDoneFailureClassEnum failureClass = _FailureClassifier.Classify(
                    label,
                    exitCode,
                    combined);
                return DefinitionOfDoneResult.Fail(
                    label,
                    exitCode,
                    BuildDiagnosticText(combined),
                    failureClass);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                TryKillProcess(process);
                throw;
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                TryKillProcess(process);
                string message = label + " command timed out after " + _Settings.CommandTimeoutSeconds + " seconds.";
                string partialOutput = await CaptureOutputAsync(stdoutTask, stderrTask).ConfigureAwait(false);
                string combined = String.IsNullOrWhiteSpace(partialOutput) ? message : message + "\n" + partialOutput;
                _Logging.Warn(_Header + message);
                return DefinitionOfDoneResult.Fail(
                    label,
                    -1,
                    BuildDiagnosticText(combined),
                    _FailureClassifier.Classify(label, -1, combined, true));
            }
            catch (Exception ex)
            {
                TryKillProcess(process);
                string partialOutput = await CaptureOutputAsync(stdoutTask, stderrTask).ConfigureAwait(false);
                string message = label + " command could not be started or completed: " + ex.Message;
                string combined = String.IsNullOrWhiteSpace(partialOutput) ? message : message + "\n" + partialOutput;
                _Logging.Warn(_Header + label + " command infrastructure failure");
                return DefinitionOfDoneResult.Fail(
                    label,
                    -1,
                    BuildDiagnosticText(combined),
                    DefinitionOfDoneFailureClassEnum.Infra);
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
                _Logging.Warn(_Header + "could not kill command process exceptionType=" + killEx.GetType().Name);
            }
        }

        private static string CombineOutput(string? stdout, string? stderr)
        {
            string combined = stdout ?? String.Empty;
            if (!String.IsNullOrEmpty(stderr))
                combined += "\n--- STDERR ---\n" + stderr;
            return combined;
        }

        private static async Task<string> CaptureOutputAsync(
            Task<string>? stdoutTask,
            Task<string>? stderrTask)
        {
            try
            {
                string stdout = stdoutTask == null ? String.Empty : await stdoutTask.ConfigureAwait(false);
                string stderr = stderrTask == null ? String.Empty : await stderrTask.ConfigureAwait(false);
                return CombineOutput(stdout, stderr);
            }
            catch
            {
                return String.Empty;
            }
        }

        private string BuildDiagnosticText(string output)
        {
            string[] lines = (output ?? String.Empty).Split('\n');
            HashSet<string> retained = new HashSet<string>(StringComparer.Ordinal);
            List<string> diagnosticLines = new List<string>();

            foreach (string line in lines)
            {
                if (diagnosticLines.Count >= _Settings.DiagnosticLines)
                    break;
                if (!DefinitionOfDoneFailureClassifier.IsActionableDiagnosticLine(line))
                    continue;

                string redacted = RedactAndBoundLine(line);
                if (!String.IsNullOrWhiteSpace(redacted) && retained.Add(redacted))
                    diagnosticLines.Add(redacted);
            }

            int tailCount = Math.Min(lines.Length, _Settings.OutputTailLines);
            int startIndex = lines.Length - tailCount;
            List<string> reversedTailLines = new List<string>();
            for (int i = lines.Length - 1; i >= startIndex; i--)
            {
                string redacted = RedactAndBoundLine(lines[i]);
                if (!String.IsNullOrWhiteSpace(redacted) && retained.Add(redacted))
                    reversedTailLines.Add(redacted);
            }
            reversedTailLines.Reverse();

            string diagnostics = diagnosticLines.Count == 0
                ? "(none recognized)"
                : BuildBoundedSection(diagnosticLines, false);
            string tail = reversedTailLines.Count == 0
                ? "(no additional output)"
                : BuildBoundedSection(reversedTailLines, true);

            string result = "--- ACTIONABLE DIAGNOSTICS ---\n" + diagnostics
                + "\n--- OUTPUT TAIL ---\n" + tail;
            if (result.Length > _MAX_DIAGNOSTIC_TEXT_CHARS)
                return result.Substring(0, _MAX_DIAGNOSTIC_TEXT_CHARS);
            return result;
        }

        private static string RedactAndBoundLine(string line)
        {
            string normalized = line.TrimEnd('\r');
            string redacted = _SecretLikePattern.Replace(normalized, match =>
            {
                int separatorIndex = match.Value.IndexOfAny(new char[] { '=', ':' });
                if (separatorIndex < 0) return match.Value;
                return match.Value.Substring(0, separatorIndex + 1) + " [REDACTED]";
            });

            if (redacted.Length <= _MAX_LINE_CHARS)
                return redacted;
            return redacted.Substring(0, _MAX_LINE_CHARS) + "...(line truncated)";
        }

        private static string BuildBoundedSection(IReadOnlyList<string> lines, bool keepEnd)
        {
            const string truncatedMarker = "...(section truncated)";
            int contentBudget = _MAX_SECTION_CHARS - truncatedMarker.Length - 1;
            List<string> selected = new List<string>();
            int usedCharacters = 0;

            if (keepEnd)
            {
                for (int i = lines.Count - 1; i >= 0; i--)
                {
                    int required = lines[i].Length + 1;
                    if (usedCharacters + required > contentBudget)
                        break;
                    selected.Add(lines[i]);
                    usedCharacters += required;
                }
                selected.Reverse();
                if (selected.Count < lines.Count)
                    selected.Insert(0, truncatedMarker);
            }
            else
            {
                for (int i = 0; i < lines.Count; i++)
                {
                    int required = lines[i].Length + 1;
                    if (usedCharacters + required > contentBudget)
                        break;
                    selected.Add(lines[i]);
                    usedCharacters += required;
                }
                if (selected.Count < lines.Count)
                    selected.Add(truncatedMarker);
            }

            return String.Join("\n", selected);
        }

        /// <summary>
        /// Strips the <c>--no-restore</c> token from a shell command string so the build or
        /// test tool performs its own NuGet restore. Called only when
        /// <see cref="DefinitionOfDoneSettings.RunRestoreBeforeBuild"/> is true.
        /// A command that does not contain <c>--no-restore</c> is returned unchanged.
        /// </summary>
        private static string EnsureRestore(string command)
        {
            return Regex.Replace(command, @"(^|\s)--no-restore\b", " ", RegexOptions.IgnoreCase).Trim();
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
