namespace Armada.Server.WebSocket
{
    using System;
    using System.Collections.Generic;
    using System.Text;

    /// <summary>
    /// Stores a bounded sequence of replayable WebSocket event frames.
    /// </summary>
    internal sealed class WebSocketReplayBuffer
    {
        private const int DefaultRecordCapacity = 128;
        private const int DefaultByteCapacity = 1024 * 1024;
        private readonly object _Sync = new object();
        private readonly Queue<WebSocketReplayRecord> _Records = new Queue<WebSocketReplayRecord>();
        private readonly int _RecordCapacity;
        private readonly int _ByteCapacity;
        private long _CurrentCursor;
        private int _RetainedBytes;

        /// <summary>
        /// Identifier for this process event stream.
        /// </summary>
        public string StreamId { get; }

        /// <summary>
        /// Most recently assigned cursor, or zero before the first event.
        /// </summary>
        public long CurrentCursor
        {
            get
            {
                lock (_Sync) return _CurrentCursor;
            }
        }

        /// <summary>
        /// Number of retained event frames.
        /// </summary>
        public int Count
        {
            get
            {
                lock (_Sync) return _Records.Count;
            }
        }

        /// <summary>
        /// Total UTF-8 size of retained event frames.
        /// </summary>
        public int RetainedBytes
        {
            get
            {
                lock (_Sync) return _RetainedBytes;
            }
        }

        /// <summary>
        /// Instantiate with the production limits and a new process stream identifier.
        /// </summary>
        public WebSocketReplayBuffer()
            : this(DefaultRecordCapacity, DefaultByteCapacity, Guid.NewGuid().ToString("N"))
        {
        }

        /// <summary>
        /// Instantiate with explicit limits and stream identifier for focused tests.
        /// </summary>
        /// <param name="recordCapacity">Maximum retained frame count.</param>
        /// <param name="byteCapacity">Maximum retained UTF-8 byte count.</param>
        /// <param name="streamId">Process stream identifier.</param>
        internal WebSocketReplayBuffer(int recordCapacity, int byteCapacity, string streamId)
        {
            if (recordCapacity < 1) throw new ArgumentOutOfRangeException(nameof(recordCapacity));
            if (byteCapacity < 1) throw new ArgumentOutOfRangeException(nameof(byteCapacity));
            if (String.IsNullOrWhiteSpace(streamId)) throw new ArgumentNullException(nameof(streamId));

            _RecordCapacity = recordCapacity;
            _ByteCapacity = byteCapacity;
            StreamId = streamId;
        }

        /// <summary>
        /// Assign the next cursor, serialize its event frame, and retain it when it fits.
        /// </summary>
        /// <param name="frameFactory">Builds the frame from the stream identifier and assigned cursor.</param>
        /// <param name="scope">Who may receive the event; null delivers to global administrators only.</param>
        /// <returns>The assigned event record, including a frame that is too large to retain.</returns>
        public WebSocketReplayRecord Append(Func<string, long, string> frameFactory, WebSocketDeliveryScope? scope = null)
        {
            if (frameFactory == null) throw new ArgumentNullException(nameof(frameFactory));

            lock (_Sync)
            {
                long cursor = checked(_CurrentCursor + 1);
                string frame = frameFactory(StreamId, cursor)
                    ?? throw new InvalidOperationException("The WebSocket replay frame factory returned null.");
                int byteCount = Encoding.UTF8.GetByteCount(frame);
                WebSocketReplayRecord record = new WebSocketReplayRecord(cursor, frame, byteCount, scope ?? WebSocketDeliveryScope.AdminOnly);
                _CurrentCursor = cursor;

                if (byteCount <= _ByteCapacity)
                {
                    _Records.Enqueue(record);
                    _RetainedBytes += byteCount;
                }
                else
                {
                    // A replay window must always be one contiguous suffix. Keeping older
                    // records across an unretained frame would falsely claim that a cursor
                    // before the oversized frame can resume without a gap.
                    _Records.Clear();
                    _RetainedBytes = 0;
                }

                TrimToLimits();
                return record;
            }
        }

