namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text.RegularExpressions;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Microsoft.Data.Sqlite;

    /// <summary>
    /// Provider-native backup and isolated-restore proof for all Armada database providers.
    /// Candidate binary validation is intentionally a separate preflight stage.
    /// </summary>
    public sealed class SelfDeployDatabaseBackupProvider : ISelfDeployDatabaseBackupProvider
    {
        private readonly DatabaseSettings _Settings;
        private readonly string _BackupDirectory;
        private readonly string? _SqlServerBackupDirectory;
        private readonly ISelfDeployNativeCommandRunner _Commands;
        private readonly TimeSpan _OperationTimeout;
        private readonly Dictionary<string, string> _OwnedTargets = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly object _OwnershipGate = new object();

        /// <summary>
        /// Instantiate a provider using the operating-system native database utilities.
        /// </summary>
        /// <param name="settings">Database settings.</param>
        /// <param name="backupDirectory">Directory for disposable preflight artifacts.</param>
        /// <param name="commands">Optional injected command runner.</param>
        /// <param name="sqlServerBackupDirectory">Server-visible backup directory required for SQL Server.</param>
        /// <param name="operationTimeout">Maximum duration for one backup and restore proof operation.</param>
        public SelfDeployDatabaseBackupProvider(
            DatabaseSettings settings,
            string backupDirectory,
            ISelfDeployNativeCommandRunner? commands = null,
            string? sqlServerBackupDirectory = null,
            TimeSpan? operationTimeout = null)
        {
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            if (String.IsNullOrWhiteSpace(backupDirectory)) throw new ArgumentException("Backup directory is required.", nameof(backupDirectory));
            _BackupDirectory = Path.GetFullPath(backupDirectory);
            _SqlServerBackupDirectory = String.IsNullOrWhiteSpace(sqlServerBackupDirectory)
                ? null
                : sqlServerBackupDirectory.Trim();
            _Commands = commands ?? new SelfDeployNativeCommandRunner();
            _OperationTimeout = operationTimeout ?? TimeSpan.FromMinutes(30);
            if (_OperationTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(operationTimeout));
        }

        /// <inheritdoc />
        public async Task<SelfDeployDatabaseBackupResult> CreateAndVerifyAsync(CancellationToken token = default)
        {
            string operationDirectory = Path.Combine(
                _BackupDirectory,
                "self-deploy-" + Guid.NewGuid().ToString("N"));
            string artifactPath = Path.Combine(operationDirectory, "armada-backup" + ArtifactExtension(_Settings.Type));
            string isolatedTarget = _Settings.Type == DatabaseTypeEnum.Sqlite
                ? Path.Combine(operationDirectory, "isolated.db")
                : "armada_preflight_" + Guid.NewGuid().ToString("N");
            string stage = "backup_not_started";
            try
            {
                SelfDeployPrivateFile.CreateDirectory(_BackupDirectory);
                SelfDeployPrivateFile.CreateDirectory(operationDirectory);
            }
            catch (SelfDeployPrivateStorageException ex)
            {
                return Failure(ex.FailureReason, artifactPath, isolatedTarget);
            }

            using (CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                timeout.CancelAfter(_OperationTimeout);
                try
                {
                    SelfDeployDatabaseBackupResult result;
                    switch (_Settings.Type)
                    {
                        case DatabaseTypeEnum.Sqlite:
                            stage = "sqlite_backup_failed";
                            result = await BackupSqliteAsync(artifactPath, isolatedTarget, timeout.Token).ConfigureAwait(false);
                            break;

                        case DatabaseTypeEnum.Mysql:
                            stage = "mysql_backup_failed";
                            result = await BackupMysqlAsync(artifactPath, isolatedTarget, timeout.Token).ConfigureAwait(false);
                            break;

                        case DatabaseTypeEnum.Postgresql:
                            stage = "postgresql_backup_failed";
                            result = await BackupPostgresqlAsync(artifactPath, isolatedTarget, timeout.Token).ConfigureAwait(false);
                            break;

                        case DatabaseTypeEnum.SqlServer:
                            stage = "sqlserver_backup_failed";
                            result = await BackupSqlServerAsync(artifactPath, isolatedTarget, timeout.Token).ConfigureAwait(false);
                            break;

                        default:
                            return Failure("unsupported_database_provider", artifactPath, isolatedTarget);
                    }

                    return result;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (SelfDeployPrivateStorageException ex)
                {
                    return Failure(ex.FailureReason, artifactPath, isolatedTarget);
                }
                catch (OperationCanceledException)
                {
                    return Failure("native_operation_timeout", artifactPath, isolatedTarget);
                }
                catch (SelfDeployBackupFailureException ex)
                {
                    return Failure(ex.FailureReason, artifactPath, isolatedTarget);
                }
                catch
                {
                    return Failure(stage, artifactPath, isolatedTarget);
                }
            }
        }

        /// <inheritdoc />
        public async Task<bool> CleanupAsync(SelfDeployDatabaseBackupResult result, CancellationToken token = default)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));
            using (CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                try
                {
                    if (_Settings.Type == DatabaseTypeEnum.Sqlite)
                    {
                        if (!OwnsTarget(result)) return false;
                        DeleteFileAndSidecars(result.IsolatedTarget);
                        return ReleaseTarget(result);
                    }

                    if (!OwnsTarget(result)) return false;
                    Dictionary<string, string> environment = BuildSecretEnvironment();
                    if (_Settings.Type == DatabaseTypeEnum.Mysql)
                    {
                        SelfDeployNativeCommandResult drop = await _Commands.RunAsync(CreateMysqlRequest(
                            "mysql", result.IsolatedTarget, "DROP DATABASE " + QuoteMysqlIdentifier(result.IsolatedTarget), environment), timeout.Token).ConfigureAwait(false);
                        return drop.Succeeded && ReleaseTarget(result);
                    }

                    if (_Settings.Type == DatabaseTypeEnum.Postgresql)
                    {
                        SelfDeployNativeCommandResult drop = await _Commands.RunAsync(new SelfDeployNativeCommandRequest
                        {
                            FileName = "dropdb",
                            Arguments = PostgresqlConnectionArguments(result.IsolatedTarget),
                            EnvironmentVariables = environment
                        }, timeout.Token).ConfigureAwait(false);
                        return drop.Succeeded && ReleaseTarget(result);
                    }

                    string server = _Settings.Hostname + "," + EffectivePort(1433);
                    string dropSql = "IF DB_ID(N'" + QuoteSqlLiteral(result.IsolatedTarget) + "') IS NOT NULL ALTER DATABASE "
                        + QuoteSqlServerIdentifier(result.IsolatedTarget) + " SET SINGLE_USER WITH ROLLBACK IMMEDIATE; IF DB_ID(N'"
                        + QuoteSqlLiteral(result.IsolatedTarget) + "') IS NOT NULL DROP DATABASE " + QuoteSqlServerIdentifier(result.IsolatedTarget);
                    SelfDeployNativeCommandResult sqlDrop = await _Commands.RunAsync(new SelfDeployNativeCommandRequest
                    {
                        FileName = "sqlcmd",
                        Arguments = SqlServerArguments(server, "master", dropSql),
                        EnvironmentVariables = environment
                    }, timeout.Token).ConfigureAwait(false);
                    return sqlDrop.Succeeded && ReleaseTarget(result);
                }
                catch (OperationCanceledException)
                {
                    return false;
                }
                catch
                {
                    return false;
                }
            }
        }

        private async Task<SelfDeployDatabaseBackupResult> BackupSqliteAsync(
            string artifactPath,
            string isolatedTarget,
            CancellationToken token)
        {
            string sourcePath = Path.GetFullPath(_Settings.Filename);
            if (!File.Exists(sourcePath)) throw new SelfDeployBackupFailureException("sqlite_source_missing");
            string sourceConnectionString = "Data Source=" + sourcePath + ";Mode=ReadWrite;Pooling=False";
            string targetConnectionString = "Data Source=" + artifactPath + ";Pooling=False";
            using (SqliteConnection source = new SqliteConnection(sourceConnectionString))
            using (SqliteConnection target = new SqliteConnection(targetConnectionString))
            {
                await source.OpenAsync(token).ConfigureAwait(false);
                await target.OpenAsync(token).ConfigureAwait(false);
                source.BackupDatabase(target);
            }

            ValidateFile(artifactPath);
            File.Copy(artifactPath, isolatedTarget, true);
            string ownershipToken = RegisterTarget(isolatedTarget);
            bool completed = false;
            try
            {
                await VerifySqliteCopyAsync(isolatedTarget, token).ConfigureAwait(false);
                completed = true;
                return Success(artifactPath, isolatedTarget);
            }
            finally
            {
                if (!completed && !await CleanupAsync(new SelfDeployDatabaseBackupResult { IsolatedTarget = isolatedTarget, OwnershipToken = ownershipToken }, CancellationToken.None).ConfigureAwait(false))
                    throw new SelfDeployBackupFailureException("sqlite_restore_cleanup_failed");
            }
        }

        private async Task<SelfDeployDatabaseBackupResult> BackupMysqlAsync(
            string artifactPath,
            string isolatedTarget,
            CancellationToken token)
        {
            Dictionary<string, string> environment = BuildSecretEnvironment();
            SelfDeployNativeCommandResult engineCheck = await RunCheckedResultAsync(new SelfDeployNativeCommandRequest
            {
                FileName = "mysql",
                Arguments = MysqlConnectionArguments(_Settings.DatabaseName, "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND (ENGINE IS NULL OR ENGINE <> 'InnoDB')"),
                EnvironmentVariables = environment
            }, "mysql_storage_engine_validation_failed", token).ConfigureAwait(false);
            ValidateZeroOutput(engineCheck);

            await RunCheckedAsync(new SelfDeployNativeCommandRequest
            {
                FileName = "mysqldump",
                Arguments = new[]
                {
                    "--single-transaction", "--routines", "--events", "--triggers", "--hex-blob",
                    "--host=" + _Settings.Hostname, "--port=" + EffectivePort(3306), "--user=" + _Settings.Username,
                    "--result-file=" + artifactPath, _Settings.DatabaseName
                },
                EnvironmentVariables = environment
            }, "mysql_backup_failed", token).ConfigureAwait(false);
            ValidateFile(artifactPath);

            await RunCheckedAsync(CreateMysqlRequest("mysql", String.Empty, "CREATE DATABASE " + QuoteMysqlIdentifier(isolatedTarget), environment), "mysql_restore_target_failed", token).ConfigureAwait(false);
            string ownershipToken = RegisterTarget(isolatedTarget);
            bool completed = false;
            try
            {
                await RunCheckedAsync(new SelfDeployNativeCommandRequest
                {
                    FileName = "mysql",
                    Arguments = MysqlConnectionArguments(isolatedTarget),
                    EnvironmentVariables = environment,
                    StandardInputFilePath = artifactPath
                }, "mysql_restore_failed", token).ConfigureAwait(false);
                SelfDeployNativeCommandResult verification = await RunCheckedResultAsync(CreateMysqlRequest("mysql", isolatedTarget,
                    "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = 'schema_migrations'",
                    environment), "mysql_restore_verification_failed", token).ConfigureAwait(false);
                ValidateVerificationOutput(verification, "mysql_restore_verification_failed");
                completed = true;
                return Success(artifactPath, isolatedTarget);
            }
            finally
            {
                if (!completed && !await CleanupAsync(new SelfDeployDatabaseBackupResult { IsolatedTarget = isolatedTarget, OwnershipToken = ownershipToken }, CancellationToken.None).ConfigureAwait(false))
                    throw new SelfDeployBackupFailureException("mysql_restore_cleanup_failed");
            }
        }

        private async Task<SelfDeployDatabaseBackupResult> BackupPostgresqlAsync(
            string artifactPath,
            string isolatedTarget,
            CancellationToken token)
        {
            Dictionary<string, string> environment = BuildSecretEnvironment();
            await RunCheckedAsync(new SelfDeployNativeCommandRequest
            {
                FileName = "pg_dump",
                Arguments = new[]
                {
                    "--format=custom", "--file=" + artifactPath,
                    "--host=" + _Settings.Hostname, "--port=" + EffectivePort(5432),
                    "--username=" + _Settings.Username, _Settings.DatabaseName
                },
                EnvironmentVariables = environment
            }, "postgresql_backup_failed", token).ConfigureAwait(false);
            ValidateFile(artifactPath);

            await RunCheckedAsync(new SelfDeployNativeCommandRequest
            {
                FileName = "createdb",
                Arguments = PostgresqlConnectionArguments(isolatedTarget),
                EnvironmentVariables = environment
            }, "postgresql_restore_target_failed", token).ConfigureAwait(false);
            string ownershipToken = RegisterTarget(isolatedTarget);
            bool completed = false;
            try
            {
                await RunCheckedAsync(new SelfDeployNativeCommandRequest
                {
                    FileName = "pg_restore",
                    Arguments = new[]
                    {
                        "--exit-on-error", "--no-owner", "--dbname=" + isolatedTarget,
                        "--host=" + _Settings.Hostname, "--port=" + EffectivePort(5432),
                        "--username=" + _Settings.Username, artifactPath
                    },
                    EnvironmentVariables = environment
                }, "postgresql_restore_failed", token).ConfigureAwait(false);
                SelfDeployNativeCommandResult verification = await RunCheckedResultAsync(new SelfDeployNativeCommandRequest
                {
                    FileName = "psql",
                    Arguments = new[]
                    {
                        "--tuples-only", "--no-align",
                        "--dbname=" + isolatedTarget, "--host=" + _Settings.Hostname,
                        "--port=" + EffectivePort(5432), "--username=" + _Settings.Username,
                        "--command=SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = current_schema() AND table_name = 'schema_migrations'"
                    },
                    EnvironmentVariables = environment
                }, "postgresql_restore_verification_failed", token).ConfigureAwait(false);
                ValidateVerificationOutput(verification, "postgresql_restore_verification_failed");
                completed = true;
                return Success(artifactPath, isolatedTarget);
            }
            finally
            {
                if (!completed && !await CleanupAsync(new SelfDeployDatabaseBackupResult { IsolatedTarget = isolatedTarget, OwnershipToken = ownershipToken }, CancellationToken.None).ConfigureAwait(false))
                    throw new SelfDeployBackupFailureException("postgresql_restore_cleanup_failed");
            }
        }

        private async Task<SelfDeployDatabaseBackupResult> BackupSqlServerAsync(
            string artifactPath,
            string isolatedTarget,
            CancellationToken token)
        {
            if (String.IsNullOrWhiteSpace(_SqlServerBackupDirectory))
                throw new SelfDeployBackupFailureException("sqlserver_server_backup_directory_not_configured");
            string serverDirectory = _SqlServerBackupDirectory!.TrimEnd('\\', '/');
            string serverSeparator = serverDirectory.IndexOf('\\') >= 0 ? "\\" : "/";
            string serverArtifactPath = serverDirectory
                + serverSeparator + "armada-backup-" + Guid.NewGuid().ToString("N") + ".bak";
            Dictionary<string, string> environment = BuildSecretEnvironment();
            string server = _Settings.Hostname + "," + EffectivePort(1433);
            string directoryCheckSql = "DECLARE @f TABLE ([File Exists] int, [File is Directory] int, [Parent Directory Exists] int); INSERT INTO @f EXEC master.dbo.xp_fileexist N'"
                + QuoteSqlLiteral(serverDirectory) + "'; IF NOT EXISTS (SELECT 1 FROM @f WHERE [File is Directory] = 1) THROW 51000, 'server backup directory is unavailable', 1";
            await RunCheckedAsync(new SelfDeployNativeCommandRequest
            {
                FileName = "sqlcmd",
                Arguments = SqlServerArguments(server, "master", directoryCheckSql),
                EnvironmentVariables = environment
            }, "sqlserver_server_backup_directory_unavailable", token).ConfigureAwait(false);
            string backupSql = "BACKUP DATABASE " + QuoteSqlServerIdentifier(_Settings.DatabaseName)
                + " TO DISK = N'" + QuoteSqlLiteral(serverArtifactPath) + "' WITH COPY_ONLY, INIT";
            await RunCheckedAsync(new SelfDeployNativeCommandRequest
            {
                FileName = "sqlcmd",
                Arguments = SqlServerArguments(server, "master", backupSql),
                EnvironmentVariables = environment
            }, "sqlserver_backup_failed", token).ConfigureAwait(false);
            await RunCheckedAsync(new SelfDeployNativeCommandRequest
            {
                FileName = "sqlcmd",
                Arguments = SqlServerArguments(server, "master", "RESTORE VERIFYONLY FROM DISK = N'" + QuoteSqlLiteral(serverArtifactPath) + "'"),
                EnvironmentVariables = environment
            }, "sqlserver_backup_validation_failed", token).ConfigureAwait(false);

            SelfDeployNativeCommandResult targetCheck = await RunCheckedResultAsync(new SelfDeployNativeCommandRequest
            {
                FileName = "sqlcmd",
                Arguments = SqlServerArguments(server, "master", "SELECT CASE WHEN DB_ID(N'" + QuoteSqlLiteral(isolatedTarget) + "') IS NULL THEN 1 ELSE 0 END"),
                EnvironmentVariables = environment
            }, "sqlserver_restore_target_exists", token).ConfigureAwait(false);
            ValidateVerificationOutput(targetCheck, "sqlserver_restore_target_exists");

            string ownershipToken = RegisterTarget(isolatedTarget);
            bool completed = false;
            try
            {
                string restoreSql = BuildSqlServerRestoreSql(serverArtifactPath, isolatedTarget);
                await RunCheckedAsync(new SelfDeployNativeCommandRequest
                {
                    FileName = "sqlcmd",
                    Arguments = SqlServerArguments(server, "master", restoreSql),
                    EnvironmentVariables = environment
                }, "sqlserver_restore_failed", token).ConfigureAwait(false);
                string verifySql = "IF OBJECT_ID(N'" + QuoteSqlLiteral(isolatedTarget) + ".dbo.schema_migrations', N'U') IS NULL THROW 51000, 'schema verification failed', 1";
                await RunCheckedAsync(new SelfDeployNativeCommandRequest
                {
                    FileName = "sqlcmd",
                    Arguments = SqlServerArguments(server, isolatedTarget, verifySql),
                    EnvironmentVariables = environment
                }, "sqlserver_restore_verification_failed", token).ConfigureAwait(false);
                completed = true;
                return Success(serverArtifactPath, isolatedTarget);
            }
            finally
            {
                if (!completed && !await CleanupAsync(new SelfDeployDatabaseBackupResult { IsolatedTarget = isolatedTarget, OwnershipToken = ownershipToken }, CancellationToken.None).ConfigureAwait(false))
                    throw new SelfDeployBackupFailureException("sqlserver_restore_cleanup_failed");
            }
        }

        private async Task VerifySqliteCopyAsync(string path, CancellationToken token)
        {
            using (SqliteConnection connection = new SqliteConnection("Data Source=" + path + ";Mode=ReadOnly;Pooling=False"))
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (SqliteCommand integrity = connection.CreateCommand())
                {
                    integrity.CommandText = "PRAGMA integrity_check;";
                    object? check = await integrity.ExecuteScalarAsync(token).ConfigureAwait(false);
                    if (!String.Equals(Convert.ToString(check), "ok", StringComparison.OrdinalIgnoreCase))
                        throw new SelfDeployBackupFailureException("sqlite_restore_verification_failed");
                }
                using (SqliteCommand schema = connection.CreateCommand())
                {
                    schema.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='schema_migrations';";
                    object? table = await schema.ExecuteScalarAsync(token).ConfigureAwait(false);
                    if (table == null || table == DBNull.Value) throw new SelfDeployBackupFailureException("sqlite_restore_verification_failed");
                }
            }
        }

        private async Task RunCheckedAsync(SelfDeployNativeCommandRequest request, string failureReason, CancellationToken token)
        {
            await RunCheckedResultAsync(request, failureReason, token).ConfigureAwait(false);
        }

        private async Task<SelfDeployNativeCommandResult> RunCheckedResultAsync(
            SelfDeployNativeCommandRequest request,
            string failureReason,
            CancellationToken token)
        {
            SelfDeployNativeCommandResult result = await _Commands.RunAsync(request, token).ConfigureAwait(false);
            if (!result.Succeeded) throw new SelfDeployBackupFailureException(failureReason);
            return result;
        }

        private static void ValidateVerificationOutput(SelfDeployNativeCommandResult result, string failureReason)
        {
            string[] lines = result.StandardOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string line in lines)
            {
                if (String.Equals(line.Trim(), "1", StringComparison.Ordinal)) return;
            }

            throw new SelfDeployBackupFailureException(failureReason);
        }

        private static void ValidateZeroOutput(SelfDeployNativeCommandResult result)
        {
            string[] lines = result.StandardOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            bool found = false;
            foreach (string line in lines)
            {
                if (!String.Equals(line.Trim(), "0", StringComparison.Ordinal))
                    throw new SelfDeployBackupFailureException("mysql_storage_engine_validation_failed");
                found = true;
            }
            if (!found) throw new SelfDeployBackupFailureException("mysql_storage_engine_validation_failed");
        }

        private SelfDeployDatabaseBackupResult Success(string artifactPath, string isolatedTarget)
        {
            string ownershipToken = FindOwnershipToken(isolatedTarget);
            if (String.IsNullOrEmpty(ownershipToken)) ownershipToken = RegisterTarget(isolatedTarget);
            return new SelfDeployDatabaseBackupResult
            {
                BackupValidated = true,
                RestoreVerified = true,
                ArtifactPath = artifactPath,
                IsolatedTarget = isolatedTarget,
                OwnershipToken = ownershipToken
            };
        }

        private string RegisterTarget(string target)
        {
            string token = Guid.NewGuid().ToString("N");
            lock (_OwnershipGate) _OwnedTargets[token] = target;
            return token;
        }

        /// <inheritdoc />
        public bool OwnsTarget(SelfDeployDatabaseBackupResult result)
        {
            if (result == null || String.IsNullOrWhiteSpace(result.OwnershipToken)) return false;
            lock (_OwnershipGate)
            {
                return _OwnedTargets.TryGetValue(result.OwnershipToken, out string? target)
                    && String.Equals(target, result.IsolatedTarget, StringComparison.Ordinal);
            }
        }

        private bool ReleaseTarget(SelfDeployDatabaseBackupResult result)
        {
            lock (_OwnershipGate) return _OwnedTargets.Remove(result.OwnershipToken);
        }

        private string FindOwnershipToken(string target)
        {
            lock (_OwnershipGate)
            {
                foreach (KeyValuePair<string, string> ownership in _OwnedTargets)
                {
                    if (String.Equals(ownership.Value, target, StringComparison.Ordinal)) return ownership.Key;
                }
            }

            return String.Empty;
        }

        private static void DeleteFileAndSidecars(string path)
        {
            foreach (string candidate in new[] { path, path + "-wal", path + "-shm" })
            {
                if (File.Exists(candidate)) File.Delete(candidate);
            }
        }

        private static SelfDeployDatabaseBackupResult Failure(string reason, string artifactPath, string isolatedTarget)
        {
            return new SelfDeployDatabaseBackupResult
            {
                FailureReason = reason,
                ArtifactPath = artifactPath,
                IsolatedTarget = isolatedTarget
            };
        }

        private static void ValidateFile(string path)
        {
            if (!File.Exists(path) || new FileInfo(path).Length == 0) throw new SelfDeployBackupFailureException("backup_artifact_missing");
            SelfDeployPrivateFile.RestrictFile(path);
        }

        private int EffectivePort(int defaultPort)
        {
            return _Settings.Port > 0 ? _Settings.Port : defaultPort;
        }

        private Dictionary<string, string> BuildSecretEnvironment()
        {
            string variable = _Settings.Type == DatabaseTypeEnum.Mysql
                ? "MYSQL_PWD"
                : _Settings.Type == DatabaseTypeEnum.Postgresql
                    ? "PGPASSWORD"
                    : "SQLCMDPASSWORD";
            return new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [variable] = _Settings.Password
            };
        }

        private SelfDeployNativeCommandRequest CreateMysqlRequest(
            string executable,
            string database,
            string statement,
            IReadOnlyDictionary<string, string> environment)
        {
            return new SelfDeployNativeCommandRequest
            {
                FileName = executable,
                Arguments = MysqlConnectionArguments(database, statement),
                EnvironmentVariables = environment
            };
        }

        private IReadOnlyList<string> MysqlConnectionArguments(string database, string? statement = null)
        {
            List<string> arguments = new List<string>
            {
                "--batch",
                "--skip-column-names",
                "--host=" + _Settings.Hostname,
                "--port=" + EffectivePort(3306),
                "--user=" + _Settings.Username
            };
            if (!String.IsNullOrWhiteSpace(database)) arguments.Add("--database=" + database);
            if (!String.IsNullOrEmpty(statement)) arguments.Add("--execute=" + statement);
            return arguments;
        }

        private IReadOnlyList<string> PostgresqlConnectionArguments(string database)
        {
            return new[]
            {
                "--host=" + _Settings.Hostname,
                "--port=" + EffectivePort(5432),
                "--username=" + _Settings.Username,
                database
            };
        }

        private IReadOnlyList<string> SqlServerArguments(string server, string database, string statement)
        {
            return new[]
            {
                "-S", server,
                "-d", database,
                "-U", _Settings.Username,
                "-b",
                _Settings.RequireEncryption ? "-Nm" : "-No",
                "-C",
                "-Q", statement
            };
        }

        private static string BuildSqlServerRestoreSql(string artifactPath, string isolatedTarget)
        {
            string backupLiteral = QuoteSqlLiteral(artifactPath);
            string nestedBackupLiteral = QuoteSqlLiteral(backupLiteral);
            string targetIdentifier = QuoteSqlServerIdentifier(isolatedTarget);
            string destinationPrefix = QuoteSqlLiteral(QuoteSqlLiteral(artifactPath));
            string sql = @"DECLARE @f TABLE (LogicalName nvarchar(128), PhysicalName nvarchar(260), Type char(1), FileGroupName nvarchar(128), Size numeric(20,0), MaxSize numeric(20,0), FileId bigint, CreateLSN numeric(25,0), DropLSN numeric(25,0), UniqueId uniqueidentifier, ReadOnlyLSN numeric(25,0), ReadWriteLSN numeric(25,0), BackupSizeInBytes bigint, SourceBlockSize int, FileGroupId int, LogGroupGUID uniqueidentifier, DifferentialBaseLSN numeric(25,0), DifferentialBaseGUID uniqueidentifier, IsReadOnly bit, IsPresent bit, TDEThumbprint varbinary(32), SnapshotURL nvarchar(360));
INSERT INTO @f EXEC(N'RESTORE FILELISTONLY FROM DISK = N''{BACKUP}''');
DECLARE @logical nvarchar(128), @type char(1), @fileId bigint, @moves nvarchar(max) = N'', @separator nvarchar(10) = N'';
DECLARE files CURSOR LOCAL FAST_FORWARD FOR SELECT LogicalName, Type, FileId FROM @f ORDER BY FileId;
OPEN files;
FETCH NEXT FROM files INTO @logical, @type, @fileId;
WHILE @@FETCH_STATUS = 0
BEGIN
    SET @moves = @moves + @separator + N'MOVE N''' + REPLACE(@logical, '''', '''''') + N''' TO N''' + N'{DESTINATION}.' + CONVERT(nvarchar(20), @fileId) + CASE WHEN @type = 'L' THEN N'.ldf' ELSE N'.mdf' END + N'''';
    SET @separator = N', ';
    FETCH NEXT FROM files INTO @logical, @type, @fileId;
END;
CLOSE files;
DEALLOCATE files;
DECLARE @sql nvarchar(max) = N'RESTORE DATABASE {TARGET} FROM DISK = N''{BACKUP}'' WITH ' + @moves;
EXEC(@sql);";
            return Regex.Replace(
                sql,
                "\\{BACKUP\\}|\\{DESTINATION\\}|\\{TARGET\\}",
                match => match.Value switch
                {
                    "{BACKUP}" => nestedBackupLiteral,
                    "{DESTINATION}" => destinationPrefix,
                    _ => targetIdentifier
                },
                RegexOptions.CultureInvariant);
        }

        private static string QuoteMysqlIdentifier(string value)
        {
            return "`" + value.Replace("`", "``", StringComparison.Ordinal) + "`";
        }

        private static string QuoteSqlServerIdentifier(string value)
        {
            return "[" + value.Replace("]", "]]", StringComparison.Ordinal) + "]";
        }

        private static string QuoteSqlLiteral(string value)
        {
            return value.Replace("'", "''", StringComparison.Ordinal);
        }

        private static string ArtifactExtension(DatabaseTypeEnum type)
        {
            switch (type)
            {
                case DatabaseTypeEnum.Sqlite: return ".db";
                case DatabaseTypeEnum.Postgresql: return ".dump";
                case DatabaseTypeEnum.SqlServer: return ".bak";
                default: return ".sql";
            }
        }

        private sealed class SelfDeployBackupFailureException : Exception
        {
            public SelfDeployBackupFailureException(string failureReason)
            {
                FailureReason = failureReason;
            }

            public string FailureReason { get; }
        }
    }
}
