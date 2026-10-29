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
                    AssertContains("recap in one or two sentences what is finished and what remains", ask.Content, "the prompt asks for a closing recap of a multi-step answer");
                    AssertEqual(ask.Content, service.GetEmbeddedDefault("ask.system"), "the seeded content is the embedded default");
                }
            });

            await RunTest("The Judge template's acceptance example passes the acceptance checker, and the forms it forbids fail it", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;
                    PromptTemplateService service = new PromptTemplateService(testDb.Driver, logging);

                    string judge = service.GetEmbeddedDefault("persona.judge") ?? "";
                    const string marker = "Example line: `";
                    int start = judge.IndexOf(marker, StringComparison.Ordinal);
                    AssertTrue(start >= 0, "the Judge template carries an example acceptance line");
                    int lineEnd = judge.IndexOf('\n', start);
                    string exampleLine = judge.Substring(start + marker.Length, lineEnd - start - marker.Length).TrimEnd();
                    exampleLine = exampleLine.Substring(0, exampleLine.Length - 1);

                    string brief = "## Acceptance Criteria\n\n- The parser rejects a truncated frame.\n";
                    AssertNull(JudgeAcceptanceWalk.ValidatePass("## Acceptance Criteria\n\n" + exampleLine + "\n", brief),
                        "the template's own example is accepted by the checker");

                    string rangeWords = "- The parser rejects a truncated frame. \u2014 MET. FrameParser rejects it (lines 88-97).";
                    AssertNotNull(JudgeAcceptanceWalk.ValidatePass("## Acceptance Criteria\n\n" + rangeWords + "\n", brief),
                        "\"lines 88-97\" is not location evidence");
                    string paraphrased = "- The parser rejects truncated frames. \u2014 MET. src/Parser/FrameParser.cs:88";
                    AssertNotNull(JudgeAcceptanceWalk.ValidatePass("## Acceptance Criteria\n\n" + paraphrased + "\n", brief),
                        "a paraphrased criterion does not match");
                }
            });

            await RunTest("Every built-in template's current content is the last entry of its hash history", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;
                    PromptTemplateService service = new PromptTemplateService(testDb.Driver, logging);
                    Dictionary<string, List<string>> history = PromptTemplateService.LoadTemplateHashHistory();

                    // Regenerate mode: append each changed default's hash and write the manifest. The guard
                    // below then passes on the next build, which embeds the written manifest.
                    string? writePath = Environment.GetEnvironmentVariable("ARMADA_WRITE_TEMPLATE_HASHES");
                    if (!String.IsNullOrWhiteSpace(writePath))
                    {
                        SortedDictionary<string, List<string>> updated = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
                        foreach (KeyValuePair<string, List<string>> entry in history) updated[entry.Key] = new List<string>(entry.Value);
                        foreach (string name in service.EmbeddedTemplateNames)
                        {
                            string hash = PromptTemplateService.HashContent(service.GetEmbeddedDefault(name));
                            if (!updated.TryGetValue(name, out List<string>? list)) updated[name] = list = new List<string>();
                            if (list.Count == 0 || !String.Equals(list[list.Count - 1], hash, StringComparison.Ordinal)) list.Add(hash);
                        }
                        System.IO.File.WriteAllText(writePath, System.Text.Json.JsonSerializer.Serialize(updated, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n");
                        return;
                    }

                    List<string> stale = new List<string>();
                    foreach (string name in service.EmbeddedTemplateNames)
                    {
                        string hash = PromptTemplateService.HashContent(service.GetEmbeddedDefault(name));
                        if (!history.TryGetValue(name, out List<string>? list) || list.Count == 0
                            || !String.Equals(list[list.Count - 1], hash, StringComparison.Ordinal))
                            stale.Add(name);
                    }

                    AssertEqual(0, stale.Count,
                        "built-in template default changed without a hash history entry: " + String.Join(", ", stale)
                        + ". Run the unit suite once with ARMADA_WRITE_TEMPLATE_HASHES=<repo>/src/Armada.Core/Resources/BuiltInTemplateHashes.json"
                        + " to append the new hash, so live rows that still hold the earlier default are upgraded on the next deploy.");
                }
            });

            await RunTest("A built-in row holding an earlier default from the hash history is upgraded, and an edited row is left alone", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;
                    PromptTemplateService seeding = new PromptTemplateService(testDb.Driver, logging);
                    await seeding.SeedDefaultsAsync().ConfigureAwait(false);

                    string earlierDefault = "An earlier built-in Judge default the platform wrote.";
                    string operatorEdit = "An operator's own Ask system prompt.";
                    PromptTemplate judge = (await testDb.Driver.PromptTemplates.ReadByNameAsync("persona.judge").ConfigureAwait(false))!;
                    judge.Content = earlierDefault;
                    await testDb.Driver.PromptTemplates.UpdateAsync(judge).ConfigureAwait(false);
                    PromptTemplate ask = (await testDb.Driver.PromptTemplates.ReadByNameAsync("ask.system").ConfigureAwait(false))!;
                    ask.Content = operatorEdit;
                    await testDb.Driver.PromptTemplates.UpdateAsync(ask).ConfigureAwait(false);

                    PromptTemplateService restarted = new PromptTemplateService(testDb.Driver, logging);
                    restarted.TemplateHashHistoryOverride = new Dictionary<string, List<string>>(StringComparer.Ordinal)
                    {
                        ["persona.judge"] = new List<string>
                        {
                            PromptTemplateService.HashContent(earlierDefault),
                            PromptTemplateService.HashContent(restarted.GetEmbeddedDefault("persona.judge"))
                        }
                    };
                    await restarted.SeedDefaultsAsync().ConfigureAwait(false);

                    PromptTemplate judgeAfter = (await testDb.Driver.PromptTemplates.ReadByNameAsync("persona.judge").ConfigureAwait(false))!;
                    AssertEqual(restarted.GetEmbeddedDefault("persona.judge"), judgeAfter.Content, "a row holding an earlier default takes the current default");
                    PromptTemplate askAfter = (await testDb.Driver.PromptTemplates.ReadByNameAsync("ask.system").ConfigureAwait(false))!;
                    AssertEqual(operatorEdit, askAfter.Content, "an operator's edit is never overwritten by the content upgrader");

                    List<ArmadaEvent> drift = await testDb.Driver.Events.EnumerateByTypeAsync("prompt_template.content_drift", 50).ConfigureAwait(false);
                    AssertEqual(1, drift.Count(evt => evt.EntityId == askAfter.Id), "the edited row's drift is reported");
                    PromptTemplateService again = new PromptTemplateService(testDb.Driver, logging);
                    await again.SeedDefaultsAsync().ConfigureAwait(false);
                    drift = await testDb.Driver.Events.EnumerateByTypeAsync("prompt_template.content_drift", 50).ConfigureAwait(false);
                    AssertEqual(1, drift.Count(evt => evt.EntityId == askAfter.Id), "an unchanged drift is not reported again on the next start");
                }
            });

            await RunTest("Seed defaults upgrades the prior TestEngineer prompt hash and preserves a custom prompt row", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;
                    PromptTemplateService initial = new PromptTemplateService(testDb.Driver, logging);
                    await initial.SeedDefaultsAsync().ConfigureAwait(false);

                    string priorDefault = File.ReadAllText(Path.Combine(
                        AppContext.BaseDirectory,
                        "Fixtures",
                        "PromptTemplates",
                        "TestEngineer.previous.txt"));
                    const string priorHash = "0f584b1d0441e3f8913da730961de46c12db1d861ce3d0d55fbd3b4036c88a0a";
                    AssertEqual(priorHash, PromptTemplateService.HashContent(priorDefault),
                        "the historical TestEngineer fixture matches its fixed content hash");
                    Dictionary<string, List<string>> history = PromptTemplateService.LoadTemplateHashHistory();
                    AssertTrue(history.TryGetValue("persona.test_engineer", out List<string>? hashes) && hashes.Contains(priorHash),
                        "the pre-change embedded default hash is recorded as a supported prior version");

                    PromptTemplate previousBuiltIn = (await testDb.Driver.PromptTemplates.ReadByNameAsync("persona.test_engineer").ConfigureAwait(false))!;
                    previousBuiltIn.Content = priorDefault;
                    await testDb.Driver.PromptTemplates.UpdateAsync(previousBuiltIn).ConfigureAwait(false);

                    PromptTemplate custom = new PromptTemplate("persona.test_engineer_operator_custom", "CUSTOM TEST ENGINEER PROMPT")
                    {
                        IsBuiltIn = false
                    };
                    await testDb.Driver.PromptTemplates.CreateAsync(custom).ConfigureAwait(false);

                    PromptTemplateService restarted = new PromptTemplateService(testDb.Driver, logging);
                    await restarted.SeedDefaultsAsync().ConfigureAwait(false);

                    PromptTemplate upgraded = (await testDb.Driver.PromptTemplates.ReadByNameAsync("persona.test_engineer").ConfigureAwait(false))!;
                    AssertEqual(restarted.GetEmbeddedDefault("persona.test_engineer"), upgraded.Content,
                        "a built-in row with the prior embedded hash takes the corrected default");
                    AssertContains("controls whether tests are in scope", upgraded.Content,
                        "the upgraded built-in prompt gives explicit objective scope priority");

                    PromptTemplate? customAfter = await testDb.Driver.PromptTemplates.ReadByNameAsync(custom.Name).ConfigureAwait(false);
                    AssertNotNull(customAfter, "the separate custom prompt row remains present");
                    AssertEqual("CUSTOM TEST ENGINEER PROMPT", customAfter!.Content,
                        "seeding leaves a custom prompt row unchanged");

                    string operatorEdit = upgraded.Content + "\nOPERATOR-SPECIFIC TEST ENGINEER GUIDANCE";
                    upgraded.Content = operatorEdit;
                    await testDb.Driver.PromptTemplates.UpdateAsync(upgraded).ConfigureAwait(false);
                    await restarted.SeedDefaultsAsync().ConfigureAwait(false);
                    PromptTemplate? editedBuiltInAfter = await testDb.Driver.PromptTemplates.ReadByNameAsync("persona.test_engineer").ConfigureAwait(false);
                    AssertNotNull(editedBuiltInAfter, "the customized built-in prompt row remains present");
                    AssertEqual(operatorEdit, editedBuiltInAfter!.Content,
                        "a built-in row with operator-edited content is not overwritten by the new default hash");
                }
            });

            await RunTest("Seed defaults upgrades an untouched Ask system prompt to the propose-and-wait rule", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;
                    PromptTemplateService initial = new PromptTemplateService(testDb.Driver, logging);
                    await initial.SeedDefaultsAsync().ConfigureAwait(false);

                    string priorDefault = File.ReadAllText(Path.Combine(
                        AppContext.BaseDirectory,
                        "Fixtures",
                        "PromptTemplates",
                        "AskSystem.previous.txt"));
                    PromptTemplate row = (await testDb.Driver.PromptTemplates.ReadByNameAsync("ask.system").ConfigureAwait(false))!;
                    row.Content = priorDefault;
                    await testDb.Driver.PromptTemplates.UpdateAsync(row).ConfigureAwait(false);

                    PromptTemplateService restarted = new PromptTemplateService(testDb.Driver, logging);
                    await restarted.SeedDefaultsAsync().ConfigureAwait(false);

                    PromptTemplate upgraded = (await testDb.Driver.PromptTemplates.ReadByNameAsync("ask.system").ConfigureAwait(false))!;
                    AssertEqual(restarted.GetEmbeddedDefault("ask.system"), upgraded.Content,
                        "an untouched installed Ask prompt takes the current default");
                    AssertContains("armada_dispatch", upgraded.Content, "the prompt names the dispatch proposal tool");
                    AssertContains("armada_create_mission", upgraded.Content, "the prompt names the mission proposal tool");
                    AssertContains("Never propose a mission or voyage on your own initiative", upgraded.Content,
                        "the prompt forbids unprompted proposals");
                    AssertFalse(upgraded.Content.Contains("dispatch work unless the operator explicitly asks"),
                        "the superseded dispatch-on-request line is gone");
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

            await RunTest("The no-push mission rules tell a captain in a detached checkout to leave branch refs alone, once", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;

                    PromptTemplate existing = new PromptTemplate("mission.rules_no_push", "## Rules\n- Operator custom rule\n");
                    existing.Category = "mission";
                    existing.IsBuiltIn = true;
                    await testDb.Driver.PromptTemplates.CreateAsync(existing).ConfigureAwait(false);

                    PromptTemplateService service = new PromptTemplateService(testDb.Driver, logging);
                    await service.SeedDefaultsAsync().ConfigureAwait(false);
                    await service.SeedDefaultsAsync().ConfigureAwait(false);

                    const string rule = "In a detached checkout, commit on HEAD and leave branch refs alone";
                    PromptTemplate? edited = await service.ResolveAsync("mission.rules_no_push").ConfigureAwait(false);
                    AssertNotNull(edited, "mission.rules_no_push should resolve");
                    AssertEqual(1, edited!.Content.Split(new[] { rule }, StringSplitOptions.None).Length - 1, "the edited row carries the detached-checkout rule exactly once");
                    AssertContains("- Operator custom rule", edited.Content, "the operator edit is preserved");
                    PromptTemplateService fresh = new PromptTemplateService(testDb.Driver, logging);
                    AssertContains(rule, fresh.GetEmbeddedDefault("mission.rules_no_push") ?? String.Empty, "a fresh no-push default carries the rule");
                }
            });

            await RunTest("Both mission rules name rm without -f and sha256sum for shell file work, once, on an existing row", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;

                    foreach (string name in new[] { "mission.rules", "mission.rules_no_push" })
                    {
                        PromptTemplate existing = new PromptTemplate(name, "## Rules\n- Operator custom rule\n");
                        existing.Category = "mission";
                        existing.IsBuiltIn = true;
                        await testDb.Driver.PromptTemplates.CreateAsync(existing).ConfigureAwait(false);
                    }

                    PromptTemplateService service = new PromptTemplateService(testDb.Driver, logging);
                    await service.SeedDefaultsAsync().ConfigureAwait(false);
                    await service.SeedDefaultsAsync().ConfigureAwait(false);
                    PromptTemplateService fresh = new PromptTemplateService(testDb.Driver, logging);

                    foreach (string name in new[] { "mission.rules", "mission.rules_no_push" })
                    {
                        PromptTemplate? edited = await service.ResolveAsync(name).ConfigureAwait(false);
                        AssertNotNull(edited, name + " should resolve");
                        AssertEqual(1, edited!.Content.Split(new[] { "Delete a temporary file with `rm <path>`" }, StringSplitOptions.None).Length - 1, name + " carries the rm rule exactly once");
                        AssertContains("`sha256sum <path>`", edited.Content, name + " names sha256sum");
                        AssertContains("- Operator custom rule", edited.Content, name + " keeps the operator edit");
                        string embedded = fresh.GetEmbeddedDefault(name) ?? String.Empty;
                        AssertContains("Delete a temporary file with `rm <path>`", embedded, "a fresh " + name + " default carries the rm rule");
                        AssertContains("`sha256sum <path>`", embedded, "a fresh " + name + " default names sha256sum");
                    }
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

            // Unfinished in-scope work is not an owner question. A row that still carries the first blocked-path
            // wording, which left a captain with unfinished work only BLOCKED or a false COMPLETE, gets the
            // current wording in its place.
            await RunTest("An edited working-persona row with the earlier blocked-path wording gets the current wording once", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;

                    string earlier =
                        "\n## When You Cannot Finish\n" +
                        "If you cannot achieve the mission's goal -- missing context, a false premise, or a question " +
                        "only the owner can answer -- end your final response with a standalone `[ARMADA:RESULT] BLOCKED` " +
                        "line followed by the specific blocker or question, instead of `[ARMADA:RESULT] COMPLETE`. Use " +
                        "`[ARMADA:RESULT] REFUSED` only when the request is unsafe or disallowed. Never report COMPLETE " +
                        "for work you did not finish.\n";
                    PromptTemplate workerEdit = new PromptTemplate("persona.worker", "You are a worker. OPERATOR CUSTOM.\n" + earlier);
                    workerEdit.Category = "persona";
                    workerEdit.IsBuiltIn = true;
                    await testDb.Driver.PromptTemplates.CreateAsync(workerEdit).ConfigureAwait(false);

                    PromptTemplateService service = new PromptTemplateService(testDb.Driver, logging, FleetRoutingSettings.CreateAdditionalPromptTemplates());
                    await service.SeedDefaultsAsync().ConfigureAwait(false);
                    await service.SeedDefaultsAsync().ConfigureAwait(false);

                    PromptTemplate? worker = await service.ResolveAsync("persona.worker").ConfigureAwait(false);
                    AssertStartsWith("You are a worker. OPERATOR CUSTOM.", worker!.Content, "the operator edit is kept");
                    AssertContains("Remaining work:", worker.Content, "unfinished in-scope work is committed and listed, not BLOCKED");
                    AssertContains("BLOCKED stops the voyage until the owner answers", worker.Content);
                    AssertFalse(worker.Content.Contains("Never report COMPLETE for work you did not finish", StringComparison.Ordinal), "the earlier wording is replaced");
                    AssertEqual(1, worker.Content.Split(new[] { "## When You Cannot Finish" }, StringSplitOptions.None).Length - 1, "the section appears once");
                }
            });

            await RunTest("The Linter duplication and test checks reach seeded and existing rows once, keep operator edits and the finding headings", async () =>
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
                    AssertContains("armada_search_context", linter.Content, "the check names the captain search tool");
                    AssertContains("4096 characters", linter.Content, "The requested excerpt fits the actual query limit");
                    AssertContains("Never fix a duplicate", linter.Content, "the check is report-only");
                    AssertContains("do not report that no duplication exists", linter.Content, "an unavailable index is never read as no duplication");
                    AssertContains("## Test Check", linter.Content, "the test check reaches an existing built-in row");
                    AssertContains("TEST: <path:line>", linter.Content, "the test check names its finding line");
                    AssertContains("Never delete, skip, or weaken a test", linter.Content, "the test check is report-only");
                    AssertContains("never remove or loosen an assertion", linter.Content, "a reported test keeps every assertion");
                    AssertContains("## Residual Issues", linter.Content, "the finding-section headings are preserved");

                    await service.SeedDefaultsAsync().ConfigureAwait(false);
                    PromptTemplate? reseeded = await service.ResolveAsync("persona.linter").ConfigureAwait(false);
                    foreach (string heading in new[] { "## Duplication Check", "## Test Check" })
                    {
                        int occurrences = reseeded!.Content.Split(heading).Length - 1;
                        AssertEqual(1, occurrences, "a second seed does not append " + heading + " again");
                    }

                    PromptTemplate? embedded = await new PromptTemplateService(testDb.Driver, logging, FleetRoutingSettings.CreateAdditionalPromptTemplates())
                        .ResetToDefaultAsync("persona.linter").ConfigureAwait(false);
                    AssertContains("## Duplication Check", embedded!.Content, "the embedded default carries the duplication check");
                    AssertContains("## Test Check", embedded.Content, "the embedded default carries the test check");
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
                    AssertContains("## Test Authoring Gate", testEngineer.Content, "the authoring gate reaches the TestEngineer");
                    AssertContains("why the existing tests miss that regression", testEngineer.Content, "the gate asks why existing coverage misses the regression");
                    AssertContains("Never remove or weaken an existing test", testEngineer.Content, "the gate never trades an existing test for a new one");

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
                    PromptTemplate? testEngineerAgain = await service.ResolveAsync("persona.test_engineer").ConfigureAwait(false);
                    AssertEqual(1, testEngineerAgain!.Content.Split(new[] { "## Test Authoring Gate" }, StringSplitOptions.None).Length - 1, "the authoring-gate section is added once");
                }
            });

            await RunTest("The Judge criterion-conflict, test-selection and blocking-rule sections reach an edited built-in row once, and the example is not read as a conflict", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;

                    PromptTemplate judgeEdit = new PromptTemplate("persona.judge", "You are a judge. OPERATOR CUSTOM.\n## Verdict\nRun the test suite in the FOREGROUND.\n");
                    judgeEdit.Category = "persona";
                    judgeEdit.IsBuiltIn = true;
                    await testDb.Driver.PromptTemplates.CreateAsync(judgeEdit).ConfigureAwait(false);

                    PromptTemplateService service = new PromptTemplateService(testDb.Driver, logging);
                    await service.SeedDefaultsAsync().ConfigureAwait(false);

                    PromptTemplate? judge = await service.ResolveAsync("persona.judge").ConfigureAwait(false);
                    AssertStartsWith("You are a judge. OPERATOR CUSTOM.", judge!.Content, "the operator edit is preserved");
                    AssertContains("## When A Required Fix Breaks A Criterion", judge.Content, "the criterion-conflict section reaches an existing built-in row");
                    AssertContains(JudgeCriterionConflicts.LinePrefix, judge.Content, "the section names the line Armada reads");
                    AssertEqual(0, JudgeCriterionConflicts.Extract(judge.Content).Count,
                        "the template's own placeholder line is not read as a declared conflict");
                    AssertContains("## Choosing Which Tests To Run", judge.Content, "the test-selection section reaches an existing built-in row");
                    AssertContains("where this template says to run the test suite, run the tests this section names", judge.Content,
                        "the section governs an edited row's own full-suite wording");
                    AssertContains(JudgeCheckEvidence.Heading, judge.Content, "the section points at the brief's Check evidence");
                    AssertContains("## When To Block", judge.Content, "the blocking-rule section reaches an existing built-in row");

                    await service.SeedDefaultsAsync().ConfigureAwait(false);
                    PromptTemplate? reseeded = await service.ResolveAsync("persona.judge").ConfigureAwait(false);
                    foreach (string heading in new[] { "## When A Required Fix Breaks A Criterion", "## Choosing Which Tests To Run", "## When To Block" })
                    {
                        AssertEqual(1, reseeded!.Content.Split(heading).Length - 1, "a second seed does not append " + heading + " again");
                    }

                    string embedded = service.GetEmbeddedDefault("persona.judge") ?? String.Empty;
                    AssertContains("## When A Required Fix Breaks A Criterion", embedded, "the embedded default carries the criterion-conflict section");
                    AssertContains("## Choosing Which Tests To Run", embedded, "the embedded default carries the test-selection section");
                    AssertContains("## When To Block", embedded, "the embedded default carries the blocking-rule section");
                    AssertEqual(0, JudgeCriterionConflicts.Extract(embedded).Count,
                        "the embedded default's placeholder line is not read as a declared conflict");
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
                        AssertContains("armada_search_context", working.Content, name + " should name the recall tool");
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

                    string shippedRecall =
                    "\n" +
                    "## Recall Existing Memory" + "\n" +
                    "Before you act, recall what earlier work on this vessel recorded. When this brief carries a " +
                    "Model Context section, read it, and when the memory tools are available call `search_memory` with " +
                    "this vessel and with keywords from your mission, then `get_memory` for a record that matters. " +
                    "Reuse the conventions, decisions and procedures already recorded instead of deriving them " +
                    "again.\n" +
                    "A memory record is working memory, not proof. Check it against the current checkout before " +
                    "you depend on it, and say so in your summary when it is wrong or stale.\n" +
                    "When this brief carries a Shared Memory section, that shared memory is the authority: a " +
                    "shared rule wins over a memory record on conflict, and so do the mission brief, the " +
                    "playbooks and the vessel instructions. Report the conflict; do not rewrite either side.\n" +
                    "When the memory tools are absent, continue without them.\n";
                    PromptTemplate edited = new PromptTemplate("persona.architect", "CUSTOM PREFIX" + shippedRecall + "CUSTOM SUFFIX");
                    edited.Category = "persona";
                    edited.IsBuiltIn = true;
                    await testDb.Driver.PromptTemplates.CreateAsync(edited).ConfigureAwait(false);

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

                    PromptTemplate? migrated = await service.ResolveAsync("persona.architect").ConfigureAwait(false);
                    AssertContains("CUSTOM PREFIX", migrated!.Content, "The operator prefix survives the exact passage upgrade");
                    AssertContains("CUSTOM SUFFIX", migrated.Content, "The operator suffix survives the exact passage upgrade");
                    AssertContains("armada_search_context", migrated.Content, "An existing shipped recall passage adopts the common query");
                    AssertFalse(migrated.Content.Contains("`search_memory`", StringComparison.Ordinal), "The old shipped recall instruction is removed");
                    AssertEqual(1, migrated.Content.Split(new[] { "## Recall Existing Memory" }, StringSplitOptions.None).Length - 1, "Repeated seeding leaves one recall passage");

                    PromptTemplate? untouched = await service.ResolveAsync("persona.house_style").ConfigureAwait(false);
                    AssertEqual("An operator's own persona.", untouched!.Content, "A template that is not built in is left alone");
                }
            });
        }
    }
}
