namespace Armada.Core.Harbor
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// The Harbor's job messages (started, output, activity, exited, and job-scoped errors) on their way to the Admiral.
    /// One outbox lives as long as the Harbor process and outlives every link session, so a job that keeps running
    /// through a link drop (Wi-Fi drop, lid close, Admiral redeploy) still reports its remaining output and its exit: the
    /// messages wait here while the link is down and are replayed in order on the next link. Every message gets an
    /// increasing <see cref="HarborMessage.OutboxSequence"/>; an Admiral that acknowledges sequences lets the outbox keep
    /// sent messages until they are acknowledged (a message written to a dead socket is replayed too), and drops
    /// replayed duplicates by sequence. The outbox is bounded (<see cref="MaxMessages"/>, <see cref="MaxBytes"/>): when
    /// full it drops the oldest streamed output first and keeps lifecycle messages. Thread safe.
    /// </summary>
    public class HarborJobOutbox
    {
        #region Public-Members

        /// <summary>
        /// Raised (on the adding thread, outside the outbox lock) after a message is added, so the link's send pump wakes.
        /// </summary>
        public event Action? MessageAdded;

        /// <summary>
        /// Identifies this outbox (a new one per Harbor process), sent in the handshake as
        /// <see cref="HarborHandshake.JobOutboxEpoch"/>.
        /// </summary>
        public string Epoch { get; } = Guid.NewGuid().ToString("N");

        /// <summary>
        /// Maximum number of messages held. Minimum 16; defaults to 20000.
        /// </summary>
        public int MaxMessages
        {
            get { lock (_Lock) return _MaxMessages; }
            set { lock (_Lock) _MaxMessages = value < 16 ? 16 : value; }
        }

        /// <summary>
        /// Maximum approximate size of the messages held, in bytes. Minimum 65536; defaults to 33554432 (32 MiB).
        /// </summary>
        public long MaxBytes
        {
            get { lock (_Lock) return _MaxBytes; }
            set { lock (_Lock) _MaxBytes = value < 65536 ? 65536 : value; }
        }

        /// <summary>
        /// Number of messages held (sent and waiting for acknowledgement, or not sent yet).
        /// </summary>
        public int Count
        {
            get { lock (_Lock) return _Entries.Count; }
        }

        /// <summary>
        /// Number of messages held that were not written to the current link yet.
        /// </summary>
        public int UnsentCount
        {
            get
            {
                lock (_Lock)
                {
                    int count = 0;
                    for (LinkedListNode<HarborJobOutboxEntry>? node = _Cursor; node != null; node = node.Next) count++;
                    return count;
                }
            }
        }

        /// <summary>
        /// Number of messages dropped because the outbox was full.
        /// </summary>
        public long DroppedCount
        {
            get { lock (_Lock) return _Dropped; }
        }

        /// <summary>
        /// How sent messages are treated on the current link.
        /// </summary>
        public HarborJobOutboxModeEnum Mode
        {
            get { lock (_Lock) return _Mode; }
        }

        #endregion

        #region Private-Members

        private readonly object _Lock = new object();
        private readonly LinkedList<HarborJobOutboxEntry> _Entries = new LinkedList<HarborJobOutboxEntry>();
        private LinkedListNode<HarborJobOutboxEntry>? _Cursor = null;
        private HarborJobOutboxModeEnum _Mode = HarborJobOutboxModeEnum.Pending;
        private long _NextSequence = 0;
        private long _Bytes = 0;
        private long _Dropped = 0;
        private int _MaxMessages = 20000;
        private long _MaxBytes = 32L * 1024L * 1024L;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate an empty outbox with a new epoch.
        /// </summary>
        public HarborJobOutbox()
        {
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Add a job message: give it the next sequence and queue it after every message already held.
        /// </summary>
        /// <param name="message">The message.</param>
        /// <returns>The sequence assigned.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="message"/> is null.</exception>
        public long Add(HarborMessage message)
        {
            if (message == null) throw new ArgumentNullException(nameof(message));

            long sequence;
            lock (_Lock)
            {
                _NextSequence++;
                sequence = _NextSequence;
                message.OutboxSequence = sequence;
                LinkedListNode<HarborJobOutboxEntry> node = _Entries.AddLast(new HarborJobOutboxEntry(message, sequence));
                _Bytes += node.Value.Bytes;
                if (_Cursor == null) _Cursor = node;
                TrimToBounds();
            }

            try { MessageAdded?.Invoke(); }
            catch { }
            return sequence;
        }

        /// <summary>
        /// Start a new link: every message held counts as not sent (it is replayed in order), and sent messages are kept
        /// until the Admiral's handshake acknowledgement says how to treat them (<see cref="SetAcknowledgementMode"/>).
        /// </summary>
        public void BeginSession()
        {
            lock (_Lock)
            {
                foreach (HarborJobOutboxEntry entry in _Entries) entry.Sent = false;
                _Cursor = _Entries.First;
                _Mode = HarborJobOutboxModeEnum.Pending;
            }
        }

        /// <summary>
        /// Apply the Admiral's handshake acknowledgement. With a sequence the Admiral acknowledges job messages: drop what
        /// it already received and keep sent messages until acknowledged. With null it predates acknowledgements: drop the
        /// messages already written to this link and drop each later one once written.
        /// </summary>
        /// <param name="lastAcknowledged">The highest sequence the Admiral received, or null.</param>
        public void SetAcknowledgementMode(long? lastAcknowledged)
        {
            lock (_Lock)
            {
                if (lastAcknowledged.HasValue)
                {
                    _Mode = HarborJobOutboxModeEnum.Acknowledged;
                    RemoveThrough(lastAcknowledged.Value);
                    return;
                }

                _Mode = HarborJobOutboxModeEnum.Legacy;
                while (_Entries.First != null && _Entries.First != _Cursor && _Entries.First.Value.Sent) RemoveNode(_Entries.First);
            }
        }

        /// <summary>
        /// The Admiral received every message up to <paramref name="lastSequence"/>: stop keeping them.
        /// </summary>
        /// <param name="lastSequence">The highest sequence received.</param>
        public void Acknowledge(long lastSequence)
        {
            lock (_Lock) RemoveThrough(lastSequence);
        }

        /// <summary>
        /// The next message to write to the current link, in order, without removing it.
        /// </summary>
        /// <param name="message">The message, or null when every message held was sent.</param>
        /// <returns>True when there is a message to send.</returns>
        public bool TryPeekNext(out HarborMessage? message)
        {
            lock (_Lock)
            {
                message = _Cursor?.Value.Message;
                return message != null;
            }
        }

        /// <summary>
        /// Record that the message returned by <see cref="TryPeekNext"/> was written to the link: it is dropped (legacy
        /// Admiral) or kept until acknowledged. A message the outbox dropped meanwhile is ignored.
        /// </summary>
        /// <param name="message">The message written.</param>
        public void MarkSent(HarborMessage message)
        {
            if (message == null) return;
            lock (_Lock)
            {
                if (_Cursor == null || !ReferenceEquals(_Cursor.Value.Message, message)) return;
                LinkedListNode<HarborJobOutboxEntry> node = _Cursor;
                _Cursor = node.Next;
                if (_Mode == HarborJobOutboxModeEnum.Legacy) RemoveNode(node);
                else node.Value.Sent = true;
            }
        }

        /// <summary>
        /// Copies of the messages held, oldest first (for diagnostics and tests).
        /// </summary>
        /// <returns>The messages.</returns>
        public List<HarborMessage> Snapshot()
        {
            lock (_Lock)
            {
                List<HarborMessage> messages = new List<HarborMessage>();
                foreach (HarborJobOutboxEntry entry in _Entries) messages.Add(entry.Message);
                return messages;
            }
        }

        #endregion

        #region Private-Methods

        private void RemoveThrough(long lastSequence)
        {
            while (_Entries.First != null && _Entries.First.Value.Sequence <= lastSequence) RemoveNode(_Entries.First);
        }

        private void RemoveNode(LinkedListNode<HarborJobOutboxEntry> node)
        {
            if (node == _Cursor) _Cursor = node.Next;
            _Bytes -= node.Value.Bytes;
            _Entries.Remove(node);
        }

        private void TrimToBounds()
        {
            while (_Entries.Count > 1 && (_Entries.Count > _MaxMessages || _Bytes > _MaxBytes))
            {
                LinkedListNode<HarborJobOutboxEntry>? victim = FindVictim(true) ?? FindVictim(false) ?? _Entries.First;
                if (victim == null) return;
                RemoveNode(victim);
                _Dropped++;
            }
        }

        private LinkedListNode<HarborJobOutboxEntry>? FindVictim(bool sentOnly)
        {
            for (LinkedListNode<HarborJobOutboxEntry>? node = _Entries.First; node != null; node = node.Next)
            {
                if (sentOnly && !node.Value.Sent) return null;
                if (node.Value.Droppable && (!sentOnly || node.Value.Sent)) return node;
            }

            return null;
        }

        #endregion
    }
}
