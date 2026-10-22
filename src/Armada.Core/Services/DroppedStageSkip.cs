namespace Armada.Core.Services
{
    /// <summary>
    /// One entry of an objective's stored default stage skip that names no stage of the chosen pipeline.
    /// The dispatch drops it and reports it; it never refuses the dispatch.
    /// </summary>
    public sealed class DroppedStageSkip
    {
        /// <summary>The stored skip entry, for example <c>Recorder</c>.</summary>
        public string Entry { get; }

        /// <summary>Why the entry names no stage, naming the pipeline and its stages.</summary>
        public string Reason { get; }

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="entry">The stored skip entry.</param>
        /// <param name="reason">Why the entry names no stage of the pipeline.</param>
        public DroppedStageSkip(string entry, string reason)
        {
            Entry = entry ?? "";
            Reason = reason ?? "";
        }
    }
}
