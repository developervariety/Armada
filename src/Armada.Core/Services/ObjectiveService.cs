namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// Event-backed objective records that capture scoped work before and alongside execution.
    /// </summary>
    public class ObjectiveService
    {
        /// <summary>
        /// Optional callback invoked whenever an objective changes.
        /// </summary>
        public Action<Objective>? OnObjectiveChanged { get; set; }

        private readonly DatabaseDriver _Database;
        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };

        /// <summary>
        /// Instantiate.
        /// </summary>
        public ObjectiveService(DatabaseDriver database)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
        }

        /// <summary>
        /// Enumerate objectives visible to the caller.
        /// </summary>
        public async Task<EnumerationResult<Objective>> EnumerateAsync(
            AuthContext auth,
            ObjectiveQuery query,
            CancellationToken token = default)
        {
            if (auth == null) throw new ArgumentNullException(nameof(auth));
            if (query == null) throw new ArgumentNullException(nameof(query));

            List<Objective> objectives = await ReadAllObjectivesAsync(auth, token).ConfigureAwait(false);
            IEnumerable<Objective> filtered = objectives;

            if (!String.IsNullOrWhiteSpace(query.Owner))
                filtered = filtered.Where(item => ContainsIgnoreCase(item.Owner, query.Owner));
            if (!String.IsNullOrWhiteSpace(query.VesselId))
                filtered = filtered.Where(item => item.VesselIds.Contains(query.VesselId, StringComparer.OrdinalIgnoreCase));
            if (!String.IsNullOrWhiteSpace(query.FleetId))
                filtered = filtered.Where(item => item.FleetIds.Contains(query.FleetId, StringComparer.OrdinalIgnoreCase));
            if (!String.IsNullOrWhiteSpace(query.PlanningSessionId))
                filtered = filtered.Where(item => item.PlanningSessionIds.Contains(query.PlanningSessionId, StringComparer.OrdinalIgnoreCase));
            if (!String.IsNullOrWhiteSpace(query.VoyageId))
                filtered = filtered.Where(item => item.VoyageIds.Contains(query.VoyageId, StringComparer.OrdinalIgnoreCase));
            if (!String.IsNullOrWhiteSpace(query.MissionId))
                filtered = filtered.Where(item => item.MissionIds.Contains(query.MissionId, StringComparer.OrdinalIgnoreCase));
            if (!String.IsNullOrWhiteSpace(query.CheckRunId))
                filtered = filtered.Where(item => item.CheckRunIds.Contains(query.CheckRunId, StringComparer.OrdinalIgnoreCase));
            if (!String.IsNullOrWhiteSpace(query.ReleaseId))
                filtered = filtered.Where(item => item.ReleaseIds.Contains(query.ReleaseId, StringComparer.OrdinalIgnoreCase));
            if (!String.IsNullOrWhiteSpace(query.DeploymentId))
                filtered = filtered.Where(item => item.DeploymentIds.Contains(query.DeploymentId, StringComparer.OrdinalIgnoreCase));
            if (!String.IsNullOrWhiteSpace(query.IncidentId))
                filtered = filtered.Where(item => item.IncidentIds.Contains(query.IncidentId, StringComparer.OrdinalIgnoreCase));
            if (!String.IsNullOrWhiteSpace(query.Tag))
                filtered = filtered.Where(item => item.Tags.Contains(query.Tag, StringComparer.OrdinalIgnoreCase));
            if (query.Status.HasValue)
                filtered = filtered.Where(item => item.Status == query.Status.Value);
            if (query.FromUtc.HasValue)
                filtered = filtered.Where(item => item.CreatedUtc >= query.FromUtc.Value.ToUniversalTime());
            if (query.ToUtc.HasValue)
                filtered = filtered.Where(item => item.CreatedUtc <= query.ToUtc.Value.ToUniversalTime());

            string? search = Normalize(query.Search);
            if (!String.IsNullOrWhiteSpace(search))
            {
                filtered = filtered.Where(item =>
                    ContainsIgnoreCase(item.Title, search)
                    || ContainsIgnoreCase(item.Description, search)
                    || ContainsIgnoreCase(item.Owner, search)
                    || item.Tags.Any(tag => ContainsIgnoreCase(tag, search))
                    || item.AcceptanceCriteria.Any(criteria => ContainsIgnoreCase(criteria, search))
                    || item.RolloutConstraints.Any(constraint => ContainsIgnoreCase(constraint, search))
                    || item.EvidenceLinks.Any(link => ContainsIgnoreCase(link, search)));
            }

            List<Objective> ordered = filtered
                .OrderByDescending(item => item.LastUpdateUtc)
                .ThenByDescending(item => item.Id, StringComparer.Ordinal)
                .ToList();

            int pageSize = query.PageSize < 1 ? 50 : Math.Min(query.PageSize, 500);
            int pageNumber = query.PageNumber < 1 ? 1 : query.PageNumber;
            int offset = (pageNumber - 1) * pageSize;
            List<Objective> page = ordered.Skip(offset).Take(pageSize).ToList();

            return new EnumerationResult<Objective>
            {
                PageNumber = pageNumber,
                PageSize = pageSize,
                TotalRecords = ordered.Count,
                TotalPages = pageSize > 0 ? (int)Math.Ceiling((double)ordered.Count / pageSize) : 0,
                Objects = page,
                TotalMs = 0
            };
        }

        /// <summary>
        /// Read one objective.
        /// </summary>
        public async Task<Objective?> ReadAsync(AuthContext auth, string id, CancellationToken token = default)
        {
            if (auth == null) throw new ArgumentNullException(nameof(auth));
            if (String.IsNullOrWhiteSpace(id)) throw new ArgumentNullException(nameof(id));

            List<ArmadaEvent> snapshots = await ReadObjectiveSnapshotEventsAsync(auth, id, token).ConfigureAwait(false);
            return ProjectLatestObjective(snapshots);
        }

        /// <summary>
        /// Create an objective.
        /// </summary>
        public async Task<Objective> CreateAsync(AuthContext auth, ObjectiveUpsertRequest request, CancellationToken token = default)
        {
            if (auth == null) throw new ArgumentNullException(nameof(auth));
            if (request == null) throw new ArgumentNullException(nameof(request));

            Objective objective = new Objective
            {
                TenantId = auth.IsAdmin ? null : auth.TenantId,
                UserId = auth.UserId,
                Title = NormalizeRequired(request.Title, nameof(request.Title)),
                Description = Normalize(request.Description),
                Status = request.Status ?? ObjectiveStatusEnum.Draft,
                Owner = Normalize(request.Owner),
                Tags = DistinctNormalized(request.Tags),
                AcceptanceCriteria = DistinctNormalized(request.AcceptanceCriteria),
                NonGoals = DistinctNormalized(request.NonGoals),
                RolloutConstraints = DistinctNormalized(request.RolloutConstraints),
                EvidenceLinks = DistinctNormalized(request.EvidenceLinks),
                FleetIds = DistinctNormalized(request.FleetIds),
                VesselIds = DistinctNormalized(request.VesselIds),
                PlanningSessionIds = DistinctNormalized(request.PlanningSessionIds),
                VoyageIds = DistinctNormalized(request.VoyageIds),
                MissionIds = DistinctNormalized(request.MissionIds),
                CheckRunIds = DistinctNormalized(request.CheckRunIds),
                ReleaseIds = DistinctNormalized(request.ReleaseIds),
                DeploymentIds = DistinctNormalized(request.DeploymentIds),
                IncidentIds = DistinctNormalized(request.IncidentIds),
                CreatedUtc = DateTime.UtcNow,
                LastUpdateUtc = DateTime.UtcNow
            };

            await ValidateLinksAsync(auth, objective, token).ConfigureAwait(false);
            ApplyLifecycleTimestamps(objective);
            await WriteSnapshotAsync(auth, objective, token).ConfigureAwait(false);
            OnObjectiveChanged?.Invoke(objective);
            return objective;
        }

        /// <summary>
        /// Update an objective.
        /// </summary>
        public async Task<Objective> UpdateAsync(AuthContext auth, string id, ObjectiveUpsertRequest request, CancellationToken token = default)
        {
            if (auth == null) throw new ArgumentNullException(nameof(auth));
            if (String.IsNullOrWhiteSpace(id)) throw new ArgumentNullException(nameof(id));
            if (request == null) throw new ArgumentNullException(nameof(request));

            Objective objective = await ReadAsync(auth, id, token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Objective not found.");

            objective.Title = Normalize(request.Title) ?? objective.Title;
            objective.Description = request.Description != null ? Normalize(request.Description) : objective.Description;
            objective.Status = request.Status ?? objective.Status;
            objective.Owner = request.Owner != null ? Normalize(request.Owner) : objective.Owner;
            if (request.Tags != null) objective.Tags = DistinctNormalized(request.Tags);
            if (request.AcceptanceCriteria != null) objective.AcceptanceCriteria = DistinctNormalized(request.AcceptanceCriteria);
            if (request.NonGoals != null) objective.NonGoals = DistinctNormalized(request.NonGoals);
            if (request.RolloutConstraints != null) objective.RolloutConstraints = DistinctNormalized(request.RolloutConstraints);
            if (request.EvidenceLinks != null) objective.EvidenceLinks = DistinctNormalized(request.EvidenceLinks);
            if (request.FleetIds != null) objective.FleetIds = DistinctNormalized(request.FleetIds);
            if (request.VesselIds != null) objective.VesselIds = DistinctNormalized(request.VesselIds);
            if (request.PlanningSessionIds != null) objective.PlanningSessionIds = DistinctNormalized(request.PlanningSessionIds);
            if (request.VoyageIds != null) objective.VoyageIds = DistinctNormalized(request.VoyageIds);
            if (request.MissionIds != null) objective.MissionIds = DistinctNormalized(request.MissionIds);
            if (request.CheckRunIds != null) objective.CheckRunIds = DistinctNormalized(request.CheckRunIds);
            if (request.ReleaseIds != null) objective.ReleaseIds = DistinctNormalized(request.ReleaseIds);
            if (request.DeploymentIds != null) objective.DeploymentIds = DistinctNormalized(request.DeploymentIds);
            if (request.IncidentIds != null) objective.IncidentIds = DistinctNormalized(request.IncidentIds);
            objective.LastUpdateUtc = DateTime.UtcNow;

            await ValidateLinksAsync(auth, objective, token).ConfigureAwait(false);
            ApplyLifecycleTimestamps(objective);
            await WriteSnapshotAsync(auth, objective, token).ConfigureAwait(false);
            OnObjectiveChanged?.Invoke(objective);
            return objective;
        }

        /// <summary>
        /// Delete an objective and all of its snapshots.
        /// </summary>
        public async Task DeleteAsync(AuthContext auth, string id, CancellationToken token = default)
        {
            if (auth == null) throw new ArgumentNullException(nameof(auth));
            if (String.IsNullOrWhiteSpace(id)) throw new ArgumentNullException(nameof(id));

            List<ArmadaEvent> snapshots = await ReadObjectiveSnapshotEventsAsync(auth, id, token).ConfigureAwait(false);
            if (snapshots.Count == 0)
                throw new InvalidOperationException("Objective not found.");

            foreach (ArmadaEvent snapshot in snapshots)
            {
                await DeleteEventAsync(auth, snapshot.Id, token).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Enumerate objectives linked to a planning session.
        /// </summary>
        public async Task<List<Objective>> EnumerateByPlanningSessionAsync(
            AuthContext auth,
            string planningSessionId,
            CancellationToken token = default)
        {
            if (auth == null) throw new ArgumentNullException(nameof(auth));
            if (String.IsNullOrWhiteSpace(planningSessionId)) throw new ArgumentNullException(nameof(planningSessionId));

            EnumerationResult<Objective> results = await EnumerateAsync(auth, new ObjectiveQuery
            {
                PageNumber = 1,
                PageSize = 500,
                PlanningSessionId = planningSessionId
            }, token).ConfigureAwait(false);

            return results.Objects;
        }

        /// <summary>
        /// Link a planning session to an objective and promote the objective into planning.
        /// </summary>
        public async Task<Objective> LinkPlanningSessionAsync(
            AuthContext auth,
            string objectiveId,
            string planningSessionId,
            CancellationToken token = default)
        {
            if (auth == null) throw new ArgumentNullException(nameof(auth));
            if (String.IsNullOrWhiteSpace(objectiveId)) throw new ArgumentNullException(nameof(objectiveId));
            if (String.IsNullOrWhiteSpace(planningSessionId)) throw new ArgumentNullException(nameof(planningSessionId));

            Objective objective = await ReadAsync(auth, objectiveId, token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Objective not found.");
            PlanningSession? session = await ReadPlanningSessionEntityAsync(auth, planningSessionId, token).ConfigureAwait(false);
            if (session == null)
                throw new InvalidOperationException("Planning session not found or not accessible: " + planningSessionId);

            AddIfMissing(objective.PlanningSessionIds, session.Id);
            AddIfMissing(objective.VesselIds, session.VesselId);
            AddIfMissing(objective.FleetIds, session.FleetId);
            PromoteStatus(objective, ObjectiveStatusEnum.Planned);
            return await PersistLinkedObjectiveAsync(auth, objective, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Link a voyage and its current mission scope to an objective.
        /// </summary>
        public async Task<Objective> LinkVoyageAsync(
            AuthContext auth,
            string objectiveId,
            string voyageId,
            CancellationToken token = default)
        {
            if (auth == null) throw new ArgumentNullException(nameof(auth));
            if (String.IsNullOrWhiteSpace(objectiveId)) throw new ArgumentNullException(nameof(objectiveId));
            if (String.IsNullOrWhiteSpace(voyageId)) throw new ArgumentNullException(nameof(voyageId));

            Objective objective = await ReadAsync(auth, objectiveId, token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Objective not found.");
            Voyage? voyage = await ReadVoyageEntityAsync(auth, voyageId, token).ConfigureAwait(false);
            if (voyage == null)
                throw new InvalidOperationException("Voyage not found or not accessible: " + voyageId);

            AddIfMissing(objective.VoyageIds, voyage.Id);

            List<Mission> missions = await ReadAccessibleMissionsForVoyageAsync(auth, voyage.Id, token).ConfigureAwait(false);
            foreach (Mission mission in missions)
            {
                AddIfMissing(objective.MissionIds, mission.Id);
                AddIfMissing(objective.VesselIds, mission.VesselId);
                await AppendFleetForVesselAsync(auth, objective, mission.VesselId, token).ConfigureAwait(false);
            }

            PromoteStatus(objective, ObjectiveStatusEnum.InProgress);
            return await PersistLinkedObjectiveAsync(auth, objective, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Link a release and its derived work scope to an objective.
        /// </summary>
        public async Task<Objective> LinkReleaseAsync(
            AuthContext auth,
            string objectiveId,
            string releaseId,
            CancellationToken token = default)
        {
            if (auth == null) throw new ArgumentNullException(nameof(auth));
            if (String.IsNullOrWhiteSpace(objectiveId)) throw new ArgumentNullException(nameof(objectiveId));
            if (String.IsNullOrWhiteSpace(releaseId)) throw new ArgumentNullException(nameof(releaseId));

            Objective objective = await ReadAsync(auth, objectiveId, token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Objective not found.");
            Release? release = await ReadReleaseEntityAsync(auth, releaseId, token).ConfigureAwait(false);
            if (release == null)
                throw new InvalidOperationException("Release not found or not accessible: " + releaseId);

            AddIfMissing(objective.ReleaseIds, release.Id);
            AddIfMissing(objective.VesselIds, release.VesselId);
            await AppendFleetForVesselAsync(auth, objective, release.VesselId, token).ConfigureAwait(false);

            foreach (string voyageId in release.VoyageIds)
                AddIfMissing(objective.VoyageIds, voyageId);
            foreach (string missionId in release.MissionIds)
                AddIfMissing(objective.MissionIds, missionId);
            foreach (string checkRunId in release.CheckRunIds)
                AddIfMissing(objective.CheckRunIds, checkRunId);

            PromoteStatus(objective, ObjectiveStatusEnum.Released);
            return await PersistLinkedObjectiveAsync(auth, objective, token).ConfigureAwait(false);
        }

        private async Task<List<Objective>> ReadAllObjectivesAsync(AuthContext auth, CancellationToken token)
        {
            List<ArmadaEvent> snapshots = await ReadObjectiveSnapshotEventsAsync(auth, null, token).ConfigureAwait(false);
            Dictionary<string, ArmadaEvent> latestByObjectiveId = new Dictionary<string, ArmadaEvent>(StringComparer.OrdinalIgnoreCase);

            foreach (ArmadaEvent snapshot in snapshots)
            {
                if (String.IsNullOrWhiteSpace(snapshot.EntityId))
                    continue;

                if (!latestByObjectiveId.TryGetValue(snapshot.EntityId, out ArmadaEvent? existing)
                    || existing.CreatedUtc < snapshot.CreatedUtc)
                {
                    latestByObjectiveId[snapshot.EntityId] = snapshot;
                }
            }

            List<Objective> objectives = new List<Objective>();
            foreach (ArmadaEvent snapshot in latestByObjectiveId.Values)
            {
                Objective? objective = DeserializeObjective(snapshot);
                if (objective != null)
                    objectives.Add(objective);
            }

            return objectives;
        }

        private async Task<List<ArmadaEvent>> ReadObjectiveSnapshotEventsAsync(
            AuthContext auth,
            string? objectiveId,
            CancellationToken token)
        {
            if (!String.IsNullOrWhiteSpace(objectiveId))
            {
                if (auth.IsAdmin)
                    return await _Database.Events.EnumerateByEntityAsync("objective", objectiveId, 500, token).ConfigureAwait(false);
                if (auth.IsTenantAdmin)
                    return await _Database.Events.EnumerateByEntityAsync(auth.TenantId!, "objective", objectiveId, 500, token).ConfigureAwait(false);
                return (await _Database.Events.EnumerateAsync(auth.TenantId!, auth.UserId!, new EnumerationQuery
                {
                    PageNumber = 1,
                    PageSize = 500
                }, token).ConfigureAwait(false)).Objects
                    .Where(item =>
                        String.Equals(item.EntityType, "objective", StringComparison.OrdinalIgnoreCase)
                        && String.Equals(item.EntityId, objectiveId, StringComparison.OrdinalIgnoreCase)
                        && String.Equals(item.EventType, "objective.snapshot", StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            List<ArmadaEvent> events;
            if (auth.IsAdmin)
                events = await _Database.Events.EnumerateByTypeAsync("objective.snapshot", 5000, token).ConfigureAwait(false);
            else if (auth.IsTenantAdmin)
                events = await _Database.Events.EnumerateByTypeAsync(auth.TenantId!, "objective.snapshot", 5000, token).ConfigureAwait(false);
            else
                events = (await _Database.Events.EnumerateAsync(auth.TenantId!, auth.UserId!, new EnumerationQuery
                {
                    PageNumber = 1,
                    PageSize = 5000
                }, token).ConfigureAwait(false)).Objects
                    .Where(item => String.Equals(item.EventType, "objective.snapshot", StringComparison.OrdinalIgnoreCase))
                    .ToList();

            return events
                .Where(item => String.Equals(item.EntityType, "objective", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        private async Task ValidateLinksAsync(AuthContext auth, Objective objective, CancellationToken token)
        {
            await ValidateIdsAsync(objective.FleetIds, async id => await ReadFleetAsync(auth, id, token).ConfigureAwait(false), "Fleet").ConfigureAwait(false);
            await ValidateIdsAsync(objective.VesselIds, async id => await ReadVesselAsync(auth, id, token).ConfigureAwait(false), "Vessel").ConfigureAwait(false);
            await ValidateIdsAsync(objective.PlanningSessionIds, async id => await ReadPlanningSessionAsync(auth, id, token).ConfigureAwait(false), "Planning session").ConfigureAwait(false);
            await ValidateIdsAsync(objective.VoyageIds, async id => await ReadVoyageAsync(auth, id, token).ConfigureAwait(false), "Voyage").ConfigureAwait(false);
            await ValidateIdsAsync(objective.MissionIds, async id => await ReadMissionAsync(auth, id, token).ConfigureAwait(false), "Mission").ConfigureAwait(false);
            await ValidateIdsAsync(objective.CheckRunIds, async id => await ReadCheckRunAsync(auth, id, token).ConfigureAwait(false), "Check run").ConfigureAwait(false);
            await ValidateIdsAsync(objective.ReleaseIds, async id => await ReadReleaseAsync(auth, id, token).ConfigureAwait(false), "Release").ConfigureAwait(false);
            await ValidateIdsAsync(objective.DeploymentIds, async id => await ReadDeploymentAsync(auth, id, token).ConfigureAwait(false), "Deployment").ConfigureAwait(false);
            await ValidateIdsAsync(objective.IncidentIds, async id => await ReadIncidentAsync(auth, id, token).ConfigureAwait(false), "Incident").ConfigureAwait(false);
        }

        private async Task ValidateIdsAsync(
            IEnumerable<string> ids,
            Func<string, Task<bool>> exists,
            string label)
        {
            foreach (string id in ids)
            {
                bool present = await exists(id).ConfigureAwait(false);
                if (!present)
                    throw new InvalidOperationException(label + " not found or not accessible: " + id);
            }
        }

        private async Task<bool> ReadFleetAsync(AuthContext auth, string id, CancellationToken token)
        {
            Fleet? fleet = auth.IsAdmin
                ? await _Database.Fleets.ReadAsync(id, token).ConfigureAwait(false)
                : await _Database.Fleets.ReadAsync(auth.TenantId!, id, token).ConfigureAwait(false);
            return fleet != null;
        }

        private async Task<bool> ReadVesselAsync(AuthContext auth, string id, CancellationToken token)
        {
            Vessel? vessel = auth.IsAdmin
                ? await _Database.Vessels.ReadAsync(id, token).ConfigureAwait(false)
                : await _Database.Vessels.ReadAsync(auth.TenantId!, id, token).ConfigureAwait(false);
            return vessel != null;
        }

        private async Task<bool> ReadPlanningSessionAsync(AuthContext auth, string id, CancellationToken token)
        {
            PlanningSession? session = auth.IsAdmin
                ? await _Database.PlanningSessions.ReadAsync(id, token).ConfigureAwait(false)
                : auth.IsTenantAdmin
                    ? await _Database.PlanningSessions.ReadAsync(auth.TenantId!, id, token).ConfigureAwait(false)
                    : await _Database.PlanningSessions.ReadAsync(auth.TenantId!, auth.UserId!, id, token).ConfigureAwait(false);
            return session != null;
        }

        private async Task<PlanningSession?> ReadPlanningSessionEntityAsync(AuthContext auth, string id, CancellationToken token)
        {
            if (auth.IsAdmin)
                return await _Database.PlanningSessions.ReadAsync(id, token).ConfigureAwait(false);
            if (auth.IsTenantAdmin)
                return await _Database.PlanningSessions.ReadAsync(auth.TenantId!, id, token).ConfigureAwait(false);
            return await _Database.PlanningSessions.ReadAsync(auth.TenantId!, auth.UserId!, id, token).ConfigureAwait(false);
        }

        private async Task<bool> ReadVoyageAsync(AuthContext auth, string id, CancellationToken token)
        {
            Voyage? voyage = auth.IsAdmin
                ? await _Database.Voyages.ReadAsync(id, token).ConfigureAwait(false)
                : await _Database.Voyages.ReadAsync(auth.TenantId!, id, token).ConfigureAwait(false);
            return voyage != null;
        }

        private async Task<Voyage?> ReadVoyageEntityAsync(AuthContext auth, string id, CancellationToken token)
        {
            if (auth.IsAdmin)
                return await _Database.Voyages.ReadAsync(id, token).ConfigureAwait(false);
            if (auth.IsTenantAdmin)
                return await _Database.Voyages.ReadAsync(auth.TenantId!, id, token).ConfigureAwait(false);
            return await _Database.Voyages.ReadAsync(auth.TenantId!, auth.UserId!, id, token).ConfigureAwait(false);
        }

        private async Task<bool> ReadMissionAsync(AuthContext auth, string id, CancellationToken token)
        {
            Mission? mission = auth.IsAdmin
                ? await _Database.Missions.ReadAsync(id, token).ConfigureAwait(false)
                : auth.IsTenantAdmin
                    ? await _Database.Missions.ReadAsync(auth.TenantId!, id, token).ConfigureAwait(false)
                    : await _Database.Missions.ReadAsync(auth.TenantId!, auth.UserId!, id, token).ConfigureAwait(false);
            return mission != null;
        }

        private async Task<bool> ReadCheckRunAsync(AuthContext auth, string id, CancellationToken token)
        {
            CheckRunQuery query = BuildCheckRunScopeQuery(auth);
            CheckRun? run = await _Database.CheckRuns.ReadAsync(id, query, token).ConfigureAwait(false);
            return run != null;
        }

        private async Task<bool> ReadReleaseAsync(AuthContext auth, string id, CancellationToken token)
        {
            ReleaseQuery query = BuildReleaseScopeQuery(auth);
            Release? release = await _Database.Releases.ReadAsync(id, query, token).ConfigureAwait(false);
            return release != null;
        }

        private async Task<Release?> ReadReleaseEntityAsync(AuthContext auth, string id, CancellationToken token)
        {
            ReleaseQuery query = BuildReleaseScopeQuery(auth);
            return await _Database.Releases.ReadAsync(id, query, token).ConfigureAwait(false);
        }

        private async Task<bool> ReadDeploymentAsync(AuthContext auth, string id, CancellationToken token)
        {
            DeploymentQuery query = BuildDeploymentScopeQuery(auth);
            Deployment? deployment = await _Database.Deployments.ReadAsync(id, query, token).ConfigureAwait(false);
            return deployment != null;
        }

        private async Task<bool> ReadIncidentAsync(AuthContext auth, string id, CancellationToken token)
        {
            IncidentService incidents = new IncidentService(_Database);
            Incident? incident = await incidents.ReadAsync(auth, id, token).ConfigureAwait(false);
            return incident != null;
        }

        private async Task WriteSnapshotAsync(AuthContext auth, Objective objective, CancellationToken token)
        {
            ArmadaEvent snapshot = new ArmadaEvent
            {
                TenantId = auth.IsAdmin ? null : auth.TenantId,
                UserId = auth.UserId,
                EventType = "objective.snapshot",
                EntityType = "objective",
                EntityId = objective.Id,
                VesselId = objective.VesselIds.Count > 0 ? objective.VesselIds[0] : null,
                VoyageId = objective.VoyageIds.Count > 0 ? objective.VoyageIds[0] : null,
                Message = objective.Title,
                Payload = JsonSerializer.Serialize(objective, _JsonOptions),
                CreatedUtc = objective.LastUpdateUtc
            };
            await _Database.Events.CreateAsync(snapshot, token).ConfigureAwait(false);
        }

        private async Task DeleteEventAsync(AuthContext auth, string eventId, CancellationToken token)
        {
            if (auth.IsAdmin)
            {
                await _Database.Events.DeleteAsync(eventId, token).ConfigureAwait(false);
                return;
            }

            await _Database.Events.DeleteAsync(auth.TenantId!, eventId, token).ConfigureAwait(false);
        }

        private async Task<Objective> PersistLinkedObjectiveAsync(AuthContext auth, Objective objective, CancellationToken token)
        {
            objective.LastUpdateUtc = DateTime.UtcNow;
            ApplyLifecycleTimestamps(objective);
            await WriteSnapshotAsync(auth, objective, token).ConfigureAwait(false);
            OnObjectiveChanged?.Invoke(objective);
            return objective;
        }

        private async Task AppendFleetForVesselAsync(AuthContext auth, Objective objective, string? vesselId, CancellationToken token)
        {
            string? normalizedVesselId = Normalize(vesselId);
            if (String.IsNullOrWhiteSpace(normalizedVesselId))
                return;

            Vessel? vessel = auth.IsAdmin
                ? await _Database.Vessels.ReadAsync(normalizedVesselId, token).ConfigureAwait(false)
                : auth.IsTenantAdmin
                    ? await _Database.Vessels.ReadAsync(auth.TenantId!, normalizedVesselId, token).ConfigureAwait(false)
                    : await _Database.Vessels.ReadAsync(auth.TenantId!, auth.UserId!, normalizedVesselId, token).ConfigureAwait(false);
            if (vessel == null)
                return;

            AddIfMissing(objective.FleetIds, vessel.FleetId);
        }

        private async Task<List<Mission>> ReadAccessibleMissionsForVoyageAsync(AuthContext auth, string voyageId, CancellationToken token)
        {
            if (auth.IsAdmin)
                return await _Database.Missions.EnumerateByVoyageAsync(voyageId, token).ConfigureAwait(false);
            if (auth.IsTenantAdmin)
                return await _Database.Missions.EnumerateByVoyageAsync(auth.TenantId!, voyageId, token).ConfigureAwait(false);

            List<Mission> tenantMissions = await _Database.Missions.EnumerateByVoyageAsync(auth.TenantId!, voyageId, token).ConfigureAwait(false);
            return tenantMissions
                .Where(mission => String.Equals(mission.UserId, auth.UserId, StringComparison.Ordinal))
                .ToList();
        }

        private static Objective? DeserializeObjective(ArmadaEvent snapshot)
        {
            if (String.IsNullOrWhiteSpace(snapshot.Payload))
                return null;

            try
            {
                return JsonSerializer.Deserialize<Objective>(snapshot.Payload, _JsonOptions);
            }
            catch
            {
                return null;
            }
        }

        private static Objective? ProjectLatestObjective(List<ArmadaEvent> snapshots)
        {
            ArmadaEvent? latest = snapshots
                .Where(item => String.Equals(item.EventType, "objective.snapshot", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(item => item.CreatedUtc)
                .FirstOrDefault();
            if (latest == null)
                return null;

            return DeserializeObjective(latest);
        }

        private static void ApplyLifecycleTimestamps(Objective objective)
        {
            if (objective.Status == ObjectiveStatusEnum.Completed)
            {
                objective.CompletedUtc ??= DateTime.UtcNow;
            }
            else if (objective.Status == ObjectiveStatusEnum.Cancelled)
            {
                objective.CompletedUtc = objective.CompletedUtc ?? DateTime.UtcNow;
            }
        }

        private static void PromoteStatus(Objective objective, ObjectiveStatusEnum targetStatus)
        {
            if (objective.Status == ObjectiveStatusEnum.Blocked
                || objective.Status == ObjectiveStatusEnum.Cancelled
                || objective.Status == ObjectiveStatusEnum.Completed)
                return;

            if ((int)objective.Status < (int)targetStatus)
                objective.Status = targetStatus;
        }

        private static void AddIfMissing(List<string> values, string? value)
        {
            string? normalized = Normalize(value);
            if (String.IsNullOrWhiteSpace(normalized))
                return;

            if (!values.Contains(normalized, StringComparer.OrdinalIgnoreCase))
                values.Add(normalized);
        }

        private static CheckRunQuery BuildCheckRunScopeQuery(AuthContext auth)
        {
            CheckRunQuery query = new CheckRunQuery();
            if (!auth.IsAdmin)
            {
                query.TenantId = auth.TenantId;
                if (!auth.IsTenantAdmin)
                    query.UserId = auth.UserId;
            }

            return query;
        }

        private static ReleaseQuery BuildReleaseScopeQuery(AuthContext auth)
        {
            ReleaseQuery query = new ReleaseQuery();
            if (!auth.IsAdmin)
            {
                query.TenantId = auth.TenantId;
                if (!auth.IsTenantAdmin)
                    query.UserId = auth.UserId;
            }

            return query;
        }

        private static DeploymentQuery BuildDeploymentScopeQuery(AuthContext auth)
        {
            DeploymentQuery query = new DeploymentQuery();
            if (!auth.IsAdmin)
            {
                query.TenantId = auth.TenantId;
                if (!auth.IsTenantAdmin)
                    query.UserId = auth.UserId;
            }

            return query;
        }

        private static List<string> DistinctNormalized(IEnumerable<string>? values)
        {
            if (values == null) return new List<string>();
            return values
                .Select(Normalize)
                .Where(value => !String.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Cast<string>()
                .ToList();
        }

        private static string NormalizeRequired(string? value, string paramName)
        {
            string? normalized = Normalize(value);
            if (String.IsNullOrWhiteSpace(normalized))
                throw new ArgumentNullException(paramName);
            return normalized;
        }

        private static string? Normalize(string? value)
        {
            return String.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        private static bool ContainsIgnoreCase(string? text, string? fragment)
        {
            if (String.IsNullOrWhiteSpace(text) || String.IsNullOrWhiteSpace(fragment))
                return false;
            return text.Contains(fragment, StringComparison.OrdinalIgnoreCase);
        }
    }
}
