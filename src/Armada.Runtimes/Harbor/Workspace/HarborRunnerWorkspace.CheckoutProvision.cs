namespace Armada.Runtimes.Harbor
{
    using System;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Harbor;
    using Armada.Core.Models;

    /// <summary>Creates a pinned runner-owned Git checkout.</summary>
    public sealed partial class HarborRunnerWorkspace
    {
        private async Task ProvisionCheckoutAsync(
            HarborWorkspaceOperationRequest request,
            HarborCheckoutBinding binding,
            HarborRepositoryGrant grant,
            HarborWorkspaceOperationResult result,
            CancellationToken token)
        {
            if (!String.Equals(request.DockId, request.DockId.Trim(), StringComparison.Ordinal)
                || !IsFullCommit(binding.BaseCommit)
                || !String.Equals(request.ExpectedHead, binding.BaseCommit, StringComparison.OrdinalIgnoreCase))
                throw Failure("workspace_checkout_identity_invalid", "The checkout commit or dock identity is invalid.");
            await VerifyRepositoryAsync(grant, token).ConfigureAwait(false);
            await VerifyCommitAsync(grant.RepositoryPath, binding.BaseCommit, token).ConfigureAwait(false);
            bool existingBranch = await HasMatchingExistingBranchAsync(grant, binding, request.Detached, token).ConfigureAwait(false);

            HarborCheckoutIntent intent;
            try
            {
                intent = _Journal.Read(binding.CheckoutId);
                if (!SameBinding(intent.Binding, binding) || intent.DockId != request.DockId || intent.VesselId != request.VesselId)
                    throw Failure("workspace_checkout_id_conflict", "The checkout identifier belongs to another durable dock binding.");
                if (String.Equals(intent.State, "Ready", StringComparison.Ordinal))
                {
                    await InspectCheckoutAsync(intent, grant, result, token).ConfigureAwait(false);
                    return;
                }
                throw Failure("workspace_checkout_uncertain", "The existing checkout intent requires operator recovery.");
            }
            catch (HarborJobJournalException exception) when (String.Equals(exception.ReasonCode, "checkout_record_missing", StringComparison.Ordinal))
            {
                intent = new HarborCheckoutIntent
                {
                    Binding = binding,
                    DockId = request.DockId,
                    VesselId = request.VesselId,
                    RequestId = request.RequestId,
                    State = "Pending"
                };
                _Journal.Create(intent);
            }

            string checkoutPath = CheckoutPath(grant, binding, requireExists: false);
            if (Directory.Exists(checkoutPath) || File.Exists(checkoutPath))
            {
                intent.State = "Uncertain";
                intent.FailureReason = "workspace_checkout_path_preexists";
                _Journal.Update(intent);
                throw Failure("workspace_checkout_path_preexists", "The checkout path already exists and is not owned by this intent.");
            }

            try
            {
                string[] arguments = request.Detached
                    ? new[] { "worktree", "add", "--detach", checkoutPath, binding.BaseCommit }
                    : existingBranch
                        ? new[] { "worktree", "add", checkoutPath, binding.BranchName }
                        : new[] { "worktree", "add", "-b", binding.BranchName, checkoutPath, binding.BaseCommit };
                HarborWorkspaceProcessResult add = await RunGitAsync(grant.RepositoryPath, arguments,
                    TimeSpan.FromSeconds(120), 64 * 1024, token).ConfigureAwait(false);
                RequireSuccess(add, "workspace_checkout_provision_failed");
                EnsureNoLinks(checkoutPath, requireExists: true);
                string head = await ReadHeadAsync(checkoutPath, token).ConfigureAwait(false);
                if (!String.Equals(head, binding.BaseCommit, StringComparison.OrdinalIgnoreCase))
                    throw Failure("workspace_checkout_base_mismatch", "The created checkout does not match its bound base commit.");
                intent.State = "Ready";
                intent.FailureReason = null;
                intent.UpdatedUtc = DateTime.UtcNow;
                _Journal.Update(intent);
                result.Binding = binding;
                result.HeadCommit = head;
                result.BranchName = request.Detached ? "(detached)" : binding.BranchName;
                result.CanonicalWorkingDirectory = checkoutPath;
                result.Accepted = true;
            }
            catch (Exception exception) when (!(exception is OperationCanceledException && token.IsCancellationRequested))
            {
                intent.State = "Uncertain";
                intent.FailureReason = exception is HarborJobJournalException journalException
                    ? journalException.ReasonCode
                    : "workspace_checkout_provision_uncertain";
                intent.UpdatedUtc = DateTime.UtcNow;
                _Journal.Update(intent);
                throw Failure("workspace_checkout_provision_uncertain", "The checkout operation did not prove a complete result.");
            }
        }

    }
}
