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
        private async Task FinishLifecyclePhasesAsync(Armada.Core.Database.DatabaseDriver database, Dock dock,
            string bindingJson, Dock final)
        {
            HarborCheckoutLifecycleIntent expected = final.HostCheckoutLifecycle!;
            HarborCheckoutLifecycleIntent references = CopyLifecycleForTest(expected, HarborCheckoutLifecyclePhaseEnum.ReferencesComplete);
            references.AnchoredHead = expected.ExpectedHead;
            references.DockAnchorRef = "refs/armada/docks/" + dock.Id;
            AssertTrue(await database.Docks.TryWriteHarborLifecycleIntentAsync(dock.Id, bindingJson, expected, references).ConfigureAwait(false),
                "the exact lifecycle owner records recovery references before checkout removal");
            final = await database.Docks.ReadAsync(dock.Id).ConfigureAwait(false);
            AssertEqual(HarborCheckoutLifecyclePhaseEnum.ReferencesComplete, final!.HostCheckoutLifecycle!.Phase,
                "the phase survives a database reopen before branch cleanup");

            expected = final.HostCheckoutLifecycle;
            HarborCheckoutLifecycleIntent branchCleanup = CopyLifecycleForTest(expected, HarborCheckoutLifecyclePhaseEnum.BranchCleanupComplete);
            AssertTrue(await database.Docks.TryWriteHarborLifecycleIntentAsync(dock.Id, bindingJson, expected, branchCleanup).ConfigureAwait(false),
                "branch cleanup receipt advances only from the exact references phase");
            final = await database.Docks.ReadAsync(dock.Id).ConfigureAwait(false);
            expected = final!.HostCheckoutLifecycle!;
            HarborCheckoutLifecycleIntent removed = CopyLifecycleForTest(expected, HarborCheckoutLifecyclePhaseEnum.CheckoutRemoved);
            removed.RemovalConfirmed = true;
            AssertTrue(await database.Docks.TryWriteHarborLifecycleIntentAsync(dock.Id, bindingJson, expected, removed).ConfigureAwait(false),
                "confirmed removal is durable before terminal cleanup");
            final = await database.Docks.ReadAsync(dock.Id).ConfigureAwait(false);
            expected = final!.HostCheckoutLifecycle!;
            HarborCheckoutLifecycleIntent terminal = CopyLifecycleForTest(expected, HarborCheckoutLifecyclePhaseEnum.Terminal);
            AssertTrue(await database.Docks.TryWriteHarborLifecycleIntentAsync(dock.Id, bindingJson, expected, terminal).ConfigureAwait(false),
                "terminal state follows the persisted confirmed-removal receipt");
        }

    }
}
