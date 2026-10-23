namespace Armada.Core.Services
{
    using System;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// Pure classifier that reads a captain runtime's exit code and the tail of its output and decides
    /// whether the run ended cleanly, hit a provider usage limit, failed authentication, or crashed.
    /// Provider usage-limit and auth failures surface as generic non-zero exits across every CLI. The
    /// classifier combines message text with typed provider evidence when available. The signatures are the ones
    /// <see cref="ProviderQuotaLimitDetector"/> defines, so the crash-loop decision and the captain bench
    /// decision read one definition. Side-effect free so it unit tests without launching anything.
    /// </summary>
    public static class RuntimeFailureClassifier
    {
        #region Public-Methods

        /// <summary>
        /// Classify a runtime exit. A zero exit code is always <see cref="RuntimeFailureKindEnum.Clean"/>.
        /// A non-zero exit is <see cref="RuntimeFailureKindEnum.UsageLimit"/> or
        /// <see cref="RuntimeFailureKindEnum.AuthFailure"/> when the tail output carries a provider signature
        /// (usage limit checked first, as throttling is the more common and more recoverable case), otherwise
        /// <see cref="RuntimeFailureKindEnum.Crash"/>.
        /// </summary>
        /// <param name="exitCode">The process exit code (null is treated as a non-zero failure).</param>
        /// <param name="tailOutput">The tail of the process output; may be null or empty.</param>
        /// <returns>The classified failure kind.</returns>
        public static RuntimeFailureKindEnum Classify(int? exitCode, string? tailOutput)
        {
            return Classify(exitCode, tailOutput, null);
        }

        /// <summary>Classify a runtime exit with typed provider evidence when the provider supplied it.</summary>
        /// <param name="exitCode">The process exit code (null is treated as a non-zero failure).</param>
        /// <param name="tailOutput">The tail of the process output; may be null or empty.</param>
        /// <param name="providerError">Typed provider evidence, or null when none was captured.</param>
        /// <returns>The classified failure kind.</returns>
        public static RuntimeFailureKindEnum Classify(int? exitCode, string? tailOutput, RuntimeProviderError? providerError)
        {
            if (exitCode.HasValue && exitCode.Value == 0) return RuntimeFailureKindEnum.Clean;
            return ProviderQuotaLimitDetector.ClassifyProviderFault(tailOutput, providerError);
        }

        /// <summary>
        /// Keep the stronger recognized provider error seen during one process run. Equal-ranked evidence keeps
        /// the first value, so a later generic event cannot erase useful details.
        /// </summary>
        /// <param name="current">Evidence already captured for the process.</param>
        /// <param name="next">Newly captured evidence.</param>
        /// <returns>The stronger recognized evidence, or current on a tie.</returns>
        public static RuntimeProviderError? PreferProviderError(RuntimeProviderError? current, RuntimeProviderError? next)
        {
            int currentRank = ProviderErrorRank(current);
            int nextRank = ProviderErrorRank(next);
            return nextRank > currentRank ? next : current;
        }

        private static int ProviderErrorRank(RuntimeProviderError? providerError)
        {
            if (ProviderQuotaLimitDetector.IsProviderQuotaError(providerError)) return 4;
            if (ProviderQuotaLimitDetector.IsProviderCreditError(providerError)) return 3;
            if (ProviderQuotaLimitDetector.IsProviderAuthError(providerError)) return 2;
            return providerError == null ? 0 : 1;
        }

        #endregion
    }
}
