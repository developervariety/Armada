namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// Tests provider-native self-deploy backup and isolated-restore proof.
    /// </summary>
    public sealed class SelfDeployDatabaseBackupProviderTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Self Deploy Database Backup Provider";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("NativeCommandRunner_BoundsFloodedOutput", async () =>
            {
                SelfDeployNativeCommandRunner runner = new SelfDeployNativeCommandRunner();
                SelfDeployNativeCommandResult result = await runner.RunAsync(BuildFloodCommand());
                AssertTrue(result.Succeeded, "flood command exits successfully");
                AssertContains(SelfDeployNativeCommandRunner.OutputTruncationMarker, result.StandardOutput,
                    "flooded output is marked as truncated");
                AssertTrue(result.StandardOutput.Length <= SelfDeployNativeCommandRunner.MaximumOutputCharacters
                    + SelfDeployNativeCommandRunner.OutputTruncationMarker.Length + 2,
                    "captured output stays bounded");
            });

            await RunTest("NativeCommandRunner_ParentExitWithInheritedPipeTimesOut", async () =>
            {
                if (OperatingSystem.IsWindows())
                {
                    SkipTest("NativeCommandRunner_ParentExitWithInheritedPipeTimesOut", "Inherited Unix pipe fixture is unavailable on Windows.");
                    return;
                }
                // The descendant holds the pipes for 30 s, so any drain timeout shorter than that proves the bound.
                SelfDeployNativeCommandRunner runner = new SelfDeployNativeCommandRunner(TimeSpan.FromMilliseconds(500));
                InvalidOperationException? captured = null;
                try
                {
                    await runner.RunAsync(BuildInheritedPipeCommand()).ConfigureAwait(false);
                }
                catch (InvalidOperationException ex)
                {
                    captured = ex;
                }
                AssertNotNull(captured, "inherited pipe must fail closed");
                AssertEqual("native_command_io_drain_timeout", captured!.Message, "bounded pipe timeout reason");
            });

            await RunTest("NativeCommandRunner_ParentExitWithRedirectedInputPipeTimesOut", async () =>
            {
                if (OperatingSystem.IsWindows())
                {
                    SkipTest("NativeCommandRunner_ParentExitWithRedirectedInputPipeTimesOut", "Inherited Unix pipe fixture is unavailable on Windows.");
                    return;
                }
                // The descendant holds the pipes for 30 s, so any drain timeout shorter than that proves the bound.
                SelfDeployNativeCommandRunner runner = new SelfDeployNativeCommandRunner(TimeSpan.FromMilliseconds(500));
                InvalidOperationException? captured = null;
                try
                {
                    await runner.RunAsync(new SelfDeployNativeCommandRequest
                    {
                        FileName = "/bin/sh",
                        Arguments = new[] { "-c", "(sleep 30) & exit 0" },
                        StandardInputFilePath = "/dev/null"
                    }).ConfigureAwait(false);
                }
                catch (InvalidOperationException ex)
                {
                    captured = ex;
                }
                AssertNotNull(captured, "inherited pipe with redirected input must fail closed");
                AssertEqual("native_command_io_drain_timeout", captured!.Message, "redirected input pipe timeout reason");
            });

            await RunTest("NativeCommandRunner_CancellationTerminatesAndPropagates", async () =>
            {
                SelfDeployNativeCommandRunner runner = new SelfDeployNativeCommandRunner();
                using CancellationTokenSource cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
                await AssertThrowsAsync<OperationCanceledException>(async () =>
                    await runner.RunAsync(BuildSleepCommand(), cancellation.Token).ConfigureAwait(false));
            });

            if (OperatingSystem.IsWindows())
            {
                SkipTest("NativeProviderRoundTrip_Windows", "Native provider storage is disabled until owner-only Windows ACL proof exists.");
                return;
            }

            await RunTest("PrivateStorage_RejectsSharedExistingBackupDirectory", async () =>
            {
                if (OperatingSystem.IsWindows()) return;
                string root = Path.Combine(Path.GetTempPath(), "selfdeploy-shared-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(root);
                try
                {
                    File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite
                        | UnixFileMode.UserExecute | UnixFileMode.GroupRead
                        | UnixFileMode.GroupExecute | UnixFileMode.OtherRead
                        | UnixFileMode.OtherExecute);
                    SelfDeployDatabaseBackupProvider provider = new SelfDeployDatabaseBackupProvider(
                        new DatabaseSettings { Type = DatabaseTypeEnum.Sqlite, Filename = Path.Combine(root, "source.db") }, root);
                    SelfDeployDatabaseBackupResult result = await provider.CreateAndVerifyAsync();
                    AssertFalse(result.BackupValidated, "shared backup directory must fail closed");
                    AssertEqual("private_storage_permissions_unverified", result.FailureReason, "shared directory reason");
                }
                finally
                {
                    TryDeleteDirectory(root);
                }
            });

            await RunTest("SqliteBackup_RoundTripsAndVerifiesCopy", async () =>
            {
                using (TestDatabase database = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    string sourcePath = database.ConnectionString.Substring("Data Source=".Length).Split(';')[0];
                    string backupDirectory = Path.Combine(Path.GetTempPath(), "selfdeploy-provider-" + Guid.NewGuid().ToString("N"));
                    DatabaseSettings settings = new DatabaseSettings
                    {
                        Type = DatabaseTypeEnum.Sqlite,
                        Filename = sourcePath
                    };
                    try
                    {
                        SelfDeployDatabaseBackupProvider provider = new SelfDeployDatabaseBackupProvider(settings, backupDirectory);
                        SelfDeployDatabaseBackupResult result = await provider.CreateAndVerifyAsync();
                        AssertTrue(result.BackupValidated, "backup proof");
                        AssertTrue(result.RestoreVerified, "restore proof");
                        AssertTrue(File.Exists(result.ArtifactPath), "artifact exists");
                        AssertPrivateFile(result.ArtifactPath);
                        AssertPrivateFile(Directory.GetParent(result.ArtifactPath)!.FullName);
                        AssertTrue(File.Exists(result.IsolatedTarget), "isolated copy exists");
                        AssertFalse(String.Equals(result.ArtifactPath, result.IsolatedTarget, StringComparison.Ordinal), "SQLite restores into a separate copy");
                        AssertTrue(File.Exists(sourcePath), "source remains present");
                        AssertTrue(await provider.CleanupAsync(result), "SQLite cleanup");
                        AssertFalse(File.Exists(result.IsolatedTarget), "isolated copy cleaned");
                        AssertTrue(File.Exists(result.ArtifactPath), "backup artifact survives isolated cleanup");
                    }
                    finally
                    {
                        TryDeleteDirectory(backupDirectory);
                    }
                }
            });

            await RunTest("NativeProviders_RoundTripThroughInjectedCommands", async () =>
            {
                foreach (DatabaseTypeEnum type in new[]
                {
                    DatabaseTypeEnum.Mysql,
                    DatabaseTypeEnum.Postgresql,
                    DatabaseTypeEnum.SqlServer
                })
                {
                    string backupDirectory = Path.Combine(Path.GetTempPath(), "selfdeploy-provider-" + Guid.NewGuid().ToString("N"));
                    RecordingCommandRunner commands = new RecordingCommandRunner();
                    DatabaseSettings settings = new DatabaseSettings
                    {
                        Type = type,
                        Hostname = "custom-db.example",
                        Username = "armada_user",
                        Password = "sentinel-password",
                        DatabaseName = "live_custom"
                    };
                    try
                    {
                        SelfDeployDatabaseBackupResult result = await new SelfDeployDatabaseBackupProvider(
                            settings,
                            backupDirectory,
                            commands,
                            type == DatabaseTypeEnum.SqlServer ? backupDirectory : null).CreateAndVerifyAsync();
                        AssertTrue(result.BackupValidated, type + " backup proof");
                        AssertTrue(result.RestoreVerified, type + " restore proof");
                        AssertTrue(commands.Calls.Count >= (type == DatabaseTypeEnum.SqlServer ? 6 : 4), type + " command count");
                        AssertFalse(commands.Calls.SelectMany(call => call.Arguments).Any(argument => argument.Contains("sentinel-password", StringComparison.Ordinal)), type + " password in arguments");
                        AssertTrue(commands.Calls.Any(call => call.EnvironmentVariables.Values.Contains("sentinel-password")), type + " password environment");
                        AssertTrue(commands.Calls.Any(call => call.Arguments.Any(argument => argument.Contains(result.IsolatedTarget, StringComparison.Ordinal))), type + " unique isolated target");
                        AssertTrue(commands.Calls.Any(call => call.Arguments.Any(argument => argument.Contains("custom-db.example", StringComparison.Ordinal))), type + " effective hostname");
                        AssertTrue(commands.Calls.Any(call => call.Arguments.Any(argument => argument.Contains("live_custom", StringComparison.Ordinal))), type + " effective database name");
                        if (type == DatabaseTypeEnum.SqlServer)
                        {
                            IEnumerable<SelfDeployNativeCommandRequest> sqlCommands = commands.Calls.Where(call => String.Equals(call.FileName, "sqlcmd", StringComparison.Ordinal));
                            AssertTrue(sqlCommands.All(call => call.Arguments.Contains("-C")), "SQL Server certificate trust is explicit");
                            AssertTrue(sqlCommands.All(call => call.Arguments.Contains("-No")), "SQL Server encryption mode is explicit");
                        }
                    }
                    finally
                    {
                        TryDeleteDirectory(backupDirectory);
                    }
                }
            });

            await RunTest("NativeProviders_CommandFailureReturnsNoProof", async () =>
            {
                foreach (DatabaseTypeEnum type in new[]
                {
                    DatabaseTypeEnum.Mysql,
                    DatabaseTypeEnum.Postgresql,
                    DatabaseTypeEnum.SqlServer
                })
                {
                    RecordingCommandRunner commands = new RecordingCommandRunner { FailFirstCall = true };
                        DatabaseSettings settings = new DatabaseSettings
                        {
                            Type = type,
                            Hostname = "custom-db.example",
                            Username = "armada_user",
                            Password = "sentinel-password",
                            DatabaseName = "live_custom"
                    };
                    SelfDeployDatabaseBackupResult result = await new SelfDeployDatabaseBackupProvider(
                        settings,
                        Path.Combine(Path.GetTempPath(), "selfdeploy-provider-" + Guid.NewGuid().ToString("N")),
                        commands,
                        type == DatabaseTypeEnum.SqlServer ? Path.Combine(Path.GetTempPath(), "selfdeploy-provider-sqlserver-" + Guid.NewGuid().ToString("N")) : null).CreateAndVerifyAsync();
                    AssertFalse(result.BackupValidated, type + " backup must fail closed");
                    AssertFalse(result.RestoreVerified, type + " restore must fail closed");
                    AssertTrue(!String.IsNullOrWhiteSpace(result.FailureReason), type + " failure code");
                }
            });

            await RunTest("SqlServerArguments_HonorEncryptionSetting", async () =>
            {
                foreach (bool requireEncryption in new[] { false, true })
                {
                    string root = Path.Combine(Path.GetTempPath(), "selfdeploy-provider-" + Guid.NewGuid().ToString("N"));
                    RecordingCommandRunner commands = new RecordingCommandRunner();
                    DatabaseSettings settings = new DatabaseSettings
                    {
                        Type = DatabaseTypeEnum.SqlServer,
                        Hostname = "sqlserver.example",
                        Username = "armada_user",
                        Password = "sentinel-password",
                        DatabaseName = "live_custom",
                        RequireEncryption = requireEncryption
                    };
                    try
                    {
                        SelfDeployDatabaseBackupResult result = await new SelfDeployDatabaseBackupProvider(
                            settings,
                            root,
                            commands,
                            "/var/opt/mssql/data").CreateAndVerifyAsync();
                        AssertTrue(result.BackupValidated, "SQL Server encryption " + requireEncryption + " proof");
                        string expectedMode = requireEncryption ? "-Nm" : "-No";
                        IEnumerable<SelfDeployNativeCommandRequest> sqlCommands = commands.Calls.Where(call => String.Equals(call.FileName, "sqlcmd", StringComparison.Ordinal));
                        AssertTrue(sqlCommands.All(call => call.Arguments.Contains("-C")), "SQL Server certificate trust remains explicit");
                        AssertTrue(sqlCommands.All(call => call.Arguments.Contains(expectedMode)), "SQL Server encryption mode " + expectedMode);
                    }
                    finally
                    {
                        TryDeleteDirectory(root);
                    }
                }
            });

            await RunTest("SqlServerBackups_UseUniqueImmutableArtifactNames", async () =>
            {
                string root = Path.Combine(Path.GetTempPath(), "selfdeploy-provider-" + Guid.NewGuid().ToString("N"));
                RecordingCommandRunner commands = new RecordingCommandRunner();
                DatabaseSettings settings = new DatabaseSettings
                {
                    Type = DatabaseTypeEnum.SqlServer,
                    Hostname = "custom-db.example",
                    Username = "armada_user",
                    Password = "sentinel-password",
                    DatabaseName = "live_custom"
                };
                try
                {
                    SelfDeployDatabaseBackupProvider provider = new SelfDeployDatabaseBackupProvider(settings, root, commands, root);
                    SelfDeployDatabaseBackupResult first = await provider.CreateAndVerifyAsync();
                    SelfDeployDatabaseBackupResult second = await provider.CreateAndVerifyAsync();
                    AssertTrue(first.BackupValidated && second.BackupValidated, "both SQL Server backups prove");
                    AssertFalse(String.Equals(first.ArtifactPath, second.ArtifactPath, StringComparison.Ordinal), "SQL Server artifact paths are unique");
                    AssertContains("armada-backup-", first.ArtifactPath, "first immutable artifact name");
                    AssertContains("armada-backup-", second.ArtifactPath, "second immutable artifact name");
                    AssertTrue(await provider.CleanupAsync(first), "first isolated target cleanup");
                    AssertTrue(await provider.CleanupAsync(second), "second isolated target cleanup");
                }
                finally
                {
                    TryDeleteDirectory(root);
                }
            });

            await RunTest("SqlServerRestoreSql_EscapesNestedPathsAndPlaceholders", async () =>
            {
                string root = Path.Combine(Path.GetTempPath(), "selfdeploy-provider-{DESTINATION}-" + Guid.NewGuid().ToString("N"));
                string serverDirectory = Path.Combine(root, "server'quoted");
                RecordingCommandRunner commands = new RecordingCommandRunner();
                DatabaseSettings settings = new DatabaseSettings
                {
                    Type = DatabaseTypeEnum.SqlServer,
                    Hostname = "sqlserver.example",
                    Username = "armada_user",
                    Password = "sentinel-password",
                    DatabaseName = "live_custom"
                };
                try
                {
                    SelfDeployDatabaseBackupResult result = await new SelfDeployDatabaseBackupProvider(
                        settings, root, commands, serverDirectory).CreateAndVerifyAsync();
                    AssertTrue(result.RestoreVerified, "SQL Server restore proof");
                    string restoreSql = commands.Calls
                        .Single(call => call.Arguments.Any(argument => argument.Contains("RESTORE FILELISTONLY", StringComparison.Ordinal)))
                        .Arguments.Single(argument => argument.Contains("RESTORE FILELISTONLY", StringComparison.Ordinal));
                    string nestedPath = result.ArtifactPath.Replace("'", "''''", StringComparison.Ordinal);
                    AssertContains(nestedPath, restoreSql, "Nested SQL path escaping");
                    AssertContains("{DESTINATION}", restoreSql, "Path placeholder remains literal");
                }
                finally
                {
                    TryDeleteDirectory(root);
                }
            });

            await RunTest("SqlServerBackup_PreservesConfiguredServerPathSeparator", async () =>
            {
                foreach (string serverDirectory in new[] { "C:\\mssql\\backup", "/var/opt/mssql/data" })
                {
                    string root = Path.Combine(Path.GetTempPath(), "selfdeploy-provider-" + Guid.NewGuid().ToString("N"));
                    RecordingCommandRunner commands = new RecordingCommandRunner();
                    DatabaseSettings settings = new DatabaseSettings
                    {
                        Type = DatabaseTypeEnum.SqlServer,
                        Hostname = "sqlserver.example",
                        Username = "armada_user",
                        Password = "sentinel-password",
                        DatabaseName = "live_custom"
                    };
                    try
                    {
                        SelfDeployDatabaseBackupResult result = await new SelfDeployDatabaseBackupProvider(
                            settings,
                            root,
                            commands,
                            serverDirectory).CreateAndVerifyAsync();
                        AssertTrue(result.BackupValidated, serverDirectory + " backup proof");
                        string expectedSeparator = serverDirectory.IndexOf('\\') >= 0 ? "\\" : "/";
                        string backupArgument = commands.Calls[1].Arguments.Single(argument => argument.Contains("BACKUP DATABASE", StringComparison.Ordinal));
                        AssertContains(serverDirectory.TrimEnd('\\', '/') + expectedSeparator + "armada-backup-", backupArgument, serverDirectory + " server path separator");
                        AssertContains("[File is Directory] = 1", commands.Calls[0].Arguments.Single(argument => argument.Contains("xp_fileexist", StringComparison.Ordinal)), serverDirectory + " directory proof");
                    }
                    finally
                    {
                        TryDeleteDirectory(root);
                    }
                }
            });

            await RunTest("SqlServerBackup_RejectsMissingServerDirectory", async () =>
            {
                string root = Path.Combine(Path.GetTempPath(), "selfdeploy-provider-" + Guid.NewGuid().ToString("N"));
                RecordingCommandRunner commands = new RecordingCommandRunner { FailFirstCall = true };
                DatabaseSettings settings = new DatabaseSettings
                {
                    Type = DatabaseTypeEnum.SqlServer,
                    Hostname = "sqlserver.example",
                    Username = "armada_user",
                    Password = "sentinel-password",
                    DatabaseName = "live_custom"
                };
                try
                {
                    SelfDeployDatabaseBackupResult result = await new SelfDeployDatabaseBackupProvider(
                        settings,
                        root,
                        commands,
                        "/var/opt/mssql/missing").CreateAndVerifyAsync();
                    AssertFalse(result.BackupValidated, "missing server directory backup proof");
                    AssertContains("sqlserver_server_backup_directory_unavailable", result.FailureReason, "missing server directory reason");
                    AssertEqual(1, commands.Calls.Count, "missing server directory stops before backup");
                    AssertContains("[File is Directory] = 1", commands.Calls[0].Arguments.Single(argument => argument.Contains("xp_fileexist", StringComparison.Ordinal)), "missing server directory check");
                }
                finally
                {
                    TryDeleteDirectory(root);
                }
            });

            await RunTest("Cleanup_RefusesUnownedTarget", async () =>
            {
                string target = Path.Combine(Path.GetTempPath(), "selfdeploy-unowned-" + Guid.NewGuid().ToString("N") + ".db");
                File.WriteAllText(target, "must remain");
                try
                {
                    DatabaseSettings settings = new DatabaseSettings { Type = DatabaseTypeEnum.Sqlite };
                    SelfDeployDatabaseBackupProvider provider = new SelfDeployDatabaseBackupProvider(
                        settings,
                        Path.Combine(Path.GetTempPath(), "selfdeploy-provider-" + Guid.NewGuid().ToString("N")));
                    bool cleaned = await provider.CleanupAsync(new SelfDeployDatabaseBackupResult { IsolatedTarget = target });
                    AssertFalse(cleaned, "arbitrary target refused");
                    AssertTrue(File.Exists(target), "arbitrary target preserved");
                }
                finally
                {
                    if (File.Exists(target)) File.Delete(target);
                }
            });

            await RunTest("SqliteBackup_RefusesMissingSourceWithoutCreatingIt", async () =>
            {
                string sourcePath = Path.Combine(Path.GetTempPath(), "selfdeploy-missing-" + Guid.NewGuid().ToString("N") + ".db");
                string root = Path.Combine(Path.GetTempPath(), "selfdeploy-provider-" + Guid.NewGuid().ToString("N"));
                try
                {
                    SelfDeployDatabaseBackupResult result = await new SelfDeployDatabaseBackupProvider(
                        new DatabaseSettings { Type = DatabaseTypeEnum.Sqlite, Filename = sourcePath }, root).CreateAndVerifyAsync();
                    AssertFalse(result.BackupValidated, "missing source fails closed");
                    AssertFalse(File.Exists(sourcePath), "missing source was not created");
                }
                finally
                {
                    TryDeleteDirectory(root);
                    if (File.Exists(sourcePath)) File.Delete(sourcePath);
                }
            });

            await RunTest("CandidateValidator_UsesOwnedCopyAndRequiresPassMarker", async () =>
            {
                using (TestDatabase database = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    string sourcePath = database.ConnectionString.Substring("Data Source=".Length).Split(';')[0];
                    string root = Path.Combine(Path.GetTempPath(), "selfdeploy-candidate-" + Guid.NewGuid().ToString("N"));
                    DatabaseSettings settings = new DatabaseSettings { Type = DatabaseTypeEnum.Sqlite, Filename = sourcePath };
                    try
                    {
                        SelfDeployDatabaseBackupProvider provider = new SelfDeployDatabaseBackupProvider(settings, root);
                        SelfDeployDatabaseBackupResult backup = await provider.CreateAndVerifyAsync();
                        CandidateCommandRunner commands = new CandidateCommandRunner();
                        SelfDeployCandidateProcessValidator validator = new SelfDeployCandidateProcessValidator(provider, commands, settings);
                        SelfDeployCandidateValidationResult validation = await validator.ValidateAsync(new SelfDeployPreflightRequest
                        {
                            CandidateServerDllPath = typeof(SelfDeployCandidateProcessValidator).Assembly.Location,
                            WorkingDirectory = root
                        }, backup);
                        AssertTrue(validation.CandidateValidated, "candidate proof");
                        if (!OperatingSystem.IsWindows())
                        {
                            AssertEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite,
                                commands.SettingsFileMode, "candidate settings are owner-only");
                            AssertEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                                commands.SettingsDirectoryMode, "candidate settings directory is owner-only");
                        }
                        AssertContains(backup.IsolatedTarget, commands.SettingsJson, "candidate settings use isolated copy");
                        AssertTrue(commands.Calls == 1, "candidate process called once");
                        AssertTrue(commands.LastRequest.Arguments.Contains("--validate-database"), "candidate validation mode");
                        AssertTrue(await provider.CleanupAsync(backup), "candidate cleanup");
                        AssertTrue(File.Exists(backup.ArtifactPath), "backup survives candidate cleanup");
                        AssertFalse(File.Exists(backup.IsolatedTarget), "isolated copy removed");
                    }
                    finally
                    {
                        TryDeleteDirectory(root);
                    }
                }
            });

            await RunTest("CandidateValidator_RejectsUnownedCopy", async () =>
            {
                string root = Path.Combine(Path.GetTempPath(), "selfdeploy-candidate-" + Guid.NewGuid().ToString("N"));
                DatabaseSettings settings = new DatabaseSettings { Type = DatabaseTypeEnum.Sqlite };
                SelfDeployDatabaseBackupProvider provider = new SelfDeployDatabaseBackupProvider(settings, root);
                CandidateCommandRunner commands = new CandidateCommandRunner();
                SelfDeployCandidateProcessValidator validator = new SelfDeployCandidateProcessValidator(provider, commands, settings);
                SelfDeployCandidateValidationResult validation = await validator.ValidateAsync(new SelfDeployPreflightRequest
                {
                    CandidateServerDllPath = typeof(SelfDeployCandidateProcessValidator).Assembly.Location,
                    WorkingDirectory = root
                }, new SelfDeployDatabaseBackupResult
                {
                    BackupValidated = true,
                    RestoreVerified = true,
                    IsolatedTarget = Path.Combine(root, "arbitrary.db"),
                    OwnershipToken = "forged"
                });
                AssertFalse(validation.CandidateValidated, "unowned copy fails closed");
                AssertEqual(0, commands.Calls, "unowned copy does not run candidate");
                TryDeleteDirectory(root);
            });

            await RunTest("Preflight_CleansCopyAfterCandidateFailure", async () =>
            {
                using (TestDatabase database = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    string sourcePath = database.ConnectionString.Substring("Data Source=".Length).Split(';')[0];
                    string root = Path.Combine(Path.GetTempPath(), "selfdeploy-preflight-" + Guid.NewGuid().ToString("N"));
                    DatabaseSettings settings = new DatabaseSettings { Type = DatabaseTypeEnum.Sqlite, Filename = sourcePath };
                    try
                    {
                        SelfDeployDatabaseBackupProvider provider = new SelfDeployDatabaseBackupProvider(settings, root);
                        CandidateCommandRunner commands = new CandidateCommandRunner { ExitCode = 1 };
                        SelfDeployBackupRestorePreflight preflight = new SelfDeployBackupRestorePreflight(
                            provider,
                            new SelfDeployCandidateProcessValidator(provider, commands, settings));
                        SelfDeployPreflightResult result = await preflight.ValidateAsync(new SelfDeployPreflightRequest
                        {
                            CandidateServerDllPath = typeof(SelfDeployCandidateProcessValidator).Assembly.Location,
                            WorkingDirectory = root
                        });
                        AssertFalse(result.IsSafeToCutover, "candidate failure blocks cutover");
                        AssertFalse(result.CandidateValidated, "candidate failure has no proof");
                        AssertContains("candidate_database_validation_failed", result.FailureReason, "candidate failure reason");
                        AssertTrue(Directory.GetFiles(root, "armada-backup.db", SearchOption.AllDirectories).Length == 1, "backup survives candidate failure cleanup");
                    }
                    finally
                    {
                        TryDeleteDirectory(root);
                    }
                }
            });

            await RunTest("Preflight_NullCandidateResultFailsClosed", async () =>
            {
                using (TestDatabase database = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    string sourcePath = database.ConnectionString.Substring("Data Source=".Length).Split(';')[0];
                    string root = Path.Combine(Path.GetTempPath(), "selfdeploy-preflight-" + Guid.NewGuid().ToString("N"));
                    DatabaseSettings settings = new DatabaseSettings { Type = DatabaseTypeEnum.Sqlite, Filename = sourcePath };
                    try
                    {
                        SelfDeployDatabaseBackupProvider provider = new SelfDeployDatabaseBackupProvider(settings, root);
                        SelfDeployBackupRestorePreflight preflight = new SelfDeployBackupRestorePreflight(provider, new NullCandidateValidator());
                        SelfDeployPreflightResult result = await preflight.ValidateAsync(new SelfDeployPreflightRequest());
                        AssertFalse(result.IsSafeToCutover, "null candidate result blocks cutover");
                        AssertFalse(result.CandidateValidated, "null candidate result has no proof");
                        AssertContains("candidate_validation_failed", result.FailureReason, "null candidate failure reason");
                    }
                    finally
                    {
                        TryDeleteDirectory(root);
                    }
                }
            });

            await RunTest("NativeProviders_RestoreVerificationFailureReturnsNoProof", async () =>
            {
                foreach (DatabaseTypeEnum type in new[]
                {
                    DatabaseTypeEnum.Mysql,
                    DatabaseTypeEnum.Postgresql,
                    DatabaseTypeEnum.SqlServer
                })
                {
                    int verificationCall = type == DatabaseTypeEnum.Mysql ? 5 : type == DatabaseTypeEnum.SqlServer ? 6 : 4;
                    RecordingCommandRunner commands = new RecordingCommandRunner { FailOnCall = verificationCall };
                    DatabaseSettings settings = new DatabaseSettings
                    {
                        Type = type,
                        Hostname = "db.example.test",
                        Username = "armada_user",
                        Password = "sentinel-password",
                        DatabaseName = "armada"
                    };
                    string root = Path.Combine(Path.GetTempPath(), "selfdeploy-provider-" + Guid.NewGuid().ToString("N"));
                    try
                    {
                        SelfDeployDatabaseBackupResult result = await new SelfDeployDatabaseBackupProvider(
                            settings,
                            root,
                            commands,
                            type == DatabaseTypeEnum.SqlServer ? root : null).CreateAndVerifyAsync();
                        AssertFalse(result.BackupValidated, type + " failed verification backup proof");
                        AssertFalse(result.RestoreVerified, type + " failed verification restore proof");
                        AssertContains("verification", result.FailureReason, type + " verification failure code");
                    }
                    finally
                    {
                        TryDeleteDirectory(root);
                    }
                }
            });

            await RunTest("NativeProviders_PartialRestoreAttemptsOwnedCleanup", async () =>
            {
                foreach (DatabaseTypeEnum type in new[]
                {
                    DatabaseTypeEnum.Mysql,
                    DatabaseTypeEnum.Postgresql,
                    DatabaseTypeEnum.SqlServer
                })
                {
                    int restoreCall = type == DatabaseTypeEnum.Mysql ? 4 : type == DatabaseTypeEnum.Postgresql ? 3 : 5;
                    RecordingCommandRunner commands = new RecordingCommandRunner { FailOnCall = restoreCall };
                    string root = Path.Combine(Path.GetTempPath(), "selfdeploy-provider-" + Guid.NewGuid().ToString("N"));
                    DatabaseSettings settings = new DatabaseSettings
                    {
                        Type = type,
                        Hostname = "custom-db.example",
                        Username = "armada_user",
                        Password = "sentinel-password",
                        DatabaseName = "live_custom"
                    };
                    try
                    {
                        SelfDeployDatabaseBackupResult result = await new SelfDeployDatabaseBackupProvider(
                            settings,
                            root,
                            commands,
                            type == DatabaseTypeEnum.SqlServer ? root : null).CreateAndVerifyAsync();
                        AssertFalse(result.RestoreVerified, type + " partial restore has no proof");
                        AssertTrue(commands.Calls.Any(call => call.FileName.Contains("drop", StringComparison.OrdinalIgnoreCase)
                            || call.Arguments.Any(argument => argument.Contains("DROP", StringComparison.OrdinalIgnoreCase))), type + " partial restore cleanup");
                    }
                    finally
                    {
                        TryDeleteDirectory(root);
                    }
                }
            });
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, true);
            }
            catch
            {
            }
        }

        private static SelfDeployNativeCommandRequest BuildFloodCommand()
        {
            if (OperatingSystem.IsWindows())
            {
                return new SelfDeployNativeCommandRequest
                {
                    FileName = "cmd.exe",
                    Arguments = new[] { "/c", "for /L %i in (1,1,100000) do @echo output" }
                };
            }

            return new SelfDeployNativeCommandRequest
            {
                FileName = "/bin/sh",
                Arguments = new[] { "-c", "yes output | head -c 400000" }
            };
        }

        private static SelfDeployNativeCommandRequest BuildSleepCommand()
        {
            if (OperatingSystem.IsWindows())
            {
                return new SelfDeployNativeCommandRequest
                {
                    FileName = "ping.exe",
                    Arguments = new[] { "-n", "30", "127.0.0.1" }
                };
            }

            return new SelfDeployNativeCommandRequest
            {
                FileName = "/bin/sh",
                Arguments = new[] { "-c", "sleep 30" }
            };
        }

        private static SelfDeployNativeCommandRequest BuildInheritedPipeCommand()
        {
            return new SelfDeployNativeCommandRequest
            {
                FileName = "/bin/sh",
                Arguments = new[] { "-c", "(sleep 30) & exit 0" }
            };
        }

        private void AssertPrivateFile(string path)
        {
            if (OperatingSystem.IsWindows()) return;
            UnixFileMode mode = File.GetUnixFileMode(path);
            UnixFileMode publicBits = UnixFileMode.GroupRead | UnixFileMode.GroupWrite
                | UnixFileMode.GroupExecute | UnixFileMode.OtherRead
                | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
            AssertEqual(UnixFileMode.None, mode & publicBits, "self-deploy secret path has no public bits");
        }

        private sealed class RecordingCommandRunner : Armada.Core.Services.Interfaces.ISelfDeployNativeCommandRunner
        {
            public List<SelfDeployNativeCommandRequest> Calls { get; } = new List<SelfDeployNativeCommandRequest>();
            public bool FailFirstCall { get; set; }
            public int FailOnCall { get; set; }

            public Task<SelfDeployNativeCommandResult> RunAsync(SelfDeployNativeCommandRequest request, CancellationToken token = default)
            {
                Calls.Add(request);
                if ((FailFirstCall && Calls.Count == 1) || (FailOnCall > 0 && Calls.Count == FailOnCall))
                {
                    return Task.FromResult(new SelfDeployNativeCommandResult { ExitCode = 1 });
                }

                foreach (string argument in request.Arguments)
                {
                    string? path = argument.StartsWith("--result-file=", StringComparison.Ordinal)
                        ? argument.Substring("--result-file=".Length)
                        : argument.StartsWith("--file=", StringComparison.Ordinal)
                            ? argument.Substring("--file=".Length)
                            : null;
                    if (!String.IsNullOrEmpty(path))
                    {
                        string? directory = Path.GetDirectoryName(path);
                        if (!String.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                        File.WriteAllText(path, "native backup fixture");
                    }
                }

                string output = request.Arguments.Any(argument => argument.Contains("ENGINE IS NULL", StringComparison.Ordinal))
                    ? "0"
                    : "1";
                return Task.FromResult(new SelfDeployNativeCommandResult { ExitCode = 0, StandardOutput = output });
            }
        }

        private sealed class CandidateCommandRunner : ISelfDeployNativeCommandRunner
        {
            public int Calls { get; private set; }
            public int ExitCode { get; set; }
            public string SettingsJson { get; private set; } = String.Empty;
            public UnixFileMode SettingsFileMode { get; private set; }
            public UnixFileMode SettingsDirectoryMode { get; private set; }
            public SelfDeployNativeCommandRequest LastRequest { get; private set; } = new SelfDeployNativeCommandRequest();

            public Task<SelfDeployNativeCommandResult> RunAsync(SelfDeployNativeCommandRequest request, CancellationToken token = default)
            {
                Calls++;
                LastRequest = request;
                if (request.EnvironmentVariables.TryGetValue(Armada.Core.Constants.DataDirectoryOverrideVariable, out string? directory))
                {
                    string settingsPath = Path.Combine(directory, "settings.json");
                    if (File.Exists(settingsPath))
                    {
                        SettingsJson = File.ReadAllText(settingsPath);
                        if (!OperatingSystem.IsWindows())
                        {
                            SettingsFileMode = File.GetUnixFileMode(settingsPath);
                            SettingsDirectoryMode = File.GetUnixFileMode(directory);
                        }
                    }
                }
                return Task.FromResult(new SelfDeployNativeCommandResult
                {
                    ExitCode = ExitCode,
                    StandardOutput = ExitCode == 0 ? "DATABASE VALIDATION PASSED; schema version 1" : String.Empty
                });
            }
        }

        private sealed class NullCandidateValidator : ISelfDeployCandidateValidator
        {
            public Task<SelfDeployCandidateValidationResult> ValidateAsync(
                SelfDeployPreflightRequest request,
                SelfDeployDatabaseBackupResult backup,
                CancellationToken token = default)
            {
                return Task.FromResult<SelfDeployCandidateValidationResult>(null!);
            }
        }
    }
}
