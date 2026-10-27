namespace Armada.Core.Models
{
    using Armada.Core.Enums;

    /// <summary>
    /// Request to start an Evaluate of a captain or a model endpoint. Exactly one of <see cref="CaptainId"/> and
    /// <see cref="ModelEndpointId"/> is set.
    /// </summary>
    public class EvaluateCaptainRequest
    {
        #region Public-Members

        /// <summary>
        /// Captain to evaluate through its own runtime, or null.
        /// </summary>
        public string? CaptainId { get; set; } = null;

        /// <summary>
        /// Model endpoint to evaluate through the API endpoint runtime, or null.
        /// </summary>
        public string? ModelEndpointId { get; set; } = null;

        /// <summary>
        /// Depth; default Quick. Full is rejected until Phase 5.
        /// </summary>
        public ModelProbeDepthEnum Depth { get; set; } = ModelProbeDepthEnum.Quick;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public EvaluateCaptainRequest()
        {
        }

        #endregion
    }
}
