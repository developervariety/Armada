namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// Item 8 PR-fallback M2 + M3 persistence pin: MergeStatusEnum.PullRequestOpen
    /// + MergeEntry.PrUrl + MergeEntry.PrBaseBranch round-trip through every
    /// supported database backend's MergeEntry methods. Schema migration v36 must
    /// have applied before the test database opens.
    /// </summary>
    public class PrFallbackPersistenceTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "PR-Fallback Persistence";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("MergeStatusEnum_PullRequestOpen_RoundTripsThroughDatabase", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                        new Vessel("pr-vessel-status", "https://github.com/test/repo.git")).ConfigureAwait(false);

                    MergeEntry entry = new MergeEntry();
                    entry.VesselId = vessel.Id;
                    entry.BranchName = "armada/captain/feat-x";
                    entry.TargetBranch = "main";
                    entry.Status = MergeStatusEnum.PullRequestOpen;
                    entry.PrUrl = "https://github.com/test/repo/pull/42";
                    entry.PrBaseBranch = "main";

                    entry = await testDb.Driver.MergeEntries.CreateAsync(entry).ConfigureAwait(false);

                    MergeEntry? readBack = await testDb.Driver.MergeEntries.ReadAsync(entry.Id).ConfigureAwait(false);
                    AssertNotNull(readBack, "Entry should round-trip");
                    AssertEqual(MergeStatusEnum.PullRequestOpen, readBack!.Status,
                        "PullRequestOpen status must round-trip (new enum value v0.7.x)");
                    AssertEqual("https://github.com/test/repo/pull/42", readBack.PrUrl);
                    AssertEqual("main", readBack.PrBaseBranch);
                }
            });

            await RunTest("MergeEntry_PrFields_NullDefault_RoundTrip", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                        new Vessel("pr-vessel-null", "https://github.com/test/repo.git")).ConfigureAwait(false);

                    MergeEntry entry = new MergeEntry();
                    entry.VesselId = vessel.Id;
                    entry.BranchName = "armada/captain/feat-y";
                    entry.TargetBranch = "main";
                    entry.Status = MergeStatusEnum.Queued;
                    // PrUrl + PrBaseBranch left null (entries that never go through PR fallback).

                    entry = await testDb.Driver.MergeEntries.CreateAsync(entry).ConfigureAwait(false);

                    MergeEntry? readBack = await testDb.Driver.MergeEntries.ReadAsync(entry.Id).ConfigureAwait(false);
                    AssertNotNull(readBack);
                    AssertNull(readBack!.PrUrl, "Unset PrUrl should round-trip as null");
                    AssertNull(readBack.PrBaseBranch, "Unset PrBaseBranch should round-trip as null");
                }
            });

            await RunTest("MergeEntry_UpdatePrFields_Persists", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                        new Vessel("pr-vessel-update", "https://github.com/test/repo.git")).ConfigureAwait(false);

                    MergeEntry entry = new MergeEntry();
                    entry.VesselId = vessel.Id;
                    entry.BranchName = "armada/captain/feat-z";
                    entry.TargetBranch = "main";
                    entry.Status = MergeStatusEnum.Passed;
                    entry = await testDb.Driver.MergeEntries.CreateAsync(entry).ConfigureAwait(false);

                    // Simulate the PR-fallback gate flipping the entry into PullRequestOpen.
                    entry.Status = MergeStatusEnum.PullRequestOpen;
                    entry.PrUrl = "https://gitlab.com/test/repo/-/merge_requests/7";
                    entry.PrBaseBranch = "armada/upstream-captain/main"; // chained-PR base
                    await testDb.Driver.MergeEntries.UpdateAsync(entry).ConfigureAwait(false);

                    MergeEntry? readBack = await testDb.Driver.MergeEntries.ReadAsync(entry.Id).ConfigureAwait(false);
                    AssertNotNull(readBack);
                    AssertEqual(MergeStatusEnum.PullRequestOpen, readBack!.Status);
                    AssertEqual("https://gitlab.com/test/repo/-/merge_requests/7", readBack.PrUrl);
                    AssertEqual("armada/upstream-captain/main", readBack.PrBaseBranch,
                        "PrBaseBranch must support chained captain-branch bases (not just vessel default)");
                }
            });
        }
    }
}
