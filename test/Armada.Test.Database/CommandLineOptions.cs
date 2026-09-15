namespace Armada.Test.Database
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Command-line options for the database integration test runner.
    /// </summary>
    public class CommandLineOptions
    {
        #region Public-Members

        /// <summary>
        /// Database type: sqlite, mysql, postgresql, postgres, sqlserver, mssql.
        /// </summary>
        public string Type { get; set; } = "sqlite";

        /// <summary>
        /// Database filename, for use with SQLite.
        /// </summary>
        public string Filename { get; set; } = "";

        /// <summary>
        /// Database server hostname.
        /// </summary>
        public string Hostname { get; set; } = "";

        /// <summary>
        /// Database server port. Zero enables auto-detection based on database type.
        /// </summary>
        public int Port { get; set; } = 0;

        /// <summary>
        /// Database username.
        /// </summary>
        public string Username { get; set; } = "";

        /// <summary>
        /// Database password.
        /// </summary>
        public string Password { get; set; } = "";

        /// <summary>
        /// Database name.
        /// </summary>
        public string Database { get; set; } = "";

        /// <summary>
        /// Database schema.
        /// </summary>
        public string Schema { get; set; } = "";

        /// <summary>
        /// When true, do not clean up test data after execution.
        /// </summary>
        public bool NoCleanup { get; set; } = false;

        /// <summary>Optional migration fixture; requires an empty test database.</summary>
        public string MigrationScenario { get; set; } = "";

        /// <summary>
        /// When true, display usage information and exit.
        /// </summary>
        public bool Help { get; set; } = false;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate with defaults.
        /// </summary>
        public CommandLineOptions()
        {
        }

        /// <summary>
        /// Parse command-line arguments into a CommandLineOptions instance.
        /// </summary>
        /// <param name="args">Command-line arguments.</param>
        /// <returns>Parsed options.</returns>
        public static CommandLineOptions Parse(string[] args)
        {
            if (args == null) throw new ArgumentNullException(nameof(args));

            CommandLineOptions options = new CommandLineOptions();

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];

                switch (arg)
                {
                    case "--type":
                    case "-t":
                        options.Type = ReadValue(args, ref i).ToLowerInvariant();
                        break;

                    case "--filename":
                    case "-f":
                        options.Filename = ReadValue(args, ref i);
                        break;

                    case "--hostname":
                    case "-h":
                        options.Hostname = ReadValue(args, ref i);
                        break;

                    case "--port":
                    case "-p":
                        if (!Int32.TryParse(ReadValue(args, ref i), out int port) || port < 1 || port > 65535)
                            throw new ArgumentException("--port requires a number from 1 to 65535");
                        options.Port = port;
                        break;

                    case "--username":
                    case "-u":
                        options.Username = ReadValue(args, ref i);
                        break;

                    case "--password":
                    case "-w":
                        options.Password = ReadValue(args, ref i);
                        break;

                    case "--database":
                    case "-d":
                        options.Database = ReadValue(args, ref i);
                        break;

                    case "--schema":
                    case "-s":
                        options.Schema = ReadValue(args, ref i);
                        break;

                    case "--migration-scenario":
                        options.MigrationScenario = ReadValue(args, ref i);
                        break;

                    case "--no-cleanup":
                        options.NoCleanup = true;
                        break;

                    case "--help":
                    case "-?":
                        options.Help = true;
                        break;

                    default:
                        throw new ArgumentException("Unknown argument: " + arg);
                }
            }

            return options;
        }

        #endregion

        private static string ReadValue(string[] args, ref int index)
        {
            if (index + 1 >= args.Length || String.IsNullOrWhiteSpace(args[index + 1]) || args[index + 1].StartsWith("--"))
                throw new ArgumentException(args[index] + " requires a nonblank value");
            return args[++index];
        }

        #region Public-Methods

        /// <summary>
        /// Validate the options, returning a list of error messages.
        /// An empty list indicates valid options.
        /// </summary>
        /// <returns>List of validation error messages.</returns>
        public List<string> Validate()
        {
            List<string> errors = new List<string>();

            HashSet<string> validTypes = new HashSet<string>
            {
                "sqlite", "mysql", "postgresql", "postgres", "sqlserver", "mssql"
            };

            if (String.IsNullOrWhiteSpace(Type))
            {
                errors.Add("Database type is required (--type/-t).");
                return errors;
            }

            if (!validTypes.Contains(Type))
            {
                errors.Add("Invalid database type '" + Type + "'. Valid types: sqlite, mysql, postgresql, postgres, sqlserver, mssql.");
                return errors;
            }

            if (Type == "sqlite")
            {
                if (String.IsNullOrWhiteSpace(Filename))
                {
                    errors.Add("SQLite requires a filename (--filename/-f).");
                }
            }
            else
            {
                if (String.IsNullOrWhiteSpace(Hostname))
                {
                    errors.Add("Hostname is required for " + Type + " (--hostname/-h).");
                }

                if (String.IsNullOrWhiteSpace(Username))
                {
                    errors.Add("Username is required for " + Type + " (--username/-u).");
                }

                if (String.IsNullOrWhiteSpace(Password))
                {
                    errors.Add("Password is required for " + Type + " (--password/-w).");
                }

                if (String.IsNullOrWhiteSpace(Database))
                {
                    errors.Add("Database name is required for " + Type + " (--database/-d).");
                }
            }

            if (MigrationScenario == "sqlserver-corrections" && Type != "sqlserver" && Type != "mssql")
                errors.Add("sqlserver-corrections requires SQL Server.");

            if (MigrationScenario == "mysql-compat" && Type != "mysql")
                errors.Add("mysql-compat requires MySQL.");

            if (MigrationScenario == "catalog-guards" && Type == "sqlite")
                errors.Add(MigrationScenario + " requires a server provider.");

            if (MigrationScenario.Length > 0 && !new HashSet<string> { "fresh", "concurrent-fresh", "upgrade-51", "partial-first", "partial-52", "catalog-guards", "mysql-compat", "partial-identity", "sqlserver-corrections", "preview-migration", "backend-migration", "anchor-migration", "partial-anchor", "memory-migration", "ownership-migration", "postgres-legacy", "admission-migration", "model-endpoint-migration", "model-endpoint-guards", "harbor-enrollment-migration", "harbor-enrollment-guards", "harbor-enrollment-combined", "catalog-column-prune", "reviewer-persona-prune", "mission-input-wait-cancel", "skipped-version" }.Contains(MigrationScenario))
                errors.Add("Unknown migration scenario: " + MigrationScenario);

            if (Port < 0 || Port > 65535)
            {
                errors.Add("Port must be between 0 and 65535.");
            }

            return errors;
        }

        /// <summary>
        /// Get the effective port, applying defaults based on database type if port is zero.
        /// </summary>
        /// <returns>Effective port number.</returns>
        public int GetEffectivePort()
        {
            if (Port > 0) return Port;

            switch (Type)
            {
                case "postgresql":
                case "postgres":
                    return 5432;

                case "sqlserver":
                case "mssql":
                    return 1433;

                case "mysql":
                    return 3306;

                default:
                    return 0;
            }
        }

        /// <summary>
        /// Print usage information to the console.
        /// </summary>
        public static void PrintUsage()
        {
            Console.WriteLine("Armada Database Integration Tests");
            Console.WriteLine();
            Console.WriteLine("Usage:");
            Console.WriteLine("  Armada.Test.Database --type <type> [options]");
            Console.WriteLine();
            Console.WriteLine("Options:");
            Console.WriteLine("  --type, -t       Database type: sqlite, mysql, postgresql, postgres, sqlserver, mssql");
            Console.WriteLine("  --filename, -f   Database filename (SQLite only)");
            Console.WriteLine("  --hostname, -h   Database server hostname");
            Console.WriteLine("  --port, -p       Database server port (default: auto-detect)");
            Console.WriteLine("  --username, -u   Database username");
            Console.WriteLine("  --password, -w   Database password");
            Console.WriteLine("  --database, -d   Database name");
            Console.WriteLine("  --schema, -s     Database schema");
            Console.WriteLine("  --migration-scenario fresh|concurrent-fresh|upgrade-51|partial-first|partial-52|partial-identity|catalog-guards|mysql-compat|sqlserver-corrections|preview-migration|backend-migration|anchor-migration|partial-anchor|memory-migration|ownership-migration|postgres-legacy|admission-migration|model-endpoint-migration|harbor-enrollment-migration|harbor-enrollment-guards|harbor-enrollment-combined|catalog-column-prune|reviewer-persona-prune|mission-input-wait-cancel|skipped-version (empty database only)");
            Console.WriteLine("  --no-cleanup     Do not clean up test data after execution");
            Console.WriteLine("  --help, -?       Show this help message");
            Console.WriteLine();
            Console.WriteLine("Default Ports:");
            Console.WriteLine("  PostgreSQL: 5432");
            Console.WriteLine("  SQL Server: 1433");
            Console.WriteLine("  MySQL:      3306");
            Console.WriteLine();
            Console.WriteLine("Examples:");
            Console.WriteLine();
            Console.WriteLine("  SQLite:");
            Console.WriteLine("    Armada.Test.Database -t sqlite -f ./test.db");
            Console.WriteLine();
            Console.WriteLine("  PostgreSQL:");
            Console.WriteLine("    Armada.Test.Database -t postgresql -h localhost -u postgres -w secret -d armada_test");
            Console.WriteLine();
            Console.WriteLine("  SQL Server:");
            Console.WriteLine("    Armada.Test.Database -t sqlserver -h localhost -u sa -w secret -d armada_test");
            Console.WriteLine();
            Console.WriteLine("  MySQL:");
            Console.WriteLine("    Armada.Test.Database -t mysql -h localhost -u root -w secret -d armada_test");
        }

        #endregion
    }
}
