namespace Armada.Test.Unit.TestHelpers
{
    using System;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// A voyage whose planner split its plan into two lines of work. Lane A is a Worker, an
    /// optional TestEngineer and a Judge on one branch; lane B is a Worker and a Judge on another.
    /// Lane B's Worker is the most recently updated stage, so a rule that reads "the latest commit
    /// of the voyage" sees lane B's commit while lane A's Judge reviews lane A's.
    /// </summary>
    internal sealed class FanOutVoyageFixture
    {
        #region Public-Members

        /// <summary>The planner commit both lanes start from.</summary>
        public const string PlanCommit = "c0c0c0c0aaaa0000000000000000000000000000";

        /// <summary>Lane A's first commit (its Worker).</summary>
        public const string LaneAWorkerCommit = "a0a0a0a0aaaa0000000000000000000000000000";

        /// <summary>Lane A's reviewed commit (its last committing stage).</summary>
        public const string LaneACommit = "a1a1a1a1aaaa1111111111111111111111111111";

        /// <summary>Lane B's reviewed commit.</summary>
        public const string LaneBCommit = "b1b1b1b1bbbb1111111111111111111111111111";

        /// <summary>The planner branch.</summary>
        public const string PlanBranch = "armada/example/plan";

        /// <summary>Lane A's branch.</summary>
        public const string LaneABranch = "armada/example/lane-a";

        /// <summary>Lane B's branch.</summary>
        public const string LaneBBranch = "armada/example/lane-b";

        /// <summary>The vessel.</summary>
        public Vessel Vessel { get; private set; } = null!;

        /// <summary>The voyage.</summary>
        public Voyage Voyage { get; private set; } = null!;

        /// <summary>The planner stage both lanes depend on.</summary>
        public Mission Architect { get; private set; } = null!;

        /// <summary>Lane A's Worker.</summary>
        public Mission LaneAWorker { get; private set; } = null!;

        /// <summary>Lane A's TestEngineer, or null when the fixture was built without it.</summary>
        public Mission? LaneATester { get; private set; }

        /// <summary>Lane A's Judge.</summary>
        public Mission LaneAJudge { get; private set; } = null!;

        /// <summary>Lane B's Worker.</summary>
        public Mission LaneBWorker { get; private set; } = null!;

        /// <summary>Lane B's Judge.</summary>
        public Mission LaneBJudge { get; private set; } = null!;

        #endregion

        #region Public-Methods

