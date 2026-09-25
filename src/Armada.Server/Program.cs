namespace Armada.Server
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Net.Http;
    using System.Runtime.InteropServices;
    using System.Runtime.Loader;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using System.Threading;
    using SyslogLogging;
    using Armada.Core;
    using Armada.Core.Database;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;

    /// <summary>
    /// Admiral server entry point.
    /// </summary>
    public class Program
    {
        private static ArmadaSettings _Settings = new ArmadaSettings();
        private static LoggingModule _Logging = null!;
        private static ArmadaServer _Server = null!;
        private static bool _ShuttingDown = false;
        private static CancellationTokenSource _TokenSource = new CancellationTokenSource();

        /// <summary>
        /// Run the self-deploy supervisor for one operation, or recovery when no operation id is given.
        /// Exit code 0 means the record proves a healthy owner or no restart record exists.
        /// </summary>
        private static async Task<int> RunSelfDeployCutoverAsync(string? operationId)
        {
            try
            {
                SelfDeployCutoverComponents components = SelfDeployCutoverComponents.CreateDefault(_Settings.DataDirectory, _Settings.SelfDeploy);
                components.Options.HoldAfterCandidateLaunch = SelfDeployRehearsal.HoldAfterCandidateLaunch();
                using (HttpClient client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan })
                {
                    SelfDeployCutoverCoordinator coordinator = new SelfDeployCutoverCoordinator(
                        components.ProcessHost,
                        new SelfDeployHttpHealthProbe(client, TimeSpan.FromSeconds(5)),
                        components.Artifacts,
                        new SelfDeployDatabaseSchemaVersionReader(_Settings.Database, _Logging),
                        components.LaunchPlanner,
                        components.Records,
                        components.Options);

                    SelfDeployCutoverResult result;
                    if (operationId == null)
                    {
                        result = await coordinator.RecoverAsync().ConfigureAwait(false);
                    }
                    else
                    {
                        SelfDeployProcessIdentity? supervisor = components.ProcessHost.Capture(Environment.ProcessId);
                        if (supervisor == null)
                        {
                            ReportSelfDeploy("SELF-DEPLOY supervisor_identity_unverified");
                            return 1;
                        }
                        result = await coordinator.SuperviseAsync(operationId, supervisor).ConfigureAwait(false);
                    }

                    ReportSelfDeploy("SELF-DEPLOY " + (result.State?.ToString() ?? "NoRecord") + ": " + result.Reason);
                    bool nothingToRecover = operationId == null && result.State == null && result.Reason == "no_restart_record";
                    return result.HealthyOwnerProven || nothingToRecover ? 0 : 1;
                }
            }
            catch (SelfDeployCutoverException ex)
            {
                ReportSelfDeploy("SELF-DEPLOY failed closed: " + ex.FailureReason);
                return 1;
            }
        }

        private static void ReportSelfDeploy(string line)
        {
            _Logging.Info("[Program] " + line);
            Console.WriteLine(line);
        }

        static async Task Main(string[] args)
        {
            // Catch unhandled exceptions so the server doesn't silently die
            AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
            {
                Exception? ex = e.ExceptionObject as Exception;
                string msg = "[Program] FATAL unhandled exception: " + (ex?.ToString() ?? e.ExceptionObject?.ToString() ?? "unknown");
                try { _Logging?.Warn(msg); } catch { }
                Console.Error.WriteLine(msg);
                try { File.AppendAllText(Path.Combine(Constants.DefaultDataDirectory, "crash.log"), DateTime.UtcNow.ToString("o") + " " + msg + Environment.NewLine); } catch { }
            };

            TaskScheduler.UnobservedTaskException += (sender, e) =>
            {
                string msg = "[Program] unobserved task exception: " + e.Exception?.ToString();
                try { _Logging?.Warn(msg); } catch { }
                Console.Error.WriteLine(msg);
                try { File.AppendAllText(Path.Combine(Constants.DefaultDataDirectory, "crash.log"), DateTime.UtcNow.ToString("o") + " " + msg + Environment.NewLine); } catch { }
                e.SetObserved(); // Prevent process termination
            };

            Console.WriteLine(@"                        _      ");
            Console.WriteLine(@" __ _ _ _ _ __  __ _ __| |__ _ ");
            Console.WriteLine(@"/ _` | '_| '  \/ _` / _` / _` |");
            Console.WriteLine(@"\__,_|_| |_|_|_\__,_\__,_\__,_|");
            Console.WriteLine();
            Console.WriteLine(Constants.ProductName + " Admiral v" + Constants.ProductVersion);
            Console.WriteLine();

            // Load settings
            string settingsPath = Path.Combine(Constants.DefaultDataDirectory, "settings.json");
            if (File.Exists(settingsPath))
            {
                string json = File.ReadAllText(settingsPath);
                JsonSerializerOptions settingsOpts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                settingsOpts.Converters.Add(new JsonStringEnumConverter());
                ArmadaSettings? loaded = JsonSerializer.Deserialize<ArmadaSettings>(json, settingsOpts);
                if (loaded != null) _Settings = loaded;
            }

            string? envGhPath = Environment.GetEnvironmentVariable("ARMADA_GH_PATH");
            if (!String.IsNullOrWhiteSpace(envGhPath))
                _Settings.GhCliPath = envGhPath.Trim();

            string? envGlabPath = Environment.GetEnvironmentVariable("ARMADA_GLAB_PATH");
            if (!String.IsNullOrWhiteSpace(envGlabPath))
                _Settings.GlabCliPath = envGlabPath.Trim();

            // Initialize directories
            _Settings.InitializeDirectories();

            // Initialize logging
            InitializeLogging();

            if (Array.IndexOf(args, "--validate-database") >= 0)
            {
                if (args.Length != 1)
                    throw new ArgumentException("--validate-database must be the only argument.");
                using (DatabaseDriver database = DatabaseDriverFactory.Create(_Settings.Database, _Logging))
                {
                    await database.InitializeAsync().ConfigureAwait(false);
                    Console.WriteLine("DATABASE VALIDATION PASSED; schema version "
                        + await database.GetSchemaVersionAsync().ConfigureAwait(false));
                }
                return;
            }

            if (Array.IndexOf(args, SelfDeployDotnetLaunchPlanner.SuperviseArgument) >= 0)
            {
                if (args.Length != 2 || args[0] != SelfDeployDotnetLaunchPlanner.SuperviseArgument)
                    throw new ArgumentException(SelfDeployDotnetLaunchPlanner.SuperviseArgument + " requires exactly one operation id.");
                Environment.ExitCode = await RunSelfDeployCutoverAsync(args[1]).ConfigureAwait(false);
                return;
            }

            if (Array.IndexOf(args, SelfDeployDotnetLaunchPlanner.RecoverArgument) >= 0)
            {
                if (args.Length != 1)
                    throw new ArgumentException(SelfDeployDotnetLaunchPlanner.RecoverArgument + " must be the only argument.");
                Environment.ExitCode = await RunSelfDeployCutoverAsync(null).ConfigureAwait(false);
                return;
            }

            string? rehearsalCandidate = null;
            if (Array.IndexOf(args, SelfDeployRehearsal.RehearseArgument) >= 0)
            {
                if (args.Length != 2 || args[0] != SelfDeployRehearsal.RehearseArgument)
                    throw new ArgumentException(SelfDeployRehearsal.RehearseArgument + " requires exactly one candidate server assembly path.");
                if (!SelfDeployRehearsal.IsAuthorized())
                {
                    string denied = "[Program] self-deploy rehearsal refused: set " + SelfDeployRehearsal.GateVariable + "="
                        + SelfDeployRehearsal.GateValue + " and " + Constants.DataDirectoryOverrideVariable + " (or " + Constants.DataDirectoryAliasVariable + ")"
                        + " to a disposable data directory.";
                    Console.Error.WriteLine(denied);
                    Environment.ExitCode = 2;
                    return;
                }
                rehearsalCandidate = args[1];
            }

            SelfDeployRestartRecordStore restartRecords = new SelfDeployRestartRecordStore(
                SelfDeployRestartRecordStore.DirectoryFor(_Settings.DataDirectory));
            SelfDeployStartupDecision startup = SelfDeployStartupGuard.Evaluate(
                await restartRecords.ReadAsync().ConfigureAwait(false),
                Environment.GetEnvironmentVariable(SelfDeployRestartRecordStore.OperationIdVariable));
            if (!startup.Allowed)
            {
                string refusal = "[Program] startup refused by the self-deploy restart record (" + startup.Reason + "): "
                    + restartRecords.RecordPath + ". Run with " + SelfDeployDotnetLaunchPlanner.RecoverArgument
                    + " to drive the restart to a terminal state.";
                _Logging.Warn(refusal);
                Console.Error.WriteLine(refusal);
                Environment.ExitCode = 3;
                return;
            }

            _Logging.Info("[Program] starting Admiral on port " + _Settings.AdmiralPort);

            // Build and run server
            EventWaitHandle waitHandle = new EventWaitHandle(false, EventResetMode.AutoReset);

            _Server = new ArmadaServer(_Logging, _Settings);
            _Server.OnStopping = () => waitHandle.Set();
            await _Server.StartAsync().ConfigureAwait(false);

            Console.WriteLine("Admiral running on port " + _Settings.AdmiralPort);
            Console.WriteLine("MCP server on port " + _Settings.McpPort);
            Console.WriteLine("WebSocket endpoint at /ws");
            Console.WriteLine("Dashboard: http://localhost:" + _Settings.AdmiralPort + "/dashboard");

            if (rehearsalCandidate != null)
            {
                SelfDeployService? selfDeploy = _Server.SelfDeploy;
                bool exitRequested = selfDeploy != null
                    && await selfDeploy.RehearseCutoverAsync(rehearsalCandidate).ConfigureAwait(false);
                if (exitRequested)
                {
                    Console.WriteLine("SELF-DEPLOY REHEARSAL EXIT REQUESTED");
                }
                else
                {
                    Console.WriteLine("SELF-DEPLOY REHEARSAL BLOCKED: " + (selfDeploy?.LastCutoverBlockReason ?? "self_deploy_service_unavailable"));
                    Environment.ExitCode = 1;
                    waitHandle.Set();
                }
            }

            // Wait for shutdown signal (Ctrl+C, SIGTERM, API stop, or assembly unload)
            AssemblyLoadContext.Default.Unloading += (ctx) => waitHandle.Set();
            Console.CancelKeyPress += (sender, e) =>
            {
                e.Cancel = true;

                if (!_ShuttingDown)
                {
                    Console.WriteLine(
                        Environment.NewLine +
                        Environment.NewLine +
                        "Shutdown requested" +
                        Environment.NewLine +
                        Environment.NewLine);
                    _TokenSource.Cancel();
                    _ShuttingDown = true;

                    waitHandle.Set();
                }
            };

            // A container stop sends SIGTERM. The runtime's default turns it into process exit as soon
            // as the unloading handlers return, before this thread reaches Stop, so the shutdown
            // sequence never ran on a container stop. Cancel the default and let the wait below end.
            PosixSignalRegistration? sigterm = null;
            try
            {
                sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
                {
                    context.Cancel = true;
                    if (!_ShuttingDown)
                    {
                        _ShuttingDown = true;
                        _Logging.Info("[Program] SIGTERM received; stopping");
                        waitHandle.Set();
                    }
                });
            }
            catch (PlatformNotSupportedException)
            {
                // The unloading handler above still ends the wait where SIGTERM cannot be handled.
            }

            bool waitHandleSignal = false;
            do
            {
                waitHandleSignal = waitHandle.WaitOne(1000);
            }
            while (!waitHandleSignal);

            _Server.Stop();
            _Logging.Info("[Program] stopped at " + DateTime.UtcNow.ToString("o"));
            sigterm?.Dispose();
        }

        private static void InitializeLogging()
        {
            List<SyslogServer> syslogServers = _Settings.SyslogServers ?? new List<SyslogServer>();

            _Logging = new LoggingModule(syslogServers, true);
            _Logging.Settings.EnableConsole = true;
            _Logging.Settings.EnableColors = true;
            _Logging.Settings.MinimumSeverity = Severity.Debug;
            _Logging.Settings.FileLogging = FileLoggingMode.FileWithDate;
            if (!Directory.Exists(_Settings.LogDirectory))
            {
                Directory.CreateDirectory(_Settings.LogDirectory);
            }

            _Logging.Settings.LogFilename = Path.Combine(_Settings.LogDirectory, "admiral.log");
        }
    }
}
