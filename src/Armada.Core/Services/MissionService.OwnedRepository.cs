namespace Armada.Core.Services
{
    using System;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Harbor;
    using Armada.Core.Models;
    using Armada.Core.Settings;

    public partial class MissionService
    {
        private async Task<string?> ReadSavedMissionBranchAsync(Mission mission, Vessel vessel, HarborMissionRoute? route, CancellationToken token)
        {
            Dock? dock = await ReadMissionDockAsync(mission, token).ConfigureAwait(false);
            if (dock?.HostCheckout != null)
                return await (_Git ?? throw new InvalidOperationException("harbor_git_provider_unavailable")).GetRevisionCommitShaAsync(HarborCheckoutAddress.ForDock(dock.Id) + "/repository",
                    mission.BranchName!, token).ConfigureAwait(false);
            if (route?.CheckoutMode == HarborCheckoutModeEnum.HarborOwned)
            {
                HarborWorkspaceOperationResult inspect = await RequireHarborCheckouts().InspectRepositoryAsync(vessel,
                    route.RunnerId, route.RepositoryGrantId ?? String.Empty, mission.BranchName!, token).ConfigureAwait(false);
                return inspect.HeadCommit;
            }
            return _Git == null ? null : await _Git.GetRevisionCommitShaAsync(RepositoryPathOf(vessel), mission.BranchName!, token).ConfigureAwait(false);
        }

        /// <summary>
        /// True when a root mission's saved branch is proven to carry no commit of its own: its head is reachable
        /// from the ref the mission started from (its start ref, else the default branch). Such a branch holds no
        /// work a fresh branch would orphan. An unknown ancestry, or a checkout this service cannot read directly,
        /// is not proof.
        /// </summary>
        private async Task<bool> SavedBranchHoldsNoCommittedWorkAsync(
            Mission mission, Vessel vessel, string savedBranchHead, HarborMissionRoute? route, CancellationToken token)
        {
            if (_Git == null || route?.CheckoutMode == HarborCheckoutModeEnum.HarborOwned) return false;
            Dock? dock = await ReadMissionDockAsync(mission, token).ConfigureAwait(false);
            if (dock?.HostCheckout != null) return false;

            string startRef = !String.IsNullOrWhiteSpace(mission.StartFromRef) ? mission.StartFromRef.Trim()
                : String.IsNullOrWhiteSpace(vessel.DefaultBranch) ? "main" : vessel.DefaultBranch.Trim();
            try
            {
                bool? reachable = await _Git.TryIsAncestorAsync(RepositoryPathOf(vessel), savedBranchHead, startRef, token).ConfigureAwait(false);
                if (reachable != true) return false;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "could not tell whether the saved branch of mission " + mission.Id + " holds committed work; it is held: " + ex.Message);
                return false;
            }

            _Logging.Info(_Header + "mission " + mission.Id + " saved branch " + mission.BranchName + " holds no commit beyond " + startRef
                + "; no dock is needed to continue it and a fresh branch is cut");
            return true;
        }

        private string RepositoryPathOf(Vessel vessel)
        {
            return !String.IsNullOrEmpty(vessel.LocalPath)
                ? vessel.LocalPath : Path.Combine(_Settings.ReposDirectory, vessel.Name + ".git");
        }
    }
}
