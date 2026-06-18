namespace Armada.Test.Unit.Suites.Services
{
    using System;
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
        }
    }
}
