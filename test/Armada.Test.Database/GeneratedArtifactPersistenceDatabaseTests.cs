namespace Armada.Test.Database
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;

    /// <summary>Provider SQL proof for optional declarations and conditional candidate review storage.</summary>
    internal sealed class GeneratedArtifactPersistenceDatabaseTests
    {
        private readonly DatabaseDriver _Driver;
        private readonly DatabaseSettings _Settings;
        private readonly bool _NoCleanup;

        internal GeneratedArtifactPersistenceDatabaseTests(DatabaseDriver driver, DatabaseSettings settings, bool noCleanup)
        { _Driver = driver; _Settings = settings; _NoCleanup = noCleanup; }

        internal async Task VerifyAsync(CancellationToken token)
        {
            DatabaseFixture fixture = new DatabaseFixture(_Driver, _NoCleanup);
            try
            {
                TenantMetadata tenant = await fixture.CreateTenantAsync("generated-review", token: token).ConfigureAwait(false);
                UserMaster user = await fixture.CreateUserAsync(tenant.Id, "generated-review", token: token).ConfigureAwait(false);
                Fleet fleet = await fixture.CreateFleetAsync(tenant.Id, user.Id, "generated-review", token).ConfigureAwait(false);
                Vessel vessel = await fixture.CreateVesselAsync(tenant.Id, user.Id, fleet.Id, "generated-review", token).ConfigureAwait(false);
                Captain captain = await fixture.CreateCaptainAsync(tenant.Id, user.Id, "generated-review", token).ConfigureAwait(false);
                Voyage voyage = await fixture.CreateVoyageAsync(tenant.Id, user.Id, "generated-review", token).ConfigureAwait(false);
                Mission mission = await fixture.CreateMissionAsync(tenant.Id, user.Id, voyage.Id, vessel.Id, captain.Id, "generated-review", token).ConfigureAwait(false);
                DatabaseAssert.True((await _Driver.Vessels.ReadAsync(vessel.Id, token).ConfigureAwait(false))!.GeneratedArtifacts == null,
                    "An undeclared vessel retains the default null policy");
                DatabaseAssert.True((await _Driver.Missions.ReadAsync(mission.Id, token).ConfigureAwait(false))!.GeneratedArtifactReview == null,
                    "An ordinary mission has no candidate review");
                vessel.GeneratedArtifacts = new GeneratedArtifactPolicy
                { Paths = new List<string> { "generated/manifest.json", "generated/index.json" }, RegenerateCommand = "sh tools/regenerate.sh" };
                await _Driver.Vessels.UpdateAsync(vessel, token).ConfigureAwait(false);
                mission.Persona = "Judge";
                mission.Status = MissionStatusEnum.WorkProduced;
                mission.CommitHash = new string('a', 40);
                mission.BranchName = "review-generated";
                mission.AgentOutput = "[ARMADA:VERDICT] PASS\nEarlier review stays retained";
                mission.RecoveryAttempts = 2;
                mission.LandingRetryCount = 1;
                await _Driver.Missions.UpdateAsync(mission, token).ConfigureAwait(false);
                Mission loaded = (await _Driver.Missions.ReadAsync(mission.Id, token).ConfigureAwait(false))!;
                Mission stale = (await _Driver.Missions.ReadAsync(mission.Id, token).ConfigureAwait(false))!;
                GeneratedArtifactReviewBinding binding = GeneratedArtifactReviewRule.NextBinding(loaded,
                    new GeneratedArtifactLandingResult
                    { Outcome = "ReviewRequired", SourceCommit = loaded.CommitHash, TargetBaseCommit = new string('b', 40), CandidateCommit = new string('c', 40) },
                    2, DateTime.UtcNow);
                DatabaseAssert.True(await _Driver.Missions.TryWriteGeneratedArtifactReviewAsync(loaded, binding, true, token).ConfigureAwait(false),
                    "The current source is conditionally replaced by its candidate");
                DatabaseAssert.True(!await _Driver.Missions.TryWriteGeneratedArtifactReviewAsync(stale, binding, true, token).ConfigureAwait(false),
                    "A stale source cannot prepare the candidate twice");
                using (DatabaseDriver reopened = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false))
                {
                    Vessel storedVessel = (await reopened.Vessels.ReadAsync(vessel.Id, token).ConfigureAwait(false))!;
                    DatabaseAssert.Equal(JsonSerializer.Serialize(vessel.GeneratedArtifacts), JsonSerializer.Serialize(storedVessel.GeneratedArtifacts), "Reopened declaration");
                    Mission pending = (await reopened.Missions.ReadAsync(mission.Id, token).ConfigureAwait(false))!;
                    DatabaseAssert.Equal(MissionStatusEnum.Pending, pending.Status, "Candidate status");
                    DatabaseAssert.Equal(binding.CandidateCommit, pending.CommitHash, "Candidate source");
                    DatabaseAssert.Equal(2, pending.RecoveryAttempts, "Recovery attempts retained");
                    DatabaseAssert.Equal(1, pending.LandingRetryCount, "Landing retries retained");
                    DatabaseAssert.True(pending.PreservedRunOutput!.Contains("Earlier review stays retained", StringComparison.Ordinal), "Prior output retained");
                    DatabaseAssert.Equal(JsonSerializer.Serialize(binding), JsonSerializer.Serialize(pending.GeneratedArtifactReview), "Reopened review binding");
                    DatabaseAssert.Equal(JsonSerializer.Serialize(binding), JsonSerializer.Serialize(
                        (await reopened.Missions.ReadSummaryAsync(mission.Id, token).ConfigureAwait(false))!.GeneratedArtifactReview), "Summary review binding");
                    pending.Status = MissionStatusEnum.WorkProduced;
                    pending.AgentOutput = "[ARMADA:VERDICT] PASS\nCandidate review";
                    await reopened.Missions.UpdateAsync(pending, token).ConfigureAwait(false);
                    pending = (await reopened.Missions.ReadAsync(mission.Id, token).ConfigureAwait(false))!;
                    GeneratedArtifactReviewBinding approved = JsonSerializer.Deserialize<GeneratedArtifactReviewBinding>(JsonSerializer.Serialize(binding))!;
                    approved.ApprovedUtc = DateTime.UtcNow;
                    DatabaseAssert.True(await reopened.Missions.TryWriteGeneratedArtifactReviewAsync(pending, approved, false, token).ConfigureAwait(false),
                        "Approval updates only the current binding");
                    Mission after = (await reopened.Missions.ReadAsync(mission.Id, token).ConfigureAwait(false))!;
                    DatabaseAssert.Equal(JsonSerializer.Serialize(approved), JsonSerializer.Serialize(after.GeneratedArtifactReview), "Approved binding");
                    storedVessel.GeneratedArtifacts = null;
                    await reopened.Vessels.UpdateAsync(storedVessel, token).ConfigureAwait(false);
                    DatabaseAssert.True((await reopened.Vessels.ReadAsync(vessel.Id, token).ConfigureAwait(false))!.GeneratedArtifacts == null,
                        "The optional policy can be explicitly cleared");
                }
            }
            finally { await fixture.CleanupAsync(token).ConfigureAwait(false); }
        }
    }
}
