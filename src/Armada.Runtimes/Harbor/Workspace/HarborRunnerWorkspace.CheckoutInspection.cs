namespace Armada.Runtimes.Harbor
{
    using System;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Harbor;
    using Armada.Core.Models;

    /// <summary>Inspects configured repositories and runner-owned checkouts.</summary>
    public sealed partial class HarborRunnerWorkspace
    {
        private async Task InspectRepositoryAsync(HarborWorkspaceOperationRequest request, HarborRepositoryGrant grant, HarborWorkspaceOperationResult result, CancellationToken token)
        {
            await VerifyRepositoryAsync(grant, token).ConfigureAwait(false);
            string revision = String.IsNullOrWhiteSpace(request.RequestedRevision) ? "HEAD" : ValidateRequestedRevision(request.RequestedRevision);
            if (!String.Equals(revision, "HEAD", StringComparison.Ordinal) && !IsFullCommit(revision))
            {
                HarborWorkspaceProcessResult refCheck = await RunGitAsync(grant.RepositoryPath,
                    new[] { "check-ref-format", "--branch", revision }, TimeSpan.FromSeconds(10), 4096, token).ConfigureAwait(false);
                RequireSuccess(refCheck, "workspace_revision_invalid");
            }
            HarborWorkspaceProcessResult head = await RunGitAsync(grant.RepositoryPath,
                new[] { "rev-parse", "--verify", revision + "^{commit}" }, TimeSpan.FromSeconds(10), 4096, token).ConfigureAwait(false);
            if (head.ExitCode != 0 || head.Truncated)
                throw Failure("workspace_revision_unavailable", "The requested repository revision is not available as a commit.");
            string commit = head.StandardOutput.Trim();
            if (!IsFullCommit(commit)) throw Failure("workspace_revision_invalid", "Git did not resolve a full repository commit.");
            if (!String.IsNullOrWhiteSpace(request.ExpectedRepositoryFingerprint)
                && !String.Equals(request.ExpectedRepositoryFingerprint, HarborRunnerWorkspace.RepositoryFingerprint(grant.RepositoryUrl), StringComparison.OrdinalIgnoreCase))
                throw Failure("workspace_repository_fingerprint_mismatch", "The requested repository fingerprint does not match the configured grant.");
            result.HeadCommit = commit;
            result.RepositoryFingerprint = HarborRunnerWorkspace.RepositoryFingerprint(grant.RepositoryUrl);
            result.CanonicalWorkingDirectory = Path.GetFullPath(grant.RepositoryPath);
            result.Accepted = true;
        }

        private async Task InspectCheckoutAsync(
            HarborCheckoutIntent intent,
            HarborRepositoryGrant grant,
            HarborWorkspaceOperationResult result,
            CancellationToken token)
        {
            string path = CheckoutPath(grant, intent.Binding, requireExists: true);
            string head = await ReadHeadAsync(path, token).ConfigureAwait(false);
            result.Binding = intent.Binding;
            result.HeadCommit = head;
            result.BranchName = await ReadBranchAsync(path, token).ConfigureAwait(false);
            result.CanonicalWorkingDirectory = path;
            result.Accepted = true;
        }
    }
}
