namespace Armada.Core.Models
{
    using System.Collections.Generic;

    /// <summary>
    /// The blocking findings of a review that fit a character bound. Each item is a whole finding; a finding that
    /// does not fit is dropped whole and counted, never cut.
    /// </summary>
    public sealed class BlockingFindingSelection
    {
        /// <summary>Kept findings in document order, then unmet acceptance criteria.</summary>
        public List<string> Items { get; } = new List<string>();

        /// <summary>Number of findings dropped whole because the bound could not hold them.</summary>
        public int Omitted { get; set; } = 0;

        /// <summary>
        /// True when the review is a rejection from which no finding could be extracted, so the absence of items
        /// does not mean the review has none.
        /// </summary>
        public bool ExtractionMissed { get; set; } = false;
    }
}
