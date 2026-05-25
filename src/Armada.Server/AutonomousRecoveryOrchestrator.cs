namespace Armada.Server
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>
    /// Armada-native policy service for failed mission incidents, runbook records, rescue dispatch,
    /// and bounded Mail nudges for live stalled work.
    /// </summary>
    public sealed class AutonomousRecoveryOrchestrator
    {
        private const string _Header = "[AutonomousRecoveryOrchestrator] ";
        private const string _RecoveryRunbookFileName = "system/mission-recovery.md";
        private const string _RescueMarker = "<!-- ARMADA:AUTO-RESCUE -->";
        private const string _NudgeMarker = "[ARMADA_AUTO_NUDGE]";

        private readonly DatabaseDriver _Database;
        private readonly IAdmiralService _Admiral;
        private readonly IncidentService _Incidents;
        private readonly RunbookService _Runbooks;
        private readonly ArmadaSettings _Settings;
        private readonly LoggingModule _Logging;
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _MissionLocks = new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.Ordinal);
        private readonly SemaphoreSlim _SweepLock = new SemaphoreSlim(1, 1);

        /// <summary>
        /// Instantiate.
        /// </summary>
        public AutonomousRecoveryOrchestrator(
            DatabaseDriver database,
            IAdmiralService admiral,
            IncidentService incidents,
            RunbookService runbooks,
            ArmadaSettings settings,
            LoggingModule logging)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Admiral = admiral ?? throw new ArgumentNullException(nameof(admiral));
            _Incidents = incidents ?? throw new ArgumentNullException(nameof(incidents));
            _Runbooks = runbooks ?? throw new ArgumentNullException(nameof(runbooks));
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
        }

        /// <summary>
        /// Handle a mission outcome emitted by MissionService.
        /// </summary>
        public async Task HandleMissionOutcomeAsync(Mission mission, bool willInvokeLandingHandler, CancellationToken token = default)
        {
            if (mission == null) throw new ArgumentNullException(nameof(mission));
            if (!_Settings.AutonomousRecovery.Enabled) return;
            if (!IsRecoverableTerminalStatus(mission.Status)) return;

            await ApplyFailurePolicyAsync(mission, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Fire-and-forget heartbeat maintenance. This method never blocks the caller.
        /// </summary>
        public void TriggerBackgroundSweep(CancellationToken token = default)
        {
            if (!_Settings.AutonomousRecovery.Enabled) return;

            _ = Task.Run(async () =>
            {
                try
                {
                    await SweepAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    _Logging.Warn(_Header + "background sweep failed: " + ex.Message);
                }
            }, CancellationToken.None);
        }

        /// <summary>
        /// Run one bounded recovery maintenance pass.
        /// </summary>
        public async Task SweepAsync(CancellationToken token = default)
        {
            if (!_Settings.AutonomousRecovery.Enabled) return;
            if (!await _SweepLock.WaitAsync(0, token).ConfigureAwait(false)) return;

            try
            {
                await NudgeStalledLiveCaptainsAsync(token).ConfigureAwait(false);
                await ProcessRecentFailedMissionsAsync(token).ConfigureAwait(false);
            }
            finally
            {
                _SweepLock.Release();
            }
        }

        private async Task ProcessRecentFailedMissionsAsync(CancellationToken token)
        {
            DateTime cutoff = DateTime.UtcNow.AddHours(-_Settings.AutonomousRecovery.FailedMissionLookbackHours);
            List<Mission> candidates = new List<Mission>();
            candidates.AddRange(await _Database.Missions.EnumerateByStatusAsync(MissionStatusEnum.Failed, token).ConfigureAwait(false));
            candidates.AddRange(await _Database.Missions.EnumerateByStatusAsync(MissionStatusEnum.LandingFailed, token).ConfigureAwait(false));

            foreach (Mission mission in candidates
                .Where(item => item.LastUpdateUtc >= cutoff)
                .OrderBy(item => item.LastUpdateUtc)
                .Take(10))
            {
                token.ThrowIfCancellationRequested();
                await ApplyFailurePolicyAsync(mission, token).ConfigureAwait(false);
            }
        }

        private async Task ApplyFailurePolicyAsync(Mission mission, CancellationToken token)
        {
            SemaphoreSlim missionLock = _MissionLocks.GetOrAdd(mission.Id, _ => new SemaphoreSlim(1, 1));
            await missionLock.WaitAsync(token).ConfigureAwait(false);

            try
            {
                Mission? latest = await ReadMissionAsync(mission, token).ConfigureAwait(false);
                if (latest == null || !IsRecoverableTerminalStatus(latest.Status))
                    return;

                if (await SuppressCancelledVoyageRecoveryAsync(latest, token).ConfigureAwait(false))
                    return;

                if (await IsAlreadyHandledAsync(latest, token).ConfigureAwait(false))
                    return;

                RecoveryDecision decision = Classify(latest);
                AuthContext auth = BuildAuth(latest);
                Incident incident = await EnsureIncidentAsync(auth, latest, decision, token).ConfigureAwait(false);
                RunbookExecution? execution = await ExecuteRecoveryRunbookAsync(auth, latest, incident, decision, token).ConfigureAwait(false);

                if (!decision.DispatchRescue)
                {
                    await MarkPolicyBlockedAsync(latest, token).ConfigureAwait(false);
                    await EmitEventAsync("autonomous_recovery.blocked",
                        "Autonomous recovery opened incident " + incident.Id + " but did not dispatch a rescue for mission " + latest.Id + ": " + decision.Reason,
                        latest, incident.Id, token).ConfigureAwait(false);
                    return;
                }

                Mission rescue = await DispatchRescueMissionAsync(latest, incident, token).ConfigureAwait(false);
                latest.RecoveryAttempts++;
                latest.LastRecoveryActionUtc = DateTime.UtcNow;
                latest.LastUpdateUtc = DateTime.UtcNow;
                await _Database.Missions.UpdateAsync(latest, token).ConfigureAwait(false);

                await _Incidents.UpdateAsync(auth, incident.Id, new IncidentUpsertRequest
                {
                    RecoveryNotes = AppendNote(incident.RecoveryNotes,
                        "Autonomous rescue mission dispatched: " + rescue.Id +
                        (execution != null ? " via runbook execution " + execution.Id + "." : "."))
                }, token).ConfigureAwait(false);

                await EmitEventAsync("autonomous_recovery.rescue_dispatched",
                    "Autonomous rescue mission " + rescue.Id + " dispatched for failed mission " + latest.Id,
                    latest, incident.Id, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "failed to apply recovery policy for mission " + mission.Id + ": " + ex.Message);
            }
            finally
            {
                missionLock.Release();
            }
        }

        private async Task<Mission?> ReadMissionAsync(Mission mission, CancellationToken token)
        {
            if (!String.IsNullOrWhiteSpace(mission.TenantId))
            {
                Mission? tenantScoped = await _Database.Missions.ReadAsync(mission.TenantId, mission.Id, token).ConfigureAwait(false);
                if (tenantScoped != null) return tenantScoped;
            }

            return await _Database.Missions.ReadAsync(mission.Id, token).ConfigureAwait(false);
        }

        private RecoveryDecision Classify(Mission mission)
        {
            string reason = mission.FailureReason ?? String.Empty;

            if (!_Settings.AutonomousRecovery.DispatchRescueMissions)
                return RecoveryDecision.Blocked("autonomous rescue dispatch is disabled");
            if (String.IsNullOrWhiteSpace(mission.VesselId))
                return RecoveryDecision.Blocked("mission has no vessel");
            if (mission.RecoveryAttempts >= _Settings.AutonomousRecovery.MaxMissionRecoveryAttempts)
                return RecoveryDecision.Blocked("mission recovery budget is exhausted");
            if (mission.Status == MissionStatusEnum.LandingFailed)
                return RecoveryDecision.Blocked("landing failures remain owned by landing and merge recovery workflows");
            if (IsAutoRescueMission(mission))
                return RecoveryDecision.Blocked("failed mission is already an autonomous rescue");
            if (HasSeriousFailureReason(reason))
                return RecoveryDecision.Blocked("failure requires human review: " + reason);

            return RecoveryDecision.Rescue("recoverable mission failure");
        }

        private async Task<bool> SuppressCancelledVoyageRecoveryAsync(Mission mission, CancellationToken token)
        {
            if (String.IsNullOrWhiteSpace(mission.VoyageId))
                return false;

            Voyage? voyage = await _Database.Voyages.ReadAsync(mission.VoyageId, token).ConfigureAwait(false);
            if (voyage?.Status != VoyageStatusEnum.Cancelled)
                return false;

            string note = "Autonomous recovery suppressed because parent voyage " + voyage.Id + " is Cancelled.";
            AuthContext auth = BuildAuth(mission);
            List<Mission> cancelledRescues = await CancelActiveRescueMissionsAsync(mission, note, token).ConfigureAwait(false);
            await CloseActiveMissionIncidentsAsync(auth, mission, AppendRescueCancellationNote(note, cancelledRescues), token).ConfigureAwait(false);
            await MarkPolicyBlockedAsync(mission, token).ConfigureAwait(false);

            await EmitEventAsync("autonomous_recovery.suppressed_cancelled_voyage",
                note + (cancelledRescues.Count > 0 ? " Cancelled rescue mission(s): " + String.Join(", ", cancelledRescues.Select(item => item.Id)) + "." : String.Empty),
                mission, null, token).ConfigureAwait(false);
            return true;
        }

        private async Task<List<Mission>> CancelActiveRescueMissionsAsync(Mission failedMission, string reason, CancellationToken token)
        {
            List<Mission> rescues = await EnumerateRescueMissionsAsync(failedMission, token).ConfigureAwait(false);
            List<Mission> cancelled = new List<Mission>();

            foreach (Mission rescue in rescues.Where(item => IsCancellableRescueStatus(item.Status)))
            {
                if (!String.IsNullOrWhiteSpace(rescue.CaptainId))
                {
                    try
                    {
                        await _Admiral.RecallCaptainAsync(rescue.CaptainId, token).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _Logging.Warn(_Header + "could not recall captain " + rescue.CaptainId + " while cancelling rescue " + rescue.Id + ": " + ex.Message);
                    }
                }

                rescue.Status = MissionStatusEnum.Cancelled;
                rescue.FailureReason = reason;
                rescue.ProcessId = null;
                rescue.CompletedUtc = DateTime.UtcNow;
                rescue.LastUpdateUtc = DateTime.UtcNow;
                await _Database.Missions.UpdateAsync(rescue, token).ConfigureAwait(false);
                cancelled.Add(rescue);
            }

            return cancelled;
        }

        private async Task CloseActiveMissionIncidentsAsync(AuthContext auth, Mission mission, string note, CancellationToken token)
        {
            EnumerationResult<Incident> existing = await _Incidents.EnumerateAsync(auth, new IncidentQuery
            {
                MissionId = mission.Id,
                PageNumber = 1,
                PageSize = 25
            }, token).ConfigureAwait(false);

            foreach (Incident incident in existing.Objects.Where(item =>
                item.Status != IncidentStatusEnum.Closed && item.Status != IncidentStatusEnum.RolledBack))
            {
                Incident updated = await _Incidents.UpdateAsync(auth, incident.Id, new IncidentUpsertRequest
                {
                    Status = IncidentStatusEnum.Closed,
                    RecoveryNotes = AppendNote(incident.RecoveryNotes, note),
                    ClosedUtc = DateTime.UtcNow
                }, token).ConfigureAwait(false);

                await EmitEventAsync("autonomous_recovery.incident_closed_cancelled_voyage",
                    "Autonomous recovery closed incident " + updated.Id + " because parent voyage " + mission.VoyageId + " is Cancelled.",
                    mission, updated.Id, token).ConfigureAwait(false);
            }
        }

        private async Task<Incident> EnsureIncidentAsync(AuthContext auth, Mission mission, RecoveryDecision decision, CancellationToken token)
        {
            EnumerationResult<Incident> existing = await _Incidents.EnumerateAsync(auth, new IncidentQuery
            {
                MissionId = mission.Id,
                PageNumber = 1,
                PageSize = 25
            }, token).ConfigureAwait(false);

            Incident? active = existing.Objects
                .FirstOrDefault(item => item.Status != IncidentStatusEnum.Closed && item.Status != IncidentStatusEnum.RolledBack);
            string recoveryNote = decision.DispatchRescue
                ? "Autonomous policy classified this as recoverable and will dispatch one rescue mission."
                : "Autonomous policy stopped before rescue dispatch: " + decision.Reason + ".";

            if (active != null)
            {
                return await _Incidents.UpdateAsync(auth, active.Id, new IncidentUpsertRequest
                {
                    Summary = BuildIncidentSummary(mission, decision),
                    Severity = decision.DispatchRescue ? IncidentSeverityEnum.Medium : IncidentSeverityEnum.High,
                    RecoveryNotes = AppendNote(active.RecoveryNotes, recoveryNote)
                }, token).ConfigureAwait(false);
            }

            Incident created = await _Incidents.CreateAsync(auth, new IncidentUpsertRequest
            {
                Title = "Mission failed: " + Truncate(mission.Title, 96),
                Summary = BuildIncidentSummary(mission, decision),
                Status = IncidentStatusEnum.Open,
                Severity = decision.DispatchRescue ? IncidentSeverityEnum.Medium : IncidentSeverityEnum.High,
                VesselId = mission.VesselId,
                MissionId = mission.Id,
                VoyageId = mission.VoyageId,
                Impact = "Mission did not reach a successful landing.",
                RootCause = mission.FailureReason,
                RecoveryNotes = recoveryNote,
                DetectedUtc = mission.CompletedUtc ?? mission.LastUpdateUtc
            }, token).ConfigureAwait(false);

            await EmitEventAsync("autonomous_recovery.incident_opened",
                "Autonomous recovery opened incident " + created.Id + " for mission " + mission.Id,
                mission, created.Id, token).ConfigureAwait(false);
            return created;
        }

        private async Task<RunbookExecution?> ExecuteRecoveryRunbookAsync(
            AuthContext auth,
            Mission mission,
            Incident incident,
            RecoveryDecision decision,
            CancellationToken token)
        {
            try
            {
                Runbook runbook = await EnsureRecoveryRunbookAsync(auth, token).ConfigureAwait(false);
                RunbookExecution execution = await _Runbooks.StartExecutionAsync(auth, runbook.Id, new RunbookExecutionStartRequest
                {
                    Title = "Autonomous recovery for " + mission.Id,
                    IncidentId = incident.Id,
                    ParameterValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["missionId"] = mission.Id,
                        ["incidentId"] = incident.Id,
                        ["vesselId"] = mission.VesselId ?? String.Empty,
                        ["failureReason"] = mission.FailureReason ?? String.Empty,
                        ["decision"] = decision.DispatchRescue ? "dispatch_rescue" : "block"
                    },
                    Notes = "Decision: " + (decision.DispatchRescue ? "dispatch rescue" : "block") + ". Reason: " + decision.Reason
                }, token).ConfigureAwait(false);

                await _Runbooks.UpdateExecutionAsync(auth, execution.Id, new RunbookExecutionUpdateRequest
                {
                    Status = RunbookExecutionStatusEnum.Completed,
                    CompletedStepIds = runbook.Steps.Select(step => step.Id).ToList(),
                    Notes = "Autonomous recovery policy completed. Decision: " +
                        (decision.DispatchRescue ? "dispatch rescue." : "block. " + decision.Reason)
                }, token).ConfigureAwait(false);

                return execution;
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "runbook execution failed for mission " + mission.Id + ": " + ex.Message);
                return null;
            }
        }

        private async Task<Runbook> EnsureRecoveryRunbookAsync(AuthContext auth, CancellationToken token)
        {
            EnumerationResult<Runbook> existing = await _Runbooks.EnumerateAsync(auth, new RunbookQuery
            {
                Search = _RecoveryRunbookFileName,
                PageNumber = 1,
                PageSize = 100
            }, token).ConfigureAwait(false);

            Runbook? runbook = existing.Objects.FirstOrDefault(item =>
                String.Equals(item.FileName, _RecoveryRunbookFileName, StringComparison.OrdinalIgnoreCase));
            if (runbook != null)
                return runbook;

            return await _Runbooks.CreateAsync(auth, new RunbookUpsertRequest
            {
                FileName = _RecoveryRunbookFileName,
                Title = "Autonomous Mission Recovery",
                Description = "Classify failed missions, open incidents, and dispatch bounded rescue work when safe.",
                Active = true,
                Parameters = new List<RunbookParameter>
                {
                    new RunbookParameter { Name = "missionId", Label = "Mission ID", Required = true },
                    new RunbookParameter { Name = "incidentId", Label = "Incident ID", Required = true },
                    new RunbookParameter { Name = "vesselId", Label = "Vessel ID", Required = false },
                    new RunbookParameter { Name = "failureReason", Label = "Failure Reason", Required = false },
                    new RunbookParameter { Name = "decision", Label = "Decision", Required = true }
                },
                Steps = new List<RunbookStep>
                {
                    new RunbookStep { Title = "Classify failure", Instructions = "Decide whether the failure is recoverable or requires human review." },
                    new RunbookStep { Title = "Open incident", Instructions = "Create or update the incident tied to the failed mission." },
                    new RunbookStep { Title = "Apply policy", Instructions = "Dispatch one rescue mission when safe; otherwise leave the incident open for human review." }
                },
                OverviewMarkdown = "System runbook used by Armada's autonomous recovery orchestrator. It records the policy decision for failed missions and links the result to the incident."
            }, token).ConfigureAwait(false);
        }

        private async Task<Mission> DispatchRescueMissionAsync(Mission failedMission, Incident incident, CancellationToken token)
        {
            int attemptNumber = failedMission.RecoveryAttempts + 1;
            Mission rescue = new Mission
            {
                TenantId = failedMission.TenantId,
                UserId = failedMission.UserId,
                VesselId = failedMission.VesselId,
                ParentMissionId = failedMission.Id,
                Persona = ResolveRescuePersona(failedMission.Persona),
                PreferredModel = failedMission.PreferredModel,
                Priority = Math.Max(0, failedMission.Priority - 10),
                Title = "Rescue " + attemptNumber + ": " + Truncate(failedMission.Title, 100),
                Description = BuildRescueDescription(failedMission, incident, attemptNumber)
            };

            return await _Admiral.DispatchMissionAsync(rescue, token).ConfigureAwait(false);
        }

        private async Task<bool> IsAlreadyHandledAsync(Mission failedMission, CancellationToken token)
        {
            int maxAttempts = _Settings.AutonomousRecovery.MaxMissionRecoveryAttempts;
            if (failedMission.LastRecoveryActionUtc.HasValue && failedMission.RecoveryAttempts >= maxAttempts)
                return true;

            List<Mission> rescues = await EnumerateRescueMissionsAsync(failedMission, token).ConfigureAwait(false);
            if (rescues.Any(rescue => !IsRetryableRescueTerminalFailure(rescue.Status)))
                return true;

            return false;
        }

        private async Task<List<Mission>> EnumerateRescueMissionsAsync(Mission failedMission, CancellationToken token)
        {
            List<Mission> vesselMissions = !String.IsNullOrWhiteSpace(failedMission.VesselId)
                ? await _Database.Missions.EnumerateByVesselAsync(failedMission.VesselId, token).ConfigureAwait(false)
                : await _Database.Missions.EnumerateAsync(token).ConfigureAwait(false);

            return vesselMissions.Where(item =>
                String.Equals(item.ParentMissionId, failedMission.Id, StringComparison.Ordinal)
                && IsAutoRescueMission(item)).ToList();
        }

        private async Task MarkPolicyBlockedAsync(Mission mission, CancellationToken token)
        {
            mission.RecoveryAttempts = Math.Max(
                mission.RecoveryAttempts + 1,
                _Settings.AutonomousRecovery.MaxMissionRecoveryAttempts);
            mission.LastRecoveryActionUtc = DateTime.UtcNow;
            mission.LastUpdateUtc = DateTime.UtcNow;
            await _Database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);
        }

        private async Task NudgeStalledLiveCaptainsAsync(CancellationToken token)
        {
            if (!_Settings.AutonomousRecovery.SendStallMailNudges) return;

            double thresholdMinutes = Math.Max(1.0, _Settings.StallThresholdMinutes * _Settings.AutonomousRecovery.StallMailNudgeThresholdRatio);
            List<Captain> working = await _Database.Captains.EnumerateByStateAsync(CaptainStateEnum.Working, token).ConfigureAwait(false);

            foreach (Captain captain in working)
            {
                if (!captain.LastHeartbeatUtc.HasValue || String.IsNullOrWhiteSpace(captain.CurrentMissionId))
                    continue;

                TimeSpan quietFor = DateTime.UtcNow - captain.LastHeartbeatUtc.Value;
                if (quietFor.TotalMinutes < thresholdMinutes)
                    continue;

                Mission? mission = await _Database.Missions.ReadAsync(captain.CurrentMissionId, token).ConfigureAwait(false);
                if (mission == null || !IsLiveMissionStatus(mission.Status))
                    continue;

                if (await HasRecentAutoNudgeAsync(captain, token).ConfigureAwait(false))
                    continue;

                Signal signal = new Signal(SignalTypeEnum.Mail,
                    _NudgeMarker + " Armada has not seen progress for " + quietFor.TotalMinutes.ToString("F1") +
                    " minutes on mission " + mission.Id + ". Please report status, continue the mission, or fail with a specific blocker.");
                signal.TenantId = captain.TenantId ?? mission.TenantId;
                signal.UserId = captain.UserId ?? mission.UserId;
                signal.ToCaptainId = captain.Id;
                await _Database.Signals.CreateAsync(signal, token).ConfigureAwait(false);

                await EmitEventAsync("autonomous_recovery.mail_nudge_sent",
                    "Autonomous Mail nudge sent to captain " + captain.Id + " for mission " + mission.Id,
                    mission, null, token).ConfigureAwait(false);
            }
        }

        private async Task<bool> HasRecentAutoNudgeAsync(Captain captain, CancellationToken token)
        {
            DateTime cutoff = DateTime.UtcNow.AddMinutes(-_Settings.AutonomousRecovery.StallMailNudgeCooldownMinutes);
            EnumerationResult<Signal> page = !String.IsNullOrWhiteSpace(captain.TenantId)
                ? await _Database.Signals.EnumerateAsync(captain.TenantId, new EnumerationQuery
                {
                    PageNumber = 1,
                    PageSize = 100,
                    SignalType = SignalTypeEnum.Mail.ToString(),
                    ToCaptainId = captain.Id
                }, token).ConfigureAwait(false)
                : await _Database.Signals.EnumerateAsync(new EnumerationQuery
                {
                    PageNumber = 1,
                    PageSize = 100,
                    SignalType = SignalTypeEnum.Mail.ToString(),
                    ToCaptainId = captain.Id
                }, token).ConfigureAwait(false);

            return page.Objects.Any(item =>
                item.CreatedUtc >= cutoff
                && String.Equals(item.ToCaptainId, captain.Id, StringComparison.Ordinal)
                && item.Type == SignalTypeEnum.Mail
                && (item.Payload ?? String.Empty).Contains(_NudgeMarker, StringComparison.Ordinal));
        }

        private async Task EmitEventAsync(string eventType, string message, Mission mission, string? incidentId, CancellationToken token)
        {
            ArmadaEvent evt = new ArmadaEvent(eventType, message)
            {
                TenantId = mission.TenantId,
                UserId = mission.UserId,
                EntityType = incidentId != null ? "incident" : "mission",
                EntityId = incidentId ?? mission.Id,
                MissionId = mission.Id,
                VesselId = mission.VesselId,
                VoyageId = mission.VoyageId
            };

            await _Database.Events.CreateAsync(evt, token).ConfigureAwait(false);
        }

        private static AuthContext BuildAuth(Mission mission)
        {
            return AuthContext.Authenticated(
                mission.TenantId ?? Constants.DefaultTenantId,
                mission.UserId ?? Constants.DefaultUserId,
                false,
                true,
                "AutonomousRecovery",
                principalDisplay: "Armada Autonomous Recovery");
        }

        private static string BuildIncidentSummary(Mission mission, RecoveryDecision decision)
        {
            return "Mission " + mission.Id + " is " + mission.Status + ". " +
                "Reason: " + (String.IsNullOrWhiteSpace(mission.FailureReason) ? "not recorded" : mission.FailureReason) + ". " +
                "Policy: " + (decision.DispatchRescue ? "dispatch rescue" : "block") + " (" + decision.Reason + ").";
        }

        private static string BuildRescueDescription(Mission failedMission, Incident incident, int attemptNumber)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(_RescueMarker);
            sb.AppendLine("Autonomous rescue attempt " + attemptNumber + " for failed mission " + failedMission.Id + ".");
            sb.AppendLine();
            sb.AppendLine("Incident: " + incident.Id);
            sb.AppendLine("Original title: " + failedMission.Title);
            sb.AppendLine("Failure status: " + failedMission.Status);
            sb.AppendLine("Failure reason: " + (failedMission.FailureReason ?? "not recorded"));
            if (!String.IsNullOrWhiteSpace(failedMission.BranchName))
                sb.AppendLine("Original branch: " + failedMission.BranchName);
            sb.AppendLine();
            sb.AppendLine("Objective:");
            sb.AppendLine("Recover the original mission without repeating the failure. Inspect the original failure, make the smallest corrective change, run the vessel's workflow profile checks when available, and leave explicit evidence in Armada records.");
            sb.AppendLine();
            sb.AppendLine("Original mission description:");
            sb.AppendLine(failedMission.Description ?? "(no description recorded)");
            return sb.ToString();
        }

        private static string ResolveRescuePersona(string? failedPersona)
        {
            if (String.IsNullOrWhiteSpace(failedPersona)) return "Worker";

            string normalized = failedPersona.Trim().ToLowerInvariant().Replace(" ", "");
            string[] reviewerPersonas =
            {
                "judge",
                "testengineer",
                "usabilityengineer",
                "diagnosticprotocolreviewer",
                "tenantsecurityreviewer",
                "migrationdatareviewer",
                "performancememoryreviewer",
                "portingreferenceanalyst",
                "frontendworkflowreviewer"
            };

            if (reviewerPersonas.Any(item => String.Equals(item, normalized, StringComparison.Ordinal)))
                return "Worker";

            if (normalized.EndsWith("reviewer", StringComparison.Ordinal)
                || normalized.EndsWith("analyst", StringComparison.Ordinal))
                return "Worker";

            return failedPersona.Trim();
        }

        private static string AppendNote(string? existing, string note)
        {
            if (String.IsNullOrWhiteSpace(existing))
                return note;
            if (existing.Contains(note, StringComparison.OrdinalIgnoreCase))
                return existing;
            return existing.TrimEnd() + Environment.NewLine + Environment.NewLine + note;
        }

        private static bool IsRecoverableTerminalStatus(MissionStatusEnum status)
        {
            return status == MissionStatusEnum.Failed || status == MissionStatusEnum.LandingFailed;
        }

        private static bool IsRetryableRescueTerminalFailure(MissionStatusEnum status)
        {
            return status == MissionStatusEnum.Failed
                || status == MissionStatusEnum.LandingFailed;
        }

        private static bool IsCancellableRescueStatus(MissionStatusEnum status)
        {
            return status != MissionStatusEnum.Complete
                && status != MissionStatusEnum.Failed
                && status != MissionStatusEnum.LandingFailed
                && status != MissionStatusEnum.Cancelled;
        }

        private static bool IsLiveMissionStatus(MissionStatusEnum status)
        {
            return status == MissionStatusEnum.Assigned
                || status == MissionStatusEnum.InProgress
                || status == MissionStatusEnum.Testing
                || status == MissionStatusEnum.Review;
        }

        private static bool IsAutoRescueMission(Mission mission)
        {
            return (mission.Description ?? String.Empty).Contains(_RescueMarker, StringComparison.Ordinal)
                || (mission.Title ?? String.Empty).StartsWith("Rescue:", StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasSeriousFailureReason(string reason)
        {
            if (String.IsNullOrWhiteSpace(reason)) return false;

            string normalized = reason.ToLowerInvariant();
            string[] seriousMarkers =
            {
                "protected path",
                "review denied",
                "approval",
                "unauthorized",
                "forbidden",
                "invalid api key",
                "authentication failed",
                "not logged in",
                "login required",
                "quota",
                "rate limit",
                "recovery exhausted",
                "blocked by failed dependency",
                "vessel deleted"
            };

            return seriousMarkers.Any(marker => normalized.Contains(marker, StringComparison.Ordinal));
        }

        private static string Truncate(string? value, int max)
        {
            string normalized = String.IsNullOrWhiteSpace(value) ? "mission" : value.Trim();
            if (normalized.Length <= max) return normalized;
            return normalized.Substring(0, Math.Max(1, max - 3)).TrimEnd() + "...";
        }

        private static string AppendRescueCancellationNote(string note, List<Mission> cancelledRescues)
        {
            if (cancelledRescues.Count == 0)
                return note;

            return note + " Cancelled active autonomous rescue mission(s): " +
                String.Join(", ", cancelledRescues.Select(item => item.Id)) + ".";
        }

        private readonly struct RecoveryDecision
        {
            public bool DispatchRescue { get; }
            public string Reason { get; }

            private RecoveryDecision(bool dispatchRescue, string reason)
            {
                DispatchRescue = dispatchRescue;
                Reason = reason;
            }

            public static RecoveryDecision Rescue(string reason) => new RecoveryDecision(true, reason);
            public static RecoveryDecision Blocked(string reason) => new RecoveryDecision(false, reason);
        }
    }
}
