namespace Armada.Core.Services
{
    using System;

    /// <summary>
    /// Detects a "no-op completion": a mission that exits cleanly almost immediately, produces no diff,
    /// and emits only a trivial/bare completion message. This is the signature of a runtime false-complete
    /// (the captain returned success without doing the work) rather than a real result. Pure and
    /// side-effect free so it can be unit tested in isolation. Deliberately conservative -- all three
    /// signals (empty diff, fast exit, trivial output) must hold, so a mission that genuinely investigated
    /// and found nothing to change (which takes real time and usually explains itself) is not flagged.
    /// </summary>
    public static class NoOpCompletionDetector
    {
        #region Public-Members

        /// <summary>
        /// Default upper bound (milliseconds) on how long a run can take and still count as a fast
        /// false-complete. A genuine "nothing to do" investigation typically exceeds this.
        /// </summary>
        public const long DefaultMaxRuntimeMs = 30000;

        /// <summary>
        /// Default upper bound on the non-whitespace length of the agent output for it to count as trivial.
        /// </summary>
        public const int DefaultMaxOutputChars = 400;

        #endregion

        #region Public-Methods

        /// <summary>
        /// Whether the completion looks like a no-op false-complete.
        /// </summary>
        /// <param name="diffSnapshot">The captured git diff for the mission (null/empty = no changes).</param>
        /// <param name="agentOutput">The captain's accumulated stdout.</param>
        /// <param name="totalRuntimeMs">Measured run duration in ms, or null if unknown.</param>
        /// <param name="maxRuntimeMs">Fast-exit threshold; runs longer than this are never flagged.</param>
        /// <param name="maxOutputChars">Trivial-output threshold on the non-whitespace output length.</param>
        /// <returns>True if all three no-op signals hold; otherwise false.</returns>
        public static bool IsNoOp(
            string? diffSnapshot,
            string? agentOutput,
            long? totalRuntimeMs,
            long maxRuntimeMs = DefaultMaxRuntimeMs,
            int maxOutputChars = DefaultMaxOutputChars)
        {
            // Signal 1: no changes produced.
            if (!String.IsNullOrWhiteSpace(diffSnapshot))
                return false;

            // Signal 2: fast exit. Unknown duration is treated as "not fast" so we never flag on a guess.
            if (totalRuntimeMs == null || totalRuntimeMs.Value > maxRuntimeMs)
                return false;

            // Signal 3: only a trivial/bare completion message. A real "nothing to do" result usually
            // explains itself at length.
            int meaningfulLength = CountNonWhitespace(agentOutput);
            if (meaningfulLength > maxOutputChars)
                return false;

            return true;
        }

        #endregion

        #region Private-Methods

        private static int CountNonWhitespace(string? value)
        {
            if (String.IsNullOrEmpty(value)) return 0;
            int count = 0;
            foreach (char c in value)
            {
                if (!Char.IsWhiteSpace(c)) count++;
            }
            return count;
        }

        #endregion
    }
}
