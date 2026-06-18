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

            await RunTest("IsQuotaLimitSignal_UnrelatedError_ReturnsFalse", () =>
            {
                AssertFalse(
                    ProviderQuotaLimitDetector.IsQuotaLimitSignal("Agent process exited with code 1"),
                    "generic exit should not be treated as quota");
                return Task.CompletedTask;
            });
        }
    }
}
