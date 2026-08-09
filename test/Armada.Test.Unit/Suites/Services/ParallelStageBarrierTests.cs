namespace Armada.Test.Unit.Suites.Services
{
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// Guards the parallel-stage barrier. Same-order pipeline stages are dispatched as siblings that
    /// share a single upstream dependency, but a mission's DependsOnMissionId names only ONE of them.
    /// Without a barrier the next order starts as soon as that one sibling finishes, letting a Judge
    /// review a diff its sibling reviewers had not finished contributing to.
    ///
    /// The group is keyed on StageOrder as well as voyage and parent. Architect fan-out clones whole
    /// downstream chains that also share a parent, so keying on the parent alone deadlocked them --
    /// caught by PipelineDispatchTests. Fan-out missions carry no StageOrder, which is what keeps them
    /// out of any group; that invariant is pinned here so it cannot silently regress.
    /// </summary>
    public class ParallelStageBarrierTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Parallel Stage Barrier";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("SatisfyingStatuses_AreExactlyTheChainingSet", () =>
            {
                AssertTrue(MissionService.IsDependencySatisfyingStatus(MissionStatusEnum.Complete),
                    "Complete satisfies a dependency");
                AssertTrue(MissionService.IsDependencySatisfyingStatus(MissionStatusEnum.WorkProduced),
                    "WorkProduced satisfies a dependency so downstream stages can chain off the branch");
                AssertTrue(MissionService.IsDependencySatisfyingStatus(MissionStatusEnum.PullRequestOpen),
                    "PullRequestOpen satisfies a dependency: the branch is finalized and pushed at PR-open time");

                AssertTrue(!MissionService.IsDependencySatisfyingStatus(MissionStatusEnum.Pending),
                    "Pending does not satisfy a dependency");
                AssertTrue(!MissionService.IsDependencySatisfyingStatus(MissionStatusEnum.Assigned),
                    "Assigned does not satisfy a dependency");
                AssertTrue(!MissionService.IsDependencySatisfyingStatus(MissionStatusEnum.Failed),
                    "Failed does not satisfy a dependency");
            });

            await RunTest("GroupMembership_RequiresVoyageParentAndStageOrder", () =>
            {
                Mission dependency = Fixture("v1", "worker-1", 2);

                AssertTrue(MissionService.IsParallelStageSibling(Fixture("v1", "worker-1", 2), dependency),
                    "same voyage, same parent, same stage order is a sibling");
                AssertTrue(!MissionService.IsParallelStageSibling(Fixture("v1", "worker-1", 3), dependency),
                    "a different stage order is a later stage, not a sibling");
                AssertTrue(!MissionService.IsParallelStageSibling(Fixture("v1", "worker-2", 2), dependency),
                    "a different parent belongs to a different chain");
                AssertTrue(!MissionService.IsParallelStageSibling(Fixture("v2", "worker-1", 2), dependency),
                    "a different voyage is unrelated");
            });

            await RunTest("FanOutMissionsWithoutStageOrder_AreNeverGrouped", () =>
            {
                // Architect fan-out creates workers and clones chains without a StageOrder. That is
                // what keeps independent chains out of each other's barrier; keying on the shared
                // parent alone deadlocked them.
                Mission pipelineStage = Fixture("v1", "architect-1", 2);
                Mission fanOutClone = Fixture("v1", "architect-1", null);

                AssertTrue(!MissionService.IsParallelStageSibling(fanOutClone, pipelineStage),
                    "a fan-out mission with no stage order must not join a pipeline stage group");
                AssertTrue(!MissionService.IsParallelStageSibling(pipelineStage, fanOutClone),
                    "the relationship must not hold in the other direction either");
                AssertTrue(!MissionService.IsParallelStageSibling(fanOutClone, Fixture("v1", "architect-1", null)),
                    "two fan-out clones sharing a parent are independent chains, not siblings");
            });

            await RunTest("DownstreamWaits_WhileAParallelSiblingIsUnfinished", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    Harness h = await Harness.CreateAsync(testDb);

                    Mission upstream = await h.CreateMissionAsync("Worker", "[Worker] impl", null, 1, MissionStatusEnum.Complete);
                    Mission siblingA = await h.CreateMissionAsync("Judge", "[Judge] review", upstream.Id, 2, MissionStatusEnum.Complete);
                    Mission siblingB = await h.CreateMissionAsync("PortingReferenceAnalyst", "[Analyst] review", upstream.Id, 2, MissionStatusEnum.Assigned);
                    Mission downstream = await h.CreateMissionAsync("TestEngineer", "[TestEngineer] validate", siblingA.Id, 3, MissionStatusEnum.Pending);

                    bool assigned = await h.Missions.TryAssignAsync(downstream, h.Vessel).ConfigureAwait(false);
                    AssertTrue(!assigned, "downstream must not assign while a parallel sibling is still running");

                    Mission? reread = await testDb.Driver.Missions.ReadAsync(downstream.Id).ConfigureAwait(false);
                    AssertTrue(reread!.AssignmentState == MissionAssignmentStateEnum.WaitingForDependency,
                        "downstream must report WaitingForDependency; was " + reread.AssignmentState);

                    // The named dependency was already satisfied, so the barrier is what held it.
                    AssertTrue(MissionService.IsDependencySatisfyingStatus(siblingA.Status),
                        "the named dependency itself was satisfied");
                    AssertTrue(!MissionService.IsDependencySatisfyingStatus(siblingB.Status),
                        "the unfinished sibling is the reason for the block");
                }
            });

            await RunTest("DownstreamProceeds_OnceEverySiblingFinishes", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    Harness h = await Harness.CreateAsync(testDb);

                    Mission upstream = await h.CreateMissionAsync("Worker", "[Worker] impl", null, 1, MissionStatusEnum.Complete);
                    Mission siblingA = await h.CreateMissionAsync("Judge", "[Judge] review", upstream.Id, 2, MissionStatusEnum.Complete);
                    Mission siblingB = await h.CreateMissionAsync("PortingReferenceAnalyst", "[Analyst] review", upstream.Id, 2, MissionStatusEnum.Assigned);
                    Mission downstream = await h.CreateMissionAsync("TestEngineer", "[TestEngineer] validate", siblingA.Id, 3, MissionStatusEnum.Pending);

                    await h.Missions.TryAssignAsync(downstream, h.Vessel).ConfigureAwait(false);
                    Mission? blocked = await testDb.Driver.Missions.ReadAsync(downstream.Id).ConfigureAwait(false);
                    AssertTrue(blocked!.AssignmentState == MissionAssignmentStateEnum.WaitingForDependency,
                        "precondition: downstream is held while the sibling runs");

                    siblingB.Status = MissionStatusEnum.Complete;
                    await testDb.Driver.Missions.UpdateAsync(siblingB).ConfigureAwait(false);

                    Mission? retry = await testDb.Driver.Missions.ReadAsync(downstream.Id).ConfigureAwait(false);
                    await h.Missions.TryAssignAsync(retry!, h.Vessel).ConfigureAwait(false);

                    Mission? after = await testDb.Driver.Missions.ReadAsync(downstream.Id).ConfigureAwait(false);
                    AssertTrue(after!.AssignmentState != MissionAssignmentStateEnum.WaitingForDependency,
                        "downstream must clear the barrier once every sibling is satisfied; was " + after.AssignmentState);
                }
            });

            await RunTest("SequentialPipeline_IsUnaffectedByTheBarrier", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    Harness h = await Harness.CreateAsync(testDb);

                    Mission upstream = await h.CreateMissionAsync("Worker", "[Worker] impl", null, 1, MissionStatusEnum.Complete);
                    Mission downstream = await h.CreateMissionAsync("Judge", "[Judge] review", upstream.Id, 2, MissionStatusEnum.Pending);

                    await h.Missions.TryAssignAsync(downstream, h.Vessel).ConfigureAwait(false);

                    Mission? reread = await testDb.Driver.Missions.ReadAsync(downstream.Id).ConfigureAwait(false);
                    AssertTrue(reread!.AssignmentState != MissionAssignmentStateEnum.WaitingForDependency,
                        "a lone-sibling group must not be held by the barrier; was " + reread.AssignmentState);
                }
            });

            // End-to-end proof of the parallel-review design: two same-order Judges
            // sharing one upstream dependency BOTH dispatch while the other is in flight -- the
            // reviewer fan-out runs concurrently, and only the barrier at the next order serializes.
            await RunTest("ParallelSiblings_BothDispatchConcurrently", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    Harness h = await Harness.CreateAsync(testDb);

                    // Parallel stages on one vessel require concurrent missions to be allowed.
                    h.Vessel.AllowConcurrentMissions = true;
                    await testDb.Driver.Vessels.UpdateAsync(h.Vessel).ConfigureAwait(false);

                    Captain secondCaptain = new Captain("barrier-captain-2");
                    secondCaptain.State = CaptainStateEnum.Idle;
                    await testDb.Driver.Captains.CreateAsync(secondCaptain).ConfigureAwait(false);

                    Mission upstream = await h.CreateMissionAsync("Worker", "[Worker] impl", null, 1, MissionStatusEnum.Complete);
                    Mission judgeA = await h.CreateMissionAsync("Judge", "[Judge] A", upstream.Id, 2, MissionStatusEnum.Pending);
                    Mission judgeB = await h.CreateMissionAsync("Judge", "[Judge] B", upstream.Id, 2, MissionStatusEnum.Pending);

                    bool assignedA = await h.Missions.TryAssignAsync(judgeA, h.Vessel).ConfigureAwait(false);
                    Mission? rereadB = await testDb.Driver.Missions.ReadAsync(judgeB.Id).ConfigureAwait(false);
                    bool assignedB = await h.Missions.TryAssignAsync(rereadB!, h.Vessel).ConfigureAwait(false);

                    AssertTrue(assignedA, "the first parallel Judge must dispatch");
                    AssertTrue(assignedB, "the second parallel Judge must dispatch while the first is in flight");

                    Mission? afterA = await testDb.Driver.Missions.ReadAsync(judgeA.Id).ConfigureAwait(false);
                    Mission? afterB = await testDb.Driver.Missions.ReadAsync(judgeB.Id).ConfigureAwait(false);
                    AssertTrue(
                        afterA!.AssignmentState == MissionAssignmentStateEnum.Assigned
                            || afterA.Status == MissionStatusEnum.InProgress,
                        "first Judge must be working; was " + afterA.Status + "/" + afterA.AssignmentState);
                    AssertTrue(
                        afterB!.AssignmentState == MissionAssignmentStateEnum.Assigned
                            || afterB.Status == MissionStatusEnum.InProgress,
                        "second Judge must be working concurrently; was " + afterB.Status + "/" + afterB.AssignmentState);
                }
            });

            await RunTest("StageOrderRoundTripsThroughTheDatabase", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    Harness h = await Harness.CreateAsync(testDb);

                    Mission staged = await h.CreateMissionAsync("Judge", "[Judge] review", null, 4, MissionStatusEnum.Pending);
                    Mission? reread = await testDb.Driver.Missions.ReadAsync(staged.Id).ConfigureAwait(false);
                    AssertTrue(reread!.StageOrder == 4,
                        "stage order must persist; was " + (reread.StageOrder?.ToString() ?? "null"));

                    reread.StageOrder = 7;
                    await testDb.Driver.Missions.UpdateAsync(reread).ConfigureAwait(false);
                    Mission? updated = await testDb.Driver.Missions.ReadAsync(staged.Id).ConfigureAwait(false);
                    AssertTrue(updated!.StageOrder == 7,
                        "stage order must survive an update; was " + (updated.StageOrder?.ToString() ?? "null"));

                    Mission unstaged = await h.CreateMissionAsync("Worker", "[Worker] fan-out", null, null, MissionStatusEnum.Pending);
                    Mission? rereadUnstaged = await testDb.Driver.Missions.ReadAsync(unstaged.Id).ConfigureAwait(false);
                    AssertTrue(!rereadUnstaged!.StageOrder.HasValue,
                        "a mission created outside a pipeline stage must persist a null stage order");
                }
            });
        }

        private static Mission Fixture(string voyageId, string? dependsOn, int? stageOrder)
        {
            Mission m = new Mission("fixture", "fixture");
            m.VoyageId = voyageId;
            m.DependsOnMissionId = dependsOn;
            m.StageOrder = stageOrder;
            return m;
        }

        /// <summary>
        /// Minimal wiring for exercising TryAssignAsync against a real database: a vessel, an idle
        /// captain, and a voyage whose missions can be shaped directly.
        /// </summary>
        private sealed class Harness
        {
            public IMissionService Missions { get; private set; } = null!;
            public Vessel Vessel { get; private set; } = null!;

            private TestDatabase _Db = null!;
            private Voyage _Voyage = null!;

            public static async Task<Harness> CreateAsync(TestDatabase testDb)
            {
                LoggingModule logging = new LoggingModule("127.0.0.1", 514, false);
                ArmadaSettings settings = new ArmadaSettings();
                StubGitService git = new StubGitService();

                IDockService docks = new DockService(logging, testDb.Driver, settings, git);
                ICaptainService captains = new CaptainService(logging, testDb.Driver, settings, git, docks);
                captains.OnLaunchAgent = (_, _, _) => Task.FromResult(4242);

                Harness h = new Harness();
                h._Db = testDb;
                h.Missions = new MissionService(logging, testDb.Driver, settings, docks, captains);

                Vessel vessel = new Vessel("barrier-vessel", "https://github.com/test/repo.git");
                vessel.DefaultBranch = "main";
                h.Vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                Captain captain = new Captain("barrier-captain");
                captain.State = CaptainStateEnum.Idle;
                await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);

                Voyage voyage = new Voyage("Barrier voyage", "parallel stage barrier");
                h._Voyage = await testDb.Driver.Voyages.CreateAsync(voyage).ConfigureAwait(false);

                return h;
            }

            public async Task<Mission> CreateMissionAsync(string persona, string title, string? dependsOn, int? stageOrder, MissionStatusEnum status)
            {
                Mission mission = new Mission(title, "barrier fixture");
                mission.VoyageId = _Voyage.Id;
                mission.VesselId = Vessel.Id;
                mission.Persona = persona;
                mission.DependsOnMissionId = dependsOn;
                mission.StageOrder = stageOrder;
                mission.Status = status;
                mission.AssignmentState = MissionAssignmentStateEnum.Pending;
                return await _Db.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);
            }
        }
    }
}
