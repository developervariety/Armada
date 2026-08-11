namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database.Sqlite;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// Covers the stage-lag hardening that advances the shared mission branch ref to a completed
    /// pipeline stage's produced commit before the downstream stage is assigned. A detached-head
    /// stage (for example PortingReferenceAnalyst) commits on a detached HEAD whose work only
    /// reaches refs/armada-preserved/&lt;branch&gt;, never the mission branch ref; without this
    /// hardening the next attached stage cuts its dock from the stale branch head and silently
    /// loses the prior stage's source-fidelity work.
    /// </summary>
    public class MissionHandoffBranchAdvanceTests : TestSuite
    {
        public override string Name => "Mission Handoff Branch Advance";

        private const string _Branch = "armada/stage-advance/msn_upstream";
        private const string _ProducedCommit = "abcdef0123456789abcdef0123456789abcdef01";

        private LoggingModule CreateLogging()
        {
            return new LoggingModule();
        }

        private ArmadaSettings CreateSettings()
        {
            ArmadaSettings settings = new ArmadaSettings();
            settings.ReposDirectory = Path.Combine(Path.GetTempPath(), "armada-test-repos-" + Guid.NewGuid().ToString("N"));
            return settings;
        }

        private async Task<Vessel> CreateVesselAsync(SqliteDatabaseDriver db, ArmadaSettings settings)
        {
            Vessel vessel = new Vessel("branch-advance-vessel", "https://github.com/test/branch-advance.git");
            vessel.LocalPath = Path.Combine(settings.ReposDirectory, vessel.Name + ".git");
            vessel.DefaultBranch = "main";
            return await db.Vessels.CreateAsync(vessel).ConfigureAwait(false);
        }

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("Handoff_DetachedStage_AdvancesSharedBranchToProducedCommit", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = CreateSettings();
                    LoggingModule logging = CreateLogging();
                    StubGitService git = new StubGitService();
                    git.HeadCommitHashResult = _ProducedCommit;
                    IDockService dockService = new DockService(logging, testDb.Driver, settings, git);
                    CaptainService captainService = new CaptainService(logging, testDb.Driver, settings, git, dockService);
                    captainService.OnLaunchAgent = (_, _, _) => Task.FromResult(64010);
                    MissionService missions = new MissionService(logging, testDb.Driver, settings, dockService, captainService, git: git);

                    Vessel vessel = await CreateVesselAsync(testDb.Driver, settings).ConfigureAwait(false);
                    Directory.CreateDirectory(Path.GetDirectoryName(vessel.LocalPath!)!);

                    // The completed analyst stage shares the mission branch but its dock is detached:
                    // its produced commit only exists in the dock worktree HEAD, not on the branch.
                    Mission analyst = new Mission("[PortingReferenceAnalyst] Review", "Analyst work.");
                    analyst.VesselId = vessel.Id;
                    analyst.Persona = "PortingReferenceAnalyst";
                    analyst.BranchName = _Branch;
                    analyst.Status = MissionStatusEnum.WorkProduced;
                    analyst.AgentOutput = "Analyst reviewed the diff.";
                    analyst = await testDb.Driver.Missions.CreateAsync(analyst).ConfigureAwait(false);

                    string worktreePath = Path.Combine(Path.GetTempPath(), "armada-dock-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(worktreePath);
                    Dock dock = new Dock(vessel.Id)
                    {
                        BranchName = _Branch,
                        WorktreePath = worktreePath,
                        Active = true,
                    };
                    dock = await testDb.Driver.Docks.CreateAsync(dock).ConfigureAwait(false);
                    analyst.DockId = dock.Id;
                    await testDb.Driver.Missions.UpdateAsync(analyst).ConfigureAwait(false);

                    // The dependent TestEngineer waits on the analyst.
                    Mission testEngineer = new Mission("TestEngineer stage", "Original TestEngineer brief.");
                    testEngineer.VesselId = vessel.Id;
                    testEngineer.Persona = "TestEngineer";
                    testEngineer.Status = MissionStatusEnum.Pending;
                    testEngineer.DependsOnMissionId = analyst.Id;
                    testEngineer.AssignmentState = MissionAssignmentStateEnum.WaitingForDependency;
                    testEngineer = await testDb.Driver.Missions.CreateAsync(testEngineer).ConfigureAwait(false);

                    Captain captain = new Captain("te-captain");
                    captain.Model = "claude-opus-5";
                    captain.AllowedPersonas = "[\"TestEngineer\"]";
                    captain.State = CaptainStateEnum.Idle;
                    captain = await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);

                    bool assigned = await missions.TryAssignAsync(testEngineer, vessel).ConfigureAwait(false);

                    AssertTrue(assigned, "The missed handoff must self-heal and assign the dependent.");
                    AssertEqual(1, git.ForceUpdateBranchRefCalls.Count, "The shared branch ref must be advanced to the analyst's produced commit exactly once.");
                    AssertTrue(
                        git.ForceUpdateBranchRefCalls[0].Contains(_ProducedCommit),
                        "The branch must advance to the dock HEAD produced commit, got: " + git.ForceUpdateBranchRefCalls[0]);

                    try
                    {
                        if (Directory.Exists(worktreePath)) Directory.Delete(worktreePath, recursive: true);
                    }
                    catch
                    {
                    }
                }
            }).ConfigureAwait(false);

            await RunTest("Handoff_NoDock_DoesNotThrowAndLeavesBranchUntouched", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = CreateSettings();
                    LoggingModule logging = CreateLogging();
                    StubGitService git = new StubGitService();
                    IDockService dockService = new DockService(logging, testDb.Driver, settings, git);
                    CaptainService captainService = new CaptainService(logging, testDb.Driver, settings, git, dockService);
                    captainService.OnLaunchAgent = (_, _, _) => Task.FromResult(64011);
                    MissionService missions = new MissionService(logging, testDb.Driver, settings, dockService, captainService, git: git);

                    Vessel vessel = await CreateVesselAsync(testDb.Driver, settings).ConfigureAwait(false);
                    Directory.CreateDirectory(Path.GetDirectoryName(vessel.LocalPath!)!);

                    // A completed stage whose dock is already reclaimed must not throw; the handoff
                    // proceeds and the branch is left alone.
                    Mission worker = new Mission("[Worker] Implement", "Worker work.");
                    worker.VesselId = vessel.Id;
                    worker.Persona = "Worker";
                    worker.BranchName = _Branch;
                    worker.Status = MissionStatusEnum.WorkProduced;
                    worker = await testDb.Driver.Missions.CreateAsync(worker).ConfigureAwait(false);

                    Mission judge = new Mission("Judge stage", "Original Judge brief.");
                    judge.VesselId = vessel.Id;
                    judge.Persona = "Judge";
                    judge.Status = MissionStatusEnum.Pending;
                    judge.DependsOnMissionId = worker.Id;
                    judge.AssignmentState = MissionAssignmentStateEnum.WaitingForDependency;
                    judge = await testDb.Driver.Missions.CreateAsync(judge).ConfigureAwait(false);

                    Captain captain = new Captain("judge-captain");
                    captain.Model = "claude-opus-5";
                    captain.AllowedPersonas = "[\"Judge\"]";
                    captain.State = CaptainStateEnum.Idle;
                    captain = await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);

                    bool assigned = await missions.TryAssignAsync(judge, vessel).ConfigureAwait(false);

                    AssertTrue(assigned, "The missed handoff must self-heal and assign the dependent even when the upstream dock is gone.");
                    AssertEqual(0, git.ForceUpdateBranchRefCalls.Count, "With no upstream dock, the branch must not be advanced.");
                    AssertEqual(0, git.PushCalls.Count, "With no upstream dock, no push must be attempted.");
                }
            }).ConfigureAwait(false);
        }
    }
}
