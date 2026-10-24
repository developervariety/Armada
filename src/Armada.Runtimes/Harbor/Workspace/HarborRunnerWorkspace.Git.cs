namespace Armada.Runtimes.Harbor
{
    using System;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Harbor;
    using Armada.Runtimes.Harbor.Workspace.Git;
    using Armada.Core.Services;

    /// <summary>Runs the fork Git service only against paths selected from durable checkout grants.</summary>
    public sealed partial class HarborRunnerWorkspace
    {
        private async Task ExecuteGitOperationAsync(
            HarborWorkspaceOperationRequest request,
            HarborCheckoutIntent intent,
            HarborRepositoryGrant grant,
            HarborWorkspaceOperationResult result,
            CancellationToken token)
        {
            if (_Git == null) throw Failure("workspace_git_unavailable", "The fork Git service is not configured on this runner.");
            if (request.Git == null) throw Failure("workspace_git_request_missing", "The structured Git operation is missing.");
            bool retainedRepositoryOperation = AllowsRetainedRepositoryOperation(request)
                && String.Equals(intent.State, "Removed", StringComparison.Ordinal);
            if (retainedRepositoryOperation) await VerifyRepositoryAsync(grant, token).ConfigureAwait(false);
            string checkout = retainedRepositoryOperation ? Path.GetFullPath(grant.RepositoryPath)
                : CheckoutPath(grant, intent.Binding, requireExists: true);
            if (request.Git.UseRepository && request.Git.UseWorkingDirectory)
                throw Failure("workspace_git_target_invalid", "A Git request selects one configured target.");
            string expectedHeadTarget = request.Git.UseRepository
                ? Path.GetFullPath(grant.RepositoryPath)
                : checkout;
            if (request.Git.UseWorkingDirectory)
            {
                if (String.IsNullOrWhiteSpace(grant.WorkingDirectory))
                    throw Failure("workspace_primary_working_directory_unavailable", "No configured landing working directory is available.");
                checkout = Path.GetFullPath(grant.WorkingDirectory);
                EnsureNoLinks(checkout, requireExists: true);
                await VerifyPrimaryWorkingDirectoryAsync(grant, checkout, token).ConfigureAwait(false);
                expectedHeadTarget = checkout;
            }
            string? targetCheckout = null;
            if (!String.IsNullOrWhiteSpace(request.Git.TargetCheckoutId))
            {
                HarborCheckoutIntent targetIntent = _Journal.Read(request.Git.TargetCheckoutId);
                if (!SameBinding(intent.Binding, targetIntent.Binding) || !String.Equals(targetIntent.State, "Ready", StringComparison.Ordinal)
                    || !String.Equals(targetIntent.DockId, intent.DockId, StringComparison.Ordinal))
                    throw Failure("workspace_git_target_mismatch", "The target checkout is not owned by the same dock and grant.");
                targetCheckout = CheckoutPath(grant, targetIntent.Binding, requireExists: true);
            }
            if (!retainedRepositoryOperation && HarborWorkspaceOperationRules.IsGitMutation(request.Git.Operation))
                await CheckExpectedHeadAtAsync(request.ExpectedHead, expectedHeadTarget, token).ConfigureAwait(false);
            HarborGitOperationResult gitResult = await HarborGitDispatcher.ExecuteAsync(
                _Git, checkout, Path.GetFullPath(grant.RepositoryPath), targetCheckout, request.Git, token).ConfigureAwait(false);
            result.Git = gitResult;
            result.Binding = intent.Binding;
            result.HeadCommit = await ReadHeadAsync(checkout, token).ConfigureAwait(false);
            result.BranchName = await ReadBranchAsync(checkout, token).ConfigureAwait(false);
            result.CanonicalWorkingDirectory = checkout;
            result.Accepted = true;
        }

        private async Task VerifyPrimaryWorkingDirectoryAsync(HarborRepositoryGrant grant, string workingDirectory, CancellationToken token)
        {
            HarborWorkspaceProcessResult top = await RunGitAsync(workingDirectory,
                new[] { "rev-parse", "--show-toplevel" }, TimeSpan.FromSeconds(10), 4096, token).ConfigureAwait(false);
            RequireSuccess(top, "workspace_primary_working_directory_invalid");
            if (!String.Equals(Path.GetFullPath(top.StandardOutput.Trim()), workingDirectory, PathComparison))
                throw Failure("workspace_primary_working_directory_invalid", "The configured landing path is not a canonical worktree root.");
            HarborWorkspaceProcessResult remote = await RunGitAsync(workingDirectory,
                new[] { "config", "--get", "remote.origin.url" }, TimeSpan.FromSeconds(10), 8192, token).ConfigureAwait(false);
            RequireSuccess(remote, "workspace_primary_remote_unavailable");
            if (!String.Equals(HarborRunnerWorkspace.RepositoryFingerprint(remote.StandardOutput.Trim()),
                HarborRunnerWorkspace.RepositoryFingerprint(grant.RepositoryUrl), StringComparison.Ordinal))
                throw Failure("workspace_primary_repository_mismatch", "The landing worktree origin does not match the configured repository grant.");
        }

        private async Task CheckExpectedHeadAtAsync(string? expectedHead, string workingDirectory, CancellationToken token)
        {
            if (String.IsNullOrWhiteSpace(expectedHead) || !IsFullCommit(expectedHead))
                throw Failure("workspace_expected_head_required", "The operation requires a full expected target commit.");
            string current = await ReadHeadAsync(workingDirectory, token).ConfigureAwait(false);
            if (!String.Equals(current, expectedHead, StringComparison.OrdinalIgnoreCase))
                throw Failure("workspace_checkout_head_conflict", "The selected target HEAD changed since the operation was prepared.");
        }
    }
}
