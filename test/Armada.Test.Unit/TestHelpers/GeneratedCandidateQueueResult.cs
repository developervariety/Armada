namespace Armada.Test.Unit.TestHelpers
{
    using Armada.Core.Models;

    internal sealed class GeneratedCandidateQueueResult
    {
        internal MergeEntry Entry { get; set; } = null!;
        internal string MainBefore { get; set; } = string.Empty;
        internal string MainAfter { get; set; } = string.Empty;
        internal string Tip { get; set; } = string.Empty;
        internal int PreparationCalls { get; set; }
    }
}
