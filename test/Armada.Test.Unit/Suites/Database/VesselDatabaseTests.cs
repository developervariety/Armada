namespace Armada.Test.Unit.Suites.Database
{
    using Armada.Core.Database.Sqlite;
    using Armada.Core.Models;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    public class VesselDatabaseTests : TestSuite
    {
        public override string Name => "Vessel Database";

        protected override async Task RunTestsAsync()
        {
            await RunTest("CreateAsync persists SiblingRepos and ReadAsync round-trips them", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    Vessel vessel = new Vessel("SiblingPersistVessel", "https://github.com/test/repo");
                    vessel.SiblingRepos = "[{\"relativePath\":\"../SibA\",\"repoUrl\":\"https://github.com/test/sibA.git\",\"branchStrategy\":\"MatchBranchElseDefault\",\"defaultBranch\":\"main\"}]";
                    await db.Vessels.CreateAsync(vessel);

                    Vessel? result = await db.Vessels.ReadAsync(vessel.Id);
                    AssertNotNull(result);
                    AssertEqual(vessel.SiblingRepos, result!.SiblingRepos);
                    AssertEqual(1, result.GetSiblingRepos().Count);
                    AssertEqual("../SibA", result.GetSiblingRepos()[0].RelativePath);
                }
            });

            await RunTest("CreateAsync with null SiblingRepos persists null", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    Vessel vessel = new Vessel("NullSiblingVessel", "https://github.com/test/repo");
                    await db.Vessels.CreateAsync(vessel);

                    Vessel? result = await db.Vessels.ReadAsync(vessel.Id);
                    AssertNotNull(result);
                    AssertNull(result!.SiblingRepos);
                    AssertEqual(0, result.GetSiblingRepos().Count);
                }
            });

            await RunTest("UpdateAsync modifies and clears SiblingRepos", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    Vessel vessel = new Vessel("UpdateSiblingVessel", "https://github.com/test/repo");
                    await db.Vessels.CreateAsync(vessel);

                    vessel.SiblingRepos = "[{\"relativePath\":\"../SibB\"}]";
                    await db.Vessels.UpdateAsync(vessel);

                    Vessel? updated = await db.Vessels.ReadAsync(vessel.Id);
                    AssertEqual(1, updated!.GetSiblingRepos().Count);
                    AssertEqual("../SibB", updated.GetSiblingRepos()[0].RelativePath);

                    vessel.SiblingRepos = null;
                    await db.Vessels.UpdateAsync(vessel);

                    Vessel? cleared = await db.Vessels.ReadAsync(vessel.Id);
                    AssertNull(cleared!.SiblingRepos);
                }
            });
        }
    }
}
