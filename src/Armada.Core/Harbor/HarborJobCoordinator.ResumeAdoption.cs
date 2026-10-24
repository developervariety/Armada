namespace Armada.Core.Harbor
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;

    public sealed partial class HarborJobCoordinator
    {
        private async Task ResumeOneJobAsync(HarborRunnerSession session, HarborResumeJobInfo proof, HarborResumeJobResult decision, CancellationToken token)
        {
            JobRecord? job;
            lock (_Gate) _Jobs.TryGetValue(proof.JobId, out job);
            if (job == null)
            {
                RejectResume(decision, "harbor_resume_attempt_mismatch");
                return;
            }
            string? invalidReason = ValidateResumeProof(session, job, proof, out bool markUncertain);
            if (invalidReason != null)
            {
                if (markUncertain)
                    await MarkUncertainAndPersistAsync(job, invalidReason).ConfigureAwait(false);
                RejectResume(decision, invalidReason);
                return;
            }
            if (HarborJobRecord.IsTerminal(job.State))
            {
                if (!job.StopConfirmed)
                {
                    decision.Decision = HarborResumeDecisionEnum.Stop;
                    decision.Reason = ReasonResumeUnconfirmed;
                    return;
                }
                decision.Decision = HarborResumeDecisionEnum.Replay;
                decision.ReplayFromSequence = job.NextOutputSequence;
                return;
            }
            await AdoptResumeProofAsync(job, session, proof, decision, token).ConfigureAwait(false);
        }

        private async Task AdoptResumeProofAsync(JobRecord job, HarborRunnerSession session, HarborResumeJobInfo proof, HarborResumeJobResult decision, CancellationToken token)
        {
            HarborJobRecoveryResult adoption = await GetResumeAdoptionAsync(job, session, token).ConfigureAwait(false);
            if (adoption.Disposition != HarborJobRecoveryDispositionEnum.Adopted)
            {
                await MarkUncertainAndPersistAsync(job, ReasonResumeUnconfirmed).ConfigureAwait(false);
                decision.Decision = HarborResumeDecisionEnum.Stop;
                decision.Reason = ReasonResumeUnconfirmed;
                return;
            }
            bool sessionCurrent;
            lock (_Gate)
            {
                sessionCurrent = _Registry.IsCurrent(session);
                if (!sessionCurrent)
                {
                    RejectResume(decision, "harbor_session_stale");
                }
                else
                {
                    job.Observer = adoption.Observer ?? job.Observer;
                    job.SessionGeneration = session.Generation;
                    job.State = job.StopRequested ? HarborJobStateEnum.Stopping : HarborJobStateEnum.Running;
                    job.StopConfirmed = false;
                    job.ResumeReplayTarget = proof.NextOutputSequence;
                    job.ResumeTerminalPending = proof.StopConfirmed;
                    job.ResumeExitCode = proof.ExitCode;
                    Touch(job, null);
                }
            }
            if (!sessionCurrent) return;
            if (!await PersistJobAsync(job, token).ConfigureAwait(false))
            {
                await MarkUncertainAndPersistAsync(job, ReasonResumeUnconfirmed).ConfigureAwait(false);
                decision.Decision = HarborResumeDecisionEnum.Stop;
                decision.Reason = ReasonResumeUnconfirmed;
                return;
            }
            decision.Decision = job.StopRequested && proof.IsRunning ? HarborResumeDecisionEnum.Stop : HarborResumeDecisionEnum.Replay;
            if (decision.Decision == HarborResumeDecisionEnum.Stop) decision.Reason = "harbor_stop_requested";
            decision.ReplayFromSequence = job.NextOutputSequence;
        }

        private async Task<HarborJobRecoveryResult> GetResumeAdoptionAsync(JobRecord job, HarborRunnerSession session, CancellationToken token)
        {
            if (String.IsNullOrWhiteSpace(job.MissionId)) return new HarborJobRecoveryResult(HarborJobRecoveryDispositionEnum.Adopted);
            if (_RecoveryHost != null) return await _RecoveryHost.TryAdoptAsync(job.Snapshot(), session, token).ConfigureAwait(false);
            return job.Observer == null
                ? new HarborJobRecoveryResult(HarborJobRecoveryDispositionEnum.Uncertain)
                : new HarborJobRecoveryResult(HarborJobRecoveryDispositionEnum.Adopted);
        }
    }
}
