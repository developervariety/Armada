namespace Armada.Core.Services.Interfaces
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;

    /// <summary>
    /// Quarantines captains that hit provider usage limits and restores them after the retry window.
    /// </summary>
    public interface ICaptainQuarantineService
    {
        /// <summary>
        /// Returns true when the captain must be skipped for assignment.
        /// </summary>
        /// <param name="captain">Captain to evaluate.</param>
        /// <returns>True when the captain is quarantined.</returns>
        bool IsQuarantined(Captain captain);

        /// <summary>
        /// Marks the captain quarantined until <paramref name="retryAfterUtc"/> or the configured default backoff.
        /// </summary>
        /// <param name="captain">Captain to quarantine.</param>
        /// <param name="reason">Operator-visible reason.</param>
        /// <param name="retryAfterUtc">Provider-published retry time, if known.</param>
        /// <param name="token">Cancellation token.</param>
        Task QuarantineAsync(Captain captain, string reason, DateTime? retryAfterUtc, CancellationToken token = default);

        /// <summary>
        /// Clears quarantine fields and returns the captain to Idle.
        /// </summary>
        /// <param name="captain">Captain to restore.</param>
        /// <param name="token">Cancellation token.</param>
        Task ClearQuarantineAsync(Captain captain, CancellationToken token = default);

        /// <summary>
        /// Restores captains whose quarantine window has elapsed.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        Task RestoreExpiredQuarantinesAsync(CancellationToken token = default);

        /// <summary>
        /// Probes a quarantined captain's provider quota and restores it early when the probe
        /// reports recovery. A failed probe leaves the captain quarantined.
        /// </summary>
        /// <param name="captain">Quarantined captain to probe.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>True when the probe succeeded and the captain was restored; otherwise false.</returns>
        Task<bool> TryProbeRestoreAsync(Captain captain, CancellationToken token = default);
    }
}
