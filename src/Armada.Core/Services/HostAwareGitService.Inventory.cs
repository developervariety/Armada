namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Harbor;
    using Armada.Core.Models;

    /// <summary>Routes read-only branch inventory operations.</summary>
    public sealed partial class HostAwareGitService
    {
        /// <inheritdoc />
        public Task<IReadOnlyList<BranchInfo>> ListBranchesAsync(string repoPath, string defaultBranch = "main", CancellationToken token = default)
        {
            if (!IsRemote(repoPath)) return LocalInventory().ListBranchesAsync(repoPath, defaultBranch, token);
            HarborGitOperationRequest request = NewRequest(HarborGitOperationEnum.ListBranches, repoPath);
            request.BaseBranch = defaultBranch;
            return SendValueAsync<IReadOnlyList<BranchInfo>>(repoPath, request, token, result => result.Branches);
        }

        /// <inheritdoc />
        public Task<RepositoryHeadInspection> InspectRepositoryHeadAsync(string repoPath, CancellationToken token = default)
        {
            if (!IsRemote(repoPath)) return LocalInventory().InspectRepositoryHeadAsync(repoPath, token);
            return SendValueAsync(repoPath, NewRequest(HarborGitOperationEnum.InspectRepositoryHead, repoPath), token,
                result => result.RepositoryHead ?? throw MissingResult("repository_head"));
        }

        /// <inheritdoc />
        public Task<bool> IsBareRepositoryAsync(string repoPath, CancellationToken token = default)
        {
            if (!IsRemote(repoPath)) return LocalInventory().IsBareRepositoryAsync(repoPath, token);
            return SendValueAsync(repoPath, NewRequest(HarborGitOperationEnum.IsBareRepository, repoPath), token,
                result => result.Flag ?? throw MissingResult("flag"));
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<string>> EnumerateLocalBranchesAsync(string repoPath, string? branchPrefix = null, CancellationToken token = default)
        {
            if (!IsRemote(repoPath)) return LocalInventory().EnumerateLocalBranchesAsync(repoPath, branchPrefix, token);
            HarborGitOperationRequest request = NewRequest(HarborGitOperationEnum.EnumerateLocalBranches, repoPath);
            request.RefPrefix = branchPrefix;
            return SendValueAsync<IReadOnlyList<string>>(repoPath, request, token, result => result.Paths);
        }

        /// <inheritdoc />
        public Task<bool> IsAncestorAsync(string repoPath, string ancestorRef, string descendantRef, CancellationToken token = default)
        {
            if (!IsRemote(repoPath)) return LocalInventory().IsAncestorAsync(repoPath, ancestorRef, descendantRef, token);
            HarborGitOperationRequest request = NewRequest(HarborGitOperationEnum.IsAncestor, repoPath);
            request.AncestorRef = ancestorRef;
            request.DescendantRef = descendantRef;
            return SendValueAsync(repoPath, request, token, result => result.Flag ?? throw MissingResult("flag"));
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<GitRefTip>> EnumerateRefTipsAsync(string repoPath, string refPrefix, CancellationToken token = default)
        {
            if (!IsRemote(repoPath)) return LocalInventory().EnumerateRefTipsAsync(repoPath, refPrefix, token);
            HarborGitOperationRequest request = NewRequest(HarborGitOperationEnum.EnumerateRefTips, repoPath);
            request.RefPrefix = refPrefix;
            return SendValueAsync<IReadOnlyList<GitRefTip>>(repoPath, request, token, result => result.RefTips);
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<GitRefTip>> EnumerateRemoteRefTipsAsync(string repoPath, string remoteName = "origin", CancellationToken token = default)
        {
            if (!IsRemote(repoPath)) return LocalInventory().EnumerateRemoteRefTipsAsync(repoPath, remoteName, token);
            HarborGitOperationRequest request = NewRequest(HarborGitOperationEnum.EnumerateRemoteRefTips, repoPath);
            request.RemoteName = remoteName;
            return SendValueAsync<IReadOnlyList<GitRefTip>>(repoPath, request, token, result => result.RefTips);
        }

        /// <inheritdoc />
        public Task<DateTime?> TryGetCommitTimeUtcAsync(string repoPath, string commitSha, CancellationToken token = default)
        {
            if (!IsRemote(repoPath)) return LocalInventory().TryGetCommitTimeUtcAsync(repoPath, commitSha, token);
            HarborGitOperationRequest request = NewRequest(HarborGitOperationEnum.TryGetCommitTimeUtc, repoPath);
            request.CommitSha = commitSha;
            return SendValueAsync(repoPath, request, token, result => result.TimeUtc);
        }

        /// <inheritdoc />
        public Task DeleteRefIfAtAsync(string repoPath, string refName, string expectedSha, CancellationToken token = default, [System.Runtime.CompilerServices.CallerMemberName] string caller = "")
        {
            if (!IsRemote(repoPath)) return LocalInventory().DeleteRefIfAtAsync(repoPath, refName, expectedSha, token, caller);
            HarborGitOperationRequest request = NewRequest(HarborGitOperationEnum.DeleteRefIfAt, repoPath);
            request.DestinationRef = refName;
            request.ExpectedOldSha = expectedSha;
            request.Reason = "DeleteRefIfAt";
            return SendVoidAsync(repoPath, request, token);
        }

        /// <inheritdoc />
        public Task DeleteRemoteRefIfAtAsync(string repoPath, string remoteName, string refName, string expectedSha, CancellationToken token = default, [System.Runtime.CompilerServices.CallerMemberName] string caller = "")
        {
            if (!IsRemote(repoPath)) return LocalInventory().DeleteRemoteRefIfAtAsync(repoPath, remoteName, refName, expectedSha, token, caller);
            HarborGitOperationRequest request = NewRequest(HarborGitOperationEnum.DeleteRemoteRefIfAt, repoPath);
            request.RemoteName = remoteName;
            request.DestinationRef = refName;
            request.ExpectedOldSha = expectedSha;
            request.Reason = "DeleteRemoteRefIfAt";
            return SendVoidAsync(repoPath, request, token);
        }
    }
}
