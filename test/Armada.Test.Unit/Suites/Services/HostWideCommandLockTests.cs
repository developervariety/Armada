namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics.Metrics;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// Order, cancellation, capacity and reporting of the host-wide build and test slots.
    /// </summary>
    public class HostWideCommandLockTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Host-Wide Command Lock";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("Requests are granted in the order they were made", async () =>
            {
                IDisposable first = await HostWideCommandLock.AcquireAsync("lock_first", "first holder", CancellationToken.None).ConfigureAwait(false);
                Task<IDisposable> second = HostWideCommandLock.AcquireAsync("lock_second", "second request", CancellationToken.None);
                Task<IDisposable> third = HostWideCommandLock.AcquireAsync("lock_third", "third request", CancellationToken.None);

                HostSlotSnapshot waiting = HostWideCommandLock.Snapshot();
                AssertEqual("lock_first", waiting.HolderKey, "the first request holds the slot");
                AssertEqual("first holder", waiting.HolderDescription);
                AssertEqual(1, waiting.FindWaiter("lock_second")!.Position, "the second request is next in line");
                AssertEqual(2, waiting.FindWaiter("lock_third")!.Position, "the third request waits behind it");

                first.Dispose();
                IDisposable secondLease = await second.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                AssertFalse(third.IsCompleted, "the third request waits while the second holds the slot");
                AssertEqual("lock_second", HostWideCommandLock.Snapshot().HolderKey);

                secondLease.Dispose();
                IDisposable thirdLease = await third.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                thirdLease.Dispose();
                HostSlotSnapshot free = HostWideCommandLock.Snapshot();
                AssertNull(free.HolderDescription, "the slot is free after the last release");
                AssertEqual(0, free.Waiters.Count);
            });

            await RunTest("A cancelled request leaves the queue and the next request is granted", async () =>
            {
                IDisposable holder = await HostWideCommandLock.AcquireAsync("cancel_holder", "holder", CancellationToken.None).ConfigureAwait(false);
                using (CancellationTokenSource cancel = new CancellationTokenSource())
                {
                    Task<IDisposable> cancelled = HostWideCommandLock.AcquireAsync("cancel_waiter", "cancelled request", cancel.Token);
                    Task<IDisposable> next = HostWideCommandLock.AcquireAsync("cancel_next", "next request", CancellationToken.None);

                    cancel.Cancel();
                    bool threw = false;
                    try
                    {
                        await cancelled.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        threw = true;
                    }

                    AssertTrue(threw, "a cancelled request ends with a cancellation");
                    HostSlotSnapshot afterCancel = HostWideCommandLock.Snapshot();
                    AssertNull(afterCancel.FindWaiter("cancel_waiter"), "a cancelled request leaves the queue");
                    AssertEqual(1, afterCancel.FindWaiter("cancel_next")!.Position, "the next request moves up");

                    holder.Dispose();
                    IDisposable nextLease = await next.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                    AssertEqual("cancel_next", HostWideCommandLock.Snapshot().HolderKey);
                    nextLease.Dispose();
                }
            });

            await RunTest("A granted and a cancelled slot wait are each recorded with their outcome", async () =>
            {
                using (ArmadaMetricsRecorder metrics = new ArmadaMetricsRecorder())
                {
                    IDisposable holder = await HostWideCommandLock.AcquireAsync("metric_holder", "holder", CancellationToken.None).ConfigureAwait(false);
                    Task<IDisposable> waiter = HostWideCommandLock.AcquireAsync("metric_waiter", "waiter", CancellationToken.None);
                    using (CancellationTokenSource cancel = new CancellationTokenSource())
                    {
                        Task<IDisposable> cancelled = HostWideCommandLock.AcquireAsync("metric_cancelled", "cancelled request", cancel.Token);
                        cancel.Cancel();
                        bool threw = false;
                        try
                        {
                            await cancelled.ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            threw = true;
                        }

                        AssertTrue(threw, "the cancelled request still ends with its cancellation");
                    }

                    await Task.Delay(50).ConfigureAwait(false);
                    holder.Dispose();
                    IDisposable waiterLease = await waiter.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                    waiterLease.Dispose();

                    List<RecordedMeasurement> waits = metrics.Of("armada.hostslot.wait.seconds");
                    AssertTrue(waits.Count(m => m.Has(ArmadaMetrics.TagOutcome, ArmadaMetrics.OutcomeGranted)) >= 2,
                        "the immediate grant and the queued grant are both recorded");
                    AssertTrue(waits.Any(m => m.Has(ArmadaMetrics.TagOutcome, ArmadaMetrics.OutcomeCanceled)),
                        "the cancelled wait is recorded as canceled");
                    AssertTrue(waits.Any(m => m.Has(ArmadaMetrics.TagOutcome, ArmadaMetrics.OutcomeGranted) && m.Value >= 0.04),
                        "the queued grant records the time it waited behind the holder");
                    AssertTrue(waits.All(m => m.Tags.Count == 1), "a slot wait carries only its outcome label, never its owner");
                }
            });

            await RunTest("A telemetry listener that throws does not change a slot grant", async () =>
            {
                long failuresBefore = ArmadaMetrics.RecordingFailures;
                using (MeterListener throwing = new MeterListener())
                {
                    throwing.InstrumentPublished = (instrument, listener) =>
                    {
                        if (instrument.Meter.Name == ArmadaMetrics.MeterName) listener.EnableMeasurementEvents(instrument);
                    };
                    throwing.SetMeasurementEventCallback<double>((instrument, value, tags, state) => throw new InvalidOperationException("listener refused the measurement"));
                    throwing.Start();

                    IDisposable lease = await HostWideCommandLock.AcquireAsync("throwing_listener", "holder", CancellationToken.None).ConfigureAwait(false);
                    AssertEqual("throwing_listener", HostWideCommandLock.Snapshot().HolderKey, "the slot is granted although the listener threw");
                    lease.Dispose();
                    AssertNull(HostWideCommandLock.Snapshot().HolderKey, "the slot is released normally");
                }

                AssertTrue(ArmadaMetrics.RecordingFailures > failuresBefore, "the refused measurement is counted, not lost silently");
            });

            await RunTest("A lease released twice frees the slot once", async () =>
            {
                IDisposable lease = await HostWideCommandLock.AcquireAsync("twice_holder", "holder", CancellationToken.None).ConfigureAwait(false);
                Task<IDisposable> waiter = HostWideCommandLock.AcquireAsync("twice_waiter", "waiter", CancellationToken.None);
                lease.Dispose();
                IDisposable waiterLease = await waiter.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                lease.Dispose();
                AssertEqual("twice_waiter", HostWideCommandLock.Snapshot().HolderKey, "a second release of an old lease does not free the new holder's slot");
                waiterLease.Dispose();
            });

            await RunTest("A pending check's slot wait names its place, its holder and the guidance", () =>
            {
                CheckRun run = new CheckRun { Status = Armada.Core.Enums.CheckRunStatusEnum.Pending, SlotRequestedUtc = DateTime.UtcNow };
                HostSlotSnapshot slot = new HostSlotSnapshot { HolderKey = "chk_other", HolderDescription = "definition-of-done build", HolderSinceUtc = DateTime.UtcNow };
                slot.Waiters.Add(new HostSlotWaiter { Position = 2, Key = run.Id, Description = "check" });

                CheckRunSlotWait? wait = CheckRunSlotWait.Describe(run, slot);
                AssertNotNull(wait);
                AssertEqual("WaitingForSlot", wait!.State);
                AssertEqual(2, wait.Position);
                AssertEqual("definition-of-done build", wait.HolderDescription);
                AssertContains("not a question for the owner", wait.Guidance);

                run.SlotRequestedUtc = null;
                run.Source = Armada.Core.Enums.CheckRunSourceEnum.Armada;
                try
                {
                    AutomaticCheckRunPlaces.Publish(4, 4);
                    CheckRunSlotWait queued = CheckRunSlotWait.Describe(run, new HostSlotSnapshot())!;
                    AssertEqual("Queued", queued.State, "a check that has not asked for the slot is queued to start");
                    AssertEqual("waiting for a place in the automatic check runner: 4 of 4 places hold started checks, and this check starts when one of them finishes",
                        queued.QueueReason, "a queued check names the full runner it waits behind");

                    AutomaticCheckRunPlaces.Publish(3, 4);
                    AssertNull(CheckRunSlotWait.Describe(run, new HostSlotSnapshot())!.QueueReason, "a queued check with a free runner place names no queue reason");
                }
                finally
                {
                    AutomaticCheckRunPlaces.Publish(0, 0);
                }

                run.Status = Armada.Core.Enums.CheckRunStatusEnum.Running;
                AssertEqual("Running", CheckRunSlotWait.Describe(run, slot)!.State);
                run.Status = Armada.Core.Enums.CheckRunStatusEnum.Failed;
                AssertNull(CheckRunSlotWait.Describe(run, slot), "a finished check has no wait");
                return Task.CompletedTask;
            });

            await RunTest("A mission's gate slot wait names its state, its place and its holder", () =>
            {
                // A gate that holds a slot is running a suite.
                HostSlotSnapshot holding = new HostSlotSnapshot();
                holding.Holders.Add(new HostSlotHolder { Key = "msn_gate", Description = "a gate", SinceUtc = DateTime.UtcNow });
                AssertEqual("Running", GateSlotWait.Describe("msn_gate", holding).State, "a gate that holds a slot is running");

                // A gate waiting in line is not lost: it names its place and the holder it waits behind.
                HostSlotSnapshot waiting = new HostSlotSnapshot { HolderKey = "msn_other", HolderDescription = "another gate", HolderSinceUtc = DateTime.UtcNow };
                waiting.Holders.Add(new HostSlotHolder { Key = "msn_other", Description = "another gate", SinceUtc = waiting.HolderSinceUtc });
                DateTime askedAt = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                waiting.Waiters.Add(new HostSlotWaiter { Position = 2, Key = "msn_gate", Description = "a gate", RequestedUtc = askedAt });

                GateSlotWait inLine = GateSlotWait.Describe("msn_gate", waiting);
                AssertEqual("WaitingForSlot", inLine.State, "a gate waiting in line is not lost");
                AssertEqual(2, inLine.Position, "the gate names its place in line");
                AssertEqual(askedAt, inLine.WaitingSinceUtc, "the wait names when the gate asked");
                AssertEqual("another gate", inLine.HolderDescription, "the wait names what holds the slot");
                AssertContains("not lost", inLine.Guidance);

                // A registered gate that neither holds nor waits for a slot is queued before its first command.
                AssertEqual("Queued", GateSlotWait.Describe("msn_gate", new HostSlotSnapshot()).State, "a gate that has not asked for a slot is queued, not lost");

                // Memory holding the queue surfaces on the gate first in line.
                HostSlotSnapshot memoryHeld = new HostSlotSnapshot { HeldForMemory = true, MemoryWaitReason = "waiting for free memory" };
                memoryHeld.Waiters.Add(new HostSlotWaiter { Position = 1, Key = "msn_gate", Description = "a gate", RequestedUtc = askedAt });
                AssertEqual("waiting for free memory", GateSlotWait.Describe("msn_gate", memoryHeld).MemoryWaitReason, "a gate first in line names the memory hold");

                return Task.CompletedTask;
            });

            await RunTest("With one slot a second request waits and the snapshot names one holder", async () =>
            {
                List<IDisposable> leases = new List<IDisposable>();
                List<Task<IDisposable>> requests = new List<Task<IDisposable>>();
                using (CancellationTokenSource cancel = new CancellationTokenSource())
                {
                    try
                    {
                        AssertEqual(1, new DefinitionOfDoneSettings().HostCommandSlots, "the setting defaults to one slot");
                        AssertEqual(1, HostWideCommandLock.SlotCount, "the lock defaults to one slot");

                        leases.Add(await HostWideCommandLock.AcquireAsync("one_first", "first holder", cancel.Token).ConfigureAwait(false));
                        Task<IDisposable> second = HostWideCommandLock.AcquireAsync("one_second", "second request", cancel.Token);
                        requests.Add(second);

                        AssertFalse(second.IsCompleted, "with one slot a second request waits for the first");
                        HostSlotSnapshot slot = HostWideCommandLock.Snapshot();
                        AssertEqual(1, slot.SlotCount);
                        AssertEqual(1, slot.Holders.Count, "one request holds the only slot");
                        AssertEqual("one_first", slot.Holders[0].Key);
                        AssertEqual("one_first", slot.HolderKey, "the single-holder fields name the holder");
                        AssertEqual("first holder", slot.HolderDescription);
                        AssertEqual(1, slot.FindWaiter("one_second")!.Position);
                    }
                    finally
                    {
                        await ReleaseAllAsync(cancel, leases, requests).ConfigureAwait(false);
                    }
                }
            });

            await RunTest("With two vessel suite slots two commands of one vessel hold at once and a third waits", async () =>
            {
                string vessel = "vsl_lane_two_slots_" + Guid.NewGuid().ToString("N");
                Func<long?> previousReader = HostWideCommandLock.MemoryReader;
                int previousFloor = HostWideCommandLock.MemoryFloorMb;
                List<IDisposable> leases = new List<IDisposable>();
                List<Task<IDisposable>> requests = new List<Task<IDisposable>>();
                using (CancellationTokenSource cancel = new CancellationTokenSource())
                {
                    try
                    {
                        HostWideCommandLock.SetMemoryReader(() => null);
                        HostWideCommandLock.SetMemoryFloorMb(0);
                        HostWideCommandLock.SetSlotCount(3);
                        HostWideCommandLock.SetVesselSlotCount(2);

                        Task<IDisposable> first = HostWideCommandLock.AcquireAsync("vss_first", "vessel first", vessel, cancel.Token);
                        Task<IDisposable> second = HostWideCommandLock.AcquireAsync("vss_second", "vessel second", vessel, cancel.Token);
                        Task<IDisposable> third = HostWideCommandLock.AcquireAsync("vss_third", "vessel third", vessel, cancel.Token);
                        requests.Add(first);
                        requests.Add(second);
                        requests.Add(third);

                        AssertTrue(first.IsCompleted, "the first of the vessel takes a vessel slot at once");
                        AssertTrue(second.IsCompleted, "the second of the vessel takes the other vessel slot at once");
                        AssertFalse(third.IsCompleted, "the third of the vessel waits on the per-vessel limit");
                        HostSlotSnapshot waiting = HostWideCommandLock.Snapshot();
                        AssertEqual(2, waiting.VesselSlotCount);
                        AssertEqual(2, waiting.Holders.Count, "two of the vessel hold host slots");
                        AssertEqual(1, waiting.FindVesselWaiter("vss_third")!.Position, "the third names its vessel wait position");
                        AssertNull(waiting.FindWaiter("vss_third"), "the third does not occupy a host slot while it waits on the vessel");

                        (await first.ConfigureAwait(false)).Dispose();
                        IDisposable thirdLease = await third.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                        leases.Add(thirdLease);
                        AssertTrue(Holds("vss_third"), "the freed vessel slot goes to the waiting command");
                        AssertNull(HostWideCommandLock.Snapshot().FindVesselWaiter("vss_third"));
                    }
                    finally
                    {
                        await ReleaseAllAsync(cancel, leases, requests).ConfigureAwait(false);
                        RestoreMemory(previousReader, previousFloor);
                    }
                }
            });

            await RunTest("With two slots two requests hold the slot at once and a third waits", async () =>
            {
                List<IDisposable> leases = new List<IDisposable>();
                List<Task<IDisposable>> requests = new List<Task<IDisposable>>();
                using (CancellationTokenSource cancel = new CancellationTokenSource())
                {
                    try
                    {
                        HostWideCommandLock.SetSlotCount(2);
                        Task<IDisposable> first = HostWideCommandLock.AcquireAsync("two_first", "first holder", cancel.Token);
                        Task<IDisposable> second = HostWideCommandLock.AcquireAsync("two_second", "second holder", cancel.Token);
                        Task<IDisposable> third = HostWideCommandLock.AcquireAsync("two_third", "third request", cancel.Token);
                        requests.Add(first);
                        requests.Add(second);
                        requests.Add(third);

                        AssertTrue(first.IsCompleted, "the first request takes a slot at once");
                        AssertTrue(second.IsCompleted, "the second request takes the other slot at once");
                        AssertFalse(third.IsCompleted, "the third request waits while both slots are held");

                        HostSlotSnapshot slot = HostWideCommandLock.Snapshot();
                        AssertEqual(2, slot.SlotCount);
                        AssertEqual(2, slot.Holders.Count, "the snapshot reports every holder");
                        AssertEqual("two_first", slot.Holders[0].Key, "holders are listed in grant order");
                        AssertEqual("two_second", slot.Holders[1].Key);
                        AssertEqual("second holder", slot.Holders[1].Description);
                        AssertNotNull(slot.Holders[1].SinceUtc);
                        AssertNotNull(slot.FindHolder("two_second"), "a holder is found by its key");
                        AssertNull(slot.FindHolder("two_third"), "a waiting request is not a holder");
                        AssertEqual("two_first", slot.HolderKey, "the single-holder fields name the earliest holder");
                        AssertEqual(1, slot.FindWaiter("two_third")!.Position);

                        IDisposable firstLease = await first.ConfigureAwait(false);
                        firstLease.Dispose();
                        AssertTrue(Holds("two_third"), "the freed slot goes to the waiting request");
                        HostSlotSnapshot after = HostWideCommandLock.Snapshot();
                        AssertEqual(2, after.Holders.Count);
                        AssertNotNull(after.FindHolder("two_third"));
                        AssertEqual(0, after.Waiters.Count);
                    }
                    finally
                    {
                        await ReleaseAllAsync(cancel, leases, requests).ConfigureAwait(false);
                    }
                }
            });

            await RunTest("A command waiting on its vessel suite lock holds no host slot another vessel can use", async () =>
            {
                // Two of one vessel's suite commands cannot run at once -- outside Armada a file lock the vessel's
                // own command takes serializes them. A command that would only wait on that lock must not occupy a
                // host slot meanwhile, or with slots to spare it starves another vessel whose command could run.
                string vesselOne = "vsl_lane_one_" + Guid.NewGuid().ToString("N");
                string vesselTwo = "vsl_lane_two_" + Guid.NewGuid().ToString("N");
                Func<long?> previousReader = HostWideCommandLock.MemoryReader;
                int previousFloor = HostWideCommandLock.MemoryFloorMb;
                List<IDisposable> leases = new List<IDisposable>();
                List<Task<IDisposable>> requests = new List<Task<IDisposable>>();
                using (CancellationTokenSource cancel = new CancellationTokenSource())
                {
                    try
                    {
                        HostWideCommandLock.SetMemoryReader(() => null);
                        HostWideCommandLock.SetMemoryFloorMb(0);
                        HostWideCommandLock.SetSlotCount(2);

                        IDisposable firstOfVesselOne = await HostWideCommandLock.AcquireAsync("vlk_one_first", "vessel one gate", vesselOne, cancel.Token).ConfigureAwait(false);
                        leases.Add(firstOfVesselOne);
                        AssertTrue(Holds("vlk_one_first"), "the first command of a vessel takes a host slot");

                        Task<IDisposable> secondOfVesselOne = HostWideCommandLock.AcquireAsync("vlk_one_second", "vessel one check", vesselOne, cancel.Token);
                        requests.Add(secondOfVesselOne);
                        await Task.Delay(50).ConfigureAwait(false);

                        AssertFalse(secondOfVesselOne.IsCompleted, "a second command for the same vessel waits on the vessel suite lock");
                        HostSlotSnapshot blocked = HostWideCommandLock.Snapshot();
                        AssertEqual(1, blocked.Holders.Count, "the command blocked on its vessel suite lock holds no host slot");
                        AssertNull(blocked.FindWaiter("vlk_one_second"), "it does not even queue for a slot while its vessel lock is held");
                        HostVesselWaiter? vesselWaiter = blocked.FindVesselWaiter("vlk_one_second");
                        AssertNotNull(vesselWaiter, "the blocked command is reported as a vessel-suite waiter");
                        AssertEqual(1, vesselWaiter!.Position, "the vessel wait names its place in that vessel's line");
                        AssertEqual(vesselOne, vesselWaiter.VesselKey, "the vessel wait names the vessel");
                        AssertEqual(1, blocked.VesselSlotCount, "the snapshot reports the per-vessel limit");

                        IDisposable otherVessel = await HostWideCommandLock.AcquireAsync("vlk_two_first", "vessel two gate", vesselTwo, cancel.Token).ConfigureAwait(false);
                        leases.Add(otherVessel);
                        AssertTrue(Holds("vlk_two_first"), "another vessel's command takes the free host slot the blocked command did not occupy");

                        firstOfVesselOne.Dispose();
                        IDisposable secondLease = await secondOfVesselOne.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                        leases.Add(secondLease);
                        AssertTrue(Holds("vlk_one_second"), "the second command runs once the first releases the vessel suite lock");
                    }
                    finally
                    {
                        await ReleaseAllAsync(cancel, leases, requests).ConfigureAwait(false);
                        RestoreMemory(previousReader, previousFloor);
                    }
                }
            });

            await RunTest("With two slots waiting requests are granted in request order", async () =>
            {
                List<IDisposable> leases = new List<IDisposable>();
                List<Task<IDisposable>> requests = new List<Task<IDisposable>>();
                using (CancellationTokenSource cancel = new CancellationTokenSource())
                {
                    try
                    {
                        HostWideCommandLock.SetSlotCount(2);
                        Task<IDisposable> a = HostWideCommandLock.AcquireAsync("fifo_a", "holder a", cancel.Token);
                        Task<IDisposable> b = HostWideCommandLock.AcquireAsync("fifo_b", "holder b", cancel.Token);
                        Task<IDisposable> c = HostWideCommandLock.AcquireAsync("fifo_c", "request c", cancel.Token);
                        Task<IDisposable> d = HostWideCommandLock.AcquireAsync("fifo_d", "request d", cancel.Token);
                        Task<IDisposable> e = HostWideCommandLock.AcquireAsync("fifo_e", "request e", cancel.Token);
                        requests.Add(a);
                        requests.Add(b);
                        requests.Add(c);
                        requests.Add(d);
                        requests.Add(e);

                        AssertTrue(a.IsCompleted && b.IsCompleted, "the first two requests take both slots");
                        HostSlotSnapshot waiting = HostWideCommandLock.Snapshot();
                        AssertEqual(1, waiting.FindWaiter("fifo_c")!.Position);
                        AssertEqual(2, waiting.FindWaiter("fifo_d")!.Position);
                        AssertEqual(3, waiting.FindWaiter("fifo_e")!.Position);

                        (await b.ConfigureAwait(false)).Dispose();
                        AssertTrue(Holds("fifo_c"), "the first waiter takes the first freed slot");
                        AssertTrue(Waits("fifo_d"), "a later waiter is not passed ahead of an earlier one");
                        AssertTrue(Waits("fifo_e"));

                        (await a.ConfigureAwait(false)).Dispose();
                        AssertTrue(Holds("fifo_d"), "the second waiter takes the next freed slot");
                        AssertTrue(Waits("fifo_e"), "the last waiter still waits while both slots are held");
                        AssertEqual(1, HostWideCommandLock.Snapshot().FindWaiter("fifo_e")!.Position, "the last waiter moves to the front");
                    }
                    finally
                    {
                        await ReleaseAllAsync(cancel, leases, requests).ConfigureAwait(false);
                    }
                }
            });

            await RunTest("Raising the slot count grants waiting requests in request order", async () =>
            {
                List<IDisposable> leases = new List<IDisposable>();
                List<Task<IDisposable>> requests = new List<Task<IDisposable>>();
                using (CancellationTokenSource cancel = new CancellationTokenSource())
                {
                    try
                    {
                        leases.Add(await HostWideCommandLock.AcquireAsync("raise_holder", "holder", cancel.Token).ConfigureAwait(false));
                        Task<IDisposable> first = HostWideCommandLock.AcquireAsync("raise_first", "first waiter", cancel.Token);
                        Task<IDisposable> second = HostWideCommandLock.AcquireAsync("raise_second", "second waiter", cancel.Token);
                        Task<IDisposable> third = HostWideCommandLock.AcquireAsync("raise_third", "third waiter", cancel.Token);
                        requests.Add(first);
                        requests.Add(second);
                        requests.Add(third);

                        HostWideCommandLock.SetSlotCount(3);

                        AssertTrue(Holds("raise_first"), "a raised count grants the first waiter without waiting for a release");
                        AssertTrue(Holds("raise_second"), "a raised count grants the next waiter too");
                        AssertTrue(Waits("raise_third"), "a waiter beyond the raised count keeps waiting");
                        HostSlotSnapshot slot = HostWideCommandLock.Snapshot();
                        AssertEqual(3, slot.Holders.Count);
                        AssertEqual("raise_first", slot.Holders[1].Key, "the raised slots go to waiters in request order");
                        AssertEqual("raise_second", slot.Holders[2].Key);
                    }
                    finally
                    {
                        await ReleaseAllAsync(cancel, leases, requests).ConfigureAwait(false);
                    }
                }
            });

            await RunTest("Lowering the slot count keeps current holders and grants nothing until they drop below it", async () =>
            {
                List<IDisposable> leases = new List<IDisposable>();
                List<Task<IDisposable>> requests = new List<Task<IDisposable>>();
                using (CancellationTokenSource cancel = new CancellationTokenSource())
                {
                    try
                    {
                        HostWideCommandLock.SetSlotCount(3);
                        Task<IDisposable> a = HostWideCommandLock.AcquireAsync("lower_a", "holder a", cancel.Token);
                        Task<IDisposable> b = HostWideCommandLock.AcquireAsync("lower_b", "holder b", cancel.Token);
                        Task<IDisposable> c = HostWideCommandLock.AcquireAsync("lower_c", "holder c", cancel.Token);
                        requests.Add(a);
                        requests.Add(b);
                        requests.Add(c);
                        AssertTrue(a.IsCompleted && b.IsCompleted && c.IsCompleted, "three slots grant three requests at once");

                        HostWideCommandLock.SetSlotCount(1);
                        AssertEqual(3, HostWideCommandLock.Snapshot().Holders.Count, "lowering the count does not take a slot from a holder");
                        Task<IDisposable> waiter = HostWideCommandLock.AcquireAsync("lower_waiter", "waiter", cancel.Token);
                        requests.Add(waiter);
                        AssertTrue(Waits("lower_waiter"), "a new request waits while holders fill the lowered count");

                        (await a.ConfigureAwait(false)).Dispose();
                        AssertTrue(Waits("lower_waiter"), "two holders still exceed one slot");
                        (await b.ConfigureAwait(false)).Dispose();
                        AssertTrue(Waits("lower_waiter"), "one holder fills the one slot");
                        (await c.ConfigureAwait(false)).Dispose();
                        AssertTrue(Holds("lower_waiter"), "the waiter is granted once no holder is left");
                        AssertEqual(1, HostWideCommandLock.Snapshot().Holders.Count);
                    }
                    finally
                    {
                        await ReleaseAllAsync(cancel, leases, requests).ConfigureAwait(false);
                    }
                }
            });

            await RunTest("The host command slot count is clamped to one through eight", () =>
            {
                try
                {
                    HostWideCommandLock.SetSlotCount(0);
                    AssertEqual(1, HostWideCommandLock.SlotCount, "zero slots is raised to one");
                    HostWideCommandLock.SetSlotCount(-4);
                    AssertEqual(1, HostWideCommandLock.SlotCount, "a negative count is raised to one");
                    HostWideCommandLock.SetSlotCount(9);
                    AssertEqual(8, HostWideCommandLock.SlotCount, "a count above eight is lowered to eight");
                    HostWideCommandLock.SetSlotCount(4);
                    AssertEqual(4, HostWideCommandLock.SlotCount, "a count in range is kept");
                }
                finally
                {
                    HostWideCommandLock.SetSlotCount(1);
                }

                DefinitionOfDoneSettings settings = new DefinitionOfDoneSettings();
                settings.HostCommandSlots = 0;
                AssertEqual(1, settings.HostCommandSlots, "the setting raises zero to one");
                settings.HostCommandSlots = 20;
                AssertEqual(8, settings.HostCommandSlots, "the setting lowers twenty to eight");
                settings.HostCommandSlots = 3;
                AssertEqual(3, settings.HostCommandSlots, "the setting keeps a count in range");
                AssertEqual(1, settings.VesselSuiteSlots, "the per-vessel suite setting defaults to one");
                settings.VesselSuiteSlots = 0;
                AssertEqual(1, settings.VesselSuiteSlots, "the per-vessel setting raises zero to one");
                settings.VesselSuiteSlots = 20;
                AssertEqual(8, settings.VesselSuiteSlots, "the per-vessel setting lowers twenty to eight");
                settings.VesselSuiteSlots = 2;
                AssertEqual(2, settings.VesselSuiteSlots, "the per-vessel setting keeps a count in range");

                DefinitionOfDoneSettings copy = new DefinitionOfDoneSettings();
                copy.CopyFrom(settings);
                AssertEqual(3, copy.HostCommandSlots, "an in-place settings copy carries the slot count");
                AssertEqual(2, copy.VesselSuiteSlots, "an in-place settings copy carries the per-vessel suite count");
                return Task.CompletedTask;
            });

            await RunTest("A pending check's slot wait names every holder when several hold the slot", () =>
            {
                CheckRun run = new CheckRun { Status = Armada.Core.Enums.CheckRunStatusEnum.Pending, SlotRequestedUtc = DateTime.UtcNow };
                DateTime earliest = DateTime.UtcNow.AddSeconds(-30);
                HostSlotSnapshot slot = new HostSlotSnapshot { SlotCount = 2, HolderKey = "chk_a", HolderDescription = "definition-of-done build", HolderSinceUtc = earliest };
                slot.Holders.Add(new HostSlotHolder { Key = "chk_a", Description = "definition-of-done build", SinceUtc = earliest });
                slot.Holders.Add(new HostSlotHolder { Key = "chk_b", Description = "merge-queue test", SinceUtc = DateTime.UtcNow });
                slot.Waiters.Add(new HostSlotWaiter { Position = 1, Key = run.Id, Description = "check" });

                CheckRunSlotWait? wait = CheckRunSlotWait.Describe(run, slot);
                AssertNotNull(wait);
                AssertEqual("WaitingForSlot", wait!.State);
                AssertEqual("definition-of-done build; merge-queue test", wait.HolderDescription, "the wait names every holder in grant order");
                AssertEqual(earliest, wait.HolderSinceUtc, "the wait dates the earliest holder");
                return Task.CompletedTask;
            });

            await RunTest("A settings reload applies the host command slot count to the lock", async () =>
            {
                string directory = Path.Combine(Path.GetTempPath(), "armada-host-slots-reload-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                Func<long?> previousReader = HostWideCommandLock.MemoryReader;
                int previousFloor = HostWideCommandLock.MemoryFloorMb;
                List<IDisposable> leases = new List<IDisposable>();
                List<Task<IDisposable>> requests = new List<Task<IDisposable>>();
                using (CancellationTokenSource cancel = new CancellationTokenSource())
                {
                    try
                    {
                        string path = Path.Combine(directory, "settings.json");
                        ArmadaSettings live = new ArmadaSettings();
                        live.SettingsFilePath = path;
                        await live.SaveAsync().ConfigureAwait(false);
                        SettingsReloadService reload = new SettingsReloadService(live, _ => Task.FromResult(new List<Captain>()));
                        AssertEqual(1, HostWideCommandLock.SlotCount);
                        HostWideCommandLock.SetMemoryReader(() => null);

                        ArmadaSettings edited = await ArmadaSettings.LoadAsync(path).ConfigureAwait(false);
                        edited.DefinitionOfDone.HostCommandSlots = 2;
                        edited.DefinitionOfDone.HostCommandMemoryFloorMb = 2048;
                        await edited.SaveAsync(path).ConfigureAwait(false);
                        SettingsReloadResult result = await reload.ReloadAsync().ConfigureAwait(false);
                        AssertTrue(result.Applied, "the edited file is a valid candidate: " + result.Reason);

                        AssertEqual(2, live.DefinitionOfDone.HostCommandSlots, "the reloaded setting is live");
                        AssertEqual(2, HostWideCommandLock.SlotCount, "the reloaded slot count reaches the lock without a restart");
                        AssertEqual(2048, HostWideCommandLock.MemoryFloorMb, "the reloaded memory floor reaches the lock without a restart");
                        Task<IDisposable> first = HostWideCommandLock.AcquireAsync("reload_first", "first holder", cancel.Token);
                        Task<IDisposable> second = HostWideCommandLock.AcquireAsync("reload_second", "second holder", cancel.Token);
                        requests.Add(first);
                        requests.Add(second);
                        AssertTrue(first.IsCompleted && second.IsCompleted, "two requests hold the slot at once after the reload");
                    }
                    finally
                    {
                        await ReleaseAllAsync(cancel, leases, requests).ConfigureAwait(false);
                        RestoreMemory(previousReader, previousFloor);
                        try { Directory.Delete(directory, true); } catch (IOException) { }
                    }
                }
            });

            await RunTest("A request is held while memory is below the floor and granted once a re-check reads enough", async () =>
            {
                FakeHost host = new FakeHost { AvailableMb = 4100 };
                Func<long?> previousReader = HostWideCommandLock.MemoryReader;
                int previousFloor = HostWideCommandLock.MemoryFloorMb;
                List<IDisposable> leases = new List<IDisposable>();
                List<Task<IDisposable>> requests = new List<Task<IDisposable>>();
                using (CancellationTokenSource cancel = new CancellationTokenSource())
                {
                    try
                    {
                        HostWideCommandLock.SetMemoryReader(() => host.AvailableMb);
                        HostWideCommandLock.SetMemoryFloorMb(6144);
                        Task<IDisposable> request = HostWideCommandLock.AcquireAsync("mem_held", "memory-held request", cancel.Token);
                        requests.Add(request);

                        AssertFalse(request.IsCompleted, "a free slot is not granted while memory is below the floor");
                        AssertTrue(Waits("mem_held"));
                        HostSlotSnapshot held = HostWideCommandLock.Snapshot();
                        AssertEqual(0, held.Holders.Count, "the slot stays free");
                        AssertTrue(held.HeldForMemory, "the snapshot says memory holds the first request");
                        AssertEqual(6144, held.MemoryFloorMb);
                        AssertEqual(4100L, held.AvailableMemoryMb);
                        AssertEqual("waiting for free memory: 4,100 MB available, floor 6,144 MB", held.MemoryWaitReason);

                        HostWideCommandLock.RecheckMemory();
                        AssertTrue(Waits("mem_held"), "a re-check that still reads below the floor keeps the request waiting");

                        host.AvailableMb = 8000;
                        HostWideCommandLock.RecheckMemory();
                        AssertTrue(Holds("mem_held"), "the re-check grants the request once memory reaches the floor");
                        HostSlotSnapshot granted = HostWideCommandLock.Snapshot();
                        AssertFalse(granted.HeldForMemory, "nothing is held for memory after the grant");
                        AssertNull(granted.MemoryWaitReason);
                        AssertFalse(granted.Holders[0].GrantedUnderMemoryFloor, "a grant above the floor is not marked under the floor");
                    }
                    finally
                    {
                        await ReleaseAllAsync(cancel, leases, requests).ConfigureAwait(false);
                        RestoreMemory(previousReader, previousFloor);
                    }
                }
            });

            await RunTest("A memory floor of zero grants a free slot whatever memory reads", async () =>
            {
                Func<long?> previousReader = HostWideCommandLock.MemoryReader;
                int previousFloor = HostWideCommandLock.MemoryFloorMb;
                List<IDisposable> leases = new List<IDisposable>();
                List<Task<IDisposable>> requests = new List<Task<IDisposable>>();
                using (CancellationTokenSource cancel = new CancellationTokenSource())
                {
                    try
                    {
                        HostWideCommandLock.SetMemoryReader(() => 100);
                        HostWideCommandLock.SetMemoryFloorMb(0);
                        Task<IDisposable> request = HostWideCommandLock.AcquireAsync("mem_off", "floor off", cancel.Token);
                        requests.Add(request);
                        AssertTrue(request.IsCompleted, "with the floor off a free slot is granted at once");
                        AssertFalse(HostWideCommandLock.Snapshot().HeldForMemory);
                    }
                    finally
                    {
                        await ReleaseAllAsync(cancel, leases, requests).ConfigureAwait(false);
                        RestoreMemory(previousReader, previousFloor);
                    }
                }
            });

            await RunTest("An unknown memory reading grants a free slot", async () =>
            {
                Func<long?> previousReader = HostWideCommandLock.MemoryReader;
                int previousFloor = HostWideCommandLock.MemoryFloorMb;
                List<IDisposable> leases = new List<IDisposable>();
                List<Task<IDisposable>> requests = new List<Task<IDisposable>>();
                using (CancellationTokenSource cancel = new CancellationTokenSource())
                {
                    try
                    {
                        HostWideCommandLock.SetMemoryReader(() => null);
                        HostWideCommandLock.SetMemoryFloorMb(6144);
                        Task<IDisposable> request = HostWideCommandLock.AcquireAsync("mem_unknown", "unknown reading", cancel.Token);
                        requests.Add(request);
                        AssertTrue(request.IsCompleted, "an unknown reading never holds a request");
                        HostSlotSnapshot slot = HostWideCommandLock.Snapshot();
                        AssertNull(slot.AvailableMemoryMb, "the snapshot shows the unknown reading");
                        AssertFalse(slot.HeldForMemory);

                        HostWideCommandLock.SetMemoryReader(() => throw new IOException("unreadable"));
                        Task<IDisposable> failing = HostWideCommandLock.AcquireAsync("mem_throws", "failing reader", cancel.Token);
                        requests.Add(failing);
                        (await request.ConfigureAwait(false)).Dispose();
                        AssertTrue(Holds("mem_throws"), "a reader that throws counts as unknown and grants the slot");
                    }
                    finally
                    {
                        await ReleaseAllAsync(cancel, leases, requests).ConfigureAwait(false);
                        RestoreMemory(previousReader, previousFloor);
                    }
                }
            });

            await RunTest("A later request is not granted ahead of a request held for memory", async () =>
            {
                FakeHost host = new FakeHost { AvailableMb = 4100 };
                Func<long?> previousReader = HostWideCommandLock.MemoryReader;
                int previousFloor = HostWideCommandLock.MemoryFloorMb;
                List<IDisposable> leases = new List<IDisposable>();
                List<Task<IDisposable>> requests = new List<Task<IDisposable>>();
                using (CancellationTokenSource cancel = new CancellationTokenSource())
                {
                    try
                    {
                        HostWideCommandLock.SetSlotCount(2);
                        HostWideCommandLock.SetMemoryReader(() => host.AvailableMb);
                        HostWideCommandLock.SetMemoryFloorMb(6144);
                        Task<IDisposable> head = HostWideCommandLock.AcquireAsync("mem_fifo_head", "first request", cancel.Token);
                        Task<IDisposable> later = HostWideCommandLock.AcquireAsync("mem_fifo_later", "later request", cancel.Token);
                        requests.Add(head);
                        requests.Add(later);

                        HostWideCommandLock.RecheckMemory();
                        AssertTrue(Waits("mem_fifo_head"), "the first request is held for memory");
                        AssertTrue(Waits("mem_fifo_later"), "the later request is not granted ahead of the held first request");
                        HostSlotSnapshot held = HostWideCommandLock.Snapshot();
                        AssertEqual(0, held.Holders.Count, "both slots stay free");
                        AssertEqual(1, held.FindWaiter("mem_fifo_head")!.Position);
                        AssertEqual(2, held.FindWaiter("mem_fifo_later")!.Position);

                        host.AvailableMb = 8000;
                        HostWideCommandLock.RecheckMemory();
                        HostSlotSnapshot granted = HostWideCommandLock.Snapshot();
                        AssertEqual(2, granted.Holders.Count, "enough memory grants both free slots");
                        AssertEqual("mem_fifo_head", granted.Holders[0].Key, "the first request is granted first");
                        AssertEqual("mem_fifo_later", granted.Holders[1].Key);
                    }
                    finally
                    {
                        await ReleaseAllAsync(cancel, leases, requests).ConfigureAwait(false);
                        RestoreMemory(previousReader, previousFloor);
                    }
                }
            });

            await RunTest("A request held for memory with no slot held is granted under the floor after the maximum wait", async () =>
            {
                FakeHost host = new FakeHost { AvailableMb = 4100, UtcNow = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc) };
                Func<long?> previousReader = HostWideCommandLock.MemoryReader;
                int previousFloor = HostWideCommandLock.MemoryFloorMb;
                List<IDisposable> leases = new List<IDisposable>();
                List<Task<IDisposable>> requests = new List<Task<IDisposable>>();
                using (CancellationTokenSource cancel = new CancellationTokenSource())
                {
                    try
                    {
                        HostWideCommandLock.SetSlotCount(2);
                        HostWideCommandLock.SetClock(() => host.UtcNow);
                        HostWideCommandLock.SetMemoryReader(() => host.AvailableMb);
                        HostWideCommandLock.SetMemoryFloorMb(6144);
                        long grantsBefore = HostWideCommandLock.Snapshot().UnderFloorGrants;
                        Task<IDisposable> starved = HostWideCommandLock.AcquireAsync("mem_starved", "starved request", cancel.Token);
                        requests.Add(starved);
                        AssertEqual(host.UtcNow, HostWideCommandLock.Snapshot().MemoryHeldSinceUtc, "the hold is dated from the first memory check");

                        host.UtcNow = host.UtcNow.Add(HostWideCommandLock.MaximumMemoryWait).AddSeconds(-1);
                        HostWideCommandLock.RecheckMemory();
                        AssertTrue(Waits("mem_starved"), "the request still waits before the maximum memory wait");

                        host.UtcNow = host.UtcNow.AddSeconds(1);
                        HostWideCommandLock.RecheckMemory();
                        AssertTrue(Holds("mem_starved"), "with no slot held the request is granted after the maximum memory wait");
                        HostSlotSnapshot afterGrant = HostWideCommandLock.Snapshot();
                        AssertTrue(afterGrant.FindHolder("mem_starved")!.GrantedUnderMemoryFloor, "the grant is recorded as under the floor");
                        AssertEqual(grantsBefore + 1, afterGrant.UnderFloorGrants, "the under-floor grant is counted");

                        Task<IDisposable> next = HostWideCommandLock.AcquireAsync("mem_next", "next request", cancel.Token);
                        requests.Add(next);
                        host.UtcNow = host.UtcNow.Add(HostWideCommandLock.MaximumMemoryWait).AddMinutes(1);
                        HostWideCommandLock.RecheckMemory();
                        AssertTrue(Waits("mem_next"), "while a slot is held the maximum memory wait grants nothing");
                        AssertTrue(HostWideCommandLock.Snapshot().HeldForMemory);
                    }
                    finally
                    {
                        await ReleaseAllAsync(cancel, leases, requests).ConfigureAwait(false);
                        RestoreMemory(previousReader, previousFloor);
                    }
                }
            });

            await RunTest("MemAvailable is read from meminfo text in megabytes and anything else reads as unknown", () =>
            {
                string meminfo = "MemTotal:       16384000 kB\nMemFree:          512000 kB\nMemAvailable:    4198400 kB\nBuffers:          100 kB\n";
                AssertEqual(4100L, HostWideCommandLock.ParseMemAvailableMb(meminfo), "kB are converted to MB");
                AssertNull(HostWideCommandLock.ParseMemAvailableMb("MemTotal:       16384000 kB\n"), "a missing line is unknown");
                AssertNull(HostWideCommandLock.ParseMemAvailableMb("MemAvailable:    lots kB\n"), "a malformed value is unknown");
                AssertNull(HostWideCommandLock.ParseMemAvailableMb("MemAvailable:\n"), "an empty value is unknown");
                AssertNull(HostWideCommandLock.ParseMemAvailableMb(""), "empty text is unknown");
                AssertNull(HostWideCommandLock.ParseMemAvailableMb(null), "no text is unknown");
                return Task.CompletedTask;
            });

            await RunTest("The memory floor setting defaults to 6144 MB, is clamped and is copied", () =>
            {
                DefinitionOfDoneSettings settings = new DefinitionOfDoneSettings();
                AssertEqual(6144, settings.HostCommandMemoryFloorMb, "the setting defaults to 6144 MB");
                settings.HostCommandMemoryFloorMb = -5;
                AssertEqual(0, settings.HostCommandMemoryFloorMb, "a negative floor is raised to zero");
                settings.HostCommandMemoryFloorMb = 2000000;
                AssertEqual(1048576, settings.HostCommandMemoryFloorMb, "a floor above the maximum is lowered to it");
                settings.HostCommandMemoryFloorMb = 4096;

                DefinitionOfDoneSettings copy = new DefinitionOfDoneSettings();
                copy.CopyFrom(settings);
                AssertEqual(4096, copy.HostCommandMemoryFloorMb, "an in-place settings copy carries the memory floor");
                return Task.CompletedTask;
            });

            await RunTest("A pending check's slot wait names the memory reason when memory holds the queue", () =>
            {
                CheckRun run = new CheckRun { Status = Armada.Core.Enums.CheckRunStatusEnum.Pending, SlotRequestedUtc = DateTime.UtcNow };
                HostSlotSnapshot slot = new HostSlotSnapshot
                {
                    MemoryFloorMb = 6144,
                    AvailableMemoryMb = 4100,
                    HeldForMemory = true,
                    MemoryWaitReason = "waiting for free memory: 4,100 MB available, floor 6,144 MB"
                };
                slot.Waiters.Add(new HostSlotWaiter { Position = 2, Key = run.Id, Description = "check" });

                CheckRunSlotWait? wait = CheckRunSlotWait.Describe(run, slot);
                AssertNotNull(wait);
                AssertEqual("WaitingForSlot", wait!.State);
                AssertEqual("waiting for free memory: 4,100 MB available, floor 6,144 MB", wait.MemoryWaitReason, "a waiter behind a memory-held request names the memory reason");
                AssertContains("enough memory is free", wait.Guidance);

                slot.HeldForMemory = false;
                AssertNull(CheckRunSlotWait.Describe(run, slot)!.MemoryWaitReason, "no memory reason while memory holds nothing");
                return Task.CompletedTask;
            });

            await RunTest("A lease records how long it waited for a host slot and which holder blocked it", async () =>
            {
                Func<long?> previousReader = HostWideCommandLock.MemoryReader;
                int previousFloor = HostWideCommandLock.MemoryFloorMb;
                FakeHost host = new FakeHost();
                List<IDisposable> leases = new List<IDisposable>();
                List<Task<IDisposable>> requests = new List<Task<IDisposable>>();
                using (CancellationTokenSource cancel = new CancellationTokenSource())
                {
                    try
                    {
                        HostWideCommandLock.SetMemoryReader(() => null);
                        HostWideCommandLock.SetMemoryFloorMb(0);
                        HostWideCommandLock.SetClock(() => host.UtcNow);

                        IDisposable holder = await HostWideCommandLock.AcquireAsync("wait_record_holder", "check Build on vsl_one", cancel.Token).ConfigureAwait(false);
                        leases.Add(holder);
                        HostSlotWaitRecord? immediate = HostWideCommandLock.ReadWaitRecord(holder);
                        AssertNotNull(immediate, "a granted lease carries a wait record");
                        AssertEqual(0L, immediate!.HostWaitMs, "an immediate grant waited no time");
                        AssertEqual(0, immediate.BlockedBy.Count, "an immediate grant names no blocking holder");
                        AssertContains("blocked_by=none", immediate.Describe());

                        Task<IDisposable> waiter = HostWideCommandLock.AcquireAsync("wait_record_waiter", "check UnitTest on vsl_two", cancel.Token);
                        requests.Add(waiter);
                        host.UtcNow = host.UtcNow.AddSeconds(7);
                        holder.Dispose();
                        leases.Remove(holder);
                        IDisposable waited = await waiter.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                        requests.Remove(waiter);
                        leases.Add(waited);

                        HostSlotWaitRecord? record = HostWideCommandLock.ReadWaitRecord(waited);
                        AssertNotNull(record);
                        AssertEqual(7000L, record!.HostWaitMs, "the host wait runs from request to grant");
                        AssertEqual(0L, record.VesselWaitMs, "a request that names no vessel has no vessel wait");
                        AssertEqual(0L, record.MemoryHeldMs);
                        AssertEqual(1, record.BlockedBy.Count, "the holder at request time is named");
                        AssertEqual("wait_record_holder: check Build on vsl_one", record.BlockedBy[0]);
                        string line = record.Describe();
                        AssertContains("host_wait_ms=7000", line);
                        AssertContains("blocked_by=wait_record_holder: check Build on vsl_one", line);
                        AssertNull(HostWideCommandLock.ReadWaitRecord(null), "a lease this lock did not grant has no record");
                    }
                    finally
                    {
                        await ReleaseAllAsync(cancel, leases, requests).ConfigureAwait(false);
                        RestoreMemory(previousReader, previousFloor);
                    }
                }
            });

            await RunTest("A lease records its vessel slot wait and the same-vessel holder that blocked it", async () =>
            {
                string vessel = "vsl_wait_record_" + Guid.NewGuid().ToString("N");
                Func<long?> previousReader = HostWideCommandLock.MemoryReader;
                int previousFloor = HostWideCommandLock.MemoryFloorMb;
                FakeHost host = new FakeHost();
                List<IDisposable> leases = new List<IDisposable>();
                List<Task<IDisposable>> requests = new List<Task<IDisposable>>();
                using (CancellationTokenSource cancel = new CancellationTokenSource())
                {
                    try
                    {
                        HostWideCommandLock.SetMemoryReader(() => null);
                        HostWideCommandLock.SetMemoryFloorMb(0);
                        HostWideCommandLock.SetSlotCount(2);
                        HostWideCommandLock.SetClock(() => host.UtcNow);

                        IDisposable holder = await HostWideCommandLock.AcquireAsync("vessel_record_holder", "captain suite lease", vessel, cancel.Token).ConfigureAwait(false);
                        leases.Add(holder);
                        Task<IDisposable> waiter = HostWideCommandLock.AcquireAsync("vessel_record_waiter", "check UnitTest", vessel, cancel.Token);
                        requests.Add(waiter);
                        host.UtcNow = host.UtcNow.AddSeconds(3);
                        holder.Dispose();
                        leases.Remove(holder);
                        IDisposable waited = await waiter.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                        requests.Remove(waiter);
                        leases.Add(waited);

                        HostSlotWaitRecord? record = HostWideCommandLock.ReadWaitRecord(waited);
                        AssertNotNull(record);
                        AssertEqual(3000L, record!.VesselWaitMs, "the vessel wait runs from the vessel request to the vessel grant");
                        AssertEqual(0L, record.HostWaitMs, "a free host slot is granted at once after the vessel slot");
                        AssertEqual(1, record.BlockedBy.Count, "the same-vessel holder is named although a host slot was free");
                        AssertEqual("vessel_record_holder: captain suite lease", record.BlockedBy[0]);
                        AssertContains("vessel_wait_ms=3000", record.Describe());
                    }
                    finally
                    {
                        await ReleaseAllAsync(cancel, leases, requests).ConfigureAwait(false);
                        RestoreMemory(previousReader, previousFloor);
                    }
                }
            });

            await RunTest("A lease records the time it was held for the memory floor", async () =>
            {
                Func<long?> previousReader = HostWideCommandLock.MemoryReader;
                int previousFloor = HostWideCommandLock.MemoryFloorMb;
                FakeHost host = new FakeHost { AvailableMb = 1000 };
                List<IDisposable> leases = new List<IDisposable>();
                List<Task<IDisposable>> requests = new List<Task<IDisposable>>();
                using (CancellationTokenSource cancel = new CancellationTokenSource())
                {
                    try
                    {
                        HostWideCommandLock.SetClock(() => host.UtcNow);
                        HostWideCommandLock.SetMemoryReader(() => host.AvailableMb);
                        HostWideCommandLock.SetMemoryFloorMb(4096);

                        Task<IDisposable> waiter = HostWideCommandLock.AcquireAsync("memory_record_waiter", "check Build", cancel.Token);
                        requests.Add(waiter);
                        AssertFalse(waiter.IsCompleted, "the request is held while memory is below the floor");
                        host.UtcNow = host.UtcNow.AddSeconds(4);
                        host.AvailableMb = 8192;
                        HostWideCommandLock.RecheckMemory();
                        IDisposable granted = await waiter.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                        requests.Remove(waiter);
                        leases.Add(granted);

                        HostSlotWaitRecord? record = HostWideCommandLock.ReadWaitRecord(granted);
                        AssertNotNull(record);
                        AssertEqual(4000L, record!.MemoryHeldMs, "the memory hold runs from the first hold to the grant");
                        AssertEqual(4000L, record.HostWaitMs);
                        AssertEqual(0, record.BlockedBy.Count, "no holder blocked a request held only for memory");
                        AssertContains("memory_held_ms=4000", record.Describe());
                    }
                    finally
                    {
                        await ReleaseAllAsync(cancel, leases, requests).ConfigureAwait(false);
                        RestoreMemory(previousReader, previousFloor);
                    }
                }
            });
        }

        /// <summary>
        /// Return the memory floor and reader to what they were before a test and the clock to the system clock.
        /// </summary>
        private static void RestoreMemory(Func<long?> previousReader, int previousFloor)
        {
            HostWideCommandLock.SetMemoryFloorMb(previousFloor);
            HostWideCommandLock.SetMemoryReader(previousReader);
            HostWideCommandLock.SetClock(null);
        }

        /// <summary>
        /// Memory reading and clock a test controls.
        /// </summary>
        private sealed class FakeHost
        {
            public long? AvailableMb { get; set; } = null;

            public DateTime UtcNow { get; set; } = DateTime.UtcNow;
        }

        /// <summary>
        /// Whether the request with this key holds a slot now. A grant moves the request to the holders under the
        /// lock, while its awaiting task completes on a later continuation, so the snapshot is the exact state.
        /// </summary>
        private static bool Holds(string key)
        {
            return HostWideCommandLock.Snapshot().FindHolder(key) != null;
        }

        /// <summary>
        /// Whether the request with this key waits for a slot now.
        /// </summary>
        private static bool Waits(string key)
        {
            return HostWideCommandLock.Snapshot().FindWaiter(key) != null;
        }

        /// <summary>
        /// Return the lock to one free slot: cancel requests still waiting, release every lease a test took, and
        /// release every request that was granted. A test that fails part-way must not leave a slot held for the
        /// suites that run after it in this process.
        /// </summary>
        private static async Task ReleaseAllAsync(CancellationTokenSource cancel, List<IDisposable> leases, List<Task<IDisposable>> requests)
        {
            cancel.Cancel();
            foreach (IDisposable lease in leases) lease.Dispose();
            foreach (Task<IDisposable> request in requests)
            {
                try
                {
                    IDisposable granted = await request.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                    granted.Dispose();
                }
                catch (OperationCanceledException)
                {
                    // A request still waiting at cleanup was cancelled and left the queue.
                }
                catch (TimeoutException)
                {
                    // A request granted to nobody within the bound is reported by the assertion that failed.
                }
            }

            HostWideCommandLock.SetSlotCount(1);
            HostWideCommandLock.SetVesselSlotCount(1);
        }
    }
}
