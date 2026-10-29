namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Runtime.CompilerServices;
    using Armada.Core.Authorization;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// Incident records, kept as snapshot events and read from their projection, tied to the existing deployment and release lifecycle.
    /// </summary>
    public class IncidentService
    {
        /// <summary>
        /// Event type of an incident snapshot. Every change to an incident is written as one of these events,
        /// and the newest snapshot of an incident is its current record; the incident projection holds that
        /// record for reads.
        /// </summary>
        public const string SnapshotEventType = "incident.snapshot";

        /// <summary>Entity type an incident snapshot carries.</summary>
        public const string IncidentEntityType = "incident";

        /// <summary>
        /// Optional callback invoked whenever an incident changes.
        /// </summary>
        public Action<Incident>? OnIncidentChanged { get; set; }

        private readonly DatabaseDriver _Database;
        private static readonly ConditionalWeakTable<DatabaseDriver, SemaphoreSlim> _WriteLocks = new ConditionalWeakTable<DatabaseDriver, SemaphoreSlim>();

        /// <summary>
        /// Instantiate.
        /// </summary>
        public IncidentService(DatabaseDriver database)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
        }

        /// <summary>
        /// Count the incidents visible to the caller by status, and the open ones by severity. The counts
        /// equal the totals of <see cref="EnumerateAsync"/> filtered on each value, and are computed in the
        /// database.
        /// </summary>
        public async Task<IncidentCountSummary> CountAsync(AuthContext auth, CancellationToken token = default)
        {
            if (auth == null) throw new ArgumentNullException(nameof(auth));

            return await _Database.Incidents.CountAsync(ScopeOf(auth), token).ConfigureAwait(false);
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

            return await _Database.Incidents.EnumerateAsync(ScopeOf(auth), query, token).ConfigureAwait(false);
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

            return await _Database.Incidents.ReadAsync(ScopeOf(auth), id, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Create an incident.
        /// </summary>
        public Task<Incident> CreateAsync(AuthContext auth, IncidentUpsertRequest request, CancellationToken token = default)
        {
            return CreateCoreAsync(auth, request, false, token);
        }

        /// <summary>
        /// Create an incident from a system observation. Its durable origin permits lifecycle
        /// supersession only by a later cancellation; caller-facing tools do not expose this path.
        /// </summary>
        public Task<Incident> CreateAutomaticallyAsync(AuthContext auth, IncidentUpsertRequest request, CancellationToken token = default)
        {
            return CreateCoreAsync(auth, request, true, token);
        }

        private async Task<Incident> CreateCoreAsync(AuthContext auth, IncidentUpsertRequest request, bool automatic, CancellationToken token)
        {
            if (auth == null) throw new ArgumentNullException(nameof(auth));
            if (request == null) throw new ArgumentNullException(nameof(request));

            Incident incident = new Incident
            {
                OpenedAutomatically = automatic,
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

            // The root cause an incident opens with is its opened reason, not a verified cause. An
            // incident created already Closed is closed by its creator, so its cause must be written.
            if (incident.Status == IncidentStatusEnum.Closed)
            {
                string? refusal = IncidentRootCauseRule.Evaluate(incident.RootCause, null);
                if (refusal != null) throw new IncidentRootCauseRefusedException(refusal);
                StampRootCauseAuthor(auth, incident);
            }
            else
            {
                incident.OpenedReason = incident.RootCause;
            }

            ApplyLifecycleTimestamps(incident);
            await WriteSnapshotAsync(auth, incident, token).ConfigureAwait(false);
            OnIncidentChanged?.Invoke(incident);
            return incident;
        }

        /// <summary>
        /// Update an incident on behalf of a person or operator. Every caller-facing surface uses this
        /// method. A supplied root cause that differs from the opened reason is recorded as written by
        /// the caller. Closing the incident, or changing the root cause of a closed incident, is refused
        /// with <see cref="IncidentRootCauseRefusedException"/> unless the root cause in force is one the
        /// caller wrote (<see cref="IncidentRootCauseRule"/>).
        /// </summary>
        public async Task<Incident> UpdateAsync(AuthContext auth, string id, IncidentUpsertRequest request, CancellationToken token = default)
        {
            return await UpdateCoreAsync(auth, id, request, false, null, token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Incident update was superseded.");
        }

        /// <summary>
        /// Update an incident from a system path (incident lifecycle, autonomous recovery). The root-cause
        /// rule does not apply, a close or rollback is recorded as automatic, and any root cause the
        /// request supplies is recorded as automatic, never as person-written.
        /// </summary>
        public async Task<Incident> UpdateAutomaticallyAsync(AuthContext auth, string id, IncidentUpsertRequest request, CancellationToken token = default)
        {
            return await UpdateCoreAsync(auth, id, request, true, null, token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Incident update was superseded.");
        }

        /// <summary>
        /// Apply a lifecycle decision only while the observed snapshot is current. A stale decision
        /// returns null. The comparison and snapshot write share the caller-update lock.
        /// </summary>
        public Task<Incident?> UpdateAutomaticallyIfUnchangedAsync(AuthContext auth, Incident observed, IncidentUpsertRequest request, CancellationToken token = default)
        {
            if (observed == null) throw new ArgumentNullException(nameof(observed));
            return UpdateCoreAsync(auth, observed.Id, request, true, observed.LastUpdateUtc, token);
        }

        private async Task<Incident?> UpdateCoreAsync(AuthContext auth, string id, IncidentUpsertRequest request, bool automatic, DateTime? expectedUpdateUtc, CancellationToken token)
        {
            SemaphoreSlim writeLock = _WriteLocks.GetValue(_Database, _ => new SemaphoreSlim(1, 1));
            await writeLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                return await UpdateLockedAsync(auth, id, request, automatic, expectedUpdateUtc, token).ConfigureAwait(false);
            }
            finally
            {
                writeLock.Release();
            }
        }

        private async Task<Incident?> UpdateLockedAsync(AuthContext auth, string id, IncidentUpsertRequest request, bool automatic, DateTime? expectedUpdateUtc, CancellationToken token)
        {
            if (auth == null) throw new ArgumentNullException(nameof(auth));
            if (String.IsNullOrWhiteSpace(id)) throw new ArgumentNullException(nameof(id));
            if (request == null) throw new ArgumentNullException(nameof(request));

            Incident incident = await ReadAsync(auth, id, token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Incident not found.");
            if (expectedUpdateUtc.HasValue && incident.LastUpdateUtc != expectedUpdateUtc.Value) return null;
            IncidentStatusEnum previousStatus = incident.Status;
            string? previousRootCause = incident.RootCause;

            // Every automatic writer acts on an incident it read as active. When the incident is terminal
            // by the time the write lands (an operator closed it in between), the stale write must neither
            // reopen it nor replace the operator's cause and notes; the caller sees the unchanged incident.
            if (automatic && IsTerminal(previousStatus)) return incident;

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
            // A new mitigation acknowledges all failure evidence observed before this transition.
            // Retaining an earlier mitigation time lets the lifecycle sweep reopen it immediately.
            if (incident.Status == IncidentStatusEnum.Mitigated
                && previousStatus != IncidentStatusEnum.Mitigated
                && !request.MitigatedUtc.HasValue)
            {
                incident.MitigatedUtc = DateTime.UtcNow;
            }
            incident.ClosedUtc = request.ClosedUtc.HasValue ? request.ClosedUtc.Value.ToUniversalTime() : incident.ClosedUtc;
            DateTime now = DateTime.UtcNow;
            incident.LastUpdateUtc = now > incident.LastUpdateUtc ? now : incident.LastUpdateUtc.AddTicks(1);
            if (!automatic && request.Status.HasValue && !IsTerminal(incident.Status))
                incident.LastOperatorStatusUtc = incident.LastUpdateUtc;

            // The text an incident opened with is an automatic reading, not a determined cause. A system
            // path that closes or rolls back the incident therefore leaves the root cause unset rather than
            // closing on that text; OpenedReason keeps it.
            if (automatic
                && IsTerminal(incident.Status)
                && incident.RootCauseWrittenBy == null
                && IsOpenedReasonText(incident.RootCause, incident.OpenedReason))
            {
                incident.RootCause = null;
            }

            RegressionLinkRules.RequirePurposeForLinks(incident.RegressionPurpose, incident.RegressionCause, incident.RegressionObjectiveId, incident.RegressionLandedCommit);
            if (!automatic && IsTerminal(incident.Status) && !IsTerminal(previousStatus))
                await RefuseCloseWhileCriterionReviewHeldAsync(incident, token).ConfigureAwait(false);
            ApplyRootCauseAuthorship(auth, incident, previousStatus, previousRootCause, request.RootCause != null, automatic);
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

            if (!await _Database.Incidents.DeleteAsync(ScopeOf(auth), id, token).ConfigureAwait(false))
                throw new InvalidOperationException("Incident not found.");
        }

        // Operators see every incident, or every incident of their tenant; every other caller sees the
        // incidents it owns.
        private static IncidentReadScope ScopeOf(AuthContext auth)
        {
            bool operatorScope = String.IsNullOrWhiteSpace(auth.MissionId);
            if (operatorScope && auth.IsAdmin) return IncidentReadScope.All();
            if (operatorScope && auth.IsTenantAdmin) return IncidentReadScope.Tenant(auth.TenantId);
            return IncidentReadScope.User(auth.TenantId, auth.UserId);
        }

        private async Task WriteSnapshotAsync(AuthContext auth, Incident incident, CancellationToken token)
        {
            ArmadaEvent snapshot = new ArmadaEvent(SnapshotEventType, incident.Title)
            {
                TenantId = incident.TenantId,
                UserId = auth.UserId,
                EntityType = IncidentEntityType,
                EntityId = incident.Id,
                MissionId = incident.MissionId,
                VesselId = incident.VesselId,
                VoyageId = incident.VoyageId,
                Payload = IncidentSnapshotCodec.Serialize(incident),
                CreatedUtc = incident.LastUpdateUtc
            };

            await _Database.Incidents.WriteSnapshotAsync(snapshot, incident, token).ConfigureAwait(false);
        }

        // A criterion-review incident stands for a Judge verdict held for an operator ruling. Closing it by hand would
        // leave the mission held with no incident to point at, so the close is refused while the hold names it; the
        // ruling (armada_review_hold clear or fail) closes the incident itself.
        private async Task RefuseCloseWhileCriterionReviewHeldAsync(Incident incident, CancellationToken token)
        {
            if (String.IsNullOrWhiteSpace(incident.MissionId)) return;
            Mission? mission = await _Database.Missions.ReadAsync(incident.MissionId!, token).ConfigureAwait(false);
            if (!JudgeCriterionConflicts.IsHeld(mission)) return;
            if (!String.Equals(JudgeCriterionConflicts.ReadIncidentId(mission!.HeldForOperatorReviewReason), incident.Id, StringComparison.Ordinal)) return;
            throw new InvalidOperationException(JudgeCriterionConflicts.IncidentCloseRefusedCode + ": mission " + mission.Id
                + " is still held for an operator ruling on this criterion conflict. Rule with armada_review_hold (clear lands the work, "
                + "fail ends it Failed with no rescue); the ruling closes this incident.");
        }

        private static void ApplyRootCauseAuthorship(
            AuthContext auth,
            Incident incident,
            IncidentStatusEnum previousStatus,
            string? previousRootCause,
            bool rootCauseSupplied,
            bool automatic)
        {
            bool rootCauseChanged = !String.Equals(incident.RootCause, previousRootCause, StringComparison.Ordinal);
            if (rootCauseChanged)
            {
                incident.RootCauseWrittenBy = null;
                incident.RootCauseWrittenUtc = null;
                if (!automatic && IncidentRootCauseRule.Evaluate(incident.RootCause, incident.OpenedReason) == null)
                    StampRootCauseAuthor(auth, incident);
            }

            bool wasTerminal = IsTerminal(previousStatus);
            bool isTerminal = IsTerminal(incident.Status);
            if (!isTerminal)
                incident.ClosedAutomatically = false;
            else if (!wasTerminal || previousStatus != incident.Status)
                incident.ClosedAutomatically = automatic;

            if (automatic || incident.Status != IncidentStatusEnum.Closed) return;
            if (previousStatus == IncidentStatusEnum.Closed && !rootCauseChanged) return;

            // A cause is person-written only once it is stamped, and it is stamped only when it passes
            // the rule. A close that supplies no cause over an automatic one therefore names the missing
            // cause; a supplied cause that fails names the rule's own reason.
            if (incident.RootCauseWrittenBy != null) return;
            string refusal = rootCauseSupplied
                ? IncidentRootCauseRule.Evaluate(incident.RootCause, incident.OpenedReason) ?? IncidentRootCauseRule.RequiredCode
                : IncidentRootCauseRule.RequiredCode;
            throw new IncidentRootCauseRefusedException(refusal);
        }

        private static void StampRootCauseAuthor(AuthContext auth, Incident incident)
        {
            incident.RootCauseWrittenBy = Normalize(auth.UserId) ?? Normalize(auth.PrincipalDisplay) ?? Normalize(auth.CredentialId) ?? "unknown";
            incident.RootCauseWrittenUtc = DateTime.UtcNow;
        }

        private static bool IsOpenedReasonText(string? rootCause, string? openedReason)
        {
            if (String.IsNullOrWhiteSpace(rootCause)) return false;
            return String.Equals(
                IncidentRootCauseRule.Evaluate(rootCause, openedReason),
                IncidentRootCauseRule.UnchangedCode,
                StringComparison.Ordinal);
        }

        private static bool IsTerminal(IncidentStatusEnum status)
        {
            return status == IncidentStatusEnum.Closed || status == IncidentStatusEnum.RolledBack;
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
