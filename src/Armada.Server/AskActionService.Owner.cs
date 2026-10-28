namespace Armada.Server
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Server.Mcp;

    public sealed partial class AskActionService
    {
        /// <summary>Read current proposals for the exact interactive owner.</summary>
        public async Task<List<AskActionProposal>?> ListAsync(AuthContext caller, string threadId, CancellationToken token = default)
        {
            if (await HumanForThreadAsync(caller, threadId, token).ConfigureAwait(false) == null) return null;
            List<AskActionProposal> proposals = await _Database.AskActionProposals.EnumerateAsync(caller.TenantId!, caller.UserId!, threadId, 200, token).ConfigureAwait(false);
            List<AskActionProposal> result = new List<AskActionProposal>();
            foreach (AskActionProposal proposal in proposals)
                result.Add(await DescribedAsync(await ObserveAsync(proposal, token).ConfigureAwait(false), token).ConfigureAwait(false));
            return result;
        }

        private async Task<AskActionProposal> DescribedAsync(AskActionProposal proposal, CancellationToken token)
        {
            await AskProposalSummary.ApplyAsync(_Database, proposal, token).ConfigureAwait(false);
            return proposal;
        }

        private async Task<AskProposalDecision> DescribedAsync(AskProposalDecision decision, CancellationToken token)
        {
            if (decision.Proposal != null) await DescribedAsync(decision.Proposal, token).ConfigureAwait(false);
            return decision;
        }

        private async Task<AuthContext?> HumanForThreadAsync(AuthContext caller, string threadId, CancellationToken token)
        {
            RequireHuman(caller);
            UserMaster? user = await _Database.Users.ReadByIdAsync(caller.UserId!, token).ConfigureAwait(false);
            TenantMetadata? tenant = await _Database.Tenants.ReadAsync(caller.TenantId!, token).ConfigureAwait(false);
            if (user == null || !user.Active || !user.IsAdmin || user.TenantId != caller.TenantId || tenant == null || !tenant.Active)
                throw new UnauthorizedAccessException("The conversation owner no longer has access.");
            AskThread? thread = await _Database.AskThreads.ReadAsync(caller.TenantId!, caller.UserId!, threadId, token).ConfigureAwait(false);
            if (thread == null) return null;
            return AuthContext.Authenticated(caller.TenantId!, caller.UserId!, user.IsAdmin, user.IsTenantAdmin, "AskApproval");
        }

        private static void RequireHuman(AuthContext caller)
        {
            if (caller == null || !caller.IsAuthenticated || !caller.IsAdmin || AskToolPolicy.HasScope(caller)
                || !String.IsNullOrWhiteSpace(caller.MissionId) || String.IsNullOrWhiteSpace(caller.TenantId)
                || String.IsNullOrWhiteSpace(caller.UserId))
                throw new UnauthorizedAccessException("Saved action decisions require the authenticated human owner.");
        }

        private static AskProposalDecision Decision(int status, AskActionProposal? proposal = null, string? message = null)
            => new AskProposalDecision { StatusCode = status, Proposal = proposal, Message = message };
    }
}
