namespace Armada.Core.Services
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Authorization;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Harbor;
    using Armada.Core.Models;
    using Armada.Core.Settings;

    /// <summary>
    /// Computes advisory repository health for one vessel. The report lists criteria and findings.
    /// It does not block dispatch, and it does not replace readiness, preflight, exact-commit Checks,
    /// or the definition-of-done gate. Git reads run on the host that owns the checkout. A Harbor-owned
    /// vessel is read on its owner host. An offline owner returns unavailable and is not read on the
    /// Admiral host. Results are computed on demand and kept in a short memory cache. Nothing is stored
    /// in the database.
    /// </summary>
    public sealed class RepositoryHealthService
    {
        /// <summary>The repository path is not a Git repository.</summary>
        public const string RepositoryUnreachable = "repository_unreachable";

        /// <summary>The default branch does not resolve to a commit.</summary>
        public const string DefaultBranchUnresolved = "default_branch_unresolved";

        /// <summary>The working checkout is missing.</summary>
        public const string WorkingCheckoutMissing = "working_checkout_missing";

        /// <summary>The working checkout has uncommitted changes.</summary>
        public const string WorkingCheckoutDirty = "working_checkout_dirty";

        /// <summary>The working checkout is behind the remote default branch.</summary>
        public const string WorkingCheckoutBehind = "working_checkout_behind";

        /// <summary>The working tip differs from origin and the ahead and behind counts could not be read.</summary>
        public const string WorkingComparisonIncomplete = "working_comparison_incomplete";

        /// <summary>The bare repository and origin each contain commits the other does not.</summary>
        public const string BareOriginDiverged = "bare_origin_diverged";

        /// <summary>The bare tip differs from origin and the ahead and behind counts could not be read.</summary>
        public const string BareComparisonIncomplete = "bare_comparison_incomplete";

        /// <summary>No active workflow profile resolves for the vessel.</summary>
        public const string WorkflowProfileMissing = "workflow_profile_missing";

        /// <summary>A check failed inside the recent window.</summary>
        public const string RecentCheckFailed = "recent_check_failed";

        /// <summary>The owner host could not be asked.</summary>
        public const string OwnerHostUnavailable = "owner_host_unavailable";

        private readonly DatabaseDriver _Database;
        private readonly WorkflowProfileService _Profiles;
        private readonly HarborSettings? _Harbor;
        private readonly IRepositoryHealthOwnerChannel? _Owner;
        private readonly ConcurrentDictionary<string, CacheEntry> _Cache = new ConcurrentDictionary<string, CacheEntry>(StringComparer.Ordinal);

        /// <summary>How long a computed report is reused. Tests set this to zero.</summary>
        public TimeSpan CacheDuration { get; set; } = TimeSpan.FromSeconds(15);

        /// <summary>How far back a failed check counts as recent.</summary>
        public TimeSpan RecentCheckWindow { get; set; } = TimeSpan.FromDays(14);

        /// <summary>Clock for the recent-check window and the report time.</summary>
        public Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

        /// <summary>Create the evaluator.</summary>
        /// <param name="database">Database driver.</param>
        /// <param name="profiles">Workflow profile resolver.</param>
        /// <param name="harbor">Harbor routes. Null treats every vessel as local.</param>
        /// <param name="owner">Owner-host reader for a Harbor-owned vessel.</param>
        public RepositoryHealthService(
            DatabaseDriver database,
            WorkflowProfileService profiles,
            HarborSettings? harbor = null,
            IRepositoryHealthOwnerChannel? owner = null)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
            _Harbor = harbor;
            _Owner = owner;
        }

        /// <summary>
        /// Evaluate one vessel in the caller's scope.
        /// </summary>
        /// <param name="caller">Authenticated caller.</param>
        /// <param name="vesselId">Vessel identifier.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The report, or null when the vessel is absent or outside the caller's scope.</returns>
        public async Task<RepositoryHealthReport?> EvaluateAsync(AuthContext caller, string vesselId, CancellationToken token = default)
        {
            if (caller == null) throw new ArgumentNullException(nameof(caller));
            if (String.IsNullOrWhiteSpace(vesselId)) throw new ArgumentNullException(nameof(vesselId));
            Vessel? vessel = await CallerScopedRead.ReadVesselAsync(_Database, caller, vesselId, token).ConfigureAwait(false);
            if (vessel == null) return null;

            string key = (vessel.TenantId ?? String.Empty) + "\n" + vessel.Id + "\n" + (vessel.LocalPath ?? String.Empty)
                + "\n" + (vessel.WorkingDirectory ?? String.Empty) + "\n" + (vessel.DefaultBranch ?? String.Empty);
            DateTime now = Clock();
            if (CacheDuration > TimeSpan.Zero
                && _Cache.TryGetValue(key, out CacheEntry cached)
                && now - cached.At <= CacheDuration)
                return cached.Report;

            RepositoryHealthReport report = await ComputeAsync(caller, vessel, token).ConfigureAwait(false);
            if (CacheDuration > TimeSpan.Zero) _Cache[key] = new CacheEntry(now, report);
            return report;
        }

        private async Task<RepositoryHealthReport> ComputeAsync(AuthContext caller, Vessel vessel, CancellationToken token)
        {
            RepositoryHealthReport report = new RepositoryHealthReport
            {
                VesselId = vessel.Id,
                Advisory = true,
                EvaluatedUtc = Clock()
            };

            HarborMissionRoute? route = HarborMissionRouting.ResolveConfiguredForVessel(_Harbor, vessel.Id);
            RepositoryHealthGitFacts? facts;
            if (route?.CheckoutMode == HarborCheckoutModeEnum.HarborOwned)
            {
                if (_Harbor?.Enabled != true || _Owner == null)
                    return Unavailable(report);
                RepositoryHealthOwnerReply reply = await _Owner.ReadAsync(vessel, route, token).ConfigureAwait(false);
                if (reply == null || reply.Unavailable || reply.Facts == null)
                    return Unavailable(report, reply?.Reason);
                facts = reply.Facts;
            }
            else
            {
                string branch = String.IsNullOrWhiteSpace(vessel.DefaultBranch) ? "main" : vessel.DefaultBranch;
                facts = await RepositoryHealthGitProbe.ReadAsync(vessel.LocalPath, vessel.WorkingDirectory, branch, token).ConfigureAwait(false);
            }

            AddGitFindings(report, facts);
            WorkflowProfile? profile = await _Profiles.ResolveForVesselAsync(caller, vessel, null, token).ConfigureAwait(false);
            if (profile == null)
            {
                report.Findings.Add(Finding(RepositoryHealthCriterionEnum.WorkflowProfile, RepositoryHealthSeverityEnum.Warning,
                    WorkflowProfileMissing, "No active workflow profile resolves for this vessel."));
            }

            if (await HasRecentFailureAsync(vessel, token).ConfigureAwait(false))
            {
                report.Findings.Add(Finding(RepositoryHealthCriterionEnum.RecentChecks, RepositoryHealthSeverityEnum.Warning,
                    RecentCheckFailed, "A check for this vessel failed inside the recent window."));
            }

            return report;
        }

        private static void AddGitFindings(RepositoryHealthReport report, RepositoryHealthGitFacts facts)
        {
            if (!facts.RepositoryReachable)
            {
                report.Findings.Add(Finding(RepositoryHealthCriterionEnum.RepositoryReachable, RepositoryHealthSeverityEnum.Error,
                    RepositoryUnreachable, "The vessel repository is not a Git repository that can be read."));
            }
            if (facts.RepositoryReachable && !facts.DefaultBranchResolvable)
            {
                report.Findings.Add(Finding(RepositoryHealthCriterionEnum.DefaultBranch, RepositoryHealthSeverityEnum.Error,
                    DefaultBranchUnresolved, "The default branch does not resolve to a commit."));
            }
            if (!facts.WorkingCheckoutPresent)
            {
                report.Findings.Add(Finding(RepositoryHealthCriterionEnum.WorkingCheckout, RepositoryHealthSeverityEnum.Warning,
                    WorkingCheckoutMissing, "The working checkout is missing."));
            }
            else if (facts.WorkingCheckoutClean == false)
            {
                report.Findings.Add(Finding(RepositoryHealthCriterionEnum.WorkingCheckout, RepositoryHealthSeverityEnum.Warning,
                    WorkingCheckoutDirty, "The working checkout has uncommitted changes."));
            }

            if (facts.WorkingComparisonIncomplete)
            {
                report.Findings.Add(Finding(RepositoryHealthCriterionEnum.WorkingCheckout, RepositoryHealthSeverityEnum.Warning,
                    WorkingComparisonIncomplete, "The working checkout tip differs from origin and the ahead and behind counts could not be read."));
            }
            else if (facts.WorkingBehind.GetValueOrDefault() > 0)
            {
                report.Findings.Add(Finding(RepositoryHealthCriterionEnum.WorkingCheckout, RepositoryHealthSeverityEnum.Warning,
                    WorkingCheckoutBehind, "The working checkout is behind the remote default branch."));
            }

            if (facts.BareComparisonIncomplete)
            {
                report.Findings.Add(Finding(RepositoryHealthCriterionEnum.BareOrigin, RepositoryHealthSeverityEnum.Warning,
                    BareComparisonIncomplete, "The bare repository tip differs from origin and the ahead and behind counts could not be read."));
            }
            else if (facts.BarePresent && facts.BareAhead.GetValueOrDefault() > 0 && facts.BareBehind.GetValueOrDefault() > 0)
            {
                report.Findings.Add(Finding(RepositoryHealthCriterionEnum.BareOrigin, RepositoryHealthSeverityEnum.Error,
                    BareOriginDiverged, "The bare repository and origin have diverged."));
            }
        }

        private async Task<bool> HasRecentFailureAsync(Vessel vessel, CancellationToken token)
        {
            CheckRunQuery query = new CheckRunQuery
            {
                TenantId = vessel.TenantId,
                VesselId = vessel.Id,
                Status = CheckRunStatusEnum.Failed,
                FromUtc = Clock().Subtract(RecentCheckWindow),
                ToUtc = Clock().AddMinutes(1),
                PageNumber = 1,
                PageSize = 1,
                IncludeOutput = false
            };
            EnumerationResult<CheckRun> page = await _Database.CheckRuns.EnumerateAsync(query, token).ConfigureAwait(false);
            return page.TotalRecords > 0 || (page.Objects != null && page.Objects.Count > 0);
        }

        private static RepositoryHealthReport Unavailable(RepositoryHealthReport report, string? reason = null)
        {
            report.Availability = RepositoryHealthAvailabilityEnum.Unavailable;
            report.UnavailableReason = String.IsNullOrWhiteSpace(reason) ? OwnerHostUnavailable : reason;
            report.Findings = new List<RepositoryHealthFinding>();
            return report;
        }

        private static RepositoryHealthFinding Finding(RepositoryHealthCriterionEnum criterion, RepositoryHealthSeverityEnum severity, string code, string message)
        {
            return new RepositoryHealthFinding
            {
                Criterion = criterion,
                Severity = severity,
                Code = code,
                Message = message
            };
        }

        private readonly struct CacheEntry
        {
            public CacheEntry(DateTime at, RepositoryHealthReport report)
            {
                At = at;
                Report = report;
            }

            public DateTime At { get; }
            public RepositoryHealthReport Report { get; }
        }
    }
}
