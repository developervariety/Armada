namespace Armada.Core.Harbor
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;

    public sealed partial class HarborJobCoordinator
    {
        /// <summary>Validate runner process proofs and return the persisted output sequence for safe replay.</summary>
        /// <param name="session">Authenticated current runner session.</param>
        /// <param name="request">Typed runner resume proof.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Per-job decisions.</returns>
        public async Task<HarborResumeResponse> HandleResumeRequestAsync(HarborRunnerSession session, HarborResumeRequest request, CancellationToken token = default)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            if (request == null || request.Jobs == null || request.Jobs.Count > 1024)
                throw new ArgumentException("Harbor resume request is invalid.", nameof(request));
            HarborRunnerCheck check = await _Registry.RevalidateAsync(session, token).ConfigureAwait(false);
            if (!check.Accepted || !_Registry.IsCurrent(session))
                throw new UnauthorizedAccessException("harbor_session_stale");

            HarborResumeResponse response = new HarborResumeResponse { CorrelationId = request.CorrelationId, TraceParent = request.TraceParent };
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (HarborResumeJobInfo proof in request.Jobs)
            {
                token.ThrowIfCancellationRequested();
                HarborResumeJobResult decision = new HarborResumeJobResult { JobId = proof?.JobId ?? String.Empty };
                response.Jobs.Add(decision);
                if (proof == null || String.IsNullOrWhiteSpace(proof.JobId) || !seen.Add(proof.JobId))
                {
                    RejectResume(decision, "harbor_resume_proof_invalid");
                    continue;
                }
                await ResumeOneJobAsync(session, proof, decision, token).ConfigureAwait(false);
            }
            return response;
        }
    }
}
