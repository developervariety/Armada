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
        private async Task LegacyNullBinding_PreservesLocalPath()
        {
            await RunTest("LegacyNullBinding_PreservesLocalPath", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = new Vessel("Legacy host checkout vessel", "https://example.invalid/legacy-checkout.git")
                    {
                        TenantId = Armada.Core.Constants.DefaultTenantId,
                        UserId = Armada.Core.Constants.DefaultUserId
                    };
                    await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);
                    string localPath = Path.Combine(Path.GetTempPath(), "legacy-dock-" + Guid.NewGuid().ToString("N"));
                    Dock dock = new Dock(vessel.Id)
                    {
                        TenantId = vessel.TenantId,
                        UserId = vessel.UserId,
                        WorktreePath = localPath
                    };
                    await testDb.Driver.Docks.CreateAsync(dock).ConfigureAwait(false);

                    Dock? read = await testDb.Driver.Docks.ReadAsync(dock.Id).ConfigureAwait(false);
                    AssertNotNull(read);
                    AssertNull(read!.HostCheckout, "a SQL NULL is the legacy local-checkout state");
                    AssertNull(read.ProvisionAttemptId, "a SQL NULL is the legacy provisioning-attempt state");
                    AssertEqual(localPath, read.WorktreePath, "legacy local path remains intact");
                }
            }).ConfigureAwait(false);
        }

        private async Task MalformedNonNullBinding_RefusesDockReadDespiteLocalPath()
        {
            await RunTest("MalformedNonNullBinding_RefusesDockReadDespiteLocalPath", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = new Vessel("Malformed host checkout vessel", "https://example.invalid/bad-checkout.git")
                    {
                        TenantId = Armada.Core.Constants.DefaultTenantId,
                        UserId = Armada.Core.Constants.DefaultUserId
                    };
                    await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);
                    Dock dock = new Dock(vessel.Id)
                    {
                        TenantId = vessel.TenantId,
                        UserId = vessel.UserId,
                        WorktreePath = Path.Combine(Path.GetTempPath(), "must-not-fallback-" + Guid.NewGuid().ToString("N"))
                    };
                    await testDb.Driver.Docks.CreateAsync(dock).ConfigureAwait(false);

                    await StoreMalformedBindingAsync(testDb.ConnectionString, dock.Id).ConfigureAwait(false);

                    try
                    {
                        await testDb.Driver.Docks.ReadAsync(dock.Id).ConfigureAwait(false);
                        AssertTrue(false, "a non-null malformed binding must not fall back to WorktreePath");
                    }
                    catch (InvalidOperationException exception)
                    {
                        AssertContains("harbor_checkout_binding_invalid", exception.Message);
                    }
                }
            }).ConfigureAwait(false);
        }

        private async Task StoreMalformedBindingAsync(string connectionString, string dockId)
        {
            using (SqliteConnection connection = new SqliteConnection(connectionString))
            {
                await connection.OpenAsync().ConfigureAwait(false);
                using (SqliteCommand command = connection.CreateCommand())
                {
                    command.CommandText = "UPDATE docks SET host_checkout_json = @payload WHERE id = @id;";
                    command.Parameters.AddWithValue("@payload", "{\"Version\":99}");
                    command.Parameters.AddWithValue("@id", dockId);
                    AssertEqual(1, await command.ExecuteNonQueryAsync().ConfigureAwait(false));
                }
            }
        }

    }
}
