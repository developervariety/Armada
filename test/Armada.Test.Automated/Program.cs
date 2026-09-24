namespace Armada.Test.Automated
{
    using System.Net;
    using System.Net.Sockets;
    using Armada.Core.Enums;
    using Armada.Core.Settings;
    using Armada.Server;
    using Armada.Test.Automated.Suites;
    using Armada.Test.Common;
    using SyslogLogging;
    using global::Test.Shared.Infrastructure;

    public class Program
    {
        public static async Task<int> Main(string[] args)
        {
            // Must run before anything touches Armada.Core.Constants: settings built without an
            // explicit DataDirectory otherwise resolve under the live Armada home and write there.
            TestDataDirectory.Redirect();
            TestDataDirectory.Verify();
            TestProcessEnvironment.RemoveProviderVariablesAndReport();
            // Test repositories are short-lived, so git auto maintenance after commits is start-up cost only.
            TestGitEnvironment.DisableAutoMaintenance();

            CommandLineOptions options;
            IReadOnlyCollection<AgentRuntimeEnum> realRuntimes;

            try
            {
                realRuntimes = TestAgentRuntimeFactory.ReadOptedInRuntimes();
            }
            catch (ArgumentException ex)
            {
                Console.WriteLine("Error: " + ex.Message);
                return 1;
            }

            try
            {
                options = CommandLineOptions.Parse(args);
            }
            catch (ArgumentException ex)
            {
                Console.WriteLine("Error: " + ex.Message);
                Console.WriteLine();
                CommandLineOptions.PrintUsage("Armada.Test.Automated");
                return 1;
            }

            if (options.Help)
            {
                CommandLineOptions.PrintUsage("Armada.Test.Automated");
                return 0;
            }

            // Create temp directory for test server files
            string tempDir = Path.Combine(Path.GetTempPath(), "armada_test_automated_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            // Build database settings
            string defaultSqlitePath = Path.Combine(tempDir, "armada.db");
            DatabaseSettings dbSettings;

            try
            {
                dbSettings = options.BuildDatabaseSettings(defaultSqlitePath);
            }
            catch (ArgumentException ex)
            {
                Console.WriteLine("Error: " + ex.Message);
                Console.WriteLine();
                CommandLineOptions.PrintUsage("Armada.Test.Automated");
                return 1;
            }

            // Print database info at startup
            PrintDatabaseInfo(dbSettings);

            string apiKey = "test-key-" + Guid.NewGuid().ToString("N");

            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;

            ArmadaSettings settings = new ArmadaSettings();
            settings.DataDirectory = tempDir;
            settings.DatabasePath = dbSettings.Filename;
            settings.Database = dbSettings;
            settings.LogDirectory = Path.Combine(tempDir, "logs");
            settings.DocksDirectory = Path.Combine(tempDir, "docks");
            settings.ReposDirectory = Path.Combine(tempDir, "repos");
            settings.ApiKey = apiKey;
            settings.HeartbeatIntervalSeconds = 300;
            // Bind the harness server's settings to its own temp file, so it neither hot-reloads the host
            // operator's live settings (whose fleet capacity limits would refuse later creates) nor writes
            // that file.
            settings.SettingsFilePath = Path.Combine(tempDir, "settings.json");
            // CRUD and paging suites retain a bounded corpus of active rows until suite cleanup.
            // Dedicated admission tests exercise the production capacity limits separately.
            settings.AutonomousObjectiveScheduler.Enabled = false;
            settings.AutonomousObjectiveScheduler.MaxConcurrentVoyages = 100;
            settings.AutonomousObjectiveScheduler.MaxConcurrentVoyagesPerVessel = 50;
            settings.InitializeDirectories();

            // Dispatched missions use the non-launching test runtime; only an opted-in runtime starts its CLI.
            // The listen ports are found free and released before the server binds them, so a start that loses
            // one to another socket is stopped and started again on new ports.
            int restPort = 0;
            int mcpPort = 0;
            ArmadaServer? startedServer = null;
            await LoopbackPorts.StartAsync(
                async () =>
                {
                    restPort = LoopbackPorts.FindFree();
                    mcpPort = LoopbackPorts.FindFree();
                    settings.AdmiralPort = restPort;
                    settings.McpPort = mcpPort;
                    startedServer = new ArmadaServer(logging, settings, new TestAgentRuntimeFactory(logging, settings, realRuntimes), quiet: true);
                    await startedServer.StartAsync().ConfigureAwait(false);
                },
                () =>
                {
                    try { startedServer?.Stop(); }
                    catch (Exception stopEx) { Console.Error.WriteLine("Stopping a server that lost its port failed: " + stopEx.Message); }
                    startedServer = null;
                }).ConfigureAwait(false);
            ArmadaServer server = startedServer!;
            await Task.Delay(500).ConfigureAwait(false);

            string baseUrl = "http://localhost:" + restPort;

            // Create shared HttpClient instances
            HttpClient authClient = new HttpClient();
            authClient.BaseAddress = new Uri(baseUrl);
            authClient.DefaultRequestHeaders.Add("X-Api-Key", apiKey);

            HttpClient unauthClient = new HttpClient();
            unauthClient.BaseAddress = new Uri(baseUrl);

            // The MCP endpoint refuses a request without credentials, like the REST API.
            HttpClient mcpClient = new HttpClient();
            mcpClient.BaseAddress = new Uri("http://localhost:" + mcpPort);
            mcpClient.DefaultRequestHeaders.Add("X-Api-Key", apiKey);

            int exitCode;

            try
            {
                TestRunner runner = new TestRunner("ARMADA AUTOMATED TEST SUITE");
                ConfigurationSurfaces surfaces = new ConfigurationSurfaces(authClient, mcpClient, restPort, apiKey);

                // First: its new captain must not be handed Pending work that later suites leave open.
                runner.AddSuite(new TestHostRuntimeTests(authClient, realRuntimes));
                runner.AddSuite(new DeploymentTests(authClient, unauthClient, baseUrl));
                runner.AddSuite(new EnvironmentTests(authClient, unauthClient));
                runner.AddSuite(new GitHubIntegrationTests(authClient, unauthClient, baseUrl));
                runner.AddSuite(new IncidentTests(authClient, unauthClient, baseUrl));
                runner.AddSuite(new ObjectiveTests(authClient, unauthClient, server));
                runner.AddSuite(new ReleaseTests(authClient, unauthClient));
                runner.AddSuite(new RequestHistoryTests(authClient, unauthClient, baseUrl));
                runner.AddSuite(new WorkflowProfileCheckRunTests(authClient, unauthClient));

                runner.AddSuite(new FleetTests(authClient, unauthClient));
                runner.AddSuite(new VesselTests(authClient, unauthClient));
                runner.AddSuite(new CaptainTests(authClient, unauthClient));
                runner.AddSuite(new MissionTests(authClient, unauthClient));
                runner.AddSuite(new VoyageTests(authClient, unauthClient));
                runner.AddSuite(new SignalTests(authClient, unauthClient));
                runner.AddSuite(new EventTests(authClient, unauthClient));
                runner.AddSuite(new DockTests(authClient, unauthClient));
                runner.AddSuite(new MergeQueueTests(authClient, unauthClient));
                runner.AddSuite(new StatusTests(authClient, unauthClient, settings.SettingsFilePath));
                runner.AddSuite(new ProductionTests(authClient, unauthClient));
                runner.AddSuite(new LogTests(authClient, unauthClient, tempDir));
                runner.AddSuite(new AuthenticationTests(authClient, unauthClient, baseUrl, apiKey));
                runner.AddSuite(new AuthApiTests(authClient, unauthClient, baseUrl, apiKey));
                runner.AddSuite(new CrossTenantApiTests(authClient, unauthClient, baseUrl, apiKey));
                runner.AddSuite(new McpToolTests(mcpClient));
                runner.AddSuite(new WebSocketTests(authClient, unauthClient, restPort, apiKey));
                runner.AddSuite(new PipelineParityTests(surfaces));
                runner.AddSuite(new ObjectiveParityTests(surfaces));
                runner.AddSuite(new PersonaParityTests(surfaces));
                runner.AddSuite(new WorkflowProfileParityTests(surfaces));
                runner.AddSuite(new PlaybookParityTests(surfaces));
                runner.AddSuite(new PromptTemplateParityTests(surfaces));
                runner.AddSuite(new PlanningSessionTests(authClient, unauthClient));
                runner.AddSuite(new PlanningWebSocketTests(authClient, unauthClient, restPort, apiKey));
                runner.AddSuite(new WorkflowTests(authClient, unauthClient));
                runner.AddSuite(new LandingPipelineTests(authClient, unauthClient, server, mcpClient, restPort, apiKey));
                runner.VerifyRegistration(typeof(Program).Assembly);

                exitCode = await runner.RunAllAsync(options.SuiteFilters).ConfigureAwait(false);

                string? launchFailure = TestProcessLaunchLog.DescribeUnpermittedProcessLaunches(realRuntimes);
                if (launchFailure != null)
                {
                    Console.WriteLine("RESULT: FAIL (agent process launches)");
                    Console.WriteLine(launchFailure);
                    exitCode = 1;
                }
            }
            finally
            {
                authClient.Dispose();
                unauthClient.Dispose();
                mcpClient.Dispose();

                try { server.Stop(); } catch { }
                await Task.Delay(200).ConfigureAwait(false);

                if (!options.NoCleanup)
                {
                    if (options.IsTempSqlite)
                    {
                        // Default behavior: delete entire temp directory including database
                        try
                        {
                            if (Directory.Exists(tempDir))
                                Directory.Delete(tempDir, true);
                        }
                        catch { }
                    }
                    else
                    {
                        // Non-default database: only delete temp subdirectories (logs/docks/repos), not the database
                        string[] tempSubDirs = new string[]
                        {
                            Path.Combine(tempDir, "logs"),
                            Path.Combine(tempDir, "docks"),
                            Path.Combine(tempDir, "repos")
                        };

                        foreach (string subDir in tempSubDirs)
                        {
                            try
                            {
                                if (Directory.Exists(subDir))
                                    Directory.Delete(subDir, true);
                            }
                            catch { }
                        }

                        // Try to clean up the temp directory if it's now empty
                        try
                        {
                            if (Directory.Exists(tempDir) && Directory.GetFileSystemEntries(tempDir).Length == 0)
                                Directory.Delete(tempDir);
                        }
                        catch { }
                    }
                }
                else
                {
                    Console.WriteLine("Test data preserved at: " + tempDir);
                }
            }

            return exitCode;
        }

        private static void PrintDatabaseInfo(DatabaseSettings dbSettings)
        {
            switch (dbSettings.Type)
            {
                case DatabaseTypeEnum.Sqlite:
                    Console.WriteLine("Database: SQLite (" + dbSettings.Filename + ")");
                    break;

                case DatabaseTypeEnum.Postgresql:
                    int pgPort = dbSettings.Port > 0 ? dbSettings.Port : 5432;
                    Console.WriteLine("Database: PostgreSQL (" + dbSettings.Hostname + ":" + pgPort + "/" + dbSettings.DatabaseName + ")");
                    break;

                case DatabaseTypeEnum.SqlServer:
                    int sqlPort = dbSettings.Port > 0 ? dbSettings.Port : 1433;
                    Console.WriteLine("Database: SQL Server (" + dbSettings.Hostname + ":" + sqlPort + "/" + dbSettings.DatabaseName + ")");
                    break;

                case DatabaseTypeEnum.Mysql:
                    int myPort = dbSettings.Port > 0 ? dbSettings.Port : 3306;
                    Console.WriteLine("Database: MySQL (" + dbSettings.Hostname + ":" + myPort + "/" + dbSettings.DatabaseName + ")");
                    break;
            }
        }
    }
}
