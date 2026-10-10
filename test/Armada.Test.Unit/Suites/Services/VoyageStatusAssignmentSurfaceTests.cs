namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Server.Mcp;
    using Armada.Server.Mcp.Tools;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// Tests that AssignmentState is surfaced via armada_voyage_status and armada_mission_status,
    /// and that armada_dispatch returns structured Code/Reason/Action errors for bad vessel/pipeline.
    /// </summary>
    public class VoyageStatusAssignmentSurfaceTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Voyage status assignment surface";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("VoyageStatus_UsesCallerScopeForEveryReturnedField", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    TenantMetadata foreignTenant = await testDb.Driver.Tenants.CreateAsync(
                        new TenantMetadata("voyage status foreign tenant")).ConfigureAwait(false);
                    UserMaster foreignOwner = await testDb.Driver.Users.CreateAsync(
                        new UserMaster(Armada.Core.Constants.DefaultTenantId, "voyage-status-foreign-owner@example.com", "password")).ConfigureAwait(false);
                    UserMaster foreignTenantOwner = await testDb.Driver.Users.CreateAsync(
                        new UserMaster(foreignTenant.Id, "voyage-status-foreign-tenant@example.com", "password")).ConfigureAwait(false);

                    Voyage voyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("scoped status voyage")
                    {
                        TenantId = Armada.Core.Constants.DefaultTenantId,
                        UserId = Armada.Core.Constants.DefaultUserId,
                        Description = "visible only in scope"
                    }).ConfigureAwait(false);
                    Voyage unrelatedVoyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("unrelated status voyage")
                    {
                        TenantId = Armada.Core.Constants.DefaultTenantId,
                        UserId = Armada.Core.Constants.DefaultUserId
                    }).ConfigureAwait(false);

                    Mission callerMission = await CreateVoyageStatusMissionAsync(
                        testDb.Driver, voyage.Id, Armada.Core.Constants.DefaultTenantId,
                        Armada.Core.Constants.DefaultUserId, "caller mission", MissionStatusEnum.Complete).ConfigureAwait(false);
                    Mission sameOwnerSibling = await CreateVoyageStatusMissionAsync(
                        testDb.Driver, voyage.Id, Armada.Core.Constants.DefaultTenantId,
                        Armada.Core.Constants.DefaultUserId, "same owner sibling", MissionStatusEnum.Failed).ConfigureAwait(false);
                    Mission sameTenantOtherOwner = await CreateVoyageStatusMissionAsync(
                        testDb.Driver, voyage.Id, Armada.Core.Constants.DefaultTenantId,
                        foreignOwner.Id, "same tenant other owner", MissionStatusEnum.Pending).ConfigureAwait(false);
                    Mission foreignTenantMission = await CreateVoyageStatusMissionAsync(
                        testDb.Driver, voyage.Id, foreignTenant.Id,
                        foreignTenantOwner.Id, "foreign tenant mission", MissionStatusEnum.Cancelled).ConfigureAwait(false);
                    Mission unrelatedMission = await CreateVoyageStatusMissionAsync(
                        testDb.Driver, unrelatedVoyage.Id, Armada.Core.Constants.DefaultTenantId,
                        Armada.Core.Constants.DefaultUserId, "unrelated caller mission", MissionStatusEnum.Pending).ConfigureAwait(false);

                    Func<JsonElement?, Task<object>>? statusHandler = null;
                    McpVoyageTools.Register(
                        (name, _, _, handler) => { if (name == "armada_voyage_status") statusHandler = handler; },
                        testDb.Driver,
                        new MinimalAdmiralDouble(),
                        null);
                    AssertNotNull(statusHandler, "armada_voyage_status handler must be registered");

                    JsonElement summaryArgs = JsonSerializer.SerializeToElement(new { voyageId = voyage.Id });
                    AuthContext foreignTenantCaller = AuthContext.Authenticated(
                        foreignTenant.Id, foreignTenantOwner.Id, false, false, "Test");
                    StatusResponse foreignTenantResult = await ReadVoyageStatusAsync(
                        statusHandler!, foreignTenantCaller, summaryArgs).ConfigureAwait(false);
                    AssertEqual("Voyage not found", foreignTenantResult.Error);

                    AuthContext foreignOwnerCaller = AuthContext.Authenticated(
                        Armada.Core.Constants.DefaultTenantId, foreignOwner.Id, false, false, "Test");
                    StatusResponse foreignOwnerResult = await ReadVoyageStatusAsync(
                        statusHandler!, foreignOwnerCaller, summaryArgs).ConfigureAwait(false);
                    AssertEqual("Voyage not found", foreignOwnerResult.Error);

                    AuthContext unrelatedMissionCaller = AuthContext.Authenticated(
                        Armada.Core.Constants.DefaultTenantId, Armada.Core.Constants.DefaultUserId, false, false, "Session");
                    unrelatedMissionCaller.MissionId = unrelatedMission.Id;
                    StatusResponse unrelatedMissionResult = await ReadVoyageStatusAsync(
                        statusHandler!, unrelatedMissionCaller, summaryArgs).ConfigureAwait(false);
                    AssertEqual("Voyage not found", unrelatedMissionResult.Error);

                    AuthContext missionCaller = AuthContext.Authenticated(
                        Armada.Core.Constants.DefaultTenantId, Armada.Core.Constants.DefaultUserId,
                        false, false, "Session");
                    missionCaller.MissionId = callerMission.Id;
                    StatusResponse missionSummary = await ReadVoyageStatusAsync(
                        statusHandler!, missionCaller, summaryArgs).ConfigureAwait(false);
                    AssertEqual(voyage.Id, missionSummary.Voyage?.Id);
                    AssertEqual(2, missionSummary.TotalMissions);
                    AssertEqual(1L, missionSummary.MissionCountsByStatus![nameof(MissionStatusEnum.Complete)]);
                    AssertEqual(1L, missionSummary.MissionCountsByStatus[nameof(MissionStatusEnum.Failed)]);
                    AssertEqual(2L, missionSummary.MissionCountsByAssignmentState![MissionAssignmentStateEnum.Pending.ToString()]);

                    JsonElement fullArgs = JsonSerializer.SerializeToElement(new
                    {
                        voyageId = voyage.Id,
                        summary = false,
                        includeMissions = true
                    });
                    StatusResponse missionDetails = await ReadVoyageStatusAsync(
                        statusHandler!, missionCaller, fullArgs).ConfigureAwait(false);
                    AssertTrue(missionDetails.Missions!.Select(mission => mission.Id).ToHashSet(StringComparer.Ordinal)
                        .SetEquals(new[] { callerMission.Id, sameOwnerSibling.Id }),
                        "mission tokens may read same-owner sibling mission metadata in their voyage");

                    AuthContext adminOwnedMissionCaller = AuthContext.Authenticated(
                        Armada.Core.Constants.DefaultTenantId, Armada.Core.Constants.DefaultUserId,
                        true, true, "Session");
                    adminOwnedMissionCaller.MissionId = callerMission.Id;
                    StatusResponse adminOwnedMissionSummary = await ReadVoyageStatusAsync(
                        statusHandler!, adminOwnedMissionCaller, summaryArgs).ConfigureAwait(false);
                    AssertEqual(2, adminOwnedMissionSummary.TotalMissions);
                    StatusResponse adminOwnedMissionUnrelated = await ReadVoyageStatusAsync(
                        statusHandler!, adminOwnedMissionCaller,
                        JsonSerializer.SerializeToElement(new { voyageId = unrelatedVoyage.Id })).ConfigureAwait(false);
                    AssertEqual("Voyage not found", adminOwnedMissionUnrelated.Error);

                    AuthContext ownerCaller = AuthContext.Authenticated(
                        Armada.Core.Constants.DefaultTenantId, Armada.Core.Constants.DefaultUserId,
                        false, false, "Test");
                    StatusResponse ownerSummary = await ReadVoyageStatusAsync(
                        statusHandler!, ownerCaller, summaryArgs).ConfigureAwait(false);
                    AssertEqual(2, ownerSummary.TotalMissions);
                    StatusResponse ownerDetails = await ReadVoyageStatusAsync(
                        statusHandler!, ownerCaller, fullArgs).ConfigureAwait(false);
                    AssertTrue(ownerDetails.Missions!.Select(mission => mission.Id).ToHashSet(StringComparer.Ordinal)
                        .SetEquals(new[] { callerMission.Id, sameOwnerSibling.Id }),
                        "ordinary users see all and only their own voyage missions");

                    AuthContext tenantAdmin = AuthContext.Authenticated(
                        Armada.Core.Constants.DefaultTenantId, Armada.Core.Constants.DefaultUserId, false, true, "Test");
                    StatusResponse tenantAdminResult = await ReadVoyageStatusAsync(
                        statusHandler!, tenantAdmin, summaryArgs).ConfigureAwait(false);
                    AssertEqual(3, tenantAdminResult.TotalMissions);
                    AssertTrue(!tenantAdminResult.MissionCountsByStatus!.ContainsKey(nameof(MissionStatusEnum.Cancelled)),
                        "tenant admins must not count missions from another tenant");
                    StatusResponse tenantAdminDetails = await ReadVoyageStatusAsync(
                        statusHandler!, tenantAdmin, fullArgs).ConfigureAwait(false);
                    AssertTrue(tenantAdminDetails.Missions!.Select(mission => mission.Id).ToHashSet(StringComparer.Ordinal)
                        .SetEquals(new[] { callerMission.Id, sameOwnerSibling.Id, sameTenantOtherOwner.Id }),
                        "tenant admins see all and only their tenant's voyage missions");

                    AuthContext globalAdmin = AuthContext.Authenticated(
                        Armada.Core.Constants.DefaultTenantId, Armada.Core.Constants.DefaultUserId,
                        true, true, "Test");
                    StatusResponse globalAdminResult = await ReadVoyageStatusAsync(
                        statusHandler!, globalAdmin, summaryArgs).ConfigureAwait(false);
                    AssertEqual(4, globalAdminResult.TotalMissions);
                    AssertTrue(globalAdminResult.MissionCountsByStatus!.ContainsKey(nameof(MissionStatusEnum.Cancelled)),
                        "global admins retain global voyage visibility");
                    StatusResponse globalAdminDetails = await ReadVoyageStatusAsync(
                        statusHandler!, globalAdmin, fullArgs).ConfigureAwait(false);
                    AssertTrue(globalAdminDetails.Missions!.Select(mission => mission.Id).ToHashSet(StringComparer.Ordinal)
                        .SetEquals(new[] { callerMission.Id, sameOwnerSibling.Id, sameTenantOtherOwner.Id, foreignTenantMission.Id }),
                        "global admins retain global mission visibility");

                    bool refusedUnauthenticated = false;
                    try
                    {
                        await statusHandler!(summaryArgs).ConfigureAwait(false);
                    }
                    catch (UnauthorizedAccessException)
                    {
                        refusedUnauthenticated = true;
                    }
                    AssertTrue(refusedUnauthenticated, "voyage status requires an authenticated MCP caller");
                }
            });

            await RunTest("VoyageStatusSummary_IncludesMissionCountsByAssignmentState_WithCorrectGrouping", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                        new Vessel("assignment-state-summary-vessel", "https://github.com/test/repo.git")).ConfigureAwait(false);

                    Voyage voyage = await testDb.Driver.Voyages.CreateAsync(
                        new Voyage("assignment state voyage", "")).ConfigureAwait(false);

                    // Seed 4 missions: 2 WaitingForVesselMutex, 1 WaitingForIdleCaptain, 1 Provisioning.
                    for (int i = 0; i < 4; i++)
                    {
                        Mission m = new Mission("M" + i, "d" + i);
                        m.VoyageId = voyage.Id;
                        m.VesselId = vessel.Id;
                        m = await testDb.Driver.Missions.CreateAsync(m).ConfigureAwait(false);

                        if (i < 2)
                            m.AssignmentState = MissionAssignmentStateEnum.WaitingForVesselMutex;
                        else if (i == 2)
                            m.AssignmentState = MissionAssignmentStateEnum.WaitingForIdleCaptain;
                        else
                            m.AssignmentState = MissionAssignmentStateEnum.Provisioning;

                        await testDb.Driver.Missions.UpdateAsync(m).ConfigureAwait(false);
                    }

                    MinimalAdmiralDouble admiralDouble = new MinimalAdmiralDouble();
                    Func<JsonElement?, Task<object>>? statusHandler = null;
                    McpVoyageTools.Register(
                        (name, _, _, handler) => { if (name == "armada_voyage_status") statusHandler = McpTestCaller.Wrap(handler); },
                        testDb.Driver,
                        admiralDouble,
                        null);

                    AssertNotNull(statusHandler, "armada_voyage_status handler must be registered");

                    JsonElement args = JsonSerializer.SerializeToElement(new { voyageId = voyage.Id });
                    object result = await statusHandler!(args).ConfigureAwait(false);
                    string json = JsonSerializer.Serialize(result);

                    AssertContains("MissionCountsByAssignmentState", json);
                    AssertContains("WaitingForVesselMutex", json);
                    AssertContains("WaitingForIdleCaptain", json);
                    AssertContains("Provisioning", json);

                    // Verify the actual counts by deserializing the relevant sub-object.
                    JsonDocument doc = JsonDocument.Parse(json);
                    JsonElement countsEl = doc.RootElement.GetProperty("MissionCountsByAssignmentState");
                    AssertEqual(2, countsEl.GetProperty("WaitingForVesselMutex").GetInt32());
                    AssertEqual(1, countsEl.GetProperty("WaitingForIdleCaptain").GetInt32());
                    AssertEqual(1, countsEl.GetProperty("Provisioning").GetInt32());
                }
            });

            await RunTest("VoyageStatusFull_MissionsIncludeAssignmentStateField", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                        new Vessel("assignment-state-full-vessel", "https://github.com/test/repo.git")).ConfigureAwait(false);

                    Voyage voyage = await testDb.Driver.Voyages.CreateAsync(
                        new Voyage("full mode voyage", "")).ConfigureAwait(false);

                    Mission m1 = new Mission("M1", "d1");
                    m1.VoyageId = voyage.Id;
                    m1.VesselId = vessel.Id;
                    m1 = await testDb.Driver.Missions.CreateAsync(m1).ConfigureAwait(false);
                    m1.AssignmentState = MissionAssignmentStateEnum.WaitingForIdleCaptain;
                    await testDb.Driver.Missions.UpdateAsync(m1).ConfigureAwait(false);

                    Mission m2 = new Mission("M2", "d2");
                    m2.VoyageId = voyage.Id;
                    m2.VesselId = vessel.Id;
                    m2 = await testDb.Driver.Missions.CreateAsync(m2).ConfigureAwait(false);
                    m2.AssignmentState = MissionAssignmentStateEnum.Assigned;
                    await testDb.Driver.Missions.UpdateAsync(m2).ConfigureAwait(false);

                    MinimalAdmiralDouble admiralDouble = new MinimalAdmiralDouble();
                    Func<JsonElement?, Task<object>>? statusHandler = null;
                    McpVoyageTools.Register(
                        (name, _, _, handler) => { if (name == "armada_voyage_status") statusHandler = McpTestCaller.Wrap(handler); },
                        testDb.Driver,
                        admiralDouble,
                        null);

                    AssertNotNull(statusHandler, "armada_voyage_status handler must be registered");

                    JsonElement args = JsonSerializer.SerializeToElement(new { voyageId = voyage.Id, summary = false, includeMissions = true });
                    object result = await statusHandler!(args).ConfigureAwait(false);
                    string json = JsonSerializer.Serialize(result);

                    AssertContains("AssignmentState", json);
                    AssertContains("WaitingForIdleCaptain", json);
                    AssertContains("Assigned", json);
                }
            });

            await RunTest("VoyageStatusFull_TruncatesLargeMissionPayloadsAndHonorsIncludeFields", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                        new Vessel("slim-status-vessel", "https://github.com/test/repo.git")).ConfigureAwait(false);

                    Voyage voyage = await testDb.Driver.Voyages.CreateAsync(
                        new Voyage("slim status voyage", new string('v', 128))).ConfigureAwait(false);

                    Mission mission = new Mission("Slim status mission", new string('d', 5000));
                    mission.VoyageId = voyage.Id;
                    mission.VesselId = vessel.Id;
                    mission.Status = MissionStatusEnum.WorkProduced;
                    mission.AgentOutput = new string('o', 5000);
                    mission = await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                    MinimalAdmiralDouble admiralDouble = new MinimalAdmiralDouble();
                    Func<JsonElement?, Task<object>>? statusHandler = null;
                    McpVoyageTools.Register(
                        (name, _, _, handler) => { if (name == "armada_voyage_status") statusHandler = McpTestCaller.Wrap(handler); },
                        testDb.Driver,
                        admiralDouble,
                        null);

                    AssertNotNull(statusHandler, "armada_voyage_status handler must be registered");

                    JsonElement args = JsonSerializer.SerializeToElement(new
                    {
                        voyageId = voyage.Id,
                        summary = false,
                        includeMissions = true,
                        includeFields = new[] { "statuses" }
                    });
                    object result = await statusHandler!(args).ConfigureAwait(false);

                    JsonDocument doc = JsonDocument.Parse(JsonSerializer.Serialize(result));
                    JsonElement root = doc.RootElement;
                    JsonElement missionStatus = root.GetProperty("Missions")[0];
                    JsonElement agentOutput = missionStatus.GetProperty("AgentOutput");
                    JsonElement description = missionStatus.GetProperty("Description");

                    AssertEqual(128, root.GetProperty("Voyage").GetProperty("DescriptionLength").GetInt32());
                    JsonElement status = missionStatus.GetProperty("Status");
                    bool statusMatches = status.ValueKind == JsonValueKind.String
                        ? status.GetString() == "WorkProduced"
                        : status.GetInt32() == (int)MissionStatusEnum.WorkProduced;
                    AssertTrue(statusMatches, "Status should be present when statuses is requested");
                    AssertTrue(agentOutput.GetProperty("Truncated").GetBoolean(), "AgentOutput should be capped in voyage status responses");
                    AssertEqual(5000, agentOutput.GetProperty("FullLength").GetInt32());
                    AssertEqual(4096, agentOutput.GetProperty("Text").GetString()!.Length);
                    AssertTrue(description.GetProperty("Truncated").GetBoolean(), "Description should be capped in voyage status responses");
                    AssertEqual(5000, description.GetProperty("FullLength").GetInt32());
                    AssertEqual(JsonValueKind.Null, missionStatus.GetProperty("Title").ValueKind, "Title should be omitted unless requested by includeFields");
                }
            });

            await RunTest("Dispatch_BadVesselId_ReturnsStructuredCodeReasonAction", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    MinimalAdmiralDouble admiralDouble = new MinimalAdmiralDouble();
                    Func<JsonElement?, Task<object>>? dispatchHandler = null;
                    McpVoyageTools.Register(
                        (name, _, _, handler) => { if (name == "armada_dispatch") dispatchHandler = McpTestCaller.Wrap(handler); },
                        testDb.Driver,
                        admiralDouble,
                        null);

                    AssertNotNull(dispatchHandler, "armada_dispatch handler must be registered");

                    JsonElement args = JsonSerializer.SerializeToElement(new
                    {
                        title = "bad vessel voyage",
                        vesselId = "vsl_does_not_exist",
                        missions = new object[] { new { title = "M1", description = "d1" } }
                    });

                    object result = await dispatchHandler!(args).ConfigureAwait(false);
                    string json = JsonSerializer.Serialize(result);

                    AssertContains("Error", json);
                    AssertContains("vessel_not_found", json);
                    AssertContains("Reason", json);
                    AssertContains("Action", json);
                    AssertContains("vsl_does_not_exist", json);
                }
            });

            await RunTest("Dispatch_BadPipelineName_ReturnsStructuredCodeReasonAction", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                        new Vessel("bad-pipeline-vessel", "https://github.com/test/repo.git")).ConfigureAwait(false);

                    MinimalAdmiralDouble admiralDouble = new MinimalAdmiralDouble();
                    Func<JsonElement?, Task<object>>? dispatchHandler = null;
                    McpVoyageTools.Register(
                        (name, _, _, handler) => { if (name == "armada_dispatch") dispatchHandler = McpTestCaller.Wrap(handler); },
                        testDb.Driver,
                        admiralDouble,
                        null);

                    AssertNotNull(dispatchHandler, "armada_dispatch handler must be registered");

                    JsonElement args = JsonSerializer.SerializeToElement(new
                    {
                        title = "bad pipeline voyage",
                        vesselId = vessel.Id,
                        pipeline = "NonExistentPipeline",
                        missions = new object[] { new { title = "M1", description = "d1" } }
                    });

                    object result = await dispatchHandler!(args).ConfigureAwait(false);
                    string json = JsonSerializer.Serialize(result);

                    AssertContains("Error", json);
                    AssertContains("pipeline_not_found", json);
                    AssertContains("Reason", json);
                    AssertContains("Action", json);
                    AssertContains("NonExistentPipeline", json);
                }
            });

            await RunTest("MissionStatus_ReturnsAssignmentState", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                        new Vessel("mission-status-assignment-vessel", "https://github.com/test/repo.git")).ConfigureAwait(false);

                    Mission mission = new Mission("Assignment State Mission", "desc");
                    mission.VesselId = vessel.Id;
                    mission = await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);
                    mission.AssignmentState = MissionAssignmentStateEnum.WaitingForIdleCaptain;
                    await testDb.Driver.Missions.UpdateAsync(mission).ConfigureAwait(false);

                    MinimalAdmiralDouble admiralDouble = new MinimalAdmiralDouble();
                    Func<JsonElement?, Task<object>>? statusHandler = null;
                    McpMissionTools.Register(
                        (name, _, _, handler) => { if (name == "armada_mission_status") statusHandler = McpTestCaller.Wrap(handler); },
                        testDb.Driver,
                        admiralDouble,
                        null,
                        null);

                    AssertNotNull(statusHandler, "armada_mission_status handler must be registered");

                    JsonElement args = JsonSerializer.SerializeToElement(new { missionId = mission.Id });
                    object result = await statusHandler!(args).ConfigureAwait(false);
                    string json = JsonSerializer.Serialize(result);

                    AssertContains("AssignmentState", json);
                    AssertContains("WaitingForIdleCaptain", json);
                }
            });

            // Edge case: voyage with zero missions. The summary handler runs
            // EnumerateSummariesAsync + GroupBy; a non-trivial guard is needed so
            // the empty-input GroupBy still produces a serializable (empty) dict
            // rather than throwing or omitting MissionCountsByAssignmentState.
            await RunTest("VoyageStatusSummary_EmptyVoyage_ReturnsEmptyMissionCountsByAssignmentState", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Voyage voyage = await testDb.Driver.Voyages.CreateAsync(
                        new Voyage("empty voyage", "")).ConfigureAwait(false);

                    MinimalAdmiralDouble admiralDouble = new MinimalAdmiralDouble();
                    Func<JsonElement?, Task<object>>? statusHandler = null;
                    McpVoyageTools.Register(
                        (name, _, _, handler) => { if (name == "armada_voyage_status") statusHandler = McpTestCaller.Wrap(handler); },
                        testDb.Driver,
                        admiralDouble,
                        null);

                    AssertNotNull(statusHandler, "armada_voyage_status handler must be registered");

                    JsonElement args = JsonSerializer.SerializeToElement(new { voyageId = voyage.Id });
                    object result = await statusHandler!(args).ConfigureAwait(false);
                    string json = JsonSerializer.Serialize(result);

                    AssertContains("MissionCountsByAssignmentState", json);

                    JsonDocument doc = JsonDocument.Parse(json);
                    JsonElement countsEl = doc.RootElement.GetProperty("MissionCountsByAssignmentState");
                    AssertEqual(JsonValueKind.Object, countsEl.ValueKind, "MissionCountsByAssignmentState must be an object, not null");
                    AssertEqual(0, countsEl.EnumerateObject().Count(),
                        "MissionCountsByAssignmentState must be an empty object for an empty voyage");
                    AssertEqual(0, doc.RootElement.GetProperty("TotalMissions").GetInt32(),
                        "TotalMissions must be zero for an empty voyage");
                }
            });

            // Sanity: counts under MissionCountsByAssignmentState must sum to TotalMissions.
            // Catches future drift if someone adds filtering or a where-clause that drops missions.
            await RunTest("VoyageStatusSummary_AssignmentCountsSum_EqualsTotalMissions", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                        new Vessel("sum-check-vessel", "https://github.com/test/repo.git")).ConfigureAwait(false);

                    Voyage voyage = await testDb.Driver.Voyages.CreateAsync(
                        new Voyage("sum check voyage", "")).ConfigureAwait(false);

                    // Seed 5 missions across 3 distinct AssignmentState values plus default Pending.
                    MissionAssignmentStateEnum[] states = new[]
                    {
                        MissionAssignmentStateEnum.Pending,
                        MissionAssignmentStateEnum.WaitingForDependency,
                        MissionAssignmentStateEnum.WaitingForIdleCaptain,
                        MissionAssignmentStateEnum.Failed,
                        MissionAssignmentStateEnum.Assigned
                    };

                    for (int i = 0; i < states.Length; i++)
                    {
                        Mission m = new Mission("M" + i, "d" + i);
                        m.VoyageId = voyage.Id;
                        m.VesselId = vessel.Id;
                        m = await testDb.Driver.Missions.CreateAsync(m).ConfigureAwait(false);
                        m.AssignmentState = states[i];
                        await testDb.Driver.Missions.UpdateAsync(m).ConfigureAwait(false);
                    }

                    MinimalAdmiralDouble admiralDouble = new MinimalAdmiralDouble();
                    Func<JsonElement?, Task<object>>? statusHandler = null;
                    McpVoyageTools.Register(
                        (name, _, _, handler) => { if (name == "armada_voyage_status") statusHandler = McpTestCaller.Wrap(handler); },
                        testDb.Driver,
                        admiralDouble,
                        null);

                    AssertNotNull(statusHandler, "armada_voyage_status handler must be registered");

                    JsonElement args = JsonSerializer.SerializeToElement(new { voyageId = voyage.Id });
                    object result = await statusHandler!(args).ConfigureAwait(false);
                    string json = JsonSerializer.Serialize(result);

                    JsonDocument doc = JsonDocument.Parse(json);
                    int totalMissions = doc.RootElement.GetProperty("TotalMissions").GetInt32();
                    int assignmentSum = 0;
                    foreach (JsonProperty entry in doc.RootElement.GetProperty("MissionCountsByAssignmentState").EnumerateObject())
                    {
                        assignmentSum += entry.Value.GetInt32();
                    }

                    AssertEqual(states.Length, totalMissions, "TotalMissions must include every seeded mission");
                    AssertEqual(totalMissions, assignmentSum,
                        "Sum of MissionCountsByAssignmentState values must equal TotalMissions (no missions lost in GroupBy)");
                }
            });

            // Validation order: vessel-not-found must short-circuit before the pipeline lookup.
            // If both vessel and pipeline are bad, the response must carry vessel_not_found so
            // operators fix the vessel reference first; otherwise they would chase a phantom
            // pipeline error and never learn the vessel id is wrong.
            await RunTest("Dispatch_BadVesselAndBadPipeline_VesselNotFoundWins", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    MinimalAdmiralDouble admiralDouble = new MinimalAdmiralDouble();
                    Func<JsonElement?, Task<object>>? dispatchHandler = null;
                    McpVoyageTools.Register(
                        (name, _, _, handler) => { if (name == "armada_dispatch") dispatchHandler = McpTestCaller.Wrap(handler); },
                        testDb.Driver,
                        admiralDouble,
                        null);

                    AssertNotNull(dispatchHandler, "armada_dispatch handler must be registered");

                    JsonElement args = JsonSerializer.SerializeToElement(new
                    {
                        title = "bad both voyage",
                        vesselId = "vsl_does_not_exist",
                        pipeline = "AlsoDoesNotExist",
                        missions = new object[] { new { title = "M1", description = "d1" } }
                    });

                    object result = await dispatchHandler!(args).ConfigureAwait(false);
                    string json = JsonSerializer.Serialize(result);

                    AssertContains("vessel_not_found", json);
                    AssertFalse(json.Contains("pipeline_not_found"),
                        "Vessel check must short-circuit before pipeline lookup; pipeline_not_found must not appear");
                }
            });

            // Different enum value than the existing happy-path WaitingForIdleCaptain test.
            // Failed is the most operationally interesting state: dock provisioning or agent
            // launch threw and reverted the mission. Verifies the JsonStringEnumConverter
            // is in effect (Failed appears as a string, not the numeric enum value).
            await RunTest("MissionStatus_WithFailedAssignmentState_AppearsAsStringInResponse", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                        new Vessel("mission-status-failed-vessel", "https://github.com/test/repo.git")).ConfigureAwait(false);

                    Mission mission = new Mission("Failed assignment mission", "desc");
                    mission.VesselId = vessel.Id;
                    mission = await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);
                    mission.AssignmentState = MissionAssignmentStateEnum.Failed;
                    await testDb.Driver.Missions.UpdateAsync(mission).ConfigureAwait(false);

                    MinimalAdmiralDouble admiralDouble = new MinimalAdmiralDouble();
                    Func<JsonElement?, Task<object>>? statusHandler = null;
                    McpMissionTools.Register(
                        (name, _, _, handler) => { if (name == "armada_mission_status") statusHandler = McpTestCaller.Wrap(handler); },
                        testDb.Driver,
                        admiralDouble,
                        null,
                        null);

                    AssertNotNull(statusHandler, "armada_mission_status handler must be registered");

                    JsonElement args = JsonSerializer.SerializeToElement(new { missionId = mission.Id });
                    object result = await statusHandler!(args).ConfigureAwait(false);
                    string json = JsonSerializer.Serialize(result);

                    AssertContains("AssignmentState", json);
                    AssertContains("\"Failed\"", json);
                }
            });
        }

        private static async Task<Mission> CreateVoyageStatusMissionAsync(
            DatabaseDriver database,
            string voyageId,
            string tenantId,
            string userId,
            string title,
            MissionStatusEnum status)
        {
            return await database.Missions.CreateAsync(new Mission(title)
            {
                TenantId = tenantId,
                UserId = userId,
                VoyageId = voyageId,
                Status = status,
                AssignmentState = MissionAssignmentStateEnum.Pending
            }).ConfigureAwait(false);
        }

        private async Task<StatusResponse> ReadVoyageStatusAsync(
            Func<JsonElement?, Task<object>> handler,
            AuthContext caller,
            JsonElement arguments)
        {
            using (McpCallerContext.Begin(caller))
            {
                object result = await handler(arguments).ConfigureAwait(false);
                string serialized = JsonSerializer.Serialize(result);
                StatusResponse? response = JsonSerializer.Deserialize<StatusResponse>(serialized);
                AssertNotNull(response, "voyage status response must deserialize");
                return response!;
            }
        }

        private sealed class StatusResponse
        {
            /// <summary>Error returned when the requested voyage is outside the caller's scope.</summary>
            public string? Error { get; set; }
            /// <summary>Voyage summary returned to the caller.</summary>
            public VoyageSummary? Voyage { get; set; }
            /// <summary>Number of mission records visible in the caller's scope.</summary>
            public int TotalMissions { get; set; }
            /// <summary>Visible mission totals by status.</summary>
            public Dictionary<string, long>? MissionCountsByStatus { get; set; }
            /// <summary>Visible mission totals by assignment state.</summary>
            public Dictionary<string, long>? MissionCountsByAssignmentState { get; set; }
            /// <summary>Mission records returned when detail mode is requested.</summary>
            public List<StatusMission>? Missions { get; set; }
        }

        private sealed class VoyageSummary
        {
            /// <summary>Voyage identifier.</summary>
            public string? Id { get; set; }
            /// <summary>Voyage title.</summary>
            public string? Title { get; set; }
        }

        private sealed class StatusMission
        {
            /// <summary>Mission identifier.</summary>
            public string? Id { get; set; }
            /// <summary>Mission title.</summary>
            public string? Title { get; set; }
        }

        /// <summary>
        /// Minimal admiral double that satisfies the IAdmiralService interface.
        /// Voyage status and mission status handlers use the database directly, not the admiral.
        /// Dispatch tests that hit vessel/pipeline validation return before admiral is called.
        /// </summary>
        private sealed class MinimalAdmiralDouble : IAdmiralService
        {
            /// <summary>Not used by the tested handlers.</summary>
            public Func<Captain, Mission, Dock, Task<int>>? OnLaunchAgent { get; set; }
            /// <summary>Not used by the tested handlers.</summary>
            public Func<Captain, Task>? OnStopAgent { get; set; }
            /// <summary>Not used by the tested handlers.</summary>
            public Func<Mission, Dock, Task>? OnCaptureDiff { get; set; }
            /// <summary>Not used by the tested handlers.</summary>
            public Func<Mission, Dock, Task>? OnMissionComplete { get; set; }
            /// <summary>Not used by the tested handlers.</summary>
            public Func<Voyage, Task>? OnVoyageComplete { get; set; }
            /// <summary>Not used by the tested handlers.</summary>
            public Func<Mission, Task<bool>>? OnReconcilePullRequest { get; set; }
            /// <summary>Not used by the tested handlers.</summary>
            public Func<Task<int>>? OnReconcileMergeEntries { get; set; }
            /// <summary>Not used by the tested handlers.</summary>
            public Func<int, bool>? OnIsProcessExitHandled { get; set; }

            /// <summary>Not invoked by the pre-persistence error paths under test.</summary>
            public Task<Voyage> DispatchVoyageAsync(
                string title, string description, string vesselId,
                List<MissionDescription> missionDescriptions,
                CancellationToken token = default)
                => throw new NotImplementedException();

            /// <summary>Not invoked by the pre-persistence error paths under test.</summary>
            public Task<Voyage> DispatchVoyageAsync(
                string title, string description, string vesselId,
                List<MissionDescription> missionDescriptions,
                List<SelectedPlaybook>? selectedPlaybooks,
                CancellationToken token = default)
                => throw new NotImplementedException();

            /// <summary>Not invoked by the pre-persistence error paths under test.</summary>
            public Task<Voyage> DispatchVoyageAsync(
                string title, string description, string vesselId,
                List<MissionDescription> missionDescriptions,
                string? pipelineId,
                CancellationToken token = default)
                => throw new NotImplementedException();

            /// <summary>Not invoked by the pre-persistence error paths under test.</summary>
            public Task<Voyage> DispatchVoyageAsync(
                string title, string description, string vesselId,
                List<MissionDescription> missionDescriptions,
                string? pipelineId,
                List<SelectedPlaybook>? selectedPlaybooks,
                CancellationToken token = default)
                => throw new NotImplementedException();

            /// <summary>Not invoked by the status handlers under test.</summary>
            public Task<Mission> DispatchMissionAsync(Mission mission, CancellationToken token = default)
                => throw new NotImplementedException();

            /// <summary>Not invoked by the handlers under test.</summary>
            public Task<Pipeline?> ResolvePipelineAsync(string? pipelineIdOrName, Vessel vessel, CancellationToken token = default)
                => Task.FromResult<Pipeline?>(null);

            /// <summary>Not invoked by the handlers under test.</summary>
            public Task<ArmadaStatus> GetStatusAsync(CancellationToken token = default)
                => throw new NotImplementedException();

            /// <summary>Not invoked by the handlers under test.</summary>
            public Task RecallCaptainAsync(string captainId, CancellationToken token = default)
                => throw new NotImplementedException();

            /// <summary>Not invoked by the handlers under test.</summary>
            public Task RecallAllAsync(CancellationToken token = default)
                => throw new NotImplementedException();
            public Task StopAllAgentProcessesAsync(CancellationToken token = default) => Task.CompletedTask;

            /// <summary>Not invoked by the handlers under test.</summary>
            public Task HealthCheckAsync(CancellationToken token = default)
                => throw new NotImplementedException();

            /// <summary>Not invoked by the handlers under test.</summary>
            public Task CleanupStaleCaptainsAsync(CancellationToken token = default)
                => throw new NotImplementedException();

            /// <summary>Not invoked by the handlers under test.</summary>
            public Task HandleProcessExitAsync(
                int processId, int? exitCode, string captainId, string missionId,
                CancellationToken token = default)
                => throw new NotImplementedException();
        }
    }
}
