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
        private async Task LifecycleIntent_UsesExactCompareAndSetAndSurvivesGenericUpdate()
        {
            await RunTest("LifecycleIntent_UsesExactCompareAndSetAndSurvivesGenericUpdate", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(new Vessel("Harbor lifecycle vessel", "https://example.invalid/lifecycle.git")
                    {
                        TenantId = Armada.Core.Constants.DefaultTenantId,
                        UserId = Armada.Core.Constants.DefaultUserId
                    }).ConfigureAwait(false);
                    Dock dock = await testDb.Driver.Docks.CreateAsync(new Dock(vessel.Id)
                    {
                        TenantId = vessel.TenantId,
                        UserId = vessel.UserId,
                        ProvisionAttemptId = "lifecycle-attempt-" + Guid.NewGuid().ToString("N"),
                        HostCheckout = CompleteBinding(vessel.TenantId!, vessel.UserId!)
                    }).ConfigureAwait(false);
                    HarborCheckoutLifecycleIntent initial = new HarborCheckoutLifecycleIntent
                    {
                        IntentId = "hcl_cas_test",
                        BindingRevision = dock.HostCheckout!.Revision,
                        DockId = dock.Id,
                        Purpose = HarborCheckoutLifecyclePurposeEnum.Reclaim,
                        InitialHead = new String('a', 40),
                        ExpectedHead = new String('a', 40),
                        PreserveSnapshot = true,
                        PreserveReferences = true,
                        PreserveBranch = true
                    };
                    string bindingJson = System.Text.Json.JsonSerializer.Serialize(dock.HostCheckout);
                    AssertTrue(await testDb.Driver.Docks.TryWriteHarborLifecycleIntentAsync(dock.Id, bindingJson, null, initial).ConfigureAwait(false),
                        "the first owner writes the lifecycle intent by null-state CAS");
                    AssertFalse(await testDb.Driver.Docks.TryWriteHarborLifecycleIntentAsync(dock.Id, bindingJson, null,
                        new HarborCheckoutLifecycleIntent
                        {
                            IntentId = "hcl_stale_test",
                            BindingRevision = dock.HostCheckout.Revision,
                            DockId = dock.Id,
                            Purpose = HarborCheckoutLifecyclePurposeEnum.Delete,
                            InitialHead = new String('a', 40),
                            ExpectedHead = new String('a', 40)
                        }).ConfigureAwait(false), "a concurrent cleanup cannot replace the intent");

                    Dock? reloaded = await testDb.Driver.Docks.ReadAsync(dock.Id).ConfigureAwait(false);
                    AssertNotNull(reloaded?.HostCheckoutLifecycle);
                    HarborCheckoutLifecycleIntent expected = reloaded!.HostCheckoutLifecycle!;
                    HarborCheckoutLifecycleIntent next = new HarborCheckoutLifecycleIntent
                    {
                        IntentId = expected.IntentId,
                        BindingRevision = expected.BindingRevision,
                        DockId = expected.DockId,
                        MissionId = expected.MissionId,
                        BranchName = expected.BranchName,
                        Purpose = expected.Purpose,
                        InitialHead = expected.InitialHead,
                        ExpectedHead = new String('b', 40),
                        SnapshotCommitSha = new String('b', 40),
                        SnapshotRefName = "refs/heads/recover/cas",
                        PreserveSnapshot = expected.PreserveSnapshot,
                        PreserveReferences = expected.PreserveReferences,
                        PreserveBranch = expected.PreserveBranch,
                        DeleteLocalBranch = expected.DeleteLocalBranch,
                        DeleteRemoteBranch = expected.DeleteRemoteBranch,
                        Phase = HarborCheckoutLifecyclePhaseEnum.SnapshotComplete
                    };
                    AssertTrue(await testDb.Driver.Docks.TryWriteHarborLifecycleIntentAsync(dock.Id, bindingJson, expected, next).ConfigureAwait(false),
                        "the exact owner advances the pinned snapshot receipt by CAS");

                    reloaded = await testDb.Driver.Docks.ReadAsync(dock.Id).ConfigureAwait(false);
                    reloaded!.BranchName = "metadata-update";
                    reloaded.HostCheckout = null;
                    reloaded.ProvisionAttemptId = null;
                    await testDb.Driver.Docks.UpdateAsync(reloaded).ConfigureAwait(false);
                    Dock? final = await testDb.Driver.Docks.ReadAsync(dock.Id).ConfigureAwait(false);
                    AssertEqual(HarborCheckoutLifecyclePhaseEnum.SnapshotComplete, final!.HostCheckoutLifecycle!.Phase,
                        "a generic dock update cannot erase or roll back the durable lifecycle phase");
                    AssertEqual(new String('b', 40), final.HostCheckoutLifecycle.ExpectedHead,
                        "the next operation retains the snapshot commit as its pinned head");
                    AssertTrue(final.HostCheckout!.IsComplete, "generic metadata updates cannot clear a remote binding into local fallback");
                    AssertEqual(dock.ProvisionAttemptId, final.ProvisionAttemptId, "generic updates cannot erase the stable provisioning attempt");

                    await FinishLifecyclePhasesAsync(testDb.Driver, dock, bindingJson, final).ConfigureAwait(false);
                }
            }).ConfigureAwait(false);
        }

    }
}
