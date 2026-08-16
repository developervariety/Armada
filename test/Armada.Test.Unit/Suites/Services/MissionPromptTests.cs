namespace Armada.Test.Unit.Suites.Services
{
    using System.Text.RegularExpressions;
    using Armada.Core.Database.Sqlite;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    public class MissionPromptTests : TestSuite
    {
        public override string Name => "Mission Prompt (ProjectContext/StyleGuide/ModelContext)";

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
            return new MissionService(logging, db, settings, dockService, captainService);
        }

        private MissionService CreateMissionServiceWithTemplates(LoggingModule logging, SqliteDatabaseDriver db, ArmadaSettings settings, StubGitService git, out IPromptTemplateService templateService)
        {
            IDockService dockService = new DockService(logging, db, settings, git);
            ICaptainService captainService = new CaptainService(logging, db, settings, git, dockService);
            templateService = new PromptTemplateService(db, logging);
            return new MissionService(logging, db, settings, dockService, captainService, templateService);
        }

        protected override async Task RunTestsAsync()
        {
            await RunTest("GenerateClaudeMdAsync includes ProjectContext when set", async () =>
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
                        Vessel vessel = new Vessel("PromptVessel", "https://github.com/test/repo");
                        vessel.ProjectContext = "This is a React TypeScript frontend with Redux state management.";

                        Mission mission = new Mission();
                        mission.Title = "Fix login bug";
                        mission.Description = "The login form does not validate email addresses.";

                        await service.GenerateClaudeMdAsync(tempDir, mission, vessel);

                        string content = await File.ReadAllTextAsync(Path.Combine(tempDir, "CLAUDE.md"));
                        AssertContains("## Project Context", content);
                        AssertContains("This is a React TypeScript frontend with Redux state management.", content);
                    }
                    finally
                    {
                        try { Directory.Delete(tempDir, true); } catch { }
                    }
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
                        objective.Title = "Scope the seed-key port";
                        objective.Description = "Port the seed-key exchange to the extractor.";
                        objective.AcceptanceCriteria = new List<string> { "The exchange round-trips 128 seeds.", "No secret bytes enter the manifest." };
                        objective.NonGoals = new List<string> { "No reflash support." };
                        objective.VoyageIds = new List<string> { voyage.Id };
                        await testDb.Driver.Objectives.CreateAsync(objective).ConfigureAwait(false);

                        Vessel vessel = new Vessel("ScopeVessel", "https://github.com/test/repo");
                        Mission mission = new Mission();
                        mission.Title = "Port seed-key";
                        mission.Description = "Implement the seed-key exchange per the objective.";
                        mission.VoyageId = voyage.Id;

                        await service.GenerateClaudeMdAsync(tempDir, mission, vessel);

                        string content = await File.ReadAllTextAsync(Path.Combine(tempDir, "CLAUDE.md"));
                        AssertContains("## Objective Scope (Definition of Done)", content);
                        AssertContains("Port the seed-key exchange to the extractor.", content);
                        AssertContains("The exchange round-trips 128 seeds.", content);
                        AssertContains("No secret bytes enter the manifest.", content);
                        AssertContains("No reflash support.", content);
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
                        AssertTrue(!content.Contains("Objective Scope"), "A voyage without a linked objective must not carry an Objective Scope module");
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
                        AssertFalse(brief.Contains("armada_code_search", StringComparison.Ordinal), "captains have no MCP tools to call");
                        AssertFalse(brief.Contains("armada_context_pack", StringComparison.Ordinal), "captains have no MCP tools to call");
                        AssertFalse(brief.Contains("MCP", StringComparison.Ordinal), "no MCP capability may be implied");
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
                        AssertContains("\"OverBudget\":true", payload, "a file over the configured budget must be flagged");
                        AssertEqual(mission.Id, events[0].MissionId, "the event must be attributed to the mission");
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

            await RunTest("GenerateClaudeMdAsync writes runtime-specific instruction file", async () =>
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
                        Vessel vessel = new Vessel("CodexVessel", "https://github.com/test/repo");
                        Captain captain = new Captain("CodexCaptain");
                        captain.Runtime = AgentRuntimeEnum.Codex;

                        Mission mission = new Mission();
                        mission.Title = "Implement feature";
                        mission.Description = "Use runtime-specific instruction files.";

                        await service.GenerateClaudeMdAsync(tempDir, mission, vessel, captain);

                        AssertTrue(File.Exists(Path.Combine(tempDir, "CODEX.md")), "Codex missions should write CODEX.md");
                        AssertFalse(File.Exists(Path.Combine(tempDir, "CLAUDE.md")), "Codex missions should not write CLAUDE.md by default");
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

            await RunTest("GenerateClaudeMdAsync includes StyleGuide when set", async () =>
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
                        Vessel vessel = new Vessel("StyleVessel", "https://github.com/test/repo");
                        vessel.StyleGuide = "Use camelCase for variables. Prefer const over let.";

                        Mission mission = new Mission();
                        mission.Title = "Add feature";
                        mission.Description = "Add dark mode toggle.";

                        await service.GenerateClaudeMdAsync(tempDir, mission, vessel);

                        string content = await File.ReadAllTextAsync(Path.Combine(tempDir, "CLAUDE.md"));
                        AssertContains("## Code Style", content);
                        AssertContains("Use camelCase for variables. Prefer const over let.", content);
                    }
                    finally
                    {
                        try { Directory.Delete(tempDir, true); } catch { }
                    }
                }
            });

            await RunTest("GenerateClaudeMdAsync includes both ProjectContext and StyleGuide", async () =>
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
                        Vessel vessel = new Vessel("BothFieldsVessel", "https://github.com/test/repo");
                        vessel.ProjectContext = "Go microservice with gRPC endpoints.";
                        vessel.StyleGuide = "Follow Effective Go guidelines.";

                        Mission mission = new Mission();
                        mission.Title = "Refactor handler";
                        mission.Description = "Refactor the user handler to use middleware.";

                        await service.GenerateClaudeMdAsync(tempDir, mission, vessel);

                        string content = await File.ReadAllTextAsync(Path.Combine(tempDir, "CLAUDE.md"));
                        AssertContains("## Project Context", content);
                        AssertContains("Go microservice with gRPC endpoints.", content);
                        AssertContains("## Code Style", content);
                        AssertContains("Follow Effective Go guidelines.", content);
                        AssertContains("# Mission Instructions", content);
                    }
                    finally
                    {
                        try { Directory.Delete(tempDir, true); } catch { }
                    }
                }
            });

            await RunTest("GenerateClaudeMdAsync omits ProjectContext section when null", async () =>
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
                        Vessel vessel = new Vessel("NoContextVessel", "https://github.com/test/repo");

                        Mission mission = new Mission();
                        mission.Title = "Simple task";
                        mission.Description = "Do something.";

                        await service.GenerateClaudeMdAsync(tempDir, mission, vessel);

                        string content = await File.ReadAllTextAsync(Path.Combine(tempDir, "CLAUDE.md"));
                        AssertFalse(content.Contains("## Project Context"), "Should not contain Project Context section when null");
                        AssertFalse(content.Contains("## Code Style"), "Should not contain Code Style section when null");
                        AssertContains("# Mission Instructions", content);
                    }
                    finally
                    {
                        try { Directory.Delete(tempDir, true); } catch { }
                    }
                }
            });

            await RunTest("GenerateClaudeMdAsync omits sections when empty string", async () =>
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
                        Vessel vessel = new Vessel("EmptyContextVessel", "https://github.com/test/repo");
                        vessel.ProjectContext = "";
                        vessel.StyleGuide = "";

                        Mission mission = new Mission();
                        mission.Title = "Empty context task";
                        mission.Description = "Task with empty context fields.";

                        await service.GenerateClaudeMdAsync(tempDir, mission, vessel);

                        string content = await File.ReadAllTextAsync(Path.Combine(tempDir, "CLAUDE.md"));
                        AssertFalse(content.Contains("## Project Context"), "Should not contain Project Context section when empty");
                        AssertFalse(content.Contains("## Code Style"), "Should not contain Code Style section when empty");
                        AssertContains("# Mission Instructions", content);
                    }
                    finally
                    {
                        try { Directory.Delete(tempDir, true); } catch { }
                    }
                }
            });

            await RunTest("GenerateClaudeMdAsync ProjectContext appears before Mission Instructions", async () =>
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
                        Vessel vessel = new Vessel("OrderVessel", "https://github.com/test/repo");
                        vessel.ProjectContext = "Order test context";
                        vessel.StyleGuide = "Order test style";

                        Mission mission = new Mission();
                        mission.Title = "Order test";
                        mission.Description = "Test ordering.";

                        await service.GenerateClaudeMdAsync(tempDir, mission, vessel);

                        string content = await File.ReadAllTextAsync(Path.Combine(tempDir, "CLAUDE.md"));
                        int contextIndex = content.IndexOf("## Project Context");
                        int styleIndex = content.IndexOf("## Code Style");
                        int missionIndex = content.IndexOf("# Mission Instructions");

                        AssertTrue(contextIndex >= 0, "Project Context should exist");
                        AssertTrue(styleIndex >= 0, "Code Style should exist");
                        AssertTrue(missionIndex >= 0, "Mission Instructions should exist");
                        AssertTrue(contextIndex < styleIndex, "Project Context should appear before Code Style");
                        AssertTrue(styleIndex < missionIndex, "Code Style should appear before Mission Instructions");
                    }
                    finally
                    {
                        try { Directory.Delete(tempDir, true); } catch { }
                    }
                }
            });

            await RunTest("GenerateClaudeMdAsync includes ModelContext when enabled and set", async () =>
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
                        Vessel vessel = new Vessel("ModelContextVessel", "https://github.com/test/repo");
                        vessel.EnableModelContext = true;
                        vessel.ModelContext = "The test suite takes 4 minutes. Auth module was recently refactored.";

                        Mission mission = new Mission();
                        mission.Title = "Fix tests";
                        mission.Description = "Fix broken integration tests.";

                        await service.GenerateClaudeMdAsync(tempDir, mission, vessel);

                        string content = await File.ReadAllTextAsync(Path.Combine(tempDir, "CLAUDE.md"));
                        AssertContains("## Model Context", content);
                        AssertContains("The test suite takes 4 minutes.", content);
                        AssertContains("## Learned-Fact Proposals", content);
                        AssertContains("[LEARNED-FACT-PROPOSAL]", content);
                        AssertContains("read-only background", content);
                        AssertFalse(content.Contains("COMPLETE updated model context"), "Prompt must not ask captains to append raw ModelContext");
                    }
                    finally
                    {
                        try { Directory.Delete(tempDir, true); } catch { }
                    }
                }
            });

            await RunTest("GenerateClaudeMdAsync_LearnedFactsDisabled_OmitsProposalAndLearnedPlaybook", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    settings.LearnedFactsEnabled = false;
                    StubGitService git = new StubGitService();
                    MissionService service = CreateMissionService(logging, testDb.Driver, settings, git);

                    string tempDir = Path.Combine(Path.GetTempPath(), "armada_prompt_test_" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(tempDir);

                    try
                    {
                        Vessel vessel = new Vessel("DisabledLearnedFactsVessel", "https://github.com/test/repo");
                        vessel.EnableModelContext = true;
                        vessel.ModelContext = "Legacy context remains readable.";

                        Mission mission = new Mission("Task", "Do something.");
                        mission.PlaybookSnapshots = new List<MissionPlaybookSnapshot>
                        {
                            new MissionPlaybookSnapshot
                            {
                                FileName = "vessel-disabled-learned.md",
                                Content = "# Learned\n\nSecret learned guidance that must be disabled."
                            },
                            new MissionPlaybookSnapshot
                            {
                                FileName = "normal-playbook.md",
                                Content = "# Normal\n\nNormal playbook guidance remains enabled."
                            }
                        };

                        await service.GenerateClaudeMdAsync(tempDir, mission, vessel);

                        string content = await File.ReadAllTextAsync(Path.Combine(tempDir, "CLAUDE.md"));
                        AssertContains("Normal playbook guidance remains enabled.", content);
                        AssertFalse(content.Contains("## Model Context"), "global disable must suppress legacy model context");
                        AssertFalse(content.Contains("Legacy context remains readable."), "global disable must suppress legacy learned facts");
                        AssertFalse(content.Contains("## Learned-Fact Proposals"), "global disable must suppress proposal routing");
                        AssertFalse(content.Contains("Secret learned guidance"), "global disable must suppress learned playbooks");
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
                    settings.LearnedFactsEnabled = false;
                    StubGitService git = new StubGitService();
                    MissionService service = CreateMissionService(logging, testDb.Driver, settings, git);

                    string tempDir = Path.Combine(Path.GetTempPath(), "armada_prompt_test_" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(tempDir);

                    try
                    {
                        Vessel vessel = new Vessel("OnlyLearnedPlaybookVessel", "https://github.com/test/repo");

                        // The only attached playbook is a learned-fact one, which the renderer drops
                        // while learned facts are disabled. The wrapper calls its content required
                        // reading, so an empty wrapper points the captain at material it never got.
                        Mission mission = new Mission("Task", "Do something.");
                        mission.PlaybookSnapshots = new List<MissionPlaybookSnapshot>
                        {
                            new MissionPlaybookSnapshot
                            {
                                FileName = "vessel-onlylearned-learned.md",
                                Content = "# Learned\n\nSecret learned guidance that must be disabled."
                            }
                        };

                        await service.GenerateClaudeMdAsync(tempDir, mission, vessel);

                        string content = await File.ReadAllTextAsync(Path.Combine(tempDir, "CLAUDE.md"));
                        AssertFalse(content.Contains("Secret learned guidance"), "the learned playbook body must be suppressed");
                        AssertFalse(content.Contains("## Playbooks"), "an empty playbook wrapper must not be emitted");
                    }
                    finally
                    {
                        try { Directory.Delete(tempDir, true); } catch { }
                    }
                }
            });

            await RunTest("GenerateClaudeMdAsync omits ModelContext when disabled", async () =>
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
                        Vessel vessel = new Vessel("DisabledModelContextVessel", "https://github.com/test/repo");
                        vessel.EnableModelContext = false;
                        vessel.ModelContext = "This should not appear.";

                        Mission mission = new Mission();
                        mission.Title = "Task";
                        mission.Description = "Do something.";

                        await service.GenerateClaudeMdAsync(tempDir, mission, vessel);

                        string content = await File.ReadAllTextAsync(Path.Combine(tempDir, "CLAUDE.md"));
                        AssertFalse(content.Contains("## Model Context"), "Should not contain Model Context when disabled");
                        AssertFalse(content.Contains("## Learned-Fact Proposals"), "Should not contain learned-fact proposal instructions when disabled");
                    }
                    finally
                    {
                        try { Directory.Delete(tempDir, true); } catch { }
                    }
                }
            });

            await RunTest("GenerateClaudeMdAsync includes update instructions even when ModelContext is empty", async () =>
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
                        Vessel vessel = new Vessel("EmptyModelContextVessel", "https://github.com/test/repo");
                        vessel.EnableModelContext = true;
                        vessel.ModelContext = null;

                        Mission mission = new Mission();
                        mission.Title = "First mission";
                        mission.Description = "First mission on this vessel.";

                        await service.GenerateClaudeMdAsync(tempDir, mission, vessel);

                        string content = await File.ReadAllTextAsync(Path.Combine(tempDir, "CLAUDE.md"));
                        AssertFalse(content.Contains("## Model Context\n"), "Should not contain Model Context section when null");
                        AssertContains("## Learned-Fact Proposals", content);
                        AssertContains("[LEARNED-FACT-PROPOSAL]", content);
                        AssertFalse(content.Contains("COMPLETE updated model context"), "Prompt must not ask captains to append raw ModelContext");
                    }
                    finally
                    {
                        try { Directory.Delete(tempDir, true); } catch { }
                    }
                }
            });

            await RunTest("Template-resolved CLAUDE.md contains mission rules", async () =>
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
                        Vessel vessel = new Vessel("TemplateRulesVessel", "https://github.com/test/repo");

                        Mission mission = new Mission();
                        mission.Title = "Template rules test";
                        mission.Description = "Verify rules section from templates.";

                        await service.GenerateClaudeMdAsync(tempDir, mission, vessel);

                        string content = await File.ReadAllTextAsync(Path.Combine(tempDir, "CLAUDE.md"));
                        AssertContains("## Rules", content);
                    }
                    finally
                    {
                        try { Directory.Delete(tempDir, true); } catch { }
                    }
                }
            });

            await RunTest("Template-resolved CLAUDE.md contains structured result and verdict markers", async () =>
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
                        Vessel vessel = new Vessel("SignalPromptVessel", "https://github.com/test/repo");

                        Mission mission = new Mission();
                        mission.Title = "Judge signal test";
                        mission.Description = "Verify structured output markers are present.";
                        mission.Persona = "Judge";

                        await service.GenerateClaudeMdAsync(tempDir, mission, vessel);

                        string content = await File.ReadAllTextAsync(Path.Combine(tempDir, "CLAUDE.md"));
                        AssertContains("[ARMADA:RESULT] COMPLETE", content);
                        AssertContains("[ARMADA:VERDICT] PASS", content);
                        AssertContains("standalone", content.ToLowerInvariant());
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

            await RunTest("GenerateClaudeMdAsync appends judge output contract after custom captain instructions", async () =>
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
                        Vessel vessel = new Vessel("JudgeCaptainPromptVessel", "https://github.com/test/repo");

                        Mission mission = new Mission();
                        mission.Title = "Judge custom instruction contract test";
                        mission.Description = "Verify custom judge instructions still include the required structured output contract.";
                        mission.Persona = "Judge";

                        Captain captain = new Captain("judge-captain");
                        captain.Runtime = Armada.Core.Enums.AgentRuntimeEnum.ClaudeCode;
                        captain.SystemInstructions = "End with exactly one standalone verdict line and give a brief explanation.";

                        await service.GenerateClaudeMdAsync(tempDir, mission, vessel, captain);

                        string content = await File.ReadAllTextAsync(Path.Combine(tempDir, "CLAUDE.md"));
                        AssertContains("## Captain Instructions", content, "Custom captain instructions should still be included");
                        AssertContains("brief explanation", content, "Original captain instruction text should be preserved");
                        AssertContains("## Required Output Contract", content, "Generated instructions should append a structured output contract");
                        AssertContains("## Completeness", content, "Judge output contract should require Completeness");
                        AssertContains("## Failure Modes", content, "Judge output contract should require Failure Modes");
                        AssertContains("[ARMADA:VERDICT] PASS", content, "Judge output contract should preserve the standalone verdict signal");
                    }
                    finally
                    {
                        try { Directory.Delete(tempDir, true); } catch { }
                    }
                }
            });

            await RunTest("Template-resolved CLAUDE.md contains persona prompt", async () =>
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
                        Vessel vessel = new Vessel("PersonaVessel", "https://github.com/test/repo");

                        Mission mission = new Mission();
                        mission.Title = "Architect persona test";
                        mission.Description = "Verify architect persona prompt.";
                        mission.Persona = "Architect";

                        await service.GenerateClaudeMdAsync(tempDir, mission, vessel);

                        string content = await File.ReadAllTextAsync(Path.Combine(tempDir, "CLAUDE.md"));
                        AssertTrue(
                            content.Contains("decompose") || content.Contains("analyze"),
                            "Architect persona should contain 'decompose' or 'analyze'");
                    }
                    finally
                    {
                        try { Directory.Delete(tempDir, true); } catch { }
                    }
                }
            });

            await RunTest("Template-resolved CLAUDE.md contains model context updates when enabled", async () =>
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
                        Vessel vessel = new Vessel("TemplateModelContextVessel", "https://github.com/test/repo");
                        vessel.EnableModelContext = true;
                        vessel.ModelContext = "The auth module was recently refactored to use JWT tokens.";

                        Mission mission = new Mission();
                        mission.Title = "Model context test";
                        mission.Description = "Verify model context section from templates.";

                        await service.GenerateClaudeMdAsync(tempDir, mission, vessel);

                        string content = await File.ReadAllTextAsync(Path.Combine(tempDir, "CLAUDE.md"));
                        AssertContains("## Learned-Fact Proposals", content);
                        AssertContains("[LEARNED-FACT-PROPOSAL]", content);
                        AssertContains("The auth module was recently refactored to use JWT tokens.", content);
                    }
                    finally
                    {
                        try { Directory.Delete(tempDir, true); } catch { }
                    }
                }
            });

            await RunTest("Template-resolved CLAUDE.md substitutes placeholders", async () =>
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
                        Vessel vessel = new Vessel("PlaceholderTestVessel", "https://github.com/test/repo");

                        Mission mission = new Mission();
                        mission.Title = "Implement user authentication";
                        mission.Description = "Add OAuth2 login flow.";

                        await service.GenerateClaudeMdAsync(tempDir, mission, vessel);

                        string content = await File.ReadAllTextAsync(Path.Combine(tempDir, "CLAUDE.md"));
                        AssertContains("Implement user authentication", content);
                        AssertContains("PlaceholderTestVessel", content);
                        AssertFalse(content.Contains("{MissionTitle}"), "Should not contain literal {MissionTitle} placeholder");
                        AssertFalse(content.Contains("{VesselName}"), "Should not contain literal {VesselName} placeholder");
                    }
                    finally
                    {
                        try { Directory.Delete(tempDir, true); } catch { }
                    }
                }
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

            await RunTest("GenerateClaudeMdAsync omits empty existing instruction wrapper after sanitization", async () =>
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
                            "# Mission Instructions\n" +
                            "\n" +
                            "## Mission\n" +
                            "- **Title:** Generated only\n";
                        await File.WriteAllTextAsync(Path.Combine(tempDir, "CLAUDE.md"), existingInstructions);

                        Vessel vessel = new Vessel("GeneratedOnlyInstructionsVessel", "https://github.com/test/repo");
                        Mission mission = new Mission("Fresh mission", "Fresh description.");

                        await service.GenerateClaudeMdAsync(tempDir, mission, vessel);

                        string content = await File.ReadAllTextAsync(Path.Combine(tempDir, ".armada", "instructions", "CLAUDE.md"));
                        AssertFalse(content.Contains("## Existing Project Instructions"), "Empty sanitized instructions should not be wrapped");
                    }
                    finally
                    {
                        try { Directory.Delete(tempDir, true); } catch { }
                    }
                }
            });

            await RunTest("Shared launch prompt builder produces compact prompt and defers to runtime instruction file", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    IPromptTemplateService templateService = new PromptTemplateService(testDb.Driver, logging);
                    await templateService.SeedDefaultsAsync();

                    Vessel vessel = new Vessel("LaunchPromptVessel", "https://github.com/test/repo");
                    vessel.ProjectContext = "Service-oriented C# backend.";
                    vessel.StyleGuide = "Prefer explicit types.";
                    vessel.EnableModelContext = true;
                    vessel.ModelContext = "Background jobs are scheduled from ArmadaServer.";

                    Captain captain = new Captain("prompt-captain");
                    captain.Runtime = AgentRuntimeEnum.Codex;
                    captain.SystemInstructions = "Be concise and careful.";

                    Mission mission = new Mission("Write tests", "Add unit tests for the service layer.");
                    mission.Persona = "Test Engineer";
                    mission.BranchName = "armada/prompt-captain/msn_test";

                    Dock dock = new Dock(vessel.Id);
                    dock.BranchName = mission.BranchName;
                    dock.WorktreePath = Path.Combine(Path.GetTempPath(), "armada_prompt_launch_" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(dock.WorktreePath);
                    await File.WriteAllTextAsync(Path.Combine(dock.WorktreePath, "CODEX.md"), "# mission instructions\n");

                    string prompt = await MissionPromptBuilder.BuildLaunchPromptAsync(
                        mission, vessel, captain, dock, templateService).ConfigureAwait(false);

                    AssertContains("test engineer", prompt.ToLowerInvariant());
                    AssertContains("## Coverage Added", prompt);
                    AssertContains("[ARMADA:RESULT] COMPLETE", prompt);
                    AssertContains("Write tests", prompt);
                    AssertContains("CODEX.md", prompt);
                    AssertContains("After reading it, perform the mission now", prompt);
                    AssertContains("do not stop after acknowledging or summarizing the instructions", prompt);
                    AssertFalse(prompt.Contains("if it exists", StringComparison.OrdinalIgnoreCase), "Launch prompt must not tell the captain to probe a missing fallback path");
                    AssertFalse(prompt.Contains("CLAUDE.md"), "Non-Claude runtimes should not be pointed at CLAUDE.md");
                    AssertFalse(prompt.Contains("Be concise and careful."), "Launch prompt should defer captain instructions to the runtime instruction file");
                    AssertFalse(prompt.Contains("Service-oriented C# backend."), "Launch prompt should defer project context to the runtime instruction file");
                    AssertFalse(prompt.Contains("Prefer explicit types."), "Launch prompt should defer style guide to the runtime instruction file");
                    AssertFalse(prompt.Contains("Background jobs are scheduled from ArmadaServer."), "Launch prompt should defer model context to the runtime instruction file");
                    try { Directory.Delete(dock.WorktreePath, true); } catch { }
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

            await RunTest("Shared launch prompt builder caps oversized prompts", async () =>
            {                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    IPromptTemplateService templateService = new PromptTemplateService(testDb.Driver, logging);
                    await templateService.SeedDefaultsAsync();

                    Vessel vessel = new Vessel("LargePromptVessel", "https://github.com/test/repo");
                    vessel.ProjectContext = new string('P', 5000);
                    vessel.StyleGuide = new string('S', 5000);
                    vessel.EnableModelContext = true;
                    vessel.ModelContext = new string('M', 5000);

                    Captain captain = new Captain("large-prompt-captain");
                    captain.Runtime = AgentRuntimeEnum.Gemini;
                    captain.SystemInstructions = new string('I', 2000);

                    Mission mission = new Mission("Large mission", new string('D', 20000));
                    mission.Persona = "Architect";
                    mission.BranchName = "armada/large-prompt";

                    Dock dock = new Dock(vessel.Id);
                    dock.BranchName = mission.BranchName;

                    string prompt = await MissionPromptBuilder.BuildLaunchPromptAsync(
                        mission, vessel, captain, dock, templateService).ConfigureAwait(false);

                    AssertTrue(prompt.Length <= 6000, "Launch prompt should stay under the hard cap");
                    AssertContains("GEMINI.md", prompt);
                    AssertContains("Large mission", prompt);
                }
            });

            await RunTest("Architect launch prompt explicitly requires ARMADA mission markers", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    IPromptTemplateService templateService = new PromptTemplateService(testDb.Driver, logging);
                    await templateService.SeedDefaultsAsync();

                    Vessel vessel = new Vessel("ArchitectVessel", "https://github.com/test/repo");
                    Captain captain = new Captain("architect-captain");
                    captain.Runtime = AgentRuntimeEnum.ClaudeCode;

                    Mission mission = new Mission("Plan work", "Break this objective into missions.");
                    mission.Persona = "Architect";
                    mission.BranchName = "armada/architect";

                    Dock dock = new Dock(vessel.Id);
                    dock.BranchName = mission.BranchName;

                    string prompt = await MissionPromptBuilder.BuildLaunchPromptAsync(
                        mission, vessel, captain, dock, templateService).ConfigureAwait(false);

                    AssertContains("[ARMADA:MISSION]", prompt);
                    AssertContains("Do not ask for more input.", prompt);
                    AssertContains("respond only with real [ARMADA:MISSION] blocks", prompt);
                }
            });

            await RunTest("Judge launch prompt repeats structured verdict contract", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    IPromptTemplateService templateService = new PromptTemplateService(testDb.Driver, logging);
                    await templateService.SeedDefaultsAsync();

                    Vessel vessel = new Vessel("JudgeLaunchVessel", "https://github.com/test/repo");
                    Captain captain = new Captain("judge-launch-captain");
                    captain.Runtime = AgentRuntimeEnum.ClaudeCode;
                    captain.SystemInstructions = "End with exactly one standalone verdict line and give a brief explanation.";

                    Mission mission = new Mission("Review work", "Assess the submitted change.");
                    mission.Persona = "Judge";
                    mission.BranchName = "armada/judge-launch";

                    Dock dock = new Dock(vessel.Id);
                    dock.BranchName = mission.BranchName;

                    string prompt = await MissionPromptBuilder.BuildLaunchPromptAsync(
                        mission, vessel, captain, dock, templateService).ConfigureAwait(false);

                    AssertContains("## Completeness", prompt);
                    AssertContains("## Failure Modes", prompt);
                    AssertContains("[ARMADA:VERDICT] PASS", prompt);
                    AssertContains("follow it exactly", prompt);
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
    }
}
