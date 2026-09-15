namespace Armada.Test.Unit.Suites.Routes
{
    using System;
    using System.Collections.Generic;
    using System.Reflection;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using Armada.Server.WebSocket;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// A record created through a WebSocket command is owned by the authenticated session caller: its tenant and
    /// user are the caller's, exactly as the matching REST create sets them, whatever owner the command body names.
    /// A create command without an authenticated caller is refused and writes nothing.
    /// </summary>
    public class WebSocketCreateOwnershipTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "WebSocket Create Ownership";

        private static readonly JsonSerializerOptions _ServerJsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };

        private static async Task<AuthContext> SeedCallerAsync(TestDatabase testDb)
        {
            TenantMetadata tenant = await testDb.Driver.Tenants.CreateAsync(new TenantMetadata("ws-owner-tenant-" + Guid.NewGuid().ToString("N"))).ConfigureAwait(false);
            UserMaster user = await testDb.Driver.Users.CreateAsync(new UserMaster(tenant.Id, "ws-owner-" + Guid.NewGuid().ToString("N") + "@example.com", "password")).ConfigureAwait(false);
            // The hub admits only global administrators to commands, so the caller is one, acting in its own tenant.
            return AuthContext.Authenticated(tenant.Id, user.Id, true, false, "Test");
        }

        private static WebSocketCommandHandler CreateHandler(TestDatabase testDb, IAdmiralService? admiral = null, IMergeQueueService? mergeQueue = null)
        {
            return new WebSocketCommandHandler(
                admiral ?? null!,
                testDb.Driver,
                mergeQueue ?? null!,
                null,
                null,
                null,
                _ServerJsonOptions,
                mission => { },
                voyage => { });
        }

        private static async Task<string> SendAsync(WebSocketCommandHandler handler, string action, object data, AuthContext? caller, string? id = null)
        {
            string rawBody = JsonSerializer.Serialize(new { Route = "command", action = action, id = id, data = data });
            object result = await handler.HandleCommandAsync(action, new WebSocketCommand { Action = action, Id = id }, rawBody, caller).ConfigureAwait(false);
            return JsonSerializer.Serialize(result, _ServerJsonOptions);
        }

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("CreateFleet_IsOwnedByTheCallerWhateverTheBodyNames", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    AuthContext caller = await SeedCallerAsync(testDb).ConfigureAwait(false);
                    string json = await SendAsync(CreateHandler(testDb), "create_fleet",
                        new { Name = "ws-owned-fleet", TenantId = Armada.Core.Constants.DefaultTenantId, UserId = Armada.Core.Constants.DefaultUserId }, caller).ConfigureAwait(false);

                    List<Fleet> fleets = await testDb.Driver.Fleets.EnumerateAsync().ConfigureAwait(false);
                    Fleet? stored = fleets.Find(f => f.Name == "ws-owned-fleet");
                    AssertNotNull(stored, "the fleet is created: " + json);
                    AssertEqual(caller.TenantId, stored!.TenantId, "the fleet belongs to the caller's tenant, not the tenant the body names");
                    AssertEqual(caller.UserId, stored.UserId, "the fleet belongs to the calling user");
                }
            }).ConfigureAwait(false);

            await RunTest("CreateFleet_WithoutAnAuthenticatedCaller_IsRefusedAndWritesNothing", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    string json = await SendAsync(CreateHandler(testDb), "create_fleet", new { Name = "ws-anonymous-fleet" }, null).ConfigureAwait(false);

                    AssertContains("command.error", json, "a create without a caller is refused");
                    AssertContains("authenticated caller", json, "the refusal names the missing caller");
                    List<Fleet> fleets = await testDb.Driver.Fleets.EnumerateAsync().ConfigureAwait(false);
                    AssertFalse(fleets.Exists(f => f.Name == "ws-anonymous-fleet"), "a refused create writes nothing");
                }
            }).ConfigureAwait(false);

            await RunTest("CreateVessel_IsOwnedByTheCaller", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    AuthContext caller = await SeedCallerAsync(testDb).ConfigureAwait(false);
                    string json = await SendAsync(CreateHandler(testDb), "create_vessel",
                        new { Name = "ws-owned-vessel", RepoUrl = "https://github.com/test/ws-owned.git" }, caller).ConfigureAwait(false);

                    List<Vessel> vessels = await testDb.Driver.Vessels.EnumerateAsync().ConfigureAwait(false);
                    Vessel? stored = vessels.Find(v => v.Name == "ws-owned-vessel");
                    AssertNotNull(stored, "the vessel is created: " + json);
                    AssertEqual(caller.TenantId, stored!.TenantId, "the vessel belongs to the caller's tenant");
                    AssertEqual(caller.UserId, stored.UserId, "the vessel belongs to the calling user");
                }
            }).ConfigureAwait(false);

            await RunTest("CreateVoyage_WithoutMissions_IsOwnedByTheCaller", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    AuthContext caller = await SeedCallerAsync(testDb).ConfigureAwait(false);
                    string json = await SendAsync(CreateHandler(testDb), "create_voyage", new { Title = "ws-owned-voyage" }, caller).ConfigureAwait(false);

                    List<Voyage> voyages = await testDb.Driver.Voyages.EnumerateAsync().ConfigureAwait(false);
                    Voyage? stored = voyages.Find(v => v.Title == "ws-owned-voyage");
                    AssertNotNull(stored, "the voyage is created: " + json);
                    AssertEqual(caller.TenantId, stored!.TenantId, "the voyage belongs to the caller's tenant");
                    AssertEqual(caller.UserId, stored.UserId, "the voyage belongs to the calling user");
                }
            }).ConfigureAwait(false);

            await RunTest("CreateMission_IsOwnedByTheCaller", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    AuthContext caller = await SeedCallerAsync(testDb).ConfigureAwait(false);
                    IAdmiralService admiral = DispatchProxy.Create<IAdmiralService, RecordingAdmiralProxy>();
                    await SendAsync(CreateHandler(testDb, admiral: admiral), "create_mission",
                        new { Title = "ws-owned-mission", Description = "owned", VesselId = "vsl_owned" }, caller).ConfigureAwait(false);

                    Mission? dispatched = ((RecordingAdmiralProxy)(object)admiral).LastDispatched;
                    AssertNotNull(dispatched, "the mission reaches dispatch");
                    AssertEqual(caller.TenantId, dispatched!.TenantId, "the mission belongs to the caller's tenant");
                    AssertEqual(caller.UserId, dispatched.UserId, "the mission belongs to the calling user");
                }
            }).ConfigureAwait(false);

            await RunTest("CreateCaptain_IsOwnedByTheCaller", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    AuthContext caller = await SeedCallerAsync(testDb).ConfigureAwait(false);
                    string json = await SendAsync(CreateHandler(testDb), "create_captain", new { Name = "ws-owned-captain" }, caller).ConfigureAwait(false);

                    Captain? stored = await testDb.Driver.Captains.ReadByNameAsync("ws-owned-captain").ConfigureAwait(false);
                    AssertNotNull(stored, "the captain is created: " + json);
                    AssertEqual(caller.TenantId, stored!.TenantId, "the captain belongs to the caller's tenant");
                    AssertEqual(caller.UserId, stored.UserId, "the captain belongs to the calling user");
                }
            }).ConfigureAwait(false);

            await RunTest("SendSignal_IsOwnedByTheCaller", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    AuthContext caller = await SeedCallerAsync(testDb).ConfigureAwait(false);
                    Captain recipient = await testDb.Driver.Captains.CreateAsync(new Captain("ws-signal-recipient")).ConfigureAwait(false);
                    string json = await SendAsync(CreateHandler(testDb), "send_signal",
                        new { Type = "Mail", Payload = "ws-owned-signal", ToCaptainId = recipient.Id }, caller).ConfigureAwait(false);

                    List<Signal> signals = await testDb.Driver.Signals.EnumerateRecentAsync(100).ConfigureAwait(false);
                    Signal? stored = signals.Find(s => s.Payload == "ws-owned-signal");
                    AssertNotNull(stored, "the signal is created: " + json);
                    AssertEqual(caller.TenantId, stored!.TenantId, "the signal belongs to the caller's tenant");
                    AssertEqual(caller.UserId, stored.UserId, "the signal belongs to the calling user");
                }
            }).ConfigureAwait(false);

            await RunTest("EnqueueMerge_IsOwnedByTheCaller", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    AuthContext caller = await SeedCallerAsync(testDb).ConfigureAwait(false);
                    IMergeQueueService mergeQueue = DispatchProxy.Create<IMergeQueueService, RecordingMergeQueueProxy>();
                    await SendAsync(CreateHandler(testDb, mergeQueue: mergeQueue), "enqueue_merge",
                        new { VesselId = "vsl_owned", BranchName = "feature/ws-owned" }, caller).ConfigureAwait(false);

                    MergeEntry? entry = ((RecordingMergeQueueProxy)(object)mergeQueue).LastEnqueued;
                    AssertNotNull(entry, "the entry reaches the queue");
                    AssertEqual(caller.TenantId, entry!.TenantId, "the merge entry belongs to the caller's tenant");
                    AssertEqual(caller.UserId, entry.UserId, "the merge entry belongs to the calling user");
                }
            }).ConfigureAwait(false);

            await RunTest("CreatePersonaAndPipeline_AreOwnedByTheCaller", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    AuthContext caller = await SeedCallerAsync(testDb).ConfigureAwait(false);
                    WebSocketCommandHandler handler = CreateHandler(testDb);

                    string personaJson = await SendAsync(handler, "create_persona", new { Name = "WsOwnedPersona", PromptTemplateName = "persona.worker" }, caller).ConfigureAwait(false);
                    Persona? persona = await testDb.Driver.Personas.ReadByNameAsync("WsOwnedPersona").ConfigureAwait(false);
                    AssertNotNull(persona, "the persona is created: " + personaJson);
                    AssertEqual(caller.TenantId, persona!.TenantId, "the persona belongs to the caller's tenant");
                    AssertEqual(caller.UserId, persona.UserId, "the persona belongs to the calling user");

                    string pipelineJson = await SendAsync(handler, "create_pipeline", new { Name = "WsOwnedPipeline" }, caller).ConfigureAwait(false);
                    Pipeline? pipeline = await testDb.Driver.Pipelines.ReadByNameAsync("WsOwnedPipeline").ConfigureAwait(false);
                    AssertNotNull(pipeline, "the pipeline is created: " + pipelineJson);
                    AssertEqual(caller.TenantId, pipeline!.TenantId, "the pipeline belongs to the caller's tenant");
                    AssertEqual(caller.UserId, pipeline.UserId, "the pipeline belongs to the calling user");
                }
            }).ConfigureAwait(false);

            await RunTest("RestartMission_ProgressSignalIsOwnedLikeTheMission", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    AuthContext caller = await SeedCallerAsync(testDb).ConfigureAwait(false);
                    Vessel vessel = new Vessel("ws-restart-vessel", "https://github.com/test/ws-restart.git");
                    vessel.TenantId = caller.TenantId;
                    vessel.UserId = caller.UserId;
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);
                    Mission mission = await testDb.Driver.Missions.CreateAsync(new Mission("ws restart owned")
                    {
                        TenantId = caller.TenantId,
                        UserId = caller.UserId,
                        VesselId = vessel.Id,
                        Status = MissionStatusEnum.Failed
                    }).ConfigureAwait(false);

                    string json = await SendAsync(CreateHandler(testDb), "restart_mission", new { }, McpTestCaller.Operator, mission.Id).ConfigureAwait(false);

                    List<Signal> signals = await testDb.Driver.Signals.EnumerateRecentAsync(100).ConfigureAwait(false);
                    Signal? restarted = signals.Find(s => (s.Payload ?? String.Empty).Contains(mission.Id + " restarted", StringComparison.Ordinal));
                    AssertNotNull(restarted, "the restart writes a progress signal: " + json);
                    AssertEqual(caller.TenantId, restarted!.TenantId, "the restart signal belongs to the mission's tenant");
                    AssertEqual(caller.UserId, restarted.UserId, "the restart signal belongs to the mission's user");
                }
            }).ConfigureAwait(false);
        }

        /// <summary>
        /// Admiral stand-in that records the mission passed to DispatchMissionAsync and returns it unchanged.
        /// </summary>
        public class RecordingAdmiralProxy : DispatchProxy
        {
            /// <summary>The last mission passed to DispatchMissionAsync.</summary>
            public Mission? LastDispatched { get; private set; }

            /// <inheritdoc />
            protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            {
                if (targetMethod != null && targetMethod.Name == "DispatchMissionAsync" && args != null && args.Length > 0 && args[0] is Mission mission)
                {
                    LastDispatched = mission;
                    return Task.FromResult(mission);
                }
                throw new NotSupportedException((targetMethod?.Name ?? "unknown") + " is not used by these tests.");
            }
        }

        /// <summary>
        /// Merge queue stand-in that records the entry passed to EnqueueAsync and returns it unchanged.
        /// </summary>
        public class RecordingMergeQueueProxy : DispatchProxy
        {
            /// <summary>The last entry passed to EnqueueAsync.</summary>
            public MergeEntry? LastEnqueued { get; private set; }

            /// <inheritdoc />
            protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            {
                if (targetMethod != null && targetMethod.Name == "EnqueueAsync" && args != null && args.Length > 0 && args[0] is MergeEntry entry)
                {
                    LastEnqueued = entry;
                    return Task.FromResult(entry);
                }
                throw new NotSupportedException((targetMethod?.Name ?? "unknown") + " is not used by these tests.");
            }
        }
    }
}
