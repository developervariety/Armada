namespace Armada.Test.Unit.Suites.Routes
{
    using System;
    using System.Collections.Generic;
    using System.Data.Common;
    using System.IO;
    using System.Linq;
    using System.Net.Http;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Database.Sqlite;
    using Armada.Core.Enums;
    using Armada.Core.Harbor;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Server;
    using Armada.Server.Mcp;
    using Armada.Test.Unit.Suites.Services;
    using Armada.Test.Unit.TestHelpers;
    using Microsoft.Data.Sqlite;
    using SyslogLogging;

    public partial class MissionRestartParityTests
    {
        private static readonly JsonSerializerOptions ReplayReplyJson = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        private async Task RunResearchReplayTestsAsync()
        {
            await RunResearchReplayAssignmentTestsAsync().ConfigureAwait(false);
            await RunTest("ResearchReplay_ArchivesAllAttemptsAndIdempotentlyResetsSameSixIdentities", async () =>
            {
                using (SurfaceParityHarness harness = await SurfaceParityHarness.StartAsync().ConfigureAwait(false))
                {
                    string source = Path.Combine(harness.Settings.ReposDirectory, "research-source");
                    GitServiceTests.SeedTemplateRepository(source);
                    string commit = (await RunGitAsync(source, "rev-parse", "HEAD").ConfigureAwait(false)).Trim();
                    ReplayFixture fixture = await SeedResearchReplayAsync(harness, commit, source).ConfigureAwait(false);
                    string protectedValue = "sentinel-" + Guid.NewGuid().ToString("N");
                    Vessel sourceVessel = (await harness.Driver.Vessels.ReadAsync(fixture.Stages[0].VesselId!).ConfigureAwait(false))!;
                    sourceVessel.GitHubTokenOverride = protectedValue;
                    await harness.Driver.Vessels.UpdateAsync(sourceVessel).ConfigureAwait(false);
                    string instructions = Path.Combine(harness.Settings.LogDirectory, "instructions");
                    Directory.CreateDirectory(instructions);
                    await File.WriteAllTextAsync(Path.Combine(instructions, fixture.Stages[0].Id + ".CODEX.md"),
                        "Original full launch instructions retained before any replay. é🙂").ConfigureAwait(false);
                    SurfaceReply previewReply = await harness.RestAsync(HttpMethod.Post,
                        "/api/v1/voyages/" + fixture.Voyage.Id + "/research-replay", new { dryRun = true, sourceCommit = commit }).ConfigureAwait(false);
                    AssertEqual(200, previewReply.Status, previewReply.Body);
                    ResearchReplayPlan preview = JsonSerializer.Deserialize<ResearchReplayPlan>(previewReply.Body, ReplayReplyJson)!;
                    using (McpCallerContext.Begin(AuthContext.Authenticated(Constants.DefaultTenantId, Constants.DefaultUserId, true, true, "test")))
                    {
                        SurfaceReply mcp = await harness.McpAsync("armada_replay_research_voyage",
                            new { voyageId = fixture.Voyage.Id, sourceCommit = commit, dryRun = true }).ConfigureAwait(false);
                        AssertEqual(200, mcp.Status, mcp.Body);
                        AssertEqual(preview.Fingerprint, JsonSerializer.Deserialize<ResearchReplayPlan>(mcp.Body, ReplayReplyJson)!.Fingerprint,
                            "REST and MCP preview the same complete expected state");
                    }
                    ResearchReplayRequest request = new ResearchReplayRequest
                    { SourceCommit = commit, ExpectedFingerprint = preview.Fingerprint, RequestId = "retained-attempt-r1" };
                    SurfaceReply appliedReply = await harness.RestAsync(HttpMethod.Post,
                        "/api/v1/voyages/" + fixture.Voyage.Id + "/research-replay", request).ConfigureAwait(false);
                    AssertEqual(200, appliedReply.Status, appliedReply.Body);
                    ResearchReplayPlan applied = JsonSerializer.Deserialize<ResearchReplayPlan>(appliedReply.Body, ReplayReplyJson)!;
                    AssertTrue(applied.Applied);
                    AssertNotNull(applied.ReplayId);
                    using (SqliteConnection archiveConnection = new SqliteConnection(harness.Database.ConnectionString))
                    {
                        await archiveConnection.OpenAsync().ConfigureAwait(false);
                        using (SqliteCommand archive = archiveConnection.CreateCommand())
                        {
                            archive.CommandText = "SELECT snapshot_json FROM research_replays WHERE id=@id;";
                            archive.Parameters.AddWithValue("@id", applied.ReplayId);
                            string snapshot = (string)(await archive.ExecuteScalarAsync().ConfigureAwait(false))!;
                            AssertFalse(snapshot.Contains(protectedValue, StringComparison.Ordinal), "Vessel credentials never enter the attempt archive");
                            AssertFalse(snapshot.Contains("github_token_override", StringComparison.OrdinalIgnoreCase));
                            AssertContains("Original full launch instructions retained before any replay.", snapshot,
                                "Prior delivered instructions survive replacement of their live filename");
                        }
                    }
                    List<Mission> current = await harness.Driver.Missions.EnumerateByVoyageAsync(fixture.Voyage.Id).ConfigureAwait(false);
                    AssertEqual(6, current.Count, "The replay adds no missions or substitute stages");
                    AssertEqual(VoyageStatusEnum.InProgress, (await harness.Driver.Voyages.ReadAsync(fixture.Voyage.Id).ConfigureAwait(false))!.Status);
                    using (SqliteDatabaseDriver reopened = new SqliteDatabaseDriver(harness.Database.ConnectionString, new LoggingModule()))
                    {
                        await reopened.InitializeAsync().ConfigureAwait(false);
                        foreach (Mission original in fixture.Stages)
                        {
                            Mission reset = current.Single(m => m.Id == original.Id);
                            AssertEqual(MissionStatusEnum.Pending, reset.Status);
                            AssertEqual(original.DependsOnMissionId, reset.DependsOnMissionId);
                            AssertEqual(original.RecoveryAttempts, reset.RecoveryAttempts, "Manual replay does not reset or consume autonomous budgets");
                            AssertEqual(original.Description, reset.Description);
                            AssertEqual(commit, reset.StartFromRef);
                            AssertEqual(applied.ReplayId, reset.ResearchReplayId);
                            AssertNull(reset.AgentOutput);
                            AssertFalse(MissionOutputArtifact.Build(reset).Complete, "Old finalized output is not current evidence");
                            Mission prior = (await reopened.ResearchReplays.ReadPriorMissionAsync(Constants.DefaultTenantId,
                                Constants.DefaultUserId, applied.ReplayId!, original.Id).ConfigureAwait(false))!;
                            AssertEqual(original.AgentOutput, prior.AgentOutput, "Full Unicode report survives database reopen");
                            AssertEqual(original.Status, prior.Status);
                            AssertEqual(original.FailureReason, prior.FailureReason);
                            AssertEqual(original.StartedUtc, prior.StartedUtc);
                            AssertEqual(original.CompletedUtc, prior.CompletedUtc, "The durable archive retains exact completion ticks");
                            AssertEqual(original.CommitHash, prior.CommitHash);
                            AssertEqual("Read the complete independent-duty contract.", prior.PlaybookSnapshots.Single().Content);
                        }
                        SurfaceReply history = await harness.RestAsync(HttpMethod.Get,
                            "/api/v1/research-replays/" + applied.ReplayId + "/missions/" + fixture.Stages[5].Id + "/output", null).ConfigureAwait(false);
                        AssertEqual(200, history.Status, history.Body);
                        MissionOutputArtifactPage page = JsonSerializer.Deserialize<MissionOutputArtifactPage>(history.Body, ReplayReplyJson)!;
                        AssertEqual(fixture.Stages[5].AgentOutput, page.Content);
                        AssertTrue(page.Finalized && !page.HasMore);
                        AssertEqual(MissionStatusEnum.Failed, page.PriorResearchAttempt!.Status);
                        // Watson REST responses use microsecond precision; the archive above keeps all ticks.
                        DateTime wireCompleted = new DateTime(fixture.Stages[5].CompletedUtc!.Value.Ticks / 10 * 10, DateTimeKind.Utc);
                        AssertEqual(wireCompleted, page.PriorResearchAttempt.CompletedUtc);
                        AssertNull(page.PriorResearchAttempt.PreviousReplayId);
                        string legacyLog = Path.Combine(harness.Settings.LogDirectory, "missions", fixture.Stages[0].Id + ".log");
                        await File.WriteAllTextAsync(legacyLog, "[ARMADA:RESULT] COMPLETE prior attempt only").ConfigureAwait(false);
                        string currentLog = MissionAttemptArtifacts.LogPath(harness.Settings.LogDirectory, current.Single(m => m.Id == fixture.Stages[0].Id));
                        await File.WriteAllTextAsync(currentLog, "New attempt activity only.").ConfigureAwait(false);
                        SurfaceReply currentLogReply = await harness.RestAsync(HttpMethod.Get, "/api/v1/missions/" + fixture.Stages[0].Id + "/log", null).ConfigureAwait(false);
                        AssertContains("New attempt activity only", currentLogReply.Body);
                        AssertFalse(currentLogReply.Body.Contains("COMPLETE prior", StringComparison.Ordinal));
                        AssertEqual("[ARMADA:RESULT] COMPLETE prior attempt only", await File.ReadAllTextAsync(legacyLog).ConfigureAwait(false));
                        Mission logMission = current.Single(m => m.Id == fixture.Stages[0].Id);
                        string? originalLogCaptain = logMission.CaptainId;
                        logMission.CaptainId = "stale-pointer-captain";
                        string pointers = Path.Combine(harness.Settings.LogDirectory, "captains"); Directory.CreateDirectory(pointers);
                        await File.WriteAllTextAsync(Path.Combine(pointers, logMission.CaptainId + ".current"), legacyLog).ConfigureAwait(false);
                        string? tail = await new CaptainLogTailReader(harness.Settings).ReadTailAsync(logMission, 20, CancellationToken.None).ConfigureAwait(false);
                        AssertEqual("New attempt activity only.", tail, "A stale captain pointer cannot select an old attempt's log");
                        logMission.CaptainId = originalLogCaptain;


                        string historyToken = harness.CreateMissionToken(Constants.DefaultTenantId, Constants.DefaultUserId, fixture.Stages[1].Id);
                        SurfaceReply scopedHistory = await harness.RestAsync(HttpMethod.Get,
                            "/api/v1/research-replays/" + applied.ReplayId + "/missions/" + fixture.Stages[5].Id + "/output", null, historyToken).ConfigureAwait(false);
                        AssertEqual(200, scopedHistory.Status, scopedHistory.Body);
                        AuthContext missionCaller = AuthContext.Authenticated(Constants.DefaultTenantId, Constants.DefaultUserId, false, false, "test");
                        missionCaller.MissionId = fixture.Stages[1].Id;
                        using (McpCallerContext.Begin(missionCaller))
                        {
                            AssertTrue(McpToolAccessPolicy.IsAllowed(missionCaller, "armada_research_replay_output"));
                            AssertFalse(McpToolAccessPolicy.IsAllowed(missionCaller, "armada_replay_research_voyage"));
                            object scopedMcp = await harness.RawMcp["armada_research_replay_output"](JsonSerializer.SerializeToElement(new
                            { replayId = applied.ReplayId, missionId = fixture.Stages[5].Id, offset = 0, length = 12 })).ConfigureAwait(false);
                            MissionOutputArtifactPage scopedPage = (MissionOutputArtifactPage)scopedMcp;
                            AssertEqual(page.Sha256, scopedPage.Sha256);
                            AssertTrue(scopedPage.HasMore);
                            ReplayFixture stillFailed = await SeedResearchReplayAsync(harness, commit).ConfigureAwait(false);
                            object refusedApply = await harness.RawMcp["armada_replay_research_voyage"](JsonSerializer.SerializeToElement(new
                            { voyageId = stillFailed.Voyage.Id, sourceCommit = commit, dryRun = true })).ConfigureAwait(false);
                            AssertContains("global operator authority", JsonSerializer.Serialize(refusedApply),
                                "An otherwise-valid failed chain is refused because the caller is mission-scoped");
                        }
                        ReplayFixture other = await SeedResearchReplayAsync(harness, commit).ConfigureAwait(false);
                        string otherToken = harness.CreateMissionToken(Constants.DefaultTenantId, Constants.DefaultUserId, other.Stages[0].Id);
                        AssertEqual(403, (await harness.RestAsync(HttpMethod.Get,
                            "/api/v1/research-replays/" + applied.ReplayId + "/missions/" + fixture.Stages[5].Id + "/output", null, otherToken).ConfigureAwait(false)).Status);


                        Mission first = current.Single(m => m.Id == fixture.Stages[0].Id);
                        first.AgentOutput = "New attempt work must survive an idempotent retry.";
                        await harness.Driver.Missions.UpdateAsync(first).ConfigureAwait(false);
                        SurfaceReply repeated = await harness.RestAsync(HttpMethod.Post,
                            "/api/v1/voyages/" + fixture.Voyage.Id + "/research-replay", request).ConfigureAwait(false);
                        AssertEqual(200, repeated.Status, repeated.Body);
                        AssertEqual(applied.ReplayId, JsonSerializer.Deserialize<ResearchReplayPlan>(repeated.Body, ReplayReplyJson)!.ReplayId);
                        AssertEqual(first.AgentOutput, (await harness.Driver.Missions.ReadAsync(first.Id).ConfigureAwait(false))!.AgentOutput);
                        bool staleWriteRefused = false;
                        fixture.Stages[0].AgentOutput = "A delayed old attempt must not overwrite new work.";
                        try { await harness.Driver.Missions.UpdateAsync(fixture.Stages[0]).ConfigureAwait(false); }
                        catch (InvalidOperationException) { staleWriteRefused = true; }
                        AssertTrue(staleWriteRefused, "The persisted replay generation fences stale full-row writers");
                        AssertEqual(first.AgentOutput, (await harness.Driver.Missions.ReadAsync(first.Id).ConfigureAwait(false))!.AgentOutput);
                        request.SourceCommit = new string('b', 40);
                        AssertEqual(409, (await harness.RestAsync(HttpMethod.Post,
                            "/api/v1/voyages/" + fixture.Voyage.Id + "/research-replay", request).ConfigureAwait(false)).Status,
                            "A reused key with different inputs conflicts instead of resetting another attempt");

                        MissionService completions = new MissionService(new LoggingModule(), harness.Driver, harness.Settings,
                            harness.Docks, new CaptainService(new LoggingModule(), harness.Driver, harness.Settings, harness.Git, harness.Docks));
                        await completions.HandleCompletionAsync(new Captain("old-launch")
                        { TenantId = Constants.DefaultTenantId, CurrentMissionId = first.Id, CurrentDockId = "old-dock" }, first.Id).ConfigureAwait(false);
                        AssertEqual(MissionStatusEnum.Pending, (await harness.Driver.Missions.ReadAsync(first.Id).ConfigureAwait(false))!.Status,
                            "A callback carried by the old launch cannot finalize the replayed pending attempt");
                        // Finish a later attempt, then prove its current roles can still reach the first archive.
                        foreach (Mission later in await harness.Driver.Missions.EnumerateByVoyageAsync(fixture.Voyage.Id).ConfigureAwait(false))
                        {
                            later.Status = later.Id == fixture.Stages[5].Id ? MissionStatusEnum.Failed : MissionStatusEnum.Complete;
                            later.AgentOutput = "Full second-attempt " + later.Persona + " report.";
                            later.CommitHash = commit;
                            later.StartedUtc = DateTime.UtcNow.AddMinutes(-5); later.CompletedUtc = DateTime.UtcNow;
                            later.ProcessId = null;
                            await harness.Driver.Missions.UpdateAsync(later).ConfigureAwait(false);
                        }
                        Voyage endedAgain = (await harness.Driver.Voyages.ReadAsync(fixture.Voyage.Id).ConfigureAwait(false))!;
                        endedAgain.Status = VoyageStatusEnum.Failed; endedAgain.CompletedUtc = DateTime.UtcNow;
                        await harness.Driver.Voyages.UpdateAsync(endedAgain).ConfigureAwait(false);
                        ResearchReplayPlan nextPreview = await harness.Driver.ResearchReplays.ExecuteAsync(Constants.DefaultTenantId,
                            Constants.DefaultUserId, fixture.Voyage.Id, new ResearchReplayRequest { DryRun = true, SourceCommit = commit }).ConfigureAwait(false);
                        ResearchReplayPlan nextReplay = await harness.Driver.ResearchReplays.ExecuteAsync(Constants.DefaultTenantId,
                            Constants.DefaultUserId, fixture.Voyage.Id, new ResearchReplayRequest
                            { SourceCommit = commit, RequestId = "retained-attempt-r2", ExpectedFingerprint = nextPreview.Fingerprint }).ConfigureAwait(false);
                        AssertTrue(nextReplay.ReplayId != applied.ReplayId);
                        AssertTrue(nextReplay.PriorStages.All(stage => stage.PreviousReplayId == applied.ReplayId));
                        using (McpCallerContext.Begin(missionCaller))
                        {
                            MissionOutputArtifactPage oldest = (MissionOutputArtifactPage)await harness.RawMcp["armada_research_replay_output"](
                                JsonSerializer.SerializeToElement(new { replayId = applied.ReplayId, missionId = fixture.Stages[5].Id })).ConfigureAwait(false);
                            AssertEqual(page.Sha256, oldest.Sha256, "A later generation can read every retained same-voyage failed attempt");
                            AssertEqual(fixture.Stages[5].AgentOutput, oldest.Content);
                        }
                        SurfaceReply noCurrentLog = await harness.RestAsync(HttpMethod.Get,
                            "/api/v1/missions/" + fixture.Stages[0].Id + "/log", null).ConfigureAwait(false);
                        AssertFalse(noCurrentLog.Body.Contains("New attempt activity", StringComparison.Ordinal), "No prior-generation log fallback");
                        AssertFalse(noCurrentLog.Body.Contains("COMPLETE prior", StringComparison.Ordinal));

                    }
                }
            }).ConfigureAwait(false);

            await RunTest("ResearchReplay_LaterResetStorageFailureRollsBackArchiveAndAllSixStages", async () =>
            {
                using (SurfaceParityHarness harness = await SurfaceParityHarness.StartAsync().ConfigureAwait(false))
                {
                    ReplayFixture fixture = await SeedResearchReplayAsync(harness, _ProducedCommit).ConfigureAwait(false);
                    ResearchReplayPlan preview = await harness.Driver.ResearchReplays.ExecuteAsync(Constants.DefaultTenantId,
                        Constants.DefaultUserId, fixture.Voyage.Id, new ResearchReplayRequest { DryRun = true, SourceCommit = _ProducedCommit }).ConfigureAwait(false);
                    using (SqliteConnection connection = new SqliteConnection(harness.Database.ConnectionString))
                    {
                        await connection.OpenAsync().ConfigureAwait(false);
                        using (SqliteCommand command = connection.CreateCommand())
                        {
                            command.CommandText = "CREATE TRIGGER reject_later_replay_reset BEFORE UPDATE OF status ON missions WHEN OLD.id='"
                                + fixture.Stages[4].Id.Replace("'", "''") + "' AND NEW.status='Pending' BEGIN SELECT RAISE(ABORT,'intentional later reset failure'); END;";
                            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
                        }
                        bool failed = false;
                        try
                        {
                            await harness.Driver.ResearchReplays.ExecuteAsync(Constants.DefaultTenantId, Constants.DefaultUserId,
                                fixture.Voyage.Id, new ResearchReplayRequest { SourceCommit = _ProducedCommit,
                                    RequestId = "fail-after-four-resets", ExpectedFingerprint = preview.Fingerprint }).ConfigureAwait(false);
                        }
                        catch (DbException) { failed = true; }
                        AssertTrue(failed, "The real SQLite write failure reaches the transaction boundary");
                        foreach (Mission original in fixture.Stages)
                        {
                            Mission current = (await harness.Driver.Missions.ReadAsync(original.Id).ConfigureAwait(false))!;
                            AssertEqual(original.Status, current.Status);
                            AssertEqual(original.AgentOutput, current.AgentOutput);
                            AssertNull(current.ResearchReplayId);
                        }
                        AssertEqual(VoyageStatusEnum.Failed, (await harness.Driver.Voyages.ReadAsync(fixture.Voyage.Id).ConfigureAwait(false))!.Status);
                        using (SqliteCommand count = connection.CreateCommand())
                        {
             count.CommandText = "SELECT COUNT(*) FROM research_replays;";
                            AssertEqual(0L, Convert.ToInt64(await count.ExecuteScalarAsync().ConfigureAwait(false)), "No partial archive commits");
                        }
                    }
                }
            }).ConfigureAwait(false);

            await RunTest("ResearchReplay_ConcurrentIdenticalRequestsCommitOneArchiveAndKeepCurrentOutputSeparate", async () =>
            {
                using (SurfaceParityHarness harness = await SurfaceParityHarness.StartAsync().ConfigureAwait(false))
                {
                    ReplayFixture fixture = await SeedResearchReplayAsync(harness, _ProducedCommit).ConfigureAwait(false);
                    ResearchReplayPlan preview = await harness.Driver.ResearchReplays.ExecuteAsync(Constants.DefaultTenantId,
                        Constants.DefaultUserId, fixture.Voyage.Id, new ResearchReplayRequest { DryRun = true, SourceCommit = _ProducedCommit }).ConfigureAwait(false);
                    TaskCompletionSource<bool> ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    int entered = 0;
                    async Task<ResearchReplayPlan> ApplyAsync()
                    {
                        if (Interlocked.Increment(ref entered) == 2) ready.TrySetResult(true);
                        await ready.Task.ConfigureAwait(false);
                        return await harness.Driver.ResearchReplays.ExecuteAsync(Constants.DefaultTenantId, Constants.DefaultUserId,
                            fixture.Voyage.Id, new ResearchReplayRequest { SourceCommit = _ProducedCommit,
                                ExpectedFingerprint = preview.Fingerprint, RequestId = "same-concurrent-request" }).ConfigureAwait(false);
                    }
                    ResearchReplayPlan[] results = await Task.WhenAll(Task.Run(ApplyAsync), Task.Run(ApplyAsync)).ConfigureAwait(false);
                    AssertEqual(results[0].ReplayId, results[1].ReplayId, "Both callers read the one committed operation");
                    using (SqliteConnection connection = new SqliteConnection(harness.Database.ConnectionString))
                    {
                        await connection.OpenAsync().ConfigureAwait(false);
                        using (SqliteCommand count = connection.CreateCommand())
                        {
             count.CommandText = "SELECT COUNT(*) FROM research_replays;";
                            AssertEqual(1L, Convert.ToInt64(await count.ExecuteScalarAsync().ConfigureAwait(false)));
                            AssertEqual(6, (await harness.Driver.Missions.EnumerateByVoyageAsync(fixture.Voyage.Id).ConfigureAwait(false)).Count);
                        }
                    }
                }
            }).ConfigureAwait(false);

            await RunTest("ResearchReplay_RefusesStalePlansForeignScopesAndInvalidOriginalDutiesWithoutReset", async () =>
            {
                using (SurfaceParityHarness harness = await SurfaceParityHarness.StartAsync().ConfigureAwait(false))
                {
                    foreach (string boundary in new[] { "mode", "order", "source", "truncated", "hold", "extra", "foreign-user", "foreign-vessel", "foreign-tenant", "stale", "barrier", "orphan-harbor", "process" })
                    {
                        ReplayFixture fixture = await SeedResearchReplayAsync(harness, _ProducedCommit).ConfigureAwait(false);
                        ResearchReplayPlan preview = await harness.Driver.ResearchReplays.ExecuteAsync(Constants.DefaultTenantId,
                            Constants.DefaultUserId, fixture.Voyage.Id, new ResearchReplayRequest { DryRun = true, SourceCommit = _ProducedCommit }).ConfigureAwait(false);
                        Mission changed = fixture.Stages[1];
                        if (boundary == "mode") changed.Mode = MissionModeEnum.Implementation;
                        if (boundary == "order") changed.DependsOnMissionId = fixture.Stages[3].Id;
                        if (boundary == "process") { changed.ProcessId = 123456; changed.ProcessStartedUtc = DateTime.UtcNow; }
                        if (boundary == "source") changed.CommitHash = new string('b', 40);
                        if (boundary == "truncated") changed.AgentOutput = MissionOutputArtifact.StreamTruncationMarker + "\nretained tail";
                        if (boundary == "hold") { changed.HeldForOperatorReview = true; changed.HeldForOperatorReviewReason = "Foreign operator hold"; }
                        if (boundary == "stale") changed.AgentOutput += "\nA later correction invalidates the older preview.";
                        if (boundary == "extra")
                            await harness.Driver.Missions.CreateAsync(new Mission("Unrelated extra stage")
                            { TenantId = Constants.DefaultTenantId, UserId = Constants.DefaultUserId, VoyageId = fixture.Voyage.Id,
                                VesselId = changed.VesselId, Status = MissionStatusEnum.Complete }).ConfigureAwait(false);
                        if (boundary == "foreign-vessel")
                        {
                            Vessel foreignVessel = (await harness.Driver.Vessels.ReadAsync(changed.VesselId!).ConfigureAwait(false))!;
                            UserMaster foreignUser = await harness.Driver.Users.CreateAsync(new UserMaster(Constants.DefaultTenantId,
                                "replay-foreign-" + Guid.NewGuid().ToString("N") + "@armada.test", "password")).ConfigureAwait(false);
                            foreignVessel.UserId = foreignUser.Id;
                            await harness.Driver.Vessels.UpdateAsync(foreignVessel).ConfigureAwait(false);
                        }
                        await harness.Driver.Missions.UpdateAsync(changed).ConfigureAwait(false);
                        if (boundary == "barrier")
                        {
                            using (SqliteConnection barrierConnection = new SqliteConnection(harness.Database.ConnectionString))
                            {
                                await barrierConnection.OpenAsync().ConfigureAwait(false);
                                using (SqliteCommand barrier = barrierConnection.CreateCommand())
                                {
                                    barrier.CommandText = "UPDATE missions SET integration_barrier_json='{}' WHERE id=@id;";
                                    barrier.Parameters.AddWithValue("@id", changed.Id);
                                    await barrier.ExecuteNonQueryAsync().ConfigureAwait(false);
                                }
                            }
                        }
                        if (boundary == "orphan-harbor")
                            await harness.Driver.HarborJobs.CreateAsync(new HarborJobRecord
                            {
                                JobId = "replay-orphan-" + Guid.NewGuid().ToString("N"), RunnerId = "old-runner", LaunchKey = "old-launch",
                                TenantId = Constants.DefaultTenantId, UserId = Constants.DefaultUserId,
                                MissionId = changed.Id, State = HarborJobStateEnum.Uncertain, StopConfirmed = false
                            }).ConfigureAwait(false);
                        bool refused = false;
                        try
                        {
                            await harness.Driver.ResearchReplays.ExecuteAsync(boundary == "foreign-tenant" ? "another-tenant" : Constants.DefaultTenantId,
                                boundary == "foreign-user" ? "another-owner" : Constants.DefaultUserId, fixture.Voyage.Id,
                                new ResearchReplayRequest { SourceCommit = _ProducedCommit, ExpectedFingerprint = preview.Fingerprint,
                                    RequestId = "refuse-" + boundary }).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (ex is InvalidOperationException || ex is UnauthorizedAccessException) { refused = true; }
                        AssertTrue(refused, boundary + ": invalid inventory, scope or stale state is refused");
                        AssertEqual(VoyageStatusEnum.Failed, (await harness.Driver.Voyages.ReadAsync(fixture.Voyage.Id).ConfigureAwait(false))!.Status);
                        AssertTrue((await harness.Driver.Missions.EnumerateByVoyageAsync(fixture.Voyage.Id).ConfigureAwait(false))
                            .All(m => m.ResearchReplayId == null && m.Status != MissionStatusEnum.Pending), boundary + ": no stage was reset");
                    }
                    ReplayFixture scoped = await SeedResearchReplayAsync(harness, _ProducedCommit).ConfigureAwait(false);
                    string token = harness.CreateMissionToken(Constants.DefaultTenantId, Constants.DefaultUserId, scoped.Stages[0].Id);
                    SurfaceReply forbidden = await harness.RestAsync(HttpMethod.Post,
                        "/api/v1/voyages/" + scoped.Voyage.Id + "/research-replay",
                        new { dryRun = true, sourceCommit = _ProducedCommit }, token).ConfigureAwait(false);
                    AssertEqual(403, forbidden.Status, "A mission-scoped administrator credential cannot preview or apply operator replay");
                }
            }).ConfigureAwait(false);
        }

        private static async Task<ReplayFixture> SeedResearchReplayAsync(SurfaceParityHarness harness, string commit, string? source = null)
        {
            Vessel vessel = await harness.Driver.Vessels.CreateAsync(new Vessel("research-replay-" + Guid.NewGuid().ToString("N"), "https://github.com/test/research.git")
            { TenantId = Constants.DefaultTenantId, UserId = Constants.DefaultUserId, LocalPath = source }).ConfigureAwait(false);
            Voyage voyage = await harness.Driver.Voyages.CreateAsync(new Voyage("Full Research replay", "Unchanged independent duties and criteria")
            { TenantId = Constants.DefaultTenantId, UserId = Constants.DefaultUserId, Status = VoyageStatusEnum.Failed, CompletedUtc = DateTime.UtcNow }).ConfigureAwait(false);
            List<Mission> stages = new List<Mission>();
            foreach (string role in new[] { PersonaCatalog.Worker, "PortingReferenceAnalyst", PersonaCatalog.TestEngineer, PersonaCatalog.Linter, PersonaCatalog.Worker, PersonaCatalog.Judge })
            {
                bool judge = stages.Count == 5;
                Mission mission = await harness.Driver.Missions.CreateAsync(new Mission(role + " independent duty")
                {
                    TenantId = Constants.DefaultTenantId, UserId = Constants.DefaultUserId, VoyageId = voyage.Id, VesselId = vessel.Id,
                    Persona = role, StageOrder = stages.Count + 1, DependsOnMissionId = stages.LastOrDefault()?.Id, Mode = MissionModeEnum.Research,
                    Status = judge ? MissionStatusEnum.Failed : MissionStatusEnum.Complete, StartFromRef = commit, CommitHash = commit,
                    Description = "Independent complete study; no criteria or source change.", RecoveryAttempts = stages.Count + 1,
                    AgentOutput = judge ? "NEEDS_REVISION: required independent duties remain incomplete. é🙂" : "Full prior " + role + " report. é🙂",
                    FailureReason = judge ? "Judge verdict: NEEDS_REVISION" : null,
                    StartedUtc = DateTime.UtcNow.AddMinutes(-20 - stages.Count), CompletedUtc = DateTime.UtcNow.AddMinutes(-10 - stages.Count)
                }).ConfigureAwait(false);
                await harness.Driver.Playbooks.SetMissionSnapshotsAsync(mission.Id, new List<MissionPlaybookSnapshot>
                { new MissionPlaybookSnapshot { FileName = "campaign.md", Content = "Read the complete independent-duty contract." } }).ConfigureAwait(false);
                stages.Add(mission);
            }
            return new ReplayFixture { Voyage = voyage, Stages = stages };
        }

        private sealed class ReplayFixture
        {
            public Voyage Voyage { get; set; } = new Voyage();
            public List<Mission> Stages { get; set; } = new List<Mission>();
        }
    }
}
