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
            string repository = !String.IsNullOrEmpty(vessel.LocalPath)
                ? vessel.LocalPath : Path.Combine(_Settings.ReposDirectory, vessel.Name + ".git");
            return _Git == null ? null : await _Git.GetRevisionCommitShaAsync(repository, mission.BranchName!, token).ConfigureAwait(false);
        }
    }
}
