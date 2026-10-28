namespace Test.Shared.Suites.Database
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Database;
    using Armada.Core.Database.Sqlite;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Settings;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    public sealed partial class MissionDatabaseSuite
    {
        private const string ReplaySource = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

        private static IEnumerable<TestCaseDescriptor> ResearchReplayCases()
        {
            yield return CaseAsync("mission_replay_round_trip_reopen_and_generation_cas", "Research replay archive, reopen and stale-write refusal", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    DatabaseDriver db = testDb.Driver;
                    ReplayStorageFixture fixture = await SeedReplayStorageAsync(db).ConfigureAwait(false);
                    ResearchReplayPlan preview = await db.ResearchReplays.ExecuteAsync(Constants.DefaultTenantId, Constants.DefaultUserId,
                        fixture.Voyage.Id, new ResearchReplayRequest { DryRun = true, SourceCommit = ReplaySource }).ConfigureAwait(false);
                    ResearchReplayRequest request = new ResearchReplayRequest
                    { SourceCommit = ReplaySource, RequestId = "provider-replay", ExpectedFingerprint = preview.Fingerprint };
                    ResearchReplayPlan applied = await db.ResearchReplays.ExecuteAsync(Constants.DefaultTenantId, Constants.DefaultUserId,
                        fixture.Voyage.Id, request).ConfigureAwait(false);
                    AssertTrue(applied.AppliedNow);
                    DatabaseSettings settings = TestDatabaseConfig.BuildSettings(TestDatabaseConfig.BaseDatabaseName + "_shared");
                    SyslogLogging.LoggingModule logging = new SyslogLogging.LoggingModule(); logging.Settings.EnableConsole = false;
                    using (DatabaseDriver reopened = TestDatabaseConfig.IsSqlite
                        ? new SqliteDatabaseDriver(testDb.ConnectionString, logging)
                        : DatabaseDriverFactory.Create(settings, logging))
                    {
                        await reopened.InitializeAsync().ConfigureAwait(false);
                        foreach (Mission original in fixture.Stages)
                        {
                            Mission current = (await reopened.Missions.ReadAsync(original.Id).ConfigureAwait(false))!;
                            AssertEqual(MissionStatusEnum.Pending, current.Status);
                            AssertEqual(applied.ReplayId, current.ResearchReplayId);
                            AssertEqual(original.DependsOnMissionId, current.DependsOnMissionId);
                            AssertEqual(original.RecoveryAttempts, current.RecoveryAttempts);
                            AssertEqual(ReplaySource, current.StartFromRef);
                            AssertEqual(null, current.AgentOutput);
                            Mission prior = (await reopened.ResearchReplays.ReadPriorMissionAsync(Constants.DefaultTenantId,
                                Constants.DefaultUserId, applied.ReplayId!, original.Id).ConfigureAwait(false))!;
                            AssertEqual(original.AgentOutput, prior.AgentOutput);
                            AssertEqual(original.Status, prior.Status);
                            AssertEqual(original.FailureReason, prior.FailureReason);
                            AssertEqual(original.StartedUtc, prior.StartedUtc);
                            AssertEqual("Whole independent duty. é🙂", prior.PlaybookSnapshots.Single().Content);
                        }
                        Mission first = (await reopened.Missions.ReadAsync(fixture.Stages[0].Id).ConfigureAwait(false))!;
                        first.AgentOutput = "New output survives retries.";
                        await reopened.Missions.UpdateAsync(first).ConfigureAwait(false);
                        Mission stale = fixture.Stages[0];
                        stale.Status = MissionStatusEnum.Pending;
                        bool refused = false;
                        try { await reopened.Missions.UpdateAsync(stale).ConfigureAwait(false); }
                        catch (InvalidOperationException) { refused = true; }
                        AssertTrue(refused, "The ordinary write is fenced by the durable generation");
                        AssertTrue(!await reopened.Missions.TryUpdateIfStatusAsync(stale, MissionStatusEnum.Pending).ConfigureAwait(false),
                            "The expected-status write cannot cross generations");
                        ResearchReplayPlan duplicate = await reopened.ResearchReplays.ExecuteAsync(Constants.DefaultTenantId,
                            Constants.DefaultUserId, fixture.Voyage.Id, request).ConfigureAwait(false);
                        AssertEqual(applied.ReplayId, duplicate.ReplayId);
                        AssertTrue(!duplicate.AppliedNow);
                        AssertEqual(first.AgentOutput, (await reopened.Missions.ReadAsync(first.Id).ConfigureAwait(false))!.AgentOutput);
                        AssertEqual(6, (await reopened.Missions.EnumerateByVoyageAsync(fixture.Voyage.Id).ConfigureAwait(false)).Count);
                    }
                }
            });

            yield return CaseAsync("mission_replay_concurrent_idempotency_and_conflict", "Research replay concurrent request identity", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ReplayStorageFixture fixture = await SeedReplayStorageAsync(testDb.Driver).ConfigureAwait(false);
                    ResearchReplayPlan preview = await testDb.Driver.ResearchReplays.ExecuteAsync(Constants.DefaultTenantId,
                        Constants.DefaultUserId, fixture.Voyage.Id, new ResearchReplayRequest { DryRun = true, SourceCommit = ReplaySource }).ConfigureAwait(false);
                    TaskCompletionSource<bool> ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    int entered = 0;
                    async Task<ResearchReplayPlan> ApplyAsync()
                    {
                        if (Interlocked.Increment(ref entered) == 2) ready.TrySetResult(true);
                        await ready.Task.ConfigureAwait(false);
                        return await testDb.Driver.ResearchReplays.ExecuteAsync(Constants.DefaultTenantId, Constants.DefaultUserId,
                            fixture.Voyage.Id, new ResearchReplayRequest
                            { SourceCommit = ReplaySource, RequestId = "concurrent-provider-replay", ExpectedFingerprint = preview.Fingerprint }).ConfigureAwait(false);
                    }
                    ResearchReplayPlan[] results = await Task.WhenAll(Task.Run(ApplyAsync), Task.Run(ApplyAsync)).ConfigureAwait(false);
                    AssertEqual(results[0].ReplayId, results[1].ReplayId);
                    AssertEqual(1, results.Count(result => result.AppliedNow));
                    bool conflictingRequestRefused = false;
                    try
                    {
                        await testDb.Driver.ResearchReplays.ExecuteAsync(Constants.DefaultTenantId, Constants.DefaultUserId,
                            fixture.Voyage.Id, new ResearchReplayRequest
                            { SourceCommit = ReplaySource, RequestId = "different-request", ExpectedFingerprint = preview.Fingerprint }).ConfigureAwait(false);
                    }
                    catch (InvalidOperationException) { conflictingRequestRefused = true; }
                    AssertTrue(conflictingRequestRefused, "A second request cannot reset the now-active attempt");
                    AssertEqual(6, (await testDb.Driver.Missions.EnumerateByVoyageAsync(fixture.Voyage.Id).ConfigureAwait(false)).Count);
                }
            });
        }

        private static async Task<ReplayStorageFixture> SeedReplayStorageAsync(DatabaseDriver db)
        {
            MissionTestPrerequisites prerequisites = await CreatePrerequisitesAsync(db).ConfigureAwait(false);
            Vessel vessel = prerequisites.Vessel;
            vessel.TenantId = Constants.DefaultTenantId; vessel.UserId = Constants.DefaultUserId;
            await db.Vessels.UpdateAsync(vessel).ConfigureAwait(false);
            Voyage voyage = prerequisites.Voyage;
            voyage.TenantId = Constants.DefaultTenantId; voyage.UserId = Constants.DefaultUserId;
            voyage.Status = VoyageStatusEnum.Failed; voyage.CompletedUtc = _BaseTime.AddHours(1);
            await db.Voyages.UpdateAsync(voyage).ConfigureAwait(false);
            List<Mission> stages = new List<Mission>();
            foreach (string role in new[] { "Worker", "PortingReferenceAnalyst", "TestEngineer", "Linter", "Worker", "Judge" })
            {
                Mission mission = await db.Missions.CreateAsync(new Mission("Replay storage " + role, "Unchanged full independent duty.")
                {
                    TenantId = Constants.DefaultTenantId, UserId = Constants.DefaultUserId,
                    VesselId = vessel.Id, VoyageId = voyage.Id, Persona = role, Mode = MissionModeEnum.Research,
                    StageOrder = stages.Count + 1, DependsOnMissionId = stages.LastOrDefault()?.Id,
                    Status = stages.Count == 5 ? MissionStatusEnum.Failed : MissionStatusEnum.Complete,
                    CommitHash = ReplaySource, StartFromRef = ReplaySource, RecoveryAttempts = stages.Count + 1,
                    AgentOutput = "Retained full " + role + " report. é🙂", FailureReason = stages.Count == 5 ? "Judge verdict: NEEDS_REVISION" : null,
                    StartedUtc = _BaseTime.AddMinutes(stages.Count), CompletedUtc = _BaseTime.AddMinutes(stages.Count + 1)
                }).ConfigureAwait(false);
                await db.Playbooks.SetMissionSnapshotsAsync(mission.Id, new List<MissionPlaybookSnapshot>
                { new MissionPlaybookSnapshot { FileName = "duty.md", Content = "Whole independent duty. é🙂" } }).ConfigureAwait(false);
                stages.Add(mission);
            }
            return new ReplayStorageFixture { Voyage = voyage, Stages = stages };
        }

        private sealed class ReplayStorageFixture
        {
            public Voyage Voyage { get; set; } = new Voyage();
            public List<Mission> Stages { get; set; } = new List<Mission>();
        }
    }
}
