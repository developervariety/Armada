namespace Armada.Test.Unit.Suites.Services
{
    using System.Text.Json;
    using Armada.Core;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Server;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    public class RemoteControlQueryServiceTests : TestSuite
    {
        public override string Name => "Remote Control Query Service";

        protected override async Task RunTestsAsync()
        {
            await RunTest("InstanceSummary BundlesHealthStatusAndRecentCollections", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = CreateSettings();
                    RemoteControlQueryService service = CreateService(testDb.Driver, settings);

                    Captain captain = new Captain("summary-captain");
                    captain.State = CaptainStateEnum.Working;
                    captain.Model = "gpt-5.4";
                    captain = await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);

                    Voyage voyage = new Voyage("summary-voyage");
                    voyage.Status = VoyageStatusEnum.InProgress;
                    voyage = await testDb.Driver.Voyages.CreateAsync(voyage).ConfigureAwait(false);

                    Mission mission = new Mission("summary-mission");
                    mission.CaptainId = captain.Id;
                    mission.VoyageId = voyage.Id;
                    mission.Persona = "Worker";
                    mission.Status = MissionStatusEnum.InProgress;
                    mission = await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                    ArmadaEvent evt = new ArmadaEvent("mission.started", "Mission started");
                    evt.MissionId = mission.Id;
                    evt.CaptainId = captain.Id;
                    evt.VoyageId = voyage.Id;
                    await testDb.Driver.Events.CreateAsync(evt).ConfigureAwait(false);

                    RemoteTunnelRequestResult result = await service.HandleAsync(
                        RemoteTunnelProtocol.CreateRequest("armada.instance.summary", null),
                        CancellationToken.None).ConfigureAwait(false);

                    string json = JsonSerializer.Serialize(result.Payload, RemoteTunnelProtocol.JsonOptions);
                    AssertEqual(200, result.StatusCode);
                    AssertContains("summary-captain", json);
                    AssertContains("summary-voyage", json);
                    AssertContains("summary-mission", json);
                    AssertContains("mission.started", json);
                    AssertContains("\"healthy\"", json);
                }
            });

            await RunTest("MissionDetail IncludesRelatedEntitiesAndDock", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = CreateSettings();
                    RemoteControlQueryService service = CreateService(testDb.Driver, settings);

                    Vessel vessel = new Vessel("detail-vessel", "https://github.com/example/repo.git");
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Voyage voyage = new Voyage("detail-voyage");
                    voyage = await testDb.Driver.Voyages.CreateAsync(voyage).ConfigureAwait(false);

                    Captain captain = new Captain("detail-captain");
                    captain = await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);

                    Dock dock = new Dock(vessel.Id);
                    dock.CaptainId = captain.Id;
                    dock.BranchName = "feature/detail";
                    dock.WorktreePath = Path.Combine(Path.GetTempPath(), "armada_remote_detail_" + Guid.NewGuid().ToString("N"));
                    await testDb.Driver.Docks.CreateAsync(dock).ConfigureAwait(false);

                    Mission mission = new Mission("detail-mission");
                    mission.CaptainId = captain.Id;
                    mission.VoyageId = voyage.Id;
                    mission.VesselId = vessel.Id;
                    mission.DockId = dock.Id;
                    mission.BranchName = dock.BranchName;
                    mission = await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                    RemoteTunnelRequestResult result = await service.HandleAsync(
                        RemoteTunnelProtocol.CreateRequest("armada.mission.detail", new RemoteTunnelQueryRequest { MissionId = mission.Id }),
                        CancellationToken.None).ConfigureAwait(false);

                    string json = JsonSerializer.Serialize(result.Payload, RemoteTunnelProtocol.JsonOptions);
                    AssertEqual(200, result.StatusCode);
                    AssertContains("detail-mission", json);
                    AssertContains("detail-captain", json);
                    AssertContains("detail-voyage", json);
                    AssertContains("detail-vessel", json);
                    AssertContains("feature/detail", json);
                }
            });

            await RunTest("MissionLog ReadsRequestedSlice", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = CreateSettings();
                    Directory.CreateDirectory(Path.Combine(settings.LogDirectory, "missions"));

                    Mission mission = new Mission("log-mission");
                    mission = await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);
                    string logPath = Path.Combine(settings.LogDirectory, "missions", mission.Id + ".log");
                    await File.WriteAllTextAsync(logPath, "line-1\nline-2\nline-3").ConfigureAwait(false);

                    RemoteControlQueryService service = CreateService(testDb.Driver, settings);
                    RemoteTunnelRequestResult result = await service.HandleAsync(
                        RemoteTunnelProtocol.CreateRequest("armada.mission.log", new RemoteTunnelQueryRequest
                        {
                            MissionId = mission.Id,
                            Offset = 1,
                            Lines = 2
                        }),
                        CancellationToken.None).ConfigureAwait(false);

                    string json = JsonSerializer.Serialize(result.Payload, RemoteTunnelProtocol.JsonOptions);
                    AssertEqual(200, result.StatusCode);
                    AssertContains("line-2", json);
                    AssertContains("line-3", json);
                    AssertFalse(json.Contains("line-1"), "Offset should skip the first line");
                }
            });

            await RunTest("MissionDiff UsesSavedSnapshotWhenNoLiveDockExists", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = CreateSettings();
                    RemoteControlQueryService service = CreateService(testDb.Driver, settings);

                    Mission mission = new Mission("diff-mission");
                    mission.BranchName = "feature/diff";
                    mission.DiffSnapshot = "diff --git a/file.txt b/file.txt";
                    mission = await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                    RemoteTunnelRequestResult result = await service.HandleAsync(
                        RemoteTunnelProtocol.CreateRequest("armada.mission.diff", new RemoteTunnelQueryRequest { MissionId = mission.Id }),
                        CancellationToken.None).ConfigureAwait(false);

                    string json = JsonSerializer.Serialize(result.Payload, RemoteTunnelProtocol.JsonOptions);
                    AssertEqual(200, result.StatusCode);
                    AssertContains("savedSnapshot", json);
                    AssertContains("diff --git a/file.txt b/file.txt", json);
                    AssertContains("feature/diff", json);
                }
            });

            await RunTest("CaptainLog ResolvesPointerFile", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = CreateSettings();
                    string captainsLogDir = Path.Combine(settings.LogDirectory, "captains");
                    Directory.CreateDirectory(captainsLogDir);

                    Captain captain = new Captain("captain-log");
                    captain = await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);

                    string actualLogPath = Path.Combine(captainsLogDir, captain.Id + ".session.log");
                    await File.WriteAllTextAsync(actualLogPath, "captain-a\ncaptain-b\ncaptain-c").ConfigureAwait(false);
                    await File.WriteAllTextAsync(Path.Combine(captainsLogDir, captain.Id + ".current"), actualLogPath).ConfigureAwait(false);

                    RemoteControlQueryService service = CreateService(testDb.Driver, settings);
                    RemoteTunnelRequestResult result = await service.HandleAsync(
                        RemoteTunnelProtocol.CreateRequest("armada.captain.log", new RemoteTunnelQueryRequest
                        {
                            CaptainId = captain.Id,
                            Lines = 2
                        }),
                        CancellationToken.None).ConfigureAwait(false);

                    string json = JsonSerializer.Serialize(result.Payload, RemoteTunnelProtocol.JsonOptions);
                    AssertEqual(200, result.StatusCode);
                    AssertContains("captain-a", json);
                    AssertContains("captain-b", json);
                }
            });
        }

        private static ArmadaSettings CreateSettings()
        {
            ArmadaSettings settings = new ArmadaSettings();
            settings.LogDirectory = Path.Combine(Path.GetTempPath(), "armada_remote_logs_" + Guid.NewGuid().ToString("N"));
            settings.AdmiralPort = 7890;
            settings.McpPort = 7891;
            settings.WebSocketPort = 7892;
            return settings;
        }

        private static RemoteControlQueryService CreateService(DatabaseDriver database, ArmadaSettings settings)
        {
            return new RemoteControlQueryService(
                database,
                settings,
                new StubGitService(),
                token => Task.FromResult(new ArmadaStatus
                {
                    ActiveVoyages = 1,
                    WorkingCaptains = 1
                }),
                () => new RemoteTunnelStatus
                {
                    Enabled = true,
                    State = RemoteTunnelStateEnum.Connected,
                    InstanceId = "armada-test",
                    LatencyMs = 42
                },
                new DateTime(2026, 4, 3, 12, 0, 0, DateTimeKind.Utc));
        }

        private class StubGitService : IGitService
        {
            public Task<string> DiffAsync(string worktreePath, string baseBranch = "main", CancellationToken token = default) => Task.FromResult("diff --git a/live.txt b/live.txt");
            public Task CloneBareAsync(string repoUrl, string localPath, CancellationToken token = default) { throw new NotImplementedException(); }
            public Task CreateWorktreeAsync(string repoPath, string worktreePath, string branchName, string baseBranch = "main", CancellationToken token = default) { throw new NotImplementedException(); }
            public Task RemoveWorktreeAsync(string worktreePath, CancellationToken token = default) { throw new NotImplementedException(); }
            public Task FetchAsync(string repoPath, CancellationToken token = default) { throw new NotImplementedException(); }
            public Task PushBranchAsync(string worktreePath, string remoteName = "origin", CancellationToken token = default) { throw new NotImplementedException(); }
            public Task<string> CreatePullRequestAsync(string worktreePath, string title, string body, CancellationToken token = default) { throw new NotImplementedException(); }
            public Task RepairWorktreeAsync(string worktreePath, CancellationToken token = default) { throw new NotImplementedException(); }
            public Task<bool> IsRepositoryAsync(string path, CancellationToken token = default) { throw new NotImplementedException(); }
            public Task DeleteLocalBranchAsync(string repoPath, string branchName, CancellationToken token = default) { throw new NotImplementedException(); }
            public Task DeleteRemoteBranchAsync(string repoPath, string branchName, CancellationToken token = default) { throw new NotImplementedException(); }
            public Task PruneWorktreesAsync(string repoPath, CancellationToken token = default) { throw new NotImplementedException(); }
            public Task EnableAutoMergeAsync(string worktreePath, string prUrl, CancellationToken token = default) { throw new NotImplementedException(); }
            public Task MergeBranchLocalAsync(string targetWorkDir, string sourceRepoPath, string branchName, string? targetBranch = null, string? commitMessage = null, CancellationToken token = default) { throw new NotImplementedException(); }
            public Task PullAsync(string workingDirectory, CancellationToken token = default) { throw new NotImplementedException(); }
            public Task<string?> GetHeadCommitHashAsync(string worktreePath, CancellationToken token = default) { throw new NotImplementedException(); }
            public Task<IReadOnlyList<string>> GetChangedFilesSinceAsync(string worktreePath, string startCommit, CancellationToken token = default) { throw new NotImplementedException(); }
            public Task<bool> IsPrMergedAsync(string workingDirectory, string prUrl, CancellationToken token = default) { throw new NotImplementedException(); }
            public Task<bool> BranchExistsAsync(string repoPath, string branchName, CancellationToken token = default) { throw new NotImplementedException(); }
            public Task<bool> EnsureLocalBranchAsync(string repoPath, string branchName, CancellationToken token = default) { throw new NotImplementedException(); }
            public Task<bool> IsWorktreeRegisteredAsync(string repoPath, string worktreePath, CancellationToken token = default) { throw new NotImplementedException(); }
        }
    }
}
