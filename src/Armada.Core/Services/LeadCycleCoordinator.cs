namespace Armada.Core.Services
{
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Settings;

    /// <summary>
    /// Coordinates all unattended lead implementations through one durable lease and event trail.
    /// </summary>
    public class LeadCycleCoordinator
    {
        #region Public-Members

        /// <summary>
        /// Shared durable lease name used by every lead implementation.
        /// </summary>
        public const string LeaseName = "autonomy:lead-cycle";

        /// <summary>
        /// Event type used for durable lead-mode changes.
        /// </summary>
        public const string ModeChangedEventType = "lead_mode.changed";

        /// <summary>
        /// Event type used when a cycle starts.
        /// </summary>
        public const string CycleStartedEventType = "lead_cycle.started";

        /// <summary>
        /// Event type used when a cycle renews its lease.
        /// </summary>
        public const string CycleHeartbeatEventType = "lead_cycle.heartbeat";

        /// <summary>
        /// Event type used when a cycle completes normally.
        /// </summary>
        public const string CycleCompletedEventType = "lead_cycle.completed";

        /// <summary>
        /// Event type used when a cycle fails or stops.
        /// </summary>
        public const string CycleFailedEventType = "lead_cycle.failed";

        #endregion

        #region Private-Members

        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };

        private readonly DatabaseDriver _Database;
        private readonly GrokLeadSettings _Settings;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate the coordinator.
        /// </summary>
        /// <param name="database">Armada database.</param>
        /// <param name="settings">Grok lead settings.</param>
        public LeadCycleCoordinator(DatabaseDriver database, GrokLeadSettings settings)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Read the effective durable operating mode.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Effective mode.</returns>
        public async Task<LeadOperatingModeEnum> GetModeAsync(CancellationToken token = default)
        {
            ArmadaEvent? modeEvent = await ReadLatestEventAsync(ModeChangedEventType, token).ConfigureAwait(false);
            LeadModeEventPayload? payload = DeserializePayload<LeadModeEventPayload>(modeEvent);
            return payload?.Mode ?? _Settings.DefaultMode;
        }

        /// <summary>
        /// Change the durable operating mode and append an audit event.
        /// </summary>
        /// <param name="mode">New mode.</param>
        /// <param name="actor">Authenticated owner or system identity.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>New mode.</returns>
        public async Task<LeadOperatingModeEnum> SetModeAsync(
            LeadOperatingModeEnum mode,
            string actor,
            CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(actor)) throw new ArgumentNullException(nameof(actor));

            LeadModeEventPayload payload = new LeadModeEventPayload
            {
                Mode = mode,
                Actor = actor.Trim()
            };
            ArmadaEvent modeEvent = new ArmadaEvent(
                ModeChangedEventType,
                "Unattended lead mode changed to " + mode + " by " + actor.Trim() + ".")
            {
                EntityType = "lead_mode",
                EntityId = "unattended-lead",
                Payload = JsonSerializer.Serialize(payload, _JsonOptions)
            };
            await _Database.Events.CreateAsync(modeEvent, token).ConfigureAwait(false);
            return mode;
        }

        /// <summary>
        /// Attempt to start one bounded lead cycle.
        /// </summary>
        /// <param name="runner">Lead implementation.</param>
        /// <param name="participantKey">Stable participant key.</param>
        /// <param name="standbyFallback">True when the legacy lead requests standby fallback.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Start result.</returns>
        public async Task<LeadCycleStartResult> TryBeginAsync(
            LeadRunnerTypeEnum runner,
            string participantKey,
            bool standbyFallback = false,
            CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(participantKey)) throw new ArgumentNullException(nameof(participantKey));

            LeadOperatingModeEnum mode = await GetModeAsync(token).ConfigureAwait(false);
            string? refusal = await GetModeRefusalAsync(mode, runner, standbyFallback, token).ConfigureAwait(false);
            if (refusal != null)
            {
                return new LeadCycleStartResult
                {
                    Acquired = false,
                    Mode = mode,
                    RefusalReason = refusal
                };
            }

            if (runner == LeadRunnerTypeEnum.Grok
                && !String.Equals(participantKey.Trim(), _Settings.ParticipantKey, StringComparison.Ordinal))
            {
                return new LeadCycleStartResult
                {
                    Acquired = false,
                    Mode = mode,
                    RefusalReason = "The Grok participant key does not match the configured server identity."
                };
            }

            string cycleId = Constants.IdGenerator.GenerateKSortable("lcy_", 24);
            TimeSpan ttl = TimeSpan.FromMinutes(_Settings.CycleLeaseMinutes);
            bool acquired = await _Database.CoordinationLeases.TryAcquireAsync(
                LeaseName,
                cycleId,
                ttl,
                Constants.DefaultTenantId,
                token).ConfigureAwait(false);
            if (!acquired)
            {
                return new LeadCycleStartResult
                {
                    Acquired = false,
                    Mode = mode,
                    RefusalReason = "Another unattended lead cycle holds the shared lease."
                };
            }

            DateTime startedUtc = DateTime.UtcNow;
            DateTime deadlineUtc = startedUtc.Add(ttl);
            try
            {
                LeadCycleEventPayload payload = new LeadCycleEventPayload
                {
                    CycleId = cycleId,
                    Runner = runner,
                    ParticipantKey = participantKey.Trim(),
                    StandbyFallback = standbyFallback,
                    StartedUtc = startedUtc,
                    DeadlineUtc = deadlineUtc
                };
                await WriteCycleEventAsync(
                    CycleStartedEventType,
                    cycleId,
                    "Unattended " + runner + " lead cycle started.",
                    payload,
                    token).ConfigureAwait(false);
            }
            catch
            {
                await _Database.CoordinationLeases.ReleaseAsync(LeaseName, cycleId, token).ConfigureAwait(false);
                throw;
            }

            return new LeadCycleStartResult
            {
                Acquired = true,
                CycleId = cycleId,
                Mode = mode,
                DeadlineUtc = deadlineUtc
            };
        }

        /// <summary>
        /// Renew an active cycle lease and append a heartbeat event.
        /// </summary>
        /// <param name="cycleId">Cycle identifier.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>True when the cycle still owns the lease.</returns>
        public async Task<bool> HeartbeatAsync(string cycleId, CancellationToken token = default)
        {
            ValidateCycleId(cycleId);
            TimeSpan ttl = TimeSpan.FromMinutes(_Settings.CycleLeaseMinutes);
            bool renewed = await _Database.CoordinationLeases.TryRenewAsync(
                LeaseName, cycleId, ttl, token).ConfigureAwait(false);
            if (!renewed) return false;

            LeadCycleEventPayload? started = await ReadCyclePayloadAsync(cycleId, token).ConfigureAwait(false);
            LeadCycleEventPayload payload = started ?? new LeadCycleEventPayload { CycleId = cycleId };
            payload.DeadlineUtc = DateTime.UtcNow.Add(ttl);
            await WriteCycleEventAsync(
                CycleHeartbeatEventType,
                cycleId,
                "Unattended lead cycle renewed its lease.",
                payload,
                token).ConfigureAwait(false);
            return true;
        }

        /// <summary>
        /// Complete an active cycle after it posts a non-empty handoff.
        /// </summary>
        /// <param name="cycleId">Cycle identifier.</param>
        /// <param name="handoff">Final handoff text.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>True when the caller owned and completed the cycle.</returns>
        public async Task<bool> CompleteAsync(string cycleId, string handoff, CancellationToken token = default)
        {
            ValidateCycleId(cycleId);
            if (String.IsNullOrWhiteSpace(handoff)) throw new ArgumentNullException(nameof(handoff));
            if (!await OwnsActiveLeaseAsync(cycleId, token).ConfigureAwait(false)) return false;

            LeadCycleEventPayload? started = await ReadCyclePayloadAsync(cycleId, token).ConfigureAwait(false);
            LeadCycleEventPayload payload = started ?? new LeadCycleEventPayload { CycleId = cycleId };
            payload.Handoff = handoff.Trim();
            await WriteCycleEventAsync(
                CycleCompletedEventType,
                cycleId,
                "Unattended lead cycle completed with a handoff.",
                payload,
                token).ConfigureAwait(false);
            await _Database.CoordinationLeases.ReleaseAsync(LeaseName, cycleId, token).ConfigureAwait(false);
            return true;
        }

        /// <summary>
        /// Fail or stop an active cycle and release its lease.
        /// </summary>
        /// <param name="cycleId">Cycle identifier.</param>
        /// <param name="reason">Failure or stop reason.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>True when the caller owned and failed the cycle.</returns>
        public async Task<bool> FailAsync(string cycleId, string reason, CancellationToken token = default)
        {
            ValidateCycleId(cycleId);
            if (String.IsNullOrWhiteSpace(reason)) throw new ArgumentNullException(nameof(reason));
            if (!await OwnsActiveLeaseAsync(cycleId, token).ConfigureAwait(false)) return false;

            LeadCycleEventPayload? started = await ReadCyclePayloadAsync(cycleId, token).ConfigureAwait(false);
            LeadCycleEventPayload payload = started ?? new LeadCycleEventPayload { CycleId = cycleId };
            payload.Reason = reason.Trim();
            await WriteCycleEventAsync(
                CycleFailedEventType,
                cycleId,
                "Unattended lead cycle failed or stopped: " + reason.Trim(),
                payload,
                token).ConfigureAwait(false);
            await _Database.CoordinationLeases.ReleaseAsync(LeaseName, cycleId, token).ConfigureAwait(false);
            return true;
        }

        /// <summary>
        /// Read the current mode and active cycle lease.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Current lead status.</returns>
        public async Task<LeadCycleStatus> GetStatusAsync(CancellationToken token = default)
        {
            LeadOperatingModeEnum mode = await GetModeAsync(token).ConfigureAwait(false);
            CoordinationLease? lease = await _Database.CoordinationLeases.ReadAsync(LeaseName, token).ConfigureAwait(false);
            if (lease == null || lease.ExpiresUtc <= DateTime.UtcNow)
                return new LeadCycleStatus { Mode = mode };

            LeadCycleEventPayload? payload = await ReadCyclePayloadAsync(lease.Holder, token).ConfigureAwait(false);
            return new LeadCycleStatus
            {
                Mode = mode,
                Active = true,
                CycleId = lease.Holder,
                StartedUtc = payload?.StartedUtc,
                DeadlineUtc = lease.ExpiresUtc,
                Runner = payload?.Runner,
                ParticipantKey = payload?.ParticipantKey
            };
        }

        /// <summary>
        /// Confirm that an authenticated Grok participant owns the current cycle.
        /// </summary>
        /// <param name="participantKey">Authenticated participant key.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Active cycle identifier.</returns>
        public async Task<string> RequireActiveGrokCycleAsync(
            string participantKey,
            CancellationToken token = default)
        {
            LeadCycleStatus status = await GetStatusAsync(token).ConfigureAwait(false);
            if (String.IsNullOrEmpty(status.CycleId))
                throw new InvalidOperationException("An authenticated Grok lead cycle must hold the shared lease before it can use coordination tools.");
            await RequireActiveCycleAsync(
                status.CycleId,
                LeadRunnerTypeEnum.Grok,
                participantKey,
                token).ConfigureAwait(false);
            return status.CycleId;
        }

        /// <summary>
        /// Confirm that one server-assigned runner and participant own a specific cycle.
        /// </summary>
        /// <param name="cycleId">Expected cycle identifier.</param>
        /// <param name="runner">Server-assigned lead implementation.</param>
        /// <param name="participantKey">Server-assigned participant identity.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>A completed task when ownership is valid.</returns>
        public async Task RequireActiveCycleAsync(
            string cycleId,
            LeadRunnerTypeEnum runner,
            string participantKey,
            CancellationToken token = default)
        {
            ValidateCycleId(cycleId);
            if (String.IsNullOrWhiteSpace(participantKey)) throw new ArgumentNullException(nameof(participantKey));
            LeadCycleStatus status = await GetStatusAsync(token).ConfigureAwait(false);
            if (!status.Active
                || !String.Equals(status.CycleId, cycleId, StringComparison.Ordinal)
                || status.Runner != runner
                || !String.Equals(status.ParticipantKey, participantKey.Trim(), StringComparison.Ordinal))
            {
                throw new InvalidOperationException("This lead implementation does not own the active shared cycle lease.");
            }
        }

        #endregion

        #region Private-Methods

        private async Task<string?> GetModeRefusalAsync(
            LeadOperatingModeEnum mode,
            LeadRunnerTypeEnum runner,
            bool standbyFallback,
            CancellationToken token)
        {
            if (mode == LeadOperatingModeEnum.Maintenance)
                return "Unattended lead cycles are disabled in Maintenance mode.";
            if (mode == LeadOperatingModeEnum.LegacyPrimary && runner == LeadRunnerTypeEnum.Grok)
                return "Grok Bot cannot start a cycle while the legacy lead is primary.";
            if (mode == LeadOperatingModeEnum.GrokPrimary && runner == LeadRunnerTypeEnum.Legacy)
            {
                if (!standbyFallback)
                    return "The legacy lead is standby while Grok Bot is primary.";
                if (!await IsStandbyFallbackDueAsync(token).ConfigureAwait(false))
                    return "The Grok standby fallback threshold has not elapsed.";
            }

            return null;
        }

        private async Task<bool> IsStandbyFallbackDueAsync(CancellationToken token)
        {
            DateTime? latestGrokActivity = null;
            string[] eventTypes = new[]
            {
                CycleStartedEventType,
                CycleHeartbeatEventType,
                CycleCompletedEventType,
                CycleFailedEventType
            };
            foreach (string eventType in eventTypes)
            {
                List<ArmadaEvent> events = await _Database.Events.EnumerateByTypeAsync(
                    eventType, 50, token).ConfigureAwait(false);
                foreach (ArmadaEvent armadaEvent in events)
                {
                    LeadCycleEventPayload? payload = DeserializePayload<LeadCycleEventPayload>(armadaEvent);
                    if (payload?.Runner != LeadRunnerTypeEnum.Grok) continue;
                    if (!latestGrokActivity.HasValue || armadaEvent.CreatedUtc > latestGrokActivity.Value)
                        latestGrokActivity = armadaEvent.CreatedUtc;
                }
            }

            ArmadaEvent? modeEvent = await ReadLatestEventAsync(ModeChangedEventType, token).ConfigureAwait(false);
            DateTime? baseline = latestGrokActivity;
            if (modeEvent != null && (!baseline.HasValue || modeEvent.CreatedUtc > baseline.Value))
                baseline = modeEvent.CreatedUtc;
            if (!baseline.HasValue) return false;
            return DateTime.UtcNow - baseline.Value >= TimeSpan.FromMinutes(_Settings.StandbyFallbackAfterMinutes);
        }

        private async Task<bool> OwnsActiveLeaseAsync(string cycleId, CancellationToken token)
        {
            CoordinationLease? lease = await _Database.CoordinationLeases.ReadAsync(LeaseName, token).ConfigureAwait(false);
            return lease != null
                && lease.ExpiresUtc > DateTime.UtcNow
                && String.Equals(lease.Holder, cycleId, StringComparison.Ordinal);
        }

        private async Task<LeadCycleEventPayload?> ReadCyclePayloadAsync(
            string cycleId,
            CancellationToken token)
        {
            List<ArmadaEvent> events = await _Database.Events.EnumerateByEntityAsync(
                "lead_cycle", cycleId, 50, token).ConfigureAwait(false);
            ArmadaEvent? started = events
                .Where(item => String.Equals(item.EventType, CycleStartedEventType, StringComparison.Ordinal))
                .OrderByDescending(item => item.CreatedUtc)
                .FirstOrDefault();
            return DeserializePayload<LeadCycleEventPayload>(started);
        }

        private async Task<ArmadaEvent?> ReadLatestEventAsync(string eventType, CancellationToken token)
        {
            List<ArmadaEvent> events = await _Database.Events.EnumerateByTypeAsync(eventType, 50, token).ConfigureAwait(false);
            return events.OrderByDescending(item => item.CreatedUtc).FirstOrDefault();
        }

        private async Task WriteCycleEventAsync(
            string eventType,
            string cycleId,
            string message,
            LeadCycleEventPayload payload,
            CancellationToken token)
        {
            ArmadaEvent armadaEvent = new ArmadaEvent(eventType, message)
            {
                EntityType = "lead_cycle",
                EntityId = cycleId,
                Payload = JsonSerializer.Serialize(payload, _JsonOptions)
            };
            await _Database.Events.CreateAsync(armadaEvent, token).ConfigureAwait(false);
        }

        private static T? DeserializePayload<T>(ArmadaEvent? armadaEvent)
            where T : class
        {
            if (String.IsNullOrWhiteSpace(armadaEvent?.Payload)) return null;
            try
            {
                return JsonSerializer.Deserialize<T>(armadaEvent.Payload, _JsonOptions);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static void ValidateCycleId(string cycleId)
        {
            if (String.IsNullOrWhiteSpace(cycleId)) throw new ArgumentNullException(nameof(cycleId));
            if (!cycleId.StartsWith("lcy_", StringComparison.Ordinal))
                throw new ArgumentException("Lead cycle identifiers must start with lcy_.", nameof(cycleId));
        }

        #endregion
    }
}
