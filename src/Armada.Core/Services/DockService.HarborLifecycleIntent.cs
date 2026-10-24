namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Harbor;
    using Armada.Core.Models;

    /// <summary>Persists and advances exact Harbor checkout cleanup intents.</summary>
    public partial class DockService
    {
        private async Task<HarborCheckoutLifecycleIntent> AdvanceHarborLifecycleAsync(Dock dock,
            HarborCheckoutLifecycleIntent expected, HarborCheckoutLifecyclePhaseEnum phase,
            Action<HarborCheckoutLifecycleIntent>? update, CancellationToken token)
        {
            HarborCheckoutLifecycleIntent next = CopyHarborLifecycleIntent(expected);
            next.Phase = phase;
            update?.Invoke(next);
            HarborCheckoutBinding binding = RequireHarborCheckout(dock);
            string bindingJson = HarborCheckoutBindingPersistence.Serialize(binding);
            if (await _Database.Docks.TryWriteHarborLifecycleIntentAsync(dock.Id, bindingJson, expected, next, token).ConfigureAwait(false))
            {
                dock.HostCheckoutLifecycle = next;
                return next;
            }

            Dock winner = await ReadCurrentHarborDockAsync(dock, token).ConfigureAwait(false);
            HarborCheckoutLifecycleIntent? actual = winner.HostCheckoutLifecycle;
            if (actual == null || !String.Equals(actual.IntentId, expected.IntentId, StringComparison.Ordinal)
                || actual.Phase < phase)
                throw new InvalidOperationException("dock_harbor_lifecycle_intent_cas_conflict");
            dock.HostCheckoutLifecycle = actual;
            return actual;
        }

        private async Task<Dock> ReadCurrentHarborDockAsync(Dock dock, CancellationToken token)
        {
            Dock? current = await _Database.Docks.ReadAsync(dock.TenantId!, dock.Id, token).ConfigureAwait(false);
            if (current == null) throw new InvalidOperationException("dock_harbor_lifecycle_dock_missing");
            RequireHarborCheckout(current);
            return current;
        }

        private async Task RequireHarborStopsConfirmedAsync(Dock dock, CancellationToken token)
        {
            List<HarborJobRecord> jobs = await _Database.HarborJobs.EnumerateAsync(new HarborJobQuery
            {
                DockId = dock.Id,
                UnconfirmedOnly = true,
                Limit = 1
            }, token).ConfigureAwait(false);
            if (jobs.Count == 0)
                jobs = await _Database.HarborJobs.EnumerateAsync(new HarborJobQuery
                {
                    TenantId = dock.TenantId,
                    UnboundMissionOnly = true,
                    UnconfirmedOnly = true,
                    Limit = 1
                }, token).ConfigureAwait(false);
            if (jobs.Count > 0)
                throw new InvalidOperationException("dock_worktree_held: Harbor job " + jobs[0].JobId + " has no confirmed stop");
        }

        private static HarborCheckoutLifecycleIntent CopyHarborLifecycleIntent(HarborCheckoutLifecycleIntent intent)
        {
            return new HarborCheckoutLifecycleIntent
            {
                Version = intent.Version,
                IntentId = intent.IntentId,
                BindingRevision = intent.BindingRevision,
                DockId = intent.DockId,
                MissionId = intent.MissionId,
                BranchName = intent.BranchName,
                Purpose = intent.Purpose,
                InitialHead = intent.InitialHead,
                ExpectedHead = intent.ExpectedHead,
                SnapshotCommitSha = intent.SnapshotCommitSha,
                SnapshotRefName = intent.SnapshotRefName,
                AnchoredHead = intent.AnchoredHead,
                DockAnchorRef = intent.DockAnchorRef,
                MissionAnchorRef = intent.MissionAnchorRef,
                BranchAnchorRef = intent.BranchAnchorRef,
                PreserveSnapshot = intent.PreserveSnapshot,
                PreserveReferences = intent.PreserveReferences,
                PreserveBranch = intent.PreserveBranch,
                DeleteLocalBranch = intent.DeleteLocalBranch,
                DeleteRemoteBranch = intent.DeleteRemoteBranch,
                Phase = intent.Phase,
                RemovalConfirmed = intent.RemovalConfirmed,
                CheckoutAlreadyRemoved = intent.CheckoutAlreadyRemoved
            };
        }
    }
}
