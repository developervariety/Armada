namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// Unit coverage for internal-first objective capture, linkage, and filtering flows.
    /// </summary>
    public class ObjectiveServiceTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Objective Service";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("CreateUpdateEnumerateAndDeleteAsync manages scoped objectives", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                ObjectiveService objectives = new ObjectiveService(testDb.Driver);

                string tenantId = "ten_objective";
                string userId = "usr_objective";
                string workingDirectory = Path.Combine(Path.GetTempPath(), "armada-objective-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(workingDirectory);

                try
                {
                    await EnsureTenantAndUserAsync(testDb, tenantId, userId).ConfigureAwait(false);

                    Vessel vessel = new Vessel
                    {
                        TenantId = tenantId,
                        UserId = userId,
                        Name = "Objective Vessel",
                        RepoUrl = "file:///tmp/objective.git",
                        LocalPath = workingDirectory,
                        WorkingDirectory = workingDirectory,
                        DefaultBranch = "main"
                    };
                    await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    PlanningSession planningSession = new PlanningSession
                    {
                        TenantId = tenantId,
                        UserId = userId,
                        CaptainId = "cpt_objective",
                        VesselId = vessel.Id,
                        FleetId = vessel.FleetId,
                        Title = "Objective Planning Session",
                        Status = PlanningSessionStatusEnum.Active
                    };
                    await testDb.Driver.PlanningSessions.CreateAsync(planningSession).ConfigureAwait(false);

                    Voyage voyage = new Voyage("Objective Voyage", "Implementation voyage")
                    {
                        TenantId = tenantId,
                        UserId = userId
                    };
                    await testDb.Driver.Voyages.CreateAsync(voyage).ConfigureAwait(false);

                    Mission mission = new Mission
                    {
                        TenantId = tenantId,
                        UserId = userId,
                        VesselId = vessel.Id,
                        VoyageId = voyage.Id,
                        Title = "Objective Mission",
                        Description = "Implement objective scope",
                        Status = MissionStatusEnum.Pending
                    };
                    await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                    Release release = new Release
                    {
                        TenantId = tenantId,
                        UserId = userId,
                        VesselId = vessel.Id,
                        Title = "Objective Release",
                        Status = ReleaseStatusEnum.Draft
                    };
                    await testDb.Driver.Releases.CreateAsync(release).ConfigureAwait(false);

                    AuthContext auth = AuthContext.Authenticated(tenantId, userId, false, true, "UnitTest");
                    Objective created = await objectives.CreateAsync(auth, new ObjectiveUpsertRequest
                    {
                        Title = "Ship Objective Support",
                        Description = "Capture internal-first scoping data.",
                        Status = ObjectiveStatusEnum.Scoped,
                        Owner = "joel",
                        Tags = new List<string> { "delivery", "scope" },
                        AcceptanceCriteria = new List<string> { "Objective exists", "History can filter by objective" },
                        VesselIds = new List<string> { vessel.Id }
                    }).ConfigureAwait(false);

                    AssertStartsWith("obj_", created.Id);
                    AssertEqual(ObjectiveStatusEnum.Scoped, created.Status);
                    AssertTrue(created.VesselIds.Contains(vessel.Id), "Expected vessel link.");

                    EnumerationResult<Objective> filtered = await objectives.EnumerateAsync(auth, new ObjectiveQuery
                    {
                        PageNumber = 1,
                        PageSize = 25,
                        VesselId = vessel.Id,
                        Search = "objective support"
                    }).ConfigureAwait(false);

                    AssertEqual(1, filtered.Objects.Count);
                    AssertEqual(created.Id, filtered.Objects[0].Id);

                    Objective planned = await objectives.LinkPlanningSessionAsync(auth, created.Id, planningSession.Id).ConfigureAwait(false);
                    AssertTrue(planned.PlanningSessionIds.Contains(planningSession.Id), "Expected planning session link.");
                    AssertEqual(ObjectiveStatusEnum.Planned, planned.Status);

                    Objective inProgress = await objectives.LinkVoyageAsync(auth, created.Id, voyage.Id).ConfigureAwait(false);
                    AssertTrue(inProgress.VoyageIds.Contains(voyage.Id), "Expected voyage link.");
                    AssertTrue(inProgress.MissionIds.Contains(mission.Id), "Expected mission linkage from voyage.");
                    AssertEqual(ObjectiveStatusEnum.InProgress, inProgress.Status);

                    Objective released = await objectives.LinkReleaseAsync(auth, created.Id, release.Id).ConfigureAwait(false);
                    AssertTrue(released.ReleaseIds.Contains(release.Id), "Expected release link.");
                    AssertEqual(ObjectiveStatusEnum.Released, released.Status);

                    Objective updated = await objectives.UpdateAsync(auth, created.Id, new ObjectiveUpsertRequest
                    {
                        Status = ObjectiveStatusEnum.Completed,
                        EvidenceLinks = new List<string> { "https://example.test/releases/objective" }
                    }).ConfigureAwait(false);

                    AssertEqual(ObjectiveStatusEnum.Completed, updated.Status);
                    AssertTrue(updated.CompletedUtc.HasValue, "Expected completed timestamp.");
                    AssertEqual(1, updated.EvidenceLinks.Count);

                    Objective? loaded = await objectives.ReadAsync(auth, created.Id).ConfigureAwait(false);
                    loaded = NotNull(loaded);
                    AssertEqual(created.Id, loaded.Id);
                    AssertEqual(ObjectiveStatusEnum.Completed, loaded.Status);

                    await objectives.DeleteAsync(auth, created.Id).ConfigureAwait(false);
                    Objective? deleted = await objectives.ReadAsync(auth, created.Id).ConfigureAwait(false);
                    AssertNull(deleted);
                }
                finally
                {
                    TryDeleteDirectory(workingDirectory);
                }
            }).ConfigureAwait(false);
        }

        private static async Task EnsureTenantAndUserAsync(TestDatabase testDb, string tenantId, string userId)
        {
            TenantMetadata? existingTenant = await testDb.Driver.Tenants.ReadAsync(tenantId).ConfigureAwait(false);
            if (existingTenant == null)
            {
                await testDb.Driver.Tenants.CreateAsync(new TenantMetadata
                {
                    Id = tenantId,
                    Name = tenantId
                }).ConfigureAwait(false);
            }

            UserMaster? existingUser = await testDb.Driver.Users.ReadByIdAsync(userId).ConfigureAwait(false);
            if (existingUser == null)
            {
                await testDb.Driver.Users.CreateAsync(new UserMaster
                {
                    Id = userId,
                    TenantId = tenantId,
                    Email = userId + "@armada.test",
                    PasswordSha256 = UserMaster.ComputePasswordHash("password"),
                    IsTenantAdmin = true
                }).ConfigureAwait(false);
            }
        }

        private static Objective NotNull(Objective? objective)
        {
            if (objective == null) throw new InvalidOperationException("Expected objective to be present.");
            return objective;
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, true);
            }
            catch
            {
            }
        }
    }
}
