namespace Armada.Core.Services
{
    using System.Collections.Generic;
    using Armada.Core.Models;

    /// <summary>
    /// The outcome of applying a stage skip to a pipeline.
    /// </summary>
    public sealed class PipelineStageSkipResult
    {
        /// <summary>The pipeline to materialise, or null for a single-stage Worker dispatch with no skip.</summary>
        public Pipeline? Pipeline { get; }

        /// <summary>The stages that were dropped, in pipeline order.</summary>
        public IReadOnlyList<PipelineStage> SkippedStages { get; }

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="pipeline">Pipeline to materialise.</param>
        /// <param name="skippedStages">Dropped stages.</param>
        public PipelineStageSkipResult(Pipeline? pipeline, IReadOnlyList<PipelineStage> skippedStages)
        {
            Pipeline = pipeline;
            SkippedStages = skippedStages ?? new List<PipelineStage>();
        }
    }
}
