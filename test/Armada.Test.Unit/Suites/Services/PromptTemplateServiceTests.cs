namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading.Tasks;
    using SyslogLogging;
    using Armada.Core.Database;
    using Armada.Core.Database.Sqlite;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// Tests for the PromptTemplateService: seeding, resolving, rendering, resetting, and listing templates.
    /// </summary>
    public class PromptTemplateServiceTests : TestSuite
    {
        /// <summary>
        /// Suite name.
        /// </summary>
        public override string Name => "Prompt Template Service";

        /// <summary>
        /// Run all tests.
        /// </summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("Seed defaults creates all built-in templates", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;

                    PromptTemplateService service = new PromptTemplateService(testDb.Driver, logging);
                    await service.SeedDefaultsAsync().ConfigureAwait(false);

                    List<PromptTemplate> templates = await service.ListAsync().ConfigureAwait(false);
                    AssertTrue(templates.Count >= 11, "Expected at least 11 built-in templates, got " + templates.Count);

                    // Verify some known template names exist
                    List<string> names = templates.Select(t => t.Name).ToList();
                    AssertTrue(names.Contains("mission.rules"), "Should contain mission.rules");
                    AssertTrue(names.Contains("agent.launch_prompt"), "Should contain agent.launch_prompt");
                    AssertTrue(names.Contains("persona.worker"), "Should contain persona.worker");
                    AssertTrue(names.Contains("persona.architect"), "Should contain persona.architect");
                    AssertTrue(names.Contains("persona.judge"), "Should contain persona.judge");
                }
            });

            await RunTest("Seed defaults includes specialist persona templates", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;

                    PromptTemplateService service = new PromptTemplateService(testDb.Driver, logging);
                    await service.SeedDefaultsAsync().ConfigureAwait(false);

                    List<PromptTemplate> personaTemplates = await service.ListAsync("persona").ConfigureAwait(false);
                    List<string> names = personaTemplates.Select(t => t.Name).ToList();
                    List<string> specialistNames = new List<string>
                    {
                        "persona.diagnostic_protocol_reviewer",
                        "persona.tenant_security_reviewer",
                        "persona.migration_data_reviewer",
                        "persona.performance_memory_reviewer",
                        "persona.porting_reference_analyst",
                        "persona.frontend_workflow_reviewer"
                    };

                    foreach (string name in specialistNames)
                    {
                        AssertTrue(names.Contains(name), "Should contain " + name);

                        PromptTemplate? template = personaTemplates.FirstOrDefault(t => t.Name == name);
                        AssertNotNull(template, "Template should be listed by persona category");
                        AssertEqual("persona", template!.Category, "Specialist template category");
                        AssertTrue(template.IsBuiltIn, "Specialist template should be built in");
                        AssertContains("[ARMADA:RESULT] COMPLETE", template.Content, "Specialist template should include completion signal contract");
                    }
                }
            });

            await RunTest("Resolve falls back to specialist embedded defaults when unseeded", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;

                    PromptTemplateService service = new PromptTemplateService(testDb.Driver, logging);
                    Dictionary<string, string> expectedRoleNames = new Dictionary<string, string>
                    {
                        { "persona.diagnostic_protocol_reviewer", "DiagnosticProtocolReviewer" },
                        { "persona.tenant_security_reviewer", "TenantSecurityReviewer" },
                        { "persona.migration_data_reviewer", "MigrationDataReviewer" },
                        { "persona.performance_memory_reviewer", "PerformanceMemoryReviewer" },
                        { "persona.porting_reference_analyst", "PortingReferenceAnalyst" },
                        { "persona.frontend_workflow_reviewer", "FrontendWorkflowReviewer" }
                    };

                    foreach (KeyValuePair<string, string> kvp in expectedRoleNames)
                    {
                        PromptTemplate? resolved = await service.ResolveAsync(kvp.Key).ConfigureAwait(false);
                        AssertNotNull(resolved, "Specialist template should resolve from embedded defaults: " + kvp.Key);
                        AssertEqual(kvp.Key, resolved!.Name, "Specialist template name");
                        AssertEqual("persona", resolved.Category, "Specialist template category");
                        AssertTrue(resolved.IsBuiltIn, "Embedded specialist template should be built in");
                        AssertContains(kvp.Value, resolved.Content, "Specialist content should identify the role");
                        AssertContains("{Diff}", resolved.Content, "Specialist content should include diff placeholder");
                        AssertContains("{PreviousStageOutput}", resolved.Content, "Specialist content should include previous stage placeholder");
                        AssertContains("[ARMADA:RESULT] COMPLETE", resolved.Content, "Specialist content should include completion signal");
                    }
                }
            });

            await RunTest("Seed defaults preserves existing template content", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;

                    PromptTemplate existing = new PromptTemplate("persona.diagnostic_protocol_reviewer", "CUSTOM CONTENT");
                    existing.Category = "custom";
                    existing.Description = "Custom reviewer";
                    existing.IsBuiltIn = false;
                    await testDb.Driver.PromptTemplates.CreateAsync(existing).ConfigureAwait(false);

                    PromptTemplateService service = new PromptTemplateService(testDb.Driver, logging);
                    await service.SeedDefaultsAsync().ConfigureAwait(false);

                    PromptTemplate? resolved = await service.ResolveAsync("persona.diagnostic_protocol_reviewer").ConfigureAwait(false);
                    AssertNotNull(resolved, "Resolved template should not be null");
                    AssertEqual("CUSTOM CONTENT", resolved!.Content, "Seeding should preserve database-edited content");
                    AssertEqual("persona", resolved.Category, "Seeding should reconcile built-in category metadata");
                    AssertTrue(resolved.IsBuiltIn, "Seeding should reconcile built-in metadata");
                }
            });

            await RunTest("Resolve returns database template when exists", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;

                    PromptTemplateService service = new PromptTemplateService(testDb.Driver, logging);
                    await service.SeedDefaultsAsync().ConfigureAwait(false);

                    PromptTemplate? resolved = await service.ResolveAsync("mission.rules").ConfigureAwait(false);
                    AssertNotNull(resolved, "Resolved template should not be null");
                    AssertEqual("mission.rules", resolved!.Name, "Template name");
                    AssertTrue(resolved.Content.Contains("## Rules"), "Content should contain '## Rules'");
                }
            });

            await RunTest("Resolve falls back to embedded default", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;

                    // Do NOT seed -- the database is empty, so resolve should fall back to embedded defaults
                    PromptTemplateService service = new PromptTemplateService(testDb.Driver, logging);

                    PromptTemplate? resolved = await service.ResolveAsync("mission.rules").ConfigureAwait(false);
                    AssertNotNull(resolved, "Resolved template should not be null even without seeding");
                    AssertEqual("mission.rules", resolved!.Name, "Template name");
                    AssertTrue(resolved.Content.Length > 0, "Content should not be empty");
                }
            });

            await RunTest("Mission rules embedded default constrains file scope", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;

                    PromptTemplateService service = new PromptTemplateService(testDb.Driver, logging);

                    PromptTemplate? resolved = await service.ResolveAsync("mission.rules").ConfigureAwait(false);
                    AssertNotNull(resolved, "Resolved template should not be null");
                    AssertContains("Stay strictly within the mission scope and listed files", resolved!.Content, "Mission rules should explicitly constrain scope to the assigned files");
                    AssertContains("report it in your result instead of expanding scope on your own", resolved.Content, "Mission rules should tell agents to report needed out-of-scope changes instead of freelancing");
                }
            });

            await RunTest("Judge and test engineer embedded defaults require structured risk-aware review", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;

                    PromptTemplateService service = new PromptTemplateService(testDb.Driver, logging);

                    PromptTemplate? judge = await service.ResolveAsync("persona.judge").ConfigureAwait(false);
                    AssertNotNull(judge, "Judge template should resolve");
                    AssertContains("## Completeness", judge!.Content, "Judge template should require a Completeness section");
                    AssertContains("## Failure Modes", judge.Content, "Judge template should require a Failure Modes section");
                    AssertContains("PASS is not allowed", judge.Content, "Judge template should constrain PASS when review is incomplete");

                    PromptTemplate? testEngineer = await service.ResolveAsync("persona.test_engineer").ConfigureAwait(false);
                    AssertNotNull(testEngineer, "Test engineer template should resolve");
                    AssertContains("negative or edge-path test", testEngineer!.Content, "Test engineer template should require negative-path coverage");
                    AssertContains("## Coverage Added", testEngineer.Content, "Test engineer template should request a coverage summary section");
                    AssertContains("residual risk", testEngineer.Content, "Test engineer template should require residual risk notes");
                }
            });

            await RunTest("Memory consolidator embedded default declares evidence and write-surface restrictions", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;

                    PromptTemplateService service = new PromptTemplateService(testDb.Driver, logging);

                    PromptTemplate? resolved = await service.ResolveAsync("persona.memory_consolidator").ConfigureAwait(false);
                    AssertNotNull(resolved, "Memory consolidator template should resolve from embedded defaults");
                    AssertEqual("persona.memory_consolidator", resolved!.Name, "Memory consolidator template name");
                    AssertEqual("persona", resolved.Category, "Memory consolidator template category");
                    AssertTrue(resolved.IsBuiltIn, "Memory consolidator template should be built in");
                    AssertContains("MemoryConsolidator", resolved.Content, "Memory consolidator content should identify the role");
                    AssertContains("read-only", resolved.Content, "Memory consolidator content should describe read-only evidence surface");
                    AssertContains("AgentOutput", resolved.Content, "Memory consolidator content should restrict writes to AgentOutput");
                    AssertContains("reflections-candidate", resolved.Content, "Memory consolidator content should require the reflections-candidate block");
                    AssertContains("reflections-diff", resolved.Content, "Memory consolidator content should require the reflections-diff block");
                    AssertContains("CLAUDE.md", resolved.Content, "Memory consolidator content should explicitly forbid CLAUDE.md edits");
                    AssertContains("[ARMADA:RESULT] COMPLETE", resolved.Content, "Memory consolidator content should include completion signal");
                    AssertTrue(IsAscii(resolved.Content), "Memory consolidator content should be ASCII only");
                }
            });

            await RunTest("Seed defaults includes memory consolidator persona template", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;

                    PromptTemplateService service = new PromptTemplateService(testDb.Driver, logging);
                    await service.SeedDefaultsAsync().ConfigureAwait(false);

                    List<PromptTemplate> personaTemplates = await service.ListAsync("persona").ConfigureAwait(false);
                    PromptTemplate? seeded = personaTemplates.FirstOrDefault(t => t.Name == "persona.memory_consolidator");
                    AssertNotNull(seeded, "Memory consolidator template should be seeded under the persona category");
                    AssertEqual("persona", seeded!.Category, "Seeded memory consolidator category");
                    AssertTrue(seeded.IsBuiltIn, "Seeded memory consolidator should be built in");
                }
            });

            await RunTest("Render substitutes placeholders", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;

                    PromptTemplateService service = new PromptTemplateService(testDb.Driver, logging);
                    await service.SeedDefaultsAsync().ConfigureAwait(false);

                    Dictionary<string, string> parameters = new Dictionary<string, string>
                    {
                        { "MissionTitle", "Test" },
                        { "MissionDescription", "A test mission description." }
                    };

                    string rendered = await service.RenderAsync("agent.launch_prompt", parameters).ConfigureAwait(false);
                    AssertContains("Test", rendered, "Rendered output should contain substituted MissionTitle");
                    AssertContains("A test mission description.", rendered, "Rendered output should contain substituted MissionDescription");
                }
            });

            await RunTest("Reset to default restores original content", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;

                    PromptTemplateService service = new PromptTemplateService(testDb.Driver, logging);
                    await service.SeedDefaultsAsync().ConfigureAwait(false);

                    // Read the original content
                    PromptTemplate? original = await service.ResolveAsync("mission.rules").ConfigureAwait(false);
                    AssertNotNull(original, "Original template should not be null");
                    string originalContent = original!.Content;

                    // Modify the template content in the database
                    original.Content = "MODIFIED CONTENT";
                    await testDb.Driver.PromptTemplates.UpdateAsync(original).ConfigureAwait(false);

                    // Verify modification took effect
                    PromptTemplate? modified = await service.ResolveAsync("mission.rules").ConfigureAwait(false);
                    AssertEqual("MODIFIED CONTENT", modified!.Content, "Content should be modified");

                    // Reset to default
                    PromptTemplate? reset = await service.ResetToDefaultAsync("mission.rules").ConfigureAwait(false);
                    AssertNotNull(reset, "Reset template should not be null");
                    AssertEqual(originalContent, reset!.Content, "Content should be restored to original");
                }
            });

            await RunTest("List returns all templates", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;

                    PromptTemplateService service = new PromptTemplateService(testDb.Driver, logging);
                    await service.SeedDefaultsAsync().ConfigureAwait(false);

                    List<PromptTemplate> templates = await service.ListAsync().ConfigureAwait(false);
                    AssertTrue(templates.Count >= 11, "Expected at least 11 templates, got " + templates.Count);
                }
            });

            await RunTest("List by category filters correctly", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;

                    PromptTemplateService service = new PromptTemplateService(testDb.Driver, logging);
                    await service.SeedDefaultsAsync().ConfigureAwait(false);

                    List<PromptTemplate> personaTemplates = await service.ListAsync("persona").ConfigureAwait(false);
                    AssertTrue(personaTemplates.Count > 0, "Should have at least one persona template");

                    foreach (PromptTemplate template in personaTemplates)
                    {
                        AssertEqual("persona", template.Category, "Category for template " + template.Name);
                    }
                }
            });
        }

        private static bool IsAscii(string value)
        {
            if (value == null) return true;
            foreach (char c in value)
            {
                if (c > 127) return false;
            }
            return true;
        }
    }
}