        /// <summary>
        /// Create the fixture.
        /// </summary>
        /// <param name="testDb">The test database.</param>
        /// <param name="laneATester">When true, lane A runs a TestEngineer that commits on top of its
        /// Worker, so lane A has an older commit of its own.</param>
        /// <param name="laneBWorkerStatus">Lane B's Worker status; InProgress means lane B can still commit.</param>
        /// <returns>The fixture.</returns>
        public static async Task<FanOutVoyageFixture> CreateAsync(
            TestDatabase testDb,
            bool laneATester = false,
            MissionStatusEnum laneBWorkerStatus = MissionStatusEnum.WorkProduced)
        {
            FanOutVoyageFixture fixture = new FanOutVoyageFixture();
            DateTime start = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            Vessel vessel = new Vessel("fan-out-vessel", "https://github.com/test/repo.git");
            vessel.DefaultBranch = "main";
            fixture.Vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

            Voyage voyage = new Voyage("fan-out-voyage");
            voyage.Status = VoyageStatusEnum.InProgress;
            fixture.Voyage = await testDb.Driver.Voyages.CreateAsync(voyage).ConfigureAwait(false);

            fixture.Architect = await StageAsync(testDb, fixture, "Architect", MissionStatusEnum.WorkProduced, null, PlanBranch, PlanCommit, start).ConfigureAwait(false);

            fixture.LaneAWorker = await StageAsync(testDb, fixture, "Worker", MissionStatusEnum.WorkProduced, fixture.Architect.Id,
                LaneABranch, laneATester ? LaneAWorkerCommit : LaneACommit, start.AddMinutes(10)).ConfigureAwait(false);
            Mission laneAParent = fixture.LaneAWorker;
            if (laneATester)
            {
                fixture.LaneATester = await StageAsync(testDb, fixture, "TestEngineer", MissionStatusEnum.WorkProduced, fixture.LaneAWorker.Id,
                    LaneABranch, LaneACommit, start.AddMinutes(20)).ConfigureAwait(false);
                laneAParent = fixture.LaneATester;
            }

            fixture.LaneBWorker = await StageAsync(testDb, fixture, "Worker", laneBWorkerStatus, fixture.Architect.Id,
                LaneBBranch, laneBWorkerStatus == MissionStatusEnum.InProgress ? null : LaneBCommit, start.AddMinutes(40)).ConfigureAwait(false);

            fixture.LaneAJudge = await StageAsync(testDb, fixture, "Judge", MissionStatusEnum.InProgress, laneAParent.Id,
                LaneABranch, LaneACommit, start.AddMinutes(30)).ConfigureAwait(false);
            fixture.LaneAJudge.AgentOutput = "review body\n[ARMADA:VERDICT] PASS";
            fixture.LaneAJudge = await testDb.Driver.Missions.UpdateAsync(fixture.LaneAJudge).ConfigureAwait(false);

            fixture.LaneBJudge = await StageAsync(testDb, fixture, "Judge",
                laneBWorkerStatus == MissionStatusEnum.InProgress ? MissionStatusEnum.Pending : MissionStatusEnum.InProgress,
                fixture.LaneBWorker.Id, LaneBBranch, laneBWorkerStatus == MissionStatusEnum.InProgress ? null : LaneBCommit,
                start.AddMinutes(35)).ConfigureAwait(false);
            fixture.LaneBJudge.AgentOutput = "review body\n[ARMADA:VERDICT] PASS";
            fixture.LaneBJudge = await testDb.Driver.Missions.UpdateAsync(fixture.LaneBJudge).ConfigureAwait(false);

            return fixture;
        }

        /// <summary>
        /// Attach a voyage-armed Check record that measured a commit on a branch.
        /// </summary>
        /// <param name="testDb">The test database.</param>
        /// <param name="type">The Check type.</param>
        /// <param name="status">The record status.</param>
        /// <param name="branch">The branch the record measured; null for an unstamped record.</param>
        /// <param name="commit">The commit the record measured; null for an unstamped record.</param>
        /// <returns>The record.</returns>
        public async Task<CheckRun> AddVoyageCheckAsync(
            TestDatabase testDb, CheckRunTypeEnum type, CheckRunStatusEnum status, string? branch, string? commit)
        {
            CheckRun run = new CheckRun
            {
                VesselId = Vessel.Id,
                VoyageId = Voyage.Id,
                Type = type,
                Source = CheckRunSourceEnum.Armada,
                Status = status,
                Label = type.ToString() + " (armed at dispatch)",
                Command = commit == null ? "echo" : "dotnet test",
                BranchName = branch,
                CommitHash = commit,
                ExitCode = status == CheckRunStatusEnum.Passed ? 0 : status == CheckRunStatusEnum.Failed ? 1 : null,
                Output = status == CheckRunStatusEnum.Passed ? "Passed!" : status == CheckRunStatusEnum.Failed ? "Failed!" : null
            };
            return await testDb.Driver.CheckRuns.CreateAsync(run).ConfigureAwait(false);
        }

        #endregion

        #region Private-Methods

        private static async Task<Mission> StageAsync(
            TestDatabase testDb,
            FanOutVoyageFixture fixture,
            string persona,
            MissionStatusEnum status,
            string? dependsOn,
            string? branch,
            string? commit,
            DateTime at)
        {
            Mission mission = new Mission("[" + persona + "] fan-out stage", "description");
            mission.VesselId = fixture.Vessel.Id;
            mission.VoyageId = fixture.Voyage.Id;
            mission.Persona = persona;
            mission.Status = status;
            mission.DependsOnMissionId = dependsOn;
            mission.BranchName = branch;
            mission.CommitHash = commit;
            mission.CreatedUtc = at;
            mission = await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);
            // The driver may stamp times at creation; pin the intended ordering explicitly.
            mission.CreatedUtc = at;
            mission.LastUpdateUtc = at;
            return await testDb.Driver.Missions.UpdateAsync(mission).ConfigureAwait(false);
        }

        #endregion
    }
}
