namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Server.Mcp;
    using Armada.Server.Mcp.Tools;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

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
