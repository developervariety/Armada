namespace Armada.Runtimes.Harbor.Workspace.Git
{
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Harbor;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;

    public static partial class HarborGitDispatcher
    {
        private static async Task<HarborGitOperationResult?> ExecuteWriteAsync(
            IGitService git, string checkoutPath, string repositoryPath, string? targetCheckoutPath,
            HarborGitOperationRequest request, CancellationToken token)
        {
            string path;
            string selectedPath = SelectPath(request, checkoutPath, repositoryPath, true);
            switch (request.Operation)
            {
                case HarborGitOperationEnum.LockWorktree:
                    path = targetCheckoutPath ?? checkoutPath;
                    return await EmptyAsync(git.LockWorktreeAsync(path, Required(request.Reason, "reason", 512), token)).ConfigureAwait(false);
                case HarborGitOperationEnum.UnlockWorktree:
                    path = targetCheckoutPath ?? checkoutPath;
                    return await EmptyAsync(git.UnlockWorktreeAsync(path, token)).ConfigureAwait(false);
                case HarborGitOperationEnum.Fetch:
                    return await EmptyAsync(git.FetchAsync(SelectPath(request, checkoutPath, repositoryPath, true), token)).ConfigureAwait(false);
                case HarborGitOperationEnum.PushBranch:
                    return await EmptyAsync(git.PushBranchAsync(SelectPath(request, checkoutPath, repositoryPath, true), Remote(request), token)).ConfigureAwait(false);
                case HarborGitOperationEnum.CreatePullRequest:
                    if (String.IsNullOrWhiteSpace(request.BaseBranch))
                        return await TextAsync(git.CreatePullRequestAsync(selectedPath, Required(request.Title, "title", 300), Required(request.Body, "body", 20000), token)).ConfigureAwait(false);
                    if (String.IsNullOrWhiteSpace(request.BranchName))
                        return await TextAsync(git.CreatePullRequestAsync(selectedPath, Required(request.Title, "title", 300), Required(request.Body, "body", 20000), RequiredBranch(request.BaseBranch), token)).ConfigureAwait(false);
                    return await TextAsync(git.CreatePullRequestAsync(selectedPath, Required(request.Title, "title", 300), Required(request.Body, "body", 20000),
                        RequiredBranch(request.BranchName), RequiredBranch(request.BaseBranch), token)).ConfigureAwait(false);
                case HarborGitOperationEnum.RepairWorktree:
                    return await EmptyAsync(git.RepairWorktreeAsync(selectedPath, token)).ConfigureAwait(false);
                case HarborGitOperationEnum.DeleteLocalBranch:
                    return await EmptyAsync(git.DeleteLocalBranchAsync(SelectPath(request, checkoutPath, repositoryPath, true), RequiredBranch(request.BranchName), token)).ConfigureAwait(false);
                case HarborGitOperationEnum.DeleteRemoteBranch:
                    return await EmptyAsync(git.DeleteRemoteBranchAsync(SelectPath(request, checkoutPath, repositoryPath, true), RequiredBranch(request.BranchName), token)).ConfigureAwait(false);
                case HarborGitOperationEnum.PushRefSpec:
                    return await EmptyAsync(git.PushRefSpecAsync(SelectPath(request, checkoutPath, repositoryPath, true),
                        RequiredPushSource(request.SourceRef), RequiredRef(request.DestinationRef), token)).ConfigureAwait(false);
                case HarborGitOperationEnum.ForceUpdateBranchRef:
                    return await EmptyAsync(git.ForceUpdateBranchRefAsync(SelectPath(request, checkoutPath, repositoryPath, true), RequiredBranch(request.BranchName), RequiredCommit(request.CommitSha), token)).ConfigureAwait(false);
                case HarborGitOperationEnum.CompareAndSwapBranchRef:
                    return await EmptyAsync(git.CompareAndSwapBranchRefAsync(SelectPath(request, checkoutPath, repositoryPath, true), RequiredBranch(request.BranchName),
                        RequiredCommit(request.CommitSha), RequiredCommit(request.ExpectedOldSha, "expected_old_commit"), token)).ConfigureAwait(false);
                case HarborGitOperationEnum.PushHeadToBranch:
                    return await EmptyAsync(git.PushHeadToBranchAsync(selectedPath, Remote(request), RequiredBranch(request.BranchName), token)).ConfigureAwait(false);
                case HarborGitOperationEnum.CopyRef:
                    return await EmptyAsync(git.CopyRefAsync(SelectPath(request, checkoutPath, repositoryPath, true),
                        RequiredPushSource(request.SourceRef), RequiredRef(request.DestinationRef), token)).ConfigureAwait(false);
                case HarborGitOperationEnum.SetRepositoryHead:
                    return await EmptyAsync(git.SetRepositoryHeadAsync(SelectPath(request, checkoutPath, repositoryPath, true), RequiredBranch(request.BranchName), token)).ConfigureAwait(false);
                case HarborGitOperationEnum.PruneWorktrees:
                    return await EmptyAsync(git.PruneWorktreesAsync(SelectPath(request, checkoutPath, repositoryPath, true), token)).ConfigureAwait(false);
                case HarborGitOperationEnum.EnableAutoMerge:
                    return await EmptyAsync(git.EnableAutoMergeAsync(selectedPath, RequiredHttpUrl(request.PullRequestUrl), token)).ConfigureAwait(false);
                case HarborGitOperationEnum.MergeBranchLocal:
                    return await EmptyAsync(git.MergeBranchLocalAsync(checkoutPath, repositoryPath, RequiredBranch(request.BranchName),
                        OptionalBranch(request.TargetBranch), Optional(request.CommitMessage, "commit_message", 2000), token)).ConfigureAwait(false);
                case HarborGitOperationEnum.Pull:
                    return await EmptyAsync(git.PullAsync(selectedPath, token)).ConfigureAwait(false);
                case HarborGitOperationEnum.PullFastForwardOnly:
                    return await EmptyAsync(git.PullFastForwardOnlyAsync(selectedPath, token)).ConfigureAwait(false);
                case HarborGitOperationEnum.EnsureLocalBranch:
                    return await FlagAsync(git.EnsureLocalBranchAsync(SelectPath(request, checkoutPath, repositoryPath, true), RequiredBranch(request.BranchName), token)).ConfigureAwait(false);
                case HarborGitOperationEnum.PushHeadToRepositoryBranch:
                    return await EmptyAsync(git.PushHeadToRepositoryBranchAsync(checkoutPath, repositoryPath, RequiredBranch(request.BranchName), token)).ConfigureAwait(false);
                case HarborGitOperationEnum.SnapshotDockWork:
                    return await SnapshotAsync(git.SnapshotDockWorkAsync(selectedPath, Required(request.MissionId, "mission_id", 900),
                        Required(request.CommitMessage, "commit_message", 2000), token)).ConfigureAwait(false);
                case HarborGitOperationEnum.SetHeadSymbolicRef:
                    return await EmptyAsync(git.SetHeadSymbolicRefAsync(SelectPath(request, checkoutPath, repositoryPath, true), RequiredRef(request.TargetRef), token)).ConfigureAwait(false);
                default:
                    return null;
            }
        }

    }
}
