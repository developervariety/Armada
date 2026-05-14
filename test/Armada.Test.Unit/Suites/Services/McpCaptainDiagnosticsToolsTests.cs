namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using Armada.Server.Mcp.Tools;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// Tests for captain progress diagnostics MCP tooling.
    /// </summary>
    public class McpCaptainDiagnosticsToolsTests : TestSuite
    {
        /// <summary>
        /// Suite name.
        /// </summary>
        public override string Name => "MCP Captain Diagnostics Tools";

        /// <summary>
        /// Run all tests.
        /// </summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("Register_AddsCaptainDiagnosticsTool", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Dictionary<string, Func<JsonElement?, Task<object>>> handlers = RegisterHandlers(testDb.Driver, null);

                    AssertTrue(handlers.ContainsKey("armada_captain_diagnostics"));
                }
            });

            await RunTest("Diagnostics_MissingOrBlankCaptainId_ReturnsValidationErrors", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Dictionary<string, Func<JsonElement?, Task<object>>> handlers = RegisterHandlers(testDb.Driver, null);
                    object missingArgsResult = await handlers["armada_captain_diagnostics"](null).ConfigureAwait(false);
                    JsonElement blankArgs = JsonSerializer.SerializeToElement(new { captainId = " " });

                    object blankResult = await handlers["armada_captain_diagnostics"](blankArgs).ConfigureAwait(false);

                    AssertContains("missing args", JsonSerializer.Serialize(missingArgsResult));
                    AssertContains("captainId is required", JsonSerializer.Serialize(blankResult));
                }
            });

            await RunTest("Diagnostics_UnknownCaptain_ReturnsError", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Dictionary<string, Func<JsonElement?, Task<object>>> handlers = RegisterHandlers(testDb.Driver, null);
                    JsonElement args = JsonSerializer.SerializeToElement(new { captainId = "cpt_missing" });

                    object result = await handlers["armada_captain_diagnostics"](args).ConfigureAwait(false);
                    string resultJson = JsonSerializer.Serialize(result);

                    AssertContains("Captain not found", resultJson);
                }
            });

            await RunTest("Diagnostics_IdleCaptain_ReturnsNoActiveMission", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Captain captain = await testDb.Driver.Captains.CreateAsync(new Captain("idle-diag")).ConfigureAwait(false);
                    Dictionary<string, Func<JsonElement?, Task<object>>> handlers = RegisterHandlers(testDb.Driver, null);
                    JsonElement args = JsonSerializer.SerializeToElement(new { captainId = captain.Id });

                    object result = await handlers["armada_captain_diagnostics"](args).ConfigureAwait(false);
                    string resultJson = JsonSerializer.Serialize(result);

                    AssertContains("\"captainId\":\"" + captain.Id + "\"", resultJson);
                    AssertContains("\"state\":\"Idle\"", resultJson);
                    AssertContains("\"activeMissionId\":null", resultJson);
                    AssertContains("\"dockPath\":null", resultJson);
                    AssertContains("\"hasUncommittedDockChanges\":false", resultJson);
                }
            });

            await RunTest("Diagnostics_WorkingCaptain_ReturnsMissionDockAndElapsedMinutes", async () =>
            {
                string root = Path.Combine(Path.GetTempPath(), "armada_diag_" + Guid.NewGuid().ToString("N"));
                try
                {
                    string repo = await CreateGitRepoAsync(root, false).ConfigureAwait(false);
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        WorkingCaptainFixture fixture = await CreateWorkingCaptainAsync(testDb.Driver, repo).ConfigureAwait(false);
                        Dictionary<string, Func<JsonElement?, Task<object>>> handlers = RegisterHandlers(testDb.Driver, null);
                        JsonElement args = JsonSerializer.SerializeToElement(new { captainId = fixture.Captain.Id });

                        object result = await handlers["armada_captain_diagnostics"](args).ConfigureAwait(false);
                        string resultJson = JsonSerializer.Serialize(result);

                        JsonDocument document = JsonDocument.Parse(resultJson);
                        JsonElement rootElement = document.RootElement;
                        AssertEqual(fixture.Mission.Id, rootElement.GetProperty("activeMissionId").GetString());
                        AssertEqual("working mission", rootElement.GetProperty("activeMissionTitle").GetString());
                        AssertEqual(repo, rootElement.GetProperty("dockPath").GetString());
                        AssertFalse(rootElement.GetProperty("hasUncommittedDockChanges").GetBoolean());
                        AssertTrue(rootElement.GetProperty("elapsedMinutes").GetDouble() >= 9);
                    }
                }
                finally
                {
                    DeleteDirectory(root);
                }
            });

            await RunTest("Diagnostics_MissingDockPath_ReturnsGitStatusError", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    string missingDockPath = Path.Combine(Path.GetTempPath(), "armada_diag_missing_" + Guid.NewGuid().ToString("N"));
                    WorkingCaptainFixture fixture = await CreateWorkingCaptainAsync(testDb.Driver, missingDockPath).ConfigureAwait(false);
                    Dictionary<string, Func<JsonElement?, Task<object>>> handlers = RegisterHandlers(testDb.Driver, null);
                    JsonElement args = JsonSerializer.SerializeToElement(new { captainId = fixture.Captain.Id });

                    object result = await handlers["armada_captain_diagnostics"](args).ConfigureAwait(false);
                    string resultJson = JsonSerializer.Serialize(result);

                    AssertContains("\"activeMissionId\":\"" + fixture.Mission.Id + "\"", resultJson);
                    AssertContains("\"dockGitStatusError\":\"dock path does not exist\"", resultJson);
                    AssertContains("\"hasUncommittedDockChanges\":false", resultJson);
                }
            });

            await RunTest("Diagnostics_DirtyDockAndStaleCodeIndex_ReturnsChangesAndFreshness", async () =>
            {
                string root = Path.Combine(Path.GetTempPath(), "armada_diag_" + Guid.NewGuid().ToString("N"));
                try
                {
                    string repo = await CreateGitRepoAsync(root, true).ConfigureAwait(false);
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        WorkingCaptainFixture fixture = await CreateWorkingCaptainAsync(testDb.Driver, repo).ConfigureAwait(false);
                        RecordingCodeIndexService codeIndex = new RecordingCodeIndexService
                        {
                            Status = new CodeIndexStatus
                            {
                                VesselId = fixture.Vessel.Id,
                                Freshness = "Stale",
                                IndexedCommitSha = "old",
                                CurrentCommitSha = "new"
                            }
                        };
                        Dictionary<string, Func<JsonElement?, Task<object>>> handlers = RegisterHandlers(testDb.Driver, codeIndex);
                        JsonElement args = JsonSerializer.SerializeToElement(new { captainId = fixture.Captain.Id });

                        object result = await handlers["armada_captain_diagnostics"](args).ConfigureAwait(false);
                        string resultJson = JsonSerializer.Serialize(result);

                        AssertContains("\"hasUncommittedDockChanges\":true", resultJson);
                        AssertContains("README.md", resultJson);
                        AssertContains("\"freshness\":\"Stale\"", resultJson);
                        AssertContains("\"isStale\":true", resultJson);
                        AssertEqual(fixture.Vessel.Id, codeIndex.LastStatusVesselId);
                    }
                }
                finally
                {
                    DeleteDirectory(root);
                }
            });

            await RunTest("Diagnostics_FailingCodeIndex_ReturnsErrorFreshness", async () =>
            {
                string root = Path.Combine(Path.GetTempPath(), "armada_diag_" + Guid.NewGuid().ToString("N"));
                try
                {
                    string repo = await CreateGitRepoAsync(root, false).ConfigureAwait(false);
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        WorkingCaptainFixture fixture = await CreateWorkingCaptainAsync(testDb.Driver, repo).ConfigureAwait(false);
                        RecordingCodeIndexService codeIndex = new RecordingCodeIndexService
                        {
                            ThrowOnStatus = true
                        };
                        Dictionary<string, Func<JsonElement?, Task<object>>> handlers = RegisterHandlers(testDb.Driver, codeIndex);
                        JsonElement args = JsonSerializer.SerializeToElement(new { captainId = fixture.Captain.Id });

                        object result = await handlers["armada_captain_diagnostics"](args).ConfigureAwait(false);
                        string resultJson = JsonSerializer.Serialize(result);

                        AssertContains("\"freshness\":\"Error\"", resultJson);
                        AssertContains("\"isStale\":true", resultJson);
                        AssertContains("index failed", resultJson);
                    }
                }
                finally
                {
                    DeleteDirectory(root);
                }
            });
        }

        private static Dictionary<string, Func<JsonElement?, Task<object>>> RegisterHandlers(DatabaseDriver database, ICodeIndexService? codeIndex)
        {
            Dictionary<string, Func<JsonElement?, Task<object>>> handlers = new Dictionary<string, Func<JsonElement?, Task<object>>>();
            McpCaptainDiagnosticsTools.Register(
                (name, _, _, handler) =>
                {
                    handlers[name] = handler;
                },
                database,
                codeIndex);
            return handlers;
        }

        private static async Task<WorkingCaptainFixture> CreateWorkingCaptainAsync(DatabaseDriver database, string repo)
        {
            Vessel vessel = await database.Vessels.CreateAsync(new Vessel("diag-vessel", "https://github.com/test/repo.git")).ConfigureAwait(false);
            Captain captain = await database.Captains.CreateAsync(new Captain("working-diag")).ConfigureAwait(false);
            Mission mission = new Mission("working mission", "diagnostic test");
            mission.VesselId = vessel.Id;
            mission.CaptainId = captain.Id;
            mission.Status = MissionStatusEnum.InProgress;
            mission.StartedUtc = DateTime.UtcNow.AddMinutes(-10);
            mission = await database.Missions.CreateAsync(mission).ConfigureAwait(false);

            Dock dock = new Dock(vessel.Id);
            dock.CaptainId = captain.Id;
            dock.WorktreePath = repo;
            dock.BranchName = "main";
            dock = await database.Docks.CreateAsync(dock).ConfigureAwait(false);

            mission.DockId = dock.Id;
            mission = await database.Missions.UpdateAsync(mission).ConfigureAwait(false);

            captain.State = CaptainStateEnum.Working;
            captain.CurrentMissionId = mission.Id;
            captain.CurrentDockId = dock.Id;
            captain = await database.Captains.UpdateAsync(captain).ConfigureAwait(false);

            return new WorkingCaptainFixture
            {
                Vessel = vessel,
                Captain = captain,
                Mission = mission,
                Dock = dock
            };
        }

        private static async Task<string> CreateGitRepoAsync(string root, bool dirty)
        {
            Directory.CreateDirectory(root);
            await RunGitAsync(root, "init", "-b", "main").ConfigureAwait(false);
            await RunGitAsync(root, "config", "user.name", "Armada Tests").ConfigureAwait(false);
            await RunGitAsync(root, "config", "user.email", "armada-tests@example.com").ConfigureAwait(false);

            string readme = Path.Combine(root, "README.md");
            File.WriteAllText(readme, "initial\n");
            await RunGitAsync(root, "add", "README.md").ConfigureAwait(false);
            await RunGitAsync(root, "commit", "-m", "Initial commit").ConfigureAwait(false);

            if (dirty)
            {
                File.WriteAllText(readme, "dirty\n");
            }

            return root;
        }

        private static async Task<string> RunGitAsync(string workingDirectory, params string[] args)
        {
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (string arg in args)
            {
                startInfo.ArgumentList.Add(arg);
            }

            using (Process process = new Process { StartInfo = startInfo })
            {
                process.Start();
                string stdout = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
                string stderr = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
                await process.WaitForExitAsync().ConfigureAwait(false);
                if (process.ExitCode != 0)
                {
                    throw new InvalidOperationException("git " + String.Join(" ", args) + " failed: " + stderr);
                }

                return stdout;
            }
        }

        private static void DeleteDirectory(string path)
        {
            if (!Directory.Exists(path)) return;
            try
            {
                Directory.Delete(path, true);
            }
            catch
            {
            }
        }

        private sealed class WorkingCaptainFixture
        {
            public Vessel Vessel { get; set; } = null!;

            public Captain Captain { get; set; } = null!;

            public Mission Mission { get; set; } = null!;

            public Dock Dock { get; set; } = null!;
        }

        private sealed class RecordingCodeIndexService : ICodeIndexService
        {
            public string? LastStatusVesselId { get; private set; }

            public bool ThrowOnStatus { get; set; } = false;

            public CodeIndexStatus Status { get; set; } = new CodeIndexStatus
            {
                VesselId = "vsl_default",
                Freshness = "Fresh"
            };

            public Task<CodeIndexStatus> GetStatusAsync(string vesselId, CancellationToken token = default)
            {
                LastStatusVesselId = vesselId;
                if (ThrowOnStatus) throw new InvalidOperationException("index failed");
                Status.VesselId = vesselId;
                return Task.FromResult(Status);
            }

            public Task<CodeIndexStatus> UpdateAsync(string vesselId, CancellationToken token = default)
            {
                throw new NotSupportedException();
            }

            public Task<CodeSearchResponse> SearchAsync(CodeSearchRequest request, CancellationToken token = default)
            {
                throw new NotSupportedException();
            }

            public Task<ContextPackResponse> BuildContextPackAsync(ContextPackRequest request, CancellationToken token = default)
            {
                throw new NotSupportedException();
            }

            public Task<FleetCodeSearchResponse> SearchFleetAsync(FleetCodeSearchRequest request, CancellationToken token = default)
            {
                throw new NotSupportedException();
            }

            public Task<FleetContextPackResponse> BuildFleetContextPackAsync(FleetContextPackRequest request, CancellationToken token = default)
            {
                throw new NotSupportedException();
            }
        }
    }
}
