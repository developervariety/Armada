namespace Armada.Runtimes
{
    /// <summary>
    /// Removes known low-value structured-runtime lifecycle records from displayed mission logs.
    /// </summary>
    public static class RuntimeLogNoiseFilter
    {
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
