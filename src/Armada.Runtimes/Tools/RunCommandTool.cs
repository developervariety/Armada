namespace Armada.Runtimes.Tools
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Text;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Services;

    /// <summary>
    /// Runs one shell command in the mission workspace, so an API-endpoint captain can use git, build and run
    /// the vessel's tests: the work a Worker, Test Engineer, Judge or Linter cannot do with file tools alone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// What this tool enforces, and what it does not. It enforces a working directory inside the workspace
    /// (the same <see cref="WorkspacePathPolicy"/> the file tools use, reparse points included), an environment
    /// built from an allowlist so no admiral or provider credential reaches the command, a closed standard
    /// input so an interactive prompt fails instead of hanging, a timeout that kills the whole process tree, and
    /// an output cap that keeps the head and the tail.
    /// </para>
    /// <para>
    /// It does NOT confine what the command itself touches once it runs: a shell can change directory, read
    /// any file the runtime user can read, and reach the network. That needs kernel isolation, and the
    /// container this runs in permits none (user namespaces are refused and no sandbox binary is installed).
    /// This is the same posture every CLI-harness captain already has, since those harnesses run their own
    /// shells in the same container, so the tool is at parity with them rather than a new class of hazard.
    /// The <see cref="ToolMutationKind"/> it is registered with is Mutating.
    /// </para>
    /// <para>
    /// It is registered only when a run opts in. The mission launch path opts in; the dashboard chat path
    /// never does, because a chat caller is a different principal from a dispatched mission.
    /// </para>
    /// </remarks>
    public class RunCommandTool : IToolExecutor
    {
        #region Public-Members

        /// <summary>Largest timeout a caller may request, in seconds. A full vessel suite can need most of it.</summary>
        public const int MaximumTimeoutSeconds = 3600;

        /// <summary>Timeout applied when the caller names none, in seconds.</summary>
        public const int DefaultTimeoutSeconds = 120;

        /// <summary>
        /// Environment variables a command inherits, by exact name. Everything else is dropped, which is how
        /// provider keys, admiral credentials and MCP session tokens stay out of the command's reach through
        /// its environment. An allowlist, not a denylist: a new secret variable is excluded by default.
        /// </summary>
        public static readonly IReadOnlyList<string> InheritedEnvironmentNames = new List<string>
        {
            "PATH", "HOME", "USER", "LOGNAME", "SHELL", "LANG", "LC_ALL", "LC_CTYPE", "TZ", "TMPDIR",
            "DOTNET_ROOT", "JAVA_HOME"
        };

        /// <summary>The unique name of this tool.</summary>
        public string Name => "run_command";

        /// <summary>A human-readable description of what this tool does.</summary>
        public string Description => "Runs one shell command with bash in the mission workspace and returns its exit code "
            + "and combined output. Use it for git (status, log, diff), builds and tests. The working directory must be "
            + "inside the workspace. Standard input is closed, so interactive commands fail rather than wait. Output "
            + "above the limit keeps its beginning and its end, where a test summary usually is. Progress-only chunks of "
            + "a still-larger log may then be dropped; diagnostics, test totals and structured documents stay, and the "
            + "full output is archived in .armada-tool-output/ in the workspace. The command is killed, "
            + "with everything it started, when its timeout expires.";

        /// <summary>The JSON Schema object describing the tool's input parameters.</summary>
        public object ParametersSchema => new
        {
            type = "object",
            properties = new
            {
                command = new
                {
                    type = "string",
                    description = "The shell command to run, for example \"git diff --stat origin/main...HEAD\"."
                },
                working_directory = new
                {
                    type = "string",
                    description = "Directory to run in, relative to the workspace. Defaults to the workspace root."
                },
                timeout_seconds = new
                {
                    type = "integer",
                    description = "Seconds before the command is killed. Default " + DefaultTimeoutSeconds + ", maximum " + MaximumTimeoutSeconds + "."
                }
            },
            required = new[] { "command" }
        };

        #endregion

        #region Public-Methods

        /// <summary>
        /// Run the command and report its exit code and output.
        /// </summary>
        /// <param name="toolCallId">The unique identifier for this tool call.</param>
        /// <param name="argumentsJson">The JSON arguments: command, optional working_directory and timeout_seconds.</param>
        /// <param name="workingDirectory">The mission workspace.</param>
        /// <param name="cancellationToken">A token to cancel the operation.</param>
        /// <returns>A <see cref="ToolResult"/> carrying the exit code and output.</returns>
        public async Task<ToolResult> ExecuteAsync(string toolCallId, string argumentsJson, string workingDirectory, CancellationToken cancellationToken)
        {
            RunCommandArguments request = ToolArgumentParser.Parse<RunCommandArguments>(argumentsJson);
            if (String.IsNullOrWhiteSpace(request.Command))
                return Failure(toolCallId, "missing_parameter", "The 'command' parameter is required.");

            // Throws WorkspaceBoundaryException for a directory outside the workspace; the agent loop reports
            // that as boundary_refused, the same class a file tool's out-of-workspace path gets.
            string runDirectory = WorkspacePathPolicy.ResolvePath(
                String.IsNullOrWhiteSpace(request.WorkingDirectory) ? "." : request.WorkingDirectory!,
                workingDirectory);
            if (!Directory.Exists(runDirectory))
                return Failure(toolCallId, "directory_not_found", "The working directory does not exist in the workspace.");

            int timeoutSeconds = Math.Clamp(request.TimeoutSeconds ?? DefaultTimeoutSeconds, 1, MaximumTimeoutSeconds);
            int outputLimit = Math.Clamp(ToolSafetyLimits.MaxProcessOutputBytes, 1024, 16 * 1024 * 1024);

            ProcessStartInfo startInfo = BuildStartInfo(request.Command!, runDirectory);
            BoundedOutput output = new BoundedOutput(outputLimit);
            Stopwatch clock = Stopwatch.StartNew();

            using (Process process = new Process { StartInfo = startInfo, EnableRaisingEvents = true })
            {
                process.OutputDataReceived += (sender, e) => { if (e.Data != null) output.AppendLine(e.Data); };
                process.ErrorDataReceived += (sender, e) => { if (e.Data != null) output.AppendLine(e.Data); };

                process.Start();
                process.StandardInput.Close();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                bool timedOut = false;
                using (CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    limit.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
                    try
                    {
                        await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        timedOut = !cancellationToken.IsCancellationRequested;
                        KillTree(process);
                        if (cancellationToken.IsCancellationRequested)
                            return ToolExecution.Cancelled(toolCallId, cancellationToken);
                    }
                }

                // Let the asynchronous readers drain what the process wrote before it exited.
                if (!timedOut) process.WaitForExit();
                clock.Stop();

                int? exitCode = timedOut ? null : process.ExitCode;
                string error = timedOut ? "timed_out" : (exitCode == 0 ? String.Empty : "nonzero_exit");
                string rendered = output.Render();
                ToolOutputPruneResult prune = ToolOutputRetention.Prune(request.Command, rendered);
                string kept = prune.Output;
                string? archive = null;
                long omitted = output.OmittedBytes;
                if (prune.Pruned)
                {
                    archive = TryArchive(workingDirectory, toolCallId, rendered);
                    if (archive == null)
                    {
                        // A prune whose archive cannot be written leaves the original: the captain can
                        // still read every line, and a hole it cannot recover is worse than a large log.
                        kept = rendered;
                        prune = ToolOutputPruneResult.Unchanged(rendered, "archive_failed");
                    }
                    else
                    {
                        omitted += Encoding.UTF8.GetByteCount(rendered) - Encoding.UTF8.GetByteCount(kept);
                    }
                }

                RunCommandResult result = new RunCommandResult
                {
                    ExitCode = exitCode,
                    TimedOut = timedOut,
                    DurationMs = clock.ElapsedMilliseconds,
                    Truncated = output.Truncated || prune.Pruned,
                    Pruned = prune.Pruned,
                    OmittedBytes = omitted,
                    OutputArchive = archive,
                    Output = kept,
                    Error = String.IsNullOrEmpty(error) ? null : error
                };

                return new ToolResult
                {
                    ToolCallId = toolCallId,
                    Success = String.IsNullOrEmpty(error),
                    Content = JsonSerializer.Serialize(result)
                };
            }
        }

        /// <summary>
        /// Build the process start info: bash in the requested directory, closed-over environment, redirected streams.
        /// </summary>
        /// <param name="command">Shell command text.</param>
        /// <param name="runDirectory">Resolved working directory inside the workspace.</param>
        /// <returns>The start info.</returns>
        internal static ProcessStartInfo BuildStartInfo(string command, string runDirectory)
        {
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = "bash",
                WorkingDirectory = runDirectory,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(command);

            ApplyEnvironment(startInfo.Environment, Environment.GetEnvironmentVariables());
            return startInfo;
        }

        /// <summary>
        /// Replace a start environment with the allowlisted subset of a source environment, plus the settings a
        /// non-interactive command needs.
        /// </summary>
        /// <param name="target">Environment the child process will receive; cleared first.</param>
        /// <param name="source">Environment to copy allowlisted names from.</param>
        internal static void ApplyEnvironment(IDictionary<string, string?> target, System.Collections.IDictionary source)
        {
            target.Clear();
            foreach (string name in InheritedEnvironmentNames)
            {
                if (source.Contains(name) && source[name] is string value)
                    target[name] = value;
            }

            if (!target.ContainsKey("PATH"))
                target["PATH"] = "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin";

            // Fail rather than wait on a prompt nobody can answer.
            target["GIT_TERMINAL_PROMPT"] = "0";
            target["GIT_PAGER"] = "cat";
            target["PAGER"] = "cat";
            target["TERM"] = "dumb";
            target["CI"] = "true";
            target["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
            target["DOTNET_NOLOGO"] = "1";
        }

        #endregion

        #region Private-Methods

        private static void KillTree(Process process)
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Exited between the check and the kill.
            }
        }

        private static string? TryArchive(string workspace, string toolCallId, string fullOutput)
        {
            try
            {
                string folder = Path.Combine(workspace, ".armada-tool-output");
                Directory.CreateDirectory(folder);
                string safe = SanitizeFileName(toolCallId);
                File.WriteAllText(Path.Combine(folder, safe + ".txt"), fullOutput);
                return ".armada-tool-output/" + safe + ".txt";
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string SanitizeFileName(string toolCallId)
        {
            StringBuilder safe = new StringBuilder();
            foreach (char ch in toolCallId ?? String.Empty)
            {
                if (Char.IsLetterOrDigit(ch) || ch == '.' || ch == '_' || ch == '-') safe.Append(ch);
                if (safe.Length >= 64) break;
            }
            return safe.Length == 0 ? "output" : safe.ToString();
        }

        private static ToolResult Failure(string toolCallId, string error, string message)
        {
            return new ToolResult
            {
                ToolCallId = toolCallId,
                Success = false,
                Content = JsonSerializer.Serialize(new { error, message })
            };
        }

        #endregion

        #region Private-Types

        private sealed class RunCommandArguments
        {
            public string? Command { get; set; }

            [JsonPropertyName("working_directory")]
            public string? WorkingDirectory { get; set; }

            [JsonPropertyName("timeout_seconds")]
            public int? TimeoutSeconds { get; set; }
        }

        private sealed class RunCommandResult
        {
            [JsonPropertyName("error")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public string? Error { get; set; }

            [JsonPropertyName("exit_code")]
            public int? ExitCode { get; set; }

            [JsonPropertyName("timed_out")]
            public bool TimedOut { get; set; }

            [JsonPropertyName("duration_ms")]
            public long DurationMs { get; set; }

            [JsonPropertyName("truncated")]
            public bool Truncated { get; set; }

            [JsonPropertyName("pruned")]
            public bool Pruned { get; set; }

            [JsonPropertyName("omitted_bytes")]
            public long OmittedBytes { get; set; }

            [JsonPropertyName("output_archive")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public string? OutputArchive { get; set; }

            [JsonPropertyName("output")]
            public string Output { get; set; } = String.Empty;
        }

        #endregion
    }
}
