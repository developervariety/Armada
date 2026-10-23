namespace Armada.Server.WebSocket
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;

    /// <summary>
    /// History one subscription cannot deliver: the reason, the lost cursor range and the time bounds a client
    /// uses to re-read what changed through the persisted events.
    /// </summary>
    internal sealed class WebSocketGapNotice
    {
        #region Public-Members

        /// <summary>
        /// Stable gap reason from <see cref="WebSocketReplayGapReasons"/>.
        /// </summary>
        public string Reason { get; }

        /// <summary>
        /// First lost cursor, or null when the range is unknown.
        /// </summary>
        public long? LostFromCursor { get; }

        /// <summary>
        /// Last lost cursor, or null when the range is unknown.
        /// </summary>
        public long? LostToCursor { get; }

        /// <summary>
        /// The lost events were appended after this UTC time; null means after the client's last received event.
        /// </summary>
        public DateTime? LostAfterUtc { get; }

        /// <summary>
        /// The lost events were appended before this UTC time; null when unknown.
        /// </summary>
        public DateTime? LostBeforeUtc { get; }

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="reason">Stable gap reason.</param>
        /// <param name="lostFromCursor">First lost cursor, or null when unknown.</param>
        /// <param name="lostToCursor">Last lost cursor, or null when unknown.</param>
        /// <param name="lostAfterUtc">Lower time bound, or null for the client's last received event.</param>
        /// <param name="lostBeforeUtc">Upper time bound, or null when unknown.</param>
        public WebSocketGapNotice(string reason, long? lostFromCursor, long? lostToCursor, DateTime? lostAfterUtc, DateTime? lostBeforeUtc)
        {
            if (String.IsNullOrWhiteSpace(reason)) throw new ArgumentNullException(nameof(reason));
            Reason = reason;
            LostFromCursor = lostFromCursor;
            LostToCursor = lostToCursor;
            LostAfterUtc = lostAfterUtc;
            LostBeforeUtc = lostBeforeUtc;
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// REST query of the persisted events in the lost time window, or null when no upper bound is known. A
        /// client without a lower bound adds <c>createdAfter</c> from its last received event.
        /// </summary>
        /// <returns>Relative REST path and query, or null.</returns>
        public string? EventsQuery()
        {
            if (!LostBeforeUtc.HasValue) return null;
            List<string> parts = new List<string>();
            if (LostAfterUtc.HasValue) parts.Add("createdAfter=" + Uri.EscapeDataString(LostAfterUtc.Value.ToString("o", CultureInfo.InvariantCulture)));
            parts.Add("createdBefore=" + Uri.EscapeDataString(LostBeforeUtc.Value.ToString("o", CultureInfo.InvariantCulture)));
            return "/api/v1/events?" + String.Join("&", parts);
        }

        #endregion
    }
}
