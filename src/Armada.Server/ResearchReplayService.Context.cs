namespace Armada.Server
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Security.Cryptography;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Authorization;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;

    /// <summary>Opaque execution context capture and committed replay notifications.</summary>
    public sealed partial class ResearchReplayService
    {
        private async Task<string> CaptureConfigurationDigestAsync(AuthContext actor, Vessel vessel, CancellationToken token)
        {
            WorkflowProfile? profile = await new WorkflowProfileService(_Database, _Logging)
                .ResolveForVesselAsync(actor, vessel, token: token).ConfigureAwait(false);
            // Only the digest leaves this method. Profile environment values and settings are never archived.
            string context = JsonSerializer.Serialize(new
            {
                Version = 1, Profile = profile, _Settings.DefinitionOfDone, _Settings.AutonomousRecovery,
                FleetLimit = _Settings.AutonomousObjectiveScheduler.MaxConcurrentVoyages,
                VesselLimit = _Settings.AutonomousObjectiveScheduler.MaxConcurrentVoyagesPerVessel
            });
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(context))).ToLowerInvariant();
        }

        private async Task NotifyCommittedAsync(ResearchReplayPlan plan)
        {
            // Notification failure cannot turn a committed atomic reset into an apparent failed apply.
            try
            {
                await _Notifier.EmitAsync("voyage.research_replayed", "Research replay " + plan.ReplayId
                    + " archived and reset all six original stages", "voyage", plan.VoyageId, voyageId: plan.VoyageId).ConfigureAwait(false);
                Voyage? voyage = await _Database.Voyages.ReadAsync(plan.VoyageId).ConfigureAwait(false);
                if (voyage != null) _Notifier.VoyageChanged(voyage);
                foreach (string id in plan.MissionIds)
                {
                    Mission? mission = await _Database.Missions.ReadAsync(id).ConfigureAwait(false);
                    if (mission != null) _Notifier.MissionChanged(mission);
                }
            }
            catch (Exception ex) { _Logging.Warn("[ResearchReplay] committed replay notification failed: " + ex.GetType().Name); }
        }

    }
}
