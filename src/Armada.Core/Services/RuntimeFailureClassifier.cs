namespace Armada.Core.Services
{
    using System;
    using Armada.Core.Enums;

    /// <summary>
    /// Pure classifier that reads a captain runtime's exit code and the tail of its output and decides
    /// whether the run ended cleanly, hit a provider usage limit, failed authentication, or crashed.
    /// Provider usage-limit and auth failures surface as generic non-zero exits across every CLI, so the
    /// only reliable discriminator is the message text. The provider signatures are the ones
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
            if (exitCode.HasValue && exitCode.Value == 0) return RuntimeFailureKindEnum.Clean;
            return ProviderQuotaLimitDetector.ClassifyProviderFault(tailOutput);
        }

        #endregion
    }
}
