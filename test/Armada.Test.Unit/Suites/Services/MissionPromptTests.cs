namespace Armada.Test.Unit.Suites.Services
{
    using System.Diagnostics;
    using System.Text;
    using System.Text.Json;
    using System.Text.RegularExpressions;
    using Armada.Core.Context;
    using Armada.Core.Database.Sqlite;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using TestResourcePressure = global::Test.Shared.Infrastructure.TestResourcePressure;
    using SyslogLogging;

    public class MissionPromptTests : TestSuite
    {
        public override string Name => "Mission Prompt (ProjectContext/StyleGuide/ModelContext)";

        private static async Task<string> RunGitAsync(string workingDirectory, params string[] args)
        {
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (string arg in args) startInfo.ArgumentList.Add(arg);
            using (Process process = new Process { StartInfo = startInfo })
            {
                process.Start();
                Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
                Task<string> stderrTask = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync().ConfigureAwait(false);
                string stdout = await stdoutTask.ConfigureAwait(false);
                string stderr = await stderrTask.ConfigureAwait(false);
                if (process.ExitCode != 0)
                    throw new InvalidOperationException("git " + String.Join(" ", args) + " failed (exit " + process.ExitCode + "): " + stderr.Trim());
                return stdout;
            }
        }

        private LoggingModule CreateLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        private ArmadaSettings CreateSettings()
        {
            ArmadaSettings settings = new ArmadaSettings();
            settings.DocksDirectory = Path.Combine(Path.GetTempPath(), "armada_test_docks_" + Guid.NewGuid().ToString("N"));
            settings.ReposDirectory = Path.Combine(Path.GetTempPath(), "armada_test_repos_" + Guid.NewGuid().ToString("N"));
            return settings;
        }

        private MissionService CreateMissionService(LoggingModule logging, SqliteDatabaseDriver db, ArmadaSettings settings, StubGitService git)
        {
            IDockService dockService = new DockService(logging, db, settings, git);
            ICaptainService captainService = new CaptainService(logging, db, settings, git, dockService);
            return new MissionService(logging, db, settings, dockService, captainService, resourcePressureAdmission: TestResourcePressure.Unconstrained(settings));
        }

        private MissionService CreateMissionServiceWithTemplates(LoggingModule logging, SqliteDatabaseDriver db, ArmadaSettings settings, StubGitService git, out IPromptTemplateService templateService)
        {
            IDockService dockService = new DockService(logging, db, settings, git);
            ICaptainService captainService = new CaptainService(logging, db, settings, git, dockService);
            templateService = new PromptTemplateService(db, logging);
            return new MissionService(logging, db, settings, dockService, captainService, templateService, resourcePressureAdmission: TestResourcePressure.Unconstrained(settings));
        }

        protected override async Task RunTestsAsync()
        {
            await RunTest("Optional dock anchor JSON rejects unsupported and invalid evidence", () =>
            {
                DockGitAnchorSnapshot snapshot = new DockGitAnchorSnapshot
                {
                    DockId = "dock", VesselId = "vessel", MissionId = "mission",
                    ProvisionedCommit = new string('a', 40), ProvisionedUtc = DateTime.UtcNow,
                    Anchors = new GitAnchors { BaseCommit = new string('a', 40) }
                };
                string json = Armada.Core.Database.DockGitAnchorPersistence.Serialize(snapshot);
                AssertNotNull(Armada.Core.Database.DockGitAnchorPersistence.Read(json, "dock", "vessel"));
                foreach (string invalid in new[]
                {
                    "{", "null", json.Replace("\"Version\":1", "\"Version\":2"),
                    json.Replace("\"Anchors\":{", "\"Anchors\":null,\"ignored\":{"),
                    new string(' ', 32769), json.Replace("\"Files\":[]", "\"Files\":[null]")
                })
                    AssertNull(Armada.Core.Database.DockGitAnchorPersistence.Read(invalid, "dock", "vessel"));
                AssertNull(Armada.Core.Database.DockGitAnchorPersistence.Read(json, "other-dock", "vessel"));
                snapshot.Anchors.Files.Add(new GitAnchorFileHistory { Path = "../outside" });
                AssertThrows<InvalidOperationException>(() => Armada.Core.Database.DockGitAnchorPersistence.Serialize(snapshot));
                snapshot.Anchors.Files.Clear();
                snapshot.State = DockGitAnchorStateEnum.Complete;
                snapshot.ResolvedUtc = DateTime.UtcNow;
                snapshot.Truncated = true;
                AssertThrows<InvalidOperationException>(() => Armada.Core.Database.DockGitAnchorPersistence.Serialize(snapshot));
            });

            await RunTest("Dock anchors persist once and refuse missing or reclaimed evidence", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    StubGitService git = new StubGitService();
                    IDockService docks = new DockService(logging, testDb.Driver, settings, git);
                    ICaptainService captains = new CaptainService(logging, testDb.Driver, settings, git, docks);
                    MissionService service = new MissionService(logging, testDb.Driver, settings, docks, captains, git: git, resourcePressureAdmission: TestResourcePressure.Unconstrained(settings));
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(new Vessel("anchors", "https://example.invalid/repo"));
                    Captain captain = await testDb.Driver.Captains.CreateAsync(new Captain("anchors"));
                    Mission mission = new Mission { Title = "inspect", VesselId = vessel.Id, CaptainId = captain.Id };
                    Dock dock = new Dock(vessel.Id) { CaptainId = captain.Id, WorktreePath = "/anchor-test", BranchName = "mission" };
                    string source = new string('a', 40);
                    dock.GitAnchorsSnapshot = new DockGitAnchorSnapshot
                    {
                        DockId = dock.Id, MissionId = mission.Id, VesselId = vessel.Id,
                        ProvisionedCommit = source, ProvisionedUtc = DateTime.UtcNow,
                        Anchors = new GitAnchors { BaseCommit = source }
                    };
                    dock = await testDb.Driver.Docks.CreateAsync(dock);
                    mission.DockId = dock.Id;
                    git.RevisionCommitShas["/anchor-test|" + source] = source;
                    git.RevisionCommitShaResult = new string('b', 40);
                    GitAnchors first = await service.ResolveDispatchGitAnchorsAsync("/anchor-test", mission, vessel, default);
                    AssertEqual(source, first.BaseCommit);
                    AssertEqual(new string('b', 40), first.TargetTip);
                    int queries = git.RevisionCommitShaCalls.Count;
                    git.RevisionCommitShaResult = new string('c', 40);
                    GitAnchors second = await service.ResolveDispatchGitAnchorsAsync("/anchor-test", mission, vessel, default);
                    AssertEqual(first.TargetTip, second.TargetTip, "Evidence is stable after the target moves");
                    AssertEqual(queries, git.RevisionCommitShaCalls.Count, "Completed evidence is reused");
                    Dock stored = (await testDb.Driver.Docks.ReadAsync(dock.Id))!;
                    AssertEqual(DockGitAnchorStateEnum.Complete, stored.GitAnchorsSnapshot!.State);
                    stored.Active = false;
                    stored.CaptainId = null;
                    await testDb.Driver.Docks.UpdateAsync(stored);
                    GitAnchors reclaimed = await service.ResolveDispatchGitAnchorsAsync("/anchor-test", mission, vessel, default);
                    AssertFalse(reclaimed.HasContent, "Reclaimed evidence is unavailable");
                    Dock legacy = await testDb.Driver.Docks.CreateAsync(new Dock(vessel.Id)
                    { CaptainId = captain.Id, WorktreePath = "/anchor-test" });
                    mission.DockId = legacy.Id;
                    GitAnchors missing = await service.ResolveDispatchGitAnchorsAsync("/anchor-test", mission, vessel, default);
                    AssertFalse(missing.HasContent, "Legacy docks do not invent a provisioning commit from HEAD");
                    AssertEqual(queries, git.RevisionCommitShaCalls.Count);
                    Dock unavailable = new Dock(vessel.Id) { CaptainId = captain.Id, WorktreePath = "/anchor-test" };
                    unavailable.GitAnchorsSnapshot = new DockGitAnchorSnapshot
                    {
                        DockId = unavailable.Id, MissionId = mission.Id, VesselId = vessel.Id,
                        ProvisionedCommit = source, ProvisionedUtc = DateTime.UtcNow,
                        Anchors = new GitAnchors { BaseCommit = source }
                    };
                    await testDb.Driver.Docks.CreateAsync(unavailable);
                    mission.DockId = unavailable.Id;
                    git.RevisionCommitShaResult = null;
                    GitAnchors incomplete = await service.ResolveDispatchGitAnchorsAsync("/anchor-test", mission, vessel, default);
                    AssertEqual(source, incomplete.BaseCommit, "Failure retains actual provisioning source");
                    AssertEqual("anchor_query_failed", incomplete.ResolutionError);
                    queries = git.RevisionCommitShaCalls.Count;
                    git.RevisionCommitShaResult = new string('b', 40);
                    await service.ResolveDispatchGitAnchorsAsync("/anchor-test", mission, vessel, default);
                    AssertEqual(queries, git.RevisionCommitShaCalls.Count, "Incomplete evidence is stable too");
                }
            });

            await RunTest("Only a PullRequest landing asks the captain to push", async () =>
            {
                ArmadaSettings settings = CreateSettings();
                settings.LandingMode = null;

                Vessel localMerge = new Vessel("lm", "https://github.com/test/lm.git") { LandingMode = LandingModeEnum.LocalMerge };
                Vessel mergeQueue = new Vessel("mq", "https://github.com/test/mq.git") { LandingMode = LandingModeEnum.MergeQueue };
                Vessel none = new Vessel("no", "https://github.com/test/no.git") { LandingMode = LandingModeEnum.None };
                Vessel pullRequest = new Vessel("pr", "https://github.com/test/pr.git") { LandingMode = LandingModeEnum.PullRequest };
                Vessel unset = new Vessel("un", "https://github.com/test/un.git") { LandingMode = null };

                // A push is only needed when a PR is opened against the remote branch. The other modes
                // land from the bare repo, and a pushed branch there is never collected: LocalOnly
                // cleanup skips origin, and no PR merge fires the host's auto-delete.
                AssertFalse(MissionService.RequiresCaptainPush(localMerge, settings), "LocalMerge lands from the bare repo, so no push is needed");
                AssertFalse(MissionService.RequiresCaptainPush(mergeQueue, settings), "MergeQueue takes the branch from the bare repo");
                AssertFalse(MissionService.RequiresCaptainPush(none, settings), "None leaves the branch for manual integration locally");
                AssertTrue(MissionService.RequiresCaptainPush(pullRequest, settings), "PullRequest needs the branch on origin to open the PR");

                // Unresolved stays with the historical instruction: the legacy boolean settings can
                // still derive a PullRequest landing, and suppressing the push would strand it.
                AssertTrue(MissionService.RequiresCaptainPush(unset, settings), "An unresolved landing mode must keep the push");
                AssertTrue(MissionService.RequiresCaptainPush(null, settings), "A null vessel must keep the push");

                // A vessel setting overrides the global default in both directions.
                settings.LandingMode = LandingModeEnum.PullRequest;
                AssertFalse(MissionService.RequiresCaptainPush(localMerge, settings), "The vessel's LocalMerge must win over a PullRequest default");
                settings.LandingMode = LandingModeEnum.LocalMerge;
                AssertTrue(MissionService.RequiresCaptainPush(pullRequest, settings), "The vessel's PullRequest must win over a LocalMerge default");
                AssertFalse(MissionService.RequiresCaptainPush(unset, settings), "With no vessel mode the settings default decides");

                await Task.CompletedTask;
            });

            await RunTest("The no-push rules module forbids pushing and keeps committing", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    PromptTemplateService templates = new PromptTemplateService(testDb.Driver, logging);
                    await templates.SeedDefaultsAsync();

                    PromptTemplate? noPush = await templates.ResolveAsync("mission.rules_no_push");
                    AssertTrue(noPush != null, "the mission.rules_no_push template must be seeded");

                    string content = noPush!.Content ?? String.Empty;
                    AssertContains("Commit all changes to the current branch", content);
                    AssertContains("Do NOT push", content);
                    AssertFalse(content.Contains("Commit and push your changes"),
                        "the no-push module must not carry the push instruction it exists to remove");
                    AssertFalse(content.Contains("git push origin HEAD:refs/heads/"),
                        "the detached-HEAD push recipe must not survive in the no-push module either");
                }
            });

            // Objective scope: a voyage that links an objective must carry the
            // objective's scope, acceptance criteria, and non-goals once in the generated brief,
            // and the mission description must appear exactly once.
            await RunTest("GenerateClaudeMdAsync includes Objective Scope once when the voyage links an objective", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    StubGitService git = new StubGitService();
                    MissionService service = CreateMissionService(logging, testDb.Driver, settings, git);

                    string tempDir = Path.Combine(Path.GetTempPath(), "armada_prompt_test_" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(tempDir);

                    try
                    {
                        Voyage voyage = new Voyage("scope-voyage");
                        voyage = await testDb.Driver.Voyages.CreateAsync(voyage).ConfigureAwait(false);

                        Objective objective = new Objective();
                        objective.Title = "Scope the token port";
                        objective.Description = "Port the token exchange to the extractor.";
                        objective.AcceptanceCriteria = new List<string> { "The exchange round-trips 128 seeds.", "No secret bytes enter the manifest." };
                        objective.NonGoals = new List<string> { "No batch import support." };
                        objective.VoyageIds = new List<string> { voyage.Id };
                        await testDb.Driver.Objectives.CreateAsync(objective).ConfigureAwait(false);

                        Vessel vessel = new Vessel("ScopeVessel", "https://github.com/test/repo");
                        Mission mission = new Mission();
                        mission.Title = "Port token";
                        mission.Description = "Implement the token exchange per the objective.";
                        mission.VoyageId = voyage.Id;

                        await service.GenerateClaudeMdAsync(tempDir, mission, vessel);

                        string content = await File.ReadAllTextAsync(Path.Combine(tempDir, "CLAUDE.md"));
                        AssertContains("## Objective Scope (Definition of Done)", content);
                        AssertContains("Port the token exchange to the extractor.", content);
                        AssertContains("The exchange round-trips 128 seeds.", content);
                        AssertContains("No secret bytes enter the manifest.", content);
                        AssertContains("No batch import support.", content);
                        AssertContains("the Judge reviews against these acceptance criteria", content);

                        int descriptionOccurrences = CountOccurrences(content, mission.Description);
                        AssertEqual(1, descriptionOccurrences, "The mission description must appear exactly once in the generated brief");
                    }
                    finally
                    {
                        try { Directory.Delete(tempDir, true); } catch { }
                    }
                }
            });

            await RunTest("Objective Scope names the stored parent id and says other cited objective ids do not control", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    StubGitService git = new StubGitService();
                    MissionService service = CreateMissionService(logging, testDb.Driver, settings, git);

                    string tempDir = Path.Combine(Path.GetTempPath(), "armada_prompt_test_" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(tempDir);

                    try
                    {
                        Voyage voyage = new Voyage("parent-scope-voyage");
                        voyage = await testDb.Driver.Voyages.CreateAsync(voyage).ConfigureAwait(false);

                        Objective parent = new Objective();
                        parent.Title = "Widget program";
                        parent = await testDb.Driver.Objectives.CreateAsync(parent).ConfigureAwait(false);

                        Objective objective = new Objective();
                        objective.Title = "Study the widget";
                        objective.ParentObjectiveId = parent.Id;
                        objective.Description = "Study the widget. The current authorizing objective is obj_cccccccc_Older.";
                        objective.AcceptanceCriteria = new List<string> { "The widget study is complete." };
                        objective.VoyageIds = new List<string> { voyage.Id };
                        await testDb.Driver.Objectives.CreateAsync(objective).ConfigureAwait(false);

                        Vessel vessel = new Vessel("ScopeVessel", "https://github.com/test/repo");
                        Mission mission = new Mission();
                        mission.Title = "Study widget";
                        mission.Description = "Study the widget per the objective.";
                        mission.VoyageId = voyage.Id;

                        await service.GenerateClaudeMdAsync(tempDir, mission, vessel);

                        string content = await File.ReadAllTextAsync(Path.Combine(tempDir, "CLAUDE.md"));
                        AssertContains("**Parent objective id:** `" + parent.Id + "`", content,
                            "the brief names the stored parent id");
                        AssertContains("Any other objective id the text calls current, parent or authorizing does not control.", content,
                            "a brief whose stored text names another current objective says which ids control");
                    }
                    finally
                    {
                        try { Directory.Delete(tempDir, true); } catch { }
                    }
                }
            });

            await RunTest("TestEngineer brief lets an objective no-test non-goal override the embedded test-writing role", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    StubGitService git = new StubGitService();
                    MissionService service = CreateMissionServiceWithTemplates(logging, testDb.Driver, settings, git, out IPromptTemplateService templates);
                    string tempDir = Path.Combine(Path.GetTempPath(), "armada_prompt_test_" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(tempDir);

                    try
                    {
                        Voyage voyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("docs-only-test-engineer-voyage")).ConfigureAwait(false);
                        Objective objective = new Objective();
                        objective.Title = "Correct the documentation example";
                        objective.Description = "Update the example text to match the current option names.";
                        objective.AcceptanceCriteria = new List<string> { "The example names the current options." };
                        objective.NonGoals = new List<string> { "Do not add, edit, or commit test files; this objective changes documentation only." };
                        objective.VoyageIds = new List<string> { voyage.Id };
                        await testDb.Driver.Objectives.CreateAsync(objective).ConfigureAwait(false);

                        Vessel vessel = await testDb.Driver.Vessels.CreateAsync(new Vessel("DocsOnlyTestEngineerVessel", "https://github.com/test/repo")).ConfigureAwait(false);
                        Mission mission = new Mission();
                        mission.Title = "Update the example";
                        mission.Description = "Update the docs example only. Do not change or add tests.";
                        mission.Persona = "TestEngineer";
                        mission.Mode = MissionModeEnum.Implementation;
                        mission.VoyageId = voyage.Id;

                        await service.GenerateClaudeMdAsync(tempDir, mission, vessel).ConfigureAwait(false);
                        string brief = await File.ReadAllTextAsync(Path.Combine(tempDir, "CLAUDE.md")).ConfigureAwait(false);

                        AssertContains("Do not add, edit, or commit test files", brief, "the linked objective non-goal reaches the TestEngineer brief");
                        AssertFalse(brief.Contains("You do not patch production code. Commit test files only."),
                            "the brief must not issue an unconditional test-only commit command when the objective forbids test changes");
                        AssertContains("controls whether tests are in scope", brief,
                            "the generated TestEngineer brief must give explicit objective scope priority over its default test-writing role");
                        AssertContains("run any validation that Objective Scope requires and permits", brief,
                            "no-test scope must not suppress required and permitted validation");
                        AssertContains("## Residual Risks", brief, "the TestEngineer can report why no tests were needed");

                        PromptTemplate customDbTemplate = new PromptTemplate(
                            "persona.test_engineer",
                            "CUSTOM TEST ENGINEER PROMPT: follow the objective scope and report only.\n{TestOwnership}");
                        await testDb.Driver.PromptTemplates.CreateAsync(customDbTemplate).ConfigureAwait(false);
                        const string ownership = "SYNTHETIC OWNERSHIP DIRECTIVE";
                        string customPrompt = await MissionPromptBuilder.ResolvePersonaPromptAsync(
                            mission.Persona,
                            new Dictionary<string, string> { ["TestOwnership"] = ownership },
                            templates).ConfigureAwait(false);
                        AssertContains("CUSTOM TEST ENGINEER PROMPT", customPrompt,
                            "a shared database template takes precedence over the embedded default");
                        AssertEqual(1, customPrompt.Split(new[] { ownership }, StringSplitOptions.None).Length - 1,
                            "a template placeholder receives the ownership directive once");
                        AssertFalse(customPrompt.Contains("You are an Armada test engineer agent"),
                            "the embedded source default must not overwrite a custom database template");

                        await testDb.Driver.PromptTemplates.CreateAsync(new PromptTemplate(
                            "persona.test_engineer_without_ownership_placeholder",
                            "CUSTOM FALLBACK TEST ENGINEER PROMPT: follow the objective scope."))
                            .ConfigureAwait(false);
                        PersonaOverride noPlaceholderOverride = new PersonaOverride
                        {
                            PersonaName = "TestEngineer",
                            PromptTemplateName = "persona.test_engineer_without_ownership_placeholder"
                        };
                        string fallbackPrompt = await MissionPromptBuilder.ResolvePersonaPromptAsync(
                            mission.Persona,
                            new Dictionary<string, string> { ["TestOwnership"] = ownership },
                            templates,
                            noPlaceholderOverride).ConfigureAwait(false);
                        AssertContains("CUSTOM FALLBACK TEST ENGINEER PROMPT", fallbackPrompt,
                            "the custom template without a placeholder remains active");
                        AssertEqual(1, fallbackPrompt.Split(new[] { ownership }, StringSplitOptions.None).Length - 1,
                            "the ownership directive is appended once when the template has no placeholder");

                        string joinedBrief = MissionService.BuildPersonaPreamble("TestEngineer", MissionModeEnum.Implementation) + customPrompt;
                        AssertContains("Objective Scope", joinedBrief,
                            "the joined handoff preamble and persona prompt preserve the explicit scope rule");
                        AssertEqual(1, joinedBrief.Split(new[] { ownership }, StringSplitOptions.None).Length - 1,
                            "joining the handoff preamble does not duplicate ownership prose");

                        await testDb.Driver.PromptTemplates.CreateAsync(new PromptTemplate(
                            "persona.test_engineer_profile_custom",
                            "PROFILE TEST ENGINEER PROMPT: follow the project profile."))
                            .ConfigureAwait(false);
                        PersonaOverride personaOverride = new PersonaOverride();
                        personaOverride.PersonaName = "TestEngineer";
                        personaOverride.PromptTemplateName = "persona.test_engineer_profile_custom";
                        ProjectProfile profile = new ProjectProfile();
                        profile.Name = "test-engineer-profile";
                        profile.VesselId = vessel.Id;
                        profile.PersonaOverrides = new List<PersonaOverride> { personaOverride };
                        await testDb.Driver.ProjectProfiles.CreateAsync(profile).ConfigureAwait(false);

                        string profileDir = Path.Combine(tempDir, "profile");
                        Directory.CreateDirectory(profileDir);
                        await service.GenerateClaudeMdAsync(profileDir, mission, vessel).ConfigureAwait(false);
                        string profileBrief = await File.ReadAllTextAsync(Path.Combine(profileDir, "CLAUDE.md")).ConfigureAwait(false);
                        AssertContains("PROFILE TEST ENGINEER PROMPT", profileBrief,
                            "the active project-profile override takes precedence over the database persona template");
                        AssertFalse(profileBrief.Contains("CUSTOM TEST ENGINEER PROMPT"),
                            "the project-profile template replaces the shared database persona template");

                    }
                    finally
                    {
                        try { Directory.Delete(tempDir, true); } catch { }
                    }
                }
            });

            await RunTest("A brief names the assigned runtime's instruction file, not the one a prior stage named", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    StubGitService git = new StubGitService();
                    MissionService service = CreateMissionService(logging, testDb.Driver, settings, git);
                    string tempDir = Path.Combine(Path.GetTempPath(), "armada_prompt_test_" + Guid.NewGuid().ToString("N"));
                    string trackedDir = Path.Combine(tempDir, "tracked");
                    Directory.CreateDirectory(trackedDir);

                    try
                    {
                        Vessel vessel = new Vessel("InstructionNameVessel", "https://github.com/test/repo");
                        Captain captain = new Captain("cursor-captain");
                        captain.Runtime = AgentRuntimeEnum.Cursor;
                        Mission mission = new Mission();
                        mission.Title = "Carry a plan written on another runtime";
                        mission.Persona = "Worker";
                        mission.Description = "**Reads:**\n- `CODEX.md` objective brief and Product Manager handoff\n- MUX.md stage notes\n";

                        await service.GenerateClaudeMdAsync(tempDir, mission, vessel, captain);
                        string brief = await File.ReadAllTextAsync(Path.Combine(tempDir, "CURSOR.md"));
                        AssertContains("`CURSOR.md` objective brief", brief, "a foreign instruction filename is retargeted to the captain's own file");
                        AssertContains("- CURSOR.md stage notes", brief, "every foreign Armada instruction filename is retargeted");
                        AssertFalse(brief.Contains("`CODEX.md` objective brief"), "a Cursor brief must not send the captain to CODEX.md, which this dock does not hold");
                        AssertFalse(brief.Contains("MUX.md stage notes"), "a Cursor brief must not send the captain to MUX.md, which this dock does not hold");

                        await File.WriteAllTextAsync(Path.Combine(trackedDir, "CODEX.md"), "# repository notes\n");
                        await service.GenerateClaudeMdAsync(trackedDir, mission, vessel, captain);
                        string trackedBrief = await File.ReadAllTextAsync(Path.Combine(trackedDir, "CURSOR.md"));
                        AssertContains("`CODEX.md` objective brief", trackedBrief, "a filename the checkout really holds is repository content and is kept");
                    }
                    finally
                    {
                        try { Directory.Delete(tempDir, true); } catch { }
                    }
                }
            });

            await RunTest("Objective scope and staged reports name the assigned runtime's instruction file", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    StubGitService git = new StubGitService();
                    MissionService service = CreateMissionService(logging, testDb.Driver, settings, git);
                    string tempDir = Path.Combine(Path.GetTempPath(), "armada_prompt_test_" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(tempDir);

                    try
                    {
                        Voyage voyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("retarget-scope-voyage"));
                        Objective objective = new Objective();
                        objective.Title = "Carry a plan across runtimes";
                        objective.Description = "Follow the reads list in CODEX.md before editing.";
                        objective.AcceptanceCriteria = new List<string> { "The plan named in MUX.md is applied." };
                        objective.VoyageIds = new List<string> { voyage.Id };
                        await testDb.Driver.Objectives.CreateAsync(objective);

                        string report = "Worker report: read CODEX.md, then apply the plan.";
                        Mission worker = await testDb.Driver.Missions.CreateAsync(new Mission("Worker on another runtime")
                        {
                            Persona = "Worker",
                            AgentOutput = report,
                            Status = MissionStatusEnum.Complete
                        });

                        Vessel vessel = new Vessel("RetargetScopeVessel", "https://github.com/test/repo");
                        Captain captain = new Captain("cursor-captain");
                        captain.Runtime = AgentRuntimeEnum.Cursor;
                        Mission judge = new Mission();
                        judge.Title = "Review the plan";
                        judge.Persona = "Judge";
                        judge.VoyageId = voyage.Id;
                        judge.Description = "Prior stage output: mission-output:" + worker.Id + "\n";

                        await service.GenerateClaudeMdAsync(tempDir, judge, vessel, captain);
                        string brief = await File.ReadAllTextAsync(Path.Combine(tempDir, "CURSOR.md"));

                        AssertContains("Follow the reads list in CURSOR.md before editing.", brief, "objective scope names this runtime's instruction file");
                        AssertContains("The plan named in CURSOR.md is applied.", brief, "acceptance criteria name this runtime's instruction file");
                        AssertFalse(brief.Contains("reads list in CODEX.md"), "objective scope must not name a file this dock does not hold");

                        string staged = await File.ReadAllTextAsync(Path.Combine(tempDir, "_briefing", "handoff", worker.Id + ".md"));
                        AssertEqual(report, staged, "a staged report keeps its exact bytes, so its digest still matches");
                        AssertContains("## Instruction File Names in Staged Reports", brief, "the brief names the replacement for a staged report");
                        AssertContains("`CODEX.md`, which this dock does not hold. Read each as `CURSOR.md`", brief, "the section names the foreign file and the replacement");
                        AssertContains("- `_briefing/handoff/" + worker.Id + ".md`", brief, "the section lists the staged report that names it");
                    }
                    finally
                    {
                        try { Directory.Delete(tempDir, true); } catch { }
                    }
                }
            });

            await RunTest("A Git Anchors block copied into the description is dropped from the brief", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    StubGitService git = new StubGitService();
                    MissionService service = CreateMissionService(logging, testDb.Driver, settings, git);
                    string tempDir = Path.Combine(Path.GetTempPath(), "armada_prompt_test_" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(tempDir);

                    try
                    {
                        Vessel vessel = new Vessel("CopiedAnchorsVessel", "https://github.com/test/repo");
                        Mission mission = new Mission();
                        mission.Title = "Continue the plan";
                        mission.Persona = "Worker";
                        mission.Description =
                            "Apply the plan below.\n\n" +
                            "## Git Anchors\n" +
                            "Resolved by the admiral at dispatch.\n\n" +
                            "- Your work starts at (this checkout's HEAD): `0ldbase1`\n" +
                            "\n### Recent history of the paths this mission names\n" +
                            "- `src/StaleAnchorPath.cs` does not exist on this checkout. It is new work, not an edit.\n\n" +
                            "### Agent Output (from Architect stage)\n" +
                            "Keep-this-handoff-line\n\n" +
                            "## Plan\n" +
                            "Keep-this-plan-line\n";

                        await service.GenerateClaudeMdAsync(tempDir, mission, vessel);
                        string brief = await File.ReadAllTextAsync(Path.Combine(tempDir, "CLAUDE.md"));

                        AssertFalse(brief.Contains("0ldbase1"), "a copied anchor block's start commit must not reach the brief");
                        AssertFalse(brief.Contains("StaleAnchorPath"), "a copied anchor block's path history must not reach the brief");
                        AssertContains("Apply the plan below.", brief, "text before the copied block is kept");
                        AssertContains("### Agent Output (from Architect stage)\nKeep-this-handoff-line", brief, "a sub-heading the anchors section does not render ends the copied block");
                        AssertContains("## Plan\nKeep-this-plan-line", brief, "the next section is kept");
                    }
                    finally
                    {
                        try { Directory.Delete(tempDir, true); } catch { }
                    }
                }
            });

            await RunTest("A stage whose start lacks its predecessor's commit records an event and one brief line", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    StubGitService git = new StubGitService();
                    git.HeadCommitHashResult = new string('a', 40);
                    IDockService docks = new DockService(logging, testDb.Driver, settings, git);
                    ICaptainService captains = new CaptainService(logging, testDb.Driver, settings, git, docks);
                    MissionService service = new MissionService(logging, testDb.Driver, settings, docks, captains, git: git, resourcePressureAdmission: TestResourcePressure.Unconstrained(settings));
                    string tempDir = Path.Combine(Path.GetTempPath(), "armada_prompt_test_" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(tempDir);

                    try
                    {
                        Vessel vessel = await testDb.Driver.Vessels.CreateAsync(new Vessel("StartGapVessel", "https://github.com/test/repo"));
                        string predecessorCommit = new string('b', 40);
                        Mission predecessor = await testDb.Driver.Missions.CreateAsync(new Mission("Predecessor stage")
                        {
                            Persona = "Worker",
                            VesselId = vessel.Id,
                            CommitHash = predecessorCommit,
                            Status = MissionStatusEnum.WorkProduced
                        });
                        Mission stage = await testDb.Driver.Missions.CreateAsync(new Mission("Later stage")
                        {
                            Persona = "Worker",
                            VesselId = vessel.Id,
                            DependsOnMissionId = predecessor.Id,
                            Description = "Continue from the predecessor."
                        });

                        git.IsAncestorResult = false;
                        await service.GenerateClaudeMdAsync(tempDir, stage, vessel);
                        string brief = await File.ReadAllTextAsync(Path.Combine(tempDir, "CLAUDE.md"));
                        AssertContains("This checkout starts at `" + new string('a', 40) + "`, which does not contain `" + predecessorCommit + "`", brief,
                            "the brief names the start commit and the predecessor commit it lacks");
                        List<ArmadaEvent> events = await testDb.Driver.Events.EnumerateByTypeAsync("mission.start_behind_predecessor", 10);
                        AssertEqual(1, events.Count, "one start_behind_predecessor event is recorded");
                        AssertEqual(stage.Id, events[0].MissionId, "the event names the stage");
                        AssertContains(predecessorCommit, events[0].Payload ?? "", "the event payload names the predecessor commit");

                        git.IsAncestorResult = true;
                        string containedDir = Path.Combine(tempDir, "contained");
                        Directory.CreateDirectory(containedDir);
                        await service.GenerateClaudeMdAsync(containedDir, stage, vessel);
                        string containedBrief = await File.ReadAllTextAsync(Path.Combine(containedDir, "CLAUDE.md"));
                        AssertFalse(containedBrief.Contains("which does not contain"), "a start that contains the predecessor commit adds no line");

                        git.IsAncestorResult = null;
                        string unknownDir = Path.Combine(tempDir, "unknown");
                        Directory.CreateDirectory(unknownDir);
                        await service.GenerateClaudeMdAsync(unknownDir, stage, vessel);
                        string unknownBrief = await File.ReadAllTextAsync(Path.Combine(unknownDir, "CLAUDE.md"));
                        AssertFalse(unknownBrief.Contains("which does not contain"), "an ancestry probe that cannot answer is not a gap");
                        AssertEqual(1, (await testDb.Driver.Events.EnumerateByTypeAsync("mission.start_behind_predecessor", 10)).Count,
                            "no event is recorded when the start contains the commit or the probe cannot answer");
                    }
                    finally
                    {
                        try { Directory.Delete(tempDir, true); } catch { }
                    }
                }
            });

            await RunTest("A brief points every read of an unstaged _briefing file at the inline text and lists the other absent paths", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    StubGitService git = new StubGitService();
                    MissionService service = CreateMissionService(logging, testDb.Driver, settings, git);
                    string tempDir = Path.Combine(Path.GetTempPath(), "armada_prompt_test_" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(Path.Combine(tempDir, "_briefing"));

                    try
                    {
                        await File.WriteAllTextAsync(Path.Combine(tempDir, "_briefing", "context-pack.md"), "# pack\n");
                        Vessel vessel = new Vessel("StagedPathVessel", "https://github.com/test/repo");
                        Mission mission = new Mission();
                        mission.Title = "Use the staged inputs";
                        mission.Persona = "Worker";
                        mission.Description = "Read `_briefing/context-pack.md`, then _briefing/mission/01-description.md through " +
                            "_briefing/mission/03-description.md.\nThe source table is kept at _briefing/source/table.md.";

                        await service.GenerateClaudeMdAsync(tempDir, mission, vessel);
                        string brief = await File.ReadAllTextAsync(Path.Combine(tempDir, "CLAUDE.md"));
                        const string heading = "## Named Files Not Staged";
                        int sectionStart = brief.IndexOf(heading, StringComparison.Ordinal);
                        AssertTrue(sectionStart >= 0, "a brief that names unstaged _briefing paths carries the unstaged-paths section");
                        string section = brief.Substring(sectionStart);
                        int sectionEnd = section.IndexOf("\n## ", 3, StringComparison.Ordinal);
                        if (sectionEnd > 0) section = section.Substring(0, sectionEnd);
                        AssertFalse(brief.Contains("_briefing/mission/01-description.md"), "a read of an absent description part is not left in the brief");
                        AssertFalse(brief.Contains("_briefing/mission/03-description.md"), "the far end of a named range is not left in the brief");
                        AssertContains("Read `_briefing/context-pack.md`, then the inline text of this brief.", brief,
                            "the read of the absent range points at the inline text, and the staged file stays named");
                        AssertContains("_briefing/source/table.md", section, "an absent path that no line asks to read is listed");
                        AssertFalse(section.Contains("context-pack.md"), "a staged file is not listed as absent");

                        string cleanDir = Path.Combine(tempDir, "clean");
                        Directory.CreateDirectory(cleanDir);
                        mission.Description = "Implement the change named in the title.";
                        await service.GenerateClaudeMdAsync(cleanDir, mission, vessel);
                        string cleanBrief = await File.ReadAllTextAsync(Path.Combine(cleanDir, "CLAUDE.md"));
                        AssertFalse(cleanBrief.Contains(heading), "a brief naming no absent path carries no section");
                    }
                    finally
                    {
                        try { Directory.Delete(tempDir, true); } catch { }
                    }
                }
            });

            await RunTest("A brief reports each declared sibling's HEAD and uncommitted changes, and names an unprovisioned sibling as absent", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    StubGitService git = new StubGitService();
                    git.HeadCommitHashResult = "0123456789abcdef0123456789abcdef01234567";
                    git.IsWorkingDirectoryCleanResult = false;
                    IDockService docks = new DockService(logging, testDb.Driver, settings, git);
                    ICaptainService captains = new CaptainService(logging, testDb.Driver, settings, git, docks);
                    MissionService service = new MissionService(logging, testDb.Driver, settings, docks, captains, git: git, resourcePressureAdmission: TestResourcePressure.Unconstrained(settings));
                    string root = Path.Combine(Path.GetTempPath(), "armada_sibling_brief_" + Guid.NewGuid().ToString("N"));
                    string worktree = Path.Combine(root, "target");
                    Directory.CreateDirectory(worktree);
                    Directory.CreateDirectory(Path.Combine(root, "present"));

                    try
                    {
                        await File.WriteAllTextAsync(Path.Combine(root, "present", "readme.md"), "sibling\n");
                        Vessel vessel = new Vessel("SiblingBriefVessel", "https://github.com/test/repo");
                        vessel.SiblingRepos = JsonSerializer.Serialize(new List<SiblingRepo>
                        {
                            new SiblingRepo { RelativePath = "../present" },
                            new SiblingRepo { RelativePath = "../absent" }
                        });
                        Mission mission = new Mission { Title = "Read the sibling", Persona = "Worker", Description = "Compare the sibling reader." };

                        await service.GenerateClaudeMdAsync(worktree, mission, vessel);
                        string brief = await File.ReadAllTextAsync(Path.Combine(worktree, "CLAUDE.md"));
                        AssertContains("## Sibling Checkouts", brief, "a vessel that declares siblings gets the sibling section");
                        AssertContains("`../present` HEAD `0123456789ab`, with uncommitted changes", brief, "the provisioned HEAD and its uncommitted changes are named");
                        AssertContains("`../absent` is absent", brief, "a sibling provisioning did not produce is named as absent");
                        AssertContains("`grep -rn <pattern> <sibling path>`", brief, "a launch without workspace roots names the shell search");

                        Captain claude = new Captain("sibling-claude", AgentRuntimeEnum.ClaudeCode);
                        File.Delete(Path.Combine(worktree, "CLAUDE.md"));
                        await service.GenerateClaudeMdAsync(worktree, mission, vessel, claude);
                        string claudeBrief = await File.ReadAllTextAsync(Path.Combine(worktree, "CLAUDE.md"));
                        AssertContains("Built-in search reaches these checkouts", claudeBrief, "a runtime that receives workspace roots is told its search reaches the siblings");

                        string plain = Path.Combine(root, "plain");
                        Directory.CreateDirectory(plain);
                        await service.GenerateClaudeMdAsync(plain, mission, new Vessel("NoSiblingVessel", "https://github.com/test/repo"));
                        AssertFalse((await File.ReadAllTextAsync(Path.Combine(plain, "CLAUDE.md"))).Contains("## Sibling Checkouts"),
                            "a vessel with no sibling declaration gets no sibling section");
                    }
                    finally
                    {
                        try { Directory.Delete(root, true); } catch { }
                    }
                }
            });

            await RunTest("The sibling brief lists assigned paths changed since an ancestor pin and says HEAD controls over a pin that is not an ancestor", async () =>
            {
                LoggingModule logging = CreateLogging();
                string root = Path.Combine(Path.GetTempPath(), "armada_sibling_pin_" + Guid.NewGuid().ToString("N"));
                string worktree = Path.Combine(root, "target");
                string sibling = Path.Combine(root, "source");
                Directory.CreateDirectory(worktree);
                Directory.CreateDirectory(Path.Combine(sibling, "assigned"));
                Directory.CreateDirectory(Path.Combine(sibling, "other"));

                try
                {
                    await RunGitAsync(sibling, "init", "-q", "-b", "main");
                    await RunGitAsync(sibling, "config", "user.email", "test@example.invalid");
                    await RunGitAsync(sibling, "config", "user.name", "Test");
                    await File.WriteAllTextAsync(Path.Combine(sibling, "assigned", "reader.txt"), "one\n");
                    await File.WriteAllTextAsync(Path.Combine(sibling, "other", "notes.txt"), "one\n");
                    await RunGitAsync(sibling, "add", "-A");
                    await RunGitAsync(sibling, "commit", "-q", "-m", "first");
                    string pin = (await RunGitAsync(sibling, "rev-parse", "HEAD")).Trim();
                    await RunGitAsync(sibling, "checkout", "-q", "-b", "side");
                    await File.WriteAllTextAsync(Path.Combine(sibling, "other", "side.txt"), "side\n");
                    await RunGitAsync(sibling, "add", "-A");
                    await RunGitAsync(sibling, "commit", "-q", "-m", "side");
                    string sidePin = (await RunGitAsync(sibling, "rev-parse", "HEAD")).Trim();
                    await RunGitAsync(sibling, "checkout", "-q", "main");
                    await File.WriteAllTextAsync(Path.Combine(sibling, "assigned", "reader.txt"), "two\n");
                    await File.WriteAllTextAsync(Path.Combine(sibling, "other", "notes.txt"), "two\n");
                    await RunGitAsync(sibling, "add", "-A");
                    await RunGitAsync(sibling, "commit", "-q", "-m", "second");

                    Vessel vessel = new Vessel("PinVessel", "https://github.com/test/repo");
                    vessel.SiblingRepos = JsonSerializer.Serialize(new List<SiblingRepo> { new SiblingRepo { RelativePath = "../source" } });
                    Objective objective = new Objective { Title = "pinned" };
                    ObjectivePreparationSiblingInput input = new ObjectivePreparationSiblingInput
                    {
                        VesselRef = "source",
                        RelativePath = "../source",
                        RequiredArtifactPaths = new List<string> { "assigned", "missing-tree" },
                        PinnedCommit = pin
                    };
                    objective.Preparation.RequiredSiblingInputs.Add(input);
                    GitService git = new GitService(logging);

                    List<SiblingCheckoutState> states = await SiblingCheckoutBrief.ReadAsync(git, vessel, worktree, objective);
                    string section = SiblingCheckoutBrief.Render(states, false);
                    AssertContains("an ancestor of HEAD; changed since: `assigned/reader.txt`.", section, "only the assigned paths changed since the pin are listed");
                    AssertFalse(section.Contains("other/notes.txt"), "a changed path outside the assigned paths is not listed");
                    AssertContains("Required paths absent: `missing-tree`", section, "a required path the checkout does not hold is named");

                    input.PinnedCommit = sidePin;
                    string divergent = SiblingCheckoutBrief.Render(await SiblingCheckoutBrief.ReadAsync(git, vessel, worktree, objective), false);
                    AssertContains("not an ancestor of HEAD; HEAD controls.", divergent, "a pin off the provisioned history defers to HEAD");
                }
                finally
                {
                    try { Directory.Delete(root, true); } catch { }
                }
            });

            await RunTest("Configured brief path rewrites turn host paths in the description into the paths a captain resolves", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    settings.BriefPathRewrites = new List<BriefPathRewrite> { new BriefPathRewrite { From = "/host/volume/", To = "/mnt/evidence" } };
                    StubGitService git = new StubGitService();
                    MissionService service = CreateMissionService(logging, testDb.Driver, settings, git);
                    string tempDir = Path.Combine(Path.GetTempPath(), "armada_prompt_test_" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(tempDir);

                    try
                    {
                        Mission mission = new Mission
                        {
                            Title = "Read the packet",
                            Persona = "Worker",
                            Description = "Read /host/volume/packets/run-1/report.md and leave /host/volume-old/notes.md and /other/host/volume/x.md alone."
                        };
                        await service.GenerateClaudeMdAsync(tempDir, mission, new Vessel("RewriteVessel", "https://github.com/test/repo"));
                        string brief = await File.ReadAllTextAsync(Path.Combine(tempDir, "CLAUDE.md"));
                        AssertContains("/mnt/evidence/packets/run-1/report.md", brief, "a host path prefix is rewritten to the captain path");
                        AssertFalse(brief.Contains("/host/volume/packets"), "the host path no longer appears");
                        AssertContains("/host/volume-old/notes.md", brief, "a longer directory sharing the prefix is left alone");
                        AssertContains("/other/host/volume/x.md", brief, "a path that only contains the prefix is left alone");

                        ArmadaSettings reloaded = CreateSettings();
                        reloaded.ApplyHotReloadableFrom(settings);
                        AssertEqual(1, reloaded.BriefPathRewrites.Count, "a settings reload carries the rewrites");
                    }
                    finally
                    {
                        try { Directory.Delete(tempDir, true); } catch { }
                    }
                }
            });

            await RunTest("A brief stages the complete output of every earlier stage it cites, and nothing from another owner", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    StubGitService git = new StubGitService();
                    MissionService service = CreateMissionService(logging, testDb.Driver, settings, git);
                    string tempDir = Path.Combine(Path.GetTempPath(), "armada_prompt_test_" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(tempDir);

                    try
                    {
                        string report = "HEAD-SENTINEL\n" + new string('a', 20000) + "\nMIDDLE-SENTINEL\n" + new string('b', 20000) + "\nTAIL-SENTINEL";
                        Mission worker = await testDb.Driver.Missions.CreateAsync(new Mission("Long worker report")
                        {
                            Persona = "Worker",
                            AgentOutput = report,
                            Status = MissionStatusEnum.Complete
                        });
                        UserMaster otherUser = await testDb.Driver.Users.CreateAsync(
                            new UserMaster(Armada.Core.Constants.DefaultTenantId, "handoff-other@example.com", "password"));
                        Mission foreign = await testDb.Driver.Missions.CreateAsync(new Mission("Other owner report")
                        {
                            Persona = "Worker",
                            UserId = otherUser.Id,
                            AgentOutput = "PRIVATE-SENTINEL",
                            Status = MissionStatusEnum.Complete
                        });

                        Vessel vessel = new Vessel("HandoffOutputVessel", "https://github.com/test/repo");
                        Mission judge = new Mission();
                        judge.Title = "Review the long report";
                        judge.Persona = "Judge";
                        judge.Description = "## Prior Stage Output\nAuthoritative safe artifact: mission-output:" + worker.Id +
                            "\nAlso cited: mission-output:" + foreign.Id + "\n";

                        await service.GenerateClaudeMdAsync(tempDir, judge, vessel);

                        string staged = Path.Combine(tempDir, "_briefing", "handoff", worker.Id + ".md");
                        AssertTrue(File.Exists(staged), "the cited earlier stage's complete output is staged in the dock");
                        string content = await File.ReadAllTextAsync(staged);
                        AssertContains("MIDDLE-SENTINEL", content, "the staged output is not a head-and-tail preview");
                        AssertEqual(MissionOutputArtifact.Build(worker).Sha256,
                            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant(),
                            "the staged file has the digest the brief states");
                        AssertFalse(File.Exists(Path.Combine(tempDir, "_briefing", "handoff", foreign.Id + ".md")),
                            "another owner's output is never staged");
                    }
                    finally
                    {
                        try { Directory.Delete(tempDir, true); } catch { }
                    }
                }
            });

            await RunTest("Judge and Worker briefs hold only ASCII, so the acceptance-line form agrees with the ASCII rule", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    StubGitService git = new StubGitService();
                    MissionService service = CreateMissionService(logging, testDb.Driver, settings, git);
                    string tempDir = Path.Combine(Path.GetTempPath(), "armada_prompt_test_" + Guid.NewGuid().ToString("N"));

                    try
                    {
                        foreach (string persona in new[] { "Judge", "Worker" })
                        {
                            string dir = Path.Combine(tempDir, persona);
                            Directory.CreateDirectory(dir);
                            Vessel vessel = new Vessel("AsciiBriefVessel", "https://github.com/test/repo");
                            Mission mission = new Mission();
                            mission.Title = "Review the parser change";
                            mission.Persona = persona;
                            mission.Description = "## Acceptance Criteria\n\n- The parser rejects a truncated frame.\n";

                            await service.GenerateClaudeMdAsync(dir, mission, vessel);
                            string brief = await File.ReadAllTextAsync(Path.Combine(dir, "CLAUDE.md"));
                            List<string> offenders = new List<string>();
                            foreach (string line in brief.Split('\n'))
                            {
                                if (line.Any(c => c > 0x7E)) offenders.Add(line.Length > 120 ? line.Substring(0, 120) : line);
                            }
                            AssertEqual(0, offenders.Count, "a " + persona + " brief built from ASCII input holds only ASCII: " + String.Join(" | ", offenders));
                        }

                        string asciiLine = "- The parser rejects a truncated frame. -- MET. src/Parser/FrameParser.cs:88 returns Truncated";
                        AssertNull(JudgeAcceptanceWalk.ValidatePass("## Acceptance Criteria\n\n" + asciiLine + "\n",
                            "## Acceptance Criteria\n\n- The parser rejects a truncated frame.\n"),
                            "the ASCII acceptance line the contract asks for passes the mechanical check");
                    }
                    finally
                    {
                        try { Directory.Delete(tempDir, true); } catch { }
                    }
                }
            });

            await RunTest("A Linter brief verifies edits in full without a duplicate unchanged review run, and only Codex names its shell commands", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    StubGitService git = new StubGitService();
                    MissionService service = CreateMissionService(logging, testDb.Driver, settings, git);
                    string tempDir = Path.Combine(Path.GetTempPath(), "armada_prompt_test_" + Guid.NewGuid().ToString("N"));

                    try
                    {
                        Vessel vessel = new Vessel("LinterVessel", "https://github.com/test/repo");
                        Mission linter = new Mission();
                        linter.Title = "Lint the change";
                        linter.Persona = "Linter";
                        linter.Description = "Lint the diff.";

                        Captain codex = new Captain("codex-captain");
                        codex.Runtime = AgentRuntimeEnum.Codex;
                        string codexDir = Path.Combine(tempDir, "codex");
                        Directory.CreateDirectory(codexDir);
                        await service.GenerateClaudeMdAsync(codexDir, linter, vessel, codex);
                        string codexBrief = await File.ReadAllTextAsync(Path.Combine(codexDir, MissionPromptBuilder.GetInstructionsFileName(codex.Runtime.ToString())));
                        AssertContains("scripts/check.sh", codexBrief, "the Linter verifies with the repository check script");
                        AssertContains("armada_suite_lease", codexBrief, "the Linter takes the suite lease around a full-suite run");
                        AssertContains("If you leave the tracked tree unchanged", codexBrief, "an unchanged Linter uses bounded review");
                        AssertContains("If you change any tracked file", codexBrief, "edits retain full repository verification");
                        AssertContains("Automatic DoD gates and required structured Checks remain required", codexBrief, "review guidance does not waive gates");
                        AssertContains("## Codex Shell", codexBrief, "a Codex brief names the commands its runner accepts");
                        AssertContains("sha256sum", codexBrief, "a Codex brief names the digest command");

                        Captain claude = new Captain("claude-captain");
                        claude.Runtime = AgentRuntimeEnum.ClaudeCode;
                        string claudeDir = Path.Combine(tempDir, "claude");
                        Directory.CreateDirectory(claudeDir);
                        await service.GenerateClaudeMdAsync(claudeDir, linter, vessel, claude);
                        string claudeBrief = await File.ReadAllTextAsync(Path.Combine(claudeDir, MissionPromptBuilder.GetInstructionsFileName(claude.Runtime.ToString())));
                        AssertContains("If you leave the tracked tree unchanged", claudeBrief, "the unchanged-review rule is runtime independent");
                        AssertFalse(claudeBrief.Contains("## Codex Shell"), "another runtime's brief carries no Codex section");
                    }
                    finally
                    {
                        try { Directory.Delete(tempDir, true); } catch { }
                    }
                }
            });

            await RunTest("A read-only brief lets a criterion that requires a test run win over the read-only default, and keeps the default otherwise", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    StubGitService git = new StubGitService();
                    MissionService service = CreateMissionService(logging, testDb.Driver, settings, git);
                    string tempDir = Path.Combine(Path.GetTempPath(), "armada_prompt_test_" + Guid.NewGuid().ToString("N"));
                    string runDir = Path.Combine(tempDir, "run");
                    string reportDir = Path.Combine(tempDir, "report");
                    Directory.CreateDirectory(runDir);
                    Directory.CreateDirectory(reportDir);
                    const string defaultRule = "- Do not run builds, tests, or any command that changes repository state.";

                    try
                    {
                        Vessel vessel = new Vessel("ReadOnlyRunVessel", "https://github.com/test/repo");

                        Voyage runVoyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("read-only-run-voyage")).ConfigureAwait(false);
                        Objective runObjective = new Objective();
                        runObjective.Title = "Verify the parser report";
                        runObjective.Kind = ObjectiveKindEnum.Research;
                        runObjective.Description = "Report whether the parser handles empty records.";
                        runObjective.AcceptanceCriteria = new List<string>
                        {
                            "The report cites each parser branch it read.",
                            "Run the focused parser test class and quote its totals."
                        };
                        runObjective.VoyageIds = new List<string> { runVoyage.Id };
                        await testDb.Driver.Objectives.CreateAsync(runObjective).ConfigureAwait(false);

                        Mission runMission = new Mission();
                        runMission.Title = "Verify the parser report";
                        runMission.Persona = "Worker";
                        runMission.Mode = MissionModeEnum.Research;
                        runMission.Description = "Report whether the parser handles empty records.";
                        runMission.VoyageId = runVoyage.Id;
                        await service.GenerateClaudeMdAsync(runDir, runMission, vessel);
                        string runBrief = await File.ReadAllTextAsync(Path.Combine(runDir, "CLAUDE.md"));
                        AssertContains(MissionService.ReadOnlyVerificationRunRule, runBrief,
                            "a read-only brief whose criteria require a test run lets the criterion win and says so");
                        AssertFalse(runBrief.Contains(defaultRule), "the brief does not also forbid the run the criterion requires");
                        AssertContains("Do not commit, stage, or push anything.", runBrief, "read-only still forbids commits");

                        Voyage reportVoyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("read-only-report-voyage")).ConfigureAwait(false);
                        Objective reportObjective = new Objective();
                        reportObjective.Title = "Describe the parser";
                        reportObjective.Kind = ObjectiveKindEnum.Research;
                        reportObjective.Description = "Report how the parser handles empty records.";
                        reportObjective.AcceptanceCriteria = new List<string> { "The report cites each parser branch it read." };
                        reportObjective.VoyageIds = new List<string> { reportVoyage.Id };
                        await testDb.Driver.Objectives.CreateAsync(reportObjective).ConfigureAwait(false);

                        Mission reportMission = new Mission();
                        reportMission.Title = "Describe the parser";
                        reportMission.Persona = "Worker";
                        reportMission.Mode = MissionModeEnum.Research;
                        reportMission.Description = "Report how the parser handles empty records.";
                        reportMission.VoyageId = reportVoyage.Id;
                        await service.GenerateClaudeMdAsync(reportDir, reportMission, vessel);
                        string reportBrief = await File.ReadAllTextAsync(Path.Combine(reportDir, "CLAUDE.md"));
                        AssertContains(defaultRule, reportBrief, "a read-only brief with no run-requiring criterion keeps the default rule");
                        AssertFalse(reportBrief.Contains(MissionService.ReadOnlyVerificationRunRule),
                            "the verification-run rule appears only when a criterion requires a run");
                    }
                    finally
                    {
                        try { Directory.Delete(tempDir, true); } catch { }
                    }
                }
            });

            await RunTest("Objective Scope names the objective id and states that a non-goal wins over a conflicting role duty", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    StubGitService git = new StubGitService();
                    MissionService service = CreateMissionService(logging, testDb.Driver, settings, git);
                    string tempDir = Path.Combine(Path.GetTempPath(), "armada_prompt_test_" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(tempDir);

                    try
                    {
                        Voyage voyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("rule-wins-voyage")).ConfigureAwait(false);
                        Objective objective = new Objective();
                        objective.Title = "Correct the audit report";
                        objective.Description = "Rewrite the report section.";
                        objective.NonGoals = new List<string> { "No new tests; the report is the only changed file." };
                        objective.VoyageIds = new List<string> { voyage.Id };
                        objective = await testDb.Driver.Objectives.CreateAsync(objective).ConfigureAwait(false);

                        Vessel vessel = new Vessel("RuleWinsVessel", "https://github.com/test/repo");
                        Mission tester = new Mission();
                        tester.Title = "Validate the report";
                        tester.Persona = "TestEngineer";
                        tester.Description = "Validate the report change.";
                        tester.VoyageId = voyage.Id;

                        await service.GenerateClaudeMdAsync(tempDir, tester, vessel);
                        string testerBrief = await File.ReadAllTextAsync(Path.Combine(tempDir, "CLAUDE.md"));
                        AssertContains(objective.Id, testerBrief, "the brief states the own objective id");
                        AssertContains("validate-only", testerBrief, "a TestEngineer on a no-new-test objective gets a validate-only duty");
                        AssertContains("the non-goal wins", testerBrief, "the brief records which rule wins");

                        string workerDir = Path.Combine(tempDir, "worker");
                        Directory.CreateDirectory(workerDir);
                        Mission worker = new Mission();
                        worker.Title = "Rewrite the report";
                        worker.Persona = "Worker";
                        worker.Description = "Rewrite the report section.";
                        worker.VoyageId = voyage.Id;
                        await service.GenerateClaudeMdAsync(workerDir, worker, vessel);
                        string workerBrief = await File.ReadAllTextAsync(Path.Combine(workerDir, "CLAUDE.md"));
                        AssertContains("the non-goal wins", workerBrief, "every stage learns that a non-goal wins over a conflicting duty");
                        AssertFalse(workerBrief.Contains("validate-only"), "the validate-only duty is for the TestEngineer only");
                    }
                    finally
                    {
                        try { Directory.Delete(tempDir, true); } catch { }
                    }
                }
            });

            await RunTest("A TestEngineer brief names native profile commands and inputs without exposing environment values", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = CreateSettings();
                    MissionService service = CreateMissionService(CreateLogging(), testDb.Driver, settings, new StubGitService());
                    string tempDir = Path.Combine(Path.GetTempPath(), "armada_profile_brief_" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(tempDir);
                    try
                    {
                        Vessel vessel = await testDb.Driver.Vessels.CreateAsync(new Vessel("Profile brief", "https://example.invalid/repo")).ConfigureAwait(false);
                        WorkflowProfile profile = await testDb.Driver.WorkflowProfiles.CreateAsync(new WorkflowProfile
                        {
                            Name = "Native validation", Scope = WorkflowProfileScopeEnum.Vessel, VesselId = vessel.Id,
                            BuildCommand = "dotnet build scoped.sln",
                            UnitTestCommand = "sh scripts/check.sh opaque-profile-value",
                            EnvironmentVariables = new Dictionary<string, string>
                            { ["EXTERNAL_ARTIFACT_HOST"] = "opaque-profile-value", ["SERVICE_TOKEN"] = "hidden-profile-token" }
                        }).ConfigureAwait(false);
                        Mission mission = new Mission("Validate current source")
                        { Persona = "TestEngineer", VesselId = vessel.Id };
                        await service.GenerateClaudeMdAsync(tempDir, mission, vessel).ConfigureAwait(false);
                        string brief = await File.ReadAllTextAsync(Path.Combine(tempDir, "CLAUDE.md")).ConfigureAwait(false);
                        AssertContains("## Native Required Checks", brief);
                        AssertContains("dotnet build scoped.sln", brief);
                        AssertContains("sh scripts/check.sh", brief);
                        AssertContains("EXTERNAL_ARTIFACT_HOST", brief);
                        AssertContains("SERVICE_TOKEN", brief);
                        AssertFalse(brief.Contains("opaque-profile-value", StringComparison.Ordinal));
                        AssertFalse(brief.Contains("hidden-profile-token", StringComparison.Ordinal));
                        AssertContains("operator settings", brief);
                        AssertContains("missing inputs", brief);
                        AssertContains("native Check", brief);
                        foreach (string unsafeCommand in new[]
                        {
                            "tool --password inline-password-sentinel",
                            "curl -H 'Authorization: Basic inline-basic-sentinel' https://example.invalid",
                            "curl https://alice:inline-userinfo-sentinel@example.invalid/check",
                            "curl -ualice:inline-attached-user-sentinel https://example.invalid/check",
                            "curl -H'Cookie: inline-attached-header-sentinel' https://example.invalid/check",
                            "curl -binline-cookie-sentinel https://example.invalid/check"
                        })
                        {
                            profile.UnitTestCommand = unsafeCommand;
                            profile.Name = new string('N', 6000) + "\nPROFILE_LABEL_TAIL";
                            await testDb.Driver.WorkflowProfiles.UpdateAsync(profile).ConfigureAwait(false);
                            await service.GenerateClaudeMdAsync(tempDir, mission, vessel).ConfigureAwait(false);
                            string protectedBrief = await File.ReadAllTextAsync(Path.Combine(tempDir,
                                MissionPromptBuilder.GetGeneratedInstructionsRelativePath(null))).ConfigureAwait(false);
                            AssertContains("[WITHHELD: credential-bearing native command", protectedBrief);
                            AssertFalse(protectedBrief.Contains("inline-password-sentinel", StringComparison.Ordinal));
                            AssertFalse(protectedBrief.Contains("inline-basic-sentinel", StringComparison.Ordinal));
                            AssertFalse(protectedBrief.Contains("inline-userinfo-sentinel", StringComparison.Ordinal));
                            AssertFalse(protectedBrief.Contains("inline-attached-user-sentinel", StringComparison.Ordinal));
                            AssertFalse(protectedBrief.Contains("inline-attached-header-sentinel", StringComparison.Ordinal));
                            AssertFalse(protectedBrief.Contains("inline-cookie-sentinel", StringComparison.Ordinal));
                            AssertFalse(protectedBrief.Contains("PROFILE_LABEL_TAIL", StringComparison.Ordinal));
                            AssertFalse(protectedBrief.Contains(new string('N', 161), StringComparison.Ordinal));
                        }

                    }
                    finally { Directory.Delete(tempDir, true); }
                }
            }).ConfigureAwait(false);

            await RunTest("An Architect brief names the gate inputs its vessel's workflow profile declares", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    StubGitService git = new StubGitService();
                    MissionService service = CreateMissionService(logging, testDb.Driver, settings, git);
                    string tempDir = Path.Combine(Path.GetTempPath(), "armada_prompt_test_" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(tempDir);

                    try
                    {
                        Vessel vessel = await testDb.Driver.Vessels.CreateAsync(new Vessel("GateInputVessel", "https://github.com/test/repo")).ConfigureAwait(false);
                        Mission architect = new Mission();
                        architect.Title = "Plan the export change";
                        architect.Persona = "Architect";
                        architect.Description = "Plan the export column.";
                        architect.VesselId = vessel.Id;

                        await service.GenerateClaudeMdAsync(tempDir, architect, vessel);
                        string undeclaredBrief = await File.ReadAllTextAsync(Path.Combine(tempDir, "CLAUDE.md"));
                        AssertFalse(undeclaredBrief.Contains("## Gate Input Files"), "a profile that declares no gate inputs adds no section");

                        await testDb.Driver.WorkflowProfiles.CreateAsync(new WorkflowProfile
                        {
                            Name = "gate-input-profile",
                            Scope = WorkflowProfileScopeEnum.Vessel,
                            VesselId = vessel.Id,
                            BuildCommand = "echo build",
                            GateInputPaths = new List<string> { "docs/data-dictionary.md", "schema/**/*.sql" }
                        }).ConfigureAwait(false);

                        string architectDir = Path.Combine(tempDir, "architect");
                        Directory.CreateDirectory(architectDir);
                        await service.GenerateClaudeMdAsync(architectDir, architect, vessel);
                        string architectBrief = await File.ReadAllTextAsync(Path.Combine(architectDir, "CLAUDE.md"));
                        AssertContains("## Gate Input Files", architectBrief, "the Architect brief carries the gate-input section");
                        AssertContains("`docs/data-dictionary.md`", architectBrief, "each declared path is listed");
                        AssertContains("`schema/**/*.sql`", architectBrief, "a declared glob is listed as written");

                        string workerDir = Path.Combine(tempDir, "worker");
                        Directory.CreateDirectory(workerDir);
                        Mission worker = new Mission();
                        worker.Title = "Add the export column";
                        worker.Persona = "Worker";
                        worker.Description = "Add the export column.";
                        worker.VesselId = vessel.Id;
                        await service.GenerateClaudeMdAsync(workerDir, worker, vessel);
                        string workerBrief = await File.ReadAllTextAsync(Path.Combine(workerDir, "CLAUDE.md"));
                        AssertFalse(workerBrief.Contains("## Gate Input Files"), "only the Architect, which writes the file lists, gets the section");
                    }
                    finally
                    {
                        try { Directory.Delete(tempDir, true); } catch { }
                    }
                }
            });

            await RunTest("Board notes say printing the marker posts the note, and prior art is skipped where it is off", async () =>
            {
                string notes = MissionService.BuildProgressNotesSection();
                AssertContains("[ARMADA:NOTE]", notes);
                AssertContains("no board tool", notes, "the brief says the marker line is the post, so a captain does not look for a board tool");

                foreach (bool excluded in new[] { true, false })
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                    {
                        LoggingModule logging = CreateLogging();
                        ArmadaSettings settings = CreateSettings();
                        settings.TypedDecisions.Mode = TypedDecisionModeEnum.Gate;
                        settings.TypedDecisions.Decisions["prior_art"].Mode = TypedDecisionModeEnum.Gate;
                        Vessel vessel = new Vessel("PriorArtVessel", "https://github.com/test/repo");
                        if (excluded) settings.TypedDecisions.EgressExcludedVesselIds = new List<string> { vessel.Id };
                        StubGitService git = new StubGitService();
                        MissionService service = CreateMissionService(logging, testDb.Driver, settings, git);
                        string tempDir = Path.Combine(Path.GetTempPath(), "armada_prompt_test_" + Guid.NewGuid().ToString("N"));
                        Directory.CreateDirectory(tempDir);

                        try
                        {
                            Mission mission = new Mission();
                            mission.Title = "Add the reader";
                            mission.Persona = "Worker";
                            mission.Description = "Add the reader.";
                            await service.GenerateClaudeMdAsync(tempDir, mission, vessel);
                            string brief = await File.ReadAllTextAsync(Path.Combine(tempDir, "CLAUDE.md"));
                            if (excluded)
                                AssertContains("`armada_check_prior_art` answers `unavailable` for this vessel", brief, "an egress-excluded vessel is told to skip prior art");
                            else
                                AssertFalse(brief.Contains("answers `unavailable` for this vessel"), "an enabled prior-art tool is not called off");
                            AssertContains("that is not a papercut", brief, "an unavailable typed-decision answer is not reported as friction");
                        }
                        finally
                        {
                            try { Directory.Delete(tempDir, true); } catch { }
                        }
                    }
                }
            });

            await RunTest("GenerateClaudeMdAsync omits Objective Scope when the voyage links no objective", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    StubGitService git = new StubGitService();
                    MissionService service = CreateMissionService(logging, testDb.Driver, settings, git);

                    string tempDir = Path.Combine(Path.GetTempPath(), "armada_prompt_test_" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(tempDir);

                    try
                    {
                        Voyage voyage = new Voyage("unlinked-voyage");
                        voyage = await testDb.Driver.Voyages.CreateAsync(voyage).ConfigureAwait(false);

                        Vessel vessel = new Vessel("UnlinkedVessel", "https://github.com/test/repo");
                        Mission mission = new Mission();
                        mission.Title = "Standalone work";
                        mission.Description = "No objective is linked to this voyage.";
                        mission.VoyageId = voyage.Id;

                        await service.GenerateClaudeMdAsync(tempDir, mission, vessel);

                        string content = await File.ReadAllTextAsync(Path.Combine(tempDir, "CLAUDE.md"));
                        AssertFalse(content.Contains("## Objective Scope (Definition of Done)"),
                            "A voyage without a linked objective must not carry the Objective Scope module heading");
                    }
                    finally
                    {
                        try { Directory.Delete(tempDir, true); } catch { }
                    }
                }
            });

            await RunTest("A configured AI-Memory root is named once for every runtime, content not inlined", async () =>
            {
                AgentRuntimeEnum[] runtimes = new AgentRuntimeEnum[]
                {
                    AgentRuntimeEnum.ClaudeCode,
                    AgentRuntimeEnum.Codex,
                    AgentRuntimeEnum.Cursor,
                    AgentRuntimeEnum.OpenCode
                };

                foreach (AgentRuntimeEnum runtime in runtimes)
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                    {
                        LoggingModule logging = CreateLogging();
                        ArmadaSettings settings = CreateSettings();
                        settings.AiMemoryRoot = "/memory-root/";
                        StubGitService git = new StubGitService();
                        MissionService service = CreateMissionService(logging, testDb.Driver, settings, git);

                        string tempDir = Path.Combine(Path.GetTempPath(), "armada_memory_test_" + Guid.NewGuid().ToString("N"));
                        Directory.CreateDirectory(tempDir);

                        try
                        {
                            Vessel vessel = new Vessel("MemoryVessel", "https://github.com/test/repo");
                            Captain captain = new Captain("MemoryCaptain");
                            captain.Runtime = runtime;

                            Mission mission = new Mission();
                            mission.Title = "Use shared memory";
                            mission.Description = "Memory must be discoverable.";

                            await service.GenerateClaudeMdAsync(tempDir, mission, vessel, captain);

                            string fileName = MissionPromptBuilder.GetInstructionsFileName(runtime.ToString());
                            string brief = await File.ReadAllTextAsync(Path.Combine(tempDir, fileName));

                            AssertContains("## Shared Memory", brief, runtime + " must be told shared memory exists");
                            AssertContains("/memory-root/shared/", brief, runtime + " must be pointed at the shared set");
                            AssertContains("holds no rules", brief, runtime + " must be told the index alone is not enough");
                            // The trailing separator must be normalized, not doubled.
                            AssertFalse(brief.Contains("//shared", StringComparison.Ordinal), "the root separator must be normalized");
                            // The wording must not contradict a repo instruction file the runtime
                            // auto-loads: an absolute "do not read the whole tree" against an
                            // auto-loaded directive that names specific memory files reads as a
                            // contradiction (probe papercut 2026-08-09).
                            AssertFalse(brief.Contains("Do not read the whole tree", StringComparison.Ordinal),
                                runtime + " memory section must not order the captain to avoid the tree the repo file names");
                            AssertContains("If the repository instruction file", brief,
                                runtime + " memory section must defer to the repo instruction file");
                        }
                        finally
                        {
                            try { Directory.Delete(tempDir, true); } catch { }
                        }
                    }
                }
            });

            await RunTest("No AI-Memory section is emitted when no memory root is configured", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    StubGitService git = new StubGitService();
                    MissionService service = CreateMissionService(logging, testDb.Driver, settings, git);

                    string tempDir = Path.Combine(Path.GetTempPath(), "armada_memory_test_" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(tempDir);

                    try
                    {
                        Vessel vessel = new Vessel("MemoryVessel", "https://github.com/test/repo");
                        Mission mission = new Mission();
                        mission.Title = "No memory configured";
                        mission.Description = "The module must be absent.";

                        await service.GenerateClaudeMdAsync(tempDir, mission, vessel);

                        string brief = await File.ReadAllTextAsync(Path.Combine(tempDir, "CLAUDE.md"));
                        AssertFalse(brief.Contains("## Shared Memory", StringComparison.Ordinal), "no memory section without a configured root");
                    }
                    finally
                    {
                        try { Directory.Delete(tempDir, true); } catch { }
                    }
                }
            });

            await RunTest("The code-index section names no Armada MCP tool", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    settings.CodeIndex.Enabled = true;
                    StubGitService git = new StubGitService();
                    MissionService service = CreateMissionService(logging, testDb.Driver, settings, git);

                    string tempDir = Path.Combine(Path.GetTempPath(), "armada_pack_test_" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(tempDir);

                    try
                    {
                        Vessel vessel = new Vessel("PackVessel", "https://github.com/test/repo");
                        Mission mission = new Mission();
                        mission.Title = "Find the handler";
                        mission.Description = "Locate the request handler.";
                        mission.VesselId = "vsl_pack_test";

                        await service.GenerateClaudeMdAsync(tempDir, mission, vessel);

                        string brief = await File.ReadAllTextAsync(Path.Combine(tempDir, "CLAUDE.md"));
                        AssertContains("## Code Index Context", brief, "the code-index section must still be present");
                        AssertFalse(brief.Contains("armada_code_search", StringComparison.Ordinal), "code-index guidance must not direct captains to an operator tool");
                        AssertFalse(brief.Contains("armada_context_pack", StringComparison.Ordinal), "code-index guidance must not direct captains to an operator tool");
                        AssertFalse(brief.Contains("MCP", StringComparison.Ordinal), "code-index guidance stays runtime-neutral");
                    }
                    finally
                    {
                        try { Directory.Delete(tempDir, true); } catch { }
                    }
                }
            });

            await RunTest("OpenCode missions write AGENTS.md, the file OpenCode loads natively", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    StubGitService git = new StubGitService();
                    MissionService service = CreateMissionService(logging, testDb.Driver, settings, git);

                    string tempDir = Path.Combine(Path.GetTempPath(), "armada_prompt_test_" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(tempDir);

                    try
                    {
                        Vessel vessel = new Vessel("OpenCodeVessel", "https://github.com/test/repo");
                        Captain captain = new Captain("OpenCodeCaptain");
                        captain.Runtime = AgentRuntimeEnum.OpenCode;

                        Mission mission = new Mission();
                        mission.Title = "Implement feature";
                        mission.Description = "OpenCode must get its own instruction filename.";

                        await service.GenerateClaudeMdAsync(tempDir, mission, vessel, captain);

                        AssertTrue(File.Exists(Path.Combine(tempDir, "AGENTS.md")), "OpenCode missions should write AGENTS.md");
                        AssertFalse(File.Exists(Path.Combine(tempDir, "CLAUDE.md")), "OpenCode missions should no longer fall through to CLAUDE.md");
                    }
                    finally
                    {
                        try { Directory.Delete(tempDir, true); } catch { }
                    }
                }
            });

            await RunTest("A generated-at-root brief is never duplicated under .armada/instructions", async () =>
            {
                // Probe papercut 2026-08-09: on a repo with no tracked AGENTS.md, generation wrote
                // the brief at the root (the file OpenCode auto-loads), then EnsureMissionInstructions
                // Present re-checked existence, saw the root file, and re-homed the brief under
                // .armada/instructions/ - shipping the same 6.4 KB text twice. The restore path must
                // decide by TRACKED status, not existence.
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    StubGitService git = new StubGitService();
                    MissionService service = CreateMissionService(logging, testDb.Driver, settings, git);

                    string tempDir = Path.Combine(Path.GetTempPath(), "armada_prompt_test_" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(tempDir);

                    try
                    {
                        Vessel vessel = new Vessel("OpenCodeVessel2", "https://github.com/test/repo");
                        Captain captain = new Captain("OpenCodeCaptain2");
                        captain.Runtime = AgentRuntimeEnum.OpenCode;

                        Mission mission = new Mission();
                        mission.Title = "Implement feature";
                        mission.Description = "The brief belongs at the root, once.";

                        await service.GenerateClaudeMdAsync(tempDir, mission, vessel, captain);
                        await service.EnsureMissionInstructionsPresentAsync(tempDir, mission, captain, CancellationToken.None);

                        AssertTrue(File.Exists(Path.Combine(tempDir, "AGENTS.md")), "the brief must exist at the root");
                        AssertFalse(
                            File.Exists(Path.Combine(tempDir, ".armada", "instructions", "AGENTS.md")),
                            "the same brief must not be duplicated under .armada/instructions/");
                    }
                    finally
                    {
                        try { Directory.Delete(tempDir, true); } catch { }
                    }
                }
            });

            await RunTest("A runtime that auto-loads the root file gets a pointer, not a second copy", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    StubGitService git = new StubGitService();
                    MissionService service = CreateMissionService(logging, testDb.Driver, settings, git);

                    string tempDir = Path.Combine(Path.GetTempPath(), "armada_prompt_test_" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(tempDir);

                    try
                    {
                        string rootPath = Path.Combine(tempDir, "CLAUDE.md");
                        await File.WriteAllTextAsync(rootPath, "# Durable project rules\nUnique-root-marker-9f3a\n");

                        Vessel vessel = new Vessel("PointerVessel", "https://github.com/test/repo");
                        Captain captain = new Captain("ClaudeCaptain");
                        captain.Runtime = AgentRuntimeEnum.ClaudeCode;

                        Mission mission = new Mission();
                        mission.Title = "Avoid the double load";
                        mission.Description = "Claude Code already loaded the root file.";

                        await service.GenerateClaudeMdAsync(tempDir, mission, vessel, captain);

                        string generated = await File.ReadAllTextAsync(Path.Combine(tempDir, ".armada", "instructions", "CLAUDE.md"));
                        AssertContains("## Existing Project Instructions", generated, "the pointer section must still be present");
                        AssertContains("already loaded `CLAUDE.md`", generated, "the pointer must name the auto-loaded file");
                        AssertFalse(
                            generated.Contains("Unique-root-marker-9f3a", StringComparison.Ordinal),
                            "root instruction text must not be inlined for a runtime that auto-loads it");

                        string rootAfter = await File.ReadAllTextAsync(rootPath);
                        AssertContains("Unique-root-marker-9f3a", rootAfter, "the root file itself must be untouched");
                    }
                    finally
                    {
                        try { Directory.Delete(tempDir, true); } catch { }
                    }
                }
            });

            await RunTest("A runtime that does not auto-load its file still gets the root text inlined", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    StubGitService git = new StubGitService();
                    MissionService service = CreateMissionService(logging, testDb.Driver, settings, git);

                    string tempDir = Path.Combine(Path.GetTempPath(), "armada_prompt_test_" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(tempDir);

                    try
                    {
                        await File.WriteAllTextAsync(Path.Combine(tempDir, "CURSOR.md"), "# Durable project rules\nUnique-root-marker-7c1b\n");

                        Vessel vessel = new Vessel("CursorVessel", "https://github.com/test/repo");
                        Captain captain = new Captain("CursorCaptain");
                        captain.Runtime = AgentRuntimeEnum.Cursor;

                        Mission mission = new Mission();
                        mission.Title = "Keep the inline";
                        mission.Description = "Nothing else surfaces CURSOR.md.";

                        await service.GenerateClaudeMdAsync(tempDir, mission, vessel, captain);

                        string generated = await File.ReadAllTextAsync(Path.Combine(tempDir, ".armada", "instructions", "CURSOR.md"));
                        AssertContains("Unique-root-marker-7c1b", generated, "Cursor still needs the root text inlined");
                    }
                    finally
                    {
                        try { Directory.Delete(tempDir, true); } catch { }
                    }
                }
            });

            await RunTest("A stale Armada model-context dump at the repo root is never inlined", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    StubGitService git = new StubGitService();
                    MissionService service = CreateMissionService(logging, testDb.Driver, settings, git);

                    string tempDir = Path.Combine(Path.GetTempPath(), "armada_prompt_test_" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(tempDir);

                    try
                    {
                        // Shape of a stale Armada-generated model-context dump left in a tracked file.
                        string dump =
                            "## Model Context\n" +
                            "The following context was accumulated by AI agents during previous missions on this repository.\n\n" +
                            "## Test Framework\nStale-dump-marker-4e2d\n";
                        await File.WriteAllTextAsync(Path.Combine(tempDir, "CURSOR.md"), dump);

                        Vessel vessel = new Vessel("DumpVessel", "https://github.com/test/repo");
                        Captain captain = new Captain("CursorCaptain");
                        captain.Runtime = AgentRuntimeEnum.Cursor;

                        Mission mission = new Mission();
                        mission.Title = "Reject the dump";
                        mission.Description = "A generated dump is not project instructions.";

                        await service.GenerateClaudeMdAsync(tempDir, mission, vessel, captain);

                        string generated = await File.ReadAllTextAsync(Path.Combine(tempDir, ".armada", "instructions", "CURSOR.md"));
                        AssertFalse(
                            generated.Contains("Stale-dump-marker-4e2d", StringComparison.Ordinal),
                            "a stale generated dump must not be re-fed to the captain");
                    }
                    finally
                    {
                        try { Directory.Delete(tempDir, true); } catch { }
                    }
                }
            });

            await RunTest("Instruction filename and auto-load mapping covers every runtime", async () =>
            {
                AssertEqual("CLAUDE.md", MissionPromptBuilder.GetInstructionsFileName("ClaudeCode"), "ClaudeCode filename");
                AssertEqual("CODEX.md", MissionPromptBuilder.GetInstructionsFileName("Codex"), "Codex filename");
                AssertEqual("CURSOR.md", MissionPromptBuilder.GetInstructionsFileName("Cursor"), "Cursor filename");
                AssertEqual("GEMINI.md", MissionPromptBuilder.GetInstructionsFileName("Gemini"), "Gemini filename");
                AssertEqual("MUX.md", MissionPromptBuilder.GetInstructionsFileName("Mux"), "Mux filename");
                AssertEqual("AGENTS.md", MissionPromptBuilder.GetInstructionsFileName("OpenCode"), "OpenCode filename");

                foreach (AgentRuntimeEnum runtime in Enum.GetValues<AgentRuntimeEnum>())
                {
                    string fileName = MissionPromptBuilder.GetInstructionsFileName(runtime.ToString());
                    AssertTrue(!String.IsNullOrEmpty(fileName), "every runtime must resolve an instruction filename");
                }

                AssertTrue(MissionPromptBuilder.RuntimeAutoLoadsInstructionsFile("ClaudeCode"), "Claude Code auto-loads CLAUDE.md");
                AssertTrue(MissionPromptBuilder.RuntimeAutoLoadsInstructionsFile("OpenCode"), "OpenCode auto-loads AGENTS.md");
                AssertTrue(MissionPromptBuilder.RuntimeAutoLoadsInstructionsFile("Gemini"), "Gemini auto-loads GEMINI.md");
                AssertFalse(MissionPromptBuilder.RuntimeAutoLoadsInstructionsFile("Cursor"), "CURSOR.md is an Armada convention");
                AssertFalse(MissionPromptBuilder.RuntimeAutoLoadsInstructionsFile("Codex"), "Codex reads AGENTS.md, not CODEX.md");
                AssertFalse(MissionPromptBuilder.RuntimeAutoLoadsInstructionsFile("Mux"), "MUX.md is an Armada convention");
                AssertFalse(MissionPromptBuilder.RuntimeAutoLoadsInstructionsFile(null), "an unknown runtime must not be assumed to auto-load");

                await Task.CompletedTask;
            });

            await RunTest("GenerateClaudeMdAsync records per-module prompt-budget telemetry", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    settings.CaptainInstructionByteBudget = 1;
                    StubGitService git = new StubGitService();
                    MissionService service = CreateMissionService(logging, testDb.Driver, settings, git);

                    string tempDir = Path.Combine(Path.GetTempPath(), "armada_prompt_test_" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(tempDir);

                    try
                    {
                        Vessel vessel = new Vessel("BudgetVessel", "https://github.com/test/repo");
                        vessel.ProjectContext = "Telemetry probe vessel context.";
                        vessel.EnableModelContext = true;
                        vessel.ModelContext = "Telemetry probe model context.";

                        Mission mission = new Mission();
                        mission.Title = "Record prompt budget";
                        mission.Description = "Verify the admiral measures what it sent.";

                        await service.GenerateClaudeMdAsync(tempDir, mission, vessel);

                        string content = await File.ReadAllTextAsync(Path.Combine(tempDir, "CLAUDE.md"));
                        int fileBytes = System.Text.Encoding.UTF8.GetByteCount(content);

                        List<ArmadaEvent> events = await testDb.Driver.Events.EnumerateByTypeAsync("mission.prompt_budget", 10);
                        AssertEqual(1, events.Count, "exactly one prompt-budget event must be recorded");

                        string payload = events[0].Payload ?? "";
                        AssertContains("\"InstructionFileBytes\":" + fileBytes, payload, "recorded file size must match the written file");
                        AssertContains("mission.rules", payload, "module names must be recorded");
                        AssertContains("mission.project_context_wrapper", payload, "vessel context module must be recorded");
                        AssertContains("mission.model_context_wrapper", payload, "model context module must be recorded");
                        AssertContains("\"OverBudget\":true", payload, "a file over the configured budget must be flagged");
                        AssertEqual(mission.Id, events[0].MissionId, "the event must be attributed to the mission");
                    }
                    finally
                    {
                        try { Directory.Delete(tempDir, true); } catch { }
                    }
                }
            });

            // Both project-profile resolvers are best-effort and return empty on failure, so brief
            // assembly that drops their result renders a brief with no error. Only the rendered file
            // and its budget accounting show whether the profile reached the captain.
            await RunTest("GenerateClaudeMdAsync renders profile skills and the persona override into the brief and its budget", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    settings.CaptainInstructionByteBudget = 5000000;
                    StubGitService git = new StubGitService();
                    IPromptTemplateService templateService;
                    MissionService service = CreateMissionServiceWithTemplates(logging, testDb.Driver, settings, git, out templateService);

                    string tempDir = Path.Combine(Path.GetTempPath(), "armada_prompt_test_" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(tempDir);

                    try
                    {
                        Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                            new Vessel("ProfileBriefVessel", "https://github.com/test/repo"));

                        Skill skill = new Skill();
                        skill.Name = "Bench Safety";
                        skill.Content = "Always isolate the bench before probing.";
                        await testDb.Driver.Skills.CreateAsync(skill);

                        await testDb.Driver.PromptTemplates.CreateAsync(new PromptTemplate(
                            "persona.worker_profile_custom",
                            "PROFILE WORKER TEMPLATE: follow the project's worker contract."));

                        PersonaOverride personaOverride = new PersonaOverride();
                        personaOverride.PersonaName = "Worker";
                        personaOverride.PromptTemplateName = "persona.worker_profile_custom";
                        personaOverride.AdditionalInstructions = "PROFILE ADDENDUM: cite the bench log line.";

                        ProjectProfile profile = new ProjectProfile();
                        profile.Name = "brief-profile";
                        profile.VesselId = vessel.Id;
                        profile.Skills = new List<string> { "Bench Safety" };
                        profile.PersonaOverrides = new List<PersonaOverride> { personaOverride };
                        await testDb.Driver.ProjectProfiles.CreateAsync(profile);

                        Mission mission = new Mission();
                        mission.Title = "Profile brief";
                        mission.Description = "Carry the vessel profile into the brief.";
                        mission.Persona = "Worker";

                        await service.GenerateClaudeMdAsync(tempDir, mission, vessel);

                        string content = await File.ReadAllTextAsync(Path.Combine(tempDir, "CLAUDE.md"));
                        AssertContains("## Skills", content, "the profile skills section must render");
                        AssertContains("### Bench Safety", content, "the profile skill heading must render");
                        AssertContains("Always isolate the bench before probing.", content, "the profile skill body must render");
                        AssertContains("PROFILE WORKER TEMPLATE: follow the project's worker contract.", content,
                            "the persona override's template must replace the default persona template");
                        AssertContains("PROFILE ADDENDUM: cite the bench log line.", content,
                            "the persona override's additional instructions must render");

                        string skillsMarkdown = await service.ResolveSkillsMarkdownAsync(vessel, CancellationToken.None);
                        int expectedSkillsBytes = System.Text.Encoding.UTF8.GetByteCount("## Skills\n\n" + skillsMarkdown + "\n");

                        List<ArmadaEvent> events = await testDb.Driver.Events.EnumerateByTypeAsync("mission.prompt_budget", 10);
                        AssertEqual(1, events.Count, "exactly one prompt-budget event must be recorded");
                        PromptBudgetModules? budget = System.Text.Json.JsonSerializer.Deserialize<PromptBudgetModules>(events[0].Payload ?? "{}");
                        AssertNotNull(budget, "the prompt-budget payload must deserialize");
                        AssertTrue(budget!.Modules.ContainsKey("mission.skills"), "the skills section must be tracked in the budget ledger");
                        AssertEqual(expectedSkillsBytes, budget.Modules["mission.skills"], "the ledger must count the rendered skills section bytes");
                        AssertTrue(budget.Modules.ContainsKey("mission.metadata"), "the metadata module carrying the persona prompt must be tracked");
                        AssertTrue(
                            budget.Modules["mission.metadata"] >= System.Text.Encoding.UTF8.GetByteCount("PROFILE ADDENDUM: cite the bench log line."),
                            "the persona override must be counted inside the metadata module");
                    }
                    finally
                    {
                        try { Directory.Delete(tempDir, true); } catch { }
                    }
                }
            });

            await RunTest("GenerateClaudeMdAsync leaves prompt-budget telemetry unflagged when under budget", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    settings.CaptainInstructionByteBudget = 5000000;
                    StubGitService git = new StubGitService();
                    MissionService service = CreateMissionService(logging, testDb.Driver, settings, git);

                    string tempDir = Path.Combine(Path.GetTempPath(), "armada_prompt_test_" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(tempDir);

                    try
                    {
                        Vessel vessel = new Vessel("BudgetVessel", "https://github.com/test/repo");
                        Mission mission = new Mission();
                        mission.Title = "Stay under budget";
                        mission.Description = "Small brief.";

                        await service.GenerateClaudeMdAsync(tempDir, mission, vessel);

                        List<ArmadaEvent> events = await testDb.Driver.Events.EnumerateByTypeAsync("mission.prompt_budget", 10);
                        AssertEqual(1, events.Count, "exactly one prompt-budget event must be recorded");
                        AssertContains("\"OverBudget\":false", events[0].Payload ?? "", "a file under budget must not be flagged");
                    }
                    finally
                    {
                        try { Directory.Delete(tempDir, true); } catch { }
                    }
                }
            });

            await RunTest("A worst-case stage and rescue chain renders every persona brief within the instruction budget", async () =>
            {
                // The brief a captain receives is bounded by the rendered file, not by any one cap on the
                // persisted description. This builds the largest realistic chain: a full objective brief,
                // a doubled prior-stage handoff with long agent output and a large diff, a rescue whose
                // failure reason is a whole gate log, a second handoff onto that rescue, operator captain
                // instructions and multi-byte text. Every persona must still receive a brief within the
                // byte budget, and must still see the head of its brief and the newest handoff block.
                foreach (string persona in new[] { "Judge", "Architect", "Worker", "TestEngineer", "Linter" })
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                    {
                        LoggingModule logging = CreateLogging();
                        ArmadaSettings settings = CreateSettings();
                        settings.CaptainInstructionByteBudget = 32768; // exercises the backstop, which is off by default
                        StubGitService git = new StubGitService();
                        IPromptTemplateService templateService;
                        MissionService service = CreateMissionServiceWithTemplates(logging, testDb.Driver, settings, git, out templateService);
                        await templateService.SeedDefaultsAsync();

                        string tempDir = Path.Combine(Path.GetTempPath(), "armada_prompt_test_" + Guid.NewGuid().ToString("N"));
                        Directory.CreateDirectory(tempDir);

                        try
                        {
                            Vessel vessel = new Vessel("BudgetChainVessel", "https://github.com/test/repo");
                            vessel.ProjectContext = "Synthetic context for the budget chain. " + new string('c', 2000);

                            Objective objective = new Objective();
                            objective.Title = "Synthetic worst-case objective";
                            objective.Description = "Objective head sentence. " + string.Concat(Enumerable.Repeat("Scope line with multi-byte text éè中文.\n", 400));
                            for (int i = 0; i < 40; i++) objective.AcceptanceCriteria.Add("Criterion " + i + " " + new string('a', 300));
                            string objectiveBrief = ObjectiveBriefRenderer.Render(objective);

                            string handoffBlock =
                                "\n\n---\n" + MissionService.BuildHandoffMarker("msn_budget_worker") + "\n" +
                                "## Prior Stage Output\nThe previous pipeline stage (Worker) completed mission.\nBranch: armada/budget\n" +
                                "\n### Agent Output (from Worker stage)\n```\n" + new string('o', 8000) + "\n```\n" +
                                "\n### Diff from prior stage\n```diff\n" +
                                string.Concat(Enumerable.Repeat("diff --git a/f.cs b/f.cs\n+" + new string('d', 1500) + "\n", 40)) +
                                "NEWEST-STAGE-DIFF-TAIL\n```\n";

                            // A handoff that ran twice with no idempotency guard: the persisted description
                            // is far over any persisted cap.
                            string stageDescription = objectiveBrief + handoffBlock + handoffBlock;

                            Mission failed = new Mission();
                            failed.Title = "Failed stage";
                            failed.Persona = "Judge";
                            failed.Description = stageDescription;
                            failed.FailureReason = "gate failed: " + string.Concat(Enumerable.Repeat("warning CS0618: obsolete member used in generated code\n", 1200));
                            failed.ReviewComment = "## Completeness\n" + new string('r', 12000) + "\n## Verdict\nNEEDS_REVISION";
                            Incident incident = new Incident();
                            string rescueDescription = Armada.Server.AutonomousRecoveryOrchestrator.BuildRescueDescription(failed, incident, 1);

                            string rescueJudgeHandoff =
                                "\n\n---\n" + MissionService.BuildHandoffMarker("msn_budget_rescue") + "\n" +
                                "## Prior Stage Output\nBranch: armada/budget\n" +
                                "\n### Diff from prior stage\n```diff\n" + new string('z', 60000) + "\nNEWEST-RESCUE-DIFF-TAIL\n```\n";

                            Mission mission = new Mission();
                            mission.Title = "Worst-case chain " + persona;
                            mission.Persona = persona;
                            mission.Description = rescueDescription + rescueJudgeHandoff;

                            Captain captain = new Captain("budget-captain");
                            captain.Runtime = Armada.Core.Enums.AgentRuntimeEnum.ClaudeCode;
                            captain.SystemInstructions = "Operator captain instructions. " + new string('i', 3500);

                            await service.GenerateClaudeMdAsync(tempDir, mission, vessel, captain);

                            string content = await File.ReadAllTextAsync(Path.Combine(tempDir, "CLAUDE.md"));
                            int fileBytes = System.Text.Encoding.UTF8.GetByteCount(content);

                            AssertTrue(fileBytes <= settings.CaptainInstructionByteBudget,
                                persona + " brief is " + fileBytes + " bytes, over the " + settings.CaptainInstructionByteBudget + " byte budget");
                            AssertContains("Autonomous rescue attempt 1", content, persona + " brief must keep the head of its description");
                            AssertContains("NEWEST-RESCUE-DIFF-TAIL", content, persona + " brief must keep the newest handoff block");
                            AssertContains("Operator captain instructions", content, persona + " brief must keep the captain instructions");

                            List<ArmadaEvent> events = await testDb.Driver.Events.EnumerateByTypeAsync("mission.prompt_budget", 10);
                            AssertEqual(1, events.Count, "exactly one prompt-budget event must be recorded");
                            AssertContains("\"OverBudget\":false", events[0].Payload ?? "", persona + " telemetry must report the brief within budget");
                        }
                        finally
                        {
                            try { Directory.Delete(tempDir, true); } catch { }
                        }
                    }
                }
            });

            await RunTest("A rescue description stays bounded when the failure reason is a whole gate log", async () =>
            {
                Mission failed = new Mission();
                failed.Title = "Failed stage";
                failed.Description = "Short scope.";
                failed.FailureReason = "gate failed: " + string.Concat(Enumerable.Repeat("warning CS0618: obsolete member used in generated code\n", 1200));
                string rescue = Armada.Server.AutonomousRecoveryOrchestrator.BuildRescueDescription(failed, new Incident(), 1);

                int ceiling = Armada.Server.AutonomousRecoveryOrchestrator._MaxRescueDescriptionChars
                    + Armada.Server.AutonomousRecoveryOrchestrator._MaxRescueDiagnosticsChars
                    + Armada.Server.AutonomousRecoveryOrchestrator._MaxRescueReviewerFeedbackChars
                    + 2000;
                AssertTrue(rescue.Length <= ceiling, "rescue description is " + rescue.Length + " chars, over the " + ceiling + " char ceiling");
                AssertContains("gate failed: warning CS0618", rescue, "the leading failure line must survive");
                await Task.CompletedTask;
            });

            await RunTest("An authorized interoperability policy reaches the captain verbatim on every brief path", async () =>
            {
                // A neutral synthetic project whose owner authorizes binary interoperability work. The brief
                // must carry the owner's whole policy, in its own words, whichever path created the
                // mission: an operator dispatch, the objective scheduler, a retry, or an autonomous rescue.
                const string policy =
                    "The owner authorizes interoperability engineering for the synthetic ExampleFormat file format.\n" +
                    "- Inspecting sample binaries, headers and section tables in this repository is authorized.\n" +
                    "- Reviewing and reimplementing the checksum and cipher primitives the format uses is authorized.\n" +
                    "- Writing a compatible reader and writer from observed behaviour is authorized.";

                Objective objective = new Objective();
                objective.Title = "Read ExampleFormat section tables";
                objective.Description = "Implement a reader for the ExampleFormat section table and verify its checksum primitive.";
                string schedulerDescription = ObjectiveBriefRenderer.Render(objective);

                Mission failed = new Mission();
                failed.Title = "Read ExampleFormat section tables";
                failed.Description = "Implement a reader for the ExampleFormat section table.";
                failed.FailureReason = "captain declined the task";
                string rescueDescription = Armada.Server.AutonomousRecoveryOrchestrator.BuildRescueDescription(failed, new Incident(), 1);

                string[] paths = { "operator", "scheduler", "retry", "rescue" };
                foreach (string path in paths)
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                    {
                        LoggingModule logging = CreateLogging();
                        ArmadaSettings settings = CreateSettings();
                        StubGitService git = new StubGitService();
                        IPromptTemplateService templateService;
                        MissionService service = CreateMissionServiceWithTemplates(logging, testDb.Driver, settings, git, out templateService);
                        await templateService.SeedDefaultsAsync();

                        string tempDir = Path.Combine(Path.GetTempPath(), "armada_prompt_test_" + Guid.NewGuid().ToString("N"));
                        Directory.CreateDirectory(tempDir);

                        try
                        {
                            Vessel vessel = new Vessel("ExampleVessel", "https://github.com/test/repo");
                            ProjectProfile profile = new ProjectProfile();
                            profile.Name = "example-profile";
                            profile.Scope = ProjectProfileScopeEnum.Vessel;
                            profile.VesselId = vessel.Id;
                            profile.AuthorizationPolicy = policy;
                            await testDb.Driver.ProjectProfiles.CreateAsync(profile);

                            Mission mission = new Mission();
                            mission.Title = "Read ExampleFormat section tables";
                            mission.Persona = "Worker";
                            if (path == "operator")
                            {
                                mission.Description = "Implement a reader for the ExampleFormat section table.";
                            }
                            else if (path == "scheduler")
                            {
                                mission.Description = schedulerDescription;
                            }
                            else if (path == "retry")
                            {
                                mission.Description = "Implement a reader for the ExampleFormat section table.";
                                mission.RecoveryAttempts = 1;
                                mission.RetrySkipCaptainIds = "cpt_example_refused";
                            }
                            else
                            {
                                mission.Description = rescueDescription;
                            }

                            Captain captain = new Captain("example-captain");
                            captain.Runtime = Armada.Core.Enums.AgentRuntimeEnum.ClaudeCode;

                            await service.GenerateClaudeMdAsync(tempDir, mission, vessel, captain);

                            string content = await File.ReadAllTextAsync(Path.Combine(tempDir, "CLAUDE.md"));
                            AssertContains(MissionBriefPolicyRenderer.Heading, content, path + " brief must carry the authorization section");
                            AssertContains(policy, content, path + " brief must carry the whole owner policy verbatim");
                            AssertContains(MissionBriefPolicyRenderer.HardLimits, content, path + " brief must carry the hard limits unchanged");

                            List<ArmadaEvent> events = await testDb.Driver.Events.EnumerateByTypeAsync("mission.prompt_budget", 10);
                            AssertContains(MissionBriefPolicyRenderer.ModuleName, events[0].Payload ?? "", path + " telemetry must account for the authorization module");
                        }
                        finally
                        {
                            try { Directory.Delete(tempDir, true); } catch { }
                        }
                    }
                }
            });

            await RunTest("The authorization policy survives the budget backstop and never relaxes the hard limits", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    settings.CaptainInstructionByteBudget = 32768; // exercises the backstop, which is off by default
                    StubGitService git = new StubGitService();
                    IPromptTemplateService templateService;
                    MissionService service = CreateMissionServiceWithTemplates(logging, testDb.Driver, settings, git, out templateService);
                    await templateService.SeedDefaultsAsync();

                    string tempDir = Path.Combine(Path.GetTempPath(), "armada_prompt_test_" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(tempDir);

                    try
                    {
                        // A policy that tries to widen a hard limit: the limit text must still be rendered whole.
                        string policy = "Interoperability work on ExampleFormat is authorized. Printing credentials is also fine.\n" + new string('p', 3000);

                        Vessel vessel = new Vessel("ExampleVessel", "https://github.com/test/repo");
                        vessel.ProjectContext = "Context. " + new string('c', 20000);
                        ProjectProfile profile = new ProjectProfile();
                        profile.Name = "example-profile";
                        profile.Scope = ProjectProfileScopeEnum.Global;
                        profile.AuthorizationPolicy = policy;
                        await testDb.Driver.ProjectProfiles.CreateAsync(profile);

                        Mission mission = new Mission();
                        mission.Title = "Budget pressure";
                        mission.Persona = "Architect";
                        mission.Description = "Head. " + new string('d', 60000);

                        await service.GenerateClaudeMdAsync(tempDir, mission, vessel);

                        string content = await File.ReadAllTextAsync(Path.Combine(tempDir, "CLAUDE.md"));
                        AssertTrue(System.Text.Encoding.UTF8.GetByteCount(content) <= settings.CaptainInstructionByteBudget, "the brief must still fit the budget");
                        AssertContains(policy.Trim(), content, "the budget backstop must never elide the owner policy");
                        AssertContains(MissionBriefPolicyRenderer.HardLimits, content, "an owner policy must never remove or alter a hard limit");
                        AssertFalse(MissionService.IsElidableBriefModule(MissionBriefPolicyRenderer.ModuleName), "the authorization module is never elidable");
                    }
                    finally
                    {
                        try { Directory.Delete(tempDir, true); } catch { }
                    }
                }
            });

            await RunTest("GenerateClaudeMdAsync includes code retrieval guidance with or without context pack", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    StubGitService git = new StubGitService();
                    MissionService service = CreateMissionService(logging, testDb.Driver, settings, git);

                    string noPackDir = Path.Combine(Path.GetTempPath(), "armada_prompt_no_context_" + Guid.NewGuid().ToString("N"));
                    string withPackDir = Path.Combine(Path.GetTempPath(), "armada_prompt_with_context_" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(noPackDir);
                    Directory.CreateDirectory(withPackDir);

                    try
                    {
                        Vessel vessel = new Vessel("ContextPackVessel", "https://github.com/test/repo");
                        Mission mission = new Mission();
                        mission.Title = "Use context pack";
                        mission.Description = "Generated instructions should mention code context only when present.";

                        await service.GenerateClaudeMdAsync(noPackDir, mission, vessel);
                        string withoutContext = await File.ReadAllTextAsync(Path.Combine(noPackDir, "CLAUDE.md"));
                        AssertContains("## Code Index Context", withoutContext);
                        AssertContains("No `_briefing/context-pack.md` is staged", withoutContext);
                        AssertContains("Use ordinary file search", withoutContext);

                        string briefingDir = Path.Combine(withPackDir, "_briefing");
                        Directory.CreateDirectory(briefingDir);
                        await File.WriteAllTextAsync(Path.Combine(briefingDir, "context-pack.md"), "# Context Pack");

                        await service.GenerateClaudeMdAsync(withPackDir, mission, vessel);
                        string withContext = await File.ReadAllTextAsync(Path.Combine(withPackDir, "CLAUDE.md"));
                        AssertContains("## Code Index Context", withContext);
                        AssertContains("Read it before broad code search.", withContext);
                        AssertContains("discovery evidence, not authority", withContext);
                        AssertContains("verified against the current branch before editing", withContext);
                        AssertContains("Final report must include one `Pack:` line", withContext);
                    }
                    finally
                    {
                        try { Directory.Delete(noPackDir, true); } catch { }
                        try { Directory.Delete(withPackDir, true); } catch { }
                    }
                }
            });

            await RunTest("GenerateClaudeMdAsync omits code retrieval guidance when code indexing is disabled", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    settings.CodeIndex.Enabled = false;
                    StubGitService git = new StubGitService();
                    MissionService service = CreateMissionService(logging, testDb.Driver, settings, git);

                    string tempDir = Path.Combine(Path.GetTempPath(), "armada_prompt_disabled_context_" + Guid.NewGuid().ToString("N"));
                    string briefingDir = Path.Combine(tempDir, "_briefing");
                    Directory.CreateDirectory(briefingDir);

                    try
                    {
                        await File.WriteAllTextAsync(Path.Combine(briefingDir, "context-pack.md"), "# Stale Context Pack");

                        Vessel vessel = new Vessel("DisabledContextPackVessel", "https://github.com/test/repo");
                        Mission mission = new Mission("Use direct search", "Do not use code-index context.");

                        await service.GenerateClaudeMdAsync(tempDir, mission, vessel);

                        string content = await File.ReadAllTextAsync(Path.Combine(tempDir, "CLAUDE.md"));
                        AssertFalse(content.Contains("## Code Index Context"), "disabled code indexing must suppress the complete section");
                        AssertFalse(content.Contains("armada_code_search"), "disabled code indexing must suppress MCP search guidance");
                        AssertFalse(content.Contains("armada_context_pack"), "disabled code indexing must suppress context-pack guidance");
                        AssertFalse(content.Contains("Final report must include one `Pack:` line"), "disabled code indexing must suppress pack reporting");
                    }
                    finally
                    {
                        try { Directory.Delete(tempDir, true); } catch { }
                    }
                }
            });

            await RunTest("GenerateClaudeMdAsync preserves existing root CLAUDE.md and writes ignored generated copy", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    StubGitService git = new StubGitService();
                    MissionService service = CreateMissionService(logging, testDb.Driver, settings, git);

                    string tempDir = Path.Combine(Path.GetTempPath(), "armada_prompt_test_" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(tempDir);

                    try
                    {
                        string rootClaudePath = Path.Combine(tempDir, "CLAUDE.md");
                        await File.WriteAllTextAsync(rootClaudePath, "# Stable project instructions\n");

                        Vessel vessel = new Vessel("PromptVessel", "https://github.com/test/repo");
                        vessel.ProjectContext = "Generated mission context.";

                        Mission mission = new Mission();
                        mission.Title = "Fix login bug";
                        mission.Description = "The login form does not validate email addresses.";

                        await service.GenerateClaudeMdAsync(tempDir, mission, vessel);

                        string rootContent = await File.ReadAllTextAsync(rootClaudePath);
                        AssertEqual("# Stable project instructions\n", rootContent, "Root CLAUDE.md should not be overwritten");

                        string generatedPath = Path.Combine(tempDir, ".armada", "instructions", "CLAUDE.md");
                        AssertTrue(File.Exists(generatedPath), "Generated mission instructions should be written under .armada/instructions");
                        string generatedContent = await File.ReadAllTextAsync(generatedPath);
                        AssertContains("Generated mission context.", generatedContent);
                        AssertContains("# Stable project instructions", generatedContent);
                    }
                    finally
                    {
                        try { Directory.Delete(tempDir, true); } catch { }
                    }
                }
            });

            await RunTest("GenerateClaudeMdAsync omits the playbook wrapper when every playbook is filtered out", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    StubGitService git = new StubGitService();
                    MissionService service = CreateMissionService(logging, testDb.Driver, settings, git);

                    string tempDir = Path.Combine(Path.GetTempPath(), "armada_prompt_test_" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(tempDir);

                    try
                    {
                        Vessel vessel = new Vessel("PlaceholderPlaybookVessel", "https://github.com/test/repo");

                        // The only attached playbook holds a heading and a placeholder line, which the
                        // renderer drops. The wrapper calls its content required reading, so an empty
                        // wrapper points the captain at material it never got.
                        Mission mission = new Mission("Task", "Do something.");
                        mission.PlaybookSnapshots = new List<MissionPlaybookSnapshot>
                        {
                            new MissionPlaybookSnapshot
                            {
                                FileName = "vessel-notes.md",
                                Content = "# Placeholder Heading\n\nNo accepted notes yet."
                            }
                        };

                        await service.GenerateClaudeMdAsync(tempDir, mission, vessel);

                        string content = await File.ReadAllTextAsync(Path.Combine(tempDir, "CLAUDE.md"));
                        AssertFalse(content.Contains("Placeholder Heading"), "the placeholder playbook body must be suppressed");
                        AssertFalse(content.Contains("## Playbooks"), "an empty playbook wrapper must not be emitted");
                    }
                    finally
                    {
                        try { Directory.Delete(tempDir, true); } catch { }
                    }
                }
            });

            await RunTest("Template-resolved CLAUDE.md de-duplicates shared context sections", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    StubGitService git = new StubGitService();
                    IPromptTemplateService templateService;
                    MissionService service = CreateMissionServiceWithTemplates(logging, testDb.Driver, settings, git, out templateService);
                    await templateService.SeedDefaultsAsync();

                    string tempDir = Path.Combine(Path.GetTempPath(), "armada_prompt_test_" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(tempDir);

                    try
                    {
                        Vessel vessel = new Vessel("DedupVessel", "https://github.com/test/repo");
                        vessel.ProjectContext = "Service-oriented C# backend.";
                        vessel.StyleGuide = "Prefer explicit types.";
                        vessel.EnableModelContext = true;
                        vessel.ModelContext = "Background jobs are scheduled from ArmadaServer.";

                        Captain captain = new Captain("architect-prompt-captain");
                        captain.Runtime = AgentRuntimeEnum.Codex;
                        captain.SystemInstructions = "Be concise and careful.";

                        Mission mission = new Mission("Plan work", "Break this objective into missions.");
                        mission.Persona = "Architect";

                        await service.GenerateClaudeMdAsync(tempDir, mission, vessel, captain);

                        string content = await File.ReadAllTextAsync(Path.Combine(tempDir, "CODEX.md"));
                        AssertEqual(1, Regex.Matches(content, "^## Project Context$", RegexOptions.Multiline).Count);
                        AssertEqual(1, Regex.Matches(content, "^## Code Style$", RegexOptions.Multiline).Count);
                        AssertEqual(1, Regex.Matches(content, "^## Model Context$", RegexOptions.Multiline).Count);
                        AssertEqual(1, Regex.Matches(content, "^## Repository$", RegexOptions.Multiline).Count);
                    }
                    finally
                    {
                        try { Directory.Delete(tempDir, true); } catch { }
                    }
                }
            });

            await RunTest("Template-resolved persona prompts require structured test and judge analysis", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    StubGitService git = new StubGitService();
                    IPromptTemplateService templateService;
                    MissionService service = CreateMissionServiceWithTemplates(logging, testDb.Driver, settings, git, out templateService);
                    await templateService.SeedDefaultsAsync();

                    string tempDir = Path.Combine(Path.GetTempPath(), "armada_prompt_test_" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(tempDir);

                    try
                    {
                        Vessel vessel = new Vessel("PersonaPromptVessel", "https://github.com/test/repo");

                        Mission judgeMission = new Mission();
                        judgeMission.Title = "Judge structure test";
                        judgeMission.Description = "Verify judge review requirements.";
                        judgeMission.Persona = "Judge";

                        await service.GenerateClaudeMdAsync(tempDir, judgeMission, vessel);

                        string judgeContent = await File.ReadAllTextAsync(Path.Combine(tempDir, "CLAUDE.md"));
                        AssertContains("## Completeness", judgeContent, "Judge prompt should require a Completeness section");
                        AssertContains("## Failure Modes", judgeContent, "Judge prompt should require a Failure Modes section");
                        AssertContains("PASS is not allowed", judgeContent, "Judge prompt should constrain PASS approvals");

                        Mission testMission = new Mission();
                        testMission.Title = "Test coverage structure test";
                        testMission.Description = "Verify test engineer requirements.";
                        testMission.Persona = "Test Engineer";

                        await service.GenerateClaudeMdAsync(tempDir, testMission, vessel);

                        string generatedTestInstructions = Path.Combine(tempDir, ".armada", "instructions", "CLAUDE.md");
                        string testContent = await File.ReadAllTextAsync(
                            File.Exists(generatedTestInstructions)
                                ? generatedTestInstructions
                                : Path.Combine(tempDir, "CLAUDE.md"));
                        AssertContains("negative or edge-path", testContent, "Test engineer prompt should require negative-path coverage");
                        AssertContains("## Coverage Added", testContent, "Test engineer prompt should request a coverage summary section");
                        AssertContains("## Residual Risks", testContent, "Test engineer prompt should request residual risk reporting");
                    }
                    finally
                    {
                        try { Directory.Delete(tempDir, true); } catch { }
                    }
                }
            });

            await RunTest("A Judge brief names a passing UnitTest Check at the reviewed commit, and asks for the full suite when only another commit passed", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    MissionService service = CreateMissionService(logging, testDb.Driver, settings, new StubGitService());
                    Vessel vessel = new Vessel("CheckEvidenceVessel", "https://github.com/test/repo");
                    const string reviewedCommit = "1111111111111111111111111111111111111111";
                    const string newerCommit = "3333333333333333333333333333333333333333";

                    Voyage voyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("check-evidence-voyage")).ConfigureAwait(false);
                    Mission worker = new Mission("[Worker] Implement", "worker description");
                    worker.VoyageId = voyage.Id;
                    worker.Persona = "Worker";
                    worker.Status = MissionStatusEnum.WorkProduced;
                    worker.CommitHash = reviewedCommit;
                    worker = await testDb.Driver.Missions.CreateAsync(worker).ConfigureAwait(false);

                    Mission judge = new Mission("[Judge] Review", "Review the change.");
                    judge.VoyageId = voyage.Id;
                    judge.Persona = "Judge";
                    judge.DependsOnMissionId = worker.Id;
                    judge = await testDb.Driver.Missions.CreateAsync(judge).ConfigureAwait(false);

                    CheckRun passing = await testDb.Driver.CheckRuns.CreateAsync(new CheckRun
                    {
                        VoyageId = voyage.Id,
                        Label = "UnitTest",
                        Type = CheckRunTypeEnum.UnitTest,
                        Status = CheckRunStatusEnum.Passed,
                        Command = "dotnet test",
                        WorkingDirectory = "C:/temp",
                        BranchName = "armada/worker/check-evidence",
                        CommitHash = reviewedCommit,
                        StartedUtc = DateTime.UtcNow,
                        CompletedUtc = DateTime.UtcNow,
                        ExitCode = 0
                    }).ConfigureAwait(false);

                    CheckRun adoptedBuild = await testDb.Driver.CheckRuns.CreateAsync(new CheckRun
                    {
                        VoyageId = voyage.Id, Label = "Build", Type = CheckRunTypeEnum.Build,
                        Status = CheckRunStatusEnum.Passed, Command = "dotnet build", CommitHash = reviewedCommit,
                        SatisfiedBy = new CheckRunSatisfiedBy
                        {
                            MissionId = worker.Id, Label = "build", CommitHash = reviewedCommit
                        }
                    }).ConfigureAwait(false);

                    CheckRun customSlop = await testDb.Driver.CheckRuns.CreateAsync(new CheckRun
                    {
                        VoyageId = voyage.Id, Label = "Local style", Type = CheckRunTypeEnum.Slop,
                        Status = CheckRunStatusEnum.Passed, Command = "local-style-audit", CommitHash = reviewedCommit
                    }).ConfigureAwait(false);

                    CheckRun nativeSlop = await testDb.Driver.CheckRuns.CreateAsync(new CheckRun
                    {
                        VoyageId = voyage.Id, Label = "Slop", Type = CheckRunTypeEnum.Slop,
                        Status = CheckRunStatusEnum.Pending, Command = "armada slop-classifier (native, reviewed diff)",
                        CommitHash = reviewedCommit
                    }).ConfigureAwait(false);

                    string firstDir = Path.Combine(Path.GetTempPath(), "armada_prompt_test_" + Guid.NewGuid().ToString("N"));
                    string secondDir = Path.Combine(Path.GetTempPath(), "armada_prompt_test_" + Guid.NewGuid().ToString("N"));
                    string workerDir = Path.Combine(Path.GetTempPath(), "armada_prompt_test_" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(firstDir);
                    Directory.CreateDirectory(secondDir);
                    Directory.CreateDirectory(workerDir);
                    try
                    {
                        await service.GenerateClaudeMdAsync(firstDir, judge, vessel);
                        string evidenceBrief = await File.ReadAllTextAsync(Path.Combine(firstDir, "CLAUDE.md"));
                        AssertContains(JudgeCheckEvidence.Heading, evidenceBrief, "the Judge brief carries the Check evidence section");
                        AssertContains("Independent UnitTest Check `" + passing.Id + "`", evidenceBrief, "the passing Check is named by id");
                        AssertContains("passed at commit `" + reviewedCommit + "`", evidenceBrief, "the section names the commit the Check measured");
                        AssertContains("run only focused tests", evidenceBrief, "a measured commit asks only for focused tests");
                        AssertContains(nativeSlop.Id, evidenceBrief, "the Judge receives the exact native Slop Check identity");
                        AssertContains("Slop", evidenceBrief, "the native classifier type is named");
                        AssertContains("Pending", evidenceBrief, "a pending native gate is not reported green");
                        AssertContains("armada_enumerate entityType=checks", evidenceBrief, "the launch snapshot names the mission-accessible current receipt lookup");
                        AssertFalse(evidenceBrief.Contains("get_check_run", StringComparison.Ordinal), "the Judge is not directed to an operator-only tool");
                        AssertContains("not a substitute", evidenceBrief, "a separate local tool cannot replace a native gate");
                        AssertContains("Snapshot only", evidenceBrief, "a launch-time snapshot does not claim current results");
                        AssertContains(adoptedBuild.Id, evidenceBrief, "an adopted Build receipt is named");
                        AssertContains("this Check ran no command", evidenceBrief, "adoption is not described as another execution");
                        string customLine = evidenceBrief.Split('\n').Single(line => line.Contains(customSlop.Id, StringComparison.Ordinal));
                        AssertFalse(customLine.Contains("native reviewed-diff classifier", StringComparison.Ordinal), "a custom Slop command is not mislabeled as the native classifier");




                        // The Worker committed again: the green now measures an older commit than the one under review.
                        worker.CommitHash = newerCommit;
                        await testDb.Driver.Missions.UpdateAsync(worker).ConfigureAwait(false);
                        await service.GenerateClaudeMdAsync(secondDir, judge, vessel);
                        string staleBrief = await File.ReadAllTextAsync(Path.Combine(secondDir, "CLAUDE.md"));
                        AssertContains("No passing independent UnitTest Check is recorded for commit `" + newerCommit + "`", staleBrief,
                            "a green for another commit is not evidence for the commit under review");
                        AssertContains("Run the full test suite in the FOREGROUND", staleBrief, "without evidence the Judge runs the full suite");
                        AssertFalse(staleBrief.Contains(passing.Id, StringComparison.Ordinal), "the stale Check is not cited");

                        await service.GenerateClaudeMdAsync(workerDir, worker, vessel);
                        string workerBrief = await File.ReadAllTextAsync(Path.Combine(workerDir, "CLAUDE.md"));
                        AssertFalse(workerBrief.Contains(JudgeCheckEvidence.Heading, StringComparison.Ordinal), "only a Judge brief carries the section");
                    }
                    finally
                    {
                        foreach (string dir in new[] { firstDir, secondDir, workerDir })
                        {
                            try { Directory.Delete(dir, true); } catch { }
                        }
                    }
                }
            });

            await RunTest("Judge Check evidence reads only the latest record of each Check, and a later failure withdraws the evidence", () =>
            {
                const string commit = "1111111111111111111111111111111111111111";
                DateTime earlier = DateTime.UtcNow.AddMinutes(-10);
                CheckRun passed = new CheckRun
                {
                    Id = "chk_passed",
                    VoyageId = "vyg_evidence",
                    Label = "UnitTest",
                    Type = CheckRunTypeEnum.UnitTest,
                    Status = CheckRunStatusEnum.Passed,
                    Command = "dotnet test",
                    CommitHash = commit,
                    CreatedUtc = earlier,
                    LastUpdateUtc = earlier
                };
                CheckRun build = new CheckRun
                {
                    Id = "chk_build",
                    VoyageId = "vyg_evidence",
                    Type = CheckRunTypeEnum.Build,
                    Status = CheckRunStatusEnum.Passed,
                    Command = "dotnet build",
                    CommitHash = commit
                };
                AssertEqual("chk_passed", JudgeCheckEvidence.FindPassingUnitTest(new List<CheckRun> { passed, build }, commit.Substring(0, 9))?.Id,
                    "a passing UnitTest Check at the commit is evidence, and an abbreviated commit names the same commit");

                CheckRun rerunFailed = new CheckRun
                {
                    Id = "chk_rerun_failed",
                    VoyageId = "vyg_evidence",
                    Label = "UnitTest",
                    Type = CheckRunTypeEnum.UnitTest,
                    Status = CheckRunStatusEnum.Failed,
                    Command = "dotnet test",
                    CommitHash = commit,
                    CreatedUtc = DateTime.UtcNow,
                    LastUpdateUtc = DateTime.UtcNow
                };
                AssertNull(JudgeCheckEvidence.FindPassingUnitTest(new List<CheckRun> { passed, rerunFailed }, commit),
                    "a later failed record of the same Check is its current verdict, so there is no passing evidence");
                AssertNull(JudgeCheckEvidence.FindPassingUnitTest(new List<CheckRun> { build }, commit),
                    "a passing Build Check is not suite evidence");
                return Task.CompletedTask;
            });

            await RunTest("GenerateClaudeMdAsync strips stale Armada mission blocks from existing instructions", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    StubGitService git = new StubGitService();
                    MissionService service = CreateMissionService(logging, testDb.Driver, settings, git);

                    string tempDir = Path.Combine(Path.GetTempPath(), "armada_prompt_test_" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(tempDir);

                    try
                    {
                        string existingInstructions =
                            "## Project Context\n" +
                            "Stable project guidance.\n" +
                            "\n" +
                            "## Code Style\n" +
                            "Use explicit types.\n" +
                            "\n" +
                            "# Mission Instructions\n" +
                            "\n" +
                            "## Mission\n" +
                            "- **Title:** Stale mission title\n" +
                            "\n" +
                            "[ARMADA:MISSION] Old task\n";
                        await File.WriteAllTextAsync(Path.Combine(tempDir, "CLAUDE.md"), existingInstructions);

                        Vessel vessel = new Vessel("ExistingInstructionsVessel", "https://github.com/test/repo");
                        Mission mission = new Mission("Fresh mission", "Fresh description.");

                        await service.GenerateClaudeMdAsync(tempDir, mission, vessel);

                        string content = await File.ReadAllTextAsync(Path.Combine(tempDir, ".armada", "instructions", "CLAUDE.md"));
                        AssertContains("## Existing Project Instructions", content);
                        AssertContains("Stable project guidance.", content);
                        AssertFalse(content.Contains("Stale mission title"), "Generated mission blocks from the existing file should be stripped");
                        AssertTrue(
                            content.IndexOf("## Existing Project Instructions", StringComparison.Ordinal) ==
                            content.LastIndexOf("## Existing Project Instructions", StringComparison.Ordinal),
                            "Existing project instructions should be wrapped only once");
                    }
                    finally
                    {
                        try { Directory.Delete(tempDir, true); } catch { }
                    }
                }
            });

            await RunTest("Launch prompt does not demand a re-read of an auto-loaded root instruction file", async () =>
            {
                // Probe papercut 2026-08-09: the launch prompt told an OpenCode captain to read
                // AGENTS.md explicitly although the runtime had already injected it, doubling the
                // received bytes. For a runtime that auto-loads the root file, the directive states
                // the file is already loaded instead of ordering a read.
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    IPromptTemplateService templateService = new PromptTemplateService(testDb.Driver, logging);
                    await templateService.SeedDefaultsAsync();

                    Vessel vessel = new Vessel("AutoLoadVessel", "https://github.com/test/repo");

                    Mission mission = new Mission("Probe mission", "Measure received context.");
                    mission.BranchName = "armada/auto-load";

                    Dock dock = new Dock(vessel.Id);
                    dock.BranchName = mission.BranchName;
                    dock.WorktreePath = Path.Combine(Path.GetTempPath(), "armada_prompt_autoload_" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(dock.WorktreePath);
                    await File.WriteAllTextAsync(Path.Combine(dock.WorktreePath, "AGENTS.md"), "# mission instructions\n");

                    try
                    {
                        Captain captain = new Captain("autoload-captain");
                        captain.Runtime = AgentRuntimeEnum.OpenCode;

                        string prompt = await MissionPromptBuilder.BuildLaunchPromptAsync(
                            mission, vessel, captain, dock, templateService).ConfigureAwait(false);

                        AssertContains("AGENTS.md", prompt, "the directive must name the auto-loaded file");
                        AssertContains("already loaded by your runtime", prompt,
                            "an auto-loaded root file must be stated as loaded, not ordered to be read again");
                        AssertFalse(prompt.Contains("Read `AGENTS.md`", StringComparison.Ordinal),
                            "the launch prompt must not order an explicit read of an already-loaded file");

                        // A runtime that does NOT auto-load the file still gets the explicit read order.
                        Captain codex = new Captain("codex-captain");
                        codex.Runtime = AgentRuntimeEnum.Codex;
                        await File.WriteAllTextAsync(Path.Combine(dock.WorktreePath, "CODEX.md"), "# mission instructions\n");

                        string codexPrompt = await MissionPromptBuilder.BuildLaunchPromptAsync(
                            mission, vessel, codex, dock, templateService).ConfigureAwait(false);
                        AssertContains("Read `CODEX.md` in the working directory immediately", codexPrompt,
                            "a non-auto-load runtime must still be ordered to read the file");
                    }
                    finally
                    {
                        try { Directory.Delete(dock.WorktreePath, true); } catch { }
                    }
                }
            });

            await RunTest("LaunchPrompt ReadOnly Mission Uses ReportOnly Completion Clause", async () =>
            {
                Mission mission = new Mission("vsl_test");
                mission.Mode = MissionModeEnum.Research;
                mission.Title = "Context probe: test";
                mission.Persona = "Worker";
                Vessel vessel = new Vessel { Id = "vsl_test", Name = "test", DefaultBranch = "main" };
                Captain captain = new Captain { Name = "captain-1", Runtime = AgentRuntimeEnum.Codex };
                string worktree = Path.Combine(Path.GetTempPath(), "armada-launch-wt-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(worktree);
                try
                {
                    Dock dock = new Dock("vsl_test");
                    dock.WorktreePath = worktree;
                    dock.BranchName = "armada/branch";
                    string prompt = await MissionPromptBuilder.BuildLaunchPromptAsync(mission, vessel, captain, dock, null).ConfigureAwait(false);
                    AssertContains("report-only Research mission", prompt, "Read-only launch must state the report-only completion contract");
                    AssertFalse(prompt.Contains("For an Implementation mission"), "Read-only launch must not carry the implementation completion clause");
                }
                finally
                {
                    if (Directory.Exists(worktree))
                    {
                        try { Directory.Delete(worktree, true); }
                        catch { }
                    }
                }
            });

            await RunTest("LaunchPrompt Implementation Mission Keeps Completion Clause", async () =>
            {
                Mission mission = new Mission("vsl_test");
                mission.Mode = MissionModeEnum.Implementation;
                mission.Title = "Implement a thing";
                mission.Persona = "Worker";
                Vessel vessel = new Vessel { Id = "vsl_test", Name = "test", DefaultBranch = "main" };
                Captain captain = new Captain { Name = "captain-1", Runtime = AgentRuntimeEnum.Codex };
                string worktree = Path.Combine(Path.GetTempPath(), "armada-launch-wt-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(worktree);
                try
                {
                    Dock dock = new Dock("vsl_test");
                    dock.WorktreePath = worktree;
                    dock.BranchName = "armada/branch";
                    string prompt = await MissionPromptBuilder.BuildLaunchPromptAsync(mission, vessel, captain, dock, null).ConfigureAwait(false);
                    AssertContains("For an Implementation mission", prompt, "Implementation launch must keep the completion clause");
                    AssertFalse(prompt.Contains("report-only Research mission"), "Implementation launch must not carry report-only framing");
                }
                finally
                {
                    if (Directory.Exists(worktree))
                    {
                        try { Directory.Delete(worktree, true); }
                        catch { }
                    }
                }
            });

            await RunTest("SanitizeLaunchText Maps Typographic Characters To Ascii", () =>
            {
                string input = "Probe \u2014 title\uFEFF with \u2018quotes\u2019 and \u201Cdouble\u201D \u2026 \u00A0\u2022";
                string output = MissionPromptBuilder.SanitizeLaunchText(input);
                bool allAscii = output.All(c => c <= 0x7E);
                AssertTrue(allAscii, "Sanitized output must be pure ASCII");
                AssertFalse(output.Contains('\u2014'), "em dash must be replaced");
                AssertFalse(output.Contains('\uFEFF'), "byte-order mark must be removed");
                AssertContains("-", output, "em dash must map to hyphen");
            });

            await RunTest("AiMemorySection States Authoritative Precedence", () =>
            {
                string section = MissionService.BuildAiMemorySection("/memory-root", null);
                AssertContains("authoritative durable memory", section, "AI-Memory precedence must be stated");
                AssertContains("do not write to it", section, "runtime file-memory write must be forbidden");
            });

            await RunTest("AiMemorySection Names This Vessel's Folder And Rules Out The Others", () =>
            {
                // A captain told only to read the index guesses which folders are its own, and a guess
                // lands on another repository's memory: material that applies to nothing it is doing.
                string section = MissionService.BuildAiMemorySection("/memory-root", "examplevessel");

                AssertContains("/memory-root/repos/examplevessel/", section, "the vessel's own folder must be named");
                AssertContains("Read every file under `/memory-root/shared/`", section, "the shared set must be read in full");
                AssertContains("holds no rules", section, "the index alone must be called insufficient");
                AssertContains("Do not read another repository's folder", section, "other repositories must be ruled out");
            });

            await RunTest("AiMemorySection Says So When A Vessel Has No Memory Folder", () =>
            {
                string section = MissionService.BuildAiMemorySection("/memory-root", null);

                AssertContains("has no folder under", section, "the absence must be stated, not left to inference");
                AssertFalse(section.Contains("/repos/examplevessel/", StringComparison.Ordinal), "no folder may be named");
            });

            await RunTest("MemoryRepoFolder Normalizes A Vessel Name To Its Folder Form", () =>
            {
                AssertEqual("examplevessel", MissionService.NormalizeMemoryRepoFolder("ExampleVessel"), "case must be folded");
                AssertEqual("secondexample", MissionService.NormalizeMemoryRepoFolder("SecondExample"), "case must be folded");
                AssertEqual("thirdexample", MissionService.NormalizeMemoryRepoFolder("third-example"), "punctuation must be dropped");
                AssertEqual("", MissionService.NormalizeMemoryRepoFolder(null), "a null name yields nothing");
                AssertEqual("", MissionService.NormalizeMemoryRepoFolder("   "), "a blank name yields nothing");
            });

            await RunTest("MemoryRepoFolder Returns Null When The Folder Does Not Exist", () =>
            {
                AssertNull(MissionService.ResolveMemoryRepoFolder(null, "ExampleVessel"), "no memory root yields no folder");
                AssertNull(MissionService.ResolveMemoryRepoFolder("/nonexistent-memory-root", "ExampleVessel"),
                    "a vessel with no folder on disk must resolve to null rather than a guess");
            });

            await RunTest("MemoryRepoFolder Matches A Hyphenated Folder And Returns Its Real Name", () =>
            {
                string root = Path.Combine(Path.GetTempPath(), "armada_memory_folder_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path.Combine(root, "repos", "example-ledger"));
                try
                {
                    AssertEqual("example-ledger", MissionService.ResolveMemoryRepoFolder(root, "ExampleLedger"),
                        "a vessel name must match a folder whose name carries separators, and the real folder name must be returned");
                }
                finally
                {
                    Directory.Delete(root, true);
                }
            });

            await RunTest("MemoryRepoFolder Keeps Resolving A Folder Without Separators", () =>
            {
                string root = Path.Combine(Path.GetTempPath(), "armada_memory_folder_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path.Combine(root, "repos", "examplevessel"));
                Directory.CreateDirectory(Path.Combine(root, "repos", "other-vessel"));
                try
                {
                    string? reason;
                    AssertEqual("examplevessel", MissionService.ResolveMemoryRepoFolder(root, "ExampleVessel", out reason),
                        "a folder already in the reduced form must still resolve");
                    AssertNull(reason, "a resolved folder carries no reason");
                }
                finally
                {
                    Directory.Delete(root, true);
                }
            });

            await RunTest("MemoryRepoFolder States The Reason When No Folder Matches", () =>
            {
                string root = Path.Combine(Path.GetTempPath(), "armada_memory_folder_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path.Combine(root, "repos", "other-vessel"));
                try
                {
                    string? reason;
                    AssertNull(MissionService.ResolveMemoryRepoFolder(root, "ExampleLedger", out reason), "no folder may be guessed");
                    AssertEqual(MissionService.MemoryFolderNoMatchReason, reason, "the no-match reason must be carried");

                    string section = MissionService.BuildAiMemorySection(root, null, reason);
                    AssertContains("has no folder under", section, "the brief must state that no folder exists");
                    AssertFalse(section.Contains("/repos/other-vessel/", StringComparison.Ordinal), "another folder must not be named");
                }
                finally
                {
                    Directory.Delete(root, true);
                }
            });

            await RunTest("MemoryRepoFolder Refuses An Ambiguous Match And Names It", () =>
            {
                string root = Path.Combine(Path.GetTempPath(), "armada_memory_folder_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path.Combine(root, "repos", "example-ledger"));
                Directory.CreateDirectory(Path.Combine(root, "repos", "exampleledger"));
                try
                {
                    string? reason;
                    AssertNull(MissionService.ResolveMemoryRepoFolder(root, "ExampleLedger", out reason),
                        "two folders with one match key must resolve to no folder, never an arbitrary one");
                    AssertNotNull(reason, "the ambiguity must carry a reason");
                    AssertContains("matches 2 folders", reason!, "the reason must count the matches");
                    AssertContains("example-ledger, exampleledger", reason!, "the reason must name both folders");

                    string section = MissionService.BuildAiMemorySection(root, null, reason);
                    AssertContains("was resolved for this vessel because", section, "the brief must state the ambiguity");
                    AssertContains("example-ledger, exampleledger", section, "the brief must name the folders");
                    AssertFalse(section.Contains("has no folder under", StringComparison.Ordinal),
                        "an ambiguous match must not read as a vessel with no folder");
                }
                finally
                {
                    Directory.Delete(root, true);
                }
            });

            await RunTest("MemoryRepoFolder Unresolved Log Is Claimed Once Per Vessel", () =>
            {
                string vesselName = "ExampleVessel-" + Guid.NewGuid().ToString("N");
                AssertTrue(MissionService.ClaimUnresolvedMemoryFolderLog(vesselName), "the first claim must log");
                AssertFalse(MissionService.ClaimUnresolvedMemoryFolderLog(vesselName), "a repeat claim for the same vessel must not log");
            });

            await RunTest("Brief Names The Real Memory Folder And Reads Its Deferred Facts", async () =>
            {
                string root = Path.Combine(Path.GetTempPath(), "armada_memory_folder_" + Guid.NewGuid().ToString("N"));
                string folder = Path.Combine(root, "repos", "example-ledger");
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, "deferred-facts.md"),
                    "fact: the bench suite needs hardware this dock does not have\n" +
                    "fix: example-objective\n" +
                    "expires: 2099-09-30\n");
                string tempDir = Path.Combine(Path.GetTempPath(), "armada_memory_test_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDir);

                try
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                    {
                        LoggingModule logging = CreateLogging();
                        ArmadaSettings settings = CreateSettings();
                        settings.AiMemoryRoot = root;
                        StubGitService git = new StubGitService();
                        MissionService service = CreateMissionService(logging, testDb.Driver, settings, git);

                        AssertEqual(1, service.LoadDeferredFacts(root, MissionService.ResolveMemoryRepoFolder(root, "ExampleLedger")).Count,
                            "the deferred-facts lookup must use the resolved real folder");

                        Vessel vessel = new Vessel("ExampleLedger", "https://github.com/test/repo");
                        Captain captain = new Captain("MemoryCaptain");
                        captain.Runtime = AgentRuntimeEnum.ClaudeCode;
                        Mission mission = new Mission();
                        mission.Title = "Use the vessel memory folder";
                        mission.Description = "The real folder must be named.";

                        await service.GenerateClaudeMdAsync(tempDir, mission, vessel, captain);
                        string fileName = MissionPromptBuilder.GetInstructionsFileName(AgentRuntimeEnum.ClaudeCode.ToString());
                        string brief = await File.ReadAllTextAsync(Path.Combine(tempDir, fileName));

                        AssertContains("/repos/example-ledger/`", brief, "the brief must name the folder by its real name");
                        AssertContains("## Known Deferred Facts (1)", brief, "the brief must carry the folder's deferred facts");
                    }
                }
                finally
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                    try { Directory.Delete(root, true); } catch { }
                }
            });

            await RunTest("ReadOnlyPlaybooksWrapper Demotes Playbooks To Reference", () =>
            {
                string section = MissionService.BuildReadOnlyPlaybooksWrapperSection("## Some Playbook\nContent\n");
                AssertContains("report-only mission", section, "wrapper must name the read-only mode");
                AssertContains("mission rules win on conflict", section, "wrapper must state precedence");
                AssertContains("## Some Playbook", section, "playbook content must be preserved");
            });

            // Deferred facts: the small residue that cannot be fixed today, with the teeth that keep
            // the list from becoming a place to record problems instead of solving them.

            await RunTest("DeferredFacts Refuses An Entry With No Fix Objective", () =>
            {
                List<DeferredFact> accepted;
                List<string> refusals;
                DeferredFactsParser.Parse(
                    "fact: something is deferred\nexpires: 2099-01-01\n", out accepted, out refusals);

                AssertEqual(0, accepted.Count, "an entry with no fix objective must not reach the brief");
                AssertEqual(1, refusals.Count, "the refusal must be reported");
                AssertContains("no fix objective", refusals[0], "the reason must name the missing field");
            });

            await RunTest("DeferredFacts Refuses An Entry With No Usable Expiry", () =>
            {
                List<DeferredFact> accepted;
                List<string> refusals;
                DeferredFactsParser.Parse(
                    "fact: something is deferred\nfix: obj_example0000\n", out accepted, out refusals);
                AssertEqual(0, accepted.Count, "an entry with no expiry must not reach the brief");
                AssertContains("no usable expiry", refusals[0], "the reason must name the missing field");

                DeferredFactsParser.Parse(
                    "fact: x\nfix: obj_example0000\nexpires: soon\n", out accepted, out refusals);
                AssertEqual(0, accepted.Count, "an unparseable expiry must be refused, not ignored");
            });

            await RunTest("DeferredFacts Accepts A Complete Entry And Reads Every Field", () =>
            {
                List<DeferredFact> accepted;
                List<string> refusals;
                DeferredFactsParser.Parse(
                    "# a comment is ignored\n" +
                    "fact: the bench suite needs hardware this dock does not have\n" +
                    "fix: obj_example0000\n" +
                    "expires: 2099-09-30\n" +
                    "verified-at: 8510ae4\n", out accepted, out refusals);

                AssertEqual(0, refusals.Count, "a complete entry must not be refused");
                AssertEqual(1, accepted.Count, "the entry must be accepted");
                AssertEqual("obj_example0000", accepted[0].FixObjectiveId, "the fix objective must be read");
                AssertEqual("8510ae4", accepted[0].LastVerifiedCommit, "the verified commit must be read");
                AssertTrue(accepted[0].IsComplete(), "the entry must report itself complete");
            });

            await RunTest("DeferredFacts Section Reads As Owned, Never As Normal", () =>
            {
                DeferredFact fact = new DeferredFact();
                fact.Text = "the bench suite needs hardware this dock does not have";
                fact.FixObjectiveId = "obj_example0000";
                fact.ExpiresUtc = new DateTime(2099, 9, 30, 0, 0, 0, DateTimeKind.Utc);
                fact.LastVerifiedCommit = "8510ae4";

                string section = MissionService.BuildDeferredFactsSection(
                    new List<DeferredFact> { fact }, new DateTime(2026, 8, 11, 0, 0, 0, DateTimeKind.Utc));

                AssertContains("Known Deferred Facts (1)", section, "the count must be visible so growth reads as a regression");
                AssertContains("being fixed under obj_example0000", section, "each entry must name its owning objective");
                AssertFalse(section.Contains("STALE", StringComparison.Ordinal), "an in-date entry must not be marked stale");
            });

            await RunTest("DeferredFacts Marks An Expired Entry Stale Rather Than Dropping It", () =>
            {
                DeferredFact fact = new DeferredFact();
                fact.Text = "a red test pending a decision";
                fact.FixObjectiveId = "obj_example0000";
                fact.ExpiresUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                fact.LastVerifiedCommit = "8510ae4";

                string section = MissionService.BuildDeferredFactsSection(
                    new List<DeferredFact> { fact }, new DateTime(2026, 8, 11, 0, 0, 0, DateTimeKind.Utc));

                AssertContains("STALE", section, "a lapsed entry must be marked, not silently trusted");
                AssertContains("a red test pending a decision", section, "a lapsed entry must not be dropped");
                AssertContains("unknown rather than as current", section, "the captain must be told how to treat it");
            });

            await RunTest("DeferredFacts Section Is Empty When There Is Nothing Deferred", () =>
            {
                AssertEqual("", MissionService.BuildDeferredFactsSection(new List<DeferredFact>(), DateTime.UtcNow),
                    "an empty list must add no bytes to the brief");
                AssertEqual("", MissionService.BuildDeferredFactsSection(null!, DateTime.UtcNow),
                    "a null list must add no bytes to the brief");
            });

            // Git anchors: the facts a captain would otherwise spend its opening turns deriving.

            await RunTest("Dispatch Git anchors omit the generated mission-description self-reference", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    StubGitService git = new StubGitService();
                    git.PathsOnRevision.Add("src/Present.cs");
                    string headCommit = new string('a', 40);
                    git.HeadCommitHashResult = headCommit;
                    git.RevisionShaResult = headCommit.Substring(0, 12);
                    IDockService dockService = new DockService(logging, testDb.Driver, settings, git);
                    ICaptainService captainService = new CaptainService(logging, testDb.Driver, settings, git, dockService);
                    MissionService service = new MissionService(logging, testDb.Driver, settings, dockService, captainService,
                        git: git, resourcePressureAdmission: TestResourcePressure.Unconstrained(settings));
                    string tempDir = Path.Combine(Path.GetTempPath(), "armada_prompt_test_" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(tempDir);

                    try
                    {
                        Mission mission = new Mission
                        {
                            Title = "Review generated briefing",
                            Description = "Read _briefing/mission/01-description.md, src/Present.cs, and src/Existing.cs.\n\n"
                                + new string('x', 12000)
                        };
                        Vessel vessel = new Vessel("AnchorVessel", "https://github.com/test/repo");

                        await service.GenerateClaudeMdAsync(tempDir, mission, vessel).ConfigureAwait(false);

                        string content = await File.ReadAllTextAsync(Path.Combine(tempDir, "CLAUDE.md")).ConfigureAwait(false);
                        string generatedDescription = Path.Combine(tempDir, "_briefing", "mission", "01-description.md");
                        AssertTrue(File.Exists(generatedDescription), "the named description file is generated in the dock");
                        AssertFalse(content.Contains("- `_briefing/mission/01-description.md` does not exist on this checkout."),
                            "the dispatch anchor must not report its own generated briefing file as absent source");
                        AssertContains("- `src/Existing.cs` does not exist on this checkout. It is new work, not an edit.", content,
                            "an ordinary absent source path must keep its existing new-work warning");
                        AssertContains("- `src/Present.cs`\n", content,
                            "an ordinary present source path must remain anchored in the generated brief");
                    }
                    finally
                    {
                        try { Directory.Delete(tempDir, true); } catch { }
                    }
                }
            });

            await RunTest("Dispatch Git anchors flag a cited range that no longer holds its symbol and a stale cited path", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    StubGitService git = new StubGitService();
                    git.PathsOnRevision.Add("src/Present.cs");
                    git.FileLinesOnRevision["src/Present.cs"] = new List<string>
                    {
                        "namespace Example {",
                        "    void ParseFrame() { }",
                        "    void Unrelated() { }",
                        "}"
                    };
                    string headCommit = new string('a', 40);
                    git.HeadCommitHashResult = headCommit;
                    git.RevisionShaResult = headCommit.Substring(0, 12);
                    IDockService dockService = new DockService(logging, testDb.Driver, settings, git);
                    ICaptainService captainService = new CaptainService(logging, testDb.Driver, settings, git, dockService);
                    MissionService service = new MissionService(logging, testDb.Driver, settings, dockService, captainService,
                        git: git, resourcePressureAdmission: TestResourcePressure.Unconstrained(settings));
                    string tempDir = Path.Combine(Path.GetTempPath(), "armada_prompt_test_" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(tempDir);

                    try
                    {
                        Mission mission = new Mission
                        {
                            Title = "Fix the frame parser",
                            Description = "Change `ParseFrame` (`src/Present.cs:3-4`) and keep `src/Moved/Codec.cs:10` in step."
                        };
                        Vessel vessel = new Vessel("AnchorVessel", "https://github.com/test/repo");

                        await service.GenerateClaudeMdAsync(tempDir, mission, vessel).ConfigureAwait(false);

                        string content = await File.ReadAllTextAsync(Path.Combine(tempDir, "CLAUDE.md")).ConfigureAwait(false);
                        AssertContains("`ParseFrame` is not within `src/Present.cs:3-4`; it is declared at line 2.", content,
                            "a cited range that no longer holds its symbol is flagged with where the symbol is");
                        AssertContains("`src/Moved/Codec.cs` does not exist on this checkout, but the text cites lines in it", content,
                            "an absent path cited with lines is reported as a stale citation");
                        AssertFalse(content.Contains("`src/Moved/Codec.cs` does not exist on this checkout. It is new work"),
                            "a stale citation is never new work");
                    }
                    finally
                    {
                        try { Directory.Delete(tempDir, true); } catch { }
                    }
                }
            });

            await RunTest("Dock Git anchors resolve a bare cited file name and flag a stale range with the symbol's current line", async () =>
            {
                string root = Path.Combine(Path.GetTempPath(), "armada_bare_citation_" + Guid.NewGuid().ToString("N"));
                try
                {
                    string worktree = Path.Combine(root, "Target");
                    await CreateFixtureRepositoryAsync(worktree, "src/Ledger/Ledger.cs", LedgerFixture);

                    GitAnchors anchors = await ResolveRealDockAnchorsAsync(worktree,
                        "The cap comes from Ledger.cs:3-4 for a `Math.Min(...)` deadline.", null);

                    AssertNull(anchors.ResolutionError, "the anchors resolve completely");
                    GitAnchorFileHistory file = anchors.Files.Single();
                    AssertEqual("src/Ledger/Ledger.cs", file.Path, "a unique bare name is anchored at its tracked path");
                    AssertEqual("Ledger.cs", file.RequestedPath, "the anchor keeps the name the text used");
                    AssertTrue(file.ExistsOnRevision, "a present file is never called absent");
                    AssertEqual(1, anchors.CitationIssues.Count, "the stale range is the only issue: " + String.Join(" | ", anchors.CitationIssues));
                    AssertEqual("`Math.Min` is not within `Ledger.cs:3-4`; it is at line 7.", anchors.CitationIssues[0],
                        "the call expression named after the citation is found at its current line");
                    string section = MissionService.BuildGitAnchorsSection(anchors);
                    AssertContains("### Cited line ranges to recheck", section, "the stale range is listed in the existing section");
                    AssertContains("`src/Ledger/Ledger.cs` (the mission names it `Ledger.cs`", section, "the brief names the tracked path");
                }
                finally
                {
                    try { Directory.Delete(root, true); } catch { }
                }
            });

            await RunTest("Dock Git anchors leave bare-name ranges that still hold their symbols unflagged", async () =>
            {
                string root = Path.Combine(Path.GetTempPath(), "armada_bare_citation_" + Guid.NewGuid().ToString("N"));
                try
                {
                    string worktree = Path.Combine(root, "Target");
                    await CreateFixtureRepositoryAsync(worktree, "src/Ledger/Ledger.cs", LedgerFixture);

                    GitAnchors anchors = await ResolveRealDockAnchorsAsync(worktree,
                        "Change `Total` at Ledger.cs:5 so it rounds. Ledger.cs:7 keeps the `Math.Min(...)` cap.", null);

                    AssertNull(anchors.ResolutionError, "the anchors resolve completely");
                    AssertEqual("src/Ledger/Ledger.cs", anchors.Files.Single().Path, "the bare name resolves once");
                    AssertEqual(0, anchors.CitationIssues.Count, "ranges that hold their symbols are not flagged: " + String.Join(" | ", anchors.CitationIssues));
                }
                finally
                {
                    try { Directory.Delete(root, true); } catch { }
                }
            });

            await RunTest("Dock Git anchors call a bare file name that several tracked files share ambiguous, never absent", async () =>
            {
                string root = Path.Combine(Path.GetTempPath(), "armada_bare_citation_" + Guid.NewGuid().ToString("N"));
                try
                {
                    string worktree = Path.Combine(root, "Target");
                    await CreateFixtureRepositoryAsync(worktree, "src/Alpha/Widget.cs", LedgerFixture);
                    Directory.CreateDirectory(Path.Combine(worktree, "src", "Beta"));
                    await File.WriteAllTextAsync(Path.Combine(worktree, "src", "Beta", "Widget.cs"), LedgerFixture);
                    await RunGitAsync(worktree, "add", "-A");
                    await RunGitAsync(worktree, "commit", "-q", "-m", "second widget");

                    GitAnchors anchors = await ResolveRealDockAnchorsAsync(worktree, "Widget.cs:5 declares `Total`.", null);

                    GitAnchorFileHistory file = anchors.Files.Single();
                    AssertEqual(2, file.MatchingPathCount, "both tracked files are counted");
                    AssertFalse(file.ExistsOnRevision, "an ambiguous name resolves to neither file");
                    AssertEqual(0, anchors.CitationIssues.Count, "an ambiguous name is not also reported as untracked");
                    string section = MissionService.BuildGitAnchorsSection(anchors);
                    AssertContains("- `Widget.cs` matches 2 tracked paths on this checkout", section, "the name is reported as ambiguous");
                    AssertFalse(section.Contains("`Widget.cs` does not exist"), "an ambiguous name is never called absent");
                }
                finally
                {
                    try { Directory.Delete(root, true); } catch { }
                }
            });

            await RunTest("Dock Git anchors check a citation into a provisioned sibling checkout at its HEAD", async () =>
            {
                string root = Path.Combine(Path.GetTempPath(), "armada_sibling_citation_" + Guid.NewGuid().ToString("N"));
                try
                {
                    string worktree = Path.Combine(root, "Target");
                    await CreateFixtureRepositoryAsync(worktree, "README.md", "target\n");
                    await CreateFixtureRepositoryAsync(Path.Combine(root, "Shared"), "lib/Codec.cs", CodecFixture);
                    string siblings = JsonSerializer.Serialize(new List<SiblingRepo> { new SiblingRepo { RelativePath = "../Shared" } });

                    GitAnchors anchors = await ResolveRealDockAnchorsAsync(worktree,
                        "`Encode` (../Shared/lib/Codec.cs:1-2) adds one. The sibling file ../Shared/lib/Codec.cs:5 declares `Encode` as well.",
                        siblings);

                    AssertNull(anchors.ResolutionError, "the anchors resolve completely");
                    AssertEqual(1, anchors.CitationIssues.Count, "only the stale sibling range is flagged: " + String.Join(" | ", anchors.CitationIssues));
                    AssertEqual("`Encode` is not within `../Shared/lib/Codec.cs:1-2`; it is declared at line 5.", anchors.CitationIssues[0],
                        "the sibling file is read at its HEAD and the symbol's current line is named");
                }
                finally
                {
                    try { Directory.Delete(root, true); } catch { }
                }
            });

            await RunTest("Dock Git anchors list a sibling citation they cannot check with the reason", async () =>
            {
                string root = Path.Combine(Path.GetTempPath(), "armada_sibling_citation_" + Guid.NewGuid().ToString("N"));
                try
                {
                    string worktree = Path.Combine(root, "Target");
                    await CreateFixtureRepositoryAsync(worktree, "README.md", "target\n");
                    string siblings = JsonSerializer.Serialize(new List<SiblingRepo> { new SiblingRepo { RelativePath = "../Shared" } });

                    GitAnchors anchors = await ResolveRealDockAnchorsAsync(worktree,
                        "Read ../Shared/lib/Codec.cs:5 first. Then compare ../Other/src/Tool.cs:3 with it.", siblings);

                    AssertEqual(2, anchors.CitationIssues.Count, "each unchecked sibling citation is listed: " + String.Join(" | ", anchors.CitationIssues));
                    AssertEqual("`../Shared/lib/Codec.cs:5` is in the sibling checkout `../Shared`, which is not provisioned beside this dock, so it was not checked.",
                        anchors.CitationIssues[0], "a declared sibling that is absent is named as unprovisioned");
                    AssertEqual("`../Other/src/Tool.cs:3` is outside this checkout and in no sibling checkout this vessel declares, so it was not checked.",
                        anchors.CitationIssues[1], "an undeclared sibling is named as undeclared");
                    AssertContains("### Cited line ranges to recheck", MissionService.BuildGitAnchorsSection(anchors),
                        "unchecked citations are listed in the existing section");
                }
                finally
                {
                    try { Directory.Delete(root, true); } catch { }
                }
            });

            await RunTest("Dock Git anchors read a path the way text names it: own repository prefix, sibling prefix, bare sibling name, ambiguous, absent", async () =>
            {
                string root = Path.Combine(Path.GetTempPath(), "armada_named_path_" + Guid.NewGuid().ToString("N"));
                try
                {
                    string worktree = Path.Combine(root, "Target");
                    await CreateFixtureRepositoryAsync(worktree, "src/Owner/SpeedWorkflow.cs", LedgerFixture);
                    foreach (string widget in new[] { "src/Alpha/Widget.cs", "src/Beta/Widget.cs" })
                    {
                        string file = Path.Combine(worktree, widget.Replace('/', Path.DirectorySeparatorChar));
                        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                        await File.WriteAllTextAsync(file, LedgerFixture);
                    }
                    await RunGitAsync(worktree, "add", "-A");
                    await RunGitAsync(worktree, "commit", "-q", "-m", "widgets");
                    string sibling = Path.Combine(root, "ExampleSibling");
                    await CreateFixtureRepositoryAsync(sibling, "lib/Codec.cs", CodecFixture);
                    await File.WriteAllTextAsync(Path.Combine(sibling, "lib", "SourceOnly.cs"), CodecFixture);
                    await RunGitAsync(sibling, "add", "-A");
                    await RunGitAsync(sibling, "commit", "-q", "-m", "source");
                    string siblings = JsonSerializer.Serialize(new List<SiblingRepo> { new SiblingRepo { RelativePath = "../ExampleSibling" } });

                    GitAnchors anchors = await ResolveRealDockAnchorsAsync(worktree,
                        "`Total` is at ExampleVessel/src/Owner/SpeedWorkflow.cs:5 today.\n" +
                        "`Encode` is at ExampleSibling/lib/Codec.cs:5 in the source.\n" +
                        "`Encode` also appears at SourceOnly.cs:5 in the source.\n" +
                        "`Total` is declared at Widget.cs:5 as well.\n" +
                        "Create src/Gone/Missing.cs for the new step.\n" +
                        "`Lookup` came from NoSuch.cs:2 once.",
                        siblings, "ExampleVessel");

                    AssertNull(anchors.ResolutionError, "the anchors resolve completely");
                    Dictionary<string, GitAnchorFileHistory> byName = new Dictionary<string, GitAnchorFileHistory>(StringComparer.Ordinal);
                    foreach (GitAnchorFileHistory file in anchors.Files) byName[String.IsNullOrEmpty(file.RequestedPath) ? file.Path : file.RequestedPath] = file;

                    GitAnchorFileHistory own = byName["ExampleVessel/src/Owner/SpeedWorkflow.cs"];
                    AssertTrue(own.ExistsOnRevision, "a path with the repository's own directory in front is present, never absent");
                    AssertEqual("src/Owner/SpeedWorkflow.cs", own.Path, "it is anchored at the tracked path");
                    AssertTrue(byName["ExampleSibling/lib/Codec.cs"].IsExternalSourceTree, "a path with a sibling's directory in front is a sibling path");
                    AssertTrue(byName["SourceOnly.cs"].IsExternalSourceTree, "a bare name a provisioned sibling tracks is a sibling path");
                    AssertEqual(2, byName["Widget.cs"].MatchingPathCount, "an ambiguous bare name is counted, not resolved");
                    AssertFalse(byName["src/Gone/Missing.cs"].ExistsOnRevision, "a genuinely absent path stays absent");
                    AssertEqual(0, anchors.CitationIssues.Count, "every reachable citation holds its symbol: " + String.Join(" | ", anchors.CitationIssues));

                    string section = MissionService.BuildGitAnchorsSection(anchors);
                    AssertContains("`src/Owner/SpeedWorkflow.cs` (the mission names it `ExampleVessel/src/Owner/SpeedWorkflow.cs`", section,
                        "the brief names the tracked path");
                    foreach (string named in new[] { "ExampleVessel/src/Owner/SpeedWorkflow.cs", "ExampleSibling/lib/Codec.cs", "SourceOnly.cs", "Widget.cs", "NoSuch.cs" })
                        AssertFalse(section.Contains("`" + named + "` does not exist"), named + " is never called absent");
                    AssertContains("- `src/Gone/Missing.cs` does not exist on this checkout. It is new work, not an edit.", section,
                        "a genuinely absent path is still stated");
                    AssertContains("- `NoSuch.cs` is not found in this checkout or in a provisioned sibling checkout, but the text cites lines in it", section,
                        "a bare name found nowhere is stated as not found");
                }
                finally
                {
                    try { Directory.Delete(root, true); } catch { }
                }
            });

            await RunTest("Dock Git anchors check a cited bare name against every same-named sibling file and flag it only when no file holds the lines", async () =>
            {
                string root = Path.Combine(Path.GetTempPath(), "armada_same_named_" + Guid.NewGuid().ToString("N"));
                try
                {
                    string worktree = Path.Combine(root, "Target");
                    await CreateFixtureRepositoryAsync(worktree, "src/Owner/SpeedWorkflow.cs", LedgerFixture);
                    // The sibling tracks a 3-line Codec.cs; its extraction artifact tree holds a 173-line Codec.cs.
                    string sibling = Path.Combine(root, "ExampleSibling");
                    await CreateFixtureRepositoryAsync(sibling, "lib/Codec.cs", "line 1\nline 2\nline 3\n");
                    string artifact = Path.Combine(sibling, "output", "extracted", "lib", "Codec.cs");
                    Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);
                    StringBuilder longFile = new StringBuilder();
                    for (int line = 1; line <= 173; line++) longFile.Append("line " + line + "\n");
                    await File.WriteAllTextAsync(artifact, longFile.ToString());
                    string siblings = JsonSerializer.Serialize(new List<SiblingRepo>
                    {
                        new SiblingRepo { RelativePath = "../ExampleSibling", ExtractionArtifactPaths = new List<string> { "output/extracted" } }
                    });

                    GitAnchors held = await ResolveRealDockAnchorsAsync(worktree, "The port follows Codec.cs:120-130 in the source.", siblings);
                    AssertNull(held.ResolutionError, "the anchors resolve completely");
                    AssertEqual(0, held.CitationIssues.Count,
                        "a range that holds in the 173-line copy is not flagged against the 3-line copy: " + String.Join(" | ", held.CitationIssues));

                    GitAnchors stale = await ResolveRealDockAnchorsAsync(worktree, "The port follows Codec.cs:200-210 in the source.", siblings);
                    AssertEqual(1, stale.CitationIssues.Count, "a range that holds in no same-named file is flagged");
                    AssertContains("hold in none of them (`lib/Codec.cs`, `output/extracted/lib/Codec.cs`)", stale.CitationIssues[0],
                        "the stale citation lists every file it was checked against");
                }
                finally
                {
                    try { Directory.Delete(root, true); } catch { }
                }
            });

            await RunTest("Dock Git anchors read a Git LFS file's content, never its pointer, and name an unreadable one as not checked", async () =>
            {
                string root = Path.Combine(Path.GetTempPath(), "armada_lfs_pointer_" + Guid.NewGuid().ToString("N"));
                try
                {
                    // The commit holds the 3-line LFS pointer of a 173-line file.
                    StringBuilder longFile = new StringBuilder();
                    for (int line = 1; line <= 173; line++) longFile.Append("line " + line + "\n");
                    byte[] content = Encoding.UTF8.GetBytes(longFile.ToString());
                    string oid = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content)).ToLowerInvariant();
                    string pointer = "version https://git-lfs.github.com/spec/v1\noid sha256:" + oid + "\nsize " + content.Length + "\n";
                    string worktree = Path.Combine(root, "Target");
                    await CreateFixtureRepositoryAsync(worktree, "lib/Codec.cs", pointer);
                    string workingFile = Path.Combine(worktree, "lib", "Codec.cs");
                    string storedObject = Path.Combine(worktree, ".git", "lfs", "objects", oid.Substring(0, 2), oid.Substring(2, 2), oid);

                    GitAnchors unreadable = await ResolveRealDockAnchorsAsync(worktree, "The port follows lib/Codec.cs:120-130 in the source.", null);
                    AssertEqual(1, unreadable.CitationIssues.Count, "a citation of an LFS file with no local content is named once");
                    AssertContains("stored through Git LFS whose content is not available here, so it was not checked", unreadable.CitationIssues[0],
                        "the citation is named as not checked");
                    AssertFalse(unreadable.CitationIssues[0].Contains("past the end", StringComparison.Ordinal),
                        "the pointer is never read as a 3-line file: " + unreadable.CitationIssues[0]);

                    Directory.CreateDirectory(Path.GetDirectoryName(storedObject)!);
                    await File.WriteAllBytesAsync(storedObject, content);
                    GitAnchors fromStore = await ResolveRealDockAnchorsAsync(worktree, "The port follows lib/Codec.cs:120-130 in the source.", null);
                    AssertEqual(0, fromStore.CitationIssues.Count,
                        "a range that holds in the stored object is not flagged: " + String.Join(" | ", fromStore.CitationIssues));
                    GitAnchors pastEnd = await ResolveRealDockAnchorsAsync(worktree, "The port follows lib/Codec.cs:200-210 in the source.", null);
                    AssertEqual(1, pastEnd.CitationIssues.Count, "a range past the real file's end is flagged");
                    AssertContains("past the end of the file (173 lines)", pastEnd.CitationIssues[0], "the real file's length is measured");

                    File.Delete(storedObject);
                    await File.WriteAllBytesAsync(workingFile, content);
                    GitAnchors fromWorkingTree = await ResolveRealDockAnchorsAsync(worktree, "The port follows lib/Codec.cs:120-130 in the source.", null);
                    AssertEqual(0, fromWorkingTree.CitationIssues.Count,
                        "a range that holds in the smudged working-tree file is not flagged: " + String.Join(" | ", fromWorkingTree.CitationIssues));
                }
                finally
                {
                    try { Directory.Delete(root, true); } catch { }
                }
            });

            await RunTest("Dock Git anchors read a path from a sibling checkout's root in that sibling and never call an unsearched path absent", async () =>
            {
                string root = Path.Combine(Path.GetTempPath(), "armada_sibling_root_path_" + Guid.NewGuid().ToString("N"));
                try
                {
                    string worktree = Path.Combine(root, "Target");
                    await CreateFixtureRepositoryAsync(worktree, "README.md", "target\n");
                    string sibling = Path.Combine(root, "ExampleSibling");
                    await CreateFixtureRepositoryAsync(sibling, "src/Engine/Reader.cs", CodecFixture);
                    string siblings = JsonSerializer.Serialize(new List<SiblingRepo>
                    {
                        new SiblingRepo { RelativePath = "../ExampleSibling" },
                        new SiblingRepo { RelativePath = "../Unprovisioned" }
                    });

                    GitAnchors anchors = await ResolveRealDockAnchorsAsync(worktree,
                        "`Encode` is at src/Engine/Reader.cs:5 in the sibling.\n" +
                        "Compare it with lib/Other/Parser.cs and ../FromText/notes/readme.md.",
                        siblings);

                    AssertNull(anchors.ResolutionError, "the anchors resolve completely");
                    AssertEqual(0, anchors.CitationIssues.Count, "the cited range is read in the sibling that tracks it: " + String.Join(" | ", anchors.CitationIssues));
                    string section = MissionService.BuildGitAnchorsSection(anchors);
                    AssertContains("- `src/Engine/Reader.cs` is not tracked here; the sibling checkout `../ExampleSibling` tracks it at its HEAD.", section,
                        "a path a provisioned sibling tracks from its own root is named as that sibling's file");
                    AssertContains("- `lib/Other/Parser.cs` is not tracked here. It may be a sibling path, not verified here: the sibling checkouts `../Unprovisioned`, `../FromText` could not be searched", section,
                        "a path no searched checkout tracks, with declared siblings unsearched, is stated as unverified");
                    AssertFalse(section.Contains("does not exist"), "no path is called absent while a declared sibling was not searched: " + section);
                }
                finally
                {
                    try { Directory.Delete(root, true); } catch { }
                }
            });

            await RunTest("GitAnchors Section Is Omitted When Nothing Resolved", () =>
            {
                GitAnchors anchors = GitAnchors.Unresolved("no git service is configured on this admiral");
                string section = MissionService.BuildGitAnchorsSection(anchors);

                AssertEqual("", section, "an unresolved block must cost the brief nothing");
            });

            await RunTest("GitAnchors Section States A Negative Prior-Art Result Explicitly", () =>
            {
                GitAnchors anchors = new GitAnchors();
                anchors.TargetBranch = "main";
                anchors.BaseCommit = "abc1234";
                anchors.TargetTip = "abc1234";

                GitAnchorPriorArt absent = new GitAnchorPriorArt();
                absent.Term = "ExampleWidgetDecoder";
                absent.Found = false;
                anchors.PriorArt.Add(absent);

                string section = MissionService.BuildGitAnchorsSection(anchors);

                AssertContains("VERIFIED ABSENT", section, "absence must be stated, not implied by silence");
                AssertContains("ExampleWidgetDecoder", section, "the searched term must be named");
                AssertContains("abc1234", section, "absence must be anchored to the commit it was proven against");
            });

            await RunTest("GitAnchors Section Reports Present Terms With Sample Locations", () =>
            {
                GitAnchors anchors = new GitAnchors();
                anchors.BaseCommit = "abc1234";

                GitAnchorPriorArt present = new GitAnchorPriorArt();
                present.Term = "MissionPromptBuilder";
                present.Found = true;
                present.MatchingFileCount = 3;
                present.SampleLocations.Add("src/Armada.Core/Services/MissionPromptBuilder.cs:13");
                anchors.PriorArt.Add(present);

                string section = MissionService.BuildGitAnchorsSection(anchors);

                AssertContains("present in 3 files", section, "the file count must be reported");
                AssertContains("MissionPromptBuilder.cs:13", section, "a sample location must be given");
                AssertFalse(section.Contains("VERIFIED ABSENT"), "a found term must not be reported as absent");
            });

            await RunTest("GitAnchors Section Flags A New File Rather Than Listing No History", () =>
            {
                GitAnchors anchors = new GitAnchors();
                anchors.BaseCommit = "abc1234";

                GitAnchorFileHistory missing = new GitAnchorFileHistory();
                missing.Path = "src/New/Thing.cs";
                missing.ExistsOnRevision = false;
                anchors.Files.Add(missing);

                string section = MissionService.BuildGitAnchorsSection(anchors);

                AssertContains("does not exist on this checkout", section, "a new path must be named as new work");
                AssertContains("new work, not an edit", section, "the captain must not hunt for history that cannot exist");
            });

            await RunTest("GitAnchors Section Does Not Call A Sibling Source Tree New Work", () =>
            {
                GitAnchors anchors = new GitAnchors();
                anchors.BaseCommit = "abc1234";

                GitAnchorFileHistory external = new GitAnchorFileHistory();
                external.Path = "output/decompiled-src/ExampleVendor.Core/ExampleDecoder.cs";
                external.ExistsOnRevision = false;
                external.IsExternalSourceTree = true;
                anchors.Files.Add(external);

                string section = MissionService.BuildGitAnchorsSection(anchors);

                AssertContains("sibling read-only source tree", section,
                    "an absent source tree must be named as a source, not as a gap in this repository");
                AssertContains("never create it in this repository", section,
                    "the captain must be told the path is not its to write");
                AssertFalse(section.Contains("new work, not an edit"),
                    "porting source is absent by design and must never read as work to do");
            });

            await RunTest("GitAnchors Section Reports Both The Named Path And The Tracked Path", () =>
            {
                GitAnchors anchors = new GitAnchors();
                anchors.BaseCommit = "abc1234";

                GitAnchorFileHistory resolved = new GitAnchorFileHistory();
                resolved.Path = "src/Example/Example.Core/Vendor/Widget/WidgetStep.cs";
                resolved.RequestedPath = "Vendor/Widget/WidgetStep.cs";
                resolved.ExistsOnRevision = true;
                anchors.Files.Add(resolved);

                string section = MissionService.BuildGitAnchorsSection(anchors);

                AssertContains("src/Example/Example.Core/Vendor/Widget/WidgetStep.cs", section,
                    "the tracked path must be given so the captain can open the file");
                AssertContains("the mission names it", section,
                    "the captain must be able to tie the tracked path to the name the brief used");
                AssertFalse(section.Contains("does not exist on this checkout"),
                    "a file named by a suffix of its tracked path is present, not absent");
            });

            await RunTest("GitAnchors Section never calls an ambiguous or stale-cited path new work, and names citation issues", () =>
            {
                GitAnchors anchors = new GitAnchors();
                anchors.BaseCommit = "abc1234";

                GitAnchorFileHistory ambiguous = new GitAnchorFileHistory();
                ambiguous.Path = "Widgets/Shared.cs";
                ambiguous.MatchingPathCount = 2;
                anchors.Files.Add(ambiguous);

                GitAnchorFileHistory stale = new GitAnchorFileHistory();
                stale.Path = "src/Moved/Codec.cs";
                stale.CitedWithLines = true;
                anchors.Files.Add(stale);

                anchors.CitationIssues.Add("`ParseFrame` is not within `src/Present.cs:40-42`; it is at line 2.");

                string section = MissionService.BuildGitAnchorsSection(anchors);

                AssertContains("`Widgets/Shared.cs` matches 2 tracked paths", section,
                    "an ambiguous name is reported as ambiguous");
                AssertContains("`src/Moved/Codec.cs` does not exist on this checkout, but the text cites lines in it", section,
                    "an absent path cited with lines is a stale citation");
                AssertFalse(section.Contains("new work, not an edit"),
                    "neither an ambiguous name nor a stale citation is new work");
                AssertContains("### Cited line ranges to recheck", section, "citation issues get their own heading");
                AssertContains("`ParseFrame` is not within `src/Present.cs:40-42`; it is at line 2.", section,
                    "each citation issue is stated as resolved");
            });

            await RunTest("GitAnchors Section Marks A Partial Resolution Incomplete", () =>
            {
                GitAnchors anchors = new GitAnchors();
                anchors.BaseCommit = "abc1234";

                GitAnchorFileHistory history = new GitAnchorFileHistory();
                history.Path = "src/Thing.cs";
                history.ExistsOnRevision = true;
                anchors.Files.Add(history);

                anchors.ResolutionError = "git anchor resolution failed: timeout";

                string section = MissionService.BuildGitAnchorsSection(anchors);

                AssertContains("INCOMPLETE", section, "a partial block must say it is partial");
                AssertContains("unknown, not as absent", section, "silence in a partial block must not read as absence");
                AssertContains("src/Thing.cs", section, "facts resolved before the failure must be kept");
            });

            await RunTest("GitAnchors Section Truncates On A Line Boundary With A Marker", () =>
            {
                string oversized = new string('x', MissionService.MaxGitAnchorsSectionChars + 500);
                string bounded = MissionService.BoundGitAnchorsSection("- line one\n" + oversized + "\n");

                AssertContains("[git anchors truncated at", bounded, "a cut block must say it was cut");
                AssertTrue(
                    bounded.Length <= MissionService.MaxGitAnchorsSectionChars + 200,
                    "the bounded block must respect its cap plus the marker");
            });

            await RunTest("GitAnchors Section Names Both Commits When The Dock Is Behind The Target", () =>
            {
                GitAnchors anchors = new GitAnchors();
                anchors.TargetBranch = "main";
                anchors.BaseCommit = "aaa1111";
                anchors.TargetTip = "bbb2222";

                string section = MissionService.BuildGitAnchorsSection(anchors);

                AssertContains("aaa1111", section, "the captain must see where its work starts");
                AssertContains("bbb2222", section, "the captain must see the target tip");
                AssertContains("These differ", section, "a dock cut from an older base must be called out");
            });

            await RunTest("GitAnchors Section Does Not Claim A Difference Between A Full And An Abbreviated Hash", () =>
            {
                // Observed live: rev-parse HEAD returns a full hash and rev-parse --short returns an
                // abbreviated one, so an ordinal comparison called one commit two. Every dispatch was
                // told its checkout was behind the target tip when it was sitting on it.
                GitAnchors anchors = new GitAnchors();
                anchors.TargetBranch = "main";
                anchors.BaseCommit = "8510ae4ab2d5fd1e897391d7228151b9ceb45e63";
                anchors.TargetTip = "8510ae4";

                string section = MissionService.BuildGitAnchorsSection(anchors);

                AssertFalse(section.Contains("These differ", StringComparison.Ordinal),
                    "the same commit in two forms must not be reported as a difference");
            });

            await RunTest("IsSameCommit Matches On Prefix And Refuses A Too-Short One", () =>
            {
                AssertTrue(MissionService.IsSameCommit("8510ae4ab2d5fd1e", "8510ae4"), "an abbreviation must match its full hash");
                AssertTrue(MissionService.IsSameCommit("8510ae4", "8510ae4ab2d5fd1e"), "order must not matter");
                AssertTrue(MissionService.IsSameCommit("8510ae4", "8510AE4"), "comparison must ignore case");
                AssertFalse(MissionService.IsSameCommit("8510ae4", "bbb2222"), "different commits must not match");
                AssertFalse(MissionService.IsSameCommit("851", "8510ae4"), "a prefix under four characters is not comparable");
                AssertFalse(MissionService.IsSameCommit(null, "8510ae4"), "a null side must not match");
                AssertFalse(MissionService.IsSameCommit("", ""), "two empty strings must not match");
            });

            await RunTest("MissionSubjectExtractor Finds Paths And Identifiers And Caps Both", () =>
            {
                // Placeholder subjects only. A test fixture that quotes a real vessel's paths or
                // symbols carries that repository's private context into this one, where it is
                // neither needed nor reviewable.
                string text =
                    "Port ExampleWidgetDecoder onto the shared parameter seam. " +
                    "Touch src/Example/Example.Core/Widgets/WidgetMessageData.cs and " +
                    "src/Example/Example.Core/Widgets/WidgetMessageDataEmitter.cs. " +
                    "Tracked under obj_exampleid0000.";

                List<string> paths = MissionSubjectExtractor.ExtractPaths(text);
                List<string> terms = MissionSubjectExtractor.ExtractTerms(text);

                AssertTrue(paths.Contains("src/Example/Example.Core/Widgets/WidgetMessageData.cs"),
                    "a named source path must be extracted");
                AssertTrue(terms.Contains("ExampleWidgetDecoder"), "a named identifier must be extracted");
                AssertTrue(paths.Count <= MissionSubjectExtractor.MaxPaths, "paths must respect their cap");
                AssertTrue(terms.Count <= MissionSubjectExtractor.MaxTerms, "terms must respect their cap");

                foreach (string term in terms)
                {
                    AssertFalse(term.StartsWith("obj_", StringComparison.Ordinal),
                        "an Armada record id names nothing in vessel source and must not become a search term");
                }
            });

            await RunTest("MissionSubjectExtractor Returns Nothing For Empty Mission Text", () =>
            {
                AssertEqual(0, MissionSubjectExtractor.ExtractPaths(null).Count, "null text yields no paths");
                AssertEqual(0, MissionSubjectExtractor.ExtractTerms("   ").Count, "blank text yields no terms");
            });

            await RunTest("Architect marker segment with front-matter takes its title from the title line", () =>
            {
                string segment =
                    "id: M1\n" +
                    "title: fix(bundlesources): fail closed on malformed device-info rows\n" +
                    "preferredModel: mid\n" +
                    "dependsOnMissionId: M2\n" +
                    "description: |\n" +
                    "  **Goal:** reject the four malformed-row cases.\n" +
                    "  Note: the export has 6 ISO rows.\n";

                MissionService.SplitArchitectMarkerSegment(segment, out string title, out string description);

                AssertEqual("fix(bundlesources): fail closed on malformed device-info rows", title, "the title comes from the title: line, never from id: M1");
                AssertFalse(description.Contains("title:"), "the title line leaves the description");
                AssertContains("dependsOnMissionId: M2", description, "the remaining front-matter stays for the dependency extractor");
                AssertContains("**Goal:** reject the four malformed-row cases.", description, "the body survives");

                (string body, string? dependency) = MissionService.ExtractArchitectFrontMatter(description);
                AssertEqual("M2", dependency, "the dependency still resolves after the split");
                AssertFalse(body.Contains("id: M1"), "the id line is stripped with the front-matter");
            });

            await RunTest("Architect marker segment without a title line keeps the first line as the title", () =>
            {
                MissionService.SplitArchitectMarkerSegment("Add API endpoint\nImplement endpoint", out string title, out string description);
                AssertEqual("Add API endpoint", title, "first line is the title");
                AssertEqual("Implement endpoint", description, "rest is the description");

                MissionService.SplitArchitectMarkerSegment("Only a title", out string t2, out string d2);
                AssertEqual("Only a title", t2, "single line is the title");
                AssertEqual("", d2, "no description");

                MissionService.SplitArchitectMarkerSegment("Verify: the count moved\nthen land", out string t3, out _);
                AssertEqual("Verify: the count moved", t3, "a prose first line with a colon but no title: key is still the title");
            });

            await RunTest("Architect front-matter dependency is captured and stripped from the brief", () =>
            {
                string block =
                    "title: fix(sequences): keep the routine projection\n" +
                    "preferredModel: mid\n" +
                    "dependsOnMissionId: M2\n" +
                    "description: |\n" +
                    "  **Goal:** Land the stranded commit.\n" +
                    "  **Files:** one file.\n";

                (string body, string? dependency) = MissionService.ExtractArchitectFrontMatter(block);

                AssertEqual("M2", dependency, "the declared M-alias must be captured");
                AssertContains("**Goal:** Land the stranded commit.", body, "the body must survive with the block indent stripped");
                AssertFalse(body.Contains("dependsOnMissionId"), "the front-matter must be stripped from the brief");
                AssertFalse(body.Contains("preferredModel"), "the front-matter must be stripped from the brief");
            });

            await RunTest("A one-line description: value reaches the mission brief while the front-matter keys around it are stripped", () =>
            {
                string segment =
                    "title: feat(export): add the export column\n" +
                    "preferredModel: mid\n" +
                    "description: Edit src/Export/Writer.cs and its tests.\n" +
                    "dependsOnMissionId: M1\n" +
                    "Keep the column order stable.\n";

                MissionService.SplitArchitectMarkerSegment(segment, out string title, out string description);
                (string body, string? dependency) = MissionService.ExtractArchitectFrontMatter(description);

                AssertEqual("feat(export): add the export column", title, "the title comes from the title: line");
                AssertEqual("M1", dependency, "a key after the one-line description is still front-matter");
                AssertEqual("Edit src/Export/Writer.cs and its tests.\nKeep the column order stable.", body,
                    "the one-line description leads the body and the prose after the front-matter follows it");
                AssertFalse(body.Contains("preferredModel"), "the other front-matter keys are stripped");
                AssertFalse(body.Contains("description:"), "the key name itself does not reach the brief");
            });

            await RunTest("Architect description without front-matter is passed through unchanged", () =>
            {
                string prose = "**Goal:** Land the stranded commit.\nNote: the recovery projection must be enriched.";

                (string body, string? dependency) = MissionService.ExtractArchitectFrontMatter(prose);

                AssertEqual(prose, body, "a body with no front-matter must be untouched");
                AssertNull(dependency, "no dependency when none is declared");
            });

            await RunTest("Architect dependency resolver maps the M-alias to the earlier block terminal stage", () =>
            {
                Mission m1 = new Mission("M1 [Worker]");
                Mission m2 = new Mission("M2 [Worker]");
                Mission m3 = new Mission("M3 [Worker]");
                Dictionary<int, Mission> byIndex = new Dictionary<int, Mission> { { 1, m1 }, { 2, m2 }, { 3, m3 } };
                Dictionary<string, Mission> byTitle = new Dictionary<string, Mission>(StringComparer.OrdinalIgnoreCase);

                Mission? resolved = MissionService.ResolveArchitectDependencyTerminalStage(byIndex, byTitle, 3, "M2");
                AssertNotNull(resolved, "M3 depending on M2 must resolve");
                AssertEqual(m2.Id, resolved!.Id, "resolved to block 2's terminal stage");

                Mission? forward = MissionService.ResolveArchitectDependencyTerminalStage(byIndex, byTitle, 2, "M3");
                AssertNull(forward, "a forward reference must not resolve; ordering is enforced");

                Mission? unknown = MissionService.ResolveArchitectDependencyTerminalStage(byIndex, byTitle, 3, "M9");
                AssertNull(unknown, "an out-of-range alias must not resolve");
            });

            // Mode- and persona-gated brief modules. Each test renders a production-shaped brief (seeded
            // templates, project-profile skills, captain instructions, dock git anchors, shared memory, code
            // index) and asserts on the file the captain receives.
            await RunTest("Brief module ledger for a Research Worker and an Implementation Worker", async () =>
            {
                SampleBrief research = await RenderSampleBriefAsync(MissionModeEnum.Research, "Worker");
                SampleBrief implementation = await RenderSampleBriefAsync(MissionModeEnum.Implementation, "Worker");

                foreach (SampleBrief brief in new[] { research, implementation })
                {
                    Console.WriteLine("  [brief-ledger] " + brief.Label + " file=" + brief.FileBytes + " launch=" + brief.LaunchPromptBytes);
                    foreach (KeyValuePair<string, int> module in brief.Modules.OrderByDescending(m => m.Value))
                        Console.WriteLine("  [brief-ledger] " + brief.Label + " " + module.Key + "=" + module.Value);
                }

                AssertTrue(research.UntrackedBytes < 100, "the ledger accounts for every module of the Research brief; untracked " + research.UntrackedBytes);
                AssertTrue(research.FileBytes < implementation.FileBytes, "a Research brief is smaller than the Implementation brief for the same mission");
            });

            await RunTest("A read-only brief leaves out skills that govern code edits", async () =>
            {
                SampleBrief research = await RenderSampleBriefAsync(MissionModeEnum.Research, "Worker");
                SampleBrief audit = await RenderSampleBriefAsync(MissionModeEnum.Audit, "Worker");
                SampleBrief implementation = await RenderSampleBriefAsync(MissionModeEnum.Implementation, "Worker");

                foreach (SampleBrief readOnly in new[] { research, audit })
                {
                    AssertFalse(readOnly.Content.Contains("### dry", StringComparison.Ordinal), readOnly.Label + " must not carry the engineering skill dry");
                    AssertFalse(readOnly.Content.Contains("### project-structure", StringComparison.Ordinal), readOnly.Label + " must not carry the engineering skill project-structure");
                    AssertContains("### reading-evidence", readOnly.Content, readOnly.Label + " keeps a skill with no edit-governing category");
                }

                AssertContains("### dry", implementation.Content, "an Implementation brief keeps the engineering skills");
                AssertContains("### project-structure", implementation.Content, "an Implementation brief keeps the engineering skills");
            });

            await RunTest("The Shared Memory read-first list never tells a read-only captain to change anything", async () =>
            {
                ContextRetrievalResult result = new ContextRetrievalResult();
                result.Core.Add(new ContextChunk { Topic = "memory.shared.core", Path = "shared/core.md", Text = "Core rule text.", Tier = ContextTierEnum.Core });
                result.Leaves.Add(new ContextChunk { Topic = "memory.repos.example.leaf", Path = "repos/example/README.md", Text = "Leaf rule text." });

                ContextBriefDelivery delivery = ContextBriefRenderer.RenderDelivery(result, "/memory-root", "example", true);

                AssertContains("### Read first", delivery.Section, "the read-first list is still rendered");
                AssertFalse(delivery.Section.Contains("change anything", StringComparison.OrdinalIgnoreCase),
                    "the read-first rule must not assume the mission edits files");
                AssertFalse(Regex.IsMatch(delivery.Section, @"\b(edit|commit|implement)\b", RegexOptions.IgnoreCase),
                    "the memory section must use no implementing verb that contradicts a read-only mission");
            });

            await RunTest("A read-only brief states git anchors as facts, not new work or prior art", async () =>
            {
                SampleBrief research = await RenderSampleBriefAsync(MissionModeEnum.Research, "Worker");
                SampleBrief implementation = await RenderSampleBriefAsync(MissionModeEnum.Implementation, "Worker");

                AssertContains("## Git Anchors", research.Content, "a read-only brief still carries the anchors");
                AssertContains("`src/Widget/Widget.cs` does not exist on this checkout.", research.Content, "the absent path is still stated");
                AssertFalse(research.Content.Contains("new work", StringComparison.OrdinalIgnoreCase), "a read-only brief must not call an absent path new work");
                AssertFalse(research.Content.Contains("Prior art", StringComparison.Ordinal), "a read-only brief must not frame subject terms as prior art");
                AssertFalse(research.Content.Contains("Your work starts at", StringComparison.Ordinal), "a read-only brief does not describe a starting point for work");
                AssertContains("`WidgetRegistry`: not found in tracked content", research.Content, "an absent term is still reported");

                AssertContains("It is new work, not an edit.", implementation.Content, "an Implementation brief keeps the new-work wording");
                AssertContains("### Prior art for this mission's subject terms", implementation.Content, "an Implementation brief keeps prior art");
            });

            await RunTest("Runtime Signals carry verdict lines only for a Judge and the Architect line only for an Architect", async () =>
            {
                SampleBrief worker = await RenderSampleBriefAsync(MissionModeEnum.Implementation, "Worker");
                SampleBrief judge = await RenderSampleBriefAsync(MissionModeEnum.Implementation, "Judge");
                SampleBrief architect = await RenderSampleBriefAsync(MissionModeEnum.Implementation, "Architect");

                string workerSignals = SectionOf(worker.Content, "## Runtime Signals");
                string judgeSignals = SectionOf(judge.Content, "## Runtime Signals");
                string architectSignals = SectionOf(architect.Content, "## Runtime Signals");

                AssertContains("armada_suite_lease", worker.Content, "a Worker takes the suite lease around a full-suite run");
                AssertContains("armada_suite_lease", judge.Content, "a Judge takes the suite lease around a full-suite run");
                AssertContains("[ARMADA:RESULT] COMPLETE", workerSignals, "a Worker keeps its result signal");
                AssertFalse(workerSignals.Contains("[ARMADA:VERDICT]", StringComparison.Ordinal), "a Worker brief carries no verdict signal");
                AssertFalse(workerSignals.Contains("Architect missions", StringComparison.Ordinal), "a Worker brief carries no Architect signal rule");

                AssertContains("`[ARMADA:VERDICT] PASS`", judgeSignals, "a Judge keeps the verdict signals");
                AssertContains("`[ARMADA:VERDICT] NEEDS_REVISION`", judgeSignals, "a Judge keeps the verdict signals");
                AssertFalse(judgeSignals.Contains("Architect missions", StringComparison.Ordinal), "a Judge brief carries no Architect signal rule");

                AssertContains("Architect missions must not emit", architectSignals, "an Architect keeps its signal rule");
                AssertFalse(architectSignals.Contains("`[ARMADA:VERDICT] PASS`", StringComparison.Ordinal), "an Architect brief lists no verdict signal");
            });

            await RunTest("The output contract is emitted once, not in the captain instructions or the launch prompt", async () =>
            {
                SampleBrief research = await RenderSampleBriefAsync(MissionModeEnum.Research, "Worker");
                string researchContract = MissionPromptBuilder.GetPersonaOutputContract("Worker", MissionModeEnum.Research);
                AssertEqual(1, CountOccurrences(research.Content, researchContract), "the Research contract appears once in the brief");
                AssertTrue(CountOccurrences(research.Content, "## Required Output Contract") <= 1, "the contract heading appears at most once");
                AssertFalse(research.LaunchPrompt.Contains("[ARMADA:RESULT]", StringComparison.Ordinal), "the launch prompt does not restate the contract");

                SampleBrief judge = await RenderSampleBriefAsync(MissionModeEnum.Implementation, "Judge");
                AssertEqual(1, CountOccurrences(judge.Content, "DELIVERY-EVIDENCE RULE"), "the Judge contract appears once in a brief with captain instructions");
                AssertFalse(judge.LaunchPrompt.Contains("[ARMADA:VERDICT]", StringComparison.Ordinal), "the launch prompt does not restate the Judge contract");
                AssertFalse(judge.LaunchPrompt.Contains("## Completeness", StringComparison.Ordinal), "the launch prompt does not list the Judge sections");

                SampleBrief bareJudge = await RenderSampleBriefAsync(MissionModeEnum.Implementation, "Judge", captainInstructions: false);
                AssertEqual(1, CountOccurrences(bareJudge.Content, "DELIVERY-EVIDENCE RULE"), "a captain with no instructions of its own still receives the Judge contract once");
            });

            await RunTest("The instruction byte budget is enforced by default and 0 selects the default", async () =>
            {
                ArmadaSettings settings = new ArmadaSettings();
                AssertTrue(settings.CaptainInstructionByteBudget > 0, "the default budget must be enforced");
                int fallback = settings.CaptainInstructionByteBudget;
                settings.CaptainInstructionByteBudget = 0;
                AssertEqual(fallback, settings.CaptainInstructionByteBudget, "a stored 0 selects the default rather than disabling the budget");
                settings.CaptainInstructionByteBudget = 40000;
                AssertEqual(40000, settings.CaptainInstructionByteBudget, "an explicit budget is kept");
                AssertThrows<ArgumentOutOfRangeException>(() => settings.CaptainInstructionByteBudget = -1);
                await Task.CompletedTask;
            });

            await RunTest("An over-budget brief records a named warning event", async () =>
            {
                SampleBrief over = await RenderSampleBriefAsync(MissionModeEnum.Implementation, "Worker", byteBudget: 4000);
                AssertEqual(1, over.OverBudgetPayloads.Count, "one mission.prompt_over_budget event is recorded");
                string payload = over.OverBudgetPayloads[0];
                AssertContains("\"ByteBudget\":4000", payload, "the event names the budget");
                AssertContains("\"AssembledBytes\":", payload, "the event names the assembled size");
                AssertContains("\"LargestModules\":", payload, "the event names the largest modules");

                SampleBrief under = await RenderSampleBriefAsync(MissionModeEnum.Implementation, "Worker", byteBudget: 5000000);
                AssertEqual(0, under.OverBudgetPayloads.Count, "a brief within budget records no warning");
            });
        }

        /// <summary>
        /// A rendered sample brief: the file, its ledger, the launch prompt and any over-budget events.
        /// </summary>
        private sealed class SampleBrief
        {
            public string Label { get; set; } = "";
            public string Content { get; set; } = "";
            public int FileBytes { get; set; }
            public int UntrackedBytes { get; set; }
            public Dictionary<string, int> Modules { get; set; } = new Dictionary<string, int>();
            public string LaunchPrompt { get; set; } = "";
            public int LaunchPromptBytes { get; set; }
            public List<string> OverBudgetPayloads { get; set; } = new List<string>();
        }

        private const string SampleSkillDry =
            "One rule, one home. Find duplication by inventory, not by reading.\n\n" +
            "1. Before adding a type, helper, catalogue, or test, grep for the capability\n   and count the definitions. Report what you found.\n" +
            "2. Re-use a landed seam. Consume the existing type and write only the missing\n   behaviour. Declare the reuse.\n" +
            "3. Edit the file that owns the subject. A new sibling file splits the subject\n   and both halves drift. A new file needs a stated reason.\n" +
            "4. Where a rule has several entry points, compare them across the whole input\n   domain, not the case in the brief. Prefer one owner; otherwise make each\n   caller delegate, and assert the delegation.\n\n" +
            "Not duplication -- do not \"fix\" these:\n\n" +
            "5. Parallel structure: parametrized cases, distinct methods on one type, an\n   API-level test and a service-level test of one subject.\n" +
            "6. A deliberate divergence. Diff two similar routines before assuming they\n   share a construct. Report it; do not flatten it.\n" +
            "7. A ported construct. Source fidelity outranks this skill.";

        private const string SampleSkillProjectStructure =
            "Match the layout that governs the area you edit. The repository root does not\ntell you the local pattern.\n\n" +
            "1. Open the sibling directory first. The shape the neighbours follow is the\n   shape your change follows.\n" +
            "2. Feature or slice folder: keep the whole slice inside it -- endpoint, handler,\n   service, model.\n" +
            "3. Domain slice: add your folder beside its peers and mirror a peer's shape.\n" +
            "4. Layered area: respect the dependency direction.\n" +
            "5. No shared or common bucket for a type one slice uses.\n" +
            "6. A test mirrors the path of the code it covers and extends the suite that\n   owns the subject.\n" +
            "7. Namespace and file name follow the folder. One public type per file.\n" +
            "8. Do not introduce an architectural pattern the brief did not ask for.";

        /// <summary>
        /// Renders a production-shaped brief on a sample vessel: seeded templates, a project profile with two
        /// engineering skills and one uncategorized skill, captain instructions, completed dock git anchors, a
        /// shared-memory root and the code-index section. Returns what the captain receives.
        /// </summary>
        private async Task<SampleBrief> RenderSampleBriefAsync(MissionModeEnum mode, string persona, int byteBudget = 0, bool captainInstructions = true)
        {
            using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
            {
                LoggingModule logging = CreateLogging();
                ArmadaSettings settings = CreateSettings();
                if (byteBudget > 0) settings.CaptainInstructionByteBudget = byteBudget;
                settings.CodeIndex.Enabled = true;
                string memoryRoot = Path.Combine(Path.GetTempPath(), "armada_sample_memory_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path.Combine(memoryRoot, "shared"));
                await File.WriteAllTextAsync(Path.Combine(memoryRoot, "shared", "INDEX.md"), "# Memory Index\n");
                settings.AiMemoryRoot = memoryRoot;
                StubGitService git = new StubGitService();
                IPromptTemplateService templateService;
                MissionService service = CreateMissionServiceWithTemplates(logging, testDb.Driver, settings, git, out templateService);
                await templateService.SeedDefaultsAsync();

                string worktree = Path.Combine(Path.GetTempPath(), "armada_sample_brief_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(worktree);

                try
                {
                    Vessel vessel = new Vessel("SampleVessel", "https://github.com/test/sample");
                    vessel.ProjectContext = "SampleVessel is a service library. Keep work in src/ and verify with the build and test profile.";
                    vessel.EnableModelContext = true;
                    vessel.ModelContext = "Use the code search tool when available, then read the matching source. Native captain memory is advice; verify it against the current checkout.";
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel);

                    foreach (Skill skill in new[]
                    {
                        new Skill { Name = "dry", Category = "engineering", Content = SampleSkillDry },
                        new Skill { Name = "project-structure", Category = "engineering", Content = SampleSkillProjectStructure },
                        new Skill { Name = "reading-evidence", Content = "Quote the file and line for every claim." }
                    })
                    {
                        await testDb.Driver.Skills.CreateAsync(skill);
                    }

                    ProjectProfile profile = new ProjectProfile();
                    profile.Name = "sample-profile";
                    profile.VesselId = vessel.Id;
                    profile.Skills = new List<string> { "dry", "project-structure", "reading-evidence" };
                    await testDb.Driver.ProjectProfiles.CreateAsync(profile);

                    Captain captain = new Captain("sample-captain");
                    captain.Runtime = AgentRuntimeEnum.ClaudeCode;
                    if (captainInstructions)
                    {
                        captain.SystemInstructions = "You are an Armada specialist captain. Keep work scoped, run relevant checks, and report exact evidence.";
                    }
                    captain = await testDb.Driver.Captains.CreateAsync(captain);

                    Mission mission = new Mission();
                    mission.Title = "Measure the widget registry";
                    mission.Description = "Examine `src/Widget/Widget.cs` and the WidgetRegistry type, and report what the registry resolves.";
                    mission.Persona = persona;
                    mission.Mode = mode;
                    mission.CaptainId = captain.Id;
                    mission.BranchName = "armada/sample";

                    string baseCommit = new string('a', 40);
                    Dock dock = new Dock(vessel.Id) { CaptainId = captain.Id, WorktreePath = worktree, BranchName = mission.BranchName };
                    GitAnchors anchors = new GitAnchors { BaseCommit = baseCommit, TargetBranch = "main", TargetTip = baseCommit };
                    anchors.Files.Add(new GitAnchorFileHistory { Path = "src/Widget/Widget.cs" });
                    anchors.PriorArt.Add(new GitAnchorPriorArt { Term = "WidgetRegistry", Found = false });
                    anchors.PriorArt.Add(new GitAnchorPriorArt { Term = "Widget", Found = true, MatchingFileCount = 2, SampleLocations = new List<string> { "src/Other.cs:12" } });
                    dock.GitAnchorsSnapshot = new DockGitAnchorSnapshot
                    {
                        DockId = dock.Id, MissionId = mission.Id, VesselId = vessel.Id,
                        ProvisionedCommit = baseCommit, ProvisionedUtc = DateTime.UtcNow, ResolvedUtc = DateTime.UtcNow,
                        State = DockGitAnchorStateEnum.Complete, Anchors = anchors
                    };
                    dock = await testDb.Driver.Docks.CreateAsync(dock);
                    mission.DockId = dock.Id;

                    await service.GenerateClaudeMdAsync(worktree, mission, vessel, captain);

                    SampleBrief brief = new SampleBrief();
                    brief.Label = mode + " " + persona;
                    brief.Content = await File.ReadAllTextAsync(Path.Combine(worktree, "CLAUDE.md"));
                    brief.FileBytes = System.Text.Encoding.UTF8.GetByteCount(brief.Content);

                    List<ArmadaEvent> budgets = await testDb.Driver.Events.EnumerateByTypeAsync("mission.prompt_budget", 10);
                    AssertEqual(1, budgets.Count, "one prompt-budget event per brief");
                    PromptBudgetModules? ledger = System.Text.Json.JsonSerializer.Deserialize<PromptBudgetModules>(budgets[0].Payload ?? "{}");
                    brief.Modules = ledger?.Modules ?? new Dictionary<string, int>();
                    brief.UntrackedBytes = brief.FileBytes - brief.Modules.Values.Sum();

                    foreach (ArmadaEvent evt in await testDb.Driver.Events.EnumerateByTypeAsync("mission.prompt_over_budget", 10))
                        brief.OverBudgetPayloads.Add(evt.Payload ?? "");

                    brief.LaunchPrompt = await MissionPromptBuilder.BuildLaunchPromptAsync(mission, vessel, captain, dock, templateService);
                    brief.LaunchPromptBytes = System.Text.Encoding.UTF8.GetByteCount(brief.LaunchPrompt);
                    return brief;
                }
                finally
                {
                    try { Directory.Delete(worktree, true); } catch { }
                    try { Directory.Delete(memoryRoot, true); } catch { }
                }
            }
        }

        private const string LedgerFixture =
            "namespace Example.Ledger\n" +
            "{\n" +
            "    public class Ledger\n" +
            "    {\n" +
            "        public int Total(int a, int b)\n" +
            "        {\n" +
            "            int capped = Math.Min(a, b);\n" +
            "            return capped;\n" +
            "        }\n" +
            "    }\n" +
            "}\n";

        private const string CodecFixture =
            "namespace Example.Shared\n" +
            "{\n" +
            "    public static class Codec\n" +
            "    {\n" +
            "        public static int Encode(int value)\n" +
            "        {\n" +
            "            return value + 1;\n" +
            "        }\n" +
            "    }\n" +
            "}\n";

        /// <summary>
        /// Create a git repository on <c>main</c> whose one commit holds a single file.
        /// </summary>
        private static async Task CreateFixtureRepositoryAsync(string repository, string relativePath, string content)
        {
            string file = Path.Combine(repository, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await File.WriteAllTextAsync(file, content);
            await RunGitAsync(repository, "init", "-q", "-b", "main");
            await RunGitAsync(repository, "config", "user.email", "test@example.invalid");
            await RunGitAsync(repository, "config", "user.name", "Test");
            await RunGitAsync(repository, "config", "commit.gpgsign", "false");
            await RunGitAsync(repository, "add", "-A");
            await RunGitAsync(repository, "commit", "-q", "-m", "fixture");
        }

        /// <summary>
        /// Record a dock provisioned at the repository's HEAD for a mission with the given description, and
        /// resolve its git anchors the way dispatch does.
        /// </summary>
        private async Task<GitAnchors> ResolveRealDockAnchorsAsync(string worktree, string description, string? siblingRepos,
            string vesselName = "citations")
        {
            using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
            {
                LoggingModule logging = CreateLogging();
                ArmadaSettings settings = CreateSettings();
                GitService git = new GitService(logging);
                IDockService docks = new DockService(logging, testDb.Driver, settings, git);
                ICaptainService captains = new CaptainService(logging, testDb.Driver, settings, git, docks);
                MissionService service = new MissionService(logging, testDb.Driver, settings, docks, captains, git: git,
                    resourcePressureAdmission: TestResourcePressure.Unconstrained(settings));
                Vessel vessel = await testDb.Driver.Vessels.CreateAsync(new Vessel(vesselName, "https://example.invalid/repo"));
                vessel.SiblingRepos = siblingRepos;
                Captain captain = await testDb.Driver.Captains.CreateAsync(new Captain("citations"));
                Mission mission = new Mission
                {
                    Title = "Recheck the cited code",
                    Description = description,
                    VesselId = vessel.Id,
                    CaptainId = captain.Id
                };
                string source = (await RunGitAsync(worktree, "rev-parse", "HEAD")).Trim();
                Dock dock = new Dock(vessel.Id) { CaptainId = captain.Id, WorktreePath = worktree, BranchName = "mission" };
                dock.GitAnchorsSnapshot = new DockGitAnchorSnapshot
                {
                    DockId = dock.Id, MissionId = mission.Id, VesselId = vessel.Id,
                    ProvisionedCommit = source, ProvisionedUtc = DateTime.UtcNow,
                    Anchors = new GitAnchors { BaseCommit = source }
                };
                dock = await testDb.Driver.Docks.CreateAsync(dock);
                mission.DockId = dock.Id;
                return await service.ResolveDispatchGitAnchorsAsync(worktree, mission, vessel, default);
            }
        }

        /// <summary>
        /// The text of one level-two section, from its heading to the next level-two heading.
        /// </summary>
        private static string SectionOf(string content, string heading)
        {
            int start = content.IndexOf(heading, StringComparison.Ordinal);
            if (start < 0) return "";
            int next = content.IndexOf("\n## ", start + heading.Length, StringComparison.Ordinal);
            return next < 0 ? content.Substring(start) : content.Substring(start, next - start);
        }

        private static int CountOccurrences(string haystack, string needle)
        {
            int count = 0;
            int index = 0;
            while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += needle.Length;
            }
            return count;
        }

        /// <summary>
        /// The per-module byte accounting of a mission.prompt_budget event payload.
        /// </summary>
        private sealed class PromptBudgetModules
        {
            /// <summary>Bytes per tracked brief module.</summary>
            public Dictionary<string, int> Modules { get; set; } = new Dictionary<string, int>();
        }
    }
}
