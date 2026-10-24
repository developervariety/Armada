namespace Armada.Runtimes.Harbor
{
    using System;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Harbor;
    using Armada.Core.Services;

    public sealed partial class HarborRunnerWorkspace
    {
        private async Task AddGitExcludeAsync(HarborWorkspaceOperationRequest request, HarborCheckoutIntent intent,
            HarborRepositoryGrant grant, HarborWorkspaceOperationResult result, CancellationToken token)
        {
            string checkout = CheckoutPath(grant, intent.Binding, true);
            await CheckExpectedHeadAsync(request, checkout, token).ConfigureAwait(false);
            string entry = request.RelativePath ?? String.Empty;
            string normalized = entry.TrimEnd('/');
            ResolveRelativePath(normalized, checkout, false);
            if (entry.IndexOfAny(new[] { '\r', '\n', '*', '?', '[', ']', '!' }) >= 0 || entry.Length > 1024)
                throw Failure("workspace_exclude_invalid", "The exclusion must name one contained artifact path.");
            string repositoryCommon = await ReadCommonDirectoryAsync(grant.RepositoryPath, token).ConfigureAwait(false);
            string checkoutCommon = await ReadCommonDirectoryAsync(checkout, token).ConfigureAwait(false);
            if (!String.Equals(repositoryCommon, checkoutCommon, OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw Failure("workspace_repository_identity_mismatch", "The checkout common directory does not match its repository grant.");
            string exclude = Path.Combine(repositoryCommon, "info", "exclude");
            EnsureNoLinks(exclude, false);
            await GitInfoExclude.EnsureEntryAsync(exclude, entry, token).ConfigureAwait(false);
            EnsureNoLinks(exclude, true);
            result.Binding = intent.Binding;
            result.HeadCommit = await ReadHeadAsync(checkout, token).ConfigureAwait(false);
            result.Accepted = true;
        }

        private async Task<string> ReadCommonDirectoryAsync(string repository, CancellationToken token)
        {
            HarborWorkspaceProcessResult read = await RunGitAsync(repository,
                new[] { "rev-parse", "--path-format=absolute", "--git-common-dir" }, TimeSpan.FromSeconds(10), 4096, token).ConfigureAwait(false);
            RequireSuccess(read, "workspace_repository_common_directory_unavailable");
            string common = Path.GetFullPath(read.StandardOutput.Trim());
            EnsureNoLinks(common, true);
            return common;
        }
    }
}
