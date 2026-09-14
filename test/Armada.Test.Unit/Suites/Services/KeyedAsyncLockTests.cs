namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Services;
    using Armada.Test.Common;

    /// <summary>
    /// Behavioural coverage for the bounded per-key asynchronous lock.
    /// </summary>
    public class KeyedAsyncLockTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Keyed Async Lock";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("Callers for one key never overlap and the entry is removed afterwards", async () =>
            {
                KeyedAsyncLock locks = new KeyedAsyncLock(StringComparer.OrdinalIgnoreCase);
                int inside = 0;
                int maxInside = 0;
                List<Task> callers = new List<Task>();
                for (int i = 0; i < 50; i++)
                {
                    string key = i % 2 == 0 ? "objective-a" : "OBJECTIVE-A";
                    callers.Add(Task.Run(async () =>
                    {
                        using (await locks.AcquireAsync(key).ConfigureAwait(false))
                        {
                            int now = Interlocked.Increment(ref inside);
                            int seen;
                            do
                            {
                                seen = Volatile.Read(ref maxInside);
                            } while (now > seen && Interlocked.CompareExchange(ref maxInside, now, seen) != seen);
                            await Task.Delay(1).ConfigureAwait(false);
                            Interlocked.Decrement(ref inside);
                        }
                    }));
                }

                await Task.WhenAll(callers).ConfigureAwait(false);
                AssertEqual(1, maxInside, "Callers for one key must be serialized.");
                AssertEqual(0, locks.Count, "No entry may remain once every caller released the key.");
            });

            await RunTest("Different keys proceed while one key is held", async () =>
            {
                KeyedAsyncLock locks = new KeyedAsyncLock();
                using (await locks.AcquireAsync("held").ConfigureAwait(false))
                {
                    Task<IDisposable> other = locks.AcquireAsync("other");
                    Task completed = await Task.WhenAny(other, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
                    AssertTrue(ReferenceEquals(completed, other), "A different key must not wait for the held key.");
                    AssertEqual(2, locks.Count);
                    other.Result.Dispose();
                    AssertEqual(1, locks.Count, "Releasing one key removes only that entry.");
                }

                AssertEqual(0, locks.Count);
            });

            await RunTest("A cancelled waiter releases its reservation and the entry is removed", async () =>
            {
                KeyedAsyncLock locks = new KeyedAsyncLock();
                IDisposable holder = await locks.AcquireAsync("busy").ConfigureAwait(false);
                using (CancellationTokenSource cancel = new CancellationTokenSource())
                {
                    Task<IDisposable> waiter = locks.AcquireAsync("busy", cancel.Token);
                    AssertFalse(waiter.IsCompleted, "The waiter must wait while the key is held.");
                    cancel.Cancel();
                    bool cancelled = false;
                    try
                    {
                        await waiter.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        cancelled = true;
                    }

                    AssertTrue(cancelled, "The waiter must observe cancellation.");
                }

                AssertEqual(1, locks.Count, "Only the holder keeps the entry after a waiter cancels.");
                holder.Dispose();
                holder.Dispose();
                AssertEqual(0, locks.Count, "A repeated release is a no-op and the entry is removed once.");

                using (await locks.AcquireAsync("busy").ConfigureAwait(false))
                {
                    AssertEqual(1, locks.Count, "A later caller acquires the released key.");
                }
            });

            await RunTest("A stress run over many keys returns the entry count to zero", async () =>
            {
                KeyedAsyncLock locks = new KeyedAsyncLock();
                List<Task> callers = new List<Task>();
                for (int i = 0; i < 2000; i++)
                {
                    string key = "key-" + (i % 400);
                    callers.Add(Task.Run(async () =>
                    {
                        using (await locks.AcquireAsync(key).ConfigureAwait(false))
                        {
                            await Task.Yield();
                        }
                    }));
                }

                await Task.WhenAll(callers).ConfigureAwait(false);
                AssertEqual(0, locks.Count, "The lock must not retain entries for keys nobody holds.");
            });
        }
    }
}
