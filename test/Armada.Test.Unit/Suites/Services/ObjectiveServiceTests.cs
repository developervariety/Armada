namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text.Json;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Server.Mcp.Tools;
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

                    DeploymentEnvironment environment = new DeploymentEnvironment
                    {
                        TenantId = tenantId,
                        UserId = userId,
                        VesselId = vessel.Id,
                        Name = "objective-test",
                        Kind = EnvironmentKindEnum.Staging,
                        IsDefault = true,
                        Active = true
                    };
                    await testDb.Driver.Environments.CreateAsync(environment).ConfigureAwait(false);

                    Deployment deployment = new Deployment
                    {
                        TenantId = tenantId,
                        UserId = userId,
                        VesselId = vessel.Id,
                        EnvironmentId = environment.Id,
                        EnvironmentName = environment.Name,
                        ReleaseId = release.Id,
                        MissionId = mission.Id,
                        VoyageId = voyage.Id,
                        Title = "Objective Deployment",
                        Status = DeploymentStatusEnum.Succeeded,
                        VerificationStatus = DeploymentVerificationStatusEnum.Passed,
                        CheckRunIds = new List<string> { "chk_objective_deploy" }
                    };
                    await testDb.Driver.Deployments.CreateAsync(deployment).ConfigureAwait(false);

                    CheckRun deploymentCheckRun = new CheckRun
                    {
                        Id = "chk_objective_deploy",
                        TenantId = tenantId,
                        UserId = userId,
                        VesselId = vessel.Id,
                        MissionId = mission.Id,
                        VoyageId = voyage.Id,
                        DeploymentId = deployment.Id,
                        Label = "Objective deploy verification",
                        Type = CheckRunTypeEnum.DeploymentVerification,
                        Source = CheckRunSourceEnum.Armada,
                        Status = CheckRunStatusEnum.Passed,
                        Command = "echo objective-deploy"
                    };
                    await testDb.Driver.CheckRuns.CreateAsync(deploymentCheckRun).ConfigureAwait(false);

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

                    Objective deployed = await objectives.LinkDeploymentAsync(auth, created.Id, deployment.Id).ConfigureAwait(false);
                    AssertTrue(deployed.DeploymentIds.Contains(deployment.Id), "Expected deployment link.");
                    AssertTrue(deployed.ReleaseIds.Contains(release.Id), "Expected deployment to preserve release lineage.");
                    AssertTrue(deployed.MissionIds.Contains(mission.Id), "Expected deployment to preserve mission lineage.");
                    AssertTrue(deployed.CheckRunIds.Contains("chk_objective_deploy"), "Expected deployment check-run linkage.");
                    AssertEqual(ObjectiveStatusEnum.Deployed, deployed.Status);
                    AssertEqual(ObjectiveBacklogStateEnum.Dispatched, deployed.BacklogState);

                    IncidentService incidents = new IncidentService(testDb.Driver);
                    Incident incident = await incidents.CreateAsync(auth, new IncidentUpsertRequest
                    {
                        Title = "Objective Incident",
                        Status = IncidentStatusEnum.Open,
                        Severity = IncidentSeverityEnum.High,
                        EnvironmentId = environment.Id,
                        EnvironmentName = environment.Name,
                        DeploymentId = deployment.Id,
                        ReleaseId = release.Id,
                        VesselId = vessel.Id,
                        MissionId = mission.Id,
                        VoyageId = voyage.Id,
                        RollbackDeploymentId = deployment.Id
                    }).ConfigureAwait(false);

                    Objective withIncident = await objectives.LinkIncidentAsync(auth, created.Id, incident.Id).ConfigureAwait(false);
                    AssertTrue(withIncident.IncidentIds.Contains(incident.Id), "Expected incident link.");
                    AssertTrue(withIncident.DeploymentIds.Contains(deployment.Id), "Expected incident to preserve deployment lineage.");
                    AssertTrue(withIncident.ReleaseIds.Contains(release.Id), "Expected incident to preserve release lineage.");

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

            await RunTest("EnumerateAsync backfills latest snapshot into normalized objective storage", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                ObjectiveService objectives = new ObjectiveService(testDb.Driver);

                string tenantId = "ten_objective_backfill";
                string userId = "usr_objective_backfill";
                await EnsureTenantAndUserAsync(testDb, tenantId, userId).ConfigureAwait(false);

                DateTime createdUtc = DateTime.UtcNow.AddMinutes(-30);
                Objective original = new Objective
                {
                    Id = "obj_backfill_latest_snapshot",
                    TenantId = tenantId,
                    UserId = userId,
                    Title = "Original backlog title",
                    Description = "Original description",
                    Status = ObjectiveStatusEnum.Scoped,
                    Kind = ObjectiveKindEnum.Feature,
                    Priority = ObjectivePriorityEnum.P2,
                    Rank = 100,
                    BacklogState = ObjectiveBacklogStateEnum.Inbox,
                    Effort = ObjectiveEffortEnum.M,
                    CreatedUtc = createdUtc,
                    LastUpdateUtc = createdUtc.AddMinutes(1)
                };

                Objective latest = new Objective
                {
                    Id = original.Id,
                    TenantId = tenantId,
                    UserId = userId,
                    Title = "Latest backlog title",
                    Description = "Latest description",
                    Status = ObjectiveStatusEnum.Planned,
                    Kind = ObjectiveKindEnum.Bug,
                    Priority = ObjectivePriorityEnum.P0,
                    Rank = 5,
                    BacklogState = ObjectiveBacklogStateEnum.ReadyForPlanning,
                    Effort = ObjectiveEffortEnum.S,
                    RefinementSummary = "Use the safer release rollback path.",
                    CreatedUtc = createdUtc,
                    LastUpdateUtc = createdUtc.AddMinutes(10)
                };

                await testDb.Driver.Events.CreateAsync(new ArmadaEvent
                {
                    TenantId = tenantId,
                    UserId = userId,
                    EventType = "objective.snapshot",
                    EntityType = "objective",
                    EntityId = original.Id,
                    Message = original.Title,
                    Payload = JsonSerializer.Serialize(original),
                    CreatedUtc = original.LastUpdateUtc
                }).ConfigureAwait(false);

                await testDb.Driver.Events.CreateAsync(new ArmadaEvent
                {
                    TenantId = tenantId,
                    UserId = userId,
                    EventType = "objective.snapshot",
                    EntityType = "objective",
                    EntityId = latest.Id,
                    Message = latest.Title,
                    Payload = JsonSerializer.Serialize(latest),
                    CreatedUtc = latest.LastUpdateUtc
                }).ConfigureAwait(false);

                AuthContext auth = AuthContext.Authenticated(tenantId, userId, false, true, "UnitTest");
                EnumerationResult<Objective> result = await objectives.EnumerateAsync(auth, new ObjectiveQuery
                {
                    PageNumber = 1,
                    PageSize = 25,
                    Search = "Latest backlog title"
                }).ConfigureAwait(false);

                AssertEqual(1, result.Objects.Count);
                AssertEqual(latest.Id, result.Objects[0].Id);
                AssertEqual(latest.Title, result.Objects[0].Title);
                AssertEqual(latest.Description, result.Objects[0].Description);
                AssertEqual(latest.Status, result.Objects[0].Status);
                AssertEqual(latest.Kind, result.Objects[0].Kind);
                AssertEqual(latest.Priority, result.Objects[0].Priority);
                AssertEqual(latest.Rank, result.Objects[0].Rank);
                AssertEqual(latest.BacklogState, result.Objects[0].BacklogState);
                AssertEqual(latest.Effort, result.Objects[0].Effort);
                AssertEqual(latest.RefinementSummary, result.Objects[0].RefinementSummary);

                Objective? stored = await testDb.Driver.Objectives.ReadAsync(latest.Id).ConfigureAwait(false);
                stored = NotNull(stored);
                AssertEqual(latest.Title, stored.Title);
                AssertEqual(latest.Status, stored.Status);
                AssertEqual(latest.Kind, stored.Kind);
                AssertEqual(latest.Priority, stored.Priority);
                AssertEqual(latest.Rank, stored.Rank);
                AssertEqual(latest.BacklogState, stored.BacklogState);
                AssertEqual(latest.Effort, stored.Effort);
                AssertEqual(latest.RefinementSummary, stored.RefinementSummary);
            }).ConfigureAwait(false);

            await RunTest("MCP update_backlog_item returns structured errors and accepts backlogItemId alias", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                ObjectiveService objectives = new ObjectiveService(testDb.Driver);
                Dictionary<string, Func<JsonElement?, Task<object>>> handlers = new Dictionary<string, Func<JsonElement?, Task<object>>>();
                McpObjectiveTools.Register(
                    (name, _, _, handler) => handlers[name] = handler,
                    testDb.Driver,
                    objectives);

                AuthContext auth = AuthContext.Authenticated(Armada.Core.Constants.DefaultTenantId, Armada.Core.Constants.DefaultUserId, false, true, "UnitTest");
                Objective objective = await objectives.CreateAsync(auth, new ObjectiveUpsertRequest
                {
                    Title = "MCP backlog update",
                    Status = ObjectiveStatusEnum.Draft
                }).ConfigureAwait(false);

                using JsonDocument aliasDoc = JsonDocument.Parse("{\"backlogItemId\":\"" + objective.Id + "\",\"status\":\"Completed\",\"refinementSummary\":\"done\"}");
                object aliasResult = await handlers["update_backlog_item"](aliasDoc.RootElement).ConfigureAwait(false);
                Objective updated = (Objective)aliasResult;
                AssertEqual(ObjectiveStatusEnum.Completed, updated.Status);
                AssertEqual("done", updated.RefinementSummary);

                using JsonDocument missingDoc = JsonDocument.Parse("{\"backlogItemId\":\"obj_missing\",\"status\":\"Completed\"}");
                object missingResult = await handlers["update_backlog_item"](missingDoc.RootElement).ConfigureAwait(false);
                string missingJson = JsonSerializer.Serialize(missingResult);
                AssertContains("backlog_update_failed", missingJson);
                AssertContains("Objective not found", missingJson);

                using JsonDocument noIdDoc = JsonDocument.Parse("{\"status\":\"Completed\"}");
                object noIdResult = await handlers["update_backlog_item"](noIdDoc.RootElement).ConfigureAwait(false);
                AssertContains("backlog_item_id_required", JsonSerializer.Serialize(noIdResult));
            }).ConfigureAwait(false);

            await RunTest("MCP create_backlog_item creates records and returns structured enum errors", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                ObjectiveService objectives = new ObjectiveService(testDb.Driver);
                Dictionary<string, Func<JsonElement?, Task<object>>> handlers = new Dictionary<string, Func<JsonElement?, Task<object>>>();
                McpObjectiveTools.Register(
                    (name, _, _, handler) => handlers[name] = handler,
                    testDb.Driver,
                    objectives);

                using JsonDocument validDoc = JsonDocument.Parse("{\"title\":\"MCP backlog create\",\"kind\":\"Bug\",\"priority\":\"P0\",\"status\":\"Scoped\"}");
                object validResult = await handlers["create_backlog_item"](validDoc.RootElement).ConfigureAwait(false);
                Objective created = (Objective)validResult;
                AssertStartsWith("obj_", created.Id);
                AssertEqual(ObjectiveKindEnum.Bug, created.Kind);
                AssertEqual(ObjectivePriorityEnum.P0, created.Priority);

                using JsonDocument invalidEnumDoc = JsonDocument.Parse("{\"title\":\"Bad backlog create\",\"kind\":\"NotARealKind\"}");
                object invalidEnumResult = await handlers["create_backlog_item"](invalidEnumDoc.RootElement).ConfigureAwait(false);
                string invalidJson = JsonSerializer.Serialize(invalidEnumResult);
                AssertContains("backlog_create_failed", invalidJson);
                AssertContains("ValidEnums", invalidJson);
                AssertContains("Feature", invalidJson);
            }).ConfigureAwait(false);

            await RunTest("DeleteAsync tombstones an objective so a fresh-instance backfill does not resurrect it", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                ObjectiveService objectives = new ObjectiveService(testDb.Driver);

                string tenantId = "ten_objective_tombstone";
                string userId = "usr_objective_tombstone";
                await EnsureTenantAndUserAsync(testDb, tenantId, userId).ConfigureAwait(false);

                AuthContext auth = AuthContext.Authenticated(tenantId, userId, false, true, "UnitTest");
                Objective imported = new Objective
                {
                    Id = "obj_tombstone_core",
                    TenantId = tenantId,
                    UserId = userId,
                    Title = "Tombstone core regression",
                    Status = ObjectiveStatusEnum.Scoped,
                    Kind = ObjectiveKindEnum.Feature,
                    Priority = ObjectivePriorityEnum.P2,
                    Rank = 10,
                    BacklogState = ObjectiveBacklogStateEnum.Inbox,
                    Effort = ObjectiveEffortEnum.M
                };
                await objectives.PersistImportedAsync(auth, imported).ConfigureAwait(false);

                await objectives.DeleteAsync(auth, imported.Id).ConfigureAwait(false);

                // A fresh instance resets _BackfillCompleted so EnumerateAsync re-runs the snapshot backfill.
                ObjectiveService rehydrated = new ObjectiveService(testDb.Driver);
                EnumerationResult<Objective> result = await rehydrated.EnumerateAsync(auth, new ObjectiveQuery
                {
                    PageNumber = 1,
                    PageSize = 25
                }).ConfigureAwait(false);

                foreach (Objective candidate in result.Objects)
                    AssertTrue(!String.Equals(candidate.Id, imported.Id, StringComparison.OrdinalIgnoreCase), "Deleted objective must not reappear after backfill.");

                Objective? read = await rehydrated.ReadAsync(auth, imported.Id).ConfigureAwait(false);
                AssertNull(read);
            }).ConfigureAwait(false);

            await RunTest("DeleteAsync purges null-tenant snapshots that a tenant-admin caller cannot see", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                ObjectiveService objectives = new ObjectiveService(testDb.Driver);

                string tenantId = "ten_objective_mismatch";
                string userId = "usr_objective_mismatch";
                await EnsureTenantAndUserAsync(testDb, tenantId, userId).ConfigureAwait(false);

                // Persist under an admin context so WriteSnapshotAsync stamps the snapshot with a null
                // tenant, mirroring an admin/import-time snapshot that a tenant-admin delete cannot see
                // through its tenant-scoped snapshot read. The objective row stays tenant-owned.
                AuthContext adminAuth = AuthContext.Authenticated(tenantId, userId, true, false, "UnitTest");
                Objective imported = new Objective
                {
                    Id = "obj_tombstone_mismatch",
                    TenantId = tenantId,
                    UserId = userId,
                    Title = "Tenant mismatch resurrection",
                    Status = ObjectiveStatusEnum.Scoped,
                    Kind = ObjectiveKindEnum.Feature,
                    Priority = ObjectivePriorityEnum.P2,
                    Rank = 20,
                    BacklogState = ObjectiveBacklogStateEnum.Inbox,
                    Effort = ObjectiveEffortEnum.M
                };
                await objectives.PersistImportedAsync(adminAuth, imported).ConfigureAwait(false);

                // Delete under a tenant-admin context (the MCP armada_delete_objective path).
                AuthContext tenantAdminAuth = AuthContext.Authenticated(tenantId, userId, false, true, "UnitTest");
                await objectives.DeleteAsync(tenantAdminAuth, imported.Id).ConfigureAwait(false);

                ObjectiveService rehydrated = new ObjectiveService(testDb.Driver);
                EnumerationResult<Objective> result = await rehydrated.EnumerateAsync(adminAuth, new ObjectiveQuery
                {
                    PageNumber = 1,
                    PageSize = 25
                }).ConfigureAwait(false);

                foreach (Objective candidate in result.Objects)
                    AssertTrue(!String.Equals(candidate.Id, imported.Id, StringComparison.OrdinalIgnoreCase), "Null-tenant snapshot must not resurrect after a tenant-admin delete.");

                Objective? read = await rehydrated.ReadAsync(adminAuth, imported.Id).ConfigureAwait(false);
                AssertNull(read);
            }).ConfigureAwait(false);

            await RunTest("ReadAsync refuses to rehydrate a tombstoned objective even when a snapshot survives", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                ObjectiveService objectives = new ObjectiveService(testDb.Driver);

                string tenantId = "ten_objective_readguard";
                string userId = "usr_objective_readguard";
                await EnsureTenantAndUserAsync(testDb, tenantId, userId).ConfigureAwait(false);

                AuthContext auth = AuthContext.Authenticated(tenantId, userId, false, true, "UnitTest");
                Objective imported = new Objective
                {
                    Id = "obj_tombstone_readguard",
                    TenantId = tenantId,
                    UserId = userId,
                    Title = "Read guard objective",
                    Status = ObjectiveStatusEnum.Scoped,
                    Kind = ObjectiveKindEnum.Feature,
                    Priority = ObjectivePriorityEnum.P2,
                    Rank = 30,
                    BacklogState = ObjectiveBacklogStateEnum.Inbox,
                    Effort = ObjectiveEffortEnum.M
                };
                await objectives.PersistImportedAsync(auth, imported).ConfigureAwait(false);
                await objectives.DeleteAsync(auth, imported.Id).ConfigureAwait(false);

                // Simulate a snapshot that escaped the delete-time purge; the tombstone must still win.
                await testDb.Driver.Events.CreateAsync(new ArmadaEvent
                {
                    TenantId = tenantId,
                    UserId = userId,
                    EventType = "objective.snapshot",
                    EntityType = "objective",
                    EntityId = imported.Id,
                    Message = imported.Title,
                    Payload = JsonSerializer.Serialize(imported),
                    CreatedUtc = DateTime.UtcNow
                }).ConfigureAwait(false);

                Objective? read = await objectives.ReadAsync(auth, imported.Id).ConfigureAwait(false);
                AssertNull(read);
            }).ConfigureAwait(false);

            await RunTest("PersistImportedAsync clears a tombstone so a re-created objective becomes visible again", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                ObjectiveService objectives = new ObjectiveService(testDb.Driver);

                string tenantId = "ten_objective_repersist";
                string userId = "usr_objective_repersist";
                await EnsureTenantAndUserAsync(testDb, tenantId, userId).ConfigureAwait(false);

                AuthContext auth = AuthContext.Authenticated(tenantId, userId, false, true, "UnitTest");
                Objective imported = new Objective
                {
                    Id = "obj_tombstone_repersist",
                    TenantId = tenantId,
                    UserId = userId,
                    Title = "Repersist objective",
                    Status = ObjectiveStatusEnum.Scoped,
                    Kind = ObjectiveKindEnum.Feature,
                    Priority = ObjectivePriorityEnum.P2,
                    Rank = 40,
                    BacklogState = ObjectiveBacklogStateEnum.Inbox,
                    Effort = ObjectiveEffortEnum.M
                };
                await objectives.PersistImportedAsync(auth, imported).ConfigureAwait(false);
                await objectives.DeleteAsync(auth, imported.Id).ConfigureAwait(false);

                // A deliberate re-import of the same id must clear the stale tombstone.
                Objective reimported = new Objective
                {
                    Id = imported.Id,
                    TenantId = tenantId,
                    UserId = userId,
                    Title = "Repersist objective revived",
                    Status = ObjectiveStatusEnum.Planned,
                    Kind = ObjectiveKindEnum.Feature,
                    Priority = ObjectivePriorityEnum.P1,
                    Rank = 41,
                    BacklogState = ObjectiveBacklogStateEnum.ReadyForPlanning,
                    Effort = ObjectiveEffortEnum.M
                };
                await objectives.PersistImportedAsync(auth, reimported).ConfigureAwait(false);

                ObjectiveService rehydrated = new ObjectiveService(testDb.Driver);
                Objective? read = await rehydrated.ReadAsync(auth, imported.Id).ConfigureAwait(false);
                read = NotNull(read);
                AssertEqual("Repersist objective revived", read.Title);

                bool present = false;
                EnumerationResult<Objective> result = await rehydrated.EnumerateAsync(auth, new ObjectiveQuery
                {
                    PageNumber = 1,
                    PageSize = 25
                }).ConfigureAwait(false);
                foreach (Objective candidate in result.Objects)
                {
                    if (String.Equals(candidate.Id, imported.Id, StringComparison.OrdinalIgnoreCase))
                        present = true;
                }
                AssertTrue(present, "Re-persisted objective must be visible after a fresh-instance backfill.");
            }).ConfigureAwait(false);

            await RunTest("DeleteAsync throws when the objective has neither a row nor any snapshots", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                ObjectiveService objectives = new ObjectiveService(testDb.Driver);

                string tenantId = "ten_objective_notfound";
                string userId = "usr_objective_notfound";
                await EnsureTenantAndUserAsync(testDb, tenantId, userId).ConfigureAwait(false);

                AuthContext auth = AuthContext.Authenticated(tenantId, userId, false, true, "UnitTest");

                // The "not found if no row AND no snapshots" guard must survive the tenant-agnostic
                // purge rewrite: deleting an id that was never imported still throws.
                await AssertThrowsAsync<InvalidOperationException>(async () =>
                    await objectives.DeleteAsync(auth, "obj_never_existed").ConfigureAwait(false)).ConfigureAwait(false);
            }).ConfigureAwait(false);

            await RunTest("DeleteAsync a second time still throws because the tombstone is not counted as a snapshot", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                ObjectiveService objectives = new ObjectiveService(testDb.Driver);

                string tenantId = "ten_objective_doubledelete";
                string userId = "usr_objective_doubledelete";
                await EnsureTenantAndUserAsync(testDb, tenantId, userId).ConfigureAwait(false);

                AuthContext auth = AuthContext.Authenticated(tenantId, userId, false, true, "UnitTest");
                Objective imported = new Objective
                {
                    Id = "obj_tombstone_doubledelete",
                    TenantId = tenantId,
                    UserId = userId,
                    Title = "Double delete objective",
                    Status = ObjectiveStatusEnum.Scoped,
                    Kind = ObjectiveKindEnum.Feature,
                    Priority = ObjectivePriorityEnum.P2,
                    Rank = 50,
                    BacklogState = ObjectiveBacklogStateEnum.Inbox,
                    Effort = ObjectiveEffortEnum.M
                };
                await objectives.PersistImportedAsync(auth, imported).ConfigureAwait(false);
                await objectives.DeleteAsync(auth, imported.Id).ConfigureAwait(false);

                // After delete the row is gone and the snapshots are purged, but an objective.deleted
                // tombstone event remains for the id. The not-found guard reads only objective.snapshot
                // events, so the tombstone must not be mistaken for a surviving snapshot.
                await AssertThrowsAsync<InvalidOperationException>(async () =>
                    await objectives.DeleteAsync(auth, imported.Id).ConfigureAwait(false)).ConfigureAwait(false);
            }).ConfigureAwait(false);

            await RunTest("BackfillFromSnapshotsAsync skips a snapshot that survived the delete purge when the id is tombstoned", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                ObjectiveService objectives = new ObjectiveService(testDb.Driver);

                string tenantId = "ten_objective_backfillskip";
                string userId = "usr_objective_backfillskip";
                await EnsureTenantAndUserAsync(testDb, tenantId, userId).ConfigureAwait(false);

                AuthContext auth = AuthContext.Authenticated(tenantId, userId, false, true, "UnitTest");
                Objective imported = new Objective
                {
                    Id = "obj_tombstone_backfillskip",
                    TenantId = tenantId,
                    UserId = userId,
                    Title = "Backfill skip objective",
                    Status = ObjectiveStatusEnum.Scoped,
                    Kind = ObjectiveKindEnum.Feature,
                    Priority = ObjectivePriorityEnum.P2,
                    Rank = 60,
                    BacklogState = ObjectiveBacklogStateEnum.Inbox,
                    Effort = ObjectiveEffortEnum.M
                };
                await objectives.PersistImportedAsync(auth, imported).ConfigureAwait(false);
                await objectives.DeleteAsync(auth, imported.Id).ConfigureAwait(false);

                // Inject a snapshot that escaped the delete-time purge (e.g. raced the delete). Without
                // the tombstone this projectable snapshot would resurrect the objective during backfill;
                // the BackfillFromSnapshotsAsync skip branch must drop it before upserting a row.
                await testDb.Driver.Events.CreateAsync(new ArmadaEvent
                {
                    TenantId = tenantId,
                    UserId = userId,
                    EventType = "objective.snapshot",
                    EntityType = "objective",
                    EntityId = imported.Id,
                    Message = imported.Title,
                    Payload = JsonSerializer.Serialize(imported),
                    CreatedUtc = DateTime.UtcNow
                }).ConfigureAwait(false);

                // Fresh instance resets _BackfillCompleted so EnumerateAsync re-runs the backfill loop
                // (the read-path rehydrate guard is a different code path and is covered separately).
                ObjectiveService rehydrated = new ObjectiveService(testDb.Driver);
                EnumerationResult<Objective> result = await rehydrated.EnumerateAsync(auth, new ObjectiveQuery
                {
                    PageNumber = 1,
                    PageSize = 25
                }).ConfigureAwait(false);

                foreach (Objective candidate in result.Objects)
                    AssertTrue(!String.Equals(candidate.Id, imported.Id, StringComparison.OrdinalIgnoreCase), "A surviving snapshot must not resurrect a tombstoned objective during backfill.");

                Objective? read = await rehydrated.ReadAsync(auth, imported.Id).ConfigureAwait(false);
                AssertNull(read);
            }).ConfigureAwait(false);

            await RunTest("Deleting one objective leaves an untombstoned sibling visible after a fresh-instance backfill", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                ObjectiveService objectives = new ObjectiveService(testDb.Driver);

                string tenantId = "ten_objective_sibling";
                string userId = "usr_objective_sibling";
                await EnsureTenantAndUserAsync(testDb, tenantId, userId).ConfigureAwait(false);

                AuthContext auth = AuthContext.Authenticated(tenantId, userId, false, true, "UnitTest");
                Objective deleted = new Objective
                {
                    Id = "obj_tombstone_sibling_deleted",
                    TenantId = tenantId,
                    UserId = userId,
                    Title = "Sibling deleted",
                    Status = ObjectiveStatusEnum.Scoped,
                    Kind = ObjectiveKindEnum.Feature,
                    Priority = ObjectivePriorityEnum.P2,
                    Rank = 70,
                    BacklogState = ObjectiveBacklogStateEnum.Inbox,
                    Effort = ObjectiveEffortEnum.M
                };
                Objective survivor = new Objective
                {
                    Id = "obj_tombstone_sibling_survivor",
                    TenantId = tenantId,
                    UserId = userId,
                    Title = "Sibling survivor",
                    Status = ObjectiveStatusEnum.Scoped,
                    Kind = ObjectiveKindEnum.Feature,
                    Priority = ObjectivePriorityEnum.P2,
                    Rank = 71,
                    BacklogState = ObjectiveBacklogStateEnum.Inbox,
                    Effort = ObjectiveEffortEnum.M
                };
                await objectives.PersistImportedAsync(auth, deleted).ConfigureAwait(false);
                await objectives.PersistImportedAsync(auth, survivor).ConfigureAwait(false);

                // Only the deleted id gets a tombstone. The tombstone set must be keyed precisely by id,
                // so the untouched sibling must still rehydrate through the fresh-instance backfill.
                await objectives.DeleteAsync(auth, deleted.Id).ConfigureAwait(false);

                ObjectiveService rehydrated = new ObjectiveService(testDb.Driver);
                EnumerationResult<Objective> result = await rehydrated.EnumerateAsync(auth, new ObjectiveQuery
                {
                    PageNumber = 1,
                    PageSize = 25
                }).ConfigureAwait(false);

                bool survivorPresent = false;
                foreach (Objective candidate in result.Objects)
                {
                    AssertTrue(!String.Equals(candidate.Id, deleted.Id, StringComparison.OrdinalIgnoreCase), "Tombstoned objective must not reappear after backfill.");
                    if (String.Equals(candidate.Id, survivor.Id, StringComparison.OrdinalIgnoreCase))
                        survivorPresent = true;
                }
                AssertTrue(survivorPresent, "Untombstoned sibling must remain visible after a fresh-instance backfill.");

                Objective? survivorRead = await rehydrated.ReadAsync(auth, survivor.Id).ConfigureAwait(false);
                AssertNotNull(survivorRead);
                Objective? deletedRead = await rehydrated.ReadAsync(auth, deleted.Id).ConfigureAwait(false);
                AssertNull(deletedRead);
            }).ConfigureAwait(false);

            await RunTest("Incident links are validated at write time but tolerated when already dangling", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                ObjectiveService objectives = new ObjectiveService(testDb.Driver);
                IncidentService incidents = new IncidentService(testDb.Driver);

                string tenantId = "ten_incident_link";
                string userId = "usr_incident_link";
                await EnsureTenantAndUserAsync(testDb, tenantId, userId).ConfigureAwait(false);
                AuthContext auth = AuthContext.Authenticated(tenantId, userId, false, true, "UnitTest");

                // AC1 + AC3: linking an incident that does not exist is rejected, and the error names
                // the offending id rather than surfacing as a generic internal failure.
                Objective target = await objectives.CreateAsync(auth, new ObjectiveUpsertRequest
                {
                    Title = "Objective with incident lineage"
                }).ConfigureAwait(false);

                string missingIncidentId = "inc_does_not_exist";
                string linkError = "";
                try
                {
                    await objectives.LinkIncidentAsync(auth, target.Id, missingIncidentId).ConfigureAwait(false);
                }
                catch (InvalidOperationException ex)
                {
                    linkError = ex.Message;
                }
                AssertContains("not found or not accessible", linkError, "A missing incident must be rejected with a specific message");
                AssertContains(missingIncidentId, linkError, "The rejection must name the offending incident id");

                // AC1: the same strictness applies on the ordinary write path.
                string writeError = "";
                try
                {
                    await objectives.CreateAsync(auth, new ObjectiveUpsertRequest
                    {
                        Title = "Objective naming a missing incident",
                        IncidentIds = new List<string> { missingIncidentId }
                    }).ConfigureAwait(false);
                }
                catch (InvalidOperationException ex)
                {
                    writeError = ex.Message;
                }
                AssertContains(missingIncidentId, writeError, "Creating an objective that names a missing incident must be rejected");

                // AC2: an incident that WAS valid and later disappeared must not block an unrelated
                // link operation. This is the dispatch path: LinkVoyageAsync runs after the voyage
                // already exists, so throwing here stranded a live voyage behind an internal error.
                Incident incident = await incidents.CreateAsync(auth, new IncidentUpsertRequest
                {
                    Title = "Transient incident",
                    Status = IncidentStatusEnum.Open,
                    Severity = IncidentSeverityEnum.Medium
                }).ConfigureAwait(false);

                Objective linked = await objectives.LinkIncidentAsync(auth, target.Id, incident.Id).ConfigureAwait(false);
                AssertTrue(linked.IncidentIds.Contains(incident.Id), "Incident link should be recorded while the incident exists");

                await incidents.DeleteAsync(auth, incident.Id).ConfigureAwait(false);

                Voyage voyage = new Voyage("Voyage over a dangling incident link");
                voyage.TenantId = tenantId;
                voyage.UserId = userId;
                await testDb.Driver.Voyages.CreateAsync(voyage).ConfigureAwait(false);

                Objective afterVoyageLink = await objectives.LinkVoyageAsync(auth, target.Id, voyage.Id).ConfigureAwait(false);

                AssertTrue(afterVoyageLink.VoyageIds.Contains(voyage.Id),
                    "Linking a voyage must succeed even though the objective carries a dangling incident reference");
                AssertTrue(afterVoyageLink.IncidentIds.Contains(incident.Id),
                    "The stale incident id is left in place, not silently pruned, because a read can also miss on scoping");
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
