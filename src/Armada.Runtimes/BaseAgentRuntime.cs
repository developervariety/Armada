namespace Armada.Runtimes
{
    using System.Diagnostics;
    using System.Text;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using SyslogLogging;
    using Armada.Runtimes.Interfaces;

    /// <summary>
    /// Base implementation for agent runtimes with common process management.
    /// </summary>
    public abstract class BaseAgentRuntime : IAgentRuntime
    {
        #region Public-Members

        /// <summary>
        /// Runtime display name.
        /// </summary>
        public abstract string Name { get; }

        /// <summary>
        /// Whether this runtime supports session resume.
        /// </summary>
        public abstract bool SupportsResume { get; }

        /// <summary>
        /// Whether this runtime can participate in planning sessions.
        /// The default transcript-relaunch planning flow works for all built-in runtimes.
        /// </summary>
        public virtual bool SupportsPlanningSessions => true;

        /// <summary>
        /// Event raised when the agent writes a line to stdout.
        /// </summary>
        public event Action<int, string>? OnOutputReceived;

        /// <summary>
        /// Event raised when the runtime receives authoritative provider token usage.
        /// </summary>
        public event Action<int, RuntimeTokenUsage>? OnTokenUsageReceived;

        /// <summary>
        /// Event raised alongside <see cref="OnTokenUsageReceived"/> when the runtime receives a
        /// provider-progress signal -- authoritative evidence the underlying provider has made
        /// forward motion on a request. Captains whose providers have silently hung inside a
        /// long-running request keep their OS process alive (so the captain heartbeat stays
        /// fresh) but stop publishing this signal. The autonomous recovery orchestrator
        /// subscribes to this event to distinguish a provider-silent stall from a captain-wide
        /// heartbeat stall and to bound the silent-provider case within the configured stall
        /// window.
        /// </summary>
        public event Action<int, RuntimeTokenUsage>? OnProviderProgressReceived;

        /// <summary>
        /// Event raised immediately after the agent process starts and a PID is available.
        /// </summary>
        public event Action<int>? OnProcessStarted;

        /// <summary>
        /// Event raised when the agent process exits.
        /// Parameters: processId, exitCode (null if unavailable).
        /// </summary>
        public event Action<int, int?>? OnProcessExited;

        #endregion

        #region Protected-Members

        /// <summary>
        /// Working directory of the running agent, captured at launch. Runtimes strip this prefix
        /// from paths in activity records so the mission log shows dock-relative paths instead of
        /// repeating the full dock root on every line.
        /// </summary>
        protected string? WorkingDirectory { get; set; }

        #endregion

        #region Private-Members

        private string _Header = "[BaseAgentRuntime] ";
        private LoggingModule _Logging;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="logging">Logging module.</param>
        public BaseAgentRuntime(LoggingModule logging)
        {
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Start an agent process.
        /// </summary>
        /// <param name="workingDirectory">Working directory for the agent.</param>
        /// <param name="prompt">Prompt/instructions for the agent.</param>
        /// <param name="environment">Optional environment variables.</param>
        /// <param name="logFilePath">Optional path to write agent stdout/stderr output.</param>
        /// <param name="finalMessageFilePath">Optional path to write the agent's final response artifact.</param>
        /// <param name="model">Optional model override.</param>
        /// <param name="captain">Optional captain metadata used by runtimes that need persisted runtime-specific options.</param>
        /// <param name="token">Cancellation token.</param>
        public virtual async Task<int> StartAsync(
            string workingDirectory,
            string prompt,
            Dictionary<string, string>? environment = null,
            string? logFilePath = null,
            string? finalMessageFilePath = null,
            string? model = null,
            Captain? captain = null,
            CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(workingDirectory)) throw new ArgumentNullException(nameof(workingDirectory));
            if (String.IsNullOrEmpty(prompt)) throw new ArgumentNullException(nameof(prompt));

            // Recorded so activity records can render dock-relative paths. A runtime instance is
            // created per launch (AgentRuntimeFactory.Create), so this is not shared across missions.
            WorkingDirectory = workingDirectory;

            string command = GetCommand();
            List<string> args = BuildArguments(workingDirectory, prompt, model, finalMessageFilePath, captain);

            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = command,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardInput = RedirectStdin,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                CreateNoWindow = true
            };
            if (RedirectStdin)
                startInfo.StandardInputEncoding = Encoding.UTF8;

            foreach (string arg in args)
            {
                startInfo.ArgumentList.Add(arg);
            }

            if (environment != null)
            {
                foreach (KeyValuePair<string, string> kvp in environment)
                {
                    startInfo.Environment[kvp.Key] = kvp.Value;
                }
            }

            ApplySharedCaptainEnvironment(startInfo);
            ApplyEnvironment(startInfo, captain, model);

            // Set up optional log file writer. If a prior launch leaked a handle on the
            // canonical log path (admiral crash mid-launch, orphan agent process holding
            // the file), `new StreamWriter(...)` throws IOException due to the share
            // violation and the entire launch fails in a tight retry loop. Recover by
            // falling back to a unique-suffix path; the dashboard and admiral's log API
            // continue to read the canonical path until log rotation merges them.
            StreamWriter? logWriter = null;
            string? actualLogFilePath = null;
            if (!String.IsNullOrEmpty(logFilePath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(logFilePath)!);

                // Best-effort cleanup: if the canonical log file is stale and not held by
                // any live process, deleting it now lets us reopen it cleanly. Failures
                // are silent; the open below will either succeed (we win the race) or
                // throw (we fall through to the suffix path).
                try { if (File.Exists(logFilePath)) File.Delete(logFilePath); }
                catch { }

                actualLogFilePath = logFilePath;
                try
                {
                    logWriter = new StreamWriter(logFilePath, append: true) { AutoFlush = true };
                }
                catch (IOException)
                {
                    // Canonical path locked. Suffix with a unix timestamp so successive
                    // retries within the same second still pick distinct paths.
                    string baseName = Path.GetFileNameWithoutExtension(logFilePath);
                    string ext = Path.GetExtension(logFilePath);
                    string dir = Path.GetDirectoryName(logFilePath)!;
                    string suffix = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
                    actualLogFilePath = Path.Combine(dir, baseName + "." + suffix + ext);
                    _Logging.Warn(_Header + "canonical log path locked (" + logFilePath +
                        "); falling back to " + actualLogFilePath);
                    logWriter = new StreamWriter(actualLogFilePath, append: true) { AutoFlush = true };
                }

                string timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
                string argsJoined = String.Join(" ", args);
                // Write command on first line, then prompt content preserving newlines.
                // Runtimes that deliver the prompt via stdin (UsePromptStdin) do not include the
                // prompt text in their CLI arguments, so the header would otherwise lose the
                // role/persona preamble and mission instructions. Echo the prompt parameter for
                // those runtimes so the mission log always shows which role the captain is
                // running as, matching what Claude/Codex expose through their positional prompt
                // argument.
                string firstFlag = "";
                string promptContent;
                if (UsePromptStdin)
                {
                    firstFlag = argsJoined;
                    promptContent = prompt;
                }
                else
                {
                    promptContent = argsJoined;
                    int promptStart = argsJoined.IndexOf("Mission:");
                    if (promptStart > 0)
                    {
                        firstFlag = argsJoined.Substring(0, promptStart).Trim();
                        promptContent = argsJoined.Substring(promptStart);
                    }
                }
                await logWriter.WriteLineAsync("[" + timestamp + "] Agent starting: " + command + " " + firstFlag).ConfigureAwait(false);
                await logWriter.WriteLineAsync(promptContent).ConfigureAwait(false);
                await logWriter.WriteLineAsync("").ConfigureAwait(false);
            }

            // Captured for the Exited closure so the final-message parity echo can run
            // even when stderr is suppressed from the log file.
            string? capturedFinalMessageFilePath = finalMessageFilePath;

            Process process = new Process { StartInfo = startInfo };

            process.OutputDataReceived += (sender, e) =>
            {
                if (!String.IsNullOrEmpty(e.Data))
                {
                    try { HandleRawOutputLine(process.Id, e.Data); }
                    catch (Exception ex) { _Logging.Warn(_Header + "error parsing runtime telemetry: " + ex.Message); }

                    foreach (string outputLine in TransformOutputRecords(e.Data))
                    {
                        // A runtime may transform a structured event to empty to SUPPRESS it from
                        // the mission log (e.g. OpenCode tool_use / step events). Writing an empty
                        // string would emit a blank log line, so skip suppressed lines entirely --
                        // this keeps the log tight and has no markers to detect anyway.
                        if (String.IsNullOrEmpty(outputLine)) continue;

                        _Logging.Debug(_Header + "[stdout] " + outputLine);
                        try { logWriter?.WriteLine(outputLine); }
                        catch (ObjectDisposedException) { }

                        try { OnOutputReceived?.Invoke(process.Id, outputLine); }
                        catch { }
                    }
                }
            };

            process.ErrorDataReceived += (sender, e) =>
            {
                if (!String.IsNullOrEmpty(e.Data))
                {
                    _Logging.Debug(_Header + "[stderr] " + e.Data);

                    // Gate ONLY the log-file write. Runtimes that stream their full
                    // working transcript on stderr (Codex exec) would otherwise bloat
                    // the mission log 75-220x; WriteStderrToLogFile=false keeps the file
                    // bounded while syslog and OnOutputReceived still see every line.
                    // Provider usage/quota-limit signals are always preserved in the log
                    // file so the admiral's failure-lifecycle detector can route them into
                    // captain quarantine even when the full stderr transcript is suppressed.
                    // Standalone reset-time lines ("try again at HH:MM") are also preserved so
                    // the retry parser can compute an accurate quarantine deadline when a
                    // provider splits its usage-limit message across multiple stderr lines.
                    bool quotaSignal = ProviderQuotaLimitDetector.IsQuotaLimitSignal(e.Data);
                    // Preserve standalone reset-time lines so the admiral's failure-lifecycle
                    // code can later call TryParseRetryAfterUtc on the full stderr text and
                    // compute an accurate quarantine deadline. The gate uses a lightweight
                    // substring check rather than the full parser to avoid doing expensive
                    // regex/DateTime work inside the process stderr event handler.
                    bool resetTimeLine = e.Data.Contains("try again at", StringComparison.OrdinalIgnoreCase);
                    if (WriteStderrToLogFile || quotaSignal || resetTimeLine)
                    {
                        try { logWriter?.WriteLine("[stderr] " + e.Data); }
                        catch (ObjectDisposedException) { }
                    }

                    // Treat stderr as runtime output for heartbeat/progress/output capture.
                    // Some agent CLIs emit useful diagnostics or status lines on stderr.
                    try { OnOutputReceived?.Invoke(process.Id, e.Data); }
                    catch { }
                }
            };

            // A fast-exiting agent (bad model, missing dependency) can exit before the launch path
            // attaches the async readers. Disposing the Process from this handler while that is
            // still pending drops every buffered stdout/stderr line -- the agent's only diagnostic.
            // Wait for the readers to be attached, then let WaitForExit drain them.
            ManualResetEventSlim readersAttached = new ManualResetEventSlim(false);

            process.Exited += (sender, e) =>
            {
                try { readersAttached.Wait(TimeSpan.FromSeconds(10)); } catch { }
                try { process.WaitForExit(); } catch { }

                int? code = null;
                int processId = 0;
                try { processId = process.Id; } catch { }
                try { code = ((Process?)sender)?.ExitCode; } catch { }

                // Give the runtime a chance to write records it was still holding. A runtime that
                // correlates a tool call with a later result event has nothing to write when the
                // process is killed mid-call -- and that unfinished call is the most useful line
                // in the log when diagnosing a hang. Written here, while the writer is open.
                try
                {
                    foreach (string exitRecord in BuildProcessExitRecords())
                    {
                        if (String.IsNullOrEmpty(exitRecord)) continue;

                        try { logWriter?.WriteLine(exitRecord); }
                        catch (ObjectDisposedException) { }

                        try { OnOutputReceived?.Invoke(processId, exitRecord); }
                        catch { }
                    }
                }
                catch (Exception ex) { _Logging.Warn(_Header + "error building process-exit records: " + ex.Message); }

                // Parity echo: when stderr is suppressed from the log file, the agent's
                // final answer (captured via the runtime's final-message file) would
                // otherwise never appear in the mission log. Echo it here while the
                // writer is still open. Never let this throw out of the handler.
                if (!WriteStderrToLogFile && !String.IsNullOrEmpty(capturedFinalMessageFilePath))
                {
                    try
                    {
                        if (File.Exists(capturedFinalMessageFilePath))
                        {
                            string finalMsg = File.ReadAllText(capturedFinalMessageFilePath);
                            if (!String.IsNullOrWhiteSpace(finalMsg))
                            {
                                logWriter?.WriteLine();
                                logWriter?.WriteLine("=== Final message ===");
                                logWriter?.WriteLine(finalMsg);
                            }
                        }
                    }
                    catch (Exception) { }
                }

                try { logWriter?.WriteLine("[" + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss") + "] Agent exited with code " + (code?.ToString() ?? "unknown")); }
                catch (ObjectDisposedException) { }
                logWriter?.Dispose();

                // Notify subscribers that the process has exited BEFORE disposing.
                // Disposing first invalidates the PID, which can cause the health check
                // to race with the exit handler and trigger spurious recovery.
                try { OnProcessExited?.Invoke(processId, code); }
                catch (Exception ex) { _Logging.Warn(_Header + "error in OnProcessExited handler for process " + processId + ": " + ex.Message); }

                // Dispose the Process object to release the working directory handle.
                // On Windows, undisposed Process objects hold handles on the WorkingDirectory
                // which prevents dock worktree directories from being deleted.
                try { process.Dispose(); }
                catch { }
            };
            process.EnableRaisingEvents = true;

            // Anything that throws between here and the moment process.Exited can fire
            // (i.e. before the process actually starts and dies) leaks the open
            // logWriter handle. The next launch attempt then can't open the canonical
            // log path, hits the IOException recovery above (or worse, blocks forever).
            // Ensure logWriter is disposed if the launch fails before the process is
            // running.
            try
            {
                bool started = process.Start();
                if (!started)
                    throw new InvalidOperationException("Failed to start agent process: " + command);

                try { OnProcessStarted?.Invoke(process.Id); }
                catch (Exception ex) { _Logging.Warn(_Header + "error in OnProcessStarted handler for process " + process.Id + ": " + ex.Message); }

                if (RedirectStdin)
                {
                    try
                    {
                        if (UsePromptStdin)
                        {
                            await process.StandardInput.WriteAsync(prompt).ConfigureAwait(false);
                            await process.StandardInput.FlushAsync().ConfigureAwait(false);
                        }

                        // Close stdin after writing any prompt content so the agent doesn't block
                        // waiting for piped input.
                        process.StandardInput.Close();
                    }
                    catch (IOException ex)
                    {
                        // The agent exited before it read the prompt, so the read end of the pipe
                        // is already gone and the write raises EPIPE ("Broken pipe"). That is a
                        // normal race with a fast-exiting agent, not a launch failure: the process
                        // did start, and its exit code and buffered output are still the useful
                        // diagnostic. Treating it as fatal threw away that output and aborted a
                        // launch that had in fact succeeded.
                        _Logging.Warn(_Header + "agent closed stdin before the prompt was written: " + ex.Message);
                    }
                    catch (ObjectDisposedException)
                    {
                        // Same race, surfaced as a disposed stream rather than EPIPE.
                    }
                }

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                _Logging.Info(_Header + "started process " + process.Id + " (" + command + ") in " + workingDirectory);

                return process.Id;
            }
            catch
            {
                // Release the exit handler FIRST. If the process did start and has already exited,
                // process.Exited is running and is parked in readersAttached.Wait(10s). Dispose()
                // unregisters the exit watch and therefore waits for that callback to return, so
                // disposing before signalling deadlocks the two against each other until the wait
                // times out -- a fixed 10-second stall on every failed launch. Set() is idempotent
                // and is still called in the finally below.
                try { readersAttached.Set(); } catch { }

                // Dispose the writer + process here to release the file/pipe handles. Note the
                // process MAY be alive or already exited: the earlier assumption that a launch can
                // only fail before the process starts is not true for a fast-exiting agent.
                try { logWriter?.Dispose(); } catch { }
                try { process.Dispose(); } catch { }
                throw;
            }
            finally
            {
                // Release the exit handler whether the readers were attached or the launch failed,
                // so it never sits out its full timeout.
                readersAttached.Set();
            }
        }

        /// <summary>
        /// Grace period, in milliseconds, to wait for a stopped agent to exit on its own
        /// before falling back to a hard kill. The previous 10s value was chosen against
        /// a hang-model that never materialised and made every captain stop -- and a
        /// fleet-wide stop -- pay the full timeout serially.
        /// </summary>
        protected const int StopGracePeriodMs = 3000;

        /// <summary>
        /// Stop an agent process gracefully.
        /// </summary>
        /// <param name="processId">Process ID to stop.</param>
        /// <param name="token">Cancellation token.</param>
        public virtual async Task StopAsync(int processId, CancellationToken token = default)
        {
            try
            {
                Process process = Process.GetProcessById(processId);
                if (process.HasExited) return;

                // Note: process was obtained via Process.GetProcessById, which returns a handle
                // that does NOT own the child's redirected streams. The previous implementation
                // closed StandardInput here to "ask the child to exit", but that access is on a
                // handle with no writer and the access itself can throw; the bare catch was
                // hiding that. The graceful path on a re-fetched handle is unusable, so we
                // attempt the exit wait directly. Subclasses that keep the original Process
                // reference can override StopAsync to perform a real graceful shutdown.

                using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                linkedCts.CancelAfter(StopGracePeriodMs);

                try
                {
                    await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    _Logging.Warn(_Header + "process " + processId + " did not exit within " + StopGracePeriodMs + "ms, killing");
                    try
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    catch (Exception killEx)
                    {
                        // The process may exit between the timeout and the kill attempt; surface
                        // the kill failure but do not propagate -- the stop attempt is over.
                        _Logging.Warn(_Header + "kill of process " + processId + " after grace timeout failed: " + killEx.Message);
                    }
                }

                _Logging.Info(_Header + "stopped process " + processId);
            }
            catch (ArgumentException)
            {
                _Logging.Debug(_Header + "process " + processId + " already exited");
            }
            catch (InvalidOperationException)
            {
                // Process.GetProcessById throws InvalidOperationException on Unix when the
                // pid is not a current process; treat that as already-exited.
                _Logging.Debug(_Header + "process " + processId + " not running");
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "error stopping process " + processId + ": " + ex.Message);
            }
        }

        /// <summary>
        /// Build runtime-specific command-line arguments.
        /// </summary>
        protected abstract List<string> BuildArguments(
            string workingDirectory,
            string prompt,
            string? model,
            string? finalMessageFilePath,
            Captain? captain);

        /// <summary>
        /// Check if a process is still running.
        /// </summary>
        /// <param name="processId">Process ID to check.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>True if the process is running.</returns>
        public virtual async Task<bool> IsRunningAsync(int processId, CancellationToken token = default)
        {
            // A non-positive id is never a live process. Process.GetProcessById rejects it with a
            // platform-dependent exception (ArgumentException on Windows, InvalidOperationException
            // on Unix), so screen it here instead of relying on the exception type.
            if (processId <= 0) return false;

            try
            {
                Process process = Process.GetProcessById(processId);
                return !process.HasExited;
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        #endregion

        #region Private-Methods

        /// <summary>
        /// Get the command to execute for this runtime.
        /// </summary>
        protected abstract string GetCommand();

        /// <summary>
        /// Whether the runtime expects the prompt to be written to stdin instead of passed as a CLI argument.
        /// </summary>
        protected virtual bool UsePromptStdin => false;

        /// <summary>
        /// Whether to redirect stdin for the agent process. Defaults to true. Override to false
        /// for runtimes that receive the prompt as a CLI argument and do not need a stdin pipe.
        /// When false the process inherits no stdin pipe, which prevents CLIs that probe for piped
        /// input from printing spurious startup diagnostics.
        /// </summary>
        protected virtual bool RedirectStdin => true;

        /// <summary>
        /// Whether stderr lines are written to the mission/captain log FILE. Default true. Override false for runtimes that stream their full transcript on stderr (Codex exec). When false, _Logging.Debug and OnOutputReceived STILL receive stderr (syslog, heartbeat, progress, handoff unaffected) -- only the log-file write is suppressed.
        /// </summary>
        protected virtual bool WriteStderrToLogFile => true;

        /// <summary>
        /// Apply runtime-specific environment variables to the process start info.
        /// The captain instance is forwarded so derived runtimes can read
        /// per-captain settings (e.g. <c>Captain.RuntimeOptionsJson</c>).
        /// </summary>
        protected virtual void ApplyEnvironment(ProcessStartInfo startInfo, Captain? captain, string? model = null)
        {
        }

        /// <summary>
        /// Transform a raw stdout line before it is written to the log and fired via
        /// <see cref="OnOutputReceived"/>. The default implementation returns the line
        /// unchanged. Override in runtimes that wrap output in a structured format
        /// (e.g. JSON event streams) and need to extract the inner text so that
        /// plain-text protocol markers remain detectable by subscribers.
        /// </summary>
        protected virtual string TransformOutputLine(string line) => line;

        /// <summary>
        /// Transform a raw stdout line into one or more mission-log records. One structured event
        /// can carry several distinct records -- e.g. a Claude Code assistant event that holds
        /// assistant text and a tool call in the same message. Each record is written, classified,
        /// and marker-parsed on its own, which keeps [ARMADA:*] protocol markers detectable and
        /// keeps [ARMADA:ACTIVITY] records out of the captain's accumulated output. The default
        /// implementation forwards the single <see cref="TransformOutputLine"/> result.
        /// </summary>
        /// <param name="line">Raw stdout line.</param>
        /// <returns>Zero or more mission-log records; empty records are suppressed by the caller.</returns>
        protected virtual IEnumerable<string> TransformOutputRecords(string line)
        {
            return new string[] { TransformOutputLine(line) };
        }

        /// <summary>
        /// Build any mission-log records the runtime is still holding when the agent process
        /// exits. Called once, while the log writer is still open. The default is none.
        /// </summary>
        /// <returns>Zero or more mission-log records; empty records are skipped by the caller.</returns>
        protected virtual IEnumerable<string> BuildProcessExitRecords()
        {
            return Array.Empty<string>();
        }

        /// <summary>
        /// Inspect a raw stdout line for runtime telemetry before log transformation.
        /// </summary>
        /// <param name="processId">Agent process identifier.</param>
        /// <param name="line">Raw stdout line.</param>
        protected virtual void HandleRawOutputLine(int processId, string line)
        {
        }

        /// <summary>
        /// Publish authoritative token usage to lifecycle subscribers.
        /// </summary>
        /// <param name="processId">Agent process identifier.</param>
        /// <param name="usage">Authoritative usage sample.</param>
        protected void PublishTokenUsage(int processId, RuntimeTokenUsage usage)
        {
            try { OnTokenUsageReceived?.Invoke(processId, usage); }
            catch { }
            try { OnProviderProgressReceived?.Invoke(processId, usage); }
            catch { }
        }

        private static void ApplySharedCaptainEnvironment(ProcessStartInfo startInfo)
        {
            startInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";
            startInfo.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
        }

        /// <summary>
        /// Resolve a PATH-based executable name to a concrete Windows-friendly launcher when needed.
        /// npm-installed CLIs on Windows often expose .cmd wrappers that must be launched directly
        /// when UseShellExecute=false.
        /// </summary>
        protected string ResolveExecutable(string command)
        {
            if (String.IsNullOrEmpty(command)) throw new ArgumentNullException(nameof(command));

            if (!OperatingSystem.IsWindows())
                return command;

            if (command.Contains(Path.DirectorySeparatorChar) || command.Contains(Path.AltDirectorySeparatorChar))
                return command;

            string appDataNpm = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "npm",
                command + ".cmd");

            if (File.Exists(appDataNpm))
                return appDataNpm;

            return command;
        }

        #endregion
    }
}
