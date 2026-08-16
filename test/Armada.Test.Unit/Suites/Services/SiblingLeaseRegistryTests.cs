namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// Coverage for the persisted sibling-worktree lease registry: reference-counted leases,
    /// lease-guarded removal, and stale-lease reconciliation across restarts.
    /// </summary>
    public class SiblingLeaseRegistryTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Sibling Lease Registry";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("Two docks sharing one sibling target: lease removal is reference-counted", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                ArmadaSettings settings = CreateSettings();

                try
                {
                    SiblingLeaseRegistry registry = new SiblingLeaseRegistry(logging, testDb.Driver, settings);
                    string siblingPath = Path.Combine(settings.DocksDirectory, "ExampleVessel", "ExampleSibling");

                    Dock dockA = await CreateActiveDockAsync(testDb, "vsl_test").ConfigureAwait(false);
                    Dock dockB = await CreateActiveDockAsync(testDb, "vsl_test").ConfigureAwait(false);

                    await registry.TryAcquireAsync(dockA.Id, "vsl_test", siblingPath).ConfigureAwait(false);
                    await registry.TryAcquireAsync(dockB.Id, "vsl_test", siblingPath).ConfigureAwait(false);

                    AssertTrue(
                        await registry.HasOtherLeaseAsync("vsl_test", siblingPath, dockA.Id).ConfigureAwait(false),
                        "Dock B must still hold a lease on the shared sibling after dock A acquires.");

                    bool removed = false;
                    bool result = await registry.RemoveIfUnleasedAsync(
                        dockA.Id,
                        "vsl_test",
                        siblingPath,
                        (token) =>
                        {
                            removed = true;
                            return Task.CompletedTask;
                        }).ConfigureAwait(false);

                    AssertFalse(result, "Sibling must not be removed while another dock holds a lease.");
                    AssertFalse(removed, "Removal action must not run while another dock holds a lease.");

                    result = await registry.RemoveIfUnleasedAsync(
                        dockB.Id,
                        "vsl_test",
                        siblingPath,
                        (token) =>
                        {
                            removed = true;
                            return Task.CompletedTask;
                        }).ConfigureAwait(false);

                    AssertTrue(result, "Sibling must be removed when the last lease holder releases.");
                    AssertTrue(removed, "Removal action must run when no lease remains.");
                }
                finally
                {
                    Cleanup(settings);
                }
            }).ConfigureAwait(false);

            await RunTest("RemoveIfUnleased with no prior lease still runs the removal action", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                ArmadaSettings settings = CreateSettings();

                try
                {
                    SiblingLeaseRegistry registry = new SiblingLeaseRegistry(logging, testDb.Driver, settings);
                    string siblingPath = Path.Combine(settings.DocksDirectory, "ExampleVessel", "ExampleSibling");

                    bool removed = false;
                    bool result = await registry.RemoveIfUnleasedAsync(
                        "dck_unknown",
                        "vsl_test",
                        siblingPath,
                        (token) =>
                        {
                            removed = true;
                            return Task.CompletedTask;
                        }).ConfigureAwait(false);

                    AssertTrue(result, "An unleased sibling target is removable immediately.");
                    AssertTrue(removed, "Removal action must run for an unleased target.");
                }
                finally
                {
                    Cleanup(settings);
                }
            }).ConfigureAwait(false);

            await RunTest("Lease file survives a registry restart and purges when the holder dock is gone", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                ArmadaSettings settings = CreateSettings();

                try
                {
                    string siblingPath = Path.Combine(settings.DocksDirectory, "ExampleVessel", "ExampleSibling");

                    Dock dock = await CreateActiveDockAsync(testDb, "vsl_test").ConfigureAwait(false);

                    SiblingLeaseRegistry first = new SiblingLeaseRegistry(logging, testDb.Driver, settings);
                    await first.TryAcquireAsync(dock.Id, "vsl_test", siblingPath).ConfigureAwait(false);
                    string leasePath = first.GetLeasePath("vsl_test", siblingPath);
                    AssertTrue(File.Exists(leasePath), "Lease must be persisted to disk.");

                    // Simulate an Admiral restart: a fresh registry instance sees the same lease.
                    SiblingLeaseRegistry restarted = new SiblingLeaseRegistry(logging, testDb.Driver, settings);
                    AssertTrue(
                        await restarted.HasOtherLeaseAsync("vsl_test", siblingPath, null).ConfigureAwait(false),
                        "Lease must survive a registry restart (crash-safe).");

                    // The holder dock disappears; reconciliation must purge the stale lease.
                    await testDb.Driver.Docks.DeleteAsync(dock.Id).ConfigureAwait(false);
                    int removed = await restarted.ReconcileAsync(TimeSpan.FromHours(1)).ConfigureAwait(false);
                    AssertTrue(removed >= 1, "Reconciliation must purge a lease whose holder dock no longer exists.");
                    AssertFalse(File.Exists(leasePath), "Lease file must be deleted after reconciliation.");
                }
                finally
                {
                    Cleanup(settings);
                }
            }).ConfigureAwait(false);

            await RunTest("Reconciliation keeps a lease whose holder dock is still active", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                ArmadaSettings settings = CreateSettings();

                try
                {
                    string siblingPath = Path.Combine(settings.DocksDirectory, "ExampleVessel", "ExampleSibling");

                    Dock dock = await CreateActiveDockAsync(testDb, "vsl_test").ConfigureAwait(false);
                    SiblingLeaseRegistry registry = new SiblingLeaseRegistry(logging, testDb.Driver, settings);
                    await registry.TryAcquireAsync(dock.Id, "vsl_test", siblingPath).ConfigureAwait(false);

                    int removed = await registry.ReconcileAsync(TimeSpan.FromHours(1)).ConfigureAwait(false);
                    AssertEqual(0, removed, "A lease held by an active dock must survive reconciliation.");
                }
                finally
                {
                    Cleanup(settings);
                }
            }).ConfigureAwait(false);
        }

        #region Private-Methods

        private static LoggingModule CreateLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        private static ArmadaSettings CreateSettings()
        {
            ArmadaSettings settings = new ArmadaSettings();
            settings.DataDirectory = Path.Combine(Path.GetTempPath(), "armada-lease-" + Guid.NewGuid().ToString("N"));
            settings.DocksDirectory = Path.Combine(settings.DataDirectory, "docks");
            settings.ReposDirectory = Path.Combine(settings.DataDirectory, "repos");
            settings.LogDirectory = Path.Combine(settings.DataDirectory, "logs");
            return settings;
        }

        private static void Cleanup(ArmadaSettings settings)
        {
            try
            {
                if (Directory.Exists(settings.DataDirectory))
                {
                    Directory.Delete(settings.DataDirectory, true);
                }
            }
            catch
            {
            }
        }

        private static async Task<Dock> CreateActiveDockAsync(TestDatabase testDb, string vesselId)
        {
            Vessel? existing = await testDb.Driver.Vessels.ReadAsync(vesselId).ConfigureAwait(false);
            if (existing == null)
            {
                Vessel vessel = new Vessel("Lease Test Vessel", "https://github.com/test/repo.git");
                vessel.Id = vesselId;
                await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);
            }

            Dock dock = new Dock(vesselId);
            dock.WorktreePath = Path.Combine(Path.GetTempPath(), "armada-lease-wt-" + Guid.NewGuid().ToString("N"));
            return await testDb.Driver.Docks.CreateAsync(dock).ConfigureAwait(false);
        }

        #endregion
    }
}
