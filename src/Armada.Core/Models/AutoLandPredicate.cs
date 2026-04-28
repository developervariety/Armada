namespace Armada.Core.Models
{
    using System.Collections.Generic;

    /// <summary>
    /// Vessel-level configuration that gates auto-landing of merge-queue entries.
    /// When non-null and Enabled, MissionLandingHandler evaluates this against the
    /// captain branch's diff after EnqueueAsync; on Pass the entry is auto-processed.
    /// </summary>
    public sealed class AutoLandPredicate
    {
        /// <summary>Gets or sets a value indicating whether auto-land is active for this vessel.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>Gets or sets the maximum number of added lines permitted; null means uncapped.</summary>
        public int? MaxAddedLines { get; set; }

        /// <summary>Gets or sets the maximum number of changed files permitted; null means uncapped.</summary>
        public int? MaxFiles { get; set; }

        /// <summary>
        /// Gets or sets glob patterns that every changed path must match at least one of.
        /// Null or empty list means the allow-path rule is not enforced.
        /// </summary>
        public List<string>? AllowPaths { get; set; }

        /// <summary>
        /// Gets or sets glob patterns that no changed path may match.
        /// Null or empty list means the deny-path rule is not enforced.
        /// </summary>
        public List<string>? DenyPaths { get; set; }
    }

    /// <summary>Result of evaluating an <see cref="AutoLandPredicate"/> against a unified diff.</summary>
    public abstract record EvaluationResult
    {
        /// <summary>The predicate evaluation passed; auto-land may proceed.</summary>
        public sealed record Pass : EvaluationResult;

        /// <summary>The predicate evaluation failed; auto-land should not proceed.</summary>
        public sealed record Fail(string Reason) : EvaluationResult;
    }
}
