namespace Armada.Core.Services
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Harbor;
    using Armada.Core.Models;

    /// <summary>Executes one replayable Harbor cleanup step under its durable intent identity.</summary>
    public partial class DockService
    {
        private async Task PreserveHarborLifecycleRefsAsync(Dock dock, HarborCheckoutLifecycleIntent intent, CancellationToken token)
        {
            if (!intent.PreserveReferences) return;
            await CopyHarborLifecycleRefAsync(dock, intent, "refs/armada/docks/" + intent.DockId, token).ConfigureAwait(false);
            string? missionId = intent.MissionId;
            if (!String.IsNullOrWhiteSpace(missionId))
                await CopyHarborLifecycleRefAsync(dock, intent, "refs/armada/missions/" + missionId, token).ConfigureAwait(false);
            if (intent.PreserveBranch && !String.IsNullOrWhiteSpace(intent.BranchName))
                await CopyHarborLifecycleRefAsync(dock, intent, "refs/armada-preserved/" + intent.BranchName, token).ConfigureAwait(false);
        }

        private async Task CopyHarborLifecycleRefAsync(Dock dock, HarborCheckoutLifecycleIntent intent,
            string destinationRef, CancellationToken token)
        {
            HarborWorkspaceOperationRequest request = HarborCheckoutService.CreateRequest(dock, HarborWorkspaceOperationEnum.Git);
            request.Git = new HarborGitOperationRequest
            {
                Operation = HarborGitOperationEnum.CopyRef,
                SourceRef = "HEAD",
                DestinationRef = destinationRef
            };
            using (HarborCheckoutOperationScope scope = new HarborCheckoutOperationScope(
                "dock:" + intent.DockId + ":lifecycle:" + intent.IntentId + ":anchor:" + destinationRef, intent.ExpectedHead))
                await RequireHarborCheckouts().ExecuteAsync(dock, request, token).ConfigureAwait(false);
        }

        private async Task CleanupHarborLifecycleBranchAsync(Dock dock, HarborCheckoutLifecycleIntent intent, CancellationToken token)
        {
            if (String.IsNullOrWhiteSpace(intent.BranchName)) return;
            if (intent.DeleteRemoteBranch)
            {
                HarborWorkspaceOperationRequest remote = HarborCheckoutService.CreateRequest(dock, HarborWorkspaceOperationEnum.Git);
                remote.Git = intent.CheckoutAlreadyRemoved
                    ? new HarborGitOperationRequest
                    {
                        Operation = HarborGitOperationEnum.DeleteRemoteRefIfAt,
                        DestinationRef = "refs/heads/" + intent.BranchName,
                        ExpectedOldSha = intent.AnchoredHead,
                        RemoteName = "origin",
                        UseRepository = true
                    }
                    : new HarborGitOperationRequest
                {
                    Operation = HarborGitOperationEnum.DeleteRemoteBranch,
                    BranchName = intent.BranchName,
                    UseWorkingDirectory = true
                };
                string remoteHead = intent.CheckoutAlreadyRemoved
                    ? intent.ExpectedHead
                    : await ReadHarborTargetHeadAsync(HarborCheckoutAddress.ForDock(dock.Id) + "/working", token).ConfigureAwait(false);
                using (HarborCheckoutOperationScope scope = new HarborCheckoutOperationScope(
                    "dock:" + intent.DockId + ":lifecycle:" + intent.IntentId + ":delete-remote-branch", remoteHead))
                    await RequireHarborCheckouts().ExecuteAsync(dock, remote, token).ConfigureAwait(false);
            }
            if (!intent.DeleteLocalBranch) return;
            HarborWorkspaceOperationRequest local = HarborCheckoutService.CreateRequest(dock, HarborWorkspaceOperationEnum.Git);
            local.Git = intent.CheckoutAlreadyRemoved
                ? new HarborGitOperationRequest
                {
                    Operation = HarborGitOperationEnum.DeleteRefIfAt,
                    DestinationRef = "refs/heads/" + intent.BranchName,
                    ExpectedOldSha = intent.AnchoredHead,
                    UseRepository = true
                }
                : new HarborGitOperationRequest
            {
                Operation = HarborGitOperationEnum.DeleteLocalBranch,
                BranchName = intent.BranchName,
                UseRepository = true
            };
            string localHead = intent.CheckoutAlreadyRemoved
                ? intent.ExpectedHead
                : await ReadHarborTargetHeadAsync(HarborCheckoutAddress.ForDock(dock.Id) + "/repository", token).ConfigureAwait(false);
            using (HarborCheckoutOperationScope scope = new HarborCheckoutOperationScope(
                "dock:" + intent.DockId + ":lifecycle:" + intent.IntentId + ":delete-local-branch", localHead))
                await RequireHarborCheckouts().ExecuteAsync(dock, local, token).ConfigureAwait(false);
        }

        private async Task<string> ReadHarborTargetHeadAsync(string target, CancellationToken token)
        {
            string? head = await (_Git ?? throw new InvalidOperationException("harbor_git_provider_unavailable"))
                .GetHeadCommitHashAsync(target, token).ConfigureAwait(false);
            if (String.IsNullOrWhiteSpace(head)) throw new InvalidOperationException("harbor_target_head_unavailable");
            return head;
        }

        private async Task RemoveHarborLifecycleCheckoutAsync(Dock dock, HarborCheckoutLifecycleIntent intent, CancellationToken token)
        {
            HarborWorkspaceOperationRequest remove = HarborCheckoutService.CreateRequest(dock, HarborWorkspaceOperationEnum.RemoveCheckout);
            using (HarborCheckoutOperationScope scope = new HarborCheckoutOperationScope(
                "dock:" + intent.DockId + ":lifecycle:" + intent.IntentId + ":remove", intent.ExpectedHead))
                await RequireHarborCheckouts().ExecuteAsync(dock, remove, token).ConfigureAwait(false);
        }

    }
}
