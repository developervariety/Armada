namespace Armada.Core.Services
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Settings;

    public partial class MissionService
    {
        #region Public-Members

        /// <summary>
        /// Opening of the error a handoff-outcome hold choice returns while another choice or the timeout
        /// release is already acting on the same hold.
        /// </summary>
        public const string HandoffHoldBusyReason = "handoff_hold_busy";

        #endregion

        #region Private-Members

        // One choice or timeout release acts on a hold at a time, so an operator choice and the timeout
        // sweep can never both hand the same stage off.
        private static readonly ConcurrentDictionary<string, byte> _HandoffHoldsInFlight =
            new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);

        private const string _HandoffOutcomeDecisionPoint = "handoff_outcome";

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        public async Task<Mission> ResolveHandoffOutcomeHoldAsync(
            string missionId,
            HandoffOutcomeHoldChoiceEnum choice,
            string operatorName,
            string reason,
            CancellationToken token = default)
        {
            Mission held = await RequireHeldMissionAsync(missionId, operatorName, reason, token).ConfigureAwait(false);
            if (!HandoffOutcomeHold.IsHeld(held))
            {
                throw new InvalidOperationException("Mission " + missionId + " is not held by the handoff-outcome hold ("
                    + (held.HeldForOperatorReviewReason ?? "no reason recorded") + "); resolve other holds with armada_review_hold.");
            }

            if (!_HandoffHoldsInFlight.TryAdd(held.Id, 0))
            {
                throw new InvalidOperationException(HandoffHoldBusyReason + ": another choice or the timeout release is already acting on the hold of mission "
                    + missionId + ". Read the mission again before choosing.");
            }

            try
            {
                Mission? current = await _Database.Missions.ReadAsync(held.Id, token).ConfigureAwait(false);
                if (current == null) throw new InvalidOperationException("Mission not found: " + missionId);
                if (!HandoffOutcomeHold.IsHeld(current))
                {
                    throw new InvalidOperationException("Mission " + missionId + " is not held for operator review.");
                }

                string operatorText = operatorName.Trim();
                string reasonText = reason.Trim();
                if (choice == HandoffOutcomeHoldChoiceEnum.Rescue)
                {
                    return await RescueHeldHandoffAsync(current, operatorText, reasonText, token).ConfigureAwait(false);
                }

                return await ReleaseHeldHandoffAsync(
                    current,
                    choice == HandoffOutcomeHoldChoiceEnum.MailAndRelease,
                    HandoffOutcomeHold.LabelFor(choice),
                    operatorText,
                    reasonText,
                    true,
                    token).ConfigureAwait(false);
            }
            finally
            {
                _HandoffHoldsInFlight.TryRemove(held.Id, out byte _);
            }
        }

        /// <summary>
        /// Release every handoff-outcome hold that has waited longer than the configured timeout with no
        /// operator choice. Each is released with the unmet-criteria Mail, the same handoff the decision
        /// makes without a hold, and records the <see cref="HandoffOutcomeHold.LabelTimeout"/> label. The
        /// sweep runs whether or not the hold is enabled, so a hold engaged before the flag was turned off
        /// still ends. It never cancels, rescues, lands or dispatches.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The number of holds released.</returns>
        public async Task<int> ReleaseExpiredHandoffOutcomeHoldsAsync(CancellationToken token = default)
        {
            TypedDecisionHandoffHoldSettings settings = _Settings.TypedDecisions.HandoffOutcomeHold ?? new TypedDecisionHandoffHoldSettings();
            TimeSpan timeout = TimeSpan.FromMinutes(settings.TimeoutMinutes);
            DateTime now = _UtcNow();

            List<Mission> produced = await _Database.Missions.EnumerateByStatusAsync(MissionStatusEnum.WorkProduced, token).ConfigureAwait(false);
            int released = 0;
            foreach (Mission candidate in produced)
            {
                if (!HandoffOutcomeHold.IsHeld(candidate)) continue;
                if (!HandoffOutcomeHold.TryParse(candidate.HeldForOperatorReviewReason, out DateTime sinceUtc, out string _, out List<int> _))
                {
                    _Logging.Warn(_Header + "mission " + candidate.Id + " handoff-outcome hold reason is not readable; it waits for an operator choice: "
                        + candidate.HeldForOperatorReviewReason);
                    continue;
                }

                if (now - sinceUtc < timeout) continue;
                if (!_HandoffHoldsInFlight.TryAdd(candidate.Id, 0)) continue;

                try
                {
                    Mission? current = await _Database.Missions.ReadAsync(candidate.Id, token).ConfigureAwait(false);
                    if (current == null || !HandoffOutcomeHold.IsHeld(current)) continue;

                    await ReleaseHeldHandoffAsync(
                        current,
                        true,
                        HandoffOutcomeHold.LabelTimeout,
                        HandoffOutcomeHold.DecidedByTimeout,
                        "no operator choice within " + settings.TimeoutMinutes.ToString(CultureInfo.InvariantCulture) + " min",
                        false,
                        token).ConfigureAwait(false);
                    released++;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _Logging.Warn(_Header + "could not release the expired handoff-outcome hold of mission " + candidate.Id + ": " + ex.Message);
                }
                finally
                {
                    _HandoffHoldsInFlight.TryRemove(candidate.Id, out byte _);
                }
            }

            return released;
        }

        #endregion

        #region Private-Methods

        /// <summary>
        /// Hold the finished stage instead of Mailing its partial outcome, when the hold is enabled, the
        /// gated partial confidence is at least <see cref="HandoffOutcomeHold.MinimumConfidence"/>, the
        /// stage is WorkProduced and not already held, and the gated decision event can be read. Any other
        /// case returns false and the caller Mails the next stage as it does without a hold. The hold only
        /// sets the operator-review flag and records why; it never cancels, rescues, lands or dispatches.
        /// Never throws into the handoff.
        /// </summary>
        /// <param name="completedMission">The finished stage.</param>
        /// <param name="verdict">The gated handoff-outcome verdict.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>True when the hold engaged and the caller must skip the handoff.</returns>
        private async Task<bool> TryEngageHandoffOutcomeHoldAsync(Mission completedMission, HandoffOutcomeVerdict verdict, CancellationToken token)
        {
            TypedDecisionHandoffHoldSettings? settings = _Settings.TypedDecisions.HandoffOutcomeHold;
            if (settings == null || !settings.Enabled) return false;
            if (verdict.Action != HandoffOutcomeAction.MailPartial) return false;
            if (verdict.Confidence < HandoffOutcomeHold.MinimumConfidence) return false;
            if (completedMission.Status != MissionStatusEnum.WorkProduced) return false;
            if (completedMission.HeldForOperatorReview) return false;

            string reason;
            try
            {
                ArmadaEvent? decisionEvent = await FindHandoffOutcomeDecisionEventAsync(completedMission.Id, token).ConfigureAwait(false);
                if (decisionEvent == null)
                {
                    _Logging.Warn(_Header + "handoff-outcome hold not engaged for mission " + completedMission.Id
                        + ": the gated decision event could not be read, so the next stage is Mailed instead");
                    return false;
                }

                IReadOnlyList<string> criteria = await ReadObjectiveAcceptanceCriteriaAsync(completedMission, token).ConfigureAwait(false);
                reason = HandoffOutcomeHold.BuildReason(
                    _UtcNow(),
                    decisionEvent.Id,
                    verdict.UnmetCriterionIndices,
                    criteria,
                    verdict.Confidence,
                    settings.TimeoutMinutes);

                completedMission.HeldForOperatorReview = true;
                completedMission.HeldForOperatorReviewReason = reason;
                completedMission.LastUpdateUtc = DateTime.UtcNow;
                await _Database.Missions.UpdateAsync(completedMission, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // The hold could not be written: keep the in-memory row consistent with the stored one and
                // let the caller Mail the next stage, which is the behaviour without a hold.
                ClearOperatorReviewHold(completedMission);
                _Logging.Warn(_Header + "handoff-outcome hold not engaged for mission " + completedMission.Id + ", next stage is Mailed instead: " + ex.Message);
                return false;
            }

            try
            {
                await AppendMissionActivityAsync(completedMission.Id, "handoff held for an operator choice: " + reason, token).ConfigureAwait(false);
                HandoffOutcomeHold.TryParse(reason, out DateTime _, out string decisionEventId, out List<int> unmet);
                ArmadaEvent engaged = new ArmadaEvent
                {
                    TenantId = completedMission.TenantId,
                    UserId = completedMission.UserId,
                    EventType = HandoffOutcomeHold.EngagedEventType,
                    EntityType = "mission",
                    EntityId = completedMission.Id,
                    MissionId = completedMission.Id,
                    CaptainId = completedMission.CaptainId,
                    VesselId = completedMission.VesselId,
                    VoyageId = completedMission.VoyageId,
                    Message = "Handoff of mission " + completedMission.Id + " held for an operator choice: partial at "
                        + verdict.Confidence.ToString("0.00", CultureInfo.InvariantCulture),
                    Payload = JsonSerializer.Serialize(new HandoffHoldEngagedPayload
                    {
                        MissionId = completedMission.Id,
                        DecisionEventId = decisionEventId,
                        UnmetCriteria = unmet,
                        Confidence = verdict.Confidence,
                        TimeoutMinutes = settings.TimeoutMinutes
                    })
                };
                await _Database.Events.CreateAsync(engaged, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // The hold itself is stored; a missing activity line or event never undoes it.
                _Logging.Warn(_Header + "handoff-outcome hold engaged for mission " + completedMission.Id + " but its event was not recorded: " + ex.Message);
            }

            _Logging.Info(_Header + "mission " + completedMission.Id + " handoff held for an operator choice: " + reason);
            return true;
        }

        /// <summary>
        /// Find the newest gated <c>handoff_outcome</c> decision event for a mission, or null.
        /// </summary>
        private async Task<ArmadaEvent?> FindHandoffOutcomeDecisionEventAsync(string missionId, CancellationToken token)
        {
            EnumerationResult<ArmadaEvent> page = await _Database.Events.EnumerateAsync(new EnumerationQuery
            {
                MissionId = missionId,
                EventType = TypedDecisionRecorder.EventTypeGated,
                Order = EnumerationOrderEnum.CreatedDescending,
                PageNumber = 1,
                PageSize = 20
            }, token).ConfigureAwait(false);

            foreach (ArmadaEvent evt in page.Objects)
            {
                if (String.IsNullOrWhiteSpace(evt.Payload)) continue;
                DecisionEventPayload? payload;
                try
                {
                    payload = JsonSerializer.Deserialize<DecisionEventPayload>(evt.Payload!);
                }
                catch (JsonException)
                {
                    continue;
                }

                if (payload != null && String.Equals(payload.Decision, _HandoffOutcomeDecisionPoint, StringComparison.Ordinal))
                    return evt;
            }

            return null;
        }

        /// <summary>
        /// End a handoff-outcome hold by handing the stage off: with the unmet-criteria Mail when
        /// <paramref name="mail"/> is true, unchanged otherwise. The handoff does not consult the decision
        /// again. Records the label before the handoff, so the outcome is kept even when the handoff fails.
        /// </summary>
        private async Task<Mission> ReleaseHeldHandoffAsync(
            Mission mission,
            bool mail,
            string label,
            string decidedBy,
            string reasonText,
            bool operatorChoice,
            CancellationToken token)
        {
            string previousHold = mission.HeldForOperatorReviewReason ?? String.Empty;
            HandoffOutcomeHold.TryParse(previousHold, out DateTime sinceUtc, out string decisionEventId, out List<int> unmet);

            ClearOperatorReviewHold(mission);
            mission.LastUpdateUtc = DateTime.UtcNow;
            await _Database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);
            await AppendMissionActivityAsync(mission.Id, "handoff-outcome hold ended (" + label + ") by " + decidedBy + ": " + reasonText, token).ConfigureAwait(false);
            await RecordHandoffHoldLabelAsync(mission, decisionEventId, label, decidedBy, reasonText, unmet, sinceUtc, token).ConfigureAwait(false);
            _Logging.Info(_Header + "mission " + mission.Id + " handoff-outcome hold ended (" + label + ") by " + decidedBy + ": " + reasonText);

            bool hasDependentPipelineStages = await HasDependentPipelineStages(mission.VoyageId, mission.Id, token).ConfigureAwait(false);
            if (!hasDependentPipelineStages)
            {
                // A stage with no dependent stage left in its voyage continues the way any released stage
                // does, to its landing.
                return await ContinueReleasedPassAsync(mission, "Handoff-outcome hold released", token).ConfigureAwait(false);
            }

            await TryHandoffToNextStageAsync(mission, new HandoffHoldRelease(mail, unmet), token).ConfigureAwait(false);

            // The next stage checks out this branch in its own dock, which git refuses while this dock
            // still has it checked out.
            if (!String.IsNullOrEmpty(mission.DockId))
                await ReclaimMissionDockAsync(mission.DockId!, token).ConfigureAwait(false);
            if (operatorChoice)
                await DispatchPendingMissionsAsync(token).ConfigureAwait(false);
            await UpdateVoyageTerminalStatusAsync(mission.VoyageId, token).ConfigureAwait(false);

            Mission? handedOff = await _Database.Missions.ReadAsync(mission.Id, token).ConfigureAwait(false);
            return handedOff ?? mission;
        }

        /// <summary>
        /// End a handoff-outcome hold with the operator's rescue choice: the stage fails with a fixed
        /// reason naming only the unmet criteria indices, its dependent stages are cancelled, and its
        /// branch is preserved by the terminal reap. Autonomous recovery decides the rescue from there,
        /// under its own budget and rules; this method starts no mission.
        /// </summary>
        private async Task<Mission> RescueHeldHandoffAsync(Mission mission, string operatorText, string reasonText, CancellationToken token)
        {
            string previousHold = mission.HeldForOperatorReviewReason ?? String.Empty;
            HandoffOutcomeHold.TryParse(previousHold, out DateTime sinceUtc, out string decisionEventId, out List<int> unmet);
            Dock? dock = await ReadMissionDockAsync(mission, token).ConfigureAwait(false);

            ClearOperatorReviewHold(mission);
            mission.Status = MissionStatusEnum.Failed;
            mission.FailureReason = BuildHandoffHoldRescueFailureReason(unmet);
            mission.CompletedUtc = DateTime.UtcNow;
            mission.ProcessId = null;
            mission.DockId = null;
            mission.LastUpdateUtc = DateTime.UtcNow;
            await _Database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);
            await AppendMissionActivityAsync(mission.Id, "handoff-outcome hold ended (rescue) by " + operatorText + ": " + reasonText, token).ConfigureAwait(false);
            await RecordHandoffHoldLabelAsync(mission, decisionEventId, HandoffOutcomeHold.LabelRescue, operatorText, reasonText, unmet, sinceUtc, token).ConfigureAwait(false);
            _Logging.Warn(_Header + "mission " + mission.Id + " handoff-outcome hold ended with rescue by " + operatorText + ": " + reasonText);

            if (dock != null)
            {
                await ReclaimMissionDockAsync(dock.Id, token).ConfigureAwait(false);
            }

            await CancelDependentPipelineStagesAsync(mission, token).ConfigureAwait(false);
            await UpdateVoyageTerminalStatusAsync(mission.VoyageId, token).ConfigureAwait(false);
            await ReapTerminalMissionBranchAsync(mission, token).ConfigureAwait(false);
            return mission;
        }

        /// <summary>
        /// The failure reason a rescue choice writes. It carries only fixed text and criteria indices, so
        /// autonomous recovery classifies it as ordinary partial work, not as an approval or policy stop.
        /// </summary>
        private static string BuildHandoffHoldRescueFailureReason(IReadOnlyList<int> unmet)
        {
            string indices = unmet == null || unmet.Count == 0
                ? "not named"
                : String.Join(", ", unmet.Select(index => index.ToString(CultureInfo.InvariantCulture)));
            return HandoffOutcomeHold.RescueFailurePrefix + ": an operator chose to rescue this stage instead of handing off its partial work; "
                + "unmet acceptance criteria " + indices + " (see the objective).";
        }

        private async Task RecordHandoffHoldLabelAsync(
            Mission mission,
            string decisionEventId,
            string label,
            string decidedBy,
            string reasonText,
            List<int> unmet,
            DateTime sinceUtc,
            CancellationToken token)
        {
            TypedDecisionRecorder recorder = new TypedDecisionRecorder(_Database, _Logging);
            Dictionary<string, object?> details = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["unmet_criteria"] = unmet ?? new List<int>(),
                ["held_since_utc"] = sinceUtc == DateTime.MinValue ? null : sinceUtc.ToString("o", CultureInfo.InvariantCulture),
                ["held_minutes"] = sinceUtc == DateTime.MinValue ? (double?)null : Math.Round((_UtcNow() - sinceUtc).TotalMinutes, 1)
            };
            string? eventId = String.Equals(decisionEventId, HandoffOutcomeHold.NoEvent, StringComparison.Ordinal) ? null : decisionEventId;
            await recorder.RecordOutcomeLabelAsync(_HandoffOutcomeDecisionPoint, eventId, label, decidedBy, reasonText, mission, details, token).ConfigureAwait(false);
        }

        #endregion

        #region Private-Types

        /// <summary>
        /// How a released handoff-outcome hold hands off: with or without the unmet-criteria Mail. Its
        /// presence tells the handoff not to consult the decision again.
        /// </summary>
        private sealed class HandoffHoldRelease
        {
            public HandoffHoldRelease(bool mail, IReadOnlyList<int> unmetCriterionIndices)
            {
                Mail = mail;
                UnmetCriterionIndices = unmetCriterionIndices ?? new List<int>();
            }

            public bool Mail { get; }

            public IReadOnlyList<int> UnmetCriterionIndices { get; }
        }

        private sealed class DecisionEventPayload
        {
            [JsonPropertyName("decision")]
            public string? Decision { get; set; } = null;
        }

        private sealed class HandoffHoldEngagedPayload
        {
            [JsonPropertyName("mission_id")]
            public string MissionId { get; set; } = String.Empty;

            [JsonPropertyName("decision_event_id")]
            public string DecisionEventId { get; set; } = String.Empty;

            [JsonPropertyName("unmet_criteria")]
            public List<int> UnmetCriteria { get; set; } = new List<int>();

            [JsonPropertyName("confidence")]
            public double Confidence { get; set; } = 0.0;

            [JsonPropertyName("timeout_minutes")]
            public int TimeoutMinutes { get; set; } = 0;
        }

        #endregion
    }
}
