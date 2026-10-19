namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.IO;
    using System.Linq;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// The history timeline lists every mission without loading its diff snapshot, agent output or description:
    /// a server's missions hold gigabytes of captured diffs, and a timeline read that loads them all exhausts the
    /// admiral's memory.
    /// </summary>
    public class HistoricalTimelineMemoryTests : TestSuite
    {
        private const int _MissionCount = 12;
        private const int _HeavyChars = 2_000_000;

        /// <summary>Suite name.</summary>
        public override string Name => "Historical Timeline Memory";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("The history timeline lists every mission without loading its diff snapshot or agent output", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(new Vessel
                    {
                        Name = "History Memory Vessel",
                        RepoUrl = "file:///tmp/armada-history-memory.git",
                        LocalPath = Path.GetTempPath(),
                        WorkingDirectory = Path.GetTempPath(),
                        DefaultBranch = "main"
                    }).ConfigureAwait(false);

                    string heavy = new string('d', _HeavyChars);
                    for (int i = 0; i < _MissionCount; i++)
                    {
                        await testDb.Driver.Missions.CreateAsync(new Mission
                        {
                            VesselId = vessel.Id,
                            Title = "Heavy mission " + i,
                            Description = "brief",
                            DiffSnapshot = heavy,
                            AgentOutput = heavy,
                            FailureReason = "short reason",
                            Status = MissionStatusEnum.Failed
                        }).ConfigureAwait(false);
                    }

                    HistoricalTimelineService history = new HistoricalTimelineService(testDb.Driver);
                    AuthContext admin = AuthContext.Authenticated("default", "default", true, true, "UnitTest");
                    HistoricalTimelineQuery query = new HistoricalTimelineQuery { PageNumber = 1, PageSize = 250 };

                    // The diffs and outputs total 2 * _MissionCount * _HeavyChars characters, two bytes each as
                    // strings. A read that loads them allocates at least that; one that never selects them stays
                    // far under a quarter of it.
                    long budget = 2L * _MissionCount * _HeavyChars * 2 / 4;
                    await history.EnumerateAsync(admin, query).ConfigureAwait(false);
                    long before = GC.GetTotalAllocatedBytes(true);
                    HistoricalTimelineResult result = await history.EnumerateAsync(admin, query).ConfigureAwait(false);
                    long allocated = GC.GetTotalAllocatedBytes(true) - before;

                    AssertTrue(allocated < budget, "the timeline allocated " + allocated + " bytes; a read that never loads diffs or output stays under " + budget);
                    AssertEqual(_MissionCount, result.Objects.Count(entry => entry.SourceType == "Mission"), "every mission is still listed");
                    AssertTrue(result.Objects.Where(entry => entry.SourceType == "Mission").All(entry => entry.Description == "short reason"),
                        "each mission entry carries its failure reason");
                    AssertEqual(0, result.TruncatedSources.Count, "no source is over the default limit");
                }
            }).ConfigureAwait(false);
        }
    }
}
