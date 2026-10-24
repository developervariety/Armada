namespace Armada.Runtimes.Harbor
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Harbor;

    /// <summary>Builds exact local process proofs and requests safe stops.</summary>
    public sealed partial class HarborRunnerClient
    {
        private void ApplyResumeResponse(
            HarborResumeResponse response,
            List<HarborResumeJobInfo> proofs,
            List<HarborLocalJobRecord> records,
            Func<HarborMessage, Task> send,
            HashSet<string> exitSent,
            List<Task> background,
            CancellationToken token)
        {
            Dictionary<string, HarborLocalJobRecord> byId = records.ToDictionary(record => record.JobId, StringComparer.Ordinal);
            HashSet<string> decidedIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (HarborResumeJobResult decision in response.Jobs)
            {
                if (!byId.TryGetValue(decision.JobId, out HarborLocalJobRecord? record)) continue;
                if (!decidedIds.Add(decision.JobId))
                {
                    _Log("Harbor resume response contains a duplicate decision: " + decision.JobId + " harbor_resume_response_duplicate");
                    continue;
                }
                if (decision.Decision == HarborResumeDecisionEnum.Reject)
                {
                    RequestSafeStop(record);
                    _Log("Harbor job resume refused: " + record.JobId + " " + SafeReason(decision.Reason));
                }
                else if (decision.Decision == HarborResumeDecisionEnum.Stop)
                {
                    RequestSafeStop(record);
                    StartJobPump(record.JobId, decision.ReplayFromSequence, send, exitSent, background, token);
                }
                else if (decision.Decision == HarborResumeDecisionEnum.Replay)
                    StartJobPump(record.JobId, decision.ReplayFromSequence, send, exitSent, background, token);
            }
            foreach (HarborResumeJobInfo proof in proofs)
            {
                if (decidedIds.Contains(proof.JobId)) continue;
                _Log("Harbor job resume response missing: " + proof.JobId + " harbor_resume_response_missing");
                if (byId.TryGetValue(proof.JobId, out HarborLocalJobRecord? record)) RequestSafeStop(record);
            }
        }

        private HarborResumeJobInfo? BuildResumeProof(HarborLocalJobRecord record)
        {
            if (record.OutputGap || record.NextOutputSequence == Int64.MaxValue || !String.IsNullOrWhiteSpace(record.FailureReason)) return null;
            bool hasIdentity = record.ProcessId.HasValue && record.ProcessId.Value > 0 && record.ProcessStartedUtc.HasValue;
            if (!hasIdentity) return null;
            HarborResumeJobInfo proof = new HarborResumeJobInfo
            {
                JobId = record.JobId,
                LaunchKey = record.LaunchKey,
                ProcessId = record.ProcessId,
                ProcessStartedUtc = NormalizeUtc(record.ProcessStartedUtc.Value),
                FirstAvailableOutputSequence = record.FirstOutputSequence,
                NextOutputSequence = record.NextOutputSequence
            };
            if (record.StopConfirmed && record.ExitCode.HasValue)
            {
                proof.StopConfirmed = true;
                proof.ExitCode = record.ExitCode;
                return proof;
            }
            if (!record.IsRunning
                || !HarborJobJournal.IsExactProcessAlive(record.SupervisorProcessId, record.SupervisorStartedUtc,
                    record.SupervisorLinuxStartTicks, record.SupervisorLinuxBootId)
                || !HarborJobJournal.IsExactProcessAlive(record.ProcessId.Value, record.ProcessStartedUtc,
                    record.ProcessLinuxStartTicks, record.ProcessLinuxBootId)) return null;
            proof.IsRunning = true;
            return proof;
        }

        private bool RequestSafeStop(HarborLocalJobRecord record, int gracePeriodMs = 10000)
        {
            if (!record.IsRunning || !record.ProcessId.HasValue || !record.ProcessStartedUtc.HasValue
                || !HarborJobJournal.IsExactProcessAlive(record.SupervisorProcessId, record.SupervisorStartedUtc,
                    record.SupervisorLinuxStartTicks, record.SupervisorLinuxBootId)
                || !HarborJobJournal.IsExactProcessAlive(record.ProcessId.Value, record.ProcessStartedUtc,
                    record.ProcessLinuxStartTicks, record.ProcessLinuxBootId))
            {
                _Log("Harbor job stop remains fenced: " + record.JobId + " harbor_process_identity_unverified");
                return false;
            }
            _Journal.WriteStopRequest(record.JobId, record.LaunchKey, gracePeriodMs);
            return true;
        }
    }
}
