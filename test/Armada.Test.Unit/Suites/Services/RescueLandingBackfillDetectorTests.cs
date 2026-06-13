namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Server.Mcp;
    using Armada.Server.Mcp.Tools;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// Tests for RescueLandingBackfillDetector and the armada_detect_false_rescue_landings MCP tool.
    /// </summary>
    public class RescueLandingBackfillDetectorTests : TestSuite
    {
        #region Public-Members

        /// <summary>Suite name.</summary>
        public override string Name => "Rescue Landing Backfill Detector";

        #endregion

        #region Public-Methods

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("JudgePersonaRescue_IsFlaggedWithReviewerPersonaReason", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = CreateLogging();

                    Mission parent = new Mission("Original mission");
                    parent.Status = MissionStatusEnum.Failed;
                    parent.CommitHash = "abc123";
                    parent = await testDb.Driver.Missions.CreateAsync(parent).ConfigureAwait(false);

                    Mission rescue = new Mission("Rescue Original mission");
                    rescue.Status = MissionStatusEnum.Complete;
                    rescue.ParentMissionId = parent.Id;
                    rescue.Persona = "Judge";
                    rescue.CommitHash = "def456";
                    rescue.Description = "<!-- ARMADA:AUTO-RESCUE --> Recovering mission.";
                    rescue = await testDb.Driver.Missions.CreateAsync(rescue).ConfigureAwait(false);

                    RescueLandingBackfillDetector detector = new RescueLandingBackfillDetector(testDb.Driver, logging);
                    List<SuspectRescueLanding> results = await detector.DetectAsync().ConfigureAwait(false);

                    AssertTrue(results.Count >= 1, "At least one suspect should be returned");
                    SuspectRescueLanding? suspect = FindSuspect(results, rescue.Id);
                    AssertNotNull(suspect, "Seeded rescue mission should be in suspect list");
                    AssertTrue(suspect!.Reasons.Contains("reviewer_persona_rescue_completed"),
                        "Reason reviewer_persona_rescue_completed expected");
                }
            });

            await RunTest("WorkerRescueWithEmptyCommitHash_IsFlaggedWithEmptyCommitHashReason", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = CreateLogging();

                    Mission parent = new Mission("Work mission");
                    parent.Status = MissionStatusEnum.Failed;
                    parent.CommitHash = "abc999";
                    parent = await testDb.Driver.Missions.CreateAsync(parent).ConfigureAwait(false);

                    Mission rescue = new Mission("Rescue Work mission");
                    rescue.Status = MissionStatusEnum.Complete;
                    rescue.ParentMissionId = parent.Id;
                    rescue.Persona = "Worker";
                    rescue.CommitHash = null; // empty -- triggers reason (b)
                    rescue.Description = "<!-- ARMADA:AUTO-RESCUE --> Recovering.";
                    rescue = await testDb.Driver.Missions.CreateAsync(rescue).ConfigureAwait(false);

                    RescueLandingBackfillDetector detector = new RescueLandingBackfillDetector(testDb.Driver, logging);
                    List<SuspectRescueLanding> results = await detector.DetectAsync().ConfigureAwait(false);

                    SuspectRescueLanding? suspect = FindSuspect(results, rescue.Id);
                    AssertNotNull(suspect, "Empty-hash rescue should appear in suspect list");
                    AssertTrue(suspect!.Reasons.Contains("empty_commit_hash"),
                        "Reason empty_commit_hash expected");
                }
            });

            await RunTest("WorkerRescueWithLandedZeroDiffMergeEntry_IsFlaggedWithNoopMergeEntryReason", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = CreateLogging();

                    Mission parent = new Mission("Noop parent");
                    parent.Status = MissionStatusEnum.Failed;
                    parent.CommitHash = "parenthash1";
                    parent = await testDb.Driver.Missions.CreateAsync(parent).ConfigureAwait(false);

                    Mission rescue = new Mission("Rescue Noop parent");
                    rescue.Status = MissionStatusEnum.Complete;
                    rescue.ParentMissionId = parent.Id;
                    rescue.Persona = "Worker";
                    rescue.CommitHash = null; // also triggers empty_commit_hash
                    rescue.Description = "<!-- ARMADA:AUTO-RESCUE --> Recovering.";
                    rescue = await testDb.Driver.Missions.CreateAsync(rescue).ConfigureAwait(false);

                    MergeEntry mergeEntry = new MergeEntry("armada/rescue-branch", "main");
                    mergeEntry.MissionId = rescue.Id;
                    mergeEntry.Status = MergeStatusEnum.Landed;
                    mergeEntry.DiffLineCount = 0; // noop -- triggers reason (c)
                    await testDb.Driver.MergeEntries.CreateAsync(mergeEntry).ConfigureAwait(false);

                    RescueLandingBackfillDetector detector = new RescueLandingBackfillDetector(testDb.Driver, logging);
                    List<SuspectRescueLanding> results = await detector.DetectAsync().ConfigureAwait(false);

                    SuspectRescueLanding? suspect = FindSuspect(results, rescue.Id);
                    AssertNotNull(suspect, "Noop-merge rescue should appear in suspect list");
                    AssertTrue(suspect!.Reasons.Contains("noop_merge_entry"),
                        "Reason noop_merge_entry expected");
                    AssertTrue(suspect.Reasons.Contains("empty_commit_hash"),
                        "Reason empty_commit_hash also expected for null commit hash");
                }
            });

            await RunTest("LegitimateWorkerRescue_IsNotFlagged", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = CreateLogging();

                    Mission parent = new Mission("Real work parent");
                    parent.Status = MissionStatusEnum.Failed;
                    parent.CommitHash = "parentcommit99";
                    parent = await testDb.Driver.Missions.CreateAsync(parent).ConfigureAwait(false);

                    Mission rescue = new Mission("Rescue Real work parent");
                    rescue.Status = MissionStatusEnum.Complete;
                    rescue.ParentMissionId = parent.Id;
                    rescue.Persona = "Worker";
                    rescue.CommitHash = "distinctrescuecommit"; // distinct from parent -- no identity flag
                    rescue.Description = "<!-- ARMADA:AUTO-RESCUE --> Recovering.";
                    rescue = await testDb.Driver.Missions.CreateAsync(rescue).ConfigureAwait(false);

                    MergeEntry mergeEntry = new MergeEntry("armada/legit-branch", "main");
                    mergeEntry.MissionId = rescue.Id;
                    mergeEntry.Status = MergeStatusEnum.Landed;
                    mergeEntry.DiffLineCount = 42; // real diff -- no noop flag
                    await testDb.Driver.MergeEntries.CreateAsync(mergeEntry).ConfigureAwait(false);

                    RescueLandingBackfillDetector detector = new RescueLandingBackfillDetector(testDb.Driver, logging);
                    List<SuspectRescueLanding> results = await detector.DetectAsync().ConfigureAwait(false);

                    SuspectRescueLanding? suspect = FindSuspect(results, rescue.Id);
                    AssertTrue(suspect == null, "Legitimate rescue should NOT appear in suspect list");
                }
            });

            await RunTest("McpHandler_ReturnsSuspectMissionId_WhenRescueIsFlagged", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = CreateLogging();

                    Mission parent = new Mission("MCP test parent");
                    parent.Status = MissionStatusEnum.Failed;
                    parent.CommitHash = "mcpparenthash";
                    parent = await testDb.Driver.Missions.CreateAsync(parent).ConfigureAwait(false);

                    Mission rescue = new Mission("Rescue MCP test parent");
                    rescue.Status = MissionStatusEnum.Complete;
                    rescue.ParentMissionId = parent.Id;
                    rescue.Persona = "Judge"; // reviewer persona -> flagged
                    rescue.CommitHash = "mcprescuehash";
                    rescue.Description = "<!-- ARMADA:AUTO-RESCUE --> MCP test rescue.";
                    rescue = await testDb.Driver.Missions.CreateAsync(rescue).ConfigureAwait(false);

                    Func<JsonElement?, Task<object>>? handler = null;
                    McpRecoveryBackfillTools.Register(
                        (name, _, _, h) => { if (name == "armada_detect_false_rescue_landings") handler = h; },
                        testDb.Driver,
                        logging);

                    AssertNotNull(handler, "armada_detect_false_rescue_landings handler must be registered");

                    JsonElement args = JsonSerializer.SerializeToElement(new { });
                    object result = await handler!(args).ConfigureAwait(false);
                    string resultJson = JsonSerializer.Serialize(result);

                    AssertContains(rescue.Id, resultJson, "Suspect rescue mission id must appear in MCP result");
                    AssertContains("reviewer_persona_rescue_completed", resultJson, "Reason must appear in MCP result");
                }
            });
        }

        #endregion

        #region Private-Methods

        private static LoggingModule CreateLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        private static SuspectRescueLanding? FindSuspect(List<SuspectRescueLanding> list, string missionId)
        {
            foreach (SuspectRescueLanding s in list)
            {
                if (String.Equals(s.MissionId, missionId, StringComparison.Ordinal))
                    return s;
            }
            return null;
        }

        #endregion
    }
}
