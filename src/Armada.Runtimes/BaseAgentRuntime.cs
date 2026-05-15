namespace Armada.Runtimes
{
    using System.Diagnostics;
    using System.Text;
    using Armada.Core.Models;
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
        /// Event raised immediately after the agent process starts and a PID is available.
        /// </summary>
        public event Action<int>? OnProcessStarted;

        /// <summary>
        /// Event raised when the agent process exits.
        /// Parameters: processId, exitCode (null if unavailable).
        /// </summary>
        public event Action<int, int?>? OnProcessExited;

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
                StandardInputEncoding = Encoding.UTF8,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                CreateNoWindow = true
            };

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

            ApplyEnvironment(startInfo, captain);

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
                // Write command on first line, then prompt content preserving newlines
                string firstFlag = "";
                string promptContent = argsJoined;
                int promptStart = argsJoined.IndexOf("Mission:");
                if (promptStart > 0)
                {
                    firstFlag = argsJoined.Substring(0, promptStart).Trim();
                    promptContent = argsJoined.Substring(promptStart);
                }
                await logWriter.WriteLineAsync("[" + timestamp + "] Agent starting: " + command + " " + firstFlag).ConfigureAwait(false);
                await logWriter.WriteLineAsync(promptContent).ConfigureAwait(false);
                await logWriter.WriteLineAsync("").ConfigureAwait(false);
            }

            Process process = new Process { StartInfo = startInfo };

            process.OutputDataReceived += (sender, e) =>
            {
                if (!String.IsNullOrEmpty(e.Data))
                {
                    _Logging.Debug(_Header + "[stdout] " + e.Data);
                    try { logWriter?.WriteLine(e.Data); }
                    catch (ObjectDisposedException) { }

                    try { OnOutputReceived?.Invoke(process.Id, e.Data); }
                    catch { }
                }
            };

            process.ErrorDataReceived += (sender, e) =>
            {
                if (!String.IsNullOrEmpty(e.Data))
                {
                    _Logging.Debug(_Header + "[stderr] " + e.Data);
                    try { logWriter?.WriteLine("[stderr] " + e.Data); }
                    catch (ObjectDisposedException) { }

                    // Treat stderr as runtime output for heartbeat/progress/output capture.
                    // Some agent CLIs emit useful diagnostics or status lines on stderr.
                    try { OnOutputReceived?.Invoke(process.Id, e.Data); }
                    catch { }
                }
            };

            process.Exited += (sender, e) =>
            {
                int? code = null;
                int processId = 0;
                try { processId = process.Id; } catch { }
                try { code = ((Process?)sender)?.ExitCode; } catch { }
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
                    if (UsePromptStdin)
                    {
                        await process.StandardInput.WriteAsync(prompt).ConfigureAwait(false);
                        await process.StandardInput.FlushAsync().ConfigureAwait(false);
                    }

                    // Close stdin after writing any prompt content so the agent doesn't block
                    // waiting for piped input.
                    process.StandardInput.Close();
                }

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                _Logging.Info(_Header + "started process " + process.Id + " (" + command + ") in " + workingDirectory);

                return process.Id;
            }
            catch
            {
                // Launch failed before the process is alive. process.Exited will not fire,
                // so dispose the writer + process here to release the file/pipe handles.
                try { logWriter?.Dispose(); } catch { }
                try { process.Dispose(); } catch { }
                throw;
            }
        }

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
                if (!process.HasExited)
                {
                    // Try graceful shutdown first by closing stdin
                    try
                    {
                        process.StandardInput.Close();
                    }
                    catch
                    {
                        // stdin may already be closed
                    }

                    // Wait up to 10 seconds for graceful exit
                    bool exited = process.WaitForExit(10000);
                    if (!exited)
                    {
                        _Logging.Warn(_Header + "process " + processId + " did not exit gracefully, killing");
                        process.Kill(entireProcessTree: true);
                    }
                }

                _Logging.Info(_Header + "stopped process " + processId);
            }
            catch (ArgumentException)
            {
                _Logging.Debug(_Header + "process " + processId + " already exited");
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
            try
            {
                Process process = Process.GetProcessById(processId);
                return !process.HasExited;
            }
            catch (ArgumentException)
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
        /// Apply runtime-specific environment variables to the process start info.
        /// The captain instance is forwarded so derived runtimes can read
        /// per-captain settings (e.g. <c>Captain.RuntimeOptionsJson</c>).
        /// </summary>
        protected virtual void ApplyEnvironment(ProcessStartInfo startInfo, Captain? captain)
        {
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
