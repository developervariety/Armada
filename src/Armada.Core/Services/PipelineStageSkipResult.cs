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
        /// Entries of an objective's stored default skip that name no stage of the chosen pipeline, so
        /// they were dropped instead of refusing the dispatch. Empty for a skip the request named.
        /// </summary>
        public IReadOnlyList<DroppedStageSkip> DroppedDefaults { get; }

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="pipeline">Pipeline to materialise.</param>
        /// <param name="skippedStages">Dropped stages.</param>
        /// <param name="droppedDefaults">Stored default entries that name no stage of the pipeline, or null.</param>
        public PipelineStageSkipResult(
            Pipeline? pipeline,
            IReadOnlyList<PipelineStage> skippedStages,
            IReadOnlyList<DroppedStageSkip>? droppedDefaults = null)
        {
            Pipeline = pipeline;
            SkippedStages = skippedStages ?? new List<PipelineStage>();
            DroppedDefaults = droppedDefaults ?? new List<DroppedStageSkip>();
        }
    }
}
