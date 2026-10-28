namespace Armada.Test.Unit.Suites.Routes
{
    using System;
    using System.IO;
    using System.Linq;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Test.Unit.Suites.Services;
    using Armada.Test.Unit.TestHelpers;
    using TestResourcePressure = global::Test.Shared.Infrastructure.TestResourcePressure;
    using SyslogLogging;

    public partial class MissionRestartParityTests
    {
        private async Task RunResearchReplayAssignmentTestsAsync()
        {
            await RunTest("ResearchReplay_AssignmentUsesExactSourcePreservesOldRefsAndRejectsOldCallbackAfterLaunch", async () =>
            {
                using (SurfaceParityHarness harness = await SurfaceParityHarness.StartAsync().ConfigureAwait(false))
                {
                    string root = Path.Combine(harness.Settings.ReposDirectory, "replay-assignment");
                    Directory.CreateDirectory(root);
                    string source = Path.Combine(root, "source");
                    string bare = Path.Combine(root, "vessel.git");
                    GitServiceTests.SeedTemplateRepository(source);
                    string sourceCommit = (await RunGitAsync(source, "rev-parse", "HEAD").ConfigureAwait(false)).Trim();
                    string defaultBranch = (await RunGitAsync(source, "symbolic-ref", "--short", "HEAD").ConfigureAwait(false)).Trim();
                    await RunGitAsync(source, "config", "user.name", "Armada tests").ConfigureAwait(false);
                    await RunGitAsync(source, "config", "user.email", "armada-tests@example.com").ConfigureAwait(false);
                    await File.WriteAllTextAsync(Path.Combine(source, "later-default.txt"), "Must not enter the replay checkout").ConfigureAwait(false);
                    await RunGitAsync(source, "add", "later-default.txt").ConfigureAwait(false);
                    await RunGitAsync(source, "commit", "-m", "Advance default past the immutable Research input").ConfigureAwait(false);
                    string newerCommit = (await RunGitAsync(source, "rev-parse", "HEAD").ConfigureAwait(false)).Trim();
                    await RunGitAsync(root, "clone", "--bare", source, bare).ConfigureAwait(false);
                    ReplayFixture fixture = await SeedResearchReplayAsync(harness, sourceCommit, bare).ConfigureAwait(false);
                    Vessel vessel = (await harness.Driver.Vessels.ReadAsync(fixture.Stages[0].VesselId!).ConfigureAwait(false))!;
                    vessel.RepoUrl = source; vessel.DefaultBranch = defaultBranch;
                    vessel.WorkingDirectory = Path.Combine(root, "working");
                    await harness.Driver.Vessels.UpdateAsync(vessel).ConfigureAwait(false);
                    foreach (Mission original in fixture.Stages)
                    {
                        original.BranchName = "armada/prior/" + original.Id;
                        await RunGitAsync(bare, "update-ref", "refs/heads/" + original.BranchName, newerCommit).ConfigureAwait(false);
                        await harness.Driver.Missions.UpdateAsync(original).ConfigureAwait(false);
                    }
                    ResearchReplayPlan preview = await harness.Driver.ResearchReplays.ExecuteAsync(Constants.DefaultTenantId,
                        Constants.DefaultUserId, fixture.Voyage.Id, new ResearchReplayRequest { DryRun = true, SourceCommit = sourceCommit }).ConfigureAwait(false);
                    ResearchReplayRequest request = new ResearchReplayRequest
                    { SourceCommit = sourceCommit, RequestId = "real-assignment", ExpectedFingerprint = preview.Fingerprint };
                    ResearchReplayPlan replay = await harness.Driver.ResearchReplays.ExecuteAsync(Constants.DefaultTenantId,
                        Constants.DefaultUserId, fixture.Voyage.Id, request).ConfigureAwait(false);
                    LoggingModule logging = new LoggingModule(); logging.Settings.EnableConsole = false;
                    GitService git = new GitService(logging);
                    DockService docks = new DockService(logging, harness.Driver, harness.Settings, git);
                    CaptainService captains = new CaptainService(logging, harness.Driver, harness.Settings, git, docks);
                    int launches = 0;
                    captains.OnLaunchAgent = (_, _, _) => { launches++; return Task.FromResult(Int32.MaxValue - 30); };
                    MissionService missions = new MissionService(logging, harness.Driver, harness.Settings, docks, captains,
                        git: git, resourcePressureAdmission: TestResourcePressure.Unconstrained(harness.Settings));
                    Captain captain = await harness.Driver.Captains.CreateAsync(new Captain("replay-assignment")
                    { TenantId = Constants.DefaultTenantId, UserId = Constants.DefaultUserId, Model = "claude-opus-5", AllowedPersonas = "[\"Worker\"]" }).ConfigureAwait(false);
                    Mission pending = (await harness.Driver.Missions.ReadAsync(fixture.Stages[0].Id).ConfigureAwait(false))!;
                    AssertTrue(await missions.TryAssignAsync(pending, vessel).ConfigureAwait(false), "The first replayed duty assigns through the real native path");
                    Mission assigned = (await harness.Driver.Missions.ReadAsync(pending.Id).ConfigureAwait(false))!;
                    Dock dock = (await harness.Driver.Docks.ReadAsync(assigned.DockId!).ConfigureAwait(false))!;
                    AssertEqual(sourceCommit, (await RunGitAsync(dock.WorktreePath!, "rev-parse", "HEAD").ConfigureAwait(false)).Trim());
                    AssertContains(replay.ReplayId!, assigned.BranchName!);
                    AssertFalse(File.Exists(Path.Combine(dock.WorktreePath!, "later-default.txt")));
                    AssertEqual(1, launches);
                    Mission next = (await harness.Driver.Missions.ReadAsync(fixture.Stages[1].Id).ConfigureAwait(false))!;
                    AssertFalse(await missions.TryAssignAsync(next, vessel).ConfigureAwait(false), "Independent duties keep their original predecessor dependency");
                    assigned.AgentOutput = "New launched attempt output survives old callbacks.";
                    await harness.Driver.Missions.UpdateAsync(assigned).ConfigureAwait(false);
                    Captain oldCallback = new Captain("prior launch")
                    { Id = captain.Id, TenantId = Constants.DefaultTenantId, CurrentMissionId = assigned.Id, CurrentDockId = "prior-dock" };
                    await missions.HandleCompletionAsync(oldCallback, assigned.Id).ConfigureAwait(false);
                    Mission after = (await harness.Driver.Missions.ReadAsync(assigned.Id).ConfigureAwait(false))!;
                    AssertEqual(assigned.Status, after.Status);
                    AssertEqual(assigned.AgentOutput, after.AgentOutput);
                    AdmiralService lifecycle = new AdmiralService(logging, harness.Driver, harness.Settings, captains, missions,
                        new VoyageService(logging, harness.Driver), docks);
                    await lifecycle.HandleProcessExitAsync(Int32.MaxValue - 31, 0, assigned.CaptainId!, assigned.Id).ConfigureAwait(false);
                    Mission afterOldExit = (await harness.Driver.Missions.ReadAsync(assigned.Id).ConfigureAwait(false))!;
                    AssertEqual(assigned.Status, afterOldExit.Status);
                    AssertEqual(assigned.AgentOutput, afterOldExit.AgentOutput, "An old PID cannot speak for the new launch");

                    ResearchReplayPlan duplicate = await harness.Driver.ResearchReplays.ExecuteAsync(Constants.DefaultTenantId,
                        Constants.DefaultUserId, fixture.Voyage.Id, request).ConfigureAwait(false);
                    AssertEqual(replay.ReplayId, duplicate.ReplayId);
                    AssertEqual(assigned.AgentOutput, (await harness.Driver.Missions.ReadAsync(assigned.Id).ConfigureAwait(false))!.AgentOutput);
                    foreach (Mission original in fixture.Stages)
                        AssertEqual(newerCommit, (await RunGitAsync(bare, "rev-parse", "refs/heads/" + original.BranchName).ConfigureAwait(false)).Trim());
                    AssertEqual(newerCommit, (await RunGitAsync(bare, "rev-parse", "refs/heads/" + defaultBranch).ConfigureAwait(false)).Trim());
                    Mission readyToComplete = (await harness.Driver.Missions.ReadAsync(assigned.Id).ConfigureAwait(false))!;
                    readyToComplete.AgentOutput = String.Join("\n", Enumerable.Range(1, 16).Select(index =>
                        "Finding " + index + ": the immutable source was inspected independently; retained branch references and current review duties remain separate and unchanged."))
                        + "\n[ARMADA:RESULT] COMPLETE";
                    await harness.Driver.Missions.UpdateAsync(readyToComplete).ConfigureAwait(false);
                    await lifecycle.HandleProcessExitAsync(Int32.MaxValue - 30, 0, assigned.CaptainId!, assigned.Id).ConfigureAwait(false);
                    Mission completed = (await harness.Driver.Missions.ReadAsync(assigned.Id).ConfigureAwait(false))!;
                    AssertEqual(MissionStatusEnum.WorkProduced, completed.Status,
                        "The valid current launch produces work before pipeline handoff: " + completed.FailureReason);
                    AssertNull(completed.ProcessId, "Normal process exit releases the current process");
                    AssertNull(completed.FailureReason, "The current report is accepted without a failure");
                    AssertEqual(readyToComplete.AgentOutput, completed.AgentOutput, "The new report remains authoritative");
                    AssertEqual(replay.ReplayId, completed.ResearchReplayId);
                    AssertEqual(1, launches, "Completion does not duplicate the original launch");

                }
            }).ConfigureAwait(false);
        }
    }
}
