namespace Test.Shared.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Runtimes;
    using SyslogLogging;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// Descriptors for the dispatch and handoff hardening in archive/HANDOFF_EXAMINATION_RESULTS.md: claim-first assignment with
    /// one rollback boundary (D1, D2, D6), compare-and-set launch recording (D4), terminal launch failures that settle
    /// dependents (H16), isolated health-check phases (D7), and bounded output-contract repair turns (L4, H2, H3).
    /// </summary>
    public sealed class HandoffResilienceSuite : IArmadaTestSuite
    {
        #region Private-Members

        private const string SuiteId = "Services.HandoffResilience";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(CaseAsync("provisioning_failure_rolls_back_cleanly", "A dock provisioning failure returns the mission to Pending and the captain to Idle", TestTags.Negative, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                StubGitService git = new StubGitService();
                git.ShouldThrowOnWorktree = true;
                Harness h = new Harness(testDb, git);
                h.Captains.OnLaunchAgent = (_, _, _) => throw new InvalidOperationException("launch must not be reached");

                Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                Captain captain = await testDb.Driver.Captains.CreateAsync(new Captain("provision-fails")).ConfigureAwait(false);
                Mission mission = await testDb.Driver.Missions.CreateAsync(new Mission("Work", "Do it") { VesselId = vessel.Id }).ConfigureAwait(false);

                bool assigned = await h.Missions.TryAssignAsync(mission, vessel).ConfigureAwait(false);
                AssertFalse(assigned);

                Mission? reverted = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                AssertEqual(MissionStatusEnum.Pending, reverted!.Status);
                AssertNull(reverted.CaptainId);
                AssertNull(reverted.DockId);
                Captain? idle = await testDb.Driver.Captains.ReadAsync(captain.Id).ConfigureAwait(false);
                AssertEqual(CaptainStateEnum.Idle, idle!.State);
                AssertNull(idle.CurrentMissionId);
            }));

            cases.Add(CaseAsync("launch_failure_rolls_back_once", "A launch exception rolls back the mission, captain and dock through one boundary", TestTags.Negative, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Harness h = new Harness(testDb, new DirCreatingGitService());
                h.Captains.OnLaunchAgent = (_, _, _) => throw new IOException("simulated launch failure");

                Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                Captain captain = await testDb.Driver.Captains.CreateAsync(new Captain("launch-fails")).ConfigureAwait(false);
                Mission mission = await testDb.Driver.Missions.CreateAsync(new Mission("Work", "Do it") { VesselId = vessel.Id }).ConfigureAwait(false);

                AssertFalse(await h.Missions.TryAssignAsync(mission, vessel).ConfigureAwait(false));

                Mission? reverted = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                AssertEqual(MissionStatusEnum.Pending, reverted!.Status);
                AssertNull(reverted.DockId);
                Captain? idle = await testDb.Driver.Captains.ReadAsync(captain.Id).ConfigureAwait(false);
                AssertEqual(CaptainStateEnum.Idle, idle!.State, "the claimed captain is released");
                AssertNull(idle.CurrentDockId);
                List<Dock> docks = await testDb.Driver.Docks.EnumerateAsync().ConfigureAwait(false);
                AssertFalse(docks.Any(d => d.Active && d.CaptainId == captain.Id), "the provisioned dock is reclaimed");
            }));

            cases.Add(CaseAsync("fast_exit_during_launch_keeps_exit_outcome", "An agent that exits before its launch is recorded keeps its outcome and an Idle captain", TestTags.Reliability, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Harness h = new Harness(testDb, new DirCreatingGitService());
                DatabaseDriver db = testDb.Driver;

                // The launch callback plays the exit handler of a millisecond agent: it settles the mission and releases
                // the captain before the launch call returns.
                h.Captains.OnLaunchAgent = async (Captain c, Mission m, Dock d) =>
                {
                    Mission? current = await db.Missions.ReadAsync(m.Id).ConfigureAwait(false);
                    current!.Status = MissionStatusEnum.WorkProduced;
                    current.ProcessId = null;
                    await db.Missions.UpdateAsync(current).ConfigureAwait(false);
                    await db.Captains.TryReleaseFromMissionAsync(c.Id, m.Id).ConfigureAwait(false);
                    return 9100;
                };

                Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                Captain captain = await db.Captains.CreateAsync(new Captain("fast-exit")).ConfigureAwait(false);
                Mission mission = await db.Missions.CreateAsync(new Mission("Work", "Do it") { VesselId = vessel.Id }).ConfigureAwait(false);

                AssertTrue(await h.Missions.TryAssignAsync(mission, vessel).ConfigureAwait(false));

                Mission? settled = await db.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                AssertEqual(MissionStatusEnum.WorkProduced, settled!.Status, "the exit outcome is not overwritten with InProgress");
                AssertNull(settled.ProcessId);
                Captain? idle = await db.Captains.ReadAsync(captain.Id).ConfigureAwait(false);
                AssertEqual(CaptainStateEnum.Idle, idle!.State, "the released captain is not re-marked as running");
                AssertNull(idle.ProcessId, "no process handle is written onto a released captain");
            }));

            cases.Add(CaseAsync("missing_harbor_dock_settles_dependents_and_voyage", "A launch that finds its Harbor dock missing fails terminally and cancels dependents instead of stranding the voyage", TestTags.Negative, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Harness h = new Harness(testDb, new DirCreatingGitService());
                DatabaseDriver db = testDb.Driver;
                h.Captains.OnLaunchAgent = (_, _, _) => throw new HarborDockNotFoundException("hbr_missing", "/nowhere", "the Harbor does not have this dock");

                Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                Captain captain = await db.Captains.CreateAsync(new Captain("dock-missing")).ConfigureAwait(false);
                Voyage voyage = await db.Voyages.CreateAsync(new Voyage("dock-missing") { Status = VoyageStatusEnum.InProgress }).ConfigureAwait(false);
                Mission worker = await db.Missions.CreateAsync(new Mission("[Worker] Build", "Build it") { VesselId = vessel.Id, VoyageId = voyage.Id, Persona = "Worker" }).ConfigureAwait(false);
                Mission judge = await db.Missions.CreateAsync(new Mission("[Judge] Review", "Review it") { VesselId = vessel.Id, VoyageId = voyage.Id, Persona = "Judge", DependsOnMissionId = worker.Id }).ConfigureAwait(false);

                AssertFalse(await h.Missions.TryAssignAsync(worker, vessel).ConfigureAwait(false));

                Mission? failed = await db.Missions.ReadAsync(worker.Id).ConfigureAwait(false);
                AssertEqual(MissionStatusEnum.Failed, failed!.Status);
                AssertEqual(MissionFailureKindEnum.Infra, failed.FailureKind);
                AssertContains("does not have this dock", failed.FailureReason ?? String.Empty);
                Mission? dependent = await db.Missions.ReadAsync(judge.Id).ConfigureAwait(false);
                AssertEqual(MissionStatusEnum.Cancelled, dependent!.Status, "the dependent stage settles");
                Voyage? settledVoyage = await db.Voyages.ReadAsync(voyage.Id).ConfigureAwait(false);
                AssertTrue(settledVoyage!.Status != VoyageStatusEnum.InProgress, "the voyage settles instead of waiting forever");
                Captain? idle = await db.Captains.ReadAsync(captain.Id).ConfigureAwait(false);
                AssertEqual(CaptainStateEnum.Idle, idle!.State);
            }));

            cases.Add(CaseAsync("throwing_health_phase_does_not_stop_dispatch", "A health-check phase that throws is isolated; pending missions are still dispatched", TestTags.Reliability, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                DatabaseDriver db = testDb.Driver;
                DirCreatingGitService git = new DirCreatingGitService();
                ArmadaSettings settings = CreateSettings();
                LoggingModule logging = CreateLogging();
                IDockService docks = new DockService(logging, db, settings, git);
                CaptainService captains = new CaptainService(logging, db, settings, git, docks);
                captains.OnLaunchAgent = (_, _, _) => Task.FromResult(9200);
                MissionService missions = new MissionService(logging, db, settings, docks, captains, git: git);
                AdmiralService admiral = new AdmiralService(logging, db, settings, captains, missions, new VoyageService(logging, db), docks);
                admiral.OnReconcileManualLandings = _ => throw new InvalidOperationException("simulated reconciliation failure");

                Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                await db.Captains.CreateAsync(new Captain("dispatch-after-failure")).ConfigureAwait(false);
                Mission mission = await db.Missions.CreateAsync(new Mission("Work", "Do it") { VesselId = vessel.Id }).ConfigureAwait(false);

                await admiral.HealthCheckAsync().ConfigureAwait(false);

                Mission? dispatched = await db.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                AssertEqual(MissionStatusEnum.InProgress, dispatched!.Status, "dispatch ran after the failed phase");
            }));

            cases.Add(CaseAsync("repair_note_is_replaced_not_stacked", "Successive output repairs replace the earlier repair note and stop at the configured budget", TestTags.Positive, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Harness h = new Harness(testDb, new DirCreatingGitService());
                h.Settings.MaxOutputRepairAttempts = 2;
                h.Missions.OnGetMissionOutput = _ => "I reviewed the change and it looks fine overall.";
                DatabaseDriver db = testDb.Driver;

                Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                Captain judgeCaptain = await db.Captains.CreateAsync(new Captain("no-verdict-judge") { State = CaptainStateEnum.Working }).ConfigureAwait(false);
                Voyage voyage = await db.Voyages.CreateAsync(new Voyage("repair") { Status = VoyageStatusEnum.InProgress }).ConfigureAwait(false);
                Mission judge = await CreateRunningMissionAsync(testDb, h, vessel, voyage, judgeCaptain, "Judge", false).ConfigureAwait(false);

                for (int attempt = 1; attempt <= 3; attempt++)
                {
                    await PutInProgressAsync(db, judge.Id, judgeCaptain.Id).ConfigureAwait(false);
                    Captain? holder = await db.Captains.ReadAsync(judgeCaptain.Id).ConfigureAwait(false);
                    await h.Missions.HandleCompletionAsync(holder!, judge.Id).ConfigureAwait(false);
                }

                Mission? final = await db.Missions.ReadAsync(judge.Id).ConfigureAwait(false);
                AssertEqual(MissionStatusEnum.Failed, final!.Status, "the third violation fails closed");
                AssertEqual(2, final.OutputRepairAttempts);
                string description = final.Description ?? String.Empty;
                AssertEqual(1, CountOccurrences(description, "## Output contract repair"), "only the latest repair note is kept");
                AssertContains("attempt 2 of 2", description);
                AssertContains("after 2 output-contract repair attempt(s)", final.FailureReason ?? String.Empty);
            }));

            cases.Add(CaseAsync("review_gated_stage_is_never_repaired", "A review-gated Judge that breaks its contract goes to the reviewer, not a silent repair turn", TestTags.Negative, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Harness h = new Harness(testDb, new DirCreatingGitService());
                h.Missions.OnGetMissionOutput = _ => "Looks fine.";
                DatabaseDriver db = testDb.Driver;

                Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                Captain judgeCaptain = await db.Captains.CreateAsync(new Captain("gated-judge") { State = CaptainStateEnum.Working }).ConfigureAwait(false);
                Voyage voyage = await db.Voyages.CreateAsync(new Voyage("gated") { Status = VoyageStatusEnum.InProgress }).ConfigureAwait(false);
                Mission judge = await CreateRunningMissionAsync(testDb, h, vessel, voyage, judgeCaptain, "Judge", true).ConfigureAwait(false);

                await h.Missions.HandleCompletionAsync(judgeCaptain, judge.Id).ConfigureAwait(false);

                Mission? gated = await db.Missions.ReadAsync(judge.Id).ConfigureAwait(false);
                AssertEqual(MissionStatusEnum.Review, gated!.Status);
                AssertEqual(0, gated.OutputRepairAttempts);
            }));

            cases.Add(CaseAsync("stage_without_upstream_commit_is_refused", "A later stage whose dock lacks the previous stage's commit is refused instead of starting from the default branch", TestTags.Negative, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                StubGitService git = new StubGitService();
                git.HeadCommitResult = "1111111111111111111111111111111111111111";
                git.IsAncestorResult = null;
                Harness h = new Harness(testDb, git);
                int launches = 0;
                h.Captains.OnLaunchAgent = (_, _, _) => { launches++; return Task.FromResult(9300); };
                DatabaseDriver db = testDb.Driver;

                Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                Captain captain = await db.Captains.CreateAsync(new Captain("continuity")).ConfigureAwait(false);
                Voyage voyage = await db.Voyages.CreateAsync(new Voyage("continuity") { Status = VoyageStatusEnum.InProgress }).ConfigureAwait(false);
                Mission upstream = await db.Missions.CreateAsync(new Mission("[Worker] Build", "Build it")
                {
                    VesselId = vessel.Id, VoyageId = voyage.Id, Persona = "Worker", Status = MissionStatusEnum.Complete,
                    BranchName = "armada/continuity", CommitHash = "2222222222222222222222222222222222222222"
                }).ConfigureAwait(false);
                Mission downstream = await db.Missions.CreateAsync(new Mission("[Judge] Review", "Review it")
                {
                    VesselId = vessel.Id, VoyageId = voyage.Id, Persona = "Judge", DependsOnMissionId = upstream.Id, BranchName = "armada/continuity"
                }).ConfigureAwait(false);

                AssertFalse(await h.Missions.TryAssignAsync(downstream, vessel).ConfigureAwait(false));
                AssertEqual(0, launches, "no agent starts on the wrong code");

                Mission? refused = await db.Missions.ReadAsync(downstream.Id).ConfigureAwait(false);
                AssertEqual(MissionStatusEnum.Failed, refused!.Status);
                AssertEqual(MissionFailureKindEnum.Infra, refused.FailureKind);
                AssertContains("Commit continuity check failed", refused.FailureReason ?? String.Empty);
                AssertContains("2222222222222222222222222222222222222222", refused.FailureReason ?? String.Empty, "the missing commit is named");
                Captain? idle = await db.Captains.ReadAsync(captain.Id).ConfigureAwait(false);
                AssertEqual(CaptainStateEnum.Idle, idle!.State);
            }));

            cases.Add(CaseAsync("stage_with_descendant_head_is_accepted", "A later stage whose dock contains the previous stage's commit starts normally", TestTags.Positive, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                StubGitService git = new StubGitService();
                git.HeadCommitResult = "3333333333333333333333333333333333333333";
                git.IsAncestorResult = true;
                Harness h = new Harness(testDb, git);
                h.Captains.OnLaunchAgent = (_, _, _) => Task.FromResult(9301);
                DatabaseDriver db = testDb.Driver;

                Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                await db.Captains.CreateAsync(new Captain("continuity-ok")).ConfigureAwait(false);
                Mission upstream = await db.Missions.CreateAsync(new Mission("[Worker] Build", "Build it")
                {
                    VesselId = vessel.Id, Persona = "Worker", Status = MissionStatusEnum.Complete,
                    BranchName = "armada/continuity-ok", CommitHash = "4444444444444444444444444444444444444444"
                }).ConfigureAwait(false);
                Mission downstream = await db.Missions.CreateAsync(new Mission("[Judge] Review", "Review it")
                {
                    VesselId = vessel.Id, Persona = "Judge", DependsOnMissionId = upstream.Id, BranchName = "armada/continuity-ok"
                }).ConfigureAwait(false);

                AssertTrue(await h.Missions.TryAssignAsync(downstream, vessel).ConfigureAwait(false));
                Mission? running = await db.Missions.ReadAsync(downstream.Id).ConfigureAwait(false);
                AssertEqual(MissionStatusEnum.InProgress, running!.Status);
            }));

            cases.Add(CaseAsync("completion_records_produced_commit", "A finished stage records the exact commit it produced for the next stage to verify", TestTags.Positive, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                StubGitService git = new StubGitService();
                git.HeadCommitResult = "5555555555555555555555555555555555555555";
                Harness h = new Harness(testDb, git);
                h.Missions.OnGetMissionOutput = _ => "Implemented the change and committed it.";
                DatabaseDriver db = testDb.Driver;

                Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                Captain captain = await db.Captains.CreateAsync(new Captain("records-commit") { State = CaptainStateEnum.Working }).ConfigureAwait(false);
                Voyage voyage = await db.Voyages.CreateAsync(new Voyage("records") { Status = VoyageStatusEnum.InProgress }).ConfigureAwait(false);
                Mission worker = await CreateRunningMissionAsync(testDb, h, vessel, voyage, captain, "Worker", false).ConfigureAwait(false);
                await db.Missions.CreateAsync(new Mission("[Judge] Review", "Review it")
                {
                    VesselId = vessel.Id, VoyageId = voyage.Id, Persona = "Judge", DependsOnMissionId = worker.Id
                }).ConfigureAwait(false);

                await h.Missions.HandleCompletionAsync(captain, worker.Id).ConfigureAwait(false);
                Mission? finished = await db.Missions.ReadAsync(worker.Id).ConfigureAwait(false);
                AssertEqual("5555555555555555555555555555555555555555", finished!.CommitHash);
            }));

            cases.Add(CaseAsync("endpoint_concurrency_cap_is_shared_across_captains", "Captains sharing one endpoint base URL share its MaxConcurrentMissions budget", TestTags.Reliability, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Harness h = new Harness(testDb, new DirCreatingGitService());
                h.Captains.OnLaunchAgent = (_, _, _) => Task.FromResult(9400);
                DatabaseDriver db = testDb.Driver;

                ModelEndpoint first = new ModelEndpoint { Name = "gpu-a", BaseUrl = "http://gpu-box:11434/", Model = "qwen" };
                first.InferenceOptions = new InferenceOptions { MaxConcurrentMissions = 1 };
                first = await db.ModelEndpoints.CreateAsync(first).ConfigureAwait(false);
                ModelEndpoint second = new ModelEndpoint { Name = "gpu-b", BaseUrl = "http://GPU-BOX:11434", Model = "llama" };
                second.InferenceOptions = new InferenceOptions { MaxConcurrentMissions = 1 };
                second = await db.ModelEndpoints.CreateAsync(second).ConfigureAwait(false);

                await db.Captains.CreateAsync(new Captain("busy-local", AgentRuntimeEnum.ApiEndpoint) { ModelEndpointId = first.Id, State = CaptainStateEnum.Working, CurrentMissionId = "msn_elsewhere" }).ConfigureAwait(false);
                Captain idle = await db.Captains.CreateAsync(new Captain("idle-local", AgentRuntimeEnum.ApiEndpoint) { ModelEndpointId = second.Id }).ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                Mission mission = await db.Missions.CreateAsync(new Mission("Work", "Do it") { VesselId = vessel.Id }).ConfigureAwait(false);

                AssertFalse(await h.Missions.TryAssignAsync(mission, vessel).ConfigureAwait(false), "the shared server is at its limit");
                Captain? stillIdle = await db.Captains.ReadAsync(idle.Id).ConfigureAwait(false);
                AssertEqual(CaptainStateEnum.Idle, stillIdle!.State);

                second.InferenceOptions = new InferenceOptions { MaxConcurrentMissions = 2 };
                await db.ModelEndpoints.UpdateAsync(second).ConfigureAwait(false);
                AssertTrue(await h.Missions.TryAssignAsync(mission, vessel).ConfigureAwait(false), "a higher limit admits the second mission");
            }));

            cases.Add(CaseAsync("endpoint_breaker_holds_missions_while_a_shared_server_is_down", "Repeated endpoint failures stop new missions going to any captain on that endpoint until a clean run", TestTags.Reliability, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Harness h = new Harness(testDb, new DirCreatingGitService());
                h.Captains.OnLaunchAgent = (_, _, _) => Task.FromResult(9800);
                DatabaseDriver db = testDb.Driver;

                ModelEndpoint gpu = await db.ModelEndpoints.CreateAsync(new ModelEndpoint { Name = "gpu", BaseUrl = "http://gpu-box:11434", Model = "qwen" }).ConfigureAwait(false);
                Captain first = await db.Captains.CreateAsync(new Captain("gpu-one", AgentRuntimeEnum.ApiEndpoint) { ModelEndpointId = gpu.Id }).ConfigureAwait(false);
                await db.Captains.CreateAsync(new Captain("gpu-two", AgentRuntimeEnum.ApiEndpoint) { ModelEndpointId = gpu.Id }).ConfigureAwait(false);

                for (int i = 0; i < 3; i++) await h.Missions.RecordEndpointOutcomeAsync(first, false).ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                Mission mission = await db.Missions.CreateAsync(new Mission("Work", "Do it") { VesselId = vessel.Id }).ConfigureAwait(false);
                AssertFalse(await h.Missions.TryAssignAsync(mission, vessel).ConfigureAwait(false), "neither captain on the failing endpoint gets the mission");

                await h.Missions.RecordEndpointOutcomeAsync(first, true).ConfigureAwait(false);
                AssertTrue(await h.Missions.TryAssignAsync(mission, vessel).ConfigureAwait(false), "a clean run closes the breaker");
            }));

            cases.Add(CaseAsync("recently_unhealthy_endpoint_is_skipped", "A captain whose endpoint failed a recent health check is not picked; a stale result does not block", TestTags.Reliability, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Harness h = new Harness(testDb, new DirCreatingGitService());
                h.Captains.OnLaunchAgent = (_, _, _) => Task.FromResult(9810);
                DatabaseDriver db = testDb.Driver;

                ModelEndpoint gpu = await db.ModelEndpoints.CreateAsync(new ModelEndpoint { Name = "gpu", BaseUrl = "http://gpu-box:11434", Model = "qwen",
                    HealthStatus = EndpointHealthStatusEnum.Unhealthy, LastHealthCheckUtc = DateTime.UtcNow.AddMinutes(-1), LastHealthError = "connection refused" }).ConfigureAwait(false);
                await db.Captains.CreateAsync(new Captain("gpu-captain", AgentRuntimeEnum.ApiEndpoint) { ModelEndpointId = gpu.Id }).ConfigureAwait(false);
                Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                Mission mission = await db.Missions.CreateAsync(new Mission("Work", "Do it") { VesselId = vessel.Id }).ConfigureAwait(false);
                AssertFalse(await h.Missions.TryAssignAsync(mission, vessel).ConfigureAwait(false), "a recently unhealthy endpoint gets no new mission");

                gpu.LastHealthCheckUtc = DateTime.UtcNow.AddHours(-2);
                await db.ModelEndpoints.UpdateAsync(gpu).ConfigureAwait(false);
                AssertTrue(await h.Missions.TryAssignAsync(mission, vessel).ConfigureAwait(false), "a stale unhealthy result does not block dispatch");
            }));

            cases.Add(CaseAsync("worker_that_stops_mid_task_is_redispatched", "A Worker that ran for minutes but ended with no commit, no diff and no completion line is re-dispatched, not handed off as WorkProduced", TestTags.Reliability, async () =>
            {
                foreach (bool reportedComplete in new[] { false, true })
                {
                    using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                    StubGitService git = new StubGitService();
                    git.HeadCommitResult = "1111111111111111111111111111111111111111";
                    Harness h = new Harness(testDb, git);
                    h.Missions.OnGetMissionOutput = _ => reportedComplete
                        ? "Nothing needed changing: StartNew already exists.\n[ARMADA:RESULT] COMPLETE\nVerified the existing factory and its test."
                        : "I'll start by reading the key files. Now let me examine the test file structure.";
                    DatabaseDriver db = testDb.Driver;

                    Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                    Captain captain = await db.Captains.CreateAsync(new Captain("stopped-early") { State = CaptainStateEnum.Working }).ConfigureAwait(false);
                    Voyage voyage = await db.Voyages.CreateAsync(new Voyage("stopped") { Status = VoyageStatusEnum.InProgress }).ConfigureAwait(false);
                    Mission mission = await CreateRunningMissionAsync(testDb, h, vessel, voyage, captain, "Worker", false).ConfigureAwait(false);
                    mission.StartedUtc = DateTime.UtcNow.AddMinutes(-3);
                    mission.TotalRuntimeMs = 180000;
                    await db.Missions.UpdateAsync(mission).ConfigureAwait(false);
                    string metadata = Path.Combine(h.Settings.LogDirectory, "docks");
                    Directory.CreateDirectory(metadata);
                    File.WriteAllText(Path.Combine(metadata, mission.DockId + ".start"), "1111111111111111111111111111111111111111\n");

                    await h.Missions.HandleCompletionAsync(captain, mission.Id).ConfigureAwait(false);

                    Mission? after = await db.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    if (reportedComplete)
                    {
                        AssertEqual(0, after!.NoOpRedispatchAttempts, "a stage that reports completion with nothing to change is not rejected");
                    }
                    else
                    {
                        AssertEqual(1, after!.NoOpRedispatchAttempts, "the unfinished run is re-dispatched: " + after.Status + " " + after.FailureReason);
                        AssertTrue(after.Status != MissionStatusEnum.WorkProduced && after.Status != MissionStatusEnum.Complete, "not handed off as finished work");
                    }
                }
            }));

            cases.Add(CaseAsync("noop_budget_is_not_spent_by_earlier_resumes", "A mission resumed after transient errors still gets its no-op re-dispatch", TestTags.Reliability, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Harness h = new Harness(testDb, new DirCreatingGitService());
                h.Missions.OnGetMissionOutput = _ => "Done.";
                DatabaseDriver db = testDb.Driver;

                Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                Captain captain = await db.Captains.CreateAsync(new Captain("resumed-then-noop") { State = CaptainStateEnum.Working }).ConfigureAwait(false);
                Voyage voyage = await db.Voyages.CreateAsync(new Voyage("noop") { Status = VoyageStatusEnum.InProgress }).ConfigureAwait(false);
                Mission mission = await CreateRunningMissionAsync(testDb, h, vessel, voyage, captain, "Worker", false).ConfigureAwait(false);
                mission.RedispatchAttempts = 2;
                mission.TotalRuntimeMs = 500;
                await db.Missions.UpdateAsync(mission).ConfigureAwait(false);

                await h.Missions.HandleCompletionAsync(captain, mission.Id).ConfigureAwait(false);

                Mission? after = await db.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                AssertTrue(after!.Status != MissionStatusEnum.Failed, "the no-op is re-dispatched, not failed: " + after.Status + " " + after.FailureReason);
                AssertEqual(1, after.NoOpRedispatchAttempts);
                AssertEqual(2, after.RedispatchAttempts, "the resume counter is not consumed by the no-op");
            }));

            cases.Add(CaseAsync("runtime_max_concurrent_is_enforced", "AgentSettings.MaxConcurrent limits how many captains of a runtime work at once", TestTags.Reliability, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Harness h = new Harness(testDb, new DirCreatingGitService());
                h.Settings.Agents.Add(new AgentSettings(AgentRuntimeEnum.Mux, "mux") { MaxConcurrent = 1 });
                h.Captains.OnLaunchAgent = (_, _, _) => Task.FromResult(9401);
                DatabaseDriver db = testDb.Driver;

                await db.Captains.CreateAsync(new Captain("mux-busy", AgentRuntimeEnum.Mux) { State = CaptainStateEnum.Working, CurrentMissionId = "msn_elsewhere" }).ConfigureAwait(false);
                await db.Captains.CreateAsync(new Captain("mux-idle", AgentRuntimeEnum.Mux)).ConfigureAwait(false);
                Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                Mission mission = await db.Missions.CreateAsync(new Mission("Work", "Do it") { VesselId = vessel.Id }).ConfigureAwait(false);

                AssertFalse(await h.Missions.TryAssignAsync(mission, vessel).ConfigureAwait(false), "Mux is at MaxConcurrent");

                await db.Captains.CreateAsync(new Captain("claude-idle", AgentRuntimeEnum.ClaudeCode)).ConfigureAwait(false);
                AssertTrue(await h.Missions.TryAssignAsync(mission, vessel).ConfigureAwait(false), "another runtime still takes the work");
                Mission? running = await db.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                Captain? taker = await db.Captains.ReadAsync(running!.CaptainId!).ConfigureAwait(false);
                AssertEqual(AgentRuntimeEnum.ClaudeCode, taker!.Runtime);
            }));

            cases.Add(CaseAsync("broken_captain_is_not_repicked_for_the_same_mission", "After a captain fails to start a mission, the retry goes to another idle captain right away", TestTags.Reliability, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Harness h = new Harness(testDb, new DirCreatingGitService());
                DatabaseDriver db = testDb.Driver;
                Captain broken = await db.Captains.CreateAsync(new Captain("a-broken")).ConfigureAwait(false);
                Captain healthy = await db.Captains.CreateAsync(new Captain("b-healthy")).ConfigureAwait(false);
                List<string> attempts = new List<string>();
                h.Captains.OnLaunchAgent = (Captain c, Mission m, Dock d) =>
                {
                    attempts.Add(c.Id);
                    if (c.Id == broken.Id) throw new IOException("this captain's CLI crashes on start");
                    return Task.FromResult(9500);
                };

                Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                Mission mission = await db.Missions.CreateAsync(new Mission("Work", "Do it") { VesselId = vessel.Id }).ConfigureAwait(false);

                AssertFalse(await h.Missions.TryAssignAsync(mission, vessel).ConfigureAwait(false), "the first pick fails to start");
                AssertTrue(await h.Missions.TryAssignAsync(mission, vessel).ConfigureAwait(false), "the retry does not wait out the backoff");
                AssertEqual(2, attempts.Count);
                AssertEqual(broken.Id, attempts[0]);
                AssertEqual(healthy.Id, attempts[1], "the retry uses a captain that has not failed this mission");
            }));

            cases.Add(CaseAsync("misconfigured_captain_is_quarantined", "A captain whose launch fails on a deterministic configuration problem is quarantined instead of failing the next mission too", TestTags.Reliability, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Harness h = new Harness(testDb, new DirCreatingGitService());
                DatabaseDriver db = testDb.Driver;
                h.Captains.OnLaunchAgent = (_, _, _) => throw new MissionLaunchConfigurationException("This API captain has tools disabled; enable tools under Configuration > Endpoints.");
                Captain captain = await db.Captains.CreateAsync(new Captain("misconfigured")).ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                Mission first = await db.Missions.CreateAsync(new Mission("First", "Do it") { VesselId = vessel.Id }).ConfigureAwait(false);
                Mission second = await db.Missions.CreateAsync(new Mission("Second", "Do it") { VesselId = vessel.Id }).ConfigureAwait(false);

                AssertFalse(await h.Missions.TryAssignAsync(first, vessel).ConfigureAwait(false));
                Mission? failed = await db.Missions.ReadAsync(first.Id).ConfigureAwait(false);
                AssertEqual(MissionStatusEnum.Failed, failed!.Status);

                Captain? quarantined = await db.Captains.ReadAsync(captain.Id).ConfigureAwait(false);
                AssertEqual(CaptainStateEnum.Quarantined, quarantined!.State);
                AssertNotNull(quarantined.QuarantineUntilUtc);
                AssertContains("tools disabled", quarantined.QuarantineReason ?? String.Empty);

                AssertFalse(await h.Missions.TryAssignAsync(second, vessel).ConfigureAwait(false), "the next mission is not handed to the same broken captain");
                Mission? waiting = await db.Missions.ReadAsync(second.Id).ConfigureAwait(false);
                AssertEqual(MissionStatusEnum.Pending, waiting!.Status, "the next mission waits instead of failing");
            }));

            cases.Add(CaseAsync("harbor_message_decodes_split_multibyte_characters", "A Harbor link message whose UTF-8 characters straddle receive chunks decodes intact", TestTags.Positive, () =>
            {
                string text = "naïve café 日本語 \U0001F680 done";
                byte[] bytes = System.Text.Encoding.UTF8.GetBytes(text);
                int split = System.Text.Encoding.UTF8.GetByteCount("naïve café 日") - 1;
                using MemoryStream message = new MemoryStream();
                message.Write(bytes, 0, split);
                message.Write(bytes, split, bytes.Length - split);

                AssertEqual(text, WebSocketHarborTransport.DecodeUtf8(message));
                string perChunk = System.Text.Encoding.UTF8.GetString(bytes, 0, split) + System.Text.Encoding.UTF8.GetString(bytes, split, bytes.Length - split);
                AssertFalse(String.Equals(text, perChunk, StringComparison.Ordinal), "decoding chunks separately would have corrupted the split character");
                return Task.CompletedTask;
            }));

            cases.Add(CaseAsync("voyage_stays_open_while_a_completion_is_in_flight", "A voyage whose last stage reads WorkProduced but is still being completed is not closed", TestTags.Reliability, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                DatabaseDriver db = testDb.Driver;
                VoyageService voyages = new VoyageService(CreateLogging(), db);
                Voyage voyage = await db.Voyages.CreateAsync(new Voyage("in-flight") { Status = VoyageStatusEnum.InProgress }).ConfigureAwait(false);
                await db.Missions.CreateAsync(new Mission("[Worker] Build", "Build it") { VoyageId = voyage.Id, Persona = "Worker", Status = MissionStatusEnum.WorkProduced }).ConfigureAwait(false);
                Mission judge = await db.Missions.CreateAsync(new Mission("[Judge] Review", "Review it") { VoyageId = voyage.Id, Persona = "Judge", Status = MissionStatusEnum.WorkProduced }).ConfigureAwait(false);

                List<Voyage> closed = await voyages.CheckCompletionsAsync(id => id == judge.Id).ConfigureAwait(false);
                AssertEqual(0, closed.Count, "the Judge's completion may still schedule a repair or rework");
                Voyage? open = await db.Voyages.ReadAsync(voyage.Id).ConfigureAwait(false);
                AssertEqual(VoyageStatusEnum.InProgress, open!.Status);

                List<Voyage> settled = await voyages.CheckCompletionsAsync(id => false).ConfigureAwait(false);
                AssertEqual(1, settled.Count, "once completion finished, the voyage settles");
            }));

            cases.Add(CaseAsync("transient_resume_waits_and_stands_down_when_cancelled", "A transient failure waits before resuming, and a mission cancelled during the wait is not relaunched", TestTags.Reliability, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                DatabaseDriver db = testDb.Driver;
                StubGitService git = new StubGitService();
                ArmadaSettings settings = CreateSettings();
                settings.TransientResumeDelaySeconds = 2;
                settings.MaxRecoveryAttempts = 2;
                LoggingModule logging = CreateLogging();
                IDockService docks = new DockService(logging, db, settings, git);
                CaptainService captains = new CaptainService(logging, db, settings, git, docks);
                int relaunches = 0;
                captains.OnLaunchAgent = (_, _, _) => { Interlocked.Increment(ref relaunches); return Task.FromResult(9600); };
                MissionService missions = new MissionService(logging, db, settings, docks, captains);
                AdmiralService admiral = new AdmiralService(logging, db, settings, captains, missions, new VoyageService(logging, db), docks);

                Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                Dock dock = new Dock(vessel.Id) { WorktreePath = TestTemp.NewDirectory("resilience_resume"), Active = true };
                Directory.CreateDirectory(dock.WorktreePath);
                dock = await db.Docks.CreateAsync(dock).ConfigureAwait(false);
                Mission mission = await db.Missions.CreateAsync(new Mission("Work", "Do it") { VesselId = vessel.Id, Status = MissionStatusEnum.InProgress, ProcessId = 4300, DockId = dock.Id }).ConfigureAwait(false);
                Captain captain = await db.Captains.CreateAsync(new Captain("waits") { State = CaptainStateEnum.Working, CurrentMissionId = mission.Id, CurrentDockId = dock.Id, ProcessId = 4300 }).ConfigureAwait(false);

                RuntimeProviderError stream = new RuntimeProviderError { ErrorType = "llm_stream_error", Reason = "lost the stream" };
                Stopwatch watch = Stopwatch.StartNew();
                Task exit = admiral.HandleProcessExitAsync(4300, RuntimeFailureClassifier.Decide(1, stream), captain.Id, mission.Id);

                await Task.Delay(500).ConfigureAwait(false);
                AssertEqual(0, relaunches, "no relaunch before the backoff elapses");
                Mission? waiting = await db.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                waiting!.Status = MissionStatusEnum.Cancelled;
                await db.Missions.UpdateAsync(waiting).ConfigureAwait(false);

                await exit.ConfigureAwait(false);
                AssertTrue(watch.Elapsed >= TimeSpan.FromSeconds(1), "the resume waited");
                AssertEqual(0, relaunches, "a mission cancelled during the wait is not relaunched");
                Mission? cancelled = await db.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                AssertEqual(MissionStatusEnum.Cancelled, cancelled!.Status, "the cancel is not overwritten");
                try { Directory.Delete(dock.WorktreePath, true); } catch { }
            }));

            cases.Add(CaseAsync("halted_voyage_stops_running_siblings", "When a failure halts a voyage of independent missions, a running sibling's captain is actually stopped, not just marked Cancelled", TestTags.Reliability, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                DatabaseDriver db = testDb.Driver;
                StubGitService git = new StubGitService();
                ArmadaSettings settings = CreateSettings();
                LoggingModule logging = CreateLogging();
                IDockService docks = new DockService(logging, db, settings, git);
                CaptainService captains = new CaptainService(logging, db, settings, git, docks);
                List<string> stopped = new List<string>();
                captains.OnStopAgent = c => { lock (stopped) stopped.Add(c.Id); return Task.CompletedTask; };
                MissionService missions = new MissionService(logging, db, settings, docks, captains);
                AdmiralService admiral = new AdmiralService(logging, db, settings, captains, missions, new VoyageService(logging, db), docks);

                Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                Voyage voyage = await db.Voyages.CreateAsync(new Voyage("independent") { Status = VoyageStatusEnum.InProgress }).ConfigureAwait(false);
                Mission failing = await db.Missions.CreateAsync(new Mission("A", "Do A") { VesselId = vessel.Id, VoyageId = voyage.Id, Status = MissionStatusEnum.InProgress, ProcessId = 4400 }).ConfigureAwait(false);
                Mission sibling = await db.Missions.CreateAsync(new Mission("B", "Do B") { VesselId = vessel.Id, VoyageId = voyage.Id, Status = MissionStatusEnum.InProgress, ProcessId = 4401 }).ConfigureAwait(false);
                Captain first = await db.Captains.CreateAsync(new Captain("first") { State = CaptainStateEnum.Working, CurrentMissionId = failing.Id, ProcessId = 4400 }).ConfigureAwait(false);
                Captain second = await db.Captains.CreateAsync(new Captain("second") { State = CaptainStateEnum.Working, CurrentMissionId = sibling.Id, ProcessId = 4401 }).ConfigureAwait(false);
                failing.CaptainId = first.Id;
                await db.Missions.UpdateAsync(failing).ConfigureAwait(false);
                sibling.CaptainId = second.Id;
                await db.Missions.UpdateAsync(sibling).ConfigureAwait(false);

                await admiral.HandleProcessExitAsync(4400, RuntimeFailureClassifier.Decide(1, null), first.Id, failing.Id).ConfigureAwait(false);

                Mission? cancelled = await db.Missions.ReadAsync(sibling.Id).ConfigureAwait(false);
                AssertEqual(MissionStatusEnum.Cancelled, cancelled!.Status);
                lock (stopped) AssertTrue(stopped.Contains(second.Id), "the running sibling's captain was stopped");
                Captain? released = await db.Captains.ReadAsync(second.Id).ConfigureAwait(false);
                AssertEqual(CaptainStateEnum.Idle, released!.State);
                AssertNull(released.CurrentMissionId);
            }));

            cases.Add(CaseAsync("dangling_handoff_recovery_keeps_the_judge_gate", "Restart recovery does not hand an unsubstantiated Judge's work to the next stage", TestTags.Negative, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Harness h = new Harness(testDb, new DirCreatingGitService());
                DatabaseDriver db = testDb.Driver;
                Voyage voyage = await db.Voyages.CreateAsync(new Voyage("recovery") { Status = VoyageStatusEnum.InProgress }).ConfigureAwait(false);
                Mission judge = await db.Missions.CreateAsync(new Mission("[Judge] Review", "Review it")
                {
                    VoyageId = voyage.Id, Persona = "Judge", Status = MissionStatusEnum.WorkProduced, BranchName = "armada/recovery",
                    AgentOutput = "I looked at it and it seems fine."
                }).ConfigureAwait(false);
                Mission recorder = await db.Missions.CreateAsync(new Mission("[Recorder] Record", "Record it")
                {
                    VoyageId = voyage.Id, Persona = "Recorder", DependsOnMissionId = judge.Id
                }).ConfigureAwait(false);

                int redriven = await h.Missions.RecoverDanglingHandoffsAsync().ConfigureAwait(false);
                AssertEqual(0, redriven, "no handoff without a substantiated PASS");
                Mission? untouched = await db.Missions.ReadAsync(recorder.Id).ConfigureAwait(false);
                AssertNull(untouched!.BranchName, "the Recorder was not prepared with the unreviewed branch");
            }));

            cases.Add(CaseAsync("spec_conflict_goes_to_a_person", "A Judge that reports an unsatisfiable criterion sends the stage to review instead of spending the rework budget", TestTags.Positive, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Harness h = new Harness(testDb, new DirCreatingGitService());
                h.Missions.OnGetMissionOutput = _ =>
                    "## Correctness\nThe partial tool-call case reports Completed.\n"
                    + "## Spec Conflict\nAC-INF-3 requires State=Failed for partial tool arguments, but the pinned dependency reports Completed and this slice may not change the dependency.\n"
                    + "## Verdict\n[ARMADA:VERDICT] SPEC_CONFLICT";
                DatabaseDriver db = testDb.Driver;

                Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                Captain judgeCaptain = await db.Captains.CreateAsync(new Captain("conflict-judge") { State = CaptainStateEnum.Working }).ConfigureAwait(false);
                Voyage voyage = await db.Voyages.CreateAsync(new Voyage("conflict") { Status = VoyageStatusEnum.InProgress }).ConfigureAwait(false);
                Mission judge = await CreateRunningMissionAsync(testDb, h, vessel, voyage, judgeCaptain, "Judge", false).ConfigureAwait(false);

                await h.Missions.HandleCompletionAsync(judgeCaptain, judge.Id).ConfigureAwait(false);

                Mission? gated = await db.Missions.ReadAsync(judge.Id).ConfigureAwait(false);
                AssertEqual(MissionStatusEnum.Review, gated!.Status, "a person decides");
                AssertEqual(0, gated.OutputRepairAttempts, "a quoted conflict is a valid verdict, not a format failure");
                AssertContains("AC-INF-3", gated.ReviewComment ?? String.Empty, "the reviewer sees the conflicting criterion");
                List<Mission> voyageMissions = await db.Missions.EnumerateByVoyageAsync(voyage.Id).ConfigureAwait(false);
                AssertEqual(1, voyageMissions.Count, "no rework Worker was created");
            }));

            cases.Add(CaseAsync("spec_conflict_without_quote_is_repaired", "A SPEC_CONFLICT verdict without the quoted criterion is a contract violation and gets a repair turn", TestTags.Negative, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Harness h = new Harness(testDb, new DirCreatingGitService());
                h.Missions.OnGetMissionOutput = _ => "This cannot be done.\n[ARMADA:VERDICT] SPEC_CONFLICT";
                DatabaseDriver db = testDb.Driver;

                Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                Captain judgeCaptain = await db.Captains.CreateAsync(new Captain("vague-judge") { State = CaptainStateEnum.Working }).ConfigureAwait(false);
                Voyage voyage = await db.Voyages.CreateAsync(new Voyage("vague") { Status = VoyageStatusEnum.InProgress }).ConfigureAwait(false);
                Mission judge = await CreateRunningMissionAsync(testDb, h, vessel, voyage, judgeCaptain, "Judge", false).ConfigureAwait(false);

                await h.Missions.HandleCompletionAsync(judgeCaptain, judge.Id).ConfigureAwait(false);

                Mission? repaired = await db.Missions.ReadAsync(judge.Id).ConfigureAwait(false);
                AssertEqual(1, repaired!.OutputRepairAttempts);
                AssertContains("Spec Conflict", repaired.Description ?? String.Empty, "the repair note names the required section");
            }));

            cases.Add(CaseAsync("stop_signals_gracefully_then_forces", "Stopping a local agent asks it to exit first and force-kills only one that ignores the request", TestTags.Reliability, async () =>
            {
                if (OperatingSystem.IsWindows()) return;
                int previous = BaseAgentRuntime.GracefulStopTimeoutMs;
                ClaudeCodeRuntime runtime = new ClaudeCodeRuntime(CreateLogging());
                try
                {
                    BaseAgentRuntime.GracefulStopTimeoutMs = 5000;
                    using (Process polite = Process.Start(new ProcessStartInfo("sleep", "30") { UseShellExecute = false })!)
                    {
                        Armada.Core.ProcessSupervisor.RecordLaunch(polite.Id, polite.StartTime.ToUniversalTime());
                        Stopwatch watch = Stopwatch.StartNew();
                        AgentStopResult politeStop = await runtime.StopAsync(polite.Id).ConfigureAwait(false);
                        AssertEqual(AgentStopOutcomeEnum.Stopped, politeStop.Outcome, "the recorded launch is stopped");
                        AssertTrue(polite.WaitForExit(2000), "the process stopped");
                        AssertTrue(watch.Elapsed < TimeSpan.FromSeconds(4), "SIGTERM ended it well before the forced-stop deadline (" + watch.Elapsed.TotalMilliseconds.ToString("0") + " ms)");
                    }

                    BaseAgentRuntime.GracefulStopTimeoutMs = 500;
                    ProcessStartInfo stubbornInfo = new ProcessStartInfo("sh") { UseShellExecute = false };
                    stubbornInfo.ArgumentList.Add("-c");
                    stubbornInfo.ArgumentList.Add("trap '' TERM; sleep 30");
                    using (Process stubborn = Process.Start(stubbornInfo)!)
                    {
                        Armada.Core.ProcessSupervisor.RecordLaunch(stubborn.Id, stubborn.StartTime.ToUniversalTime());
                        await Task.Delay(200).ConfigureAwait(false);
                        await runtime.StopAsync(stubborn.Id).ConfigureAwait(false);
                        AssertTrue(stubborn.WaitForExit(3000), "a process that ignores SIGTERM is killed after the deadline");
                    }
                }
                finally
                {
                    BaseAgentRuntime.GracefulStopTimeoutMs = previous;
                }
            }));

            foreach (bool captainCommitted in new bool[] { true, false })
            {
                bool committed = captainCommitted;
                cases.Add(CaseAsync(committed ? "leftover_changes_are_not_committed_after_the_captain_committed" : "leftover_changes_are_committed_for_a_captain_that_never_commits",
                    committed
                        ? "A captain that committed its work keeps leftover build output off the branch"
                        : "A captain that never commits still has its edits committed by Armada",
                    TestTags.Positive, async () =>
                {
                    using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                    StubGitService git = new StubGitService();
                    Harness h = new Harness(testDb, git);
                    h.Missions.OnGetMissionOutput = _ => "Updated README.md and committed it.";
                    DatabaseDriver db = testDb.Driver;

                    Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                    Captain captain = await db.Captains.CreateAsync(new Captain("leftover") { State = CaptainStateEnum.Working }).ConfigureAwait(false);
                    Voyage voyage = await db.Voyages.CreateAsync(new Voyage("leftover") { Status = VoyageStatusEnum.InProgress }).ConfigureAwait(false);
                    Mission worker = await CreateRunningMissionAsync(testDb, h, vessel, voyage, captain, "Worker", false).ConfigureAwait(false);

                    string metadata = Path.Combine(h.Settings.LogDirectory, "docks");
                    Directory.CreateDirectory(metadata);
                    File.WriteAllText(Path.Combine(metadata, worker.DockId + ".start"), "1111111111111111111111111111111111111111\n");
                    git.HeadCommitResult = committed ? "2222222222222222222222222222222222222222" : "1111111111111111111111111111111111111111";

                    await h.Missions.HandleCompletionAsync(captain, worker.Id).ConfigureAwait(false);
                    AssertEqual(committed ? 0 : 1, git.CommitAllCalls.Count, committed
                        ? "nothing beyond the captain's own commits is added"
                        : "a captain that never committed has its work rescued");
                }));
            }

            cases.Add(CaseAsync("stage_check_restores_files_its_build_regenerates", "A stage check that rewrites a tracked file leaves the dock as the stage left it", TestTags.Positive, async () =>
            {
                using StageCheckFixture fx = await StageCheckFixture.CreateAsync("echo regenerated >> gen.txt").ConfigureAwait(false);
                await fx.CommitStageChangeAsync("notes.txt", "stage work").ConfigureAwait(false);

                string? failure = await fx.Missions.VerifyStageWorkAsync(fx.Mission, fx.Captain).ConfigureAwait(false);
                AssertNull(failure, failure ?? "");
                AssertEqual("original\n", File.ReadAllText(Path.Combine(fx.Repo, "gen.txt")).Replace("\r\n", "\n"), "the regenerated tracked file is restored");
            }));

            cases.Add(CaseAsync("stage_check_blocks_a_regression", "A stage check that passes on the base commit and fails after the stage blocks the handoff", TestTags.Negative, async () =>
            {
                using StageCheckFixture fx = await StageCheckFixture.CreateAsync("test ! -f broken.txt").ConfigureAwait(false);
                await fx.CommitStageChangeAsync("broken.txt", "the stage broke the build").ConfigureAwait(false);

                string? failure = await fx.Missions.VerifyStageWorkAsync(fx.Mission, fx.Captain).ConfigureAwait(false);
                AssertNotNull(failure, "a regression this stage introduced is reported");
                AssertContains("stage check failed", failure!);
            }));

            cases.Add(CaseAsync("stage_check_ignores_a_pre_existing_failure", "A stage check that already fails on the base commit does not block the stage", TestTags.Positive, async () =>
            {
                using StageCheckFixture fx = await StageCheckFixture.CreateAsync("exit 3").ConfigureAwait(false);
                await fx.CommitStageChangeAsync("notes.txt", "unrelated stage work").ConfigureAwait(false);

                string? failure = await fx.Missions.VerifyStageWorkAsync(fx.Mission, fx.Captain).ConfigureAwait(false);
                AssertNull(failure, "a failure the base already has is not this stage's regression");
                AssertFalse(Directory.Exists(fx.Repo + ".base-check"), "the base-commit worktree is removed");
            }));

            cases.Add(CaseAsync("stage_check_skips_a_frontier_stage_that_committed_nothing", "A frontier stage that made no commit is not checked", TestTags.Positive, async () =>
            {
                using StageCheckFixture fx = await StageCheckFixture.CreateAsync("touch ran.txt && exit 3").ConfigureAwait(false);

                string? failure = await fx.Missions.VerifyStageWorkAsync(fx.Mission, fx.Captain).ConfigureAwait(false);
                AssertNull(failure);
                AssertFalse(File.Exists(Path.Combine(fx.Repo, "ran.txt")), "the check did not run");
            }));

            cases.Add(CaseAsync("stage_check_result_reports_passed_with_commands", "A passing stage check reports Passed and the commands it ran", TestTags.Positive, async () =>
            {
                using StageCheckFixture fx = await StageCheckFixture.CreateAsync("true").ConfigureAwait(false);
                await fx.CommitStageChangeAsync("notes.txt", "stage work").ConfigureAwait(false);

                StageCheckResult result = await fx.Missions.CheckStageWorkAsync(fx.Mission, fx.Captain).ConfigureAwait(false);
                AssertEqual(StageCheckStatusEnum.Passed, result.Status);
                AssertEqual(StageCheckReasonEnum.None, result.Reason);
                AssertEqual(1, result.Commands.Count, "the build command is listed");
                AssertFalse(result.IsBlocking, "a pass does not block");
                AssertNull(result.FailureMessage);
            }));

            cases.Add(CaseAsync("stage_check_result_reports_failed_with_the_command", "A regression reports Failed with the command, exit code and the note for the captain", TestTags.Negative, async () =>
            {
                using StageCheckFixture fx = await StageCheckFixture.CreateAsync("test ! -f broken.txt").ConfigureAwait(false);
                await fx.CommitStageChangeAsync("broken.txt", "the stage broke the build").ConfigureAwait(false);

                StageCheckResult result = await fx.Missions.CheckStageWorkAsync(fx.Mission, fx.Captain).ConfigureAwait(false);
                AssertEqual(StageCheckStatusEnum.Failed, result.Status);
                AssertEqual("test ! -f broken.txt", result.FailedCommand);
                AssertNotNull(result.ExitCode, "the exit code is kept");
                AssertTrue(result.IsBlocking, "a failure blocks the stage");
                AssertContains("stage check failed", result.FailureMessage!);
            }));

            cases.Add(CaseAsync("stage_check_result_lists_a_pre_existing_failure", "A failure the base commit already has is a pass that names the command", TestTags.Positive, async () =>
            {
                using StageCheckFixture fx = await StageCheckFixture.CreateAsync("exit 3").ConfigureAwait(false);
                await fx.CommitStageChangeAsync("notes.txt", "unrelated stage work").ConfigureAwait(false);

                StageCheckResult result = await fx.Missions.CheckStageWorkAsync(fx.Mission, fx.Captain).ConfigureAwait(false);
                AssertEqual(StageCheckStatusEnum.Passed, result.Status);
                AssertEqual(1, result.PreExistingFailures.Count, "the pre-existing failure is named, not hidden");
            }));

            cases.Add(CaseAsync("stage_check_result_says_why_a_frontier_stage_was_skipped", "A frontier stage with no new commit reports Skipped with NoNewCommit, not a pass", TestTags.Negative, async () =>
            {
                using StageCheckFixture fx = await StageCheckFixture.CreateAsync("touch ran.txt && exit 3").ConfigureAwait(false);

                StageCheckResult result = await fx.Missions.CheckStageWorkAsync(fx.Mission, fx.Captain).ConfigureAwait(false);
                AssertEqual(StageCheckStatusEnum.Skipped, result.Status);
                AssertEqual(StageCheckReasonEnum.NoNewCommit, result.Reason);
                AssertFalse(result.IsBlocking);
            }));

            cases.Add(CaseAsync("stage_check_result_skips_review_personas_and_missing_docks", "Review personas and missions without a dock report Skipped with their reasons", TestTags.Negative, async () =>
            {
                using StageCheckFixture fx = await StageCheckFixture.CreateAsync("exit 3").ConfigureAwait(false);
                await fx.CommitStageChangeAsync("notes.txt", "stage work").ConfigureAwait(false);

                fx.Mission.Persona = "Judge";
                StageCheckResult judge = await fx.Missions.CheckStageWorkAsync(fx.Mission, fx.Captain).ConfigureAwait(false);
                AssertEqual(StageCheckStatusEnum.Skipped, judge.Status);
                AssertEqual(StageCheckReasonEnum.ReviewPersona, judge.Reason);

                fx.Mission.Persona = "Worker";
                fx.Mission.DockId = null;
                StageCheckResult noDock = await fx.Missions.CheckStageWorkAsync(fx.Mission, fx.Captain).ConfigureAwait(false);
                AssertEqual(StageCheckStatusEnum.Skipped, noDock.Status);
                AssertEqual(StageCheckReasonEnum.NoDock, noDock.Reason);

                await AssertThrowsAsync<ArgumentNullException>(() => fx.Missions.CheckStageWorkAsync(null!, fx.Captain));
                await AssertThrowsAsync<ArgumentNullException>(() => fx.Missions.CheckStageWorkAsync(fx.Mission, null!));
            }));

            cases.Add(CaseAsync("stage_check_event_round_trips_the_result", "A stage-check event carries the mission's ids and reads back the same result", TestTags.Positive, () =>
            {
                Mission mission = new Mission("Work", "Do it") { TenantId = "ten_x", VesselId = "vsl_x", VoyageId = "vyg_x" };
                Captain captain = new Captain("qwen", AgentRuntimeEnum.Mux);
                StageCheckResult result = new StageCheckResult { Status = StageCheckStatusEnum.CouldNotRun, Reason = StageCheckReasonEnum.TimedOut, FailedCommand = "dotnet test", FailureMessage = "timed out" };
                result.Commands.Add("dotnet test");

                ArmadaEvent evt = StageCheckEvents.Build(mission, captain, result);
                AssertEqual(StageCheckEvents.EventType, evt.EventType);
                AssertEqual(mission.Id, evt.MissionId);
                AssertEqual("ten_x", evt.TenantId);
                AssertEqual(captain.Id, evt.CaptainId);
                AssertContains("could not run (TimedOut)", evt.Message);

                StageCheckResult? read = StageCheckEvents.Read(evt);
                AssertNotNull(read);
                AssertEqual(StageCheckStatusEnum.CouldNotRun, read!.Status);
                AssertEqual(StageCheckReasonEnum.TimedOut, read.Reason);
                AssertEqual("dotnet test", read.FailedCommand);
                AssertNull(StageCheckEvents.Read(new ArmadaEvent("other", "x") { Payload = evt.Payload }), "another event type is not read");
                AssertNull(StageCheckEvents.Read(new ArmadaEvent(StageCheckEvents.EventType, "x") { Payload = "{not json" }), "a malformed payload reads as null");
                AssertThrows<ArgumentNullException>(() => StageCheckEvents.Build(mission, captain, null!));
                return Task.CompletedTask;
            }));

            cases.Add(CaseAsync("quota_failure_quarantines_without_an_idle_gap", "A usage-limit failure moves the captain straight to Quarantined, so a waiting mission is never handed to it", TestTags.Reliability, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                DatabaseDriver db = testDb.Driver;
                StubGitService git = new StubGitService();
                ArmadaSettings settings = CreateSettings();
                LoggingModule logging = CreateLogging();
                IDockService docks = new DockService(logging, db, settings, git);
                CaptainService captains = new CaptainService(logging, db, settings, git, docks);
                int launches = 0;
                captains.OnLaunchAgent = (_, _, _) => { Interlocked.Increment(ref launches); return Task.FromResult(9700); };
                MissionService missions = new MissionService(logging, db, settings, docks, captains);
                AdmiralService admiral = new AdmiralService(logging, db, settings, captains, missions, new VoyageService(logging, db), docks);

                Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                Mission running = await db.Missions.CreateAsync(new Mission("Running", "Do it") { VesselId = vessel.Id, Status = MissionStatusEnum.InProgress, ProcessId = 4500 }).ConfigureAwait(false);
                Captain captain = await db.Captains.CreateAsync(new Captain("over-quota") { State = CaptainStateEnum.Working, CurrentMissionId = running.Id, ProcessId = 4500 }).ConfigureAwait(false);
                running.CaptainId = captain.Id;
                await db.Missions.UpdateAsync(running).ConfigureAwait(false);
                Mission waiting = await db.Missions.CreateAsync(new Mission("Waiting", "Do it next") { VesselId = vessel.Id }).ConfigureAwait(false);

                RuntimeProviderError quota = new RuntimeProviderError { HttpStatusCode = 429, ErrorType = "rate_limit_error", Reason = "quota exhausted" };
                await admiral.HandleProcessExitAsync(4500, RuntimeFailureClassifier.Decide(1, quota), captain.Id, running.Id).ConfigureAwait(false);

                Captain? after = await db.Captains.ReadAsync(captain.Id).ConfigureAwait(false);
                AssertEqual(CaptainStateEnum.Quarantined, after!.State);
                AssertNull(after.CurrentMissionId);
                AssertEqual(0, launches, "the quarantined captain was not given the waiting mission");
                Mission? stillWaiting = await db.Missions.ReadAsync(waiting.Id).ConfigureAwait(false);
                AssertEqual(MissionStatusEnum.Pending, stillWaiting!.Status);
            }));

            return new TestSuiteDescriptor(
                suiteId: SuiteId,
                displayName: "Handoff and Dispatch Resilience",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        private static TestCaseDescriptor CaseAsync(string caseId, string displayName, string tag, Func<Task> body)
        {
            return new TestCaseDescriptor(
                suiteId: SuiteId,
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) => body(),
                tags: new List<string> { tag });
        }

        private static LoggingModule CreateLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        private static ArmadaSettings CreateSettings()
        {
            ArmadaSettings settings = new ArmadaSettings();
            settings.DocksDirectory = TestTemp.NewDirectory("resilience_docks");
            settings.ReposDirectory = TestTemp.NewDirectory("resilience_repos");
            settings.LogDirectory = TestTemp.NewDirectory("resilience_logs");
            return settings;
        }

        private static async Task<Vessel> CreateVesselAsync(TestDatabase testDb)
        {
            Vessel vessel = new Vessel("resilience-vessel-" + Guid.NewGuid().ToString("N").Substring(0, 8), "https://github.com/test/repo.git");
            vessel.LocalPath = TestTemp.NewDirectory("resilience_bare");
            vessel.WorkingDirectory = TestTemp.NewDirectory("resilience_work");
            vessel.DefaultBranch = "main";
            return await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);
        }

        private static async Task<Mission> CreateRunningMissionAsync(TestDatabase testDb, Harness h, Vessel vessel, Voyage voyage, Captain captain, string persona, bool requiresReview)
        {
            Mission mission = new Mission("[" + persona + "] Task", "Do the task");
            mission.VesselId = vessel.Id;
            mission.VoyageId = voyage.Id;
            mission.Persona = persona;
            mission.RequiresReview = requiresReview;
            mission.Status = MissionStatusEnum.InProgress;
            mission.CaptainId = captain.Id;
            mission.BranchName = "armada/resilience/" + persona.ToLowerInvariant();
            mission = await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

            Dock dock = new Dock(vessel.Id);
            dock.CaptainId = captain.Id;
            dock.WorktreePath = Path.Combine(h.Settings.DocksDirectory, vessel.Name, mission.Id);
            Directory.CreateDirectory(dock.WorktreePath);
            dock.BranchName = mission.BranchName;
            dock.Active = true;
            dock = await testDb.Driver.Docks.CreateAsync(dock).ConfigureAwait(false);

            mission.DockId = dock.Id;
            await testDb.Driver.Missions.UpdateAsync(mission).ConfigureAwait(false);
            captain.CurrentMissionId = mission.Id;
            captain.CurrentDockId = dock.Id;
            await testDb.Driver.Captains.UpdateAsync(captain).ConfigureAwait(false);
            return mission;
        }

        private static async Task PutInProgressAsync(DatabaseDriver db, string missionId, string captainId)
        {
            Mission? mission = await db.Missions.ReadAsync(missionId).ConfigureAwait(false);
            if (mission == null || mission.Status == MissionStatusEnum.Failed) return;
            mission.Status = MissionStatusEnum.InProgress;
            mission.CaptainId = captainId;
            await db.Missions.UpdateAsync(mission).ConfigureAwait(false);

            Captain? captain = await db.Captains.ReadAsync(captainId).ConfigureAwait(false);
            captain!.State = CaptainStateEnum.Working;
            captain.CurrentMissionId = missionId;
            await db.Captains.UpdateAsync(captain).ConfigureAwait(false);
        }

        private static int CountOccurrences(string text, string value)
        {
            int count = 0;
            int index = 0;
            while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += value.Length;
            }
            return count;
        }

        #endregion

        #region Private-Classes

        private sealed class StageCheckFixture : IDisposable
        {
            public TestDatabase Db { get; private set; } = null!;

            public MissionService Missions { get; private set; } = null!;

            public Mission Mission { get; private set; } = null!;

            public Captain Captain { get; private set; } = null!;

            public string Repo { get; private set; } = "";

            public static async Task<StageCheckFixture> CreateAsync(string buildCommand)
            {
                StageCheckFixture fx = new StageCheckFixture();
                fx.Db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                DatabaseDriver db = fx.Db.Driver;
                LoggingModule logging = CreateLogging();
                ArmadaSettings settings = CreateSettings();
                GitService git = new GitService(logging);
                IDockService docks = new DockService(logging, db, settings, git);
                CaptainService captains = new CaptainService(logging, db, settings, git, docks);
                fx.Missions = new MissionService(logging, db, settings, docks, captains, git: git);

                fx.Repo = TestTemp.NewDirectory("stagecheck_repo");
                await Git(fx.Repo, "init", "-q", "-b", "main").ConfigureAwait(false);
                File.WriteAllText(Path.Combine(fx.Repo, "gen.txt"), "original\n");
                await Git(fx.Repo, "add", "-A").ConfigureAwait(false);
                await Git(fx.Repo, "-c", "user.name=t", "-c", "user.email=t@t", "-c", "commit.gpgsign=false", "commit", "-q", "-m", "base").ConfigureAwait(false);
                string start = (await Git(fx.Repo, "rev-parse", "HEAD").ConfigureAwait(false)).Trim();

                Vessel vessel = await db.Vessels.CreateAsync(new Vessel("stagecheck-" + Guid.NewGuid().ToString("N").Substring(0, 6), "https://example.invalid/repo.git")).ConfigureAwait(false);
                await db.WorkflowProfiles.CreateAsync(new WorkflowProfile { Name = "check", Scope = WorkflowProfileScopeEnum.Vessel, VesselId = vessel.Id, IsDefault = true, Active = true, BuildCommand = buildCommand }).ConfigureAwait(false);
                Dock dock = await db.Docks.CreateAsync(new Dock(vessel.Id) { WorktreePath = fx.Repo, Active = true }).ConfigureAwait(false);
                string metadata = Path.Combine(settings.LogDirectory, "docks");
                Directory.CreateDirectory(metadata);
                File.WriteAllText(Path.Combine(metadata, dock.Id + ".start"), start + "\n");

                fx.Captain = await db.Captains.CreateAsync(new Captain("frontier", AgentRuntimeEnum.ClaudeCode) { State = CaptainStateEnum.Working }).ConfigureAwait(false);
                fx.Mission = await db.Missions.CreateAsync(new Mission("Work", "Do it") { VesselId = vessel.Id, DockId = dock.Id, Persona = "Worker", Status = MissionStatusEnum.InProgress }).ConfigureAwait(false);
                return fx;
            }

            public async Task CommitStageChangeAsync(string file, string content)
            {
                File.WriteAllText(Path.Combine(Repo, file), content + "\n");
                await Git(Repo, "add", "-A").ConfigureAwait(false);
                await Git(Repo, "-c", "user.name=t", "-c", "user.email=t@t", "-c", "commit.gpgsign=false", "commit", "-q", "-m", "stage").ConfigureAwait(false);
            }

            public void Dispose()
            {
                Db?.Dispose();
            }

            private static async Task<string> Git(string directory, params string[] args)
            {
                ProcessStartInfo info = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                foreach (string arg in args) info.ArgumentList.Add(arg);
                using Process process = Process.Start(info)!;
                string output = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
                await process.WaitForExitAsync().ConfigureAwait(false);
                return output;
            }
        }

        private sealed class Harness
        {
            public ArmadaSettings Settings { get; }

            public CaptainService Captains { get; }

            public MissionService Missions { get; }

            public Harness(TestDatabase testDb, IGitService git)
            {
                LoggingModule logging = CreateLogging();
                Settings = CreateSettings();
                IDockService docks = new DockService(logging, testDb.Driver, Settings, git);
                Captains = new CaptainService(logging, testDb.Driver, Settings, git, docks);
                Missions = new MissionService(logging, testDb.Driver, Settings, docks, Captains, git: git);
            }
        }

        #endregion
    }
}
