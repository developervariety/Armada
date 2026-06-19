namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text.RegularExpressions;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Memory;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// Tests that accepted learned-fact proposals are landed in the canonical per-vessel file.
    /// </summary>
    public class LearnedFactsApplyTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Learned Facts Apply";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("AcceptMemoryProposalAsync_FileLand_SucceedsAndWritesFact", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    string repoRoot = CreateTempRepoRoot();
                    try
                    {
                        Vessel vessel = await CreateApplyVesselAsync(testDb.Driver, "apply-happy", repoRoot).ConfigureAwait(false);
                        string fact = "[high] Always close database connections in a using statement.";
                        Mission mission = await CreateReflectionMissionAsync(
                            testDb.Driver,
                            vessel.Id,
                            ReflectionTestHelpers.BuildReflectionProposalAgentOutput("# Learned Facts\n\n" + fact)).ConfigureAwait(false);

                        ReflectionMemoryService svc = new ReflectionMemoryService(testDb.Driver);
                        ReflectionOutputParser parser = new ReflectionOutputParser();
                        ReflectionAcceptProposalResult outcome = await svc.AcceptMemoryProposalAsync(
                            mission.Id,
                            null,
                            parser).ConfigureAwait(false);

                        AssertNull(outcome.Error, "Happy-path accept must not error");
                        AssertNotNull(outcome.PlaybookId, "Playbook ID should be set");

                        string? fileContent = await LearnedFactsFile.ReadAsync(repoRoot).ConfigureAwait(false);
                        AssertNotNull(fileContent, "Canonical file should contain the accepted fact");
                        AssertContains(fact, fileContent!, "Canonical file must contain the accepted fact");

                        List<ArmadaEvent> events = await testDb.Driver.Events.EnumerateByMissionAsync(mission.Id).ConfigureAwait(false);
                        AssertTrue(events.Exists(e => e.EventType == "reflection.accepted"), "Accepted event must be recorded");
                    }
                    finally
                    {
                        DeleteTempRepoRoot(repoRoot);
                    }
                }
            });

            await RunTest("AcceptMemoryProposalAsync_MergesFactsWithoutDuplicates", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    string repoRoot = CreateTempRepoRoot();
                    try
                    {
                        Vessel vessel = await CreateApplyVesselAsync(testDb.Driver, "apply-merge", repoRoot).ConfigureAwait(false);
                        string priorFact = "[medium] Existing vessel convention.";
                        WriteLearnedFile(repoRoot, "# Learned Facts\n\n" + priorFact);

                        string newFact = "[high] New accepted fact from this mission.";
                        Mission mission = await CreateReflectionMissionAsync(
                            testDb.Driver,
                            vessel.Id,
                            ReflectionTestHelpers.BuildReflectionProposalAgentOutput("# Learned Facts\n\n" + newFact)).ConfigureAwait(false);

                        ReflectionMemoryService svc = new ReflectionMemoryService(testDb.Driver);
                        ReflectionOutputParser parser = new ReflectionOutputParser();
                        ReflectionAcceptProposalResult outcome = await svc.AcceptMemoryProposalAsync(
                            mission.Id,
                            null,
                            parser).ConfigureAwait(false);

                        AssertNull(outcome.Error, "Merge accept must not error");

                        string? fileContent = await LearnedFactsFile.ReadAsync(repoRoot).ConfigureAwait(false);
                        AssertNotNull(fileContent, "Canonical file should exist after merge");
                        AssertContains(priorFact, fileContent!, "Prior fact must be preserved");
                        AssertContains(newFact, fileContent!, "New fact must be appended");
                    }
                    finally
                    {
                        DeleteTempRepoRoot(repoRoot);
                    }
                }
            });

            await RunTest("AcceptMemoryProposalAsync_NonexistentRepoRoot_ReturnsError", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    string repoRoot = Path.Combine(Path.GetTempPath(), "armada_lfa_missing_" + Guid.NewGuid().ToString("N"));
                    Vessel vessel = await CreateApplyVesselAsync(testDb.Driver, "apply-missing-root", repoRoot).ConfigureAwait(false);

                    string fact = "[high] Fact that cannot be landed because repo root is missing.";
                    Mission mission = await CreateReflectionMissionAsync(
                        testDb.Driver,
                        vessel.Id,
                        ReflectionTestHelpers.BuildReflectionProposalAgentOutput("# Learned Facts\n\n" + fact)).ConfigureAwait(false);

                    ReflectionMemoryService svc = new ReflectionMemoryService(testDb.Driver);
                    ReflectionOutputParser parser = new ReflectionOutputParser();
                    ReflectionAcceptProposalResult outcome = await svc.AcceptMemoryProposalAsync(
                        mission.Id,
                        null,
                        parser).ConfigureAwait(false);

                    AssertNotNull(outcome.Error, "Missing repo root must surface an error");
                    AssertContains("repo_root_not_found", outcome.Error!, "Error should name the missing repo root condition");

                    List<ArmadaEvent> events = await testDb.Driver.Events.EnumerateByMissionAsync(mission.Id).ConfigureAwait(false);
                    AssertFalse(events.Exists(e => e.EventType == "reflection.accepted"), "Accepted event must not be recorded when file land fails");
                }
            });

            await RunTest("AcceptMemoryProposalAsync_ReadOnlyFile_ReturnsError", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    string repoRoot = CreateTempRepoRoot();
                    try
                    {
                        Vessel vessel = await CreateApplyVesselAsync(testDb.Driver, "apply-readonly", repoRoot).ConfigureAwait(false);
                        WriteLearnedFile(repoRoot, "# Learned Facts\n\n[medium] Existing fact.");
                        string path = Path.Combine(repoRoot, ".armada", "LEARNED.md");
                        File.SetAttributes(path, FileAttributes.ReadOnly);

                        string fact = "[high] Fact that cannot be written because file is read-only.";
                        Mission mission = await CreateReflectionMissionAsync(
                            testDb.Driver,
                            vessel.Id,
                            ReflectionTestHelpers.BuildReflectionProposalAgentOutput("# Learned Facts\n\n" + fact)).ConfigureAwait(false);

                        ReflectionMemoryService svc = new ReflectionMemoryService(testDb.Driver);
                        ReflectionOutputParser parser = new ReflectionOutputParser();
                        ReflectionAcceptProposalResult outcome = await svc.AcceptMemoryProposalAsync(
                            mission.Id,
                            null,
                            parser).ConfigureAwait(false);

                        AssertNotNull(outcome.Error, "Read-only file must surface an error");
                        AssertContains("apply_failed", outcome.Error!, "Error should indicate apply failure");

                        List<ArmadaEvent> events = await testDb.Driver.Events.EnumerateByMissionAsync(mission.Id).ConfigureAwait(false);
                        AssertFalse(events.Exists(e => e.EventType == "reflection.accepted"), "Accepted event must not be recorded when file land fails");
                    }
                    finally
                    {
                        string path = Path.Combine(repoRoot, ".armada", "LEARNED.md");
                        try
                        {
                            if (File.Exists(path))
                                File.SetAttributes(path, FileAttributes.Normal);
                        }
                        catch
                        {
                            // Best-effort cleanup.
                        }

                        DeleteTempRepoRoot(repoRoot);
                    }
                }
            });

            await RunTest("LearnedFactsFile_ApplyAsync_WritesUtf8NoBomAndLfLineEndings", async () =>
            {
                string repoRoot = CreateTempRepoRoot();
                try
                {
                    string fact = "[high] Line one.\r\nLine two.";
                    LearnedFactsFileApplyResult result = await LearnedFactsFile.ApplyAsync(repoRoot, fact).ConfigureAwait(false);
                    AssertTrue(result.Success, "Apply should succeed");

                    string path = Path.Combine(repoRoot, ".armada", "LEARNED.md");
                    byte[] bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
                    AssertFalse(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "File must not have a UTF-8 BOM");

                    string content = await File.ReadAllTextAsync(path).ConfigureAwait(false);
                    AssertFalse(content.Contains("\r\n"), "File must use LF line endings");
                    AssertContains("Line one.", content, "Content should be present");
                    AssertContains("Line two.", content, "Content should be present");
                }
                finally
                {
                    DeleteTempRepoRoot(repoRoot);
                }
            });

            await RunTest("AcceptMemoryProposalAsync_NoRepoRoot_AcceptsWithoutFileLand", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    // Vessel has neither WorkingDirectory nor LocalPath: the conditional file-land
                    // block is skipped entirely, so accept must still succeed and be recorded.
                    Vessel vessel = new Vessel("apply-no-root", "https://github.com/test/apply-no-root.git");
                    vessel.TenantId = Constants.DefaultTenantId;
                    vessel.WorkingDirectory = null;
                    vessel.LocalPath = null;
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    string fact = "[high] Fact landed only to the DB playbook because there is no in-dock checkout.";
                    Mission mission = await CreateReflectionMissionAsync(
                        testDb.Driver,
                        vessel.Id,
                        ReflectionTestHelpers.BuildReflectionProposalAgentOutput("# Learned Facts\n\n" + fact)).ConfigureAwait(false);

                    ReflectionMemoryService svc = new ReflectionMemoryService(testDb.Driver);
                    ReflectionOutputParser parser = new ReflectionOutputParser();
                    ReflectionAcceptProposalResult outcome = await svc.AcceptMemoryProposalAsync(
                        mission.Id,
                        null,
                        parser).ConfigureAwait(false);

                    AssertNull(outcome.Error, "Accept with no repo root must not error");
                    AssertNotNull(outcome.PlaybookId, "DB playbook must still be persisted");

                    List<ArmadaEvent> events = await testDb.Driver.Events.EnumerateByMissionAsync(mission.Id).ConfigureAwait(false);
                    AssertTrue(events.Exists(e => e.EventType == "reflection.accepted"), "Accepted event must be recorded even without a file land");
                }
            });

            await RunTest("AcceptMemoryProposalAsync_LocalPathRepoRoot_LandsFact", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    string repoRoot = CreateTempRepoRoot();
                    try
                    {
                        // WorkingDirectory empty -> the write side must resolve the repo root from LocalPath.
                        Vessel vessel = new Vessel("apply-localpath", "https://github.com/test/apply-localpath.git");
                        vessel.TenantId = Constants.DefaultTenantId;
                        vessel.WorkingDirectory = null;
                        vessel.LocalPath = repoRoot;
                        vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                        string fact = "[high] Fact landed via the LocalPath fallback.";
                        Mission mission = await CreateReflectionMissionAsync(
                            testDb.Driver,
                            vessel.Id,
                            ReflectionTestHelpers.BuildReflectionProposalAgentOutput("# Learned Facts\n\n" + fact)).ConfigureAwait(false);

                        ReflectionMemoryService svc = new ReflectionMemoryService(testDb.Driver);
                        ReflectionOutputParser parser = new ReflectionOutputParser();
                        ReflectionAcceptProposalResult outcome = await svc.AcceptMemoryProposalAsync(
                            mission.Id,
                            null,
                            parser).ConfigureAwait(false);

                        AssertNull(outcome.Error, "LocalPath land must not error");

                        string? fileContent = await LearnedFactsFile.ReadAsync(repoRoot).ConfigureAwait(false);
                        AssertNotNull(fileContent, "LocalPath repo root should receive the canonical file");
                        AssertContains(fact, fileContent!, "Fact must be landed in the LocalPath repo root");
                    }
                    finally
                    {
                        DeleteTempRepoRoot(repoRoot);
                    }
                }
            });

            await RunTest("AcceptMemoryProposalAsync_DuplicateFact_NotDuplicatedInFile", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    string repoRoot = CreateTempRepoRoot();
                    try
                    {
                        Vessel vessel = await CreateApplyVesselAsync(testDb.Driver, "apply-dup", repoRoot).ConfigureAwait(false);
                        string fact = "[high] An already-present fact that must not be duplicated.";

                        // Pre-seed the canonical file with exactly the content the accept will apply.
                        WriteLearnedFile(repoRoot, "# Learned Facts\n\n" + fact);

                        Mission mission = await CreateReflectionMissionAsync(
                            testDb.Driver,
                            vessel.Id,
                            ReflectionTestHelpers.BuildReflectionProposalAgentOutput("# Learned Facts\n\n" + fact)).ConfigureAwait(false);

                        ReflectionMemoryService svc = new ReflectionMemoryService(testDb.Driver);
                        ReflectionOutputParser parser = new ReflectionOutputParser();
                        ReflectionAcceptProposalResult outcome = await svc.AcceptMemoryProposalAsync(
                            mission.Id,
                            null,
                            parser).ConfigureAwait(false);

                        AssertNull(outcome.Error, "Duplicate accept must not error");

                        string? fileContent = await LearnedFactsFile.ReadAsync(repoRoot).ConfigureAwait(false);
                        AssertNotNull(fileContent, "Canonical file should still exist");
                        AssertEqual(1, CountOccurrences(fileContent!, fact), "Fact must appear exactly once after a duplicate accept");
                    }
                    finally
                    {
                        DeleteTempRepoRoot(repoRoot);
                    }
                }
            });

            await RunTest("AcceptMemoryProposalAsync_TemplateOnlyFile_ReplacedByRealFact", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    string repoRoot = CreateTempRepoRoot();
                    try
                    {
                        Vessel vessel = await CreateApplyVesselAsync(testDb.Driver, "apply-template", repoRoot).ConfigureAwait(false);

                        // A template-only file reads as empty, so the accepted fact replaces it cleanly
                        // rather than being appended below the template boilerplate.
                        WriteLearnedFile(repoRoot, LearnedFactsFile.DefaultTemplateContent);

                        string fact = "[high] First real fact landed over an empty-state template.";
                        Mission mission = await CreateReflectionMissionAsync(
                            testDb.Driver,
                            vessel.Id,
                            ReflectionTestHelpers.BuildReflectionProposalAgentOutput("# Learned Facts\n\n" + fact)).ConfigureAwait(false);

                        ReflectionMemoryService svc = new ReflectionMemoryService(testDb.Driver);
                        ReflectionOutputParser parser = new ReflectionOutputParser();
                        ReflectionAcceptProposalResult outcome = await svc.AcceptMemoryProposalAsync(
                            mission.Id,
                            null,
                            parser).ConfigureAwait(false);

                        AssertNull(outcome.Error, "Template-replacement accept must not error");

                        string? fileContent = await LearnedFactsFile.ReadAsync(repoRoot).ConfigureAwait(false);
                        AssertNotNull(fileContent, "Canonical file should contain the real fact");
                        AssertContains(fact, fileContent!, "Real fact must be landed");
                        AssertFalse(fileContent!.Contains("No accepted reflection facts yet"), "Empty-state template boilerplate must not remain in the file");
                    }
                    finally
                    {
                        DeleteTempRepoRoot(repoRoot);
                    }
                }
            });

            await RunTest("LearnedFactsFile_ApplyAsync_NullOrWhitespaceRepoRoot_ReturnsRepoRootMissing", async () =>
            {
                LearnedFactsFileApplyResult nullResult = await LearnedFactsFile.ApplyAsync(null!, "[high] fact").ConfigureAwait(false);
                AssertFalse(nullResult.Success, "Null repo root must fail");
                AssertEqual("repo_root_missing", nullResult.Error, "Null repo root surfaces repo_root_missing");

                LearnedFactsFileApplyResult whitespaceResult = await LearnedFactsFile.ApplyAsync("   ", "[high] fact").ConfigureAwait(false);
                AssertFalse(whitespaceResult.Success, "Whitespace repo root must fail");
                AssertEqual("repo_root_missing", whitespaceResult.Error, "Whitespace repo root surfaces repo_root_missing");
            });

            await RunTest("LearnedFactsFile_ApplyAsync_NullOrWhitespaceContent_ReturnsContentMissing", async () =>
            {
                string repoRoot = CreateTempRepoRoot();
                try
                {
                    LearnedFactsFileApplyResult nullResult = await LearnedFactsFile.ApplyAsync(repoRoot, null!).ConfigureAwait(false);
                    AssertFalse(nullResult.Success, "Null content must fail");
                    AssertEqual("content_missing", nullResult.Error, "Null content surfaces content_missing");

                    LearnedFactsFileApplyResult whitespaceResult = await LearnedFactsFile.ApplyAsync(repoRoot, "   \n  ").ConfigureAwait(false);
                    AssertFalse(whitespaceResult.Success, "Whitespace content must fail");
                    AssertEqual("content_missing", whitespaceResult.Error, "Whitespace content surfaces content_missing");

                    // Guard rejection must run before any file is created.
                    AssertFalse(File.Exists(Path.Combine(repoRoot, ".armada", "LEARNED.md")), "No file should be written when content is missing");
                }
                finally
                {
                    DeleteTempRepoRoot(repoRoot);
                }
            });

            await RunTest("LearnedFactsFile_ApplyAsync_AppendsSecondDistinctFact", async () =>
            {
                string repoRoot = CreateTempRepoRoot();
                try
                {
                    LearnedFactsFileApplyResult first = await LearnedFactsFile.ApplyAsync(repoRoot, "[medium] First fact.").ConfigureAwait(false);
                    AssertTrue(first.Success, "First apply should succeed");

                    LearnedFactsFileApplyResult second = await LearnedFactsFile.ApplyAsync(repoRoot, "[high] Second distinct fact.").ConfigureAwait(false);
                    AssertTrue(second.Success, "Second apply should succeed");

                    string? content = await LearnedFactsFile.ReadAsync(repoRoot).ConfigureAwait(false);
                    AssertNotNull(content, "File should exist after two applies");
                    AssertContains("First fact.", content!, "First fact must be preserved when a second is appended");
                    AssertContains("Second distinct fact.", content!, "Second distinct fact must be appended");
                }
                finally
                {
                    DeleteTempRepoRoot(repoRoot);
                }
            });
        }

        #region Private-Methods

        private static async Task<Vessel> CreateApplyVesselAsync(DatabaseDriver database, string name, string repoRoot)
        {
            Vessel vessel = new Vessel(name, "https://github.com/test/" + name + ".git");
            vessel.TenantId = Constants.DefaultTenantId;
            vessel.WorkingDirectory = repoRoot;
            return await database.Vessels.CreateAsync(vessel).ConfigureAwait(false);
        }

        private static async Task<Mission> CreateReflectionMissionAsync(
            DatabaseDriver database,
            string vesselId,
            string agentOutput)
        {
            Mission mission = new Mission("apply-reflection", "apply test");
            mission.VesselId = vesselId;
            mission.Persona = "MemoryConsolidator";
            mission.Status = MissionStatusEnum.WorkProduced;
            mission.AgentOutput = agentOutput;
            return await database.Missions.CreateAsync(mission).ConfigureAwait(false);
        }

        private static string CreateTempRepoRoot()
        {
            string path = Path.Combine(Path.GetTempPath(), "armada_lfa_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        private static void WriteLearnedFile(string repoRoot, string content)
        {
            string dir = Path.Combine(repoRoot, ".armada");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "LEARNED.md");
            File.WriteAllText(path, content);
        }

        private static void DeleteTempRepoRoot(string repoRoot)
        {
            try
            {
                if (Directory.Exists(repoRoot))
                    Directory.Delete(repoRoot, true);
            }
            catch
            {
                // Best-effort cleanup.
            }
        }

        private static int CountOccurrences(string haystack, string needle)
        {
            if (String.IsNullOrEmpty(haystack) || String.IsNullOrEmpty(needle))
                return 0;

            int count = 0;
            int index = 0;
            while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += needle.Length;
            }

            return count;
        }

        #endregion
    }
}
