namespace Armada.Core.Services
{
    using System;
    using Armada.Core.Models;

    /// <summary>
    /// Recognizes the failure reasons of a landing the boundary or leak gates refused. Such a mission holds work
    /// Armada declined to land, so no later proof about the default branch may read it as landed.
    /// </summary>
    public static class LandingRefusalReasons
    {
        /// <summary>
        /// Prefix of the reason recorded when the dock-boundary gate blocks a landing.
        /// </summary>
        public const string DockBoundaryPrefix = "Dock-boundary gate failed";

        /// <summary>
        /// Whether a failure reason is a landing refusal: unreadable landing evidence, a dock-boundary finding, or a
        /// protected-path change.
        /// </summary>
        /// <param name="failureReason">The mission's failure reason.</param>
        /// <returns>True for a refusal; false for any other reason or none.</returns>
        public static bool IsRefusal(string? failureReason)
        {
            if (String.IsNullOrWhiteSpace(failureReason)) return false;
            string reason = failureReason.TrimStart();
            return reason.StartsWith(LandingEvidence.RefusalPrefix, StringComparison.Ordinal)
                || reason.StartsWith(DockBoundaryPrefix, StringComparison.Ordinal)
                || reason.StartsWith(ProtectedPathsValidator.FailureReasonPrefix, StringComparison.Ordinal);
        }
    }
}
