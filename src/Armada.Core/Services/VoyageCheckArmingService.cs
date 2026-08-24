namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>
    /// Creates the Check records a freshly dispatched voyage is armed with.
    /// </summary>
    /// <remarks>
    /// This is the single arming seam. Every path that starts a voyage calls it, because a voyage
    /// dispatched without armed Checks reaches its Judge with nothing to satisfy the gate, and the
    /// operator then has to attach a Check by hand hours later. An earlier arrangement placed the
    /// arming inside one dispatch caller, so voyages started by the autonomous objective scheduler
    /// - which dispatches through the admiral directly - were armed with nothing at all.
    /// <para>
    /// A record is created Pending with no command and no branch. That is the intended armed state,
    /// not an incomplete one: at dispatch there is no branch to measure. The executor stamps the
    /// command and the branch onto the record once a stage has committed, and runs it against that
    /// work.
    /// </para>
    /// </remarks>
    public sealed class VoyageCheckArmingService
    {
        #region Private-Members

        private const string _Header = "[VoyageCheckArmingService] ";
        private readonly DatabaseDriver _Database;
        private readonly ArmadaSettings? _Settings;
        private readonly LoggingModule? _Logging;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate the arming service.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="settings">Armada settings. A null or disabled arming section arms nothing.</param>
        /// <param name="logging">Optional logging module.</param>
        public VoyageCheckArmingService(DatabaseDriver database, ArmadaSettings? settings, LoggingModule? logging)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Settings = settings;
            _Logging = logging;
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Arm the voyage's Checks from the vessel's resolved workflow profile.
        /// </summary>
        /// <param name="voyage">The dispatched voyage.</param>
        /// <param name="vessel">The vessel the voyage runs on.</param>
        /// <param name="source">Which dispatch path armed these, for the log line.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The number of Check records created.</returns>
        public async Task<int> ArmAsync(Voyage voyage, Vessel vessel, string source, CancellationToken token = default)
        {
            if (voyage == null) throw new ArgumentNullException(nameof(voyage));
            if (vessel == null) throw new ArgumentNullException(nameof(vessel));

            try
            {
                VoyageCheckArmingSettings? arming = _Settings?.VoyageCheckArming;
                if (arming == null || !arming.Enabled) return 0;

                AuthContext auth = AuthContext.Authenticated(
                    voyage.TenantId ?? vessel.TenantId ?? "default",
                    voyage.UserId ?? "default",
                    true,
                    true,
                    "system");

                WorkflowProfileService profiles = new WorkflowProfileService(_Database, _Logging!);
                WorkflowProfile? profile = await profiles.ResolveForVesselAsync(auth, vessel, null, token).ConfigureAwait(false);

                IReadOnlyList<CheckRunTypeEnum> planned = VoyageCheckArmingPlan.Resolve(arming, profile, null);
                if (planned.Count == 0)
                {
                    _Logging?.Info(_Header + "checks_armed voyage " + voyage.Id + " armed=0 source=" + source
                        + (profile == null ? " reason=no_workflow_profile" : " reason=no_matching_commands"));
                    return 0;
                }

                foreach (CheckRunTypeEnum type in planned)
                {
                    CheckRun run = new CheckRun
                    {
                        TenantId = voyage.TenantId,
                        UserId = voyage.UserId,
                        VesselId = vessel.Id,
                        VoyageId = voyage.Id,
                        WorkflowProfileId = profile?.Id,
                        Type = type,
                        Source = CheckRunSourceEnum.Armada,
                        Status = CheckRunStatusEnum.Pending,
                        Label = type.ToString() + " (armed at dispatch)"
                    };

                    await _Database.CheckRuns.CreateAsync(run, token).ConfigureAwait(false);
                }

                _Logging?.Info(_Header + "checks_armed voyage " + voyage.Id + " armed=" + planned.Count + " source=" + source);
                return planned.Count;
            }
            catch (Exception ex)
            {
                _Logging?.Warn(_Header + "could not arm Checks for voyage " + voyage.Id + " source=" + source + ": " + ex.Message);
                return 0;
            }
        }

        #endregion
    }
}
