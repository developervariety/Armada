namespace Test.Shared.Suites.Database
{
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Test.Shared.Infrastructure;
    using static Test.Shared.Infrastructure.Asserts;

    public sealed partial class AskThreadDatabaseSuite
    {
        private static async Task OwnerScopeAsync()
        {
            using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
            {
                DatabaseDriver database = testDb.Driver;
                Armada.Core.Models.AskThread first = NewThread();
                first.CaptainId = "cpt_original";
                await database.AskThreads.CreateAsync(first).ConfigureAwait(false);
                Armada.Core.Models.AskThread other = NewThread("ten_ask", "usr_other");
                await database.AskThreads.CreateAsync(other).ConfigureAwait(false);
                AssertNotNull(await database.AskThreads.ReadAsync("ten_ask", "usr_owner", first.Id).ConfigureAwait(false));
                AssertNull(await database.AskThreads.ReadAsync("ten_ask", "usr_other", first.Id).ConfigureAwait(false));
                AssertNull(await database.AskThreads.ReadAsync("ten_other", "usr_owner", first.Id).ConfigureAwait(false));

                Armada.Core.Models.EnumerationResult<Armada.Core.Models.AskThread> page = await database.AskThreads.EnumerateAsync("ten_ask", "usr_owner", 1, 10, false).ConfigureAwait(false);
                AssertEqual(1L, page.TotalRecords);
                first.Title = "Renamed";
                first.CaptainId = null;
                first.Pinned = true;
                first.Archived = true;
                Armada.Core.Models.AskThread? updated = await database.AskThreads.UpdateAsync(first).ConfigureAwait(false);
                AssertNotNull(updated);
                AssertEqual("Renamed", updated!.Title);
                AssertNull(updated.CaptainId);
                AssertTrue(updated.Pinned);
                AssertTrue(updated.Archived);
                AssertEqual(0L, (await database.AskThreads.EnumerateAsync("ten_ask", "usr_owner", 1, 10, false).ConfigureAwait(false)).TotalRecords);
                AssertEqual(1L, (await database.AskThreads.EnumerateAsync("ten_ask", "usr_owner", 1, 10, true).ConfigureAwait(false)).TotalRecords);
            }
        }
    }
}
