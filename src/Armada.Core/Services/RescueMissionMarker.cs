namespace Armada.Core.Services
{
    using System;
    using Armada.Core.Models;

    /// <summary>
    /// The single definition of how an autonomously dispatched rescue mission is recognized.
    /// </summary>
    /// <remarks>
    /// The marker literal previously appeared in more than one file. A rule with several copies
    /// stays consistent only for as long as nobody edits one of them, and a test that pins one
    /// copy reports the rule as enforced while the others drift. Every caller resolves the
    /// question here so there is one answer to change.
    /// </remarks>
    public static class RescueMissionMarker
    {
        #region Public-Members

        /// <summary>
        /// HTML comment embedded in a rescue mission's description. It is a comment so it renders
        /// as nothing in any brief the captain reads.
        /// </summary>
        public const string Marker = "<!-- ARMADA:AUTO-RESCUE -->";

        /// <summary>
        /// Title prefix carried by rescue missions created before the description marker existed.
        /// </summary>
        public const string LegacyTitlePrefix = "Rescue:";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Whether a mission was created by autonomous recovery.
        /// </summary>
        /// <param name="mission">Mission to test. Null is not a rescue.</param>
        /// <returns>True when the mission is an autonomous rescue.</returns>
        public static bool IsAutoRescue(Mission? mission)
        {
            if (mission == null) return false;
            return IsAutoRescue(mission.Description, mission.Title);
        }

        /// <summary>
        /// Whether a description and title identify an autonomous rescue.
        /// </summary>
        /// <param name="description">Mission description.</param>
        /// <param name="title">Mission title.</param>
        /// <returns>True when either carries the rescue signature.</returns>
        public static bool IsAutoRescue(string? description, string? title)
        {
            if ((description ?? String.Empty).Contains(Marker, StringComparison.Ordinal)) return true;
            return (title ?? String.Empty).StartsWith(LegacyTitlePrefix, StringComparison.OrdinalIgnoreCase);
        }

        #endregion
    }
}
