namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// Supervised cutover, rollback and interrupted-restart recovery against real operating-system processes.
    /// </summary>
    public sealed class SelfDeployCutoverCoordinatorTests : TestSuite
    {
        private const string HealthyScript = "echo ready > \"$ARMADA_TEST_HEALTH_DIR/$$.ready\"\nexec sleep 60\n";
        private const string NeverHealthyScript = "exec sleep 60\n";
        private const string CrashScript = "exit 3\n";

        /// <inheritdoc />
        public override string Name => "Self Deploy Cutover Coordinator";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("ProcessHost_ReusedProcessIdWithDifferentStartTime_IsNeverSignalled", async () =>
            {
                if (SkipWindows("ProcessHost_ReusedProcessIdWithDifferentStartTime_IsNeverSignalled")) return;
                using (CutoverFixture fixture = new CutoverFixture())
                {
                    SelfDeployProcessIdentity live = fixture.StartShell("exec sleep 60");
                    SelfDeployProcessIdentity reused = new SelfDeployProcessIdentity
                    {
                        ProcessId = live.ProcessId,
                        StartedUtc = live.StartedUtc.AddHours(-1)
                    };
                    AssertEqual(SelfDeployProcessStateEnum.Exited, fixture.Host.GetState(reused), "a reused id reads as exited");
                    bool confirmed = await fixture.Host.TerminateAsync(reused, false, TimeSpan.FromSeconds(1));
                    AssertTrue(confirmed, "an exited identity is already confirmed gone");
                    AssertEqual(SelfDeployProcessStateEnum.Running, fixture.Host.GetState(live), "the process now holding the id must not be signalled");
                }
            });

            await RunTest("ProcessHost_ChildThatExitsImmediately_IsStartedThenExitedNeverLaunchFailed", async () =>
            {
                if (SkipWindows("ProcessHost_ChildThatExitsImmediately_IsStartedThenExitedNeverLaunchFailed")) return;
                using (CutoverFixture fixture = new CutoverFixture())
                {
                    // A child can exit and be reaped before its start time is read. It still started, so the
                    // launch returns an identity that reads as exited instead of failing the launch.
                    int launchFailures = 0;
                    string firstFailure = String.Empty;
                    object gate = new object();
                    Task[] workers = new Task[8];
                    for (int w = 0; w < workers.Length; w++)
                    {
                        workers[w] = Task.Run(async () =>
                        {
                            for (int i = 0; i < 40; i++)
                            {
                                try
                                {
                                    SelfDeployProcessIdentity child = fixture.StartShell("exit 0");
                                    SelfDeployProcessStateEnum state = await fixture.Host.WaitForExitAsync(child, TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(20));
                                    if (state != SelfDeployProcessStateEnum.Exited) throw new InvalidOperationException("child read " + state + " after it exited");
                                }
                                catch (SelfDeployCutoverException ex)
                                {
                                    lock (gate)
                                    {
                                        launchFailures++;
                                        if (firstFailure.Length == 0) firstFailure = ex.FailureReason + ": " + ex.InnerException?.Message;
                                    }
                                }
                            }
                        });
                    }
                    await Task.WhenAll(workers);
                    AssertEqual(0, launchFailures, "an immediately exiting child is not a failed launch (" + firstFailure + ")");
                }
            });

            await RunTest("ProcessHost_TerminatedChild_IsConfirmedExitedEveryTime", async () =>
            {
                if (SkipWindows("ProcessHost_TerminatedChild_IsConfirmedExitedEveryTime")) return;
                using (CutoverFixture fixture = new CutoverFixture())
                {
                    for (int i = 0; i < 40; i++)
                    {
                        SelfDeployProcessIdentity child = fixture.StartShell("exec sleep 60");
                        if (i % 4 != 0) await Task.Delay(i % 4 * 10);
                        bool confirmed = await fixture.Host.TerminateAsync(child, true, TimeSpan.FromSeconds(5));
                        AssertTrue(confirmed, "termination " + i + " confirmed exit; final state " + fixture.Host.GetState(child));
                    }
                }
            });

            await RunTest("Supervise_HealthyCandidate_CommitsOnlyAfterOldExitConfirmed", async () =>
            {
                if (SkipWindows("Supervise_HealthyCandidate_CommitsOnlyAfterOldExitConfirmed")) return;
                using (CutoverFixture fixture = new CutoverFixture())
                {
                    fixture.Options.OldProcessExitTimeout = TimeSpan.FromSeconds(10);
                    SelfDeployProcessIdentity old = fixture.StartShell("sleep 1");
                    SelfDeployRestartRecord record = await fixture.CreateRecordAsync(SelfDeployRestartStateEnum.Prepared, old,
                        await fixture.CreateArtifactAsync("candidate", HealthyScript),
                        await fixture.CreateArtifactAsync("rollback", HealthyScript));

                    Task<SelfDeployCutoverResult> supervise = fixture.CreateCoordinator().SuperviseAsync(record.OperationId, fixture.Self());
                    await fixture.RequestExitAsync(record.OperationId);
                    SelfDeployCutoverResult result = await supervise;

                    AssertEqual(SelfDeployRestartStateEnum.Committed, result.State, "candidate commits");
                    SelfDeployRestartRecord final = await fixture.ReadRecordAsync();
                    AssertNotNull(final.OldExitConfirmedUtc, "old exit confirmation recorded");
                    AssertNotNull(final.CandidateProcess, "candidate identity recorded");
                    AssertEqual(SelfDeployProcessStateEnum.Exited, fixture.Host.GetState(old), "old admiral is gone");
                    AssertEqual(SelfDeployProcessStateEnum.Running, fixture.Host.GetState(final.CandidateProcess!), "candidate owns the service");
                    AssertTrue(final.CandidateProcess!.StartedUtc >= final.OldExitConfirmedUtc!.Value - SelfDeployProcessHost.StartTimeTolerance,
                        "candidate started only after the old exit was confirmed");
                    AssertTrue(IndexOf(final, SelfDeployRestartStateEnum.OldStopped) < IndexOf(final, SelfDeployRestartStateEnum.CandidateStarting),
                        "old stop is recorded before candidate launch");
                    AssertEqual(1, fixture.Planner.ServerLaunches.Count, "only the candidate launched");
                    AssertEqual(final.Candidate.Digest, fixture.Planner.ServerLaunches[0], "launched artifact is the candidate");
                }
            });

            await RunTest("Supervise_OldAdmiralIgnoresExit_TerminatedByIdentityBeforeCandidate", async () =>
            {
                if (SkipWindows("Supervise_OldAdmiralIgnoresExit_TerminatedByIdentityBeforeCandidate")) return;
                using (CutoverFixture fixture = new CutoverFixture())
                {
                    SelfDeployProcessIdentity old = fixture.StartShell("exec sleep 60");
                    SelfDeployRestartRecord record = await fixture.CreateRecordAsync(SelfDeployRestartStateEnum.Prepared, old,
                        await fixture.CreateArtifactAsync("candidate", HealthyScript),
                        await fixture.CreateArtifactAsync("rollback", HealthyScript));

                    Task<SelfDeployCutoverResult> supervise = fixture.CreateCoordinator().SuperviseAsync(record.OperationId, fixture.Self());
                    await fixture.RequestExitAsync(record.OperationId);
                    SelfDeployCutoverResult result = await supervise;

                    AssertEqual(SelfDeployRestartStateEnum.Committed, result.State, "candidate commits");
                    SelfDeployRestartRecord final = await fixture.ReadRecordAsync();
                    AssertEqual("old_process_terminated_by_identity", ReasonOf(final, SelfDeployRestartStateEnum.OldStopped), "hung admiral stopped by identity");
                    AssertEqual(SelfDeployProcessStateEnum.Exited, fixture.Host.GetState(old), "hung admiral is gone before candidate launch");
                }
            });

            await RunTest("Supervise_ExitNeverRequested_AbortsWithoutTouchingOldAdmiral", async () =>
            {
                if (SkipWindows("Supervise_ExitNeverRequested_AbortsWithoutTouchingOldAdmiral")) return;
                using (CutoverFixture fixture = new CutoverFixture())
                {
                    fixture.Options.HandshakeTimeout = TimeSpan.FromMilliseconds(600);
                    SelfDeployProcessIdentity old = fixture.StartShell("exec sleep 60");
                    SelfDeployRestartRecord record = await fixture.CreateRecordAsync(SelfDeployRestartStateEnum.Prepared, old,
                        await fixture.CreateArtifactAsync("candidate", HealthyScript),
                        await fixture.CreateArtifactAsync("rollback", HealthyScript));

                    SelfDeployCutoverResult result = await fixture.CreateCoordinator().SuperviseAsync(record.OperationId, fixture.Self());

                    AssertEqual(SelfDeployRestartStateEnum.Aborted, result.State, "cutover aborts");
                    AssertEqual("exit_request_timeout", result.Reason, "abort reason");
                    AssertEqual(SelfDeployProcessStateEnum.Running, fixture.Host.GetState(old), "old admiral stays the owner");
                    AssertEqual(0, fixture.Planner.ServerLaunches.Count, "nothing launched");
                }
            });

            await RunTest("Supervise_OldAdmiralStateUnverified_FailsWithoutLaunching", async () =>
            {
                if (SkipWindows("Supervise_OldAdmiralStateUnverified_FailsWithoutLaunching")) return;
                using (CutoverFixture fixture = new CutoverFixture())
                {
                    SelfDeployProcessIdentity old = fixture.StartShell("exec sleep 60");
                    SelfDeployRestartRecord record = await fixture.CreateRecordAsync(SelfDeployRestartStateEnum.Prepared, old,
                        await fixture.CreateArtifactAsync("candidate", HealthyScript),
                        await fixture.CreateArtifactAsync("rollback", HealthyScript));

                    Task<SelfDeployCutoverResult> supervise = fixture.CreateCoordinator().SuperviseAsync(record.OperationId, fixture.Self());
                    await fixture.WaitForStateAsync(SelfDeployRestartStateEnum.Armed);
                    fixture.Host.UnverifiedProcessIds.Add(old.ProcessId);
                    await fixture.RequestExitAsync(record.OperationId);
                    SelfDeployCutoverResult result = await supervise;

                    AssertEqual(SelfDeployRestartStateEnum.Failed, result.State, "unverifiable owner fails closed");
                    AssertEqual("old_process_state_unverified", result.Reason, "failure reason");
                    AssertEqual(0, fixture.Planner.ServerLaunches.Count, "nothing launched while ownership is unknown");
                    fixture.Host.UnverifiedProcessIds.Clear();
                    AssertEqual(SelfDeployProcessStateEnum.Running, fixture.Host.GetState(old), "unverified process was not signalled");
                }
            });

            await RunTest("Supervise_CandidateNeverHealthy_StopsCandidateThenRollsBack", async () =>
            {
                if (SkipWindows("Supervise_CandidateNeverHealthy_StopsCandidateThenRollsBack")) return;
                using (CutoverFixture fixture = new CutoverFixture())
                {
                    SelfDeployProcessIdentity old = fixture.StartShell("sleep 1");
                    fixture.Options.OldProcessExitTimeout = TimeSpan.FromSeconds(10);
                    // The health timeout is waited out once for the candidate; the healthy rollback reports ready
                    // well inside it.
                    fixture.Options.HealthTimeout = TimeSpan.FromMilliseconds(1500);
                    SelfDeployRestartRecord record = await fixture.CreateRecordAsync(SelfDeployRestartStateEnum.Prepared, old,
                        await fixture.CreateArtifactAsync("candidate", NeverHealthyScript),
                        await fixture.CreateArtifactAsync("rollback", HealthyScript));

                    Task<SelfDeployCutoverResult> supervise = fixture.CreateCoordinator().SuperviseAsync(record.OperationId, fixture.Self());
                    await fixture.RequestExitAsync(record.OperationId);
                    SelfDeployCutoverResult result = await supervise;

                    AssertEqual(SelfDeployRestartStateEnum.RolledBack, result.State, "rollback owns the service");
                    SelfDeployRestartRecord final = await fixture.ReadRecordAsync();
                    AssertContains("health_timeout", ReasonOf(final, SelfDeployRestartStateEnum.RollingBack), "health failure recorded");
                    AssertEqual(SelfDeployProcessStateEnum.Exited, fixture.Host.GetState(final.CandidateProcess!), "unhealthy candidate stopped");
                    AssertEqual(SelfDeployProcessStateEnum.Running, fixture.Host.GetState(final.RollbackProcess!), "rollback runs");
                    AssertEqual(2, fixture.Planner.ServerLaunches.Count, "candidate then rollback launched");
                    AssertEqual(final.Rollback.Digest, fixture.Planner.ServerLaunches[1], "second launch is the rollback artifact");
                }
            });

            await RunTest("Supervise_CandidateCrashes_RollsBack", async () =>
            {
                if (SkipWindows("Supervise_CandidateCrashes_RollsBack")) return;
                using (CutoverFixture fixture = new CutoverFixture())
                {
                    SelfDeployProcessIdentity old = fixture.StartShell("exec sleep 60");
                    SelfDeployRestartRecord record = await fixture.CreateRecordAsync(SelfDeployRestartStateEnum.Prepared, old,
                        await fixture.CreateArtifactAsync("candidate", CrashScript),
                        await fixture.CreateArtifactAsync("rollback", HealthyScript));

                    Task<SelfDeployCutoverResult> supervise = fixture.CreateCoordinator().SuperviseAsync(record.OperationId, fixture.Self());
                    await fixture.RequestExitAsync(record.OperationId);
                    SelfDeployCutoverResult result = await supervise;

                    AssertEqual(SelfDeployRestartStateEnum.RolledBack, result.State, "crashed candidate rolls back");
                    SelfDeployRestartRecord final = await fixture.ReadRecordAsync();
                    AssertEqual("candidate_process_exited_before_healthy", ReasonOf(final, SelfDeployRestartStateEnum.RollingBack), "crash recorded");
                }
            });

            await RunTest("Supervise_CandidateAdvancedSchema_BlocksRollbackToOldBinary", async () =>
            {
                if (SkipWindows("Supervise_CandidateAdvancedSchema_BlocksRollbackToOldBinary")) return;
                using (CutoverFixture fixture = new CutoverFixture())
                {
                    SelfDeployProcessIdentity old = fixture.StartShell("exec sleep 60");
                    SelfDeployRestartRecord record = await fixture.CreateRecordAsync(SelfDeployRestartStateEnum.Prepared, old,
                        await fixture.CreateArtifactAsync("candidate", CrashScript),
                        await fixture.CreateArtifactAsync("rollback", HealthyScript));
                    fixture.Schema.Version = record.SchemaVersionBefore + 1;

                    Task<SelfDeployCutoverResult> supervise = fixture.CreateCoordinator().SuperviseAsync(record.OperationId, fixture.Self());
                    await fixture.RequestExitAsync(record.OperationId);
                    SelfDeployCutoverResult result = await supervise;

                    AssertEqual(SelfDeployRestartStateEnum.RollbackBlocked, result.State, "rollback blocked");
                    AssertEqual("schema_advanced_restore_required", result.Reason, "blocked reason");
                    AssertEqual(1, fixture.Planner.ServerLaunches.Count, "old binary is not started against the advanced schema");
                }
            });

            await RunTest("Supervise_UnreadableSchema_BlocksRollback", async () =>
            {
                if (SkipWindows("Supervise_UnreadableSchema_BlocksRollback")) return;
                using (CutoverFixture fixture = new CutoverFixture())
                {
                    SelfDeployProcessIdentity old = fixture.StartShell("exec sleep 60");
                    SelfDeployRestartRecord record = await fixture.CreateRecordAsync(SelfDeployRestartStateEnum.Prepared, old,
                        await fixture.CreateArtifactAsync("candidate", CrashScript),
                        await fixture.CreateArtifactAsync("rollback", HealthyScript));
                    fixture.Schema.Throw = true;

                    Task<SelfDeployCutoverResult> supervise = fixture.CreateCoordinator().SuperviseAsync(record.OperationId, fixture.Self());
                    await fixture.RequestExitAsync(record.OperationId);
                    SelfDeployCutoverResult result = await supervise;

                    AssertEqual(SelfDeployRestartStateEnum.RollbackBlocked, result.State, "unknown schema fails closed");
                    AssertEqual("schema_version_unreadable", result.Reason, "blocked reason");
                }
            });

            await RunTest("Supervise_CandidateArtifactTamperedAfterArm_NeverLaunchesCandidate", async () =>
            {
                if (SkipWindows("Supervise_CandidateArtifactTamperedAfterArm_NeverLaunchesCandidate")) return;
                using (CutoverFixture fixture = new CutoverFixture())
                {
                    SelfDeployProcessIdentity old = fixture.StartShell("exec sleep 60");
                    SelfDeployRestartRecord record = await fixture.CreateRecordAsync(SelfDeployRestartStateEnum.Prepared, old,
                        await fixture.CreateArtifactAsync("candidate", HealthyScript),
                        await fixture.CreateArtifactAsync("rollback", HealthyScript));

                    Task<SelfDeployCutoverResult> supervise = fixture.CreateCoordinator().SuperviseAsync(record.OperationId, fixture.Self());
                    await fixture.WaitForStateAsync(SelfDeployRestartStateEnum.Armed);
                    SelfDeployTestDirectory.MakeWritable(record.Candidate.Directory);
                    File.AppendAllText(Path.Combine(record.Candidate.Directory, "role.txt"), "tampered");
                    await fixture.RequestExitAsync(record.OperationId);
                    SelfDeployCutoverResult result = await supervise;

                    AssertEqual(SelfDeployRestartStateEnum.RolledBack, result.State, "tampered candidate is refused");
                    SelfDeployRestartRecord final = await fixture.ReadRecordAsync();
                    AssertEqual("candidate_artifact_digest_mismatch", ReasonOf(final, SelfDeployRestartStateEnum.RollingBack), "digest mismatch recorded");
                    AssertEqual(1, fixture.Planner.ServerLaunches.Count, "only the rollback launched");
                    AssertEqual(final.Rollback.Digest, fixture.Planner.ServerLaunches[0], "rollback artifact launched");
                }
            });

            await RunTest("Supervise_RollbackUnhealthy_FailsAndStopsRollback", async () =>
            {
                if (SkipWindows("Supervise_RollbackUnhealthy_FailsAndStopsRollback")) return;
                using (CutoverFixture fixture = new CutoverFixture())
                {
                    // Only the never-healthy rollback waits on the health timeout; the candidate exits at once.
                    fixture.Options.HealthTimeout = TimeSpan.FromMilliseconds(750);
                    SelfDeployProcessIdentity old = fixture.StartShell("exec sleep 60");
                    SelfDeployRestartRecord record = await fixture.CreateRecordAsync(SelfDeployRestartStateEnum.Prepared, old,
                        await fixture.CreateArtifactAsync("candidate", CrashScript),
                        await fixture.CreateArtifactAsync("rollback", NeverHealthyScript));

                    Task<SelfDeployCutoverResult> supervise = fixture.CreateCoordinator().SuperviseAsync(record.OperationId, fixture.Self());
                    await fixture.RequestExitAsync(record.OperationId);
                    SelfDeployCutoverResult result = await supervise;

                    AssertEqual(SelfDeployRestartStateEnum.Failed, result.State, "no healthy owner");
                    AssertContains("rollback_health_timeout", result.Reason, "failure reason");
                    SelfDeployRestartRecord final = await fixture.ReadRecordAsync();
                    AssertEqual(SelfDeployProcessStateEnum.Exited, fixture.Host.GetState(final.RollbackProcess!), "unhealthy rollback stopped");
                }
            });

            await RunTest("Supervise_SupervisorLockHeld_ChangesNothing", async () =>
            {
                if (SkipWindows("Supervise_SupervisorLockHeld_ChangesNothing")) return;
                using (CutoverFixture fixture = new CutoverFixture())
                {
                    SelfDeployProcessIdentity old = fixture.StartShell("exec sleep 60");
                    SelfDeployRestartRecord record = await fixture.CreateRecordAsync(SelfDeployRestartStateEnum.Prepared, old,
                        await fixture.CreateArtifactAsync("candidate", HealthyScript),
                        await fixture.CreateArtifactAsync("rollback", HealthyScript));

                    using (IDisposable? held = fixture.Records.TryAcquireSupervisorLock())
                    {
                        AssertNotNull(held, "test holds the lock");
                        SelfDeployCutoverResult result = await fixture.CreateCoordinator().SuperviseAsync(record.OperationId, fixture.Self());
                        AssertEqual("supervisor_lock_held", result.Reason, "second supervisor refuses");
                    }

                    SelfDeployRestartRecord final = await fixture.ReadRecordAsync();
                    AssertEqual(SelfDeployRestartStateEnum.Prepared, final.State, "record unchanged");
                    AssertEqual(0, fixture.Planner.ServerLaunches.Count, "nothing launched");
                }
            });

            await RunTest("Recover_SupervisorInterruptedAfterCandidateLaunch_HealthyCandidateCommits", async () =>
            {
                if (SkipWindows("Recover_SupervisorInterruptedAfterCandidateLaunch_HealthyCandidateCommits")) return;
                using (CutoverFixture fixture = new CutoverFixture())
                {
                    SelfDeployRestartRecord record = await fixture.InterruptAfterCandidateLaunchAsync(HealthyScript);
                    AssertEqual(SelfDeployRestartStateEnum.CandidateStarting, record.State, "interruption leaves a non-terminal durable state");
                    AssertNotNull(record.CandidateProcess, "launched candidate identity survived the interruption");

                    SelfDeployCutoverResult result = await fixture.CreateCoordinator().RecoverAsync();

                    AssertEqual(SelfDeployRestartStateEnum.Committed, result.State, "running healthy candidate commits");
                    AssertEqual("recovery_candidate_healthy", result.Reason, "recovery reason");
                    AssertEqual(1, fixture.Planner.ServerLaunches.Count, "recovery launched nothing new");
                }
            });

            await RunTest("Recover_SupervisorInterruptedAfterCandidateLaunch_DeadCandidateRollsBack", async () =>
            {
                if (SkipWindows("Recover_SupervisorInterruptedAfterCandidateLaunch_DeadCandidateRollsBack")) return;
                using (CutoverFixture fixture = new CutoverFixture())
                {
                    SelfDeployRestartRecord record = await fixture.InterruptAfterCandidateLaunchAsync(HealthyScript);
                    AssertTrue(await fixture.Host.TerminateAsync(record.CandidateProcess!, true, TimeSpan.FromSeconds(5)), "candidate crash simulated");

                    SelfDeployCutoverResult result = await fixture.CreateCoordinator().RecoverAsync();

                    AssertEqual(SelfDeployRestartStateEnum.RolledBack, result.State, "rollback restored");
                    SelfDeployRestartRecord final = await fixture.ReadRecordAsync();
                    AssertEqual("recovery_candidate_exited", ReasonOf(final, SelfDeployRestartStateEnum.RollingBack), "recovery reason");
                    AssertEqual(2, fixture.Planner.ServerLaunches.Count, "candidate from the interrupted run, then rollback");
                    AssertEqual(final.Rollback.Digest, fixture.Planner.ServerLaunches[1], "recovery launched the rollback artifact");
                }
            });

            await RunTest("Recover_InterruptedBeforeCandidateLaunch_RestoresRollbackNotCandidate", async () =>
            {
                if (SkipWindows("Recover_InterruptedBeforeCandidateLaunch_RestoresRollbackNotCandidate")) return;
                using (CutoverFixture fixture = new CutoverFixture())
                {
                    SelfDeployProcessIdentity old = await fixture.StartExitedAsync();
                    SelfDeployRestartRecord record = await fixture.CreateRecordAsync(SelfDeployRestartStateEnum.OldStopped, old,
                        await fixture.CreateArtifactAsync("candidate", HealthyScript),
                        await fixture.CreateArtifactAsync("rollback", HealthyScript));

                    SelfDeployCutoverResult result = await fixture.CreateCoordinator().RecoverAsync();

                    AssertEqual(SelfDeployRestartStateEnum.RolledBack, result.State, "known-good binary restored");
                    AssertEqual(1, fixture.Planner.ServerLaunches.Count, "one launch");
                    AssertEqual(record.Rollback.Digest, fixture.Planner.ServerLaunches[0], "recovery never promotes the candidate");
                }
            });

            await RunTest("Recover_OldAdmiralStillRunning_AbortsAndLaunchesNothing", async () =>
            {
                if (SkipWindows("Recover_OldAdmiralStillRunning_AbortsAndLaunchesNothing")) return;
                using (CutoverFixture fixture = new CutoverFixture())
                {
                    SelfDeployProcessIdentity old = fixture.StartShell("exec sleep 60");
                    await fixture.CreateRecordAsync(SelfDeployRestartStateEnum.Armed, old,
                        await fixture.CreateArtifactAsync("candidate", HealthyScript),
                        await fixture.CreateArtifactAsync("rollback", HealthyScript));

                    SelfDeployCutoverResult result = await fixture.CreateCoordinator().RecoverAsync();

                    AssertEqual(SelfDeployRestartStateEnum.Aborted, result.State, "old admiral keeps ownership");
                    AssertEqual("recovery_old_process_still_owner", result.Reason, "recovery reason");
                    AssertEqual(0, fixture.Planner.ServerLaunches.Count, "nothing launched");
                    AssertEqual(SelfDeployProcessStateEnum.Running, fixture.Host.GetState(old), "old admiral untouched");
                }
            });

            await RunTest("Recover_TwoRecordedProcessesRunning_FailsClosedWithoutActing", async () =>
            {
                if (SkipWindows("Recover_TwoRecordedProcessesRunning_FailsClosedWithoutActing")) return;
                using (CutoverFixture fixture = new CutoverFixture())
                {
                    SelfDeployProcessIdentity old = fixture.StartShell("exec sleep 60");
                    SelfDeployProcessIdentity candidate = fixture.StartShell("exec sleep 60");
                    await fixture.CreateRecordAsync(SelfDeployRestartStateEnum.CandidateStarting, old,
                        await fixture.CreateArtifactAsync("candidate", HealthyScript),
                        await fixture.CreateArtifactAsync("rollback", HealthyScript),
                        candidate);

                    SelfDeployCutoverResult result = await fixture.CreateCoordinator().RecoverAsync();

                    AssertEqual(SelfDeployRestartStateEnum.Failed, result.State, "overlap fails closed");
                    AssertEqual("recovery_ownership_overlap_detected", result.Reason, "recovery reason");
                    AssertEqual(SelfDeployProcessStateEnum.Running, fixture.Host.GetState(old), "no process signalled");
                    AssertEqual(SelfDeployProcessStateEnum.Running, fixture.Host.GetState(candidate), "no process signalled");
                    AssertEqual(0, fixture.Planner.ServerLaunches.Count, "nothing launched");
                }
            });

            await RunTest("Recover_CandidateLaunchIdentityUnrecorded_FailsClosed", async () =>
            {
                if (SkipWindows("Recover_CandidateLaunchIdentityUnrecorded_FailsClosed")) return;
                using (CutoverFixture fixture = new CutoverFixture())
                {
                    SelfDeployProcessIdentity old = await fixture.StartExitedAsync();
                    await fixture.CreateRecordAsync(SelfDeployRestartStateEnum.CandidateStarting, old,
                        await fixture.CreateArtifactAsync("candidate", HealthyScript),
                        await fixture.CreateArtifactAsync("rollback", HealthyScript));

                    SelfDeployCutoverResult result = await fixture.CreateCoordinator().RecoverAsync();

                    AssertEqual(SelfDeployRestartStateEnum.Failed, result.State, "an unidentifiable launch is never overlapped");
                    AssertEqual("recovery_candidate_launch_identity_unrecorded", result.Reason, "recovery reason");
                    AssertEqual(0, fixture.Planner.ServerLaunches.Count, "rollback not started over an unknown candidate");
                }
            });
        }

        private bool SkipWindows(string testName)
        {
            if (!OperatingSystem.IsWindows()) return false;
            SkipTest(testName, "The process fixtures use a Unix shell.");
            return true;
        }

        private static int IndexOf(SelfDeployRestartRecord record, SelfDeployRestartStateEnum state)
        {
            for (int i = 0; i < record.Transitions.Count; i++)
            {
                if (record.Transitions[i].State == state) return i;
            }
            return Int32.MaxValue;
        }

        private static string ReasonOf(SelfDeployRestartRecord record, SelfDeployRestartStateEnum state)
        {
            foreach (SelfDeployRestartTransition transition in record.Transitions)
            {
                if (transition.State == state) return transition.Reason;
            }
            return String.Empty;
        }

        private sealed class CutoverFixture : IDisposable
        {
            private readonly SelfDeployTestDirectory _Directory = new SelfDeployTestDirectory();

            public CutoverFixture()
            {
                HealthDirectory = Path.Combine(_Directory.Root, "health");
                Directory.CreateDirectory(HealthDirectory);
                string state = Path.Combine(_Directory.Root, "state");
                Host = new RecordingProcessHost();
                Artifacts = new SelfDeployArtifactStore(Path.Combine(state, "releases"));
                Records = new SelfDeployRestartRecordStore(state);
                Schema = new FakeSchemaReader();
                Planner = new ShellLaunchPlanner(HealthDirectory);
                Options = new SelfDeployCutoverOptions
                {
                    HandshakeTimeout = TimeSpan.FromSeconds(5),
                    OldProcessExitTimeout = TimeSpan.FromSeconds(1),
                    TerminationTimeout = TimeSpan.FromSeconds(5),
                    HealthTimeout = TimeSpan.FromSeconds(3),
                    PollInterval = TimeSpan.FromMilliseconds(50)
                };
            }

            public string HealthDirectory { get; }
            public RecordingProcessHost Host { get; }
            public SelfDeployArtifactStore Artifacts { get; }
            public SelfDeployRestartRecordStore Records { get; }
            public FakeSchemaReader Schema { get; }
            public ShellLaunchPlanner Planner { get; }
            public SelfDeployCutoverOptions Options { get; }

            public SelfDeployCutoverCoordinator CreateCoordinator(ISelfDeployHealthProbe? probe = null)
            {
                return new SelfDeployCutoverCoordinator(Host, probe ?? new MarkerHealthProbe(), Artifacts, Schema, Planner, Records, Options);
            }

            public SelfDeployProcessIdentity Self()
            {
                return Host.Capture(Environment.ProcessId) ?? throw new InvalidOperationException("test process identity unavailable");
            }

            public SelfDeployProcessIdentity StartShell(string command)
            {
                return Host.Start(new SelfDeployLaunchSpec
                {
                    FileName = "/bin/sh",
                    Arguments = new[] { "-c", command },
                    WorkingDirectory = _Directory.Root
                });
            }

            public async Task<SelfDeployProcessIdentity> StartExitedAsync()
            {
                SelfDeployProcessIdentity identity = StartShell("exit 0");
                SelfDeployProcessStateEnum state = await Host.WaitForExitAsync(identity, TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(20));
                if (state != SelfDeployProcessStateEnum.Exited) throw new InvalidOperationException("fixture process did not exit");
                return identity;
            }

            public async Task<SelfDeployReleaseArtifact> CreateArtifactAsync(string role, string script)
            {
                string source = Path.Combine(_Directory.Root, "build-" + role + "-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(source);
                File.WriteAllText(Path.Combine(source, "server.sh"), "#!/bin/sh\n" + script);
                File.WriteAllText(Path.Combine(source, "role.txt"), role + " " + Guid.NewGuid().ToString("N"));
                return await Artifacts.CaptureAsync(source, "server.sh");
            }

            public async Task<SelfDeployRestartRecord> CreateRecordAsync(
                SelfDeployRestartStateEnum state,
                SelfDeployProcessIdentity? old,
                SelfDeployReleaseArtifact candidate,
                SelfDeployReleaseArtifact rollback,
                SelfDeployProcessIdentity? candidateProcess = null)
            {
                SelfDeployRestartRecord record = new SelfDeployRestartRecord
                {
                    OperationId = "sdo_" + Guid.NewGuid().ToString("N"),
                    CreatedUtc = DateTime.UtcNow,
                    OldProcess = old,
                    CandidateProcess = candidateProcess,
                    Candidate = candidate,
                    Rollback = rollback,
                    SchemaVersionBefore = Schema.Version,
                    HealthUrl = HealthDirectory
                };
                record.MoveTo(state, "fixture");
                SelfDeployRestartTransitionResult created = await Records.CreateAsync(record);
                if (!created.Applied) throw new InvalidOperationException("fixture record rejected: " + created.FailureReason);
                return record;
            }

            public async Task<SelfDeployRestartRecord> InterruptAfterCandidateLaunchAsync(string candidateScript)
            {
                SelfDeployProcessIdentity old = StartShell("exec sleep 60");
                SelfDeployRestartRecord record = await CreateRecordAsync(SelfDeployRestartStateEnum.Prepared, old,
                    await CreateArtifactAsync("candidate", candidateScript),
                    await CreateArtifactAsync("rollback", HealthyScript));
                using (CancellationTokenSource interruption = new CancellationTokenSource())
                {
                    Task<SelfDeployCutoverResult> supervise = CreateCoordinator(new InterruptingProbe(interruption))
                        .SuperviseAsync(record.OperationId, Self(), interruption.Token);
                    await RequestExitAsync(record.OperationId);
                    bool interrupted = false;
                    try
                    {
                        await supervise;
                    }
                    catch (OperationCanceledException)
                    {
                        interrupted = true;
                    }
                    if (!interrupted) throw new InvalidOperationException("supervisor was not interrupted");
                }
                return await ReadRecordAsync();
            }

            public async Task RequestExitAsync(string operationId)
            {
                await WaitForStateAsync(SelfDeployRestartStateEnum.Armed);
                SelfDeployRestartTransitionResult result = await Records.TryTransitionAsync(operationId,
                    new[] { SelfDeployRestartStateEnum.Armed },
                    r => r.MoveTo(SelfDeployRestartStateEnum.ExitRequested, "test_admiral_exit_requested"));
                if (!result.Applied) throw new InvalidOperationException("exit request rejected: " + result.FailureReason);
            }

            public async Task WaitForStateAsync(SelfDeployRestartStateEnum state)
            {
                DateTime deadline = DateTime.UtcNow.AddSeconds(10);
                while (DateTime.UtcNow < deadline)
                {
                    SelfDeployRestartRecordReadResult read = await Records.ReadAsync();
                    if (read.IsReadable && read.Record!.State == state) return;
                    await Task.Delay(20);
                }
                throw new TimeoutException("record did not reach " + state);
            }

            public async Task<SelfDeployRestartRecord> ReadRecordAsync()
            {
                SelfDeployRestartRecordReadResult read = await Records.ReadAsync();
                if (!read.IsReadable) throw new InvalidOperationException("record unreadable: " + read.FailureReason);
                return read.Record!;
            }

            public void Dispose()
            {
                foreach (SelfDeployProcessIdentity identity in Host.Started)
                {
                    bool stopped = Host.TerminateAsync(identity, true, TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                    if (!stopped) Console.Error.WriteLine("[SelfDeployCutoverCoordinatorTests] process " + identity.ProcessId + " did not stop");
                }
                _Directory.Dispose();
            }
        }

        private sealed class RecordingProcessHost : ISelfDeployProcessHost
        {
            private readonly SelfDeployProcessHost _Inner = new SelfDeployProcessHost();

            public List<SelfDeployProcessIdentity> Started { get; } = new List<SelfDeployProcessIdentity>();
            public HashSet<int> UnverifiedProcessIds { get; } = new HashSet<int>();

            public SelfDeployProcessIdentity? Capture(int processId) => _Inner.Capture(processId);

            public SelfDeployProcessStateEnum GetState(SelfDeployProcessIdentity identity)
            {
                if (UnverifiedProcessIds.Contains(identity.ProcessId)) return SelfDeployProcessStateEnum.Unverified;
                return _Inner.GetState(identity);
            }

            public SelfDeployProcessIdentity Start(SelfDeployLaunchSpec spec)
            {
                SelfDeployProcessIdentity identity = _Inner.Start(spec);
                lock (Started) Started.Add(identity);
                return identity;
            }

            public Task<bool> TerminateAsync(SelfDeployProcessIdentity identity, bool entireProcessTree, TimeSpan timeout, CancellationToken token = default)
            {
                if (UnverifiedProcessIds.Contains(identity.ProcessId)) return Task.FromResult(false);
                return _Inner.TerminateAsync(identity, entireProcessTree, timeout, token);
            }

            public async Task<SelfDeployProcessStateEnum> WaitForExitAsync(SelfDeployProcessIdentity identity, TimeSpan timeout, TimeSpan pollInterval, CancellationToken token = default)
            {
                if (UnverifiedProcessIds.Contains(identity.ProcessId)) return SelfDeployProcessStateEnum.Unverified;
                return await _Inner.WaitForExitAsync(identity, timeout, pollInterval, token);
            }
        }

        private sealed class ShellLaunchPlanner : ISelfDeployLaunchPlanner
        {
            private readonly string _HealthDirectory;

            public ShellLaunchPlanner(string healthDirectory)
            {
                _HealthDirectory = healthDirectory;
            }

            public List<string> ServerLaunches { get; } = new List<string>();

            public SelfDeployLaunchSpec ForServer(SelfDeployReleaseArtifact artifact, string operationId)
            {
                ServerLaunches.Add(artifact.Digest);
                return new SelfDeployLaunchSpec
                {
                    FileName = "/bin/sh",
                    Arguments = new[] { Path.Combine(artifact.Directory, artifact.EntryAssembly) },
                    WorkingDirectory = artifact.Directory,
                    EnvironmentVariables = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [SelfDeployRestartRecordStore.OperationIdVariable] = operationId,
                        ["ARMADA_TEST_HEALTH_DIR"] = _HealthDirectory
                    }
                };
            }

            public SelfDeployLaunchSpec ForSupervisor(SelfDeployReleaseArtifact rollback, string operationId)
            {
                throw new NotSupportedException("The coordinator never launches a supervisor.");
            }
        }

        private sealed class MarkerHealthProbe : ISelfDeployHealthProbe
        {
            public Task<SelfDeployHealthResult> CheckAsync(string healthUrl, SelfDeployProcessIdentity process, CancellationToken token = default)
            {
                bool ready = File.Exists(Path.Combine(healthUrl, process.ProcessId + ".ready"));
                return Task.FromResult(new SelfDeployHealthResult
                {
                    Healthy = ready,
                    FailureReason = ready ? String.Empty : "marker_missing"
                });
            }
        }

        private sealed class InterruptingProbe : ISelfDeployHealthProbe
        {
            private readonly CancellationTokenSource _Interruption;

            public InterruptingProbe(CancellationTokenSource interruption)
            {
                _Interruption = interruption;
            }

            public Task<SelfDeployHealthResult> CheckAsync(string healthUrl, SelfDeployProcessIdentity process, CancellationToken token = default)
            {
                _Interruption.Cancel();
                token.ThrowIfCancellationRequested();
                throw new InvalidOperationException("interruption token was not observed");
            }
        }

        private sealed class FakeSchemaReader : ISelfDeploySchemaVersionReader
        {
            public int Version { get; set; } = 42;
            public bool Throw { get; set; }

            public Task<int> ReadAsync(CancellationToken token = default)
            {
                if (Throw) throw new InvalidOperationException("schema read failure");
                return Task.FromResult(Version);
            }
        }
    }
}
