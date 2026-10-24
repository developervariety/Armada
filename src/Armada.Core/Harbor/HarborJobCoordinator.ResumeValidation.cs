namespace Armada.Core.Harbor
{
    using System;

    public sealed partial class HarborJobCoordinator
    {
        private string? ValidateResumeProof(HarborRunnerSession session, JobRecord job, HarborResumeJobInfo proof, out bool markUncertain)
        {
            markUncertain = false;
            if (!String.Equals(job.RunnerId, session.Identity.RunnerId, StringComparison.Ordinal)
                || !String.Equals(job.OwnerTenantId, session.Identity.TenantId, StringComparison.Ordinal)
                || !String.Equals(job.OwnerUserId, session.Identity.UserId, StringComparison.Ordinal))
                return "harbor_resume_attempt_mismatch";
            if (job.EnrollmentGeneration != session.EnrollmentGeneration)
            {
                markUncertain = !HarborJobRecord.IsTerminal(job.State);
                return "harbor_enrollment_changed";
            }
            if (!String.Equals(job.LaunchKey, proof.LaunchKey, StringComparison.Ordinal) || !SameRunnerProcess(job, proof))
            {
                markUncertain = !HarborJobRecord.IsTerminal(job.State);
                return "harbor_resume_attempt_mismatch";
            }
            if (proof.FirstAvailableOutputSequence < 0 || proof.NextOutputSequence < proof.FirstAvailableOutputSequence
                || proof.NextOutputSequence == Int64.MaxValue || job.NextOutputSequence < proof.FirstAvailableOutputSequence
                || job.NextOutputSequence > proof.NextOutputSequence)
            {
                markUncertain = true;
                return "harbor_resume_output_gap";
            }
            if (proof.IsRunning == proof.StopConfirmed) return "harbor_resume_state_invalid";
            if (proof.StopConfirmed && !proof.ExitCode.HasValue) return "harbor_resume_exit_code_unknown";
            return null;
        }

        private static void RejectResume(HarborResumeJobResult decision, string reason)
        {
            decision.Decision = HarborResumeDecisionEnum.Reject;
            decision.Reason = reason;
        }
    }
}
