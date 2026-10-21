namespace Armada.Core.Services
{
    using System;
    using Armada.Core.Models;

    /// <summary>
    /// The one rule every landing entry point consults before it merges, pushes or enqueues a mission:
    /// a mission whose deliverable is a ruling or a report, not a change, is never landed.
    /// </summary>
    /// <remarks>
    /// A Challenger starts from the commit a Judge blocked and only rules on that Judge's findings, so
    /// landing it would land the blocked work. A read-only (Audit or Research) mission dispatched outside
    /// a voyage has no stage that could own a change, so its branch is never a deliverable either. Inside a
    /// voyage a read-only mission is left to the pipeline, whose final stage owns the landing decision.
    /// Completion of a refused mission records its result only.
    /// </remarks>
    public static class MissionLandingEligibility
    {
        #region Public-Members

        /// <summary>
        /// Event recorded when a completed mission is not landed because this rule refuses it.
        /// </summary>
        public const string SkippedEvent = "mission.landing_skipped";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Why a mission must never be landed, or null when it may be.
        /// </summary>
        /// <param name="mission">Mission about to be landed, merged or enqueued.</param>
        /// <returns>The refusal reason, or null.</returns>
        public static string? RefusalReason(Mission? mission)
        {
            if (mission == null) return null;

            if (PersonaCatalog.Matches(mission.Persona, PersonaCatalog.Challenger))
                return "a Challenger mission records rulings on a Judge's findings and never lands the work it reviews";

            if (String.IsNullOrEmpty(mission.VoyageId) && mission.IsReadOnlyMode)
                return "a standalone " + mission.Mode + " mission records a report and never lands";

            return null;
        }

        #endregion
    }
}
