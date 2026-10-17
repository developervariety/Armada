namespace Test.Shared.Suites.Database
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Storage;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>Provider-neutral statistics contract, run unchanged across the database matrix.</summary>
    public sealed class StorageDatabaseSuite : IArmadaTestSuite
    {
        /// <summary>Build the shared database storage suite.</summary>
        public TestSuiteDescriptor Build()
        {
            return new TestSuiteDescriptor("Database.Storage", "Database storage statistics", new List<TestCaseDescriptor>
            {
                new TestCaseDescriptor("Database.Storage", "catalog_and_exact", "Catalog sizes and opt-in exact record counts", async ct =>
                {
                    using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        await db.Driver.Fleets.CreateAsync(new Fleet { Name = "Storage fixture" }, ct).ConfigureAwait(false);
                        StorageDatabase metadata = await db.Driver.Storage.MeasureAsync(false, ct).ConfigureAwait(false);
                        AssertTrue(metadata.AllocatedBytes > 0);
                        AssertTrue(metadata.Tables.Any(t => t.Name == "fleets"));
                        AssertTrue(metadata.Tables.All(t => t.CountKind != "Exact"));
                        StorageDatabase exact = await db.Driver.Storage.MeasureAsync(true, ct).ConfigureAwait(false);
                        StorageTable fleets = exact.Tables.Single(t => t.Name == "fleets");
                        AssertEqual("Exact", fleets.CountKind);
                        AssertTrue(fleets.Rows >= 1);
                        AssertTrue(exact.Tables.All(t => t.Rows >= 0));
                        AssertTrue(exact.MeasuredUtc > DateTime.UtcNow.AddMinutes(-1));
                        AssertTrue(exact.Tables.All(t => t.DataBytes == null || t.DataBytes >= 0));
                    }
                }),
                new TestCaseDescriptor("Database.Storage", "cancelled", "Statistics honor cancellation", async ct =>
                {
                    using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    using (CancellationTokenSource cancellation = new CancellationTokenSource())
                    {
                        cancellation.Cancel();
                        bool threw = false;
                        try { await db.Driver.Storage.MeasureAsync(true, cancellation.Token).ConfigureAwait(false); }
                        catch (OperationCanceledException) { threw = true; }
                        AssertTrue(threw);
                    }
                })
            });
        }
    }
}
