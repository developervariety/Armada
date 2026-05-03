namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Server.Mcp.Tools;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// Tests for McpArchitectTools: armada_decompose_plan and armada_parse_architect_output.
    /// </summary>
    public class McpArchitectToolsTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "MCP Architect Tools";

        /// <summary>Run all MCP architect tool tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("DecomposePlan_ValidArgs_DispatchesArchitectVoyage", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(new Vessel("arch-1", "https://github.com/test/repo.git")).ConfigureAwait(false);
                    string specFile = Path.GetTempFileName();
                    string claudeFile = Path.GetTempFileName();
                    try
                    {
                        File.WriteAllText(specFile, "# Test Spec");
                        File.WriteAllText(claudeFile, "# Project CLAUDE.md");

                        string? origEnv = Environment.GetEnvironmentVariable("ARMADA_PROJECT_CLAUDE_MD");
                        Environment.SetEnvironmentVariable("ARMADA_PROJECT_CLAUDE_MD", claudeFile);
                        try
                        {
                            RecordingAdmiralService admiralDouble = new RecordingAdmiralService(testDb.Driver);
                            Func<JsonElement?, Task<object>>? decomposeHandler = null;
                            McpArchitectTools.Register(
                                (name, _, _, handler) => { if (name == "armada_decompose_plan") decomposeHandler = handler; },
                                testDb.Driver,
                                new ArchitectOutputParser(),
                                admiralDouble);
                            AssertNotNull(decomposeHandler);

                            JsonElement args = JsonSerializer.SerializeToElement(new { specPath = specFile, vesselId = vessel.Id });
                            object result = await decomposeHandler!(args).ConfigureAwait(false);
                            string resultJson = JsonSerializer.Serialize(result);

                            AssertFalse(resultJson.Contains("\"Error\""), "Should not return error: " + resultJson);
                            AssertContains("voyageId", resultJson);
                            AssertContains("architectMissionId", resultJson);

                            AssertTrue(admiralDouble.Dispatched.Count == 1, "Should have dispatched one mission description");
                            MissionDescription dispatched = admiralDouble.Dispatched[0];
                            AssertEqual("claude-opus-4-7", dispatched.PreferredModel);
                            AssertNotNull(dispatched.PrestagedFiles);

                            bool hasSpec = false;
                            bool hasClaude = false;
                            foreach (PrestagedFile pf in dispatched.PrestagedFiles!)
                            {
                                if (pf.DestPath == "_briefing/spec.md") hasSpec = true;
                                if (pf.DestPath == "_briefing/PROJECT-CLAUDE.md") hasClaude = true;
                            }
                            AssertTrue(hasSpec, "PrestagedFiles should contain _briefing/spec.md");
                            AssertTrue(hasClaude, "PrestagedFiles should contain _briefing/PROJECT-CLAUDE.md");

                            JsonDocument resultDoc = JsonDocument.Parse(resultJson);
                            string voyageId = resultDoc.RootElement.GetProperty("voyageId").GetString()!;
                            List<Mission> voyageMissions = await testDb.Driver.Missions.EnumerateByVoyageAsync(voyageId).ConfigureAwait(false);
                            AssertTrue(voyageMissions.Count > 0, "Mission should exist in DB");
                            AssertEqual("Architect", voyageMissions[0].Persona);
                        }
                        finally
                        {
                            if (origEnv == null)
                                Environment.SetEnvironmentVariable("ARMADA_PROJECT_CLAUDE_MD", null);
                            else
                                Environment.SetEnvironmentVariable("ARMADA_PROJECT_CLAUDE_MD", origEnv);
                        }
                    }
                    finally
                    {
                        if (File.Exists(specFile)) File.Delete(specFile);
                        if (File.Exists(claudeFile)) File.Delete(claudeFile);
                    }
                }
            });

            await RunTest("DecomposePlan_MissingSpecFile_ReturnsError", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    RecordingAdmiralService admiralDouble = new RecordingAdmiralService(testDb.Driver);
                    Func<JsonElement?, Task<object>>? decomposeHandler = null;
                    McpArchitectTools.Register(
                        (name, _, _, handler) => { if (name == "armada_decompose_plan") decomposeHandler = handler; },
                        testDb.Driver,
                        new ArchitectOutputParser(),
                        admiralDouble);
                    AssertNotNull(decomposeHandler);

                    JsonElement args = JsonSerializer.SerializeToElement(new { specPath = "/nonexistent/path/spec.md", vesselId = "vsl_test" });
                    object result = await decomposeHandler!(args).ConfigureAwait(false);
                    string resultJson = JsonSerializer.Serialize(result);
                    AssertContains("does not exist", resultJson);
                }
            });

            await RunTest("DecomposePlan_CustomPreferredModel_RespectsInput", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(new Vessel("arch-3", "https://github.com/test/repo.git")).ConfigureAwait(false);
                    string specFile = Path.GetTempFileName();
                    string claudeFile = Path.GetTempFileName();
                    try
                    {
                        File.WriteAllText(specFile, "# Test Spec");
                        File.WriteAllText(claudeFile, "# Project CLAUDE.md");

                        string? origEnv = Environment.GetEnvironmentVariable("ARMADA_PROJECT_CLAUDE_MD");
                        Environment.SetEnvironmentVariable("ARMADA_PROJECT_CLAUDE_MD", claudeFile);
                        try
                        {
                            RecordingAdmiralService admiralDouble = new RecordingAdmiralService(testDb.Driver);
                            Func<JsonElement?, Task<object>>? decomposeHandler = null;
                            McpArchitectTools.Register(
                                (name, _, _, handler) => { if (name == "armada_decompose_plan") decomposeHandler = handler; },
                                testDb.Driver,
                                new ArchitectOutputParser(),
                                admiralDouble);

                            JsonElement args = JsonSerializer.SerializeToElement(new { specPath = specFile, vesselId = vessel.Id, preferredModel = "gpt-5.5" });
                            await decomposeHandler!(args).ConfigureAwait(false);

                            AssertTrue(admiralDouble.Dispatched.Count == 1, "Should have dispatched one mission description");
                            AssertEqual("gpt-5.5", admiralDouble.Dispatched[0].PreferredModel);
                        }
                        finally
                        {
                            if (origEnv == null)
                                Environment.SetEnvironmentVariable("ARMADA_PROJECT_CLAUDE_MD", null);
                            else
                                Environment.SetEnvironmentVariable("ARMADA_PROJECT_CLAUDE_MD", origEnv);
                        }
                    }
                    finally
                    {
                        if (File.Exists(specFile)) File.Delete(specFile);
                        if (File.Exists(claudeFile)) File.Delete(claudeFile);
                    }
                }
            });

            await RunTest("ParseArchitectOutput_ValidOutput_ReturnsValidVerdict", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(new Vessel("arch-4", "https://github.com/test/repo.git")).ConfigureAwait(false);
                    Voyage voyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("test voyage", "desc")).ConfigureAwait(false);
                    Mission mission = new Mission("test mission", "desc");
                    mission.VoyageId = voyage.Id;
                    mission.VesselId = vessel.Id;
                    mission.Status = MissionStatusEnum.WorkProduced;
                    mission.AgentOutput = BuildValidArchitectOutput(2);
                    mission = await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                    RecordingAdmiralService admiralDouble = new RecordingAdmiralService(testDb.Driver);
                    Func<JsonElement?, Task<object>>? parseHandler = null;
                    McpArchitectTools.Register(
                        (name, _, _, handler) => { if (name == "armada_parse_architect_output") parseHandler = handler; },
                        testDb.Driver,
                        new ArchitectOutputParser(),
                        admiralDouble);
                    AssertNotNull(parseHandler);

                    JsonElement args = JsonSerializer.SerializeToElement(new { missionId = mission.Id });
                    object result = await parseHandler!(args).ConfigureAwait(false);
                    string resultJson = JsonSerializer.Serialize(result);

                    ArchitectParseResult parsed = JsonSerializer.Deserialize<ArchitectParseResult>(
                        resultJson,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
                    AssertEqual(ArchitectParseVerdict.Valid, parsed.Verdict);
                    AssertEqual(2, parsed.Missions.Count);
                }
            });

            await RunTest("ParseArchitectOutput_MissionNotWorkProduced_ReturnsError", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(new Vessel("arch-5", "https://github.com/test/repo.git")).ConfigureAwait(false);
                    Voyage voyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("test voyage", "desc")).ConfigureAwait(false);
                    Mission mission = new Mission("test mission", "desc");
                    mission.VoyageId = voyage.Id;
                    mission.VesselId = vessel.Id;
                    mission.Status = MissionStatusEnum.InProgress;
                    mission = await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                    RecordingAdmiralService admiralDouble = new RecordingAdmiralService(testDb.Driver);
                    Func<JsonElement?, Task<object>>? parseHandler = null;
                    McpArchitectTools.Register(
                        (name, _, _, handler) => { if (name == "armada_parse_architect_output") parseHandler = handler; },
                        testDb.Driver,
                        new ArchitectOutputParser(),
                        admiralDouble);

                    JsonElement args = JsonSerializer.SerializeToElement(new { missionId = mission.Id });
                    object result = await parseHandler!(args).ConfigureAwait(false);
                    string resultJson = JsonSerializer.Serialize(result);

                    AssertContains("Error", resultJson);
                    AssertContains("InProgress", resultJson);
                }
            });

            await RunTest("DecomposePlan_VesselWithDefaultPlaybooks_MergesPlaybooksIntoVoyage", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(new Vessel("arch-dp", "https://github.com/test/repo.git")).ConfigureAwait(false);
                    string defaultPlaybooksJson = "[{\"playbookId\":\"pbk_default1\",\"deliveryMode\":\"InlineFullContent\"},{\"playbookId\":\"pbk_default2\",\"deliveryMode\":\"AttachIntoWorktree\"}]";
                    vessel.DefaultPlaybooks = defaultPlaybooksJson;
                    vessel = await testDb.Driver.Vessels.UpdateAsync(vessel).ConfigureAwait(false);

                    string specFile = Path.GetTempFileName();
                    string claudeFile = Path.GetTempFileName();
                    try
                    {
                        File.WriteAllText(specFile, "# Test Spec");
                        File.WriteAllText(claudeFile, "# Project CLAUDE.md");

                        string? origEnv = Environment.GetEnvironmentVariable("ARMADA_PROJECT_CLAUDE_MD");
                        Environment.SetEnvironmentVariable("ARMADA_PROJECT_CLAUDE_MD", claudeFile);
                        try
                        {
                            RecordingAdmiralService admiralDouble = new RecordingAdmiralService(testDb.Driver);
                            Func<JsonElement?, Task<object>>? decomposeHandler = null;
                            McpArchitectTools.Register(
                                (name, _, _, handler) => { if (name == "armada_decompose_plan") decomposeHandler = handler; },
                                testDb.Driver,
                                new ArchitectOutputParser(),
                                admiralDouble);
                            AssertNotNull(decomposeHandler);

                            // Caller passes empty selectedPlaybooks -- defaults should be applied.
                            JsonElement args = JsonSerializer.SerializeToElement(new { specPath = specFile, vesselId = vessel.Id });
                            object result = await decomposeHandler!(args).ConfigureAwait(false);
                            string resultJson = JsonSerializer.Serialize(result);

                            AssertFalse(resultJson.Contains("\"Error\""), "Should not return error: " + resultJson);
                            AssertNotNull(admiralDouble.LastDispatchedPlaybooks, "Playbooks should be passed to DispatchVoyageAsync");
                            AssertEqual(2, admiralDouble.LastDispatchedPlaybooks!.Count, "Both vessel defaults should appear in merged list");
                            AssertEqual("pbk_default1", admiralDouble.LastDispatchedPlaybooks[0].PlaybookId);
                            AssertEqual("pbk_default2", admiralDouble.LastDispatchedPlaybooks[1].PlaybookId);
                        }
                        finally
                        {
                            if (origEnv == null)
                                Environment.SetEnvironmentVariable("ARMADA_PROJECT_CLAUDE_MD", null);
                            else
                                Environment.SetEnvironmentVariable("ARMADA_PROJECT_CLAUDE_MD", origEnv);
                        }
                    }
                    finally
                    {
                        if (File.Exists(specFile)) File.Delete(specFile);
                        if (File.Exists(claudeFile)) File.Delete(claudeFile);
                    }
                }
            });

            await RunTest("ParseArchitectOutput_BlockedVerdict_ReturnsQuestions", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(new Vessel("arch-6", "https://github.com/test/repo.git")).ConfigureAwait(false);
                    Voyage voyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("test voyage", "desc")).ConfigureAwait(false);
                    Mission mission = new Mission("test mission", "desc");
                    mission.VoyageId = voyage.Id;
                    mission.VesselId = vessel.Id;
                    mission.Status = MissionStatusEnum.WorkProduced;
                    mission.AgentOutput = "Some plan preamble.\n[ARMADA:RESULT] BLOCKED\n- Q1: What is the scope?\n- Q2: Which database?\n";
                    mission = await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                    RecordingAdmiralService admiralDouble = new RecordingAdmiralService(testDb.Driver);
                    Func<JsonElement?, Task<object>>? parseHandler = null;
                    McpArchitectTools.Register(
                        (name, _, _, handler) => { if (name == "armada_parse_architect_output") parseHandler = handler; },
                        testDb.Driver,
                        new ArchitectOutputParser(),
                        admiralDouble);

                    JsonElement args = JsonSerializer.SerializeToElement(new { missionId = mission.Id });
                    object result = await parseHandler!(args).ConfigureAwait(false);
                    string resultJson = JsonSerializer.Serialize(result);

                    ArchitectParseResult parsed = JsonSerializer.Deserialize<ArchitectParseResult>(
                        resultJson,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
                    AssertEqual(ArchitectParseVerdict.Blocked, parsed.Verdict);
                    AssertTrue(parsed.BlockedQuestions.Count == 2, "Should have 2 blocked questions");
                    AssertContains("Q1", parsed.BlockedQuestions[0]);
                    AssertContains("Q2", parsed.BlockedQuestions[1]);
                }
            });
        }

        private static string BuildValidArchitectOutput(int missionCount)
        {
            string plan = "# Test Plan\n\n" +
                          "**Goal:** Test the architect output parser.\n" +
                          "**Architecture:** Simple test.\n" +
                          "**Tech Stack:** C# .NET\n\n" +
                          "## File structure\n\n" +
                          "| File | Responsibility | New/Modify |\n" +
                          "|---|---|---|\n" +
                          "| TestFile.cs | test | New |\n\n" +
                          "## Task dispatch graph\n\nM1 -> M2\n\n";

            string missions = "";
            for (int i = 1; i <= missionCount; i++)
            {
                string dep = i > 1 ? "M" + (i - 1) : "";
                missions += "[ARMADA:MISSION]\n";
                missions += "id: M" + i + "\n";
                missions += "title: feat(test): M" + i + " -- task " + i + "\n";
                missions += "preferredModel: mid\n";
                missions += "dependsOnMissionId: " + dep + "\n";
                missions += "description: |\n";
                missions += "  Do task " + i + ".\n";
                missions += "[ARMADA:MISSION-END]\n\n";
            }
            return plan + missions;
        }

        private sealed class RecordingAdmiralService : IAdmiralService
        {
            private readonly DatabaseDriver _Database;

            public List<MissionDescription> Dispatched { get; } = new List<MissionDescription>();

            /// <summary>Playbooks passed in the most recent DispatchVoyageAsync call.</summary>
            public List<SelectedPlaybook>? LastDispatchedPlaybooks { get; private set; }

            public Func<Captain, Mission, Dock, Task<int>>? OnLaunchAgent { get; set; }
            public Func<Captain, Task>? OnStopAgent { get; set; }
            public Func<Mission, Dock, Task>? OnCaptureDiff { get; set; }
            public Func<Mission, Dock, Task>? OnMissionComplete { get; set; }
            public Func<Voyage, Task>? OnVoyageComplete { get; set; }
            public Func<Mission, Task<bool>>? OnReconcilePullRequest { get; set; }
            public Func<Task<int>>? OnReconcileMergeEntries { get; set; }
            public Func<int, bool>? OnIsProcessExitHandled { get; set; }

            public RecordingAdmiralService(DatabaseDriver database)
            {
                _Database = database;
            }

            public async Task<Voyage> DispatchVoyageAsync(
                string title,
                string description,
                string vesselId,
                List<MissionDescription> missionDescriptions,
                CancellationToken token = default)
            {
                Dispatched.AddRange(missionDescriptions);
                Voyage voyage = new Voyage(title, description);
                voyage = await _Database.Voyages.CreateAsync(voyage, token).ConfigureAwait(false);
                foreach (MissionDescription md in missionDescriptions)
                {
                    Mission mission = new Mission(md.Title, md.Description);
                    mission.VoyageId = voyage.Id;
                    mission.VesselId = vesselId;
                    mission.PreferredModel = md.PreferredModel;
                    mission.PrestagedFiles = md.PrestagedFiles != null
                        ? new List<PrestagedFile>(md.PrestagedFiles)
                        : new List<PrestagedFile>();
                    await _Database.Missions.CreateAsync(mission, token).ConfigureAwait(false);
                }
                return voyage;
            }

            public async Task<Voyage> DispatchVoyageAsync(
                string title,
                string description,
                string vesselId,
                List<MissionDescription> missionDescriptions,
                List<SelectedPlaybook>? selectedPlaybooks,
                CancellationToken token = default)
            {
                LastDispatchedPlaybooks = selectedPlaybooks;
                return await DispatchVoyageAsync(title, description, vesselId, missionDescriptions, token).ConfigureAwait(false);
            }

            public Task<Voyage> DispatchVoyageAsync(
                string title,
                string description,
                string vesselId,
                List<MissionDescription> missionDescriptions,
                string? pipelineId,
                CancellationToken token = default)
                => DispatchVoyageAsync(title, description, vesselId, missionDescriptions, token);

            public Task<Voyage> DispatchVoyageAsync(
                string title,
                string description,
                string vesselId,
                List<MissionDescription> missionDescriptions,
                string? pipelineId,
                List<SelectedPlaybook>? selectedPlaybooks,
                CancellationToken token = default)
            {
                LastDispatchedPlaybooks = selectedPlaybooks;
                return DispatchVoyageAsync(title, description, vesselId, missionDescriptions, token);
            }

            public Task<Mission> DispatchMissionAsync(Mission mission, CancellationToken token = default)
                => throw new NotImplementedException();

            public Task<Pipeline?> ResolvePipelineAsync(string? pipelineIdOrName, Vessel vessel, CancellationToken token = default)
                => Task.FromResult<Pipeline?>(null);

            public Task<ArmadaStatus> GetStatusAsync(CancellationToken token = default)
                => throw new NotImplementedException();

            public Task RecallCaptainAsync(string captainId, CancellationToken token = default)
                => throw new NotImplementedException();

            public Task RecallAllAsync(CancellationToken token = default)
                => throw new NotImplementedException();

            public Task HealthCheckAsync(CancellationToken token = default)
                => throw new NotImplementedException();

            public Task CleanupStaleCaptainsAsync(CancellationToken token = default)
                => throw new NotImplementedException();

            public Task HandleProcessExitAsync(
                int processId,
                int? exitCode,
                string captainId,
                string missionId,
                CancellationToken token = default)
                => throw new NotImplementedException();
        }
    }
}
