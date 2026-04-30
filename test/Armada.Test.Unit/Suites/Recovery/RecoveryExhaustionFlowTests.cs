namespace Armada.Test.Unit.Suites.Recovery
{
    using System.Collections.Generic;
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
                    MergeRecoveryHandler handler = new MergeRecoveryHandler(logging, db.Driver, settings, router, setup, mergeQueue);

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

                    Mission mission = new Mission("exhausted-mission-2", "body")
                    {
                        BranchName = "captain/exhausted2",
                        RecoveryAttempts = 2
                    };
                    await db.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                    MergeEntry entry = new MergeEntry("captain/exhausted2", "main")
                    {
                        MissionId = mission.Id,
                        Status = MergeStatusEnum.Failed,
                        MergeFailureClass = MergeFailureClassEnum.TextConflict,
                        MergeFailureSummary = "exhausted-2",
                        ConflictedFiles = "[]",
                        DiffLineCount = 5
                    };
                    await db.Driver.MergeEntries.CreateAsync(entry).ConfigureAwait(false);

                    MergeRecoveryHandlerRebasePathTests.StubRebaseCaptainDockSetup setup = new MergeRecoveryHandlerRebasePathTests.StubRebaseCaptainDockSetup();
                    MergeRecoveryHandlerRebasePathTests.StubMergeQueueServiceForRecovery mergeQueue = new MergeRecoveryHandlerRebasePathTests.StubMergeQueueServiceForRecovery
                    {
                        RecoveryPokeReturn = true
                    };
                    IRecoveryRouter router = new RecoveryRouter(2);
                    MergeRecoveryHandler handler = new MergeRecoveryHandler(logging, db.Driver, settings, router, setup, mergeQueue);

                    await handler.OnMergeFailedAsync(entry.Id).ConfigureAwait(false);

                    AssertEqual(1, mergeQueue.RecoveryPokeCalls.Count, "PR-fallback recovery hook should be called exactly once");
                    AssertEqual(entry.Id, mergeQueue.RecoveryPokeCalls[0], "PR-fallback hook should receive the failed entry id");
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
