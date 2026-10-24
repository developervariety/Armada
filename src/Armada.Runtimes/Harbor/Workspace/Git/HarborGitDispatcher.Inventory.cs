namespace Armada.Runtimes.Harbor.Workspace.Git
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Harbor;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;

    public static partial class HarborGitDispatcher
    {
        private static async Task<HarborGitOperationResult?> ExecuteInventoryAsync(
            IGitService git, string checkoutPath, string repositoryPath,
            HarborGitOperationRequest request, CancellationToken token)
        {
            IBranchInventory? inventory = git as IBranchInventory;
            string path = SelectPath(request, checkoutPath, repositoryPath, true);
            switch (request.Operation)
            {
                case HarborGitOperationEnum.ListBranches:
                    if (inventory == null) throw Unsupported("branch_inventory_unavailable");
                    return new HarborGitOperationResult { Branches = new List<BranchInfo>(await inventory.ListBranchesAsync(
                        path, String.IsNullOrWhiteSpace(request.BaseBranch) ? "main" : RequiredBranch(request.BaseBranch), token).ConfigureAwait(false)) };
                case HarborGitOperationEnum.InspectRepositoryHead:
                    if (inventory == null) throw Unsupported("branch_inventory_unavailable");
                    return new HarborGitOperationResult { RepositoryHead = await inventory.InspectRepositoryHeadAsync(path, token).ConfigureAwait(false) };
                case HarborGitOperationEnum.IsBareRepository:
                    if (inventory == null) throw Unsupported("branch_inventory_unavailable");
                    return await FlagAsync(inventory.IsBareRepositoryAsync(path, token)).ConfigureAwait(false);
                case HarborGitOperationEnum.EnumerateLocalBranches:
                    if (inventory == null) throw Unsupported("branch_inventory_unavailable");
                    return await PathsAsync(inventory.EnumerateLocalBranchesAsync(path, OptionalBranchPrefix(request.RefPrefix), token)).ConfigureAwait(false);
                case HarborGitOperationEnum.IsAncestor:
                    if (inventory == null) throw Unsupported("branch_inventory_unavailable");
                    return await FlagAsync(inventory.IsAncestorAsync(path, RequiredRevision(request.AncestorRef),
                        RequiredRevision(request.DescendantRef), token)).ConfigureAwait(false);
                case HarborGitOperationEnum.EnumerateRefTips:
                    if (inventory == null) throw Unsupported("branch_inventory_unavailable");
                    return new HarborGitOperationResult { RefTips = new List<GitRefTip>(await inventory.EnumerateRefTipsAsync(
                        path, RequiredRefPrefix(request.RefPrefix), token).ConfigureAwait(false)) };
                case HarborGitOperationEnum.EnumerateRemoteRefTips:
                    if (inventory == null) throw Unsupported("branch_inventory_unavailable");
                    return new HarborGitOperationResult { RefTips = new List<GitRefTip>(await inventory.EnumerateRemoteRefTipsAsync(
                        path, Remote(request), token).ConfigureAwait(false)) };
                case HarborGitOperationEnum.TryGetCommitTimeUtc:
                    if (inventory == null) throw Unsupported("branch_inventory_unavailable");
                    return await TimeAsync(inventory.TryGetCommitTimeUtcAsync(path, RequiredCommit(request.CommitSha), token)).ConfigureAwait(false);
                case HarborGitOperationEnum.DeleteRefIfAt:
                    if (inventory == null) throw Unsupported("branch_inventory_unavailable");
                    return await EmptyAsync(inventory.DeleteRefIfAtAsync(path, RequiredRef(request.DestinationRef),
                        RequiredCommit(request.ExpectedOldSha, "expected_old_commit"), token)).ConfigureAwait(false);
                case HarborGitOperationEnum.DeleteRemoteRefIfAt:
                    if (inventory == null) throw Unsupported("branch_inventory_unavailable");
                    return await EmptyAsync(inventory.DeleteRemoteRefIfAtAsync(path, Remote(request), RequiredRef(request.DestinationRef),
                        RequiredCommit(request.ExpectedOldSha, "expected_old_commit"), token)).ConfigureAwait(false);
                default:
                    return null;
            }
        }
    }
}
