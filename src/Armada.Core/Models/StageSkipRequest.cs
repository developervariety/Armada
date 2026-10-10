namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// A request to drop named pipeline stages when a voyage is materialised. Stored objective skips
    /// require server-issued operator confirmation; immediate dispatch uses the authenticated operator
    /// builder. Nothing infers a skip from the stage-necessity classifier. The Judge can never be skipped.
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
        /// Server-recorded operator identity for a stored objective skip. Request values are ignored.
        /// </summary>
        public string? ConfirmedBy { get; set; } = null;

        /// <summary>
        /// Server-recorded confirmation time for a stored objective skip, or null when not recorded.
        /// </summary>
        public DateTime? ConfirmedUtc { get; set; } = null;

        /// <summary>
        /// Server-issued proof that an authenticated operator confirmed this stored skip. Request values
        /// are never trusted; the objective service issues this identifier only when confirmStageSkip is set.
        /// Older records have no identifier and require explicit reconfirmation.
        /// </summary>
        public string? OperatorConfirmationId { get; set; } = null;

        /// <summary>
        /// True when the request names at least one stage.
        /// </summary>
        /// <param name="request">Request, or null.</param>
        /// <returns>True when there is something to skip.</returns>
        public static bool HasStages(StageSkipRequest? request)
        {
            return request != null && request.Stages != null && request.Stages.Count > 0;
        }

        /// <summary>
        /// True when this request carries the complete server-issued confirmation proof required for a
        /// stored objective skip. Immediate dispatch builders also issue this proof server-side.
        /// </summary>
        /// <param name="request">Skip request, or null.</param>
        /// <returns>True when confirmation metadata and server-issued proof are present.</returns>
        public static bool HasTrustedConfirmation(StageSkipRequest? request)
        {
            return HasStages(request)
                && !String.IsNullOrWhiteSpace(request!.ConfirmedBy)
                && request.ConfirmedUtc.HasValue
                && !String.IsNullOrWhiteSpace(request.OperatorConfirmationId);
        }
    }
}
