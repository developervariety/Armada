namespace Armada.Runtimes.Harbor.Workspace.Git
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Harbor;
    using Armada.Core.Services.Interfaces;

    /// <summary>Dispatches fixed, typed Harbor Git operations to the configured Git service.</summary>
    public static partial class HarborGitDispatcher
    {
        /// <summary>Execute one fixed operation against caller-resolved and grant-bound paths.</summary>
        /// <param name="git">Git service used to perform the operation.</param>
        /// <param name="checkoutPath">Canonical path for the owned checkout.</param>
        /// <param name="repositoryPath">Canonical path for the granted repository.</param>
        /// <param name="targetCheckoutPath">Canonical path for another checkout under the same grant, when required.</param>
        /// <param name="request">Bounded operation and structured inputs.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The typed result of the selected operation.</returns>
        public static async Task<HarborGitOperationResult> ExecuteAsync(
            IGitService git,
            string checkoutPath,
            string repositoryPath,
            string? targetCheckoutPath,
            HarborGitOperationRequest request,
            CancellationToken token = default)
        {
            if (git == null) throw new ArgumentNullException(nameof(git));
            if (request == null) throw new ArgumentNullException(nameof(request));
            RequireCanonicalPath(checkoutPath, "checkout");
            RequireCanonicalPath(repositoryPath, "repository");
            if (targetCheckoutPath != null) RequireCanonicalPath(targetCheckoutPath, "target checkout");
            if (!Enum.IsDefined(request.Operation)) throw Invalid("operation_unknown");
            ValidateRepositoryChoice(request);
            ValidateBounds(request);

            if (request.Operation is HarborGitOperationEnum.CloneBare
                or HarborGitOperationEnum.CreateWorktree
                or HarborGitOperationEnum.CreateDetachedWorktreeAtCommit
                or HarborGitOperationEnum.RemoveWorktree)
                throw Unsupported("workspace_journal_required");

            HarborGitOperationResult? result = await ExecuteWriteAsync(
                git, checkoutPath, repositoryPath, targetCheckoutPath, request, token).ConfigureAwait(false);
            if (result != null) return result;

            result = await ExecuteReadAsync(git, checkoutPath, repositoryPath, targetCheckoutPath, request, token).ConfigureAwait(false);
            if (result != null) return result;

            result = await ExecuteInventoryAsync(git, checkoutPath, repositoryPath, request, token).ConfigureAwait(false);
            if (result != null) return result;
            throw Unsupported("operation_not_implemented");
        }

        private static string SelectPath(HarborGitOperationRequest request, string checkoutPath, string repositoryPath, bool repositoryAllowed)
        {
            if (request.UseRepository)
            {
                if (!repositoryAllowed) throw Invalid("repository_path_not_allowed_for_operation");
                return repositoryPath;
            }
            return checkoutPath;
        }

    }
}
