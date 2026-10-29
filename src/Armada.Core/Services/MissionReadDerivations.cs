namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// Mission fields computed from other records when a mission is read, so the mission row stores neither.
    /// </summary>
    public static class MissionReadDerivations
    {
        private static readonly JsonSerializerOptions _PayloadOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        /// <summary>
        /// The failure kind of a mission: the reason code of its latest attempt when the mission has failed.
        /// </summary>
        /// <param name="mission">The mission.</param>
        /// <param name="latestAttempt">The mission's latest recorded attempt, or null when it has none.</param>
        /// <returns>The reason code, or null when the mission has not failed or the attempt carries none.</returns>
        public static string? FailureKindOf(Mission mission, MissionAttemptProvenance? latestAttempt)
        {
            if (mission == null) throw new ArgumentNullException(nameof(mission));
            if (mission.Status != MissionStatusEnum.Failed && mission.Status != MissionStatusEnum.LandingFailed) return null;
            return String.IsNullOrWhiteSpace(latestAttempt?.ReasonCode) ? null : latestAttempt!.ReasonCode;
        }

        /// <summary>
        /// Fill the computed fields of a mission the caller may already see.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="mission">The mission to fill.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>A task that completes when the fields are set.</returns>
        public static async Task ApplyAsync(DatabaseDriver database, Mission mission, CancellationToken token = default)
        {
            if (database == null) throw new ArgumentNullException(nameof(database));
            if (mission == null) throw new ArgumentNullException(nameof(mission));

            if (mission.Status == MissionStatusEnum.Failed || mission.Status == MissionStatusEnum.LandingFailed)
            {
                ProductionFactPage<MissionAttemptProvenance> page =
                    await database.MissionAttempts.EnumerateByMissionAsync(mission.TenantId, mission.Id, 1, token).ConfigureAwait(false);
                mission.FailureKind = FailureKindOf(mission, page.Items.Count > 0 ? page.Items[page.Items.Count - 1] : null);
            }

            if (mission.Status == MissionStatusEnum.Pending
                && String.Equals(mission.AssignmentHoldReason, MissionAssignmentHold.NoEligibleCaptain, StringComparison.Ordinal))
            {
                List<ArmadaEvent> events = String.IsNullOrWhiteSpace(mission.TenantId)
                    ? await database.Events.EnumerateByMissionAsync(mission.Id, 20, token).ConfigureAwait(false)
                    : await database.Events.EnumerateByMissionAsync(mission.TenantId, mission.Id, 20, token).ConfigureAwait(false);
                foreach (ArmadaEvent evt in events)
                {
                    if (!String.Equals(evt.EventType, MissionAssignmentHold.HeldEventType, StringComparison.Ordinal)
                        || String.IsNullOrWhiteSpace(evt.Payload)) continue;
                    NoEligibleCaptainPayload? payload;
                    try { payload = JsonSerializer.Deserialize<NoEligibleCaptainPayload>(evt.Payload!, _PayloadOptions); }
                    catch (JsonException) { continue; }
                    if (payload == null || !String.Equals(payload.Reason, MissionAssignmentHold.NoEligibleCaptain, StringComparison.Ordinal)) continue;
                    mission.AssignmentHoldCaptains = payload.Captains;
                    break;
                }
            }
        }
    }
}
