namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text.Json;
    using Microsoft.Data.Sqlite;
    using Armada.Core.Enums;
    using Armada.Core.Database;
    using Armada.Core.Database.Mysql;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Server.Mcp.Tools;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;
    using MysqlTableQueries = Armada.Core.Database.Mysql.Queries.TableQueries;
    using PostgresqlTableQueries = Armada.Core.Database.Postgresql.Queries.TableQueries;
    using SqliteTableQueries = Armada.Core.Database.Sqlite.Queries.TableQueries;
    using SqlServerTableQueries = Armada.Core.Database.SqlServer.Queries.TableQueries;

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
            await RunTest("DeleteAsync reports the deleted objective to the deletion callback", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ObjectiveService objectives = new ObjectiveService(testDb.Driver);
                    string tenantId = "ten_objective_delete";
                    string userId = "usr_objective_delete";
                    await EnsureTenantAndUserAsync(testDb, tenantId, userId).ConfigureAwait(false);
                    AuthContext auth = AuthContext.Authenticated(tenantId, userId, false, true, "UnitTest");

                    Objective created = await objectives.CreateAsync(auth, new ObjectiveUpsertRequest
                    {
                        Title = "Objective to delete",
                        Status = ObjectiveStatusEnum.Scoped
                    }).ConfigureAwait(false);

                    List<Objective> deleted = new List<Objective>();
                    objectives.OnObjectiveDeleted = objective => deleted.Add(objective);
                    await objectives.DeleteAsync(auth, created.Id).ConfigureAwait(false);

                    AssertEqual(1, deleted.Count, "one deletion is reported once");
                    AssertEqual(created.Id, deleted[0].Id, "the callback names the deleted objective");
                    AssertEqual(tenantId, deleted[0].TenantId, "the callback carries the owning tenant for delivery scope");
                    AssertEqual(userId, deleted[0].UserId, "the callback carries the owning user for delivery scope");
                }
            }).ConfigureAwait(false);

            await RunTest("LinkIncidentAsync is an annotation: a never-dispatched objective stays auto-dispatch eligible", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                ObjectiveService objectives = new ObjectiveService(testDb.Driver);
                IncidentService incidents = new IncidentService(testDb.Driver);

                string tenantId = "ten_incident_link";
                string userId = "usr_incident_link";
                await EnsureTenantAndUserAsync(testDb, tenantId, userId).ConfigureAwait(false);
                AuthContext auth = AuthContext.Authenticated(tenantId, userId, false, false, "UnitTest");

                Vessel vessel = new Vessel
                {
                    TenantId = tenantId,
                    UserId = userId,
                    Name = "Incident Link Vessel",
                    RepoUrl = "file:///tmp/incident-link.git",
                    LocalPath = Path.GetTempPath(),
                    WorkingDirectory = Path.GetTempPath(),
                    DefaultBranch = "main"
                };
                await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                // The voyage belongs to some OTHER objective's failed run; this incident merely records it.
                Voyage otherVoyage = new Voyage("Another objective's failed voyage")
                {
                    TenantId = tenantId,
                    UserId = userId,
                    Status = VoyageStatusEnum.Failed
                };
                await testDb.Driver.Voyages.CreateAsync(otherVoyage).ConfigureAwait(false);
                Mission otherMission = new Mission("Failed stage of the other voyage")
                {
                    TenantId = tenantId,
                    UserId = userId,
                    VesselId = vessel.Id,
                    VoyageId = otherVoyage.Id,
                    Status = MissionStatusEnum.Failed
                };
                await testDb.Driver.Missions.CreateAsync(otherMission).ConfigureAwait(false);

                Objective never = await objectives.CreateAsync(auth, new ObjectiveUpsertRequest
                {
                    Title = "Owns the fix, was never dispatched",
                    Status = ObjectiveStatusEnum.Scoped,
                    VesselIds = new List<string> { vessel.Id }
                }).ConfigureAwait(false);
                never = await objectives.UpdateAsync(auth, never.Id, new ObjectiveUpsertRequest
                {
                    AutoDispatchEnabled = true
                }).ConfigureAwait(false);
                AssertTrue(never.AutoDispatchEnabled, "Precondition: the objective is auto-dispatch enabled.");
                AssertEqual(1, AutonomousObjectiveSelector.SelectEligible(new List<Objective> { never }).Count,
                    "Precondition: a Scoped, auto-enabled, never-dispatched objective is eligible.");

                Incident incident = await incidents.CreateAsync(auth, new IncidentUpsertRequest
                {
                    Title = "Mission failed in the other voyage",
                    Status = IncidentStatusEnum.Open,
                    Severity = IncidentSeverityEnum.Medium,
                    VesselId = vessel.Id,
                    MissionId = otherMission.Id,
                    VoyageId = otherVoyage.Id
                }).ConfigureAwait(false);

                Objective linked = await objectives.LinkIncidentAsync(auth, never.Id, incident.Id).ConfigureAwait(false);

                AssertTrue(linked.IncidentIds.Contains(incident.Id), "The incident link itself is recorded.");
                AssertTrue(linked.VesselIds.Contains(vessel.Id), "Delivery context (vessel) is still carried.");
                AssertEqual(0, linked.VoyageIds.Count, "The incident's voyage must not become the objective's dispatch lineage.");
                AssertEqual(0, linked.MissionIds.Count, "The incident's mission must not become the objective's dispatch lineage.");

                Objective? reloaded = await objectives.ReadAsync(auth, never.Id).ConfigureAwait(false);
                reloaded = NotNull(reloaded);
                AssertEqual(0, reloaded.VoyageIds.Count, "Persisted objective carries no voyage from the incident link.");
                AssertEqual(1, AutonomousObjectiveSelector.SelectEligible(new List<Objective> { reloaded }).Count,
                    "Annotating the objective with an incident must leave it auto-dispatch eligible.");
            }).ConfigureAwait(false);

            await RunTest("MCP update_backlog_item returns structured errors and accepts backlogItemId alias", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                ObjectiveService objectives = new ObjectiveService(testDb.Driver);
                Dictionary<string, Func<JsonElement?, Task<object>>> handlers = new Dictionary<string, Func<JsonElement?, Task<object>>>();
                McpObjectiveTools.Register(
                    (name, _, _, handler) => handlers[name] = McpTestCaller.Wrap(handler),
                    testDb.Driver,
                    objectives);

                AuthContext auth = AuthContext.Authenticated(Armada.Core.Constants.DefaultTenantId, Armada.Core.Constants.DefaultUserId, false, true, "UnitTest");
                Objective objective = await objectives.CreateAsync(auth, new ObjectiveUpsertRequest
                {
                    Title = "MCP backlog update",
                    Status = ObjectiveStatusEnum.Draft
                }).ConfigureAwait(false);

                using JsonDocument aliasDoc = JsonDocument.Parse("{\"backlogItemId\":\"" + objective.Id + "\",\"status\":\"Completed\",\"refinementSummary\":\"done\",\"preparation\":{\"target\":{\"vesselId\":\"vsl_mcp\",\"ref\":\"main\",\"resolvedCommit\":\"abc123\"},\"claims\":[{\"id\":\"opc_mcp_update\",\"kind\":\"DispatchEntryPoint\",\"text\":\"Use the dispatch entry point\",\"dependsOn\":\"Target\",\"state\":\"Verified\"}]}}");
                object aliasResult = await handlers["update_backlog_item"](aliasDoc.RootElement).ConfigureAwait(false);
                Objective updated = (Objective)aliasResult;
                AssertEqual(ObjectiveStatusEnum.Completed, updated.Status);
                AssertEqual("done", updated.RefinementSummary);
                AssertEqual("abc123", updated.Preparation.Target?.ResolvedCommit);
                AssertEqual("opc_mcp_update", updated.Preparation.Claims[0].Id);

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
                Dictionary<string, object> schemas = new Dictionary<string, object>();
                McpObjectiveTools.Register(
                    (name, _, schema, handler) =>
                    {
                        handlers[name] = McpTestCaller.Wrap(handler);
                        schemas[name] = schema;
                    },
                    testDb.Driver,
                    objectives);

                foreach (string toolName in new[] { "create_objective", "create_backlog_item", "update_objective", "update_backlog_item" })
                {
                    string objectiveSchema = JsonSerializer.Serialize(schemas[toolName]);
                    AssertContains("suggestedPlaybooks", objectiveSchema);
                    AssertContains("playbookId", objectiveSchema);
                    AssertContains("deliveryMode", objectiveSchema);
                }

                string createSchema = JsonSerializer.Serialize(schemas["create_backlog_item"]);
                AssertContains("requiredForDispatch", createSchema);
                AssertContains("requiredClaimKinds", createSchema);
                AssertContains("requiredSiblingInputs", createSchema);
                AssertContains("requiredArtifactPaths", createSchema);

                using JsonDocument validDoc = JsonDocument.Parse("{\"title\":\"MCP backlog create\",\"kind\":\"Bug\",\"priority\":\"P0\",\"status\":\"Scoped\",\"suggestedPlaybooks\":[{\"playbookId\":\"pbk_mcp\",\"deliveryMode\":\"InstructionWithReference\"}],\"preparation\":{\"requiredForDispatch\":true,\"requiredClaimKinds\":[\"SourcePath\"],\"requiredSiblingInputs\":[{\"vesselRef\":\"ReferenceSource\",\"relativePath\":\"../ReferenceSource\",\"requiredArtifactPaths\":[]}],\"source\":{\"vesselId\":\"vsl_source\",\"ref\":\"main\",\"resolvedCommit\":\"def456\"},\"claims\":[{\"id\":\"opc_mcp_create\",\"kind\":\"SourcePath\",\"text\":\"Read src/Entry.cs\",\"dependsOn\":\"Source\",\"state\":\"Verified\"}]}}");
                object validResult = await handlers["create_backlog_item"](validDoc.RootElement).ConfigureAwait(false);
                Objective created = (Objective)validResult;
                AssertStartsWith("obj_", created.Id);
                AssertEqual(ObjectiveKindEnum.Bug, created.Kind);
                AssertEqual(ObjectivePriorityEnum.P0, created.Priority);
                AssertEqual("def456", created.Preparation.Source?.ResolvedCommit);
                AssertEqual("opc_mcp_create", created.Preparation.Claims[0].Id);
                AssertTrue(created.Preparation.RequiredForDispatch);
                AssertEqual(ObjectivePreparationClaimKindEnum.SourcePath, created.Preparation.RequiredClaimKinds[0]);
                AssertEqual("ReferenceSource", created.Preparation.RequiredSiblingInputs[0].VesselRef);
                AssertEqual("pbk_mcp", created.SuggestedPlaybooks[0].PlaybookId);
                AssertEqual(PlaybookDeliveryModeEnum.InstructionWithReference, created.SuggestedPlaybooks[0].DeliveryMode);

                using JsonDocument invalidEnumDoc = JsonDocument.Parse("{\"title\":\"Bad backlog create\",\"kind\":\"NotARealKind\"}");
                object invalidEnumResult = await handlers["create_backlog_item"](invalidEnumDoc.RootElement).ConfigureAwait(false);
                string invalidJson = JsonSerializer.Serialize(invalidEnumResult);
                AssertContains("backlog_create_failed", invalidJson);
                AssertContains("ValidEnums", invalidJson);
                AssertContains("Feature", invalidJson);
            }).ConfigureAwait(false);

            await RunTest("MCP preview_objective_dispatch returns the shared persisted-objective preview", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                using LoggingModule logging = new LoggingModule();
                ObjectiveService objectives = new ObjectiveService(testDb.Driver);
                WorkflowProfileService profiles = new WorkflowProfileService(testDb.Driver, logging);
                VesselReadinessService readiness = new VesselReadinessService(testDb.Driver, profiles, logging);
                ObjectiveDispatchPreviewService previews = new ObjectiveDispatchPreviewService(
                    testDb.Driver,
                    profiles,
                    readiness,
                    new GitService(logging),
                    new ArmadaSettings());
                Dictionary<string, Func<JsonElement?, Task<object>>> handlers = new Dictionary<string, Func<JsonElement?, Task<object>>>();
                McpObjectiveTools.Register(
                    (name, _, _, handler) => handlers[name] = McpTestCaller.Wrap(handler),
                    testDb.Driver,
                    objectives,
                    dispatchPreviewService: previews);

                AuthContext auth = AuthContext.Authenticated(
                    Armada.Core.Constants.DefaultTenantId,
                    Armada.Core.Constants.DefaultUserId,
                    false,
                    true,
                    "UnitTest");
                Objective objective = await objectives.CreateAsync(auth, new ObjectiveUpsertRequest
                {
                    Title = "MCP dispatch preview",
                    Description = "Preview this persisted objective without creating fleet state.",
                    RefinementSummary = "Use the shared preview service.",
                    AcceptanceCriteria = new List<string> { "Return all blockers." }
                }).ConfigureAwait(false);

                using JsonDocument previewDoc = JsonDocument.Parse("{\"objectiveId\":\"" + objective.Id + "\"}");
                object previewResult = await handlers["preview_objective_dispatch"](previewDoc.RootElement).ConfigureAwait(false);
                ObjectiveDispatchPreview preview = (ObjectiveDispatchPreview)previewResult;
                AssertEqual(objective.Id, preview.ObjectiveId);
                AssertFalse(preview.IsReady, "an objective with no target vessel must not be ready");
                AssertTrue(preview.Issues.Exists(issue => issue.Code == "target_vessel_count"));

                using JsonDocument missingDoc = JsonDocument.Parse("{\"objectiveId\":\"missing-objective\"}");
                object missingResult = await handlers["preview_objective_dispatch"](missingDoc.RootElement).ConfigureAwait(false);
                string missingJson = JsonSerializer.Serialize(missingResult);
                AssertContains("objective_not_found", missingJson);
                AssertContains("missing-objective", missingJson);
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

            await RunTest("DeleteAsync leaves another tenant's orphan snapshots alone and reports not found", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                ObjectiveService objectives = new ObjectiveService(testDb.Driver);

                await EnsureTenantAndUserAsync(testDb, "ten_orphan_owner", "usr_orphan_owner").ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb, "ten_orphan_other", "usr_orphan_other").ConfigureAwait(false);
                AuthContext owner = AuthContext.Authenticated("ten_orphan_owner", "usr_orphan_owner", false, true, "UnitTest");
                AuthContext other = AuthContext.Authenticated("ten_orphan_other", "usr_orphan_other", false, true, "UnitTest");

                // Finish the one-time backfill first, so the snapshot written next has no objective row anywhere.
                await objectives.EnumerateAsync(owner, new ObjectiveQuery { PageNumber = 1, PageSize = 1 }).ConfigureAwait(false);
                Objective orphan = new Objective
                {
                    Id = "obj_orphanforeign",
                    TenantId = "ten_orphan_owner",
                    UserId = "usr_orphan_owner",
                    Title = "Orphan snapshot",
                    Status = ObjectiveStatusEnum.Scoped
                };
                await testDb.Driver.Events.CreateAsync(new ArmadaEvent
                {
                    TenantId = orphan.TenantId,
                    UserId = orphan.UserId,
                    EventType = "objective.snapshot",
                    EntityType = "objective",
                    EntityId = orphan.Id,
                    Message = orphan.Title,
                    Payload = JsonSerializer.Serialize(orphan),
                    CreatedUtc = DateTime.UtcNow
                }).ConfigureAwait(false);

                await AssertThrowsAsync<InvalidOperationException>(async () =>
                    await objectives.DeleteAsync(other, orphan.Id).ConfigureAwait(false)).ConfigureAwait(false);
                List<ArmadaEvent> remaining = await testDb.Driver.Events.EnumerateByEntityAsync("objective", orphan.Id, 100).ConfigureAwait(false);
                AssertEqual(1, remaining.Count(item => item.EventType == "objective.snapshot"), "another tenant's delete purges nothing");

                await objectives.DeleteAsync(owner, orphan.Id).ConfigureAwait(false);
                remaining = await testDb.Driver.Events.EnumerateByEntityAsync("objective", orphan.Id, 100).ConfigureAwait(false);
                AssertEqual(0, remaining.Count(item => item.EventType == "objective.snapshot"), "the owning tenant still purges its orphan snapshots");
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

                // An edit that does not touch incidents must not be blocked by the stale one. Found in
                // real use: retagging this objective's vessels failed with
                // "Incident not found or not accessible", making any objective that carried a dangling
                // incident permanently uneditable.
                Objective retagged = await objectives.UpdateAsync(auth, target.Id, new ObjectiveUpsertRequest
                {
                    Tags = new List<string> { "retagged" }
                }).ConfigureAwait(false);
                AssertTrue(retagged.Tags.Contains("retagged"),
                    "An update that never mentions incidents must succeed despite a pre-existing dangling incident");

                // ...but supplying a bad incident id explicitly is still rejected.
                string suppliedError = "";
                try
                {
                    await objectives.UpdateAsync(auth, target.Id, new ObjectiveUpsertRequest
                    {
                        IncidentIds = new List<string> { "inc_definitely_missing" }
                    }).ConfigureAwait(false);
                }
                catch (InvalidOperationException ex)
                {
                    suppliedError = ex.Message;
                }
                AssertContains("inc_definitely_missing", suppliedError,
                    "Explicitly supplying a missing incident id must still be rejected on update");
            }).ConfigureAwait(false);

            await RunTest("UpdateAsync tolerates unchanged legacy links but validates caller-supplied links", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                ObjectiveService objectives = new ObjectiveService(testDb.Driver);

                string tenantId = "ten_objective_legacy_links";
                string userId = "usr_objective_legacy_links";
                await EnsureTenantAndUserAsync(testDb, tenantId, userId).ConfigureAwait(false);
                AuthContext auth = AuthContext.Authenticated(tenantId, userId, false, true, "UnitTest");

                // Simulate a migrated objective whose linked entities are no longer visible through
                // the current user-scoped readers. Import/migration historically persisted these
                // rows directly, so seed it below the service validation boundary.
                Objective legacy = new Objective
                {
                    Id = "obj_legacy_inaccessible_links",
                    TenantId = tenantId,
                    UserId = userId,
                    Title = "Legacy linked objective",
                    Status = ObjectiveStatusEnum.Completed,
                    Kind = ObjectiveKindEnum.Feature,
                    Priority = ObjectivePriorityEnum.P2,
                    Rank = 80,
                    BacklogState = ObjectiveBacklogStateEnum.Dispatched,
                    Effort = ObjectiveEffortEnum.M,
                    FleetIds = new List<string> { "flt_legacy_inaccessible" },
                    VesselIds = new List<string> { "vsl_legacy_inaccessible" }
                };
                await testDb.Driver.Objectives.CreateAsync(legacy).ConfigureAwait(false);

                Objective evidenced = await objectives.UpdateAsync(auth, legacy.Id, new ObjectiveUpsertRequest
                {
                    EvidenceLinks = new List<string> { "commit:abc123" },
                    RefinementSummary = "Verified from repository history."
                }).ConfigureAwait(false);

                AssertTrue(evidenced.EvidenceLinks.Contains("commit:abc123"),
                    "An unrelated evidence update must not revalidate unchanged legacy links");
                AssertTrue(evidenced.FleetIds.Contains("flt_legacy_inaccessible"),
                    "The unchanged legacy fleet link must be preserved");
                AssertTrue(evidenced.VesselIds.Contains("vsl_legacy_inaccessible"),
                    "The unchanged legacy vessel link must be preserved");

                string fleetError = "";
                try
                {
                    await objectives.UpdateAsync(auth, legacy.Id, new ObjectiveUpsertRequest
                    {
                        FleetIds = new List<string> { "flt_legacy_inaccessible" }
                    }).ConfigureAwait(false);
                }
                catch (InvalidOperationException ex)
                {
                    fleetError = ex.Message;
                }
                AssertContains("flt_legacy_inaccessible", fleetError,
                    "Explicitly supplying an inaccessible fleet must remain a strict validation error");

                string vesselError = "";
                try
                {
                    await objectives.UpdateAsync(auth, legacy.Id, new ObjectiveUpsertRequest
                    {
                        VesselIds = new List<string> { "vsl_new_inaccessible" }
                    }).ConfigureAwait(false);
                }
                catch (InvalidOperationException ex)
                {
                    vesselError = ex.Message;
                }
                AssertContains("vsl_new_inaccessible", vesselError,
                    "Explicitly supplying an inaccessible vessel must remain a strict validation error");
            }).ConfigureAwait(false);

            await RunTest("UpdateAsync accepts a shared fleet with NULL tenant and rejects a genuinely missing fleet", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                ObjectiveService objectives = new ObjectiveService(testDb.Driver);

                string tenantId = "ten_objective_shared_fleet";
                string userId = "usr_objective_shared_fleet";
                await EnsureTenantAndUserAsync(testDb, tenantId, userId).ConfigureAwait(false);
                AuthContext auth = AuthContext.Authenticated(tenantId, userId, false, true, "UnitTest");

                // A shared/global fleet carries no tenant id; the tenant-scoped read cannot match it,
                // but linking it to an objective must still succeed (the read path resolves it by id).
                Fleet shared = new Fleet("Shared Fleet")
                {
                    Id = "flt_shared_null_tenant",
                    TenantId = null
                };
                await testDb.Driver.Fleets.CreateAsync(shared).ConfigureAwait(false);

                Objective objective = new Objective
                {
                    Id = "obj_shared_fleet_link",
                    TenantId = tenantId,
                    UserId = userId,
                    Title = "Objective with a shared fleet link",
                    Status = ObjectiveStatusEnum.Scoped,
                    Kind = ObjectiveKindEnum.Feature,
                    Priority = ObjectivePriorityEnum.P2,
                    BacklogState = ObjectiveBacklogStateEnum.Triaged
                };
                await testDb.Driver.Objectives.CreateAsync(objective).ConfigureAwait(false);

                Objective updated = await objectives.UpdateAsync(auth, objective.Id, new ObjectiveUpsertRequest
                {
                    FleetIds = new List<string> { "flt_shared_null_tenant" }
                }).ConfigureAwait(false);

                AssertTrue(updated.FleetIds.Contains("flt_shared_null_tenant"),
                    "A shared fleet with NULL tenant must be linkable to an objective");

                string missingError = "";
                try
                {
                    await objectives.UpdateAsync(auth, objective.Id, new ObjectiveUpsertRequest
                    {
                        FleetIds = new List<string> { "flt_does_not_exist" }
                    }).ConfigureAwait(false);
                }
                catch (InvalidOperationException ex)
                {
                    missingError = ex.Message;
                }
                AssertContains("flt_does_not_exist", missingError,
                    "A genuinely missing fleet must remain a strict validation error");
            }).ConfigureAwait(false);

            await RunTest("Preparation anchors selectively invalidate only dependent existing claims", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                ObjectiveService objectives = new ObjectiveService(testDb.Driver);
                string tenantId = "ten_objective_preparation";
                string userId = "usr_objective_preparation";
                await EnsureTenantAndUserAsync(testDb, tenantId, userId).ConfigureAwait(false);
                AuthContext auth = AuthContext.Authenticated(tenantId, userId, false, true, "UnitTest");
                DateTime verifiedUtc = DateTime.UtcNow.AddMinutes(-10);

                Objective created = await objectives.CreateAsync(auth, new ObjectiveUpsertRequest
                {
                    Title = "Prepared objective",
                    Preparation = new ObjectivePreparation
                    {
                        Source = new ObjectivePreparationAnchor { VesselId = "vsl_source", Ref = "main", ResolvedCommit = "aaaa" },
                        Target = new ObjectivePreparationAnchor { VesselId = "vsl_target", Ref = "main", ResolvedCommit = "bbbb" },
                        Claims = new List<ObjectivePreparationClaim>
                        {
                            Claim("opc_source", ObjectivePreparationDependencyEnum.Source, verifiedUtc),
                            Claim("opc_target", ObjectivePreparationDependencyEnum.Target, verifiedUtc),
                            Claim("opc_both", ObjectivePreparationDependencyEnum.Source | ObjectivePreparationDependencyEnum.Target, verifiedUtc),
                            Claim("opc_none", ObjectivePreparationDependencyEnum.None, verifiedUtc)
                        }
                    }
                }).ConfigureAwait(false);

                Objective? persisted = await objectives.ReadAsync(auth, created.Id).ConfigureAwait(false);
                persisted = NotNull(persisted);
                AssertEqual("aaaa", persisted.Preparation.Source?.ResolvedCommit);
                AssertEqual(4, persisted.Preparation.Claims.Count);

                Objective sourceChanged = await objectives.UpdateAsync(auth, created.Id, new ObjectiveUpsertRequest
                {
                    Preparation = new ObjectivePreparation
                    {
                        Source = new ObjectivePreparationAnchor { VesselId = "vsl_source", Ref = "main", ResolvedCommit = "cccc" },
                        Target = persisted.Preparation.Target,
                        Claims = persisted.Preparation.Claims
                    }
                }).ConfigureAwait(false);

                AssertEqual(ObjectivePreparationClaimStateEnum.NeedsRecheck, FindClaim(sourceChanged, "opc_source").State);
                AssertEqual(ObjectivePreparationClaimStateEnum.Verified, FindClaim(sourceChanged, "opc_target").State);
                AssertEqual(ObjectivePreparationClaimStateEnum.NeedsRecheck, FindClaim(sourceChanged, "opc_both").State);
                AssertEqual(ObjectivePreparationClaimStateEnum.Verified, FindClaim(sourceChanged, "opc_none").State);
                AssertContains("Source preparation anchor changed", FindClaim(sourceChanged, "opc_source").InvalidationReason ?? String.Empty);
                AssertTrue(FindClaim(sourceChanged, "opc_source").InvalidatedUtc.HasValue, "Invalidated claim must retain its invalidation timestamp.");

                DateTime reverifiedUtc = DateTime.UtcNow;
                FindClaim(sourceChanged, "opc_both").State = ObjectivePreparationClaimStateEnum.Verified;
                FindClaim(sourceChanged, "opc_both").VerifiedUtc = reverifiedUtc;
                Objective targetChanged = await objectives.UpdateAsync(auth, created.Id, new ObjectiveUpsertRequest
                {
                    Preparation = new ObjectivePreparation
                    {
                        Source = sourceChanged.Preparation.Source,
                        Target = new ObjectivePreparationAnchor { VesselId = "vsl_target", Ref = "main", ResolvedCommit = "dddd" },
                        Claims = sourceChanged.Preparation.Claims.Concat(new[]
                        {
                            Claim("opc_new_target", ObjectivePreparationDependencyEnum.Target, reverifiedUtc)
                        }).ToList()
                    }
                }).ConfigureAwait(false);

                AssertEqual(ObjectivePreparationClaimStateEnum.NeedsRecheck, FindClaim(targetChanged, "opc_target").State);
                AssertEqual(ObjectivePreparationClaimStateEnum.Verified, FindClaim(targetChanged, "opc_both").State);
                AssertEqual(reverifiedUtc, FindClaim(targetChanged, "opc_both").VerifiedUtc);
                AssertEqual(ObjectivePreparationClaimStateEnum.Verified, FindClaim(targetChanged, "opc_new_target").State);

                Objective unrelatedUpdate = await objectives.UpdateAsync(auth, created.Id, new ObjectiveUpsertRequest
                {
                    Owner = "new-owner"
                }).ConfigureAwait(false);
                AssertEqual("dddd", unrelatedUpdate.Preparation.Target?.ResolvedCommit);
                AssertEqual(5, unrelatedUpdate.Preparation.Claims.Count);

                Objective dispatchRefChanged = await objectives.UpdateAsync(auth, created.Id, new ObjectiveUpsertRequest
                {
                    StartFromRef = "refs/tags/new-target"
                }).ConfigureAwait(false);
                AssertEqual(ObjectivePreparationClaimStateEnum.NeedsRecheck, FindClaim(dispatchRefChanged, "opc_both").State,
                    "Changing the actual dispatch ref must invalidate target-dependent preparation even when the stored anchor was not replaced.");

                Objective unresolved = await objectives.CreateAsync(auth, new ObjectiveUpsertRequest
                {
                    Title = "Unresolved source preparation",
                    Preparation = new ObjectivePreparation
                    {
                        Source = new ObjectivePreparationAnchor { VesselId = "vsl_source", Ref = "main" },
                        Claims = new List<ObjectivePreparationClaim>
                        {
                            Claim("opc_unresolved", ObjectivePreparationDependencyEnum.Source, DateTime.UtcNow)
                        }
                    }
                }).ConfigureAwait(false);
                AssertEqual(ObjectivePreparationClaimStateEnum.NeedsRecheck, FindClaim(unresolved, "opc_unresolved").State);
                AssertContains("Source revision is unresolved", FindClaim(unresolved, "opc_unresolved").InvalidationReason ?? String.Empty);
            }).ConfigureAwait(false);

            await RunTest("Preparation write bounds reject excessive claims and claim text", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                ObjectiveService objectives = new ObjectiveService(testDb.Driver);
                string tenantId = "ten_objective_preparation_bounds";
                string userId = "usr_objective_preparation_bounds";
                await EnsureTenantAndUserAsync(testDb, tenantId, userId).ConfigureAwait(false);
                AuthContext auth = AuthContext.Authenticated(tenantId, userId, false, true, "UnitTest");

                string tooManyError = String.Empty;
                try
                {
                    await objectives.CreateAsync(auth, new ObjectiveUpsertRequest
                    {
                        Title = "Too many preparation claims",
                        Preparation = new ObjectivePreparation
                        {
                            Claims = Enumerable.Range(0, 51)
                                .Select(index => Claim("opc_bound_" + index, ObjectivePreparationDependencyEnum.None, DateTime.UtcNow))
                                .ToList()
                        }
                    }).ConfigureAwait(false);
                }
                catch (InvalidOperationException ex)
                {
                    tooManyError = ex.Message;
                }
                AssertContains("more than 50 claims", tooManyError);

                string longTextError = String.Empty;
                try
                {
                    await objectives.CreateAsync(auth, new ObjectiveUpsertRequest
                    {
                        Title = "Long preparation claim",
                        Preparation = new ObjectivePreparation
                        {
                            Claims = new List<ObjectivePreparationClaim>
                            {
                                new ObjectivePreparationClaim { Id = "opc_long", Text = new string('x', 2001) }
                            }
                        }
                    }).ConfigureAwait(false);
                }
                catch (InvalidOperationException ex)
                {
                    longTextError = ex.Message;
                }
                AssertContains("cannot exceed 2000 characters", longTextError);

                string licenseMaterialError = String.Empty;
                try
                {
                    await objectives.CreateAsync(auth, new ObjectiveUpsertRequest
                    {
                        Title = "License material in execution requirements",
                        Preparation = new ObjectivePreparation
                        {
                            ExecutionRequirements = new ObjectiveExecutionRequirements
                            {
                                LicensedContext = "EXAMPLE KEY: 1234-5678-ABCD-EF00 issued to example"
                            }
                        }
                    }).ConfigureAwait(false);
                }
                catch (InvalidOperationException ex)
                {
                    licenseMaterialError = ex.Message;
                }
                AssertContains("never record license material or credentials", licenseMaterialError);

                string unknownOperatingSystemError = String.Empty;
                try
                {
                    await objectives.CreateAsync(auth, new ObjectiveUpsertRequest
                    {
                        Title = "Unknown execution operating system",
                        Preparation = new ObjectivePreparation
                        {
                            ExecutionRequirements = new ObjectiveExecutionRequirements { OperatingSystem = "Plan9" }
                        }
                    }).ConfigureAwait(false);
                }
                catch (InvalidOperationException ex)
                {
                    unknownOperatingSystemError = ex.Message;
                }
                AssertContains("must be one of Linux, Windows, MacOS", unknownOperatingSystemError);

                Objective declared = await objectives.CreateAsync(auth, new ObjectiveUpsertRequest
                {
                    Title = "Declared execution requirements",
                    Preparation = new ObjectivePreparation
                    {
                        ExecutionRequirements = new ObjectiveExecutionRequirements
                        {
                            OperatingSystem = "windows",
                            Executables = new List<string> { " example-format-loader ", "example-format-loader" },
                            LicensedContext = "example-license"
                        }
                    }
                }).ConfigureAwait(false);
                Objective? reread = await testDb.Driver.Objectives.ReadAsync(declared.Id).ConfigureAwait(false);
                AssertEqual("Windows", reread!.Preparation.ExecutionRequirements!.OperatingSystem, "the operating system is normalized to its canonical name");
                AssertEqual(1, reread.Preparation.ExecutionRequirements.Executables.Count, "executables are trimmed and de-duplicated");
                AssertEqual("example-license", reread.Preparation.ExecutionRequirements.LicensedContext, "a licensed context name persists");

                string invalidStateError = String.Empty;
                try
                {
                    await objectives.CreateAsync(auth, new ObjectiveUpsertRequest
                    {
                        Title = "Invalid preparation state",
                        Preparation = new ObjectivePreparation
                        {
                            Claims = new List<ObjectivePreparationClaim>
                            {
                                new ObjectivePreparationClaim
                                {
                                    Id = "opc_invalid_state",
                                    Text = "This state is not defined.",
                                    State = (ObjectivePreparationClaimStateEnum)999
                                }
                            }
                        }
                    }).ConfigureAwait(false);
                }
                catch (InvalidOperationException ex)
                {
                    invalidStateError = ex.Message;
                }
                AssertContains("invalid state value", invalidStateError);

                string siblingBoundsError = String.Empty;
                try
                {
                    await objectives.CreateAsync(auth, new ObjectiveUpsertRequest
                    {
                        Title = "Too many required sibling inputs",
                        Preparation = new ObjectivePreparation
                        {
                            RequiredSiblingInputs = Enumerable.Range(0, 21)
                                .Select(index => new ObjectivePreparationSiblingInput
                                {
                                    VesselRef = "vsl_" + index,
                                    RelativePath = "../sibling-" + index
                                })
                                .ToList()
                        }
                    }).ConfigureAwait(false);
                }
                catch (InvalidOperationException ex)
                {
                    siblingBoundsError = ex.Message;
                }
                AssertContains("more than 20 required sibling inputs", siblingBoundsError);

                string nullSiblingError = String.Empty;
                try
                {
                    await objectives.CreateAsync(auth, new ObjectiveUpsertRequest
                    {
                        Title = "Null required sibling input",
                        Preparation = new ObjectivePreparation
                        {
                            RequiredSiblingInputs = new List<ObjectivePreparationSiblingInput> { null! }
                        }
                    }).ConfigureAwait(false);
                }
                catch (InvalidOperationException ex)
                {
                    nullSiblingError = ex.Message;
                }
                AssertContains("cannot contain null entries", nullSiblingError);
            }).ConfigureAwait(false);

            await RunTest("Preparation updates are complete replacements", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                ObjectiveService objectives = new ObjectiveService(testDb.Driver);
                string tenantId = "ten_objective_preparation_replace";
                string userId = "usr_objective_preparation_replace";
                await EnsureTenantAndUserAsync(testDb, tenantId, userId).ConfigureAwait(false);
                AuthContext auth = AuthContext.Authenticated(tenantId, userId, false, true, "UnitTest");

                Objective created = await objectives.CreateAsync(auth, new ObjectiveUpsertRequest
                {
                    Title = "Replace preparation",
                    Preparation = new ObjectivePreparation
                    {
                        Source = new ObjectivePreparationAnchor { ResolvedCommit = "aaaa" },
                        Claims = new List<ObjectivePreparationClaim>
                        {
                            Claim("opc_replace", ObjectivePreparationDependencyEnum.None, DateTime.UtcNow)
                        }
                    }
                }).ConfigureAwait(false);

                Objective replaced = await objectives.UpdateAsync(auth, created.Id, new ObjectiveUpsertRequest
                {
                    Preparation = new ObjectivePreparation
                    {
                        Target = new ObjectivePreparationAnchor { ResolvedCommit = "bbbb" }
                    }
                }).ConfigureAwait(false);

                AssertNull(replaced.Preparation.Source,
                    "A supplied preparation object replaces omitted nested source data.");
                AssertEqual(0, replaced.Preparation.Claims.Count,
                    "A supplied preparation object replaces omitted nested claims.");
                AssertEqual("bbbb", replaced.Preparation.Target?.ResolvedCommit);
            }).ConfigureAwait(false);

            await RunTest("Stored preparation with null claims normalizes on read", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Objective objective = await testDb.Driver.Objectives.CreateAsync(new Objective
                {
                    Title = "Legacy null claims"
                }).ConfigureAwait(false);

                using (SqliteConnection connection = new SqliteConnection(testDb.ConnectionString))
                {
                    await connection.OpenAsync().ConfigureAwait(false);
                    using SqliteCommand command = connection.CreateCommand();
                    command.CommandText = "UPDATE objectives SET preparation_json = @preparation WHERE id = @id;";
                    command.Parameters.AddWithValue("@preparation", "{\"requiredClaimKinds\":null,\"requiredSiblingInputs\":[{\"vesselRef\":\"ReferenceSource\",\"relativePath\":\"../ReferenceSource\",\"requiredArtifactPaths\":null}],\"claims\":null}");
                    command.Parameters.AddWithValue("@id", objective.Id);
                    await command.ExecuteNonQueryAsync().ConfigureAwait(false);
                }

                Objective? persisted = await testDb.Driver.Objectives.ReadAsync(objective.Id).ConfigureAwait(false);
                AssertNotNull(persisted);
                AssertNotNull(persisted!.Preparation.Claims,
                    "A structurally valid legacy preparation payload must not crash dispatch rendering.");
                AssertNotNull(persisted.Preparation.RequiredClaimKinds);
                AssertNotNull(persisted.Preparation.RequiredSiblingInputs);
                AssertNotNull(persisted.Preparation.RequiredSiblingInputs[0].RequiredArtifactPaths);
                ObjectiveBriefRenderer.Render(persisted);
            }).ConfigureAwait(false);

            await RunTest("A malformed stored objective list is a named read error and is never written back empty", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    string tenantId = "ten_corrupt_list";
                    string userId = "usr_corrupt_list";
                    await EnsureTenantAndUserAsync(testDb, tenantId, userId).ConfigureAwait(false);
                    ObjectiveService objectives = new ObjectiveService(testDb.Driver);
                    AuthContext auth = AuthContext.Authenticated(tenantId, userId, false, true, "UnitTest");
                    Objective blocker = await objectives.CreateAsync(auth, new ObjectiveUpsertRequest { Title = "Blocker" }).ConfigureAwait(false);
                    Objective blocked = await objectives.CreateAsync(auth, new ObjectiveUpsertRequest
                    {
                        Title = "Blocked",
                        BlockedByObjectiveIds = new List<string> { blocker.Id }
                    }).ConfigureAwait(false);

                    const string corrupt = "[\"" + "obj_truncated";
                    await SetObjectiveColumnAsync(testDb, blocked.Id, "blocked_by_objective_ids_json", corrupt).ConfigureAwait(false);

                    string? readError = await CaptureInvalidOperationAsync(() => testDb.Driver.Objectives.ReadAsync(blocked.Id)).ConfigureAwait(false);
                    AssertNotNull(readError, "A malformed blocker list must not read as an objective with no blockers.");
                    AssertContains(blocked.Id, readError!, "The read error names the objective row.");
                    AssertContains("blocked_by_objective_ids_json", readError!, "The read error names the field.");

                    string? updateError = await CaptureInvalidOperationAsync(() => objectives.UpdateAsync(auth, blocked.Id, new ObjectiveUpsertRequest
                    {
                        Title = "Blocked (renamed)"
                    })).ConfigureAwait(false);
                    AssertNotNull(updateError, "An update of a row that cannot be read must fail rather than rewrite it.");
                    AssertEqual(corrupt, await ReadObjectiveColumnAsync(testDb, blocked.Id, "blocked_by_objective_ids_json").ConfigureAwait(false),
                        "The stored blocker list is left for repair, never replaced with an empty list.");
                }
            }).ConfigureAwait(false);

            await RunTest("An unknown stored objective enum value is a named read error, never a default", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Objective objective = await testDb.Driver.Objectives.CreateAsync(new Objective
                    {
                        Title = "Unknown status",
                        Status = ObjectiveStatusEnum.Blocked
                    }).ConfigureAwait(false);
                    await SetObjectiveColumnAsync(testDb, objective.Id, "status", "NoSuchStatus").ConfigureAwait(false);

                    string? readError = await CaptureInvalidOperationAsync(() => testDb.Driver.Objectives.ReadAsync(objective.Id)).ConfigureAwait(false);
                    AssertNotNull(readError, "An unknown status must not read as Draft, or the next update writes Draft over it.");
                    AssertContains(objective.Id, readError!);
                    AssertContains("status", readError!);
                }
            }).ConfigureAwait(false);

            await RunTest("A list skips a malformed objective row by name and still returns every other row", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    string tenantId = "ten_corrupt_row";
                    string userId = "usr_corrupt_row";
                    await EnsureTenantAndUserAsync(testDb, tenantId, userId).ConfigureAwait(false);
                    ObjectiveService writer = new ObjectiveService(testDb.Driver);
                    AuthContext auth = AuthContext.Authenticated(tenantId, userId, true, true, "UnitTest");
                    Objective good = await writer.CreateAsync(auth, new ObjectiveUpsertRequest { Title = "Readable" }).ConfigureAwait(false);
                    Objective bad = await writer.CreateAsync(auth, new ObjectiveUpsertRequest
                    {
                        Title = "Corrupt voyages",
                        Tags = new List<string> { "keep" }
                    }).ConfigureAwait(false);
                    await SetObjectiveColumnAsync(testDb, bad.Id, "voyage_ids_json", "{not json").ConfigureAwait(false);

                    List<Objective> rows = await testDb.Driver.Objectives.EnumerateAsync().ConfigureAwait(false);
                    AssertEqual(1, rows.Count, "The malformed row is skipped instead of reading as an objective with no voyages.");
                    AssertEqual(good.Id, rows[0].Id);

                    // A fresh service backfills from snapshots on first use; the malformed row must not stop that
                    // backfill or the list for every other objective.
                    ObjectiveService reader = new ObjectiveService(testDb.Driver);
                    EnumerationResult<Objective> page = await reader.EnumerateAsync(auth, new ObjectiveQuery { PageSize = 100 }).ConfigureAwait(false);
                    AssertEqual(1, page.Objects.Count, "The service list returns the readable row.");
                    AssertEqual(good.Id, page.Objects[0].Id);
                    AssertEqual("{not json", await ReadObjectiveColumnAsync(testDb, bad.Id, "voyage_ids_json").ConfigureAwait(false),
                        "Neither the list nor the snapshot backfill rewrites the malformed row.");
                }
            }).ConfigureAwait(false);

            await RunTest("An unknown stored refinement session status is a named read error and lists skip the row", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    string tenantId = "ten_corrupt_refinement";
                    string userId = "usr_corrupt_refinement";
                    await EnsureTenantAndUserAsync(testDb, tenantId, userId).ConfigureAwait(false);
                    ObjectiveService objectives = new ObjectiveService(testDb.Driver);
                    AuthContext auth = AuthContext.Authenticated(tenantId, userId, true, true, "UnitTest");
                    Objective objective = await objectives.CreateAsync(auth, new ObjectiveUpsertRequest { Title = "Refined" }).ConfigureAwait(false);
                    Captain captain = await testDb.Driver.Captains.CreateAsync(new Captain("corrupt-refinement-captain")).ConfigureAwait(false);
                    ObjectiveRefinementSession good = await testDb.Driver.ObjectiveRefinementSessions.CreateAsync(new ObjectiveRefinementSession
                    {
                        ObjectiveId = objective.Id,
                        TenantId = tenantId,
                        UserId = userId,
                        CaptainId = captain.Id,
                        Title = "Readable session",
                        Status = ObjectiveRefinementSessionStatusEnum.Active
                    }).ConfigureAwait(false);
                    ObjectiveRefinementSession bad = await testDb.Driver.ObjectiveRefinementSessions.CreateAsync(new ObjectiveRefinementSession
                    {
                        ObjectiveId = objective.Id,
                        TenantId = tenantId,
                        UserId = userId,
                        CaptainId = captain.Id,
                        Title = "Corrupt session",
                        Status = ObjectiveRefinementSessionStatusEnum.Active
                    }).ConfigureAwait(false);
                    await SetTableColumnAsync(testDb, "objective_refinement_sessions", bad.Id, "status", "NoSuchStatus").ConfigureAwait(false);

                    string? readError = await CaptureInvalidOperationAsync(() => testDb.Driver.ObjectiveRefinementSessions.ReadAsync(bad.Id)).ConfigureAwait(false);
                    AssertNotNull(readError, "An unknown status must not read as a Created session.");
                    AssertContains(bad.Id, readError!, "The read error names the session row.");
                    AssertContains("status", readError!, "The read error names the field.");

                    List<ObjectiveRefinementSession> listed = await testDb.Driver.ObjectiveRefinementSessions.EnumerateByObjectiveAsync(objective.Id).ConfigureAwait(false);
                    AssertEqual(1, listed.Count, "The unreadable row is skipped instead of listing as a Created session.");
                    AssertEqual(good.Id, listed[0].Id);
                    List<ObjectiveRefinementSession> created = await testDb.Driver.ObjectiveRefinementSessions.EnumerateByStatusAsync(ObjectiveRefinementSessionStatusEnum.Created).ConfigureAwait(false);
                    AssertEqual(0, created.Count, "No row reads as Created.");
                }
            }).ConfigureAwait(false);

            await RunTest("All database providers register objective preparation persistence", () =>
            {
                AssertPreparationMigration(SqliteTableQueries.GetMigrations(), 81, "SQLite");
                AssertPreparationMigration(PostgresqlTableQueries.GetMigrations(), 83, "PostgreSQL");
                AssertPreparationMigration(SqlServerTableQueries.GetMigrations(), 77, "SQL Server");
                AssertContains("preparation_json", MysqlTableQueries.MigrationV74Statements[0]);

                System.Reflection.MethodInfo? mysqlGetMigrations = typeof(MysqlDatabaseDriver).GetMethod(
                    "GetMigrations",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                AssertNotNull(mysqlGetMigrations, "MySQL must expose its migration registration list internally");
                List<SchemaMigration> mysqlMigrations = (List<SchemaMigration>)mysqlGetMigrations!.Invoke(null, Array.Empty<object>())!;
                AssertPreparationMigration(mysqlMigrations, 74, "MySQL");
                return Task.CompletedTask;
            });

            await RunTest("Concurrent voyage links settle on one nonterminal voyage per objective", async () =>
            {
                // The scheduler and an operator dispatch can both read "no active voyage", both
                // create one, and both link. The link admission guard must settle them on a single
                // winner: exactly one concurrent link succeeds and the other is refused by name.
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                ObjectiveService objectives = new ObjectiveService(testDb.Driver);
                AuthContext auth = AuthContext.Authenticated(
                    Armada.Core.Constants.DefaultTenantId,
                    Armada.Core.Constants.DefaultUserId,
                    false,
                    true,
                    "UnitTest");

                Objective objective = await testDb.Driver.Objectives.CreateAsync(new Objective
                {
                    TenantId = Armada.Core.Constants.DefaultTenantId,
                    UserId = Armada.Core.Constants.DefaultUserId,
                    Title = "Concurrent dispatch guard",
                    Status = ObjectiveStatusEnum.Scoped
                }).ConfigureAwait(false);
                Voyage first = await testDb.Driver.Voyages.CreateAsync(new Voyage("First dispatch")
                {
                    TenantId = Armada.Core.Constants.DefaultTenantId,
                    UserId = Armada.Core.Constants.DefaultUserId,
                    Status = VoyageStatusEnum.Open
                }).ConfigureAwait(false);
                Voyage second = await testDb.Driver.Voyages.CreateAsync(new Voyage("Second dispatch")
                {
                    TenantId = Armada.Core.Constants.DefaultTenantId,
                    UserId = Armada.Core.Constants.DefaultUserId,
                    Status = VoyageStatusEnum.Open
                }).ConfigureAwait(false);

                int succeeded = 0;
                List<Exception> failures = new List<Exception>();
                await Task.WhenAll(LinkAsync(first.Id), LinkAsync(second.Id)).ConfigureAwait(false);

                async Task LinkAsync(string voyageId)
                {
                    try
                    {
                        await objectives.LinkVoyageAsync(auth, objective.Id, voyageId).ConfigureAwait(false);
                        succeeded++;
                    }
                    catch (Exception ex)
                    {
                        failures.Add(ex);
                    }
                }

                AssertEqual(1, succeeded, "Exactly one concurrent link must win the race.");
                AssertEqual(1, failures.Count, "Exactly one concurrent link must be refused.");
                AssertTrue(failures[0] is ObjectiveAlreadyDispatchedException,
                    "The refused link must report the already-dispatched state, not a generic failure.");

                Objective stored = (await testDb.Driver.Objectives.ReadAsync(objective.Id).ConfigureAwait(false))!;
                AssertEqual(1, stored.VoyageIds.Count, "The objective must carry exactly one linked voyage.");
                Voyage winner = (await testDb.Driver.Voyages.ReadAsync(stored.VoyageIds[0]).ConfigureAwait(false))!;
                AssertTrue(ObjectiveService.IsActiveVoyageStatus(winner.Status),
                    "The winning voyage stays nonterminal.");
            });

            await RunTest("Link locks for many objectives return to the baseline count after the links finish", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                ObjectiveService objectives = new ObjectiveService(testDb.Driver);
                AuthContext auth = AuthContext.Authenticated(
                    Armada.Core.Constants.DefaultTenantId,
                    Armada.Core.Constants.DefaultUserId,
                    false,
                    true,
                    "UnitTest");
                int baseline = ObjectiveService.ActiveVoyageLinkLockCount;

                List<Task> links = new List<Task>();
                for (int i = 0; i < 60; i++)
                {
                    Objective objective = await testDb.Driver.Objectives.CreateAsync(new Objective
                    {
                        TenantId = auth.TenantId,
                        UserId = auth.UserId,
                        Title = "Lock lifecycle " + i,
                        Status = ObjectiveStatusEnum.Scoped
                    }).ConfigureAwait(false);
                    for (int j = 0; j < 2; j++)
                    {
                        Voyage voyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("Lock voyage " + i + "-" + j)
                        {
                            TenantId = auth.TenantId,
                            UserId = auth.UserId,
                            Status = VoyageStatusEnum.Open
                        }).ConfigureAwait(false);
                        links.Add(LinkIgnoringConflictAsync(objective.Id, voyage.Id));
                    }
                }

                await Task.WhenAll(links).ConfigureAwait(false);
                AssertEqual(baseline, ObjectiveService.ActiveVoyageLinkLockCount,
                    "Every per-objective link lock must be removed once no caller holds or awaits it.");

                async Task LinkIgnoringConflictAsync(string objectiveId, string voyageId)
                {
                    try
                    {
                        await objectives.LinkVoyageAsync(auth, objectiveId, voyageId).ConfigureAwait(false);
                    }
                    catch (ObjectiveAlreadyDispatchedException)
                    {
                        // The second concurrent link for one objective is refused by design.
                    }
                }
            });

            await RunTest("Objective dispatch admission persists until disposal and releases for the next holder", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                ObjectiveService firstService = new ObjectiveService(testDb.Driver);
                ObjectiveService secondService = new ObjectiveService(testDb.Driver);
                AuthContext auth = AuthContext.Authenticated(
                    Armada.Core.Constants.DefaultTenantId,
                    Armada.Core.Constants.DefaultUserId,
                    false,
                    true,
                    "UnitTest");
                Objective objective = await testDb.Driver.Objectives.CreateAsync(new Objective
                {
                    TenantId = Armada.Core.Constants.DefaultTenantId,
                    UserId = Armada.Core.Constants.DefaultUserId,
                    Title = "Durable dispatch admission",
                    Status = ObjectiveStatusEnum.Scoped
                }).ConfigureAwait(false);

                string leaseName = ObjectiveService.BuildDispatchAdmissionLeaseName(auth.TenantId, objective.Id);
                await using (ObjectiveDispatchAdmission first = await firstService
                    .AcquireDispatchAdmissionAsync(auth, objective.Id).ConfigureAwait(false))
                {
                    CoordinationLease? storedLease = await testDb.Driver.CoordinationLeases
                        .ReadAsync(leaseName).ConfigureAwait(false);
                    AssertNotNull(storedLease, "Admission must persist a durable database lease.");
                    bool secondWon = await testDb.Driver.CoordinationLeases.TryAcquireAsync(
                        leaseName,
                        "second-holder",
                        TimeSpan.FromMinutes(1),
                        auth.TenantId).ConfigureAwait(false);
                    AssertFalse(secondWon, "A second process must not acquire the held objective admission.");
                }

                AssertNull(await testDb.Driver.CoordinationLeases.ReadAsync(leaseName).ConfigureAwait(false),
                    "Disposal must release the durable admission lease.");
                await using ObjectiveDispatchAdmission second = await secondService
                    .AcquireDispatchAdmissionAsync(auth, objective.Id).ConfigureAwait(false);
                AssertNotNull(second, "A second service instance must acquire after release.");
            });

            await RunTest("Objective dispatch admission names an active winner and releases its failed claim", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                ObjectiveService objectives = new ObjectiveService(testDb.Driver);
                AuthContext auth = AuthContext.Authenticated(
                    Armada.Core.Constants.DefaultTenantId,
                    Armada.Core.Constants.DefaultUserId,
                    false,
                    true,
                    "UnitTest");
                Voyage winner = await testDb.Driver.Voyages.CreateAsync(new Voyage("Admission winner")
                {
                    TenantId = auth.TenantId,
                    UserId = auth.UserId,
                    Status = VoyageStatusEnum.InProgress
                }).ConfigureAwait(false);
                Objective objective = await testDb.Driver.Objectives.CreateAsync(new Objective
                {
                    TenantId = auth.TenantId,
                    UserId = auth.UserId,
                    Title = "Already admitted",
                    Status = ObjectiveStatusEnum.InProgress,
                    VoyageIds = new List<string> { winner.Id }
                }).ConfigureAwait(false);

                ObjectiveAlreadyDispatchedException? failure = null;
                try
                {
                    await objectives.AcquireDispatchAdmissionAsync(auth, objective.Id).ConfigureAwait(false);
                }
                catch (ObjectiveAlreadyDispatchedException ex)
                {
                    failure = ex;
                }

                AssertNotNull(failure, "An active linked voyage must refuse admission.");
                AssertEqual(winner.Id, failure!.WinningVoyageId);
                string leaseName = ObjectiveService.BuildDispatchAdmissionLeaseName(auth.TenantId, objective.Id);
                AssertNull(await testDb.Driver.CoordinationLeases.ReadAsync(leaseName).ConfigureAwait(false),
                    "A refused admission must release its temporary lease.");
            });

            await RunTest("A terminal voyage does not prevent an intentional successor voyage", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                ObjectiveService objectives = new ObjectiveService(testDb.Driver);
                AuthContext auth = AuthContext.Authenticated(
                    Armada.Core.Constants.DefaultTenantId,
                    Armada.Core.Constants.DefaultUserId,
                    false,
                    true,
                    "UnitTest");

                foreach (VoyageStatusEnum terminal in new[]
                {
                    VoyageStatusEnum.Cancelled,
                    VoyageStatusEnum.Failed,
                    VoyageStatusEnum.Complete
                })
                {
                    Objective objective = await testDb.Driver.Objectives.CreateAsync(new Objective
                    {
                        TenantId = Armada.Core.Constants.DefaultTenantId,
                        UserId = Armada.Core.Constants.DefaultUserId,
                        Title = "Successor after " + terminal,
                        Status = ObjectiveStatusEnum.Scoped
                    }).ConfigureAwait(false);
                    Voyage ended = await testDb.Driver.Voyages.CreateAsync(new Voyage("Ended " + terminal)
                    {
                        TenantId = Armada.Core.Constants.DefaultTenantId,
                        UserId = Armada.Core.Constants.DefaultUserId,
                        Status = terminal
                    }).ConfigureAwait(false);
                    objective.VoyageIds.Add(ended.Id);
                    await testDb.Driver.Objectives.UpdateAsync(objective).ConfigureAwait(false);

                    Voyage successor = await testDb.Driver.Voyages.CreateAsync(new Voyage("Successor")
                    {
                        TenantId = Armada.Core.Constants.DefaultTenantId,
                        UserId = Armada.Core.Constants.DefaultUserId,
                        Status = VoyageStatusEnum.Open
                    }).ConfigureAwait(false);

                    Objective linked = await objectives.LinkVoyageAsync(auth, objective.Id, successor.Id).ConfigureAwait(false);
                    AssertTrue(linked.VoyageIds.Contains(successor.Id),
                        "A " + terminal + " voyage must not block the intentional successor.");
                }
            });

            await RunTest("An active voyage refuses a second link and names the winner", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                ObjectiveService objectives = new ObjectiveService(testDb.Driver);
                AuthContext auth = AuthContext.Authenticated(
                    Armada.Core.Constants.DefaultTenantId,
                    Armada.Core.Constants.DefaultUserId,
                    false,
                    true,
                    "UnitTest");

                Objective objective = await testDb.Driver.Objectives.CreateAsync(new Objective
                {
                    TenantId = Armada.Core.Constants.DefaultTenantId,
                    UserId = Armada.Core.Constants.DefaultUserId,
                    Title = "Active voyage blocks a second dispatch",
                    Status = ObjectiveStatusEnum.Scoped
                }).ConfigureAwait(false);
                Voyage winner = await testDb.Driver.Voyages.CreateAsync(new Voyage("Winning voyage")
                {
                    TenantId = Armada.Core.Constants.DefaultTenantId,
                    UserId = Armada.Core.Constants.DefaultUserId,
                    Status = VoyageStatusEnum.InProgress
                }).ConfigureAwait(false);
                objective.VoyageIds.Add(winner.Id);
                await testDb.Driver.Objectives.UpdateAsync(objective).ConfigureAwait(false);

                Voyage loser = await testDb.Driver.Voyages.CreateAsync(new Voyage("Duplicate dispatch")
                {
                    TenantId = Armada.Core.Constants.DefaultTenantId,
                    UserId = Armada.Core.Constants.DefaultUserId,
                    Status = VoyageStatusEnum.Open
                }).ConfigureAwait(false);

                try
                {
                    await objectives.LinkVoyageAsync(auth, objective.Id, loser.Id).ConfigureAwait(false);
                    AssertTrue(false, "Linking a second active voyage must be refused.");
                }
                catch (ObjectiveAlreadyDispatchedException ex)
                {
                    AssertEqual(winner.Id, ex.WinningVoyageId, "The refusal must identify the winning voyage.");
                }

                Objective stored = (await testDb.Driver.Objectives.ReadAsync(objective.Id).ConfigureAwait(false))!;
                AssertEqual(1, stored.VoyageIds.Count, "The duplicate voyage must not be linked.");
                AssertFalse(stored.VoyageIds.Contains(loser.Id), "The losing voyage stays out of the dispatch lineage.");
            });
        }

        private void AssertPreparationMigration(IEnumerable<SchemaMigration> migrations, int version, string provider)
        {
            SchemaMigration? migration = migrations.FirstOrDefault(item => item.Version == version);
            AssertNotNull(migration, provider + " must register objective preparation migration " + version + ".");
            AssertTrue(migration!.Statements.Any(statement => statement.Contains("preparation_json", StringComparison.OrdinalIgnoreCase)),
                provider + " objective preparation migration must add preparation_json.");
        }

        private static ObjectivePreparationClaim Claim(string id, ObjectivePreparationDependencyEnum dependsOn, DateTime verifiedUtc)
        {
            return new ObjectivePreparationClaim
            {
                Id = id,
                Kind = ObjectivePreparationClaimKindEnum.SourcePath,
                Text = "Prepared claim " + id,
                EvidenceLinks = new List<string> { "https://example.test/" + id },
                DependsOn = dependsOn,
                State = ObjectivePreparationClaimStateEnum.Verified,
                VerifiedUtc = verifiedUtc
            };
        }

        private static ObjectivePreparationClaim FindClaim(Objective objective, string id)
        {
            return objective.Preparation.Claims.Find(claim => claim.Id == id)
                ?? throw new InvalidOperationException("Expected preparation claim " + id + ".");
        }

        private static async Task SetObjectiveColumnAsync(TestDatabase testDb, string objectiveId, string column, string value)
        {
            using (SqliteConnection connection = new SqliteConnection(testDb.ConnectionString))
            {
                await connection.OpenAsync().ConfigureAwait(false);
                using (SqliteCommand command = connection.CreateCommand())
                {
                    command.CommandText = "UPDATE objectives SET " + column + " = @value WHERE id = @id;";
                    command.Parameters.AddWithValue("@value", value);
                    command.Parameters.AddWithValue("@id", objectiveId);
                    await command.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
            }
        }

        private static async Task SetTableColumnAsync(TestDatabase testDb, string table, string id, string column, string value)
        {
            using (SqliteConnection connection = new SqliteConnection(testDb.ConnectionString))
            {
                await connection.OpenAsync().ConfigureAwait(false);
                using (SqliteCommand command = connection.CreateCommand())
                {
                    command.CommandText = "UPDATE " + table + " SET " + column + " = @value WHERE id = @id;";
                    command.Parameters.AddWithValue("@value", value);
                    command.Parameters.AddWithValue("@id", id);
                    await command.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
            }
        }

        private static async Task<string?> ReadObjectiveColumnAsync(TestDatabase testDb, string objectiveId, string column)
        {
            using (SqliteConnection connection = new SqliteConnection(testDb.ConnectionString))
            {
                await connection.OpenAsync().ConfigureAwait(false);
                using (SqliteCommand command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT " + column + " FROM objectives WHERE id = @id;";
                    command.Parameters.AddWithValue("@id", objectiveId);
                    object? value = await command.ExecuteScalarAsync().ConfigureAwait(false);
                    return value == null || value == DBNull.Value ? null : value.ToString();
                }
            }
        }

        private static async Task<string?> CaptureInvalidOperationAsync(Func<Task> action)
        {
            try
            {
                await action().ConfigureAwait(false);
                return null;
            }
            catch (InvalidOperationException ex)
            {
                return ex.Message;
            }
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
    }
}
