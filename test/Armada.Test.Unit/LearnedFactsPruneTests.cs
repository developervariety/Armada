namespace Armada.Test.Unit
{
    using System;
    using System.Collections.Generic;
    using System.IO;
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
    /// Tests for pruning and deduplicating the canonical per-vessel learned-facts file.
    /// </summary>
    public class LearnedFactsPruneTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Learned Facts Prune";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("PruneAsync_CapExceeded_RemovesOldest", async () =>
            {
                string repoRoot = CreateTempRepoRoot();
                try
                {
                    string content = "# Learned Facts\n\n"
                        + "[medium] Oldest fact that should be removed.\n\n"
                        + "[high] Second fact that should be kept.\n\n"
                        + "[high] Third fact that should be kept.";
                    WriteLearnedFile(repoRoot, content);

                    LearnedFactsPruneOptions options = new LearnedFactsPruneOptions
                    {
                        MaxEntries = 2,
                        DedupeSimilarityThreshold = 1.0
                    };

                    LearnedFactsPruneResult result = await LearnedFactsFile.PruneAsync(repoRoot, options).ConfigureAwait(false);
                    AssertTrue(result.Success, "Prune should succeed");
                    AssertTrue(result.Changed, "Prune should change the file");
                    AssertEqual(1, result.RemovedCount, "One entry should be removed");
                    AssertEqual(0, result.MergedCount, "No entries should be merged");

                    string? fileContent = await LearnedFactsFile.ReadAsync(repoRoot).ConfigureAwait(false);
                    AssertNotNull(fileContent, "File should still exist");
                    AssertFalse(fileContent!.Contains("Oldest fact that should be removed"), "Oldest entry should be removed");
                    AssertContains("Second fact that should be kept", fileContent, "Second entry should be kept");
                    AssertContains("Third fact that should be kept", fileContent, "Third entry should be kept");
                }
                finally
                {
                    DeleteTempRepoRoot(repoRoot);
                }
            });

            await RunTest("PruneAsync_DedupeMergesNearDuplicates", async () =>
            {
                string repoRoot = CreateTempRepoRoot();
                try
                {
                    string entryA = "[high] Always close database connections in a using statement.";
                    string entryB = "[high] Always close database connections in a using statement before returning.";
                    string content = "# Learned Facts\n\n"
                        + entryA + "\n\n"
                        + entryB + "\n\n"
                        + "[high] Keep tests in the custom TestSuite harness.";
                    WriteLearnedFile(repoRoot, content);

                    double sim = Armada.Core.Memory.HabitPatternMiner.Jaccard3GramSimilarity(entryA, entryB);
                    AssertTrue(sim >= 0.70 && sim < 1.0, "Similarity should be a near-duplicate between 0.70 and 1.0 but was " + sim);

                    LearnedFactsPruneOptions options = new LearnedFactsPruneOptions
                    {
                        MaxEntries = 0,
                        DedupeSimilarityThreshold = 0.70
                    };

                    LearnedFactsPruneResult result = await LearnedFactsFile.PruneAsync(repoRoot, options).ConfigureAwait(false);
                    AssertTrue(result.Success, "Prune should succeed");
                    AssertEqual(1, result.MergedCount, "One entry should be merged before checking changed flag");
                    AssertTrue(result.Changed, "Prune should change the file");
                    AssertEqual(0, result.RemovedCount, "No entries should be removed by cap");
                    AssertEqual(1, result.MergedCount, "One duplicate entry should be merged");

                    string? fileContent = await LearnedFactsFile.ReadAsync(repoRoot).ConfigureAwait(false);
                    AssertNotNull(fileContent, "File should still exist");
                    AssertEqual(2, CountTaggedEntries(fileContent!), "Two distinct entries should remain");
                    AssertContains("Always close database connections in a using statement", fileContent!, "Database connection fact should be kept");
                    AssertContains("Keep tests in the custom TestSuite harness", fileContent!, "Custom harness fact should be kept");
                }
                finally
                {
                    DeleteTempRepoRoot(repoRoot);
                }
            });

            await RunTest("PruneAsync_PreservesDistinctEntries", async () =>
            {
                string repoRoot = CreateTempRepoRoot();
                try
                {
                    string content = "# Learned Facts\n\n"
                        + "[high] Build with dotnet build src/Armada.sln.\n\n"
                        + "[medium] Use the custom TestSuite harness, not xUnit.\n\n"
                        + "[low] Prefer explicit types over var.";
                    WriteLearnedFile(repoRoot, content);

                    LearnedFactsPruneOptions options = new LearnedFactsPruneOptions
                    {
                        MaxEntries = 5,
                        DedupeSimilarityThreshold = 0.85
                    };

                    LearnedFactsPruneResult result = await LearnedFactsFile.PruneAsync(repoRoot, options).ConfigureAwait(false);
                    AssertTrue(result.Success, "Prune should succeed");
                    AssertFalse(result.Changed, "Distinct entries should not be pruned");
                    AssertEqual(0, result.RemovedCount, "No entries should be removed");
                    AssertEqual(0, result.MergedCount, "No entries should be merged");

                    string? fileContent = await LearnedFactsFile.ReadAsync(repoRoot).ConfigureAwait(false);
                    AssertNotNull(fileContent, "File should still exist");
                    AssertEqual(3, CountTaggedEntries(fileContent!), "All three distinct entries should remain");
                }
                finally
                {
                    DeleteTempRepoRoot(repoRoot);
                }
            });

            await RunTest("PruneAsync_DisabledOptions_DoesNothing", async () =>
            {
                string repoRoot = CreateTempRepoRoot();
                try
                {
                    string content = "# Learned Facts\n\n[high] A fact.\n\n[high] Another fact.";
                    WriteLearnedFile(repoRoot, content);

                    LearnedFactsPruneOptions options = new LearnedFactsPruneOptions
                    {
                        MaxEntries = 0,
                        DedupeSimilarityThreshold = 1.0
                    };

                    LearnedFactsPruneResult result = await LearnedFactsFile.PruneAsync(repoRoot, options).ConfigureAwait(false);
                    AssertTrue(result.Success, "Prune should succeed");
                    AssertFalse(result.Changed, "Disabled prune should not change the file");

                    string? fileContent = await LearnedFactsFile.ReadAsync(repoRoot).ConfigureAwait(false);
                    AssertEqual(content, fileContent, "Content should be unchanged");
                }
                finally
                {
                    DeleteTempRepoRoot(repoRoot);
                }
            });

            await RunTest("PruneAsync_SentimentDisagreement_DoesNotMerge", async () =>
            {
                string repoRoot = CreateTempRepoRoot();
                try
                {
                    string content = "# Learned Facts\n\n"
                        + "[high] Always close database connections in a using statement.\n\n"
                        + "[medium] Do not close database connections in a using statement.";
                    WriteLearnedFile(repoRoot, content);

                    LearnedFactsPruneOptions options = new LearnedFactsPruneOptions
                    {
                        MaxEntries = 0,
                        DedupeSimilarityThreshold = 0.5
                    };

                    LearnedFactsPruneResult result = await LearnedFactsFile.PruneAsync(repoRoot, options).ConfigureAwait(false);
                    AssertTrue(result.Success, "Prune should succeed");
                    AssertFalse(result.Changed, "Contradictory entries should not be merged");
                    AssertEqual(0, result.MergedCount, "No entries should be merged when sentiment disagrees");

                    string? fileContent = await LearnedFactsFile.ReadAsync(repoRoot).ConfigureAwait(false);
                    AssertEqual(2, CountTaggedEntries(fileContent!), "Both contradictory entries should remain");
                }
                finally
                {
                    DeleteTempRepoRoot(repoRoot);
                }
            });

            await RunTest("PruneAsync_HighConfidencePreferredOverOldest", async () =>
            {
                string repoRoot = CreateTempRepoRoot();
                try
                {
                    string content = "# Learned Facts\n\n"
                        + "[low] Oldest low-confidence fact.\n\n"
                        + "[high] Important high-confidence fact.\n\n"
                        + "[medium] Medium-confidence fact.";
                    WriteLearnedFile(repoRoot, content);

                    LearnedFactsPruneOptions options = new LearnedFactsPruneOptions
                    {
                        MaxEntries = 1,
                        DedupeSimilarityThreshold = 1.0
                    };

                    LearnedFactsPruneResult result = await LearnedFactsFile.PruneAsync(repoRoot, options).ConfigureAwait(false);
                    AssertTrue(result.Success, "Prune should succeed");
                    AssertEqual(2, result.RemovedCount, "Two entries should be removed");

                    string? fileContent = await LearnedFactsFile.ReadAsync(repoRoot).ConfigureAwait(false);
                    AssertNotNull(fileContent, "File should still exist");
                    AssertContains("Important high-confidence fact", fileContent!, "High-confidence entry should survive the cap");
                    AssertFalse(fileContent!.Contains("Oldest low-confidence fact"), "Low-confidence entry should be removed");
                    AssertFalse(fileContent.Contains("Medium-confidence fact"), "Medium-confidence entry should be removed");
                }
                finally
                {
                    DeleteTempRepoRoot(repoRoot);
                }
            });

            await RunTest("ApplyAsync_WithPruneOptions_TriggersPrune", async () =>
            {
                string repoRoot = CreateTempRepoRoot();
                try
                {
                    string existing = "# Learned Facts\n\n"
                        + "[high] First fact.\n\n"
                        + "[medium] Second fact.";
                    WriteLearnedFile(repoRoot, existing);

                    LearnedFactsPruneOptions options = new LearnedFactsPruneOptions
                    {
                        MaxEntries = 2,
                        DedupeSimilarityThreshold = 1.0
                    };

                    LearnedFactsFileApplyResult result = await LearnedFactsFile.ApplyAsync(
                        repoRoot,
                        "[low] Third fact.",
                        options).ConfigureAwait(false);
                    AssertTrue(result.Success, "Apply should succeed");
                    AssertEqual(1, result.PrunedRemovedCount, "One entry should be pruned to keep cap at 2");

                    string? fileContent = await LearnedFactsFile.ReadAsync(repoRoot).ConfigureAwait(false);
                    AssertNotNull(fileContent, "File should exist");
                    AssertEqual(2, CountTaggedEntries(fileContent!), "File should be at or under the cap");
                }
                finally
                {
                    DeleteTempRepoRoot(repoRoot);
                }
            });

            await RunTest("AcceptMemoryProposalAsync_WithPruneOptions_TriggersPrune", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    string repoRoot = CreateTempRepoRoot();
                    try
                    {
                        Vessel vessel = await CreateApplyVesselAsync(testDb.Driver, "prune-accept", repoRoot).ConfigureAwait(false);
                        WriteLearnedFile(repoRoot, "# Learned Facts\n\n[high] Existing fact one.\n\n[medium] Existing fact two.");

                        string newFact = "[low] Existing fact three.";
                        Mission mission = await CreateReflectionMissionAsync(
                            testDb.Driver,
                            vessel.Id,
                            ReflectionTestHelpers.BuildReflectionProposalAgentOutput("# Learned Facts\n\n" + newFact)).ConfigureAwait(false);

                        ReflectionMemoryService svc = new ReflectionMemoryService(testDb.Driver);
                        ReflectionOutputParser parser = new ReflectionOutputParser();
                        LearnedFactsPruneOptions options = new LearnedFactsPruneOptions
                        {
                            MaxEntries = 2,
                            DedupeSimilarityThreshold = 1.0
                        };

                        ReflectionAcceptProposalResult outcome = await svc.AcceptMemoryProposalAsync(
                            mission.Id,
                            null,
                            parser,
                            options).ConfigureAwait(false);

                        AssertNull(outcome.Error, "Accept with prune options must not error");
                        AssertEqual(1, outcome.PrunedRemovedCount, "Production accept path should prune one entry");

                        string? fileContent = await LearnedFactsFile.ReadAsync(repoRoot).ConfigureAwait(false);
                        AssertNotNull(fileContent, "Canonical file should exist");
                        AssertEqual(2, CountTaggedEntries(fileContent!), "File should be brought under cap");
                    }
                    finally
                    {
                        DeleteTempRepoRoot(repoRoot);
                    }
                }
            });

            await RunTest("PruneAsync_MissingRepoRoot_ReturnsRepoRootMissing", async () =>
            {
                LearnedFactsPruneOptions options = new LearnedFactsPruneOptions { MaxEntries = 2 };
                LearnedFactsPruneResult result = await LearnedFactsFile.PruneAsync("   ", options).ConfigureAwait(false);
                AssertFalse(result.Success, "Whitespace repo root must fail");
                AssertEqual("repo_root_missing", result.Error, "Whitespace repo root surfaces repo_root_missing");
            });

            await RunTest("PruneAsync_NullOptions_ReturnsOptionsMissing", async () =>
            {
                string repoRoot = CreateTempRepoRoot();
                try
                {
                    WriteLearnedFile(repoRoot, "# Learned Facts\n\n[high] Fact.");
                    LearnedFactsPruneResult result = await LearnedFactsFile.PruneAsync(repoRoot, null!).ConfigureAwait(false);
                    AssertFalse(result.Success, "Null options must fail");
                    AssertEqual("prune_options_missing", result.Error, "Null options surfaces prune_options_missing");
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
            Mission mission = new Mission("prune-reflection", "prune test");
            mission.VesselId = vesselId;
            mission.Persona = "MemoryConsolidator";
            mission.Status = MissionStatusEnum.WorkProduced;
            mission.AgentOutput = agentOutput;
            return await database.Missions.CreateAsync(mission).ConfigureAwait(false);
        }

        private static string CreateTempRepoRoot()
        {
            string path = Path.Combine(Path.GetTempPath(), "armada_lfp_test_" + Guid.NewGuid().ToString("N"));
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

        private static int CountTaggedEntries(string content)
        {
            if (String.IsNullOrWhiteSpace(content))
                return 0;

            int count = 0;
            foreach (string raw in content.Split('\n'))
            {
                string trimmed = raw.TrimStart();
                if (System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"^\[(high|medium|low)\]", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    count++;
            }

            return count;
        }

        #endregion
    }
}
