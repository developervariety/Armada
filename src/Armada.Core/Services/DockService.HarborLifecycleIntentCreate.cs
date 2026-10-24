namespace Armada.Core.Services
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>Creates exact first-time and post-removal cleanup intents.</summary>
    public partial class DockService
    {
        private async Task<HarborCheckoutLifecycleIntent> GetOrCreateHarborLifecycleIntentAsync(Dock dock,
            HarborCheckoutLifecyclePurposeEnum requestedPurpose, CancellationToken token)
        {
            Dock current = await ReadCurrentHarborDockAsync(dock, token).ConfigureAwait(false);
            HarborCheckoutBinding binding = RequireHarborCheckout(current);
            if (current.HostCheckoutLifecycle != null)
            {
                HarborCheckoutLifecycleIntent existing = current.HostCheckoutLifecycle;
                if (!String.Equals(existing.BindingRevision, binding.Revision, StringComparison.Ordinal))
                    throw new InvalidOperationException("dock_harbor_lifecycle_binding_conflict");
                if (existing.Phase == HarborCheckoutLifecyclePhaseEnum.Terminal
                    && existing.Purpose == HarborCheckoutLifecyclePurposeEnum.Reclaim
                    && requestedPurpose != HarborCheckoutLifecyclePurposeEnum.Reclaim)
                    return await CreatePostRemovalBranchCleanupIntentAsync(current, existing, requestedPurpose, token).ConfigureAwait(false);
                dock.HostCheckoutLifecycle = existing;
                return existing;
            }

            string head = await ReadHarborHeadAsync(current, token).ConfigureAwait(false);
            Vessel? vessel = await _Database.Vessels.ReadAsync(current.TenantId!, current.VesselId, token).ConfigureAwait(false);
            BranchCleanupPolicyEnum cleanup = vessel?.BranchCleanupPolicy ?? _Settings.BranchCleanupPolicy;
            HarborCheckoutLifecycleIntent intent = new HarborCheckoutLifecycleIntent
            {
                IntentId = "hcl_" + Guid.NewGuid().ToString("N"),
                BindingRevision = binding.Revision,
                DockId = current.Id,
                MissionId = current.GitAnchorsSnapshot?.MissionId,
                BranchName = current.BranchName,
                Purpose = requestedPurpose,
                InitialHead = head,
                ExpectedHead = head,
                PreserveSnapshot = true,
                PreserveReferences = true,
                PreserveBranch = true,
                DeleteLocalBranch = requestedPurpose == HarborCheckoutLifecyclePurposeEnum.Delete && cleanup != BranchCleanupPolicyEnum.None,
                DeleteRemoteBranch = requestedPurpose == HarborCheckoutLifecyclePurposeEnum.Delete && cleanup == BranchCleanupPolicyEnum.LocalAndRemote
            };
            string bindingJson = HarborCheckoutBindingPersistence.Serialize(binding);
            if (!await _Database.Docks.TryWriteHarborLifecycleIntentAsync(current.Id, bindingJson, null, intent, token).ConfigureAwait(false))
            {
                Dock winner = await ReadCurrentHarborDockAsync(current, token).ConfigureAwait(false);
                HarborCheckoutLifecycleIntent? winnerIntent = winner.HostCheckoutLifecycle;
                if (winnerIntent == null || winnerIntent.BindingRevision != binding.Revision)
                    throw new InvalidOperationException("dock_harbor_lifecycle_intent_cas_conflict");
                dock.HostCheckoutLifecycle = winnerIntent;
                return winnerIntent;
            }
            dock.HostCheckoutLifecycle = intent;
            return intent;
        }

        private async Task<HarborCheckoutLifecycleIntent> CreatePostRemovalBranchCleanupIntentAsync(Dock dock,
            HarborCheckoutLifecycleIntent completedReclaim, HarborCheckoutLifecyclePurposeEnum purpose,
            CancellationToken token)
        {
            if (!DockGitAnchorPersistence.IsCommit(completedReclaim.AnchoredHead))
                throw new InvalidOperationException("dock_harbor_branch_cleanup_anchor_missing");
            Vessel? vessel = await _Database.Vessels.ReadAsync(dock.TenantId!, dock.VesselId, token).ConfigureAwait(false);
            BranchCleanupPolicyEnum cleanup = vessel?.BranchCleanupPolicy ?? _Settings.BranchCleanupPolicy;
            HarborCheckoutLifecycleIntent next = CopyHarborLifecycleIntent(completedReclaim);
            next.IntentId = "hcl_" + Guid.NewGuid().ToString("N");
            next.Purpose = purpose;
            next.InitialHead = completedReclaim.AnchoredHead;
            next.ExpectedHead = completedReclaim.AnchoredHead;
            next.PreserveSnapshot = false;
            next.PreserveReferences = false;
            next.PreserveBranch = false;
            next.DeleteLocalBranch = cleanup != BranchCleanupPolicyEnum.None;
            next.DeleteRemoteBranch = cleanup == BranchCleanupPolicyEnum.LocalAndRemote;
            next.Phase = HarborCheckoutLifecyclePhaseEnum.Prepared;
            next.RemovalConfirmed = false;
            next.CheckoutAlreadyRemoved = true;
            if (!await _Database.Docks.TryWriteHarborLifecycleIntentAsync(dock.Id,
                HarborCheckoutBindingPersistence.Serialize(RequireHarborCheckout(dock)), completedReclaim, next, token).ConfigureAwait(false))
            {
                Dock winner = await ReadCurrentHarborDockAsync(dock, token).ConfigureAwait(false);
                HarborCheckoutLifecycleIntent? winnerIntent = winner.HostCheckoutLifecycle;
                if (winnerIntent == null || winnerIntent.IntentId == completedReclaim.IntentId
                    || !winnerIntent.CheckoutAlreadyRemoved || winnerIntent.Purpose != purpose
                    || winnerIntent.InitialHead != completedReclaim.AnchoredHead)
                    throw new InvalidOperationException("dock_harbor_lifecycle_intent_cas_conflict");
                dock.HostCheckoutLifecycle = winnerIntent;
                return winnerIntent;
            }
            dock.HostCheckoutLifecycle = next;
            return next;
        }
    }
}
