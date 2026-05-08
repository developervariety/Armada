namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
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
    /// Tests for armada_consolidate_memory and the shared ReflectionDispatcher helper.
    /// </summary>
    public class ReflectionConsolidateToolsTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Reflection Consolidate Tools";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("ConsolidateMemory_MissingVessel_ReturnsVesselNotFound", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = new ArmadaSettings();
                    RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                    ReflectionDispatcher dispatcher = CreateDispatcher(testDb.Driver, admiral, settings);
                    Func<JsonElement?, Task<object>>? handler = CaptureHandler(testDb.Driver, dispatcher, settings);

                    JsonElement args = JsonSerializer.SerializeToElement(new { vesselId = "vsl_missing" });
                    object result = await handler!(args).ConfigureAwait(false);
                    string json = JsonSerializer.Serialize(result);

                    AssertContains("vessel_not_found", json, "Missing vessel should return stable error");
                    AssertEqual(0, admiral.DispatchCount, "Missing vessel must not dispatch");
                }
            });

            await RunTest("ConsolidateMemory_NoTerminalEvidence_ReturnsNoEvidenceAvailable", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, "rc-no-evidence").ConfigureAwait(false);
                    ArmadaSettings settings = new ArmadaSettings();
                    RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                    ReflectionDispatcher dispatcher = CreateDispatcher(testDb.Driver, admiral, settings);
                    Func<JsonElement?, Task<object>>? handler = CaptureHandler(testDb.Driver, dispatcher, settings);

                    JsonElement args = JsonSerializer.SerializeToElement(new { vesselId = vessel.Id });
                    object result = await handler!(args).ConfigureAwait(false);
                    string json = JsonSerializer.Serialize(result);

                    AssertContains("no_evidence_available", json, "No terminal missions should no-op");
                    AssertEqual(0, admiral.DispatchCount, "No evidence must not dispatch");
                }
            });

            await RunTest("ConsolidateMemory_InFlightReflection_ReturnsExistingMission", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, "rc-inflight").ConfigureAwait(false);
                    Mission existing = new Mission("existing reflection", "desc");
                    existing.VesselId = vessel.Id;
                    existing.Persona = "MemoryConsolidator";
                    existing.Status = MissionStatusEnum.InProgress;
                    existing = await testDb.Driver.Missions.CreateAsync(existing).ConfigureAwait(false);

                    ArmadaSettings settings = new ArmadaSettings();
                    RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                    ReflectionDispatcher dispatcher = CreateDispatcher(testDb.Driver, admiral, settings);
                    Func<JsonElement?, Task<object>>? handler = CaptureHandler(testDb.Driver, dispatcher, settings);

                    JsonElement args = JsonSerializer.SerializeToElement(new { vesselId = vessel.Id });
                    object result = await handler!(args).ConfigureAwait(false);
                    string json = JsonSerializer.Serialize(result);

                    AssertContains("reflection_already_in_flight", json, "Duplicate trigger should surface in-flight mission");
                    AssertContains(existing.Id, json, "Existing mission id should be returned");
                    AssertEqual(0, admiral.DispatchCount, "In-flight reflection must not dispatch another");
                }
            });

            await RunTest("BuildEvidenceBundle_FirstRun_UsesInitialReflectionWindow", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, "rc-window").ConfigureAwait(false);
                    Mission oldest = await CreateTerminalMissionAsync(testDb.Driver, vessel.Id, "oldest", DateTime.UtcNow.AddMinutes(-30)).ConfigureAwait(false);
                    Mission middle = await CreateTerminalMissionAsync(testDb.Driver, vessel.Id, "middle", DateTime.UtcNow.AddMinutes(-20)).ConfigureAwait(false);
                    Mission newest = await CreateTerminalMissionAsync(testDb.Driver, vessel.Id, "newest", DateTime.UtcNow.AddMinutes(-10)).ConfigureAwait(false);

                    ArmadaSettings settings = new ArmadaSettings { InitialReflectionWindow = 2 };
                    RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                    ReflectionDispatcher dispatcher = CreateDispatcher(testDb.Driver, admiral, settings);

                    ReflectionDispatcher.EvidenceBundleResult bundle = await dispatcher.BuildEvidenceBundleAsync(
                        vessel,
                        null,
                        settings.DefaultReflectionTokenBudget).ConfigureAwait(false);

                    AssertEqual(2, bundle.EvidenceMissionCount, "First run should include InitialReflectionWindow missions");
                    AssertContains(newest.Id, bundle.Brief, "Newest mission should be included");
                    AssertContains(middle.Id, bundle.Brief, "Second newest mission should be included");
                    AssertFalse(bundle.Brief.Contains(oldest.Id), "Oldest mission should be outside the first-run window");
                }
            });

            await RunTest("BuildEvidenceBundle_SinceMissionId_IncludesOnlyNewerTerminalEvidence", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, "rc-since").ConfigureAwait(false);
                    Mission before = await CreateTerminalMissionAsync(testDb.Driver, vessel.Id, "before", DateTime.UtcNow.AddMinutes(-40)).ConfigureAwait(false);
                    Mission marker = await CreateTerminalMissionAsync(testDb.Driver, vessel.Id, "marker", DateTime.UtcNow.AddMinutes(-30)).ConfigureAwait(false);
                    Mission after = await CreateTerminalMissionAsync(testDb.Driver, vessel.Id, "after", DateTime.UtcNow.AddMinutes(-20)).ConfigureAwait(false);

                    ReflectionDispatcher dispatcher = CreateDispatcher(
                        testDb.Driver,
                        new RecordingAdmiralService(testDb.Driver),
                        new ArmadaSettings { InitialReflectionWindow = 10 });

                    ReflectionDispatcher.EvidenceBundleResult bundle = await dispatcher.BuildEvidenceBundleAsync(
                        vessel,
                        marker.Id,
                        8000).ConfigureAwait(false);

                    AssertEqual(1, bundle.EvidenceMissionCount, "Only missions completed after sinceMissionId should be included");
                    AssertContains(after.Id, bundle.Brief, "Newer mission should be included");
                    AssertFalse(bundle.Brief.Contains(marker.Id), "sinceMissionId marker should not be included");
                    AssertFalse(bundle.Brief.Contains(before.Id), "Older mission should not be included");
                }
            });

            await RunTest("BuildEvidenceBundle_LastReflectionMissionId_IncludesOnlyNewerTerminalEvidence", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, "rc-last-reflection").ConfigureAwait(false);
                    Mission before = await CreateTerminalMissionAsync(testDb.Driver, vessel.Id, "before", DateTime.UtcNow.AddMinutes(-40)).ConfigureAwait(false);
                    Mission lastReflection = await CreateTerminalMissionAsync(testDb.Driver, vessel.Id, "last reflection", DateTime.UtcNow.AddMinutes(-30)).ConfigureAwait(false);
                    Mission after = await CreateTerminalMissionAsync(testDb.Driver, vessel.Id, "after", DateTime.UtcNow.AddMinutes(-20)).ConfigureAwait(false);
                    vessel.LastReflectionMissionId = lastReflection.Id;

                    ReflectionDispatcher dispatcher = CreateDispatcher(
                        testDb.Driver,
                        new RecordingAdmiralService(testDb.Driver),
                        new ArmadaSettings { InitialReflectionWindow = 10 });

                    ReflectionDispatcher.EvidenceBundleResult bundle = await dispatcher.BuildEvidenceBundleAsync(
                        vessel,
                        null,
                        8000).ConfigureAwait(false);

                    AssertEqual(1, bundle.EvidenceMissionCount, "Stored last reflection should start the evidence window");
                    AssertContains(after.Id, bundle.Brief, "Newer mission should be included");
                    AssertFalse(bundle.Brief.Contains(lastReflection.Id), "Last reflection mission should not be included again");
                    AssertFalse(bundle.Brief.Contains(before.Id), "Older mission should not be included");
                }
            });

            await RunTest("DispatchReflection_UsesReflectionsPipelineAndHighTier", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, "rc-dispatch").ConfigureAwait(false);
                    ArmadaSettings settings = new ArmadaSettings();
                    RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                    ReflectionDispatcher dispatcher = CreateDispatcher(testDb.Driver, admiral, settings);

                    ReflectionDispatcher.DispatchResult result = await dispatcher.DispatchReflectionAsync(vessel, "brief").ConfigureAwait(false);
                    Mission? mission = await testDb.Driver.Missions.ReadAsync(result.MissionId).ConfigureAwait(false);

                    AssertEqual("Reflections", admiral.LastPipelineId, "Dispatcher should request Reflections pipeline");
                    AssertEqual(1, admiral.DispatchCount, "Dispatcher should dispatch exactly once");
                    AssertNotNull(mission, "Reflection mission should be persisted by the admiral double");
                    AssertEqual("MemoryConsolidator", mission!.Persona, "Reflection mission persona");
                    AssertEqual("high", mission.PreferredModel, "Reflection mission model tier");
                }
            });

            await RunTest("BuildEvidenceBundle_SmallTokenBudget_TruncatesOldestEvidence", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, "rc-truncate").ConfigureAwait(false);
                    await CreateTerminalMissionAsync(testDb.Driver, vessel.Id, "one", DateTime.UtcNow.AddMinutes(-30), new string('a', 5000)).ConfigureAwait(false);
                    await CreateTerminalMissionAsync(testDb.Driver, vessel.Id, "two", DateTime.UtcNow.AddMinutes(-20), new string('b', 5000)).ConfigureAwait(false);
                    await CreateTerminalMissionAsync(testDb.Driver, vessel.Id, "three", DateTime.UtcNow.AddMinutes(-10), new string('c', 5000)).ConfigureAwait(false);

                    ArmadaSettings settings = new ArmadaSettings { InitialReflectionWindow = 10 };
                    RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                    ReflectionDispatcher dispatcher = CreateDispatcher(testDb.Driver, admiral, settings);

                    ReflectionDispatcher.EvidenceBundleResult bundle = await dispatcher.BuildEvidenceBundleAsync(
                        vessel,
                        null,
                        2000).ConfigureAwait(false);

                    AssertTrue(bundle.Truncated, "Small token budget should evict older evidence");
                    AssertTrue(bundle.EvidenceMissionCount < 3, "Truncated bundle should include fewer mission records");
                    AssertContains("Evidence truncated", bundle.Brief, "Brief should explain skipped evidence");
                }
            });

            await RunTest("IsReflectionInFlight_CompleteReflection_IgnoresTerminalMission", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, "rc-terminal").ConfigureAwait(false);
                    Mission terminal = new Mission("done reflection", "desc");
                    terminal.VesselId = vessel.Id;
                    terminal.Persona = "MemoryConsolidator";
                    terminal.Status = MissionStatusEnum.Complete;
                    terminal.CompletedUtc = DateTime.UtcNow;
                    await testDb.Driver.Missions.CreateAsync(terminal).ConfigureAwait(false);

                    ReflectionDispatcher dispatcher = CreateDispatcher(
                        testDb.Driver,
                        new RecordingAdmiralService(testDb.Driver),
                        new ArmadaSettings());

                    Mission? inFlight = await dispatcher.IsReflectionInFlightAsync(vessel.Id).ConfigureAwait(false);
                    AssertNull(inFlight, "Terminal MemoryConsolidator mission should not block a new reflection");
                }
            });
        }

        private static Func<JsonElement?, Task<object>>? CaptureHandler(
            DatabaseDriver database,
            ReflectionDispatcher dispatcher,
            ArmadaSettings settings)
        {
            Func<JsonElement?, Task<object>>? handler = null;
            McpReflectionTools.Register(
                (name, _, _, h) => { if (name == "armada_consolidate_memory") handler = h; },
                database,
                dispatcher,
                settings);
            if (handler == null) throw new InvalidOperationException("armada_consolidate_memory handler should be registered");
            return handler;
        }

        private static ReflectionDispatcher CreateDispatcher(DatabaseDriver database, IAdmiralService admiral, ArmadaSettings settings)
        {
            return new ReflectionDispatcher(database, admiral, settings, new ReflectionMemoryService(database));
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
            DateTime completedUtc,
            string? diff = null)
        {
            Mission mission = new Mission(title, "desc " + title);
            mission.VesselId = vesselId;
            mission.Persona = "Worker";
            mission.Status = MissionStatusEnum.Complete;
            mission.CompletedUtc = completedUtc;
            mission.DiffSnapshot = diff ?? "diff " + title;
            mission.AgentOutput = "output " + title;
            return await database.Missions.CreateAsync(mission).ConfigureAwait(false);
        }

        private sealed class RecordingAdmiralService : IAdmiralService
        {
            private readonly DatabaseDriver _Database;

            public int DispatchCount { get; private set; }

            public string? LastPipelineId { get; private set; }

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
                LastPipelineId = pipelineId;
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
