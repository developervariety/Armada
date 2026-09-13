namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>
    /// Persists captain quarantine state and restores captains after the retry window.
    /// </summary>
    public sealed class CaptainQuarantineService : ICaptainQuarantineService
    {
        #region Public-Methods

        /// <inheritdoc />
        public bool IsQuarantined(Captain captain)
        {
            if (captain == null) throw new ArgumentNullException(nameof(captain));

            if (captain.State == CaptainStateEnum.Quarantined)
            {
                return true;
            }

            return captain.QuarantineUntilUtc.HasValue && captain.QuarantineUntilUtc.Value > DateTime.UtcNow;
        }

        /// <inheritdoc />
        public async Task QuarantineAsync(Captain captain, string reason, DateTime? retryAfterUtc, CancellationToken token = default)
        {
            if (captain == null) throw new ArgumentNullException(nameof(captain));
            if (String.IsNullOrWhiteSpace(reason)) throw new ArgumentException("Quarantine reason is required.", nameof(reason));

            // Quota/backoff quarantines always carry a finite retry window (coerced to a default
            // backoff when the provider gave no retry-after) so the restore sweep can auto-recover them.
            await ApplyQuarantineAsync(captain, reason, ResolveRetryAfterUtc(retryAfterUtc), token).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<bool> TryQuarantineCrashLoopAsync(string captainId, string reason, DateTime untilUtc, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(captainId)) throw new ArgumentException("Captain id is required.", nameof(captainId));
            if (String.IsNullOrWhiteSpace(reason)) throw new ArgumentException("Quarantine reason is required.", nameof(reason));
            DateTime expiry = untilUtc.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(untilUtc, DateTimeKind.Utc)
                : untilUtc.ToUniversalTime();
            if (expiry <= DateTime.UtcNow) throw new ArgumentException("Crash-loop expiry must be in the future.", nameof(untilUtc));
            bool applied = await _Database.Captains.TryQuarantineIdleAsync(captainId, reason, expiry, token, true).ConfigureAwait(false);
            if (applied)
            {
                _Logging.Warn(_Header + "captain " + captainId + " crash-loop hold applied untilUtc=" + expiry.ToString("O"));
            }
            else
            {
                _Logging.Warn(_Header + "captain " + captainId + " crash-loop hold refused because its existing hold or ownership is stronger");
            }
            return applied;
        }

        /// <inheritdoc />
        public async Task<CaptainQuarantineResult> QuarantineCaptainAsync(AuthContext auth, string captainId, string? reason, DateTime? untilUtc, CancellationToken token = default)
        {
            if (auth == null) throw new ArgumentNullException(nameof(auth));
            if (String.IsNullOrWhiteSpace(captainId))
                return new CaptainQuarantineResult(CaptainQuarantineOutcomeEnum.InvalidRequest, null, "Captain id is required.");

            Captain? captain = await ReadScopedAsync(auth, captainId, token).ConfigureAwait(false);
            if (captain == null)
                return new CaptainQuarantineResult(CaptainQuarantineOutcomeEnum.NotFound, null, "Captain not found: " + captainId);

            if (String.IsNullOrWhiteSpace(reason))
                return new CaptainQuarantineResult(CaptainQuarantineOutcomeEnum.InvalidRequest, captain, "A reason is required so the quarantine is auditable.");

            // An expiry without a zone is taken as UTC, so the hold length does not depend on the server's local time zone.
            DateTime? expiry = null;
            if (untilUtc.HasValue)
                expiry = untilUtc.Value.Kind == DateTimeKind.Unspecified
                    ? DateTime.SpecifyKind(untilUtc.Value, DateTimeKind.Utc)
                    : untilUtc.Value.ToUniversalTime();
            if (expiry.HasValue && expiry.Value <= DateTime.UtcNow)
                return new CaptainQuarantineResult(CaptainQuarantineOutcomeEnum.InvalidRequest, captain, "The quarantine expiry must be in the future.");

            // The conditional write is the ownership guard: it matches only an Idle or already-quarantined captain
            // with no mission, dock or process, so a captain claimed or started after the read above keeps its work.
            bool applied = await _Database.Captains.TryQuarantineIdleAsync(captain.Id, reason, expiry, token).ConfigureAwait(false);
            Captain? after = await ReadScopedAsync(auth, captain.Id, token).ConfigureAwait(false);
            if (!applied)
            {
                string state = after != null ? after.State.ToString() : "unknown";
                _Logging.Warn(_Header + "manual quarantine refused captainId=" + captain.Id + " state=" + state + " (captain owns work or is not Idle)");
                return new CaptainQuarantineResult(
                    after == null ? CaptainQuarantineOutcomeEnum.NotFound : CaptainQuarantineOutcomeEnum.Busy,
                    after,
                    "Captain " + captain.Id + " was not quarantined: only an Idle or already-quarantined captain that owns no mission, dock or process can be held (current state " + state + "). Stop the captain first.");
            }

            _Logging.Warn(_Header + "captain manually quarantined captainId=" + captain.Id + " untilUtc=" + (expiry.HasValue ? expiry.Value.ToString("O") : "indefinite"));
            return new CaptainQuarantineResult(CaptainQuarantineOutcomeEnum.Quarantined, after, "Captain quarantined.");
        }

        /// <inheritdoc />
        public async Task<CaptainQuarantineResult> ReleaseCaptainAsync(AuthContext auth, string captainId, CancellationToken token = default)
        {
            if (auth == null) throw new ArgumentNullException(nameof(auth));
            if (String.IsNullOrWhiteSpace(captainId))
                return new CaptainQuarantineResult(CaptainQuarantineOutcomeEnum.InvalidRequest, null, "Captain id is required.");

            Captain? captain = await ReadScopedAsync(auth, captainId, token).ConfigureAwait(false);
            if (captain == null)
                return new CaptainQuarantineResult(CaptainQuarantineOutcomeEnum.NotFound, null, "Captain not found: " + captainId);

            bool released = await _Database.Captains.TryReleaseQuarantineAsync(captain.Id, token).ConfigureAwait(false);
            Captain? after = await ReadScopedAsync(auth, captain.Id, token).ConfigureAwait(false);
            if (!released)
            {
                string state = after != null ? after.State.ToString() : "unknown";
                return new CaptainQuarantineResult(
                    after == null ? CaptainQuarantineOutcomeEnum.NotFound : CaptainQuarantineOutcomeEnum.NotQuarantined,
                    after,
                    "Captain " + captain.Id + " is not quarantined (state " + state + "); nothing changed.");
            }

            _Logging.Info(_Header + "quarantine manually released captainId=" + captain.Id);
            return new CaptainQuarantineResult(CaptainQuarantineOutcomeEnum.Released, after, "Quarantine released.");
        }

        /// <inheritdoc />
        public async Task RestoreExpiredQuarantinesAsync(CancellationToken token = default)
        {
            List<Captain> quarantinedCaptains = await _Database.Captains.EnumerateByStateAsync(CaptainStateEnum.Quarantined, token).ConfigureAwait(false);
            if (quarantinedCaptains.Count == 0)
            {
                return;
            }

            bool probeEnabled = _Settings.CaptainQuarantine.UseProbeOnRestore && _Probe != null;
            DateTime nowUtc = DateTime.UtcNow;

            foreach (Captain captain in quarantinedCaptains)
            {
                if (!captain.QuarantineUntilUtc.HasValue)
                {
                    // An indefinite manual hold (null window) is an operator bench, not a quota/backoff
                    // condition. Never auto-restore it and never probe it; only an explicit manual release
                    // clears it. This keeps operator benches from being un-benched mid-voyage.
                    continue;
                }

                if (probeEnabled)
                {
                    // Probe-driven restore: a successful probe can return the captain to service
                    // early, before its bench window elapses; a failed probe keeps it quarantined.
                    await TryProbeRestoreAsync(captain, token).ConfigureAwait(false);
                    continue;
                }

                if (captain.QuarantineUntilUtc.Value > nowUtc)
                {
                    continue;
                }

                await ReleaseTimedAsync(captain, nowUtc, token).ConfigureAwait(false);
            }
        }

        /// <inheritdoc />
        public async Task<bool> TryProbeRestoreAsync(Captain captain, CancellationToken token = default)
        {
            if (captain == null) throw new ArgumentNullException(nameof(captain));

            if (_Probe == null)
            {
                return false;
            }

            bool recovered;
            try
            {
                recovered = await _Probe.HasRecoveredAsync(captain, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "quota probe threw for captainId=" + captain.Id + "; leaving quarantined: " + ex.Message);
                return false;
            }

            if (!recovered)
            {
                _Logging.Debug(_Header + "quota probe negative; captain stays quarantined captainId=" + captain.Id);
                return false;
            }

            _Logging.Info(_Header + "quota probe positive; restoring captain early captainId=" + captain.Id);
            return await ReleaseTimedAsync(captain, null, token).ConfigureAwait(false);
        }

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Creates the quarantine service.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="settings">Application settings.</param>
        /// <param name="logging">Logging module.</param>
        /// <param name="probe">Optional quota probe used for early restore; null disables probe-driven restore.</param>
        public CaptainQuarantineService(DatabaseDriver database, ArmadaSettings settings, LoggingModule logging, ICaptainQuotaProbe? probe = null)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
            _Probe = probe;
        }

        #endregion

        #region Private-Methods

        private async Task ApplyQuarantineAsync(Captain captain, string reason, DateTime? untilUtc, CancellationToken token)
        {
            captain.State = CaptainStateEnum.Quarantined;
            captain.QuarantineUntilUtc = untilUtc; // null = indefinite manual hold (cleared only by explicit unbench)
            captain.QuarantineReason = reason.Trim();
            captain.CurrentMissionId = null;
            captain.CurrentDockId = null;
            captain.ProcessId = null;
            captain.LastUpdateUtc = DateTime.UtcNow;

            await _Database.Captains.UpdateAsync(captain, token).ConfigureAwait(false);
            _Logging.Warn(_Header + "captain quarantined captainId=" + captain.Id + " untilUtc=" + (untilUtc.HasValue ? untilUtc.Value.ToString("O") : "indefinite"));
        }

        /// <summary>
        /// Release a timed hold for the expiry sweep or the quota probe. The write matches only a captain that is still
        /// quarantined with an expiry (at or before the cutoff when given), so an indefinite hold or other change made
        /// after the sweep read the captain is never released from that earlier read.
        /// </summary>
        private async Task<bool> ReleaseTimedAsync(Captain captain, DateTime? expiredAtOrBeforeUtc, CancellationToken token)
        {
            bool released = await _Database.Captains.TryReleaseTimedQuarantineAsync(captain.Id, expiredAtOrBeforeUtc, token).ConfigureAwait(false);
            if (!released)
            {
                _Logging.Warn(_Header + "timed quarantine release skipped captainId=" + captain.Id + " (the hold changed or is no longer timed)");
                return false;
            }

            captain.State = CaptainStateEnum.Idle;
            captain.QuarantineUntilUtc = null;
            captain.QuarantineReason = null;
            captain.LastUpdateUtc = DateTime.UtcNow;
            _Logging.Info(_Header + "quarantine cleared captainId=" + captain.Id);
            return true;
        }

        private async Task<Captain?> ReadScopedAsync(AuthContext auth, string captainId, CancellationToken token)
        {
            if (auth.IsAdmin) return await _Database.Captains.ReadAsync(captainId, token).ConfigureAwait(false);
            if (String.IsNullOrWhiteSpace(auth.TenantId)) return null;
            if (auth.IsTenantAdmin) return await _Database.Captains.ReadAsync(auth.TenantId, captainId, token).ConfigureAwait(false);
            if (String.IsNullOrWhiteSpace(auth.UserId)) return null;
            return await _Database.Captains.ReadAsync(auth.TenantId, auth.UserId, captainId, token).ConfigureAwait(false);
        }

        private DateTime ResolveRetryAfterUtc(DateTime? retryAfterUtc)
        {
            DateTime nowUtc = DateTime.UtcNow;
            if (retryAfterUtc.HasValue && retryAfterUtc.Value > nowUtc)
            {
                return retryAfterUtc.Value.ToUniversalTime();
            }

            return nowUtc.AddSeconds(_Settings.CaptainQuarantine.DefaultBackoffSeconds);
        }

        #endregion

        #region Private-Members

        private readonly DatabaseDriver _Database;
        private readonly ArmadaSettings _Settings;
        private readonly LoggingModule _Logging;
        private readonly ICaptainQuotaProbe? _Probe;
        private const string _Header = "[CaptainQuarantineService] ";

        #endregion
    }
}
