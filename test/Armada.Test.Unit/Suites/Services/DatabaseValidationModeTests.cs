namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Microsoft.Data.Sqlite;

    /// <summary>Process-level proof that image preflight never starts Admiral services.</summary>
    public sealed class DatabaseValidationModeTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Database Validation Mode";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("Database validation initializes and exits without captain or mission creation", () => VerifyAsync(false));
            await RunTest("Database validation refuses additional arguments without starting the server", () => VerifyAsync(true));
        }

        private async Task VerifyAsync(bool extraArgument)
        {
            string directory = Path.Combine(Path.GetTempPath(), "armada-preflight-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string filename = Path.Combine(directory, "preflight.db");
            try
            {
                Dictionary<string, object> settings = new Dictionary<string, object>
                {
                    ["Database"] = new DatabaseSettings { Type = DatabaseTypeEnum.Sqlite, Filename = filename },
                    ["LogDirectory"] = Path.Combine(directory, "logs")
                };
                await File.WriteAllTextAsync(Path.Combine(directory, "settings.json"), JsonSerializer.Serialize(settings));
                ProcessStartInfo start = new ProcessStartInfo("dotnet")
                {
                    UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                    WorkingDirectory = directory
                };
                start.ArgumentList.Add(typeof(Armada.Server.Program).Assembly.Location);
                start.ArgumentList.Add("--validate-database");
                if (extraArgument) start.ArgumentList.Add("unexpected");
                start.Environment["ARMADA_DATA_DIRECTORY"] = directory;
                using (Process process = new Process { StartInfo = start })
                using (CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
                {
                    process.Start();
                    Task<string> output = process.StandardOutput.ReadToEndAsync();
                    Task<string> errors = process.StandardError.ReadToEndAsync();
                    try { await process.WaitForExitAsync(timeout.Token); }
                    finally { if (!process.HasExited) process.Kill(true); }
                    string text = await output;
                    await errors;
                    AssertFalse(text.Contains("Admiral running", StringComparison.Ordinal), "Preflight must not start listeners");
                    if (extraArgument)
                    {
                        AssertTrue(process.ExitCode != 0, "Invalid preflight arguments fail closed");
                        AssertFalse(File.Exists(filename), "Invalid arguments must not initialize a database");
                    }
                    else
                    {
                        AssertEqual(0, process.ExitCode, "Successful preflight exits zero");
                        AssertContains("DATABASE VALIDATION PASSED", text, "Explicit preflight result");
                        using (SqliteConnection connection = new SqliteConnection("Data Source=" + filename + ";Mode=ReadOnly"))
                        {
                            await connection.OpenAsync();
                            using (SqliteCommand command = connection.CreateCommand())
                            {
                                command.CommandText = "SELECT (SELECT COUNT(*) FROM missions) + (SELECT COUNT(*) FROM captains);";
                                AssertEqual(0L, Convert.ToInt64(await command.ExecuteScalarAsync()), "No dispatch or captain provisioning");
                            }
                        }
                    }
                }
            }
            finally { SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
        }
    }
}
