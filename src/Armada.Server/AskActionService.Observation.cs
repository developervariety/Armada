namespace Armada.Server
{
    using System;
    using System.Linq;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    public sealed partial class AskActionService
    {
        private async Task<AskActionProposal> ObserveAsync(AskActionProposal proposal, CancellationToken token)
        {
            if (proposal.State == AskProposalStateEnum.Pending && proposal.ExpiresUtc <= DateTime.UtcNow)
            {
                proposal.State = AskProposalStateEnum.Expired;
                return proposal;
            }
            if (proposal.State != AskProposalStateEnum.Executing || _Active.ContainsKey(proposal.Id)) return proposal;
            AskWorkReference? reference = proposal.WorkReferences.FirstOrDefault(item => item.Kind == AskWorkKindEnum.Job);
            if (reference == null || !_Jobs.TryGetStatus(reference.Id, out LongRunningJob? job) || job == null
                || job.Status == LongRunningJobStatusEnum.Lost)
                return Uncertain(proposal, "The prior execution has no confirmed outcome. It will not run again automatically.");
            if (job.Status == LongRunningJobStatusEnum.Accepted || job.Status == LongRunningJobStatusEnum.Running) return proposal;
            AskHandlerOutcome outcome;
            try
            {
                outcome = job.Status == LongRunningJobStatusEnum.Succeeded
                    ? AskHandlerOutcome.Read(job.Result) : new AskHandlerOutcome { Failed = true };
            }
            catch (Exception ex) when (ex is JsonException || ex is InvalidOperationException)
            {
                return Uncertain(proposal, "The completed job result cannot be confirmed. It will not run again automatically.");
            }
            outcome.Add(AskWorkKindEnum.Job, reference.Id);
            bool completed = await CompleteObservedAsync(proposal, outcome, token).ConfigureAwait(false);
            AskActionProposal current = await ReadCurrentAsync(proposal, token).ConfigureAwait(false);
            if (!completed && current.State == AskProposalStateEnum.Executing)
                return Uncertain(current, "The job ended, but its saved result could not be confirmed. Do not repeat the action.");
            return current;
        }

        private static AskActionProposal Uncertain(AskActionProposal proposal, string message)
        {
            proposal.State = AskProposalStateEnum.Uncertain;
            proposal.Outcome = message;
            return proposal;
        }
    }
}
