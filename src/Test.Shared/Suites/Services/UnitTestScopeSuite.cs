namespace Test.Shared.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using SyslogLogging;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// Full and abbreviated unit-test commands on workflow profiles: the <c>[ARMADA:UNIT-TESTS]</c> directive parser and
    /// its precedence (mission description over persona prompt over the Full default), command selection and fallbacks,
    /// the AbbreviatedUnitTest check type in previews, resolution and validation, persistence of the new column, and the
    /// stage check choosing the command from the mission description or the persona's prompt template.
    /// </summary>
    public sealed class UnitTestScopeSuite : IArmadaTestSuite
    {
        private const string SuiteId = "Services.UnitTestScope";

        /// <summary>
        /// Build the descriptor for the unit-test scope suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(CaseAsync("directive_parses_scopes", "The directive parses full, abbreviated and their synonyms case-insensitively", TestTags.Positive, () =>
            {
                AssertEqual(UnitTestScopeEnum.Full, UnitTestScopeDirective.Parse("[ARMADA:UNIT-TESTS] full"));
                AssertEqual(UnitTestScopeEnum.Abbreviated, UnitTestScopeDirective.Parse("Run quickly.\n[ARMADA:UNIT-TESTS] abbreviated\nThanks"));
                AssertEqual(UnitTestScopeEnum.Abbreviated, UnitTestScopeDirective.Parse("[armada:unit-tests]   Quick"));
                AssertEqual(UnitTestScopeEnum.Abbreviated, UnitTestScopeDirective.Parse("[ARMADA:UNIT-TESTS]\tshort"));
                AssertEqual(UnitTestScopeEnum.Full, UnitTestScopeDirective.Parse("[ARMADA:UNIT-TESTS] abbreviated then later [ARMADA:UNIT-TESTS] FULL"), "the last directive wins");
                return Task.CompletedTask;
            }));

            cases.Add(CaseAsync("directive_ignores_missing_or_malformed", "Text without a valid directive yields no scope", TestTags.Negative, () =>
            {
                AssertNull(UnitTestScopeDirective.Parse(null));
                AssertNull(UnitTestScopeDirective.Parse(""));
                AssertNull(UnitTestScopeDirective.Parse("Write unit tests for the abbreviated path."));
                AssertNull(UnitTestScopeDirective.Parse("[ARMADA:UNIT-TESTS] partial"), "an unknown scope word is ignored");
                AssertNull(UnitTestScopeDirective.Parse("[ARMADA:UNIT-TESTS] fullest"), "a word that only starts with a scope is ignored");
                AssertNull(UnitTestScopeDirective.Parse("[ARMADA:UNIT-TESTS]\nabbreviated"), "the scope must be on the marker's line");
                return Task.CompletedTask;
            }));

            cases.Add(CaseAsync("directive_precedence", "The mission description beats the persona prompt, which beats the Full default", TestTags.Positive, () =>
            {
                AssertEqual(UnitTestScopeEnum.Full, UnitTestScopeDirective.Resolve(null, null));
                AssertEqual(UnitTestScopeEnum.Full, UnitTestScopeDirective.Resolve("no directive", "no directive"));
                AssertEqual(UnitTestScopeEnum.Abbreviated, UnitTestScopeDirective.Resolve("no directive", "[ARMADA:UNIT-TESTS] abbreviated"));
                AssertEqual(UnitTestScopeEnum.Full, UnitTestScopeDirective.Resolve("[ARMADA:UNIT-TESTS] full", "[ARMADA:UNIT-TESTS] abbreviated"));
                AssertEqual(UnitTestScopeEnum.Abbreviated, UnitTestScopeDirective.Resolve("[ARMADA:UNIT-TESTS] abbreviated", "[ARMADA:UNIT-TESTS] full"));
                return Task.CompletedTask;
            }));

            cases.Add(CaseAsync("select_command_fallbacks", "Command selection trims, treats blanks as unset, and falls back to the other command", TestTags.Negative, () =>
            {
                WorkflowProfile both = new WorkflowProfile { UnitTestCommand = "  dotnet test  ", AbbreviatedUnitTestCommand = " dotnet test --filter Smoke " };
                AssertEqual("dotnet test", UnitTestScopeDirective.SelectCommand(both, UnitTestScopeEnum.Full));
                AssertEqual("dotnet test --filter Smoke", UnitTestScopeDirective.SelectCommand(both, UnitTestScopeEnum.Abbreviated));

                WorkflowProfile fullOnly = new WorkflowProfile { UnitTestCommand = "dotnet test", AbbreviatedUnitTestCommand = "   " };
                AssertEqual("dotnet test", UnitTestScopeDirective.SelectCommand(fullOnly, UnitTestScopeEnum.Abbreviated), "abbreviated falls back to full");

                WorkflowProfile abbreviatedOnly = new WorkflowProfile { AbbreviatedUnitTestCommand = "npm run test:quick" };
                AssertEqual("npm run test:quick", UnitTestScopeDirective.SelectCommand(abbreviatedOnly, UnitTestScopeEnum.Full), "full falls back to abbreviated");

                WorkflowProfile neither = new WorkflowProfile { UnitTestCommand = "", AbbreviatedUnitTestCommand = null };
                AssertNull(UnitTestScopeDirective.SelectCommand(neither, UnitTestScopeEnum.Full));
                AssertNull(UnitTestScopeDirective.SelectCommand(neither, UnitTestScopeEnum.Abbreviated));

                AssertThrows<ArgumentNullException>(() => UnitTestScopeDirective.SelectCommand(null!, UnitTestScopeEnum.Full));
                return Task.CompletedTask;
            }));

            cases.Add(CaseAsync("service_exposes_abbreviated_check_type", "Previews, available types and ResolveCommand include AbbreviatedUnitTest", TestTags.Positive, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                WorkflowProfileService service = new WorkflowProfileService(testDb.Driver, CreateLogging());

                WorkflowProfile both = new WorkflowProfile { Name = "Both", UnitTestCommand = "dotnet test", AbbreviatedUnitTestCommand = "dotnet test --filter Smoke" };
                List<WorkflowProfileCommandPreview> previews = WorkflowProfileService.BuildCommandPreviews(both);
                AssertEqual("dotnet test", previews.Single(p => p.CheckType == CheckRunTypeEnum.UnitTest).Command);
                AssertEqual("dotnet test --filter Smoke", previews.Single(p => p.CheckType == CheckRunTypeEnum.AbbreviatedUnitTest).Command);
                List<string> types = WorkflowProfileService.GetAvailableCheckTypeNames(both);
                AssertTrue(types.Contains("UnitTest") && types.Contains("AbbreviatedUnitTest"));
                AssertEqual("dotnet test", service.ResolveCommand(both, CheckRunTypeEnum.UnitTest));
                AssertEqual("dotnet test --filter Smoke", service.ResolveCommand(both, CheckRunTypeEnum.AbbreviatedUnitTest));

                WorkflowProfile fullOnly = new WorkflowProfile { Name = "Full", UnitTestCommand = "dotnet test" };
                AssertFalse(WorkflowProfileService.GetAvailableCheckTypeNames(fullOnly).Contains("AbbreviatedUnitTest"), "not offered when unset");
                AssertFalse(WorkflowProfileService.BuildCommandPreviews(fullOnly).Any(p => p.CheckType == CheckRunTypeEnum.AbbreviatedUnitTest));
                AssertEqual("dotnet test", service.ResolveCommand(fullOnly, CheckRunTypeEnum.AbbreviatedUnitTest), "an explicit abbreviated run falls back to the full command");

                WorkflowProfile abbreviatedOnly = new WorkflowProfile { Name = "Quick", AbbreviatedUnitTestCommand = "npm run test:quick" };
                AssertNull(service.ResolveCommand(abbreviatedOnly, CheckRunTypeEnum.UnitTest), "a UnitTest run never silently runs the abbreviated suite");
            }));

            cases.Add(CaseAsync("validate_accepts_abbreviated_only_profile", "A profile whose only command is the abbreviated unit-test command is valid", TestTags.Positive, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                WorkflowProfileService service = new WorkflowProfileService(testDb.Driver, CreateLogging());
                WorkflowProfileValidationResult result = await service.ValidateAsync(new WorkflowProfile
                {
                    TenantId = "ten_scope",
                    Name = "Quick Only",
                    Scope = WorkflowProfileScopeEnum.Global,
                    AbbreviatedUnitTestCommand = "dotnet test --filter Smoke"
                }).ConfigureAwait(false);
                AssertTrue(result.IsValid, String.Join(" ", result.Errors));
            }));

            cases.Add(CaseAsync("abbreviated_command_persists", "The abbreviated command round-trips through create, update and clear", TestTags.Database, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                WorkflowProfile created = await testDb.Driver.WorkflowProfiles.CreateAsync(new WorkflowProfile
                {
                    Name = "Persisted",
                    UnitTestCommand = "dotnet test",
                    AbbreviatedUnitTestCommand = "dotnet test --filter Smoke"
                }).ConfigureAwait(false);
                WorkflowProfile? read = await testDb.Driver.WorkflowProfiles.ReadAsync(created.Id).ConfigureAwait(false);
                AssertNotNull(read);
                AssertEqual("dotnet test", read.UnitTestCommand);
                AssertEqual("dotnet test --filter Smoke", read.AbbreviatedUnitTestCommand);

                read.AbbreviatedUnitTestCommand = "dotnet test --filter Fast";
                await testDb.Driver.WorkflowProfiles.UpdateAsync(read).ConfigureAwait(false);
                WorkflowProfile? updated = await testDb.Driver.WorkflowProfiles.ReadAsync(created.Id).ConfigureAwait(false);
                AssertEqual("dotnet test --filter Fast", updated!.AbbreviatedUnitTestCommand);
                AssertEqual("dotnet test", updated.UnitTestCommand, "updating one command leaves the other alone");

                updated.AbbreviatedUnitTestCommand = null;
                await testDb.Driver.WorkflowProfiles.UpdateAsync(updated).ConfigureAwait(false);
                WorkflowProfile? cleared = await testDb.Driver.WorkflowProfiles.ReadAsync(created.Id).ConfigureAwait(false);
                AssertNull(cleared!.AbbreviatedUnitTestCommand);
            }));

            cases.Add(CaseAsync("stage_check_defaults_to_full", "With no directive the stage check runs the build then the full unit-test command", TestTags.Positive, async () =>
            {
                using ScopeFixture fx = await ScopeFixture.CreateAsync("Do it", null).ConfigureAwait(false);
                StageCheckResult result = await fx.Missions.CheckStageWorkAsync(fx.Mission, fx.Captain).ConfigureAwait(false);
                AssertEqual(StageCheckStatusEnum.Passed, result.Status);
                AssertEqual(2, result.Commands.Count);
                AssertEqual(ScopeFixture.FullCommand, result.Commands[1]);
                AssertEqual(UnitTestScopeEnum.Full, result.UnitTestScope);
            }));

            cases.Add(CaseAsync("stage_check_honors_mission_description", "A mission description directive selects the abbreviated command", TestTags.Positive, async () =>
            {
                using ScopeFixture fx = await ScopeFixture.CreateAsync("Do it.\n[ARMADA:UNIT-TESTS] abbreviated", null).ConfigureAwait(false);
                StageCheckResult result = await fx.Missions.CheckStageWorkAsync(fx.Mission, fx.Captain).ConfigureAwait(false);
                AssertEqual(StageCheckStatusEnum.Passed, result.Status);
                AssertEqual(ScopeFixture.AbbreviatedCommand, result.Commands.Last());
                AssertFalse(result.Commands.Contains(ScopeFixture.FullCommand), "only one unit-test command runs");
                AssertEqual(UnitTestScopeEnum.Abbreviated, result.UnitTestScope);
            }));

            cases.Add(CaseAsync("stage_check_honors_persona_prompt", "A directive in the persona's prompt template selects the abbreviated command", TestTags.Positive, async () =>
            {
                using ScopeFixture fx = await ScopeFixture.CreateAsync("Do it", "You are a test engineer.\n[ARMADA:UNIT-TESTS] abbreviated").ConfigureAwait(false);
                StageCheckResult result = await fx.Missions.CheckStageWorkAsync(fx.Mission, fx.Captain).ConfigureAwait(false);
                AssertEqual(ScopeFixture.AbbreviatedCommand, result.Commands.Last());
                AssertEqual(UnitTestScopeEnum.Abbreviated, result.UnitTestScope);
            }));

            cases.Add(CaseAsync("stage_check_description_overrides_persona", "The mission description's directive wins over the persona prompt's", TestTags.Negative, async () =>
            {
                using ScopeFixture fx = await ScopeFixture.CreateAsync("[ARMADA:UNIT-TESTS] full", "[ARMADA:UNIT-TESTS] abbreviated").ConfigureAwait(false);
                StageCheckResult result = await fx.Missions.CheckStageWorkAsync(fx.Mission, fx.Captain).ConfigureAwait(false);
                AssertEqual(ScopeFixture.FullCommand, result.Commands.Last());
                AssertEqual(UnitTestScopeEnum.Full, result.UnitTestScope);
            }));

            cases.Add(CaseAsync("stage_check_abbreviated_falls_back_to_full", "Requesting the abbreviated suite on a profile without one runs the full command", TestTags.Negative, async () =>
            {
                using ScopeFixture fx = await ScopeFixture.CreateAsync("[ARMADA:UNIT-TESTS] abbreviated", null, includeAbbreviated: false).ConfigureAwait(false);
                StageCheckResult result = await fx.Missions.CheckStageWorkAsync(fx.Mission, fx.Captain).ConfigureAwait(false);
                AssertEqual(StageCheckStatusEnum.Passed, result.Status);
                AssertEqual(ScopeFixture.FullCommand, result.Commands.Last());
            }));

            cases.Add(CaseAsync("stage_check_abbreviated_failure_blocks", "A failing abbreviated command fails the stage check and names that command", TestTags.Negative, async () =>
            {
                // Fails only where the stage's work.txt exists, so the base commit passes and the failure is the stage's own.
                string failing = OperatingSystem.IsWindows() ? "if exist work.txt exit 4" : "test ! -f work.txt";
                using ScopeFixture fx = await ScopeFixture.CreateAsync("[ARMADA:UNIT-TESTS] abbreviated", null, abbreviatedCommand: failing).ConfigureAwait(false);
                StageCheckResult result = await fx.Missions.CheckStageWorkAsync(fx.Mission, fx.Captain).ConfigureAwait(false);
                AssertEqual(StageCheckStatusEnum.Failed, result.Status);
                AssertEqual(failing, result.FailedCommand);
                AssertTrue(result.IsBlocking);
            }));

            return new TestSuiteDescriptor(
                suiteId: SuiteId,
                displayName: "Full and Abbreviated Unit-Test Commands",
                cases: cases);
        }

        private static LoggingModule CreateLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        private static TestCaseDescriptor CaseAsync(string caseId, string displayName, string tag, Func<Task> body)
        {
            return new TestCaseDescriptor(
                suiteId: SuiteId,
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) => body(),
                tags: new List<string> { tag });
        }

        private sealed class ScopeFixture : IDisposable
        {
            public const string FullCommand = "echo full-suite";
            public const string AbbreviatedCommand = "echo abbreviated-suite";

            public TestDatabase Db { get; private set; } = null!;
            public MissionService Missions { get; private set; } = null!;
            public Mission Mission { get; private set; } = null!;
            public Captain Captain { get; private set; } = null!;

            public static async Task<ScopeFixture> CreateAsync(string description, string? personaPrompt, bool includeAbbreviated = true, string? abbreviatedCommand = null)
            {
                ScopeFixture fx = new ScopeFixture();
                fx.Db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                DatabaseDriver db = fx.Db.Driver;
                LoggingModule logging = CreateLogging();
                ArmadaSettings settings = new ArmadaSettings();
                settings.DocksDirectory = TestTemp.NewDirectory("unitscope_docks");
                settings.ReposDirectory = TestTemp.NewDirectory("unitscope_repos");
                settings.LogDirectory = TestTemp.NewDirectory("unitscope_logs");
                GitService git = new GitService(logging);
                IDockService docks = new DockService(logging, db, settings, git);
                CaptainService captains = new CaptainService(logging, db, settings, git, docks);
                PromptTemplateService templates = new PromptTemplateService(db, logging);
                fx.Missions = new MissionService(logging, db, settings, docks, captains, templates, git);

                string repo = TestTemp.NewDirectory("unitscope_repo");
                await Git(repo, "init", "-q", "-b", "main").ConfigureAwait(false);
                File.WriteAllText(Path.Combine(repo, "base.txt"), "base\n");
                await Commit(repo, "base").ConfigureAwait(false);
                string start = (await Git(repo, "rev-parse", "HEAD").ConfigureAwait(false)).Trim();
                File.WriteAllText(Path.Combine(repo, "work.txt"), "work\n");
                await Commit(repo, "stage").ConfigureAwait(false);

                Vessel vessel = await db.Vessels.CreateAsync(new Vessel("unitscope-" + Guid.NewGuid().ToString("N").Substring(0, 6), "https://example.invalid/repo.git")).ConfigureAwait(false);
                await db.WorkflowProfiles.CreateAsync(new WorkflowProfile
                {
                    Name = "scope",
                    Scope = WorkflowProfileScopeEnum.Vessel,
                    VesselId = vessel.Id,
                    IsDefault = true,
                    Active = true,
                    BuildCommand = "echo build",
                    UnitTestCommand = FullCommand,
                    AbbreviatedUnitTestCommand = includeAbbreviated ? (abbreviatedCommand ?? AbbreviatedCommand) : null
                }).ConfigureAwait(false);
                Dock dock = await db.Docks.CreateAsync(new Dock(vessel.Id) { WorktreePath = repo, Active = true }).ConfigureAwait(false);
                string metadata = Path.Combine(settings.LogDirectory, "docks");
                Directory.CreateDirectory(metadata);
                File.WriteAllText(Path.Combine(metadata, dock.Id + ".start"), start + "\n");

                string personaName = "Worker";
                if (personaPrompt != null)
                {
                    personaName = "Scope Tester " + Guid.NewGuid().ToString("N").Substring(0, 6);
                    string templateName = "persona.scope_tester_" + Guid.NewGuid().ToString("N").Substring(0, 6);
                    await db.PromptTemplates.CreateAsync(new PromptTemplate(templateName, personaPrompt)).ConfigureAwait(false);
                    await db.Personas.CreateAsync(new Persona(personaName, templateName)).ConfigureAwait(false);
                }

                fx.Captain = await db.Captains.CreateAsync(new Captain("frontier", AgentRuntimeEnum.ClaudeCode) { State = CaptainStateEnum.Working }).ConfigureAwait(false);
                fx.Mission = await db.Missions.CreateAsync(new Mission("Work", description) { VesselId = vessel.Id, DockId = dock.Id, Persona = personaName, Status = MissionStatusEnum.InProgress }).ConfigureAwait(false);
                return fx;
            }

            public void Dispose()
            {
                Db?.Dispose();
            }

            private static Task<string> Commit(string repo, string message)
            {
                return Git(repo, "add", "-A").ContinueWith(_ => Git(repo, "-c", "user.name=t", "-c", "user.email=t@t", "-c", "commit.gpgsign=false", "commit", "-q", "-m", message)).Unwrap();
            }

            private static async Task<string> Git(string directory, params string[] args)
            {
                ProcessStartInfo info = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                foreach (string arg in args) info.ArgumentList.Add(arg);
                using Process process = Process.Start(info)!;
                string output = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
                await process.WaitForExitAsync().ConfigureAwait(false);
                return output;
            }
        }
    }
}
