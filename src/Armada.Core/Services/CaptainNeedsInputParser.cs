namespace Armada.Core.Services
{
    using System.Text.RegularExpressions;
    using Armada.Core.Models;

    /// <summary>
    /// Parses [ARMADA:NEEDS-INPUT soft|block] markers from captain agent output.
    /// Returns a result describing whether the marker was absent, malformed, or a valid soft/block request.
    /// Does not throw on arbitrary input.
    /// </summary>
    public static class CaptainNeedsInputParser
    {
        #region Private-Members

        // Matches [ARMADA:NEEDS-INPUT <mode>] <question>
        // The marker must appear at the start of a line (with optional leading whitespace).
        // mode is case-insensitive: soft or block.
        // question text is everything after the closing bracket (trimmed).
        private static readonly Regex _MarkerRegex = new Regex(
            @"(?m)^\s*\[ARMADA:NEEDS-INPUT\s+(soft|block)\s*\]\s*(.*)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Detect malformed markers: has NEEDS-INPUT but bad mode token.
        private static readonly Regex _MalformedRegex = new Regex(
            @"(?m)^\s*\[ARMADA:NEEDS-INPUT[^\]]*\]",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        #endregion

        #region Public-Methods

        /// <summary>
        /// Parse the last [ARMADA:NEEDS-INPUT soft|block] marker in <paramref name="agentOutput"/>.
        /// Returns a result with Found=false when the marker is absent.
        /// Returns Malformed=true when the marker has an unrecognized mode token.
        /// Never throws.
        /// </summary>
        /// <param name="agentOutput">Raw stdout captured from a captain agent process.</param>
        /// <returns>Parsed needs-input request.</returns>
        public static CaptainNeedsInputRequest Parse(string? agentOutput)
        {
            CaptainNeedsInputRequest result = new CaptainNeedsInputRequest();

            if (String.IsNullOrEmpty(agentOutput))
                return result;

            // Look for a valid match first (using last match so a downgraded re-run wins)
            MatchCollection validMatches = _MarkerRegex.Matches(agentOutput);
            if (validMatches.Count > 0)
            {
                Match last = validMatches[validMatches.Count - 1];
                result.Found = true;
                string modeToken = last.Groups[1].Value.Trim().ToLowerInvariant();
                result.Mode = String.Equals(modeToken, "block", StringComparison.Ordinal)
                    ? NeedsInputModeEnum.Block
                    : NeedsInputModeEnum.Soft;
                result.QuestionText = last.Groups[2].Value.Trim();
                return result;
            }

            // Check for malformed marker presence
            if (_MalformedRegex.IsMatch(agentOutput))
            {
                result.Found = true;
                result.Malformed = true;
                return result;
            }

            return result;
        }

        #endregion
    }
}
