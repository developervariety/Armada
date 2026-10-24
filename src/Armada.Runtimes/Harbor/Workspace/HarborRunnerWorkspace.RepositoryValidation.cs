namespace Armada.Runtimes.Harbor
{
    using System;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Harbor;

    /// <summary>Validates repository identity and pinned commit references.</summary>
    public sealed partial class HarborRunnerWorkspace
    {
        private async Task VerifyRepositoryAsync(HarborRepositoryGrant grant, CancellationToken token)
        {
            grant.Validate();
            EnsureNoLinks(Path.GetFullPath(grant.RepositoryPath), requireExists: true);
            HarborWorkspaceProcessResult bare = await RunGitAsync(grant.RepositoryPath,
                new[] { "rev-parse", "--is-bare-repository" }, TimeSpan.FromSeconds(10), 4096, token).ConfigureAwait(false);
            RequireSuccess(bare, "workspace_repository_invalid");
            if (!String.Equals(bare.StandardOutput.Trim(), "true", StringComparison.Ordinal))
                throw Failure("workspace_repository_not_bare", "The configured repository grant is not a bare repository.");
            HarborWorkspaceProcessResult remote = await RunGitAsync(grant.RepositoryPath,
                new[] { "config", "--get", "remote.origin.url" }, TimeSpan.FromSeconds(10), 8192, token).ConfigureAwait(false);
            RequireSuccess(remote, "workspace_repository_remote_unavailable");
            if (!String.Equals(RepositoryFingerprint(remote.StandardOutput.Trim()), RepositoryFingerprint(grant.RepositoryUrl), StringComparison.Ordinal))
                throw Failure("workspace_repository_fingerprint_mismatch", "The configured repository identity does not match the local bare repository.");
        }

        private async Task VerifyCommitAsync(string repositoryPath, string commit, CancellationToken token)
        {
            HarborWorkspaceProcessResult verify = await RunGitAsync(repositoryPath,
                new[] { "rev-parse", "--verify", commit + "^{commit}" }, TimeSpan.FromSeconds(10), 4096, token).ConfigureAwait(false);
            RequireSuccess(verify, "workspace_base_commit_unavailable");
            if (!String.Equals(verify.StandardOutput.Trim(), commit, StringComparison.OrdinalIgnoreCase))
                throw Failure("workspace_base_commit_invalid", "The configured base commit is not a full commit identity.");
        }

        private static string ValidateRequestedRevision(string revision)
        {
            if (revision.Length > 1024 || revision[0] == '-' || revision.Contains("..", StringComparison.Ordinal)
                || revision.Contains("@{", StringComparison.Ordinal) || revision.Contains('\\', StringComparison.Ordinal))
                throw Failure("workspace_revision_invalid", "The requested revision is not a safe commit or ref name.");
            if (IsFullCommit(revision)) return revision;
            foreach (char character in revision)
                if (Char.IsControl(character) || Char.IsWhiteSpace(character))
                    throw Failure("workspace_revision_invalid", "The requested revision is not a safe commit or ref name.");
            return revision;
        }

        private static void RequireSuccess(HarborWorkspaceProcessResult result, string reason)
        {
            if (result.ExitCode != 0 || result.Truncated)
                throw Failure(reason, "The bounded Git operation did not complete successfully.");
        }
    }
}
