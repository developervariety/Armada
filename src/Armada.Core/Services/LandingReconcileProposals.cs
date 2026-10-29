namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Models;

    /// <summary>
    /// The record of a landing proposal: the landing sweep found proof that a LandingFailed mission's work is on the
    /// default branch and asked an operator to confirm it. The proposal is an event; the Needs You inbox reads it, and
    /// confirming the mission is the only way it takes effect.
    /// </summary>
    public static class LandingReconcileProposals
    {
        /// <summary>Event type recorded once per mission when the sweep finds proof.</summary>
        public const string ProposedEventType = "mission.landing_reconcile_proposed";

        /// <summary>Event type recorded when an operator confirms a proposal and the mission completes.</summary>
        public const string ReconciledEventType = "mission.landing_reconciled";

        private const int _EventWindow = 200;

        /// <summary>
        /// Read the proposal recorded for a mission.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="missionId">Mission id.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The recorded proof, or null when the sweep has proposed nothing for the mission.</returns>
        public static async Task<LandingReconcileEvidence?> ReadAsync(DatabaseDriver database, string missionId, CancellationToken token = default)
        {
            if (database == null) throw new ArgumentNullException(nameof(database));
            if (String.IsNullOrEmpty(missionId)) return null;

            List<ArmadaEvent> events = await database.Events.EnumerateByMissionAsync(missionId, _EventWindow, token).ConfigureAwait(false);
            foreach (ArmadaEvent evt in events)
            {
                if (!String.Equals(evt.EventType, ProposedEventType, StringComparison.Ordinal) || String.IsNullOrEmpty(evt.Payload)) continue;
                try
                {
                    LandingReconcileEvidence? evidence = JsonSerializer.Deserialize<LandingReconcileEvidence>(evt.Payload);
                    if (evidence != null && String.Equals(evidence.MissionId, missionId, StringComparison.Ordinal)) return evidence;
                }
                catch (JsonException)
                {
                    // A payload this version cannot read is not a proposal.
                }
            }

            return null;
        }

        /// <summary>
        /// Record a proposal for a mission.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="mission">The LandingFailed mission.</param>
        /// <param name="evidence">The proof.</param>
        /// <param name="token">Cancellation token.</param>
        public static async Task RecordAsync(DatabaseDriver database, Mission mission, LandingReconcileEvidence evidence, CancellationToken token = default)
        {
            if (database == null) throw new ArgumentNullException(nameof(database));
            if (mission == null) throw new ArgumentNullException(nameof(mission));
            if (evidence == null) throw new ArgumentNullException(nameof(evidence));

            ArmadaEvent evt = new ArmadaEvent(ProposedEventType, "Landing may be complete for " + mission.Title + ": " + evidence.Describe());
            evt.EntityType = "mission";
            evt.EntityId = mission.Id;
            evt.MissionId = mission.Id;
            evt.VesselId = mission.VesselId;
            evt.VoyageId = mission.VoyageId;
            evt.Payload = JsonSerializer.Serialize(evidence);
            EventOwnerScope.ApplyFromMission(evt, mission);
            await database.Events.CreateAsync(evt, token).ConfigureAwait(false);
        }
    }
}
