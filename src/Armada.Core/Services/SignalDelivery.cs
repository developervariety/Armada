namespace Armada.Core.Services
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// How operator mail and a brief edit reach a captain. No shipped runtime injects either into a
    /// process that is already running: CLI runtimes close stdin after the launch prompt, and the
    /// in-process API loop does not poll signals between turns. A send or brief edit therefore stores
    /// the change and, when the captain is running, names that fact on the response.
    /// </summary>
    public static class SignalDelivery
    {
        #region Public-Members

        /// <summary>
        /// Delivery value when the change is stored and is not injected into the running process.
        /// </summary>
        public const string StoredUntilNextLaunch = "stored_until_next_launch";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Whether a runtime injects operator mail into a captain process that is already running.
        /// </summary>
        /// <param name="runtime">Captain runtime.</param>
        /// <returns>True when the runtime delivers mail mid-mission.</returns>
        public static bool RuntimeDeliversMidMissionMail(AgentRuntimeEnum runtime)
        {
            switch (runtime)
            {
                default:
                    return false;
            }
        }

        /// <summary>
        /// Annotate a stored signal with how it will reach its recipient. When the recipient is running
        /// on a runtime that does not deliver mail mid-mission, sets <see cref="Signal.Delivery"/> to
        /// <see cref="StoredUntilNextLaunch"/> and a reason that names the runtime.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="signal">Stored signal.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The same signal, annotated when a notice applies.</returns>
        public static async Task<Signal> AnnotateAsync(DatabaseDriver database, Signal signal, CancellationToken token = default)
        {
            if (database == null) throw new ArgumentNullException(nameof(database));
            if (signal == null) throw new ArgumentNullException(nameof(signal));
            if (String.IsNullOrWhiteSpace(signal.ToCaptainId)) return signal;

            Captain? captain = await database.Captains.ReadAsync(signal.ToCaptainId, token).ConfigureAwait(false);
            if (captain == null || !IsRunningCaptain(captain)) return signal;
            if (RuntimeDeliversMidMissionMail(captain.Runtime)) return signal;

            signal.Delivery = StoredUntilNextLaunch;
            signal.DeliveryReason = MailReason(captain.Runtime);
            return signal;
        }

        /// <summary>
        /// Annotate a stored mission when a brief edit will not reach a running captain. When the
        /// mission is running on a runtime that does not apply a brief mid-mission, sets
        /// <see cref="Mission.Delivery"/> to <see cref="StoredUntilNextLaunch"/> and a reason that
        /// names the runtime.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="mission">Stored mission.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The same mission, annotated when a notice applies.</returns>
        public static async Task<Mission> AnnotateBriefEditAsync(DatabaseDriver database, Mission mission, CancellationToken token = default)
        {
            if (database == null) throw new ArgumentNullException(nameof(database));
            if (mission == null) throw new ArgumentNullException(nameof(mission));
            if (!IsRunningMission(mission)) return mission;

            AgentRuntimeEnum? runtime = null;
            if (!String.IsNullOrWhiteSpace(mission.CaptainId))
            {
                Captain? captain = await database.Captains.ReadAsync(mission.CaptainId, token).ConfigureAwait(false);
                if (captain != null) runtime = captain.Runtime;
            }

            if (runtime.HasValue && RuntimeDeliversMidMissionMail(runtime.Value)) return mission;

            mission.Delivery = StoredUntilNextLaunch;
            mission.DeliveryReason = BriefReason(runtime);
            return mission;
        }

        #endregion

        #region Private-Methods

        private static bool IsRunningCaptain(Captain captain)
        {
            return captain.State == CaptainStateEnum.Working || captain.State == CaptainStateEnum.Stalled;
        }

        private static bool IsRunningMission(Mission mission)
        {
            return mission.Status == MissionStatusEnum.Assigned || mission.Status == MissionStatusEnum.InProgress;
        }

        private static string MailReason(AgentRuntimeEnum runtime)
        {
            return "The " + runtime + " runtime does not deliver mail to a running captain. The signal is stored until the next launch. Cancel and redispatch if the captain must see it before this stage ends.";
        }

        private static string BriefReason(AgentRuntimeEnum? runtime)
        {
            if (runtime.HasValue)
            {
                return "The " + runtime.Value + " runtime does not apply a brief edit to a running captain. The stored description is used at the next launch. Cancel and redispatch if the captain must see it before this stage ends.";
            }

            return "No shipped runtime applies a brief edit to a running captain. The stored description is used at the next launch. Cancel and redispatch if the captain must see it before this stage ends.";
        }

        #endregion
    }
}
