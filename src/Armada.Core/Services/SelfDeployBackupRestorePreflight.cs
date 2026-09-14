namespace Armada.Core.Services
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;

    /// <summary>
    /// Runs backup, isolated restore, candidate validation, and owned-target cleanup
    /// as one fail-closed preflight operation.
    /// </summary>
    public sealed class SelfDeployBackupRestorePreflight : ISelfDeployPreflight
    {
        private readonly ISelfDeployDatabaseBackupProvider _BackupProvider;
        private readonly ISelfDeployCandidateValidator? _CandidateValidator;

        /// <summary>Instantiate the native database preflight adapter.</summary>
        /// <param name="backupProvider">Provider that proves and owns the isolated target.</param>
        /// <param name="candidateValidator">Optional candidate validator. Omission fails closed.</param>
        public SelfDeployBackupRestorePreflight(
            ISelfDeployDatabaseBackupProvider backupProvider,
            ISelfDeployCandidateValidator? candidateValidator = null)
        {
            _BackupProvider = backupProvider ?? throw new ArgumentNullException(nameof(backupProvider));
            _CandidateValidator = candidateValidator;
        }

        /// <inheritdoc />
        public async Task<SelfDeployPreflightResult> ValidateAsync(
            SelfDeployPreflightRequest request,
            CancellationToken token = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            SelfDeployDatabaseBackupResult? backup = await _BackupProvider.CreateAndVerifyAsync(token).ConfigureAwait(false);
            if (backup == null)
            {
                return new SelfDeployPreflightResult { FailureReason = "backup_provider_returned_no_result" };
            }
            SelfDeployPreflightResult result;
            try
            {
                if (!backup.BackupValidated || !backup.RestoreVerified)
                {
                    result = MapBackupFailure(backup);
                }
                else if (_CandidateValidator == null)
                {
                    result = MapBackupFailure(backup, "candidate_validator_not_configured");
                }
                else
                {
                    SelfDeployCandidateValidationResult? candidate = await _CandidateValidator.ValidateAsync(request, backup, token).ConfigureAwait(false);
                    bool candidateProof = candidate != null
                        && candidate.CandidateValidated
                        && String.IsNullOrWhiteSpace(candidate.FailureReason);
                    result = new SelfDeployPreflightResult
                    {
                        BackupValidated = backup.BackupValidated,
                        RestoreVerified = backup.RestoreVerified,
                        CandidateValidated = candidateProof,
                        FailureReason = candidateProof
                            ? backup.FailureReason
                            : String.IsNullOrWhiteSpace(candidate?.FailureReason) ? "candidate_validation_failed" : candidate!.FailureReason,
                        OutputTail = candidate?.OutputTail ?? String.Empty
                    };
                }
            }
            finally
            {
                if (_BackupProvider.OwnsTarget(backup))
                {
                    bool cleanupSucceeded = await _BackupProvider.CleanupAsync(backup, CancellationToken.None).ConfigureAwait(false);
                    if (!cleanupSucceeded)
                    {
                        result = new SelfDeployPreflightResult
                        {
                            BackupValidated = false,
                            RestoreVerified = false,
                            CandidateValidated = false,
                            FailureReason = "preflight_cleanup_failed"
                        };
                    }
                }
            }

            return result;
        }

        private static SelfDeployPreflightResult MapBackupFailure(
            SelfDeployDatabaseBackupResult backup,
            string? overrideReason = null)
        {
            return new SelfDeployPreflightResult
            {
                BackupValidated = backup.BackupValidated,
                RestoreVerified = backup.RestoreVerified,
                CandidateValidated = false,
                FailureReason = String.IsNullOrWhiteSpace(overrideReason) ? backup.FailureReason : overrideReason,
                OutputTail = backup.OutputTail
            };
        }
    }
}
