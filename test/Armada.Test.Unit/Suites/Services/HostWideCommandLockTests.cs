namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Test.Common;

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
                AssertEqual("Queued", CheckRunSlotWait.Describe(run, new HostSlotSnapshot())!.State, "a check that has not asked for the slot is queued to start");
                run.Status = Armada.Core.Enums.CheckRunStatusEnum.Running;
                AssertEqual("Running", CheckRunSlotWait.Describe(run, slot)!.State);
                run.Status = Armada.Core.Enums.CheckRunStatusEnum.Failed;
                AssertNull(CheckRunSlotWait.Describe(run, slot), "a finished check has no wait");
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

                DefinitionOfDoneSettings copy = new DefinitionOfDoneSettings();
                copy.CopyFrom(settings);
                AssertEqual(3, copy.HostCommandSlots, "an in-place settings copy carries the slot count");
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

                        ArmadaSettings edited = await ArmadaSettings.LoadAsync(path).ConfigureAwait(false);
                        edited.DefinitionOfDone.HostCommandSlots = 2;
                        await edited.SaveAsync(path).ConfigureAwait(false);
                        SettingsReloadResult result = await reload.ReloadAsync().ConfigureAwait(false);
                        AssertTrue(result.Applied, "the edited file is a valid candidate: " + result.Reason);

                        AssertEqual(2, live.DefinitionOfDone.HostCommandSlots, "the reloaded setting is live");
                        AssertEqual(2, HostWideCommandLock.SlotCount, "the reloaded slot count reaches the lock without a restart");
                        Task<IDisposable> first = HostWideCommandLock.AcquireAsync("reload_first", "first holder", cancel.Token);
                        Task<IDisposable> second = HostWideCommandLock.AcquireAsync("reload_second", "second holder", cancel.Token);
                        requests.Add(first);
                        requests.Add(second);
                        AssertTrue(first.IsCompleted && second.IsCompleted, "two requests hold the slot at once after the reload");
                    }
                    finally
                    {
                        await ReleaseAllAsync(cancel, leases, requests).ConfigureAwait(false);
                        try { Directory.Delete(directory, true); } catch (IOException) { }
                    }
                }
            });
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
        }
    }
}
