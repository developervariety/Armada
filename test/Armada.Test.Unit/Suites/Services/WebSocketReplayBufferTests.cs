namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Linq;
    using System.Threading.Tasks;
    using Armada.Server.WebSocket;
    using Armada.Test.Common;

    /// <summary>
    /// Unit coverage for bounded WebSocket event replay.
    /// </summary>
    public class WebSocketReplayBufferTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "WebSocket Replay Buffer";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("Cursors increase and retained frames read in exact order", () =>
            {
                WebSocketReplayBuffer buffer = new WebSocketReplayBuffer(8, 1024, "stream-a");
                WebSocketReplayRecord first = buffer.Append((stream, cursor) => stream + ":" + cursor + ":first");
                WebSocketReplayRecord second = buffer.Append((stream, cursor) => stream + ":" + cursor + ":second");
                WebSocketReplayRecord third = buffer.Append((stream, cursor) => stream + ":" + cursor + ":third");

                AssertEqual(1L, first.Cursor);
                AssertEqual(2L, second.Cursor);
                AssertEqual(3L, third.Cursor);

                WebSocketReplayReadResult result = buffer.ReadAfter("stream-a", 1);
                AssertFalse(result.HasGap);
                AssertEqual("2,3", String.Join(',', result.Records.Select(record => record.Cursor)));
                AssertEqual("stream-a:2:second,stream-a:3:third", String.Join(',', result.Records.Select(record => record.Frame)));
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("A cursor at the eviction boundary remains replayable", () =>
            {
                WebSocketReplayBuffer buffer = new WebSocketReplayBuffer(2, 1024, "stream-boundary");
                buffer.Append((_, cursor) => "frame-" + cursor);
                buffer.Append((_, cursor) => "frame-" + cursor);
                buffer.Append((_, cursor) => "frame-" + cursor);

                WebSocketReplayReadResult result = buffer.ReadAfter("stream-boundary", 1);
                AssertFalse(result.HasGap);
                AssertEqual(2L, result.OldestAvailableCursor);
                AssertEqual("2,3", String.Join(',', result.Records.Select(record => record.Cursor)));
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("An evicted cursor returns history_evicted", () =>
            {
                WebSocketReplayBuffer buffer = new WebSocketReplayBuffer(2, 1024, "stream-evicted");
                buffer.Append((_, cursor) => "frame-" + cursor);
                buffer.Append((_, cursor) => "frame-" + cursor);
                buffer.Append((_, cursor) => "frame-" + cursor);

                WebSocketReplayReadResult result = buffer.ReadAfter("stream-evicted", 0);
                AssertTrue(result.HasGap);
                AssertEqual(WebSocketReplayGapReasons.HistoryEvicted, result.GapReason);
                AssertEqual(0, result.Records.Count);
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("A different process stream returns stream_changed", () =>
            {
                WebSocketReplayBuffer buffer = new WebSocketReplayBuffer(2, 1024, "stream-current");
                WebSocketReplayReadResult result = buffer.ReadAfter("stream-old", 0);

                AssertTrue(result.HasGap);
                AssertEqual(WebSocketReplayGapReasons.StreamChanged, result.GapReason);
                AssertEqual("stream-current", result.StreamId);
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("A future cursor returns cursor_ahead", () =>
            {
                WebSocketReplayBuffer buffer = new WebSocketReplayBuffer(2, 1024, "stream-future");
                buffer.Append((_, cursor) => "frame-" + cursor);
                WebSocketReplayReadResult result = buffer.ReadAfter("stream-future", 2);

                AssertTrue(result.HasGap);
                AssertEqual(WebSocketReplayGapReasons.CursorAhead, result.GapReason);
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("The production record cap retains no more than 128 frames", () =>
            {
                WebSocketReplayBuffer buffer = new WebSocketReplayBuffer();
                for (int index = 0; index < 129; index++)
                    buffer.Append((_, cursor) => "frame-" + cursor);

                AssertEqual(128, buffer.Count);
                AssertEqual(129L, buffer.CurrentCursor);
                WebSocketReplayReadResult result = buffer.ReadAfter(buffer.StreamId, 1);
                AssertFalse(result.HasGap);
                AssertEqual(2L, result.OldestAvailableCursor);
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("The production byte cap retains no more than one MiB", () =>
            {
                WebSocketReplayBuffer buffer = new WebSocketReplayBuffer();
                string largeFrame = new string('x', 600 * 1024);
                buffer.Append((_, _) => largeFrame + "-first");
                WebSocketReplayRecord second = buffer.Append((_, _) => largeFrame + "-second");

                AssertEqual(1, buffer.Count);
                AssertTrue(buffer.RetainedBytes <= 1024 * 1024);
                WebSocketReplayReadResult result = buffer.ReadAfter(buffer.StreamId, 1);
                AssertFalse(result.HasGap);
                AssertEqual(second.Frame, result.Records[0].Frame, "Replay must preserve the serialized frame without modification.");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("A frame larger than the byte cap creates an explicit history gap", () =>
            {
                WebSocketReplayBuffer buffer = new WebSocketReplayBuffer(8, 16, "stream-large");
                buffer.Append((_, _) => "small-frame");
                WebSocketReplayRecord record = buffer.Append((_, _) => "this-frame-is-too-large");

                AssertEqual(2L, record.Cursor);
                AssertEqual(0, buffer.Count);
                AssertEqual(0, buffer.RetainedBytes);
                WebSocketReplayReadResult result = buffer.ReadAfter("stream-large", 1);
                AssertEqual(WebSocketReplayGapReasons.HistoryEvicted, result.GapReason);
                AssertEqual(3L, result.OldestAvailableCursor);
                return Task.CompletedTask;
            }).ConfigureAwait(false);
        }
    }
}
