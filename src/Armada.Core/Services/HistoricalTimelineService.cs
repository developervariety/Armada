namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using SyslogLogging;

        /// <summary>
        /// Builds a unified cross-entity historical timeline from Armada's existing records.
        /// The sources that grow without bound (missions, check runs, deployments, events and request
        /// history) are read newest first, at most <see cref="SourceRowLimit"/> records each, and missions
        /// are read without their description, diff snapshot and agent output, so one timeline read holds
        /// a bounded number of light records however long the server has run.
        /// </summary>
        public class HistoricalTimelineService
        {
        /// <summary>
        /// Default number of records read from each bounded source.
        /// </summary>
        public const int DefaultSourceRowLimit = 2000;

        /// <summary>
        /// Largest accepted per-source record limit.
        /// </summary>
        public const int MaximumSourceRowLimit = 100000;

        /// <summary>
        /// Number of records read, newest first, from each bounded source. A source holding more names
        /// itself in <see cref="HistoricalTimelineResult.TruncatedSources"/>.
        /// </summary>
        public int SourceRowLimit => _SourceRowLimit;

        private const int _SourcePageSize = 500;
        private readonly string _Header = "[HistoricalTimelineService] ";
        private readonly DatabaseDriver _Database;
        private readonly LoggingModule? _Logging;
        private readonly int _SourceRowLimit;
        private static readonly JsonSerializerOptions _EventPayloadJsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="logging">Logging module; a source the provider does not store is logged here.</param>
        /// <param name="sourceRowLimit">Records read, newest first, from each bounded source; clamped to 1-<see cref="MaximumSourceRowLimit"/>.</param>
        public HistoricalTimelineService(DatabaseDriver database, LoggingModule? logging = null, int sourceRowLimit = DefaultSourceRowLimit)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Logging = logging;
            _SourceRowLimit = Math.Clamp(sourceRowLimit, 1, MaximumSourceRowLimit);
        }

        /// <summary>
        /// Enumerate timeline entries across missions, voyages, planning sessions, merge entries, checks, releases, deployments, events, and requests.
        /// A source the database provider does not store is omitted and named in
        /// <see cref="HistoricalTimelineResult.UnavailableSources"/>.
        /// </summary>
        public async Task<HistoricalTimelineResult> EnumerateAsync(
            AuthContext auth,
            HistoricalTimelineQuery query,
            CancellationToken token = default)
        {
            if (auth == null) throw new ArgumentNullException(nameof(auth));
            if (query == null) throw new ArgumentNullException(nameof(query));

            Stopwatch stopwatch = Stopwatch.StartNew();

            Objective? objective = null;
            if (!String.IsNullOrWhiteSpace(query.ObjectiveId))
                objective = await ReadObjectiveAsync(auth, query.ObjectiveId, token).ConfigureAwait(false);

            List<HistoricalTimelineEntry> entries = new List<HistoricalTimelineEntry>();
            List<string> unavailable = new List<string>();
            List<string> truncated = new List<string>();
            entries.AddRange(await CollectAsync("Objective", () => BuildObjectiveEntriesAsync(auth, token), unavailable).ConfigureAwait(false));
            entries.AddRange(await CollectAsync("ObjectiveRefinementSession", () => BuildObjectiveRefinementEntriesAsync(auth, token), unavailable).ConfigureAwait(false));
            entries.AddRange(await CollectAsync("Mission", () => BuildMissionEntriesAsync(auth, query, objective, truncated, token), unavailable).ConfigureAwait(false));
            entries.AddRange(await CollectAsync("Voyage", () => BuildVoyageEntriesAsync(auth, token), unavailable).ConfigureAwait(false));
            entries.AddRange(await CollectAsync("Planning", () => BuildPlanningEntriesAsync(auth, token), unavailable).ConfigureAwait(false));
            entries.AddRange(await CollectAsync("MergeEntry", () => BuildMergeEntriesAsync(auth, token), unavailable).ConfigureAwait(false));
            entries.AddRange(await CollectAsync("CheckRun", () => BuildCheckRunEntriesAsync(auth, query, truncated, token), unavailable).ConfigureAwait(false));
            entries.AddRange(await CollectAsync("Release", () => BuildReleaseEntriesAsync(auth, token), unavailable).ConfigureAwait(false));
            entries.AddRange(await CollectAsync("Deployment", () => BuildDeploymentEntriesAsync(auth, query, truncated, token), unavailable).ConfigureAwait(false));
            entries.AddRange(await CollectAsync("Incident", () => BuildIncidentEntriesAsync(auth, token), unavailable).ConfigureAwait(false));
            entries.AddRange(await CollectAsync("RunbookExecution", () => BuildRunbookExecutionEntriesAsync(auth, token), unavailable).ConfigureAwait(false));
            entries.AddRange(await CollectAsync("Event", () => BuildEventEntriesAsync(auth, query, truncated, token), unavailable).ConfigureAwait(false));
            entries.AddRange(await CollectAsync("Request", () => BuildRequestEntriesAsync(auth, query, truncated, token), unavailable).ConfigureAwait(false));

            IEnumerable<HistoricalTimelineEntry> filtered = entries;

            if (!String.IsNullOrWhiteSpace(query.ObjectiveId))
            {
                if (objective == null)
                    filtered = Enumerable.Empty<HistoricalTimelineEntry>();
                else
                    filtered = filtered.Where(entry => MatchesObjective(entry, objective));
            }

            if (!String.IsNullOrWhiteSpace(query.VesselId))
                filtered = filtered.Where(entry => String.Equals(entry.VesselId, query.VesselId, StringComparison.OrdinalIgnoreCase));
            if (!String.IsNullOrWhiteSpace(query.EnvironmentId))
                filtered = filtered.Where(entry => String.Equals(entry.EnvironmentId, query.EnvironmentId, StringComparison.OrdinalIgnoreCase));
            if (!String.IsNullOrWhiteSpace(query.DeploymentId))
                filtered = filtered.Where(entry => String.Equals(entry.DeploymentId, query.DeploymentId, StringComparison.OrdinalIgnoreCase));
            if (!String.IsNullOrWhiteSpace(query.IncidentId))
                filtered = filtered.Where(entry => String.Equals(entry.IncidentId, query.IncidentId, StringComparison.OrdinalIgnoreCase));
            if (query.PostmortemOnly)
            {
                List<Incident> postmortemIncidents = (await ReadIncidentsAsync(auth, token).ConfigureAwait(false))
                    .Where(item => !String.IsNullOrWhiteSpace(item.Postmortem))
                    .ToList();

                if (postmortemIncidents.Count == 0)
                {
                    filtered = Enumerable.Empty<HistoricalTimelineEntry>();
                }
                else
                {
                    HashSet<string> incidentIds = new HashSet<string>(
                        postmortemIncidents.Select(item => item.Id),
                        StringComparer.OrdinalIgnoreCase);
                    HashSet<string> deploymentIds = new HashSet<string>(
                        postmortemIncidents
                            .Where(item => !String.IsNullOrWhiteSpace(item.DeploymentId))
                            .Select(item => item.DeploymentId!),
                        StringComparer.OrdinalIgnoreCase);
                    HashSet<string> releaseIds = new HashSet<string>(
                        postmortemIncidents
                            .Where(item => !String.IsNullOrWhiteSpace(item.ReleaseId))
                            .Select(item => item.ReleaseId!),
                        StringComparer.OrdinalIgnoreCase);
                    HashSet<string> missionIds = new HashSet<string>(
                        postmortemIncidents
                            .Where(item => !String.IsNullOrWhiteSpace(item.MissionId))
                            .Select(item => item.MissionId!),
                        StringComparer.OrdinalIgnoreCase);
                    HashSet<string> voyageIds = new HashSet<string>(
                        postmortemIncidents
                            .Where(item => !String.IsNullOrWhiteSpace(item.VoyageId))
                            .Select(item => item.VoyageId!),
                        StringComparer.OrdinalIgnoreCase);
                    filtered = filtered.Where(entry => MatchesPostmortemContext(
                        entry,
                        incidentIds,
                        deploymentIds,
                        releaseIds,
                        missionIds,
                        voyageIds));
                }
            }
            if (!String.IsNullOrWhiteSpace(query.MissionId))
                filtered = filtered.Where(entry => String.Equals(entry.MissionId, query.MissionId, StringComparison.OrdinalIgnoreCase));
            if (!String.IsNullOrWhiteSpace(query.VoyageId))
                filtered = filtered.Where(entry => String.Equals(entry.VoyageId, query.VoyageId, StringComparison.OrdinalIgnoreCase));
            if (query.FromUtc.HasValue)
                filtered = filtered.Where(entry => entry.OccurredUtc >= query.FromUtc.Value.ToUniversalTime());
            if (query.ToUtc.HasValue)
                filtered = filtered.Where(entry => entry.OccurredUtc <= query.ToUtc.Value.ToUniversalTime());

            if (!String.IsNullOrWhiteSpace(query.Actor))
            {
                filtered = filtered.Where(entry =>
                    ContainsIgnoreCase(entry.ActorId, query.Actor)
                    || ContainsIgnoreCase(entry.ActorDisplay, query.Actor));
            }

            if (!String.IsNullOrWhiteSpace(query.Text))
            {
                filtered = filtered.Where(entry =>
                    ContainsIgnoreCase(entry.Title, query.Text)
                    || ContainsIgnoreCase(entry.Description, query.Text)
                    || ContainsIgnoreCase(entry.Status, query.Text)
                    || ContainsIgnoreCase(entry.Route, query.Text)
                    || ContainsIgnoreCase(entry.MetadataJson, query.Text));
            }

            if (query.SourceTypes != null && query.SourceTypes.Count > 0)
            {
                HashSet<string> allowed = new HashSet<string>(
                    query.SourceTypes.Where(item => !String.IsNullOrWhiteSpace(item)),
                    StringComparer.OrdinalIgnoreCase);
                filtered = filtered.Where(entry => allowed.Contains(entry.SourceType));
            }

            List<HistoricalTimelineEntry> ordered = (query.Order == EnumerationOrderEnum.CreatedAscending
                    ? filtered.OrderBy(entry => entry.OccurredUtc).ThenBy(entry => entry.SourceId, StringComparer.Ordinal)
                    : filtered.OrderByDescending(entry => entry.OccurredUtc).ThenByDescending(entry => entry.SourceId, StringComparer.Ordinal))
                .ToList();

            int pageSize = query.PageSize < 1 ? 50 : query.PageSize;
            int offset = query.Offset < 0 ? 0 : query.Offset;
            List<HistoricalTimelineEntry> page = ordered.Skip(offset).Take(pageSize).ToList();

            stopwatch.Stop();

            return new HistoricalTimelineResult
            {
                PageNumber = query.PageNumber,
                PageSize = pageSize,
                TotalRecords = ordered.Count,
                TotalPages = pageSize > 0 ? (int)Math.Ceiling((double)ordered.Count / pageSize) : 0,
                Objects = page,
                TotalMs = Math.Round(stopwatch.Elapsed.TotalMilliseconds, 2),
                UnavailableSources = unavailable,
                TruncatedSources = truncated
            };
        }

        /// <summary>
        /// Build one source's entries. A source whose records the configured database provider does not
        /// store is left out of the timeline, named in the result, and logged; any other failure propagates.
        /// </summary>
        private async Task<List<HistoricalTimelineEntry>> CollectAsync(
            string sourceType,
            Func<Task<List<HistoricalTimelineEntry>>> build,
            List<string> unavailable)
        {
            try
            {
                return await build().ConfigureAwait(false);
            }
            catch (NotSupportedException ex)
            {
                unavailable.Add(sourceType);
                _Logging?.Info(_Header + sourceType + " entries are not in the timeline because this database provider does not store them: " + ex.Message);
                return new List<HistoricalTimelineEntry>();
            }
        }

        private async Task<List<HistoricalTimelineEntry>> BuildObjectiveEntriesAsync(AuthContext auth, CancellationToken token)
        {
            List<Objective> objectives = await ReadObjectivesAsync(auth, token).ConfigureAwait(false);
            return objectives.Select(objective => new HistoricalTimelineEntry
            {
                Id = "timeline-objective-" + objective.Id,
                SourceType = "Objective",
                SourceId = objective.Id,
                EntityType = "objective",
                EntityId = objective.Id,
                ObjectiveId = objective.Id,
                VesselId = objective.VesselIds.Count > 0 ? objective.VesselIds[0] : null,
                ActorId = objective.UserId,
                ActorDisplay = objective.Owner ?? objective.UserId,
                Title = objective.Title,
                Description = objective.Description,
                Status = objective.Status.ToString(),
                Severity = ObjectiveSeverity(objective),
                Route = "/backlog/" + objective.Id,
                OccurredUtc = objective.CompletedUtc ?? objective.LastUpdateUtc,
                MetadataJson = JsonSerializer.Serialize(new
                {
                    objective.Owner,
                    objective.Tags,
                    fleetCount = objective.FleetIds.Count,
                    vesselCount = objective.VesselIds.Count,
                    planningCount = objective.PlanningSessionIds.Count,
                    voyageCount = objective.VoyageIds.Count,
                    missionCount = objective.MissionIds.Count,
                    checkRunCount = objective.CheckRunIds.Count,
                    releaseCount = objective.ReleaseIds.Count,
                    deploymentCount = objective.DeploymentIds.Count,
                    incidentCount = objective.IncidentIds.Count
                })
            }).ToList();
        }

        private async Task<List<HistoricalTimelineEntry>> BuildObjectiveRefinementEntriesAsync(AuthContext auth, CancellationToken token)
        {
            List<ObjectiveRefinementSession> sessions = await ReadObjectiveRefinementSessionsAsync(auth, token).ConfigureAwait(false);
            return sessions.Select(session => new HistoricalTimelineEntry
            {
                Id = "timeline-objective-refinement-" + session.Id,
                SourceType = "ObjectiveRefinementSession",
                SourceId = session.Id,
                EntityType = "objective-refinement-session",
                EntityId = session.Id,
                ObjectiveId = session.ObjectiveId,
                VesselId = session.VesselId,
                ActorId = session.CaptainId,
                ActorDisplay = session.CaptainId,
                Title = session.Title,
                Description = !String.IsNullOrWhiteSpace(session.FailureReason)
                    ? session.FailureReason
                    : "Backlog refinement session",
                Status = session.Status.ToString(),
                Severity = session.Status == ObjectiveRefinementSessionStatusEnum.Failed ? "error"
                    : session.Status == ObjectiveRefinementSessionStatusEnum.Stopped ? "warning"
                    : session.Status == ObjectiveRefinementSessionStatusEnum.Completed ? "success"
                    : "info",
                Route = "/backlog/" + session.ObjectiveId,
                OccurredUtc = session.CompletedUtc ?? session.LastUpdateUtc,
                MetadataJson = JsonSerializer.Serialize(new
                {
                    session.ObjectiveId,
                    session.CaptainId,
                    session.FleetId,
                    session.VesselId,
                    session.ProcessId,
                    session.FailureReason,
                    session.StartedUtc,
                    session.CompletedUtc
                })
            }).ToList();
        }

        private async Task<List<HistoricalTimelineEntry>> BuildMissionEntriesAsync(
            AuthContext auth,
            HistoricalTimelineQuery timelineQuery,
            Objective? objective,
            List<string> truncated,
            CancellationToken token)
        {
            List<Mission> missions;
            if (objective != null)
                missions = await ReadMissionsByIdAsync(auth, objective.MissionIds, truncated, token).ConfigureAwait(false);
            else if (!String.IsNullOrWhiteSpace(timelineQuery.MissionId))
                missions = await ReadMissionsByIdAsync(auth, new List<string> { timelineQuery.MissionId }, truncated, token).ConfigureAwait(false);
            else
                missions = await ReadMissionsAsync(auth, timelineQuery, truncated, token).ConfigureAwait(false);
            // Summaries carry no description; the failure reason is the mission's own short account.
            return missions.Select(mission => new HistoricalTimelineEntry
            {
                Id = "timeline-mission-" + mission.Id,
                SourceType = "Mission",
                SourceId = mission.Id,
                EntityType = "mission",
                EntityId = mission.Id,
                VesselId = mission.VesselId,
                MissionId = mission.Id,
                VoyageId = mission.VoyageId,
                ActorId = mission.CaptainId,
                ActorDisplay = mission.Persona ?? mission.CaptainId,
                Title = mission.Title,
                Description = mission.FailureReason,
                Status = mission.Status.ToString(),
                Severity = MissionSeverity(mission),
                Route = "/missions/" + mission.Id,
                OccurredUtc = mission.LastUpdateUtc,
                MetadataJson = JsonSerializer.Serialize(new
                {
                    mission.Priority,
                    mission.BranchName,
                    mission.CommitHash,
                    mission.FailureReason,
                    mission.RequiresReview,
                    mission.ReviewDenyAction
                })
            }).ToList();
        }

        private async Task<List<HistoricalTimelineEntry>> BuildVoyageEntriesAsync(AuthContext auth, CancellationToken token)
        {
            List<Voyage> voyages = await ReadVoyagesAsync(auth, token).ConfigureAwait(false);
            return voyages.Select(voyage => new HistoricalTimelineEntry
            {
                Id = "timeline-voyage-" + voyage.Id,
                SourceType = "Voyage",
                SourceId = voyage.Id,
                EntityType = "voyage",
                EntityId = voyage.Id,
                VoyageId = voyage.Id,
                Title = voyage.Title,
                Description = voyage.Description,
                Status = voyage.Status.ToString(),
                Severity = VoyageSeverity(voyage),
                Route = "/voyages/" + voyage.Id,
                OccurredUtc = voyage.LastUpdateUtc,
                MetadataJson = JsonSerializer.Serialize(new
                {
                    voyage.AutoPush,
                    voyage.AutoCreatePullRequests,
                    voyage.AutoMergePullRequests,
                    voyage.LandingMode,
                    voyage.SourcePlanningSessionId
                })
            }).ToList();
        }

        private async Task<List<HistoricalTimelineEntry>> BuildPlanningEntriesAsync(AuthContext auth, CancellationToken token)
        {
            List<PlanningSession> sessions = await ReadPlanningSessionsAsync(auth, token).ConfigureAwait(false);
            return sessions.Select(session => new HistoricalTimelineEntry
            {
                Id = "timeline-planning-" + session.Id,
                SourceType = "Planning",
                SourceId = session.Id,
                EntityType = "planning-session",
                EntityId = session.Id,
                VesselId = session.VesselId,
                ActorId = session.CaptainId,
                Title = session.Title,
                Description = "Planning session",
                Status = session.Status.ToString(),
                Severity = session.Status == PlanningSessionStatusEnum.Failed ? "error" : "info",
                Route = "/planning/" + session.Id,
                OccurredUtc = session.LastUpdateUtc,
                MetadataJson = JsonSerializer.Serialize(new
                {
                    session.FleetId,
                    session.BranchName,
                    session.PipelineId,
                    session.FailureReason
                })
            }).ToList();
        }

        private async Task<List<HistoricalTimelineEntry>> BuildMergeEntriesAsync(AuthContext auth, CancellationToken token)
        {
            List<MergeEntry> entries = await ReadMergeEntriesAsync(auth, token).ConfigureAwait(false);
            return entries.Select(entry => new HistoricalTimelineEntry
            {
                Id = "timeline-merge-" + entry.Id,
                SourceType = "MergeEntry",
                SourceId = entry.Id,
                EntityType = "merge-entry",
                EntityId = entry.Id,
                VesselId = entry.VesselId,
                MissionId = entry.MissionId,
                Title = entry.BranchName + " -> " + entry.TargetBranch,
                Description = "Merge queue entry",
                Status = entry.Status.ToString(),
                Severity = entry.Status == MergeStatusEnum.Failed ? "error" : entry.Status == MergeStatusEnum.Cancelled ? "warning" : "info",
                Route = "/merge-queue/" + entry.Id,
                OccurredUtc = entry.LastUpdateUtc,
                MetadataJson = JsonSerializer.Serialize(new
                {
                    entry.Priority,
                    entry.BatchId,
                    entry.TestCommand,
                    entry.TestExitCode
                })
            }).ToList();
        }

        private async Task<List<HistoricalTimelineEntry>> BuildCheckRunEntriesAsync(AuthContext auth, HistoricalTimelineQuery timelineQuery, List<string> truncated, CancellationToken token)
        {
            List<CheckRun> runs = await ReadCheckRunsAsync(auth, timelineQuery, truncated, token).ConfigureAwait(false);
            return runs.Select(run => new HistoricalTimelineEntry
            {
                Id = "timeline-check-" + run.Id,
                SourceType = "CheckRun",
                SourceId = run.Id,
                EntityType = "check-run",
                EntityId = run.Id,
                DeploymentId = run.DeploymentId,
                VesselId = run.VesselId,
                MissionId = run.MissionId,
                VoyageId = run.VoyageId,
                ActorId = run.UserId,
                ActorDisplay = run.Source == CheckRunSourceEnum.External ? run.ProviderName ?? "External" : "Armada",
                Title = run.Label ?? run.Type.ToString(),
                Description = run.Summary,
                Status = run.Status.ToString(),
                Severity = run.Status == CheckRunStatusEnum.Failed ? "error" : run.Status == CheckRunStatusEnum.Canceled ? "warning" : "info",
                Route = "/checks/" + run.Id,
                OccurredUtc = run.CompletedUtc ?? run.LastUpdateUtc,
                MetadataJson = JsonSerializer.Serialize(new
                {
                    run.Type,
                    run.Source,
                    run.ProviderName,
                    run.EnvironmentName,
                    run.BranchName,
                    run.CommitHash,
                    run.DurationMs,
                    run.ExitCode
                })
            }).ToList();
        }

        private async Task<List<HistoricalTimelineEntry>> BuildReleaseEntriesAsync(AuthContext auth, CancellationToken token)
        {
            List<Release> releases = await ReadReleasesAsync(auth, token).ConfigureAwait(false);
            return releases.Select(release => new HistoricalTimelineEntry
            {
                Id = "timeline-release-" + release.Id,
                SourceType = "Release",
                SourceId = release.Id,
                EntityType = "release",
                EntityId = release.Id,
                VesselId = release.VesselId,
                ActorId = release.UserId,
                ActorDisplay = release.UserId,
                Title = !String.IsNullOrWhiteSpace(release.Version)
                    ? release.Title + " (" + release.Version + ")"
                    : release.Title,
                Description = release.Summary,
                Status = release.Status.ToString(),
                Severity = release.Status == ReleaseStatusEnum.Failed ? "error"
                    : release.Status == ReleaseStatusEnum.RolledBack ? "warning"
                    : "info",
                Route = "/releases/" + release.Id,
                OccurredUtc = release.PublishedUtc ?? release.LastUpdateUtc,
                MetadataJson = JsonSerializer.Serialize(new
                {
                    release.Version,
                    release.TagName,
                    release.WorkflowProfileId,
                    voyageCount = release.VoyageIds.Count,
                    missionCount = release.MissionIds.Count,
                    checkRunCount = release.CheckRunIds.Count,
                    artifactCount = release.Artifacts.Count
                })
            }).ToList();
        }

        private async Task<List<HistoricalTimelineEntry>> BuildDeploymentEntriesAsync(AuthContext auth, HistoricalTimelineQuery timelineQuery, List<string> truncated, CancellationToken token)
        {
            List<Deployment> deployments = await ReadDeploymentsAsync(auth, timelineQuery, truncated, token).ConfigureAwait(false);
            return deployments.Select(deployment => new HistoricalTimelineEntry
            {
                Id = "timeline-deployment-" + deployment.Id,
                SourceType = "Deployment",
                SourceId = deployment.Id,
                EntityType = "deployment",
                EntityId = deployment.Id,
                VesselId = deployment.VesselId,
                EnvironmentId = deployment.EnvironmentId,
                DeploymentId = deployment.Id,
                MissionId = deployment.MissionId,
                VoyageId = deployment.VoyageId,
                ActorId = deployment.UserId,
                ActorDisplay = deployment.UserId,
                Title = deployment.Title,
                Description = deployment.Summary,
                Status = deployment.Status.ToString(),
                Severity = DeploymentSeverity(deployment),
                Route = "/deployments/" + deployment.Id,
                OccurredUtc = deployment.CompletedUtc ?? deployment.StartedUtc ?? deployment.LastUpdateUtc,
                MetadataJson = JsonSerializer.Serialize(new
                {
                    deployment.EnvironmentId,
                    deployment.EnvironmentName,
                    deployment.ReleaseId,
                    deployment.SourceRef,
                    deployment.VerificationStatus,
                    deployment.ApprovalRequired,
                    deployment.ApprovedByUserId,
                    checkRunCount = deployment.CheckRunIds.Count
                })
            }).ToList();
        }

        private async Task<List<HistoricalTimelineEntry>> BuildIncidentEntriesAsync(AuthContext auth, CancellationToken token)
        {
            List<Incident> incidents = await ReadIncidentsAsync(auth, token).ConfigureAwait(false);
            return incidents.Select(incident => new HistoricalTimelineEntry
            {
                Id = "timeline-incident-" + incident.Id,
                SourceType = "Incident",
                SourceId = incident.Id,
                EntityType = "incident",
                EntityId = incident.Id,
                VesselId = incident.VesselId,
                EnvironmentId = incident.EnvironmentId,
                DeploymentId = incident.DeploymentId,
                IncidentId = incident.Id,
                MissionId = incident.MissionId,
                VoyageId = incident.VoyageId,
                ActorId = incident.UserId,
                ActorDisplay = incident.UserId,
                Title = incident.Title,
                Description = incident.Summary,
                Status = incident.Status.ToString(),
                Severity = IncidentSeverity(incident),
                Route = "/incidents/" + incident.Id,
                OccurredUtc = incident.LastUpdateUtc,
                MetadataJson = JsonSerializer.Serialize(new
                {
                    incident.EnvironmentId,
                    incident.EnvironmentName,
                    incident.DeploymentId,
                    incident.ReleaseId,
                    incident.RollbackDeploymentId,
                    incident.Severity,
                    incident.Impact,
                    HasPostmortem = !String.IsNullOrWhiteSpace(incident.Postmortem),
                    incident.DetectedUtc,
                    incident.MitigatedUtc,
                    incident.ClosedUtc
                })
            }).ToList();
        }

        private async Task<List<HistoricalTimelineEntry>> BuildRunbookExecutionEntriesAsync(AuthContext auth, CancellationToken token)
        {
            List<RunbookExecution> executions = await ReadRunbookExecutionsAsync(auth, token).ConfigureAwait(false);
            return executions.Select(execution => new HistoricalTimelineEntry
            {
                Id = "timeline-runbook-execution-" + execution.Id,
                SourceType = "RunbookExecution",
                SourceId = execution.Id,
                EntityType = "runbook-execution",
                EntityId = execution.Id,
                EnvironmentId = execution.EnvironmentId,
                DeploymentId = execution.DeploymentId,
                IncidentId = execution.IncidentId,
                ActorId = execution.UserId,
                ActorDisplay = execution.UserId,
                Title = execution.Title,
                Description = execution.Notes,
                Status = execution.Status.ToString(),
                Severity = RunbookExecutionSeverity(execution),
                Route = "/runbooks/" + execution.RunbookId,
                OccurredUtc = execution.CompletedUtc ?? execution.LastUpdateUtc,
                MetadataJson = JsonSerializer.Serialize(new
                {
                    execution.RunbookId,
                    execution.PlaybookId,
                    execution.WorkflowProfileId,
                    execution.EnvironmentId,
                    execution.EnvironmentName,
                    execution.CheckType,
                    execution.DeploymentId,
                    execution.IncidentId,
                    completedStepCount = execution.CompletedStepIds.Count,
                    execution.StartedUtc,
                    execution.CompletedUtc
                })
            }).ToList();
        }

        private async Task<List<HistoricalTimelineEntry>> BuildEventEntriesAsync(AuthContext auth, HistoricalTimelineQuery timelineQuery, List<string> truncated, CancellationToken token)
        {
            List<ArmadaEvent> events = await ReadEventsAsync(auth, timelineQuery, truncated, token).ConfigureAwait(false);
            events = events
                .Where(entry =>
                    !String.Equals(entry.EventType, "incident.snapshot", StringComparison.OrdinalIgnoreCase)
                    && !String.Equals(entry.EventType, "objective.snapshot", StringComparison.OrdinalIgnoreCase)
                    && !String.Equals(entry.EventType, "runbook-execution.snapshot", StringComparison.OrdinalIgnoreCase))
                .ToList();
            return events.Select(entry => new HistoricalTimelineEntry
            {
                Id = "timeline-event-" + entry.Id,
                SourceType = "Event",
                SourceId = entry.Id,
                EntityType = entry.EntityType,
                EntityId = entry.EntityId,
                VesselId = entry.VesselId,
                MissionId = entry.MissionId,
                VoyageId = entry.VoyageId,
                ActorId = entry.CaptainId,
                ActorDisplay = entry.CaptainId,
                Title = entry.EventType,
                Description = entry.Message,
                Status = entry.EntityType,
                Severity = entry.EventType.IndexOf("fail", StringComparison.OrdinalIgnoreCase) >= 0 ? "error" : "info",
                Route = "/events/" + entry.Id,
                OccurredUtc = entry.CreatedUtc,
                MetadataJson = entry.Payload
            }).ToList();
        }

        private async Task<List<HistoricalTimelineEntry>> BuildRequestEntriesAsync(AuthContext auth, HistoricalTimelineQuery timelineQuery, List<string> truncated, CancellationToken token)
        {
            RequestHistoryQuery requestQuery = new RequestHistoryQuery
            {
                TenantId = auth.IsAdmin ? timelineQuery.TenantId : auth.TenantId,
                UserId = auth.IsAdmin || auth.IsTenantAdmin ? timelineQuery.UserId : auth.UserId,
                Route = null,
                ExcludeReadMethods = timelineQuery.ExcludeReadRequests,
                FromUtc = timelineQuery.FromUtc,
                ToUtc = timelineQuery.ToUtc
            };

            List<RequestHistoryEntry> entries = await ReadNewestAsync("Request", truncated, async (pageNumber, pageSize) =>
            {
                requestQuery.PageNumber = pageNumber;
                requestQuery.PageSize = pageSize;
                return (await _Database.RequestHistory.EnumerateAsync(requestQuery, token).ConfigureAwait(false)).Objects;
            }).ConfigureAwait(false);
            return entries.Select(entry => new HistoricalTimelineEntry
            {
                Id = "timeline-request-" + entry.Id,
                SourceType = "Request",
                SourceId = entry.Id,
                EntityType = "request-history",
                EntityId = entry.Id,
                ActorId = entry.UserId,
                ActorDisplay = entry.PrincipalDisplay,
                Title = entry.Method + " " + entry.Route,
                Description = entry.IsSuccess
                    ? "HTTP " + entry.StatusCode + " in " + entry.DurationMs + " ms"
                    : "HTTP " + entry.StatusCode + " failed in " + entry.DurationMs + " ms",
                Status = entry.StatusCode.ToString(),
                Severity = entry.IsSuccess ? "info" : "warning",
                Route = "/requests/" + entry.Id,
                OccurredUtc = entry.CreatedUtc,
                MetadataJson = JsonSerializer.Serialize(new
                {
                    entry.Method,
                    entry.Route,
                    entry.RouteTemplate,
                    entry.StatusCode,
                    entry.DurationMs,
                    entry.AuthMethod,
                    entry.CorrelationId
                })
            }).ToList();
        }

        private async Task<List<Mission>> ReadMissionsAsync(AuthContext auth, HistoricalTimelineQuery query, List<string> truncated, CancellationToken token)
        {
            EnumerationQuery missionQuery = new EnumerationQuery
            {
                VesselId = query.VesselId,
                VoyageId = query.VoyageId,
                // A mission's entry time is its last update, so only the upper bound can be applied at creation.
                CreatedBefore = query.ToUtc
            };

            return await ReadNewestAsync("Mission", truncated, async (pageNumber, pageSize) =>
            {
                missionQuery.PageNumber = pageNumber;
                missionQuery.PageSize = pageSize;
                EnumerationResult<Mission> page;
                if (auth.IsAdmin)
                    page = await _Database.Missions.EnumerateSummariesAsync(missionQuery, token).ConfigureAwait(false);
                else if (auth.IsTenantAdmin)
                    page = await _Database.Missions.EnumerateSummariesAsync(auth.TenantId!, missionQuery, token).ConfigureAwait(false);
                else
                    page = await _Database.Missions.EnumerateSummariesAsync(auth.TenantId!, auth.UserId!, missionQuery, token).ConfigureAwait(false);
                return page.Objects;
            }).ConfigureAwait(false);
        }

        /// <summary>
        /// Read named missions by identifier, within the caller's scope, so a mission or objective filter finds its
        /// missions however many newer missions the server holds.
        /// </summary>
        private async Task<List<Mission>> ReadMissionsByIdAsync(AuthContext auth, List<string> ids, List<string> truncated, CancellationToken token)
        {
            List<string> missionIds = ids
                .Where(id => !String.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (missionIds.Count > _SourceRowLimit)
            {
                missionIds = missionIds.Take(_SourceRowLimit).ToList();
                truncated.Add("Mission");
            }

            List<Mission> missions = new List<Mission>();
            foreach (string missionId in missionIds)
            {
                Mission? mission = await _Database.Missions.ReadSummaryAsync(missionId, token).ConfigureAwait(false);
                if (mission == null) continue;
                if (!auth.IsAdmin && !String.Equals(mission.TenantId, auth.TenantId, StringComparison.Ordinal)) continue;
                if (!auth.IsAdmin && !auth.IsTenantAdmin && !String.Equals(mission.UserId, auth.UserId, StringComparison.Ordinal)) continue;
                missions.Add(mission);
            }

            return missions;
        }

        /// <summary>
        /// Read a source newest first, one page at a time, and stop once <see cref="SourceRowLimit"/> records are
        /// held. A source with more records than the limit is added to <paramref name="truncated"/>. At most one
        /// page beyond the limit is ever read.
        /// </summary>
        /// <param name="sourceType">Timeline source type, named in <paramref name="truncated"/> when it is cut.</param>
        /// <param name="truncated">Source types that held more records than the limit.</param>
        /// <param name="readPage">Reads one page, newest first, given the one-based page number and the page size.</param>
        private async Task<List<T>> ReadNewestAsync<T>(string sourceType, List<string> truncated, Func<int, int, Task<List<T>>> readPage)
        {
            int pageSize = Math.Min(_SourceRowLimit, _SourcePageSize);
            List<T> results = new List<T>();
            int pageNumber = 1;
            while (true)
            {
                List<T> page = await readPage(pageNumber, pageSize).ConfigureAwait(false);
                results.AddRange(page);
                if (results.Count > _SourceRowLimit)
                {
                    results.RemoveRange(_SourceRowLimit, results.Count - _SourceRowLimit);
                    if (!truncated.Contains(sourceType)) truncated.Add(sourceType);
                    break;
                }
                if (page.Count < pageSize)
                    break;
                pageNumber += 1;
            }

            return results;
        }

        private async Task<List<Voyage>> ReadVoyagesAsync(AuthContext auth, CancellationToken token)
        {
            if (auth.IsAdmin)
                return await _Database.Voyages.EnumerateAsync(token).ConfigureAwait(false);
            if (auth.IsTenantAdmin)
                return await _Database.Voyages.EnumerateAsync(auth.TenantId!, token).ConfigureAwait(false);
            return await _Database.Voyages.EnumerateAsync(auth.TenantId!, auth.UserId!, token).ConfigureAwait(false);
        }

        private async Task<List<PlanningSession>> ReadPlanningSessionsAsync(AuthContext auth, CancellationToken token)
        {
            if (auth.IsAdmin)
                return await _Database.PlanningSessions.EnumerateAsync(token).ConfigureAwait(false);
            if (auth.IsTenantAdmin)
                return await _Database.PlanningSessions.EnumerateAsync(auth.TenantId!, token).ConfigureAwait(false);
            return await _Database.PlanningSessions.EnumerateAsync(auth.TenantId!, auth.UserId!, token).ConfigureAwait(false);
        }

        private async Task<List<ObjectiveRefinementSession>> ReadObjectiveRefinementSessionsAsync(AuthContext auth, CancellationToken token)
        {
            if (auth.IsAdmin)
                return await _Database.ObjectiveRefinementSessions.EnumerateAsync(token).ConfigureAwait(false);
            if (auth.IsTenantAdmin)
                return await _Database.ObjectiveRefinementSessions.EnumerateAsync(auth.TenantId!, token).ConfigureAwait(false);
            return await _Database.ObjectiveRefinementSessions.EnumerateAsync(auth.TenantId!, auth.UserId!, token).ConfigureAwait(false);
        }

        private async Task<List<MergeEntry>> ReadMergeEntriesAsync(AuthContext auth, CancellationToken token)
        {
            if (auth.IsAdmin)
                return await _Database.MergeEntries.EnumerateAsync(token).ConfigureAwait(false);
            if (auth.IsTenantAdmin)
                return await _Database.MergeEntries.EnumerateAsync(auth.TenantId!, token).ConfigureAwait(false);
            return await _Database.MergeEntries.EnumerateAsync(auth.TenantId!, auth.UserId!, token).ConfigureAwait(false);
        }

        private async Task<List<CheckRun>> ReadCheckRunsAsync(AuthContext auth, HistoricalTimelineQuery query, List<string> truncated, CancellationToken token)
        {
            CheckRunQuery checkQuery = new CheckRunQuery
            {
                TenantId = auth.IsAdmin ? query.TenantId : auth.TenantId,
                UserId = auth.IsAdmin || auth.IsTenantAdmin ? query.UserId : auth.UserId,
                VesselId = query.VesselId,
                DeploymentId = query.DeploymentId,
                MissionId = query.MissionId,
                VoyageId = query.VoyageId,
                FromUtc = query.FromUtc,
                ToUtc = query.ToUtc,
                IncludeOutput = false
            };

            return await ReadNewestAsync("CheckRun", truncated, async (pageNumber, pageSize) =>
            {
                checkQuery.PageNumber = pageNumber;
                checkQuery.PageSize = pageSize;
                return (await _Database.CheckRuns.EnumerateAsync(checkQuery, token).ConfigureAwait(false)).Objects;
            }).ConfigureAwait(false);
        }

        private async Task<List<ArmadaEvent>> ReadEventsAsync(AuthContext auth, HistoricalTimelineQuery query, List<string> truncated, CancellationToken token)
        {
            EnumerationQuery eventQuery = new EnumerationQuery
            {
                VesselId = query.VesselId,
                MissionId = query.MissionId,
                VoyageId = query.VoyageId,
                CreatedAfter = query.FromUtc,
                CreatedBefore = query.ToUtc
            };

            return await ReadNewestAsync("Event", truncated, async (pageNumber, pageSize) =>
            {
                eventQuery.PageNumber = pageNumber;
                eventQuery.PageSize = pageSize;
                EnumerationResult<ArmadaEvent> page;
                if (auth.IsAdmin)
                    page = await _Database.Events.EnumerateAsync(eventQuery, token).ConfigureAwait(false);
                else if (auth.IsTenantAdmin)
                    page = await _Database.Events.EnumerateAsync(auth.TenantId!, eventQuery, token).ConfigureAwait(false);
                else
                    page = await _Database.Events.EnumerateAsync(auth.TenantId!, auth.UserId!, eventQuery, token).ConfigureAwait(false);
                return page.Objects;
            }).ConfigureAwait(false);
        }

        private async Task<List<Release>> ReadReleasesAsync(AuthContext auth, CancellationToken token)
        {
            ReleaseQuery releaseQuery = new ReleaseQuery
            {
                TenantId = auth.IsAdmin ? null : auth.TenantId,
                UserId = auth.IsAdmin || auth.IsTenantAdmin ? null : auth.UserId,
                PageNumber = 1,
                PageSize = 500
            };

            return await _Database.Releases.EnumerateAllAsync(releaseQuery, token).ConfigureAwait(false);
        }

        private async Task<List<Deployment>> ReadDeploymentsAsync(AuthContext auth, HistoricalTimelineQuery query, List<string> truncated, CancellationToken token)
        {
            DeploymentQuery deploymentQuery = new DeploymentQuery
            {
                TenantId = auth.IsAdmin ? query.TenantId : auth.TenantId,
                UserId = auth.IsAdmin || auth.IsTenantAdmin ? query.UserId : auth.UserId,
                VesselId = query.VesselId,
                EnvironmentId = query.EnvironmentId,
                MissionId = query.MissionId,
                VoyageId = query.VoyageId,
                FromUtc = query.FromUtc,
                ToUtc = query.ToUtc
            };

            return await ReadNewestAsync("Deployment", truncated, async (pageNumber, pageSize) =>
            {
                deploymentQuery.PageNumber = pageNumber;
                deploymentQuery.PageSize = pageSize;
                return (await _Database.Deployments.EnumerateAsync(deploymentQuery, token).ConfigureAwait(false)).Objects;
            }).ConfigureAwait(false);
        }

        private async Task<List<Objective>> ReadObjectivesAsync(AuthContext auth, CancellationToken token)
        {
            if (auth.IsAdmin)
                return await _Database.Objectives.EnumerateAsync(token).ConfigureAwait(false);
            if (auth.IsTenantAdmin)
                return await _Database.Objectives.EnumerateAsync(auth.TenantId!, token).ConfigureAwait(false);
            return await _Database.Objectives.EnumerateAsync(auth.TenantId!, auth.UserId!, token).ConfigureAwait(false);
        }

        private async Task<Objective?> ReadObjectiveAsync(AuthContext auth, string id, CancellationToken token)
        {
            if (auth.IsAdmin)
                return await _Database.Objectives.ReadAsync(id, token).ConfigureAwait(false);
            if (auth.IsTenantAdmin)
                return await _Database.Objectives.ReadAsync(auth.TenantId!, id, token).ConfigureAwait(false);
            return await _Database.Objectives.ReadAsync(auth.TenantId!, auth.UserId!, id, token).ConfigureAwait(false);
        }

        private async Task<List<Incident>> ReadIncidentsAsync(AuthContext auth, CancellationToken token)
        {
            List<ArmadaEvent> snapshots = await ReadEventSnapshotsByEntityTypeAsync(auth, "incident", IncidentService.SnapshotEventType, token).ConfigureAwait(false);
            Dictionary<string, ArmadaEvent> latestById = new Dictionary<string, ArmadaEvent>(StringComparer.OrdinalIgnoreCase);

            foreach (ArmadaEvent snapshot in snapshots)
            {
                if (String.IsNullOrWhiteSpace(snapshot.EntityId))
                    continue;

                if (!latestById.TryGetValue(snapshot.EntityId, out ArmadaEvent? existing)
                    || IsSnapshotNewer(snapshot, existing))
                {
                    latestById[snapshot.EntityId] = snapshot;
                }
            }

            List<Incident> incidents = new List<Incident>();
            foreach (ArmadaEvent snapshot in latestById.Values)
            {
                Incident? incident = DeserializeEventPayload<Incident>(snapshot.Payload);
                if (incident != null)
                    incidents.Add(incident);
            }

            return incidents;
        }

        private async Task<List<RunbookExecution>> ReadRunbookExecutionsAsync(AuthContext auth, CancellationToken token)
        {
            List<ArmadaEvent> snapshots = await ReadEventSnapshotsByEntityTypeAsync(auth, "runbook-execution", RunbookService.ExecutionSnapshotEventType, token).ConfigureAwait(false);
            Dictionary<string, ArmadaEvent> latestById = new Dictionary<string, ArmadaEvent>(StringComparer.OrdinalIgnoreCase);

            foreach (ArmadaEvent snapshot in snapshots)
            {
                if (String.IsNullOrWhiteSpace(snapshot.EntityId))
                    continue;

                if (!latestById.TryGetValue(snapshot.EntityId, out ArmadaEvent? existing)
                    || IsSnapshotNewer(snapshot, existing))
                {
                    latestById[snapshot.EntityId] = snapshot;
                }
            }

            List<RunbookExecution> executions = new List<RunbookExecution>();
            foreach (ArmadaEvent snapshot in latestById.Values)
            {
                RunbookExecution? execution = DeserializeEventPayload<RunbookExecution>(snapshot.Payload);
                if (execution != null)
                    executions.Add(execution);
            }

            return executions;
        }

        /// <summary>
        /// Read every snapshot event of one entity type. The database selects only the snapshot event type, so the
        /// read scales with the number of snapshots, not with the whole event table.
        /// </summary>
        private async Task<List<ArmadaEvent>> ReadEventSnapshotsByEntityTypeAsync(AuthContext auth, string entityType, string snapshotEventType, CancellationToken token)
        {
            EnumerationQuery query = new EnumerationQuery
            {
                PageNumber = 1,
                PageSize = _SourcePageSize,
                EventType = snapshotEventType
            };
            List<ArmadaEvent> events = new List<ArmadaEvent>();

            while (true)
            {
                EnumerationResult<ArmadaEvent> page;
                if (auth.IsAdmin)
                    page = await _Database.Events.EnumerateAsync(query, token).ConfigureAwait(false);
                else if (auth.IsTenantAdmin)
                    page = await _Database.Events.EnumerateAsync(auth.TenantId!, query, token).ConfigureAwait(false);
                else
                    page = await _Database.Events.EnumerateAsync(auth.TenantId!, auth.UserId!, query, token).ConfigureAwait(false);

                events.AddRange(page.Objects.Where(item =>
                    String.Equals(item.EntityType, entityType, StringComparison.OrdinalIgnoreCase)
                    && String.Equals(item.EventType, snapshotEventType, StringComparison.OrdinalIgnoreCase)));
                if (page.Objects.Count < query.PageSize)
                    break;
                query.PageNumber += 1;
            }

            return events;
        }

        private static TEntity? DeserializeEventPayload<TEntity>(string? payload)
            where TEntity : class
        {
            if (String.IsNullOrWhiteSpace(payload))
                return null;

            try
            {
                return JsonSerializer.Deserialize<TEntity>(payload, _EventPayloadJsonOptions);
            }
            catch
            {
                return null;
            }
        }

        private static string IncidentSeverity(Incident incident)
        {
            if (incident.Status == IncidentStatusEnum.Closed)
                return "success";
            if (incident.Status == IncidentStatusEnum.RolledBack)
                return "warning";
            if (incident.Status == IncidentStatusEnum.Mitigated || incident.Status == IncidentStatusEnum.Monitoring)
                return "warning";
            if (incident.Severity == IncidentSeverityEnum.Critical || incident.Severity == IncidentSeverityEnum.High)
                return "error";
            return "info";
        }

        private static string RunbookExecutionSeverity(RunbookExecution execution)
        {
            if (execution.Status == RunbookExecutionStatusEnum.Completed)
                return "success";
            if (execution.Status == RunbookExecutionStatusEnum.Cancelled)
                return "warning";
            return "info";
        }

        private static string MissionSeverity(Mission mission)
        {
            if (mission.Status == MissionStatusEnum.Failed || mission.Status == MissionStatusEnum.LandingFailed)
                return "error";
            if (mission.Status == MissionStatusEnum.Cancelled || mission.Status == MissionStatusEnum.Review)
                return "warning";
            return "info";
        }

        private static string VoyageSeverity(Voyage voyage)
        {
            if (voyage.Status == VoyageStatusEnum.Failed)
                return "error";
            if (voyage.Status == VoyageStatusEnum.Cancelled)
                return "warning";
            return "info";
        }

        private static string DeploymentSeverity(Deployment deployment)
        {
            if (deployment.Status == DeploymentStatusEnum.Failed || deployment.Status == DeploymentStatusEnum.VerificationFailed)
                return "error";
            if (deployment.Status == DeploymentStatusEnum.Denied
                || deployment.Status == DeploymentStatusEnum.RollingBack
                || deployment.Status == DeploymentStatusEnum.RolledBack)
                return "warning";
            return "info";
        }

        private static string ObjectiveSeverity(Objective objective)
        {
            if (objective.Status == ObjectiveStatusEnum.Completed || objective.Status == ObjectiveStatusEnum.Deployed || objective.Status == ObjectiveStatusEnum.Released)
                return "success";
            if (objective.Status == ObjectiveStatusEnum.Blocked)
                return "error";
            if (objective.Status == ObjectiveStatusEnum.Cancelled)
                return "warning";
            return "info";
        }

        private static bool ContainsIgnoreCase(string? value, string? search)
        {
            if (String.IsNullOrWhiteSpace(value) || String.IsNullOrWhiteSpace(search))
                return false;
            return value.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool MatchesPostmortemContext(
            HistoricalTimelineEntry entry,
            HashSet<string> incidentIds,
            HashSet<string> deploymentIds,
            HashSet<string> releaseIds,
            HashSet<string> missionIds,
            HashSet<string> voyageIds)
        {
            if (!String.IsNullOrWhiteSpace(entry.IncidentId) && incidentIds.Contains(entry.IncidentId))
                return true;
            if (SourceIdMatches(entry, "Incident", incidentIds))
                return true;

            if (!String.IsNullOrWhiteSpace(entry.DeploymentId) && deploymentIds.Contains(entry.DeploymentId))
                return true;
            if (SourceIdMatches(entry, "Deployment", deploymentIds))
                return true;

            if (SourceIdMatches(entry, "Release", releaseIds))
                return true;

            if (!String.IsNullOrWhiteSpace(entry.MissionId) && missionIds.Contains(entry.MissionId))
                return true;
            if (SourceIdMatches(entry, "Mission", missionIds))
                return true;

            if (!String.IsNullOrWhiteSpace(entry.VoyageId) && voyageIds.Contains(entry.VoyageId))
                return true;
            if (SourceIdMatches(entry, "Voyage", voyageIds))
                return true;

            return false;
        }

        private static bool IsSnapshotNewer(ArmadaEvent candidate, ArmadaEvent existing)
        {
            if (candidate.CreatedUtc > existing.CreatedUtc)
                return true;
            if (candidate.CreatedUtc < existing.CreatedUtc)
                return false;

            string candidateId = candidate.Id ?? String.Empty;
            string existingId = existing.Id ?? String.Empty;
            return StringComparer.Ordinal.Compare(candidateId, existingId) > 0;
        }

        private static bool SourceIdMatches(HistoricalTimelineEntry entry, string sourceType, HashSet<string> ids)
        {
            if (!String.Equals(entry.SourceType, sourceType, StringComparison.OrdinalIgnoreCase))
                return false;
            if (String.IsNullOrWhiteSpace(entry.SourceId))
                return false;
            return ids.Contains(entry.SourceId);
        }

        private static bool MatchesObjective(HistoricalTimelineEntry entry, Objective objective)
        {
            if (String.Equals(entry.ObjectiveId, objective.Id, StringComparison.OrdinalIgnoreCase))
                return true;
            if (String.Equals(entry.EntityType, "objective", StringComparison.OrdinalIgnoreCase)
                && String.Equals(entry.EntityId, objective.Id, StringComparison.OrdinalIgnoreCase))
                return true;
            if (!String.IsNullOrWhiteSpace(entry.VoyageId)
                && objective.VoyageIds.Contains(entry.VoyageId, StringComparer.OrdinalIgnoreCase))
                return true;
            if (!String.IsNullOrWhiteSpace(entry.MissionId)
                && objective.MissionIds.Contains(entry.MissionId, StringComparer.OrdinalIgnoreCase))
                return true;
            if (!String.IsNullOrWhiteSpace(entry.DeploymentId)
                && objective.DeploymentIds.Contains(entry.DeploymentId, StringComparer.OrdinalIgnoreCase))
                return true;
            if (!String.IsNullOrWhiteSpace(entry.IncidentId)
                && objective.IncidentIds.Contains(entry.IncidentId, StringComparer.OrdinalIgnoreCase))
                return true;
            if (!String.IsNullOrWhiteSpace(entry.EntityId)
                && objective.ReleaseIds.Contains(entry.EntityId, StringComparer.OrdinalIgnoreCase))
                return true;
            if (!String.IsNullOrWhiteSpace(entry.EntityId)
                && objective.CheckRunIds.Contains(entry.EntityId, StringComparer.OrdinalIgnoreCase))
                return true;
            if (!String.IsNullOrWhiteSpace(entry.EntityId)
                && objective.PlanningSessionIds.Contains(entry.EntityId, StringComparer.OrdinalIgnoreCase))
                return true;
            if (!String.IsNullOrWhiteSpace(entry.EntityId)
                && objective.RefinementSessionIds.Contains(entry.EntityId, StringComparer.OrdinalIgnoreCase))
                return true;
            if (!String.IsNullOrWhiteSpace(entry.SourceId)
                && objective.RefinementSessionIds.Contains(entry.SourceId, StringComparer.OrdinalIgnoreCase))
                return true;
            return false;
        }
    }
}
