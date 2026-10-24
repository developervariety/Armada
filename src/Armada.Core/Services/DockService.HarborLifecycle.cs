namespace Armada.Core.Services
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>Resumes durable remote cleanup phases for Harbor-owned docks.</summary>
    public partial class DockService
    {
        private async Task<bool> ReclaimHarborDockAsync(Dock dock, CancellationToken token)
        {
            if (!await RunHarborLifecycleAsync(dock, HarborCheckoutLifecyclePurposeEnum.Reclaim, token).ConfigureAwait(false))
                return false;
            Dock current = await ReadCurrentHarborDockAsync(dock, token).ConfigureAwait(false);
            current.Active = false;
            current.CaptainId = null;
            current.LastUpdateUtc = DateTime.UtcNow;
            await _Database.Docks.UpdateAsync(current, token).ConfigureAwait(false);
            ArmadaMetrics.RecordDockReclaimed();
            return true;
        }

        private async Task<bool> CleanupHarborCheckoutAsync(Dock dock, string operation, CancellationToken token)
        {
            HarborCheckoutLifecyclePurposeEnum purpose = String.Equals(operation, "delete", StringComparison.Ordinal)
                ? HarborCheckoutLifecyclePurposeEnum.Delete
                : String.Equals(operation, "purge", StringComparison.Ordinal)
                    ? HarborCheckoutLifecyclePurposeEnum.Purge
                    : HarborCheckoutLifecyclePurposeEnum.Reclaim;
            return await RunHarborLifecycleAsync(dock, purpose, token).ConfigureAwait(false);
        }

        private async Task<bool> RunHarborLifecycleAsync(Dock dock, HarborCheckoutLifecyclePurposeEnum purpose, CancellationToken token)
        {
            await RequireHarborStopsConfirmedAsync(dock, token).ConfigureAwait(false);
            HarborCheckoutLifecycleIntent intent = await GetOrCreateHarborLifecycleIntentAsync(dock, purpose, token).ConfigureAwait(false);
            while (true)
            {
                HarborCheckoutLifecycleIntent? completed = await RunHarborLifecycleIntentAsync(dock, intent, token).ConfigureAwait(false);
                if (completed == null) return false;
                intent = completed;
                if (intent.Purpose != HarborCheckoutLifecyclePurposeEnum.Reclaim
                    || purpose == HarborCheckoutLifecyclePurposeEnum.Reclaim)
                    return intent.RemovalConfirmed;

                // A caller can arrive while an earlier reclaim is incomplete. Finish that exact
                // journal first, then persist the requested delete/purge phase before removing
                // the database row. The transition uses the reclaim's anchored head and CAS.
                intent = await CreatePostRemovalBranchCleanupIntentAsync(dock, intent, purpose, token).ConfigureAwait(false);
            }
        }

        private async Task<HarborCheckoutLifecycleIntent?> RunHarborLifecycleIntentAsync(Dock dock,
            HarborCheckoutLifecycleIntent intent, CancellationToken token)
        {
            while (intent.Phase != HarborCheckoutLifecyclePhaseEnum.Terminal)
            {
                if (intent.Phase == HarborCheckoutLifecyclePhaseEnum.Prepared)
                {
                    DockWorkSnapshot? snapshot = intent.PreserveSnapshot
                        ? await SnapshotHarborWorkAsync(dock, intent, token).ConfigureAwait(false)
                        : DockWorkSnapshot.NoChanges();
                    if (snapshot == null) return null;
                    intent = await AdvanceHarborLifecycleAsync(dock, intent, HarborCheckoutLifecyclePhaseEnum.SnapshotComplete,
                        next =>
                        {
                            next.SnapshotCommitSha = snapshot.CommitSha;
                            next.SnapshotRefName = snapshot.RefName;
                            next.ExpectedHead = snapshot.CommitSha ?? intent.ExpectedHead;
                        }, token).ConfigureAwait(false);
                }
                else if (intent.Phase == HarborCheckoutLifecyclePhaseEnum.SnapshotComplete)
                {
                    await PreserveHarborLifecycleRefsAsync(dock, intent, token).ConfigureAwait(false);
                    intent = await AdvanceHarborLifecycleAsync(dock, intent, HarborCheckoutLifecyclePhaseEnum.ReferencesComplete,
                        next =>
                        {
                            next.AnchoredHead = intent.ExpectedHead;
                            next.DockAnchorRef = "refs/armada/docks/" + intent.DockId;
                            next.MissionAnchorRef = String.IsNullOrWhiteSpace(intent.MissionId)
                                ? null
                                : "refs/armada/missions/" + intent.MissionId;
                            next.BranchAnchorRef = String.IsNullOrWhiteSpace(intent.BranchName)
                                ? null
                                : "refs/armada-preserved/" + intent.BranchName;
                        }, token).ConfigureAwait(false);
                }
                else if (intent.Phase == HarborCheckoutLifecyclePhaseEnum.ReferencesComplete)
                {
                    await CleanupHarborLifecycleBranchAsync(dock, intent, token).ConfigureAwait(false);
                    intent = await AdvanceHarborLifecycleAsync(dock, intent, HarborCheckoutLifecyclePhaseEnum.BranchCleanupComplete,
                        null, token).ConfigureAwait(false);
                }
                else if (intent.Phase == HarborCheckoutLifecyclePhaseEnum.BranchCleanupComplete)
                {
                    if (!intent.CheckoutAlreadyRemoved)
                        await RemoveHarborLifecycleCheckoutAsync(dock, intent, token).ConfigureAwait(false);
                    intent = await AdvanceHarborLifecycleAsync(dock, intent, HarborCheckoutLifecyclePhaseEnum.CheckoutRemoved,
                        next => next.RemovalConfirmed = true, token).ConfigureAwait(false);
                }
                else if (intent.Phase == HarborCheckoutLifecyclePhaseEnum.CheckoutRemoved && intent.RemovalConfirmed)
                {
                    intent = await AdvanceHarborLifecycleAsync(dock, intent, HarborCheckoutLifecyclePhaseEnum.Terminal,
                        null, token).ConfigureAwait(false);
                }
                else
                {
                    throw new InvalidOperationException("dock_harbor_lifecycle_phase_invalid");
                }
            }
            return intent.RemovalConfirmed ? intent : null;
        }
    }
}
