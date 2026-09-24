namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using Armada.Core.Authorization;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// Event-backed incident records tied to the existing deployment and release lifecycle.
    /// </summary>
    public class IncidentService
    {
        /// <summary>
        /// Optional callback invoked whenever an incident changes.
        /// </summary>
        public Action<Incident>? OnIncidentChanged { get; set; }

        private readonly DatabaseDriver _Database;
        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };

        /// <summary>
        /// Instantiate.
        /// </summary>
        public IncidentService(DatabaseDriver database)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
        }

        /// <summary>
        /// Enumerate incidents visible to the caller.
        /// </summary>
        public async Task<EnumerationResult<Incident>> EnumerateAsync(
            AuthContext auth,
            IncidentQuery query,
            CancellationToken token = default)
        {
            if (auth == null) throw new ArgumentNullException(nameof(auth));
            if (query == null) throw new ArgumentNullException(nameof(query));

            List<Incident> incidents = await ReadAllIncidentsAsync(auth, token).ConfigureAwait(false);
            IEnumerable<Incident> filtered = incidents;

            if (!String.IsNullOrWhiteSpace(query.VesselId))
                filtered = filtered.Where(item => String.Equals(item.VesselId, query.VesselId, StringComparison.OrdinalIgnoreCase));
            if (!String.IsNullOrWhiteSpace(query.EnvironmentId))
                filtered = filtered.Where(item => String.Equals(item.EnvironmentId, query.EnvironmentId, StringComparison.OrdinalIgnoreCase));
            if (!String.IsNullOrWhiteSpace(query.CheckRunId))
                filtered = filtered.Where(item => String.Equals(item.CheckRunId, query.CheckRunId, StringComparison.OrdinalIgnoreCase));
            if (!String.IsNullOrWhiteSpace(query.DeploymentId))
                filtered = filtered.Where(item => String.Equals(item.DeploymentId, query.DeploymentId, StringComparison.OrdinalIgnoreCase));
            if (!String.IsNullOrWhiteSpace(query.ReleaseId))
                filtered = filtered.Where(item => String.Equals(item.ReleaseId, query.ReleaseId, StringComparison.OrdinalIgnoreCase));
            if (!String.IsNullOrWhiteSpace(query.MissionId))
                filtered = filtered.Where(item => String.Equals(item.MissionId, query.MissionId, StringComparison.OrdinalIgnoreCase));
            if (!String.IsNullOrWhiteSpace(query.VoyageId))
                filtered = filtered.Where(item => String.Equals(item.VoyageId, query.VoyageId, StringComparison.OrdinalIgnoreCase));
            if (query.Status.HasValue)
                filtered = filtered.Where(item => item.Status == query.Status.Value);
            if (query.Severity.HasValue)
                filtered = filtered.Where(item => item.Severity == query.Severity.Value);

            string? search = Normalize(query.Search);
            if (!String.IsNullOrWhiteSpace(search))
            {
                filtered = filtered.Where(item =>
                    ContainsIgnoreCase(item.Title, search)
                    || ContainsIgnoreCase(item.Summary, search)
                    || ContainsIgnoreCase(item.Impact, search)
                    || ContainsIgnoreCase(item.RootCause, search)
                    || ContainsIgnoreCase(item.RecoveryNotes, search)
                    || ContainsIgnoreCase(item.Postmortem, search)
                    || ContainsIgnoreCase(item.EnvironmentName, search));
            }

            if (query.ExcludeTerminal)
                filtered = filtered.Where(item => item.Status != IncidentStatusEnum.Closed && item.Status != IncidentStatusEnum.RolledBack);

            List<Incident> ordered;
            if (query.OldestFirst)
            {
                if (query.AfterLastUpdateUtc.HasValue)
                {
                    DateTime afterUtc = query.AfterLastUpdateUtc.Value;
                    string afterId = query.AfterId ?? String.Empty;
                    filtered = filtered.Where(item =>
                        item.LastUpdateUtc > afterUtc
                        || (item.LastUpdateUtc == afterUtc && String.CompareOrdinal(item.Id, afterId) > 0));
                }

                ordered = filtered
                    .OrderBy(item => item.LastUpdateUtc)
                    .ThenBy(item => item.Id, StringComparer.Ordinal)
                    .ToList();
            }
            else
            {
                ordered = filtered
                    .OrderByDescending(item => item.LastUpdateUtc)
                    .ThenByDescending(item => item.Id, StringComparer.Ordinal)
                    .ToList();
            }

            int pageSize = query.PageSize < 1 ? 50 : Math.Min(query.PageSize, 500);
            int pageNumber = query.PageNumber < 1 ? 1 : query.PageNumber;
            int offset = (pageNumber - 1) * pageSize;
            List<Incident> page = ordered.Skip(offset).Take(pageSize).ToList();

            return new EnumerationResult<Incident>
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
        /// Enumerate every non-terminal incident (not Closed and not RolledBack) that matches the
        /// query's filters. Terminal incidents are filtered out before paging and every page is
        /// visited, so newer closed incidents can never hide an older active one.
        /// </summary>
        /// <param name="auth">Caller context.</param>
        /// <param name="query">Filters to apply. Paging and the terminal filter are set by this method.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Every matching active incident, newest first.</returns>
        public async Task<List<Incident>> EnumerateActiveAsync(
            AuthContext auth,
            IncidentQuery query,
            CancellationToken token = default)
        {
            if (auth == null) throw new ArgumentNullException(nameof(auth));
            if (query == null) throw new ArgumentNullException(nameof(query));

            query.ExcludeTerminal = true;
            query.OldestFirst = false;
            query.PageSize = 500;
            query.PageNumber = 1;
            List<Incident> active = new List<Incident>();
            while (true)
            {
                EnumerationResult<Incident> page = await EnumerateAsync(auth, query, token).ConfigureAwait(false);
                active.AddRange(page.Objects);
                if (page.Objects.Count == 0 || page.PageNumber >= page.TotalPages) return active;
                query.PageNumber++;
            }
        }

        /// <summary>
        /// Read one incident.
        /// </summary>
        public async Task<Incident?> ReadAsync(AuthContext auth, string id, CancellationToken token = default)
        {
            if (auth == null) throw new ArgumentNullException(nameof(auth));
            if (String.IsNullOrWhiteSpace(id)) throw new ArgumentNullException(nameof(id));

            List<ArmadaEvent> snapshots = await ReadIncidentSnapshotEventsAsync(auth, id, token).ConfigureAwait(false);
            return ProjectLatestIncident(snapshots);
        }

        /// <summary>
        /// Create an incident.
        /// </summary>
        public async Task<Incident> CreateAsync(AuthContext auth, IncidentUpsertRequest request, CancellationToken token = default)
        {
            if (auth == null) throw new ArgumentNullException(nameof(auth));
            if (request == null) throw new ArgumentNullException(nameof(request));

            Incident incident = new Incident
            {
                TenantId = auth.IsAdmin ? null : auth.TenantId,
                UserId = auth.UserId,
                Title = NormalizeRequired(request.Title, nameof(request.Title)),
                Summary = Normalize(request.Summary),
                Status = request.Status ?? IncidentStatusEnum.Open,
                Severity = request.Severity ?? IncidentSeverityEnum.High,
                EnvironmentId = Normalize(request.EnvironmentId),
                EnvironmentName = Normalize(request.EnvironmentName),
                CheckRunId = Normalize(request.CheckRunId),
                DeploymentId = Normalize(request.DeploymentId),
                ReleaseId = Normalize(request.ReleaseId),
                VesselId = Normalize(request.VesselId),
                MissionId = Normalize(request.MissionId),
                VoyageId = Normalize(request.VoyageId),
                RegressionPurpose = request.RegressionPurpose ?? RegressionPurposeEnum.None,
                RegressionCause = request.RegressionCause ?? RegressionCauseEnum.Unclassified,
                RegressionObjectiveId = RegressionLinkRules.NormalizeObjectiveId(request.RegressionObjectiveId),
                RegressionLandedCommit = RegressionLinkRules.NormalizeCommit(request.RegressionLandedCommit),
                RollbackDeploymentId = Normalize(request.RollbackDeploymentId),
                Impact = Normalize(request.Impact),
                RootCause = Normalize(request.RootCause),
                RecoveryNotes = Normalize(request.RecoveryNotes),
                Postmortem = Normalize(request.Postmortem),
                DetectedUtc = request.DetectedUtc?.ToUniversalTime() ?? DateTime.UtcNow,
                MitigatedUtc = request.MitigatedUtc?.ToUniversalTime(),
                ClosedUtc = request.ClosedUtc?.ToUniversalTime(),
                LastUpdateUtc = DateTime.UtcNow
            };

            RegressionLinkRules.RequirePurposeForLinks(incident.RegressionPurpose, incident.RegressionCause, incident.RegressionObjectiveId, incident.RegressionLandedCommit);
            ApplyLifecycleTimestamps(incident);
            await WriteSnapshotAsync(auth, incident, token).ConfigureAwait(false);
            OnIncidentChanged?.Invoke(incident);
            return incident;
        }

        /// <summary>
        /// Update an incident.
        /// </summary>
        public async Task<Incident> UpdateAsync(AuthContext auth, string id, IncidentUpsertRequest request, CancellationToken token = default)
        {
            if (auth == null) throw new ArgumentNullException(nameof(auth));
            if (String.IsNullOrWhiteSpace(id)) throw new ArgumentNullException(nameof(id));
            if (request == null) throw new ArgumentNullException(nameof(request));

            Incident incident = await ReadAsync(auth, id, token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Incident not found.");

            incident.Title = Normalize(request.Title) ?? incident.Title;
            incident.Summary = request.Summary != null ? Normalize(request.Summary) : incident.Summary;
            incident.Status = request.Status ?? incident.Status;
            incident.Severity = request.Severity ?? incident.Severity;
            incident.EnvironmentId = request.EnvironmentId != null ? Normalize(request.EnvironmentId) : incident.EnvironmentId;
            incident.EnvironmentName = request.EnvironmentName != null ? Normalize(request.EnvironmentName) : incident.EnvironmentName;
            incident.CheckRunId = request.CheckRunId != null ? Normalize(request.CheckRunId) : incident.CheckRunId;
            incident.DeploymentId = request.DeploymentId != null ? Normalize(request.DeploymentId) : incident.DeploymentId;
            incident.ReleaseId = request.ReleaseId != null ? Normalize(request.ReleaseId) : incident.ReleaseId;
            incident.VesselId = request.VesselId != null ? Normalize(request.VesselId) : incident.VesselId;
            incident.MissionId = request.MissionId != null ? Normalize(request.MissionId) : incident.MissionId;
            incident.VoyageId = request.VoyageId != null ? Normalize(request.VoyageId) : incident.VoyageId;
            incident.RegressionPurpose = request.RegressionPurpose ?? incident.RegressionPurpose;
            incident.RegressionCause = request.RegressionCause ?? incident.RegressionCause;
            incident.RegressionObjectiveId = request.RegressionObjectiveId != null ? RegressionLinkRules.NormalizeObjectiveId(request.RegressionObjectiveId) : incident.RegressionObjectiveId;
            incident.RegressionLandedCommit = request.RegressionLandedCommit != null ? RegressionLinkRules.NormalizeCommit(request.RegressionLandedCommit) : incident.RegressionLandedCommit;
            incident.RollbackDeploymentId = request.RollbackDeploymentId != null ? Normalize(request.RollbackDeploymentId) : incident.RollbackDeploymentId;
            incident.Impact = request.Impact != null ? Normalize(request.Impact) : incident.Impact;
            incident.RootCause = request.RootCause != null ? Normalize(request.RootCause) : incident.RootCause;
            incident.RecoveryNotes = request.RecoveryNotes != null ? Normalize(request.RecoveryNotes) : incident.RecoveryNotes;
            incident.Postmortem = request.Postmortem != null ? Normalize(request.Postmortem) : incident.Postmortem;
            incident.MitigatedUtc = request.MitigatedUtc.HasValue ? request.MitigatedUtc.Value.ToUniversalTime() : incident.MitigatedUtc;
            incident.ClosedUtc = request.ClosedUtc.HasValue ? request.ClosedUtc.Value.ToUniversalTime() : incident.ClosedUtc;
            incident.LastUpdateUtc = DateTime.UtcNow;

            RegressionLinkRules.RequirePurposeForLinks(incident.RegressionPurpose, incident.RegressionCause, incident.RegressionObjectiveId, incident.RegressionLandedCommit);
            ApplyLifecycleTimestamps(incident);
            await WriteSnapshotAsync(auth, incident, token).ConfigureAwait(false);
            OnIncidentChanged?.Invoke(incident);
            return incident;
        }

        /// <summary>
        /// Refuse a caller's incident create or update that links a delivery record the caller may not see.
        /// The incident lifecycle reads each linked record by id to move the incident and to write its
        /// recovery notes, so every caller-facing create and update surface calls this before writing. A
        /// global administrator may link any id. Only links the request supplies are checked.
        /// </summary>
        /// <param name="auth">Caller.</param>
        /// <param name="request">Incident create or update request.</param>
        /// <param name="token">Cancellation token.</param>
        /// <exception cref="InvalidOperationException">A supplied link names a record the caller may not see; the message names it.</exception>
        public async Task EnsureCallerLinksVisibleAsync(AuthContext auth, IncidentUpsertRequest request, CancellationToken token = default)
        {
            if (auth == null) throw new ArgumentNullException(nameof(auth));
            if (request == null) throw new ArgumentNullException(nameof(request));
            string? unreachable = await FindUnreachableLinkAsync(auth, request, token).ConfigureAwait(false);
            if (unreachable != null) throw new InvalidOperationException(unreachable);
        }

        private async Task<string?> FindUnreachableLinkAsync(AuthContext auth, IncidentUpsertRequest request, CancellationToken token)
        {
            if (auth.IsAdmin) return null;

            if (Supplied(request.DeploymentId)
                && await CallerScopedRead.ReadDeploymentAsync(_Database, auth, request.DeploymentId, token).ConfigureAwait(false) == null)
                return "Deployment not found: " + request.DeploymentId;
            if (Supplied(request.RollbackDeploymentId)
                && await CallerScopedRead.ReadDeploymentAsync(_Database, auth, request.RollbackDeploymentId, token).ConfigureAwait(false) == null)
                return "Rollback deployment not found: " + request.RollbackDeploymentId;
            if (Supplied(request.ReleaseId)
                && await CallerScopedRead.ReadReleaseAsync(_Database, auth, request.ReleaseId, token).ConfigureAwait(false) == null)
                return "Release not found: " + request.ReleaseId;
            if (Supplied(request.CheckRunId)
                && await CallerScopedRead.ReadCheckRunAsync(_Database, auth, request.CheckRunId, token).ConfigureAwait(false) == null)
                return "Check run not found: " + request.CheckRunId;
            if (Supplied(request.EnvironmentId)
                && await CallerScopedRead.ReadEnvironmentAsync(_Database, auth, request.EnvironmentId, token).ConfigureAwait(false) == null)
                return "Environment not found: " + request.EnvironmentId;
            if (Supplied(request.VesselId)
                && await CallerScopedRead.ReadVesselAsync(_Database, auth, request.VesselId, token).ConfigureAwait(false) == null)
                return "Vessel not found: " + request.VesselId;
            if (Supplied(request.MissionId)
                && await CallerScopedRead.ReadMissionAsync(_Database, auth, request.MissionId, token).ConfigureAwait(false) == null)
                return "Mission not found: " + request.MissionId;
            if (Supplied(request.VoyageId)
                && await CallerScopedRead.ReadVoyageAsync(_Database, auth, request.VoyageId, token).ConfigureAwait(false) == null)
                return "Voyage not found: " + request.VoyageId;
            string? regressionObjectiveId = Supplied(request.RegressionObjectiveId) ? RegressionLinkRules.NormalizeObjectiveId(request.RegressionObjectiveId) : null;
            if (regressionObjectiveId != null
                && await CallerScopedRead.ReadObjectiveAsync(_Database, auth, regressionObjectiveId, token).ConfigureAwait(false) == null)
                return "Regression objective not found: " + regressionObjectiveId;
            return null;
        }

        private static bool Supplied(string? id)
        {
            return !String.IsNullOrWhiteSpace(id);
        }

        /// <summary>
        /// Delete an incident and all of its snapshots.
        /// </summary>
        public async Task DeleteAsync(AuthContext auth, string id, CancellationToken token = default)
        {
            if (auth == null) throw new ArgumentNullException(nameof(auth));
            if (String.IsNullOrWhiteSpace(id)) throw new ArgumentNullException(nameof(id));

            List<ArmadaEvent> snapshots = await ReadIncidentSnapshotEventsAsync(auth, id, token).ConfigureAwait(false);
            if (snapshots.Count == 0)
                throw new InvalidOperationException("Incident not found.");

            foreach (ArmadaEvent snapshot in snapshots)
            {
                await DeleteEventAsync(auth, snapshot.Id, token).ConfigureAwait(false);
            }
        }

        private async Task<List<Incident>> ReadAllIncidentsAsync(AuthContext auth, CancellationToken token)
        {
            List<ArmadaEvent> snapshots = await ReadIncidentSnapshotEventsAsync(auth, null, token).ConfigureAwait(false);
            Dictionary<string, ArmadaEvent> latestByIncidentId = new Dictionary<string, ArmadaEvent>(StringComparer.OrdinalIgnoreCase);

            foreach (ArmadaEvent snapshot in snapshots)
            {
                if (String.IsNullOrWhiteSpace(snapshot.EntityId))
                    continue;

                if (!latestByIncidentId.TryGetValue(snapshot.EntityId, out ArmadaEvent? existing)
                    || IsSnapshotNewer(snapshot, existing))
                {
                    latestByIncidentId[snapshot.EntityId] = snapshot;
                }
            }

            List<Incident> incidents = new List<Incident>();
            foreach (ArmadaEvent snapshot in latestByIncidentId.Values)
            {
                Incident? incident = DeserializeIncident(snapshot);
                if (incident != null)
                    incidents.Add(incident);
            }

            return incidents;
        }

        private async Task<List<ArmadaEvent>> ReadIncidentSnapshotEventsAsync(
            AuthContext auth,
            string? incidentId,
            CancellationToken token)
        {
            if (!String.IsNullOrWhiteSpace(incidentId))
            {
                if (auth.IsAdmin)
                    return (await _Database.Events.EnumerateByEntityAsync("incident", incidentId, 500, token).ConfigureAwait(false))
                        .Where(IsIncidentSnapshotEvent)
                        .ToList();
                if (auth.IsTenantAdmin)
                    return (await _Database.Events.EnumerateByEntityAsync(auth.TenantId!, "incident", incidentId, 500, token).ConfigureAwait(false))
                        .Where(IsIncidentSnapshotEvent)
                        .ToList();
                return (await _Database.Events.EnumerateAsync(auth.TenantId!, auth.UserId!, new EnumerationQuery
                {
                    PageNumber = 1,
                    PageSize = 500
                }, token).ConfigureAwait(false)).Objects
                    .Where(item =>
                        String.Equals(item.EntityType, "incident", StringComparison.OrdinalIgnoreCase)
                        && String.Equals(item.EntityId, incidentId, StringComparison.OrdinalIgnoreCase)
                        && String.Equals(item.EventType, "incident.snapshot", StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            EnumerationQuery query = new EnumerationQuery
            {
                PageNumber = 1,
                PageSize = 500,
                EventType = "incident.snapshot"
            };

            List<ArmadaEvent> results = new List<ArmadaEvent>();
            while (true)
            {
                EnumerationResult<ArmadaEvent> page;
                if (auth.IsAdmin)
                    page = await _Database.Events.EnumerateAsync(query, token).ConfigureAwait(false);
                else if (auth.IsTenantAdmin)
                    page = await _Database.Events.EnumerateAsync(auth.TenantId!, query, token).ConfigureAwait(false);
                else
                    page = await _Database.Events.EnumerateAsync(auth.TenantId!, auth.UserId!, query, token).ConfigureAwait(false);

                results.AddRange(page.Objects.Where(item => String.Equals(item.EntityType, "incident", StringComparison.OrdinalIgnoreCase)));
                if (page.Objects.Count < query.PageSize)
                    break;
                query.PageNumber += 1;
            }

            return results;
        }

        private async Task WriteSnapshotAsync(AuthContext auth, Incident incident, CancellationToken token)
        {
            ArmadaEvent snapshot = new ArmadaEvent("incident.snapshot", incident.Title)
            {
                TenantId = incident.TenantId,
                UserId = auth.UserId,
                EntityType = "incident",
                EntityId = incident.Id,
                MissionId = incident.MissionId,
                VesselId = incident.VesselId,
                VoyageId = incident.VoyageId,
                Payload = JsonSerializer.Serialize(incident, _JsonOptions),
                CreatedUtc = incident.LastUpdateUtc
            };

            await _Database.Events.CreateAsync(snapshot, token).ConfigureAwait(false);
        }

        private async Task DeleteEventAsync(AuthContext auth, string id, CancellationToken token)
        {
            if (auth.IsAdmin)
                await _Database.Events.DeleteAsync(id, token).ConfigureAwait(false);
            else if (auth.IsTenantAdmin)
                await _Database.Events.DeleteAsync(auth.TenantId!, id, token).ConfigureAwait(false);
            else
                await _Database.Events.DeleteAsync(auth.TenantId!, auth.UserId!, id, token).ConfigureAwait(false);
        }

        private static Incident? ProjectLatestIncident(List<ArmadaEvent> snapshots)
        {
            ArmadaEvent? latest = snapshots
                .OrderByDescending(item => item.CreatedUtc)
                .ThenByDescending(item => item.Id, StringComparer.Ordinal)
                .FirstOrDefault();
            return latest != null ? DeserializeIncident(latest) : null;
        }

        private static Incident? DeserializeIncident(ArmadaEvent snapshot)
        {
            if (String.IsNullOrWhiteSpace(snapshot.Payload))
                return null;

            Incident? incident = JsonSerializer.Deserialize<Incident>(snapshot.Payload, _JsonOptions);
            if (incident == null)
                return null;

            incident.TenantId = incident.TenantId ?? snapshot.TenantId;
            incident.UserId = incident.UserId ?? snapshot.UserId;
            incident.LastUpdateUtc = incident.LastUpdateUtc == default ? snapshot.CreatedUtc : incident.LastUpdateUtc;
            return incident;
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

        private static bool IsIncidentSnapshotEvent(ArmadaEvent item)
        {
            return String.Equals(item.EntityType, "incident", StringComparison.OrdinalIgnoreCase)
                && String.Equals(item.EventType, "incident.snapshot", StringComparison.OrdinalIgnoreCase);
        }

        private static void ApplyLifecycleTimestamps(Incident incident)
        {
            if (incident.Status == IncidentStatusEnum.Mitigated && !incident.MitigatedUtc.HasValue)
                incident.MitigatedUtc = DateTime.UtcNow;
            if ((incident.Status == IncidentStatusEnum.Closed || incident.Status == IncidentStatusEnum.RolledBack)
                && !incident.ClosedUtc.HasValue)
            {
                incident.ClosedUtc = DateTime.UtcNow;
            }
        }

        private static bool ContainsIgnoreCase(string? value, string? search)
        {
            if (String.IsNullOrWhiteSpace(value) || String.IsNullOrWhiteSpace(search))
                return false;
            return value.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string? Normalize(string? value)
        {
            return String.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        private static string NormalizeRequired(string? value, string parameterName)
        {
            string? normalized = Normalize(value);
            if (String.IsNullOrWhiteSpace(normalized))
                throw new InvalidOperationException(parameterName + " is required.");
            return normalized;
        }
    }
}
