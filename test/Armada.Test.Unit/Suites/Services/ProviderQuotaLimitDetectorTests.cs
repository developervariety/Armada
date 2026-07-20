namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using Armada.Core.Enums;
    using Armada.Core.Services;
    using Armada.Test.Common;

    /// <summary>Tests for provider quota limit detection and retry parsing.</summary>
    public sealed class ProviderQuotaLimitDetectorTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Provider Quota Limit Detector";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("IsQuotaLimitSignal_UsageLimitPhrase_ReturnsTrue", () =>
            {
                AssertTrue(
                    ProviderQuotaLimitDetector.IsQuotaLimitSignal("You've hit your usage limit for Codex"),
                    "usage limit phrase should be detected");
                return Task.CompletedTask;
            });

            await RunTest("TryParseRetryAfterUtc_DatedCodexUsageLimit_ReturnsThatDateNotToday", () =>
            {
                // Regression: this is the verbatim Codex message seen 2026-07-19. The clock-only
                // pattern never matched it (it hits "Jul", not a digit), so parsing returned null
                // and the caller fell back to the 300s default backoff -- a 5-minute quarantine for
                // a limit lasting 5 days. Captains were released into a still-exhausted account and
                // burned one per retry.
                DateTime referenceUtc = new DateTime(2026, 7, 20, 0, 14, 0, DateTimeKind.Utc);
                string text = "ERROR: You've hit your usage limit. Upgrade to Pro "
                    + "(https://chatgpt.com/explore/pro), visit https://chatgpt.com/codex/settings/usage "
                    + "to purchase more credits or try again at Jul 25th, 2026 7:22 AM.";

                DateTime? retryAfterUtc = ProviderQuotaLimitDetector.TryParseRetryAfterUtc(text, referenceUtc);

                AssertNotNull(retryAfterUtc, "dated retry hint should parse");
                AssertEqual(2026, retryAfterUtc!.Value.Year, "year should come from the message");
                AssertEqual(7, retryAfterUtc.Value.Month, "month should come from the message");
                AssertEqual(25, retryAfterUtc.Value.Day, "day must be the 25th, not the reference day");
                AssertTrue(
                    retryAfterUtc.Value - referenceUtc > TimeSpan.FromDays(4),
                    "quarantine must span the real limit window, not minutes");
                return Task.CompletedTask;
            });

            await RunTest("TryParseRetryAfterUtc_ClaudeEpochSuffix_ParsesAbsoluteInstant", () =>
            {
                // Claude Code publishes the reset instant as a unix epoch appended to the message
                // rather than as prose, so no text pattern would ever match it.
                DateTime referenceUtc = new DateTime(2026, 7, 20, 1, 0, 0, DateTimeKind.Utc);
                DateTime expectedUtc = new DateTime(2026, 7, 20, 6, 0, 0, DateTimeKind.Utc);
                long epochSeconds = new DateTimeOffset(expectedUtc).ToUnixTimeSeconds();
                string text = "Claude AI usage limit reached|" + epochSeconds;

                DateTime? retryAfterUtc = ProviderQuotaLimitDetector.TryParseRetryAfterUtc(text, referenceUtc);

                AssertNotNull(retryAfterUtc, "epoch reset hint should parse");
                AssertEqual(expectedUtc, retryAfterUtc!.Value, "epoch should decode to the exact reset instant");
                return Task.CompletedTask;
            });

            await RunTest("TryParseRetryAfterUtc_ClaudeEpochMilliseconds_ParsesAbsoluteInstant", () =>
            {
                DateTime referenceUtc = new DateTime(2026, 7, 20, 1, 0, 0, DateTimeKind.Utc);
                DateTime expectedUtc = new DateTime(2026, 7, 20, 6, 0, 0, DateTimeKind.Utc);
                long epochMs = new DateTimeOffset(expectedUtc).ToUnixTimeMilliseconds();
                string text = "Claude AI usage limit reached|" + epochMs;

                DateTime? retryAfterUtc = ProviderQuotaLimitDetector.TryParseRetryAfterUtc(text, referenceUtc);

                AssertNotNull(retryAfterUtc, "millisecond epoch should parse");
                AssertEqual(expectedUtc, retryAfterUtc!.Value, "13-digit epoch should be treated as milliseconds");
                return Task.CompletedTask;
            });

            await RunTest("TryParseRetryAfterUtc_ClaudeResetAtPhrase_Parses", () =>
            {
                DateTime referenceUtc = new DateTime(2026, 7, 20, 6, 0, 0, DateTimeKind.Utc);
                string text = "Claude usage limit reached. Your limit will reset at 9:30 PM";

                DateTime? retryAfterUtc = ProviderQuotaLimitDetector.TryParseRetryAfterUtc(text, referenceUtc);

                AssertNotNull(retryAfterUtc, "'will reset at' phrasing should parse");
                AssertEqual(21, retryAfterUtc!.Value.Hour, "hour should come from the message");
                return Task.CompletedTask;
            });

            await RunTest("GetQuotaFallbackWindow_OpenCode_IsThreeDays", () =>
            {
                // OpenCode reports the limit but never publishes a reset time, so nothing is
                // parseable and the fallback is the ONLY thing preventing an immediate re-release
                // into an exhausted account.
                TimeSpan? window = ProviderQuotaLimitDetector.GetQuotaFallbackWindow(AgentRuntimeEnum.OpenCode);

                AssertNotNull(window, "OpenCode must have an explicit fallback window");
                AssertEqual(TimeSpan.FromDays(3), window!.Value, "OpenCode fallback should be 3 days");
                return Task.CompletedTask;
            });

            await RunTest("GetQuotaFallbackWindow_ClaudeCode_IsFiveHourRollingWindow", () =>
            {
                TimeSpan? window = ProviderQuotaLimitDetector.GetQuotaFallbackWindow(AgentRuntimeEnum.ClaudeCode);

                AssertNotNull(window, "ClaudeCode should have a fallback window");
                AssertEqual(TimeSpan.FromHours(5), window!.Value, "ClaudeCode fallback should track its 5-hour window");
                return Task.CompletedTask;
            });

            await RunTest("ResolveQuotaRetryAfterUtc_OpenCodeWithNoPublishedTime_UsesThreeDayFallback", () =>
            {
                // OpenCode reports the limit with no reset time at all. Before the fallback the
                // caller got null and applied the short default backoff, releasing the captain
                // straight back into an exhausted account.
                DateTime referenceUtc = new DateTime(2026, 7, 20, 1, 0, 0, DateTimeKind.Utc);
                string text = "opencode: request failed - usage limit exceeded for this account";

                DateTime? deadline = ProviderQuotaLimitDetector.ResolveQuotaRetryAfterUtc(
                    text, AgentRuntimeEnum.OpenCode, referenceUtc);

                AssertNotNull(deadline, "OpenCode quota failure must still yield a deadline");
                AssertEqual(referenceUtc.AddDays(3), deadline!.Value, "should bench OpenCode for 3 days");
                return Task.CompletedTask;
            });

            await RunTest("ResolveQuotaRetryAfterUtc_PublishedTimeBeatsRuntimeFallback", () =>
            {
                // A provider-published reset time is always more accurate than our per-runtime
                // guess, so it must win even for a runtime that has a fallback window.
                DateTime referenceUtc = new DateTime(2026, 7, 20, 1, 0, 0, DateTimeKind.Utc);
                DateTime expectedUtc = new DateTime(2026, 7, 20, 4, 0, 0, DateTimeKind.Utc);
                long epochSeconds = new DateTimeOffset(expectedUtc).ToUnixTimeSeconds();
                string text = "Claude AI usage limit reached|" + epochSeconds;

                DateTime? deadline = ProviderQuotaLimitDetector.ResolveQuotaRetryAfterUtc(
                    text, AgentRuntimeEnum.ClaudeCode, referenceUtc);

                AssertEqual(expectedUtc, deadline!.Value, "published epoch should win over the 5-hour fallback");
                return Task.CompletedTask;
            });

            await RunTest("ResolveQuotaRetryAfterUtc_UnknownRuntimeNoPublishedTime_ReturnsNullForConfiguredDefault", () =>
            {
                DateTime referenceUtc = new DateTime(2026, 7, 20, 1, 0, 0, DateTimeKind.Utc);

                AssertNull(
                    ProviderQuotaLimitDetector.ResolveQuotaRetryAfterUtc("quota exceeded", AgentRuntimeEnum.Cursor, referenceUtc),
                    "runtime without a fallback window should defer to the configured default backoff");
                return Task.CompletedTask;
            });

            await RunTest("GetQuotaFallbackWindow_UnknownRuntime_UsesConfiguredDefault", () =>
            {
                // Null means "fall through to CaptainQuarantineSettings.DefaultBackoffSeconds";
                // runtimes that publish a usable reset time must not be force-benched for days.
                AssertNull(
                    ProviderQuotaLimitDetector.GetQuotaFallbackWindow(AgentRuntimeEnum.Codex),
                    "Codex publishes a dated reset time, so it should use the configured default");
                AssertNull(
                    ProviderQuotaLimitDetector.GetQuotaFallbackWindow(null),
                    "unknown runtime should use the configured default");
                return Task.CompletedTask;
            });

            await RunTest("TryParseRetryAfterUtc_DatedHintWithoutOrdinalSuffix_Parses", () =>
            {
                DateTime referenceUtc = new DateTime(2026, 7, 20, 0, 14, 0, DateTimeKind.Utc);
                string text = "usage limit reached, try again at 2026-08-01 09:30.";

                DateTime? retryAfterUtc = ProviderQuotaLimitDetector.TryParseRetryAfterUtc(text, referenceUtc);

                AssertNotNull(retryAfterUtc, "ISO-style dated hint should parse");
                AssertEqual(2026, retryAfterUtc!.Value.Year, "year should come from the message");
                AssertEqual(8, retryAfterUtc.Value.Month, "month should come from the message");
                AssertEqual(1, retryAfterUtc.Value.Day, "day should come from the message");
                return Task.CompletedTask;
            });

            await RunTest("TryParseRetryAfterUtc_ClockOnlyHint_StillAnchorsToReferenceDay", () =>
            {
                // The dated path must not swallow bare clock times: "7:22 AM" has no date component,
                // so it should keep anchoring to the reference day rather than being treated as absolute.
                DateTime referenceUtc = new DateTime(2026, 7, 20, 6, 0, 0, DateTimeKind.Utc);
                string text = "You've hit your limit. try again at 7:22 AM";

                DateTime? retryAfterUtc = ProviderQuotaLimitDetector.TryParseRetryAfterUtc(text, referenceUtc);

                AssertNotNull(retryAfterUtc, "clock-only hint should still parse");
                AssertEqual(20, retryAfterUtc!.Value.Day, "clock-only hint should stay on the reference day");
                AssertEqual(7, retryAfterUtc.Value.Hour, "hour should come from the message");
                AssertTrue(retryAfterUtc.Value > referenceUtc, "retry time should be in the future");
                return Task.CompletedTask;
            });

            await RunTest("TryParseRetryAfterUtc_TryAgainAtPhrase_ReturnsFutureUtc", () =>
            {
                DateTime referenceUtc = new DateTime(2026, 6, 18, 22, 0, 0, DateTimeKind.Utc);
                string text = "You've hit your limit. try again at 11:57 PM";
                DateTime? retryAfterUtc = ProviderQuotaLimitDetector.TryParseRetryAfterUtc(text, referenceUtc);
                AssertNotNull(retryAfterUtc, "retry time should parse");
                AssertTrue(retryAfterUtc!.Value > referenceUtc, "retry time should be in the future");
                return Task.CompletedTask;
            });

            await RunTest("IsQuotaLimitSignal_ChatGptUsageLimitStderr_ReturnsTrue", () =>
            {
                // Real-world signature: codex gpt-5.5 captains hitting the ChatGPT usage cap,
                // exiting code 1 within seconds with this exact stderr.
                AssertTrue(
                    ProviderQuotaLimitDetector.IsQuotaLimitSignal(
                        "[stderr] You've hit your usage limit. Upgrade to Pro (https://openai.com/chatgpt/pricing) or try again at 11:12 AM."),
                    "ChatGPT usage-limit stderr should be detected");
                return Task.CompletedTask;
            });

            await RunTest("TryParseRetryAfterUtc_ChatGptUsageLimitStderr_ParsesResetTime", () =>
            {
                DateTime referenceUtc = new DateTime(2026, 6, 18, 14, 0, 0, DateTimeKind.Utc);
                DateTime? retryAfterUtc = ProviderQuotaLimitDetector.TryParseRetryAfterUtc(
                    "[stderr] You've hit your usage limit. Upgrade to Pro or try again at 11:12 AM.",
                    referenceUtc);
                AssertNotNull(retryAfterUtc, "published reset time should parse from the ChatGPT stderr");
                AssertEqual(11, retryAfterUtc!.Value.Hour, "retry hour should be preserved");
                AssertEqual(12, retryAfterUtc.Value.Minute, "retry minute should be preserved");
                return Task.CompletedTask;
            });

            await RunTest("IsQuotaLimitSignal_CodexStreamErrorUsageLimit_ReturnsTrue", () =>
            {
                // Codex CLI surfaces the cap as a raw stream-error line (no [stderr] prefix) when the
                // process exits code 1 within seconds; the "usage limit" substring must still trip the detector.
                AssertTrue(
                    ProviderQuotaLimitDetector.IsQuotaLimitSignal(
                        "stream error: stream disconnected before completion: You've reached your usage limit."),
                    "codex stream-error usage-limit line should be detected");
                return Task.CompletedTask;
            });

            await RunTest("IsQuotaLimitSignal_UnrelatedError_ReturnsFalse", () =>
            {
                AssertFalse(
                    ProviderQuotaLimitDetector.IsQuotaLimitSignal("Agent process exited with code 1"),
                    "generic exit should not be treated as quota");
                return Task.CompletedTask;
            });

            await RunTest("IsQuotaLimitSignal_QuotaKeyword_ReturnsTrue", () =>
            {
                AssertTrue(
                    ProviderQuotaLimitDetector.IsQuotaLimitSignal("Error: monthly quota exceeded for this org"),
                    "quota keyword should be detected");
                return Task.CompletedTask;
            });

            await RunTest("IsQuotaLimitSignal_RateLimitKeyword_ReturnsTrue", () =>
            {
                AssertTrue(
                    ProviderQuotaLimitDetector.IsQuotaLimitSignal("HTTP 429: rate limit reached, slow down"),
                    "rate limit keyword should be detected");
                return Task.CompletedTask;
            });

            await RunTest("IsQuotaLimitSignal_InsufficientQuotaToken_ReturnsTrue", () =>
            {
                AssertTrue(
                    ProviderQuotaLimitDetector.IsQuotaLimitSignal("{\"error\":{\"code\":\"insufficient_quota\"}}"),
                    "insufficient_quota token should be detected");
                return Task.CompletedTask;
            });

            await RunTest("IsQuotaLimitSignal_StderrPrefixStripped_ReturnsTrue", () =>
            {
                AssertTrue(
                    ProviderQuotaLimitDetector.IsQuotaLimitSignal("[stderr] You've hit your limit. try again later."),
                    "stderr-prefixed quota signal should be detected after normalization");
                return Task.CompletedTask;
            });

            await RunTest("IsQuotaLimitSignal_NullOrWhitespace_ReturnsFalse", () =>
            {
                AssertFalse(ProviderQuotaLimitDetector.IsQuotaLimitSignal(null), "null should not be a quota signal");
                AssertFalse(ProviderQuotaLimitDetector.IsQuotaLimitSignal(""), "empty should not be a quota signal");
                AssertFalse(ProviderQuotaLimitDetector.IsQuotaLimitSignal("   \t  "), "whitespace should not be a quota signal");
                return Task.CompletedTask;
            });

            await RunTest("IsCreditAuthBenchSignal_InsufficientCredits_ReturnsTrue", () =>
            {
                AssertTrue(
                    ProviderQuotaLimitDetector.IsCreditAuthBenchSignal("insufficient credits for this account"),
                    "credit phrase should be detected");
                AssertTrue(
                    ProviderQuotaLimitDetector.IsCreditAuthBenchSignal("{\"error\":{\"code\":\"insufficient_credits\"}}"),
                    "insufficient_credits token should be detected");
                return Task.CompletedTask;
            });

            await RunTest("IsCreditAuthBenchSignal_AuthKeywords_ReturnsTrue", () =>
            {
                AssertTrue(
                    ProviderQuotaLimitDetector.IsCreditAuthBenchSignal("HTTP 401: unauthorized"),
                    "unauthorized should be detected");
                AssertTrue(
                    ProviderQuotaLimitDetector.IsCreditAuthBenchSignal("invalid_api_key supplied"),
                    "invalid_api_key should be detected");
                AssertTrue(
                    ProviderQuotaLimitDetector.IsCreditAuthBenchSignal("authentication failed for provider"),
                    "authentication should be detected");
                AssertTrue(
                    ProviderQuotaLimitDetector.IsCreditAuthBenchSignal("permission_denied for resource"),
                    "permission_denied should be detected");
                return Task.CompletedTask;
            });

            await RunTest("IsCreditAuthBenchSignal_BillingPayment_ReturnsTrue", () =>
            {
                AssertTrue(
                    ProviderQuotaLimitDetector.IsCreditAuthBenchSignal("billing account suspended"),
                    "billing keyword should be detected");
                AssertTrue(
                    ProviderQuotaLimitDetector.IsCreditAuthBenchSignal("payment required to continue"),
                    "payment keyword should be detected");
                return Task.CompletedTask;
            });

            await RunTest("IsCreditAuthBenchSignal_StderrPrefixStripped_ReturnsTrue", () =>
            {
                AssertTrue(
                    ProviderQuotaLimitDetector.IsCreditAuthBenchSignal("[stderr] invalid_api_key for this org"),
                    "stderr-prefixed auth signal should be detected after normalization");
                return Task.CompletedTask;
            });

            await RunTest("IsCreditAuthBenchSignal_UnrelatedError_ReturnsFalse", () =>
            {
                AssertFalse(
                    ProviderQuotaLimitDetector.IsCreditAuthBenchSignal("unknown model 'gpt-99'"),
                    "genuine model errors should not be treated as credit/auth bench signals");
                AssertFalse(
                    ProviderQuotaLimitDetector.IsCreditAuthBenchSignal("You've hit your usage limit for Codex"),
                    "quota-only text should not match the credit/auth classifier");
                return Task.CompletedTask;
            });

            await RunTest("IsCreditAuthBenchSignal_NullOrWhitespace_ReturnsFalse", () =>
            {
                AssertFalse(ProviderQuotaLimitDetector.IsCreditAuthBenchSignal(null), "null should not be a credit/auth signal");
                AssertFalse(ProviderQuotaLimitDetector.IsCreditAuthBenchSignal(""), "empty should not be a credit/auth signal");
                AssertFalse(ProviderQuotaLimitDetector.IsCreditAuthBenchSignal("   \t  "), "whitespace should not be a credit/auth signal");
                return Task.CompletedTask;
            });

            await RunTest("TryParseRetryAfterUtc_RetryEarlierThanReference_RollsToNextDay", () =>
            {
                DateTime referenceUtc = new DateTime(2026, 6, 18, 23, 0, 0, DateTimeKind.Utc);
                string text = "You've hit your limit. try again at 10:00 AM";
                DateTime? retryAfterUtc = ProviderQuotaLimitDetector.TryParseRetryAfterUtc(text, referenceUtc);
                AssertNotNull(retryAfterUtc, "retry time should parse");
                AssertTrue(retryAfterUtc!.Value > referenceUtc, "earlier clock time must roll forward past reference");
                AssertEqual(19, retryAfterUtc.Value.Day, "retry should land on the next calendar day");
                AssertEqual(10, retryAfterUtc.Value.Hour, "retry hour should be preserved");
                AssertEqual(DateTimeKind.Utc, retryAfterUtc.Value.Kind, "parsed retry time should be UTC");
                return Task.CompletedTask;
            });

            await RunTest("TryParseRetryAfterUtc_NoRetryPhrase_ReturnsNull", () =>
            {
                DateTime referenceUtc = new DateTime(2026, 6, 18, 22, 0, 0, DateTimeKind.Utc);
                DateTime? retryAfterUtc = ProviderQuotaLimitDetector.TryParseRetryAfterUtc(
                    "You've hit your usage limit for Codex", referenceUtc);
                AssertNull(retryAfterUtc, "no try-again phrase should yield null");
                return Task.CompletedTask;
            });

            await RunTest("TryParseRetryAfterUtc_NullText_ReturnsNull", () =>
            {
                DateTime referenceUtc = new DateTime(2026, 6, 18, 22, 0, 0, DateTimeKind.Utc);
                AssertNull(ProviderQuotaLimitDetector.TryParseRetryAfterUtc(null, referenceUtc), "null text should yield null");
                return Task.CompletedTask;
            });

            await RunTest("TryParseRetryAfterUtc_MalformedTimeToken_ReturnsNull", () =>
            {
                DateTime referenceUtc = new DateTime(2026, 6, 18, 22, 0, 0, DateTimeKind.Utc);
                DateTime? retryAfterUtc = ProviderQuotaLimitDetector.TryParseRetryAfterUtc(
                    "try again at 99:99 PM", referenceUtc);
                AssertNull(retryAfterUtc, "unparseable clock token should yield null");
                return Task.CompletedTask;
            });

            await RunTest("IsCodexUsageLimitCrash_CodexSignature_ReturnsTrue", () =>
            {
                AssertTrue(
                    ProviderQuotaLimitDetector.IsCodexUsageLimitCrash(
                        1,
                        TimeSpan.FromSeconds(3),
                        "You've hit your usage limit. Upgrade to Pro or try again at 11:12 AM."),
                    "codex exit-1 usage-limit crash should be detected");
                return Task.CompletedTask;
            });

            await RunTest("IsCodexUsageLimitCrash_TryAgainAtOnly_ReturnsTrue", () =>
            {
                AssertTrue(
                    ProviderQuotaLimitDetector.IsCodexUsageLimitCrash(
                        1,
                        TimeSpan.FromSeconds(2),
                        "try again at 11:12 AM"),
                    "codex crash with only try-again-at text should still be detected");
                return Task.CompletedTask;
            });

            await RunTest("IsCodexUsageLimitCrash_OrdinaryExitOne_ReturnsFalse", () =>
            {
                AssertFalse(
                    ProviderQuotaLimitDetector.IsCodexUsageLimitCrash(
                        1,
                        TimeSpan.FromSeconds(3),
                        "Agent process exited with code 1"),
                    "ordinary exit-1 failure without usage-limit text must not be treated as codex quota crash");
                return Task.CompletedTask;
            });

            await RunTest("IsCodexUsageLimitCrash_LongRuntime_ReturnsFalse", () =>
            {
                AssertFalse(
                    ProviderQuotaLimitDetector.IsCodexUsageLimitCrash(
                        1,
                        TimeSpan.FromMinutes(5),
                        "You've hit your usage limit. try again at 11:12 AM."),
                    "long-running exit-1 with usage-limit text should not match the codex crash signature");
                return Task.CompletedTask;
            });

            await RunTest("IsCodexUsageLimitCrash_NonOneExitCode_ReturnsFalse", () =>
            {
                AssertFalse(
                    ProviderQuotaLimitDetector.IsCodexUsageLimitCrash(
                        2,
                        TimeSpan.FromSeconds(3),
                        "You've hit your usage limit. try again at 11:12 AM."),
                    "non-1 exit code should not match the codex crash signature");
                return Task.CompletedTask;
            });

            await RunTest("IsCodexUsageLimitCrash_NullOutput_ReturnsFalse", () =>
            {
                AssertFalse(
                    ProviderQuotaLimitDetector.IsCodexUsageLimitCrash(1, TimeSpan.FromSeconds(3), null),
                    "null output should not match the codex crash signature");
                return Task.CompletedTask;
            });
        }
    }
}
