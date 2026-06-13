namespace Armada.Core.Models
{
    using Armada.Core.Enums;

    /// <summary>
    /// Result of recording a captain runtime exit against crash-loop detection rules.
    /// </summary>
    public sealed class CaptainHealthDecision
    {
        /// <summary>
        /// Whether the caller should bench this captain based on consecutive near-instant failures.
        /// </summary>
        public bool ShouldBench { get; set; }

        /// <summary>
        /// Current consecutive near-instant exit-1 count after this exit was processed.
        /// </summary>
        public int ConsecutiveInstantFailures { get; set; }

        /// <summary>
        /// Human-readable reason when <see cref="ShouldBench"/> is true; empty otherwise.
        /// </summary>
        public string Reason { get; set; } = string.Empty;

        /// <summary>
        /// Runtime that produced this exit.
        /// </summary>
        public AgentRuntimeEnum Runtime { get; set; }
    }
}
