#nullable enable

namespace Armada.Test.Database
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Harbor;
    using Armada.Core.Models;

    internal sealed partial class OperationalRoundTripDatabaseTests
    {
        private async Task VerifyDockLifecyclePersistenceAsync(Dock dock, HarborCheckoutBinding binding, string suffix, CancellationToken token)
        {
            HarborCheckoutLifecycleIntent prepared = new HarborCheckoutLifecycleIntent
            {
                IntentId = "hcl-dock-" + suffix,
                BindingRevision = binding.Revision,
                DockId = dock.Id,
                MissionId = "mission-dock-" + suffix,
                BranchName = binding.BranchName,
                Purpose = HarborCheckoutLifecyclePurposeEnum.Reclaim,
                InitialHead = binding.BaseCommit,
                ExpectedHead = binding.BaseCommit,
                PreserveSnapshot = true,
                PreserveReferences = true,
                PreserveBranch = true
            };
            string bindingJson = System.Text.Json.JsonSerializer.Serialize(binding);
            DatabaseAssert.True(await _Driver.Docks.TryWriteHarborLifecycleIntentAsync(dock.Id, bindingJson, null, prepared, token).ConfigureAwait(false),
                "provider stores the first lifecycle intent through null-state CAS");
            using (DatabaseDriver reopened = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false))
            {
                await AdvanceDockReclaimLifecycleAsync(reopened, dock, binding, suffix, prepared, bindingJson, token).ConfigureAwait(false);
            }
        }

        private async Task AdvanceDockReclaimLifecycleAsync(DatabaseDriver reopened, Dock dock, HarborCheckoutBinding binding,
            string suffix, HarborCheckoutLifecycleIntent prepared, string bindingJson, CancellationToken token)
        {
            Dock persisted = DatabaseAssert.NotNull(await reopened.Docks.ReadAsync(dock.Id, token).ConfigureAwait(false), "Dock reopened before snapshot phase");
            HarborCheckoutLifecycleIntent durablePrepared = DatabaseAssert.NotNull(persisted.HostCheckoutLifecycle, "Prepared intent survives reopen");
            HarborCheckoutLifecycleIntent snapshot = CopyLifecycle(durablePrepared, HarborCheckoutLifecyclePhaseEnum.SnapshotComplete);
            snapshot.ExpectedHead = new string('c', 40);
            snapshot.SnapshotCommitSha = snapshot.ExpectedHead;
            snapshot.SnapshotRefName = "refs/heads/recover/" + suffix;
            DatabaseAssert.True(await _Driver.Docks.TryWriteHarborLifecycleIntentAsync(dock.Id, bindingJson,
                durablePrepared, snapshot, token).ConfigureAwait(false), "provider accepts a pinned snapshot receipt");
            DatabaseAssert.True(!await reopened.Docks.TryWriteHarborLifecycleIntentAsync(dock.Id, bindingJson,
                durablePrepared, durablePrepared, token).ConfigureAwait(false), "a stale lifecycle writer cannot overwrite the advanced snapshot receipt");

            Dock afterSnapshot = DatabaseAssert.NotNull(await reopened.Docks.ReadAsync(dock.Id, token).ConfigureAwait(false), "Dock reopened after snapshot phase");
            HarborCheckoutLifecycleIntent durableSnapshot = DatabaseAssert.NotNull(afterSnapshot.HostCheckoutLifecycle, "Snapshot phase survives reopen");
            HarborCheckoutLifecycleIntent references = CopyLifecycle(durableSnapshot, HarborCheckoutLifecyclePhaseEnum.ReferencesComplete);
            references.AnchoredHead = durableSnapshot.ExpectedHead;
            references.DockAnchorRef = "refs/armada/docks/" + dock.Id;
            references.MissionAnchorRef = "refs/armada/missions/" + prepared.MissionId;
            references.BranchAnchorRef = "refs/armada-preserved/" + binding.BranchName;
            DatabaseAssert.True(await reopened.Docks.TryWriteHarborLifecycleIntentAsync(dock.Id, bindingJson,
                durableSnapshot, references, token).ConfigureAwait(false), "provider accepts reference receipts at the pinned snapshot head");

            Dock afterReferences = DatabaseAssert.NotNull(await _Driver.Docks.ReadAsync(dock.Id, token).ConfigureAwait(false), "Dock reopened after reference phase");
            HarborCheckoutLifecycleIntent cleanup = CopyLifecycle(afterReferences.HostCheckoutLifecycle!, HarborCheckoutLifecyclePhaseEnum.BranchCleanupComplete);
            DatabaseAssert.True(await _Driver.Docks.TryWriteHarborLifecycleIntentAsync(dock.Id, bindingJson,
                afterReferences.HostCheckoutLifecycle, cleanup, token).ConfigureAwait(false), "provider persists the branch cleanup receipt");
            await PersistDockRemovalReceiptAsync(reopened, dock, bindingJson, token).ConfigureAwait(false);
        }

        private async Task PersistDockRemovalReceiptAsync(DatabaseDriver reopened, Dock dock, string bindingJson, CancellationToken token)
        {
            Dock beforeRemoval = DatabaseAssert.NotNull(await reopened.Docks.ReadAsync(dock.Id, token).ConfigureAwait(false), "Dock reopened before removal phase");
            HarborCheckoutLifecycleIntent removed = CopyLifecycle(beforeRemoval.HostCheckoutLifecycle!, HarborCheckoutLifecyclePhaseEnum.CheckoutRemoved);
            removed.RemovalConfirmed = true;
            DatabaseAssert.True(await reopened.Docks.TryWriteHarborLifecycleIntentAsync(dock.Id, bindingJson,
                beforeRemoval.HostCheckoutLifecycle, removed, token).ConfigureAwait(false), "provider persists confirmed removal");
            Dock beforeTerminal = DatabaseAssert.NotNull(await _Driver.Docks.ReadAsync(dock.Id, token).ConfigureAwait(false), "Dock reopened before terminal phase");
            HarborCheckoutLifecycleIntent terminal = CopyLifecycle(beforeTerminal.HostCheckoutLifecycle!, HarborCheckoutLifecyclePhaseEnum.Terminal);
            DatabaseAssert.True(await _Driver.Docks.TryWriteHarborLifecycleIntentAsync(dock.Id, bindingJson,
                beforeTerminal.HostCheckoutLifecycle, terminal, token).ConfigureAwait(false), "provider persists terminal cleanup after confirmed removal");
        }
    }
}
