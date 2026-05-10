namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Text.Json.Nodes;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Memory;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Server.Mcp.Tools;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// Tests for armada_drain_audit_queue reflection auto-dispatch.
    /// </summary>
    public class ReflectionAuditDrainTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Reflection Audit Drain";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("Drain_BelowReflectionThreshold_NoAutoDispatch", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = new ArmadaSettings { DefaultReflectionThreshold = 5 };
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, "below-threshold").ConfigureAwait(false);
                    vessel.ReflectionThreshold = 5;
                    vessel = await testDb.Driver.Vessels.UpdateAsync(vessel).ConfigureAwait(false);

                    for (int i = 0; i < 4; i++)
                    {
                        await CreateTerminalMissionAsync(
                                testDb.Driver,
                                vessel.Id,
                                "m" + i,
                                DateTime.UtcNow.AddMinutes(-10 + i))
                            .ConfigureAwait(false);
                    }

                    RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                    ReflectionDispatcher dispatcher = new ReflectionDispatcher(
                        testDb.Driver,
                        admiral,
                        settings,
                        new ReflectionMemoryService(testDb.Driver));

                    Func<JsonElement?, Task<object>>? drainHandler = null;
                    McpAuditTools.Register(
                        (name, _, _, h) => { if (name == "armada_drain_audit_queue") drainHandler = h; },
                        testDb.Driver,
                        null,
                        dispatcher);
                    AssertNotNull(drainHandler);

                    JsonElement args = JsonSerializer.SerializeToElement(new { vesselId = vessel.Id, limit = 10 });
                    object result = await drainHandler!(args).ConfigureAwait(false);
                    JsonNode? root = JsonNode.Parse(JsonSerializer.Serialize(result));
                    JsonArray? reflections = root?["reflectionsDispatched"]?.AsArray();

                    AssertNotNull(reflections);
                    AssertEqual(0, reflections!.Count);
                    AssertEqual(0, admiral.DispatchCount);
                }
            });

            await RunTest("Drain_AtReflectionThreshold_DispatchesReflection", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = new ArmadaSettings { DefaultReflectionThreshold = 5 };
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, "at-threshold").ConfigureAwait(false);
                    vessel.ReflectionThreshold = 5;
                    vessel = await testDb.Driver.Vessels.UpdateAsync(vessel).ConfigureAwait(false);

                    for (int i = 0; i < 5; i++)
                    {
                        await CreateTerminalMissionAsync(
                                testDb.Driver,
                                vessel.Id,
                                "m" + i,
                                DateTime.UtcNow.AddMinutes(-10 + i))
                            .ConfigureAwait(false);
                    }

                    RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                    ReflectionDispatcher dispatcher = new ReflectionDispatcher(
                        testDb.Driver,
                        admiral,
                        settings,
                        new ReflectionMemoryService(testDb.Driver));

                    Func<JsonElement?, Task<object>>? drainHandler = null;
                    McpAuditTools.Register(
                        (name, _, _, h) => { if (name == "armada_drain_audit_queue") drainHandler = h; },
                        testDb.Driver,
                        null,
                        dispatcher);
                    AssertNotNull(drainHandler);

                    JsonElement args = JsonSerializer.SerializeToElement(new { vesselId = vessel.Id, limit = 10 });
                    object result = await drainHandler!(args).ConfigureAwait(false);
                    JsonNode? root = JsonNode.Parse(JsonSerializer.Serialize(result));
                    JsonArray? reflections = root?["reflectionsDispatched"]?.AsArray();

                    AssertNotNull(reflections);
                    AssertEqual(1, reflections!.Count);
                    AssertEqual(vessel.Id, reflections[0]?["vesselId"]?.GetValue<string>());
                    AssertEqual(1, admiral.DispatchCount);
                    string dispatchedMissionId = reflections[0]?["missionId"]?.GetValue<string>() ?? "";
                    AssertFalse(String.IsNullOrEmpty(dispatchedMissionId));
                }
            });

            await RunTest("Drain_DefaultReflectionThreshold_DispatchesReflection", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = new ArmadaSettings { DefaultReflectionThreshold = 4 };
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, "default-threshold").ConfigureAwait(false);

                    for (int i = 0; i < 4; i++)
                    {
                        await CreateTerminalMissionAsync(
                                testDb.Driver,
                                vessel.Id,
                                "m" + i,
                                DateTime.UtcNow.AddMinutes(-10 + i))
                            .ConfigureAwait(false);
                    }

                    RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                    ReflectionDispatcher dispatcher = new ReflectionDispatcher(
                        testDb.Driver,
                        admiral,
                        settings,
                        new ReflectionMemoryService(testDb.Driver));

                    Func<JsonElement?, Task<object>>? drainHandler = null;
                    McpAuditTools.Register(
                        (name, _, _, h) => { if (name == "armada_drain_audit_queue") drainHandler = h; },
                        testDb.Driver,
                        null,
                        dispatcher);
                    AssertNotNull(drainHandler);

                    JsonElement args = JsonSerializer.SerializeToElement(new { vesselId = vessel.Id, limit = 10 });
                    object result = await drainHandler!(args).ConfigureAwait(false);
                    JsonNode? root = JsonNode.Parse(JsonSerializer.Serialize(result));
                    JsonArray? reflections = root?["reflectionsDispatched"]?.AsArray();

                    AssertNotNull(reflections);
                    AssertEqual(1, reflections!.Count);
                    AssertEqual(vessel.Id, reflections[0]?["vesselId"]?.GetValue<string>());
                    AssertEqual(1, admiral.DispatchCount);
                }
            });

            await RunTest("Drain_InFlightReflection_PreventsDuplicate", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = new ArmadaSettings { DefaultReflectionThreshold = 3 };
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, "inflight-block").ConfigureAwait(false);
                    vessel.ReflectionThreshold = 3;
                    vessel = await testDb.Driver.Vessels.UpdateAsync(vessel).ConfigureAwait(false);

                    for (int i = 0; i < 3; i++)
                    {
                        await CreateTerminalMissionAsync(
                                testDb.Driver,
                                vessel.Id,
                                "m" + i,
                                DateTime.UtcNow.AddMinutes(-10 + i))
                            .ConfigureAwait(false);
                    }

                    Mission inflight = new Mission("open consolidator", "d");
                    inflight.VesselId = vessel.Id;
                    inflight.Persona = "MemoryConsolidator";
                    inflight.Status = MissionStatusEnum.InProgress;
                    await testDb.Driver.Missions.CreateAsync(inflight).ConfigureAwait(false);

                    RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                    ReflectionDispatcher dispatcher = new ReflectionDispatcher(
                        testDb.Driver,
                        admiral,
                        settings,
                        new ReflectionMemoryService(testDb.Driver));

                    Func<JsonElement?, Task<object>>? drainHandler = null;
                    McpAuditTools.Register(
                        (name, _, _, h) => { if (name == "armada_drain_audit_queue") drainHandler = h; },
                        testDb.Driver,
                        null,
                        dispatcher);
                    AssertNotNull(drainHandler);

                    JsonElement args = JsonSerializer.SerializeToElement(new { vesselId = vessel.Id, limit = 10 });
                    object result = await drainHandler!(args).ConfigureAwait(false);
                    JsonNode? root = JsonNode.Parse(JsonSerializer.Serialize(result));
                    JsonArray? reflections = root?["reflectionsDispatched"]?.AsArray();

                    AssertNotNull(reflections);
                    AssertEqual(0, reflections!.Count);
                    AssertEqual(0, admiral.DispatchCount);
                }
            });

            await RunTest("Drain_VesselIdFilter_LimitsReflectionChecks", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = new ArmadaSettings { DefaultReflectionThreshold = 3 };
                    Vessel vesselA = await CreateVesselAsync(testDb.Driver, "filter-a").ConfigureAwait(false);
                    vesselA.ReflectionThreshold = 3;
                    vesselA = await testDb.Driver.Vessels.UpdateAsync(vesselA).ConfigureAwait(false);

                    Vessel vesselB = await CreateVesselAsync(testDb.Driver, "filter-b").ConfigureAwait(false);
                    vesselB.ReflectionThreshold = 3;
                    vesselB = await testDb.Driver.Vessels.UpdateAsync(vesselB).ConfigureAwait(false);

                    for (int i = 0; i < 3; i++)
                    {
                        await CreateTerminalMissionAsync(
                                testDb.Driver,
                                vesselA.Id,
                                "a" + i,
                                DateTime.UtcNow.AddMinutes(-20 + i))
                            .ConfigureAwait(false);
                        await CreateTerminalMissionAsync(
                                testDb.Driver,
                                vesselB.Id,
                                "b" + i,
                                DateTime.UtcNow.AddMinutes(-20 + i))
                            .ConfigureAwait(false);
                    }

                    RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                    ReflectionDispatcher dispatcher = new ReflectionDispatcher(
                        testDb.Driver,
                        admiral,
                        settings,
                        new ReflectionMemoryService(testDb.Driver));

                    Func<JsonElement?, Task<object>>? drainHandler = null;
                    McpAuditTools.Register(
                        (name, _, _, h) => { if (name == "armada_drain_audit_queue") drainHandler = h; },
                        testDb.Driver,
                        null,
                        dispatcher);
                    AssertNotNull(drainHandler);

                    JsonElement args = JsonSerializer.SerializeToElement(new { vesselId = vesselA.Id, limit = 10 });
                    object result = await drainHandler!(args).ConfigureAwait(false);
                    JsonNode? root = JsonNode.Parse(JsonSerializer.Serialize(result));
                    JsonArray? reflections = root?["reflectionsDispatched"]?.AsArray();

                    AssertNotNull(reflections);
                    AssertEqual(1, reflections!.Count);
                    AssertEqual(vesselA.Id, reflections[0]?["vesselId"]?.GetValue<string>());
                    AssertEqual(1, admiral.DispatchCount);
                }
            });

            await RunTest("Drain_InactiveVessel_AllVesselsPath_SkipsAutoDispatch", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = new ArmadaSettings { DefaultReflectionThreshold = 3 };
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, "inactive-all-vessels").ConfigureAwait(false);
                    vessel.Active = false;
                    vessel.ReflectionThreshold = 3;
                    vessel = await testDb.Driver.Vessels.UpdateAsync(vessel).ConfigureAwait(false);

                    for (int i = 0; i < 3; i++)
                    {
                        await CreateTerminalMissionAsync(
                                testDb.Driver,
                                vessel.Id,
                                "inactive" + i,
                                DateTime.UtcNow.AddMinutes(-10 + i))
                            .ConfigureAwait(false);
                    }

                    RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                    ReflectionDispatcher dispatcher = new ReflectionDispatcher(
                        testDb.Driver,
                        admiral,
                        settings,
                        new ReflectionMemoryService(testDb.Driver));

                    Func<JsonElement?, Task<object>>? drainHandler = null;
                    McpAuditTools.Register(
                        (name, _, _, h) => { if (name == "armada_drain_audit_queue") drainHandler = h; },
                        testDb.Driver,
                        null,
                        dispatcher);
                    AssertNotNull(drainHandler);

                    JsonElement args = JsonSerializer.SerializeToElement(new { limit = 10 });
                    object result = await drainHandler!(args).ConfigureAwait(false);
                    JsonNode? root = JsonNode.Parse(JsonSerializer.Serialize(result));
                    JsonArray? reflections = root?["reflectionsDispatched"]?.AsArray();

                    AssertNotNull(reflections);
                    AssertEqual(0, reflections!.Count);
                    AssertEqual(0, admiral.DispatchCount);
                }
            });

            await RunTest("Drain_MixedActiveAndInactive_AllVesselsPath_DispatchesOnlyActive", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = new ArmadaSettings { DefaultReflectionThreshold = 2 };
                    Vessel activeVessel = await CreateVesselAsync(testDb.Driver, "mixed-active").ConfigureAwait(false);
                    activeVessel.ReflectionThreshold = 2;
                    activeVessel = await testDb.Driver.Vessels.UpdateAsync(activeVessel).ConfigureAwait(false);

                    Vessel inactiveVessel = await CreateVesselAsync(testDb.Driver, "mixed-inactive").ConfigureAwait(false);
                    inactiveVessel.Active = false;
                    inactiveVessel.ReflectionThreshold = 2;
                    inactiveVessel = await testDb.Driver.Vessels.UpdateAsync(inactiveVessel).ConfigureAwait(false);

                    for (int i = 0; i < 2; i++)
                    {
                        await CreateTerminalMissionAsync(
                                testDb.Driver,
                                activeVessel.Id,
                                "active" + i,
                                DateTime.UtcNow.AddMinutes(-10 + i))
                            .ConfigureAwait(false);
                        await CreateTerminalMissionAsync(
                                testDb.Driver,
                                inactiveVessel.Id,
                                "inactive" + i,
                                DateTime.UtcNow.AddMinutes(-10 + i))
                            .ConfigureAwait(false);
                    }

                    RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                    ReflectionDispatcher dispatcher = new ReflectionDispatcher(
                        testDb.Driver,
                        admiral,
                        settings,
                        new ReflectionMemoryService(testDb.Driver));

                    Func<JsonElement?, Task<object>>? drainHandler = null;
                    McpAuditTools.Register(
                        (name, _, _, h) => { if (name == "armada_drain_audit_queue") drainHandler = h; },
                        testDb.Driver,
                        null,
                        dispatcher);
                    AssertNotNull(drainHandler);

                    JsonElement args = JsonSerializer.SerializeToElement(new { limit = 10 });
                    object result = await drainHandler!(args).ConfigureAwait(false);
                    JsonNode? root = JsonNode.Parse(JsonSerializer.Serialize(result));
                    JsonArray? reflections = root?["reflectionsDispatched"]?.AsArray();

                    AssertNotNull(reflections);
                    AssertEqual(1, reflections!.Count);
                    AssertEqual(activeVessel.Id, reflections[0]?["vesselId"]?.GetValue<string>());
                    AssertEqual(1, admiral.DispatchCount);
                }
            });
        }

        private static async Task<Vessel> CreateVesselAsync(DatabaseDriver database, string name)
        {
            Vessel vessel = new Vessel(name, "https://github.com/test/" + name + ".git");
            vessel.TenantId = Constants.DefaultTenantId;
            return await database.Vessels.CreateAsync(vessel).ConfigureAwait(false);
        }

        private static async Task<Mission> CreateTerminalMissionAsync(
            DatabaseDriver database,
            string vesselId,
            string title,
            DateTime completedUtc)
        {
            Mission mission = new Mission(title, "desc " + title);
            mission.VesselId = vesselId;
            mission.Persona = "Worker";
            mission.Status = MissionStatusEnum.Complete;
            mission.CompletedUtc = completedUtc;
            mission.DiffSnapshot = "diff " + title;
            mission.AgentOutput = "output " + title;
            return await database.Missions.CreateAsync(mission).ConfigureAwait(false);
        }

        private sealed class RecordingAdmiralService : IAdmiralService
        {
            private readonly DatabaseDriver _Database;

            public int DispatchCount { get; private set; }

            public Func<Captain, Mission, Dock, Task<int>>? OnLaunchAgent { get; set; }
            public Func<Captain, Task>? OnStopAgent { get; set; }
            public Func<Mission, Dock, Task>? OnCaptureDiff { get; set; }
            public Func<Mission, Dock, Task>? OnMissionComplete { get; set; }
            public Func<Voyage, Task>? OnVoyageComplete { get; set; }
            public Func<Mission, Task<bool>>? OnReconcilePullRequest { get; set; }
            public Func<Task<int>>? OnReconcileMergeEntries { get; set; }
            public Func<int, bool>? OnIsProcessExitHandled { get; set; }

            public RecordingAdmiralService(DatabaseDriver database)
            {
                _Database = database;
            }

            public Task<Voyage> DispatchVoyageAsync(
                string title,
                string description,
                string vesselId,
                List<MissionDescription> missionDescriptions,
                CancellationToken token = default)
            {
                return DispatchVoyageAsync(title, description, vesselId, missionDescriptions, (string?)null, token);
            }

            public Task<Voyage> DispatchVoyageAsync(
                string title,
                string description,
                string vesselId,
                List<MissionDescription> missionDescriptions,
                List<SelectedPlaybook>? selectedPlaybooks,
                CancellationToken token = default)
            {
                return DispatchVoyageAsync(title, description, vesselId, missionDescriptions, (string?)null, token);
            }

            public async Task<Voyage> DispatchVoyageAsync(
                string title,
                string description,
                string vesselId,
                List<MissionDescription> missionDescriptions,
                string? pipelineId,
                CancellationToken token = default)
            {
                DispatchCount++;
                Voyage voyage = await _Database.Voyages.CreateAsync(new Voyage(title, description), token).ConfigureAwait(false);
                foreach (MissionDescription md in missionDescriptions)
                {
                    Mission mission = new Mission(md.Title, md.Description);
                    mission.VoyageId = voyage.Id;
                    mission.VesselId = vesselId;
                    mission.Persona = pipelineId == "Reflections" ? "MemoryConsolidator" : "Worker";
                    mission.PreferredModel = md.PreferredModel;
                    await _Database.Missions.CreateAsync(mission, token).ConfigureAwait(false);
                }

                return voyage;
            }

            public Task<Voyage> DispatchVoyageAsync(
                string title,
                string description,
                string vesselId,
                List<MissionDescription> missionDescriptions,
                string? pipelineId,
                List<SelectedPlaybook>? selectedPlaybooks,
                CancellationToken token = default)
            {
                return DispatchVoyageAsync(title, description, vesselId, missionDescriptions, pipelineId, token);
            }

            public Task<Mission> DispatchMissionAsync(Mission mission, CancellationToken token = default)
                => throw new NotImplementedException();

            public Task<Pipeline?> ResolvePipelineAsync(string? pipelineIdOrName, Vessel vessel, CancellationToken token = default)
                => throw new NotImplementedException();

            public Task<ArmadaStatus> GetStatusAsync(CancellationToken token = default)
                => throw new NotImplementedException();

            public Task RecallCaptainAsync(string captainId, CancellationToken token = default)
                => throw new NotImplementedException();

            public Task RecallAllAsync(CancellationToken token = default)
                => throw new NotImplementedException();

            public Task HealthCheckAsync(CancellationToken token = default)
                => throw new NotImplementedException();

            public Task CleanupStaleCaptainsAsync(CancellationToken token = default)
                => throw new NotImplementedException();

            public Task HandleProcessExitAsync(
                int processId,
                int? exitCode,
                string captainId,
                string missionId,
                CancellationToken token = default)
                => throw new NotImplementedException();
        }
    }
}
