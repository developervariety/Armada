namespace Armada.Server.WebSocket
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// The ordered frames that activate one subscription: replayed events, any gap notices, the status snapshot,
    /// the snapshot catch-up events, and the closing <c>stream.ready</c>.
    /// </summary>
    internal sealed class WebSocketSubscriptionFrames
    {
        #region Public-Members

        /// <summary>
        /// Frames in send order.
        /// </summary>
        public IReadOnlyList<string> Frames { get; }

        /// <summary>
        /// Cursor of the <c>status.snapshot</c> frame: the position the following events continue from.
        /// </summary>
        public long SnapshotCursor { get; }

        /// <summary>
        /// Cursor of the <c>stream.ready</c> frame: the last event the subscription delivered or covered.
        /// </summary>
        public long ReadyCursor { get; }

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="frames">Frames in send order.</param>
        /// <param name="snapshotCursor">Cursor of the snapshot frame.</param>
        /// <param name="readyCursor">Cursor of the ready frame.</param>
        public WebSocketSubscriptionFrames(IReadOnlyList<string> frames, long snapshotCursor, long readyCursor)
        {
            Frames = frames ?? throw new ArgumentNullException(nameof(frames));
            SnapshotCursor = snapshotCursor;
            ReadyCursor = readyCursor;
        }

        #endregion
    }
}
