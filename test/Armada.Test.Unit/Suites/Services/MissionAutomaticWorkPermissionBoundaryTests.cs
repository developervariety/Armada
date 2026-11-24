namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;
    using TestResourcePressure = global::Test.Shared.Infrastructure.TestResourcePressure;

    /// <summary>Exercises permission holds at completion, durable continuation and dependent assignment owners.</summary>
    public sealed class MissionAutomaticWorkPermissionBoundaryTests : TestSuite
    {
        private static readonly TimeSpan _Limit = TimeSpan.FromSeconds(30);

        /// <inheritdoc />
        public override string Name => "Mission Automatic Work Permission Boundaries";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("An opted-in unexpected exit holds its launch and downstream work without automatic retry", async () =>
            {
                foreach (int? exitCode in new int?[] { -1, 137, 1, null })
                foreach (MissionStatusEnum runningStatus in new[] { MissionStatusEnum.Assigned, MissionStatusEnum.InProgress,
                    MissionStatusEnum.Testing, MissionStatusEnum.Review })
                {
                    using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    using (Harness h = await Harness.CreateAsync(db).ConfigureAwait(false))
                    {
                        Mission parent = await h.CreateParentAsync(runningStatus, true).ConfigureAwait(false);
                        Mission child = await h.CreateChildAsync(parent, false).ConfigureAwait(false);
                        Captain captain = await h.ClaimAsync(parent).ConfigureAwait(false);
                        await h.EnableAsync(parent).ConfigureAwait(false);
                        Mission before = await h.ReadAsync(parent.Id).ConfigureAwait(false);
                        AdmiralService admiral = h.NewAdmiral();
                        await admiral.HandleProcessExitAsync(parent.ProcessId!.Value, exitCode, captain.Id, parent.Id).ConfigureAwait(false);
                        Mission held = await h.ReadAsync(parent.Id).ConfigureAwait(false);
                        AssertTrue(held.HeldForOperatorReview,
                            "An opted-in unexpected exit must hold before automatic retry or failure policy; exit " + exitCode);
                        AssertNull(held.ProcessId);
                        AssertEqual(before.StartedUtc, held.StartedUtc, "The hold preserves the actual launch.");
                        AssertEqual(before.CommitHash, held.CommitHash, "The hold preserves captured source.");
                        AssertEqual(0, h.Launches, "No replacement captain may start automatically.");
                        AssertFalse(File.Exists(h.Marker), "No verification command may start on the failed exit.");
                        await AssertUntouchedChildAsync(h, child).ConfigureAwait(false);
                        List<ArmadaEvent> events = await db.Driver.Events.EnumerateByMissionAsync(parent.Id, 100).ConfigureAwait(false);
                        AssertFalse(events.Any(item => item.EventType == "mission.interrupted_redispatched"
                            || item.EventType == "mission.failed" || item.EventType == "mission.failed_recoverable_work"),
                            "The exit cannot publish automatic retry or rescue-triggering failure while held.");
                        Captain released = (await db.Driver.Captains.ReadAsync(captain.Id).ConfigureAwait(false))!;
                        AssertEqual(CaptainStateEnum.Idle, released.State);
                        AssertEqual(MissionStatusEnum.Review, held.Status, "The held exit is not successful produced work.");
                        AssertTrue((held.FailureReason ?? "").Contains("exited with code", StringComparison.Ordinal));
                        AssertEqual(before.DockId, held.DockId);
                        AssertTrue((await db.Driver.Docks.ReadAsync(held.DockId!).ConfigureAwait(false))!.Active,
                            "The operator must retain access to the actual worktree.");
                        await h.GrantAsync(held, MissionAutomaticActionEnum.Verification).ConfigureAwait(false);
                        await h.GrantAsync(held, MissionAutomaticActionEnum.DependentAdmission).ConfigureAwait(false);
                        await h.Service.HandleCompletionAsync(captain, parent.Id).ConfigureAwait(false);
                        await admiral.HandleProcessExitAsync(parent.ProcessId.Value, exitCode, captain.Id, parent.Id).ConfigureAwait(false);
                        AssertTrue((await h.ReadAsync(parent.Id).ConfigureAwait(false)).HeldForOperatorReview,
                            "Grants and late callbacks must not turn a failed exit into accepted work.");
                        AssertEqual(0, await h.Service.StartInterruptedDefinitionOfDoneGateRerunsAsync().ConfigureAwait(false));
                        AssertFalse(File.Exists(h.Marker));
                        bool clearRefused = false;
                        try { await h.Service.ClearOperatorReviewHoldAsync(parent.Id, "operator", "Inspect actual exit").ConfigureAwait(false); }
                        catch (InvalidOperationException) { clearRefused = true; }
                        AssertTrue(clearRefused, "Clearing a PASS hold cannot accept a runtime failure.");
                        bool approvalRefused = false;
                        try { await h.Service.ApproveReviewAsync(parent.Id, "operator").ConfigureAwait(false); }
                        catch (InvalidOperationException) { approvalRefused = true; }
                        AssertTrue(approvalRefused, "An exit hold is not an approvable successful review.");
                        await AssertUntouchedChildAsync(h, child).ConfigureAwait(false);
                        await h.Service.DenyReviewAsync(parent.Id, "operator", "Reject this actual failed exit",
                            ReviewDenyActionEnum.FailPipeline).ConfigureAwait(false);
                        AssertEqual(MissionStatusEnum.Failed, (await h.ReadAsync(parent.Id).ConfigureAwait(false)).Status,
                            "The operator retains the supported explicit failure decision.");
                    }
                }
            }).ConfigureAwait(false);

            await RunTest("Unpermitted completion retains its raw refusal or blocked result before automatic failure policy", async () =>
            {
                foreach (string output in new[] { CaptainRefusalClassifier.RefusalMarker + ": I will not inspect the sample binary",
                    "[ARMADA:RESULT] BLOCKED\nAn operator must supply the missing prerequisite." })
                {
                    using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    using (Harness h = await Harness.CreateAsync(db).ConfigureAwait(false))
                    {
                        Mission parent = await h.CreateParentAsync(MissionStatusEnum.InProgress, true).ConfigureAwait(false);
                        Mission child = await h.CreateChildAsync(parent, false).ConfigureAwait(false);
                        Captain captain = await h.ClaimAsync(parent).ConfigureAwait(false);
                        await h.EnableAsync(parent).ConfigureAwait(false);
                        h.Service.OnGetMissionOutput = _ => output;
                        await h.Service.HandleCompletionAsync(captain, parent.Id).ConfigureAwait(false);
                        Mission held = await h.ReadAsync(parent.Id).ConfigureAwait(false);
                        AssertEqual(MissionStatusEnum.WorkProduced, held.Status,
                            "Missing permission must stop completion before refusal or failure policy can act.");
                        AssertEqual(output, held.AgentOutput, "The actual outcome remains available for the operator decision.");
                        AssertFalse(File.Exists(h.Marker));
                        AssertEqual(0, h.Launches);
                        await AssertUntouchedChildAsync(h, child).ConfigureAwait(false);
                        AssertFalse((await db.Driver.Events.EnumerateByMissionAsync(parent.Id, 100).ConfigureAwait(false))
                            .Any(item => item.EventType == "mission.failed" || item.EventType == "mission.policy_refusal_continued"));
                    }
                }
            }).ConfigureAwait(false);

            await RunTest("Unexpected completion returns without blocking its caller while verification permission is missing", async () =>
            {
                using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                using (Harness h = await Harness.CreateAsync(db).ConfigureAwait(false))
                using (CancellationTokenSource stop = new CancellationTokenSource(_Limit))
                {
                    Mission parent = await h.CreateParentAsync(MissionStatusEnum.InProgress, true).ConfigureAwait(false);
                    Mission child = await h.CreateChildAsync(parent, false).ConfigureAwait(false);
                    Captain captain = await h.ClaimAsync(parent).ConfigureAwait(false);
                    await h.EnableAsync(parent).ConfigureAwait(false);
                    Task completion = h.Service.HandleCompletionAsync(captain, parent.Id, stop.Token);
                    bool returnedWithoutGrant = false;
                    try
                    {
                        try
                        {
                            await completion.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                            returnedWithoutGrant = true;
                        }
                        catch (TimeoutException)
                        {
                            stop.Cancel();
                            await DrainAsync(completion).ConfigureAwait(false);
                        }
                        AssertTrue(returnedWithoutGrant, "Completion callers must return without waiting for operator permission.");
                        Mission held = await h.ReadAsync(parent.Id).ConfigureAwait(false);
                        AssertEqual(MissionStatusEnum.WorkProduced, held.Status);
                        AssertNull(held.ProcessId);
                        AssertFalse(File.Exists(h.Marker), "No gate command may run before verification permission.");
                        Captain released = (await db.Driver.Captains.ReadAsync(captain.Id).ConfigureAwait(false))!;
                        AssertEqual(CaptainStateEnum.Idle, released.State);
                        AssertNull(released.CurrentMissionId);
                        await AssertUntouchedChildAsync(h, child).ConfigureAwait(false);
                        if (h.Service.InFlightCompletionsForTests.TryGetValue(parent.Id,
                            out MissionService.CompletionGuardEntry? retained) && retained != null)
                            AssertTrue(retained.Gate.IsCompleted, "A retained deduplication record must not keep completion active.");
                        await h.GrantAsync(held, MissionAutomaticActionEnum.DependentAdmission).ConfigureAwait(false);
                        AssertEqual(0, await h.Service.StartInterruptedDefinitionOfDoneGateRerunsAsync().ConfigureAwait(false));
                        AssertFalse(await h.Service.TryAssignAsync(await h.ReadAsync(child.Id).ConfigureAwait(false), h.Vessel).ConfigureAwait(false));
                        await AssertUntouchedChildAsync(h, child).ConfigureAwait(false);
                        await h.GrantAsync(held, MissionAutomaticActionEnum.Verification).ConfigureAwait(false);
                        TaskCompletionSource<Task> settled = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
                        h.Service.OnReviewRequested = mission =>
                        {
                            if (mission.Id == parent.Id && h.Service.InFlightCompletionsForTests.TryGetValue(mission.Id,
                                out MissionService.CompletionGuardEntry? entry) && entry != null) settled.TrySetResult(entry.Gate);
                        };
                        AssertEqual(1, await h.Service.StartInterruptedDefinitionOfDoneGateRerunsAsync().ConfigureAwait(false));
                        Task resumed = await settled.Task.WaitAsync(_Limit).ConfigureAwait(false);
                        await resumed.WaitAsync(_Limit).ConfigureAwait(false);
                        AssertTrue(File.ReadAllLines(h.Marker).Select(line => line.Trim()).SequenceEqual(new[] { "build", "test" }));
                        Mission review = await h.ReadAsync(parent.Id).ConfigureAwait(false);
                        AssertEqual(MissionStatusEnum.Review, review.Status);
                        AssertTrue(review.RequiresReview);
                        AssertNull(review.ReviewedUtc);
                        AssertNotNull(review.ReviewRequestedUtc);
                        await AssertPassedGateAsync(h, review).ConfigureAwait(false);
                        AssertFalse(await h.Service.TryAssignAsync(await h.ReadAsync(child.Id).ConfigureAwait(false), h.Vessel).ConfigureAwait(false));
                        await AssertUntouchedChildAsync(h, child).ConfigureAwait(false);
                    }
                    finally
                    {
                        stop.Cancel();
                        await DrainAsync(completion).ConfigureAwait(false);
                    }
                }
            }).ConfigureAwait(false);

            foreach (bool prepared in new[] { true, false })
            {
                await RunTest((prepared ? "Prepared matching branch" : "Unprepared handoff") + " cannot provision or launch without the current parent grant", async () =>
                {
                    using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    using (Harness h = await Harness.CreateAsync(db).ConfigureAwait(false))
                    {
                        Mission parent = await h.CreateParentAsync(MissionStatusEnum.WorkProduced).ConfigureAwait(false);
                        Mission child = await h.CreateChildAsync(parent, prepared).ConfigureAwait(false);
                        await h.EnableAsync(parent).ConfigureAwait(false);
                        AssertFalse(await h.Service.TryAssignAsync(child, h.Vessel).ConfigureAwait(false));
                        await AssertUntouchedChildAsync(h, child).ConfigureAwait(false);
                        Mission held = await h.ReadAsync(child.Id).ConfigureAwait(false);
                        AssertEqual(MissionAssignmentStateEnum.WaitingForDependency, held.AssignmentState);
                        AssertTrue((await db.Driver.Events.EnumerateByMissionAsync(parent.Id, 100).ConfigureAwait(false))
                            .Any(item => item.EventType == "mission.automatic_work_held"));

                        // Complete is a normal dependency-satisfying state. Reuse exactly the same fixture
                        // after permission, so an absent captain or broken dock cannot explain the hold.
                        await h.GrantAsync(parent, MissionAutomaticActionEnum.DependentAdmission).ConfigureAwait(false);
                        parent = await h.ReadAsync(parent.Id).ConfigureAwait(false);
                        parent.Status = MissionStatusEnum.Complete;
                        await db.Driver.Missions.UpdateAsync(parent).ConfigureAwait(false);
                        bool assigned = await h.Service.TryAssignAsync(await h.ReadAsync(child.Id).ConfigureAwait(false), h.Vessel).ConfigureAwait(false);
                        AssertTrue(assigned, "The same dependent must reach the launch owner after its parent is admitted.");
                        AssertEqual(1, h.Launches);
                        AssertTrue(h.Git.WorktreeCalls.Count > 0);
                        AssertEqual(MissionStatusEnum.InProgress, (await h.ReadAsync(child.Id).ConfigureAwait(false)).Status);
                    }
                    if (prepared) await RunAcceptedBarrierPermissionControlAsync().ConfigureAwait(false);
                }).ConfigureAwait(false);
            }

            await RunTest("A finished parallel sibling still needs its own dependent permission", async () =>
            {
                using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                using (Harness h = await Harness.CreateAsync(db).ConfigureAwait(false))
                {
                    Mission parent = await h.CreateParentAsync(MissionStatusEnum.WorkProduced).ConfigureAwait(false);
                    Mission sibling = await h.CreateParentAsync(MissionStatusEnum.WorkProduced).ConfigureAwait(false);
                    Mission child = await h.CreateChildAsync(parent, true).ConfigureAwait(false);
                    await h.EnableAsync(parent).ConfigureAwait(false);
                    await h.GrantAsync(parent, MissionAutomaticActionEnum.DependentAdmission).ConfigureAwait(false);
                    await h.EnableAsync(sibling).ConfigureAwait(false);
                    foreach (Mission producer in new[] { parent, sibling })
                    {
                        Mission current = await h.ReadAsync(producer.Id).ConfigureAwait(false);
                        current.Status = MissionStatusEnum.Complete;
                        await db.Driver.Missions.UpdateAsync(current).ConfigureAwait(false);
                    }
                    AssertFalse(await h.Service.TryAssignAsync(child, h.Vessel).ConfigureAwait(false));
                    await AssertUntouchedChildAsync(h, child).ConfigureAwait(false);
                    AssertEqual(MissionAssignmentStateEnum.WaitingForDependency, (await h.ReadAsync(child.Id).ConfigureAwait(false)).AssignmentState);

                    sibling = await h.ReadAsync(sibling.Id).ConfigureAwait(false);
                    await h.GrantAsync(sibling, MissionAutomaticActionEnum.DependentAdmission).ConfigureAwait(false);
                    Mission grantedSibling = await h.ReadAsync(sibling.Id).ConfigureAwait(false);
                    AssertEqual(MissionStatusEnum.Complete, grantedSibling.Status);
                    AssertEqual(sibling.StartedUtc, grantedSibling.StartedUtc);
                    AssertEqual(sibling.CommitHash, grantedSibling.CommitHash);
                    AssertTrue(await h.Service.TryAssignAsync(await h.ReadAsync(child.Id).ConfigureAwait(false), h.Vessel).ConfigureAwait(false));
                    AssertEqual(1, h.Launches);
                }
            }).ConfigureAwait(false);

            foreach (bool retryReview in new[] { false, true })
            {
                await RunTest((retryReview ? "RetryStage" : "Same-ID restart") + " keeps old permission historical and blocks the next attempt", async () =>
                {
                    using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    using (Harness h = await Harness.CreateAsync(db).ConfigureAwait(false))
                    {
                        Mission parent = await h.CreateParentAsync(MissionStatusEnum.Review, retryReview).ConfigureAwait(false);
                        await h.EnableAsync(parent).ConfigureAwait(false);
                        await h.GrantAsync(parent, MissionAutomaticActionEnum.Verification).ConfigureAwait(false);
                        await h.GrantAsync(parent, MissionAutomaticActionEnum.DependentAdmission).ConfigureAwait(false);
                        parent = await h.ReadAsync(parent.Id).ConfigureAwait(false);
                        string historical = JsonSerializer.Serialize(parent.AutomaticWorkPermission);
                        // Keep the retry owner from starting a new captain. The next durable launch identity
                        // below is explicit fixture input, not a claim that this test ran a new captain.
                        Captain unavailable = (await db.Driver.Captains.ReadAsync(h.Captain.Id).ConfigureAwait(false))!;
                        unavailable.State = CaptainStateEnum.Working;
                        unavailable.CurrentMissionId = "another-owned-mission";
                        await db.Driver.Captains.UpdateAsync(unavailable).ConfigureAwait(false);
                        Mission rewound;
                        if (retryReview)
                        {
                            rewound = await h.Service.DenyReviewAsync(parent.Id, Constants.DefaultUserId,
                                "Repeat this stage", ReviewDenyActionEnum.RetryStage).ConfigureAwait(false);
                        }
                        else
                        {
                            parent.Status = MissionStatusEnum.Failed;
                            await db.Driver.Missions.UpdateAsync(parent).ConfigureAwait(false);
                            rewound = await new MissionRestartService(db.Driver, h.Settings)
                                .RestartAsync(parent).ConfigureAwait(false);
                        }
                        AssertEqual(parent.Id, rewound.Id);
                        AssertNull(rewound.StartedUtc);
                        AssertNull(rewound.CommitHash);
                        AssertEqual(historical, JsonSerializer.Serialize(rewound.AutomaticWorkPermission));
                        Mission next = await h.ReadAsync(parent.Id).ConfigureAwait(false);
                        next.Status = MissionStatusEnum.WorkProduced;
                        next.StartedUtc = DateTime.UtcNow;
                        next.CommitHash = new string('b', 40);
                        next.BranchName = "next-attempt";
                        next.RequiresReview = false;
                        await db.Driver.Missions.UpdateAsync(next).ConfigureAwait(false);
                        unavailable = (await db.Driver.Captains.ReadAsync(h.Captain.Id).ConfigureAwait(false))!;
                        unavailable.State = CaptainStateEnum.Idle;
                        unavailable.CurrentMissionId = null;
                        unavailable.CurrentDockId = null;
                        unavailable.ProcessId = null;
                        await db.Driver.Captains.UpdateAsync(unavailable).ConfigureAwait(false);
                        Mission child = await h.CreateChildAsync(next, true).ConfigureAwait(false);
                        AssertFalse(await h.Service.TryAssignAsync(child, h.Vessel).ConfigureAwait(false));
                        await AssertUntouchedChildAsync(h, child).ConfigureAwait(false);
                        AssertEqual(0, h.Launches);
                        AssertTrue((await db.Driver.Events.EnumerateByMissionAsync(next.Id, 100).ConfigureAwait(false))
                            .Any(item => item.EventType == "mission.automatic_work_held"
                                && item.Payload != null && item.Payload.Contains("automatic_work_permission_stale", StringComparison.Ordinal)),
                            "The old launch/source grant must be the actual refusal, rather than an unavailable captain.");
                    }
                }).ConfigureAwait(false);
            }

            await RunTest("A gate result must match a measured current commit before resuming completion", async () =>
            {
                foreach (bool unmeasured in new[] { false, true })
                {
                using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                using (Harness h = await Harness.CreateAsync(db).ConfigureAwait(false))
                {
                    Mission parent = await h.CreateParentAsync(MissionStatusEnum.WorkProduced, true).ConfigureAwait(false);
                    await h.ClaimAsync(parent).ConfigureAwait(false);
                    parent = await h.ReadAsync(parent.Id).ConfigureAwait(false);
                    parent.ProcessId = null;
                    await db.Driver.Missions.UpdateAsync(parent).ConfigureAwait(false);
                    await h.EnableAsync(parent).ConfigureAwait(false);
                    ArmadaEvent oldResult = new ArmadaEvent(DefinitionOfDoneEvaluationRecord.EventType, "Earlier source passed");
                    oldResult.TenantId = parent.TenantId;
                    oldResult.UserId = parent.UserId;
                    oldResult.MissionId = parent.Id;
                    oldResult.Payload = JsonSerializer.Serialize(new DefinitionOfDoneEvaluationRecord
                    {
                        Outcome = DefinitionOfDoneEvaluationOutcomeEnum.Passed,
                        CommitHash = unmeasured ? parent.CommitHash : new string('b', 40),
                        MeasuredCommitHash = unmeasured ? null : new string('b', 40),
                        BranchName = parent.BranchName, CaptainId = parent.CaptainId, DockId = parent.DockId,
                        RecoveryAttempts = parent.RecoveryAttempts
                    });
                    await db.Driver.Events.CreateAsync(oldResult).ConfigureAwait(false);
                    MissionService recovered = h.NewService();
                    TaskCompletionSource<Task> settled = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
                    recovered.OnReviewRequested = mission =>
                    {
                        if (mission.Id == parent.Id && recovered.InFlightCompletionsForTests.TryGetValue(mission.Id,
                            out MissionService.CompletionGuardEntry? entry) && entry != null) settled.TrySetResult(entry.Gate);
                    };
                    int started = await recovered.StartInterruptedDefinitionOfDoneGateRerunsAsync().ConfigureAwait(false);
                    if (started != 0)
                    {
                        Task completed = await settled.Task.WaitAsync(_Limit).ConfigureAwait(false);
                        await completed.WaitAsync(_Limit).ConfigureAwait(false);
                    }
                    AssertEqual(0, started, "A result for another or unmeasured commit must not bypass verification permission.");
                    AssertEqual(MissionStatusEnum.WorkProduced, (await h.ReadAsync(parent.Id).ConfigureAwait(false)).Status);
                    AssertFalse(File.Exists(h.Marker));
                    AssertEqual(0, h.Launches);
                }
                }
            }).ConfigureAwait(false);

            await RunTest("A new service resumes durable WorkProduced after grant without a new captain or counter reset", async () =>
            {
                using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                using (Harness h = await Harness.CreateAsync(db).ConfigureAwait(false))
                using (CancellationTokenSource stop = new CancellationTokenSource(_Limit))
                {
                    Mission parent = await h.CreateParentAsync(MissionStatusEnum.InProgress, true).ConfigureAwait(false);
                    Captain captain = await h.ClaimAsync(parent).ConfigureAwait(false);
                    await h.EnableAsync(parent).ConfigureAwait(false);
                    Task interrupted = h.Service.HandleCompletionAsync(captain, parent.Id, stop.Token);
                    try
                    {
                        await interrupted.WaitAsync(stop.Token).ConfigureAwait(false);
                    }
                    finally
                    {
                        stop.Cancel();
                        await DrainAsync(interrupted).ConfigureAwait(false);
                    }
                    Mission durable = await h.ReadAsync(parent.Id).ConfigureAwait(false);
                    AssertEqual(MissionStatusEnum.WorkProduced, durable.Status);
                    AssertNull(durable.ProcessId);
                    MissionService recovered = h.NewService();
                    AssertEqual(0, await recovered.StartInterruptedDefinitionOfDoneGateRerunsAsync().ConfigureAwait(false));
                    AssertFalse(File.Exists(h.Marker));
                    // The interrupted-gate owner also sees repeated health cycles while permission is absent.
                    // A permission hold is not an attempted command failure and must not add failure chatter.
                    Mission held = await h.ReadAsync(parent.Id).ConfigureAwait(false);
                    held.HeldForOperatorReview = true;
                    held.HeldForOperatorReviewReason = MissionService.DefinitionOfDoneNotRunHoldPrefix + "Owned interrupted gate";
                    await db.Driver.Missions.UpdateAsync(held).ConfigureAwait(false);
                    string logPath = MissionAttemptArtifacts.LogPath(h.Settings.LogDirectory, held);
                    string beforeRepeatedHold = File.ReadAllText(logPath);
                    for (int cycle = 0; cycle < 2; cycle++)
                        AssertNull(await recovered.RerunInterruptedDefinitionOfDoneGateAsync(parent.Id, CancellationToken.None).ConfigureAwait(false));
                    AssertEqual(beforeRepeatedHold, File.ReadAllText(logPath),
                        "Repeated permission holds must not append validation-started or did-not-finish activity.");
                    AssertFalse(File.Exists(h.Marker));
                    held = await h.ReadAsync(parent.Id).ConfigureAwait(false);
                    held.HeldForOperatorReview = false;
                    held.HeldForOperatorReviewReason = null;
                    await db.Driver.Missions.UpdateAsync(held).ConfigureAwait(false);
                    await h.GrantAsync(durable, MissionAutomaticActionEnum.Verification, recovered).ConfigureAwait(false);
                    TaskCompletionSource<Task> settled = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
                    recovered.OnReviewRequested = mission =>
                    {
                        if (mission.Id == parent.Id && recovered.InFlightCompletionsForTests.TryGetValue(mission.Id,
                            out MissionService.CompletionGuardEntry? entry) && entry != null) settled.TrySetResult(entry.Gate);
                    };
                    Task? owned = null;
                    try
                    {
                        AssertEqual(1, await recovered.StartInterruptedDefinitionOfDoneGateRerunsAsync().ConfigureAwait(false));
                        owned = await settled.Task.WaitAsync(_Limit).ConfigureAwait(false);
                        await owned.WaitAsync(_Limit).ConfigureAwait(false);
                        Mission resumed = await h.ReadAsync(parent.Id).ConfigureAwait(false);
                        AssertEqual(MissionStatusEnum.Review, resumed.Status);
                        AssertEqual(durable.StartedUtc, resumed.StartedUtc);
                        AssertEqual(durable.CommitHash, resumed.CommitHash);
                        AssertEqual(durable.RecoveryAttempts, resumed.RecoveryAttempts);
                        AssertEqual(durable.LandingRetryCount, resumed.LandingRetryCount);
                        AssertEqual(durable.CaptainId, resumed.CaptainId);
                        AssertNull(resumed.ProcessId);
                        AssertEqual(0, h.Launches);
                        AssertTrue(File.ReadAllLines(h.Marker).Select(line => line.Trim()).SequenceEqual(new[] { "build", "test" }));
                        await AssertPassedGateAsync(h, resumed).ConfigureAwait(false);
                        AssertEqual(0, await recovered.StartInterruptedDefinitionOfDoneGateRerunsAsync().ConfigureAwait(false));
                    }
                    finally
                    {
                        DefinitionOfDoneGateRuns.Cancel(parent.Id);
                        if (owned == null && recovered.InFlightCompletionsForTests.TryGetValue(parent.Id,
                            out MissionService.CompletionGuardEntry? entry) && entry != null) owned = entry.Gate;
                        if (owned != null) await owned.WaitAsync(_Limit).ConfigureAwait(false);
                    }
                }
            }).ConfigureAwait(false);
        }

        private static async Task DrainAsync(Task task)
        {
            try { await task.WaitAsync(_Limit).ConfigureAwait(false); }
            catch (OperationCanceledException) when (task.IsCanceled) { }
        }

        private async Task RunAcceptedBarrierPermissionControlAsync()
        {
            using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
            using (Harness h = await Harness.CreateAsync(db).ConfigureAwait(false))
            {
                h.Settings.LandingMode = LandingModeEnum.LocalMerge;
                // Completion accepts a running launch. A pre-existing WorkProduced row is
                // intentionally an idempotent no-op for this public completion owner.
                Mission architect = await h.CreateParentAsync(MissionStatusEnum.InProgress).ConfigureAwait(false);
                architect.Persona = "Architect";
                architect.StartedUtc = null;
                architect.CommitHash = null;
                architect.BranchName = null;
                architect.DiffSnapshot = null;
                architect.StageOrder = null;
                architect.AgentOutput = BarrierPlan();
                await db.Driver.Missions.UpdateAsync(architect).ConfigureAwait(false);
                Mission template = await h.CreateChildAsync(architect, false).ConfigureAwait(false);
                template.Persona = "Worker";
                await db.Driver.Missions.UpdateAsync(template).ConfigureAwait(false);

                // Complete the real planner through its configured gate owner. A WorkProduced
                // row alone is not a recorded gate result and cannot authorize recovery. Restrict
                // this fixture captain to Architect until completion returns, so its final pending
                // sweep cannot launch a branch while this test is preparing its permission cases.
                string? allowedPersonas = h.Captain.AllowedPersonas;
                h.Captain.AllowedPersonas = "[\"Architect\"]";
                await db.Driver.Captains.UpdateAsync(h.Captain).ConfigureAwait(false);
                IReadOnlyList<string> changedFiles = h.Git.ChangedFilesSinceResult;
                h.Git.ChangedFilesSinceResult = Array.Empty<string>();
                Func<string, string?>? priorOutput = h.Service.OnGetMissionOutput;
                h.Service.OnGetMissionOutput = _ => "[ARMADA:RESULT] COMPLETE\n" + BarrierPlan();
                try
                {
                    Captain plannerCaptain = await h.ClaimAsync(architect).ConfigureAwait(false);
                    Mission runningPlanner = await h.ReadAsync(architect.Id).ConfigureAwait(false);
                    AssertEqual(MissionStatusEnum.InProgress, runningPlanner.Status,
                        "The real completion owner receives a running planner, not an already processed WorkProduced row.");
                    AssertNull(runningPlanner.AutomaticWorkPermission, "This planner fixture has not opted into operator permissions.");
                    await h.Service.HandleCompletionAsync(plannerCaptain, architect.Id).ConfigureAwait(false);
                }
                finally
                {
                    h.Git.ChangedFilesSinceResult = changedFiles;
                    h.Service.OnGetMissionOutput = priorOutput;
                    Captain current = (await db.Driver.Captains.ReadAsync(h.Captain.Id).ConfigureAwait(false))!;
                    current.AllowedPersonas = allowedPersonas;
                    await db.Driver.Captains.UpdateAsync(current).ConfigureAwait(false);
                }
                Mission completedPlanner = await h.ReadAsync(architect.Id).ConfigureAwait(false);
                List<ArmadaEvent> plannerEvents = await db.Driver.Events.EnumerateByMissionAsync(architect.Id, 100).ConfigureAwait(false);
                List<ArmadaEvent> plannerEvaluations = plannerEvents.Where(item => item.EventType == DefinitionOfDoneEvaluationRecord.EventType).ToList();
                AssertEqual(1, plannerEvaluations.Count,
                    "Actual planner completion must record one gate outcome before barrier assertions; status=" + completedPlanner.Status
                    + "; failure=" + (completedPlanner.FailureReason ?? "none")
                    + "; hold=" + (completedPlanner.HeldForOperatorReviewReason ?? "none")
                    + "; events=" + String.Join(",", plannerEvents.Select(item => item.EventType)));
                ArmadaEvent plannerEvaluation = plannerEvaluations.Single();
                DefinitionOfDoneEvaluationRecord plannerRecord = JsonSerializer.Deserialize<DefinitionOfDoneEvaluationRecord>(plannerEvaluation.Payload!)!;
                AssertTrue(plannerRecord.Outcome == DefinitionOfDoneEvaluationOutcomeEnum.Skipped
                    || plannerRecord.Outcome == DefinitionOfDoneEvaluationOutcomeEnum.NotVerifiable,
                    "The real gate records the unchanged planner outcome; the fixture does not claim a build passed.");
                AssertFalse(File.Exists(h.Marker), "Planner preparation runs no verification command.");
                // The real Architect handoff publishes Preparing -> Accepted. No Accepted barrier,
                // member list or graph-completion marker is supplied by the fixture.
                AssertEqual(0, await h.Service.RecoverDanglingHandoffsAsync().ConfigureAwait(false));
                List<Mission> rows = await db.Driver.Missions.EnumerateByVoyageAsync(architect.VoyageId!).ConfigureAwait(false);
                List<Mission> integrations = rows.Where(item => item.IntegrationBarrier != null).ToList();
                AssertEqual(1, integrations.Count,
                    "Actual planner handoff must create one integration barrier; plannerStatus=" + completedPlanner.Status
                    + "; failure=" + (completedPlanner.FailureReason ?? "none")
                    + "; hold=" + (completedPlanner.HeldForOperatorReviewReason ?? "none")
                    + "; rows=" + String.Join(",", rows.Select(item => item.Persona + ":" + item.Title + ":" + item.Status)));
                Mission integration = integrations.Single();
                MissionIntegrationBarrier barrier = integration.IntegrationBarrier!;
                AssertEqual(MissionIntegrationBarrierPhaseEnum.Accepted, barrier.Phase);
                AssertEqual(1, barrier.ExpectedBranchCount);
                AssertEqual(1, barrier.Members.Count);
                AssertEqual(architect.Id, barrier.ArchitectMissionId);
                AssertEqual(integration.Id, barrier.IntegrationMissionId);
                Mission member = await h.ReadAsync(barrier.Members[0].CurrentTerminalMissionId).ConfigureAwait(false);
                AssertEqual(template.Id, member.Id);
                AssertEqual(member.Id, barrier.Members[0].RootMissionId);
                AssertEqual(member.Id, barrier.Members[0].AcceptedTerminalMissionId);
                AssertEqual(member.Id, integration.DependsOnMissionId);
                AssertEqual(0, h.Launches);

                // A reviewed branch is fixture input. Its approval and Complete transition belong
                // to the public review owner, rather than a raw Complete write in this test.
                member.Status = MissionStatusEnum.Review;
                member.RequiresReview = true;
                member.StartedUtc = DateTime.UtcNow.AddMinutes(-5);
                member.CommitHash = new string('a', 40);
                member.StartFromRef = new string('d', 40);
                await db.Driver.Missions.UpdateAsync(member).ConfigureAwait(false);
                member = await h.ReadAsync(member.Id).ConfigureAwait(false);
                await h.EnableAsync(member).ConfigureAwait(false);
                await h.GrantAsync(member, MissionAutomaticActionEnum.Verification).ConfigureAwait(false);
                string target = new string('c', 40);
                h.Git.RevisionCommitShaResult = target;
                h.Git.HeadCommitHashResult = target;
                h.Git.AncestorResults[member.CommitHash + "|" + target] = true;
                h.Git.AncestorResults[member.StartFromRef + "|" + target] = true;

                // Approval also drives the real pending-dispatch owner. All landing/readiness
                // evidence is supplied through the existing git double; permission is still absent.
                Mission approved = await h.Service.ApproveReviewAsync(member.Id, Constants.DefaultUserId,
                    "Accept this branch before integration").ConfigureAwait(false);
                AssertEqual(MissionStatusEnum.Complete, approved.Status);
                AssertNotNull(approved.ReviewedUtc);
                AssertFalse(await h.Service.TryAssignAsync(await h.ReadAsync(integration.Id).ConfigureAwait(false), h.Vessel).ConfigureAwait(false));
                Mission held = await h.ReadAsync(integration.Id).ConfigureAwait(false);
                AssertEqual(MissionStatusEnum.Pending, held.Status);
                AssertEqual(MissionAssignmentStateEnum.WaitingForDependency, held.AssignmentState);
                AssertNull(held.DockId);
                AssertNull(held.ProcessId);
                AssertEqual(0, h.Git.WorktreeCalls.Count);
                AssertEqual(0, h.Launches);
                AssertTrue((await db.Driver.Events.EnumerateByMissionAsync(member.Id, 100).ConfigureAwait(false))
                    .Any(item => item.EventType == "mission.automatic_work_held" && item.Payload != null
                        && item.Payload.Contains("dependent_admission_permission_required", StringComparison.Ordinal)),
                    "The barrier member permission must be the actual refusal after its review is approved.");
                AssertTrue(h.Git.IsAncestorCalls.Contains(h.Vessel.LocalPath + ":" + member.CommitHash + ":" + target));
                AssertTrue(h.Git.IsAncestorCalls.Contains(h.Vessel.LocalPath + ":" + member.StartFromRef + ":" + target));

                // Directly grant the already Complete member. Identity, accepted membership and
                // readiness proof stay unchanged, so this is the positive control for that hold.
                await h.GrantAsync(approved, MissionAutomaticActionEnum.DependentAdmission).ConfigureAwait(false);
                Mission granted = await h.ReadAsync(member.Id).ConfigureAwait(false);
                AssertEqual(MissionStatusEnum.Complete, granted.Status);
                AssertEqual(approved.StartedUtc, granted.StartedUtc);
                AssertEqual(approved.CommitHash, granted.CommitHash);
                AssertTrue(await h.Service.TryAssignAsync(await h.ReadAsync(integration.Id).ConfigureAwait(false), h.Vessel).ConfigureAwait(false));
                Mission assigned = await h.ReadAsync(integration.Id).ConfigureAwait(false);
                AssertEqual(MissionStatusEnum.InProgress, assigned.Status);
                AssertEqual(target, assigned.StartFromRef);
                AssertEqual(JsonSerializer.Serialize(barrier), JsonSerializer.Serialize(assigned.IntegrationBarrier));
                AssertEqual(1, h.Git.WorktreeCalls.Count);
                AssertEqual(1, h.Launches);
            }
        }

        private static string BarrierPlan()
        {
            VersionedArchitectPlan plan = new VersionedArchitectPlan
            {
                ContractVersion = 2, Requirements = new List<string> { "REQ-1" },
                Decisions = new List<string> { "Keep routing defaults" }
            };
            for (int index = 0; index < 2; index++)
            {
                bool integration = index == 1;
                plan.Missions.Add(new VersionedArchitectPlanMission
                {
                    Title = integration ? "Permission integration" : "Permission branch",
                    Kind = integration ? "integration" : "implementation",
                    Description = integration ? "Combine the approved branch." : "Implement the requested branch.",
                    DependsOn = integration ? 1 : null, WaitForOtherMissions = integration,
                    ValidationCommands = new List<string> { "echo verify" },
                    AcceptanceCriteria = new List<VersionedArchitectAcceptanceCriterion>
                    {
                        new VersionedArchitectAcceptanceCriterion
                        {
                            Id = integration ? "AC-2" : "AC-1", RequirementIds = new List<string> { "REQ-1" },
                            Scenario = "Run the branch check.", Expected = "The check passes.",
                            Verification = "The named check passes.", NegativeCases = new List<string> { "A missing grant prevents integration." }
                        }
                    }
                });
            }
            return "```armada-plan\n" + JsonSerializer.Serialize(plan) + "\n```";
        }

        private async Task AssertUntouchedChildAsync(Harness h, Mission child)
        {
            Mission stored = await h.ReadAsync(child.Id).ConfigureAwait(false);
            AssertEqual(MissionStatusEnum.Pending, stored.Status);
            AssertEqual(child.BranchName, stored.BranchName);
            AssertEqual(child.Description, stored.Description);
            AssertNull(stored.DockId);
            AssertNull(stored.ProcessId);
            AssertEqual(0, h.Git.WorktreeCalls.Count);
            AssertEqual(0, h.Launches);
        }

        private async Task AssertPassedGateAsync(Harness h, Mission mission)
        {
            ArmadaEvent evaluation = (await h.Database.Driver.Events.EnumerateByMissionAsync(mission.Id, 100).ConfigureAwait(false))
                .Single(item => item.EventType == DefinitionOfDoneEvaluationRecord.EventType);
            DefinitionOfDoneEvaluationRecord record = JsonSerializer.Deserialize<DefinitionOfDoneEvaluationRecord>(evaluation.Payload!)
                ?? throw new InvalidOperationException("The actual gate must retain its evaluation.");
            AssertEqual(DefinitionOfDoneEvaluationOutcomeEnum.Passed, record.Outcome);
            AssertEqual(mission.CommitHash, record.CommitHash);
            AssertEqual(mission.BranchName, record.BranchName);
        }

        private sealed class Harness : IDisposable
        {
            /// <summary>Database owned by the outer test.</summary>
            public TestDatabase Database { get; private set; } = null!;
            /// <summary>Settings limited to this fixture directory.</summary>
            public ArmadaSettings Settings { get; private set; } = null!;
            /// <summary>Existing git double with observable provisioning calls.</summary>
            public StubGitService Git { get; private set; } = null!;
            /// <summary>Vessel owned by this fixture.</summary>
            public Vessel Vessel { get; private set; } = null!;
            /// <summary>Captain whose launch is replaced by the existing callback.</summary>
            public Captain Captain { get; private set; } = null!;
            /// <summary>Initial service under test.</summary>
            public MissionService Service { get; private set; } = null!;
            /// <summary>Number of calls to the captain launch owner.</summary>
            public int Launches { get; private set; }
            /// <summary>File written only by actual gate shell commands.</summary>
            public string Marker => Path.Combine(_Root, "gate-markers.txt");

            private string _Root = "";
            private Voyage _Voyage = null!;
            private LoggingModule _Logging = null!;
            private ICaptainService _Captains = null!;
            private IDockService _Docks = null!;

            /// <summary>Create actual service owners and an isolated marker-command profile.</summary>
            public static async Task<Harness> CreateAsync(TestDatabase db)
            {
                Harness h = new Harness();
                h.Database = db;
                h._Root = Path.Combine(Path.GetTempPath(), "armada_permission_boundary_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(h._Root);
                string repository = Path.Combine(h._Root, "repository.git");
                string workingDirectory = Path.Combine(h._Root, "working");
                Directory.CreateDirectory(repository);
                Directory.CreateDirectory(workingDirectory);
                h._Logging = new LoggingModule();
                h._Logging.Settings.EnableConsole = false;
                h.Settings = new ArmadaSettings
                {
                    DataDirectory = h._Root, LogDirectory = Path.Combine(h._Root, "logs"),
                    DocksDirectory = Path.Combine(h._Root, "docks"), ReposDirectory = Path.Combine(h._Root, "repos")
                };
                h.Git = new StubGitService
                {
                    CreateWorktreeDirectories = true, RevisionCommitShaResult = new string('a', 40),
                    HeadCommitHashResult = new string('a', 40), ChangedFilesSinceResult = new[] { "src/Behavior.cs" }
                };
                h.Git.ExistingBranches.Add("permission-parent");
                h._Docks = new DockService(h._Logging, db.Driver, h.Settings, h.Git);
                h._Captains = new CaptainService(h._Logging, db.Driver, h.Settings, h.Git, h._Docks);
                h._Captains.OnLaunchAgent = (_, _, _) => { h.Launches++; return Task.FromResult(4747477); };
                h.Vessel = await db.Driver.Vessels.CreateAsync(new Vessel("permission-boundary", "https://github.com/test/permission.git")
                {
                    TenantId = Constants.DefaultTenantId, UserId = Constants.DefaultUserId,
                    LocalPath = repository, WorkingDirectory = workingDirectory, DefaultBranch = "main"
                }).ConfigureAwait(false);
                h.Captain = await db.Driver.Captains.CreateAsync(new Captain("permission-boundary-captain")
                {
                    TenantId = Constants.DefaultTenantId, UserId = Constants.DefaultUserId, State = CaptainStateEnum.Idle
                }).ConfigureAwait(false);
                h._Voyage = await db.Driver.Voyages.CreateAsync(new Voyage("Permission boundaries", "Owned test fixtures")
                {
                    TenantId = Constants.DefaultTenantId, UserId = Constants.DefaultUserId
                }).ConfigureAwait(false);
                await db.Driver.WorkflowProfiles.CreateAsync(new WorkflowProfile
                {
                    TenantId = Constants.DefaultTenantId, Name = "Permission marker commands", Scope = WorkflowProfileScopeEnum.Vessel,
                    VesselId = h.Vessel.Id, IsDefault = true, Active = true, GateCommandTimeoutSeconds = 5,
                    BuildCommand = "echo build >> \"" + h.Marker + "\"", UnitTestCommand = "echo test >> \"" + h.Marker + "\""
                }).ConfigureAwait(false);
                h.Service = h.NewService();
                return h;
            }

            /// <summary>Construct the actual exit owner with no explicit success declaration.</summary>
            public AdmiralService NewAdmiral()
            {
                Service.OnGetMissionOutput = _ => "The runtime ended before a result was declared.";
                return new AdmiralService(_Logging, Database.Driver, Settings, _Captains, Service,
                    new VoyageService(_Logging, Database.Driver), _Docks, git: Git);
            }

            /// <summary>Construct another owner over the same durable rows.</summary>
            public MissionService NewService()
            {
                MissionService service = new MissionService(_Logging, Database.Driver, Settings, _Docks, _Captains,
                    git: Git, resourcePressureAdmission: TestResourcePressure.Unconstrained(Settings));
                service.DefinitionOfDone = new DefinitionOfDoneGate(new DefinitionOfDoneSettings
                {
                    Enabled = true, RunRestoreBeforeBuild = false
                }, Database.Driver, _Logging, gitService: Git);
                service.OnGetMissionOutput = _ => "[ARMADA:RESULT] COMPLETE\nImplemented the requested behavior.\n" + new string('x', 1200);
                return service;
            }

            /// <summary>Persist a producer with captured work and source identity.</summary>
            public async Task<Mission> CreateParentAsync(MissionStatusEnum status, bool review = false)
            {
                Mission created = await Database.Driver.Missions.CreateAsync(new Mission("Permission producer", "Implement the requested behavior")
                {
                    TenantId = Constants.DefaultTenantId, UserId = Constants.DefaultUserId, VesselId = Vessel.Id,
                    VoyageId = _Voyage.Id, StageOrder = 1, Persona = "Worker", Status = status,
                    StartedUtc = DateTime.UtcNow.AddMinutes(-5), CommitHash = new string('a', 40),
                    BranchName = "permission-parent", RequiresReview = review, RecoveryAttempts = 2, LandingRetryCount = 1,
                    DiffSnapshot = "diff --git a/src/Behavior.cs b/src/Behavior.cs\n--- a/src/Behavior.cs\n+++ b/src/Behavior.cs\n@@ -1 +1 @@\n-old\n+new"
                }).ConfigureAwait(false);
                return await ReadAsync(created.Id).ConfigureAwait(false);
            }

            /// <summary>Persist a dependent with or without an already prepared branch.</summary>
            public async Task<Mission> CreateChildAsync(Mission parent, bool prepared)
            {
                Mission created = await Database.Driver.Missions.CreateAsync(new Mission("Permission consumer", "Check the exact predecessor result")
                {
                    TenantId = Constants.DefaultTenantId, UserId = Constants.DefaultUserId, VesselId = Vessel.Id,
                    VoyageId = _Voyage.Id, DependsOnMissionId = parent.Id, StageOrder = 2, Persona = "TestEngineer",
                    Status = MissionStatusEnum.Pending, BranchName = prepared ? parent.BranchName : null
                }).ConfigureAwait(false);
                return await ReadAsync(created.Id).ConfigureAwait(false);
            }

            /// <summary>Represent the exiting captain and its owned dock without starting a process.</summary>
            public async Task<Captain> ClaimAsync(Mission parent)
            {
                string worktree = Path.Combine(_Root, "produced-worktree");
                Directory.CreateDirectory(worktree);
                Dock dock = await Database.Driver.Docks.CreateAsync(new Dock(Vessel.Id)
                {
                    TenantId = Constants.DefaultTenantId, UserId = Constants.DefaultUserId,
                    CaptainId = Captain.Id, BranchName = parent.BranchName, WorktreePath = worktree
                }).ConfigureAwait(false);
                parent.CaptainId = Captain.Id;
                parent.DockId = dock.Id;
                parent.ProcessId = 4747477;
                await Database.Driver.Missions.UpdateAsync(parent).ConfigureAwait(false);
                Captain captain = (await Database.Driver.Captains.ReadAsync(Captain.Id).ConfigureAwait(false))!;
                captain.State = CaptainStateEnum.Working;
                captain.CurrentMissionId = parent.Id;
                captain.CurrentDockId = dock.Id;
                captain.ProcessId = 4747477;
                return await Database.Driver.Captains.UpdateAsync(captain).ConfigureAwait(false);
            }

            /// <summary>Opt in through the authenticated public operator owner.</summary>
            public async Task EnableAsync(Mission mission)
            {
                await Service.UpdateAutomaticWorkPermissionAsync(Operator(), mission.Id, new MissionAutomaticWorkPermissionRequest
                {
                    Operation = MissionAutomaticPermissionOperationEnum.Enable, Reason = "Hold owned automatic work"
                }).ConfigureAwait(false);
            }

            /// <summary>Grant one current action through the public operator owner.</summary>
            public async Task GrantAsync(Mission mission, MissionAutomaticActionEnum action, MissionService? service = null)
            {
                await (service ?? Service).UpdateAutomaticWorkPermissionAsync(Operator(), mission.Id, new MissionAutomaticWorkPermissionRequest
                {
                    Operation = MissionAutomaticPermissionOperationEnum.Grant, Action = action,
                    ExpectedStartedUtc = mission.StartedUtc, ExpectedCommitHash = mission.CommitHash,
                    ExpiresUtc = DateTime.UtcNow.AddMinutes(10), Reason = "Admit only this current action"
                }).ConfigureAwait(false);
            }

            /// <summary>Read the current persisted mission including its permission revision.</summary>
            public async Task<Mission> ReadAsync(string id) => await Database.Driver.Missions.ReadAsync(id).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The boundary fixture requires a persisted mission.");

            private static AuthContext Operator() => AuthContext.Authenticated(Constants.DefaultTenantId, Constants.DefaultUserId, true, true, "Test");

            /// <summary>Delete only this fixture directory after its owned tasks settle.</summary>
            public void Dispose()
            {
                if (Directory.Exists(_Root)) Directory.Delete(_Root, true);
            }
        }
    }
}
