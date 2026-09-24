namespace Armada.Runtimes
{
    using System;

    /// <summary>
    /// Reads the activity records runtimes write alongside captain output. An activity record is runtime
    /// telemetry for viewers, never captain prose. The record shape is built by the runtime log formatter
    /// in this assembly from the same prefix.
    /// </summary>
    public static class ActivityRecords
    {
        #region Public-Members

        /// <summary>
        /// Prefix of every activity record.
        /// </summary>
        public const string ActivityMarker = "[ARMADA:ACTIVITY]";

        #endregion

        #region Internal-Members

        /// <summary>
        /// Prefix of a tool activity record: "[ARMADA:ACTIVITY] tool &lt;name&gt; &lt;detail&gt; (&lt;status&gt;)".
        /// </summary>
        internal const string ToolPrefix = ActivityMarker + " tool ";

        /// <summary>
        /// Word that follows the runtime label in a provider failure record:
        /// "[ARMADA:ACTIVITY] &lt;runtime&gt; error &lt;message&gt;".
        /// </summary>
        internal const string ProviderFailureWord = "error";

        #endregion

        #region Public-Methods

        /// <summary>
        /// True when a line is an activity record.
        /// </summary>
        /// <param name="line">Output line.</param>
        /// <returns>True when the line starts with <see cref="ActivityMarker"/>.</returns>
        public static bool IsActivityRecord(string? line)
        {
            return !String.IsNullOrEmpty(line) && line.StartsWith(ActivityMarker, StringComparison.Ordinal);
        }

        /// <summary>
        /// True when a line is a provider failure record, "[ARMADA:ACTIVITY] &lt;runtime&gt; error ...", the one
        /// shape every runtime writes when its provider or CLI reports that the turn failed (a quota, an
        /// authentication, a model or a transport error). Chat, planning and refinement fail the turn on it, and
        /// the mission log keeps it. A tool record is never a provider failure, and neither is a record whose
        /// second word is not "error" (for example "codex item error", a non-fatal item).
        /// </summary>
        /// <param name="line">Output line.</param>
        /// <returns>True when the line is a provider failure record.</returns>
        public static bool IsProviderFailure(string? line)
        {
            if (String.IsNullOrEmpty(line) || !line.StartsWith(ActivityMarker + " ", StringComparison.Ordinal))
                return false;

            string rest = line.Substring(ActivityMarker.Length + 1);
            int space = rest.IndexOf(' ');
            if (space <= 0) return false;

            string label = rest.Substring(0, space);
            if (String.Equals(label, "tool", StringComparison.Ordinal)) return false;
            foreach (char c in label)
            {
                if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-' || c == '_')) return false;
            }

            string afterLabel = rest.Substring(space + 1);
            if (!afterLabel.StartsWith(ProviderFailureWord, StringComparison.Ordinal)) return false;
            return afterLabel.Length == ProviderFailureWord.Length || afterLabel[ProviderFailureWord.Length] == ' ';
        }

        /// <summary>
        /// The text of a provider failure record without the activity marker, for example
        /// "codex error usage limit reached". Used as the failure reason of a chat, planning or refinement turn.
        /// </summary>
        /// <param name="line">Output line.</param>
        /// <returns>The failure text, or null when the line is not a provider failure record.</returns>
        public static string? ProviderFailureText(string? line)
        {
            if (!IsProviderFailure(line)) return null;
            return line!.Substring(ActivityMarker.Length).Trim();
        }

        /// <summary>
        /// Read a tool activity record back into its name, detail, and status. A trailing parenthesized value
        /// is the status only when it is a canonical status or a single lowercase word; otherwise it stays part
        /// of the detail. Other activity records return false.
        /// </summary>
        /// <param name="line">Output line.</param>
        /// <param name="record">The parsed tool call, or an empty record when the line is not a tool record.</param>
        /// <returns>True when the line is a tool activity record with a name.</returns>
        public static bool TryParseToolActivity(string? line, out ToolActivityRecord record)
        {
            record = new ToolActivityRecord();
            if (String.IsNullOrEmpty(line) || !line.StartsWith(ToolPrefix, StringComparison.Ordinal))
                return false;

            string rest = line.Substring(ToolPrefix.Length).Trim();
            if (rest.Length == 0)
                return false;

            string? status = null;
            if (rest.EndsWith(")", StringComparison.Ordinal))
            {
                int open = rest.LastIndexOf(" (", StringComparison.Ordinal);
                if (open >= 0)
                {
                    string candidate = rest.Substring(open + 2, rest.Length - open - 3);
                    if (IsRenderedStatus(candidate))
                    {
                        status = candidate;
                        rest = rest.Substring(0, open).TrimEnd();
                    }
                }
            }

            int space = rest.IndexOf(' ');
            record.Name = space < 0 ? rest : rest.Substring(0, space);
            string detail = space < 0 ? String.Empty : rest.Substring(space + 1).Trim();
            record.Detail = detail.Length > 0 ? detail : null;
            record.Status = status;
            return record.Name.Length > 0;
        }

        #endregion

        #region Private-Methods

        private static bool IsRenderedStatus(string candidate)
        {
            if (candidate == StructuredRuntimeLogFormatter.OkStatus
                || candidate == StructuredRuntimeLogFormatter.ErrorStatus
                || candidate == StructuredRuntimeLogFormatter.IncompleteStatus)
                return true;
            if (candidate.StartsWith(StructuredRuntimeLogFormatter.ErrorStatus + " exit ", StringComparison.Ordinal))
                return true;
            if (candidate.Length == 0 || candidate.Length > StructuredRuntimeLogFormatter.StatusLimit)
                return false;
            foreach (char c in candidate)
            {
                if (c < 'a' || c > 'z') return false;
            }
            return true;
        }

        #endregion
    }
}
