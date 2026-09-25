namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// The <c>memory_relevance</c> model reading. Per leaf the model answers one Noul, whether the leaf
    /// applies to the mission's work. The gate confidence is the strongest "does not apply" across the
    /// leaves; the leaves at or above the gate threshold are the ones a gated verdict moves.
    /// </summary>
    public sealed class MemoryRelevanceReading : TypedModelReading
    {
        private readonly double _Confidence;
        private readonly string _Label;

        /// <summary>Zero-based indexes of the leaves the model is confident do not apply.</summary>
        public IReadOnlyList<int> ReferenceIndexes { get; }

        /// <summary>Create a reading.</summary>
        /// <param name="confidence">The strongest "does not apply" across the leaves.</param>
        /// <param name="referenceIndexes">Zero-based indexes of the leaves to move to reference.</param>
        /// <param name="label">The short verdict label for the event message.</param>
        public MemoryRelevanceReading(double confidence, IReadOnlyList<int> referenceIndexes, string label)
        {
            _Confidence = confidence;
            ReferenceIndexes = referenceIndexes ?? new List<int>();
            _Label = label ?? String.Empty;
        }

        /// <inheritdoc />
        public override double Confidence => _Confidence;

        /// <inheritdoc />
        public override string Label => _Label;
    }
}
