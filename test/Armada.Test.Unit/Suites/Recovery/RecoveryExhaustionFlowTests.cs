namespace Armada.Test.Unit.Suites.Recovery
{
    using System.Collections.Generic;
    using System.Threading.Tasks;
    using SyslogLogging;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Recovery;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// Verifies the recovery-exhaustion path: once a mission has burned its
    /// recovery budget, the next failed entry must be marked
    /// <c>recovery_exhausted</c> and the merge-queue PR-fallback recovery hook
    /// must be invoked so a real PR is opened for human review.
    /// </summary>
    public class RecoveryExhaustionFlowTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Recovery Exhaustion Flow";

        /// <summary>Run all cases.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("Recovery_ExhaustsBudget_SurfacesWithRecoveryExhaustedTrigger", async () =>
            {
                using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = NewQuietLogging();
                    ArmadaSettings settings = new ArmadaSettings { MaxRecoveryAttempts = 2 };

                    Mission mission = new Mission("exhausted-mission", "body")
                    {
                        BranchName = "captain/exhausted",
                        RecoveryAttempts = 2 // already at the cap
                    };
                    await db.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                    MergeEntry entry = new MergeEntry("captain/exhausted", "main")
                    {
                        MissionId = mission.Id,
                        Status = MergeStatusEnum.Failed,
                        MergeFailureClass = MergeFailureClassEnum.TextConflict,
                        MergeFailureSummary = "exhausted",
                        ConflictedFiles = "[\"src/A.cs\"]",
                        DiffLineCount = 10
                    };
                    await db.Driver.MergeEntries.CreateAsync(entry).ConfigureAwait(false);

                    MergeRecoveryHandlerRebasePathTests.StubRebaseCaptainDockSetup setup = new MergeRecoveryHandlerRebasePathTests.StubRebaseCaptainDockSetup();
                    MergeRecoveryHandlerRebasePathTests.StubMergeQueueServiceForRecovery mergeQueue = new MergeRecoveryHandlerRebasePathTests.StubMergeQueueServiceForRecovery();
                    IRecoveryRouter router = new RecoveryRouter(2);
                    MergeRecoveryHandler handler = new MergeRecoveryHandler(logging, db.Driver, settings, router, setup, mergeQueue, new PlaybookService(db.Driver, logging));

                    await handler.OnMergeFailedAsync(entry.Id).ConfigureAwait(false);

                    MergeEntry? read = await db.Driver.MergeEntries.ReadAsync(entry.Id).ConfigureAwait(false);
                    AssertEqual("recovery_exhausted", read!.AuditCriticalTrigger ?? "", "trigger should mark recovery_exhausted");
                    AssertEqual(0, setup.BuildCalls, "exhausted entries must NOT call dock-setup -- straight to surface");
                }
            });

            await RunTest("Recovery_RecoveryExhausted_TriggersExistingPRFallback", async () =>
            {
                using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = NewQuietLogging();
                    ArmadaSettings settings = new ArmadaSettings { MaxRecoveryAttempts = 2 };

                    // Vessel record is required because the PR-fallback path reads vessel.RepoUrl
                    // for platform detection and vessel.WorkingDirectory for the PR CLI working dir.
                    Vessel vessel = new Vessel("exhausted-vessel", "https://github.com/test/repo.git");
                    vessel.LocalPath = "/tmp/repo";
                    vessel.WorkingDirectory = "/tmp/repo";
                    vessel.DefaultBranch = "main";
                    await db.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Mission mission = new Mission("exhausted-mission-2", "body")
                    {
                        BranchName = "captain/exhausted2",
                        VesselId = vessel.Id,
                        RecoveryAttempts = 2
                    };
                    await db.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                    MergeEntry entry = new MergeEntry("captain/exhausted2", "main")
                    {
                        VesselId = vessel.Id,
                        MissionId = mission.Id,
                        Status = MergeStatusEnum.Failed,
                        MergeFailureClass = MergeFailureClassEnum.TextConflict,
                        MergeFailureSummary = "exhausted-2",
                        ConflictedFiles = "[]",
                        DiffLineCount = 5
                    };
                    await db.Driver.MergeEntries.CreateAsync(entry).ConfigureAwait(false);

                    // Real merge-queue service with a recording PR factory: when
                    // TryOpenPullRequestForRecoveryAsync fires, it calls the factory and
                    // we capture the title/body for assertion.
                    StubGitService git = new StubGitService();
                    RecordingPullRequestService recordingPrService = new RecordingPullRequestService();
                    Func<PullRequestPlatform, string, IPullRequestService> prFactory =
                        (platform, workingDir) => recordingPrService;

                    MergeQueueService mergeQueue = new MergeQueueService(
                        logging, db.Driver, settings, git, new MergeFailureClassifier(), prFactory);

                    IRecoveryRouter router = new RecoveryRouter(2);
                    IRebaseCaptainDockSetup dockSetup = new RebaseCaptainDockSetup(git, db.Driver, logging);
                    MergeRecoveryHandler handler = new MergeRecoveryHandler(
                        logging, db.Driver, settings, router, dockSetup, mergeQueue,
                        new PlaybookService(db.Driver, logging));

                    await handler.OnMergeFailedAsync(entry.Id).ConfigureAwait(false);

                    AssertEqual(1, recordingPrService.OpenedPrs.Count, "PR-fallback should open exactly one PR for the recovery-exhausted entry");

                    RecordingPullRequestService.OpenedPr opened = recordingPrService.OpenedPrs[0];
                    AssertEqual("captain/exhausted2", opened.Branch, "PR head branch should be the captain branch from the failed entry");
                    AssertEqual("main", opened.BaseBranch, "PR base branch should match the failed entry's target branch");
                    AssertContains("recovery_exhausted", opened.Body, "PR body must surface the recovery_exhausted reason so reviewers see why this surfaced");

                    MergeEntry? read = await db.Driver.MergeEntries.ReadAsync(entry.Id).ConfigureAwait(false);
                    AssertEqual(MergeStatusEnum.PullRequestOpen, read!.Status, "entry should transition to PullRequestOpen after PR creation");
                }
            });
        }

        private static LoggingModule NewQuietLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }
    }
}
