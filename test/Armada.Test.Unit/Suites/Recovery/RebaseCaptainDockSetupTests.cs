namespace Armada.Test.Unit.Suites.Recovery
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading.Tasks;
    using SyslogLogging;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Recovery;
    using Armada.Core.Services.Interfaces;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// Unit tests for <see cref="RebaseCaptainDockSetup"/>: confirms the
    /// rebase-captain mission spec is built correctly from the failed merge entry,
    /// failed mission, and classification trio. Uses hand-rolled doubles only.
    /// </summary>
    public class RebaseCaptainDockSetupTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Rebase Captain Dock Setup";

        /// <summary>Run all cases.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("Build_BriefIncludesOriginalAndConflictAppendix", async () =>
            {
                using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = NewQuietLogging();
                    RebaseCaptainDockSetup setup = new RebaseCaptainDockSetup(new StubGitService(), db.Driver, logging);

                    Mission failedMission = new Mission("Original Title", "ORIGINAL_BRIEF_BODY");
                    failedMission.BranchName = "captain/abc";
                    MergeEntry failedEntry = NewFailedEntry(missionId: failedMission.Id, branch: "captain/abc");
                    failedEntry.TestOutput = "line1\nline2\nFATAL: conflict in src/Foo.cs";
                    MergeFailureClassification classification = new MergeFailureClassification(
                        MergeFailureClassEnum.TextConflict,
                        "Text conflict in 1 file",
                        new List<string> { "src/Foo.cs" });

                    RebaseCaptainMissionSpec spec = await setup.BuildAsync(failedEntry, failedMission, classification).ConfigureAwait(false);

                    AssertContains("ORIGINAL_BRIEF_BODY", spec.Brief, "brief should include the original mission description verbatim");
                    AssertContains("Conflict context appendix", spec.Brief, "brief should include the conflict-context appendix header");
                    AssertContains("TextConflict", spec.Brief, "brief should mention the failure class");
                    AssertContains("src/Foo.cs", spec.Brief, "brief should include conflicted-file list");
                    AssertContains("FATAL: conflict in src/Foo.cs", spec.Brief, "brief should include a tail of the failed test output");
                }
            });

            await RunTest("Build_PrestagedFilesIncludesConflictStateMarker", async () =>
            {
                using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = NewQuietLogging();
                    RebaseCaptainDockSetup setup = new RebaseCaptainDockSetup(new StubGitService(), db.Driver, logging);

                    Mission failedMission = new Mission("title", "body");
                    failedMission.BranchName = "captain/xyz";
                    MergeEntry entry = NewFailedEntry(failedMission.Id, "captain/xyz");
                    MergeFailureClassification cls = new MergeFailureClassification(MergeFailureClassEnum.TextConflict, "x", new List<string>());

                    RebaseCaptainMissionSpec spec = await setup.BuildAsync(entry, failedMission, cls).ConfigureAwait(false);

                    bool hasMarker = spec.PrestagedFiles.Any(f => f.DestPath == RebaseCaptainDockSetup.ConflictStateMarkerRelativePath);
                    AssertTrue(hasMarker, "prestaged files should include the synthesized _briefing/conflict-state.md marker");
                }
            });

            await RunTest("Build_SelectedPlaybooksIncludesPbkRebaseCaptain_InlineMode", async () =>
            {
                using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = NewQuietLogging();
                    RebaseCaptainDockSetup setup = new RebaseCaptainDockSetup(new StubGitService(), db.Driver, logging);

                    Mission failedMission = new Mission("title", "body");
                    failedMission.BranchName = "captain/xyz";
                    MergeEntry entry = NewFailedEntry(failedMission.Id, "captain/xyz");
                    MergeFailureClassification cls = new MergeFailureClassification(MergeFailureClassEnum.TextConflict, "x", new List<string>());

                    RebaseCaptainMissionSpec spec = await setup.BuildAsync(entry, failedMission, cls).ConfigureAwait(false);

                    AssertEqual(1, spec.SelectedPlaybooks.Count, "exactly one playbook expected");
                    AssertEqual(RebaseCaptainDockSetup.RebaseCaptainPlaybookId, spec.SelectedPlaybooks[0].PlaybookId, "playbook id should be pbk_rebase_captain");
                    AssertEqual(PlaybookDeliveryModeEnum.InlineFullContent, spec.SelectedPlaybooks[0].DeliveryMode, "delivery mode should be InlineFullContent");
                    AssertEqual(RebaseCaptainPlaybookContent.Markdown, spec.SelectedPlaybooks[0].InlineFullContent ?? "",
                        "InlineFullContent should be populated with the compile-time playbook body so the dispatched mission ships it without a DB lookup");
                }
            });

            await RunTest("Build_LandingTargetBranchEqualsFailedMissionCaptainBranch", async () =>
            {
                using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = NewQuietLogging();
                    RebaseCaptainDockSetup setup = new RebaseCaptainDockSetup(new StubGitService(), db.Driver, logging);

                    Mission failedMission = new Mission("title", "body");
                    failedMission.BranchName = "captain/recover-me";
                    MergeEntry entry = NewFailedEntry(failedMission.Id, "captain/recover-me");
                    MergeFailureClassification cls = new MergeFailureClassification(MergeFailureClassEnum.TextConflict, "x", new List<string>());

                    RebaseCaptainMissionSpec spec = await setup.BuildAsync(entry, failedMission, cls).ConfigureAwait(false);

                    AssertEqual("captain/recover-me", spec.LandingTargetBranch, "resolution must land on the failed mission's captain branch");
                    AssertEqual(0, spec.RecoveryAttempts, "rebase mission starts with its own recovery budget at 0");
                }
            });

            await RunTest("Build_PreferredModelIsClaudeOpus47", async () =>
            {
                using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = NewQuietLogging();
                    RebaseCaptainDockSetup setup = new RebaseCaptainDockSetup(new StubGitService(), db.Driver, logging);

                    Mission failedMission = new Mission("title", "body");
                    failedMission.BranchName = "captain/xyz";
                    MergeEntry entry = NewFailedEntry(failedMission.Id, "captain/xyz");
                    MergeFailureClassification cls = new MergeFailureClassification(MergeFailureClassEnum.TextConflict, "x", new List<string>());

                    RebaseCaptainMissionSpec spec = await setup.BuildAsync(entry, failedMission, cls).ConfigureAwait(false);

                    AssertEqual(RebaseCaptainDockSetup.PreferredModelClaudeOpus47, spec.PreferredModel, "preferred model should be claude-opus-4-7");
                }
            });

            await RunTest("Build_ConflictStateEntry_IsContentBased_NoSyntheticToken", async () =>
            {
                using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = NewQuietLogging();
                    RebaseCaptainDockSetup setup = new RebaseCaptainDockSetup(new StubGitService(), db.Driver, logging);

                    Mission failedMission = new Mission("title", "body");
                    failedMission.BranchName = "captain/rescue-me";
                    MergeEntry entry = NewFailedEntry(failedMission.Id, "captain/rescue-me");
                    MergeFailureClassification cls = new MergeFailureClassification(
                        MergeFailureClassEnum.TextConflict, "summary", new List<string> { "src/Foo.cs" });

                    RebaseCaptainMissionSpec spec = await setup.BuildAsync(entry, failedMission, cls).ConfigureAwait(false);

                    PrestagedFile? marker = spec.PrestagedFiles
                        .FirstOrDefault(f => f.DestPath == RebaseCaptainDockSetup.ConflictStateMarkerRelativePath);
                    AssertNotNull(marker, "conflict-state.md entry must be present");
                    AssertNotNull(marker!.Content, "conflict-state.md entry must be content-based (Content != null)");
                    AssertFalse(marker.Content!.Contains("<conflict-state-synthesized"),
                        "Content must not contain the old synthetic token");
                }
            });

            await RunTest("Build_ConflictStateEntry_ContentIncludesBranchAndConflictedFiles", async () =>
            {
                using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = NewQuietLogging();
                    RebaseCaptainDockSetup setup = new RebaseCaptainDockSetup(new StubGitService(), db.Driver, logging);

                    Mission failedMission = new Mission("title", "body");
                    failedMission.BranchName = "captain/fix-conflict";
                    MergeEntry entry = NewFailedEntry(failedMission.Id, "captain/fix-conflict");
                    entry.TestOutput = "FATAL: merge conflict detected in Widget.cs";
                    MergeFailureClassification cls = new MergeFailureClassification(
                        MergeFailureClassEnum.TextConflict,
                        "Text conflict in Widget.cs",
                        new List<string> { "src/Widget.cs", "src/Gadget.cs" });

                    RebaseCaptainMissionSpec spec = await setup.BuildAsync(entry, failedMission, cls).ConfigureAwait(false);

                    PrestagedFile? marker = spec.PrestagedFiles
                        .FirstOrDefault(f => f.DestPath == RebaseCaptainDockSetup.ConflictStateMarkerRelativePath);
                    AssertNotNull(marker, "conflict-state.md entry must be present");
                    string content = marker!.Content ?? "";
                    AssertContains("captain/fix-conflict", content, "captain branch must appear in content");
                    AssertContains("src/Widget.cs", content, "conflicted file must appear in content");
                    AssertContains("src/Gadget.cs", content, "all conflicted files must appear in content");
                    AssertContains("FATAL: merge conflict detected in Widget.cs", content, "test output tail must appear in content");
                }
            });
        }

        private static MergeEntry NewFailedEntry(string missionId, string branch)
        {
            MergeEntry entry = new MergeEntry(branch, "main");
            entry.MissionId = missionId;
            entry.Status = MergeStatusEnum.Failed;
            entry.MergeFailureClass = MergeFailureClassEnum.TextConflict;
            entry.DiffLineCount = 17;
            return entry;
        }

        private static LoggingModule NewQuietLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }
    }
}
