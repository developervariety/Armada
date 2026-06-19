namespace Armada.Core.Services
{
    using System;
    using System.Globalization;
    using System.Text.RegularExpressions;

    /// <summary>
    /// Detects provider usage/quota-limit signals in runtime stderr or validation output.
    /// </summary>
    public static class ProviderQuotaLimitDetector
    {
        #region Public-Methods

        /// <summary>
        /// Returns true when <paramref name="text"/> looks like a provider usage or quota limit response.
        /// </summary>
        /// <param name="text">Runtime stderr, validation output, or failure reason text.</param>
        /// <returns>True when the text indicates a quota or usage limit.</returns>
        public static bool IsQuotaLimitSignal(string? text)
        {
            if (String.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            string normalized = Normalize(text);
            return ContainsUsageLimitText(normalized) ||
                normalized.Contains("rate limit", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("quota", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("insufficient_quota", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Detects the codex CLI usage-limit crash signature: a very short run that exits code 1
        /// and prints a ChatGPT usage-limit message. The usage-limit text is the discriminator;
        /// exit code and runtime are corroborating evidence so ordinary build/test failures are
        /// not treated as quota crashes.
        /// </summary>
        /// <param name="exitCode">Agent process exit code.</param>
        /// <param name="runtime">Agent process runtime.</param>
        /// <param name="output">Captured agent stdout/stderr or failure reason text.</param>
        /// <returns>True when the output looks like a codex usage-limit crash.</returns>
        public static bool IsCodexUsageLimitCrash(int? exitCode, TimeSpan runtime, string? output)
        {
            if (exitCode != 1)
            {
                return false;
            }

            if (runtime.TotalSeconds > 30)
            {
                return false;
            }

            if (String.IsNullOrWhiteSpace(output))
            {
                return false;
            }

            string normalized = Normalize(output);
            return ContainsUsageLimitText(normalized) ||
                normalized.Contains("try again at", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Parses a provider-published retry time when present; otherwise returns null.
        /// </summary>
        /// <param name="text">Runtime stderr or failure reason text.</param>
        /// <param name="referenceUtc">Reference instant used to resolve clock times without a date.</param>
        /// <returns>UTC retry time when parsed; otherwise null.</returns>
        public static DateTime? TryParseRetryAfterUtc(string? text, DateTime referenceUtc)
        {
            if (String.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            string normalized = Normalize(text);
            Match match = _RetryAtPattern.Match(normalized);
            if (!match.Success)
            {
                return null;
            }

            string timeToken = match.Groups[1].Value.Trim();
            if (!DateTime.TryParse(timeToken, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out DateTime localTime))
            {
                return null;
            }

            DateTime candidate = new DateTime(
                referenceUtc.Year,
                referenceUtc.Month,
                referenceUtc.Day,
                localTime.Hour,
                localTime.Minute,
                localTime.Second,
                DateTimeKind.Utc);

            if (candidate <= referenceUtc)
            {
                candidate = candidate.AddDays(1);
            }

            return candidate;
        }

        #endregion

        #region Private-Members

        private static readonly Regex _RetryAtPattern = new Regex(
            @"try again at\s+(\d{1,2}:\d{2}\s*(?:[AP]M)?)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static string Normalize(string text)
        {
            string normalized = text.Trim();
            if (normalized.StartsWith("[stderr]", StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized.Substring("[stderr]".Length).Trim();
            }

            return normalized;
        }

        private static bool ContainsUsageLimitText(string? text)
        {
            if (String.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            string normalized = Normalize(text);
            return normalized.Contains("hit your limit", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("hit your usage limit", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("usage limit", StringComparison.OrdinalIgnoreCase);
        }

        #endregion
    }
}
