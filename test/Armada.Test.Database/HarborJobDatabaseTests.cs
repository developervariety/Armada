namespace Armada.Test.Database
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Harbor;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;

    /// <summary>Provider-backed Harbor job recovery records, keyset queries, completion ack, and mission binding.</summary>
    internal sealed class HarborJobDatabaseTests
    {
        private readonly DatabaseDriver _Driver;
        private readonly DatabaseSettings _Settings;

        internal HarborJobDatabaseTests(DatabaseDriver driver, DatabaseSettings settings)
        {
            _Driver = driver ?? throw new ArgumentNullException(nameof(driver));
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        internal async Task VerifyAsync(CancellationToken token)
        {
            int version = VersionFor(_Settings.Type);
            Dictionary<int, string> history = await new MigrationScenarioRunner(_Settings).ReadHistoryAsync(token).ConfigureAwait(false);
            DatabaseAssert.True(history.ContainsKey(version), "Harbor job migration " + version + " is recorded in history");
            DatabaseAssert.True(history[version].StartsWith("Persist fenced Harbor job recovery identity|", StringComparison.Ordinal),
                "Harbor recovery migration " + version + " description: " + history[version]);

            string suffix = Guid.NewGuid().ToString("N");
            string runnerId = "hbr_jobs_" + suffix;
            HarborJobRecord running = NewRecord("hjob_" + suffix + "_a", runnerId, HarborJobStateEnum.Pending, 1);
            await _Driver.HarborJobs.CreateAsync(running, token).ConfigureAwait(false);

            HarborJobRecord? stored = await _Driver.HarborJobs.ReadAsync(running.JobId, token).ConfigureAwait(false);
            DatabaseAssert.NotNull(stored, "Harbor job record reads back");
            DatabaseAssert.Equal("mission:" + suffix, stored!.LaunchKey, "launch key");
            DatabaseAssert.Equal(4L, stored.EnrollmentGeneration, "enrollment generation");
            DatabaseAssert.True(stored.ProcessId == null && stored.ExitCode == null && stored.CompletedUtc == null, "nullable columns read back as null");
            DatabaseAssert.Equal("msn_" + suffix, stored.MissionId, "mission id");
            DatabaseAssert.Equal("dock_" + suffix, stored.DockId, "dock id");
            DatabaseAssert.Equal(9101, stored.AdmiralProcessId ?? 0, "Admiral synthetic process id");
            AssertUtc(running.AdmiralProcessStartedUtc, stored.AdmiralProcessStartedUtc, "Admiral process start time");
            DatabaseAssert.True(!stored.StopConfirmed && !stored.CompletionDelivered, "new active job has no stop or completion acknowledgment");

            running.State = HarborJobStateEnum.Running;
            running.ProcessId = 4242;
            running.RunnerProcessStartedUtc = DateTime.UtcNow.AddSeconds(-5);
            running.NextOutputSequence = 17;
            running.SessionGeneration = 9;
            running.Revision = 3;
            DatabaseAssert.True(await _Driver.HarborJobs.TryUpdateAsync(running, token).ConfigureAwait(false), "a higher revision replaces the record");
            HarborJobRecord delayed = NewRecord(running.JobId, runnerId, HarborJobStateEnum.Pending, 2);
            DatabaseAssert.True(!await _Driver.HarborJobs.TryUpdateAsync(delayed, token).ConfigureAwait(false), "a delayed lower revision never overwrites a newer state");
            DatabaseAssert.True(!await _Driver.HarborJobs.TryUpdateAsync(running, token).ConfigureAwait(false), "the same revision is not applied twice");
            HarborJobRecord exactRevisionUpdate = running;
            exactRevisionUpdate.Revision = 3;
            DatabaseAssert.True(!await _Driver.HarborJobs.TryUpdateAtRevisionAsync(exactRevisionUpdate, 2, token).ConfigureAwait(false),
                "an older expected revision cannot apply a terminal or recovery update");
            exactRevisionUpdate.Revision = 5;
            DatabaseAssert.True(!await _Driver.HarborJobs.TryUpdateAtRevisionAsync(exactRevisionUpdate, 4, token).ConfigureAwait(false),
                "a newer expected revision cannot apply ahead of the durable row");
            exactRevisionUpdate.Revision = 4;
            DatabaseAssert.True(await _Driver.HarborJobs.TryUpdateAtRevisionAsync(exactRevisionUpdate, 3, token).ConfigureAwait(false),
                "an exact expected revision applies one compare-and-set update");
            running.Revision = 4;
            stored = await _Driver.HarborJobs.ReadAsync(running.JobId, token).ConfigureAwait(false);
            DatabaseAssert.Equal(HarborJobStateEnum.Running, stored!.State, "state after the guarded update");
            DatabaseAssert.Equal(17L, stored.NextOutputSequence, "output sequence after the guarded update");
            DatabaseAssert.Equal(4242, stored.ProcessId ?? 0, "process id after the guarded update");
            AssertUtc(running.RunnerProcessStartedUtc, stored.RunnerProcessStartedUtc, "runner process start time after the guarded update");

            HarborJobRecord exited = NewRecord("hjob_" + suffix + "_b", runnerId, HarborJobStateEnum.Exited, 5);
            exited.MissionId = "msn_" + suffix + "_exited";
            exited.ExitCode = 0;
            exited.CompletedUtc = DateTime.UtcNow;
            exited.StopConfirmed = true;
            await _Driver.HarborJobs.CreateAsync(exited, token).ConfigureAwait(false);
            HarborJobRecord unconfirmedTerminal = NewRecord("hjob_" + suffix + "_c", runnerId, HarborJobStateEnum.Lost, 2);
            unconfirmedTerminal.MissionId = "msn_" + suffix + "_unconfirmed";
            unconfirmedTerminal.CompletedUtc = DateTime.UtcNow;
            unconfirmedTerminal.StopConfirmed = false;
            await _Driver.HarborJobs.CreateAsync(unconfirmedTerminal, token).ConfigureAwait(false);
            HarborJobRecord delivered = NewRecord("hjob_" + suffix + "_d", runnerId, HarborJobStateEnum.Exited, 4);
            delivered.MissionId = "msn_" + suffix + "_delivered";
            delivered.CompletedUtc = DateTime.UtcNow;
            delivered.StopConfirmed = true;
            delivered.CompletionDelivered = true;
            await _Driver.HarborJobs.CreateAsync(delivered, token).ConfigureAwait(false);
            HarborJobRecord legacyMissionJob = NewRecord("hjob_" + suffix + "_legacy", "hbr_jobs_legacy_" + suffix, HarborJobStateEnum.Uncertain, 1);
            legacyMissionJob.DockId = null;
            legacyMissionJob.MissionId = "msn_legacy_" + suffix;
            await _Driver.HarborJobs.CreateAsync(legacyMissionJob, token).ConfigureAwait(false);
            HarborJobRecord genericOperatorJob = NewRecord("hjob_" + suffix + "_operator", "hbr_jobs_operator_" + suffix, HarborJobStateEnum.Uncertain, 1);
            genericOperatorJob.DockId = null;
            genericOperatorJob.MissionId = null;
            await _Driver.HarborJobs.CreateAsync(genericOperatorJob, token).ConfigureAwait(false);

            List<HarborJobRecord> active = await _Driver.HarborJobs.EnumerateAsync(new HarborJobQuery { RunnerId = runnerId, ActiveOnly = true }, token).ConfigureAwait(false);
            DatabaseAssert.Equal(1, active.Count, "active filter excludes terminal jobs");
            DatabaseAssert.Equal(running.JobId, active[0].JobId, "active job listed");
            List<HarborJobRecord> scoped = await _Driver.HarborJobs.EnumerateAsync(new HarborJobQuery
            {
                RunnerId = runnerId,
                TenantId = "ten_jobs",
                UserId = "usr_jobs",
                MissionId = running.MissionId,
                DockId = running.DockId,
                Limit = 1
            }, token).ConfigureAwait(false);
            DatabaseAssert.Equal(1, scoped.Count, "limit bounds the result");
            DatabaseAssert.Equal(running.JobId, scoped[0].JobId, "mission and dock filters match the active attempt");

            List<HarborJobRecord> recoveryPage = await _Driver.HarborJobs.EnumerateAsync(new HarborJobQuery
            {
                RecoveryOnly = true,
                RunnerId = runnerId,
                Limit = 2
            }, token).ConfigureAwait(false);
            DatabaseAssert.Equal(2, recoveryPage.Count, "recovery paging returns a bounded first page");
            DatabaseAssert.Equal(running.JobId, recoveryPage[0].JobId, "recovery paging uses ascending job id order");
            DatabaseAssert.Equal(exited.JobId, recoveryPage[1].JobId, "terminal mission job with undelivered completion remains in recovery");
            List<HarborJobRecord> recoveryTail = await _Driver.HarborJobs.EnumerateAsync(new HarborJobQuery
            {
                RecoveryOnly = true,
                RunnerId = runnerId,
                JobIdAfter = recoveryPage[1].JobId,
                Limit = 2
            }, token).ConfigureAwait(false);
            DatabaseAssert.Equal(1, recoveryTail.Count, "keyset paging returns the remaining unconfirmed terminal job");
            DatabaseAssert.Equal(unconfirmedTerminal.JobId, recoveryTail[0].JobId, "delivered terminal jobs are excluded from recovery pages");
            List<HarborJobRecord> unconfirmed = await _Driver.HarborJobs.EnumerateAsync(new HarborJobQuery
            {
                UnconfirmedOnly = true,
                RunnerId = runnerId
            }, token).ConfigureAwait(false);
            DatabaseAssert.Equal(2, unconfirmed.Count, "unconfirmed query contains only rows whose stop is not confirmed");
            DatabaseAssert.True(unconfirmed.Exists(record => record.JobId == running.JobId), "active job remains in the unconfirmed query");
            DatabaseAssert.True(unconfirmed.Exists(record => record.JobId == unconfirmedTerminal.JobId), "terminal job without stop confirmation remains in the unconfirmed query");
            DatabaseAssert.True(!unconfirmed.Exists(record => record.JobId == exited.JobId), "confirmed terminal delivery is fenced through recovery paging, not the unconfirmed-stop query");
            List<HarborJobRecord> legacyMissionFence = await _Driver.HarborJobs.EnumerateAsync(new HarborJobQuery
            {
                TenantId = "ten_jobs",
                UserId = "usr_jobs",
                UnboundMissionOnly = true,
                UnconfirmedOnly = true,
                Limit = 10
            }, token).ConfigureAwait(false);
            DatabaseAssert.Equal(1, legacyMissionFence.Count, "the legacy mission fence is scoped to jobs with no dock binding");
            DatabaseAssert.Equal(legacyMissionJob.JobId, legacyMissionFence[0].JobId,
                "the unbound-mission query includes legacy mission jobs but excludes generic operator jobs");

            DatabaseAssert.True(!await _Driver.HarborJobs.TryAcknowledgeCompletionDeliveredAsync(exited.JobId, exited.Revision - 1, token).ConfigureAwait(false),
                "stale completion revision is not acknowledged");
            DatabaseAssert.True(await _Driver.HarborJobs.TryAcknowledgeCompletionDeliveredAsync(exited.JobId, exited.Revision, token).ConfigureAwait(false),
                "exact confirmed-terminal revision is acknowledged");
            DatabaseAssert.True(!await _Driver.HarborJobs.TryAcknowledgeCompletionDeliveredAsync(exited.JobId, exited.Revision, token).ConfigureAwait(false),
                "completion delivery cannot be acknowledged twice");
            HarborJobRecord? acknowledged = await _Driver.HarborJobs.ReadAsync(exited.JobId, token).ConfigureAwait(false);
            DatabaseAssert.True(acknowledged!.CompletionDelivered, "completion acknowledgment is durable");
            DatabaseAssert.Equal(exited.Revision + 1, acknowledged.Revision, "completion acknowledgment advances the revision");

            running.State = HarborJobStateEnum.Uncertain;
            running.FailureReason = HarborJobCoordinator.ReasonResumeUnconfirmed;
            running.Revision = 5;
            DatabaseAssert.True(await _Driver.HarborJobs.TryUpdateAsync(running, token).ConfigureAwait(false), "uncertain restart fence is durable");
            HarborJobRecord? reconciled = await _Driver.HarborJobs.ReadAsync(running.JobId, token).ConfigureAwait(false);
            DatabaseAssert.Equal(HarborJobStateEnum.Uncertain, reconciled!.State, "unfinished job remains fenced, not reported as stopped");
            DatabaseAssert.Equal(HarborJobCoordinator.ReasonResumeUnconfirmed, reconciled.FailureReason, "uncertain reason recorded");
            DatabaseAssert.True(!reconciled.StopConfirmed, "uncertain runner state does not confirm process exit");

            HarborCompletionIntent completionIntent = new HarborCompletionIntent
            {
                JobId = running.JobId,
                Outcome = HarborCompletionOutcomeEnum.Transient,
                FailureReason = "provider recovery fixture",
                ReasonCode = "provider_recovery_fixture",
                CaptainState = CaptainStateEnum.Idle,
                FactId = "fact_harbor_" + suffix,
                EventId = "event_harbor_" + suffix,
                SignalId = "signal_harbor_" + suffix,
                Payload = "{\"reason\":\"provider recovery fixture\"}"
            };
            DatabaseAssert.True(await _Driver.HarborJobs.TryCreateCompletionIntentAsync(completionIntent, token).ConfigureAwait(false),
                "the first computed completion intent is stored");
            HarborCompletionIntent conflictingIntent = new HarborCompletionIntent
            {
                JobId = completionIntent.JobId,
                Outcome = HarborCompletionOutcomeEnum.Interrupted,
                FailureReason = "replacement must not overwrite",
                ReasonCode = "replacement_must_not_overwrite",
                CaptainState = CaptainStateEnum.Stalled,
                FactId = "fact_replacement_" + suffix,
                EventId = "event_replacement_" + suffix,
                SignalId = "signal_replacement_" + suffix,
                Payload = "{\"reason\":\"replacement\"}"
            };
            DatabaseAssert.True(!await _Driver.HarborJobs.TryCreateCompletionIntentAsync(conflictingIntent, token).ConfigureAwait(false),
                "a retry cannot replace the first completion decision");
            HarborCompletionIntent? storedIntent = await _Driver.HarborJobs.ReadCompletionIntentAsync(running.JobId, token).ConfigureAwait(false);
            DatabaseAssert.NotNull(storedIntent, "completion intent reads back by Harbor job id");
            DatabaseAssert.Equal(completionIntent.Outcome, storedIntent!.Outcome, "first outcome is preserved");
            DatabaseAssert.Equal(completionIntent.ReasonCode, storedIntent.ReasonCode, "first reason code is preserved");
            DatabaseAssert.Equal(completionIntent.FactId, storedIntent.FactId, "first stable fact id is preserved");
            DatabaseAssert.Equal(completionIntent.Payload, storedIntent.Payload, "first audit payload is preserved");
            DatabaseAssert.True(!storedIntent.Completed, "new completion intent remains pending until all effects finish");
            MissionAttemptFact stableFact = await _Driver.MissionAttemptFacts.CreateAsync(new MissionAttemptFact
            {
                Id = completionIntent.FactId,
                TenantId = running.TenantId,
                UserId = running.UserId,
                MissionId = running.MissionId,
                RootMissionId = running.MissionId,
                FactType = MissionAttemptFactTypeEnum.Failed,
                ReasonCode = completionIntent.ReasonCode,
                CreatedUtc = DateTime.UtcNow
            }, token).ConfigureAwait(false);
            MissionAttemptFact? readFact = await _Driver.MissionAttemptFacts.ReadAsync(completionIntent.FactId, token).ConfigureAwait(false);
            DatabaseAssert.NotNull(readFact, "the intent's stable fact id resolves to the append-only fact");
            DatabaseAssert.Equal(stableFact.Id, readFact!.Id, "stable fact read keeps the same id after replay");
            DatabaseAssert.Equal(completionIntent.ReasonCode, readFact.ReasonCode, "stable fact read keeps its machine reason");
            DatabaseAssert.True(await _Driver.HarborJobs.TryCompleteCompletionIntentAsync(running.JobId, token).ConfigureAwait(false),
                "completion intent can be acknowledged after effects finish");
            DatabaseAssert.True(await _Driver.HarborJobs.TryCompleteCompletionIntentAsync(running.JobId, token).ConfigureAwait(false),
                "repeating completion acknowledgment is safe");
            HarborJobRecord? untouched = await _Driver.HarborJobs.ReadAsync(exited.JobId, token).ConfigureAwait(false);
            DatabaseAssert.Equal(HarborJobStateEnum.Exited, untouched!.State, "terminal jobs keep their state after acknowledgment");

            await VerifyMissionBindingAsync(suffix, token).ConfigureAwait(false);
            await VerifyCaptainReleaseAsync(suffix, token).ConfigureAwait(false);

            using (DatabaseDriver reopened = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false))
            {
                HarborJobRecord? survived = await reopened.HarborJobs.ReadAsync(running.JobId, token).ConfigureAwait(false);
                DatabaseAssert.NotNull(survived, "Harbor job record survives provider reopen");
                DatabaseAssert.Equal(HarborJobStateEnum.Uncertain, survived!.State, "reopened state remains fenced");
                AssertUtc(running.AdmiralProcessStartedUtc, survived.AdmiralProcessStartedUtc, "Admiral process identity survives provider reopen");
                AssertUtc(running.RunnerProcessStartedUtc, survived.RunnerProcessStartedUtc, "runner process identity survives provider reopen");
                HarborCompletionIntent? reopenedIntent = await reopened.HarborJobs.ReadCompletionIntentAsync(running.JobId, token).ConfigureAwait(false);
                DatabaseAssert.NotNull(reopenedIntent, "completion intent survives provider reopen");
                DatabaseAssert.True(reopenedIntent!.Completed, "completion acknowledgment survives provider reopen");
                DatabaseAssert.Equal(completionIntent.FactId, reopenedIntent.FactId, "stable fact id survives provider reopen");
                DatabaseAssert.Equal(completionIntent.Payload, reopenedIntent.Payload, "audit payload survives provider reopen");
                MissionAttemptFact? reopenedFact = await reopened.MissionAttemptFacts.ReadAsync(completionIntent.FactId, token).ConfigureAwait(false);
                DatabaseAssert.NotNull(reopenedFact, "stable mission attempt fact survives provider reopen");
                DatabaseAssert.Equal(completionIntent.FactId, reopenedFact!.Id, "stable fact identifier survives provider reopen");
            }
        }

        private async Task VerifyCaptainReleaseAsync(string suffix, CancellationToken token)
        {
            CaptainStateEnum[] startingStates = { CaptainStateEnum.Idle, CaptainStateEnum.Stalled, CaptainStateEnum.Quarantined };
            foreach (CaptainStateEnum startingState in startingStates)
            {
                int expectedRecoveryAttempts = startingState == CaptainStateEnum.Quarantined ? 3 : 0;
                await VerifyCaptainReleaseOutcomeAsync(suffix + "_" + startingState.ToString().ToLowerInvariant(),
                    startingState, expectedRecoveryAttempts, token).ConfigureAwait(false);
            }
        }

        private async Task VerifyCaptainReleaseOutcomeAsync(string suffix, CaptainStateEnum startingState, int expectedRecoveryAttempts,
            CancellationToken token)
        {
            string tenantId = Armada.Core.Constants.DefaultTenantId;
            string userId = Armada.Core.Constants.DefaultUserId;
            Vessel vessel = await _Driver.Vessels.CreateAsync(new Vessel("Harbor release " + suffix, "https://example.invalid/harbor-release")
            {
                TenantId = tenantId,
                UserId = userId
            }, token).ConfigureAwait(false);
            Dock dock = await _Driver.Docks.CreateAsync(new Dock(vessel.Id)
            {
                TenantId = tenantId,
                UserId = userId,
                WorktreePath = "/tmp/harbor-release-" + suffix
            }, token).ConfigureAwait(false);
            Mission mission = new Mission("Harbor release " + suffix)
            {
                TenantId = tenantId,
                UserId = userId,
                VesselId = vessel.Id,
                Status = MissionStatusEnum.Pending,
                AssignmentState = MissionAssignmentStateEnum.Pending,
                DockId = dock.Id
            };
            Captain captain = new Captain("Harbor release " + suffix)
            {
                TenantId = tenantId,
                UserId = userId,
                State = startingState,
                RecoveryAttempts = 3,
                CurrentMissionId = mission.Id,
                CurrentDockId = dock.Id,
                ProcessId = 9310,
                ProcessStartedUtc = DateTime.UtcNow
            };
            mission.CaptainId = captain.Id;
            HarborJobRecord job = NewRecord("hjob_release_" + suffix, "hbr_release_" + suffix, HarborJobStateEnum.Exited, 1);
            job.TenantId = tenantId;
            job.UserId = userId;
            job.MissionId = mission.Id;
            job.CaptainId = captain.Id;
            job.DockId = dock.Id;
            job.AdmiralProcessId = captain.ProcessId;
            job.AdmiralProcessStartedUtc = captain.ProcessStartedUtc;
            job.StopConfirmed = true;
            job.CompletedUtc = DateTime.UtcNow;
            mission.HarborJobId = job.JobId;

            await _Driver.Captains.CreateAsync(captain, token).ConfigureAwait(false);
            await _Driver.Missions.CreateAsync(mission, token).ConfigureAwait(false);
            await _Driver.HarborJobs.CreateAsync(job, token).ConfigureAwait(false);

            HarborRunnerEnrollmentService enrollments = new HarborRunnerEnrollmentService(_Driver);
            HarborRunnerSessionRegistry registry = new HarborRunnerSessionRegistry(true, enrollments);
            HarborJobCoordinator coordinator = new HarborJobCoordinator(registry, enrollments, _Driver.HarborJobs);
            HarborJobRecoverySnapshotHost recoveryHost = new HarborJobRecoverySnapshotHost();
            DatabaseAssert.True(await coordinator.InitializeRecoveryAsync(recoveryHost, token).ConfigureAwait(false),
                "the coordinator reads provider-backed recovery snapshots");
            HarborJobSnapshot? snapshot = null;
            foreach (HarborJobSnapshot candidate in coordinator.GetSnapshots())
            {
                if (String.Equals(candidate.JobId, job.JobId, StringComparison.Ordinal)) snapshot = candidate;
            }
            DatabaseAssert.NotNull(snapshot, "the exact confirmed job is available as a public recovery snapshot");
            DatabaseAssert.True(recoveryHost.DeliveredJobs.Exists(candidate => String.Equals(candidate.JobId, job.JobId, StringComparison.Ordinal)),
                "the recovery host observes the terminal attempt without changing it");

            Captain? staleStamp = await _Driver.Captains.ReadAsync(captain.Id, token).ConfigureAwait(false);
            staleStamp!.LastUpdateUtc = staleStamp.LastUpdateUtc.AddSeconds(-1);
            DatabaseAssert.True(!await _Driver.Captains.TryReleaseHarborAttemptAsync(snapshot!, staleStamp, token).ConfigureAwait(false),
                "a stale captain write stamp cannot release the Harbor attempt");

            Mission newerMission = await _Driver.Missions.CreateAsync(new Mission("Harbor newer assignment " + suffix)
            {
                TenantId = tenantId,
                UserId = userId,
                VesselId = vessel.Id,
                Status = MissionStatusEnum.Pending,
                AssignmentState = MissionAssignmentStateEnum.Pending
            }, token).ConfigureAwait(false);
            Captain? reassigned = await _Driver.Captains.ReadAsync(captain.Id, token).ConfigureAwait(false);
            reassigned!.CurrentMissionId = newerMission.Id;
            await _Driver.Captains.UpdateAsync(reassigned, token).ConfigureAwait(false);
            DatabaseAssert.True(!await _Driver.Captains.TryReleaseHarborAttemptAsync(snapshot!, reassigned, token).ConfigureAwait(false),
                "a confirmed old completion cannot release a captain assigned to newer work");

            reassigned.CurrentMissionId = mission.Id;
            await _Driver.Captains.UpdateAsync(reassigned, token).ConfigureAwait(false);
            Captain? currentCaptain = await _Driver.Captains.ReadAsync(captain.Id, token).ConfigureAwait(false);
            DatabaseAssert.True(await _Driver.Captains.TryReleaseHarborAttemptAsync(snapshot!, currentCaptain!, token).ConfigureAwait(false),
                "the exact confirmed attempt and current captain stamp release together");
            Captain? released = await _Driver.Captains.ReadAsync(captain.Id, token).ConfigureAwait(false);
            DatabaseAssert.True(released!.CurrentMissionId == null && released.CurrentDockId == null
                && released.ProcessId == null && released.ProcessStartedUtc == null,
                "release clears only the exact completed process binding");
            DatabaseAssert.Equal(startingState, released.State, "release keeps the captain's established outcome state");
            DatabaseAssert.Equal(expectedRecoveryAttempts, released.RecoveryAttempts,
                "release resets retry count unless the established outcome is quarantine");
        }

        private sealed class HarborJobRecoverySnapshotHost : IHarborJobRecoveryHost
        {
            public List<HarborJobSnapshot> DeliveredJobs { get; } = new List<HarborJobSnapshot>();

            public Task<HarborJobRecoveryResult> RestoreAsync(HarborJobSnapshot job, CancellationToken token = default)
            {
                return Task.FromResult(new HarborJobRecoveryResult(HarborJobRecoveryDispositionEnum.Uncertain));
            }

            public Task<HarborJobRecoveryResult> TryAdoptAsync(HarborJobSnapshot job, HarborRunnerSession session, CancellationToken token = default)
            {
                return Task.FromResult(new HarborJobRecoveryResult(HarborJobRecoveryDispositionEnum.Uncertain));
            }

            public Task<bool> DeliverCompletionAsync(HarborJobSnapshot job, CancellationToken token = default)
            {
                DeliveredJobs.Add(job);
                return Task.FromResult(false);
            }
        }

        private async Task VerifyMissionBindingAsync(string suffix, CancellationToken token)
        {
            string tenantId = Armada.Core.Constants.DefaultTenantId;
            string userId = Armada.Core.Constants.DefaultUserId;
            Vessel vessel = await _Driver.Vessels.CreateAsync(new Vessel("Harbor binding " + suffix, "https://example.invalid/harbor-binding")
            {
                TenantId = tenantId,
                UserId = userId
            }, token).ConfigureAwait(false);
            string dockId = "dock_bind_" + suffix;
            Dock dock = await _Driver.Docks.CreateAsync(new Dock(vessel.Id)
            {
                Id = dockId,
                TenantId = tenantId,
                UserId = userId,
                WorktreePath = "/tmp/harbor-binding-" + suffix
            }, token).ConfigureAwait(false);
            Captain captain = await _Driver.Captains.CreateAsync(new Captain("Harbor binding " + suffix)
            {
                TenantId = tenantId,
                UserId = userId,
                State = CaptainStateEnum.Working
            }, token).ConfigureAwait(false);
            Mission mission = await _Driver.Missions.CreateAsync(new Mission("Harbor binding " + suffix)
            {
                TenantId = tenantId,
                UserId = userId,
                VesselId = vessel.Id,
                CaptainId = captain.Id,
                DockId = dock.Id,
                Status = MissionStatusEnum.Assigned,
                AssignmentState = MissionAssignmentStateEnum.Assigned
            }, token).ConfigureAwait(false);
            captain.CurrentMissionId = mission.Id;
            captain.CurrentDockId = "dock_mismatch_" + suffix;
            await _Driver.Captains.UpdateAsync(captain, token).ConfigureAwait(false);

            int processId = 9300;
            DateTime processStartedUtc = DateTime.UtcNow;
            HarborJobRecord pendingJob = NewRecord("hjob_bind_" + suffix, "hbr_bind_" + suffix, HarborJobStateEnum.Pending, 1);
            pendingJob.TenantId = tenantId;
            pendingJob.UserId = userId;
            pendingJob.MissionId = mission.Id;
            pendingJob.CaptainId = captain.Id;
            pendingJob.DockId = dock.Id;
            pendingJob.AdmiralProcessId = processId;
            pendingJob.AdmiralProcessStartedUtc = processStartedUtc;
            await _Driver.HarborJobs.CreateAsync(pendingJob, token).ConfigureAwait(false);
            HarborMissionBindingUpdate update = new HarborMissionBindingUpdate
            {
                MissionId = mission.Id,
                CaptainId = captain.Id,
                DockId = dock.Id,
                JobId = pendingJob.JobId,
                TenantId = tenantId,
                UserId = userId,
                AdmiralProcessId = processId,
                ProcessStartedUtc = processStartedUtc
            };

            DatabaseAssert.True(!await _Driver.Missions.TryBindHarborJobAsync(update, token).ConfigureAwait(false),
                "a captain dock mismatch refuses the atomic Harbor binding");
            Mission? rolledBack = await _Driver.Missions.ReadAsync(mission.Id, token).ConfigureAwait(false);
            DatabaseAssert.True(rolledBack!.HarborJobId == null && rolledBack.ProcessId == null,
                "mission update rolls back when the captain fence fails");

            captain.CurrentDockId = dock.Id;
            captain.State = CaptainStateEnum.Stalled;
            await _Driver.Captains.UpdateAsync(captain, token).ConfigureAwait(false);
            DatabaseAssert.True(!await _Driver.Missions.TryBindHarborJobAsync(update, token).ConfigureAwait(false),
                "a captain that became stalled cannot launch with unchanged mission and dock pointers");
            Mission? stalledBind = await _Driver.Missions.ReadAsync(mission.Id, token).ConfigureAwait(false);
            DatabaseAssert.True(stalledBind!.HarborJobId == null && stalledBind.ProcessId == null,
                "the mission update rolls back when the captain state fence fails");

            captain.State = CaptainStateEnum.Working;
            await _Driver.Captains.UpdateAsync(captain, token).ConfigureAwait(false);
            DatabaseAssert.True(await _Driver.Missions.TryBindHarborJobAsync(update, token).ConfigureAwait(false),
                "the exact assigned mission, captain, dock and owner bind together");
            Mission? boundMission = await _Driver.Missions.ReadAsync(mission.Id, token).ConfigureAwait(false);
            Captain? boundCaptain = await _Driver.Captains.ReadAsync(captain.Id, token).ConfigureAwait(false);
            DatabaseAssert.Equal(pendingJob.JobId, boundMission!.HarborJobId, "mission owns the durable Harbor job");
            DatabaseAssert.Equal(processId, boundMission.ProcessId ?? 0, "mission receives the synthetic process id");
            DatabaseAssert.Equal(processId, boundCaptain!.ProcessId ?? 0, "captain receives the same synthetic process id");
            DatabaseAssert.Equal(dock.Id, boundCaptain.CurrentDockId, "captain remains bound to the same dock");

            HarborJobRecord replacement = NewRecord("hjob_rebind_" + suffix, "hbr_bind_" + suffix, HarborJobStateEnum.Pending, 1);
            replacement.TenantId = tenantId;
            replacement.UserId = userId;
            replacement.MissionId = mission.Id;
            replacement.CaptainId = captain.Id;
            replacement.DockId = dock.Id;
            replacement.AdmiralProcessId = processId + 1;
            replacement.AdmiralProcessStartedUtc = processStartedUtc.AddSeconds(1);
            await _Driver.HarborJobs.CreateAsync(replacement, token).ConfigureAwait(false);
            HarborMissionBindingUpdate conflicting = new HarborMissionBindingUpdate
            {
                MissionId = mission.Id,
                CaptainId = captain.Id,
                DockId = dock.Id,
                JobId = replacement.JobId,
                TenantId = tenantId,
                UserId = userId,
                AdmiralProcessId = processId + 1,
                ProcessStartedUtc = processStartedUtc.AddSeconds(1)
            };
            DatabaseAssert.True(!await _Driver.Missions.TryBindHarborJobAsync(conflicting, token).ConfigureAwait(false),
                "a replacement cannot overwrite an already-bound attempt when no prior job was expected");
            Mission? afterConflict = await _Driver.Missions.ReadAsync(mission.Id, token).ConfigureAwait(false);
            Captain? captainAfterConflict = await _Driver.Captains.ReadAsync(captain.Id, token).ConfigureAwait(false);
            DatabaseAssert.Equal(pendingJob.JobId, afterConflict!.HarborJobId, "failed replacement leaves the mission's job unchanged");
            DatabaseAssert.Equal(processId, afterConflict.ProcessId ?? 0, "failed replacement leaves mission process identity unchanged");
            DatabaseAssert.Equal(processId, captainAfterConflict!.ProcessId ?? 0, "failed replacement leaves captain process identity unchanged");

            pendingJob.State = HarborJobStateEnum.Exited;
            pendingJob.ExitCode = 0;
            pendingJob.StopConfirmed = true;
            pendingJob.CompletionDelivered = true;
            pendingJob.CompletedUtc = DateTime.UtcNow;
            pendingJob.Revision++;
            DatabaseAssert.True(await _Driver.HarborJobs.TryUpdateAsync(pendingJob, token).ConfigureAwait(false),
                "the binding fixture job is made terminal after the fence check");
            replacement.State = HarborJobStateEnum.Exited;
            replacement.ExitCode = 0;
            replacement.StopConfirmed = true;
            replacement.CompletionDelivered = true;
            replacement.CompletedUtc = DateTime.UtcNow;
            replacement.Revision++;
            DatabaseAssert.True(await _Driver.HarborJobs.TryUpdateAsync(replacement, token).ConfigureAwait(false),
                "the conflicting fixture job is made terminal after the fence check");
            boundMission!.Status = MissionStatusEnum.Cancelled;
            await _Driver.Missions.UpdateAsync(boundMission, token).ConfigureAwait(false);
            boundCaptain!.State = CaptainStateEnum.Idle;
            boundCaptain.CurrentMissionId = null;
            boundCaptain.CurrentDockId = null;
            boundCaptain.ProcessId = null;
            boundCaptain.ProcessStartedUtc = null;
            await _Driver.Captains.UpdateAsync(boundCaptain, token).ConfigureAwait(false);
            dock.Active = false;
            await _Driver.Docks.UpdateAsync(dock, token).ConfigureAwait(false);
        }

        private static HarborJobRecord NewRecord(string jobId, string runnerId, HarborJobStateEnum state, long revision)
        {
            string suffix = runnerId.Substring("hbr_jobs_".Length);
            DateTime now = DateTime.UtcNow;
            return new HarborJobRecord
            {
                JobId = jobId,
                RunnerId = runnerId,
                LaunchKey = "mission:" + suffix,
                TenantId = "ten_jobs",
                UserId = "usr_jobs",
                EnrollmentGeneration = 4,
                SessionGeneration = 2,
                State = state,
                MissionId = "msn_" + suffix,
                CaptainId = "cpt_" + suffix,
                DockId = "dock_" + suffix,
                AdmiralProcessId = 9101,
                AdmiralProcessStartedUtc = now.AddSeconds(-20),
                ProcessId = state == HarborJobStateEnum.Pending ? null : 4242,
                RunnerProcessStartedUtc = state == HarborJobStateEnum.Pending ? null : now.AddSeconds(-10),
                Revision = revision,
                CreatedUtc = now,
                LastUpdateUtc = now
            };
        }

        private static void AssertUtc(DateTime? expected, DateTime? actual, string message)
        {
            if (!expected.HasValue) throw new Exception(message + " expected time is present");
            if (!actual.HasValue) throw new Exception(message + " actual time is present");
            DateTime actualUtc = actual!.Value.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(actual.Value, DateTimeKind.Utc)
                : actual.Value.ToUniversalTime();
            DatabaseAssert.True((actualUtc - expected!.Value).Duration() <= TimeSpan.FromMilliseconds(1), message + " round-trips in UTC");
        }

        private static int VersionFor(DatabaseTypeEnum type)
        {
            return type switch
            {
                DatabaseTypeEnum.Sqlite => 123,
                DatabaseTypeEnum.Postgresql => 127,
                DatabaseTypeEnum.Mysql => 116,
                DatabaseTypeEnum.SqlServer => 119,
                _ => throw new NotSupportedException()
            };
        }
    }
}
