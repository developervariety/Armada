namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Threading.Tasks;
    using SyslogLogging;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Recovery;
    using Armada.Core.Services;
    using Armada.Core.Settings;
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

            await RunTest("ReconcilePullRequestEntries_LandsEntriesWhoseMissionIsComplete", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;
                    ArmadaSettings settings = new ArmadaSettings();
                    StubGitService git = new StubGitService();
                    MergeQueueService mergeQueue = new MergeQueueService(logging, testDb.Driver, settings, git, new MergeFailureClassifier());

                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                        new Vessel("pr-reconcile-vessel", "https://github.com/test/repo.git")).ConfigureAwait(false);

                    // Mission already flipped to Complete by the existing PR-mode reconciler
                    // (HandleReconcilePullRequestAsync polled the platform CLI and saw merged).
                    Mission mergedMission = new Mission("merged feature", "");
                    mergedMission.VesselId = vessel.Id;
                    mergedMission.Status = MissionStatusEnum.Complete;
                    mergedMission.PrUrl = "https://github.com/test/repo/pull/100";
                    mergedMission = await testDb.Driver.Missions.CreateAsync(mergedMission).ConfigureAwait(false);

                    // Mission still in PullRequestOpen (PR has not merged yet).
                    Mission openMission = new Mission("still-open feature", "");
                    openMission.VesselId = vessel.Id;
                    openMission.Status = MissionStatusEnum.PullRequestOpen;
                    openMission.PrUrl = "https://github.com/test/repo/pull/101";
                    openMission = await testDb.Driver.Missions.CreateAsync(openMission).ConfigureAwait(false);

                    MergeEntry mergedEntry = new MergeEntry();
                    mergedEntry.VesselId = vessel.Id;
                    mergedEntry.MissionId = mergedMission.Id;
                    mergedEntry.BranchName = "armada/captain/feat-merged";
                    mergedEntry.TargetBranch = "main";
                    mergedEntry.Status = MergeStatusEnum.PullRequestOpen;
                    mergedEntry.PrUrl = mergedMission.PrUrl;
                    mergedEntry = await testDb.Driver.MergeEntries.CreateAsync(mergedEntry).ConfigureAwait(false);

                    MergeEntry openEntry = new MergeEntry();
                    openEntry.VesselId = vessel.Id;
                    openEntry.MissionId = openMission.Id;
                    openEntry.BranchName = "armada/captain/feat-open";
                    openEntry.TargetBranch = "main";
                    openEntry.Status = MergeStatusEnum.PullRequestOpen;
                    openEntry.PrUrl = openMission.PrUrl;
                    openEntry = await testDb.Driver.MergeEntries.CreateAsync(openEntry).ConfigureAwait(false);

                    int reconciled = await mergeQueue.ReconcilePullRequestEntriesAsync().ConfigureAwait(false);
                    AssertEqual(1, reconciled, "Exactly the merged-mission entry should reconcile");

                    MergeEntry? mergedReadBack = await testDb.Driver.MergeEntries.ReadAsync(mergedEntry.Id).ConfigureAwait(false);
                    AssertEqual(MergeStatusEnum.Landed, mergedReadBack!.Status,
                        "Entry whose linked mission is Complete must land");
                    AssertNotNull(mergedReadBack.CompletedUtc, "Landed entry must stamp CompletedUtc");

                    MergeEntry? openReadBack = await testDb.Driver.MergeEntries.ReadAsync(openEntry.Id).ConfigureAwait(false);
                    AssertEqual(MergeStatusEnum.PullRequestOpen, openReadBack!.Status,
                        "Entry whose linked mission is still PullRequestOpen must stay PullRequestOpen");
                }
            });
        }
    }
}
