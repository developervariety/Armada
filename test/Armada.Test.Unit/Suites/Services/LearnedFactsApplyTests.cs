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

        #endregion
    }
}
