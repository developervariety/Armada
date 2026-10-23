namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Settings;
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

            await RunTest("The configured record cap bounds the retained frames", () =>
            {
                WebSocketReplayBuffer buffer = new WebSocketReplayBuffer(new WebSocketSettings { ReplayHistoryEvents = 128 });
                for (int index = 0; index < 129; index++)
                    buffer.Append((_, cursor) => "frame-" + cursor);

                AssertEqual(128, buffer.Count);
                AssertEqual(129L, buffer.CurrentCursor);
                WebSocketReplayReadResult result = buffer.ReadAfter(buffer.StreamId, 1);
                AssertFalse(result.HasGap);
                AssertEqual(2L, result.OldestAvailableCursor);
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("The configured byte cap bounds the retained bytes", () =>
            {
                WebSocketReplayBuffer buffer = new WebSocketReplayBuffer(new WebSocketSettings { ReplayHistoryBytes = 1024 * 1024 });
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

            await RunTest("A snapshot whose catch-up history was evicted resumes contiguously and names the lost range", () =>
            {
                // Events 3 and 4 are appended after the snapshot watermark and evicted before the catch-up is read,
                // as a slow snapshot read under load does. The client must see one named gap and then a contiguous
                // stream, so its next live event is not read as a skipped sequence.
                WebSocketReplayBuffer buffer = new WebSocketReplayBuffer(4, 1024 * 1024, "stream-snapshot");
                AppendEvent(buffer);
                AppendEvent(buffer);
                long watermark = buffer.CurrentCursor;
                for (int index = 0; index < 6; index++) AppendEvent(buffer);

                WebSocketSubscriptionFrames composed = WebSocketSubscriptionReconciliation.Compose(
                    buffer,
                    new WebSocketSubscribeRequest { Route = "subscribe" },
                    null,
                    watermark,
                    DateTime.UtcNow,
                    new { status = (object?)null },
                    new AuthContext { IsAuthenticated = true, IsAdmin = true },
                    64,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web));

                List<JsonElement> frames = composed.Frames.Select(Parse).ToList();
                long cursor = -1;
                bool ready = false;
                foreach (JsonElement frame in frames)
                {
                    string type = frame.GetProperty("type").GetString()!;
                    if (type == "status.snapshot")
                    {
                        cursor = frame.GetProperty("cursor").GetInt64();
                    }
                    else if (type == "stream.ready")
                    {
                        AssertEqual(cursor, frame.GetProperty("cursor").GetInt64(), "Ready must name the last delivered cursor.");
                        ready = true;
                    }
                    else if (type != "event.gap")
                    {
                        AssertTrue(cursor >= 0, "Events after the gap must follow the snapshot.");
                        AssertEqual(cursor + 1, frame.GetProperty("cursor").GetInt64(), "Events after the snapshot must be contiguous.");
                        cursor = frame.GetProperty("cursor").GetInt64();
                    }
                }

                AssertTrue(ready, "The subscription must end with stream.ready.");
                WebSocketReplayRecord live = AppendEvent(buffer);
                AssertEqual(cursor + 1, live.Cursor, "The first live event must follow the ready cursor without a skip.");

                List<JsonElement> gaps = frames.Where(frame => frame.GetProperty("type").GetString() == "event.gap").ToList();
                AssertEqual(1, gaps.Count, "One gap notice must name the lost catch-up history.");
                JsonElement gap = gaps[0].GetProperty("data");
                AssertEqual("snapshot_overflow", gap.GetProperty("reason").GetString());
                AssertTrue(gap.TryGetProperty("lostFromCursor", out JsonElement lostFrom) && lostFrom.ValueKind == JsonValueKind.Number,
                    "The gap must name the first lost cursor.");
                AssertEqual(3L, lostFrom.GetInt64());
                AssertEqual(4L, gap.GetProperty("lostToCursor").GetInt64());
                return Task.CompletedTask;
            }).ConfigureAwait(false);
        }

        private static WebSocketReplayRecord AppendEvent(WebSocketReplayBuffer buffer)
        {
            return buffer.Append((stream, cursor) => JsonSerializer.Serialize(new { type = "mission.changed", streamId = stream, cursor = cursor }));
        }

        private static JsonElement Parse(string frame)
        {
            using (JsonDocument document = JsonDocument.Parse(frame))
            {
                return document.RootElement.Clone();
            }
        }
    }
}
