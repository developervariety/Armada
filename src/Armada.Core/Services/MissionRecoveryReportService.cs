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
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>
    /// Builds the read-only recovery report for a mission from recorded state only. It reuses the scoped incident and
    /// runbook services, never dispatches or classifies, and never infers a landing from mission status.
    /// </summary>
    public class MissionRecoveryReportService
    {
        #region Public-Members

        /// <summary>
        /// Maximum rescues listed.
        /// </summary>
        public const int MaxRescues = 50;

        /// <summary>
        /// Maximum incidents listed.
        /// </summary>
        public const int MaxIncidents = 50;

        /// <summary>
        /// Maximum runbook executions listed per incident.
        /// </summary>
        public const int MaxRunbookExecutionsPerIncident = 20;

        /// <summary>
        /// Number of most recent mission events searched for recovery events.
        /// </summary>
        public const int EventWindow = 100;

        /// <summary>
        /// Maximum stored length of a free-text field in the report.
        /// </summary>
        public const int MaxTextLength = 2000;

        #endregion

        #region Private-Members

        private readonly DatabaseDriver _Database;
        private readonly LoggingModule _Logging;
        private readonly ArmadaSettings _Settings;
        private readonly IncidentService _Incidents;
        private readonly RunbookService _Runbooks;
        private const string _Header = "[MissionRecoveryReportService] ";

        private static readonly HashSet<string> _RecoveryEventTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            "mission.orphan_recovered",
            "mission.failed_recoverable_work",
            "mission.retry_requeued",
            "mission.restarted",
            "mission.landing_retry"
        };

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="logging">Logging module.</param>
        /// <param name="settings">Application settings; recovery budgets are read on each report.</param>
        public MissionRecoveryReportService(DatabaseDriver database, LoggingModule logging, ArmadaSettings settings)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _Incidents = new IncidentService(database);
            _Runbooks = new RunbookService(database, logging);
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build the report for a mission the caller is already authorized to read.
        /// </summary>
        /// <param name="auth">Caller scope; related records are read in the same scope.</param>
        /// <param name="mission">Mission read in the caller's scope.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Recovery report.</returns>
        public async Task<MissionRecoveryReport> GetForMissionAsync(AuthContext auth, Mission mission, CancellationToken token = default)
        {
            if (auth == null) throw new ArgumentNullException(nameof(auth));
            if (mission == null) throw new ArgumentNullException(nameof(mission));

            AutonomousRecoverySettings recovery = _Settings.AutonomousRecovery ?? new AutonomousRecoverySettings();
            MissionRecoveryReport report = new MissionRecoveryReport
            {
                MissionId = mission.Id,
                Status = mission.Status,
                FailureReason = Bound(mission.FailureReason),
                ParentMissionId = mission.ParentMissionId,
                IsRescue = !String.IsNullOrWhiteSpace(mission.ParentMissionId),
                RecoveryAttempts = mission.RecoveryAttempts,
                MaxRecoveryAttempts = recovery.MaxMissionRecoveryAttempts,
                RecoveryBudgetExhausted = mission.RecoveryAttempts >= recovery.MaxMissionRecoveryAttempts,
                AutonomousRecoveryEnabled = recovery.Enabled,
                DispatchRescueMissions = recovery.DispatchRescueMissions,
                LandingRetryCount = mission.LandingRetryCount,
                MaxLandingRetries = _Settings.MaxLandingRetries,
                LastRecoveryActionUtc = mission.LastRecoveryActionUtc
            };

            await PopulateRescuesAsync(auth, mission, report, token).ConfigureAwait(false);
            await PopulateIncidentsAsync(auth, mission, report, token).ConfigureAwait(false);
            await PopulateEventsAsync(auth, mission, report, token).ConfigureAwait(false);
            return report;
        }

        #endregion

        #region Private-Methods

        private async Task PopulateRescuesAsync(AuthContext auth, Mission mission, MissionRecoveryReport report, CancellationToken token)
        {
            if (String.IsNullOrWhiteSpace(mission.VesselId))
            {
                report.RescuesUnavailableReason = "the mission has no vessel; rescue missions are found among the vessel's missions";
                return;
            }

            try
            {
                List<MissionSummary> vesselMissions = auth.IsAdmin
                    ? await _Database.Missions.EnumerateMissionSummariesByVesselAsync(mission.VesselId, token).ConfigureAwait(false)
                    : await _Database.Missions.EnumerateMissionSummariesByVesselAsync(auth.TenantId!, mission.VesselId, token).ConfigureAwait(false);

                IEnumerable<MissionSummary> rescues = vesselMissions
                    .Where(item => String.Equals(item.ParentMissionId, mission.Id, StringComparison.Ordinal));
                if (!auth.IsAdmin && !auth.IsTenantAdmin)
                    rescues = rescues.Where(item => String.Equals(item.UserId, auth.UserId, StringComparison.Ordinal));

                List<MissionSummary> ordered = rescues
                    .OrderBy(item => item.CreatedUtc)
                    .ThenBy(item => item.Id, StringComparer.Ordinal)
                    .ToList();

                report.RescuesTruncated = ordered.Count > MaxRescues;
                foreach (MissionSummary rescue in ordered.Take(MaxRescues))
                {
                    report.Rescues.Add(new MissionRecoveryRescue
                    {
                        MissionId = rescue.Id,
                        Title = Bound(rescue.Title) ?? "",
                        Status = rescue.Status,
                        VoyageId = rescue.VoyageId,
                        CaptainId = rescue.CaptainId,
                        CommitHash = rescue.CommitHash,
                        FailureReason = Bound(rescue.FailureReason),
                        CreatedUtc = rescue.CreatedUtc,
                        CompletedUtc = rescue.CompletedUtc
                    });
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                report.Rescues.Clear();
                report.RescuesUnavailableReason = "rescue missions could not be read (" + ex.GetType().Name + ")";
                _Logging.Warn(_Header + "could not read rescues for mission " + mission.Id + ": " + ex.Message);
            }
        }

        private async Task PopulateIncidentsAsync(AuthContext auth, Mission mission, MissionRecoveryReport report, CancellationToken token)
        {
            try
            {
                EnumerationResult<Incident> incidents = await _Incidents.EnumerateAsync(
                    auth,
                    new IncidentQuery { MissionId = mission.Id, PageNumber = 1, PageSize = MaxIncidents },
                    token).ConfigureAwait(false);
                report.IncidentsTruncated = incidents.TotalRecords > incidents.Objects.Count;

                foreach (Incident incident in incidents.Objects)
                {
                    MissionRecoveryIncident item = new MissionRecoveryIncident
                    {
                        IncidentId = incident.Id,
                        Title = Bound(incident.Title) ?? "",
                        Status = incident.Status,
                        Severity = incident.Severity,
                        RecoveryNotes = Bound(incident.RecoveryNotes),
                        DetectedUtc = incident.DetectedUtc,
                        MitigatedUtc = incident.MitigatedUtc,
                        ClosedUtc = incident.ClosedUtc
                    };

                    EnumerationResult<RunbookExecution> executions = await _Runbooks.EnumerateExecutionsAsync(
                        auth,
                        new RunbookExecutionQuery { IncidentId = incident.Id, PageNumber = 1, PageSize = MaxRunbookExecutionsPerIncident },
                        token).ConfigureAwait(false);
                    item.RunbookExecutionsTruncated = executions.TotalRecords > executions.Objects.Count;
                    foreach (RunbookExecution execution in executions.Objects)
                    {
                        item.RunbookExecutions.Add(new MissionRecoveryRunbookExecution
                        {
                            ExecutionId = execution.Id,
                            RunbookId = execution.RunbookId,
                            Title = Bound(execution.Title) ?? "",
                            Status = execution.Status,
                            StartedUtc = execution.StartedUtc,
                            CompletedUtc = execution.CompletedUtc
                        });
                    }

                    report.Incidents.Add(item);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                report.Incidents.Clear();
                report.IncidentsTruncated = false;
                report.IncidentsUnavailableReason = "incidents could not be read (" + ex.GetType().Name + ")";
                _Logging.Warn(_Header + "could not read incidents for mission " + mission.Id + ": " + ex.Message);
            }
        }

        private async Task PopulateEventsAsync(AuthContext auth, Mission mission, MissionRecoveryReport report, CancellationToken token)
        {
            EnumerationQuery query = new EnumerationQuery
            {
                MissionId = mission.Id,
                Order = EnumerationOrderEnum.CreatedDescending,
                PageNumber = 1,
                PageSize = EventWindow
            };

            try
            {
                EnumerationResult<ArmadaEvent> result = auth.IsAdmin
                    ? await _Database.Events.EnumerateAsync(query, token).ConfigureAwait(false)
                    : auth.IsTenantAdmin
                        ? await _Database.Events.EnumerateAsync(auth.TenantId!, query, token).ConfigureAwait(false)
                        : await _Database.Events.EnumerateAsync(auth.TenantId!, auth.UserId!, query, token).ConfigureAwait(false);
                List<ArmadaEvent> events = result.Objects ?? new List<ArmadaEvent>();
                report.EventsWindowFull = events.Count >= EventWindow;

                foreach (ArmadaEvent evt in events)
                {
                    if (!IsRecoveryEventType(evt.EventType)) continue;
                    report.Events.Add(new MissionRecoveryEvent
                    {
                        EventId = evt.Id,
                        EventType = evt.EventType,
                        Message = Bound(evt.Message) ?? "",
                        CreatedUtc = evt.CreatedUtc
                    });
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                report.Events.Clear();
                report.EventsWindowFull = false;
                report.EventsUnavailableReason = "recovery events could not be read (" + ex.GetType().Name + ")";
                _Logging.Warn(_Header + "could not read recovery events for mission " + mission.Id + ": " + ex.Message);
            }
        }

        private static bool IsRecoveryEventType(string? eventType)
        {
            if (String.IsNullOrEmpty(eventType)) return false;
            if (eventType.StartsWith("autonomous_recovery.", StringComparison.Ordinal)) return true;
            if (eventType.StartsWith("landing_drain.", StringComparison.Ordinal)) return true;
            return _RecoveryEventTypes.Contains(eventType);
        }

        private static string? Bound(string? text)
        {
            if (text == null) return null;
            string redacted = SecretRedactor.Redact(text);
            if (redacted.Length <= MaxTextLength) return redacted;
            const string marker = "\n...(truncated)";
            return redacted.Substring(0, MaxTextLength - marker.Length) + marker;
        }

        #endregion
    }
}
