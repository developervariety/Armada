namespace Armada.Test.Unit.Suites.Database
{
    using System;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>Atomic source binding and output preservation for a regenerated landing review.</summary>
    public class GeneratedArtifactReviewPersistenceTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Generated Artifact Review Persistence";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("Candidate rerun preserves history and rejects stale mission writes", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Mission mission = new Mission("Review generated candidate", "Review the exact generated source")
                    {
                        TenantId = Constants.DefaultTenantId,
                        UserId = Constants.DefaultUserId,
                        Persona = "Judge",
                        Status = MissionStatusEnum.WorkProduced,
                        CommitHash = new string('a', 40),
                        BranchName = "review-candidate",
                        AgentOutput = "[ARMADA:VERDICT] PASS\nEarlier reviewed output",
                        PreservedRunOutput = "Earlier retained history",
                        RecoveryAttempts = 2,
                        LandingRetryCount = 1
                    };
                    await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);
                    Mission expected = (await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false))!;
                    Mission stale = (await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false))!;
                    GeneratedArtifactReviewBinding binding = GeneratedArtifactReviewRule.NextBinding(expected,
                        new GeneratedArtifactLandingResult
                        {
                            Outcome = "ReviewRequired", SourceCommit = mission.CommitHash,
                            TargetBaseCommit = new string('b', 40), CandidateCommit = new string('c', 40)
                        }, 2, DateTime.UtcNow);
                    AssertTrue(await testDb.Driver.Missions.TryWriteGeneratedArtifactReviewAsync(expected, binding, true).ConfigureAwait(false));
                    AssertFalse(await testDb.Driver.Missions.TryWriteGeneratedArtifactReviewAsync(stale, binding, true).ConfigureAwait(false),
                        "Only one writer can replace this reviewed source.");
                    stale.Title = "A stale writer must not restore the earlier run";
                    stale.Status = MissionStatusEnum.Complete;
                    AssertFalse(await testDb.Driver.Missions.TryUpdateIfStatusAsync(stale, MissionStatusEnum.Pending).ConfigureAwait(false),
                        "A normal save loaded before candidate preparation must be refused, even if it predicts the new status.");
                    bool staleRefused = false;
                    try { await testDb.Driver.Missions.UpdateAsync(stale).ConfigureAwait(false); }
                    catch (InvalidOperationException ex)
                    {
                        staleRefused = ex.Message.StartsWith("generated_candidate_stale_mission_write", StringComparison.Ordinal);
                    }
                    AssertTrue(staleRefused, "An unconditional stale save must report the candidate conflict by name.");
                    Mission stored = (await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false))!;
                    AssertEqual(MissionStatusEnum.Pending, stored.Status);
                    AssertEqual(binding.CandidateCommit, stored.CommitHash);
                    AssertEqual(binding.CandidateCommit, stored.StartFromRef);
                    AssertEqual(mission.BranchName, stored.BranchName);
                    AssertEqual(2, stored.RecoveryAttempts);
                    AssertEqual(1, stored.LandingRetryCount);
                    AssertTrue(stored.PreservedRunOutput!.Contains("Earlier retained history", StringComparison.Ordinal));
                    AssertTrue(stored.PreservedRunOutput.Contains(mission.AgentOutput!, StringComparison.Ordinal));
                    AssertTrue(stored.AgentOutput == null);
                    AssertEqual(JsonSerializer.Serialize(binding), JsonSerializer.Serialize(stored.GeneratedArtifactReview));
                    Mission summary = (await testDb.Driver.Missions.ReadSummaryAsync(mission.Id).ConfigureAwait(false))!;
                    AssertEqual(JsonSerializer.Serialize(binding), JsonSerializer.Serialize(summary.GeneratedArtifactReview),
                        "Summary reads must carry the same source binding as full reads.");
                    stored.Title = "Ordinary metadata update";
                    await testDb.Driver.Missions.UpdateAsync(stored).ConfigureAwait(false);
                    Mission after = (await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false))!;
                    AssertEqual(stored.Title, after.Title);
                    AssertEqual(JsonSerializer.Serialize(binding), JsonSerializer.Serialize(after.GeneratedArtifactReview));
                    after.HeldForOperatorReview = true;
                    after.HeldForOperatorReviewReason = "Operator evidence required";
                    after.Status = MissionStatusEnum.WorkProduced;
                    await testDb.Driver.Missions.UpdateAsync(after).ConfigureAwait(false);
                    Mission held = (await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false))!;
                    GeneratedArtifactReviewBinding approval = JsonSerializer.Deserialize<GeneratedArtifactReviewBinding>(JsonSerializer.Serialize(binding))!;
                    approval.ApprovedUtc = DateTime.UtcNow;
                    AssertFalse(await testDb.Driver.Missions.TryWriteGeneratedArtifactReviewAsync(held, approval, false).ConfigureAwait(false),
                        "Candidate storage cannot clear an existing operator hold.");
                    AssertTrue((await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false))!.HeldForOperatorReview);
                }
            }).ConfigureAwait(false);
        }
    }
}
