namespace Armada.Core.Services
{
    using System;

    /// <summary>
    /// Raised when a dispatch resolves, or a write would point at, a pipeline the operator has deactivated.
    /// An inactive pipeline is retired: it is never used, and no other pipeline is substituted for it, so the
    /// caller refuses by name and the operator either reactivates it or names an active pipeline.
    /// </summary>
    public class PipelineInactiveException : InvalidOperationException
    {
        #region Public-Members

        /// <summary>
        /// The reason code every surface reports when an inactive pipeline is refused.
        /// </summary>
        public const string ReasonCode = "pipeline_inactive";

        /// <summary>
        /// Identifier of the inactive pipeline.
        /// </summary>
        public string PipelineId { get; }

        /// <summary>
        /// Name of the inactive pipeline.
        /// </summary>
        public string PipelineName { get; }

        /// <summary>
        /// Where the reference came from, for example "requested pipeline", "vessel default pipeline" or
        /// "fleet default pipeline".
        /// </summary>
        public string Source { get; }

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="pipelineId">Identifier of the inactive pipeline.</param>
        /// <param name="pipelineName">Name of the inactive pipeline.</param>
        /// <param name="source">Where the reference came from.</param>
        public PipelineInactiveException(string pipelineId, string pipelineName, string source)
            : base("The " + source + " " + pipelineName + " (" + pipelineId + ") is inactive. "
                + "Reactivate it (active=true) or name an active pipeline.")
        {
            PipelineId = pipelineId ?? String.Empty;
            PipelineName = pipelineName ?? String.Empty;
            Source = source ?? String.Empty;
        }

        #endregion
    }
}
