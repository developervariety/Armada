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
        private async Task VerifyPostReclaimDeleteIntentAsync(Dock dock, HarborCheckoutBinding binding, string suffix, CancellationToken token)
        {
            using (DatabaseDriver reopened = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false))
            {
                Dock persisted = DatabaseAssert.NotNull(await reopened.Docks.ReadAsync(dock.Id, token).ConfigureAwait(false), "Terminal reclaim Dock reopens");
                HarborCheckoutLifecycleIntent terminal = DatabaseAssert.NotNull(persisted.HostCheckoutLifecycle, "Terminal reclaim receipt is durable");
                DatabaseAssert.Equal(HarborCheckoutLifecyclePhaseEnum.Terminal, terminal.Phase, "reclaim reaches its terminal phase");
                DatabaseAssert.True(terminal.RemovalConfirmed, "reclaim records confirmed checkout removal");
                DatabaseAssert.Equal(new string('c', 40), terminal.AnchoredHead, "reclaim retains its pinned snapshot head");
                DatabaseAssert.Equal("refs/armada-preserved/" + binding.BranchName, terminal.BranchAnchorRef,
                    "reclaim retains the exact protected branch reference");

                HarborCheckoutLifecycleIntent deletion = CopyLifecycle(terminal, HarborCheckoutLifecyclePhaseEnum.Prepared);
                deletion.IntentId = "hcl-delete-after-reclaim-" + suffix;
                deletion.Purpose = HarborCheckoutLifecyclePurposeEnum.Delete;
                deletion.InitialHead = terminal.AnchoredHead!;
                deletion.ExpectedHead = terminal.AnchoredHead!;
                deletion.PreserveSnapshot = false;
                deletion.PreserveReferences = false;
                deletion.PreserveBranch = false;
                deletion.RemovalConfirmed = false;
                deletion.CheckoutAlreadyRemoved = true;
                bool accepted = await _Driver.Docks.TryWriteHarborLifecycleIntentAsync(dock.Id,
                    System.Text.Json.JsonSerializer.Serialize(binding), terminal, deletion, token).ConfigureAwait(false);
                DatabaseAssert.True(accepted, "a completed reclaim receipt accepts the exact delete-as-already-removed intent");

                Dock afterDeleteIntent = DatabaseAssert.NotNull(await reopened.Docks.ReadAsync(dock.Id, token).ConfigureAwait(false), "Post-reclaim delete intent reopens");
                HarborCheckoutLifecycleIntent durableDelete = DatabaseAssert.NotNull(afterDeleteIntent.HostCheckoutLifecycle, "Post-reclaim delete intent persists");
                DatabaseAssert.Equal(deletion.IntentId, durableDelete.IntentId, "the delete has a new durable intent identity");
                DatabaseAssert.True(durableDelete.CheckoutAlreadyRemoved, "the delete replay will not remove the missing checkout again");
                DatabaseAssert.Equal(terminal.AnchoredHead, durableDelete.ExpectedHead, "the delete keeps the completed reclaim's exact head fence");
            }
        }

        private static HarborCheckoutLifecycleIntent CopyLifecycle(HarborCheckoutLifecycleIntent source,
            HarborCheckoutLifecyclePhaseEnum phase)
        {
            return new HarborCheckoutLifecycleIntent
            {
                IntentId = source.IntentId,
                BindingRevision = source.BindingRevision,
                DockId = source.DockId,
                MissionId = source.MissionId,
                BranchName = source.BranchName,
                Purpose = source.Purpose,
                InitialHead = source.InitialHead,
                ExpectedHead = source.ExpectedHead,
                SnapshotCommitSha = source.SnapshotCommitSha,
                SnapshotRefName = source.SnapshotRefName,
                AnchoredHead = source.AnchoredHead,
                DockAnchorRef = source.DockAnchorRef,
                MissionAnchorRef = source.MissionAnchorRef,
                BranchAnchorRef = source.BranchAnchorRef,
                PreserveSnapshot = source.PreserveSnapshot,
                PreserveReferences = source.PreserveReferences,
                PreserveBranch = source.PreserveBranch,
                DeleteLocalBranch = source.DeleteLocalBranch,
                DeleteRemoteBranch = source.DeleteRemoteBranch,
                Phase = phase,
                RemovalConfirmed = source.RemovalConfirmed,
                CheckoutAlreadyRemoved = source.CheckoutAlreadyRemoved
            };
        }
    }
}
