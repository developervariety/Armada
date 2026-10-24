namespace Armada.Server
{
    using System;
    using System.Threading.Tasks;
    using Armada.Core.Harbor;
    using Armada.Core.Models;
    using Armada.Core.Services;

    public partial class MissionLandingHandler
    {
        private async Task<bool> HandleHarborReconcilePullRequestAsync(Mission mission, Vessel? vessel, Dock dock)
        {
            HarborCheckoutBinding? binding = dock.HostCheckout;
            if (vessel == null || binding == null || !binding.IsComplete
                || !String.Equals(mission.VesselId, vessel.Id, StringComparison.Ordinal)
                || !String.Equals(dock.VesselId, vessel.Id, StringComparison.Ordinal)
                || !String.Equals(dock.TenantId, mission.TenantId, StringComparison.Ordinal)
                || !String.Equals(dock.UserId, mission.UserId, StringComparison.Ordinal)
                || !String.Equals(binding.TenantId, mission.TenantId, StringComparison.Ordinal)
                || !String.Equals(binding.UserId, mission.UserId, StringComparison.Ordinal))
            {
                _Logging.Warn(_Header + "PR reconciler: Harbor binding is invalid for mission " + mission.Id);
                return false;
            }

            try
            {
                string repository = HarborCheckoutAddress.ForDock(dock.Id) + "/repository";
                if (!await _Git.IsPrMergedAsync(repository, mission.PrUrl!).ConfigureAwait(false)) return false;

                bool completed = false;
                using (IDisposable admission = await DirectLandingAdmission.AcquireAsync(_Database,
                    mission.TenantId, mission.VoyageId, mission.Id).ConfigureAwait(false))
                {
                    Mission? current = !String.IsNullOrWhiteSpace(mission.TenantId)
                        ? await _Database.Missions.ReadAsync(mission.TenantId, mission.Id).ConfigureAwait(false)
                        : await _Database.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    if (current == null) return false;
                    mission = current;
                    bool cancelled = await DirectLandingAdmission.IsCancelledAsync(_Database, mission).ConfigureAwait(false);
                    if (!cancelled && mission.Status == Armada.Core.Enums.MissionStatusEnum.PullRequestOpen)
                    {
                        mission.Status = Armada.Core.Enums.MissionStatusEnum.Complete;
                        mission.CompletedUtc = DateTime.UtcNow;
                        mission.LastUpdateUtc = DateTime.UtcNow;
                        await _Database.Missions.UpdateAsync(mission).ConfigureAwait(false);
                        completed = true;
                    }
                }
                if (completed)
                {
                    _Logging.Info(_Header + "PR reconciler: Harbor mission " + mission.Id + " PR merged, status set to Complete");
                    FireIndexRefreshForVessel(mission.VesselId, "PR reconciler completed mission " + mission.Id);
                }

                await TrySyncHarborPullRequestTargetAsync(mission, vessel, dock, binding).ConfigureAwait(false);
                return true;
            }
            catch (Exception exception)
            {
                _Logging.Warn(_Header + "PR reconciler: Harbor check failed for mission " + mission.Id + ": " + exception.Message);
                return false;
            }
        }

    }
}
