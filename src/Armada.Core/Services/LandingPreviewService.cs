namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>
    /// Predicts landing behavior and blockers for vessels and missions.
    /// </summary>
    public class LandingPreviewService
    {
        private readonly DatabaseDriver _Database;
        private readonly LoggingModule _Logging;
        private readonly ArmadaSettings _Settings;

        /// <summary>
        /// Instantiate.
        /// </summary>
        public LandingPreviewService(DatabaseDriver database, LoggingModule logging)
            : this(database, logging, new ArmadaSettings())
        {
        }

        /// <summary>Instantiate with the server's current global landing settings.</summary>
        public LandingPreviewService(DatabaseDriver database, LoggingModule logging, ArmadaSettings settings)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        /// <summary>
        /// Preview landing behavior for a vessel and optional branch.
        /// </summary>
        public async Task<LandingPreviewResult> PreviewForVesselAsync(
            AuthContext auth,
            Vessel vessel,
            string? sourceBranch = null,
            CancellationToken token = default)
        {
            if (auth == null) throw new ArgumentNullException(nameof(auth));
            if (vessel == null) throw new ArgumentNullException(nameof(vessel));

            LandingConfiguration configuration = LandingConfigurationResolver.Resolve(_Settings, vessel);
            LandingPreviewResult result = new LandingPreviewResult
            {
                VesselId = vessel.Id,
                SourceBranch = NormalizeEmpty(sourceBranch),
                TargetBranch = !String.IsNullOrWhiteSpace(vessel.DefaultBranch) ? vessel.DefaultBranch : "main",
                Configuration = configuration,
                LandingMode = configuration.LandingMode,
                BranchCleanupPolicy = configuration.BranchCleanupPolicy,
                RequirePassingChecksToLand = vessel.RequirePassingChecksToLand,
                RequirePullRequestForProtectedBranches = vessel.RequirePullRequestForProtectedBranches,
                RequireMergeQueueForReleaseBranches = vessel.RequireMergeQueueForReleaseBranches
            };

            result.BranchCategory = DetermineBranchCategory(result.SourceBranch, result.TargetBranch, vessel.ReleaseBranchPrefix, vessel.HotfixBranchPrefix);
            result.ProtectedBranchMatch = DetermineProtectedBranchMatch(vessel, result.TargetBranch);
            result.TargetBranchProtected = !String.IsNullOrWhiteSpace(result.ProtectedBranchMatch);
            await PopulateCheckSummaryAsync(auth, result, token).ConfigureAwait(false);
            EvaluateCommonIssues(result);
            EvaluateBranchPolicyIssues(result);
            FinalizeResult(result);
            return result;
        }

        /// <summary>
        /// Preview landing behavior for a mission.
        /// </summary>
        public async Task<LandingPreviewResult> PreviewForMissionAsync(
            AuthContext auth,
            Vessel vessel,
            Mission mission,
            CancellationToken token = default)
        {
            if (auth == null) throw new ArgumentNullException(nameof(auth));
            if (vessel == null) throw new ArgumentNullException(nameof(vessel));
            if (mission == null) throw new ArgumentNullException(nameof(mission));

            Voyage? voyage = null;
            if (!String.IsNullOrWhiteSpace(mission.VoyageId))
            {
                voyage = auth.IsAdmin
                    ? await _Database.Voyages.ReadAsync(mission.VoyageId, token).ConfigureAwait(false)
                    : auth.IsTenantAdmin
                        ? await _Database.Voyages.ReadAsync(auth.TenantId!, mission.VoyageId, token).ConfigureAwait(false)
                        : await _Database.Voyages.ReadAsync(auth.TenantId!, auth.UserId!, mission.VoyageId, token).ConfigureAwait(false);
            }
            LandingConfiguration configuration = LandingConfigurationResolver.Resolve(_Settings, vessel, voyage);
            LandingPreviewResult result = new LandingPreviewResult
            {
                VesselId = vessel.Id,
                MissionId = mission.Id,
                SourceBranch = NormalizeEmpty(mission.BranchName),
                TargetBranch = !String.IsNullOrWhiteSpace(vessel.DefaultBranch) ? vessel.DefaultBranch : "main",
                Configuration = configuration,
                LandingMode = configuration.LandingMode,
                BranchCleanupPolicy = configuration.BranchCleanupPolicy,
                RequirePassingChecksToLand = vessel.RequirePassingChecksToLand,
                RequirePullRequestForProtectedBranches = vessel.RequirePullRequestForProtectedBranches,
                RequireMergeQueueForReleaseBranches = vessel.RequireMergeQueueForReleaseBranches
            };

            result.BranchCategory = DetermineBranchCategory(result.SourceBranch, result.TargetBranch, vessel.ReleaseBranchPrefix, vessel.HotfixBranchPrefix);
            result.ProtectedBranchMatch = DetermineProtectedBranchMatch(vessel, result.TargetBranch);
            result.TargetBranchProtected = !String.IsNullOrWhiteSpace(result.ProtectedBranchMatch);
            await PopulateCheckSummaryAsync(auth, result, token).ConfigureAwait(false);
            EvaluateCommonIssues(result);
            EvaluateBranchPolicyIssues(result);
            if (!String.IsNullOrWhiteSpace(mission.VoyageId) && voyage == null)
            {
                result.Configuration = null;
                result.LandingMode = null;
                result.ExpectedLandingAction = "Unavailable until voyage configuration can be read";
                AddIssue(result, "voyage_configuration_unavailable", ReadinessSeverityEnum.Error,
                    "Voyage configuration is unavailable", "The linked voyage cannot be read in the current scope; its override cannot be resolved.");
            }
            EvaluateMissionIssues(result, mission);
            FinalizeResult(result);
            return result;
        }

        private async Task PopulateCheckSummaryAsync(AuthContext auth, LandingPreviewResult result, CancellationToken token)
        {
            CheckRunQuery query = new CheckRunQuery
            {
                TenantId = auth.IsAdmin ? null : auth.TenantId,
                UserId = auth.IsAdmin || auth.IsTenantAdmin ? null : auth.UserId,
                VesselId = result.VesselId,
                MissionId = result.MissionId,
                PageNumber = 1,
                PageSize = 1000
            };

            EnumerationResult<CheckRun> runs = await _Database.CheckRuns.EnumerateAsync(query, token).ConfigureAwait(false);
            IEnumerable<CheckRun> filtered = runs.Objects;
            if (!String.IsNullOrWhiteSpace(result.SourceBranch))
            {
                filtered = filtered.Where(run =>
                    String.Equals(run.BranchName, result.SourceBranch, StringComparison.OrdinalIgnoreCase));
            }

            List<CheckRun> ordered = filtered
                .OrderByDescending(run => run.CompletedUtc ?? run.CreatedUtc)
                .ThenByDescending(run => run.CreatedUtc)
                .ToList();
            CheckRun? latest = ordered.FirstOrDefault();
            if (latest != null)
            {
                result.LatestCheckRunId = latest.Id;
                result.LatestCheckStatus = latest.Status;
                string safeSummary = SecretRedactor.Redact(latest.Summary ?? String.Empty);
                result.LatestCheckSummary = safeSummary.Length <= 1000 ? safeSummary : safeSummary.Substring(0, 1000) + " [truncated]";
            }

            result.HasPassingChecks = ordered.Any(run => run.Status == CheckRunStatusEnum.Passed);
        }

        private static void EvaluateCommonIssues(LandingPreviewResult result)
        {
            result.ExpectedLandingAction = DescribeLandingAction(result.LandingMode);
            if (result.Configuration is { Source: LandingConfigurationSourceEnum.Legacy } legacy)
            {
                result.ExpectedLandingAction = legacy.AutoCreatePullRequests ? "Push and open or update a pull request"
                    : legacy.AutoPush ? "Use the configured local merge path"
                    : "Manual landing only";
            }

            if (String.IsNullOrWhiteSpace(result.SourceBranch))
            {
                AddIssue(
                    result,
                    "source_branch_missing",
                    ReadinessSeverityEnum.Warning,
                    "Source branch is unknown",
                    "Armada cannot fully predict landing behavior without a source branch.");
            }

            if (!result.LandingMode.HasValue)
            {
                AddIssue(
                    result,
                    "landing_mode_inherited",
                    ReadinessSeverityEnum.Warning,
                    "Legacy landing flags apply",
                    "No explicit landing mode is selected. The resolved legacy push and pull-request flags determine the handler path.");
            }
            else if (result.LandingMode.Value == LandingModeEnum.None)
            {
                AddIssue(
                    result,
                    "manual_landing_only",
                    ReadinessSeverityEnum.Warning,
                    "Landing is manual",
                    "This vessel is configured for manual landing only. Armada will not merge or open a pull request automatically.");
            }

            if (!String.IsNullOrWhiteSpace(result.SourceBranch)
                && String.Equals(result.SourceBranch, result.TargetBranch, StringComparison.OrdinalIgnoreCase))
            {
                AddIssue(
                    result,
                    "source_matches_target",
                    ReadinessSeverityEnum.Error,
                    "Source branch matches target branch",
                    "Landing would target the same branch name that is already checked out as the source.");
            }

            if (result.RequirePassingChecksToLand && !result.HasPassingChecks)
            {
                AddIssue(
                    result,
                    "passing_checks_required",
                    ReadinessSeverityEnum.Error,
                    "Passing checks are required",
                    "This vessel requires passing checks, and this preview found no passing structured check in its scope. "
                    + "The preview is a scoped summary, not the Check gate for the landed commit.");
            }
            else if (!result.HasPassingChecks)
            {
                AddIssue(
                    result,
                    "no_passing_checks",
                    ReadinessSeverityEnum.Warning,
                    "No passing checks found",
                    "This preview found no passing structured check for the current branch or mission context.");
            }

            // An older pass does not describe newer work, so a newer non-passing run is reported. It is a warning:
            // the preview summarizes Check evidence and does not replace the Check gate.
            if (result.HasPassingChecks
                && result.LatestCheckStatus.HasValue
                && result.LatestCheckStatus.Value != CheckRunStatusEnum.Passed)
            {
                AddIssue(
                    result,
                    "latest_check_not_passed",
                    ReadinessSeverityEnum.Warning,
                    "Newest check did not pass",
                    "The newest structured check in this preview's scope is " + result.LatestCheckStatus.Value
                    + ". An older passing check does not show that the current work passed.");
            }
        }

        private static void EvaluateBranchPolicyIssues(LandingPreviewResult result)
        {
            if (result.TargetBranchProtected)
            {
                AddIssue(
                    result,
                    "protected_branch_target",
                    ReadinessSeverityEnum.Info,
                    "Target branch matches protected policy",
                    "The target branch matches protected policy '" + result.ProtectedBranchMatch + "'.");
            }

            if (result.TargetBranchProtected && result.RequirePullRequestForProtectedBranches)
            {
                if (result.LandingMode != LandingModeEnum.PullRequest && result.LandingMode != LandingModeEnum.MergeQueue)
                {
                    AddIssue(
                        result,
                        "protected_branch_requires_pr",
                        ReadinessSeverityEnum.Error,
                        "Protected branch requires PR-oriented landing",
                        "This target branch is protected and requires pull-request or merge-queue landing.");
                }
            }

            if (String.Equals(result.BranchCategory, "Release", StringComparison.OrdinalIgnoreCase)
                && result.RequireMergeQueueForReleaseBranches
                && result.LandingMode != LandingModeEnum.MergeQueue)
            {
                AddIssue(
                    result,
                    "release_branch_requires_merge_queue",
                    ReadinessSeverityEnum.Error,
                    "Release branches require merge queue",
                    "This vessel requires release branches to land through merge queue.");
            }

            if (String.Equals(result.BranchCategory, "Hotfix", StringComparison.OrdinalIgnoreCase)
                && result.TargetBranchProtected
                && result.RequirePullRequestForProtectedBranches
                && result.LandingMode == LandingModeEnum.LocalMerge)
            {
                AddIssue(
                    result,
                    "hotfix_branch_local_merge_warning",
                    ReadinessSeverityEnum.Warning,
                    "Hotfix branch is configured for direct local merge",
                    "This hotfix will land directly into a protected branch unless the landing mode is changed.");
            }
        }

        private static void EvaluateMissionIssues(LandingPreviewResult result, Mission mission)
        {
            if (mission.Status != MissionStatusEnum.WorkProduced
                && mission.Status != MissionStatusEnum.PullRequestOpen
                && mission.Status != MissionStatusEnum.LandingFailed
                && mission.Status != MissionStatusEnum.Complete)
            {
                AddIssue(
                    result,
                    "mission_not_landable",
                    ReadinessSeverityEnum.Warning,
                    "Mission is not in a landing state",
                    "This mission is currently '" + mission.Status + "' and may not yet be ready for landing.");
            }
        }

        private static void FinalizeResult(LandingPreviewResult result)
        {
            result.IsReadyToLand = !result.Issues.Any(issue => issue.Severity == ReadinessSeverityEnum.Error);
        }

        /// <summary>
        /// Classify a source branch against a target as Unknown, Default, Release, Hotfix or Feature.
        /// Shared by landing preview and operator branch writes so both apply one release rule.
        /// </summary>
        /// <param name="sourceBranch">Source branch.</param>
        /// <param name="targetBranch">Target branch.</param>
        /// <param name="releasePrefix">Vessel release prefix.</param>
        /// <param name="hotfixPrefix">Vessel hotfix prefix.</param>
        /// <returns>Branch category.</returns>
        public static string DetermineBranchCategory(string? sourceBranch, string targetBranch, string? releasePrefix, string? hotfixPrefix)
        {
            if (String.IsNullOrWhiteSpace(sourceBranch))
                return "Unknown";
            if (String.Equals(sourceBranch, targetBranch, StringComparison.OrdinalIgnoreCase))
                return "Default";
            string normalizedReleasePrefix = NormalizeBranchPrefix(releasePrefix, "release/");
            string normalizedHotfixPrefix = NormalizeBranchPrefix(hotfixPrefix, "hotfix/");
            if (sourceBranch.StartsWith(normalizedReleasePrefix, StringComparison.OrdinalIgnoreCase))
                return "Release";
            if (sourceBranch.StartsWith(normalizedHotfixPrefix, StringComparison.OrdinalIgnoreCase))
                return "Hotfix";
            return "Feature";
        }

        /// <summary>
        /// Return the protected-branch policy a target matches, or null. Shared by landing preview
        /// and operator branch writes so both apply one protection rule.
        /// </summary>
        /// <param name="vessel">Vessel with protected branch policy.</param>
        /// <param name="targetBranch">Target branch.</param>
        /// <returns>Matching pattern or default branch; null when unprotected.</returns>
        public static string? DetermineProtectedBranchMatch(Vessel vessel, string targetBranch)
        {
            List<string> patterns = vessel.ProtectedBranchPatterns ?? new List<string>();
            foreach (string pattern in patterns.Where(item => !String.IsNullOrWhiteSpace(item)))
            {
                if (MatchesBranchPattern(targetBranch, pattern))
                    return pattern;
            }

            if (vessel.RequirePullRequestForProtectedBranches
                && patterns.Count == 0
                && String.Equals(targetBranch, vessel.DefaultBranch, StringComparison.OrdinalIgnoreCase))
            {
                return vessel.DefaultBranch;
            }

            return null;
        }

        private static bool MatchesBranchPattern(string branchName, string pattern)
        {
            string trimmedPattern = pattern.Trim();
            if (String.IsNullOrWhiteSpace(trimmedPattern))
                return false;
            if (String.Equals(branchName, trimmedPattern, StringComparison.OrdinalIgnoreCase))
                return true;

            string regexPattern = "^" + System.Text.RegularExpressions.Regex.Escape(trimmedPattern)
                .Replace("\\*", ".*")
                .Replace("\\?", ".") + "$";
            return System.Text.RegularExpressions.Regex.IsMatch(branchName, regexPattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        private static string NormalizeBranchPrefix(string? value, string fallback)
        {
            return String.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        }

        private static string DescribeLandingAction(LandingModeEnum? landingMode)
        {
            if (!landingMode.HasValue)
                return "Inherited or manual landing";

            switch (landingMode.Value)
            {
                case LandingModeEnum.PullRequest:
                    return "Open or update a pull request";
                case LandingModeEnum.MergeQueue:
                    return "Enqueue the branch in merge queue";
                case LandingModeEnum.LocalMerge:
                    return "Merge the branch directly into the target branch";
                case LandingModeEnum.None:
                    return "Manual landing only";
                default:
                    return landingMode.Value.ToString();
            }
        }

        private static void AddIssue(
            LandingPreviewResult result,
            string code,
            ReadinessSeverityEnum severity,
            string title,
            string message)
        {
            result.Issues.Add(new LandingPreviewIssue
            {
                Code = code,
                Severity = severity,
                Title = title,
                Message = message
            });
        }

        private static string? NormalizeEmpty(string? value)
        {
            return String.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }
}
