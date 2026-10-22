namespace Armada.Test.Unit.Suites.Routes
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net.Http;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// Restarting a mission is one operation on REST, WebSocket and MCP. A LandingFailed mission keeps its produced
    /// work: every surface refuses to restart it and names retry-landing. A Failed mission returns to Pending, and every
    /// surface writes the same event, broadcasts the same change, and records a restart signal its owner can read. A
    /// mission whose voyage has ended (Failed, Complete or Cancelled) is refused on every surface, because a mission of
    /// an ended voyage is never assigned, except a stage cancelled behind a failed upstream stage of a Failed voyage: it
    /// reopens the voyage and runs on the last stage that produced verified work.
    /// </summary>
    public class MissionRestartParityTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Mission Restart Parity";

        private static readonly string[] Surfaces = new[] { "REST", "WebSocket", "MCP" };

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
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
