namespace Armada.Server
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Server.Mcp;

    public sealed partial class AskActionService
    {
        private async Task<AskProposalDecision> ExecuteApprovedAsync(AuthContext human, AskActionProposal proposal)
        {
            try
            {
                JsonElement arguments = JsonSerializer.Deserialize<JsonElement>(proposal.ArgumentsJson);
                if (AskActionArguments.Canonicalize(arguments) != proposal.ArgumentsJson)
                    throw new InvalidOperationException("Stored action arguments are not canonical.");
                object result;
                using (McpCallerContext.Begin(human))
                using (McpCallerContext.BeginCall(CancellationToken.None))
                    result = await _Handlers[proposal.ToolName](arguments).ConfigureAwait(false);
                AskHandlerOutcome outcome = AskHandlerOutcome.Read(result);
                if (outcome.JobId != null)
                {
                    bool recorded = await _Database.AskActionProposals.RecordExecutingWorkAsync(proposal.TenantId, proposal.UserId,
                        proposal.ThreadId, proposal.Id, outcome.WorkReferences, CancellationToken.None).ConfigureAwait(false);
                    if (!recorded) throw new InvalidOperationException("The accepted job reference could not be recorded.");
                    proposal.WorkReferences = outcome.WorkReferences;
                    return Decision(200, await ObserveAsync(proposal, CancellationToken.None).ConfigureAwait(false), "The tool accepted a job. Its outcome is tracked separately.");
                }
                if (!await CompleteObservedAsync(proposal, outcome, CancellationToken.None).ConfigureAwait(false))
                    throw new InvalidOperationException("The action result could not claim terminal persistence.");
                return Decision(200, await ReadCurrentAsync(proposal, CancellationToken.None).ConfigureAwait(false));
            }
            catch (Exception ex)
            {
                _Logging.Warn("[AskActionService] outcome uncertain for action " + proposal.Id + " (" + ex.GetType().Name + ")");
                proposal.State = AskProposalStateEnum.Uncertain;
                return Decision(409, proposal, "The action may have run. Its outcome is uncertain and it will not run again automatically.");
            }
        }

        private async Task<bool> CompleteObservedAsync(AskActionProposal proposal, AskHandlerOutcome outcome, CancellationToken token)
        {
            AskProposalStateEnum state = outcome.Failed ? AskProposalStateEnum.Failed : AskProposalStateEnum.Executed;
            string summary = outcome.Failed ? "The tool reported a failure. Linked work can have a separate outcome."
                : "The tool completed. This does not establish that its work was delivered.";
            return await _Database.AskActionProposals.CompleteAsync(proposal.TenantId, proposal.UserId,
                proposal.ThreadId, proposal.Id, state, summary, outcome.WorkReferences, DateTime.UtcNow, token).ConfigureAwait(false);
        }

        private async Task<AskActionProposal> ReadCurrentAsync(AskActionProposal proposal, CancellationToken token)
        {
            return await _Database.AskActionProposals.ReadAsync(proposal.TenantId, proposal.UserId,
                proposal.ThreadId, proposal.Id, token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The action result is not readable.");
        }
    }
}
