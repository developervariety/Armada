namespace Armada.Core.Services
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Harbor;
    using Armada.Core.Models;

    /// <summary>Captures durable recovery output before a Harbor checkout is removed.</summary>
    public partial class DockService
    {
        private async Task<DockWorkSnapshot?> SnapshotHarborWorkAsync(Dock dock,
            HarborCheckoutLifecycleIntent intent, CancellationToken token)
        {
            HarborWorkspaceOperationRequest request = HarborCheckoutService.CreateRequest(dock, HarborWorkspaceOperationEnum.Git);
            request.Git = new HarborGitOperationRequest
            {
                Operation = HarborGitOperationEnum.SnapshotDockWork,
                MissionId = intent.MissionId ?? intent.DockId,
                CommitMessage = "Armada snapshot of dock " + dock.Id + " before " + intent.Purpose.ToString().ToLowerInvariant()
            };
            HarborGitOperationResult git;
            using (HarborCheckoutOperationScope scope = new HarborCheckoutOperationScope(
                "dock:" + intent.DockId + ":lifecycle:" + intent.IntentId + ":snapshot", intent.InitialHead))
            {
                HarborWorkspaceOperationResult result = await RequireHarborCheckouts().ExecuteAsync(dock, request, token).ConfigureAwait(false);
                git = result.Git ?? throw new InvalidOperationException("dock_harbor_snapshot_result_missing");
            }
            DockWorkSnapshot snapshot = git.Snapshot ?? throw new InvalidOperationException("dock_harbor_snapshot_result_missing");
            if (snapshot.Status == DockWorkSnapshotStatusEnum.NoChanges) return snapshot;
            if (!snapshot.HasRef)
            {
                string reason = "Uncommitted work for Harbor dock " + dock.Id + " was not saved before cleanup.";
                await RecordHarborSnapshotEventAsync(dock, "mission.work_snapshot_failed", reason, null, null, token).ConfigureAwait(false);
                return null;
            }
            string message = "Saved uncommitted work for Harbor dock " + dock.Id + " as " + snapshot.RefName
                + " (" + snapshot.CommitSha + ") before cleanup.";
            await RecordHarborSnapshotEventAsync(dock, "mission.work_snapshot_saved", message,
                snapshot.RefName, snapshot.CommitSha, token).ConfigureAwait(false);
            return snapshot;
        }

        private async Task RecordHarborSnapshotEventAsync(Dock dock, string eventType, string message,
            string? refName, string? sha, CancellationToken token)
        {
            await RecordSnapshotEventAsync(new UncommittedWorkOwner
            {
                DockId = dock.Id,
                MissionId = dock.HostCheckoutLifecycle?.MissionId,
                TenantId = dock.TenantId,
                UserId = dock.UserId,
                VesselId = dock.VesselId,
                CaptainId = dock.CaptainId
            }, dock.WorktreePath ?? dock.Id, eventType, message, refName, sha, token).ConfigureAwait(false);
        }
    }
}
