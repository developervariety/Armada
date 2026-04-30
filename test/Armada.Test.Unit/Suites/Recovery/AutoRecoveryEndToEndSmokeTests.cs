namespace Armada.Test.Unit.Suites.Recovery
{
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading.Tasks;
    using SyslogLogging;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Recovery;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// End-to-end smoke test for the auto-recovery loop: classify -&gt; route -&gt; act
    /// -&gt; mission re-runs and second attempt lands without orchestrator
    /// intervention. Uses a real classifier, real router, and real handler with
    /// hand-rolled merge-queue stubs so the test focuses on the recovery
    /// interaction surface (not git plumbing). The "second attempt lands clean"
    /// step models orchestrator behavior: after the redispatched mission
    /// produces a clean re-run, the entry transitions to Landed and a follow-on
    /// <c>OnMergeFailedAsync</c> is a no-op.
    /// </summary>
    public class AutoRecoveryEndToEndSmokeTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Auto Recovery End To End Smoke";

        /// <summary>Run all cases.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("EndToEnd_DuplicateEnumEntryConflict_AutoRedispatchLandsCleanOnSecondAttempt", async () =>
            {
                using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = NewQuietLogging();
                    ArmadaSettings settings = new ArmadaSettings { MaxRecoveryAttempts = 3 };

                    Mission mission = new Mission("duplicate-enum-mission", "Add new enum value")
                    {
                        BranchName = "captain/dup-enum",
                        Status = MissionStatusEnum.Complete
                    };
                    await db.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                    // First attempt: synthetic duplicate-enum text conflict against main.
                    // Trivial: 1 file, small diff -> router returns Redispatch.
                    MergeEntry firstEntry = new MergeEntry("captain/dup-enum", "main")
                    {
                        MissionId = mission.Id,
                        Status = MergeStatusEnum.Failed,
                        MergeFailureClass = MergeFailureClassEnum.TextConflict,
                        MergeFailureSummary = "Text conflict in 1 file",
                        ConflictedFiles = "[\"src/Enums/MyEnum.cs\"]",
                        DiffLineCount = 12
                    };
                    await db.Driver.MergeEntries.CreateAsync(firstEntry).ConfigureAwait(false);

                    // Verify classifier produces TextConflict + trivial-eligible shape from
                    // the same context the merge-queue would have captured at fail-time.
                    MergeFailureClassifier classifier = new MergeFailureClassifier();
                    MergeFailureContext context = new MergeFailureContext
                    {
                        GitExitCode = 1,
                        GitStandardOutput = "Auto-merging src/Enums/MyEnum.cs\nCONFLICT (content): Merge conflict in src/Enums/MyEnum.cs",
                        GitStandardError = "Automatic merge failed; fix conflicts and then commit the result.",
                        ConflictedFiles = new List<string> { "src/Enums/MyEnum.cs" },
                        DiffLineCount = 12
                    };
                    MergeFailureClassification cls = classifier.Classify(context);
                    AssertEqual(MergeFailureClassEnum.TextConflict, cls.FailureClass, "classifier should label duplicate-enum collision as TextConflict");

                    MergeRecoveryHandlerRebasePathTests.StubRebaseCaptainDockSetup setup = new MergeRecoveryHandlerRebasePathTests.StubRebaseCaptainDockSetup();
                    MergeRecoveryHandlerRebasePathTests.StubMergeQueueServiceForRecovery mergeQueue = new MergeRecoveryHandlerRebasePathTests.StubMergeQueueServiceForRecovery();
                    IRecoveryRouter router = new RecoveryRouter(3);
                    MergeRecoveryHandler handler = new MergeRecoveryHandler(logging, db.Driver, settings, router, setup, mergeQueue);

                    // Stage 1: fail-time triggers OnMergeFailed -> Redispatch path.
                    await handler.OnMergeFailedAsync(firstEntry.Id).ConfigureAwait(false);

                    Mission? readMission = await db.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertEqual(MissionStatusEnum.Pending, readMission!.Status, "mission should be redispatched (back to Pending)");
                    AssertEqual(1, readMission.RecoveryAttempts, "first failure should burn 1 recovery attempt");

                    MergeEntry? readEntry = await db.Driver.MergeEntries.ReadAsync(firstEntry.Id).ConfigureAwait(false);
                    AssertEqual(MergeStatusEnum.Cancelled, readEntry!.Status, "first attempt entry should be cancelled");
                    AssertEqual(0, setup.BuildCalls, "redispatch path must not invoke dock-setup");

                    // Orchestrator phase: after redispatch the mission re-runs cleanly. We
                    // model that by force-pushing a clean captain branch and a fresh merge
                    // entry that succeeds.
                    MergeEntry secondEntry = new MergeEntry("captain/dup-enum", "main")
                    {
                        MissionId = mission.Id,
                        Status = MergeStatusEnum.Landed,
                        TestExitCode = 0
                    };
                    await db.Driver.MergeEntries.CreateAsync(secondEntry).ConfigureAwait(false);

                    // Stage 2: a follow-on OnMergeFailed for the SECOND entry must be a
                    // no-op because the entry is Landed, not Failed -- no further recovery.
                    await handler.OnMergeFailedAsync(secondEntry.Id).ConfigureAwait(false);

                    MergeEntry? readSecond = await db.Driver.MergeEntries.ReadAsync(secondEntry.Id).ConfigureAwait(false);
                    AssertEqual(MergeStatusEnum.Landed, readSecond!.Status, "second attempt entry should remain Landed");

                    Mission? finalMission = await db.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertEqual(1, finalMission!.RecoveryAttempts, "second attempt must NOT burn an additional recovery attempt -- it landed");

                    // Verify only one cancelled entry and one landed entry exist for this mission.
                    List<MergeEntry> entries = await db.Driver.MergeEntries.EnumerateAsync().ConfigureAwait(false);
                    int cancelled = entries.Count(e => e.MissionId == mission.Id && e.Status == MergeStatusEnum.Cancelled);
                    int landed = entries.Count(e => e.MissionId == mission.Id && e.Status == MergeStatusEnum.Landed);
                    AssertEqual(1, cancelled, "exactly one cancelled entry from the recovered first attempt");
                    AssertEqual(1, landed, "exactly one landed entry from the clean second attempt");
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
