namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// An operator-confirmed request to drop named pipeline stages when a voyage is materialised.
    /// A skip is only ever taken on an operator's confirmation: nothing infers one from the
    /// stage-necessity classifier. The Judge can never be skipped.
    /// </summary>
    public class StageSkipRequest
    {
        /// <summary>
        /// Persona names of the stages to drop, for example <c>TestEngineer</c>.
        /// </summary>
        public List<string> Stages { get; set; } = new List<string>();

        /// <summary>
        /// Why the stages are not needed. Recorded on each <c>voyage.stage_skipped</c> event.
        /// </summary>
        public string? Reason { get; set; } = null;

        /// <summary>
        /// Who confirmed the skip. The autonomous scheduler honours a stored skip only when this is set.
        /// </summary>
        public string? ConfirmedBy { get; set; } = null;

        /// <summary>
        /// When the skip was confirmed, or null when not recorded.
        /// </summary>
        public DateTime? ConfirmedUtc { get; set; } = null;

        /// <summary>
        /// True when the request names at least one stage.
        /// </summary>
        /// <param name="request">Request, or null.</param>
        /// <returns>True when there is something to skip.</returns>
        public static bool HasStages(StageSkipRequest? request)
        {
            return request != null && request.Stages != null && request.Stages.Count > 0;
        }
    }
}
