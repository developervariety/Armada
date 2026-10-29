namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>The hold that explains a Pending mission whose idle captains are all ineligible.</summary>
    public partial class MissionService
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _NoEligibleCaptainNotices =
            new System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.Ordinal);

        private static readonly JsonSerializerOptions _HoldPayloadOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        private static bool IsNoEligibleCaptainHold(Mission mission)
        {
            return String.Equals(mission.AssignmentHoldReason, MissionAssignmentHold.NoEligibleCaptain, StringComparison.Ordinal);
        }

        /// <summary>
        /// Why each captain of the mission's tenant is passed over, or null when the mission is not held for lack
        /// of an eligible captain. Idle captains exist and every one is ineligible; a mission with no idle captain
        /// is waiting for capacity, and a captain that is only reserved by another pass is free again shortly.
        /// </summary>
        private async Task<List<AssignmentHoldCaptainReason>?> DescribeNoEligibleCaptainAsync(Mission mission, CancellationToken token)
        {
            List<Captain> roster = (await _Database.Captains.EnumerateAsync(token).ConfigureAwait(false))
                .Where(item => item != null && CaptainServesTenant(item, mission.TenantId))
                .OrderBy(item => item.Name, StringComparer.Ordinal)
                .ToList();
            List<Captain> idle = roster.Where(item => item.State == CaptainStateEnum.Idle).ToList();
            if (idle.Count == 0) return null;

            Dictionary<string, string> reasons = CaptainAssignmentEligibility.ExplainRoster(
                _Settings.ModelTier, mission, roster, null, _CaptainQuarantine.IsQuarantined, ReservationHolderOf);
            foreach (Captain captain in idle)
            {
                if (!reasons.TryGetValue(captain.Id, out string? reason)
                    || String.Equals(reason, CaptainAssignmentEligibility.ReasonReserved, StringComparison.Ordinal))
                    return null;
            }

            return roster
                .Select(item => new AssignmentHoldCaptainReason
                {
                    CaptainId = item.Id,
                    CaptainName = item.Name,
                    Reason = reasons.TryGetValue(item.Id, out string? reason) ? reason : CaptainAssignmentEligibility.ReasonNotIdle
                })
                .ToList();
        }

        /// <summary>
        /// Start, update or end the <c>no_eligible_captain</c> hold for a mission that found no captain. The event is
        /// written when the hold starts and when the per-captain reasons change, never on an unchanged tick.
        /// Returns false when the mission is no longer Pending.
        /// </summary>
        private async Task<bool> ApplyNoEligibleCaptainHoldAsync(Mission mission, CancellationToken token)
        {
            List<AssignmentHoldCaptainReason>? captains;
            try
            {
                captains = await DescribeNoEligibleCaptainAsync(mission, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "could not evaluate captain eligibility for mission " + mission.Id + ": " + ex.Message);
                return true;
            }

            if (captains == null)
            {
                _NoEligibleCaptainNotices.TryRemove(mission.Id, out string? _);
                return !IsNoEligibleCaptainHold(mission) || await ReleaseAssignmentHoldAsync(mission, token).ConfigureAwait(false);
            }

            bool started = !IsNoEligibleCaptainHold(mission);
            mission.AssignmentHoldReason = MissionAssignmentHold.NoEligibleCaptain;
            mission.AssignmentHoldEntityId = null;
            if (!await WriteAssignmentAsync(mission, MissionStatusEnum.Pending, token).ConfigureAwait(false)) return false;

            string signature = String.Join(";", captains.Select(item => item.CaptainId + "=" + item.Reason));
            if (!started && _NoEligibleCaptainNotices.TryGetValue(mission.Id, out string? previous)
                && String.Equals(previous, signature, StringComparison.Ordinal))
                return true;
            _NoEligibleCaptainNotices[mission.Id] = signature;

            string message = "Mission " + mission.Id + " is held from assignment (" + MissionAssignmentHold.NoEligibleCaptain + "): no idle captain may take it.";
            _Logging.Info(_Header + message + " " + String.Join(", ", captains.Select(item => item.CaptainName + " " + item.Reason)));
            try
            {
                ArmadaEvent evt = new ArmadaEvent(MissionAssignmentHold.HeldEventType, message);
                EventOwnerScope.ApplyFromMission(evt, mission);
                evt.EntityType = "mission";
                evt.EntityId = mission.Id;
                evt.MissionId = mission.Id;
                evt.VesselId = mission.VesselId;
                evt.VoyageId = mission.VoyageId;
                evt.Payload = JsonSerializer.Serialize(new NoEligibleCaptainPayload
                {
                    MissionId = mission.Id,
                    Reason = MissionAssignmentHold.NoEligibleCaptain,
                    Persona = mission.Persona,
                    Captains = captains
                }, _HoldPayloadOptions);
                await _Database.Events.CreateAsync(evt, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _NoEligibleCaptainNotices.TryRemove(mission.Id, out string? _);
                _Logging.Warn(_Header + "could not record " + MissionAssignmentHold.HeldEventType + " for mission " + mission.Id + ": " + ex.Message);
            }
            return true;
        }
    }
}
