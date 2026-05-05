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

    public class Program
    {
        public static async Task<int> Main(string[] args)
        {
            List<string> suiteFilters = new List<string>();
            string[] filteredArgs;

            try
            {
                filteredArgs = ExtractSuiteFilters(args, suiteFilters);
            }
            catch (ArgumentException ex)
            {
                Console.WriteLine("Error: " + ex.Message);
                Console.WriteLine();
                CommandLineOptions.PrintUsage("Armada.Test.Automated");
                Console.WriteLine("Additional Options:");
                Console.WriteLine("  --suite <name>           Run only suites whose type or display name contains <name>");
                return 1;
            }

            CommandLineOptions options;

            try
            {
                options = CommandLineOptions.Parse(filteredArgs);
            }
            catch (ArgumentException ex)
            {
                Console.WriteLine("Error: " + ex.Message);
                Console.WriteLine();
                CommandLineOptions.PrintUsage("Armada.Test.Automated");
                Console.WriteLine("Additional Options:");
                Console.WriteLine("  --suite <name>           Run only suites whose type or display name contains <name>");
                return 1;
            }

            if (options.Help)
            {
                CommandLineOptions.PrintUsage("Armada.Test.Automated");
                Console.WriteLine("Additional Options:");
                Console.WriteLine("  --suite <name>           Run only suites whose type or display name contains <name>");
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

            // Allocate random ports
            int restPort = GetAvailablePort();
            int mcpPort = GetAvailablePort();
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
            settings.AdmiralPort = restPort;
            settings.McpPort = mcpPort;
            settings.ApiKey = apiKey;
            settings.HeartbeatIntervalSeconds = 300;
            settings.InitializeDirectories();

            ArmadaServer server = new ArmadaServer(logging, settings, quiet: true);
            await server.StartAsync().ConfigureAwait(false);
            await Task.Delay(500).ConfigureAwait(false);

            string baseUrl = "http://localhost:" + restPort;

            // Create shared HttpClient instances
            HttpClient authClient = new HttpClient();
            authClient.BaseAddress = new Uri(baseUrl);
            authClient.DefaultRequestHeaders.Add("X-Api-Key", apiKey);

            HttpClient unauthClient = new HttpClient();
            unauthClient.BaseAddress = new Uri(baseUrl);

            HttpClient mcpClient = new HttpClient();
            mcpClient.BaseAddress = new Uri("http://localhost:" + mcpPort);

            int exitCode;

            try
            {
                TestRunner runner = new TestRunner("ARMADA AUTOMATED TEST SUITE");

                List<TestSuite> suites = new List<TestSuite>
                {
                    new FleetTests(authClient, unauthClient),
                    new VesselTests(authClient, unauthClient),
                    new CaptainTests(authClient, unauthClient),
                    new MissionTests(authClient, unauthClient),
                    new VoyageTests(authClient, unauthClient),
                    new SignalTests(authClient, unauthClient),
                    new EventTests(authClient, unauthClient),
                    new DockTests(authClient, unauthClient),
                    new MergeQueueTests(authClient, unauthClient),
                    new StatusTests(authClient, unauthClient),
                    new LogTests(authClient, unauthClient, tempDir),
                    new AuthenticationTests(authClient, unauthClient, baseUrl, apiKey),
                    new AuthApiTests(authClient, unauthClient, baseUrl, apiKey),
                    new CrossTenantApiTests(authClient, unauthClient, baseUrl, apiKey),
                    new RequestHistoryTests(authClient, unauthClient, baseUrl),
                    new WorkflowProfileCheckRunTests(authClient, unauthClient),
                    new ObjectiveTests(authClient, unauthClient),
                    new EnvironmentTests(authClient, unauthClient),
                    new DeploymentTests(authClient, unauthClient, baseUrl),
                    new ReleaseTests(authClient, unauthClient),
                    new McpToolTests(mcpClient),
                    new WebSocketTests(authClient, unauthClient, restPort, apiKey),
                    new PlanningSessionTests(authClient, unauthClient),
                    new PlanningWebSocketTests(authClient, unauthClient, restPort, apiKey),
                    new WorkflowTests(authClient, unauthClient),
                    new LandingPipelineTests(authClient, unauthClient)
                };

                if (suiteFilters.Count > 0)
                {
                    suites = suites
                        .Where(suite => suiteFilters.Any(filter =>
                            suite.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                            || suite.GetType().Name.Contains(filter, StringComparison.OrdinalIgnoreCase)))
                        .ToList();

                    if (suites.Count == 0)
                    {
                        Console.WriteLine("No automated suites matched: " + string.Join(", ", suiteFilters));
                        return 1;
                    }
                }

                foreach (TestSuite suite in suites)
                    runner.AddSuite(suite);

                exitCode = await runner.RunAllAsync().ConfigureAwait(false);
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

        private static int GetAvailablePort()
        {
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        private static string[] ExtractSuiteFilters(string[] args, List<string> suiteFilters)
        {
            List<string> filtered = new List<string>();

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                if (arg.Equals("--suite", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 >= args.Length)
                        throw new ArgumentException("--suite requires a value");

                    suiteFilters.Add(args[++i]);
                    continue;
                }

                filtered.Add(arg);
            }

            return filtered.ToArray();
        }
    }
}
