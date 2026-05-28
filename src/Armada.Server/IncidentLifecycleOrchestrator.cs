namespace Armada.Server
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Json;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>
    /// Moves incidents through mitigation, rollback, reopening, and closure from Armada evidence.
    /// </summary>
    public sealed class IncidentLifecycleOrchestrator
    {
        private const string _Header = "[IncidentLifecycleOrchestrator] ";
        private readonly DatabaseDriver _Database;
        private readonly IncidentService _Incidents;
        private readonly ArmadaSettings _Settings;
        private readonly LoggingModule _Logging;
        private readonly SemaphoreSlim _SweepGate = new SemaphoreSlim(1, 1);
        private readonly JsonSerializerOptions _JsonOptions = JsonDefaults.Web;

        /// <summary>
        /// Instantiate.
        /// </summary>
        public IncidentLifecycleOrchestrator(
            DatabaseDriver database,
            IncidentService incidents,
            ArmadaSettings settings,
            LoggingModule logging)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Incidents = incidents ?? throw new ArgumentNullException(nameof(incidents));
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
        }

        /// <summary>
        /// Start a non-blocking lifecycle sweep if one is not already running.
        /// </summary>
        public void TriggerBackgroundSweep(CancellationToken token = default)
        {
            if (!_Settings.IncidentLifecycle.Enabled) return;
            if (!_SweepGate.Wait(0)) return;

            _ = Task.Run(async () =>
            {
                try
                {
                    await RunSweepAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                }
                catch (Exception ex)
                {
                    _Logging.Warn(_Header + "sweep failed: " + ex.Message);
                }
                finally
                {
                    _SweepGate.Release();
                }
            }, token);
        }

        /// <summary>
        /// Execute one bounded incident lifecycle sweep.
        /// </summary>
        public async Task<int> RunSweepAsync(CancellationToken token = default)
        {
            IncidentLifecycleSettings settings = _Settings.IncidentLifecycle;
            if (!settings.Enabled) return 0;

            AuthContext auth = BuildSystemAuth();
            EnumerationResult<Incident> page = await _Incidents.EnumerateAsync(auth, new IncidentQuery
            {
                PageNumber = 1,
                PageSize = Math.Max(1, settings.MaxIncidentsPerSweep)
            }, token).ConfigureAwait(false);

            int changed = 0;
            foreach (Incident incident in page.Objects
                .Where(item => item.Status != IncidentStatusEnum.Closed && item.Status != IncidentStatusEnum.RolledBack)
                .OrderBy(item => item.LastUpdateUtc)
                .Take(settings.MaxIncidentsPerSweep))
            {
                token.ThrowIfCancellationRequested();
                if (await EvaluateIncidentAsync(auth, incident, token).ConfigureAwait(false))
                    changed++;
            }

            return changed;
        }

        private async Task<bool> EvaluateIncidentAsync(AuthContext auth, Incident incident, CancellationToken token)
        {
            IncidentEvidence evidence = await ReadEvidenceAsync(incident, token).ConfigureAwait(false);
            IncidentLifecycleSettings settings = _Settings.IncidentLifecycle;

            if (evidence.Kind == IncidentEvidenceKind.ActiveFailure)
            {
                if (incident.Status == IncidentStatusEnum.Mitigated || incident.Status == IncidentStatusEnum.Monitoring)
                    return await UpdateIncidentAsync(auth, incident, IncidentStatusEnum.Open, MaxSeverity(incident.Severity, IncidentSeverityEnum.High), evidence.Note, "incident.lifecycle_reopened", token).ConfigureAwait(false);

                if (incident.Severity != MaxSeverity(incident.Severity, IncidentSeverityEnum.High)
                    || !ContainsNote(incident.RecoveryNotes, evidence.Note))
                {
                    return await UpdateIncidentAsync(auth, incident, IncidentStatusEnum.Open, MaxSeverity(incident.Severity, IncidentSeverityEnum.High), evidence.Note, "incident.lifecycle_failure_observed", token).ConfigureAwait(false);
                }

                return false;
            }

            if (evidence.Kind == IncidentEvidenceKind.Superseded)
            {
                if (settings.AutoClose && incident.Status != IncidentStatusEnum.Closed)
                    return await UpdateIncidentAsync(auth, incident, IncidentStatusEnum.Closed, incident.Severity, evidence.Note, "incident.lifecycle_superseded", token).ConfigureAwait(false);

                if (settings.AutoMitigate && incident.Status == IncidentStatusEnum.Open)
                    return await UpdateIncidentAsync(auth, incident, IncidentStatusEnum.Mitigated, incident.Severity, evidence.Note, "incident.lifecycle_mitigated", token).ConfigureAwait(false);
            }

            if (evidence.Kind == IncidentEvidenceKind.RolledBack && settings.AutoMitigate)
            {
                if (incident.Status != IncidentStatusEnum.RolledBack)
                    return await UpdateIncidentAsync(auth, incident, IncidentStatusEnum.RolledBack, incident.Severity, evidence.Note, "incident.lifecycle_rolled_back", token).ConfigureAwait(false);
                return false;
            }

            if (evidence.Kind == IncidentEvidenceKind.Mitigated && settings.AutoMitigate)
            {
                if (incident.Status == IncidentStatusEnum.Open)
                    return await UpdateIncidentAsync(auth, incident, IncidentStatusEnum.Mitigated, incident.Severity, evidence.Note, "incident.lifecycle_mitigated", token).ConfigureAwait(false);

                if ((incident.Status == IncidentStatusEnum.Mitigated || incident.Status == IncidentStatusEnum.Monitoring)
                    && settings.AutoClose
                    && QuietPeriodElapsed(incident, settings))
                {
                    return await UpdateIncidentAsync(auth, incident, IncidentStatusEnum.Closed, incident.Severity, "Closed automatically after quiet period. " + evidence.Note, "incident.lifecycle_closed", token).ConfigureAwait(false);
                }
            }

            return false;
        }

        private async Task<IncidentEvidence> ReadEvidenceAsync(Incident incident, CancellationToken token)
        {
            IncidentEvidence? rollback = await ReadRollbackEvidenceAsync(incident, token).ConfigureAwait(false);
            if (rollback.HasValue) return rollback.Value;

            if (!String.IsNullOrWhiteSpace(incident.CheckRunId)
                || incident.Title.StartsWith("Automated check failed:", StringComparison.OrdinalIgnoreCase))
            {
                IncidentEvidence checkEvidence = await ReadCheckEvidenceAsync(incident, token).ConfigureAwait(false);
                if (checkEvidence.Kind != IncidentEvidenceKind.None) return checkEvidence;
            }

            IncidentEvidence deploymentEvidence = await ReadDeploymentEvidenceAsync(incident, token).ConfigureAwait(false);
            if (deploymentEvidence.Kind != IncidentEvidenceKind.None) return deploymentEvidence;

            IncidentEvidence releaseEvidence = await ReadReleaseEvidenceAsync(incident, token).ConfigureAwait(false);
            if (releaseEvidence.Kind != IncidentEvidenceKind.None) return releaseEvidence;

            return await ReadMissionEvidenceAsync(incident, token).ConfigureAwait(false);
        }

        private async Task<IncidentEvidence?> ReadRollbackEvidenceAsync(Incident incident, CancellationToken token)
        {
            Deployment? rollbackDeployment = null;
            if (!String.IsNullOrWhiteSpace(incident.RollbackDeploymentId))
                rollbackDeployment = await _Database.Deployments.ReadAsync(incident.RollbackDeploymentId, token: token).ConfigureAwait(false);
            if (rollbackDeployment?.Status == DeploymentStatusEnum.RolledBack)
                return IncidentEvidence.RolledBack("Rollback deployment completed: " + rollbackDeployment.Id + ".");

            if (!String.IsNullOrWhiteSpace(incident.ReleaseId))
            {
                Release? release = await _Database.Releases.ReadAsync(incident.ReleaseId, token: token).ConfigureAwait(false);
                if (release?.Status == ReleaseStatusEnum.RolledBack)
                    return IncidentEvidence.RolledBack("Linked release was rolled back: " + release.Id + ".");
            }

            return null;
        }

        private async Task<IncidentEvidence> ReadCheckEvidenceAsync(Incident incident, CancellationToken token)
        {
            CheckRun? failed = !String.IsNullOrWhiteSpace(incident.CheckRunId)
                ? await _Database.CheckRuns.ReadAsync(incident.CheckRunId, token: token).ConfigureAwait(false)
                : null;

            CheckRunQuery query = new CheckRunQuery
            {
                TenantId = incident.TenantId,
                VesselId = incident.VesselId ?? failed?.VesselId,
                Type = failed?.Type,
                PageNumber = 1,
                PageSize = 500
            };

            EnumerationResult<CheckRun> page = await _Database.CheckRuns.EnumerateAsync(query, token).ConfigureAwait(false);
            List<CheckRun> candidates = page.Objects
                .Where(run => MatchesIncidentCheck(run, incident, failed))
                .OrderByDescending(CheckSortTime)
                .ToList();

            CheckRun? latest = candidates.FirstOrDefault();
            if (latest == null)
                return IncidentEvidence.None;

            if (latest.Status == CheckRunStatusEnum.Passed)
                return IncidentEvidence.Mitigated("Matching check passed: " + latest.Id + ".");
            if (latest.Status == CheckRunStatusEnum.Failed || latest.Status == CheckRunStatusEnum.Canceled)
            {
                CheckRun? superseding = await FindSupersedingPassedCheckAsync(page.Objects, incident, failed, latest, token).ConfigureAwait(false);
                if (superseding != null)
                {
                    return IncidentEvidence.Superseded("Stale check incident superseded by later same-vessel passing check: " + superseding.Id + ".");
                }

                return IncidentEvidence.ActiveFailure("Latest matching check is " + latest.Status + ": " + latest.Id + ".");
            }

            return IncidentEvidence.ActiveWork("Matching check is still " + latest.Status + ": " + latest.Id + ".");
        }

        private async Task<IncidentEvidence> ReadDeploymentEvidenceAsync(Incident incident, CancellationToken token)
        {
            if (String.IsNullOrWhiteSpace(incident.DeploymentId))
                return IncidentEvidence.None;

            Deployment? deployment = await _Database.Deployments.ReadAsync(incident.DeploymentId, token: token).ConfigureAwait(false);
            if (deployment == null) return IncidentEvidence.None;

            if (deployment.Status == DeploymentStatusEnum.RolledBack)
                return IncidentEvidence.RolledBack("Deployment rolled back: " + deployment.Id + ".");
            if (deployment.Status == DeploymentStatusEnum.Succeeded
                && (deployment.VerificationStatus == DeploymentVerificationStatusEnum.Passed
                    || deployment.VerificationStatus == DeploymentVerificationStatusEnum.Skipped))
                return IncidentEvidence.Mitigated("Deployment succeeded with verification status " + deployment.VerificationStatus + ": " + deployment.Id + ".");
            if (deployment.Status == DeploymentStatusEnum.Failed || deployment.Status == DeploymentStatusEnum.VerificationFailed)
                return IncidentEvidence.ActiveFailure("Deployment remains " + deployment.Status + ": " + deployment.Id + ".");

            return IncidentEvidence.ActiveWork("Deployment is still " + deployment.Status + ": " + deployment.Id + ".");
        }

        private async Task<IncidentEvidence> ReadReleaseEvidenceAsync(Incident incident, CancellationToken token)
        {
            if (String.IsNullOrWhiteSpace(incident.ReleaseId))
                return IncidentEvidence.None;

            Release? release = await _Database.Releases.ReadAsync(incident.ReleaseId, token: token).ConfigureAwait(false);
            if (release == null) return IncidentEvidence.None;

            if (release.Status == ReleaseStatusEnum.RolledBack)
                return IncidentEvidence.RolledBack("Release rolled back: " + release.Id + ".");
            if (release.Status == ReleaseStatusEnum.Shipped)
                return IncidentEvidence.Mitigated("Release shipped: " + release.Id + ".");
            if (release.Status == ReleaseStatusEnum.Failed)
                return IncidentEvidence.ActiveFailure("Release remains failed: " + release.Id + ".");

            return IncidentEvidence.None;
        }

        private async Task<IncidentEvidence> ReadMissionEvidenceAsync(Incident incident, CancellationToken token)
        {
            if (String.IsNullOrWhiteSpace(incident.MissionId))
                return IncidentEvidence.None;

            Mission? mission = await _Database.Missions.ReadAsync(incident.MissionId, token).ConfigureAwait(false);
            if (mission == null) return IncidentEvidence.None;

            if (mission.Status == MissionStatusEnum.Complete)
                return IncidentEvidence.Mitigated("Linked mission completed: " + mission.Id + ".");
            if (mission.Status == MissionStatusEnum.Cancelled)
                return IncidentEvidence.Superseded("Linked mission was cancelled, so the incident is superseded: " + mission.Id + ".");
            if (mission.Status != MissionStatusEnum.Failed && mission.Status != MissionStatusEnum.LandingFailed)
                return IncidentEvidence.ActiveWork("Linked mission is still " + mission.Status + ": " + mission.Id + ".");

            if (await IsMissionVoyageCancelledAsync(mission, token).ConfigureAwait(false))
                return IncidentEvidence.Superseded("Linked mission belongs to a cancelled voyage and is superseded: " + mission.Id + ".");

            List<Mission> related = !String.IsNullOrWhiteSpace(mission.VesselId)
                ? await _Database.Missions.EnumerateByVesselAsync(mission.VesselId, token).ConfigureAwait(false)
                : await _Database.Missions.EnumerateAsync(token).ConfigureAwait(false);

            Mission? latestRescue = related
                .Where(item => String.Equals(item.ParentMissionId, mission.Id, StringComparison.Ordinal)
                    && IsAutoRescueMission(item))
                .OrderByDescending(item => item.LastUpdateUtc)
                .FirstOrDefault();

            if (latestRescue == null)
                return IncidentEvidence.ActiveFailure("Linked mission remains " + mission.Status + ": " + mission.Id + ".");
            if (latestRescue.Status == MissionStatusEnum.Complete)
                return IncidentEvidence.Mitigated("Autonomous rescue mission completed: " + latestRescue.Id + ".");
            if (latestRescue.Status == MissionStatusEnum.Cancelled)
                return IncidentEvidence.Superseded("Autonomous rescue mission was cancelled and the incident is superseded: " + latestRescue.Id + ".");
            if (latestRescue.Status == MissionStatusEnum.Failed
                || latestRescue.Status == MissionStatusEnum.LandingFailed)
                return IncidentEvidence.ActiveFailure("Latest rescue mission is " + latestRescue.Status + ": " + latestRescue.Id + ".");

            return IncidentEvidence.ActiveWork("Latest rescue mission is still " + latestRescue.Status + ": " + latestRescue.Id + ".");
        }

        private async Task<CheckRun?> FindSupersedingPassedCheckAsync(
            IReadOnlyList<CheckRun> runs,
            Incident incident,
            CheckRun? failed,
            CheckRun latestStrictMatch,
            CancellationToken token)
        {
            if (!await IsSupersedableCheckIncidentAsync(incident, failed, token).ConfigureAwait(false))
                return null;

            DateTime latestStrictTime = CheckSortTime(latestStrictMatch);
            string? vesselId = incident.VesselId ?? failed?.VesselId;
            string? environmentName = incident.EnvironmentName ?? failed?.EnvironmentName;
            CheckRunTypeEnum? type = failed?.Type;

            return runs
                .Where(run => run.Status == CheckRunStatusEnum.Passed)
                .Where(run => failed == null || !String.Equals(run.Id, failed.Id, StringComparison.OrdinalIgnoreCase))
                .Where(run => !type.HasValue || run.Type == type.Value)
                .Where(run => Matches(run.VesselId, vesselId))
                .Where(run => Matches(run.EnvironmentName, environmentName))
                .Where(run => CheckSortTime(run) > latestStrictTime)
                .OrderByDescending(CheckSortTime)
                .FirstOrDefault();
        }

        private async Task<bool> IsSupersedableCheckIncidentAsync(Incident incident, CheckRun? failed, CancellationToken token)
        {
            if (IsInfrastructureBlockedCheck(incident, failed))
                return true;

            if (!String.IsNullOrWhiteSpace(incident.MissionId))
            {
                Mission? mission = await _Database.Missions.ReadAsync(incident.MissionId, token).ConfigureAwait(false);
                if (mission?.Status == MissionStatusEnum.Cancelled)
                    return true;
            }

            if (!String.IsNullOrWhiteSpace(incident.VoyageId))
            {
                Voyage? voyage = await _Database.Voyages.ReadAsync(incident.VoyageId, token).ConfigureAwait(false);
                if (voyage?.Status == VoyageStatusEnum.Cancelled)
                    return true;
            }

            return false;
        }

        private async Task<bool> IsMissionVoyageCancelledAsync(Mission mission, CancellationToken token)
        {
            if (String.IsNullOrWhiteSpace(mission.VoyageId))
                return false;

            Voyage? voyage = await _Database.Voyages.ReadAsync(mission.VoyageId, token).ConfigureAwait(false);
            return voyage?.Status == VoyageStatusEnum.Cancelled;
        }

        private async Task<bool> UpdateIncidentAsync(
            AuthContext auth,
            Incident incident,
            IncidentStatusEnum status,
            IncidentSeverityEnum severity,
            string note,
            string eventType,
            CancellationToken token)
        {
            DateTime now = DateTime.UtcNow;
            Incident updated = await _Incidents.UpdateAsync(auth, incident.Id, new IncidentUpsertRequest
            {
                Status = status,
                Severity = severity,
                RecoveryNotes = AppendNote(incident.RecoveryNotes, note),
                MitigatedUtc = status == IncidentStatusEnum.Mitigated ? now : null,
                ClosedUtc = status == IncidentStatusEnum.Closed || status == IncidentStatusEnum.RolledBack ? now : null
            }, token).ConfigureAwait(false);

            await EmitEventAsync(eventType, "Incident " + updated.Id + " moved to " + status + ".", updated, note, token).ConfigureAwait(false);
            return true;
        }

        private async Task EmitEventAsync(string eventType, string message, Incident incident, string note, CancellationToken token)
        {
            ArmadaEvent evt = new ArmadaEvent(eventType, message)
            {
                TenantId = incident.TenantId,
                UserId = incident.UserId,
                EntityType = "incident",
                EntityId = incident.Id,
                MissionId = incident.MissionId,
                VesselId = incident.VesselId,
                VoyageId = incident.VoyageId,
                Payload = JsonSerializer.Serialize(new
                {
                    incidentId = incident.Id,
                    incident.Status,
                    incident.Severity,
                    note
                }, _JsonOptions)
            };

            await _Database.Events.CreateAsync(evt, token).ConfigureAwait(false);
        }

        private static bool MatchesIncidentCheck(CheckRun run, Incident incident, CheckRun? failed)
        {
            if (!String.IsNullOrWhiteSpace(incident.CheckRunId)
                && String.Equals(run.Id, incident.CheckRunId, StringComparison.OrdinalIgnoreCase))
                return true;

            if (failed != null && run.Type != failed.Type)
                return false;
            if (failed == null && incident.Title.StartsWith("Automated check failed:", StringComparison.OrdinalIgnoreCase))
            {
                string expectedType = incident.Title.Substring("Automated check failed:".Length).Trim();
                if (!String.IsNullOrWhiteSpace(expectedType)
                    && !String.Equals(run.Label, expectedType, StringComparison.OrdinalIgnoreCase)
                    && !String.Equals(run.Type.ToString(), expectedType, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            if (!Matches(run.VesselId, incident.VesselId ?? failed?.VesselId)) return false;
            if (!Matches(run.MissionId, incident.MissionId ?? failed?.MissionId)) return false;
            if (!Matches(run.VoyageId, incident.VoyageId ?? failed?.VoyageId)) return false;
            if (!Matches(run.DeploymentId, incident.DeploymentId ?? failed?.DeploymentId)) return false;
            if (!Matches(run.EnvironmentName, incident.EnvironmentName ?? failed?.EnvironmentName)) return false;

            DateTime cutoff = incident.DetectedUtc.AddMinutes(-10);
            return CheckSortTime(run) >= cutoff;
        }

        private static bool Matches(string? value, string? expected)
        {
            return String.IsNullOrWhiteSpace(expected)
                || String.Equals(value, expected, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsInfrastructureBlockedCheck(Incident incident, CheckRun? failed)
        {
            string text = String.Join("\n", new[]
            {
                incident.Summary,
                incident.Impact,
                incident.RootCause,
                incident.RecoveryNotes,
                failed?.Summary,
                failed?.Output
            }.Where(value => !String.IsNullOrWhiteSpace(value)));

            if (String.IsNullOrWhiteSpace(text))
                return false;

            string normalized = text.ToLowerInvariant();
            string[] markers =
            {
                "docker",
                "container",
                "daemon",
                "usable working directory",
                "working directory",
                "no such file or directory",
                "cannot find the file",
                "connection refused",
                "timed out",
                "not ready for the requested check run"
            };

            return markers.Any(marker => normalized.Contains(marker, StringComparison.Ordinal));
        }

        private static DateTime CheckSortTime(CheckRun run)
        {
            return run.CompletedUtc ?? run.StartedUtc ?? run.LastUpdateUtc;
        }

        private static bool QuietPeriodElapsed(Incident incident, IncidentLifecycleSettings settings)
        {
            DateTime anchor = incident.MitigatedUtc ?? incident.LastUpdateUtc;
            return DateTime.UtcNow - anchor >= TimeSpan.FromMinutes(settings.CloseQuietPeriodMinutes);
        }

        private static bool ContainsNote(string? existing, string note)
        {
            return !String.IsNullOrWhiteSpace(existing)
                && existing.Contains(note, StringComparison.OrdinalIgnoreCase);
        }

        private static string AppendNote(string? existing, string note)
        {
            if (String.IsNullOrWhiteSpace(existing))
                return note;
            if (existing.Contains(note, StringComparison.OrdinalIgnoreCase))
                return existing;
            return existing.TrimEnd() + Environment.NewLine + Environment.NewLine + note;
        }

        private static IncidentSeverityEnum MaxSeverity(IncidentSeverityEnum current, IncidentSeverityEnum minimum)
        {
            return SeverityRank(current) >= SeverityRank(minimum) ? current : minimum;
        }

        private static int SeverityRank(IncidentSeverityEnum severity)
        {
            return severity switch
            {
                IncidentSeverityEnum.Critical => 4,
                IncidentSeverityEnum.High => 3,
                IncidentSeverityEnum.Medium => 2,
                _ => 1
            };
        }

        private static bool IsAutoRescueMission(Mission mission)
        {
            string text = (mission.Title ?? String.Empty) + "\n" + (mission.Description ?? String.Empty);
            return text.Contains("ARMADA:AUTO-RESCUE", StringComparison.OrdinalIgnoreCase)
                || text.Contains("Autonomous rescue", StringComparison.OrdinalIgnoreCase)
                || (mission.Title ?? String.Empty).StartsWith("Rescue ", StringComparison.OrdinalIgnoreCase);
        }

        private static AuthContext BuildSystemAuth()
        {
            return AuthContext.Authenticated(
                Constants.DefaultTenantId,
                Constants.DefaultUserId,
                isAdmin: true,
                isTenantAdmin: true,
                authMethod: "IncidentLifecycle",
                principalDisplay: "Armada Incident Lifecycle");
        }

        private readonly struct IncidentEvidence
        {
            public IncidentEvidenceKind Kind { get; }
            public string Note { get; }

            private IncidentEvidence(IncidentEvidenceKind kind, string note)
            {
                Kind = kind;
                Note = note;
            }

            public static IncidentEvidence None => new IncidentEvidence(IncidentEvidenceKind.None, String.Empty);
            public static IncidentEvidence ActiveWork(string note) => new IncidentEvidence(IncidentEvidenceKind.ActiveWork, note);
            public static IncidentEvidence ActiveFailure(string note) => new IncidentEvidence(IncidentEvidenceKind.ActiveFailure, note);
            public static IncidentEvidence Mitigated(string note) => new IncidentEvidence(IncidentEvidenceKind.Mitigated, note);
            public static IncidentEvidence RolledBack(string note) => new IncidentEvidence(IncidentEvidenceKind.RolledBack, note);
            public static IncidentEvidence Superseded(string note) => new IncidentEvidence(IncidentEvidenceKind.Superseded, note);
        }

        private enum IncidentEvidenceKind
        {
            None,
            ActiveWork,
            ActiveFailure,
            Mitigated,
            RolledBack,
            Superseded
        }
    }
}
