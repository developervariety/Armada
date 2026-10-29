namespace Armada.Core.Services
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Models;

    /// <summary>
    /// The one rule that routes mail addressed to a mission. Mail that names a mission goes to the captain that
    /// holds the mission now, and mail still unread when the mission moves to another captain moves with it.
    /// </summary>
    public static class MissionMailRouting
    {
        #region Public-Methods

        /// <summary>
        /// Address a signal to a mission: record the mission on the signal and set its recipient to the
        /// mission's current captain. A mission that has no captain yet leaves the recipient as it was; the
        /// mail reaches the mission's next captain through <see cref="ReaddressAsync"/>.
        /// </summary>
        /// <param name="signal">Signal about to be stored.</param>
        /// <param name="mission">Mission the signal is addressed to, already checked against the caller's scope.</param>
        public static void AddressToMission(Signal signal, Mission mission)
        {
            if (signal == null) throw new ArgumentNullException(nameof(signal));
            if (mission == null) throw new ArgumentNullException(nameof(mission));

            signal.MissionId = mission.Id;
            if (!String.IsNullOrEmpty(mission.CaptainId))
                signal.ToCaptainId = mission.CaptainId;
        }

        /// <summary>
        /// Move the unread mail of a mission to the captain that now holds it.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="missionId">Mission that was assigned.</param>
        /// <param name="captainId">Captain that holds the mission.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The number of signals moved.</returns>
        public static Task<int> ReaddressAsync(DatabaseDriver database, string missionId, string captainId, CancellationToken token = default)
        {
            if (database == null) throw new ArgumentNullException(nameof(database));
            return database.Signals.ReaddressUnreadMissionMailAsync(missionId, captainId, token);
        }

        #endregion
    }
}
