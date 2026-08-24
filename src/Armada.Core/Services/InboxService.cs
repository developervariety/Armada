namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using SyslogLogging;

    /// <summary>
    /// Builds the operator's inbox: a single consolidated list of items across the fleet that require a
    /// human's attention or action, ordered most-urgent first. Two kinds of item qualify:
    ///
    /// - Awaiting your decision (human-in-the-loop): a mission in Review, or a deployment pending approval.
    /// - Failed and needs intervention (autonomous work that could not finish on its own): a failed
    ///   mission or a mission whose work could not land (while its voyage is still live), an open
    ///   incident, a failed merge, a failed or verification-failed deployment, or a stalled captain.
    ///
    /// A failed mission whose voyage has already halted is not listed on its own: it cannot be restarted
    /// in place, and the incident Armada opened for it is the record that carries the remaining action.
    ///
    /// Purely informational state changes (completions, normal progress) are deliberately excluded --
    /// the inbox answers "is there anything waiting on me / that needs my attention?", not "what happened?".
    /// </summary>
    public class InboxService
    {
        #region Private-Members

        private readonly string _Header = "[InboxService] ";
        private readonly DatabaseDriver _Database;
        private readonly LoggingModule _Logging;
        private const int _MaxPerCategory = 100;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="database">Database driver. Required.</param>
        /// <param name="logging">Logging module. Required.</param>
        public InboxService(DatabaseDriver database, LoggingModule logging)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
        }

        #endregion

        #region Public-Methods

        private const int _StalePeerMinutes = 15;

        private async Task ScanSilentClaimHoldersAsync(List<InboxItem> items, CancellationToken token)
        {
            List<CoordinationClaim> activeClaims;
            try
            {
                activeClaims = await _Database.CoordinationClaims.EnumerateActiveAsync(null, null, token).ConfigureAwait(false);
            }
            catch (NotSupportedException)
            {
                // Claims are SQLite/PostgreSQL-only today; other backends have no scan.
                return;
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "claim scan failed: " + ex.Message);
                return;
            }

            DateTime silenceCutoff = DateTime.UtcNow.AddMinutes(-_StalePeerMinutes);
            foreach (CoordinationClaim claim in activeClaims)
            {
                if (claim.ExpiresUtc <= DateTime.UtcNow) continue;

                CoordinationParticipant? presence;
                try
                {
                    presence = await _Database.CoordinationParticipants.ReadLatestByKeyAsync(claim.ParticipantKey, token).ConfigureAwait(false);
                }
                catch (NotSupportedException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _Logging.Warn(_Header + "presence lookup failed for " + claim.ParticipantKey + ": " + ex.Message);
                    continue;
                }

                bool silent = presence == null || presence.LastSeenUtc < silenceCutoff;
                if (!silent) continue;

                string lastSeen = presence == null ? "never" : presence.LastSeenUtc.ToString("u");
                items.Add(new InboxItem
                {
                    Kind = "StalePeer",
                    Severity = InboxSeverityEnum.Warning,
                    Title = "Peer session silent while holding a claim",
                    Detail = claim.DisplayName + " (" + claim.ParticipantKey + ") last seen " + lastSeen +
                        " but still holds a claim on " + claim.SubjectType.ToString().ToLowerInvariant() + " " +
                        claim.SubjectId + ", expiring " + claim.ExpiresUtc.ToString("u") +
                        ". Adopt the work, ask on the coordination board, or wait for expiry" +
                        (String.IsNullOrWhiteSpace(claim.Note) ? "." : ". Claim note: " + claim.Note),
                    EntityType = claim.SubjectType.ToString().ToLowerInvariant(),
                    EntityId = claim.SubjectId,
                    Href = "/dashboard/chatroom"
                });
            }
        }


        /// <summary>
        /// Build the inbox: actionable items ordered most-urgent first.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The list of inbox items.</returns>
        public async Task<List<InboxItem>> GetInboxAsync(CancellationToken token = default)
        {
            List<InboxItem> items = new List<InboxItem>();
            await ScanSilentClaimHoldersAsync(items, token).ConfigureAwait(false);

            try
            {
                List<Mission> reviews = await _Database.Missions.EnumerateByStatusAsync(MissionStatusEnum.Review, token).ConfigureAwait(false);
                foreach (Mission mission in reviews.Take(_MaxPerCategory))
                {
                    items.Add(new InboxItem
                    {
                        Kind = "review",
                        Severity = InboxSeverityEnum.Warning,
                        Title = "Review: " + mission.Title,
                        Detail = "Awaiting your review.",
                        EntityType = "mission",
                        EntityId = mission.Id,
                        Href = "/missions/" + mission.Id
                    });
                }

                HashSet<string> liveVoyageIds = await ReadLiveVoyageIdsAsync(token).ConfigureAwait(false);
                Dictionary<string, Mission?> missionCache = new Dictionary<string, Mission?>(StringComparer.OrdinalIgnoreCase);

                List<Mission> landingFailed = await FilterActionableAsync(
                    await _Database.Missions.EnumerateByStatusAsync(MissionStatusEnum.LandingFailed, token).ConfigureAwait(false),
                    liveVoyageIds, missionCache, token).ConfigureAwait(false);
                foreach (Mission mission in landingFailed.Take(_MaxPerCategory))
                {
                    items.Add(new InboxItem
                    {
                        Kind = "landing_failed",
                        Severity = InboxSeverityEnum.Critical,
                        Title = "Landing failed: " + mission.Title,
                        Detail = String.IsNullOrWhiteSpace(mission.FailureReason) ? "The work could not be landed." : mission.FailureReason!,
                        EntityType = "mission",
                        EntityId = mission.Id,
                        Href = "/missions/" + mission.Id
                    });
                }

                List<Mission> failed = await FilterActionableAsync(
                    await _Database.Missions.EnumerateByStatusAsync(MissionStatusEnum.Failed, token).ConfigureAwait(false),
                    liveVoyageIds, missionCache, token).ConfigureAwait(false);
                foreach (Mission mission in failed.Take(_MaxPerCategory))
                {
                    items.Add(new InboxItem
                    {
                        Kind = "failed",
                        Severity = InboxSeverityEnum.Warning,
                        Title = "Failed: " + mission.Title,
                        Detail = String.IsNullOrWhiteSpace(mission.FailureReason) ? "The mission failed." : mission.FailureReason!,
                        EntityType = "mission",
                        EntityId = mission.Id,
                        Href = "/missions/" + mission.Id
                    });
                }

                List<Captain> stalled = await _Database.Captains.EnumerateByStateAsync(CaptainStateEnum.Stalled, token).ConfigureAwait(false);
                foreach (Captain captain in stalled.Take(_MaxPerCategory))
                {
                    items.Add(new InboxItem
                    {
                        Kind = "stalled_captain",
                        Severity = InboxSeverityEnum.Warning,
                        Title = "Stalled captain: " + captain.Name,
                        Detail = "This captain is stalled and may need recovery or a dock reclaim.",
                        EntityType = "captain",
                        EntityId = captain.Id,
                        Href = "/captains/" + captain.Id
                    });
                }

                List<MergeEntry> mergeFailed = await _Database.MergeEntries.EnumerateByStatusAsync(MergeStatusEnum.Failed, token).ConfigureAwait(false);
                foreach (MergeEntry entry in mergeFailed.Take(_MaxPerCategory))
                {
                    items.Add(new InboxItem
                    {
                        Kind = "merge_failed",
                        Severity = InboxSeverityEnum.Critical,
                        Title = "Merge failed: " + entry.TargetBranch,
                        Detail = "A queued merge failed testing or landing and needs attention.",
                        EntityType = "merge_entry",
                        EntityId = entry.Id,
                        Href = "/merge-queue/" + entry.Id
                    });
                }

                IncidentService incidentService = new IncidentService(_Database);
                AuthContext incidentAuth = AuthContext.Authenticated(Constants.DefaultTenantId, Constants.DefaultUserId, true, true, "inbox");
                EnumerationResult<Incident> incidentPage = await incidentService.EnumerateAsync(
                    incidentAuth, new IncidentQuery { PageNumber = 1, PageSize = 500 }, token).ConfigureAwait(false);
                foreach (Incident incident in incidentPage.Objects
                    .Where(i => i.Status == IncidentStatusEnum.Open || i.Status == IncidentStatusEnum.Monitoring)
                    .Take(_MaxPerCategory))
                {
                    string detail = !String.IsNullOrWhiteSpace(incident.RecoveryNotes) ? incident.RecoveryNotes!
                        : !String.IsNullOrWhiteSpace(incident.Summary) ? incident.Summary!
                        : "The incident is open and needs a verdict.";
                    if (detail.Length > 400) detail = detail.Substring(0, 400) + "...";

                    items.Add(new InboxItem
                    {
                        Kind = "open_incident",
                        Severity = incident.Severity == IncidentSeverityEnum.Critical || incident.Severity == IncidentSeverityEnum.High
                            ? InboxSeverityEnum.Critical
                            : InboxSeverityEnum.Warning,
                        Title = "Incident " + incident.Status + ": " + incident.Title,
                        Detail = detail,
                        EntityType = "incident",
                        EntityId = incident.Id,
                        Href = "/incidents/" + incident.Id
                    });
                }

                List<Deployment> deployments = await _Database.Deployments.EnumerateAllAsync(new DeploymentQuery(), token).ConfigureAwait(false);

                foreach (Deployment deployment in deployments.Where(d => d.Status == DeploymentStatusEnum.PendingApproval).Take(_MaxPerCategory))
                {
                    items.Add(new InboxItem
                    {
                        Kind = "deployment_approval",
                        Severity = InboxSeverityEnum.Warning,
                        Title = "Deployment awaiting approval: " + (String.IsNullOrWhiteSpace(deployment.EnvironmentName) ? deployment.Id : deployment.EnvironmentName!),
                        Detail = "A deployment is waiting for your approval before it runs.",
                        EntityType = "deployment",
                        EntityId = deployment.Id,
                        Href = "/deployments/" + deployment.Id
                    });
                }

                foreach (Deployment deployment in deployments.Where(d => d.Status == DeploymentStatusEnum.Failed || d.Status == DeploymentStatusEnum.VerificationFailed).Take(_MaxPerCategory))
                {
                    items.Add(new InboxItem
                    {
                        Kind = "deployment_failed",
                        Severity = InboxSeverityEnum.Critical,
                        Title = "Deployment failed: " + (String.IsNullOrWhiteSpace(deployment.EnvironmentName) ? deployment.Id : deployment.EnvironmentName!),
                        Detail = deployment.Status == DeploymentStatusEnum.VerificationFailed
                            ? "Deployment verification failed and needs attention."
                            : "A deployment failed and needs attention.",
                        EntityType = "deployment",
                        EntityId = deployment.Id,
                        Href = "/deployments/" + deployment.Id
                    });
                }
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "error building inbox: " + ex.Message);
            }

            return items
                .OrderByDescending(item => (int)item.Severity)
                .ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

                #endregion

        #region Private-Methods

        /// <summary>
        /// A failed or landing-failed mission is actionable only while the voyage it belongs to is still
        /// live. Once the voyage is terminal the mission cannot be restarted in place; the record that
        /// carries any remaining action is the incident Armada opened for it, and the inbox lists open
        /// incidents on their own. A rescue mission carries no voyage of its own, so it follows the voyage
        /// of the mission it rescues. A mission with no voyage anywhere in its chain stays visible, because
        /// nothing proves it was handled.
        /// </summary>
        private async Task<List<Mission>> FilterActionableAsync(
            List<Mission> missions,
            HashSet<string> liveVoyageIds,
            Dictionary<string, Mission?> missionCache,
            CancellationToken token)
        {
            List<Mission> actionable = new List<Mission>();
            foreach (Mission mission in missions)
            {
                string? voyageId = await ResolveEffectiveVoyageIdAsync(mission, missionCache, token).ConfigureAwait(false);
                if (String.IsNullOrWhiteSpace(voyageId) || liveVoyageIds.Contains(voyageId))
                    actionable.Add(mission);
            }
            return actionable;
        }

        private async Task<string?> ResolveEffectiveVoyageIdAsync(
            Mission mission,
            Dictionary<string, Mission?> missionCache,
            CancellationToken token)
        {
            Mission? current = mission;
            HashSet<string> visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (current != null && visited.Add(current.Id))
            {
                if (!String.IsNullOrWhiteSpace(current.VoyageId)) return current.VoyageId;
                if (String.IsNullOrWhiteSpace(current.ParentMissionId)) return null;

                if (!missionCache.TryGetValue(current.ParentMissionId, out Mission? parent))
                {
                    parent = await _Database.Missions.ReadAsync(current.ParentMissionId, token).ConfigureAwait(false);
                    missionCache[current.ParentMissionId] = parent;
                }
                current = parent;
            }
            return null;
        }

        private async Task<HashSet<string>> ReadLiveVoyageIdsAsync(CancellationToken token)
        {
            HashSet<string> ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (VoyageStatusEnum status in new[] { VoyageStatusEnum.Open, VoyageStatusEnum.InProgress })
            {
                List<Voyage> voyages = await _Database.Voyages.EnumerateByStatusAsync(status, token).ConfigureAwait(false);
                foreach (Voyage voyage in voyages) ids.Add(voyage.Id);
            }
            return ids;
        }

        #endregion
    }
}
