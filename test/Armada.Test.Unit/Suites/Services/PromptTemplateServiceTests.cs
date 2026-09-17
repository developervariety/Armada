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
    using FleetRoutingSettings = global::Test.Shared.Infrastructure.FleetRoutingSettings;

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
                    AssertTrue(templates.Count >= 13, "Expected at least 13 built-in templates, got " + templates.Count);

                    // Verify some known template names exist
                    List<string> names = templates.Select(t => t.Name).ToList();
                    AssertTrue(names.Contains("mission.rules"), "Should contain mission.rules");
                    AssertTrue(names.Contains("agent.launch_prompt"), "Should contain agent.launch_prompt");
                    AssertTrue(names.Contains("persona.worker"), "Should contain persona.worker");
                    AssertTrue(names.Contains("persona.architect"), "Should contain persona.architect");
                    AssertTrue(names.Contains("persona.product_manager"), "Should contain persona.product_manager");
                    AssertTrue(names.Contains("persona.usability_engineer"), "Should contain persona.usability_engineer");
                    AssertTrue(names.Contains("persona.judge"), "Should contain persona.judge");
                }
            });

            await RunTest("Seed defaults includes an Ask system prompt that forbids claiming tools it lacks", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;

                    PromptTemplateService service = new PromptTemplateService(testDb.Driver, logging);
                    await service.SeedDefaultsAsync().ConfigureAwait(false);

                    PromptTemplate? ask = await service.ResolveAsync("ask.system").ConfigureAwait(false);
                    AssertNotNull(ask, "ask.system is seeded, so every Ask chat turn carries the guard");
                    AssertTrue(ask!.IsBuiltIn, "ask.system is a built-in template that Reset restores");
                    AssertContains("Only use tools that are actually provided to you in this session", ask.Content, "the prompt limits the assistant to tools it was given");
                    AssertContains("Never claim to have tools", ask.Content, "the prompt forbids claiming tool access");
                    AssertContains("say so in one sentence", ask.Content, "the prompt tells the assistant how to answer when it has no tool");
                    AssertEqual(ask.Content, service.GetEmbeddedDefault("ask.system"), "the seeded content is the embedded default");
                }
            });

            await RunTest("Seed defaults includes specialist persona templates", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;

                    PromptTemplateService service = new PromptTemplateService(testDb.Driver, logging, FleetRoutingSettings.CreateAdditionalPromptTemplates());
                    await service.SeedDefaultsAsync().ConfigureAwait(false);

                    List<PromptTemplate> personaTemplates = await service.ListAsync("persona").ConfigureAwait(false);
                    List<string> names = personaTemplates.Select(t => t.Name).ToList();
                    List<string> specialistNames = new List<string>
                    {
                        "persona.diagnostic_protocol_reviewer",
                        "persona.tenant_security_reviewer",
                        "persona.porting_reference_analyst"
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

                    PromptTemplateService service = new PromptTemplateService(testDb.Driver, logging, FleetRoutingSettings.CreateAdditionalPromptTemplates());
                    Dictionary<string, string> expectedRoleNames = new Dictionary<string, string>
                    {
                        { "persona.diagnostic_protocol_reviewer", "DiagnosticProtocolReviewer" },
                        { "persona.tenant_security_reviewer", "TenantSecurityReviewer" },
                        { "persona.porting_reference_analyst", "PortingReferenceAnalyst" }
                    };

                    foreach (KeyValuePair<string, string> kvp in expectedRoleNames)
                    {
                        PromptTemplate? resolved = await service.ResolveAsync(kvp.Key).ConfigureAwait(false);
                        AssertNotNull(resolved, "Specialist template should resolve from embedded defaults: " + kvp.Key);
                        AssertEqual(kvp.Key, resolved!.Name, "Specialist template name");
                        AssertEqual("persona", resolved.Category, "Specialist template category");
                        AssertTrue(resolved.IsBuiltIn, "Embedded specialist template should be built in");
                        AssertContains(kvp.Value, resolved.Content, "Specialist content should identify the role");
                        AssertContains("Review the diff and prior-stage output carried in your mission description", resolved.Content, "Specialist content should point at the diff carried in the mission description");
                        AssertFalse(resolved.Content.Contains("{Diff}"), "Specialist content should not carry an unsubstituted diff placeholder");
                        AssertFalse(resolved.Content.Contains("{PreviousStageOutput}"), "Specialist content should not carry an unsubstituted previous-stage placeholder");
                        AssertContains("[ARMADA:RESULT] COMPLETE", resolved.Content, "Specialist content should include completion signal");
                    }
                }
            });

            await RunTest("Diagnostic protocol reviewer template focus is domain-neutral", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;

                    PromptTemplateService service = new PromptTemplateService(testDb.Driver, logging, FleetRoutingSettings.CreateAdditionalPromptTemplates());

                    // The embedded template renders its focus string and guidance bullets into
                    // the Specialist Focus / Review Checklist sections of Content. Guard against a
                    // regression back to domain-specific framing by asserting the domain-neutral
                    // vocabulary that replaced it; the persona/template NAME mapping is covered above.
                    PromptTemplate? resolved = await service.ResolveAsync("persona.diagnostic_protocol_reviewer").ConfigureAwait(false);
                    AssertNotNull(resolved, "Diagnostic protocol reviewer template should resolve from embedded defaults");
                    string content = resolved!.Content;
                    AssertContains("hardware-affecting operations", content, "Template focus should describe high-risk hardware-affecting scope");
                    AssertContains("security-sensitive access", content, "Template checklist should describe security-sensitive access scope");
                    AssertContains("high-risk safety boundary", content, "Template checklist should describe the high-risk safety boundary");
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

                    PromptTemplateService service = new PromptTemplateService(testDb.Driver, logging, FleetRoutingSettings.CreateAdditionalPromptTemplates());
                    await service.SeedDefaultsAsync().ConfigureAwait(false);

                    PromptTemplate? resolved = await service.ResolveAsync("persona.diagnostic_protocol_reviewer").ConfigureAwait(false);
                    AssertNotNull(resolved, "Resolved template should not be null");
                    // Seeding keeps operator content and only adds a missing section: the memory-recall
                    // note, which every built-in working persona carries.
                    AssertStartsWith("CUSTOM CONTENT", resolved!.Content, "Seeding should preserve database-edited content");
                    AssertContains("## Recall Existing Memory", resolved.Content, "Seeding adds the missing recall note");
                    AssertEqual(1, resolved.Content.Split(new[] { "## Recall Existing Memory" }, StringSplitOptions.None).Length - 1, "The note is added once");
                    AssertEqual("persona", resolved.Category, "Seeding should reconcile built-in category metadata");
                    AssertTrue(resolved.IsBuiltIn, "Seeding should reconcile built-in metadata");
                }
            });

            await RunTest("Content upgrader upgrades a superseded row and leaves an operator edit alone", () =>
            {
                string embedded = "## Rules\nline one\nline two\n";
                string prior = "## Rules\nline one\n";
                string operatorEdit = "## Rules\nline one\nOPERATOR ADDED\n";
                IReadOnlyList<string> priors = new List<string> { PromptTemplateService.HashContent(prior) };

                AssertEqual(PromptTemplateService.TemplateContentDecision.Current,
                    PromptTemplateService.ClassifyBuiltInContent(embedded, embedded, priors),
                    "A row equal to the current embedded default is Current");
                AssertEqual(PromptTemplateService.TemplateContentDecision.Upgrade,
                    PromptTemplateService.ClassifyBuiltInContent(prior, embedded, priors),
                    "A row equal to a recorded prior version is upgraded");
                AssertEqual(PromptTemplateService.TemplateContentDecision.Drift,
                    PromptTemplateService.ClassifyBuiltInContent(operatorEdit, embedded, priors),
                    "A row matching no known version is an operator edit and is left alone");
                // Without a recorded prior hash, even a genuine old version is treated as drift, so an
                // un-catalogued row is never silently overwritten.
                AssertEqual(PromptTemplateService.TemplateContentDecision.Drift,
                    PromptTemplateService.ClassifyBuiltInContent(prior, embedded, new List<string>()),
                    "An old version with no recorded prior hash is drift, never a silent overwrite");
                return Task.CompletedTask;
            });

            await RunTest("Fleet guard rules are appended to an operator-edited mission.rules and preserve its content", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;

                    PromptTemplate existing = new PromptTemplate("mission.rules", "## Rules\n- Operator custom rule\n");
                    existing.Category = "mission";
                    existing.IsBuiltIn = true;
                    await testDb.Driver.PromptTemplates.CreateAsync(existing).ConfigureAwait(false);

                    PromptTemplateService service = new PromptTemplateService(testDb.Driver, logging);
                    await service.SeedDefaultsAsync().ConfigureAwait(false);

                    PromptTemplate? resolved = await service.ResolveAsync("mission.rules").ConfigureAwait(false);
                    AssertNotNull(resolved, "mission.rules should resolve");
                    AssertContains("- Operator custom rule", resolved!.Content, "the operator edit is preserved");
                    AssertContains("Never delete a `recover/` ref", resolved.Content, "the recover-ref rule is appended");
                    AssertContains("Never write a mission, voyage, or objective id into committed content", resolved.Content, "the no-ids rule is appended");

                    // Idempotent: a second seed does not append the rules again.
                    await service.SeedDefaultsAsync().ConfigureAwait(false);
                    PromptTemplate? again = await service.ResolveAsync("mission.rules").ConfigureAwait(false);
                    AssertEqual(1, again!.Content.Split(new[] { "Never delete a `recover/` ref" }, StringSplitOptions.None).Length - 1, "the rule is appended exactly once");
                }
            });

            await RunTest("Fresh-seeded mission.rules carries the fleet guard rules", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;
                    PromptTemplateService service = new PromptTemplateService(testDb.Driver, logging);
                    await service.SeedDefaultsAsync().ConfigureAwait(false);
                    PromptTemplate? rules = await service.ResolveAsync("mission.rules").ConfigureAwait(false);
                    AssertContains("Never delete a `recover/` ref", rules!.Content, "fresh mission.rules carries the recover-ref rule");
                    AssertContains("Never write a mission, voyage, or objective id into committed content", rules.Content, "fresh mission.rules carries the no-ids rule");
                }
            });

            await RunTest("Blocked-path guidance reaches working personas, not the Recorder or Judge, and preserves operator edits", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;

                    PromptTemplate workerEdit = new PromptTemplate("persona.worker", "You are a worker. OPERATOR CUSTOM.\n");
                    workerEdit.Category = "persona";
                    workerEdit.IsBuiltIn = true;
                    await testDb.Driver.PromptTemplates.CreateAsync(workerEdit).ConfigureAwait(false);

                    PromptTemplateService service = new PromptTemplateService(testDb.Driver, logging, FleetRoutingSettings.CreateAdditionalPromptTemplates());
                    await service.SeedDefaultsAsync().ConfigureAwait(false);

                    PromptTemplate? worker = await service.ResolveAsync("persona.worker").ConfigureAwait(false);
                    AssertStartsWith("You are a worker. OPERATOR CUSTOM.", worker!.Content, "the operator edit is preserved");
                    AssertContains("## When You Cannot Finish", worker.Content, "the worker gains the blocked path");
                    AssertContains("`[ARMADA:RESULT] BLOCKED`", worker.Content, "the blocked path names the BLOCKED signal");

                    PromptTemplate? testEngineer = await service.ResolveAsync("persona.test_engineer").ConfigureAwait(false);
                    AssertContains("## When You Cannot Finish", testEngineer!.Content, "the test engineer gains the blocked path");

                    PromptTemplate? recorder = await service.ResolveAsync("persona.recorder").ConfigureAwait(false);
                    AssertTrue(!recorder!.Content.Contains("## When You Cannot Finish"), "the Recorder does not get the blocked path");
                    PromptTemplate? judge = await service.ResolveAsync("persona.judge").ConfigureAwait(false);
                    AssertTrue(!judge!.Content.Contains("## When You Cannot Finish"), "the Judge does not get the blocked path");

                    // Idempotent.
                    await service.SeedDefaultsAsync().ConfigureAwait(false);
                    PromptTemplate? again = await service.ResolveAsync("persona.worker").ConfigureAwait(false);
                    AssertEqual(1, again!.Content.Split(new[] { "## When You Cannot Finish" }, StringSplitOptions.None).Length - 1, "the blocked path is added once");
                }
            });

            await RunTest("The Linter duplication check reaches seeded and existing rows once, keeps operator edits and the finding headings", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;

                    PromptTemplate linterEdit = new PromptTemplate("persona.linter", "You are a linter. OPERATOR CUSTOM.\n## Code Style\nNone\n## Residual Issues\nNone\n");
                    linterEdit.Category = "persona";
                    linterEdit.IsBuiltIn = true;
                    await testDb.Driver.PromptTemplates.CreateAsync(linterEdit).ConfigureAwait(false);

                    PromptTemplateService service = new PromptTemplateService(testDb.Driver, logging, FleetRoutingSettings.CreateAdditionalPromptTemplates());
                    await service.SeedDefaultsAsync().ConfigureAwait(false);

                    PromptTemplate? linter = await service.ResolveAsync("persona.linter").ConfigureAwait(false);
                    AssertStartsWith("You are a linter. OPERATOR CUSTOM.", linter!.Content, "the operator edit is preserved");
                    AssertContains("## Duplication Check", linter.Content, "the duplication check reaches an existing built-in row");
                    AssertContains("armada_mission_code_search", linter.Content, "the check names the captain search tool");
                    AssertContains("Never fix a duplicate", linter.Content, "the check is report-only");
                    AssertContains("do not report that no duplication exists", linter.Content, "an unavailable index is never read as no duplication");
                    AssertContains("## Residual Issues", linter.Content, "the finding-section headings are preserved");

                    await service.SeedDefaultsAsync().ConfigureAwait(false);
                    PromptTemplate? reseeded = await service.ResolveAsync("persona.linter").ConfigureAwait(false);
                    int occurrences = reseeded!.Content.Split("## Duplication Check").Length - 1;
                    AssertEqual(1, occurrences, "a second seed does not append the section again");

                    PromptTemplate? embedded = await new PromptTemplateService(testDb.Driver, logging, FleetRoutingSettings.CreateAdditionalPromptTemplates())
                        .ResetToDefaultAsync("persona.linter").ConfigureAwait(false);
                    AssertContains("## Duplication Check", embedded!.Content, "the embedded default carries the check");
                }
            });

            await RunTest("D22/D24 asks reach the TestEngineer and Linter, preserve operator edits, and keep the finding headings", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;

                    PromptTemplate linterEdit = new PromptTemplate("persona.linter", "You are a linter. OPERATOR CUSTOM.\n## Code Style\nNone\n");
                    linterEdit.Category = "persona";
                    linterEdit.IsBuiltIn = true;
                    await testDb.Driver.PromptTemplates.CreateAsync(linterEdit).ConfigureAwait(false);

                    PromptTemplateService service = new PromptTemplateService(testDb.Driver, logging, FleetRoutingSettings.CreateAdditionalPromptTemplates());
                    await service.SeedDefaultsAsync().ConfigureAwait(false);

                    PromptTemplate? testEngineer = await service.ResolveAsync("persona.test_engineer").ConfigureAwait(false);
                    AssertContains("## Coverage Evidence", testEngineer!.Content, "the D22 coverage-evidence ask reaches the TestEngineer");
                    AssertContains("fails without the change", testEngineer.Content, "the D22 ask names the failing-without-change requirement");

                    PromptTemplate? linter = await service.ResolveAsync("persona.linter").ConfigureAwait(false);
                    AssertStartsWith("You are a linter. OPERATOR CUSTOM.", linter!.Content, "the operator edit is preserved");
                    AssertContains("## Finding Format", linter.Content, "the D24 finding-format ask reaches the Linter");
                    AssertContains("style_preference", linter.Content, "the D24 ask names the finding classes");
                    AssertContains("blocks_merge", linter.Content, "the D24 ask names the severities");
                    // The section headings the finding parser (ExtractLintFindings) reads are untouched.
                    AssertContains("## Code Style", linter.Content, "the finding-section headings are preserved");

                    await service.SeedDefaultsAsync().ConfigureAwait(false);
                    PromptTemplate? again = await service.ResolveAsync("persona.linter").ConfigureAwait(false);
                    AssertEqual(1, again!.Content.Split(new[] { "## Finding Format" }, StringSplitOptions.None).Length - 1, "the finding-format section is added once");
                }
            });

            await RunTest("Working persona templates carry the memory-recall note; the Recorder does not", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;

                    PromptTemplateService service = new PromptTemplateService(testDb.Driver, logging, FleetRoutingSettings.CreateAdditionalPromptTemplates());
                    await service.SeedDefaultsAsync().ConfigureAwait(false);

                    foreach (string name in new[] { "persona.worker", "persona.architect", "persona.judge", "persona.test_engineer", "persona.product_manager", "persona.usability_engineer" })
                    {
                        PromptTemplate? working = await service.ResolveAsync(name).ConfigureAwait(false);
                        AssertNotNull(working, "Template should resolve: " + name);
                        AssertContains("## Recall Existing Memory", working!.Content, name + " should carry the recall note");
                        AssertContains("search_memory", working.Content, name + " should name the recall tool");
                    }

                    PromptTemplate? recorder = await service.ResolveAsync("persona.recorder").ConfigureAwait(false);
                    AssertNotNull(recorder, "persona.recorder should be seeded");
                    AssertFalse(recorder!.Content.Contains("## Recall Existing Memory", StringComparison.Ordinal), "The Recorder writes memory and takes no recall note");
                    AssertContains("create_memory", recorder.Content, "The Recorder should name the write tool");

                }
            });

            await RunTest("The recall note states that shared memory wins on conflict", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;

                    PromptTemplateService service = new PromptTemplateService(testDb.Driver, logging);
                    await service.SeedDefaultsAsync().ConfigureAwait(false);

                    PromptTemplate? worker = await service.ResolveAsync("persona.worker").ConfigureAwait(false);
                    AssertNotNull(worker, "persona.worker should be seeded");
                    AssertContains("Shared Memory section", worker!.Content, "The note should point at the shared memory section of the brief");
                    AssertContains("wins over a memory record on conflict", worker.Content, "The note should state which side wins");

                    PromptTemplate? recorder = await service.ResolveAsync("persona.recorder").ConfigureAwait(false);
                    AssertContains("wins over a memory record on conflict", recorder!.Content, "The Recorder should state the same rule");
                    AssertContains("do not commit", recorder.Content, "The Recorder should not change the repository");
                }
            });

            await RunTest("The recall note reaches an older built-in template exactly once", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;

                    PromptTemplate legacy = new PromptTemplate("persona.worker", "You are a worker. Do the work.");
                    legacy.Category = "persona";
                    legacy.IsBuiltIn = true;
                    await testDb.Driver.PromptTemplates.CreateAsync(legacy).ConfigureAwait(false);

                    PromptTemplate custom = new PromptTemplate("persona.house_style", "An operator's own persona.");
                    custom.Category = "persona";
                    custom.IsBuiltIn = false;
                    await testDb.Driver.PromptTemplates.CreateAsync(custom).ConfigureAwait(false);

                    PromptTemplateService service = new PromptTemplateService(testDb.Driver, logging);
                    await service.SeedDefaultsAsync().ConfigureAwait(false);
                    await service.SeedDefaultsAsync().ConfigureAwait(false);

                    PromptTemplate? worker = await service.ResolveAsync("persona.worker").ConfigureAwait(false);
                    AssertNotNull(worker, "persona.worker should exist");
                    AssertContains("You are a worker. Do the work.", worker!.Content, "The operator's own content is kept");
                    int occurrences = worker.Content.Split(new[] { "## Recall Existing Memory" }, StringSplitOptions.None).Length - 1;
                    AssertEqual(1, occurrences, "The note is added once, however often startup runs");

                    PromptTemplate? untouched = await service.ResolveAsync("persona.house_style").ConfigureAwait(false);
                    AssertEqual("An operator's own persona.", untouched!.Content, "A template that is not built in is left alone");
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
                    AssertTrue(resolved.Content.Contains("refs/heads", StringComparison.Ordinal),
                        "mission.rules must teach the fully-qualified push form for detached checkouts");
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
                    AssertContains("Delivery is proven by the DIFF, not by the tree", judge.Content, "Judge template must say delivery is proven by the diff, not by presence at the tip");
                    AssertContains("cite the diff hunk", judge.Content, "Judge template must demand a diff hunk per requirement");

                    PromptTemplate? testEngineer = await service.ResolveAsync("persona.test_engineer").ConfigureAwait(false);
                    AssertNotNull(testEngineer, "Test engineer template should resolve");
                    AssertContains("negative or edge-path test", testEngineer!.Content, "Test engineer template should require negative-path coverage");
                    AssertContains("## Coverage Added", testEngineer.Content, "Test engineer template should request a coverage summary section");
                    AssertContains("residual risk", testEngineer.Content, "Test engineer template should require residual risk notes");

                    PromptTemplate? productManager = await service.ResolveAsync("persona.product_manager").ConfigureAwait(false);
                    AssertNotNull(productManager, "Product manager template should resolve");
                    AssertContains("## Product Vision", productManager!.Content, "Product manager template should require a Product Vision section");
                    AssertContains("## Future Readiness", productManager.Content, "Product manager template should require a Future Readiness section");

                    PromptTemplate? usabilityEngineer = await service.ResolveAsync("persona.usability_engineer").ConfigureAwait(false);
                    AssertNotNull(usabilityEngineer, "Usability engineer template should resolve");
                    AssertContains("## Usability", usabilityEngineer!.Content, "Usability engineer template should require a Usability section");
                    AssertContains("## Consistency", usabilityEngineer.Content, "Usability engineer template should require a Consistency section");
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
                    AssertTrue(templates.Count >= 13, "Expected at least 13 templates, got " + templates.Count);
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
