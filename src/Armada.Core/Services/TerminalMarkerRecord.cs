namespace Armada.Core.Services
{
    using System;

    /// <summary>
    /// The first terminal marker a mission's captain emitted.
    /// </summary>
    public sealed class TerminalMarkerRecord
    {
        #region Public-Members

        /// <summary>
        /// Mission identifier.
        /// </summary>
        public string MissionId { get; }

        /// <summary>
        /// Marker type as parsed: <c>verdict</c> or <c>result</c>.
        /// </summary>
        public string MarkerType { get; }

        /// <summary>
        /// Marker value, for example <c>PASS</c> or <c>COMPLETE</c>.
        /// </summary>
        public string Value { get; }

        /// <summary>
        /// UTC instant the marker was first seen in the streamed output.
        /// </summary>
        public DateTime FirstSeenUtc { get; }

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="missionId">Mission identifier.</param>
        /// <param name="markerType">Marker type.</param>
        /// <param name="value">Marker value.</param>
        /// <param name="firstSeenUtc">UTC instant the marker was first seen.</param>
        public TerminalMarkerRecord(string missionId, string markerType, string value, DateTime firstSeenUtc)
        {
            MissionId = missionId ?? throw new ArgumentNullException(nameof(missionId));
            MarkerType = markerType ?? throw new ArgumentNullException(nameof(markerType));
            Value = value ?? throw new ArgumentNullException(nameof(value));
            FirstSeenUtc = firstSeenUtc;
        }

        #endregion
    }
}
