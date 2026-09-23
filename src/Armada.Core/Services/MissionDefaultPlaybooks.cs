namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Models;

    /// <summary>
    /// Applies a vessel's default playbooks to a mission an operator creates. The REST, MCP and WebSocket
    /// mission create entry points all call this before they dispatch, so a mission created through any of
    /// them carries the same playbook set.
    /// </summary>
    public static class MissionDefaultPlaybooks
    {
        #region Public-Methods

        /// <summary>
        /// Replace the mission's selected playbooks with the vessel's default playbooks merged with the
        /// caller's selections. A playbook the caller also selects keeps the caller's delivery mode.
        /// A mission with no vessel, or whose vessel does not exist, keeps the caller's selections.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="mission">Mission about to be dispatched.</param>
        /// <param name="token">Cancellation token.</param>
        public static async Task MergeVesselDefaultsAsync(DatabaseDriver database, Mission mission, CancellationToken token = default)
        {
            if (database == null) throw new ArgumentNullException(nameof(database));
            if (mission == null) throw new ArgumentNullException(nameof(mission));

            List<SelectedPlaybook> callerPlaybooks = mission.SelectedPlaybooks ?? new List<SelectedPlaybook>();
            if (String.IsNullOrWhiteSpace(mission.VesselId))
            {
                mission.SelectedPlaybooks = callerPlaybooks;
                return;
            }

            Vessel? vessel = await database.Vessels.ReadAsync(mission.VesselId!, token).ConfigureAwait(false);
            mission.SelectedPlaybooks = PlaybookMerge.MergeWithVesselDefaults(vessel?.GetDefaultPlaybooks(), callerPlaybooks);
        }

        #endregion
    }
}
