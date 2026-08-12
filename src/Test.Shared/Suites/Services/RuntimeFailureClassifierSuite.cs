namespace Test.Shared.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Services;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// Descriptors for <see cref="RuntimeFailureClassifier"/>. Positive cases confirm real provider
    /// usage-limit and auth messages classify correctly; negative cases confirm a clean exit and an
    /// unknown non-zero failure are not mistaken for a recoverable provider condition (which would cause a
    /// false quarantine).
    /// </summary>
    public sealed class RuntimeFailureClassifierSuite : IArmadaTestSuite
    {
        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the runtime-failure-classifier suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(Case("clean_exit_is_clean", "Exit code 0 classifies Clean regardless of output", TestTags.Positive, () =>
            {
                AssertEqual(RuntimeFailureKindEnum.Clean, RuntimeFailureClassifier.Classify(0, "rate limit exceeded but we exited fine"));
                AssertEqual(RuntimeFailureKindEnum.Clean, RuntimeFailureClassifier.Classify(0, null));
            }));

            cases.Add(Case("usage_limit_signatures", "Provider throttle/quota messages classify UsageLimit", TestTags.Positive, () =>
            {
                AssertEqual(RuntimeFailureKindEnum.UsageLimit, RuntimeFailureClassifier.Classify(1, "Error 429: Too Many Requests"));
                AssertEqual(RuntimeFailureKindEnum.UsageLimit, RuntimeFailureClassifier.Classify(1, "You have hit your usage limit for this model"));
                AssertEqual(RuntimeFailureKindEnum.UsageLimit, RuntimeFailureClassifier.Classify(1, "insufficient_quota: please check your billing"));
                AssertEqual(RuntimeFailureKindEnum.UsageLimit, RuntimeFailureClassifier.Classify(1, "Your credit balance is too low"));
            }));

            cases.Add(Case("auth_signatures", "Credential rejection messages classify AuthFailure", TestTags.Positive, () =>
            {
                AssertEqual(RuntimeFailureKindEnum.AuthFailure, RuntimeFailureClassifier.Classify(1, "401 Unauthorized"));
                AssertEqual(RuntimeFailureKindEnum.AuthFailure, RuntimeFailureClassifier.Classify(1, "Invalid API key provided"));
                AssertEqual(RuntimeFailureKindEnum.AuthFailure, RuntimeFailureClassifier.Classify(1, "authentication failed"));
            }));

            cases.Add(Case("unknown_failure_is_crash", "Non-zero exit with no signature classifies Crash", TestTags.Negative, () =>
            {
                AssertEqual(RuntimeFailureKindEnum.Crash, RuntimeFailureClassifier.Classify(1, "Segmentation fault"));
                AssertEqual(RuntimeFailureKindEnum.Crash, RuntimeFailureClassifier.Classify(139, "unexpected token in JSON"));
            }));

            cases.Add(Case("no_output_non_zero_is_crash", "Non-zero exit with empty output classifies Crash", TestTags.Negative, () =>
            {
                AssertEqual(RuntimeFailureKindEnum.Crash, RuntimeFailureClassifier.Classify(1, null));
                AssertEqual(RuntimeFailureKindEnum.Crash, RuntimeFailureClassifier.Classify(1, "   "));
            }));

            cases.Add(Case("usage_limit_wins_over_auth", "Usage-limit signature is checked before auth", TestTags.Positive, () =>
            {
                // A message mentioning both should resolve to the more recoverable UsageLimit.
                AssertEqual(RuntimeFailureKindEnum.UsageLimit, RuntimeFailureClassifier.Classify(1, "429 unauthorized-looking rate limit"));
            }));

            return new TestSuiteDescriptor(
                suiteId: "Services.RuntimeFailureClassifier",
                displayName: "Runtime Failure Classifier",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        private static TestCaseDescriptor Case(string caseId, string displayName, string tag, Action body)
        {
            return new TestCaseDescriptor(
                suiteId: "Services.RuntimeFailureClassifier",
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) =>
                {
                    body();
                    return Task.CompletedTask;
                },
                tags: new List<string> { tag });
        }

        #endregion
    }
}
