namespace Armada.Core.Harbor
{
    using System;

    /// <summary>
    /// One job message held in a <see cref="HarborJobOutbox"/>.
    /// </summary>
    public class HarborJobOutboxEntry
    {
        #region Public-Members

        /// <summary>
        /// The message (its <see cref="HarborMessage.OutboxSequence"/> is set).
        /// </summary>
        public HarborMessage Message { get; }

        /// <summary>
        /// Position of the message in the outbox, starting at 1.
        /// </summary>
        public long Sequence { get; }

        /// <summary>
        /// Approximate size of the message in bytes, counted against <see cref="HarborJobOutbox.MaxBytes"/>.
        /// </summary>
        public long Bytes { get; }

        /// <summary>
        /// Whether the message was written to the current link.
        /// </summary>
        public bool Sent { get; set; } = false;

        /// <summary>
        /// Whether the outbox may drop the message when it is full (streamed output and activity). Lifecycle messages
        /// (started, exited, errors) are dropped only when nothing else is left to drop.
        /// </summary>
        public bool Droppable { get; }

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="message">The message.</param>
        /// <param name="sequence">Its position in the outbox.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="message"/> is null.</exception>
        public HarborJobOutboxEntry(HarborMessage message, long sequence)
        {
            Message = message ?? throw new ArgumentNullException(nameof(message));
            Sequence = sequence;
            Droppable = message is HarborOutput || message is HarborActivity;
            Bytes = message is HarborOutput output ? 96 + ((long)(output.Data?.Length ?? 0) * 2) : 256;
        }

        #endregion
    }
}
