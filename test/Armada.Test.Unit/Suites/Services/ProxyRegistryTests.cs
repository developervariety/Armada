namespace Armada.Test.Unit.Suites.Services
{
    using System.Collections.Generic;
    using Armada.Proxy.Services;
    using Armada.Proxy.Models;
    using Armada.Proxy.Settings;
    using Armada.Core;
    using Armada.Core.Models;
    using Armada.Test.Common;

    public class ProxyRegistryTests : TestSuite
    {
        public override string Name => "Proxy Registry";

        protected override async Task RunTestsAsync()
        {
            await RunTest("SendRequestAsync TimesOutABlockedSenderAndDropsThePendingRequest", async () =>
            {
                TaskCompletionSource neverSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                RemoteInstanceSession session = new RemoteInstanceSession((envelope, token) => neverSent.Task);

                Task<RemoteTunnelEnvelope> pending = session.SendRequestAsync(
                    "armada.status.snapshot", null, TimeSpan.FromMilliseconds(50), CancellationToken.None);
                Task finished = await Task.WhenAny(pending, Task.Delay(TimeSpan.FromSeconds(10))).ConfigureAwait(false);
                AssertTrue(ReferenceEquals(finished, pending), "A blocked sender must still time out at the request deadline");

                Exception? failure = null;
                try { await pending.ConfigureAwait(false); }
                catch (Exception ex) { failure = ex; }
                AssertTrue(failure is TimeoutException, "A deadline expiry reports TimeoutException, got: " + (failure?.GetType().Name ?? "none"));
                AssertEqual(0, session.PendingRequestCount, "A timed-out request leaves no pending waiter");
            });

            await RunTest("SendRequestAsync ReportsCallerCancellationDistinctFromTimeout", async () =>
            {
                RemoteInstanceSession session = new RemoteInstanceSession((envelope, token) => Task.CompletedTask);
                using (CancellationTokenSource caller = new CancellationTokenSource())
                {
                    Task<RemoteTunnelEnvelope> pending = session.SendRequestAsync(
                        "armada.status.snapshot", null, TimeSpan.FromMinutes(5), caller.Token);
                    caller.Cancel();

                    Exception? failure = null;
                    try { await pending.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); }
                    catch (Exception ex) { failure = ex; }
                    AssertTrue(failure is OperationCanceledException, "Caller cancellation reports OperationCanceledException, not a timeout, got: " + (failure?.GetType().Name ?? "none"));
                    AssertEqual(0, session.PendingRequestCount, "A cancelled request leaves no pending waiter");
                }
            });
        }
    }
}
