namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>
    /// Handles a captain that refused work its brief's owner policy authorizes. The refusal is always
    /// recorded with its reason. The mission then gets at most one continuation, routed to an approved
    /// captain on a different runtime through the normal routing rules; it is never retried on the runtime
    /// that refused. When no owner policy was supplied the refusal is not a policy conflict, and normal
    /// completion handling applies.
    /// </summary>
    public class PolicyRefusalContinuationService
    {
        #region Public-Members

        /// <summary>Failure-reason prefix carried by a mission requeued as a refusal continuation.</summary>
        public const string ContinuationReasonPrefix = "policy_refusal_continuation: ";

        /// <summary>Failure-reason prefix of a mission stopped after a refusal.</summary>
        public const string StoppedReasonPrefix = "policy_refusal: ";

        /// <summary>Event recorded for every classified refusal.</summary>
        public const string RefusalEventType = "mission.policy_refusal";

        /// <summary>Event recorded when the one continuation is dispatched.</summary>
        public const string ContinuedEventType = "mission.policy_refusal_continued";

        #endregion

        #region Private-Members

        private readonly string _Header = "[PolicyRefusalContinuationService] ";
        private readonly DatabaseDriver _Database;
        private readonly ArmadaSettings _Settings;
        private readonly LoggingModule _Logging;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="settings">Admiral settings.</param>
        /// <param name="logging">Logging module.</param>
        public PolicyRefusalContinuationService(DatabaseDriver database, ArmadaSettings settings, LoggingModule logging)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Whether a mission is waiting as a refusal continuation, so assignment must never place it on a
        /// captain it excludes.
        /// </summary>
        /// <param name="mission">Mission to inspect.</param>
        /// <returns>True for a pending refusal continuation.</returns>
        public static bool IsContinuation(Mission? mission)
        {
            return mission != null
                && !String.IsNullOrEmpty(mission.FailureReason)
                && mission.FailureReason.StartsWith(ContinuationReasonPrefix, StringComparison.Ordinal);
        }

        /// <summary>
        /// Decide what happens after a refusal. Pure: every input is supplied by the caller.
        /// </summary>
        /// <param name="mission">The refused mission.</param>
        /// <param name="refusingCaptain">The captain that refused.</param>
        /// <param name="refusal">The classified refusal.</param>
        /// <param name="policyPresent">Whether the brief carried an owner authorization policy.</param>
        /// <param name="continuationAlreadyUsed">Whether this mission already had its one continuation.</param>
        /// <param name="captains">Every configured captain.</param>
        /// <param name="modelTierSettings">Tier configuration used by normal routing; null uses defaults.</param>
        /// <returns>The decision.</returns>
        public static PolicyRefusalContinuationDecision Decide(
            Mission mission,
            Captain refusingCaptain,
            CaptainRefusal refusal,
            bool policyPresent,
            bool continuationAlreadyUsed,
            IReadOnlyList<Captain> captains,
            ModelTierSettings? modelTierSettings)
        {
            if (mission == null) throw new ArgumentNullException(nameof(mission));
            if (refusingCaptain == null) throw new ArgumentNullException(nameof(refusingCaptain));
            if (refusal == null) throw new ArgumentNullException(nameof(refusal));

            PolicyRefusalContinuationDecision decision = new PolicyRefusalContinuationDecision();

            if (!refusal.IsRefusal)
            {
                decision.Reason = "The captain did not refuse.";
                return decision;
            }

            // A model that declines only conflicts with an owner policy that was actually supplied. A provider
            // safeguard block is the provider's own gate refusing the request whatever the brief says, so it
            // takes the same bounded continuation with or without a policy.
            if (!policyPresent && refusal.Kind != CaptainRefusalKindEnum.ProviderSafeguardBlock)
            {
                decision.Reason = "No owner authorization policy was supplied, so the refusal does not conflict with policy.";
                return decision;
            }

            List<Captain> all = (captains ?? new List<Captain>()).Where(captain => captain != null).ToList();
            decision.ExcludedCaptainIds = all
                .Where(captain => captain.Runtime == refusingCaptain.Runtime)
                .Select(captain => captain.Id)
                .Append(refusingCaptain.Id)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToList();

            if (continuationAlreadyUsed)
            {
                decision.Outcome = PolicyRefusalContinuationOutcomeEnum.Stop;
                decision.Reason = "The mission already had its one continuation on an alternate runtime and was refused again.";
                return decision;
            }

            // An alternate is approved when assignment could choose it: the mission's tenant, the Smart Routing
            // persona routes when Smart Routing is on, and the assignment selector, so a pinned model that no
            // alternate runs is a tier floor here exactly as it is in assignment.
            ModelTierSettings tiers = modelTierSettings ?? new ModelTierSettings();
            UsageRoutingSettings usage = tiers.UsageRouting;
            List<Captain> approvedAlternates = all
                .Where(captain => captain.Runtime != refusingCaptain.Runtime)
                .Where(captain => captain.State != CaptainStateEnum.Benched && captain.State != CaptainStateEnum.Quarantined)
                .Where(captain => MissionService.CaptainServesTenant(captain, mission.TenantId))
                .Where(captain => !usage.Enabled || UsageRoutingService.PersonaRoutesAdmit(usage, mission.Persona, captain))
                .Where(captain => LegacyCaptainSelector.CouldSelect(tiers, mission, captain))
                .ToList();

            decision.AlternateRuntimes = approvedAlternates
                .Select(captain => captain.Runtime.ToString())
                .Distinct(StringComparer.Ordinal)
                .OrderBy(runtime => runtime, StringComparer.Ordinal)
                .ToList();

            if (approvedAlternates.Count == 0)
            {
                decision.Outcome = PolicyRefusalContinuationOutcomeEnum.Stop;
                decision.Reason = "No approved captain on a runtime other than " + refusingCaptain.Runtime +
                    " can run the " + (mission.Persona ?? "mission") + " role, so the refused work is not retried.";
                return decision;
            }

            decision.Outcome = PolicyRefusalContinuationOutcomeEnum.Continue;
            decision.Reason = "Continuing once on an approved alternate runtime (" + String.Join(", ", decision.AlternateRuntimes) +
                "); every captain on " + refusingCaptain.Runtime + " is excluded.";
            return decision;
        }

        /// <summary>
        /// Record a refusal and apply its decision to the mission. A continuation requeues the mission with
        /// the refusing runtime excluded; a stop fails it with the refusal reason. A not-applicable decision
        /// leaves the mission untouched.
        /// </summary>
        /// <param name="mission">The refused mission; mutated and persisted unless the decision is not applicable.</param>
        /// <param name="refusingCaptain">The captain that refused.</param>
        /// <param name="refusal">The classified refusal.</param>
        /// <param name="policyPresent">Whether the brief carried an owner authorization policy.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The decision that was applied.</returns>
        public async Task<PolicyRefusalContinuationDecision> HandleAsync(
            Mission mission,
            Captain refusingCaptain,
            CaptainRefusal refusal,
            bool policyPresent,
            CancellationToken token = default)
        {
            if (mission == null) throw new ArgumentNullException(nameof(mission));
            if (refusingCaptain == null) throw new ArgumentNullException(nameof(refusingCaptain));
            if (refusal == null) throw new ArgumentNullException(nameof(refusal));

            if (!refusal.IsRefusal)
            {
                return Decide(mission, refusingCaptain, refusal, policyPresent, false, new List<Captain>(), _Settings.ModelTier);
            }

            List<ArmadaEvent> history = await _Database.Events.EnumerateByMissionAsync(mission.Id, 500, token).ConfigureAwait(false);
            bool continuationAlreadyUsed = history.Any(evt => String.Equals(evt.EventType, ContinuedEventType, StringComparison.Ordinal));
            List<Captain> captains = await _Database.Captains.EnumerateAsync(token).ConfigureAwait(false);

            PolicyRefusalContinuationDecision decision = Decide(
                mission, refusingCaptain, refusal, policyPresent, continuationAlreadyUsed, captains, _Settings.ModelTier);

            await RecordEventAsync(RefusalEventType,
                "Captain refused mission (" + refusal.Kind + "): " + decision.Outcome,
                mission, refusingCaptain, refusal, decision, token).ConfigureAwait(false);

            if (decision.Outcome == PolicyRefusalContinuationOutcomeEnum.Continue)
            {
                mission.Status = MissionStatusEnum.Pending;
                mission.AssignmentState = MissionAssignmentStateEnum.Pending;
                mission.CaptainId = null;
                mission.DockId = null;
                mission.ProcessId = null;
                mission.StartedUtc = null;
                mission.CompletedUtc = null;
                mission.RetrySkipCaptainIds = String.Join(",", decision.ExcludedCaptainIds);
                mission.FailureReason = ContinuationReasonPrefix + refusal.Kind + " on " + refusingCaptain.Runtime + ": " +
                    (String.IsNullOrEmpty(refusal.Reason) ? "no reason given" : refusal.Reason) + ". " + decision.Reason;
                mission.LastUpdateUtc = DateTime.UtcNow;
                await _Database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);

                await RecordEventAsync(ContinuedEventType,
                    "Mission continued on an alternate runtime after a policy refusal",
                    mission, refusingCaptain, refusal, decision, token).ConfigureAwait(false);
                _Logging.Warn(_Header + "mission " + mission.Id + " refused on " + refusingCaptain.Runtime + " (" + refusal.Kind +
                    "); continuing once on " + String.Join(", ", decision.AlternateRuntimes));
            }
            else if (decision.Outcome == PolicyRefusalContinuationOutcomeEnum.Stop)
            {
                mission.Status = MissionStatusEnum.Failed;
                mission.CompletedUtc = DateTime.UtcNow;
                mission.FailureReason = StoppedReasonPrefix + refusal.Kind + " on " + refusingCaptain.Runtime + ": " +
                    (String.IsNullOrEmpty(refusal.Reason) ? "no reason given" : refusal.Reason) + ". " + decision.Reason;
                mission.LastUpdateUtc = DateTime.UtcNow;
                await _Database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);
                _Logging.Warn(_Header + "mission " + mission.Id + " stopped after a policy refusal: " + decision.Reason);
            }

            return decision;
        }

        #endregion

        #region Private-Methods

        private async Task RecordEventAsync(
            string eventType,
            string message,
            Mission mission,
            Captain refusingCaptain,
            CaptainRefusal refusal,
            PolicyRefusalContinuationDecision decision,
            CancellationToken token)
        {
            ArmadaEvent evt = new ArmadaEvent(eventType, message);
            EventOwnerScope.ApplyFromMission(evt, mission);
            evt.EntityType = "mission";
            evt.EntityId = mission.Id;
            evt.CaptainId = refusingCaptain.Id;
            evt.MissionId = mission.Id;
            evt.VesselId = mission.VesselId;
            evt.VoyageId = mission.VoyageId;
            evt.Payload = JsonSerializer.Serialize(new
            {
                RefusalKind = refusal.Kind.ToString(),
                refusal.Reason,
                refusal.Evidence,
                RefusingRuntime = refusingCaptain.Runtime.ToString(),
                Outcome = decision.Outcome.ToString(),
                DecisionReason = decision.Reason,
                decision.AlternateRuntimes,
                decision.ExcludedCaptainIds
            });
            await _Database.Events.CreateAsync(evt, token).ConfigureAwait(false);
        }

        #endregion
    }
}
