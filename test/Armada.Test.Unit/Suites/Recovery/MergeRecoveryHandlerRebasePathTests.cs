namespace Armada.Test.Unit.Suites.Recovery
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using SyslogLogging;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Recovery;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// Tests covering the RebaseCaptain branch of <see cref="MergeRecoveryHandler"/>:
    /// new mission creation, source-entry cancellation, recovery-budget bookkeeping,
    /// and surface fallback when dock setup throws.
    /// </summary>
    public class MergeRecoveryHandlerRebasePathTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Merge Recovery Handler Rebase Path";

        /// <summary>Run all cases.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("OnMergeFailed_RouterReturnsRebaseCaptain_CreatesNewMissionAndCancelsEntry", async () =>
            {
                using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = NewQuietLogging();
                    ArmadaSettings settings = new ArmadaSettings { MaxRecoveryAttempts = 3 };

                    SetupResult setupResult = await SetupFailedMissionEntryAsync(db, classification: MergeFailureClassEnum.TestFailureAfterMerge).ConfigureAwait(false);
                    StubRebaseCaptainDockSetup dockSetup = new StubRebaseCaptainDockSetup();
                    StubMergeQueueServiceForRecovery mergeQueue = new StubMergeQueueServiceForRecovery();
                    IRecoveryRouter router = new RecoveryRouter(3);

                    MergeRecoveryHandler handler = new MergeRecoveryHandler(logging, db.Driver, settings, router, dockSetup, mergeQueue, new PlaybookService(db.Driver, logging));
                    await handler.OnMergeFailedAsync(setupResult.Entry.Id).ConfigureAwait(false);

                    MergeEntry? readEntry = await db.Driver.MergeEntries.ReadAsync(setupResult.Entry.Id).ConfigureAwait(false);
                    AssertEqual(MergeStatusEnum.Cancelled, readEntry!.Status, "failed entry should transition to Cancelled");
                    AssertEqual("recovery_rebased", readEntry.TestOutput ?? "", "entry note should mark recovery_rebased");

                    Mission? readMission = await db.Driver.Missions.ReadAsync(setupResult.Mission.Id).ConfigureAwait(false);
                    AssertEqual(1, readMission!.RecoveryAttempts, "failed mission's recovery counter should increment");
                    AssertNotNull(readMission.LastRecoveryActionUtc, "LastRecoveryActionUtc should be stamped");

                    // The new rebase mission should exist as a separate row.
                    List<Mission> all = await db.Driver.Missions.EnumerateAsync().ConfigureAwait(false);
                    Mission? rebaseMission = all.FirstOrDefault(m => m.ParentMissionId == setupResult.Mission.Id);
                    AssertNotNull(rebaseMission, "new rebase mission should be created with ParentMissionId pointing at the failed mission");
                    AssertEqual(0, rebaseMission!.RecoveryAttempts, "rebase mission starts at 0 recovery attempts (own budget)");
                    AssertEqual(setupResult.CaptainBranch, rebaseMission.BranchName, "rebase mission should land on the captain branch");
                    AssertEqual("claude-opus-4-7", rebaseMission.PreferredModel ?? "", "rebase mission should be pinned to the high-tier model");
                    // The dock-setup stub is the single source of truth for the inline
                    // playbook delivery; SelectedPlaybooks is request-time metadata that
                    // does not round-trip through the missions table.
                    AssertEqual(1, dockSetup.BuildCalls, "dock setup should be invoked exactly once");
                }
            });

            await RunTest("OnMergeFailed_DockSetupThrows_SurfacesAsRecoveryUnstartable", async () =>
            {
                using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = NewQuietLogging();
                    ArmadaSettings settings = new ArmadaSettings { MaxRecoveryAttempts = 3 };

                    SetupResult setupResult = await SetupFailedMissionEntryAsync(db, classification: MergeFailureClassEnum.TestFailureAfterMerge).ConfigureAwait(false);
                    StubRebaseCaptainDockSetup dockSetup = new StubRebaseCaptainDockSetup { ThrowOnBuild = true };
                    StubMergeQueueServiceForRecovery mergeQueue = new StubMergeQueueServiceForRecovery();
                    IRecoveryRouter router = new RecoveryRouter(3);

                    MergeRecoveryHandler handler = new MergeRecoveryHandler(logging, db.Driver, settings, router, dockSetup, mergeQueue, new PlaybookService(db.Driver, logging));
                    await handler.OnMergeFailedAsync(setupResult.Entry.Id).ConfigureAwait(false);

                    MergeEntry? readEntry = await db.Driver.MergeEntries.ReadAsync(setupResult.Entry.Id).ConfigureAwait(false);
                    AssertEqual("recovery_unstartable", readEntry!.AuditCriticalTrigger ?? "", "entry should surface as recovery_unstartable");

                    Mission? readMission = await db.Driver.Missions.ReadAsync(setupResult.Mission.Id).ConfigureAwait(false);
                    AssertEqual(1, readMission!.RecoveryAttempts, "counter is incremented even when build throws -- prevents burning another recovery slot");
                }
            });

            await RunTest("OnMergeFailed_RebaseMission_HasRecoveryAttemptsZero", async () =>
            {
                using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = NewQuietLogging();
                    ArmadaSettings settings = new ArmadaSettings { MaxRecoveryAttempts = 3 };

                    // Failed mission has already burned 1 attempt -- the rebase mission must not inherit it.
                    SetupResult setupResult = await SetupFailedMissionEntryAsync(db, classification: MergeFailureClassEnum.TestFailureAfterMerge, priorAttempts: 1).ConfigureAwait(false);
                    StubRebaseCaptainDockSetup dockSetup = new StubRebaseCaptainDockSetup();
                    StubMergeQueueServiceForRecovery mergeQueue = new StubMergeQueueServiceForRecovery();
                    IRecoveryRouter router = new RecoveryRouter(3);

                    MergeRecoveryHandler handler = new MergeRecoveryHandler(logging, db.Driver, settings, router, dockSetup, mergeQueue, new PlaybookService(db.Driver, logging));
                    await handler.OnMergeFailedAsync(setupResult.Entry.Id).ConfigureAwait(false);

                    List<Mission> all = await db.Driver.Missions.EnumerateAsync().ConfigureAwait(false);
                    Mission? rebaseMission = all.FirstOrDefault(m => m.ParentMissionId == setupResult.Mission.Id);
                    AssertNotNull(rebaseMission, "rebase mission should be created");
                    AssertEqual(0, rebaseMission!.RecoveryAttempts, "rebase mission must start fresh -- own budget not inherited");
                }
            });
        }

        #region Helpers

        private sealed class SetupResult
        {
            public Mission Mission { get; set; } = null!;
            public MergeEntry Entry { get; set; } = null!;
            public string CaptainBranch { get; set; } = "";
        }

        private static async Task<SetupResult> SetupFailedMissionEntryAsync(TestDatabase db, MergeFailureClassEnum classification, int priorAttempts = 0)
        {
            string captainBranch = "captain/rebase-test";
            Mission mission = new Mission("test mission", "DESC")
            {
                BranchName = captainBranch,
                RecoveryAttempts = priorAttempts
            };
            await db.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

            MergeEntry entry = new MergeEntry(captainBranch, "main")
            {
                MissionId = mission.Id,
                Status = MergeStatusEnum.Failed,
                MergeFailureClass = classification,
                MergeFailureSummary = "synthetic " + classification + " summary",
                ConflictedFiles = "[\"src/Foo.cs\",\"src/Bar.cs\",\"src/Baz.cs\"]",
                DiffLineCount = 200,
                TestOutput = "synthetic test output"
            };
            await db.Driver.MergeEntries.CreateAsync(entry).ConfigureAwait(false);

            return new SetupResult { Mission = mission, Entry = entry, CaptainBranch = captainBranch };
        }

        private static LoggingModule NewQuietLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        #endregion

        #region Test doubles

        internal sealed class StubRebaseCaptainDockSetup : IRebaseCaptainDockSetup
        {
            public bool ThrowOnBuild { get; set; }
            public int BuildCalls { get; private set; }

            public Task<RebaseCaptainMissionSpec> BuildAsync(MergeEntry failedEntry, Mission failedMission, MergeFailureClassification classification, CancellationToken token = default)
            {
                BuildCalls++;
                if (ThrowOnBuild) throw new InvalidOperationException("simulated dock-setup failure");

                List<PrestagedFile> prestaged = new List<PrestagedFile>
                {
                    new PrestagedFile("synth", "_briefing/conflict-state.md")
                };
                List<SelectedPlaybook> playbooks = new List<SelectedPlaybook>
                {
                    new SelectedPlaybook { PlaybookId = "pbk_rebase_captain", DeliveryMode = PlaybookDeliveryModeEnum.InlineFullContent }
                };
                RebaseCaptainMissionSpec spec = new RebaseCaptainMissionSpec(
                    Brief: (failedMission.Description ?? "") + "\n\n## Conflict context appendix\n\nstub",
                    PrestagedFiles: prestaged,
                    PreferredModel: "claude-opus-4-7",
                    LandingTargetBranch: failedMission.BranchName ?? failedEntry.BranchName,
                    SelectedPlaybooks: playbooks,
                    DependsOnMissionId: null,
                    RecoveryAttempts: 0);
                return Task.FromResult(spec);
            }
        }

        internal sealed class StubMergeQueueServiceForRecovery : IMergeQueueService
        {
            public List<string> RecoveryPokeCalls { get; } = new List<string>();
            public bool RecoveryPokeReturn { get; set; } = true;

            public Task<MergeEntry> EnqueueAsync(MergeEntry entry, CancellationToken token = default) => Task.FromResult(entry);
            public Task ProcessQueueAsync(CancellationToken token = default) => Task.CompletedTask;
            public Task CancelAsync(string entryId, string? tenantId = null, CancellationToken token = default) => Task.CompletedTask;
            public Task<List<MergeEntry>> ListAsync(string? tenantId = null, CancellationToken token = default) => Task.FromResult(new List<MergeEntry>());
            public Task<MergeEntry?> ProcessSingleAsync(string entryId, string? tenantId = null, CancellationToken token = default) => Task.FromResult<MergeEntry?>(null);
            public Task ProcessEntryByIdAsync(string entryId, CancellationToken token = default) => Task.CompletedTask;
            public Task<MergeEntry?> GetAsync(string entryId, string? tenantId = null, CancellationToken token = default) => Task.FromResult<MergeEntry?>(null);
            public Task<bool> DeleteAsync(string entryId, string? tenantId = null, CancellationToken token = default) => Task.FromResult(false);
            public Task<MergeQueuePurgeResult> DeleteMultipleAsync(List<string> entryIds, string? tenantId = null, CancellationToken token = default)
                => Task.FromResult(new MergeQueuePurgeResult());
            public Task<int> PurgeTerminalAsync(string? vesselId = null, MergeStatusEnum? status = null, string? tenantId = null, CancellationToken token = default)
                => Task.FromResult(0);
            public Task<int> ReconcilePullRequestEntriesAsync(CancellationToken token = default) => Task.FromResult(0);

            public Task<bool> TryOpenPullRequestForRecoveryAsync(string mergeEntryId, CancellationToken token = default)
            {
                RecoveryPokeCalls.Add(mergeEntryId);
                return Task.FromResult(RecoveryPokeReturn);
            }
        }

        #endregion
    }
}
