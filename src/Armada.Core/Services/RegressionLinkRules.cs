namespace Armada.Core.Services
{
    using System;
    using Armada.Core.Enums;

    /// <summary>
    /// The single validation rule for typed post-land regression links on incidents and Checks.
    /// </summary>
    public static class RegressionLinkRules
    {
        /// <summary>Maximum stored landed commit length.</summary>
        public const int MaximumCommitLength = 64;

        /// <summary>Maximum stored objective identifier length.</summary>
        public const int MaximumObjectiveIdLength = 128;

        /// <summary>
        /// Normalize and validate an objective link. Blank values clear the link.
        /// </summary>
        /// <param name="objectiveId">Candidate objective identifier.</param>
        /// <returns>The trimmed identifier or null.</returns>
        /// <exception cref="InvalidOperationException">The value is not an objective identifier.</exception>
        public static string? NormalizeObjectiveId(string? objectiveId)
        {
            if (String.IsNullOrWhiteSpace(objectiveId)) return null;
            string trimmed = objectiveId.Trim();
            if (!trimmed.StartsWith(Constants.ObjectiveIdPrefix, StringComparison.Ordinal) || trimmed.Length > MaximumObjectiveIdLength)
                throw new InvalidOperationException("regressionObjectiveId must be an objective identifier (" + Constants.ObjectiveIdPrefix + " prefix).");
            return trimmed;
        }

        /// <summary>
        /// Normalize and validate a landed commit. Blank values clear the link.
        /// </summary>
        /// <param name="commit">Candidate commit hash.</param>
        /// <returns>The lower-case commit or null.</returns>
        /// <exception cref="InvalidOperationException">The value is not a hexadecimal commit hash.</exception>
        public static string? NormalizeCommit(string? commit)
        {
            if (String.IsNullOrWhiteSpace(commit)) return null;
            string trimmed = commit.Trim().ToLowerInvariant();
            if (trimmed.Length < 7 || trimmed.Length > MaximumCommitLength)
                throw new InvalidOperationException("regressionLandedCommit must be a 7 to 64 character commit hash.");
            foreach (char c in trimmed)
            {
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
                    throw new InvalidOperationException("regressionLandedCommit must be hexadecimal.");
            }
            return trimmed;
        }

        /// <summary>
        /// Validate a Check's regression purpose and links in one call.
        /// </summary>
        /// <param name="purpose">Requested purpose.</param>
        /// <param name="objectiveId">Requested objective link.</param>
        /// <param name="commit">Requested landed commit link.</param>
        /// <returns>The purpose when the links are valid.</returns>
        public static RegressionPurposeEnum ValidatedCheckPurpose(RegressionPurposeEnum purpose, string? objectiveId, string? commit)
        {
            RequirePurposeForLinks(purpose, RegressionCauseEnum.Unclassified, NormalizeObjectiveId(objectiveId), NormalizeCommit(commit));
            return purpose;
        }

        /// <summary>
        /// Reject links on a record whose purpose is None, so a link never exists without a purpose.
        /// </summary>
        /// <param name="purpose">Record purpose.</param>
        /// <param name="cause">Record cause.</param>
        /// <param name="objectiveId">Normalized objective link.</param>
        /// <param name="commit">Normalized commit link.</param>
        /// <exception cref="InvalidOperationException">A link or cause is set without a purpose.</exception>
        public static void RequirePurposeForLinks(RegressionPurposeEnum purpose, RegressionCauseEnum cause, string? objectiveId, string? commit)
        {
            if (purpose != RegressionPurposeEnum.None) return;
            if (cause != RegressionCauseEnum.Unclassified || objectiveId != null || commit != null)
                throw new InvalidOperationException("A regression cause or link requires regressionPurpose Consumer or Ledger.");
        }
    }
}
