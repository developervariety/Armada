namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Models;

    /// <summary>
    /// The authoritative deterministic backing for the change_quality decision. Two dimensions are
    /// judged without the model: <see cref="ChangeQualityDimensions.CoreRule"/> from the Slop check's
    /// FAIL findings, and <see cref="ChangeQualityDimensions.CognitiveComplexity"/> from a metric over
    /// the added lines (deepest brace nesting and the longest unbroken run of added lines). These are the
    /// verdicts the model may add to but never remove.
    /// </summary>
    public static class ChangeQualityRules
    {
        #region Public-Members

        /// <summary>Added-line brace nesting at or above which the complexity dimension hard-flags.</summary>
        public const int ComplexityNestingThreshold = 7;

        /// <summary>An unbroken run of added lines at or above which the complexity dimension hard-flags.</summary>
        public const int ComplexityAddedRunThreshold = 120;

        #endregion

        #region Public-Methods

        /// <summary>
        /// Evaluate the deterministic weaknesses of a focused diff.
        /// </summary>
        /// <param name="unifiedDiff">The unified diff under review.</param>
        /// <param name="centralPackageManagement">Whether CPM is enabled, for the Slop project rules.</param>
        /// <returns>The deterministic weaknesses; empty when the change is clean.</returns>
        public static IReadOnlyList<ChangeQualityWeakness> Evaluate(string? unifiedDiff, bool centralPackageManagement)
        {
            List<ChangeQualityWeakness> weaknesses = new List<ChangeQualityWeakness>();
            if (String.IsNullOrEmpty(unifiedDiff)) return weaknesses;

            SlopClassificationResult slop = SlopDiffClassifier.Classify(unifiedDiff, centralPackageManagement);
            if (slop.FailCount > 0)
            {
                weaknesses.Add(new ChangeQualityWeakness
                {
                    Dimension = ChangeQualityDimensions.CoreRule,
                    Severity = ChangeQualitySeverity.MustFix,
                    Source = ChangeQualitySource.Rule,
                    Reason = slop.FailCount + " core-rule (slop) FAIL finding" + (slop.FailCount == 1 ? "" : "s") + " in the change"
                });
            }

            ComplexityMetric metric = MeasureComplexity(unifiedDiff);
            if (metric.MaxAddedNesting >= ComplexityNestingThreshold || metric.LongestAddedRun >= ComplexityAddedRunThreshold)
            {
                weaknesses.Add(new ChangeQualityWeakness
                {
                    Dimension = ChangeQualityDimensions.CognitiveComplexity,
                    Severity = ChangeQualitySeverity.MustFix,
                    Source = ChangeQualitySource.Rule,
                    Reason = "added code reaches brace depth " + metric.MaxAddedNesting + " and a run of " + metric.LongestAddedRun + " added lines"
                });
            }

            return weaknesses;
        }

        /// <summary>
        /// Measure the added-line complexity of a unified diff: the deepest brace nesting reached on an
        /// added line and the longest unbroken run of added lines. Only added ('+') content counts; diff
        /// headers and removed lines are ignored.
        /// </summary>
        /// <param name="unifiedDiff">The unified diff.</param>
        /// <returns>The metric.</returns>
        public static ComplexityMetric MeasureComplexity(string? unifiedDiff)
        {
            ComplexityMetric metric = new ComplexityMetric();
            if (String.IsNullOrEmpty(unifiedDiff)) return metric;

            int nesting = 0;
            int run = 0;
            foreach (string raw in unifiedDiff.Replace("\r\n", "\n").Split('\n'))
            {
                if (raw.StartsWith("+++", StringComparison.Ordinal) || raw.StartsWith("---", StringComparison.Ordinal)
                    || raw.StartsWith("@@", StringComparison.Ordinal) || raw.StartsWith("diff ", StringComparison.Ordinal))
                {
                    nesting = 0; run = 0; continue;
                }

                if (!raw.StartsWith("+", StringComparison.Ordinal))
                {
                    run = 0;
                    continue;
                }

                run++;
                if (run > metric.LongestAddedRun) metric.LongestAddedRun = run;

                foreach (char c in raw)
                {
                    if (c == '{') { nesting++; if (nesting > metric.MaxAddedNesting) metric.MaxAddedNesting = nesting; }
                    else if (c == '}') { if (nesting > 0) nesting--; }
                }
            }

            return metric;
        }

        #endregion

        #region Public-Classes

        /// <summary>The added-line complexity metric of a diff.</summary>
        public sealed class ComplexityMetric
        {
            /// <summary>Deepest brace nesting reached on an added line.</summary>
            public int MaxAddedNesting { get; set; } = 0;

            /// <summary>Longest unbroken run of added lines.</summary>
            public int LongestAddedRun { get; set; } = 0;
        }

        #endregion
    }
}
