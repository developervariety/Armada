namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Server;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// Unit coverage for autonomous incident lifecycle transitions from Armada evidence.
    /// </summary>
    public class IncidentLifecycleOrchestratorTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Incident Lifecycle Orchestrator";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("Passing matching check mitigates failed-check incident and quiet window closes it", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb, "ten_inc_life_check", "usr_inc_life_check").ConfigureAwait(false);
                Vessel vessel = await CreateVesselAsync(testDb, "ten_inc_life_check", "usr_inc_life_check").ConfigureAwait(false);

                CheckRun failed = await testDb.Driver.CheckRuns.CreateAsync(new CheckRun
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    VesselId = vessel.Id,
                    Type = CheckRunTypeEnum.Build,
                    Label = "build",
                    Command = "dotnet build",
                    Status = CheckRunStatusEnum.Failed,
                    CompletedUtc = DateTime.UtcNow.AddMinutes(-5),
                    LastUpdateUtc = DateTime.UtcNow.AddMinutes(-5)
                }).ConfigureAwait(false);

                IncidentService incidents = new IncidentService(testDb.Driver);
                AuthContext auth = AuthContext.Authenticated(vessel.TenantId!, vessel.UserId!, false, true, "UnitTest");
                Incident incident = await incidents.CreateAsync(auth, new IncidentUpsertRequest
                {
                    Title = "Automated check failed: build",
                    Status = IncidentStatusEnum.Open,
                    Severity = IncidentSeverityEnum.Medium,
                    VesselId = vessel.Id,
                    CheckRunId = failed.Id,
                    DetectedUtc = DateTime.UtcNow.AddMinutes(-4),
                    RecoveryNotes = "Initial failed check."
                }).ConfigureAwait(false);

                await testDb.Driver.CheckRuns.CreateAsync(new CheckRun
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    VesselId = vessel.Id,
                    Type = CheckRunTypeEnum.Build,
                    Label = "build",
                    Command = "dotnet build",
                    Status = CheckRunStatusEnum.Passed,
                    CompletedUtc = DateTime.UtcNow.AddMinutes(-1),
                    LastUpdateUtc = DateTime.UtcNow.AddMinutes(-1)
                }).ConfigureAwait(false);

                IncidentLifecycleOrchestrator orchestrator = CreateOrchestrator(testDb.Driver, incidents, 0);

                AssertEqual(1, await orchestrator.RunSweepAsync().ConfigureAwait(false));
                Incident? mitigated = await incidents.ReadAsync(auth, incident.Id).ConfigureAwait(false);
                AssertTrue(mitigated != null, "Expected mitigated incident to be readable.");
                AssertEqual(IncidentStatusEnum.Mitigated, mitigated!.Status);
                AssertTrue(mitigated.MitigatedUtc.HasValue, "Expected mitigation timestamp.");

                AssertEqual(1, await orchestrator.RunSweepAsync().ConfigureAwait(false));
                Incident? closed = await incidents.ReadAsync(auth, incident.Id).ConfigureAwait(false);
                AssertTrue(closed != null, "Expected closed incident to be readable.");
                AssertEqual(IncidentStatusEnum.Closed, closed!.Status);
                AssertTrue(closed.ClosedUtc.HasValue, "Expected closure timestamp.");
            }).ConfigureAwait(false);

            await RunTest("Completed rescue mission mitigates linked failed-mission incident", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb, "ten_inc_life_rescue", "usr_inc_life_rescue").ConfigureAwait(false);
                Vessel vessel = await CreateVesselAsync(testDb, "ten_inc_life_rescue", "usr_inc_life_rescue").ConfigureAwait(false);
                Mission failed = await CreateMissionAsync(testDb, vessel, MissionStatusEnum.Failed, "Original mission").ConfigureAwait(false);
                Mission rescue = await CreateMissionAsync(testDb, vessel, MissionStatusEnum.Complete, "Rescue 1: Original mission").ConfigureAwait(false);
                rescue.ParentMissionId = failed.Id;
                rescue.Description = "Autonomous rescue mission completed. <!-- ARMADA:AUTO-RESCUE -->";
                await testDb.Driver.Missions.UpdateAsync(rescue).ConfigureAwait(false);

                IncidentService incidents = new IncidentService(testDb.Driver);
                AuthContext auth = AuthContext.Authenticated(vessel.TenantId!, vessel.UserId!, false, true, "UnitTest");
                Incident incident = await incidents.CreateAsync(auth, new IncidentUpsertRequest
                {
                    Title = "Mission failed: Original mission",
                    Status = IncidentStatusEnum.Open,
                    Severity = IncidentSeverityEnum.Medium,
                    VesselId = vessel.Id,
                    MissionId = failed.Id,
                    DetectedUtc = DateTime.UtcNow.AddMinutes(-3)
                }).ConfigureAwait(false);

                IncidentLifecycleOrchestrator orchestrator = CreateOrchestrator(testDb.Driver, incidents);
                AssertEqual(1, await orchestrator.RunSweepAsync().ConfigureAwait(false));

                Incident? updated = await incidents.ReadAsync(auth, incident.Id).ConfigureAwait(false);
                AssertTrue(updated != null, "Expected incident.");
                AssertEqual(IncidentStatusEnum.Mitigated, updated!.Status);
                AssertContains(rescue.Id, updated.RecoveryNotes ?? "", "Expected rescue evidence in recovery notes.");
            }).ConfigureAwait(false);

            await RunTest("Cancelled rescue closes linked failed-mission incident as superseded", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb, "ten_inc_life_cancelled_rescue", "usr_inc_life_cancelled_rescue").ConfigureAwait(false);
                Vessel vessel = await CreateVesselAsync(testDb, "ten_inc_life_cancelled_rescue", "usr_inc_life_cancelled_rescue").ConfigureAwait(false);
                Mission failed = await CreateMissionAsync(testDb, vessel, MissionStatusEnum.Failed, "Original mission").ConfigureAwait(false);
                Mission rescue = await CreateMissionAsync(testDb, vessel, MissionStatusEnum.Cancelled, "Rescue 1: Original mission").ConfigureAwait(false);
                rescue.ParentMissionId = failed.Id;
                rescue.Description = "Autonomous rescue mission cancelled. <!-- ARMADA:AUTO-RESCUE -->";
                await testDb.Driver.Missions.UpdateAsync(rescue).ConfigureAwait(false);

                IncidentService incidents = new IncidentService(testDb.Driver);
                AuthContext auth = AuthContext.Authenticated(vessel.TenantId!, vessel.UserId!, false, true, "UnitTest");
                Incident incident = await incidents.CreateAsync(auth, new IncidentUpsertRequest
                {
                    Title = "Mission failed: Original mission",
                    Status = IncidentStatusEnum.Open,
                    Severity = IncidentSeverityEnum.Medium,
                    VesselId = vessel.Id,
                    MissionId = failed.Id,
                    DetectedUtc = DateTime.UtcNow.AddMinutes(-3)
                }).ConfigureAwait(false);

                IncidentLifecycleOrchestrator orchestrator = CreateOrchestrator(testDb.Driver, incidents);
                AssertEqual(1, await orchestrator.RunSweepAsync().ConfigureAwait(false));

                Incident? updated = await incidents.ReadAsync(auth, incident.Id).ConfigureAwait(false);
                AssertTrue(updated != null, "Expected incident.");
                AssertEqual(IncidentStatusEnum.Closed, updated!.Status);
                AssertContains("superseded", updated.RecoveryNotes ?? "", "Expected superseded evidence in recovery notes.");
            }).ConfigureAwait(false);

            await RunTest("Cancelled voyage closes landing-failed incident as superseded", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb, "ten_inc_life_landing_cancelled", "usr_inc_life_landing_cancelled").ConfigureAwait(false);
                Vessel vessel = await CreateVesselAsync(testDb, "ten_inc_life_landing_cancelled", "usr_inc_life_landing_cancelled").ConfigureAwait(false);
                Voyage voyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("Cancelled landing voyage")
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    Status = VoyageStatusEnum.Cancelled,
                    CompletedUtc = DateTime.UtcNow.AddMinutes(-1),
                    LastUpdateUtc = DateTime.UtcNow.AddMinutes(-1)
                }).ConfigureAwait(false);
                Mission landingFailed = await CreateMissionAsync(testDb, vessel, MissionStatusEnum.LandingFailed, "Landing failed mission").ConfigureAwait(false);
                landingFailed.VoyageId = voyage.Id;
                landingFailed.FailureReason = "Zero-commit guard blocked landing.";
                await testDb.Driver.Missions.UpdateAsync(landingFailed).ConfigureAwait(false);

                IncidentService incidents = new IncidentService(testDb.Driver);
                AuthContext auth = AuthContext.Authenticated(vessel.TenantId!, vessel.UserId!, false, true, "UnitTest");
                Incident incident = await incidents.CreateAsync(auth, new IncidentUpsertRequest
                {
                    Title = "Mission failed: Landing failed mission",
                    Status = IncidentStatusEnum.Open,
                    Severity = IncidentSeverityEnum.High,
                    VesselId = vessel.Id,
                    MissionId = landingFailed.Id,
                    VoyageId = voyage.Id,
                    DetectedUtc = DateTime.UtcNow.AddMinutes(-3)
                }).ConfigureAwait(false);

                IncidentLifecycleOrchestrator orchestrator = CreateOrchestrator(testDb.Driver, incidents);
                AssertEqual(1, await orchestrator.RunSweepAsync().ConfigureAwait(false));

                Incident? updated = await incidents.ReadAsync(auth, incident.Id).ConfigureAwait(false);
                AssertTrue(updated != null, "Expected incident.");
                AssertEqual(IncidentStatusEnum.Closed, updated!.Status);
                AssertContains("cancelled voyage", updated.RecoveryNotes ?? "", "Expected cancelled-voyage evidence.");
            }).ConfigureAwait(false);

            await RunTest("New failed matching check reopens mitigated incident and raises severity", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb, "ten_inc_life_reopen", "usr_inc_life_reopen").ConfigureAwait(false);
                Vessel vessel = await CreateVesselAsync(testDb, "ten_inc_life_reopen", "usr_inc_life_reopen").ConfigureAwait(false);

                CheckRun original = await testDb.Driver.CheckRuns.CreateAsync(new CheckRun
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    VesselId = vessel.Id,
                    Type = CheckRunTypeEnum.UnitTest,
                    Label = "unit",
                    Command = "dotnet test",
                    Status = CheckRunStatusEnum.Passed,
                    CompletedUtc = DateTime.UtcNow.AddMinutes(-5),
                    LastUpdateUtc = DateTime.UtcNow.AddMinutes(-5)
                }).ConfigureAwait(false);

                IncidentService incidents = new IncidentService(testDb.Driver);
                AuthContext auth = AuthContext.Authenticated(vessel.TenantId!, vessel.UserId!, false, true, "UnitTest");
                Incident incident = await incidents.CreateAsync(auth, new IncidentUpsertRequest
                {
                    Title = "Automated check failed: unit",
                    Status = IncidentStatusEnum.Mitigated,
                    Severity = IncidentSeverityEnum.Medium,
                    VesselId = vessel.Id,
                    CheckRunId = original.Id,
                    DetectedUtc = DateTime.UtcNow.AddMinutes(-6),
                    RecoveryNotes = "Previously mitigated."
                }).ConfigureAwait(false);

                CheckRun failed = await testDb.Driver.CheckRuns.CreateAsync(new CheckRun
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    VesselId = vessel.Id,
                    Type = CheckRunTypeEnum.UnitTest,
                    Label = "unit",
                    Command = "dotnet test",
                    Status = CheckRunStatusEnum.Failed,
                    CompletedUtc = DateTime.UtcNow.AddMinutes(-1),
                    LastUpdateUtc = DateTime.UtcNow.AddMinutes(-1)
                }).ConfigureAwait(false);

                IncidentLifecycleOrchestrator orchestrator = CreateOrchestrator(testDb.Driver, incidents);
                AssertEqual(1, await orchestrator.RunSweepAsync().ConfigureAwait(false));

                Incident? updated = await incidents.ReadAsync(auth, incident.Id).ConfigureAwait(false);
                AssertTrue(updated != null, "Expected incident.");
                AssertEqual(IncidentStatusEnum.Open, updated!.Status);
                AssertEqual(IncidentSeverityEnum.High, updated.Severity);
                AssertContains(failed.Id, updated.RecoveryNotes ?? "", "Expected latest failed check evidence.");
            }).ConfigureAwait(false);

            await RunTest("Later same-vessel passed check closes stale infrastructure-blocked check incident", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb, "ten_inc_life_stale_check", "usr_inc_life_stale_check").ConfigureAwait(false);
                Vessel vessel = await CreateVesselAsync(testDb, "ten_inc_life_stale_check", "usr_inc_life_stale_check").ConfigureAwait(false);

                CheckRun failed = await testDb.Driver.CheckRuns.CreateAsync(new CheckRun
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    VesselId = vessel.Id,
                    MissionId = "msn_old_unit",
                    VoyageId = "vyg_old_unit",
                    Type = CheckRunTypeEnum.UnitTest,
                    Label = "unit",
                    Command = "dotnet test",
                    Status = CheckRunStatusEnum.Failed,
                    Output = "Cannot connect to the Docker daemon.",
                    CompletedUtc = DateTime.UtcNow.AddHours(-20),
                    LastUpdateUtc = DateTime.UtcNow.AddHours(-20)
                }).ConfigureAwait(false);

                IncidentService incidents = new IncidentService(testDb.Driver);
                AuthContext auth = AuthContext.Authenticated(vessel.TenantId!, vessel.UserId!, false, true, "UnitTest");
                Incident incident = await incidents.CreateAsync(auth, new IncidentUpsertRequest
                {
                    Title = "Automated check failed: unit",
                    Status = IncidentStatusEnum.Open,
                    Severity = IncidentSeverityEnum.High,
                    VesselId = vessel.Id,
                    CheckRunId = failed.Id,
                    MissionId = failed.MissionId,
                    VoyageId = failed.VoyageId,
                    DetectedUtc = DateTime.UtcNow.AddHours(-20),
                    RootCause = "Automated check command failed or could not run."
                }).ConfigureAwait(false);

                CheckRun passed = await testDb.Driver.CheckRuns.CreateAsync(new CheckRun
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    VesselId = vessel.Id,
                    MissionId = "msn_new_unit",
                    VoyageId = "vyg_new_unit",
                    Type = CheckRunTypeEnum.UnitTest,
                    Label = "unit",
                    Command = "dotnet test",
                    Status = CheckRunStatusEnum.Passed,
                    CompletedUtc = DateTime.UtcNow.AddMinutes(-5),
                    LastUpdateUtc = DateTime.UtcNow.AddMinutes(-5)
                }).ConfigureAwait(false);

                IncidentLifecycleOrchestrator orchestrator = CreateOrchestrator(testDb.Driver, incidents);
                AssertEqual(1, await orchestrator.RunSweepAsync().ConfigureAwait(false));

                Incident? updated = await incidents.ReadAsync(auth, incident.Id).ConfigureAwait(false);
                AssertTrue(updated != null, "Expected incident.");
                AssertEqual(IncidentStatusEnum.Closed, updated!.Status);
                AssertContains(passed.Id, updated.RecoveryNotes ?? "", "Expected later passing check evidence.");
            }).ConfigureAwait(false);

            await RunTest("Rolled-back deployment marks linked incident rolled back", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb, "ten_inc_life_rollback", "usr_inc_life_rollback").ConfigureAwait(false);
                Vessel vessel = await CreateVesselAsync(testDb, "ten_inc_life_rollback", "usr_inc_life_rollback").ConfigureAwait(false);

                Deployment deployment = await testDb.Driver.Deployments.CreateAsync(new Deployment
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    VesselId = vessel.Id,
                    Title = "Rollback deployment",
                    Status = DeploymentStatusEnum.RolledBack,
                    VerificationStatus = DeploymentVerificationStatusEnum.Passed,
                    CompletedUtc = DateTime.UtcNow.AddMinutes(-2),
                    RolledBackUtc = DateTime.UtcNow.AddMinutes(-1),
                    LastUpdateUtc = DateTime.UtcNow.AddMinutes(-1)
                }).ConfigureAwait(false);

                IncidentService incidents = new IncidentService(testDb.Driver);
                AuthContext auth = AuthContext.Authenticated(vessel.TenantId!, vessel.UserId!, false, true, "UnitTest");
                Incident incident = await incidents.CreateAsync(auth, new IncidentUpsertRequest
                {
                    Title = "Deployment failed",
                    Status = IncidentStatusEnum.Open,
                    Severity = IncidentSeverityEnum.High,
                    VesselId = vessel.Id,
                    DeploymentId = deployment.Id,
                    RollbackDeploymentId = deployment.Id,
                    DetectedUtc = DateTime.UtcNow.AddMinutes(-5)
                }).ConfigureAwait(false);

                IncidentLifecycleOrchestrator orchestrator = CreateOrchestrator(testDb.Driver, incidents);
                AssertEqual(1, await orchestrator.RunSweepAsync().ConfigureAwait(false));

                Incident? updated = await incidents.ReadAsync(auth, incident.Id).ConfigureAwait(false);
                AssertTrue(updated != null, "Expected incident.");
                AssertEqual(IncidentStatusEnum.RolledBack, updated!.Status);
                AssertTrue(updated.ClosedUtc.HasValue, "Rolled-back incidents should get closure timestamp.");
            }).ConfigureAwait(false);
        }

        private static IncidentLifecycleOrchestrator CreateOrchestrator(
            DatabaseDriver database,
            IncidentService incidents,
            int closeQuietPeriodMinutes = 60)
        {
            return new IncidentLifecycleOrchestrator(
                database,
                incidents,
                new ArmadaSettings
                {
                    IncidentLifecycle = new IncidentLifecycleSettings
                    {
                        CloseQuietPeriodMinutes = closeQuietPeriodMinutes
                    }
                },
                new LoggingModule());
        }

        private static async Task<Vessel> CreateVesselAsync(TestDatabase testDb, string tenantId, string userId)
        {
            Vessel vessel = new Vessel
            {
                TenantId = tenantId,
                UserId = userId,
                Name = "Incident Lifecycle Vessel",
                RepoUrl = "file:///tmp/incident-lifecycle.git",
                LocalPath = "C:\\tmp\\incident-lifecycle",
                WorkingDirectory = "C:\\tmp\\incident-lifecycle",
                DefaultBranch = "main"
            };
            return await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);
        }

        private static async Task<Mission> CreateMissionAsync(
            TestDatabase testDb,
            Vessel vessel,
            MissionStatusEnum status,
            string title)
        {
            Mission mission = new Mission
            {
                TenantId = vessel.TenantId,
                UserId = vessel.UserId,
                VesselId = vessel.Id,
                Title = title,
                Description = title,
                Status = status,
                FailureReason = status == MissionStatusEnum.Failed ? "Unit-test failure." : null,
                CompletedUtc = DateTime.UtcNow.AddMinutes(-2),
                LastUpdateUtc = DateTime.UtcNow.AddMinutes(-2)
            };

            return await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);
        }

        private static async Task EnsureTenantAndUserAsync(TestDatabase testDb, string tenantId, string userId)
        {
            if (await testDb.Driver.Tenants.ReadAsync(tenantId).ConfigureAwait(false) == null)
            {
                await testDb.Driver.Tenants.CreateAsync(new TenantMetadata
                {
                    Id = tenantId,
                    Name = tenantId
                }).ConfigureAwait(false);
            }

            if (await testDb.Driver.Users.ReadByIdAsync(userId).ConfigureAwait(false) == null)
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
    }
}
