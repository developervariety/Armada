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
        private async Task Binding_RoundTripsAcrossDriverReopen()
        {
            await RunTest("Binding_RoundTripsAcrossDriverReopen", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = new Vessel("Harbor binding vessel", "https://example.invalid/harbor-binding.git")
                    {
                        TenantId = Armada.Core.Constants.DefaultTenantId,
                        UserId = Armada.Core.Constants.DefaultUserId
                    };
                    await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Dock dock = new Dock(vessel.Id)
                    {
                        TenantId = vessel.TenantId,
                        UserId = vessel.UserId,
                        ProvisionAttemptId = "mission-attempt-" + Guid.NewGuid().ToString("N"),
                        WorktreePath = null,
                        HostCheckout = CompleteBinding(vessel.TenantId!, vessel.UserId!)
                    };
                    await testDb.Driver.Docks.CreateAsync(dock).ConfigureAwait(false);

                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;
                    using (SqliteDatabaseDriver reopened = new SqliteDatabaseDriver(testDb.ConnectionString, logging))
                    {
                        Dock? read = await reopened.Docks.ReadAsync(dock.Id).ConfigureAwait(false);
                        AssertNotNull(read);
                        AssertNull(read!.WorktreePath, "remote checkout identities do not use the Admiral-local path field");
                        AssertNotNull(read.HostCheckout);
                        AssertTrue(read.HostCheckout!.IsComplete, "persisted identity must stay complete");
                        AssertEqual(dock.HostCheckout!.RunnerId, read.HostCheckout.RunnerId);
                        AssertEqual(dock.HostCheckout.EnrollmentGeneration, read.HostCheckout.EnrollmentGeneration);
                        AssertEqual(dock.HostCheckout.RepositoryFingerprint, read.HostCheckout.RepositoryFingerprint);
                        AssertEqual(dock.HostCheckout.CheckoutId, read.HostCheckout.CheckoutId);
                        AssertEqual(dock.HostCheckout.Revision, read.HostCheckout.Revision);
                        AssertEqual(dock.ProvisionAttemptId, read.ProvisionAttemptId);
                        Dock? byAttempt = await reopened.Docks.ReadByProvisionAttemptAsync(dock.ProvisionAttemptId!).ConfigureAwait(false);
                        AssertNotNull(byAttempt);
                        AssertEqual(dock.Id, byAttempt!.Id);
                    }

                    Mission mission = new Mission("Mission attempt persistence")
                    {
                        TenantId = vessel.TenantId,
                        UserId = vessel.UserId,
                        VesselId = vessel.Id,
                        DockProvisionAttemptId = dock.ProvisionAttemptId
                    };
                    await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);
                    Mission? reopenedMission = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertNotNull(reopenedMission);
                    AssertEqual(mission.DockProvisionAttemptId, reopenedMission!.DockProvisionAttemptId);
                }
            }).ConfigureAwait(false);
        }

    }
}
