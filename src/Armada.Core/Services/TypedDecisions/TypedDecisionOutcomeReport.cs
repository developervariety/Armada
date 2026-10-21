namespace Armada.Core.Services.TypedDecisions
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// The outcome join over the recorded typed-decision calls in one window, one row per decision.
    /// </summary>
    public sealed class TypedDecisionOutcomeReport
    {
        /// <summary>Start of the window, UTC.</summary>
        public DateTime SinceUtc { get; set; }

        /// <summary>Calls read. Equals <see cref="MaxEvents"/> when the window held more and the read stopped.</summary>
        public int EventsRead { get; set; } = 0;

        /// <summary>The most calls one report reads.</summary>
        public int MaxEvents { get; set; } = 0;

        /// <summary>True when the window held more calls than <see cref="MaxEvents"/>, so the counts are a newest-first sample.</summary>
        public bool Truncated { get; set; } = false;

        /// <summary>One row per decision, by name.</summary>
        public List<TypedDecisionOutcomeRow> Decisions { get; set; } = new List<TypedDecisionOutcomeRow>();
    }
}
