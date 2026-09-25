namespace Armada.Core.Services
{
    using System;

    /// <summary>
    /// The one rule every caller-facing surface applies when a person or operator closes an incident:
    /// the root cause in force at close must be non-empty text that the closer wrote, and it must not
    /// be the text the incident was opened with. The opened text is usually an automatic copy of a
    /// failure reason, so accepting it would make an automatic reading look like a verified cause.
    /// Text is compared after trimming, ordinally.
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
        /// Evaluate a root cause a person supplies against the text the incident was opened with.
        /// </summary>
        /// <param name="rootCause">Root-cause text the person supplies.</param>
        /// <param name="openedReason">Text the incident was opened with, or null when it had none.</param>
        /// <returns>Null when the text is a person-written cause; otherwise the refusal code.</returns>
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
