namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// The dimensions the change_quality decision reads over a focused diff. Two are backed by a
    /// deterministic rule and may hard-flag (<see cref="CognitiveComplexity"/> by a complexity metric,
    /// <see cref="CoreRule"/> by the deterministic Slop check); the rest are informational — the model
    /// may flag or propose them, but they never hard-gate on their own.
    /// </summary>
    public static class ChangeQualityDimensions
    {
        /// <summary>Duplicated logic the change introduces or repeats (informational; the duplication scan is its intended backing).</summary>
        public const string Dry = "dry";

        /// <summary>Cognitive complexity / bloat of the change (deterministically backed by a complexity metric).</summary>
        public const string CognitiveComplexity = "cognitive_complexity";

        /// <summary>Module boundaries and cohesion (informational).</summary>
        public const string Modularity = "modularity";

        /// <summary>Readability of the change (informational).</summary>
        public const string Readability = "readability";

        /// <summary>Maintainability of the change (informational).</summary>
        public const string Maintainability = "maintainability";

        /// <summary>A core-rule violation the deterministic Slop check found (deterministically backed).</summary>
        public const string CoreRule = "core_rule";

        /// <summary>The model dimensions asked as Nouls, in order.</summary>
        public static readonly IReadOnlyList<string> ModelDimensions = new List<string>
        {
            Dry, CognitiveComplexity, Modularity, Readability, Maintainability
        };

        /// <summary>Dimensions a deterministic rule can hard-flag; a model flag on any other dimension is informational only.</summary>
        public static readonly IReadOnlyCollection<string> DeterministicallyBacked = new HashSet<string>(StringComparer.Ordinal)
        {
            CognitiveComplexity, CoreRule
        };
    }

    /// <summary>Severity of a change-quality weakness.</summary>
    public enum ChangeQualitySeverity
    {
        /// <summary>A weak dimension worth noting; never routed to a follow-up on its own.</summary>
        ShouldFix,

        /// <summary>A dimension a deterministic rule hard-flags, or the model flags with high confidence; routable to a follow-up.</summary>
        MustFix
    }

    /// <summary>Where a change-quality weakness came from.</summary>
    public enum ChangeQualitySource
    {
        /// <summary>A deterministic rule (Slop, or the complexity metric) — authoritative.</summary>
        Rule,

        /// <summary>The model, as an informational signal the rule could not produce.</summary>
        Model
    }

    /// <summary>One weak dimension of a focused change.</summary>
    public sealed class ChangeQualityWeakness
    {
        /// <summary>The dimension, from <see cref="ChangeQualityDimensions"/>.</summary>
        public string Dimension { get; init; } = "";

        /// <summary>The severity.</summary>
        public ChangeQualitySeverity Severity { get; init; } = ChangeQualitySeverity.ShouldFix;

        /// <summary>Whether the deterministic rule or the model produced this weakness.</summary>
        public ChangeQualitySource Source { get; init; } = ChangeQualitySource.Model;

        /// <summary>A short, human-readable reason.</summary>
        public string Reason { get; init; } = "";
    }

    /// <summary>The change_quality decision input: a focused unified diff and its context.</summary>
    public sealed class ChangeQualityInput
    {
        /// <summary>The mission whose change is reviewed, for event owner scope. Null for an operator-supplied diff.</summary>
        public Mission? Mission { get; init; } = null;

        /// <summary>The id of the mission whose change is reviewed, when only the id is known; never part of the state.</summary>
        public string? MissionId { get; init; } = null;

        /// <summary>The vessel the change belongs to, for the egress vessel rule; never part of the state.</summary>
        public string? VesselId { get; init; } = null;

        /// <summary>The focused unified diff under review.</summary>
        public string UnifiedDiff { get; init; } = "";

        /// <summary>True when Central Package Management is enabled, for the Slop project rules.</summary>
        public bool CentralPackageManagement { get; init; } = false;
    }

    /// <summary>
    /// The change_quality verdict: the weak dimensions of a focused change. The rule verdict carries the
    /// deterministic weaknesses (complexity metric, Slop core-rule); a gated verdict adds the model's
    /// informational weaknesses. A deterministic MustFix is always present — the model may add weaknesses,
    /// never remove one the rule found.
    /// </summary>
    public sealed class ChangeQualityVerdict
    {
        /// <summary>The weak dimensions, rule-sourced first.</summary>
        public IReadOnlyList<ChangeQualityWeakness> Weaknesses { get; init; } = new List<ChangeQualityWeakness>();

        /// <summary>A short label for the recorded event.</summary>
        public string OutcomeLabel
        {
            get
            {
                if (Weaknesses.Count == 0) return "no_weakness";
                int mustFix = 0;
                foreach (ChangeQualityWeakness w in Weaknesses) if (w.Severity == ChangeQualitySeverity.MustFix) mustFix++;
                return "weak:" + Weaknesses.Count + (mustFix > 0 ? ",must_fix:" + mustFix : "");
            }
        }

        /// <summary>Weaknesses a deterministic rule hard-flagged (MustFix, Rule-sourced) — the routable, authoritative set.</summary>
        public IReadOnlyList<ChangeQualityWeakness> RoutableWeaknesses
        {
            get
            {
                List<ChangeQualityWeakness> routable = new List<ChangeQualityWeakness>();
                foreach (ChangeQualityWeakness w in Weaknesses)
                {
                    if (w.Severity == ChangeQualitySeverity.MustFix
                        && ChangeQualityDimensions.DeterministicallyBacked.Contains(w.Dimension))
                        routable.Add(w);
                }

                return routable;
            }
        }

        /// <summary>An empty verdict.</summary>
        public static ChangeQualityVerdict Empty() => new ChangeQualityVerdict();

        /// <summary>A verdict carrying weaknesses.</summary>
        public static ChangeQualityVerdict From(IReadOnlyList<ChangeQualityWeakness> weaknesses)
            => new ChangeQualityVerdict { Weaknesses = weaknesses ?? new List<ChangeQualityWeakness>() };
    }
}
