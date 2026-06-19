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
