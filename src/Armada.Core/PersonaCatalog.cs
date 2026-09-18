namespace Armada.Core
{
    using System;

    /// <summary>
    /// Canonical built-in persona names and compatibility helpers.
    /// </summary>
    public static class PersonaCatalog
    {
        /// <summary>
        /// Worker persona name.
        /// </summary>
        public const string Worker = "Worker";

        /// <summary>
        /// Architect persona name.
        /// </summary>
        public const string Architect = "Architect";

        /// <summary>
        /// Product manager persona name.
        /// </summary>
        public const string ProductManager = "Product Manager";

        /// <summary>
        /// Usability engineer persona name.
        /// </summary>
        public const string UsabilityEngineer = "Usability Engineer";

        /// <summary>
        /// Canonical test engineer persona name.
        /// </summary>
        public const string TestEngineer = "Test Engineer";

        /// <summary>
        /// Legacy test engineer persona name used by older builds.
        /// </summary>
        public const string LegacyTestEngineer = "TestEngineer";

        /// <summary>
        /// Judge persona name.
        /// </summary>
        public const string Judge = "Judge";

        /// <summary>
        /// Linter persona name. Evaluates the code and documentation a mission changed for style and
        /// correctness, fixes clear in-scope violations, and reports what it found and fixed.
        /// </summary>
        public const string Linter = "Linter";

        /// <summary>
        /// Recorder persona name. Reviews the finished work of a voyage and records what is worth
        /// remembering into native captain memory.
        /// </summary>
        public const string Recorder = "Recorder";

        /// <summary>
        /// Read-only research persona that settles whether an objective's deliverable already exists
        /// when the D26 prior-art reading is uncertain. It commits nothing and joins no built-in pipeline.
        /// </summary>
        public const string PriorArtAnalyst = "PriorArtAnalyst";

        /// <summary>
        /// Whether a persona cannot do its job without running commands in its dock.
        /// </summary>
        /// <remarks>
        /// A Worker builds and commits, a Test Engineer runs the suite it writes, a Judge reads the diff
        /// and runs the vessel's gate before it votes, and a Linter runs the checks it reports on. A
        /// runtime that offers file tools only can occupy one of these seats and produce a confident,
        /// unfounded result: an API-endpoint Judge reported "I have no shell/execution tool in this
        /// session" and reviewed from file reads alone. The analysis personas are absent from this list on
        /// purpose, because reading and writing files is the whole of their work.
        ///
        /// This is the ONE definition. A caller asks it rather than re-deriving the list, so a persona
        /// added later is classified in one place.
        /// </remarks>
        /// <param name="persona">Persona name, canonical or legacy.</param>
        /// <returns>True when the persona needs to run commands.</returns>
        public static bool RequiresCommandExecution(string? persona)
        {
            string name = NormalizeName(persona);
            if (String.IsNullOrEmpty(name)) return false;

            return Matches(name, Worker)
                || Matches(name, TestEngineer)
                || Matches(name, Judge)
                || Matches(name, Linter);
        }

        /// <summary>
        /// Normalize a persona name to the canonical built-in display name when applicable.
        /// </summary>
        /// <param name="persona">Persona name.</param>
        /// <returns>Canonical persona name, or the trimmed original name for unknown personas.</returns>
        public static string NormalizeName(string? persona)
        {
            if (String.IsNullOrWhiteSpace(persona)) return String.Empty;

            string trimmed = persona.Trim();
            if (String.Equals(trimmed, LegacyTestEngineer, StringComparison.OrdinalIgnoreCase))
                return TestEngineer;

            return trimmed;
        }

        /// <summary>
        /// Compare two persona names using canonical normalization.
        /// </summary>
        /// <param name="left">First persona name.</param>
        /// <param name="right">Second persona name.</param>
        /// <returns>True if both names refer to the same persona.</returns>
        public static bool Matches(string? left, string? right)
        {
            string normalizedLeft = NormalizeName(left);
            string normalizedRight = NormalizeName(right);

            if (String.IsNullOrEmpty(normalizedLeft) || String.IsNullOrEmpty(normalizedRight))
                return false;

            return String.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Whether a persona's successful completion legitimately produces no repository commit and
        /// can finish quickly, so the no-op completion gate must not read its empty diff as a
        /// false-complete. The Architect delivers a downstream mission plan; the Recorder writes
        /// native memory; the PriorArtAnalyst writes its finding into its report. Each delivers its
        /// result outside the repository diff, so an empty diff is
        /// their normal outcome whatever the voyage mission mode. Reviewer personas are judged by
        /// their own output instead and are deliberately not listed here.
        /// </summary>
        /// <param name="persona">Persona name.</param>
        /// <returns>True when an empty diff is a legitimate completion for this persona.</returns>
        public static bool IsNoOpCompletionExempt(string? persona)
        {
            return Matches(persona, Architect) || Matches(persona, Recorder) || Matches(persona, PriorArtAnalyst);
        }

        /// <summary>
        /// Replace legacy built-in persona references in free-form text.
        /// </summary>
        /// <param name="value">Input text.</param>
        /// <returns>Updated text, or null when the input was null.</returns>
        public static string? ReplaceLegacyTestEngineer(string? value)
        {
            if (value == null) return null;
            return value.Replace(LegacyTestEngineer, TestEngineer, StringComparison.Ordinal);
        }
    }
}
