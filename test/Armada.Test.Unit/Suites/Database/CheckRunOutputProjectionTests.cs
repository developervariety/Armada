namespace Armada.Test.Unit.Suites.Database
{
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// A Check's Output is a whole build or test log. Readers that scan many Checks for their status, summary or
    /// links never load it: thousands of multi-megabyte logs held as strings at once exhaust the admiral's memory.
    /// </summary>
    public class CheckRunOutputProjectionTests : TestSuite
    {
        private const int _RunCount = 12;
        private const int _OutputChars = 2_000_000;

        public override string Name => "Check Run Output Projection";

        protected override async Task RunTestsAsync()
        {
            await RunTest("The history timeline lists every Check without loading its log", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    await SeedRunsWithLargeOutputAsync(testDb).ConfigureAwait(false);
                    HistoricalTimelineService history = new HistoricalTimelineService(testDb.Driver);
                    AuthContext admin = AuthContext.Authenticated("default", "default", true, true, "UnitTest");

                    HistoricalTimelineResult result = await AssertAllocatesUnderAsync(
                        () => history.EnumerateAsync(admin, new HistoricalTimelineQuery { PageNumber = 1, PageSize = 250 }),
                        "the default history timeline").ConfigureAwait(false);
                    AssertEqual(_RunCount, result.Objects.Count(entry => entry.SourceType == "CheckRun"), "every Check is still listed");
                    AssertTrue(result.Objects.Where(entry => entry.SourceType == "CheckRun").All(entry => entry.Description == "summary line"),
                        "each entry keeps the Check's summary");
                }
            }).ConfigureAwait(false);

            await RunTest("The production summary reads every Check without loading its log", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    await SeedRunsWithLargeOutputAsync(testDb).ConfigureAwait(false);
                    VerifiedProductionSummaryService production = new VerifiedProductionSummaryService(testDb.Driver);
                    AuthContext admin = AuthContext.Authenticated("default", "default", true, true, "UnitTest");
                    DateTime now = DateTime.UtcNow;

                    await AssertAllocatesUnderAsync(
                        () => production.SummarizeAsync(admin, new ProductionSummaryQuery { FromUtc = now.AddDays(-7), ToUtc = now.AddDays(1) }),
                        "the production summary").ConfigureAwait(false);
                }
            }).ConfigureAwait(false);

            await RunTest("The production summary reads only the Checks inside its window and evidence lookback", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    DateTime toUtc = DateTime.UtcNow.Date;
                    DateTime fromUtc = toUtc.AddDays(-7);
                    // The summary reads Checks created from 90 days before the window's start, the same lookback as
                    // its other evidence; older Checks cannot be evidence for a slice that completes in the window.
                    await CreateRunAtAsync(testDb, fromUtc.AddDays(-200)).ConfigureAwait(false);
                    await CreateRunAtAsync(testDb, fromUtc.AddDays(-120)).ConfigureAwait(false);
                    await CreateRunAtAsync(testDb, fromUtc.AddDays(-91)).ConfigureAwait(false);
                    await CreateRunAtAsync(testDb, fromUtc.AddDays(-30)).ConfigureAwait(false);
                    await CreateRunAtAsync(testDb, fromUtc.AddDays(1)).ConfigureAwait(false);
                    await CreateRunAtAsync(testDb, fromUtc.AddDays(2)).ConfigureAwait(false);
                    VerifiedProductionSummaryService production = new VerifiedProductionSummaryService(testDb.Driver);
                    AuthContext admin = AuthContext.Authenticated("default", "default", true, true, "UnitTest");

                    ProductionSummaryResult result = await production.SummarizeAsync(admin, new ProductionSummaryQuery { FromUtc = fromUtc, ToUtc = toUtc }).ConfigureAwait(false);
                    ProductionSourceScan checks = result.Scan.Sources.Single(source => source.Source == "checks");
                    AssertEqual(3, checks.RecordsRead, "only the Checks inside the lookback are read");
                    AssertEqual(3L, checks.TotalRecords, "the database counts only the Checks inside the lookback");
                }
            }).ConfigureAwait(false);

            await RunTest("A page read without Output leaves the stored log intact and cannot be written back", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    CheckRun stored = await testDb.Driver.CheckRuns.CreateAsync(new CheckRun
                    {
                        Type = CheckRunTypeEnum.UnitTest,
                        Status = CheckRunStatusEnum.Passed,
                        Output = "the whole log",
                        Summary = "summary line",
                        ExitCode = 0
                    }).ConfigureAwait(false);

                    EnumerationResult<CheckRun> light = await testDb.Driver.CheckRuns.EnumerateAsync(
                        new CheckRunQuery { IncludeOutput = false }).ConfigureAwait(false);
                    CheckRun read = light.Objects.Single();
                    AssertNull(read.Output, "the page carries no Output");
                    AssertEqual("the whole log".Length, read.OutputLength ?? -1, "the page carries the withheld log's length, computed by the database");
                    AssertEqual("summary line", read.Summary, "every other field is read");
                    AssertEqual(CheckRunStatusEnum.Passed, read.Status);
                    AssertEqual(0, read.ExitCode);

                    read.Summary = "changed";
                    bool refused = false;
                    try
                    {
                        await testDb.Driver.CheckRuns.UpdateAsync(read).ConfigureAwait(false);
                    }
                    catch (InvalidOperationException)
                    {
                        refused = true;
                    }
                    AssertTrue(refused, "a record read without Output is refused as an update, which would erase the stored log");

                    CheckRun? full = await testDb.Driver.CheckRuns.ReadAsync(stored.Id).ConfigureAwait(false);
                    AssertEqual("the whole log", full!.Output, "the stored log is unchanged");
                    AssertEqual("summary line", full.Summary, "the refused update wrote nothing");

                    EnumerationResult<CheckRun> whole = await testDb.Driver.CheckRuns.EnumerateAsync(new CheckRunQuery()).ConfigureAwait(false);
                    AssertEqual("the whole log", whole.Objects.Single().Output, "a default page still carries Output");
                    AssertNull(whole.Objects.Single().OutputLength, "a page that carries Output reports no withheld length");
                }
            }).ConfigureAwait(false);
        }

        private static async Task SeedRunsWithLargeOutputAsync(TestDatabase testDb)
        {
            string log = new string('x', _OutputChars);
            for (int i = 0; i < _RunCount; i++)
            {
                await testDb.Driver.CheckRuns.CreateAsync(new CheckRun
                {
                    Type = CheckRunTypeEnum.UnitTest,
                    Status = CheckRunStatusEnum.Passed,
                    Output = log,
                    Summary = "summary line",
                    CompletedUtc = DateTime.UtcNow
                }).ConfigureAwait(false);
            }
        }

        private static async Task CreateRunAtAsync(TestDatabase testDb, DateTime createdUtc)
        {
            await testDb.Driver.CheckRuns.CreateAsync(new CheckRun
            {
                Type = CheckRunTypeEnum.UnitTest,
                Status = CheckRunStatusEnum.Passed,
                Summary = "summary line",
                CreatedUtc = createdUtc
            }).ConfigureAwait(false);
        }

        private async Task<T> AssertAllocatesUnderAsync<T>(Func<Task<T>> read, string label)
        {
            // The logs total _RunCount * _OutputChars characters, two bytes each as strings. A reader that loads them
            // allocates at least that; one that never selects the column stays far under a quarter of it.
            long budget = (long)_RunCount * _OutputChars * 2 / 4;
            await read().ConfigureAwait(false);
            long before = GC.GetTotalAllocatedBytes(true);
            T result = await read().ConfigureAwait(false);
            long allocated = GC.GetTotalAllocatedBytes(true) - before;
            AssertTrue(allocated < budget, label + " allocated " + allocated + " bytes; a read that never loads Check logs stays under " + budget);
            return result;
        }
    }
}
