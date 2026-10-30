namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.IO;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Enums;
    using Armada.Core.Database.Sqlite;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Server.Mcp;
    using Armada.Server.Mcp.Tools;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;
    using TestResourcePressure = global::Test.Shared.Infrastructure.TestResourcePressure;

    /// <summary>
    /// Captain suite lease: per-vessel and host-wide limits, wait position, named wait event, auto-release,
    /// and the mission-scoped MCP tool.
    /// </summary>
    public sealed class CaptainSuiteLeaseTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Captain Suite Lease";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("A second full-suite lease on the same vessel waits and names its position", async () =>
            {
                string vessel = "vsl_" + "lease_one_" + Guid.NewGuid().ToString("N");
                string firstMission = "msn_" + "lease_hold_" + Guid.NewGuid().ToString("N");
                string secondMission = "msn_" + "lease_wait_" + Guid.NewGuid().ToString("N");
                Func<long?> previousReader = HostWideCommandLock.MemoryReader;
                int previousFloor = HostWideCommandLock.MemoryFloorMb;
                using (CancellationTokenSource cancel = new CancellationTokenSource())
                {
                    try
                    {
                        HostWideCommandLock.SetMemoryReader(() => null);
                        HostWideCommandLock.SetMemoryFloorMb(0);
                        HostWideCommandLock.SetSlotCount(2);
                        HostWideCommandLock.SetVesselSlotCount(1);

                        IDisposable first = await CaptainSuiteLeaseRegistry.AcquireAsync(firstMission, vessel, cancel.Token).ConfigureAwait(false);
                        Task<IDisposable> second = CaptainSuiteLeaseRegistry.AcquireAsync(secondMission, vessel, cancel.Token);
                        await Task.Delay(50).ConfigureAwait(false);

                        AssertTrue(CaptainSuiteLeaseRegistry.IsActive(firstMission), "the first mission holds the lease");
                        AssertFalse(CaptainSuiteLeaseRegistry.IsWaiting(firstMission), "the holder is not waiting");
                        AssertTrue(CaptainSuiteLeaseRegistry.IsWaiting(secondMission), "the second mission waits");
                        AssertEqual(1, CaptainSuiteLeaseRegistry.ReadWaitPosition(secondMission), "the wait names position 1");
                        AssertFalse(second.IsCompleted, "the second acquire does not finish while the vessel slot is held");

                        CaptainSuiteLeaseRegistry.Release(firstMission);
                        first.Dispose();
                        IDisposable secondLease = await second.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                        AssertTrue(CaptainSuiteLeaseRegistry.IsActive(secondMission), "the waiter is granted after the first release");
                        AssertEqual(0, CaptainSuiteLeaseRegistry.ReadWaitPosition(secondMission), "a granted lease has no wait position");
                        secondLease.Dispose();
                        CaptainSuiteLeaseRegistry.Release(secondMission);
                    }
                    finally
                    {
                        cancel.Cancel();
                        CaptainSuiteLeaseRegistry.Release(firstMission);
                        CaptainSuiteLeaseRegistry.Release(secondMission);
                        RestoreLock(previousReader, previousFloor);
                    }
                }
            });

            await RunTest("Cancelling a mission releases its suite lease", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    string vesselKey = "vsl_" + "lease_cancel_" + Guid.NewGuid().ToString("N");
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(new Vessel("lease-cancel-vessel", "https://example.invalid/lease-cancel.git")).ConfigureAwait(false);
                    Mission mission = await testDb.Driver.Missions.CreateAsync(new Mission("lease cancel")
                    {
                        TenantId = Constants.DefaultTenantId,
                        UserId = Constants.DefaultUserId,
                        VesselId = vessel.Id,
                        Status = MissionStatusEnum.InProgress
                    }).ConfigureAwait(false);
                    Func<long?> previousReader = HostWideCommandLock.MemoryReader;
                    int previousFloor = HostWideCommandLock.MemoryFloorMb;
                    try
                    {
                        HostWideCommandLock.SetMemoryReader(() => null);
                        HostWideCommandLock.SetMemoryFloorMb(0);
                        await CaptainSuiteLeaseRegistry.AcquireAsync(mission.Id, vesselKey, CancellationToken.None).ConfigureAwait(false);
                        AssertTrue(CaptainSuiteLeaseRegistry.IsActive(mission.Id), "the mission holds a lease before cancel");

                        MissionCancellationResult cancelled = await MissionCancellation.CancelAsync(
                            testDb.Driver, mission, MissionCancellation.OperatorCancelReason, null).ConfigureAwait(false);
                        AssertTrue(cancelled.Succeeded, "the in-progress mission is cancelled");
                        AssertFalse(CaptainSuiteLeaseRegistry.IsActive(mission.Id), "cancel releases the suite lease");
                    }
                    finally
                    {
                        CaptainSuiteLeaseRegistry.Release(mission.Id);
                        RestoreLock(previousReader, previousFloor);
                    }
                }
            });

            await RunTest("armada_suite_lease is mission-scoped, waits with a named event, and releases", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(new Vessel("lease-mcp-vessel", "https://example.invalid/lease-mcp.git")).ConfigureAwait(false);
                    Mission holder = await testDb.Driver.Missions.CreateAsync(new Mission("lease mcp holder")
                    {
                        TenantId = Constants.DefaultTenantId,
                        UserId = Constants.DefaultUserId,
                        VesselId = vessel.Id,
                        Status = MissionStatusEnum.InProgress
                    }).ConfigureAwait(false);
                    Mission waiter = await testDb.Driver.Missions.CreateAsync(new Mission("lease mcp waiter")
                    {
                        TenantId = Constants.DefaultTenantId,
                        UserId = Constants.DefaultUserId,
                        VesselId = vessel.Id,
                        Status = MissionStatusEnum.InProgress
                    }).ConfigureAwait(false);

                    Func<JsonElement?, Task<object>>? tool = null;
                    McpSuiteLeaseTools.Register((name, _, _, handler) =>
                    {
                        if (name == McpSuiteLeaseTools.ToolName) tool = handler;
                    }, testDb.Driver, null);
                    AssertNotNull(tool, "the suite lease tool is registered");

                    AuthContext noMission = McpTestCaller.Operator;
                    using (McpCallerContext.Begin(noMission))
                    {
                        string refused = JsonSerializer.Serialize(await tool!(JsonSerializer.SerializeToElement(new { action = "acquire" })).ConfigureAwait(false));
                        AssertContains("mission_required", refused, "a caller with no mission credential cannot acquire");
                    }

                    AuthContext missionCaller = McpTestCaller.Operator;
                    missionCaller.MissionId = waiter.Id;
                    AssertTrue(McpToolAccessPolicy.IsAllowed(missionCaller, McpSuiteLeaseTools.ToolName), "a mission credential may call the suite lease");

                    Func<long?> previousReader = HostWideCommandLock.MemoryReader;
                    int previousFloor = HostWideCommandLock.MemoryFloorMb;
                    using (CancellationTokenSource cancel = new CancellationTokenSource())
                    {
                        try
                        {
                            HostWideCommandLock.SetMemoryReader(() => null);
                            HostWideCommandLock.SetMemoryFloorMb(0);
                            HostWideCommandLock.SetSlotCount(2);
                            HostWideCommandLock.SetVesselSlotCount(1);

                            await CaptainSuiteLeaseRegistry.AcquireAsync(holder.Id, vessel.Id, cancel.Token).ConfigureAwait(false);

                            AuthContext waiterCaller = McpTestCaller.Operator;
                            waiterCaller.MissionId = waiter.Id;
                            using (McpCallerContext.Begin(waiterCaller))
                            using (McpCallerContext.BeginCall(cancel.Token))
                            {
                                Task<object> acquire = tool!(JsonSerializer.SerializeToElement(new { action = "acquire" }));

                                DateTime deadline = DateTime.UtcNow.AddSeconds(5);
                                while (DateTime.UtcNow < deadline
                                    && !(CaptainSuiteLeaseRegistry.IsWaiting(waiter.Id) && CaptainSuiteLeaseRegistry.ReadWaitPosition(waiter.Id) >= 1))
                                {
                                    await Task.Delay(25).ConfigureAwait(false);
                                }

                                AssertTrue(CaptainSuiteLeaseRegistry.IsWaiting(waiter.Id), "the MCP acquire is waiting");
                                AssertEqual(1, CaptainSuiteLeaseRegistry.ReadWaitPosition(waiter.Id), "the named wait reports position 1");

                                ArmadaEvent? waitEvent = null;
                                while (waitEvent == null && DateTime.UtcNow < deadline)
                                {
                                    EnumerationResult<ArmadaEvent> events = await testDb.Driver.Events.EnumerateAsync(new EnumerationQuery
                                    {
                                        MissionId = waiter.Id,
                                        EventType = CaptainSuiteLeaseRegistry.WaitEventType,
                                        PageNumber = 1,
                                        PageSize = 10
                                    }).ConfigureAwait(false);
                                    if (events.Objects.Count > 0) waitEvent = events.Objects[0];
                                    else await Task.Delay(25).ConfigureAwait(false);
                                }

                                AssertNotNull(waitEvent, "a queued acquire records " + CaptainSuiteLeaseRegistry.WaitEventType);

                                CaptainSuiteLeaseRegistry.Release(holder.Id);
                                object granted = await acquire.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                                string grantedJson = JsonSerializer.Serialize(granted);
                                AssertContains("granted", grantedJson, "the waiter is granted after the holder releases");

                                string released = JsonSerializer.Serialize(await tool!(JsonSerializer.SerializeToElement(new { action = "release" })).ConfigureAwait(false));
                                AssertContains("released", released, "release frees the lease");
                            }

                            AssertFalse(CaptainSuiteLeaseRegistry.IsActive(waiter.Id), "the mission holds no lease after release");
                        }
                        finally
                        {
                            cancel.Cancel();
                            CaptainSuiteLeaseRegistry.Release(holder.Id);
                            CaptainSuiteLeaseRegistry.Release(waiter.Id);
                            RestoreLock(previousReader, previousFloor);
                        }
                    }
                }
            });

            await RunTest("A queued armada_suite_lease acquire answers within its wait bound and a retry keeps its place", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(new Vessel("lease-queue-vessel", "https://example.invalid/lease-queue.git")).ConfigureAwait(false);
                    Mission holder = await CreateMissionAsync(testDb, vessel, "lease queue holder").ConfigureAwait(false);
                    Mission first = await CreateMissionAsync(testDb, vessel, "lease queue first").ConfigureAwait(false);
                    Mission second = await CreateMissionAsync(testDb, vessel, "lease queue second").ConfigureAwait(false);

                    Func<JsonElement?, Task<object>>? tool = null;
                    McpSuiteLeaseTools.Register((name, _, _, handler) =>
                    {
                        if (name == McpSuiteLeaseTools.ToolName) tool = handler;
                    }, testDb.Driver, null);
                    AssertNotNull(tool, "the suite lease tool is registered");

                    Func<long?> previousReader = HostWideCommandLock.MemoryReader;
                    int previousFloor = HostWideCommandLock.MemoryFloorMb;
                    try
                    {
                        HostWideCommandLock.SetMemoryReader(() => null);
                        HostWideCommandLock.SetMemoryFloorMb(0);
                        HostWideCommandLock.SetSlotCount(2);
                        HostWideCommandLock.SetVesselSlotCount(1);
                        await CaptainSuiteLeaseRegistry.AcquireAsync(holder.Id, vessel.Id, CancellationToken.None).ConfigureAwait(false);

                        JsonElement queuedFirst = await CallAsync(tool!, first, new { action = "acquire", waitSeconds = 1 }).ConfigureAwait(false);
                        AssertEqual("queued", queuedFirst.GetProperty("Status").GetString(), "an acquire behind a held vessel slot answers queued");
                        AssertEqual(1, queuedFirst.GetProperty("Position").GetInt32(), "the first queued request is at position 1");

                        // A transport timeout cancels the call; the queued request must survive it.
                        using (CancellationTokenSource timedOut = new CancellationTokenSource())
                        {
                            timedOut.Cancel();
                            JsonElement afterTimeout = await CallAsync(tool!, first, new { action = "acquire", waitSeconds = 1 }, timedOut.Token).ConfigureAwait(false);
                            AssertEqual("queued", afterTimeout.GetProperty("Status").GetString(), "a cancelled call leaves the request queued");
                        }

                        JsonElement queuedSecond = await CallAsync(tool!, second, new { action = "acquire", waitSeconds = 0 }).ConfigureAwait(false);
                        AssertEqual(2, queuedSecond.GetProperty("Position").GetInt32(), "a later mission queues behind the first");

                        JsonElement retryFirst = await CallAsync(tool!, first, new { action = "acquire", waitSeconds = 0 }).ConfigureAwait(false);
                        AssertEqual(1, retryFirst.GetProperty("Position").GetInt32(), "a retry keeps the first request's place");
                        AssertTrue(CaptainSuiteLeaseRegistry.IsActive(first.Id), "a queued request counts as an active lease between calls, so it is not a stall");

                        EnumerationResult<ArmadaEvent> waits = await testDb.Driver.Events.EnumerateAsync(new EnumerationQuery
                        {
                            MissionId = first.Id,
                            EventType = CaptainSuiteLeaseRegistry.WaitEventType,
                            PageNumber = 1,
                            PageSize = 10
                        }).ConfigureAwait(false);
                        AssertEqual(1, waits.Objects.Count, "one wait event per queued request, not one per retry");

                        JsonElement withdrawn = await CallAsync(tool!, second, new { action = "release" }).ConfigureAwait(false);
                        AssertEqual("withdrawn", withdrawn.GetProperty("Status").GetString(), "release while queued withdraws the request");
                        AssertFalse(CaptainSuiteLeaseRegistry.IsActive(second.Id), "a withdrawn request holds and waits for nothing");

                        CaptainSuiteLeaseRegistry.Release(holder.Id);
                        JsonElement granted = await CallAsync(tool!, first, new { action = "acquire", waitSeconds = 5 }).ConfigureAwait(false);
                        string grantedStatus = granted.GetProperty("Status").GetString() ?? String.Empty;
                        AssertTrue(grantedStatus == "granted" || grantedStatus == "held", "the queued request is granted once the holder releases, got " + grantedStatus);
                        AssertFalse(CaptainSuiteLeaseRegistry.IsActive(second.Id), "the withdrawn mission is never granted the slot");

                        JsonElement released = await CallAsync(tool!, first, new { action = "release" }).ConfigureAwait(false);
                        AssertEqual("released", released.GetProperty("Status").GetString(), "release frees the granted lease");
                    }
                    finally
                    {
                        CaptainSuiteLeaseRegistry.ReleaseForMission(holder.Id);
                        CaptainSuiteLeaseRegistry.ReleaseForMission(first.Id);
                        CaptainSuiteLeaseRegistry.ReleaseForMission(second.Id);
                        RestoreLock(previousReader, previousFloor);
                    }
                }
            });

            await RunTest("A captain process exit releases its suite lease and frees the vessel slot for the gate", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    SqliteDatabaseDriver database = testDb.Driver;
                    ArmadaSettings settings = CreateSettings();
                    AdmiralService admiral = CreateAdmiralService(database, settings);
                    Vessel vessel = await database.Vessels.CreateAsync(new Vessel("lease-exit-vessel", "https://example.invalid/lease-exit.git")).ConfigureAwait(false);
                    Mission mission = await database.Missions.CreateAsync(new Mission("lease exit")
                    {
                        VesselId = vessel.Id,
                        Status = MissionStatusEnum.InProgress,
                        AssignmentState = MissionAssignmentStateEnum.Assigned,
                        ProcessId = 8201,
                        StartedUtc = DateTime.UtcNow.AddMinutes(-2)
                    }).ConfigureAwait(false);
                    Captain captain = await database.Captains.CreateAsync(new Captain("lease-exit-captain")
                    {
                        State = CaptainStateEnum.Working,
                        CurrentMissionId = mission.Id,
                        ProcessId = 8201
                    }).ConfigureAwait(false);
                    mission.CaptainId = captain.Id;
                    await database.Missions.UpdateAsync(mission).ConfigureAwait(false);

                    Func<long?> previousReader = HostWideCommandLock.MemoryReader;
                    int previousFloor = HostWideCommandLock.MemoryFloorMb;
                    try
                    {
                        HostWideCommandLock.SetMemoryReader(() => null);
                        HostWideCommandLock.SetMemoryFloorMb(0);
                        HostWideCommandLock.SetSlotCount(2);
                        HostWideCommandLock.SetVesselSlotCount(1);
                        await CaptainSuiteLeaseRegistry.AcquireAsync(mission.Id, vessel.Id, CancellationToken.None).ConfigureAwait(false);

                        await admiral.HandleProcessExitAsync(8201, 1, captain.Id, mission.Id).ConfigureAwait(false);

                        AssertFalse(CaptainSuiteLeaseRegistry.IsActive(mission.Id), "the exited captain holds no suite lease");
                        Task<IDisposable> gate = HostWideCommandLock.AcquireAsync(mission.Id, "definition-of-done gate probe", vessel.Id, CancellationToken.None);
                        AssertTrue(await Task.WhenAny(gate, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false) == gate,
                            "the gate takes the vessel slot the exited captain held");
                        (await gate.ConfigureAwait(false)).Dispose();
                    }
                    finally
                    {
                        CaptainSuiteLeaseRegistry.Release(mission.Id);
                        RestoreLock(previousReader, previousFloor);
                    }
                }
            });

            await RunTest("Ending a mission cancels its waiting suite acquire, so no late grant holds the slot", async () =>
            {
                string vessel = "vsl_" + "lease_end_" + Guid.NewGuid().ToString("N");
                string holder = "msn_" + "lease_end_hold_" + Guid.NewGuid().ToString("N");
                string ended = "msn_" + "lease_end_wait_" + Guid.NewGuid().ToString("N");
                Func<long?> previousReader = HostWideCommandLock.MemoryReader;
                int previousFloor = HostWideCommandLock.MemoryFloorMb;
                try
                {
                    HostWideCommandLock.SetMemoryReader(() => null);
                    HostWideCommandLock.SetMemoryFloorMb(0);
                    HostWideCommandLock.SetSlotCount(2);
                    HostWideCommandLock.SetVesselSlotCount(1);
                    await CaptainSuiteLeaseRegistry.AcquireAsync(holder, vessel, CancellationToken.None).ConfigureAwait(false);
                    Task<IDisposable> waiting = CaptainSuiteLeaseRegistry.AcquireAsync(ended, vessel, CancellationToken.None);
                    await Task.Delay(50).ConfigureAwait(false);
                    AssertTrue(CaptainSuiteLeaseRegistry.IsWaiting(ended), "the second mission waits");

                    CaptainSuiteLeaseRegistry.ReleaseForMission(ended);
                    bool cancelled = false;
                    try { await waiting.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
                    catch (OperationCanceledException) { cancelled = true; }
                    AssertTrue(cancelled, "the waiting acquire ends cancelled");

                    CaptainSuiteLeaseRegistry.Release(holder);
                    AssertFalse(CaptainSuiteLeaseRegistry.IsActive(ended), "the ended mission is never granted the slot");
                    Task<IDisposable> next = HostWideCommandLock.AcquireAsync("lease-end-probe", "probe", vessel, CancellationToken.None);
                    AssertTrue(await Task.WhenAny(next, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false) == next,
                        "the vessel slot is free once the holder releases");
                    (await next.ConfigureAwait(false)).Dispose();
                }
                finally
                {
                    CaptainSuiteLeaseRegistry.Release(holder);
                    CaptainSuiteLeaseRegistry.Release(ended);
                    RestoreLock(previousReader, previousFloor);
                }
            });
        }

        private static Task<Mission> CreateMissionAsync(TestDatabase testDb, Vessel vessel, string title)
        {
            return testDb.Driver.Missions.CreateAsync(new Mission(title)
            {
                TenantId = Constants.DefaultTenantId,
                UserId = Constants.DefaultUserId,
                VesselId = vessel.Id,
                Status = MissionStatusEnum.InProgress
            });
        }

        private static async Task<JsonElement> CallAsync(Func<JsonElement?, Task<object>> tool, Mission mission, object args, CancellationToken callToken = default)
        {
            AuthContext caller = McpTestCaller.Operator;
            caller.MissionId = mission.Id;
            using (McpCallerContext.Begin(caller))
            using (McpCallerContext.BeginCall(callToken))
            {
                object result = await tool(JsonSerializer.SerializeToElement(args)).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                return JsonSerializer.SerializeToElement(result);
            }
        }

        private static ArmadaSettings CreateSettings()
        {
            string id = Guid.NewGuid().ToString("N");
            ArmadaSettings settings = new ArmadaSettings();
            settings.DocksDirectory = Path.Combine(Path.GetTempPath(), "armada_suite_lease_docks_" + id);
            settings.ReposDirectory = Path.Combine(Path.GetTempPath(), "armada_suite_lease_repos_" + id);
            settings.LogDirectory = Path.Combine(Path.GetTempPath(), "armada_suite_lease_logs_" + id);
            settings.MinIdleCaptains = 0;
            return settings;
        }

        private static AdmiralService CreateAdmiralService(SqliteDatabaseDriver database, ArmadaSettings settings)
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            StubGitService git = new StubGitService();
            IDockService docks = new DockService(logging, database, settings, git);
            CaptainService captains = new CaptainService(logging, database, settings, git, docks);
            MissionService missions = new MissionService(logging, database, settings, docks, captains,
                resourcePressureAdmission: TestResourcePressure.Unconstrained(settings));
            return new AdmiralService(logging, database, settings, captains, missions, new VoyageService(logging, database), docks);
        }

        private static void RestoreLock(Func<long?> previousReader, int previousFloor)
        {
            HostWideCommandLock.SetMemoryFloorMb(previousFloor);
            HostWideCommandLock.SetMemoryReader(previousReader);
            HostWideCommandLock.SetSlotCount(1);
            HostWideCommandLock.SetVesselSlotCount(1);
        }
    }
}
