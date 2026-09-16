namespace Armada.Core.Context
{
    /// <summary>
    /// Byte and count accounting for one slimmed AI-Memory brief section, recorded on the mission's
    /// prompt-budget telemetry so the before/after cost of the slimmed path is measurable per persona.
    ///
    /// A value is produced only on the slimming path (the flag on). When the path falls back to the
    /// full memory section, <see cref="FellBack"/> is true and the byte fields describe the retrieval
    /// result that was skipped (all zero when there was none), so the fallback is visible in telemetry
    /// rather than silent.
    /// </summary>
    public sealed class ContextBriefTelemetry
    {
        /// <summary>True when the slimming flag was on for this brief.</summary>
        public bool Enabled { get; set; }

        /// <summary>True when the slimming path fell back to the full memory section (a fail-safe).</summary>
        public bool FellBack { get; set; }

        /// <summary>Short reason for a fallback, or null when the slimmed section shipped.</summary>
        public string? FallbackReason { get; set; }

        /// <summary>True when retrieval returned a degraded (fail-safe) result.</summary>
        public bool Degraded { get; set; }

        /// <summary>UTF-8 bytes of the always-on core chunks shipped inline.</summary>
        public int CoreBytes { get; set; }

        /// <summary>Count of always-on core chunks shipped inline.</summary>
        public int CoreCount { get; set; }

        /// <summary>UTF-8 bytes of the matching-domain must-retrieve safety leaves shipped inline.</summary>
        public int MustRetrieveBytes { get; set; }

        /// <summary>Count of matching-domain must-retrieve safety leaves shipped inline.</summary>
        public int MustRetrieveCount { get; set; }

        /// <summary>UTF-8 bytes of the ranked, retrieved leaves shipped inline.</summary>
        public int LeafBytes { get; set; }

        /// <summary>Count of ranked, retrieved leaves shipped inline.</summary>
        public int LeafCount { get; set; }

        /// <summary>UTF-8 bytes of the whole rendered section, whichever path produced it.</summary>
        public int SectionBytes { get; set; }
    }
}
