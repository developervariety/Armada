namespace Armada.Core.Services.Interfaces
{
    using Armada.Core.Models;

    /// <summary>
    /// Pure classifier that maps a <see cref="MergeFailureContext"/> captured at fail-time
    /// into a structured <see cref="MergeFailureClassification"/>. Implementations must be
    /// side-effect free.
    /// </summary>
    public interface IMergeFailureClassifier
    {
        /// <summary>
        /// Classifies the supplied failure context.
        /// </summary>
        /// <param name="context">Failure-time signals captured by the merge-queue service.</param>
        /// <returns>The classified failure shape, summary, and conflicted-file list.</returns>
        MergeFailureClassification Classify(MergeFailureContext context);
    }
}
