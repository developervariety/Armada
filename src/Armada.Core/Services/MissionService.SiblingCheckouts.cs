namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;

    public partial class MissionService
    {
        #region Private-Methods

        /// <summary>
        /// Build the sibling checkout section for a vessel that declares siblings. The brief is generated after
        /// the dock and its siblings are provisioned, so the section reports what the captain will find; a vessel
        /// with no sibling declaration gets no section.
        /// </summary>
        private async Task<string> BuildSiblingCheckoutsSectionAsync(
            string worktreePath,
            Mission mission,
            Vessel vessel,
            Captain? captain,
            CancellationToken token)
        {
            if (_Git == null || vessel.GetSiblingRepos().Count == 0) return String.Empty;
            try
            {
                Objective? objective = await FindLinkedObjectiveAsync(mission, token).ConfigureAwait(false);
                List<SiblingCheckoutState> states = await SiblingCheckoutBrief.ReadAsync(_Git, vessel, worktreePath, objective, token).ConfigureAwait(false);
                bool rootsRegistered = captain != null && CaptainLaunchIsolationPlanner.SupportsWorkspaceRoots(captain.Runtime);
                return SiblingCheckoutBrief.Render(states, rootsRegistered);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "could not read the sibling checkouts for mission " + mission.Id
                    + "; the brief omits the sibling section: " + ex.Message);
                return String.Empty;
            }
        }

        #endregion
    }
}
