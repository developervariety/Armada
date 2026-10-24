namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Harbor;
    using Armada.Core.Models;

    public partial class AdmiralService
    {
        private async Task<HarborJobSnapshot?> ReadHarborCompletionJobAsync(Mission mission, CancellationToken token)
        {
            if (String.IsNullOrWhiteSpace(mission.HarborJobId)) return null;
            HarborJobRecord? record = await _Database.HarborJobs.ReadAsync(mission.HarborJobId, token).ConfigureAwait(false);
            if (record == null || !HarborJobRecord.IsTerminal(record.State) || !record.StopConfirmed
                || !String.Equals(record.MissionId, mission.Id, StringComparison.Ordinal) || record.CompletionDelivered
                || !String.Equals(record.CaptainId, mission.CaptainId, StringComparison.Ordinal)
                || !String.Equals(record.TenantId, mission.TenantId, StringComparison.Ordinal)
                || !String.Equals(record.UserId, mission.UserId, StringComparison.Ordinal)
                || !String.Equals(record.DockId, mission.DockId, StringComparison.Ordinal)
                || record.AdmiralProcessId != mission.ProcessId
                || !String.Equals(record.JobId, mission.HarborJobId, StringComparison.Ordinal)) return null;
            return new HarborJobSnapshot(record.JobId, record.RunnerId, record.LaunchKey, record.TenantId, record.UserId,
                record.EnrollmentGeneration, record.SessionGeneration, record.State, record.ProcessId, record.ExitCode,
                record.FailureReason, new List<HarborOutput>(), record.TenantId, record.UserId, record.MissionId,
                record.CaptainId, record.NextOutputSequence, record.RunnerProcessStartedUtc, record.DockId,
                record.AdmiralProcessId, record.AdmiralProcessStartedUtc, record.StopConfirmed,
                record.StopRequested, record.CompletionDelivered, record.Revision);
        }

        private async Task<HarborCompletionIntent?> CreateHarborCompletionIntentAsync(
            HarborJobSnapshot job, Mission mission, HarborCompletionOutcomeEnum outcome, string failureReason,
            string reasonCode, CaptainStateEnum captainState, DateTime? quarantineUntilUtc, string? quarantineReason,
            HarborCompletionPayload payload, CancellationToken token, int? recoveryAttempts = null)
        {
            HarborCompletionIntent intent = new HarborCompletionIntent
            {
                JobId = job.JobId,
                Outcome = outcome,
                FailureReason = failureReason,
                ReasonCode = reasonCode,
                CaptainState = captainState,
                QuarantineUntilUtc = quarantineUntilUtc,
                QuarantineReason = quarantineReason,
                RecoveryAttempts = recoveryAttempts,
                FactId = new MissionAttemptFact().Id,
                EventId = new ArmadaEvent().Id,
                SignalId = new Signal().Id,
                Payload = JsonSerializer.Serialize(payload)
            };
            if (await _Database.HarborJobs.TryCreateCompletionIntentAsync(intent, token).ConfigureAwait(false)) return intent;
            HarborCompletionIntent? existing = await _Database.HarborJobs.ReadCompletionIntentAsync(job.JobId, token).ConfigureAwait(false);
            if (existing == null || existing.Outcome != outcome
                || !String.Equals(existing.FailureReason, failureReason, StringComparison.Ordinal)
                || !String.Equals(existing.ReasonCode, reasonCode, StringComparison.Ordinal)
                || existing.CaptainState != captainState
                || !String.Equals(existing.QuarantineReason, quarantineReason, StringComparison.Ordinal)
                || existing.RecoveryAttempts != recoveryAttempts
                || !CompletionPayloadMatches(existing.Payload, payload)) return null;
            return existing;
        }

        private static bool CompletionPayloadMatches(string existingPayloadJson, HarborCompletionPayload expected)
        {
            HarborCompletionPayload? existing;
            try { existing = JsonSerializer.Deserialize<HarborCompletionPayload>(existingPayloadJson); }
            catch (JsonException) { return false; }
            return existing != null
                && String.Equals(existing.MissionId, expected.MissionId, StringComparison.Ordinal)
                && String.Equals(existing.CaptainId, expected.CaptainId, StringComparison.Ordinal)
                && String.Equals(existing.DockId, expected.DockId, StringComparison.Ordinal)
                && String.Equals(existing.VesselId, expected.VesselId, StringComparison.Ordinal)
                && String.Equals(existing.VoyageId, expected.VoyageId, StringComparison.Ordinal)
                && existing.RecoveryAttempts == expected.RecoveryAttempts && existing.FactType == expected.FactType
                && String.Equals(existing.EventType, expected.EventType, StringComparison.Ordinal)
                && String.Equals(existing.EventMessage, expected.EventMessage, StringComparison.Ordinal)
                && String.Equals(existing.SignalMessage, expected.SignalMessage, StringComparison.Ordinal);
        }

        private static DateTime? NormalizeHarborUtc(DateTime? value)
        {
            if (!value.HasValue) return null;
            if (value.Value.Kind == DateTimeKind.Unspecified) return DateTime.SpecifyKind(value.Value, DateTimeKind.Utc);
            return value.Value.ToUniversalTime();
        }
    }
}
