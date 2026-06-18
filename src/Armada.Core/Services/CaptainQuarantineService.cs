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

            DateTime untilUtc = ResolveRetryAfterUtc(retryAfterUtc);
            captain.State = CaptainStateEnum.Quarantined;
            captain.QuarantineUntilUtc = untilUtc;
            captain.QuarantineReason = reason.Trim();
            captain.CurrentMissionId = null;
            captain.CurrentDockId = null;
            captain.ProcessId = null;
            captain.LastUpdateUtc = DateTime.UtcNow;

            await _Database.Captains.UpdateAsync(captain, token).ConfigureAwait(false);
            _Logging.Warn(_Header + "captain quarantined captainId=" + captain.Id + " untilUtc=" + untilUtc.ToString("O"));
        }

        /// <inheritdoc />
        public async Task ClearQuarantineAsync(Captain captain, CancellationToken token = default)
        {
            if (captain == null) throw new ArgumentNullException(nameof(captain));

            captain.State = CaptainStateEnum.Idle;
            captain.QuarantineUntilUtc = null;
            captain.QuarantineReason = null;
            captain.LastUpdateUtc = DateTime.UtcNow;

            await _Database.Captains.UpdateAsync(captain, token).ConfigureAwait(false);
            _Logging.Info(_Header + "quarantine cleared captainId=" + captain.Id);
        }

        /// <inheritdoc />
        public async Task RestoreExpiredQuarantinesAsync(CancellationToken token = default)
        {
            List<Captain> quarantinedCaptains = await _Database.Captains.EnumerateByStateAsync(CaptainStateEnum.Quarantined, token).ConfigureAwait(false);
            if (quarantinedCaptains.Count == 0)
            {
                return;
            }

            DateTime nowUtc = DateTime.UtcNow;
            foreach (Captain captain in quarantinedCaptains)
            {
                if (captain.QuarantineUntilUtc.HasValue && captain.QuarantineUntilUtc.Value > nowUtc)
                {
                    continue;
                }

                if (_Settings.CaptainQuarantine.UseProbeOnRestore)
                {
                    _Logging.Info(_Header + "probe-on-restore enabled; clearing quarantine for captainId=" + captain.Id);
                }

                await ClearQuarantineAsync(captain, token).ConfigureAwait(false);
            }
        }

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Creates the quarantine service.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="settings">Application settings.</param>
        /// <param name="logging">Logging module.</param>
        public CaptainQuarantineService(DatabaseDriver database, ArmadaSettings settings, LoggingModule logging)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
        }

        #endregion

        #region Private-Methods

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
        private const string _Header = "[CaptainQuarantineService] ";

        #endregion
    }
}
