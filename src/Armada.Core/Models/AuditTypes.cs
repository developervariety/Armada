namespace Armada.Core.Models
{
    using System.Collections.Generic;

    /// <summary>Auto-land lane: Fast = no audit follow-up; Deferred = queued for deep review.</summary>
    public enum AuditLane
    {
        /// <summary>No audit follow-up required; entry is accepted as-is.</summary>
        Fast,

        /// <summary>Entry is queued for deep review by a code-reviewer subagent.</summary>
        Deferred
    }

    /// <summary>Single convention rule violation: which rule fired, on which '+' line of the diff.</summary>
    public sealed record ConventionViolation(string Rule, string Line);

    /// <summary>Convention check result over a unified diff.</summary>
    public sealed class ConventionCheckResult
    {
        /// <summary>Whether all convention rules passed.</summary>
        public bool Passed { get; set; } = true;

        /// <summary>List of violations found; empty when Passed is true.</summary>
        public List<ConventionViolation> Violations { get; set; } = new List<ConventionViolation>();
    }

    /// <summary>Critical-trigger evaluation result; triggered list is CSV-friendly.</summary>
    public sealed class CriticalTriggerResult
    {
        /// <summary>True if any criterion fired.</summary>
        public bool Fired { get; set; }

        /// <summary>Subset of {"path","content","convention","size"} that fired.</summary>
        public List<string> TriggeredCriteria { get; set; } = new List<string>();
    }
}
