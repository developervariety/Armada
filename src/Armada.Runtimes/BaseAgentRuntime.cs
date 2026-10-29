namespace Armada.Runtimes
{
    using System.Diagnostics;
    using System.IO;
    using System.Runtime.CompilerServices;
    using System.Text;
    using Armada.Core;
    using Armada.Core.Enums;
    using Armada.Core.Harbor;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using SyslogLogging;
    using Armada.Runtimes.Interfaces;

    /// <summary>
    /// Base implementation for agent runtimes with common process management.
    /// </summary>
    public abstract class BaseAgentRuntime : IAgentRuntime, IRuntimeProviderErrorSource
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

        /// <inheritdoc />
        public event Action<int, string>? OnStdoutReceived;

        /// <summary>
        /// Whether the current launch asked for the model's reasoning to be surfaced. Set at the start of
        /// <see cref="StartAsync"/> and read by runtimes that support a thinking channel.
        /// </summary>
        protected bool ShowThinking { get; private set; }

        /// <summary>
        /// Native shell and file tool access of the current launch. <see cref="StartAsync"/> takes it from the
        /// launch's isolation plan; a runtime instance serves one launch, so setting it never affects another
        /// launch. <see cref="NativeToolAccessEnum.Unrestricted"/> keeps the runtime's permissive launch defaults.
        /// </summary>
        public NativeToolAccessEnum NativeToolAccess { get; set; } = NativeToolAccessEnum.Unrestricted;

        /// <summary>
        /// Whether this runtime can launch with <see cref="NativeToolAccessEnum.ReadOnly"/>. A runtime that cannot
        /// express read-only native tools refuses the launch instead of running with permissive defaults, so a new
        /// runtime must opt in explicitly.
        /// </summary>
        public virtual bool SupportsReadOnlyNativeTools => false;

        /// <summary>
        /// The reason a read-only launch is refused for this runtime, or null when the runtime supports it.
        /// </summary>
        public string? ReadOnlyNativeToolsRefusal => SupportsReadOnlyNativeTools
            ? null
            : Name + " cannot restrict its native tools to read-only, so it cannot run an Ask turn. Use a runtime that can, or widen Ask native tool access in the server settings.";

        /// <summary>
        /// Whether this launch can write a final reply artifact.
        /// </summary>
        protected bool FinalReplyRequested
        {
            get
            {
                lock (_FinalReplyLock) return _FinalReplyRequested;
            }
        }

        /// <summary>
        /// Maximum final reply length, in UTF-16 characters.
        /// </summary>
        protected const int MaximumFinalReplyCharacters = 524288;

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

        /// <inheritdoc />
        public event Action<int, RuntimeProviderError>? OnProviderErrorReceived;

        /// <summary>
        /// Event raised immediately after the agent process starts and a PID is available.
        /// </summary>
        public event Action<int>? OnProcessStarted;

        /// <summary>
        /// Event raised when the agent process exits.
        /// Parameters: processId, exitCode (null if unavailable).
        /// </summary>
        public event Action<int, int?>? OnProcessExited;

        /// <summary>
        /// Source of the operator-configured admiral secret names a captain must not inherit (see
        /// <see cref="CaptainEnvironmentScrub"/>). Read at every launch so a settings reload applies. When null,
        /// only the built-in credential-name rule applies.
        /// </summary>
        public Func<IReadOnlyCollection<string>>? AdmiralSecretNames { get; set; }

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
        private const int LogCaptureFailureExitCode = 74;
        private readonly ConditionalWeakTable<StreamWriter, LogWriteState> _LogWriteStates = new ConditionalWeakTable<StreamWriter, LogWriteState>();
        private readonly object _FinalReplyLock = new object();
        private bool _FinalReplyRequested;
        private string? _FinalReplyText;

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
        /// <param name="showThinking">Whether runtime thinking output should be shown.</param>
        /// <param name="token">Cancellation token.</param>
        /// <param name="isolationPlan">Optional per-launch arguments and environment overrides.</param>
        public virtual async Task<int> StartAsync(
            string workingDirectory,
            string prompt,
            Dictionary<string, string>? environment = null,
            string? logFilePath = null,
            string? finalMessageFilePath = null,
            string? model = null,
            Captain? captain = null,
            bool showThinking = false,
            CancellationToken token = default,
            CaptainLaunchIsolationPlan? isolationPlan = null)
        {
            if (String.IsNullOrEmpty(workingDirectory)) throw new ArgumentNullException(nameof(workingDirectory));
            if (String.IsNullOrEmpty(prompt)) throw new ArgumentNullException(nameof(prompt));
            token.ThrowIfCancellationRequested();

            ResetFinalReplyCapture(!String.IsNullOrEmpty(finalMessageFilePath));

            ShowThinking = showThinking;

            if (isolationPlan != null) NativeToolAccess = isolationPlan.NativeToolAccess;
            if (NativeToolAccess == NativeToolAccessEnum.ReadOnly && !SupportsReadOnlyNativeTools)
                throw new InvalidOperationException(ReadOnlyNativeToolsRefusal);

            // Recorded so activity records can render dock-relative paths. A runtime instance is
            // created per launch (AgentRuntimeFactory.Create), so this is not shared across missions.
            WorkingDirectory = workingDirectory;

            string command = GetCommand();
            List<string> args = BuildArguments(workingDirectory, prompt, model, finalMessageFilePath, captain);
            AppendIsolationArguments(args, isolationPlan);

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
            // UTF-8 without a byte-order mark. Process.Start writes the stdin encoding's preamble and flushes it
            // before returning, so Encoding.UTF8 put a BOM ahead of every stdin prompt, and against an agent that
            // had already exited that write made Start itself throw a broken pipe.
            if (RedirectStdin)
                startInfo.StandardInputEncoding = new UTF8Encoding(false);

            foreach (string arg in args)
            {
                startInfo.ArgumentList.Add(arg);
            }

            string? inheritedTempDirectory = startInfo.Environment.TryGetValue("TMPDIR", out string? inheritedTemp) ? inheritedTemp : null;
            PrepareCaptainEnvironment(startInfo, environment, isolationPlan, captain, model);
            string? launchTempDirectory = null;

            StreamWriter? logWriter = OpenLogWriter(logFilePath);
            if (logWriter != null) await WriteLaunchHeaderAsync(logWriter, command, args, prompt).ConfigureAwait(false);

            // Captured for the Exited closure so the final-message parity echo can run
            // even when stderr is suppressed from the log file.
            string? capturedFinalMessageFilePath = finalMessageFilePath;

            Process process = new Process { StartInfo = startInfo };

            process.OutputDataReceived += (sender, e) =>
            {
                if (!String.IsNullOrEmpty(e.Data)) EmitStdoutLine(process.Id, e.Data, logWriter);
            };

            process.ErrorDataReceived += (sender, e) =>
            {
                if (!String.IsNullOrEmpty(e.Data)) EmitStderrLine(process.Id, e.Data, logWriter);
            };

            // A fast-exiting agent (bad model, missing dependency) can exit before the launch path
            // attaches the async readers. Disposing the Process from this handler while that is
            // still pending drops every buffered stdout/stderr line -- the agent's only diagnostic.
            // Wait for the readers to be attached, then let WaitForExit drain them.
            ManualResetEventSlim readersAttached = new ManualResetEventSlim(false);
            RuntimeProcessExitEvidence? exitEvidence = null;

            process.Exited += (sender, e) =>
            {
                try { readersAttached.Wait(TimeSpan.FromSeconds(10)); }
                catch (Exception ex) { WarnSwallowed("waiting for output readers before draining exit", ex); }
                try { process.WaitForExit(); }
                catch (Exception ex) { WarnSwallowed("draining output after process exit", ex); }

                int? code = null;
                int processId = 0;
                try { processId = process.Id; }
                catch (Exception ex) { WarnSwallowed("reading the exited process id", ex); }
                try { code = ((Process?)sender)?.ExitCode; }
                catch (Exception ex) { WarnSwallowed("reading the exit code of process " + processId + "; recording it as unknown", ex); }

                DeleteLaunchTempDirectory(launchTempDirectory);
                CompleteExit(processId, code, logWriter, capturedFinalMessageFilePath, exitEvidence);

                // Dispose the Process object to release the working directory handle.
                // On Windows, undisposed Process objects hold handles on the WorkingDirectory
                // which prevents dock worktree directories from being deleted.
                try { process.Dispose(); }
                catch (Exception ex) { WarnSwallowed("disposing exited process " + processId + "; its working-directory handle may stay open", ex); }
            };
            process.EnableRaisingEvents = true;

            // Anything that throws between here and the moment process.Exited can fire
            // (i.e. before the process actually starts and dies) leaks the open
            // logWriter handle. The next launch attempt then can't open the canonical
            // log path, hits the IOException recovery above (or worse, blocks forever).
            // Ensure logWriter is disposed if the launch fails before the process is
            // running.
            bool processStarted = false;
            try
            {
                token.ThrowIfCancellationRequested();
                launchTempDirectory = ApplyLaunchTempDirectory(startInfo, inheritedTempDirectory);
                DateTime launchUtc = DateTime.UtcNow;
                processStarted = process.Start();
                if (!processStarted)
                    throw new InvalidOperationException("Failed to start agent process: " + command);

                RecordLaunchIdentity(process, launchUtc);
                exitEvidence = ProcessSupervisor.TrackRuntimeExit(process.Id);

                try { OnProcessStarted?.Invoke(process.Id); }
                catch (Exception ex) { _Logging.Warn(_Header + "error in OnProcessStarted handler for process " + process.Id + ": " + ex.Message); }

                token.ThrowIfCancellationRequested();

                if (RedirectStdin)
                {
                    try
                    {
                        if (UsePromptStdin)
                        {
                            await process.StandardInput.WriteAsync(prompt.AsMemory(), token).ConfigureAwait(false);
                            await process.StandardInput.FlushAsync(token).ConfigureAwait(false);
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

                token.ThrowIfCancellationRequested();
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
                try { readersAttached.Set(); }
                catch (Exception ex) { WarnSwallowed("releasing the exit handler after a failed launch", ex); }

                // A launch that fails or is cancelled after the process started must not leave the
                // agent running unowned: the caller never receives its identifier, so nothing else
                // would ever stop it.
                if (processStarted) KillFailedLaunch(process);

                // A process that never started never raises Exited, which is where its temporary
                // directory is otherwise removed.
                if (!processStarted) DeleteLaunchTempDirectory(launchTempDirectory);

                // Dispose the writer + process here to release the file/pipe handles. The process
                // may already have exited on its own: a fast-exiting agent can fail the launch too.
                try { logWriter?.Dispose(); }
                catch (Exception ex) { WarnSwallowed("closing the log after a failed launch", ex); }
                try { process.Dispose(); }
                catch (Exception ex) { WarnSwallowed("disposing the process after a failed launch", ex); }
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
        /// Run this runtime's launch plan on a Harbor runner instead of as a local process. The command,
        /// arguments and forwardable environment are built exactly as for a local launch; the runner's output
        /// and exit then pass through the same parsing, mission log and events as local process output, so
        /// every subscriber treats the job like a local process. No isolation plan, account login, provider
        /// credential or final-message file travels to the runner.
        /// </summary>
        /// <param name="host">Harbor process host.</param>
        /// <param name="launch">Runner, ownership and runner-side working directory. Its request is filled here.</param>
        /// <param name="prompt">Prompt for the agent.</param>
        /// <param name="logFilePath">Optional mission log path on the Admiral.</param>
        /// <param name="model">Optional model override.</param>
        /// <param name="captain">Optional captain metadata.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Synthetic process identifier.</returns>
        /// <exception cref="HarborLaunchException">The launch cannot run on a runner or the runner refused it.</exception>
        public async Task<int> StartOnHarborAsync(
            IHarborProcessHost host,
            HarborProcessLaunch launch,
            string prompt,
            string? logFilePath = null,
            string? model = null,
            Captain? captain = null,
            CancellationToken token = default)
        {
            if (host == null) throw new ArgumentNullException(nameof(host));
            if (launch == null) throw new ArgumentNullException(nameof(launch));
            if (String.IsNullOrEmpty(launch.WorkingDirectory)) throw new ArgumentException("A runner working directory is required.", nameof(launch));
            if (String.IsNullOrEmpty(prompt)) throw new ArgumentNullException(nameof(prompt));

            ResetFinalReplyCapture(!String.IsNullOrEmpty(launch.LocalFinalMessageFilePath));

            ShowThinking = false;
            WorkingDirectory = launch.WorkingDirectory;

            string command = GetCommand();
            List<string> args = BuildArguments(launch.WorkingDirectory, prompt, model, null, captain);
            args.AddRange(launch.ExtraArguments);
            Dictionary<string, string> environment = HarborLaunchEnvironment.Select(CollectLaunchVariables(model, captain));
            launch.Request = new HarborLaunchRequest
            {
                Runtime = command,
                DockId = launch.CheckoutBinding == null ? null : launch.DockId,
                CheckoutBinding = launch.CheckoutBinding,
                MissionConfiguration = launch.MissionConfiguration,
                WorkingDirectory = launch.WorkingDirectory,
                Model = model,
                Prompt = UsePromptStdin ? prompt : null,
                PromptViaStdin = UsePromptStdin,
                Arguments = args,
                Environment = environment
            };

            StreamWriter? logWriter = OpenLogWriter(logFilePath);
            if (logWriter != null)
            {
                await logWriter.WriteLineAsync("[" + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss") + "] Harbor runner " + launch.RunnerId + " runs this agent in " + launch.WorkingDirectory).ConfigureAwait(false);
                await WriteLaunchHeaderAsync(logWriter, command, args, prompt).ConfigureAwait(false);
            }

            HarborRuntimeEvents events = new HarborRuntimeEvents(this, logWriter, launch.LocalFinalMessageFilePath);
            int processId;
            try
            {
                processId = await host.LaunchAsync(launch, events, token).ConfigureAwait(false);
            }
            catch
            {
                events.CloseLog();
                throw;
            }

            try { OnProcessStarted?.Invoke(processId); }
            catch (Exception ex) { _Logging.Warn(_Header + "error in OnProcessStarted handler for process " + processId + ": " + ex.Message); }
            _Logging.Info(_Header + "started Harbor job as process " + processId + " (" + command + ") on runner " + launch.RunnerId);
            return processId;
        }

        /// <summary>
        /// Reconnect this runtime's output handlers to an already-running Harbor job. This does not start a process,
        /// reset its runner state, or publish a final-message artifact from an earlier attempt.
        /// </summary>
        /// <param name="processId">Persisted Admiral synthetic process identifier.</param>
        /// <param name="logFilePath">Existing mission log to append.</param>
        /// <returns>Handlers for output and exit from the restored job.</returns>
        public IHarborProcessEvents CreateHarborReattachmentEvents(int processId, string? logFilePath)
        {
            if (processId <= ProcessSupervisor.SyntheticProcessIdFloor)
                throw new ArgumentOutOfRangeException(nameof(processId));

            ResetFinalReplyCapture(false);
            ShowThinking = false;
            StreamWriter? logWriter = OpenLogWriter(logFilePath, preserveExisting: true);
            if (logWriter != null)
            {
                logWriter.WriteLine("[" + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss") + "] Admiral reattached to Harbor process " + processId);
            }
            return new HarborRuntimeEvents(this, logWriter);
        }

        /// <summary>Wait for the lifecycle owner to finish handling a Harbor process exit.</summary>
        /// <remarks>The lifecycle handler sets this callback before it starts or reattaches a Harbor process.</remarks>
        public Func<int, Task>? HarborExitCompletionWaiter { get; set; }

        /// <summary>
        /// Grace period, in milliseconds, that <see cref="StopAsync"/> waits for an agent to exit on its own before
        /// it kills the agent's process tree.
        /// </summary>
        protected const int StopGracePeriodMs = 3000;

        /// <summary>
        /// Stop an agent process. No shutdown request is sent: the process gets <see cref="StopGracePeriodMs"/> to
        /// exit on its own, and its process tree is then killed. Only a process verified as the one this admiral
        /// process launched with the identifier is acted on. A live process whose start time differs from the
        /// recorded launch holds a reused identifier, and a live process with no recorded launch (one started before
        /// the admiral process restarted) cannot be told from an unrelated process that reused the identifier; both
        /// are left alone and the refusal is logged with its reason. A synthetic process registered with a stop
        /// operation, such as a Harbor job, is stopped through that operation.
        /// </summary>
        /// <param name="processId">Process ID to stop.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Whether the process was stopped, was not running, or the stop was refused and why.</returns>
        public virtual async Task<AgentStopResult> StopAsync(int processId, CancellationToken token = default)
        {
            try
            {
                if (await ProcessSupervisor.TryStopSyntheticProcessAsync(processId, token).ConfigureAwait(false))
                {
                    _Logging.Info(_Header + "stop requested for synthetic process " + processId);
                    return AgentStopResult.Stopped();
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "stop refused for synthetic process " + processId + ": " + AgentStopResult.SyntheticStopFailedReason + " (" + ex.Message + ")");
                return AgentStopResult.Refused(AgentStopResult.SyntheticStopFailedReason, ex.Message);
            }

            try
            {
                using (Process? process = ProcessSupervisor.OpenLaunchedProcess(processId, null, out LaunchedProcessIdentityEnum identity))
                {
                    if (identity == LaunchedProcessIdentityEnum.Reused)
                    {
                        AgentStopResult reused = AgentStopResult.Refused(AgentStopResult.IdentifierReusedReason,
                            "a live process holds the identifier but started at a different time from the recorded launch");
                        _Logging.Warn(_Header + "stop refused for process " + processId + ": " + reused);
                        return reused;
                    }

                    if (process == null)
                    {
                        _Logging.Debug(_Header + "process " + processId + " is not running; nothing to stop");
                        return AgentStopResult.NotRunning();
                    }

                    if (identity != LaunchedProcessIdentityEnum.Verified)
                    {
                        AgentStopResult unverified = AgentStopResult.Refused(AgentStopResult.IdentityUnverifiedReason,
                            "no launch start time is recorded or stored for the identifier, or the live process's start time is unreadable,"
                            + " so it cannot be told from an unrelated process that reused the identifier");
                        _Logging.Warn(_Header + "stop refused for process " + processId + ": " + unverified);
                        return unverified;
                    }

                    // A handle from Process.GetProcessById does not own the agent's redirected streams, so no
                    // shutdown request can be delivered through it. The stop is an exit wait followed by a kill.
                    using (CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token))
                    {
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
                            catch (InvalidOperationException)
                            {
                                // Exited between the timeout and the kill, which is the state the stop wants.
                            }
                            catch (Exception killEx)
                            {
                                AgentStopResult failed = AgentStopResult.Refused(AgentStopResult.KillFailedReason, killEx.Message);
                                _Logging.Warn(_Header + "stop of process " + processId + " failed: " + failed);
                                return failed;
                            }
                        }
                    }

                    _Logging.Info(_Header + "stopped process " + processId);
                    return AgentStopResult.Stopped();
                }
            }
            catch (Exception ex)
            {
                AgentStopResult failed = AgentStopResult.Refused(AgentStopResult.KillFailedReason, ex.Message);
                _Logging.Warn(_Header + "error stopping process " + processId + ": " + failed);
                return failed;
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
        /// Check if the launched agent process is still running. A live process whose start time differs from the
        /// launch recorded for the identifier is a reused identifier and is not running; a live process with no
        /// recorded launch cannot be proved gone, so it reads as running. A registered synthetic
        /// process, such as a Harbor job, is running while its registration lasts.
        /// </summary>
        /// <param name="processId">Process ID to check.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>True if the process is running.</returns>
        public virtual Task<bool> IsRunningAsync(int processId, CancellationToken token = default)
        {
            if (ProcessSupervisor.IsSyntheticProcessAlive(processId)) return Task.FromResult(true);

            // A non-positive id is never a live process. Process.GetProcessById rejects it with a
            // platform-dependent exception (ArgumentException on Windows, InvalidOperationException
            // on Unix), so screen it here instead of relying on the exception type.
            if (processId <= 0) return Task.FromResult(false);

            // A live process whose start time differs from the recorded launch holds a reused identifier.
            return Task.FromResult(ProcessSupervisor.IsTrackedProcessAlive(processId));
        }

        #endregion

        #region Private-Methods

        /// <summary>
        /// The executable this runtime launches, for a version probe. Null when the runtime cannot name one.
        /// </summary>
        /// <returns>Executable name or path, or null.</returns>
        public string? TryGetExecutable()
        {
            try
            {
                return GetCommand();
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Get the command to execute for this runtime.
        /// </summary>
        protected abstract string GetCommand();

        /// <summary>
        /// Whether the runtime expects the prompt to be written to stdin instead of passed as a CLI argument.
        /// A mission brief exceeds the Windows command-line limit (32,767 characters, 8,191 through the
        /// cmd.exe that runs an npm .cmd shim), so a runtime whose CLI reads stdin must return true.
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
        /// Add launch variables a runtime supplies outside <see cref="ApplyEnvironment"/>, so a Harbor launch
        /// judges them by the same forwarding rule. The default adds none.
        /// </summary>
        /// <param name="variables">Variables this launch sets.</param>
        /// <param name="model">Model for the launch.</param>
        /// <param name="captain">Captain for the launch.</param>
        protected virtual void AddLaunchVariables(Dictionary<string, string> variables, string? model, Captain? captain)
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
        /// Store the final assistant reply for the current launch. Calls are ignored when no final-message
        /// file was requested. Passing null clears any reply captured earlier in the launch.
        /// </summary>
        protected void SetFinalReply(string? text)
        {
            lock (_FinalReplyLock)
            {
                if (!_FinalReplyRequested) return;
                _FinalReplyText = text == null ? null : BoundFinalReplyText(text);
            }
        }

        /// <summary>
        /// Bound captured reply text to the configured tail size without splitting a surrogate pair.
        /// </summary>
        protected static string BoundFinalReplyText(string text)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));
            if (text.Length <= MaximumFinalReplyCharacters) return text;

            int start = text.Length - MaximumFinalReplyCharacters;
            if (Char.IsLowSurrogate(text[start]) && start > 0 && Char.IsHighSurrogate(text[start - 1]))
                start++;
            return text.Substring(start);
        }

        /// <summary>
        /// Clear provider-owned final-reply state before each launch.
        /// </summary>
        protected virtual void ResetFinalReplyState()
        {
        }

        private void ResetFinalReplyCapture(bool requested)
        {
            lock (_FinalReplyLock)
            {
                _FinalReplyRequested = requested;
                _FinalReplyText = null;
            }
            ResetFinalReplyState();
        }

        /// <summary>
        /// Inspect a raw stdout line for runtime telemetry before log transformation.
        /// </summary>
        /// <param name="processId">Agent process identifier.</param>
        /// <param name="line">Raw stdout line.</param>
        protected virtual void HandleRawOutputLine(int processId, string line)
        {
        }

        /// <summary>Inspect one raw stderr line for provider status or error-type evidence.</summary>
        /// <param name="processId">Agent process identifier.</param>
        /// <param name="line">Raw stderr line.</param>
        protected virtual void HandleRawErrorLine(int processId, string line)
        {
        }

        /// <summary>Publish compact provider status or error-type evidence to lifecycle subscribers.</summary>
        /// <param name="processId">Agent process identifier.</param>
        /// <param name="error">Typed provider error evidence.</param>
        protected void PublishProviderError(int processId, RuntimeProviderError error)
        {
            if (error == null) return;
            try { OnProviderErrorReceived?.Invoke(processId, error); }
            catch (Exception ex) { WarnSwallowed("OnProviderErrorReceived handler for process " + processId, ex); }
        }

        /// <summary>
        /// Publish authoritative token usage to lifecycle subscribers.
        /// </summary>
        /// <param name="processId">Agent process identifier.</param>
        /// <param name="usage">Authoritative usage sample.</param>
        protected void PublishTokenUsage(int processId, RuntimeTokenUsage usage)
        {
            try { OnTokenUsageReceived?.Invoke(processId, usage); }
            catch (Exception ex) { WarnSwallowed("OnTokenUsageReceived handler for process " + processId, ex); }
            try { OnProviderProgressReceived?.Invoke(processId, usage); }
            catch (Exception ex) { WarnSwallowed("OnProviderProgressReceived handler for process " + processId, ex); }
        }

        /// <summary>
        /// Record the launched process's identity so a later stop or liveness check can tell it from a process
        /// that reuses its identifier. The launch time taken just before the start stands in when the start time
        /// cannot be read, which happens when the agent has already exited.
        /// </summary>
        private void RecordLaunchIdentity(Process process, DateTime launchUtc)
        {
            DateTime startedUtc;
            try
            {
                startedUtc = process.StartTime.ToUniversalTime();
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception || ex is NotSupportedException)
            {
                WarnSwallowed("reading the start time of process " + process.Id + "; recording the launch time instead", ex);
                startedUtc = launchUtc;
            }

            ProcessSupervisor.RecordLaunchedProcess(process.Id, startedUtc);
        }

        /// <summary>
        /// Kill the process tree of a launch that failed after the process started, and wait briefly for the exit
        /// so the exit handler reports it before the process object is disposed.
        /// </summary>
        private void KillFailedLaunch(Process process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5000);
                }
            }
            catch (Exception ex)
            {
                WarnSwallowed("killing the agent process of a failed launch", ex);
            }
        }

        /// <summary>
        /// Log a failure the runtime deliberately does not propagate, naming what failed, so a swallowed
        /// exception is never silent.
        /// </summary>
        /// <param name="operation">What was being attempted.</param>
        /// <param name="ex">The swallowed exception.</param>
        private void WarnSwallowed(string operation, Exception ex)
        {
            try { _Logging.Warn(_Header + operation + " failed: " + ex.Message); }
            catch (ObjectDisposedException) { }
            catch (IOException)
            {
                // The diagnostic sink can be on the same full filesystem. Never let reporting
                // a contained I/O failure throw back into an asynchronous process callback.
                try { Console.Error.WriteLine(_Header + operation + " failed (" + ex.GetType().Name + ")."); }
                catch (IOException) { }
                catch (ObjectDisposedException) { }
            }
        }

        private void WriteLogRecord(int processId, StreamWriter? writer, string? text)
        {
            if (writer == null) return;
            LogWriteState state = _LogWriteStates.GetValue(writer, _ => new LogWriteState());
            IOException? failure = null;
            lock (state)
            {
                if (state.Failed) return;
                try { writer.WriteLine(text); }
                catch (ObjectDisposedException) { }
                catch (IOException ex) { state.Failed = true; failure = ex; }
            }
            if (failure != null) ReportLogFailure(processId, failure);
        }

        private void CloseLogWriter(int processId, StreamWriter? writer)
        {
            if (writer == null) return;
            LogWriteState state = _LogWriteStates.GetValue(writer, _ => new LogWriteState());
            IOException? failure = null;
            lock (state)
            {
                try { writer.Dispose(); }
                catch (ObjectDisposedException) { }
                catch (IOException ex)
                {
                    if (!state.Failed) failure = ex;
                    state.Failed = true;
                }
            }
            if (failure != null) ReportLogFailure(processId, failure);
        }

        private bool LogCaptureFailed(StreamWriter? writer)
        {
            if (writer == null || !_LogWriteStates.TryGetValue(writer, out LogWriteState? state)) return false;
            lock (state) return state.Failed;
        }

        private void ReportLogFailure(int processId, IOException failure)
        {
            WarnSwallowed("persisting the runtime log for process " + processId, failure);
            try { OnOutputReceived?.Invoke(processId, "[ARMADA:LOG_CAPTURE_FAILED] Runtime log persistence failed; this launch cannot succeed."); }
            catch (Exception ex) { WarnSwallowed("reporting log capture failure for process " + processId, ex); }
        }

        /// <summary>
        /// Give the launch its own temporary directory, removed when the agent exits. Agent CLIs and the
        /// builds and tests they run write to the temporary directory and leave files behind: the OpenCode
        /// CLI unpacks a native library of several megabytes there on every run and never deletes it. A
        /// caller that sets TMPDIR keeps its own directory, and that directory is never removed. A TMPDIR the
        /// admiral itself inherited is not the launch's choice: the launch directory is made inside it.
        /// </summary>
        /// <param name="startInfo">Launch start info.</param>
        /// <param name="inheritedTempDirectory">TMPDIR the launch inherited before its own variables applied.</param>
        /// <returns>The directory this launch owns, or null when the caller supplied one.</returns>
        private string? ApplyLaunchTempDirectory(ProcessStartInfo startInfo, string? inheritedTempDirectory)
        {
            if (startInfo.Environment.TryGetValue("TMPDIR", out string? supplied)
                && !String.IsNullOrEmpty(supplied)
                && !String.Equals(supplied, inheritedTempDirectory, StringComparison.Ordinal)) return null;

            string directory = Path.Combine(Path.GetTempPath(), "armada-launch-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(directory);
            }
            catch (Exception ex)
            {
                WarnSwallowed("creating launch temporary directory " + directory + "; the agent uses the shared one", ex);
                return null;
            }

            startInfo.Environment["TMPDIR"] = directory;
            startInfo.Environment["TMP"] = directory;
            startInfo.Environment["TEMP"] = directory;
            return directory;
        }

        private void DeleteLaunchTempDirectory(string? directory)
        {
            if (String.IsNullOrEmpty(directory)) return;
            try
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
            catch (Exception ex)
            {
                WarnSwallowed("removing launch temporary directory " + directory, ex);
            }
        }

        /// <summary>
        /// Build the environment a local captain process receives: inherited admiral secrets are removed first,
        /// then the launch's own variables, the isolation overrides, the shared captain settings and the
        /// runtime's variables are applied, so a variable the launch sets on purpose survives the scrub.
        /// </summary>
        internal void PrepareCaptainEnvironment(
            ProcessStartInfo startInfo,
            Dictionary<string, string>? environment,
            CaptainLaunchIsolationPlan? isolationPlan,
            Captain? captain,
            string? model)
        {
            ScrubAdmiralSecrets(startInfo);

            if (environment != null)
            {
                foreach (KeyValuePair<string, string> kvp in environment)
                    startInfo.Environment[kvp.Key] = kvp.Value;
            }

            if (isolationPlan != null)
            {
                foreach (KeyValuePair<string, string> kvp in isolationPlan.EnvironmentOverrides)
                    startInfo.Environment[kvp.Key] = kvp.Value;
            }

            ApplySharedCaptainEnvironment(startInfo);
            ApplyEnvironment(startInfo, captain, model);
        }

        private void ScrubAdmiralSecrets(ProcessStartInfo startInfo)
        {
            IReadOnlyCollection<string>? configured = null;
            try { configured = AdmiralSecretNames?.Invoke(); }
            catch (Exception ex) { WarnSwallowed("reading the configured admiral secret names; only the credential-name rule applies", ex); }
            List<string> removed = CaptainEnvironmentScrub.Scrub(startInfo.Environment, configured);
            if (removed.Count > 0)
                _Logging.Debug(_Header + "removed " + removed.Count + " admiral secret variable(s) from the captain environment: " + String.Join(", ", removed));
        }

        private static void ApplySharedCaptainEnvironment(ProcessStartInfo startInfo)
        {
            BuildServerEnvironment.Apply(startInfo);
        }

        /// <summary>
        /// Collect the variables a launch sets or changes, as a local launch would apply them. Variables the
        /// launch only inherits are not included, and removals cannot be expressed to a runner.
        /// </summary>
        private Dictionary<string, string> CollectLaunchVariables(string? model, Captain? captain)
        {
            ProcessStartInfo probe = new ProcessStartInfo();
            Dictionary<string, string?> inherited = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, string?> variable in probe.Environment) inherited[variable.Key] = variable.Value;

            ApplySharedCaptainEnvironment(probe);
            ApplyEnvironment(probe, captain, model);

            Dictionary<string, string> variables = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, string?> variable in probe.Environment)
            {
                if (variable.Value == null) continue;
                if (inherited.TryGetValue(variable.Key, out string? prior) && String.Equals(prior, variable.Value, StringComparison.Ordinal)) continue;
                variables[variable.Key] = variable.Value;
            }
            AddLaunchVariables(variables, model, captain);
            return variables;
        }

        private StreamWriter? OpenLogWriter(string? logFilePath, bool preserveExisting = false)
        {
            // If a prior launch leaked a handle on the canonical log path (admiral crash mid-launch,
            // orphan agent process holding the file), `new StreamWriter(...)` throws IOException due to
            // the share violation and the entire launch fails in a tight retry loop. Recover by
            // falling back to a unique-suffix path; the dashboard and admiral's log API continue to
            // read the canonical path until log rotation merges them.
            if (String.IsNullOrEmpty(logFilePath)) return null;
            Directory.CreateDirectory(Path.GetDirectoryName(logFilePath)!);

            // Best-effort cleanup: if the canonical log file is stale and not held by any live
            // process, deleting it now lets us reopen it cleanly. Failures are silent; the open
            // below will either succeed (we win the race) or throw (we fall through to the suffix path).
            if (!preserveExisting)
            {
                try { if (File.Exists(logFilePath)) File.Delete(logFilePath); }
                catch (Exception ex) { WarnSwallowed("deleting stale log " + logFilePath + " (the open below falls back to a suffixed path if it is locked)", ex); }
            }

            try
            {
                return new StreamWriter(logFilePath, append: true) { AutoFlush = true };
            }
            catch (IOException)
            {
                // Canonical path locked. Suffix with a unix timestamp so successive
                // retries within the same second still pick distinct paths.
                string baseName = Path.GetFileNameWithoutExtension(logFilePath);
                string ext = Path.GetExtension(logFilePath);
                string dir = Path.GetDirectoryName(logFilePath)!;
                string suffix = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
                string actualLogFilePath = Path.Combine(dir, baseName + "." + suffix + ext);
                _Logging.Warn(_Header + "canonical log path locked (" + logFilePath +
                    "); falling back to " + actualLogFilePath);
                return new StreamWriter(actualLogFilePath, append: true) { AutoFlush = true };
            }
        }

        private async Task WriteLaunchHeaderAsync(StreamWriter logWriter, string command, List<string> args, string prompt)
        {
            string timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
            string argsJoined = String.Join(" ", args);
            // Write command on first line, then prompt content preserving newlines.
            // Runtimes that deliver the prompt via stdin (UsePromptStdin) do not include the
            // prompt text in their CLI arguments, so the header would otherwise lose the
            // role/persona preamble and mission instructions. Echo the prompt parameter for
            // those runtimes so the mission log always shows which role the captain is
            // running as. A runtime that passes the prompt as an argument shows it from the
            // argument list.
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

        /// <summary>
        /// Handle one stdout line from a local process or a Harbor job: telemetry, log records and events.
        /// </summary>
        private void EmitStdoutLine(int processId, string line, StreamWriter? logWriter)
        {
            try { HandleRawOutputLine(processId, line); }
            catch (Exception ex) { WarnSwallowed("parsing runtime telemetry", ex); }

            foreach (string outputLine in TransformOutputRecords(line))
            {
                // A runtime may transform a structured event to empty to SUPPRESS it from
                // the mission log (e.g. OpenCode tool_use / step events). Writing an empty
                // string would emit a blank log line, so skip suppressed lines entirely --
                // this keeps the log tight and has no markers to detect anyway.
                if (String.IsNullOrEmpty(outputLine)) continue;

                try { _Logging.Debug(_Header + "[stdout] " + outputLine); }
                catch (ObjectDisposedException) { }
                catch (IOException ex) { WarnSwallowed("logging stdout diagnostics", ex); }
                WriteLogRecord(processId, logWriter, outputLine);

                try { OnOutputReceived?.Invoke(processId, outputLine); }
                catch (Exception ex) { WarnSwallowed("OnOutputReceived handler for process " + processId, ex); }

                // Raised only here, never from the stderr handler below. An interactive
                // consumer capturing a reply must not pick up CLI banners and prompt echoes.
                try { OnStdoutReceived?.Invoke(processId, outputLine); }
                catch (Exception ex) { WarnSwallowed("OnStdoutReceived handler for process " + processId, ex); }
            }
        }

        /// <summary>
        /// Handle one stderr line from a local process or a Harbor job.
        /// </summary>
        private void EmitStderrLine(int processId, string line, StreamWriter? logWriter)
        {
            try { HandleRawErrorLine(processId, line); }
            catch (Exception ex) { WarnSwallowed("parsing runtime provider error evidence", ex); }

            try { _Logging.Debug(_Header + "[stderr] " + line); }
            catch (ObjectDisposedException) { }
            catch (IOException ex) { WarnSwallowed("logging stderr diagnostics", ex); }

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
            bool quotaSignal = ProviderQuotaLimitDetector.IsQuotaLimitSignal(line);
            // Preserve standalone reset-time lines so the admiral's failure-lifecycle
            // code can later call TryParseRetryAfterUtc on the full stderr text and
            // compute an accurate quarantine deadline. The detector owns which lines
            // carry a parseable reset, so the gate and the parser cannot drift apart.
            bool resetTimeLine = ProviderQuotaLimitDetector.IsResetTimeLine(line);
            if (WriteStderrToLogFile || quotaSignal || resetTimeLine)
            {
                WriteLogRecord(processId, logWriter, "[stderr] " + line);
            }

            // Treat stderr as runtime output for heartbeat/progress/output capture.
            // Some agent CLIs emit useful diagnostics or status lines on stderr.
            try { OnOutputReceived?.Invoke(processId, line); }
            catch (Exception ex) { WarnSwallowed("OnOutputReceived handler for stderr of process " + processId, ex); }
        }

        /// <summary>
        /// Finish a local process or a Harbor job: held records, the final-message echo, the exit line, and the
        /// exit event. The log writer is closed here.
        /// </summary>
        private void CompleteExit(int processId, int? code, StreamWriter? logWriter, string? finalMessageFilePath, RuntimeProcessExitEvidence? exitEvidence)
        {
            // Give the runtime a chance to write records it was still holding. A runtime that
            // correlates a tool call with a later result event has nothing to write when the
            // process is killed mid-call -- and that unfinished call is the most useful line
            // in the log when diagnosing a hang. Written here, while the writer is open.
            try
            {
                foreach (string exitRecord in BuildProcessExitRecords())
                {
                    if (String.IsNullOrEmpty(exitRecord)) continue;

                    WriteLogRecord(processId, logWriter, exitRecord);

                    try { OnOutputReceived?.Invoke(processId, exitRecord); }
                    catch (Exception ex) { WarnSwallowed("OnOutputReceived handler for an exit record of process " + processId, ex); }
                }
            }
            catch (Exception ex) { WarnSwallowed("building process-exit records", ex); }

            // Parity echo: when stderr is suppressed from the log file, the agent's
            // final answer (captured via the runtime's final-message file) would
            // otherwise never appear in the mission log. Echo it here while the
            // writer is still open. Never let this throw out of the handler.
            if (!WriteStderrToLogFile && !String.IsNullOrEmpty(finalMessageFilePath))
            {
                try
                {
                    string? finalMsg = null;
                    if (File.Exists(finalMessageFilePath)) finalMsg = File.ReadAllText(finalMessageFilePath);
                    else if (code == 0)
                    {
                        lock (_FinalReplyLock) finalMsg = _FinalReplyRequested ? _FinalReplyText : null;
                    }
                    if (!String.IsNullOrWhiteSpace(finalMsg))
                    {
                        WriteLogRecord(processId, logWriter, null);
                        WriteLogRecord(processId, logWriter, "=== Final message ===");
                        WriteLogRecord(processId, logWriter, finalMsg);
                    }
                }
                catch (Exception ex) { WarnSwallowed("echoing the final message of process " + processId + " into the mission log", ex); }
            }

            WriteLogRecord(processId, logWriter, "[" + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss") + "] Agent exited with code " + (code?.ToString() ?? "unknown"));
            CloseLogWriter(processId, logWriter);

            // Drain and close before deciding success: the footer or final flush can be the
            // first failed write. Preserve provider-owned files, but never synthesize success
            // or report a successful captain when the mission log could not be persisted.
            bool logCaptureFailed = LogCaptureFailed(logWriter);
            if (logCaptureFailed) code = LogCaptureFailureExitCode;
            WriteCapturedFinalReply(processId, code, finalMessageFilePath);
            exitEvidence?.Complete(code, logCaptureFailed);

            // Notify subscribers that the process has exited BEFORE disposing.
            // Disposing first invalidates the PID, which can cause the health check
            // to race with the exit handler and trigger spurious recovery.
            try { OnProcessExited?.Invoke(processId, code); }
            catch (Exception ex) { WarnSwallowed("OnProcessExited handler for process " + processId, ex); }
        }

        private void WriteCapturedFinalReply(int processId, int? code, string? finalMessageFilePath)
        {
            if (code != 0 || String.IsNullOrEmpty(finalMessageFilePath)) return;

            string? text;
            lock (_FinalReplyLock)
            {
                if (!_FinalReplyRequested) return;
                text = _FinalReplyText;
            }

            if (String.IsNullOrWhiteSpace(text) || File.Exists(finalMessageFilePath)) return;

            string temporaryPath = finalMessageFilePath + ".tmp-" + Guid.NewGuid().ToString("N");
            bool ownsTemporaryFile = false;
            try
            {
                using (FileStream file = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    ownsTemporaryFile = true;
                    using (StreamWriter writer = new StreamWriter(file, new UTF8Encoding(false, true)))
                    {
                        writer.Write(text);
                    }
                }

                try
                {
                    File.Move(temporaryPath, finalMessageFilePath, false);
                    ownsTemporaryFile = false;
                }
                catch (IOException ex)
                {
                    if (!File.Exists(finalMessageFilePath))
                        WarnSwallowed("publishing the final reply for process " + processId, ex);
                }
            }
            catch (Exception ex)
            {
                WarnSwallowed("writing the final reply for process " + processId, ex);
            }
            finally
            {
                if (ownsTemporaryFile && File.Exists(temporaryPath))
                {
                    try { File.Delete(temporaryPath); }
                    catch (Exception ex) { WarnSwallowed("removing the temporary final reply for process " + processId, ex); }
                }
            }
        }

        private static void AppendIsolationArguments(List<string> arguments, CaptainLaunchIsolationPlan? plan)
        {
            if (plan == null) return;

            for (int i = 0; i < plan.ExtraArguments.Count; i++)
            {
                string argument = plan.ExtraArguments[i];
                if (String.Equals(argument, "--strict-mcp-config", StringComparison.Ordinal) &&
                    arguments.Contains(argument))
                {
                    continue;
                }

                if (String.Equals(argument, "--setting-sources", StringComparison.Ordinal) &&
                    arguments.Contains(argument))
                {
                    if (i + 1 < plan.ExtraArguments.Count) i++;
                    continue;
                }

                arguments.Add(argument);
            }
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

        #region Private-Types

        private sealed class LogWriteState
        {
            public bool Failed;
        }

        /// <summary>
        /// Feeds a Harbor job's output chunks into the runtime's line handling. Chunks are split into lines per
        /// stream; a partial final line is delivered when the job ends.
        /// </summary>
        private sealed class HarborRuntimeEvents : IHarborProcessCompletionEvents, IDisposable
        {
            private readonly BaseAgentRuntime _Runtime;
            private readonly object _Lock = new object();
            private readonly StringBuilder _Stdout = new StringBuilder();
            private readonly StringBuilder _Stderr = new StringBuilder();
            private StreamWriter? _Writer;
            private readonly string? _FinalMessageFilePath;
            private bool _Ended;
            private RuntimeProcessExitEvidence? _ExitEvidence;

            public HarborRuntimeEvents(BaseAgentRuntime runtime, StreamWriter? writer, string? finalMessageFilePath = null)
            {
                _Runtime = runtime;
                _Writer = writer;
                _FinalMessageFilePath = finalMessageFilePath;
            }

            public void OnOutput(int processId, HarborOutputStreamEnum stream, string data)
            {
                lock (_Lock)
                {
                    if (_Ended) return;
                    _ExitEvidence ??= ProcessSupervisor.TrackRuntimeExit(processId);
                    bool isStderr = stream == HarborOutputStreamEnum.Stderr;
                    StringBuilder buffer = isStderr ? _Stderr : _Stdout;
                    buffer.Append(data);
                    string text = buffer.ToString();
                    int lastBreak = text.LastIndexOf('\n');
                    if (lastBreak < 0) return;
                    buffer.Clear();
                    buffer.Append(text.Substring(lastBreak + 1));
                    foreach (string line in text.Substring(0, lastBreak).Split('\n')) Emit(processId, isStderr, line);
                }
            }

            public void OnExited(int processId, int? exitCode, string? failureReason)
            {
                lock (_Lock)
                {
                    if (_Ended) return;
                    _ExitEvidence ??= ProcessSupervisor.TrackRuntimeExit(processId);
                    _Ended = true;
                    Emit(processId, false, _Stdout.ToString());
                    Emit(processId, true, _Stderr.ToString());
                    _Stdout.Clear();
                    _Stderr.Clear();
                    if (!String.IsNullOrEmpty(failureReason))
                    {
                        _Runtime.WriteLogRecord(processId, _Writer, "[" + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss") + "] Harbor job ended without an exit: " + failureReason);
                    }
                    StreamWriter? writer = _Writer;
                    _Writer = null;
                    _Runtime.CompleteExit(processId, exitCode, writer, _FinalMessageFilePath, _ExitEvidence);
                }
            }

            public async Task OnExitedAsync(int processId, int? exitCode, string? failureReason)
            {
                OnExited(processId, exitCode, failureReason);
                Func<int, Task>? waiter = _Runtime.HarborExitCompletionWaiter;
                if (waiter != null)
                    await waiter(processId).ConfigureAwait(false);
            }

            public void Dispose() => CloseLog();

            public void CloseLog()
            {
                lock (_Lock)
                {
                    _Ended = true;
                    try { _Writer?.Dispose(); }
                    catch (ObjectDisposedException) { }
                    catch (IOException ex) { _Runtime.WarnSwallowed("closing the log of a failed Harbor launch", ex); }
                    _Writer = null;
                }
            }

            private void Emit(int processId, bool isStderr, string line)
            {
                string trimmed = line.EndsWith("\r", StringComparison.Ordinal) ? line.Substring(0, line.Length - 1) : line;
                if (String.IsNullOrEmpty(trimmed)) return;
                if (isStderr) _Runtime.EmitStderrLine(processId, trimmed, _Writer);
                else _Runtime.EmitStdoutLine(processId, trimmed, _Writer);
            }
        }

        #endregion
    }
}
