namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Concurrent;
    using Armada.Server.WebSocket;
    using Armada.Test.Common;

    /// <summary>
    /// Unit coverage for independent bounded WebSocket client output queues.
    /// </summary>
    public class WebSocketClientOutputQueueTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "WebSocket Client Output Queue";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("A blocked client does not delay another client", async () =>
            {
                TaskCompletionSource<bool> slowStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                TaskCompletionSource<bool> releaseSlow = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                TaskCompletionSource<bool> healthyReceived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

                await using WebSocketClientOutputQueue slow = new WebSocketClientOutputQueue(2, async (_, token) =>
                {
                    slowStarted.TrySetResult(true);
                    await releaseSlow.Task.WaitAsync(token).ConfigureAwait(false);
                });
                await using WebSocketClientOutputQueue healthy = new WebSocketClientOutputQueue(2, (_, _) =>
                {
                    healthyReceived.TrySetResult(true);
                    return Task.CompletedTask;
                });

                AssertTrue(slow.TryEnqueue("slow"));
                await slowStarted.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                AssertTrue(healthy.TryEnqueue("healthy"));
                await healthyReceived.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                releaseSlow.TrySetResult(true);
            }).ConfigureAwait(false);

            await RunTest("One client receives messages in enqueue order", async () =>
            {
                ConcurrentQueue<string> sent = new ConcurrentQueue<string>();
                TaskCompletionSource<bool> allSent = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                await using WebSocketClientOutputQueue output = new WebSocketClientOutputQueue(4, (message, _) =>
                {
                    sent.Enqueue(message);
                    if (sent.Count == 3) allSent.TrySetResult(true);
                    return Task.CompletedTask;
                });

                AssertTrue(output.TryEnqueue("first"));
                AssertTrue(output.TryEnqueue("second"));
                AssertTrue(output.TryEnqueue("third"));
                await allSent.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);

                AssertEqual("first,second,third", String.Join(',', sent));
            }).ConfigureAwait(false);

            await RunTest("A full queue rejects overflow immediately", async () =>
            {
                TaskCompletionSource<bool> sendStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                TaskCompletionSource<bool> releaseSend = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                await using WebSocketClientOutputQueue output = new WebSocketClientOutputQueue(2, async (_, token) =>
                {
                    sendStarted.TrySetResult(true);
                    await releaseSend.Task.WaitAsync(token).ConfigureAwait(false);
                });

                AssertTrue(output.TryEnqueue("active"));
                await sendStarted.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                AssertTrue(output.TryEnqueue("queued-1"));
                AssertTrue(output.TryEnqueue("queued-2"));
                AssertFalse(output.TryEnqueue("overflow"),
                    "Overflow must be explicit so the hub can disconnect the slow client.");
                releaseSend.TrySetResult(true);
            }).ConfigureAwait(false);

            await RunTest("A sender failure terminates its queue and rejects later messages", async () =>
            {
                TaskCompletionSource<Exception?> terminated = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
                await using WebSocketClientOutputQueue output = new WebSocketClientOutputQueue(
                    2,
                    (_, _) => throw new InvalidOperationException("simulated send failure"));
                output.Terminated += failure => terminated.TrySetResult(failure);

                AssertTrue(output.TryEnqueue("fails"));
                Exception? failure = await terminated.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);

                AssertNotNull(failure);
                AssertContains(failure!.Message, "simulated send failure");
                AssertFalse(output.TryEnqueue("after failure"));
            }).ConfigureAwait(false);

            await RunTest("Stop cancels an active sender and completes its loop", async () =>
            {
                TaskCompletionSource<bool> sendStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                await using WebSocketClientOutputQueue output = new WebSocketClientOutputQueue(2, async (_, token) =>
                {
                    sendStarted.TrySetResult(true);
                    await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
                });

                AssertTrue(output.TryEnqueue("active"));
                await sendStarted.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                output.Stop();
                await output.Completion.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);

                AssertFalse(output.TryEnqueue("after stop"));
            }).ConfigureAwait(false);
        }
    }
}
