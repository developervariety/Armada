namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Reads the criterion conflicts a Judge declares: a fix the Judge requires that cannot be made
    /// without breaking a stated acceptance criterion.
    /// </summary>
    /// <remarks>
    /// A rescue builds the fix a Judge required, and the next Judge then measures the rescue against the
    /// same criteria. When the fix necessarily breaks one of them, every rescue fails the next review and
    /// the loop ends only when an operator corrects the criterion. The Judge declares the conflict with a
    /// standalone line of the form <c>CRITERION-CONFLICT: &lt;criterion&gt; -- &lt;conflict&gt;</c>, and
    /// a NEEDS_REVISION or FAIL verdict that carries one is held for operator criterion review instead
    /// of being rescued.
    /// </remarks>
    public static class JudgeCriterionConflicts
    {
        #region Public-Members

        /// <summary>The prefix that opens a criterion-conflict line.</summary>
        public const string LinePrefix = "CRITERION-CONFLICT:";

        /// <summary>The separator between the criterion and the conflict on one line.</summary>
        public const string Separator = " -- ";

        /// <summary>Reason code for a Judge verdict held for operator criterion review.</summary>
        public const string ReasonCode = "criterion_conflict";

        /// <summary>
        /// The marker appended to the failure reason of a Judge verdict that declares a criterion
        /// conflict. Autonomous recovery reads it and holds the rescue for operator criterion review.
        /// </summary>
        public const string RescueBlockMarker = "[judge:criterion_conflict]";

        /// <summary>Event recorded when a Judge verdict is held for operator criterion review.</summary>
        public const string HeldEventType = "mission.criterion_conflict";

        #endregion

        #region Private-Members

        private const int _MaxConflicts = 12;
        private const int _MaxConflictChars = 600;

        #endregion

        #region Public-Methods

        /// <summary>
        /// The criterion-conflict lines in a Judge's output, each as the text after the prefix. A line
        /// counts only when it starts with the prefix (after an optional list bullet or code quote) and
        /// names both a criterion and a conflict around the separator; a placeholder that opens with
        /// <c>&lt;</c> and a line that says there is no conflict are ignored. Duplicates are dropped and
        /// the list is bounded in count and per-line length.
        /// </summary>
        /// <param name="agentOutput">The Judge's output. Null or empty yields an empty list.</param>
        /// <returns>The declared conflicts, in output order.</returns>
        public static List<string> Extract(string? agentOutput)
        {
            List<string> conflicts = new List<string>();
            if (String.IsNullOrWhiteSpace(agentOutput)) return conflicts;

            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string raw in agentOutput.Replace("\r\n", "\n").Split('\n'))
            {
                string line = raw.Trim();
                if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal))
                    line = line.Substring(2).TrimStart();
                line = line.Trim('`').Trim();
                if (!line.StartsWith(LinePrefix, StringComparison.OrdinalIgnoreCase)) continue;

                string body = line.Substring(LinePrefix.Length).Trim().Trim('`').Trim();
                if (body.Length == 0 || body.StartsWith("<", StringComparison.Ordinal)) continue;

                int separator = body.IndexOf(Separator, StringComparison.Ordinal);
                if (separator <= 0) continue;
                string criterion = body.Substring(0, separator).Trim();
                string conflict = body.Substring(separator + Separator.Length).Trim();
                if (criterion.Length == 0 || conflict.Length == 0) continue;
                if (IsNoConflictText(criterion)) continue;

                if (body.Length > _MaxConflictChars) body = body.Substring(0, _MaxConflictChars);
                if (!seen.Add(body)) continue;
                conflicts.Add(body);
                if (conflicts.Count >= _MaxConflicts) break;
            }

            return conflicts;
        }

        #endregion

        #region Private-Methods

        private static bool IsNoConflictText(string criterion)
        {
            string normalized = criterion.Trim().Trim('(', ')', '.').Trim();
            return String.Equals(normalized, "none", StringComparison.OrdinalIgnoreCase)
                || String.Equals(normalized, "n/a", StringComparison.OrdinalIgnoreCase);
        }

        #endregion
    }
}