        /// <summary>
        /// Read all retained events after a client resume position.
        /// </summary>
        /// <param name="streamId">Client process stream identifier.</param>
        /// <param name="cursor">Last cursor received by the client.</param>
        /// <returns>Ordered records or an explicit gap result.</returns>
        public WebSocketReplayReadResult ReadAfter(string streamId, long cursor)
        {
            lock (_Sync)
            {
                long oldestAvailableCursor = _Records.Count > 0
                    ? _Records.Peek().Cursor
                    : _CurrentCursor + 1;

                if (!String.Equals(streamId, StreamId, StringComparison.Ordinal))
                    return Gap(WebSocketReplayGapReasons.StreamChanged, oldestAvailableCursor);

                if (cursor > _CurrentCursor)
                    return Gap(WebSocketReplayGapReasons.CursorAhead, oldestAvailableCursor);

                if (cursor < oldestAvailableCursor - 1)
                    return Gap(WebSocketReplayGapReasons.HistoryEvicted, oldestAvailableCursor);

                List<WebSocketReplayRecord> records = new List<WebSocketReplayRecord>();
                foreach (WebSocketReplayRecord record in _Records)
                {
                    if (record.Cursor > cursor) records.Add(record);
                }

                return new WebSocketReplayReadResult(
                    StreamId,
                    oldestAvailableCursor,
                    _CurrentCursor,
                    records,
                    null);
            }
        }

        private WebSocketReplayReadResult Gap(string reason, long oldestAvailableCursor)
        {
            return new WebSocketReplayReadResult(
                StreamId,
                oldestAvailableCursor,
                _CurrentCursor,
                Array.Empty<WebSocketReplayRecord>(),
                reason);
        }

        private void TrimToLimits()
        {
            while (_Records.Count > _RecordCapacity || _RetainedBytes > _ByteCapacity)
            {
                WebSocketReplayRecord removed = _Records.Dequeue();
                _RetainedBytes -= removed.ByteCount;
            }
        }
    }

    /// <summary>
    /// One retained WebSocket event frame.
    /// </summary>
    internal sealed class WebSocketReplayRecord
    {
        /// <summary>
        /// Event cursor.
        /// </summary>
        public long Cursor { get; }

        /// <summary>
        /// Serialized event frame.
        /// </summary>
        public string Frame { get; }

        /// <summary>
        /// UTF-8 size of the serialized frame.
        /// </summary>
        public int ByteCount { get; }

        /// <summary>
        /// Who may receive the event, on live delivery and on replay alike.
        /// </summary>
        public WebSocketDeliveryScope Scope { get; }

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="cursor">Event cursor.</param>
        /// <param name="frame">Serialized event frame.</param>
        /// <param name="byteCount">UTF-8 frame size.</param>
        /// <param name="scope">Who may receive the event; null delivers to global administrators only.</param>
        public WebSocketReplayRecord(long cursor, string frame, int byteCount, WebSocketDeliveryScope? scope = null)
        {
            Cursor = cursor;
            Frame = frame ?? throw new ArgumentNullException(nameof(frame));
            ByteCount = byteCount;
            Scope = scope ?? WebSocketDeliveryScope.AdminOnly;
        }
    }

    /// <summary>
    /// Result of a WebSocket replay read.
    /// </summary>
    internal sealed class WebSocketReplayReadResult
    {
        /// <summary>
        /// Current process stream identifier.
        /// </summary>
        public string StreamId { get; }

        /// <summary>
        /// Oldest cursor that can currently be read.
        /// </summary>
        public long OldestAvailableCursor { get; }

        /// <summary>
        /// Most recently assigned cursor.
        /// </summary>
        public long CurrentCursor { get; }

        /// <summary>
        /// Retained records after the requested cursor, in cursor order.
        /// </summary>
        public IReadOnlyList<WebSocketReplayRecord> Records { get; }

        /// <summary>
        /// Named gap reason, or null when replay is complete.
        /// </summary>
        public string? GapReason { get; }

        /// <summary>
        /// True when the requested resume position cannot be replayed completely.
        /// </summary>
        public bool HasGap => GapReason != null;

        /// <summary>
        /// Instantiate.
        /// </summary>
        public WebSocketReplayReadResult(
            string streamId,
            long oldestAvailableCursor,
            long currentCursor,
            IReadOnlyList<WebSocketReplayRecord> records,
            string? gapReason)
        {
            StreamId = streamId ?? throw new ArgumentNullException(nameof(streamId));
            OldestAvailableCursor = oldestAvailableCursor;
            CurrentCursor = currentCursor;
            Records = records ?? throw new ArgumentNullException(nameof(records));
            GapReason = gapReason;
        }
    }

    /// <summary>
    /// Stable wire values for replay gap reasons.
    /// </summary>
    internal static class WebSocketReplayGapReasons
    {
        /// <summary>
        /// The client token names another Admiral process stream.
        /// </summary>
        public const string StreamChanged = "stream_changed";

        /// <summary>
        /// The client cursor is later than the current stream cursor.
        /// </summary>
        public const string CursorAhead = "cursor_ahead";

        /// <summary>
        /// The required event history is no longer retained.
        /// </summary>
        public const string HistoryEvicted = "history_evicted";
    }
}
