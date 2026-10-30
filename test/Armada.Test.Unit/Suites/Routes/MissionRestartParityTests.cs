namespace Armada.Test.Unit.Suites.Routes
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.IO;
    using System.Diagnostics;
    using System.Text.Json;
    using SyslogLogging;
    using Armada.Test.Unit.Suites.Services;
    using TestResourcePressure = global::Test.Shared.Infrastructure.TestResourcePressure;
    using System.Net.Http;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// Restarting a mission is one operation on REST, WebSocket and MCP. A LandingFailed mission keeps its produced
    /// work: every surface refuses to restart it and names retry-landing. A Failed mission returns to Pending, and every
    /// surface writes the same event, broadcasts the same change, and records a restart signal its owner can read. A
    /// mission whose voyage has ended (Failed, Complete or Cancelled) is refused on every surface, because a mission of
    /// an ended voyage is never assigned, except the failed stage of a Failed voyage or a stage cancelled behind it: it
    /// reopens the voyage, runs on the last stage that produced verified work, and the later cancelled stages run again.
    /// </summary>
    public partial class MissionRestartParityTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Mission Restart Parity";

        private static readonly string[] Surfaces = new[] { "REST", "WebSocket", "MCP" };

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunGeneratedCandidateAssignmentTestAsync().ConfigureAwait(false);
            await RunResearchReplayTestsAsync().ConfigureAwait(false);
            await RunTest("ResearchReplay_PreviewsFullChainWithoutChangingCompletedDutiesOrFailedVerdict", async () =>
            {
                using (SurfaceParityHarness harness = await SurfaceParityHarness.StartAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await harness.Driver.Vessels.CreateAsync(new Vessel("research-replay-preview", "https://github.com/test/research.git")
                    {
                        TenantId = Constants.DefaultTenantId, UserId = Constants.DefaultUserId
                    }).ConfigureAwait(false);
                    Voyage voyage = await harness.Driver.Voyages.CreateAsync(new Voyage("Research review replay", "Unchanged acceptance criteria")
                    {
                        TenantId = Constants.DefaultTenantId, UserId = Constants.DefaultUserId,
                        Status = VoyageStatusEnum.Failed, CompletedUtc = DateTime.UtcNow
                    }).ConfigureAwait(false);
                    string[] roles = { PersonaCatalog.Worker, "PortingReferenceAnalyst", PersonaCatalog.TestEngineer,
                        PersonaCatalog.Linter, PersonaCatalog.Worker, PersonaCatalog.Judge };
                    List<Mission> stages = new List<Mission>();
                    foreach (string role in roles)
                    {
                        bool judge = stages.Count == roles.Length - 1;
                        Mission stage = await harness.Driver.Missions.CreateAsync(new Mission(role + " independent duty")
                        {
                            TenantId = Constants.DefaultTenantId, UserId = Constants.DefaultUserId,
                            VoyageId = voyage.Id, VesselId = vessel.Id, Persona = role, StageOrder = stages.Count + 1,
                            DependsOnMissionId = stages.LastOrDefault()?.Id, Mode = MissionModeEnum.Research,
                            Status = judge ? MissionStatusEnum.Failed : MissionStatusEnum.Complete,
                            StartFromRef = _ProducedCommit, CommitHash = _ProducedCommit,
                            Description = "Read-only independent study; unchanged scope and criteria.",
                            AgentOutput = judge ? "NEEDS_REVISION: prior independent duties remain incomplete." : "Prior report for " + role,
                            FailureReason = judge ? "Judge verdict: NEEDS_REVISION" : null,
                            StartedUtc = DateTime.UtcNow.AddMinutes(-10), CompletedUtc = DateTime.UtcNow.AddMinutes(-1)
                        }).ConfigureAwait(false);
                        stages.Add(stage);
                    }
                    SurfaceReply reply = await harness.RestAsync(HttpMethod.Post, "/api/v1/voyages/" + voyage.Id + "/research-replay",
                        new { dryRun = true, sourceCommit = _ProducedCommit }).ConfigureAwait(false);
                    AssertEqual(200, reply.Status, "An operator can preview a full-chain continuation: " + reply);
                    using (JsonDocument body = JsonDocument.Parse(reply.Body))
                    {
                        AssertEqual(6, body.RootElement.EnumerateObject().Single(property => property.Name.Equals("MissionIds", StringComparison.OrdinalIgnoreCase)).Value.GetArrayLength(), "Every original duty is included");
                        AssertEqual(_ProducedCommit, body.RootElement.EnumerateObject().Single(property => property.Name.Equals("SourceCommit", StringComparison.OrdinalIgnoreCase)).Value.GetString(), "Preview pins the source");
                    }
                    foreach (Mission original in stages)
                    {
                        Mission? current = await harness.Driver.Missions.ReadAsync(original.Id).ConfigureAwait(false);
                        AssertEqual(original.Status, current!.Status, "Preview does not replace prior status");
                        AssertEqual(original.AgentOutput, current.AgentOutput, "Preview retains the full prior report");
                        AssertEqual(original.FailureReason, current.FailureReason, "Preview retains the failed verdict");
                    }
                    AssertEqual(VoyageStatusEnum.Failed, (await harness.Driver.Voyages.ReadAsync(voyage.Id).ConfigureAwait(false))!.Status,
                        "Preview does not reopen the voyage");
                }
            }).ConfigureAwait(false);

            await RunTest("RestartMission_LandingFailedMission_IsRefusedAndNamesRetryLandingOnEverySurface", async () =>
            {
                using (SurfaceParityHarness harness = await SurfaceParityHarness.StartAsync().ConfigureAwait(false))
                {
                    foreach (string surface in Surfaces)
                    {
                        Mission mission = await SeedAsync(harness, surface, MissionStatusEnum.LandingFailed).ConfigureAwait(false);
                        harness.ResetRecordings();

                        SurfaceReply reply = await RestartAsync(harness, surface, mission.Id).ConfigureAwait(false);

                        AssertTrue(reply.Refused, "a LandingFailed mission is not restarted: " + reply);
                        AssertContains("retry-landing", reply.Body, surface + ": the refusal names retry-landing: " + reply);
                        Mission? stored = await harness.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                        AssertEqual(MissionStatusEnum.LandingFailed, stored!.Status, surface + ": the mission keeps its status");
                        AssertEqual("armada/restart-" + surface, stored.BranchName, surface + ": the produced branch is kept");
                        AssertEqual(0, harness.Events.Count, surface + ": a refused restart writes no event");
                    }
                }
            }).ConfigureAwait(false);

            await RunTest("RestartMission_FailedMission_ReturnsToPendingWithOneEventBroadcastAndOwnedSignalOnEverySurface", async () =>
            {
                using (SurfaceParityHarness harness = await SurfaceParityHarness.StartAsync().ConfigureAwait(false))
                {
                    foreach (string surface in Surfaces)
                    {
                        Mission mission = await SeedAsync(harness, surface, MissionStatusEnum.Failed).ConfigureAwait(false);
                        harness.ResetRecordings();

                        SurfaceReply reply = await RestartAsync(harness, surface, mission.Id).ConfigureAwait(false);

                        AssertFalse(reply.Refused, "a Failed mission is restarted: " + reply);
                        Mission? stored = await harness.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                        AssertEqual(MissionStatusEnum.Pending, stored!.Status, surface + ": the mission is Pending");
                        AssertEqual(1, harness.EventTypes().Count(type => type == "mission.restarted:" + mission.Id),
                            surface + ": one mission.restarted event: " + String.Join(",", harness.EventTypes()));
                        AssertTrue(harness.Broadcasts.Contains("mission:" + mission.Id + ":Pending"),
                            surface + ": the change is broadcast: " + String.Join(",", harness.Broadcasts));
                        List<Signal> signals = await harness.Driver.Signals.EnumerateRecentAsync(200).ConfigureAwait(false);
                        Signal? restartSignal = signals.FirstOrDefault(s => s.Payload == "Mission " + mission.Id + " restarted");
                        AssertNotNull(restartSignal, surface + ": a restart signal is recorded");
                        AssertEqual(mission.TenantId, restartSignal!.TenantId, surface + ": the signal belongs to the mission's tenant");
                        AssertEqual(mission.UserId, restartSignal.UserId, surface + ": the signal belongs to the mission's owner");
                    }
                }
            }).ConfigureAwait(false);

            await RunTest("RestartMission_MissionOfTerminalVoyage_IsRefusedAndNamesTheVoyageStatusOnEverySurface", async () =>
            {
                using (SurfaceParityHarness harness = await SurfaceParityHarness.StartAsync().ConfigureAwait(false))
                {
                    VoyageStatusEnum[] terminal = new[] { VoyageStatusEnum.Failed, VoyageStatusEnum.Complete, VoyageStatusEnum.Cancelled };
                    foreach (string surface in Surfaces)
                    {
                        foreach (VoyageStatusEnum voyageStatus in terminal)
                        {
                            Mission mission = await SeedAsync(harness, surface + "-" + voyageStatus, MissionStatusEnum.Cancelled).ConfigureAwait(false);
                            Voyage voyage = await AttachVoyageAsync(harness, mission, voyageStatus).ConfigureAwait(false);
                            harness.ResetRecordings();

                            SurfaceReply reply = await RestartAsync(harness, surface, mission.Id).ConfigureAwait(false);

                            string label = surface + " (" + voyageStatus + " voyage)";
                            AssertTrue(reply.Refused, label + ": a mission of an ended voyage is not restarted: " + reply);
                            AssertContains(voyage.Id, reply.Body, label + ": the refusal names the voyage: " + reply);
                            AssertContains(voyageStatus.ToString(), reply.Body, label + ": the refusal names the voyage status: " + reply);
                            AssertContains("new voyage", reply.Body, label + ": the refusal says to dispatch a new voyage: " + reply);
                            Mission? stored = await harness.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                            AssertEqual(MissionStatusEnum.Cancelled, stored!.Status, label + ": the mission keeps its status");
                            Voyage? storedVoyage = await harness.Driver.Voyages.ReadAsync(voyage.Id).ConfigureAwait(false);
                            AssertEqual(voyageStatus, storedVoyage!.Status, label + ": the voyage keeps its status");
                            AssertEqual(0, harness.Events.Count, label + ": a refused restart writes no event");
                        }
                    }
                }
            }).ConfigureAwait(false);

            await RunTest("RestartMission_JudgeThatRejected_GetsBoundedHistoricalBlockWithRecheckInstructionOnEverySurface", async () =>
            {
                using (SurfaceParityHarness harness = await SurfaceParityHarness.StartAsync().ConfigureAwait(false))
                {
                    foreach (string surface in Surfaces)
                    {
                        Mission judge = await SeedJudgeAsync(harness, surface, RejectedReview("alpha-finding"), "Review the change on the branch.").ConfigureAwait(false);

                        SurfaceReply reply = await RestartAsync(harness, surface, judge.Id).ConfigureAwait(false);

                        AssertFalse(reply.Refused, surface + ": a Failed Judge is restarted: " + reply);
                        Mission stored = (await harness.Driver.Missions.ReadAsync(judge.Id).ConfigureAwait(false))!;
                        string description = stored.Description ?? String.Empty;
                        AssertStartsWith("Review the change on the branch.", description, surface + ": the original instructions stay first");
                        AssertEqual(1, Count(description, PriorJudgeFindingsBlock.BeginMarker), surface + ": one historical block");
                        AssertContains("alpha-finding", description, surface + ": the prior finding is carried");
                        AssertContains("historical", description, surface + ": the block is labelled historical");
                        AssertContains("Re-check every finding", description, surface + ": the block tells the Judge to re-check each finding");
                        AssertContains("not the current verdict", description, surface + ": the block is not a verdict");
                        AssertContains("armada_mission_output", description, surface + ": the block names the full-output read path");
                        AssertContains(judge.Id, description, surface + ": the block names the mission whose output to read");
                        AssertTrue(description.Length <= "Review the change on the branch.".Length + PriorJudgeFindingsBlock.MaxBlockChars + 4,
                            surface + ": the block stays within its bound");

                        AssertNull(stored.AgentOutput, surface + ": the new run starts without the old output");
                        AssertContains("alpha-finding", stored.PreservedRunOutput ?? String.Empty, surface + ": the complete earlier output is kept");
                        MissionOutputArtifactPage page = MissionOutputArtifact.Build(stored, 0, MissionOutputArtifact.MaximumPageLength);
                        AssertContains("alpha-finding", page.Content, surface + ": the mission-output read path returns the earlier run");
                    }
                }
            }).ConfigureAwait(false);

            await RunTest("RestartMission_JudgeWithHugePriorOutput_KeepsHeadAndFindingsWithinTheCap", async () =>
            {
                using (SurfaceParityHarness harness = await SurfaceParityHarness.StartAsync().ConfigureAwait(false))
                {
                    string filler = String.Concat(Enumerable.Repeat("Walkthrough line that carries no defect.\n", 6000));
                    string output = "[ARMADA:VERDICT] NEEDS_REVISION\nHEAD-SUMMARY-MARKER the review summary.\n\n" + filler
                        + "## Correctness\n- Blocking: TAIL-FINDING-MARKER the retry path drops the final item.\n";
                    AssertTrue(output.Length > 200000, "the prior output is far larger than the cap");
                    Mission judge = await SeedJudgeAsync(harness, "huge", output, "Review it.").ConfigureAwait(false);

                    SurfaceReply reply = await RestartAsync(harness, "REST", judge.Id).ConfigureAwait(false);

                    AssertFalse(reply.Refused, "the Judge is restarted: " + reply);
                    Mission stored = (await harness.Driver.Missions.ReadAsync(judge.Id).ConfigureAwait(false))!;
                    string description = stored.Description ?? String.Empty;
                    AssertTrue(description.Length <= "Review it.".Length + PriorJudgeFindingsBlock.MaxBlockChars + 4,
                        "the description holds at most the capped block: " + description.Length);
                    AssertContains("HEAD-SUMMARY-MARKER", description, "the head of the review is carried");
                    AssertContains("TAIL-FINDING-MARKER", description, "the findings section is carried");
                    AssertContains("elided", description, "the cut is named");
                    AssertContains("TAIL-FINDING-MARKER", stored.PreservedRunOutput ?? String.Empty, "the full output stays readable");
                }
            }).ConfigureAwait(false);

            await RunTest("RestartMission_JudgeWithoutRejectionAndOtherPersonas_KeepTheirDescription", async () =>
            {
                using (SurfaceParityHarness harness = await SurfaceParityHarness.StartAsync().ConfigureAwait(false))
                {
                    Mission neverRan = await SeedJudgeAsync(harness, "never-ran", null, "First run brief.").ConfigureAwait(false);
                    Mission passed = await SeedJudgeAsync(harness, "passed", "[ARMADA:VERDICT] PASS\nNothing blocks the change.", "Pass brief.").ConfigureAwait(false);
                    Mission worker = await SeedJudgeAsync(harness, "worker", RejectedReview("worker-finding"), "Worker brief.", "Worker").ConfigureAwait(false);

                    foreach (Mission mission in new[] { neverRan, passed, worker })
                    {
                        SurfaceReply reply = await RestartAsync(harness, "REST", mission.Id).ConfigureAwait(false);
                        AssertFalse(reply.Refused, mission.Title + ": restarted: " + reply);
                        Mission stored = (await harness.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false))!;
                        AssertEqual(mission.Description, stored.Description, mission.Title + ": the description is unchanged");
                        AssertEqual(mission.AgentOutput, stored.AgentOutput, mission.Title + ": the output is unchanged");
                        AssertNull(stored.PreservedRunOutput, mission.Title + ": no run is preserved");
                    }
                }
            }).ConfigureAwait(false);

            await RunTest("RestartMission_JudgeRestartedRepeatedly_HoldsOneBlockWithTheNewestExcerptAndKeepsAReplacementDescription", async () =>
            {
                using (SurfaceParityHarness harness = await SurfaceParityHarness.StartAsync().ConfigureAwait(false))
                {
                    Mission judge = await SeedJudgeAsync(harness, "repeat", RejectedReview("first-run-finding"), "Original brief.").ConfigureAwait(false);

                    await RestartAsync(harness, "REST", judge.Id).ConfigureAwait(false);
                    Mission afterFirst = (await harness.Driver.Missions.ReadAsync(judge.Id).ConfigureAwait(false))!;
                    AssertEqual(1, Count(afterFirst.Description ?? String.Empty, PriorJudgeFindingsBlock.BeginMarker), "one block after the first restart");

                    // Restarting a Judge that left no output keeps the block as it is.
                    afterFirst.Status = MissionStatusEnum.Failed;
                    await harness.Driver.Missions.UpdateAsync(afterFirst).ConfigureAwait(false);
                    await RestartAsync(harness, "REST", judge.Id).ConfigureAwait(false);
                    Mission afterEmpty = (await harness.Driver.Missions.ReadAsync(judge.Id).ConfigureAwait(false))!;
                    AssertEqual(afterFirst.Description, afterEmpty.Description, "a restart with no new output leaves the description alone");

                    // A second rejection replaces the block, and an operator's replacement description is kept.
                    afterEmpty.Status = MissionStatusEnum.Failed;
                    afterEmpty.AgentOutput = RejectedReview("second-run-finding");
                    await harness.Driver.Missions.UpdateAsync(afterEmpty).ConfigureAwait(false);
                    SurfaceReply reply = await harness.RestAsync(HttpMethod.Post, "/api/v1/missions/" + judge.Id + "/restart", new { description = "Replacement brief." }).ConfigureAwait(false);
                    AssertFalse(reply.Refused, "the Judge is restarted with a replacement description: " + reply);

                    Mission afterSecond = (await harness.Driver.Missions.ReadAsync(judge.Id).ConfigureAwait(false))!;
                    string description = afterSecond.Description ?? String.Empty;
                    AssertStartsWith("Replacement brief.", description, "the operator's description leads");
                    AssertEqual(1, Count(description, PriorJudgeFindingsBlock.BeginMarker), "one block after the second restart");
                    AssertContains("second-run-finding", description, "the newest excerpt is carried");
                    AssertFalse(description.Contains("first-run-finding", StringComparison.Ordinal), "the superseded excerpt is gone");
                    AssertContains("first-run-finding", afterSecond.PreservedRunOutput ?? String.Empty, "the first run stays readable");
                    AssertContains("second-run-finding", afterSecond.PreservedRunOutput ?? String.Empty, "the second run stays readable");
                }
            }).ConfigureAwait(false);

            await RunTest("RestartMission_MissionOfLiveVoyage_ReturnsToPendingOnEverySurface", async () =>
            {
                using (SurfaceParityHarness harness = await SurfaceParityHarness.StartAsync().ConfigureAwait(false))
                {
                    foreach (string surface in Surfaces)
                    {
                        Mission mission = await SeedAsync(harness, surface + "-live", MissionStatusEnum.Failed).ConfigureAwait(false);
                        await AttachVoyageAsync(harness, mission, VoyageStatusEnum.InProgress).ConfigureAwait(false);
                        harness.ResetRecordings();

                        SurfaceReply reply = await RestartAsync(harness, surface, mission.Id).ConfigureAwait(false);

                        AssertFalse(reply.Refused, surface + ": a mission of a live voyage is restarted: " + reply);
                        Mission? stored = await harness.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                        AssertEqual(MissionStatusEnum.Pending, stored!.Status, surface + ": the mission is Pending");
                    }
                }
            }).ConfigureAwait(false);

            await RunTest("RestartMission_StageCancelledByUpstreamFailure_ReopensTheFailedVoyageOnTheLastVerifiedCommitOnEverySurface", async () =>
            {
                using (SurfaceParityHarness harness = await SurfaceParityHarness.StartAsync().ConfigureAwait(false))
                {
                    foreach (string surface in Surfaces)
                    {
                        UpstreamFailureChain chain = await SeedUpstreamFailureChainAsync(harness, surface, _ProducedCommit).ConfigureAwait(false);
                        harness.ResetRecordings();

                        SurfaceReply reply = await RestartAsync(harness, surface, chain.Judge.Id).ConfigureAwait(false);

                        AssertFalse(reply.Refused, surface + ": a stage cancelled by an upstream failure is restarted: " + reply);
                        Mission? judge = await harness.Driver.Missions.ReadAsync(chain.Judge.Id).ConfigureAwait(false);
                        AssertEqual(MissionStatusEnum.Pending, judge!.Status, surface + ": the Judge is Pending");
                        AssertEqual(chain.Producer.Id, judge.DependsOnMissionId, surface + ": the Judge runs on the stage that produced verified work");
                        Voyage? voyage = await harness.Driver.Voyages.ReadAsync(chain.Voyage.Id).ConfigureAwait(false);
                        AssertEqual(VoyageStatusEnum.InProgress, voyage!.Status, surface + ": the voyage is reopened");
                        AssertNull(voyage.CompletedUtc, surface + ": the reopened voyage has no completion time");
                        Mission? revision = await harness.Driver.Missions.ReadAsync(chain.FailedStage.Id).ConfigureAwait(false);
                        AssertEqual(MissionStatusEnum.Cancelled, revision!.Status, surface + ": the failed stage is superseded");
                        AssertContains("Superseded by an operator restart", revision.FailureReason ?? String.Empty, surface + ": the supersession is named");
                        AssertContains("no_op_completion_detected", revision.FailureReason ?? String.Empty, surface + ": the earlier failure is kept");
                        AssertEqual(1, harness.EventTypes().Count(type => type == "voyage.reopened:" + chain.Voyage.Id),
                            surface + ": one voyage.reopened event: " + String.Join(",", harness.EventTypes()));
                        await VerifyRestartAssignmentFromPreservedCommitAsync(harness, surface).ConfigureAwait(false);
                        await VerifyRestartAssignmentFromPreservedCommitAsync(harness, surface, missingCommit: true).ConfigureAwait(false);
                        await VerifyRestartAssignmentFromPreservedCommitAsync(harness, surface, branchPresent: true).ConfigureAwait(false);
                        await VerifyRestartCaptureBoundariesAsync(harness, surface).ConfigureAwait(false);

                    }
                }
            }).ConfigureAwait(false);

            await RunTest("RestartMission_StageBehindAFailedStageThatMovedTheBranch_IsRefusedOnEverySurface", async () =>
            {
                using (SurfaceParityHarness harness = await SurfaceParityHarness.StartAsync().ConfigureAwait(false))
                {
                    foreach (string surface in Surfaces)
                    {
                        UpstreamFailureChain chain = await SeedUpstreamFailureChainAsync(harness, surface + "-moved", "fedcba9876543210fedcba9876543210fedcba98").ConfigureAwait(false);
                        harness.ResetRecordings();

                        SurfaceReply reply = await RestartAsync(harness, surface, chain.Judge.Id).ConfigureAwait(false);

                        AssertTrue(reply.Refused, surface + ": a branch tip no stage verified is not reviewed: " + reply);
                        AssertContains(chain.FailedStage.Id, reply.Body, surface + ": the refusal names the stage that moved the branch: " + reply);
                        Mission? judge = await harness.Driver.Missions.ReadAsync(chain.Judge.Id).ConfigureAwait(false);
                        AssertEqual(MissionStatusEnum.Cancelled, judge!.Status, surface + ": the Judge keeps its status");
                        Voyage? voyage = await harness.Driver.Voyages.ReadAsync(chain.Voyage.Id).ConfigureAwait(false);
                        AssertEqual(VoyageStatusEnum.Failed, voyage!.Status, surface + ": the voyage stays Failed");
                        AssertEqual(0, harness.Events.Count, surface + ": a refused restart writes no event");
                    }
                }
            }).ConfigureAwait(false);

            await RunTest("RestartMission_OperatorStoppedStageOfEndedVoyage_ReopensTheVoyageOnEverySurface", async () =>
            {
                using (SurfaceParityHarness harness = await SurfaceParityHarness.StartAsync().ConfigureAwait(false))
                {
                    VoyageStatusEnum[] ended = new[] { VoyageStatusEnum.Failed, VoyageStatusEnum.Cancelled };
                    foreach (string surface in Surfaces)
                    {
                        foreach (VoyageStatusEnum voyageStatus in ended)
                        {
                            Mission mission = await SeedAsync(harness, surface + "-stopped-" + voyageStatus, MissionStatusEnum.Cancelled).ConfigureAwait(false);
                            mission.FailureReason = OperatorStop.FailureReasonPrefix + " stopped by operator via REST at 2026-01-01T00:00:00Z";
                            await harness.Driver.Missions.UpdateAsync(mission).ConfigureAwait(false);
                            Voyage voyage = await AttachVoyageAsync(harness, mission, voyageStatus).ConfigureAwait(false);
                            harness.ResetRecordings();

                            SurfaceReply reply = await RestartAsync(harness, surface, mission.Id).ConfigureAwait(false);

                            string label = surface + " (" + voyageStatus + " voyage)";
                            AssertFalse(reply.Refused, label + ": a stage an operator stopped is restarted: " + reply);
                            Mission? stored = await harness.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                            AssertEqual(MissionStatusEnum.Pending, stored!.Status, label + ": the stopped stage is Pending");
                            Voyage? storedVoyage = await harness.Driver.Voyages.ReadAsync(voyage.Id).ConfigureAwait(false);
                            AssertEqual(VoyageStatusEnum.InProgress, storedVoyage!.Status, label + ": the voyage is reopened");
                        }
                    }
                }
            }).ConfigureAwait(false);

            await RunTest("RestartMission_FailedStageOfFailedVoyage_ReopensTheVoyageAndRunsTheLaterCancelledStagesAgainOnEverySurface", async () =>
            {
                using (SurfaceParityHarness harness = await SurfaceParityHarness.StartAsync().ConfigureAwait(false))
                {
                    foreach (string surface in Surfaces)
                    {
                        FailedStageChain chain = await SeedFailedStageChainAsync(harness, surface).ConfigureAwait(false);
                        harness.ResetRecordings();

                        SurfaceReply reply = await RestartAsync(harness, surface, chain.FailedStage.Id).ConfigureAwait(false);

                        AssertFalse(reply.Refused, surface + ": the failed stage of a Failed voyage is restarted: " + reply);
                        Mission? failed = await harness.Driver.Missions.ReadAsync(chain.FailedStage.Id).ConfigureAwait(false);
                        AssertEqual(MissionStatusEnum.Pending, failed!.Status, surface + ": the failed stage is Pending");
                        AssertEqual(chain.Producer.Id, failed.DependsOnMissionId, surface + ": the failed stage runs on the stage that produced verified work");
                        Mission? producer = await harness.Driver.Missions.ReadAsync(chain.Producer.Id).ConfigureAwait(false);
                        AssertEqual(MissionStatusEnum.WorkProduced, producer!.Status, surface + ": the producing stage hands off its work again");
                        AssertEqual(_ProducedCommit, producer.CommitHash, surface + ": the producing stage keeps its commit");
                        AssertFalse(TerminalVoyageMissionRule.IsReconciledOutcome(producer), surface + ": the producing stage is no longer a record of an ended voyage");
                        foreach (Mission later in chain.Later)
                        {
                            Mission? stored = await harness.Driver.Missions.ReadAsync(later.Id).ConfigureAwait(false);
                            AssertEqual(MissionStatusEnum.Pending, stored!.Status, surface + ": later stage " + later.Persona + " runs again");
                            AssertNull(stored.FailureReason, surface + ": later stage " + later.Persona + " drops its cancellation reason");
                            AssertNull(stored.CompletedUtc, surface + ": later stage " + later.Persona + " has no completion time");
                        }
                        Voyage? voyage = await harness.Driver.Voyages.ReadAsync(chain.Voyage.Id).ConfigureAwait(false);
                        AssertEqual(VoyageStatusEnum.InProgress, voyage!.Status, surface + ": the voyage is reopened");
                        AssertNull(voyage.CompletedUtc, surface + ": the reopened voyage has no completion time");
                        AssertEqual(1, harness.EventTypes().Count(type => type == "voyage.reopened:" + chain.Voyage.Id),
                            surface + ": one voyage.reopened event: " + String.Join(",", harness.EventTypes()));
                    }
                }
            }).ConfigureAwait(false);
        }

        private async Task VerifyRestartAssignmentFromPreservedCommitAsync(SurfaceParityHarness harness, string surface, bool missingCommit = false, bool branchPresent = false)
        {
            string variant = missingCommit ? "missing-commit" : branchPresent ? "branch-present" : "normal";
            string root = Path.Combine(Path.GetTempPath(), "armada-restart-anchor-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                string source = Path.Combine(root, "source");
                string bare = Path.Combine(root, "vessel.git");
                GitServiceTests.SeedTemplateRepository(source);
                string defaultBranch = (await RunGitAsync(source, "symbolic-ref", "--short", "HEAD").ConfigureAwait(false)).Trim();
                string defaultHead = (await RunGitAsync(source, "rev-parse", "HEAD").ConfigureAwait(false)).Trim();
                await RunGitAsync(source, "config", "user.name", "Armada test").ConfigureAwait(false);
                await RunGitAsync(source, "config", "user.email", "armada-tests@example.com").ConfigureAwait(false);
                await RunGitAsync(source, "checkout", "-b", "produced-fixture").ConfigureAwait(false);
                await File.WriteAllTextAsync(Path.Combine(source, "produced.txt"), "Verified upstream work").ConfigureAwait(false);
                await RunGitAsync(source, "add", "produced.txt").ConfigureAwait(false);
                await RunGitAsync(source, "commit", "-m", "Produce upstream work").ConfigureAwait(false);
                string producedHead = (await RunGitAsync(source, "rev-parse", "HEAD").ConfigureAwait(false)).Trim();
                await RunGitAsync(root, "clone", "--bare", source, bare).ConfigureAwait(false);
                await RunGitAsync(source, "checkout", defaultBranch).ConfigureAwait(false);
                await RunGitAsync(source, "branch", "-D", "produced-fixture").ConfigureAwait(false);
                await RunGitAsync(bare, "symbolic-ref", "HEAD", "refs/heads/" + defaultBranch).ConfigureAwait(false);

                UpstreamFailureChain chain = await SeedUpstreamFailureChainAsync(harness, surface + "-real-" + variant, producedHead).ConfigureAwait(false);
                Vessel vessel = (await harness.Driver.Vessels.ReadAsync(chain.FailedStage.VesselId!).ConfigureAwait(false))!;
                vessel.RepoUrl = source;
                vessel.LocalPath = bare;
                vessel.DefaultBranch = defaultBranch;
                vessel.WorkingDirectory = Path.Combine(root, "working");
                await harness.Driver.Vessels.UpdateAsync(vessel).ConfigureAwait(false);
                string requestedHead = missingCommit ? new string('a', 40) : producedHead;
                chain.Producer.CommitHash = requestedHead;
                chain.Producer.Status = MissionStatusEnum.Complete;
                await harness.Driver.Missions.UpdateAsync(chain.Producer).ConfigureAwait(false);
                await RunGitAsync(bare, "update-ref", "refs/armada/missions/" + chain.Producer.Id, producedHead).ConfigureAwait(false);
                await RunGitAsync(bare, "update-ref", "refs/armada-preserved/" + chain.Producer.BranchName, producedHead).ConfigureAwait(false);
                await RunGitAsync(bare, "branch", "-D", "produced-fixture").ConfigureAwait(false);
                if (branchPresent)
                    await RunGitAsync(bare, "update-ref", "refs/heads/" + chain.Producer.BranchName, defaultHead).ConfigureAwait(false);

                Dock priorDock = await harness.Driver.Docks.CreateAsync(new Dock(vessel.Id)
                {
                    TenantId = vessel.TenantId, UserId = vessel.UserId, Active = false,
                    BranchName = chain.Producer.BranchName,
                    WorktreePath = Path.Combine(root, "removed", vessel.Name)
                }).ConfigureAwait(false);
                chain.Producer.DockId = priorDock.Id;
                await harness.Driver.Missions.UpdateAsync(chain.Producer).ConfigureAwait(false);
                AssertFalse(Directory.Exists(priorDock.WorktreePath), "The original checkout is absent.");

                SurfaceReply restart = await RestartAsync(harness, surface, chain.FailedStage.Id).ConfigureAwait(false);
                AssertFalse(restart.Refused, "The original failed stage restarts through " + surface);
                Mission pending = (await harness.Driver.Missions.ReadAsync(chain.FailedStage.Id).ConfigureAwait(false))!;
                LoggingModule logging = new LoggingModule();
                logging.Settings.EnableConsole = false;
                GitService git = new GitService(logging);
                DockService docks = new DockService(logging, harness.Driver, harness.Settings, git);
                CaptainService captains = new CaptainService(logging, harness.Driver, harness.Settings, git, docks);
                int launches = 0;
                captains.OnLaunchAgent = (_, _, _) => { launches++; return Task.FromResult(Int32.MaxValue - 20); };
                MissionService missions = new MissionService(logging, harness.Driver, harness.Settings, docks, captains,
                    git: git, resourcePressureAdmission: TestResourcePressure.Unconstrained(harness.Settings));
                Captain captain = await harness.Driver.Captains.CreateAsync(new Captain("restart-real-" + surface + "-" + variant)
                {
                    TenantId = vessel.TenantId, UserId = vessel.UserId, Model = "claude-opus-5",
                    AllowedPersonas = "[\"Worker\"]"
                }).ConfigureAwait(false);
                bool didAssign = await missions.TryAssignAsync(pending, vessel).ConfigureAwait(false);
                if (branchPresent)
                {
                    AssertFalse(didAssign, "An existing branch without the captured source must not launch.");
                    AssertEqual(defaultHead, (await RunGitAsync(bare, "rev-parse", "refs/heads/" + chain.Producer.BranchName).ConfigureAwait(false)).Trim(),
                        "Restart must not reset an existing branch to repair its base.");
                    AssertEqual(0, launches);
                    return;
                }
                if (missingCommit)
                {
                    AssertFalse(didAssign, "A missing captured commit must fail before checkout or launch.");
                    Mission refused = (await harness.Driver.Missions.ReadAsync(pending.Id).ConfigureAwait(false))!;
                    AssertContains("start_from_ref_missing", refused.FailureReason ?? String.Empty);
                    AssertEqual(0, launches);
                    AssertNull(refused.DockId);
                    AssertEqual(defaultHead, (await RunGitAsync(bare, "rev-parse", "refs/heads/" + defaultBranch).ConfigureAwait(false)).Trim());
                    return;
                }
                AssertTrue(didAssign,
                    "A restarted dependent must reconstruct its missing branch from verified work, not the default branch.");
                Mission assigned = (await harness.Driver.Missions.ReadAsync(pending.Id).ConfigureAwait(false))!;
                Dock current = (await harness.Driver.Docks.ReadAsync(assigned.DockId!).ConfigureAwait(false))!;
                AssertEqual(producedHead, (await RunGitAsync(current.WorktreePath!, "rev-parse", "HEAD").ConfigureAwait(false)).Trim());
                AssertTrue(File.Exists(Path.Combine(current.WorktreePath!, "produced.txt")));
                AssertEqual(producedHead, assigned.StartFromRef, "The immutable restart base survives persistence and handoff.");
                AssertEqual(defaultHead, (await RunGitAsync(bare, "rev-parse", "refs/heads/" + defaultBranch).ConfigureAwait(false)).Trim());
                AssertFalse((await harness.Driver.Docks.ReadAsync(priorDock.Id).ConfigureAwait(false))!.Active);
                AssertEqual(1, launches, "Only the reconstructed exact source launches.");
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                        File.SetAttributes(file, FileAttributes.Normal);
                    Directory.Delete(root, true);
                }
            }
        }

        private async Task VerifyRestartCaptureBoundariesAsync(SurfaceParityHarness harness, string surface)
        {
            List<HarborMissionRoute> priorRoutes = harness.Settings.Harbor.MissionRoutes;
            try
            {
                foreach (string boundary in new[] { "vessel-owned", "captain-owned", "retained-job", "retained-attempt", "foreign-tenant", "foreign-user", "cross-vessel" })
                {
                    FailedStageChain chain = await SeedFailedStageChainAsync(harness, surface + "-" + boundary).ConfigureAwait(false);
                    chain.FailedStage.StartFromRef = "prior-explicit-source";
                    harness.Settings.Harbor.MissionRoutes = new List<HarborMissionRoute>();
                    if (boundary == "vessel-owned" || boundary == "captain-owned")
                        harness.Settings.Harbor.MissionRoutes.Add(new HarborMissionRoute
                        {
                            CheckoutMode = HarborCheckoutModeEnum.HarborOwned,
                            VesselId = boundary == "vessel-owned" ? chain.FailedStage.VesselId : null,
                            CaptainId = boundary == "captain-owned" ? "unselected-captain" : null
                        });
                    if (boundary == "retained-job") chain.FailedStage.HarborJobId = "retained-job";
                    if (boundary == "retained-attempt") chain.Producer.DockProvisionAttemptId = "retained-attempt";
                    if (boundary == "foreign-tenant")
                    {
                        TenantMetadata tenant = await harness.Driver.Tenants.CreateAsync(new TenantMetadata("restart-foreign-" + surface)).ConfigureAwait(false);
                        UserMaster user = await harness.Driver.Users.CreateAsync(new UserMaster(tenant.Id, "restart-foreign-" + surface + "@armada.test", "password")).ConfigureAwait(false);
                        chain.Producer.TenantId = tenant.Id;
                        chain.Producer.UserId = user.Id;
                    }
                    if (boundary == "foreign-user")
                    {
                        UserMaster user = await harness.Driver.Users.CreateAsync(new UserMaster(Armada.Core.Constants.DefaultTenantId, "restart-other-" + surface + "@armada.test", "password")).ConfigureAwait(false);
                        chain.Producer.UserId = user.Id;
                    }
                    if (boundary == "cross-vessel")
                    {
                        Vessel other = await harness.Driver.Vessels.CreateAsync(new Vessel("restart-other-" + surface, "https://github.com/test/other.git")).ConfigureAwait(false);
                        chain.Producer.VesselId = other.Id;
                    }
                    await harness.Driver.Missions.UpdateAsync(chain.Producer).ConfigureAwait(false);
                    await harness.Driver.Missions.UpdateAsync(chain.FailedStage).ConfigureAwait(false);
                    SurfaceReply reply = await RestartAsync(harness, surface, chain.FailedStage.Id).ConfigureAwait(false);
                    AssertFalse(reply.Refused, boundary + ": restart retains its existing supported behavior.");
                    Mission stored = (await harness.Driver.Missions.ReadAsync(chain.FailedStage.Id).ConfigureAwait(false))!;
                    AssertEqual("prior-explicit-source", stored.StartFromRef, boundary + ": do not replace another checkout or owner's source payload.");
                }
            }
            finally
            {
                harness.Settings.Harbor.MissionRoutes = priorRoutes;
            }
        }

        private static async Task<string> RunGitAsync(string directory, params string[] arguments)
        {
            ProcessStartInfo start = new ProcessStartInfo("git")
            { WorkingDirectory = directory, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string argument in arguments) start.ArgumentList.Add(argument);
            using Process process = new Process { StartInfo = start };
            process.Start();
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().ConfigureAwait(false);
            string stdout = await output.ConfigureAwait(false);
            string stderr = await error.ConfigureAwait(false);
            if (process.ExitCode != 0) throw new InvalidOperationException("Git fixture failed: " + stderr.Trim());
            return stdout;
        }

        private const string _ProducedCommit = "0123456789abcdef0123456789abcdef01234567";

        private sealed class UpstreamFailureChain
        {
            public Voyage Voyage { get; set; } = null!;
            public Mission Producer { get; set; } = null!;
            public Mission FailedStage { get; set; } = null!;
            public Mission Judge { get; set; } = null!;
        }

        /// <summary>
        /// A Failed voyage whose Worker produced <see cref="_ProducedCommit"/>, whose revising Worker then failed
        /// leaving the branch at <paramref name="failedStageCommit"/>, and whose Judge was cancelled behind it.
        /// </summary>
        private static async Task<UpstreamFailureChain> SeedUpstreamFailureChainAsync(SurfaceParityHarness harness, string surface, string failedStageCommit)
        {
            Vessel vessel = await harness.Driver.Vessels.CreateAsync(new Vessel("restart-chain-vessel-" + surface, "https://github.com/test/restart.git")
            {
                TenantId = Constants.DefaultTenantId,
                UserId = Constants.DefaultUserId
            }).ConfigureAwait(false);
            Voyage voyage = await harness.Driver.Voyages.CreateAsync(new Voyage("restart chain " + surface, "restart parity")
            {
                TenantId = Constants.DefaultTenantId,
                UserId = Constants.DefaultUserId,
                Status = VoyageStatusEnum.Failed,
                CompletedUtc = DateTime.UtcNow
            }).ConfigureAwait(false);
            string branch = "armada/restart-chain-" + surface;

            Mission producer = await harness.Driver.Missions.CreateAsync(new Mission("produce " + surface)
            {
                TenantId = Constants.DefaultTenantId, UserId = Constants.DefaultUserId, VesselId = vessel.Id, VoyageId = voyage.Id,
                Persona = "Worker", StageOrder = 2, BranchName = branch, CommitHash = _ProducedCommit,
                Status = MissionStatusEnum.WorkProduced, StartedUtc = DateTime.UtcNow.AddHours(-2)
            }).ConfigureAwait(false);
            Mission failed = await harness.Driver.Missions.CreateAsync(new Mission("revise " + surface)
            {
                TenantId = Constants.DefaultTenantId, UserId = Constants.DefaultUserId, VesselId = vessel.Id, VoyageId = voyage.Id,
                Persona = "Worker", StageOrder = 5, BranchName = branch, CommitHash = failedStageCommit, DependsOnMissionId = producer.Id,
                Status = MissionStatusEnum.Failed, StartedUtc = DateTime.UtcNow.AddHours(-1), CompletedUtc = DateTime.UtcNow,
                FailureReason = "no_op_completion_detected: captain exited with a completion marker"
            }).ConfigureAwait(false);
            Mission judge = await harness.Driver.Missions.CreateAsync(new Mission("judge " + surface)
            {
                TenantId = Constants.DefaultTenantId, UserId = Constants.DefaultUserId, VesselId = vessel.Id, VoyageId = voyage.Id,
                Persona = "Judge", StageOrder = 6, BranchName = branch, DependsOnMissionId = failed.Id,
                Status = MissionStatusEnum.Cancelled, CompletedUtc = DateTime.UtcNow,
                FailureReason = "Blocked by failed dependency " + failed.Id
            }).ConfigureAwait(false);

            return new UpstreamFailureChain { Voyage = voyage, Producer = producer, FailedStage = failed, Judge = judge };
        }

        private sealed class FailedStageChain
        {
            public Voyage Voyage { get; set; } = null!;
            public Mission Producer { get; set; } = null!;
            public Mission FailedStage { get; set; } = null!;
            public List<Mission> Later { get; set; } = new List<Mission>();
        }

        /// <summary>
        /// A Failed voyage whose first Worker produced <see cref="_ProducedCommit"/> and was then moved to Failed by the
        /// terminal-voyage rule, whose second stage failed its definition-of-done gate, and whose two later stages were
        /// cancelled behind it.
        /// </summary>
        private static async Task<FailedStageChain> SeedFailedStageChainAsync(SurfaceParityHarness harness, string surface)
        {
            Vessel vessel = await harness.Driver.Vessels.CreateAsync(new Vessel("restart-failed-stage-vessel-" + surface, "https://github.com/test/restart.git")
            {
                TenantId = Constants.DefaultTenantId,
                UserId = Constants.DefaultUserId
            }).ConfigureAwait(false);
            Voyage voyage = await harness.Driver.Voyages.CreateAsync(new Voyage("restart failed stage " + surface, "restart parity")
            {
                TenantId = Constants.DefaultTenantId,
                UserId = Constants.DefaultUserId,
                Status = VoyageStatusEnum.Failed,
                CompletedUtc = DateTime.UtcNow
            }).ConfigureAwait(false);
            string branch = "armada/restart-failed-stage-" + surface;

            Mission producer = new Mission("produce " + surface)
            {
                TenantId = Constants.DefaultTenantId, UserId = Constants.DefaultUserId, VesselId = vessel.Id, VoyageId = voyage.Id,
                Persona = "Worker", StageOrder = 1, BranchName = branch, CommitHash = _ProducedCommit,
                Status = MissionStatusEnum.Failed, StartedUtc = DateTime.UtcNow.AddHours(-2), CompletedUtc = DateTime.UtcNow
            };
            TerminalVoyageMissionRule.RecordReconciledOutcome(producer, VoyageStatusEnum.Failed, TerminalVoyageMissionRule.ReasonWorkUnlanded, DateTime.UtcNow);
            producer = await harness.Driver.Missions.CreateAsync(producer).ConfigureAwait(false);
            Mission failed = await harness.Driver.Missions.CreateAsync(new Mission("review " + surface)
            {
                TenantId = Constants.DefaultTenantId, UserId = Constants.DefaultUserId, VesselId = vessel.Id, VoyageId = voyage.Id,
                Persona = "TestEngineer", StageOrder = 2, BranchName = branch, CommitHash = "fedcba9876543210fedcba9876543210fedcba98",
                DependsOnMissionId = producer.Id, Status = MissionStatusEnum.Failed, StartedUtc = DateTime.UtcNow.AddHours(-1),
                CompletedUtc = DateTime.UtcNow, FailureReason = "DoD gate failed: classification=Infra; consumer command exited 125"
            }).ConfigureAwait(false);
            Mission revise = await harness.Driver.Missions.CreateAsync(new Mission("revise " + surface)
            {
                TenantId = Constants.DefaultTenantId, UserId = Constants.DefaultUserId, VesselId = vessel.Id, VoyageId = voyage.Id,
                Persona = "Worker", StageOrder = 3, DependsOnMissionId = failed.Id,
                Status = MissionStatusEnum.Cancelled, CompletedUtc = DateTime.UtcNow,
                FailureReason = "Blocked by failed dependency " + failed.Id
            }).ConfigureAwait(false);
            Mission judge = await harness.Driver.Missions.CreateAsync(new Mission("judge " + surface)
            {
                TenantId = Constants.DefaultTenantId, UserId = Constants.DefaultUserId, VesselId = vessel.Id, VoyageId = voyage.Id,
                Persona = "Judge", StageOrder = 4, DependsOnMissionId = revise.Id,
                Status = MissionStatusEnum.Cancelled, CompletedUtc = DateTime.UtcNow,
                FailureReason = "Blocked by failed dependency " + revise.Id
            }).ConfigureAwait(false);

            return new FailedStageChain { Voyage = voyage, Producer = producer, FailedStage = failed, Later = new List<Mission> { revise, judge } };
        }

        private static async Task<Voyage> AttachVoyageAsync(SurfaceParityHarness harness, Mission mission, VoyageStatusEnum status)
        {
            bool ended = status == VoyageStatusEnum.Failed || status == VoyageStatusEnum.Complete || status == VoyageStatusEnum.Cancelled;
            Voyage voyage = await harness.Driver.Voyages.CreateAsync(new Voyage("restart voyage " + status, "restart parity")
            {
                TenantId = mission.TenantId,
                UserId = mission.UserId,
                Status = status,
                CompletedUtc = ended ? DateTime.UtcNow : (DateTime?)null
            }).ConfigureAwait(false);
            mission.VoyageId = voyage.Id;
            await harness.Driver.Missions.UpdateAsync(mission).ConfigureAwait(false);
            return voyage;
        }

        private static string RejectedReview(string finding)
        {
            return "[ARMADA:VERDICT] NEEDS_REVISION\nSummary of the review.\n\n## Correctness\n- Blocking: " + finding + " the retry path drops the final item.\n";
        }

        private static int Count(string text, string needle)
        {
            int count = 0;
            int index = 0;
            while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += needle.Length;
            }
            return count;
        }

        private static async Task<Mission> SeedJudgeAsync(SurfaceParityHarness harness, string label, string? output, string description, string persona = "Judge")
        {
            Mission mission = await SeedAsync(harness, label, MissionStatusEnum.Failed).ConfigureAwait(false);
            mission.Title = persona + " " + label;
            mission.Persona = persona;
            mission.Description = description;
            mission.AgentOutput = output;
            return await harness.Driver.Missions.UpdateAsync(mission).ConfigureAwait(false);
        }

        private static async Task<Mission> SeedAsync(SurfaceParityHarness harness, string surface, MissionStatusEnum status)
        {
            Vessel vessel = await harness.Driver.Vessels.CreateAsync(new Vessel("restart-vessel-" + surface + "-" + status, "https://github.com/test/restart.git")
            {
                TenantId = Constants.DefaultTenantId,
                UserId = Constants.DefaultUserId
            }).ConfigureAwait(false);
            return await harness.Driver.Missions.CreateAsync(new Mission("restart " + surface)
            {
                TenantId = Constants.DefaultTenantId,
                UserId = Constants.DefaultUserId,
                VesselId = vessel.Id,
                BranchName = "armada/restart-" + surface,
                CommitHash = "0123456789abcdef0123456789abcdef01234567",
                Status = status,
                CompletedUtc = DateTime.UtcNow
            }).ConfigureAwait(false);
        }

        private static Task<SurfaceReply> RestartAsync(SurfaceParityHarness harness, string surface, string missionId)
        {
            switch (surface)
            {
                case "REST": return harness.RestAsync(HttpMethod.Post, "/api/v1/missions/" + missionId + "/restart", new { });
                case "WebSocket": return harness.WebSocketAsync("restart_mission", missionId, new { });
                default: return harness.McpAsync("armada_restart_mission", new { missionId = missionId });
            }
        }
    }
}
