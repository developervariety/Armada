namespace Armada.Core.Models
{
    /// <summary>
    /// The estimated cost of one typed-decision call. Either <see cref="EstimatedCostUsd"/> holds a value,
    /// or it is null and <see cref="UnavailableReason"/> names why; a missing price is never recorded as zero.
    /// </summary>
    public class TypedDecisionCostEstimate
    {
        #region Public-Members

        /// <summary>
        /// Estimated cost in US dollars of this call's share of its provider request, or null.
        /// </summary>
        public double? EstimatedCostUsd { get; set; } = null;

        /// <summary>
        /// Why there is no estimate, or null when there is one.
        /// </summary>
        public string? UnavailableReason { get; set; } = null;

        /// <summary>
        /// Version label of the price table the estimate was made from.
        /// </summary>
        public string PriceTableVersion { get; set; } = "unversioned";

        #endregion
    }
}
