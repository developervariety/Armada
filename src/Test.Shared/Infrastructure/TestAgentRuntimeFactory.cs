namespace Test.Shared.Infrastructure
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using Armada.Core.Enums;
    using Armada.Core.Settings;
    using Armada.Runtimes;
    using Armada.Runtimes.Interfaces;
    using SyslogLogging;

    /// <summary>
    /// Runtime factory for test hosts. By default every CLI runtime is a <see cref="NonLaunchingAgentRuntime"/>,
    /// so dispatched missions never start the agent CLIs installed on the machine running the suite. A runtime
    /// named in the opt-in set is created as the real adapter and every process it starts is recorded in
    /// <see cref="TestProcessLaunchLog"/>. API-endpoint runtimes run in-process and are created as usual.
    /// </summary>
    public sealed class TestAgentRuntimeFactory : AgentRuntimeFactory
    {
        #region Public-Members

        /// <summary>
        /// Comma-separated runtime names a test host lets start their real CLI, for example <c>ClaudeCode</c>.
        /// Unset means none.
        /// </summary>
        public const string RealRuntimesVariable = "ARMADA_TEST_REAL_RUNTIMES";

        /// <summary>
        /// Runtimes this factory creates as real adapters.
        /// </summary>
        public IReadOnlyCollection<AgentRuntimeEnum> RealRuntimes => _RealRuntimes;

        #endregion

        #region Private-Members

        private readonly LoggingModule _Logging;
        private readonly HashSet<AgentRuntimeEnum> _RealRuntimes;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="logging">Logging module.</param>
        /// <param name="settings">Settings the Admiral under test uses.</param>
        /// <param name="realRuntimes">Runtimes allowed to start their real CLI. Null or empty means none.</param>
        public TestAgentRuntimeFactory(LoggingModule logging, ArmadaSettings settings, IEnumerable<AgentRuntimeEnum>? realRuntimes = null)
            : base(logging, (settings ?? throw new ArgumentNullException(nameof(settings))).CodeIndex.OpenCodeServer, settings.ModelProviders)
        {
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
            _RealRuntimes = new HashSet<AgentRuntimeEnum>(realRuntimes ?? Enumerable.Empty<AgentRuntimeEnum>());
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Read the runtimes opted in through <see cref="RealRuntimesVariable"/>.
        /// </summary>
        /// <returns>Opted-in runtimes; empty when the variable is unset.</returns>
        /// <exception cref="ArgumentException">A listed name is not a runtime that starts a CLI.</exception>
        public static IReadOnlyCollection<AgentRuntimeEnum> ReadOptedInRuntimes()
        {
            return ParseRuntimeNames(Environment.GetEnvironmentVariable(RealRuntimesVariable));
        }

        /// <summary>
        /// Parse a comma-separated list of runtime names.
        /// </summary>
        /// <param name="value">List, or null.</param>
        /// <returns>Parsed runtimes.</returns>
        /// <exception cref="ArgumentException">A listed name is not a runtime that starts a CLI.</exception>
        public static IReadOnlyCollection<AgentRuntimeEnum> ParseRuntimeNames(string? value)
        {
            HashSet<AgentRuntimeEnum> runtimes = new HashSet<AgentRuntimeEnum>();
            if (String.IsNullOrWhiteSpace(value)) return runtimes;

            foreach (string raw in value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string name = raw.Trim();
                if (!Enum.TryParse(name, true, out AgentRuntimeEnum runtime)
                    || runtime == AgentRuntimeEnum.ApiEndpoint
                    || runtime == AgentRuntimeEnum.Custom)
                {
                    throw new ArgumentException(RealRuntimesVariable + " names '" + name
                        + "', which is not a runtime that starts a CLI.");
                }
                runtimes.Add(runtime);
            }

            return runtimes;
        }

        /// <summary>
        /// Return why a suite that needs the real <paramref name="runtime"/> must skip, or null when it can run.
        /// </summary>
        /// <param name="runtime">Runtime the suite needs.</param>
        /// <param name="optedIn">Runtimes the host opted in.</param>
        /// <returns>Named skip reason, or null.</returns>
        public static string? RealRuntimeSkipReason(AgentRuntimeEnum runtime, IReadOnlyCollection<AgentRuntimeEnum> optedIn)
        {
            return RealRuntimeSkipReason(runtime, optedIn, Environment.GetEnvironmentVariable("PATH") ?? "");
        }

        /// <summary>
        /// Return why a suite that needs the real <paramref name="runtime"/> must skip, searching
        /// <paramref name="searchPath"/> for its executable.
        /// </summary>
        /// <param name="runtime">Runtime the suite needs.</param>
        /// <param name="optedIn">Runtimes the host opted in.</param>
        /// <param name="searchPath">Directories to search, separated like PATH.</param>
        /// <returns>Named skip reason, or null.</returns>
        public static string? RealRuntimeSkipReason(AgentRuntimeEnum runtime, IReadOnlyCollection<AgentRuntimeEnum> optedIn, string searchPath)
        {
            if (optedIn == null) throw new ArgumentNullException(nameof(optedIn));
            if (!optedIn.Contains(runtime))
                return "The real " + runtime + " runtime is not opted in. Set " + RealRuntimesVariable + "=" + runtime + " to run it.";

            string command = ResolveCommand(runtime);
            if (!IsOnPath(command, searchPath ?? ""))
                return "The real " + runtime + " runtime is not provisioned: '" + command + "' was not found on PATH.";

            return null;
        }

        /// <summary>
        /// Parse a runtime list for a suite descriptor, which cannot fail discovery: an invalid list reads as
        /// empty here and the runner's own startup check reports it.
        /// </summary>
        /// <param name="value">List, or null.</param>
        /// <returns>Parsed runtimes, or empty when the list is invalid.</returns>
        public static IReadOnlyCollection<AgentRuntimeEnum> ParseRuntimeNamesOrEmpty(string? value)
        {
            try
            {
                return ParseRuntimeNames(value);
            }
            catch (ArgumentException ex)
            {
                Console.Error.WriteLine("[TestAgentRuntimeFactory] " + ex.Message);
                return new HashSet<AgentRuntimeEnum>();
            }
        }

        /// <summary>
        /// Executable the real adapter for <paramref name="runtime"/> starts, read from the adapter itself.
        /// </summary>
        /// <param name="runtime">CLI runtime.</param>
        /// <returns>Executable name or path.</returns>
        public static string ResolveCommand(AgentRuntimeEnum runtime)
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            IAgentRuntime adapter = new AgentRuntimeFactory(logging).Create(runtime);
            MethodInfo? getCommand = adapter.GetType().GetMethod("GetCommand", BindingFlags.Instance | BindingFlags.NonPublic);
            if (getCommand == null)
                throw new InvalidOperationException("Runtime adapter " + adapter.GetType().Name + " exposes no GetCommand method.");
            return (string)getCommand.Invoke(adapter, null)!;
        }

        /// <inheritdoc />
        public override IAgentRuntime Create(AgentRuntimeEnum runtimeType)
        {
            if (!_RealRuntimes.Contains(runtimeType))
            {
                if (runtimeType == AgentRuntimeEnum.ApiEndpoint || runtimeType == AgentRuntimeEnum.Custom)
                    return base.Create(runtimeType);
                return new NonLaunchingAgentRuntime(runtimeType);
            }

            IAgentRuntime runtime = base.Create(runtimeType);
            runtime.OnProcessStarted += processId => TestProcessLaunchLog.Record(new TestProcessLaunch
            {
                RuntimeType = runtimeType,
                LaunchedProcess = true,
                ProcessId = processId,
                ProcessName = ReadProcessName(processId)
            });
            runtime.OnProcessExited += (processId, exitCode) => TestProcessLaunchLog.RecordExit(processId, exitCode);
            return runtime;
        }

        #endregion

        #region Private-Methods

        private string ReadProcessName(int processId)
        {
            try
            {
                using (Process process = Process.GetProcessById(processId))
                {
                    return process.ProcessName;
                }
            }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException)
            {
                _Logging.Debug("[TestAgentRuntimeFactory] process " + processId + " exited before its name was read: " + ex.Message);
                return "exited before its name was read";
            }
        }

        private static bool IsOnPath(string command, string path)
        {
            if (Path.IsPathRooted(command)) return File.Exists(command);

            string[] extensions = OperatingSystem.IsWindows()
                ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';')
                : new[] { "" };
            foreach (string directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                foreach (string extension in extensions)
                {
                    if (File.Exists(Path.Combine(directory, command + extension))) return true;
                }
            }

            return false;
        }

        #endregion
    }
}
