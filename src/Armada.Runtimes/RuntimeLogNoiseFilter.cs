namespace Armada.Runtimes
{
    /// <summary>
    /// Removes known low-value structured-runtime lifecycle records from displayed mission logs.
    /// </summary>
    public static class RuntimeLogNoiseFilter
    {
        /// <summary>
        /// Envelope-only activity records: a runtime event type with no tool name, argument, or
        /// outcome. Claude Code and Codex logs written before named tool activity landed are full
        /// of these ("claude assistant", "claude user", ...), so they are filtered on read as well
        /// as suppressed at the source.
        /// </summary>
        private static readonly HashSet<string> _EnvelopeOnlyRecords = new HashSet<string>(StringComparer.Ordinal)
        {
            "[ARMADA:ACTIVITY] claude assistant",
            "[ARMADA:ACTIVITY] claude user",
            "[ARMADA:ACTIVITY] claude system",
            "[ARMADA:ACTIVITY] claude result",
            "[ARMADA:ACTIVITY] claude stream event",
            "[ARMADA:ACTIVITY] codex item completed",
            "[ARMADA:ACTIVITY] codex item started",
            "[ARMADA:ACTIVITY] codex item updated",
            "[ARMADA:ACTIVITY] codex thread started",
            "[ARMADA:ACTIVITY] codex turn started",
            "[ARMADA:ACTIVITY] codex turn completed"
        };

        /// <summary>
        /// Filter synthetic lifecycle records while preserving assistant text, protocol markers,
        /// named tool activity, process lifecycle, and validation output.
        /// </summary>
        public static string[] Filter(IEnumerable<string> lines)
        {
            if (lines == null)
                return Array.Empty<string>();

            return lines.Where(line => !IsNoise(line)).ToArray();
        }

        private static bool IsNoise(string line)
        {
            if (String.IsNullOrEmpty(line))
                return false;

            if (String.Equals(line, "[ARMADA:ACTIVITY] step started", StringComparison.Ordinal)
                || String.Equals(line, "[ARMADA:ACTIVITY] step finished", StringComparison.Ordinal))
            {
                return true;
            }

            if (_EnvelopeOnlyRecords.Contains(line.TrimEnd()))
            {
                return true;
            }

            // Claude Code session bookkeeping. The exact-match set above cannot catch these: the
            // runtime appended the event subtype, so every line was distinct
            // ("claude system thinking tokens" x20+ in a single mission). The runtime no longer
            // writes them, but existing logs are full of them and are still read.
            if (line.StartsWith("[ARMADA:ACTIVITY] claude system ", StringComparison.Ordinal))
            {
                return true;
            }

            if (line.StartsWith("[ARMADA:ACTIVITY] cursor ", StringComparison.Ordinal)
                || line.StartsWith("[ARMADA:ACTIVITY] gemini ", StringComparison.Ordinal)
                || line.StartsWith("[ARMADA:ACTIVITY] mux ", StringComparison.Ordinal))
            {
                return true;
            }

            return String.Equals(line, "[ARMADA:ACTIVITY] tool call", StringComparison.Ordinal)
                || String.Equals(line, "[ARMADA:ACTIVITY] tool-call", StringComparison.Ordinal)
                || String.Equals(line, "[ARMADA:ACTIVITY] tool result", StringComparison.Ordinal)
                || String.Equals(line, "[ARMADA:ACTIVITY] tool-result", StringComparison.Ordinal);
        }
    }
}
