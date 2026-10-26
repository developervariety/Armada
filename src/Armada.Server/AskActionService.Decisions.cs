namespace Armada.Server
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Server.Mcp;

    public sealed partial class AskActionService
    {
        /// <summary>Approve the exact reviewed action under the owner's current authorization.</summary>
        public async Task<AskProposalDecision> ApproveAsync(AuthContext caller, string threadId, string proposalId, string fingerprint, CancellationToken token = default)
        {
            AuthContext? human = await HumanForThreadAsync(caller, threadId, token).ConfigureAwait(false);
            if (human == null) return Decision(404, message: "Conversation not found.");
            AskActionProposal? proposal = await _Database.AskActionProposals.ReadAsync(human.TenantId!, human.UserId!, threadId, proposalId, token).ConfigureAwait(false);
            if (proposal == null) return Decision(404, message: "Action proposal not found.");
            if (!String.Equals(proposal.Fingerprint, fingerprint, StringComparison.Ordinal)
                || !String.Equals(proposal.Fingerprint, AskActionArguments.Fingerprint(proposal), StringComparison.Ordinal))
                return Decision(409, message: "The reviewed action fingerprint does not match.");
            if (!AskToolPolicy.CanPropose(proposal.ToolName) || !McpToolAccessPolicy.IsAllowed(human, proposal.ToolName)
                || !_Handlers.ContainsKey(proposal.ToolName))
                return Decision(403, message: "This action is not available under the owner's current permissions.");
            if (proposal.State != AskProposalStateEnum.Pending)
                return Decision(200, await ObserveAsync(proposal, token).ConfigureAwait(false), "The action already has a recorded decision.");
            if (proposal.ExpiresUtc <= DateTime.UtcNow)
                return Decision(409, await ObserveAsync(proposal, token).ConfigureAwait(false), "The proposal expired. It cannot execute.");
            if (!_Active.TryAdd(proposal.Id, 0)) return Decision(409, message: "This action decision is already in progress.");
            try
            {
                bool claimed = await _Database.AskActionProposals.TryClaimAsync(human.TenantId!, human.UserId!, threadId, proposalId, fingerprint, DateTime.UtcNow, token).ConfigureAwait(false);
                if (!claimed) return Decision(409, message: "The proposal was already decided or expired.");
                proposal.State = AskProposalStateEnum.Executing;
                return await ExecuteApprovedAsync(human, proposal).ConfigureAwait(false);
            }
            finally { _Active.TryRemove(proposal.Id, out byte removed); }
        }

        /// <summary>Reject the exact reviewed proposal without invoking any tool.</summary>
        public async Task<AskProposalDecision> RejectAsync(AuthContext caller, string threadId, string proposalId, string fingerprint, CancellationToken token = default)
        {
            AuthContext? human = await HumanForThreadAsync(caller, threadId, token).ConfigureAwait(false);
            if (human == null) return Decision(404, message: "Conversation not found.");
            AskActionProposal? proposal = await _Database.AskActionProposals.ReadAsync(human.TenantId!, human.UserId!, threadId, proposalId, token).ConfigureAwait(false);
            if (proposal == null) return Decision(404, message: "Action proposal not found.");
            if (!String.Equals(proposal.Fingerprint, fingerprint, StringComparison.Ordinal))
                return Decision(409, message: "The reviewed action fingerprint does not match.");
            bool rejected = await _Database.AskActionProposals.TryRejectAsync(human.TenantId!, human.UserId!, threadId, proposalId, fingerprint, DateTime.UtcNow, token).ConfigureAwait(false);
            AskActionProposal? current = await _Database.AskActionProposals.ReadAsync(human.TenantId!, human.UserId!, threadId, proposalId, token).ConfigureAwait(false);
            return Decision(rejected || current?.State == AskProposalStateEnum.Rejected ? 200 : 409, current,
                rejected ? "The action was rejected." : "The action already has a recorded decision.");
        }
    }
}
