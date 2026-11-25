namespace Armada.Core.Services
{
    using System;

    /// <summary>
    /// The shared content rule for closing an incident: the cause must be non-empty and differ from
    /// the text the incident was opened with. Caller-facing closes also require person-written
    /// authorship, which IncidentService records separately; an automatic writer never gains that
    /// authorship on automatic updates. Text is compared after trimming, ordinally.
    /// </summary>
    public static class IncidentRootCauseRule
    {
        #region Public-Members

        /// <summary>
        /// Refusal code when the close carries no root-cause text.
        /// </summary>
        public const string RequiredCode = "incident_root_cause_required";

        /// <summary>
        /// Refusal code when the root cause is the text the incident was opened with.
        /// </summary>
        public const string UnchangedCode = "incident_root_cause_unchanged";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Evaluate determined cause text against the text the incident was opened with.
        /// </summary>
        /// <param name="rootCause">Root-cause text to check.</param>
        /// <param name="openedReason">Text the incident was opened with, or null when it had none.</param>
        /// <returns>Null when the content passes; otherwise the refusal code. This does not establish authorship.</returns>
        public static string? Evaluate(string? rootCause, string? openedReason)
        {
            string? written = Trim(rootCause);
            if (written == null) return RequiredCode;
            string? opened = Trim(openedReason);
            if (opened != null && String.Equals(written, opened, StringComparison.Ordinal)) return UnchangedCode;
            return null;
        }

        /// <summary>
        /// Human-readable refusal message for a refusal code. The message starts with the code.
        /// </summary>
        /// <param name="code">Refusal code.</param>
        /// <returns>Message naming the code and what the closer must supply.</returns>
        public static string Describe(string code)
        {
            if (String.Equals(code, UnchangedCode, StringComparison.Ordinal))
                return UnchangedCode + ": the root cause is the text the incident was opened with. "
                    + "Write the cause you determined before closing the incident.";
            return RequiredCode + ": closing an incident needs a root cause written by the person or operator who closes it.";
        }

        #endregion

        #region Private-Methods

        private static string? Trim(string? value)
        {
            return String.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        #endregion
    }
}
