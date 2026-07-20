namespace Armada.Core.Services
{
    using System;
    using System.Globalization;
    using System.Text.RegularExpressions;
    using Armada.Core.Enums;

    /// <summary>
    /// Detects provider usage/quota-limit signals in runtime stderr or validation output.
    /// </summary>
    public static class ProviderQuotaLimitDetector
    {
        #region Public-Methods

        /// <summary>
        /// Returns true when <paramref name="text"/> looks like a provider credit, billing, or authentication failure
        /// that should be treated as "cannot verify now" rather than a hard model-invalid rejection.
        /// </summary>
        /// <param name="text">Runtime stderr, validation output, or failure reason text.</param>
        /// <returns>True when the text indicates a credit, billing, or auth failure.</returns>
        public static bool IsCreditAuthBenchSignal(string? text)
        {
            if (String.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            string normalized = Normalize(text);
            return normalized.Contains("credit", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("billing", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("payment", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("insufficient_credits", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("invalid_api_key", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("unauthorized", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("authentication", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("permission_denied", StringComparison.OrdinalIgnoreCase);
        }

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

            // Claude Code publishes the reset instant as a unix epoch appended to the limit
            // message ("Claude AI usage limit reached|1784563320") rather than as prose, so it is
            // checked before any text pattern.
            Match epochMatch = _EpochResetPattern.Match(normalized);
            if (epochMatch.Success
                && Int64.TryParse(epochMatch.Groups["epoch"].Value, out long epochValue))
            {
                // 13 digits is milliseconds, 10 is seconds.
                DateTimeOffset epochUtc = epochMatch.Groups["epoch"].Value.Length >= 13
                    ? DateTimeOffset.FromUnixTimeMilliseconds(epochValue)
                    : DateTimeOffset.FromUnixTimeSeconds(epochValue);
                return epochUtc.UtcDateTime;
            }

            // Providers publish two shapes of retry hint. Prefer the dated one: Codex emits
            // "try again at Jul 25th, 2026 7:22 AM", where the clock-only pattern below does not
            // match at all (it hits "Jul", not a digit), so the caller previously fell back to the
            // default backoff -- a 5-minute quarantine for a limit that lasts days. The captain was
            // then released into a still-exhausted account and failed again, burning one captain per
            // retry. Parse the absolute instant when the provider gives us one.
            Match datedMatch = _RetryAtDatedPattern.Match(normalized);
            if (datedMatch.Success)
            {
                string datedToken = _OrdinalSuffixPattern
                    .Replace(datedMatch.Groups["token"].Value.Trim(), "$1");

                if (_ExplicitDatePattern.IsMatch(datedToken)
                    && DateTime.TryParse(
                        datedToken,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                        out DateTime datedRetry))
                {
                    return DateTime.SpecifyKind(datedRetry, DateTimeKind.Utc);
                }
            }

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

        /// <summary>
        /// Quarantine window to apply when a provider reports a quota limit but publishes no
        /// parseable reset time. The generic short backoff is wrong for these: it releases the
        /// captain back into a still-exhausted account, which burns one captain per retry.
        /// </summary>
        /// <param name="runtime">Runtime of the captain that hit the limit.</param>
        /// <returns>Provider-specific window, or null to use the configured default backoff.</returns>
        public static TimeSpan? GetQuotaFallbackWindow(AgentRuntimeEnum? runtime)
        {
            switch (runtime)
            {
                // OpenCode never publishes a reset time and its limits run multi-day.
                case AgentRuntimeEnum.OpenCode:
                    return TimeSpan.FromDays(3);

                // Claude Code normally publishes an epoch (parsed above); when it does not, its
                // limits track a rolling 5-hour window.
                case AgentRuntimeEnum.ClaudeCode:
                    return TimeSpan.FromHours(5);

                default:
                    return null;
            }
        }

        /// <summary>
        /// Resolves the deadline to quarantine a quota-limited captain until: the provider's own
        /// published reset time when the message carries one, else the provider-specific fallback
        /// window, else null so the caller's configured default backoff applies.
        /// Call this only on quota / credit-auth paths -- it is deliberately not baked into the
        /// generic quarantine resolver, which must keep honouring the configured backoff.
        /// </summary>
        /// <param name="text">Runtime stderr or failure reason text.</param>
        /// <param name="runtime">Runtime of the captain that hit the limit.</param>
        /// <param name="referenceUtc">Reference instant.</param>
        /// <returns>Quarantine deadline in UTC, or null to use the configured default.</returns>
        public static DateTime? ResolveQuotaRetryAfterUtc(
            string? text,
            AgentRuntimeEnum? runtime,
            DateTime referenceUtc)
        {
            DateTime? published = TryParseRetryAfterUtc(text, referenceUtc);
            if (published.HasValue)
            {
                return published;
            }

            TimeSpan? window = GetQuotaFallbackWindow(runtime);
            return window.HasValue ? referenceUtc.Add(window.Value) : null;
        }

        #endregion

        #region Private-Members

        /// <summary>
        /// Retry-hint lead-ins across providers. Codex says "try again at", Claude says
        /// "will reset at" / "resets".
        /// </summary>
        private const string _RetryPhrase = @"(?:try again (?:at|on)|(?:will\s+)?resets?(?:\s+at)?)";

        private static readonly Regex _RetryAtPattern = new Regex(
            _RetryPhrase + @"\s+(\d{1,2}:\d{2}\s*(?:[AP]M)?|\d{1,2}\s*[AP]M)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// Captures everything after the retry lead-in up to the end of the sentence, so a dated
        /// hint ("Jul 25th, 2026 7:22 AM") survives intact for absolute parsing.
        /// </summary>
        private static readonly Regex _RetryAtDatedPattern = new Regex(
            _RetryPhrase + @"\s+(?<token>[^.\r\n]{4,60}?)\s*(?=[.\r\n]|$)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// Claude Code appends the reset instant as a unix epoch to its limit message
        /// ("Claude AI usage limit reached|1784563320").
        /// </summary>
        private static readonly Regex _EpochResetPattern = new Regex(
            @"usage limit reached\s*\|\s*(?<epoch>\d{10,13})",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// Strips English ordinal suffixes ("25th" -> "25") which <see cref="DateTime.TryParse(string)"/>
        /// cannot handle.
        /// </summary>
        private static readonly Regex _OrdinalSuffixPattern = new Regex(
            @"\b(\d{1,2})(?:st|nd|rd|th)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// Requires a real date component (4-digit year or month name) before a captured token is
        /// treated as absolute. Without this a bare "7:22 AM" would parse to today's date and be
        /// mistaken for a dated hint.
        /// </summary>
        private static readonly Regex _ExplicitDatePattern = new Regex(
            @"\d{4}|\b(?:jan|feb|mar|apr|may|jun|jul|aug|sep|oct|nov|dec)",
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
