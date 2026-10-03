namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Test.Common;

    /// <summary>
    /// Order, cancellation and reporting of the host-wide build and test slot.
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
        }
    }
}
