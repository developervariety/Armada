namespace Armada.Server.WebSocket
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using Armada.Core.Models;

    /// <summary>
    /// Builds the frames that activate a subscription from the replay buffer, the client's resume position and a
    /// snapshot read after the snapshot watermark. The caller holds the hub's broadcast lock, so no event is
    /// appended while the frames are built.
    /// </summary>
    internal static class WebSocketSubscriptionReconciliation
    {
        #region Public-Methods

        /// <summary>
        /// Build the subscription frames. Every event after the snapshot cursor is delivered in cursor order up to
        /// the current cursor, so the first live event follows <c>stream.ready</c> without a skip. History that
        /// cannot be delivered is named in an <c>event.gap</c> frame with its cursor range and time bounds.
        /// </summary>
        /// <param name="buffer">Replay buffer of the hub.</param>
        /// <param name="request">Subscribe request with the client's resume position.</param>
        /// <param name="replay">Replay read at the snapshot watermark, or null when the client sent no resume position.</param>
        /// <param name="snapshotWatermark">Buffer cursor taken before the snapshot was read.</param>
        /// <param name="snapshotWatermarkUtc">UTC time the watermark was taken.</param>
        /// <param name="snapshotData">Snapshot payload.</param>
        /// <param name="auth">Session credential; replayed events obey the same delivery scope as live ones.</param>
        /// <param name="queueCapacity">Capacity of the session's outbound queue.</param>
        /// <param name="jsonOptions">Serializer options of the hub.</param>
        /// <returns>The frames in send order.</returns>
        public static WebSocketSubscriptionFrames Compose(
            WebSocketReplayBuffer buffer,
            WebSocketSubscribeRequest request,
            WebSocketReplayReadResult? replay,
            long snapshotWatermark,
            DateTime snapshotWatermarkUtc,
            object snapshotData,
            AuthContext? auth,
            int queueCapacity,
            JsonSerializerOptions jsonOptions)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (jsonOptions == null) throw new ArgumentNullException(nameof(jsonOptions));

            // The queue must also hold up to two gap notices, the snapshot and the ready frame.
            int budget = Math.Max(0, queueCapacity - 4);
            WebSocketGapNotice? replayGap = null;
            WebSocketGapNotice? catchUpGap = null;

            // Events appended while the snapshot was read. When part of that history was evicted before this
            // read, a reconnect could never recover it either, so the subscription names the lost range and
            // continues from the oldest retained event instead of pointing the client back at a lost cursor.
            long snapshotCursor = snapshotWatermark;
            WebSocketReplayReadResult catchUp = buffer.ReadAfter(buffer.StreamId, snapshotWatermark);
            IReadOnlyList<WebSocketReplayRecord> catchUpRecords = catchUp.Records;
            if (catchUp.HasGap)
            {
                snapshotCursor = catchUp.OldestAvailableCursor - 1;
                catchUpRecords = buffer.ReadAfter(buffer.StreamId, snapshotCursor).Records;
            }
            if (catchUpRecords.Count > budget)
            {
                // Keep the newest events that fit, so the stream still ends at the current cursor.
                List<WebSocketReplayRecord> kept = new List<WebSocketReplayRecord>(budget);
                for (int index = catchUpRecords.Count - budget; index < catchUpRecords.Count; index++) kept.Add(catchUpRecords[index]);
                snapshotCursor = kept.Count > 0 ? kept[0].Cursor - 1 : buffer.CurrentCursor;
                catchUpRecords = kept;
            }
            if (snapshotCursor > snapshotWatermark)
            {
                catchUpGap = new WebSocketGapNotice(
                    WebSocketReplayGapReasons.SnapshotOverflow,
                    snapshotWatermark + 1,
                    snapshotCursor,
                    snapshotWatermarkUtc,
                    catchUpRecords.Count > 0 ? catchUpRecords[0].RecordedUtc : DateTime.UtcNow);
            }
            budget -= catchUpRecords.Count;

            // Events between the client's resume position and the watermark, sent before the snapshot.
            IReadOnlyList<WebSocketReplayRecord> initialRecords = replay?.Records ?? Array.Empty<WebSocketReplayRecord>();
            if (replay != null && replay.HasGap)
            {
                bool evicted = String.Equals(replay.GapReason, WebSocketReplayGapReasons.HistoryEvicted, StringComparison.Ordinal);
                replayGap = new WebSocketGapNotice(
                    replay.GapReason!,
                    evicted && request.Cursor.HasValue ? request.Cursor.Value + 1 : null,
                    evicted ? replay.OldestAvailableCursor - 1 : null,
                    null,
                    evicted ? replay.OldestAvailableUtc ?? snapshotWatermarkUtc : null);
            }
            else if (initialRecords.Count > budget)
            {
                replayGap = new WebSocketGapNotice(
                    WebSocketReplayGapReasons.ReplayOverflow,
                    initialRecords[0].Cursor,
                    initialRecords[initialRecords.Count - 1].Cursor,
                    null,
                    snapshotWatermarkUtc);
                initialRecords = Array.Empty<WebSocketReplayRecord>();
            }

            IReadOnlyList<WebSocketReplayRecord> initialFrames = FilterForSession(initialRecords, auth);
            IReadOnlyList<WebSocketReplayRecord> catchUpFrames = FilterForSession(catchUpRecords, auth);
            List<string> frames = new List<string>();
            foreach (WebSocketReplayRecord record in initialFrames) frames.Add(record.Frame);
            if (replayGap != null) frames.Add(SerializeGap(buffer, request, replayGap, jsonOptions));
            if (catchUpGap != null) frames.Add(SerializeGap(buffer, request, catchUpGap, jsonOptions));

            frames.Add(JsonSerializer.Serialize(new
            {
                type = "status.snapshot",
                streamId = buffer.StreamId,
                cursor = snapshotCursor,
                data = snapshotData,
                timestamp = DateTime.UtcNow
            }, jsonOptions));
            foreach (WebSocketReplayRecord record in catchUpFrames) frames.Add(record.Frame);
            long readyCursor = buffer.CurrentCursor;
            frames.Add(JsonSerializer.Serialize(new
            {
                type = "stream.ready",
                streamId = buffer.StreamId,
                cursor = readyCursor,
                data = new { replayed = initialFrames.Count + catchUpFrames.Count, gapDetected = replayGap != null || catchUpGap != null },
                timestamp = DateTime.UtcNow
            }, jsonOptions));

            return new WebSocketSubscriptionFrames(frames, snapshotCursor, readyCursor);
        }

        #endregion

        #region Private-Methods

        private static string SerializeGap(WebSocketReplayBuffer buffer, WebSocketSubscribeRequest request, WebSocketGapNotice gap, JsonSerializerOptions jsonOptions)
        {
            return JsonSerializer.Serialize(new
            {
                type = "event.gap",
                streamId = buffer.StreamId,
                data = new
                {
                    reason = gap.Reason,
                    requestedStreamId = request.StreamId,
                    requestedCursor = request.Cursor,
                    lostFromCursor = gap.LostFromCursor,
                    lostToCursor = gap.LostToCursor,
                    lostAfterUtc = gap.LostAfterUtc,
                    lostBeforeUtc = gap.LostBeforeUtc,
                    oldestCursor = buffer.ReadAfter(buffer.StreamId, buffer.CurrentCursor).OldestAvailableCursor,
                    currentCursor = buffer.CurrentCursor,
                    recovery = new
                    {
                        snapshot = "status.snapshot",
                        eventsQuery = gap.EventsQuery()
                    }
                },
                timestamp = DateTime.UtcNow
            }, jsonOptions);
        }

        private static IReadOnlyList<WebSocketReplayRecord> FilterForSession(IReadOnlyList<WebSocketReplayRecord> records, AuthContext? auth)
        {
            // Replayed events obey the same delivery scope as live ones, so reconnecting never reveals
            // an event the session could not have received.
            List<WebSocketReplayRecord> allowed = new List<WebSocketReplayRecord>(records.Count);
            foreach (WebSocketReplayRecord record in records)
            {
                if (record.Scope.CanReceive(auth)) allowed.Add(record);
            }
            return allowed;
        }

        #endregion
    }
}
