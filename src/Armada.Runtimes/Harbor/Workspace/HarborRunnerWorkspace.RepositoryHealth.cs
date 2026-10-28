namespace Armada.Runtimes.Harbor
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Harbor;
    using Armada.Core.Models;
    using Armada.Core.Services;

    /// <summary>Reads repository health on this runner with read-only Git commands.</summary>
    public sealed partial class HarborRunnerWorkspace
    {
        private async Task ReadRepositoryHealthAsync(HarborWorkspaceOperationRequest request, HarborRepositoryGrant grant, HarborWorkspaceOperationResult result, CancellationToken token)
        {
            if (request.Binding != null) throw Failure("workspace_binding_unexpected", "Repository health does not accept a checkout binding.");
            await VerifyRepositoryAsync(grant, token).ConfigureAwait(false);
            string branch = String.IsNullOrWhiteSpace(request.RequestedRevision) ? "HEAD" : request.RequestedRevision.Trim();
            if (!String.IsNullOrWhiteSpace(request.ExpectedRepositoryFingerprint)
                && !String.Equals(request.ExpectedRepositoryFingerprint, RepositoryFingerprint(grant.RepositoryUrl), StringComparison.OrdinalIgnoreCase))
                throw Failure("workspace_repository_fingerprint_mismatch", "The requested repository fingerprint does not match the configured grant.");

            RepositoryHealthGitFacts facts = await RepositoryHealthGitProbe.ReadAsync(
                grant.RepositoryPath,
                grant.WorkingDirectory,
                branch,
                token).ConfigureAwait(false);
            result.Text = facts.Format();
            result.RepositoryFingerprint = RepositoryFingerprint(grant.RepositoryUrl);
            result.Accepted = true;
        }
    }
}
