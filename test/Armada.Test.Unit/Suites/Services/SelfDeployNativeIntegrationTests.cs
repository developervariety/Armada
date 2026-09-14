namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Test.Common;

    /// <summary>
    /// Opt-in real utility integration checks for isolated self-deploy databases.
    /// </summary>
    public sealed class SelfDeployNativeIntegrationTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Self Deploy Native Integration";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            if (!String.Equals(Environment.GetEnvironmentVariable("ARMADA_SELF_DEPLOY_INTEGRATION"), "1", StringComparison.Ordinal)
                || !String.Equals(Environment.GetEnvironmentVariable("ARMADA_SELF_DEPLOY_INTEGRATION_SCOPE"), "isolated-test-databases", StringComparison.Ordinal))
            {
                SkipTest("NativeIntegration_DefaultDisabled", "Real provider proof is disabled until the explicit isolated-test scope is configured.");
                return;
            }

            string? candidateDll = Environment.GetEnvironmentVariable("ARMADA_SELF_DEPLOY_CANDIDATE_DLL");
            string? backupRoot = Environment.GetEnvironmentVariable("ARMADA_SELF_DEPLOY_BACKUP_DIRECTORY");
            if (String.IsNullOrWhiteSpace(candidateDll) || !File.Exists(candidateDll)
                || String.IsNullOrWhiteSpace(backupRoot))
            {
                SkipTest("NativeIntegration_Configuration", "Set the candidate DLL and an isolated backup directory.");
                return;
            }

            foreach (DatabaseTypeEnum type in new[]
            {
                DatabaseTypeEnum.Sqlite,
                DatabaseTypeEnum.Mysql,
                DatabaseTypeEnum.Postgresql,
                DatabaseTypeEnum.SqlServer
            })
            {
                string testName = "NativeIntegration_RoundTrip_" + type;
                if (!TryReadConfiguration(type, backupRoot, out DatabaseSettings settings, out string operationRoot, out string? sqlServerBackupDirectory, out string reason))
                {
                    SkipTest(testName, reason);
                    continue;
                }

                await RunTest(testName, async () =>
                {
                    SelfDeployDatabaseBackupProvider provider = new SelfDeployDatabaseBackupProvider(
                        settings,
                        operationRoot,
                        new SelfDeployNativeCommandRunner(),
                        sqlServerBackupDirectory);
                    SelfDeployDatabaseBackupResult backup = await provider.CreateAndVerifyAsync().ConfigureAwait(false);
                    AssertTrue(backup.BackupValidated, type + " backup proof: " + SafeReason(backup.FailureReason));
                    AssertTrue(backup.RestoreVerified, type + " restore proof: " + SafeReason(backup.FailureReason));
                    AssertTrue(provider.OwnsTarget(backup), type + " owned target proof: " + SafeReason(backup.FailureReason));
                    bool cleaned = false;
                    try
                    {
                        SelfDeployCandidateProcessValidator validator = new SelfDeployCandidateProcessValidator(
                            provider,
                            new SelfDeployNativeCommandRunner(),
                            settings);
                        SelfDeployCandidateValidationResult candidate = await validator.ValidateAsync(new SelfDeployPreflightRequest
                        {
                            CandidateServerDllPath = Path.GetFullPath(candidateDll),
                            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(candidateDll)) ?? Environment.CurrentDirectory
                        }, backup).ConfigureAwait(false);
                        AssertTrue(candidate.CandidateValidated, type + " candidate proof: " + SafeReason(candidate.FailureReason));
                    }
                    finally
                    {
                        if (provider.OwnsTarget(backup))
                            cleaned = await provider.CleanupAsync(backup, CancellationToken.None).ConfigureAwait(false);
                    }

                    AssertTrue(cleaned, type + " owned target cleanup");
                    if (type == DatabaseTypeEnum.Sqlite)
                        AssertTrue(File.Exists(backup.ArtifactPath), "SQLite rollback artifact survives cleanup");
                }).ConfigureAwait(false);
            }
        }

        private static string SafeReason(string? reason)
        {
            if (String.IsNullOrWhiteSpace(reason)) return "failure_reason_missing";
            if (reason.Length > 120) return "failure_reason_invalid";
            foreach (char character in reason)
            {
                if (!(Char.IsLetterOrDigit(character) || character == '_' || character == '-'))
                    return "failure_reason_invalid";
            }

            return reason;
        }

        private static bool TryReadConfiguration(
            DatabaseTypeEnum type,
            string backupRoot,
            out DatabaseSettings settings,
            out string operationRoot,
            out string? sqlServerBackupDirectory,
            out string reason)
        {
            settings = new DatabaseSettings { Type = type };
            sqlServerBackupDirectory = null;
            operationRoot = Path.Combine(Path.GetFullPath(backupRoot), "integration-" + type.ToString().ToLowerInvariant() + "-" + Guid.NewGuid().ToString("N"));
            reason = String.Empty;
            string prefix = "ARMADA_SELF_DEPLOY_" + type.ToString().ToUpperInvariant() + "_";
            if (type == DatabaseTypeEnum.Sqlite)
            {
                string? filename = Environment.GetEnvironmentVariable(prefix + "FILENAME");
                if (String.IsNullOrWhiteSpace(filename) || !File.Exists(filename))
                {
                    reason = "Set an existing isolated SQLite source in " + prefix + "FILENAME.";
                    return false;
                }
                settings.Filename = Path.GetFullPath(filename);
                return true;
            }

            string? hostname = Environment.GetEnvironmentVariable(prefix + "HOSTNAME");
            string? username = Environment.GetEnvironmentVariable(prefix + "USERNAME");
            string? password = Environment.GetEnvironmentVariable(prefix + "PASSWORD");
            string? databaseName = Environment.GetEnvironmentVariable(prefix + "DATABASE_NAME");
            if (String.IsNullOrWhiteSpace(hostname) || String.IsNullOrWhiteSpace(username)
                || String.IsNullOrWhiteSpace(password) || String.IsNullOrWhiteSpace(databaseName))
            {
                reason = "Set typed host, username, password, and database variables for " + type + ".";
                return false;
            }

            settings.Hostname = hostname;
            settings.Username = username;
            settings.Password = password;
            settings.DatabaseName = databaseName;
            string? port = Environment.GetEnvironmentVariable(prefix + "PORT");
            if (!String.IsNullOrWhiteSpace(port) && Int32.TryParse(port, out int parsedPort)) settings.Port = parsedPort;
            if (type == DatabaseTypeEnum.SqlServer)
            {
                sqlServerBackupDirectory = Environment.GetEnvironmentVariable("ARMADA_SELF_DEPLOY_SQLSERVER_BACKUP_DIRECTORY");
                if (String.IsNullOrWhiteSpace(sqlServerBackupDirectory))
                {
                    reason = "Set the SQL Server host-visible backup directory.";
                    return false;
                }
                sqlServerBackupDirectory = sqlServerBackupDirectory.Trim();
            }

            return true;
        }
    }
}
