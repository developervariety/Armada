namespace Armada.Core.Services
{
    using System;
    using Armada.Core.Enums;

    /// <summary>
    /// Pure classifier that reads a captain runtime's exit code and the tail of its output and decides
    /// whether the run ended cleanly, hit a provider usage limit, failed authentication, or crashed.
    /// Provider usage-limit and auth failures surface as generic non-zero exits across every CLI, so the
    /// only reliable discriminator is the message text; this scans for the signatures those providers
    /// emit. Side-effect free so it unit tests without launching anything.
    /// </summary>
    public static class RuntimeFailureClassifier
    {
        #region Private-Members

        // Substrings (lower-cased) that indicate the provider throttled or refused for quota/billing.
        private static readonly string[] _UsageLimitSignatures = new string[]
        {
            "rate limit",
            "rate-limit",
            "ratelimit",
            "too many requests",
            "429",
            "quota",
            "insufficient_quota",
            "insufficient quota",
            "usage limit",
            "usage-limit",
            "out of credit",
            "out of credits",
            "credit balance",
            "billing",
            "payment required",
            "402",
            "overloaded",
            "capacity",
        };

        // Substrings (lower-cased) that indicate a credential / authorization rejection.
        private static readonly string[] _AuthFailureSignatures = new string[]
        {
            "unauthorized",
            "401",
            "invalid api key",
            "invalid_api_key",
            "authentication failed",
            "authentication error",
            "auth failed",
            "forbidden",
            "403",
            "permission denied",
            "not authenticated",
            "no api key",
            "missing api key",
        };

        #endregion

        #region Public-Methods

        /// <summary>
        /// Classify a runtime exit. A zero exit code is always <see cref="RuntimeFailureKindEnum.Clean"/>.
        /// A non-zero exit is <see cref="RuntimeFailureKindEnum.UsageLimit"/> or
        /// <see cref="RuntimeFailureKindEnum.AuthFailure"/> when the tail output carries a matching
        /// signature (usage-limit checked first, as throttling is the more common and more recoverable
        /// case), otherwise <see cref="RuntimeFailureKindEnum.Crash"/>.
        /// </summary>
        /// <param name="exitCode">The process exit code (null is treated as a non-zero failure).</param>
        /// <param name="tailOutput">The tail of the process output; may be null or empty.</param>
        /// <returns>The classified failure kind.</returns>
        public static RuntimeFailureKindEnum Classify(int? exitCode, string? tailOutput)
        {
            if (exitCode.HasValue && exitCode.Value == 0) return RuntimeFailureKindEnum.Clean;

            if (String.IsNullOrWhiteSpace(tailOutput)) return RuntimeFailureKindEnum.Crash;

            string haystack = tailOutput.ToLowerInvariant();

            if (ContainsAny(haystack, _UsageLimitSignatures)) return RuntimeFailureKindEnum.UsageLimit;
            if (ContainsAny(haystack, _AuthFailureSignatures)) return RuntimeFailureKindEnum.AuthFailure;

            return RuntimeFailureKindEnum.Crash;
        }

        #endregion

        #region Private-Methods

        private static bool ContainsAny(string haystack, string[] needles)
        {
            foreach (string needle in needles)
            {
                if (haystack.Contains(needle, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        #endregion
    }
}
