namespace Armada.Test.Unit.Suites.Database
{
    using System;
    using System.IO;
    using System.Threading.Tasks;
    using Armada.Core.Database.Sqlite;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using Microsoft.Data.Sqlite;
    using SyslogLogging;

    /// <summary>Persistence tests for a Harbor-owned dock checkout identity.</summary>
    public sealed partial class DockHostCheckoutPersistenceTests
    {
        private async Task TerminalReclaim_CanStartPostRemovalBranchCleanupByCas()
        {
            await RunTest("TerminalReclaim_CanStartPostRemovalBranchCleanupByCas", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(new Vessel("Harbor post-removal vessel", "https://example.invalid/post-removal.git")
                    {
                        TenantId = Armada.Core.Constants.DefaultTenantId,
                        UserId = Armada.Core.Constants.DefaultUserId
                    }).ConfigureAwait(false);
                    Dock dock = new Dock(vessel.Id)
                    {
                        TenantId = vessel.TenantId,
                        UserId = vessel.UserId,
                        HostCheckout = CompleteBinding(vessel.TenantId!, vessel.UserId!)
                    };
                    dock.HostCheckoutLifecycle = new HarborCheckoutLifecycleIntent
                    {
                        IntentId = "hcl_reclaim_terminal",
                        BindingRevision = dock.HostCheckout!.Revision,
                        DockId = dock.Id,
                        MissionId = "mission_post_removal",
                        BranchName = dock.HostCheckout.BranchName,
                        Purpose = HarborCheckoutLifecyclePurposeEnum.Reclaim,
                        InitialHead = new String('a', 40),
                        ExpectedHead = new String('a', 40),
                        AnchoredHead = new String('a', 40),
                        DockAnchorRef = "refs/armada/docks/" + dock.Id,
                        MissionAnchorRef = "refs/armada/missions/mission_post_removal",
                        BranchAnchorRef = "refs/armada-preserved/harbor/mission-branch",
                        PreserveSnapshot = true,
                        PreserveReferences = true,
                        PreserveBranch = true,
                        Phase = HarborCheckoutLifecyclePhaseEnum.Terminal,
                        RemovalConfirmed = true
                    };
                    await testDb.Driver.Docks.CreateAsync(dock).ConfigureAwait(false);
                    HarborCheckoutLifecycleIntent terminal = dock.HostCheckoutLifecycle!;
                    dock = (await testDb.Driver.Docks.ReadAsync(dock.Id).ConfigureAwait(false))!;
                    terminal = dock.HostCheckoutLifecycle!;
                    string bindingJson = System.Text.Json.JsonSerializer.Serialize(dock.HostCheckout);
                    HarborCheckoutLifecycleIntent cleanup = new HarborCheckoutLifecycleIntent
                    {
                        IntentId = "hcl_post_remove_cleanup",
                        BindingRevision = terminal.BindingRevision,
                        DockId = dock.Id,
                        MissionId = terminal.MissionId,
                        BranchName = terminal.BranchName,
                        Purpose = HarborCheckoutLifecyclePurposeEnum.Delete,
                        InitialHead = terminal.AnchoredHead!,
                        ExpectedHead = terminal.AnchoredHead!,
                        SnapshotCommitSha = terminal.SnapshotCommitSha,
                        SnapshotRefName = terminal.SnapshotRefName,
                        AnchoredHead = terminal.AnchoredHead,
                        DockAnchorRef = terminal.DockAnchorRef,
                        MissionAnchorRef = terminal.MissionAnchorRef,
                        BranchAnchorRef = terminal.BranchAnchorRef,
                        DeleteLocalBranch = true,
                        DeleteRemoteBranch = true,
                        CheckoutAlreadyRemoved = true
                    };
                    AssertTrue(await testDb.Driver.Docks.TryWriteHarborLifecycleIntentAsync(dock.Id, bindingJson, terminal, cleanup).ConfigureAwait(false),
                        "a confirmed reclaim permits a separate durable CAS intent for repository-level branch cleanup");
                    Dock? reopened = await testDb.Driver.Docks.ReadAsync(dock.Id).ConfigureAwait(false);
                    AssertNotNull(reopened?.HostCheckoutLifecycle);
                    AssertTrue(reopened!.HostCheckoutLifecycle!.CheckoutAlreadyRemoved, "cleanup retains the removed-checkout fence");
                    AssertEqual(terminal.AnchoredHead, reopened.HostCheckoutLifecycle.AnchoredHead, "cleanup reuses the stored anchor without reading the removed checkout");
                    AssertEqual(HarborCheckoutLifecyclePhaseEnum.Prepared, reopened.HostCheckoutLifecycle.Phase, "cleanup remains pending until both CAS branch deletes return");
                }
            }).ConfigureAwait(false);
        }

    }
}
