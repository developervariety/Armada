namespace Armada.Core.Services
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Models;
    using SyslogLogging;

    /// <summary>Events for a Judge that lands under a terminal landing continuation.</summary>
    public static class TerminalLandingContinuationEvents
    {
        /// <summary>
        /// Record that a Judge admitted by a terminal landing continuation landed. Its voyage keeps its Failed status
        /// as history; this event is the durable record that the continued work reached the target.
        /// </summary>
        public static async Task RecordLandedAsync(DatabaseDriver database, Mission mission, LoggingModule? logging, CancellationToken token = default)
        {
            if (database == null) throw new ArgumentNullException(nameof(database));
            if (mission == null) throw new ArgumentNullException(nameof(mission));
            GeneratedArtifactReviewBinding? binding = mission.GeneratedArtifactReview;
            TerminalLandingContinuation? grant = binding?.TerminalContinuation;
            if (binding == null || grant == null || binding.ApprovedUtc == null) return;
            try
            {
                ArmadaEvent landed = new ArmadaEvent(MissionService.TerminalContinuationLandedEventType,
                    "Judge " + mission.Id + " landed candidate " + binding.CandidateCommit + " under a terminal landing continuation; voyage "
                    + grant.VoyageId + " stays Failed")
                {
                    TenantId = mission.TenantId, MissionId = mission.Id, EntityId = mission.Id, EntityType = "mission",
                    VesselId = mission.VesselId, VoyageId = mission.VoyageId,
                    Payload = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        binding.CandidateCommit, binding.SourceCommit, grant.GrantedBy, grant.VoyageId, grant.AdmittedAttempt
                    })
                };
                EventOwnerScope.ApplyFromMission(landed, mission);
                await database.Events.CreateAsync(landed, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logging?.Warn("[TerminalLandingContinuation] landed event for " + mission.Id + " was not written: " + ex.Message);
            }
        }
    }
}
