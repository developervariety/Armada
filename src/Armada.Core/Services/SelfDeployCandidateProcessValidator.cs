namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;

    /// <summary>
    /// Runs <c>--validate-database</c> from the candidate assembly with isolated settings.
    /// </summary>
    public sealed class SelfDeployCandidateProcessValidator : ISelfDeployCandidateValidator
    {
        private readonly ISelfDeployDatabaseBackupProvider _Backups;
        private readonly ISelfDeployNativeCommandRunner _Commands;
        private readonly DatabaseSettings _Database;
        private readonly TimeSpan _ValidationTimeout;

        /// <summary>Instantiate a candidate process validator.</summary>
        /// <param name="backups">Provider that owns the restored target.</param>
        /// <param name="commands">Injected command runner.</param>
        /// <param name="database">Effective database settings used by the running service.</param>
        /// <param name="validationTimeout">Maximum duration for candidate database validation.</param>
        public SelfDeployCandidateProcessValidator(
            ISelfDeployDatabaseBackupProvider backups,
            ISelfDeployNativeCommandRunner commands,
            DatabaseSettings database,
            TimeSpan? validationTimeout = null)
        {
            _Backups = backups ?? throw new ArgumentNullException(nameof(backups));
            _Commands = commands ?? throw new ArgumentNullException(nameof(commands));
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _ValidationTimeout = validationTimeout ?? TimeSpan.FromMinutes(10);
            if (_ValidationTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(validationTimeout));
        }

        /// <inheritdoc />
        public async Task<SelfDeployCandidateValidationResult> ValidateAsync(
            SelfDeployPreflightRequest request,
            SelfDeployDatabaseBackupResult backup,
            CancellationToken token = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (backup == null) throw new ArgumentNullException(nameof(backup));
            if (!_Backups.OwnsTarget(backup)) return Failure("isolated_target_not_owned");
            if (!backup.BackupValidated || !backup.RestoreVerified) return Failure("isolated_restore_proof_missing");
            if (String.IsNullOrWhiteSpace(request.CandidateServerDllPath)
                || !File.Exists(request.CandidateServerDllPath)) return Failure("candidate_binary_missing");

            string validationDirectory = Path.Combine(Path.GetTempPath(), "armada-candidate-" + Guid.NewGuid().ToString("N"));
            try
            {
                SelfDeployPrivateFile.CreateDirectory(validationDirectory);
            }
            catch (SelfDeployPrivateStorageException ex)
            {
                return Failure(ex.FailureReason);
            }
            SelfDeployCandidateValidationResult validation = Failure("candidate_validator_failed");
            using (CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                timeout.CancelAfter(_ValidationTimeout);
                try
                {
                    ArmadaSettings candidateSettings = BuildCandidateSettings(_Database, backup.IsolatedTarget, validationDirectory);
                    string settingsPath = Path.Combine(validationDirectory, "settings.json");
                    string json = JsonSerializer.Serialize(candidateSettings, new JsonSerializerOptions
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                        Converters = { new JsonStringEnumConverter() },
                        WriteIndented = false
                    });
                    await SelfDeployPrivateFile.WriteTextAsync(settingsPath, json, timeout.Token).ConfigureAwait(false);

                    SelfDeployNativeCommandResult result = await _Commands.RunAsync(new SelfDeployNativeCommandRequest
                    {
                        FileName = "dotnet",
                        Arguments = new[] { request.CandidateServerDllPath, "--validate-database" },
                        EnvironmentVariables = new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            [Constants.DataDirectoryOverrideVariable] = validationDirectory
                        },
                        WorkingDirectory = request.WorkingDirectory
                    }, timeout.Token).ConfigureAwait(false);
                    if (result == null || !result.Succeeded)
                    {
                        validation = Failure("candidate_database_validation_failed");
                    }
                    else if (result.StandardOutput.Contains(SelfDeployNativeCommandRunner.OutputTruncationMarker, StringComparison.Ordinal)
                        || result.StandardError.Contains(SelfDeployNativeCommandRunner.OutputTruncationMarker, StringComparison.Ordinal))
                    {
                        validation = Failure("candidate_database_validation_output_truncated");
                    }
                    else if (!result.StandardOutput.Contains("DATABASE VALIDATION PASSED", StringComparison.Ordinal))
                    {
                        validation = Failure("candidate_database_validation_unproved");
                    }
                    else
                    {
                        validation = new SelfDeployCandidateValidationResult { CandidateValidated = true };
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (SelfDeployPrivateStorageException ex)
                {
                    validation = Failure(ex.FailureReason);
                }
                catch (OperationCanceledException)
                {
                    validation = Failure("candidate_validation_timeout");
                }
                catch
                {
                    validation = Failure("candidate_validator_failed");
                }
                finally
                {
                    try
                    {
                        if (Directory.Exists(validationDirectory)) Directory.Delete(validationDirectory, true);
                    }
                    catch
                    {
                        validation = Failure("candidate_validator_cleanup_failed");
                    }
                }
            }

            return validation;
        }

        private static ArmadaSettings BuildCandidateSettings(
            DatabaseSettings sourceDatabase,
            string isolatedTarget,
            string dataDirectory)
        {
            DatabaseSettings candidateDatabase = new DatabaseSettings
            {
                Type = sourceDatabase.Type,
                Hostname = sourceDatabase.Hostname,
                Port = sourceDatabase.Port,
                Username = sourceDatabase.Username,
                Password = sourceDatabase.Password,
                DatabaseName = sourceDatabase.DatabaseName,
                Schema = sourceDatabase.Schema,
                RequireEncryption = sourceDatabase.RequireEncryption,
                LogQueries = sourceDatabase.LogQueries,
                MinPoolSize = sourceDatabase.MinPoolSize,
                MaxPoolSize = sourceDatabase.MaxPoolSize,
                ConnectionLifetimeSeconds = sourceDatabase.ConnectionLifetimeSeconds,
                ConnectionIdleTimeoutSeconds = sourceDatabase.ConnectionIdleTimeoutSeconds
            };
            if (candidateDatabase.Type == DatabaseTypeEnum.Sqlite)
                candidateDatabase.Filename = isolatedTarget;
            else
                candidateDatabase.DatabaseName = isolatedTarget;

            ArmadaSettings settings = new ArmadaSettings
            {
                DataDirectory = dataDirectory,
                Database = candidateDatabase
            };
            return settings;
        }

        private static SelfDeployCandidateValidationResult Failure(string reason)
        {
            return new SelfDeployCandidateValidationResult { FailureReason = reason };
        }
    }
}
