namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text.RegularExpressions;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Enums;
    using Armada.Core.Memory;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// Tests that learned-fact reads prefer the canonical in-dock file while preserving DB fallback.
    /// </summary>
    public class LearnedFactsReadPreferenceTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Learned Facts Read Preference";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("ReadLearnedPlaybookContentAsync_FilePresent_ReturnsFileContent", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    string repoRoot = CreateTempRepoRoot();
                    try
                    {
                        string fileContent = "# Vessel Learned Facts\n\nFILE-CONTENT-ABC-123";
                        WriteLearnedFile(repoRoot, fileContent);

                        Fleet fleet = new Fleet("lfrp-fleet-1");
                        fleet = await testDb.Driver.Fleets.CreateAsync(fleet).ConfigureAwait(false);

                        Vessel vessel = new Vessel("lfrp-vessel-one", "https://github.com/test/lfrp-1.git");
                        vessel.FleetId = fleet.Id;
                        vessel.TenantId = Constants.DefaultTenantId;
                        vessel.WorkingDirectory = repoRoot;
                        vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                        string fileName = "vessel-" + SanitizeName(vessel.Name) + "-learned.md";
                        Playbook dbPlaybook = new Playbook(fileName, "# Vessel Learned Facts\n\nDB-CONTENT-XYZ-789");
                        dbPlaybook.TenantId = Constants.DefaultTenantId;
                        dbPlaybook.UserId = Constants.DefaultUserId;
                        await testDb.Driver.Playbooks.CreateAsync(dbPlaybook).ConfigureAwait(false);

                        ReflectionMemoryService svc = new ReflectionMemoryService(testDb.Driver);
                        string result = await svc.ReadLearnedPlaybookContentAsync(vessel).ConfigureAwait(false);

                        AssertContains("FILE-CONTENT-ABC-123", result, "Result should come from the file");
                        AssertFalse(result.Contains("DB-CONTENT-XYZ-789"), "Result must not contain DB playbook content when file is present");
                    }
                    finally
                    {
                        DeleteTempRepoRoot(repoRoot);
                    }
                }
            });

            await RunTest("ReadLearnedPlaybookContentAsync_FileAbsent_FallsBackToDbPlaybook", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    string repoRoot = CreateTempRepoRoot();
                    try
                    {
                        Fleet fleet = new Fleet("lfrp-fleet-2");
                        fleet = await testDb.Driver.Fleets.CreateAsync(fleet).ConfigureAwait(false);

                        Vessel vessel = new Vessel("lfrp-vessel-two", "https://github.com/test/lfrp-2.git");
                        vessel.FleetId = fleet.Id;
                        vessel.TenantId = Constants.DefaultTenantId;
                        vessel.WorkingDirectory = repoRoot;
                        vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                        string fileName = "vessel-" + SanitizeName(vessel.Name) + "-learned.md";
                        Playbook dbPlaybook = new Playbook(fileName, "# Vessel Learned Facts\n\nDB-FALLBACK-CONTENT");
                        dbPlaybook.TenantId = Constants.DefaultTenantId;
                        dbPlaybook.UserId = Constants.DefaultUserId;
                        await testDb.Driver.Playbooks.CreateAsync(dbPlaybook).ConfigureAwait(false);

                        ReflectionMemoryService svc = new ReflectionMemoryService(testDb.Driver);
                        string result = await svc.ReadLearnedPlaybookContentAsync(vessel).ConfigureAwait(false);

                        AssertContains("DB-FALLBACK-CONTENT", result, "Result should fall back to DB playbook content when file is absent");
                    }
                    finally
                    {
                        DeleteTempRepoRoot(repoRoot);
                    }
                }
            });

            await RunTest("ReadLearnedPlaybookContentAsync_FileContainsOnlyTemplate_FallsBackToDbPlaybook", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    string repoRoot = CreateTempRepoRoot();
                    try
                    {
                        WriteLearnedFile(repoRoot, LearnedFactsFile.DefaultTemplateContent);

                        Fleet fleet = new Fleet("lfrp-fleet-3");
                        fleet = await testDb.Driver.Fleets.CreateAsync(fleet).ConfigureAwait(false);

                        Vessel vessel = new Vessel("lfrp-vessel-three", "https://github.com/test/lfrp-3.git");
                        vessel.FleetId = fleet.Id;
                        vessel.TenantId = Constants.DefaultTenantId;
                        vessel.WorkingDirectory = repoRoot;
                        vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                        string fileName = "vessel-" + SanitizeName(vessel.Name) + "-learned.md";
                        Playbook dbPlaybook = new Playbook(fileName, "# Vessel Learned Facts\n\nDB-TEMPLATE-FALLBACK");
                        dbPlaybook.TenantId = Constants.DefaultTenantId;
                        dbPlaybook.UserId = Constants.DefaultUserId;
                        await testDb.Driver.Playbooks.CreateAsync(dbPlaybook).ConfigureAwait(false);

                        ReflectionMemoryService svc = new ReflectionMemoryService(testDb.Driver);
                        string result = await svc.ReadLearnedPlaybookContentAsync(vessel).ConfigureAwait(false);

                        AssertContains("DB-TEMPLATE-FALLBACK", result, "Template-only file should trigger DB fallback");
                    }
                    finally
                    {
                        DeleteTempRepoRoot(repoRoot);
                    }
                }
            });

            await RunTest("BootstrapVesselAsync_DefaultPlaybook_DescriptionAndContentNameCanonicalPathAndProposalMarker", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Fleet fleet = new Fleet("lfrp-fleet-4");
                    fleet = await testDb.Driver.Fleets.CreateAsync(fleet).ConfigureAwait(false);

                    Vessel vessel = new Vessel("lfrp-vessel-four", "https://github.com/test/lfrp-4.git");
                    vessel.FleetId = fleet.Id;
                    vessel.TenantId = Constants.DefaultTenantId;
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    SyslogLogging.LoggingModule logging = new SyslogLogging.LoggingModule();
                    logging.Settings.EnableConsole = false;
                    ReflectionMemoryBootstrapService svc = new ReflectionMemoryBootstrapService(testDb.Driver, logging);
                    await svc.BootstrapAsync().ConfigureAwait(false);

                    string fileName = "vessel-" + SanitizeName(vessel.Name) + "-learned.md";
                    Playbook? playbook = await testDb.Driver.Playbooks.ReadByFileNameAsync(
                        Constants.DefaultTenantId, fileName).ConfigureAwait(false);
                    AssertNotNull(playbook, "Learned playbook should be created");

                    AssertContains(".armada/LEARNED.md", playbook!.Description, "Description should name the canonical in-dock path");
                    AssertContains("[LEARNED-FACT-PROPOSAL]", playbook.Description, "Description should contain the proposal marker");

                    AssertContains(".armada/LEARNED.md", playbook.Content, "Seeded content should name the canonical in-dock path");
                    AssertContains("[LEARNED-FACT-PROPOSAL]", playbook.Content, "Seeded content should contain the proposal marker");

                    Vessel? updated = await testDb.Driver.Vessels.ReadAsync(vessel.Id).ConfigureAwait(false);
                    AssertNotNull(updated, "Vessel should exist after bootstrap");
                    List<SelectedPlaybook> defaults = updated!.GetDefaultPlaybooks();
                    AssertEqual(1, defaults.Count, "Vessel should have exactly one DefaultPlaybooks entry");
                    AssertEqual(PlaybookDeliveryModeEnum.InstructionWithReference, defaults[0].DeliveryMode, "Delivery mode should be InstructionWithReference");
                    AssertEqual(playbook.Id, defaults[0].PlaybookId, "DefaultPlaybooks entry should reference the created playbook");
                }
            });

            await RunTest("LearnedFactsFile_ReadAsync_GitTrackedFile_IsReadable", async () =>
            {
                string repoRoot = CreateTempRepoRoot();
                try
                {
                    string expectedContent = "# Learned Facts\n\nConfirmed from git-tracked checkout.";
                    WriteLearnedFile(repoRoot, expectedContent);

                    string? result = await LearnedFactsFile.ReadAsync(repoRoot).ConfigureAwait(false);
                    AssertNotNull(result, "File helper should read the physical file");
                    AssertEqual(expectedContent, result, "Read content should match the file");
                }
                finally
                {
                    DeleteTempRepoRoot(repoRoot);
                }
            });

            await RunTest("ReadLearnedPlaybookContentAsync_NullVessel_Throws", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ReflectionMemoryService svc = new ReflectionMemoryService(testDb.Driver);
                    await AssertThrowsAsync<ArgumentNullException>(
                        () => svc.ReadLearnedPlaybookContentAsync(null!),
                        "Null vessel must throw ArgumentNullException").ConfigureAwait(false);
                }
            });

            await RunTest("ReadLearnedPlaybookContentAsync_NoRepoRootNoTenant_ReturnsDefaultEmptyState", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    // No WorkingDirectory, no LocalPath, no TenantId: file path is skipped and the
                    // DB fallback short-circuits to the default empty-state string before any DB read.
                    Vessel vessel = new Vessel("lfrp-vessel-no-root", "https://github.com/test/lfrp-no-root.git");
                    vessel.WorkingDirectory = null;
                    vessel.LocalPath = null;
                    vessel.TenantId = "";

                    ReflectionMemoryService svc = new ReflectionMemoryService(testDb.Driver);
                    string result = await svc.ReadLearnedPlaybookContentAsync(vessel).ConfigureAwait(false);

                    AssertContains("No accepted reflection facts yet", result, "Should return the default empty-state string");
                }
            });

            await RunTest("ReadLearnedPlaybookContentAsync_LocalPathFallback_ReadsFile", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    string repoRoot = CreateTempRepoRoot();
                    try
                    {
                        // WorkingDirectory empty -> repo root resolves from LocalPath.
                        WriteLearnedFile(repoRoot, "# Vessel Learned Facts\n\nLOCALPATH-FILE-CONTENT");

                        Vessel vessel = new Vessel("lfrp-vessel-localpath", "https://github.com/test/lfrp-lp.git");
                        vessel.TenantId = Constants.DefaultTenantId;
                        vessel.WorkingDirectory = null;
                        vessel.LocalPath = repoRoot;

                        string fileName = "vessel-" + SanitizeName(vessel.Name) + "-learned.md";
                        Playbook dbPlaybook = new Playbook(fileName, "# Vessel Learned Facts\n\nDB-SHOULD-NOT-WIN");
                        dbPlaybook.TenantId = Constants.DefaultTenantId;
                        dbPlaybook.UserId = Constants.DefaultUserId;
                        await testDb.Driver.Playbooks.CreateAsync(dbPlaybook).ConfigureAwait(false);

                        ReflectionMemoryService svc = new ReflectionMemoryService(testDb.Driver);
                        string result = await svc.ReadLearnedPlaybookContentAsync(vessel).ConfigureAwait(false);

                        AssertContains("LOCALPATH-FILE-CONTENT", result, "File reached via LocalPath should win");
                        AssertFalse(result.Contains("DB-SHOULD-NOT-WIN"), "DB content must not appear when LocalPath file is present");
                    }
                    finally
                    {
                        DeleteTempRepoRoot(repoRoot);
                    }
                }
            });

            await RunTest("ReadLearnedPlaybookContentAsync_WorkingDirectoryPreferredOverLocalPath", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    string workingDir = CreateTempRepoRoot();
                    string localPath = CreateTempRepoRoot();
                    try
                    {
                        // Both repo roots have a learned file; WorkingDirectory must take precedence.
                        WriteLearnedFile(workingDir, "# Vessel Learned Facts\n\nFROM-WORKING-DIRECTORY");
                        WriteLearnedFile(localPath, "# Vessel Learned Facts\n\nFROM-LOCAL-PATH");

                        Vessel vessel = new Vessel("lfrp-vessel-precedence", "https://github.com/test/lfrp-prec.git");
                        vessel.TenantId = Constants.DefaultTenantId;
                        vessel.WorkingDirectory = workingDir;
                        vessel.LocalPath = localPath;

                        ReflectionMemoryService svc = new ReflectionMemoryService(testDb.Driver);
                        string result = await svc.ReadLearnedPlaybookContentAsync(vessel).ConfigureAwait(false);

                        AssertContains("FROM-WORKING-DIRECTORY", result, "WorkingDirectory should be preferred over LocalPath");
                        AssertFalse(result.Contains("FROM-LOCAL-PATH"), "LocalPath file must not win when WorkingDirectory file is present");
                    }
                    finally
                    {
                        DeleteTempRepoRoot(workingDir);
                        DeleteTempRepoRoot(localPath);
                    }
                }
            });

            await RunTest("ReadLearnedPlaybookContentAsync_LegacyTemplateFile_FallsBackToDbPlaybook", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    string repoRoot = CreateTempRepoRoot();
                    try
                    {
                        // The pre-pointer legacy empty-state template must also be treated as template-only.
                        WriteLearnedFile(repoRoot, LearnedFactsFile.LegacyTemplateContent);

                        Vessel vessel = new Vessel("lfrp-vessel-legacy", "https://github.com/test/lfrp-legacy.git");
                        vessel.TenantId = Constants.DefaultTenantId;
                        vessel.WorkingDirectory = repoRoot;

                        string fileName = "vessel-" + SanitizeName(vessel.Name) + "-learned.md";
                        Playbook dbPlaybook = new Playbook(fileName, "# Vessel Learned Facts\n\nDB-LEGACY-FALLBACK");
                        dbPlaybook.TenantId = Constants.DefaultTenantId;
                        dbPlaybook.UserId = Constants.DefaultUserId;
                        await testDb.Driver.Playbooks.CreateAsync(dbPlaybook).ConfigureAwait(false);

                        ReflectionMemoryService svc = new ReflectionMemoryService(testDb.Driver);
                        string result = await svc.ReadLearnedPlaybookContentAsync(vessel).ConfigureAwait(false);

                        AssertContains("DB-LEGACY-FALLBACK", result, "Legacy-template file should trigger DB fallback");
                    }
                    finally
                    {
                        DeleteTempRepoRoot(repoRoot);
                    }
                }
            });

            await RunTest("LearnedFactsFile_ReadAsync_NullOrWhitespaceRepoRoot_ReturnsNull", async () =>
            {
                AssertNull(await LearnedFactsFile.ReadAsync(null!).ConfigureAwait(false), "Null repo root returns null");
                AssertNull(await LearnedFactsFile.ReadAsync("").ConfigureAwait(false), "Empty repo root returns null");
                AssertNull(await LearnedFactsFile.ReadAsync("   ").ConfigureAwait(false), "Whitespace repo root returns null");
            });

            await RunTest("LearnedFactsFile_ReadAsync_NonexistentRepoRoot_ReturnsNull", async () =>
            {
                string repoRoot = CreateTempRepoRoot(); // never created on disk
                string? result = await LearnedFactsFile.ReadAsync(repoRoot).ConfigureAwait(false);
                AssertNull(result, "Missing file returns null");
            });

            await RunTest("LearnedFactsFile_ReadAsync_TemplateWithSurroundingWhitespace_ReturnsNull", async () =>
            {
                string repoRoot = CreateTempRepoRoot();
                try
                {
                    // Trim-normalization: leading/trailing whitespace around the template is still template-only.
                    WriteLearnedFile(repoRoot, "  \n" + LearnedFactsFile.DefaultTemplateContent + "\n  ");
                    string? result = await LearnedFactsFile.ReadAsync(repoRoot).ConfigureAwait(false);
                    AssertNull(result, "Whitespace-padded template should normalize to null");
                }
                finally
                {
                    DeleteTempRepoRoot(repoRoot);
                }
            });

            await RunTest("LearnedFactsFile_ReadAsync_RealContentPrefixedByTemplate_ReturnsContent", async () =>
            {
                string repoRoot = CreateTempRepoRoot();
                try
                {
                    // Content that begins with the template but carries real facts is NOT template-only.
                    string content = LearnedFactsFile.DefaultTemplateContent + "\n\n[high] A real accepted fact.";
                    WriteLearnedFile(repoRoot, content);
                    string? result = await LearnedFactsFile.ReadAsync(repoRoot).ConfigureAwait(false);
                    AssertNotNull(result, "Template-plus-real-facts must not be suppressed");
                    AssertContains("A real accepted fact", result!, "Real fact content should be returned verbatim");
                }
                finally
                {
                    DeleteTempRepoRoot(repoRoot);
                }
            });
        }

        #region Private-Methods

        private static string CreateTempRepoRoot()
        {
            return Path.Combine(Path.GetTempPath(), "armada_lfrp_test_" + Guid.NewGuid().ToString("N"));
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

        private static string SanitizeName(string name)
        {
            string lower = name.ToLowerInvariant();
            string replaced = Regex.Replace(lower, "[^a-z0-9]+", "-");
            return replaced.Trim('-');
        }

        #endregion
    }
}
