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
        /// Manually quarantine a captain visible to the caller. The write succeeds only while the captain is Idle or
        /// already quarantined and owns no mission, dock or process; otherwise nothing changes and the outcome is Busy.
        /// </summary>
        /// <param name="auth">Caller scope used to find the captain.</param>
        /// <param name="captainId">Captain identifier.</param>
        /// <param name="reason">Required operator reason.</param>
        /// <param name="untilUtc">Future UTC expiry, or null for an indefinite hold.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Typed outcome with the captain as read afterwards.</returns>
        Task<CaptainQuarantineResult> QuarantineCaptainAsync(AuthContext auth, string captainId, string? reason, DateTime? untilUtc, CancellationToken token = default);

        /// <summary>
        /// Release a quarantine on a captain visible to the caller. The write succeeds only while the captain is
        /// quarantined; a captain in any other state is left unchanged and the outcome is NotQuarantined.
        /// </summary>
        /// <param name="auth">Caller scope used to find the captain.</param>
        /// <param name="captainId">Captain identifier.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Typed outcome with the captain as read afterwards.</returns>
        Task<CaptainQuarantineResult> ReleaseCaptainAsync(AuthContext auth, string captainId, CancellationToken token = default);

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
