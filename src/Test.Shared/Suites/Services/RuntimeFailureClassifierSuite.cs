namespace Test.Shared.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
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

            cases.Add(Case("typed_provider_errors_and_text_precedence", "Typed provider evidence classifies exits and text quota remains first", TestTags.Positive, () =>
            {
                RuntimeProviderError auth = new RuntimeProviderError { HttpStatusCode = 401 };
                RuntimeProviderError quota = new RuntimeProviderError { ErrorType = "rate_limit_error" };
                RuntimeProviderError credit = new RuntimeProviderError { HttpStatusCode = 402, ErrorType = "billing_error" };
                AssertEqual(RuntimeFailureKindEnum.AuthFailure, RuntimeFailureClassifier.Classify(1, "neutral output", auth));
                AssertEqual(RuntimeFailureKindEnum.UsageLimit, RuntimeFailureClassifier.Classify(1, "neutral output", credit));
                AssertEqual(RuntimeFailureKindEnum.UsageLimit, RuntimeFailureClassifier.Classify(1, "HTTP 429 rate limit", auth));
                AssertEqual(RuntimeFailureKindEnum.UsageLimit, RuntimeFailureClassifier.Classify(1, "401 Unauthorized", quota));
                AssertEqual(RuntimeFailureKindEnum.Crash, RuntimeFailureClassifier.Classify(1, "neutral output", new RuntimeProviderError { HttpStatusCode = 500 }));
                AssertEqual(RuntimeFailureKindEnum.Clean, RuntimeFailureClassifier.Classify(0, "HTTP 429 rate limit", auth));
                foreach (int status in new[] { 429, 529, 402 })
                    AssertEqual(RuntimeFailureKindEnum.UsageLimit, RuntimeFailureClassifier.Classify(1, "neutral output", new RuntimeProviderError { HttpStatusCode = status }), "typed quota/credit status " + status);
                foreach (int status in new[] { 401, 403 })
                    AssertEqual(RuntimeFailureKindEnum.AuthFailure, RuntimeFailureClassifier.Classify(1, "neutral output", new RuntimeProviderError { HttpStatusCode = status }), "typed auth status " + status);
                foreach (string type in new[] { "rate_limit_error", "rate_limit_exceeded", "insufficient_quota", "usage_limit_reached", "overloaded_error", "billing_error" })
                    AssertEqual(RuntimeFailureKindEnum.UsageLimit, RuntimeFailureClassifier.Classify(1, "neutral output", new RuntimeProviderError { ErrorType = type }), "typed quota/credit type " + type);
                foreach (string type in new[] { "authentication_error", "permission_error", "invalid_api_key" })
                    AssertEqual(RuntimeFailureKindEnum.AuthFailure, RuntimeFailureClassifier.Classify(1, "neutral output", new RuntimeProviderError { ErrorType = type }), "typed auth type " + type);
                foreach (RuntimeProviderError unknown in new[]
                {
                    new RuntimeProviderError { HttpStatusCode = 404 },
                    new RuntimeProviderError { HttpStatusCode = 500 },
                    new RuntimeProviderError { ErrorType = "server_error" },
                    new RuntimeProviderError { ErrorType = "rate_limit_error: try again" }
                })
                    AssertEqual(RuntimeFailureKindEnum.Crash, RuntimeFailureClassifier.Classify(1, "neutral output", unknown), "unknown typed evidence stays a crash");
            }));

            cases.Add(Case("captured_provider_evidence_keeps_stronger_result", "A later generic error cannot erase a stronger captured provider error", TestTags.Positive, () =>
            {
                RuntimeProviderError auth = new RuntimeProviderError { HttpStatusCode = 401 };
                RuntimeProviderError quota = new RuntimeProviderError { ErrorType = "rate_limit_error" };
                RuntimeProviderError generic = new RuntimeProviderError { HttpStatusCode = 500 };
                AssertTrue(Object.ReferenceEquals(quota, RuntimeFailureClassifier.PreferProviderError(auth, quota)), "quota replaces auth");
                AssertTrue(Object.ReferenceEquals(quota, RuntimeFailureClassifier.PreferProviderError(quota, generic)), "generic error does not replace quota");
                AssertTrue(Object.ReferenceEquals(auth, RuntimeFailureClassifier.PreferProviderError(auth, new RuntimeProviderError { HttpStatusCode = 403 })), "equal-rank auth keeps the first detail");
            }));

            cases.Add(Case("typed_retry_evidence_keeps_throttle_and_cap_timing", "Typed throttle uses the short backoff while caps and published reset times keep their windows", TestTags.Positive, () =>
            {
                DateTime reference = new DateTime(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);
                RuntimeProviderError typed429 = new RuntimeProviderError { HttpStatusCode = 429 };
                RuntimeProviderError typed529 = new RuntimeProviderError { HttpStatusCode = 529 };
                DateTime? plainText429Deadline = ProviderQuotaLimitDetector.ResolveQuotaRetryAfterUtc("HTTP 429", Armada.Core.Enums.AgentRuntimeEnum.ClaudeCode, reference);
                AssertNull(plainText429Deadline, "plain text 429 uses configured short backoff");
                AssertEqual(plainText429Deadline, ProviderQuotaLimitDetector.ResolveQuotaRetryAfterUtc("neutral output", Armada.Core.Enums.AgentRuntimeEnum.ClaudeCode, reference, typed429), "typed 429 matches the existing plain text throttle behavior");
                AssertEqual(reference.AddHours(5), ProviderQuotaLimitDetector.ResolveQuotaRetryAfterUtc("HTTP 429: rate limit reached", Armada.Core.Enums.AgentRuntimeEnum.ClaudeCode, reference), "text rate-limit wording retains its existing runtime fallback semantics");
                AssertNull(ProviderQuotaLimitDetector.ResolveQuotaRetryAfterUtc("neutral output", Armada.Core.Enums.AgentRuntimeEnum.OpenCode, reference, typed529), "typed 529 overload uses configured short backoff");

                DateTime? explicitReset = ProviderQuotaLimitDetector.ResolveQuotaRetryAfterUtc(
                    "HTTP 429 rate limit reached. Retry-After: 120", Armada.Core.Enums.AgentRuntimeEnum.ClaudeCode, reference, typed429);
                AssertEqual(reference.AddSeconds(120), explicitReset, "published retry time remains first");
                AssertEqual(reference.AddHours(5), ProviderQuotaLimitDetector.ResolveQuotaRetryAfterUtc(
                    "neutral output", Armada.Core.Enums.AgentRuntimeEnum.ClaudeCode, reference,
                    new RuntimeProviderError { ErrorType = "usage_limit_reached" }), "typed usage cap retains the Claude fallback window");
                AssertEqual(reference.AddHours(5), ProviderQuotaLimitDetector.ResolveQuotaRetryAfterUtc(
                    "You've hit your usage limit", Armada.Core.Enums.AgentRuntimeEnum.ClaudeCode, reference, typed429), "text cap wins over typed throttle");
            }));

            cases.Add(Case("crash_output_is_not_a_provider_limit", "A crash that mentions capacity, billing, a status-like number or a filesystem permission stays Crash", TestTags.Negative, () =>
            {
                foreach (string crash in CrashFixtures)
                {
                    AssertEqual(RuntimeFailureKindEnum.Crash, RuntimeFailureClassifier.Classify(134, crash), "must stay a crash so crash-loop quarantine can fire: " + crash);
                }
            }));

            cases.Add(Case("provider_errors_are_provider_limits", "Provider throttle, overload, quota, credit and auth errors classify as provider faults", TestTags.Positive, () =>
            {
                foreach (string limit in UsageLimitFixtures)
                {
                    AssertEqual(RuntimeFailureKindEnum.UsageLimit, RuntimeFailureClassifier.Classify(1, limit), "provider limit: " + limit);
                }
                foreach (string auth in AuthFixtures)
                {
                    AssertEqual(RuntimeFailureKindEnum.AuthFailure, RuntimeFailureClassifier.Classify(1, auth), "provider auth failure: " + auth);
                }
            }));

            cases.Add(Case("bench_decision_and_crash_classification_agree", "At the same exit, the captain bench predicate and the crash classification never disagree", TestTags.Positive, () =>
            {
                List<string> all = new List<string>();
                all.AddRange(CrashFixtures);
                all.AddRange(UsageLimitFixtures);
                all.AddRange(AuthFixtures);
                foreach (string text in all)
                {
                    // The terminal exit path benches on these predicates; the crash-loop tracker counts a Crash.
                    bool benches = ProviderQuotaLimitDetector.IsQuotaLimitSignal(text)
                        || ProviderQuotaLimitDetector.IsCreditAuthBenchSignal(text)
                        || ProviderQuotaLimitDetector.IsProviderAccountSpendLimitSignal(text);
                    bool crash = RuntimeFailureClassifier.Classify(1, text) == RuntimeFailureKindEnum.Crash;
                    AssertEqual(benches, !crash, "bench and crash classification disagree for: " + text);
                }
            }));

            return new TestSuiteDescriptor(
                suiteId: "Services.RuntimeFailureClassifier",
                displayName: "Runtime Failure Classifier",
                cases: cases);
        }

        #endregion

        #region Private-Members

        // Real crash-loop output shapes that name a capacity, a billing type, a line number, a process id or a
        // filesystem permission. None is a provider limit.
        private static readonly string[] CrashFixtures = new string[]
        {
            "Unhandled exception. System.InvalidOperationException: Pool capacity exceeded at Worker.Run()",
            "System.NullReferenceException at BillingTests.Setup() in BillingTests.cs:line 42",
            "Segmentation fault (core dumped) pid 14290",
            "at Parser.Read() in Parser.cs:line 4291",
            "Unhandled exception. System.UnauthorizedAccessException: Access to the path is denied.",
            "bash: ./run.sh: Permission denied",
            "error: channel capacity 403 exceeded in queue",
            "Loaded 402 files, 1401 symbols; aborting on assertion",
            "write failed: Disk quota exceeded",
            "Unhandled exception. RateLimiter.Acquire threw ArgumentOutOfRangeException"
        };

        // Provider throttle, overload, quota, spend and credit errors as runtimes print them.
        private static readonly string[] UsageLimitFixtures = new string[]
        {
            "stream error: exceeded retry limit, last status: 429 Too Many Requests",
            "API Error: 529 {\"type\":\"error\",\"error\":{\"type\":\"overloaded_error\",\"message\":\"Overloaded\"}}",
            "Error 429: Too Many Requests",
            "HTTP 429: rate limit reached, slow down",
            "The model is currently overloaded. Please try again later.",
            "You've hit your usage limit. Try again at 3:05 PM.",
            "Error: monthly quota exceeded for this org",
            "{\"error\":{\"code\":\"insufficient_quota\"}}",
            "RESOURCE_EXHAUSTED: Quota exceeded for aiplatform requests",
            "Your credit balance is too low to access the API",
            "API Error: 402 Insufficient balance. Please top up your account.",
            "Daily spend limit of $2000.00 reached for this user"
        };

        // Provider credential rejections.
        private static readonly string[] AuthFixtures = new string[]
        {
            "401 Unauthorized",
            "Invalid API key provided",
            "authentication failed",
            "Failed to authenticate. API Error: 403",
            "HTTP 403 Forbidden: this key cannot access the model",
            "Not logged in. Please run /login"
        };

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
