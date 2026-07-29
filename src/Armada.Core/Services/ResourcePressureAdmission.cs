namespace Armada.Core.Services
{
    using System;
    using SyslogLogging;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;

    /// <summary>
    /// Small injectable resource-pressure admission policy. Combines a host
    /// memory probe with the active captain/build pressure count to admit or
    /// defer a captain launch. After a kernel OOM (exit 137) classification,
    /// admission is suspended for a cooldown window and only resumes once the
    /// memory probe reports capacity has returned.
    /// </summary>
    public sealed class ResourcePressureAdmission : IResourcePressureAdmission
    {
        #region Private-Members

        private string _Header = "[ResourcePressureAdmission] ";
        private ResourcePressureAdmissionSettings _Settings;
        private IResourcePressureProbe _Probe;
        private LoggingModule _Logging;
        private Func<DateTime> _NowUtcProvider;
        private readonly object _Lock = new object();
        private DateTime? _OomCooldownUntilUtc;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="settings">Admission settings.</param>
        /// <param name="probe">Resource-pressure probe.</param>
        /// <param name="logging">Logging module.</param>
        /// <param name="nowUtcProvider">Optional UTC clock for deterministic tests.</param>
        public ResourcePressureAdmission(
            ResourcePressureAdmissionSettings settings,
            IResourcePressureProbe probe,
            LoggingModule logging,
            Func<DateTime>? nowUtcProvider = null)
        {
            _Settings = settings ?? new ResourcePressureAdmissionSettings();
            _Probe = probe ?? new HostResourcePressureProbe();
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
            _NowUtcProvider = nowUtcProvider ?? (() => DateTime.UtcNow);
        }

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        public ResourcePressureDecision Evaluate(int activeBuildPressure)
        {
            ResourcePressureSnapshot snapshot = _Probe.Probe();
            DateTime now = _NowUtcProvider();

            lock (_Lock)
            {
                // Still inside an OOM suspension window: capacity has not returned.
                if (_OomCooldownUntilUtc.HasValue && now < _OomCooldownUntilUtc.Value)
                {
                    string reason = "Resource pressure: captain OOM (exit 137) detected; retry deferred until capacity returns"
                        + " (cooldown until " + _OomCooldownUntilUtc.Value.ToString("o") + ").";
                    _Logging.Warn(_Header + reason);
                    return Deferred(reason, snapshot);
                }

                // OOM suspension window has elapsed: release the capacity suspension
                // before evaluating normal pressure so retries may proceed.
                if (_OomCooldownUntilUtc.HasValue)
                {
                    _Logging.Info(_Header + "OOM cooldown elapsed; releasing resource-pressure capacity suspension");
                    _OomCooldownUntilUtc = null;
                }

                if (!_Settings.Enabled)
                {
                    return Admitted(snapshot);
                }

                long thresholdBytes = (long)_Settings.MinAvailableMemoryMb * 1024L * 1024L;
                if (snapshot.AvailableMemoryBytes.HasValue && snapshot.AvailableMemoryBytes.Value < thresholdBytes)
                {
                    string reason = "Resource pressure: available memory " + snapshot.AvailableMemoryBytes.Value
                        + " bytes is below minimum " + thresholdBytes + " bytes; deferring captain launch.";
                    _Logging.Warn(_Header + reason);
                    return Deferred(reason, snapshot);
                }

                if (_Settings.MaxConcurrentBuilds > 0 && activeBuildPressure >= _Settings.MaxConcurrentBuilds)
                {
                    string reason = "Resource pressure: active build/captain pressure " + activeBuildPressure
                        + " reached max " + _Settings.MaxConcurrentBuilds + "; deferring captain launch.";
                    _Logging.Warn(_Header + reason);
                    return Deferred(reason, snapshot);
                }

                return Admitted(snapshot);
            }
        }

        /// <inheritdoc />
        public void MarkOom()
        {
            DateTime now = _NowUtcProvider();
            lock (_Lock)
            {
                DateTime until = now.AddSeconds(_Settings.OomCooldownSeconds);
                _OomCooldownUntilUtc = until;
                _Logging.Warn(_Header + "kernel OOM (exit 137) classified; deferring captain launches until " + until.ToString("o"));
            }
        }

        /// <inheritdoc />
        public bool IsCapacitySuspended()
        {
            DateTime now = _NowUtcProvider();
            lock (_Lock)
            {
                return _OomCooldownUntilUtc.HasValue && now < _OomCooldownUntilUtc.Value;
            }
        }

        #endregion

        #region Private-Methods

        private static ResourcePressureDecision Admitted(ResourcePressureSnapshot snapshot)
        {
            return new ResourcePressureDecision { Admit = true, Reason = string.Empty, Snapshot = snapshot };
        }

        private static ResourcePressureDecision Deferred(string reason, ResourcePressureSnapshot snapshot)
        {
            return new ResourcePressureDecision { Admit = false, Reason = reason, Snapshot = snapshot };
        }

        #endregion
    }
}