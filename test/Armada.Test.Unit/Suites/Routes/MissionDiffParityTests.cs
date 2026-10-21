namespace Armada.Test.Unit.Suites.Routes
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Net.Http;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using System.Threading;
    using System.Threading.Tasks;
    using SyslogLogging;
    using Armada.Core;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Server;
    using Armada.Server.Mcp;
    using Armada.Server.Mcp.Tools;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// A mission diff reads the same on REST, WebSocket and MCP: the saved diff file first, then the diff snapshot
    /// stored on the mission, then the live worktree. A mission whose worktree was reclaimed still shows the diff it
    /// stored at completion on every surface. A mission change summary reads the same on REST and MCP: the stage's
    /// recorded commit pair compared in the vessel's bare repository, with no dock, under the caller's scope and the
    /// shared bounds.
    /// </summary>
    public class MissionDiffParityTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Mission Diff Parity";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("MissionDiff_OnlyAStoredSnapshot_IsReturnedOnEverySurface", async () =>
            {
                using (SurfaceParityHarness harness = await SurfaceParityHarness.StartAsync().ConfigureAwait(false))
                {
                    Mission mission = await harness.Driver.Missions.CreateAsync(new Mission("snapshot diff")
                    {
                        TenantId = Constants.DefaultTenantId,
                        UserId = Constants.DefaultUserId,
                        Status = MissionStatusEnum.Complete,
                        BranchName = "armada/snapshot",
                        DiffSnapshot = "diff --git a/snapshot.txt b/snapshot.txt\n+SNAPSHOT-DIFF-MARKER\n"
                    }).ConfigureAwait(false);

                    foreach (SurfaceReply reply in await ReadDiffAsync(harness, mission.Id).ConfigureAwait(false))
                    {
                        AssertFalse(reply.Refused, "the stored snapshot is readable: " + reply);
                        AssertContains("SNAPSHOT-DIFF-MARKER", reply.Body, "the stored diff snapshot is returned: " + reply);
                    }
                }
            }).ConfigureAwait(false);

            await RunTest("MissionDiff_SavedDiffFile_WinsOverTheSnapshotOnEverySurface", async () =>
            {
                using (SurfaceParityHarness harness = await SurfaceParityHarness.StartAsync().ConfigureAwait(false))
                {
                    Mission mission = await harness.Driver.Missions.CreateAsync(new Mission("saved diff")
                    {
                        TenantId = Constants.DefaultTenantId,
                        UserId = Constants.DefaultUserId,
                        Status = MissionStatusEnum.Complete,
                        DiffSnapshot = "SNAPSHOT-ONLY\n"
                    }).ConfigureAwait(false);
                    harness.WriteLogFile(Path.Combine("diffs", mission.Id + ".diff"), "SAVED-FILE-DIFF\n");

                    foreach (SurfaceReply reply in await ReadDiffAsync(harness, mission.Id).ConfigureAwait(false))
                    {
                        AssertContains("SAVED-FILE-DIFF", reply.Body, "the saved diff file is read first: " + reply);
                        AssertFalse(reply.Body.Contains("SNAPSHOT-ONLY", StringComparison.Ordinal), "the snapshot is not used when a file exists: " + reply);
                    }
                }
            }).ConfigureAwait(false);

            await RunTest("MissionDiff_NoDiffAnywhere_IsRefusedOnEverySurface", async () =>
            {
                using (SurfaceParityHarness harness = await SurfaceParityHarness.StartAsync().ConfigureAwait(false))
                {
                    Mission mission = await harness.Driver.Missions.CreateAsync(new Mission("no diff")
                    {
                        TenantId = Constants.DefaultTenantId,
                        UserId = Constants.DefaultUserId,
                        Status = MissionStatusEnum.Complete
                    }).ConfigureAwait(false);

                    foreach (SurfaceReply reply in await ReadDiffAsync(harness, mission.Id).ConfigureAwait(false))
                    {
                        AssertTrue(reply.Refused, "a mission with no saved diff, snapshot or worktree has no diff: " + reply);
                        AssertContains("No diff available", reply.Body, "the refusal says why: " + reply);
                    }
                }
            }).ConfigureAwait(false);

            await RunTest("MissionChangeSummary_ReadsTheStageCommitsFromTheBareRepositoryWithNoDock_OnRestAndMcp_AndMovingRefsChangesNothing", async () =>
            {
                using (SurfaceParityHarness harness = await SurfaceParityHarness.StartAsync().ConfigureAwait(false))
                using (CommitPairFixture fixture = await CommitPairFixture.CreateAsync().ConfigureAwait(false))
                {
                    UseRealCommitPairReads(harness);
                    Mission mission = await SeedStageAsync(harness, fixture, "no-dock stage").ConfigureAwait(false);
                    Dock dock = (await harness.Driver.Docks.ReadAsync(mission.DockId!).ConfigureAwait(false))!;
                    AssertFalse(Directory.Exists(dock.WorktreePath!), "the stage's dock worktree is gone");
                    HashSet<string> gitPaths = await fixture.GitChangedPathsAsync(fixture.BaseCommit, fixture.StageCommit).ConfigureAwait(false);

                    List<SurfaceReply> first = await ReadChangeSummaryAsync(harness, mission.Id, null).ConfigureAwait(false);
                    foreach (SurfaceReply reply in first)
                        AssertStageSummary(reply, fixture, gitPaths);

                    // Move every branch the repository has; the response must keep its recorded commit pair.
                    await fixture.RunInBareAsync("update-ref", "refs/heads/main", fixture.BaseCommit).ConfigureAwait(false);
                    await fixture.RunInBareAsync("branch", "moved-stage-branch", fixture.BulkCommit).ConfigureAwait(false);
                    List<SurfaceReply> second = await ReadChangeSummaryAsync(harness, mission.Id, null).ConfigureAwait(false);
                    foreach (SurfaceReply reply in second)
                        AssertStageSummary(reply, fixture, gitPaths);
                }
            }).ConfigureAwait(false);

            await RunTest("MissionChangeSummary_MissingEvidenceMissingObjectsAndPathOverflow_AreNamedOnEverySurface_NeverAnEmptyChange", async () =>
            {
                using (SurfaceParityHarness harness = await SurfaceParityHarness.StartAsync().ConfigureAwait(false))
                using (CommitPairFixture fixture = await CommitPairFixture.CreateAsync().ConfigureAwait(false))
                {
                    UseRealCommitPairReads(harness);

                    Mission noStart = await SeedStageAsync(harness, fixture, "no recorded start", recordStart: false).ConfigureAwait(false);
                    foreach (SurfaceReply reply in await ReadChangeSummaryAsync(harness, noStart.Id, null).ConfigureAwait(false))
                    {
                        MissionChangeSummary summary = ParseSummary(reply);
                        AssertFalse(summary.Available, "no recorded start is unavailable: " + reply);
                        AssertEqual(MissionChangeSummary.StartCommitNotRecordedReason, summary.Reason, reply.ToString());
                        AssertNull(summary.StartCommit, "no start is invented from a branch: " + reply);
                        AssertEqual(0, summary.Paths.Count, "an unavailable summary lists no paths: " + reply);
                    }

                    Mission noFinal = await SeedStageAsync(harness, fixture, "no recorded final", finalCommit: null).ConfigureAwait(false);
                    foreach (SurfaceReply reply in await ReadChangeSummaryAsync(harness, noFinal.Id, null).ConfigureAwait(false))
                        AssertEqual(MissionChangeSummary.FinalCommitNotRecordedReason, ParseSummary(reply).Reason, reply.ToString());

                    Mission absentFinal = await SeedStageAsync(harness, fixture, "absent final object", finalCommit: new string('1', 40)).ConfigureAwait(false);
                    foreach (SurfaceReply reply in await ReadChangeSummaryAsync(harness, absentFinal.Id, null).ConfigureAwait(false))
                    {
                        MissionChangeSummary summary = ParseSummary(reply);
                        AssertFalse(summary.Available, "a final commit missing from the repository is unavailable: " + reply);
                        AssertEqual(ChangedPathStatusRead.FinalCommitMissingCode, summary.Reason, reply.ToString());
                    }

                    Mission stage = await SeedStageAsync(harness, fixture, "path overflow").ConfigureAwait(false);
                    foreach (SurfaceReply reply in await ReadChangeSummaryAsync(harness, stage.Id, 2).ConfigureAwait(false))
                    {
                        MissionChangeSummary summary = ParseSummary(reply);
                        AssertTrue(summary.Available, "a path overflow still answers: " + reply);
                        AssertFalse(summary.Complete, "a path overflow is incomplete: " + reply);
                        AssertEqual(ChangedPathStatusRead.PathLimitCode, summary.Reason, reply.ToString());
                        AssertEqual(2, summary.PathCount, reply.ToString());
                        AssertEqual(5, summary.TotalPathCount ?? -1, reply.ToString());
                    }

                    SurfaceReply badLimit = await harness.RestAsync(HttpMethod.Get, "/api/v1/missions/" + stage.Id + "/change-summary?maxPaths=many").ConfigureAwait(false);
                    AssertEqual(400, badLimit.Status, "a non-integer limit is refused: " + badLimit);
                    SurfaceReply unknown = await harness.RestAsync(HttpMethod.Get, "/api/v1/missions/msn_absent/change-summary").ConfigureAwait(false);
                    AssertEqual(404, unknown.Status, "an unknown mission is not found: " + unknown);
                }
            }).ConfigureAwait(false);

            await RunTest("MissionChangeSummary_ChainedStagesAnswerFromTheStartRecordedAtAssignment_AfterTheirDocksAreReclaimed", async () =>
            {
                using (SurfaceParityHarness harness = await SurfaceParityHarness.StartAsync().ConfigureAwait(false))
                using (CommitPairFixture fixture = await CommitPairFixture.CreateAsync().ConfigureAwait(false))
                {
                    UseRealCommitPairReads(harness);
                    MissionService missions = BuildAssigningMissionService(harness);
                    Vessel vessel = await harness.Driver.Vessels.CreateAsync(new Vessel("chained " + Guid.NewGuid().ToString("N"), "https://example.invalid/repo.git")
                    {
                        LocalPath = fixture.BarePath,
                        DefaultBranch = "main"
                    }).ConfigureAwait(false);
                    string branch = "armada/chain-" + Guid.NewGuid().ToString("N");
                    Mission upstream = await harness.Driver.Missions.CreateAsync(new Mission("[Worker] first stage")
                    {
                        TenantId = Constants.DefaultTenantId,
                        UserId = Constants.DefaultUserId,
                        VesselId = vessel.Id,
                        Persona = "Worker",
                        Status = MissionStatusEnum.WorkProduced,
                        BranchName = branch,
                        CommitHash = fixture.StageCommit
                    }).ConfigureAwait(false);
                    HashSet<string> ownPaths = await fixture.GitChangedPathsAsync(fixture.StageCommit, fixture.BulkCommit).ConfigureAwait(false);

                    // Each later stage starts on the first stage's commit; the Judge adds nothing, the others add the bulk commit.
                    Dictionary<string, string> finals = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["Judge"] = fixture.StageCommit,
                        ["Linter"] = fixture.BulkCommit,
                        ["Worker"] = fixture.BulkCommit
                    };
                    foreach (KeyValuePair<string, string> stage in finals)
                    {
                        harness.Git.HeadCommitHashResult = fixture.StageCommit;
                        Mission ended = await AssignAndEndStageAsync(harness, missions, vessel, upstream, branch, stage.Key, stage.Value).ConfigureAwait(false);

                        Dock reclaimed = (await harness.Driver.Docks.ReadAsync(ended.DockId!).ConfigureAwait(false))!;
                        AssertNull(reclaimed.GitAnchorsSnapshot, stage.Key + ": the reclaim cleared the dock's own anchor snapshot");
                        AssertFalse(File.Exists(Path.Combine(harness.Settings.LogDirectory, "docks", reclaimed.Id + ".start")), stage.Key + ": the reclaim removed the dock start record");

                        foreach (SurfaceReply reply in await ReadChangeSummaryAsync(harness, ended.Id, null).ConfigureAwait(false))
                        {
                            MissionChangeSummary summary = ParseSummary(reply);
                            AssertTrue(summary.Available, stage.Key + " answers after its dock is reclaimed: " + reply);
                            AssertTrue(summary.Complete, reply.ToString());
                            AssertEqual(fixture.StageCommit, summary.StartCommit, stage.Key + " starts where its dock sat at assignment: " + reply);
                            AssertEqual(MissionStageStartCommit.StageStartRecordSource, summary.StartCommitSource, reply.ToString());
                            HashSet<string> listed = new HashSet<string>(summary.Paths.Select(entry => entry.Path), StringComparer.Ordinal);
                            HashSet<string> expected = stage.Value == fixture.StageCommit ? new HashSet<string>(StringComparer.Ordinal) : ownPaths;
                            AssertTrue(expected.SetEquals(listed), stage.Key + " lists only its own changes, never the first stage's: " + reply);
                        }
                    }

                    // A dock that names no full commit records no start, so the stage says so instead of guessing one.
                    harness.Git.HeadCommitHashResult = "abc123def456";
                    Mission unknown = await AssignAndEndStageAsync(harness, missions, vessel, upstream, branch, "Linter", fixture.BulkCommit).ConfigureAwait(false);
                    foreach (SurfaceReply reply in await ReadChangeSummaryAsync(harness, unknown.Id, null).ConfigureAwait(false))
                    {
                        MissionChangeSummary summary = ParseSummary(reply);
                        AssertFalse(summary.Available, "an unknown start is unavailable: " + reply);
                        AssertEqual(MissionChangeSummary.StartCommitNotRecordedReason, summary.Reason, reply.ToString());
                        AssertNull(summary.StartCommit, "no start commit is guessed: " + reply);
                    }
                }
            }).ConfigureAwait(false);

            await RunTest("MissionChangeSummary_MissionCredentialReadsOnlyItsOwnMission_AndOtherOwnersAndTenantsAreRefused", async () =>
            {
                using (SurfaceParityHarness harness = await SurfaceParityHarness.StartAsync().ConfigureAwait(false))
                using (CommitPairFixture fixture = await CommitPairFixture.CreateAsync().ConfigureAwait(false))
                {
                    UseRealCommitPairReads(harness);
                    Mission own = await SeedStageAsync(harness, fixture, "own stage").ConfigureAwait(false);
                    Mission other = await SeedStageAsync(harness, fixture, "other stage", finalCommit: fixture.BulkCommit).ConfigureAwait(false);

                    AuthContext captain = AuthContext.Authenticated(Constants.DefaultTenantId, Constants.DefaultUserId, true, true, "Bearer");
                    captain.MissionId = own.Id;
                    AssertTrue(McpToolAccessPolicy.IsAllowed(captain, McpMissionChangeSummaryTools.ToolName), "a mission credential may call the change summary");

                    string token = harness.CreateMissionToken(Constants.DefaultTenantId, Constants.DefaultUserId, own.Id);
                    SurfaceReply rest = await harness.RestAsync(HttpMethod.Get, "/api/v1/missions/" + other.Id + "/change-summary", null, token).ConfigureAwait(false);
                    MissionChangeSummary restSummary = ParseSummary(rest);
                    AssertEqual(own.Id, restSummary.MissionId, "a mission token reads its own mission over REST: " + rest);
                    AssertEqual(fixture.StageCommit, restSummary.FinalCommit, "the other mission's commit is never read: " + rest);
                    AssertTrue(restSummary.Warnings.Any(warning => warning.Contains("was ignored", StringComparison.Ordinal)), "the answer says the supplied id was ignored: " + rest);

                    Dictionary<string, Func<JsonElement?, Task<object>>> raw = new Dictionary<string, Func<JsonElement?, Task<object>>>(StringComparer.Ordinal);
                    McpMissionChangeSummaryTools.Register((name, _, _, handler) => { raw[name] = handler; }, harness.Driver, harness.Settings, harness.Git);
                    MissionChangeSummary mcpSummary = await CallRawAsync(raw, captain, new { missionId = other.Id }).ConfigureAwait(false);
                    AssertEqual(own.Id, mcpSummary.MissionId, "an administrator-owned mission credential still reads only its own mission over MCP");
                    AssertEqual(fixture.StageCommit, mcpSummary.FinalCommit);

                    AuthContext sameTenantUser = AuthContext.Authenticated(Constants.DefaultTenantId, "usr_someone_else", false, false, "Bearer");
                    MissionChangeSummary refusedUser = await CallRawAsync(raw, sameTenantUser, new { missionId = own.Id }).ConfigureAwait(false);
                    AssertEqual(MissionChangeSummary.MissionNotFoundReason, refusedUser.Reason, "a same-tenant caller who may not read the mission is refused");
                    AssertNull(refusedUser.FinalCommit, "a refused read names no commit");

                    AuthContext otherTenant = AuthContext.Authenticated("ten_elsewhere", "usr_elsewhere", false, true, "Bearer");
                    MissionChangeSummary refusedTenant = await CallRawAsync(raw, otherTenant, new { missionId = own.Id }).ConfigureAwait(false);
                    AssertEqual(MissionChangeSummary.MissionNotFoundReason, refusedTenant.Reason, "a cross-tenant administrator of another tenant is refused");
                    AssertEqual(0, refusedTenant.Paths.Count);

                    AuthContext foreignCaptain = AuthContext.Authenticated("ten_elsewhere", "usr_elsewhere", true, true, "Bearer");
                    foreignCaptain.MissionId = own.Id;
                    MissionChangeSummary refusedForeignCaptain = await CallRawAsync(raw, foreignCaptain, new { missionId = own.Id }).ConfigureAwait(false);
                    AssertEqual(MissionChangeSummary.MissionNotFoundReason, refusedForeignCaptain.Reason, "a mission credential whose owner does not own the mission reads nothing");
                }
            }).ConfigureAwait(false);

            await RunTest("MissionChangeSummary_DeadlineConcurrencyAndCallBudget_AreBounded_AndCallerCancellationPropagates", async () =>
            {
                using (SurfaceParityHarness harness = await SurfaceParityHarness.StartAsync().ConfigureAwait(false))
                using (CommitPairFixture fixture = await CommitPairFixture.CreateAsync().ConfigureAwait(false))
                {
                    Mission mission = await SeedStageAsync(harness, fixture, "bounded stage").ConfigureAwait(false);
                    AuthContext captain = AuthContext.Authenticated(Constants.DefaultTenantId, Constants.DefaultUserId, false, false, "Bearer");
                    captain.MissionId = mission.Id;

                    harness.Git.CommitPairRead = async (repo, fromCommit, toCommit, max, timeout, limit, token) =>
                    {
                        await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
                        return ChangedPathStatusRead.FromEntries(new List<ChangedPathEntry>(), 1);
                    };
                    MissionChangeSummaryBounds deadlineBounds = new MissionChangeSummaryBounds { Deadline = TimeSpan.FromMilliseconds(200) };
                    MissionChangeSummary late = await MissionChangeSummaryReader.ReadAsync(harness.Driver, harness.Settings.LogDirectory, harness.Git,
                        captain, null, null, deadlineBounds).ConfigureAwait(false);
                    AssertFalse(late.Available, "a read past its deadline is unavailable");
                    AssertEqual(ChangedPathStatusRead.TimeoutCode, late.Reason);

                    using (CancellationTokenSource cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
                    {
                        await AssertThrowsAsync<OperationCanceledException>(() => MissionChangeSummaryReader.ReadAsync(harness.Driver, harness.Settings.LogDirectory,
                            harness.Git, captain, null, null, new MissionChangeSummaryBounds(), null, cancellation.Token),
                            "the caller's cancellation propagates instead of becoming a timeout").ConfigureAwait(false);
                    }

                    TaskCompletionSource<bool> entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    TaskCompletionSource<bool> release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    harness.Git.CommitPairRead = async (repo, fromCommit, toCommit, max, timeout, limit, token) =>
                    {
                        entered.TrySetResult(true);
                        await release.Task.ConfigureAwait(false);
                        return ChangedPathStatusRead.FromEntries(new List<ChangedPathEntry>(), 1);
                    };
                    AuthContext operatorCaller = McpTestCaller.Operator;
                    MissionChangeSummaryBounds oneSlot = new MissionChangeSummaryBounds(1) { ConcurrencyWait = TimeSpan.Zero };
                    Task<MissionChangeSummary> holder = MissionChangeSummaryReader.ReadAsync(harness.Driver, harness.Settings.LogDirectory, harness.Git,
                        operatorCaller, mission.Id, null, oneSlot);
                    await entered.Task.ConfigureAwait(false);
                    MissionChangeSummary busy = await MissionChangeSummaryReader.ReadAsync(harness.Driver, harness.Settings.LogDirectory, harness.Git,
                        operatorCaller, mission.Id, null, oneSlot).ConfigureAwait(false);
                    AssertEqual(MissionChangeSummary.BusyReason, busy.Reason, "a read past the concurrency bound answers busy");
                    release.TrySetResult(true);
                    MissionChangeSummary held = await holder.ConfigureAwait(false);
                    AssertTrue(held.Available, "the read that held the slot completes");

                    harness.Git.CommitPairRead = (repo, fromCommit, toCommit, max, timeout, limit, token) =>
                        Task.FromResult(ChangedPathStatusRead.FromEntries(new List<ChangedPathEntry>(), 1));
                    MissionChangeSummaryBounds oneCall = new MissionChangeSummaryBounds { MaxCallsPerMission = 1 };
                    MissionChangeSummary firstCall = await MissionChangeSummaryReader.ReadAsync(harness.Driver, harness.Settings.LogDirectory, harness.Git,
                        captain, null, null, oneCall).ConfigureAwait(false);
                    AssertTrue(firstCall.Available, "the first call is inside the budget");
                    MissionChangeSummary secondCall = await MissionChangeSummaryReader.ReadAsync(harness.Driver, harness.Settings.LogDirectory, harness.Git,
                        captain, null, null, oneCall).ConfigureAwait(false);
                    AssertEqual(MissionChangeSummary.BudgetReason, secondCall.Reason, "a mission credential past its call budget is refused");
                    AssertEqual(2, secondCall.CallsUsed ?? -1);
                }
            }).ConfigureAwait(false);
        }

        private const string _UseStageCommit = "use-stage-commit";

        private static readonly JsonSerializerOptions _SummaryJson = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };

        private static MissionService BuildAssigningMissionService(SurfaceParityHarness harness)
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            CaptainService captains = new CaptainService(logging, harness.Driver, harness.Settings, harness.Git, harness.Docks);
            captains.OnLaunchAgent = (_, _, _) => Task.FromResult(54321);
            return new MissionService(logging, harness.Driver, harness.Settings, harness.Docks, captains,
                resourcePressureAdmission: global::Test.Shared.Infrastructure.TestResourcePressure.Unconstrained(harness.Settings));
        }

        // A later stage assigned through the real assignment path on its own captain, ended at a recorded final
        // commit, and its dock reclaimed the way a finished stage's dock is.
        private async Task<Mission> AssignAndEndStageAsync(
            SurfaceParityHarness harness,
            MissionService missions,
            Vessel vessel,
            Mission upstream,
            string branch,
            string persona,
            string finalCommit)
        {
            await harness.Driver.Captains.CreateAsync(new Captain(persona.ToLowerInvariant() + "-" + Guid.NewGuid().ToString("N").Substring(0, 8))
            {
                State = CaptainStateEnum.Idle
            }).ConfigureAwait(false);
            Mission stage = await harness.Driver.Missions.CreateAsync(new Mission("[" + persona + "] later stage")
            {
                TenantId = Constants.DefaultTenantId,
                UserId = Constants.DefaultUserId,
                VesselId = vessel.Id,
                Persona = persona,
                DependsOnMissionId = upstream.Id,
                BranchName = branch,
                Status = MissionStatusEnum.Pending
            }).ConfigureAwait(false);

            AssertTrue(await missions.TryAssignAsync(stage, vessel).ConfigureAwait(false), persona + " is assigned through the assignment path");
            Mission assigned = (await harness.Driver.Missions.ReadAsync(stage.Id).ConfigureAwait(false))!;
            AssertNotNull(assigned.DockId, persona + " has a dock");

            assigned.Status = MissionStatusEnum.Complete;
            assigned.CommitHash = finalCommit;
            assigned.CompletedUtc = DateTime.UtcNow;
            await harness.Driver.Missions.UpdateAsync(assigned).ConfigureAwait(false);
            await harness.Docks.ReclaimAsync(assigned.DockId!).ConfigureAwait(false);
            return assigned;
        }

        private static void UseRealCommitPairReads(SurfaceParityHarness harness)
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            GitService real = new GitService(logging);
            harness.Git.CommitPairRead = real.ReadChangedPathStatusesBetweenCommitsAsync;
        }

        // A finished stage whose dock was reclaimed: the dock record keeps its anchor snapshot naming the
        // provisioned commit, its worktree no longer exists, and the vessel's bare repository holds both commits.
        private static async Task<Mission> SeedStageAsync(
            SurfaceParityHarness harness,
            CommitPairFixture fixture,
            string title,
            bool recordStart = true,
            string? finalCommit = _UseStageCommit)
        {
            Vessel vessel = await harness.Driver.Vessels.CreateAsync(new Vessel(title + " " + Guid.NewGuid().ToString("N"), "https://example.invalid/repo.git")
            {
                LocalPath = fixture.BarePath
            }).ConfigureAwait(false);
            Mission mission = new Mission(title)
            {
                TenantId = Constants.DefaultTenantId,
                UserId = Constants.DefaultUserId,
                VesselId = vessel.Id,
                Status = MissionStatusEnum.Complete,
                BranchName = "armada/stage-" + Guid.NewGuid().ToString("N"),
                CommitHash = String.Equals(finalCommit, _UseStageCommit, StringComparison.Ordinal) ? fixture.StageCommit : finalCommit
            };
            if (recordStart)
            {
                Dock dock = new Dock(vessel.Id)
                {
                    WorktreePath = Path.Combine(fixture.Root, "reclaimed-dock-" + Guid.NewGuid().ToString("N")),
                    BranchName = mission.BranchName,
                    Active = false
                };
                dock.GitAnchorsSnapshot = new DockGitAnchorSnapshot
                {
                    DockId = dock.Id,
                    MissionId = mission.Id,
                    VesselId = vessel.Id,
                    ProvisionedCommit = fixture.BaseCommit,
                    ProvisionedUtc = DateTime.UtcNow,
                    Anchors = new GitAnchors { BaseCommit = fixture.BaseCommit }
                };
                dock = await harness.Driver.Docks.CreateAsync(dock).ConfigureAwait(false);
                mission.DockId = dock.Id;
            }
            return await harness.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);
        }

        private static async Task<List<SurfaceReply>> ReadChangeSummaryAsync(SurfaceParityHarness harness, string missionId, int? maxPaths)
        {
            string query = maxPaths.HasValue ? "?maxPaths=" + maxPaths.Value : String.Empty;
            object args = maxPaths.HasValue ? (object)new { missionId = missionId, maxPaths = maxPaths.Value } : new { missionId = missionId };
            return new List<SurfaceReply>
            {
                await harness.RestAsync(HttpMethod.Get, "/api/v1/missions/" + missionId + "/change-summary" + query).ConfigureAwait(false),
                await harness.McpAsync(McpMissionChangeSummaryTools.ToolName, args).ConfigureAwait(false)
            };
        }

        private static MissionChangeSummary ParseSummary(SurfaceReply reply)
        {
            MissionChangeSummary? summary = JsonSerializer.Deserialize<MissionChangeSummary>(reply.Body, _SummaryJson);
            if (summary == null) throw new InvalidOperationException("No change summary in " + reply);
            return summary;
        }

        private void AssertStageSummary(SurfaceReply reply, CommitPairFixture fixture, HashSet<string> gitPaths)
        {
            AssertFalse(reply.Refused, "the change summary is readable: " + reply);
            MissionChangeSummary summary = ParseSummary(reply);
            AssertTrue(summary.Available, "the stage's commits are compared: " + reply);
            AssertTrue(summary.Complete, "every path is listed: " + reply);
            AssertNull(summary.Reason, "a complete summary carries no reason: " + reply);
            AssertEqual(fixture.BaseCommit, summary.StartCommit, "the start is the dock's provisioned commit: " + reply);
            AssertEqual(MissionStageStartCommit.DockAnchorSnapshotSource, summary.StartCommitSource, reply.ToString());
            AssertEqual(fixture.StageCommit, summary.FinalCommit, "the final commit is the recorded one: " + reply);
            AssertEqual(5, summary.TotalPathCount ?? -1, reply.ToString());
            HashSet<string> listed = new HashSet<string>(StringComparer.Ordinal);
            foreach (ChangedPathEntry entry in summary.Paths)
            {
                listed.Add(entry.Path);
                if (entry.PreviousPath != null) listed.Add(entry.PreviousPath);
            }
            AssertTrue(gitPaths.SetEquals(listed), "the listed paths are exactly git's: " + reply);
            AssertTrue(summary.Paths.Any(entry => entry.Status == ChangedPathStatusEnum.Renamed
                && entry.PreviousPath == "old/name.txt" && entry.Path == "new/name.txt"), "the rename is one entry: " + reply);
            AssertTrue(summary.Paths.Any(entry => entry.Status == ChangedPathStatusEnum.Added && entry.Path == CommitPairFixture.UnicodePath), "the non-ASCII path keeps its characters: " + reply);
            AssertTrue(summary.Paths.Any(entry => entry.Status == ChangedPathStatusEnum.Added && entry.Path == CommitPairFixture.CaseDistinctPath), "the case-distinct path is its own entry: " + reply);
            AssertTrue(summary.Paths.Any(entry => entry.Status == ChangedPathStatusEnum.Deleted && entry.Path == "remove.txt"), reply.ToString());
        }

        private static async Task<MissionChangeSummary> CallRawAsync(Dictionary<string, Func<JsonElement?, Task<object>>> handlers, AuthContext caller, object args)
        {
            using (McpCallerContext.Begin(caller))
            {
                object result = await handlers[McpMissionChangeSummaryTools.ToolName](JsonSerializer.SerializeToElement(args)).ConfigureAwait(false);
                return (MissionChangeSummary)result;
            }
        }

        private static async Task<List<SurfaceReply>> ReadDiffAsync(SurfaceParityHarness harness, string missionId)
        {
            return new List<SurfaceReply>
            {
                await harness.RestAsync(HttpMethod.Get, "/api/v1/missions/" + missionId + "/diff").ConfigureAwait(false),
                await harness.WebSocketAsync("get_mission_diff", missionId).ConfigureAwait(false),
                await harness.McpAsync("armada_get_mission_diff", new { missionId = missionId }).ConfigureAwait(false)
            };
        }
    }
}
