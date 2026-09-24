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
            return IsCreditSignal(text) || IsAuthFailureSignal(text);
        }

        /// <summary>
        /// Returns true when <paramref name="text"/> is a provider credit, balance, billing, or payment failure.
        /// Matches provider phrases and HTTP 402 status forms only, so a crash that merely names a billing
        /// module or a balance field is not read as an exhausted account.
        /// </summary>
        /// <param name="text">Runtime stderr, validation output, or failure reason text.</param>
        /// <returns>True when the text indicates a credit or billing failure.</returns>
        public static bool IsCreditSignal(string? text)
        {
            if (String.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            return _CreditPattern.IsMatch(Normalize(text));
        }

        /// <summary>
        /// Returns true when <paramref name="text"/> is a provider credential or authorization rejection. Matches
        /// provider phrases and HTTP 401/403 status forms only, so a crash that prints a line number, a process id
        /// or a filesystem "permission denied" is not read as a rejected key.
        /// </summary>
        /// <param name="text">Runtime stderr, validation output, or failure reason text.</param>
        /// <returns>True when the text indicates an authentication or authorization failure.</returns>
        public static bool IsAuthFailureSignal(string? text)
        {
            if (String.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            return _AuthFailurePattern.IsMatch(Normalize(text));
        }

        /// <summary>
        /// Returns true when <paramref name="text"/> looks like a provider usage or quota limit response, a
        /// request throttle (HTTP 429, "too many requests") or a provider overload (HTTP 529, "overloaded").
        /// Each means the provider, not the work, refused the turn, so the captain is benched and the mission
        /// re-routed. This is the one provider-limit definition: <see cref="RuntimeFailureClassifier"/> reads
        /// it too, so the bench decision and the crash-loop classification agree about the same exit.
        /// </summary>
        /// <param name="text">Runtime stderr, validation output, or failure reason text.</param>
        /// <returns>True when the text indicates a quota, usage, throttle or overload limit.</returns>
        public static bool IsQuotaLimitSignal(string? text)
        {
            if (String.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            string normalized = Normalize(text);
            return ContainsUsageLimitText(normalized) ||
                _QuotaPattern.IsMatch(normalized) ||
                _ThrottleOrOverloadPattern.IsMatch(normalized);
        }

        /// <summary>
        /// Returns true when <paramref name="text"/> is a provider throttle or overload: HTTP 429 or 529 in a
        /// status form, "too many requests", or a provider overload message. When no exhausted allowance is also
        /// named, the bench uses the provider's published retry time or the configured default backoff, never
        /// the multi-hour usage-cap window.
        /// </summary>
        /// <param name="text">Runtime stderr, validation output, or failure reason text.</param>
        /// <returns>True when the text indicates a throttle or overload.</returns>
        public static bool IsThrottleOrOverloadSignal(string? text)
        {
            if (String.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            return _ThrottleOrOverloadPattern.IsMatch(Normalize(text));
        }

        /// <summary>
        /// Classify a provider fault in runtime output: a usage, quota, throttle, overload, credit or spend
        /// limit is <see cref="RuntimeFailureKindEnum.UsageLimit"/>; a credential rejection is
        /// <see cref="RuntimeFailureKindEnum.AuthFailure"/>; anything else is
        /// <see cref="RuntimeFailureKindEnum.Crash"/>. Built from the same predicates the bench decision reads,
        /// so a text that benches a captain is never also counted as a crash.
        /// </summary>
        /// <param name="text">Runtime output tail or failure reason text.</param>
        /// <returns>The provider fault kind, or Crash when the text names none.</returns>
        public static RuntimeFailureKindEnum ClassifyProviderFault(string? text)
        {
            if (String.IsNullOrWhiteSpace(text)) return RuntimeFailureKindEnum.Crash;
            if (IsQuotaLimitSignal(text) || IsCreditSignal(text) || IsProviderAccountSpendLimitSignal(text))
                return RuntimeFailureKindEnum.UsageLimit;
            if (IsAuthFailureSignal(text)) return RuntimeFailureKindEnum.AuthFailure;
            return RuntimeFailureKindEnum.Crash;
        }

        /// <summary>
        /// Returns true when <paramref name="text"/> is an ACCOUNT-WIDE spend cap (e.g. "Daily spend limit of
        /// $2000.00 reached for this user"), as opposed to a per-key quota. An account cap disables EVERY captain
        /// sharing that provider account until the daily reset, so the caller benches the whole provider group at
        /// once rather than walking siblings one re-route at a time. Provider-neutral: matches the cap phrasing,
        /// never a provider name.
        /// </summary>
        /// <param name="text">Runtime stderr, validation output, or failure reason text.</param>
        /// <returns>True when the text indicates an account-wide daily spend cap.</returns>
        public static bool IsProviderAccountSpendLimitSignal(string? text)
        {
            if (String.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            string normalized = Normalize(text);
            return normalized.Contains("spend limit", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("daily spend", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Returns true when <paramref name="text"/> is a PROVIDER SAFEGUARD BLOCK -- the model provider's own
        /// content/safety gate refused the request (e.g. a cyber-topic or content-policy block) rather than the
        /// model producing a defect. This is provider-neutral by design: it matches the block phrasing, never a
        /// specific provider or model name, so a safeguard-aware re-route can bench whichever provider blocked and
        /// route to a peer. Add new providers' safeguard signatures here as they appear; never key routing off a
        /// provider name.
        /// </summary>
        /// <param name="text">Runtime stderr, validation output, or failure reason text.</param>
        /// <returns>True when the text indicates a provider content/safety safeguard block.</returns>
        public static bool IsProviderSafeguardBlockSignal(string? text)
        {
            if (String.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            string normalized = Normalize(text);
            return normalized.Contains("safety measures that flagged this message", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("flagged this message for a cybersecurity topic", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("flagged for a cybersecurity topic", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("content_policy_violation", StringComparison.OrdinalIgnoreCase) ||
                (normalized.Contains("cybersecurity", StringComparison.OrdinalIgnoreCase) &&
                    normalized.Contains("flagged", StringComparison.OrdinalIgnoreCase)) ||
                (normalized.Contains("safety system", StringComparison.OrdinalIgnoreCase) &&
                    (normalized.Contains("blocked", StringComparison.OrdinalIgnoreCase) ||
                        normalized.Contains("flagged", StringComparison.OrdinalIgnoreCase) ||
                        normalized.Contains("refused", StringComparison.OrdinalIgnoreCase))) ||
                // Newer Claude / Claude Code safeguard phrasing. The ClaudeCode CLI emits a
                // "rate_limit_event" stream event for several distinct failure modes (true usage
                // caps, session resets, and provider safeguards refusals all share the same
                // event type), and the API error text below is what distinguishes a safeguard
                // refusal from a quota exhaustion. Match the block phrasing, never a model name.
                normalized.Contains("safeguards flagged this message", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("can't respond to this message with", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("claude code can't respond to this message", StringComparison.OrdinalIgnoreCase);
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
        /// Returns true when a single output line carries a reset-time hint that
        /// <see cref="TryParseRetryAfterUtc"/> can read, so a log gate that suppresses ordinary stderr keeps it.
        /// </summary>
        /// <param name="line">One output line.</param>
        /// <returns>True when the line names a reset time.</returns>
        public static bool IsResetTimeLine(string? line)
        {
            if (String.IsNullOrWhiteSpace(line)) return false;

            // Substring checks only: this runs inside the process stderr event handler, once per line.
            return line.Contains("try again", StringComparison.OrdinalIgnoreCase)
                || line.Contains("retry-after", StringComparison.OrdinalIgnoreCase)
                || line.Contains("retry after", StringComparison.OrdinalIgnoreCase)
                || line.Contains("resets in", StringComparison.OrdinalIgnoreCase)
                || line.Contains("reset in", StringComparison.OrdinalIgnoreCase)
                || line.Contains("resets at", StringComparison.OrdinalIgnoreCase);
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

            // An explicit ISO-8601 instant after a reset/retry cue. Checked before the dated and
            // retry-after forms: "retry after 2026-09-15T18:30:00Z" would otherwise read as 2026 seconds.
            Match isoMatch = _IsoResetPattern.Match(normalized);
            if (isoMatch.Success)
            {
                if (DateTime.TryParse(
                        isoMatch.Groups["iso"].Value,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                        out DateTime isoRetry))
                {
                    DateTime isoUtc = DateTime.SpecifyKind(isoRetry, DateTimeKind.Utc);
                    return isoUtc > referenceUtc ? isoUtc : null;
                }
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
            if (match.Success)
            {
                string timeToken = match.Groups[1].Value.Trim();
                if (DateTime.TryParse(timeToken, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out DateTime localTime))
                {
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
            }

            // An HTTP-style Retry-After in seconds.
            Match secondsMatch = _RetryAfterSecondsPattern.Match(normalized);
            if (secondsMatch.Success && Int64.TryParse(secondsMatch.Groups["seconds"].Value, out long retrySeconds))
            {
                return retrySeconds > 0 ? referenceUtc.AddSeconds(retrySeconds) : null;
            }

            // A relative "try again in / resets in N <unit>" phrase. No upper bound is applied: providers
            // publish multi-day resets, and discarding one releases the captain into a still-exhausted account.
            Match relativeMatch = _RelativeResetPattern.Match(normalized);
            if (relativeMatch.Success && Int64.TryParse(relativeMatch.Groups["amount"].Value, out long amount) && amount > 0)
            {
                string unit = relativeMatch.Groups["unit"].Value.ToLowerInvariant();
                if (unit.StartsWith("d", StringComparison.Ordinal)) return referenceUtc.AddDays(amount);
                if (unit.StartsWith("h", StringComparison.Ordinal)) return referenceUtc.AddHours(amount);
                if (unit.StartsWith("m", StringComparison.Ordinal)) return referenceUtc.AddMinutes(amount);
                return referenceUtc.AddSeconds(amount);
            }

            return null;
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

            // A throttle or overload with no exhausted allowance clears in seconds or minutes, so it takes the
            // configured default backoff rather than the usage-cap window.
            if (IsTransientThrottleOnly(text))
            {
                return null;
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

        /// <summary>
        /// An exhausted allowance: a quota, a rate limit, or a spend cap. "Disk quota" is a host error, not a
        /// provider allowance, and "RateLimiter" is a type name; the anchors exclude both.
        /// </summary>
        private static readonly Regex _QuotaPattern = new Regex(
            @"(?<!\bdisk\s)\bquota\b|\binsufficient[_ ]quota\b|\bresource_exhausted\b|\brate[ -]?limit(?:ed|s)?\b|\brate_limit(?:_error|_exceeded)?\b|\bspend limit\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// A status code counts only after a status cue ("HTTP 429", "status: 429", "API Error: 529",
        /// "code=429") or before its reason phrase ("429 Too Many Requests"), never as a bare number that may
        /// be a process id, a line number or a test count.
        /// </summary>
        private const string _StatusCue = @"(?:\bhttp(?:/\d(?:\.\d)?)?|\bstatus(?:[ _]?code)?|\bapi error|\berror|\bcode|\bresponse)\s*[:=]?\s*""?";

        private static readonly Regex _ThrottleOrOverloadPattern = new Regex(
            _StatusCue + @"(?:429|529)\b"
            + @"|\btoo many requests\b"
            + @"|\boverloaded_error\b"
            + @"|\b(?:api|servers?|services?|models?|providers?|engines?|upstream|backend)\b[^\r\n]{0,24}\b(?:overloaded|over capacity|at capacity)\b"
            + @"|""message""\s*:\s*""overloaded""",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex _CreditPattern = new Regex(
            _StatusCue + @"402\b"
            + @"|\bpayment required\b|\bpayment (?:method|failed|is overdue|past due)\b"
            + @"|\binsufficient[_ ](?:credits?|balance|funds)\b|\bout of (?:credits?|balance)\b|\bcredit balance\b|\bcredits? (?:exhausted|depleted|used up)\b"
            + @"|\bbalance (?:is )?(?:too low|insufficient|exhausted|depleted)\b"
            + @"|\bcheck your billing\b|\bbilling[_ ](?:hard[_ ]limit|not[_ ]active)\b"
            + @"|\bbilling (?:account|details|information|profile) (?:is |has been )?(?:suspended|required|inactive|disabled|missing|invalid)\b"
            + @"|\bbilling (?:issue|problem|error)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex _AuthFailurePattern = new Regex(
            _StatusCue + @"40[13]\b"
            + @"|\b401\s+unauthorized\b|\b403\s+forbidden\b|\bunauthorized\b"
            + @"|\binvalid[_ ]api[_ ]key\b|\bincorrect api key\b|\bno api key\b|\bmissing api key\b|\bapi key (?:is )?(?:invalid|missing|expired|revoked)\b"
            + @"|\bauthentication[_ ](?:failed|error|required)\b|\bauth failed\b|\bfailed to authenticate\b|\bnot authenticated\b|\bcould not authenticate\b"
            + @"|\bpermission_denied\b|\bnot logged in\b|\blogin required\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

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
        /// An ISO-8601 reset instant after a reset/retry cue ("retry after 2026-09-15T18:30:00Z").
        /// </summary>
        private static readonly Regex _IsoResetPattern = new Regex(
            @"(?:reset|retry|available|try\s+again)[^0-9]{0,20}(?<iso>\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}(?::\d{2})?(?:\.\d+)?(?:Z|[+-]\d{2}:?\d{2})?)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// An HTTP-style Retry-After header value in seconds ("Retry-After: 120").
        /// </summary>
        private static readonly Regex _RetryAfterSecondsPattern = new Regex(
            @"retry[-\s]?after\s*[:=]?\s*(?<seconds>\d{1,9})\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// A relative reset ("try again in 30 seconds", "resets in 3 hours", "try again in 15 min").
        /// </summary>
        private static readonly Regex _RelativeResetPattern = new Regex(
            @"(?:resets?|retry|try\s+again|available)\s+in\s+(?<amount>\d{1,6})\s*(?<unit>seconds?|secs?|minutes?|mins?|hours?|hrs?|days?|s|m|h|d)\b",
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

        private static bool IsTransientThrottleOnly(string? text)
        {
            if (!IsThrottleOrOverloadSignal(text)) return false;
            string normalized = Normalize(text!);
            return !ContainsUsageLimitText(normalized)
                && !_QuotaPattern.IsMatch(normalized)
                && !IsCreditSignal(normalized);
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
                normalized.Contains("session limit", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("usage limit", StringComparison.OrdinalIgnoreCase);
        }

        #endregion
    }
}
