namespace Armada.Test.Unit.Suites.Routes
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
    using Armada.Core.Settings;
    using Armada.Runtimes;
    using Armada.Server;
    using Armada.Server.Mcp.Tools;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// Tests for armada_update_captain model-validation gating: metadata-only edits skip live validation,
    /// model/runtime changes invoke validation, credit/auth failures are soft with "cannot verify now" note,
    /// and genuine model errors reject.
    /// </summary>
    public sealed class CaptainUpdateValidationTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Captain Update Validation";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("MetadataOnly_NameChange_NonNullModel_SkipsValidation", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                using (CursorValidationShimScope shim = CursorValidationShimScope.Create())
                {
                    AgentLifecycleHandler lifecycle = CreateAgentLifecycleHandler(testDb.Driver);
                    ValidateCaptainModelInvocationRecorder recorder = new ValidateCaptainModelInvocationRecorder(shim.ArgsFile);
                    recorder.CaptureBaseline();

                    Captain captain = await CreateCaptainAsync(testDb.Driver, AgentRuntimeEnum.Cursor, "ok-model").ConfigureAwait(false);
                    Func<JsonElement?, Task<object>> updateHandler = RegisterUpdateCaptainHandler(testDb.Driver, lifecycle);

                    JsonElement args = JsonSerializer.SerializeToElement(new
                    {
                        captainId = captain.Id,
                        name = "renamed-captain"
                    });
                    object result = await updateHandler(args).ConfigureAwait(false);
                    string resultJson = JsonSerializer.Serialize(result);

                    AssertFalse(recorder.WasValidationLaunched(), "Metadata-only rename on non-null model captain must not launch model validation");
                    AssertFalse(resultJson.Contains("\"content\"", StringComparison.Ordinal), "Rename should not return MCP tool error envelope");
                    AssertFalse(resultJson.Contains("CannotVerifyNow", StringComparison.OrdinalIgnoreCase), "Metadata-only rename should not return cannot-verify-now note");

                    Captain? updated = await testDb.Driver.Captains.ReadAsync(captain.Id).ConfigureAwait(false);
                    AssertNotNull(updated, "Captain should exist after update");
                    AssertEqual("renamed-captain", updated!.Name, "Renamed captain should persist");
                    AssertEqual("ok-model", updated.Model, "Model should be unchanged after rename");
                }
            });

            await RunTest("MetadataOnly_ReasoningEffortChange_NonNullModel_SkipsValidation", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                using (CursorValidationShimScope shim = CursorValidationShimScope.Create())
                {
                    AgentLifecycleHandler lifecycle = CreateAgentLifecycleHandler(testDb.Driver);
                    ValidateCaptainModelInvocationRecorder recorder = new ValidateCaptainModelInvocationRecorder(shim.ArgsFile);
                    recorder.CaptureBaseline();

                    Captain captain = await CreateCaptainAsync(testDb.Driver, AgentRuntimeEnum.Cursor, "ok-model").ConfigureAwait(false);
                    Func<JsonElement?, Task<object>> updateHandler = RegisterUpdateCaptainHandler(testDb.Driver, lifecycle);

                    JsonElement args = JsonSerializer.SerializeToElement(new
                    {
                        captainId = captain.Id,
                        reasoningEffort = "high"
                    });
                    object result = await updateHandler(args).ConfigureAwait(false);
                    string resultJson = JsonSerializer.Serialize(result);

                    AssertFalse(recorder.WasValidationLaunched(), "Reasoning-effort-only edit on non-null model captain must not launch model validation");
                    AssertFalse(resultJson.Contains("\"content\"", StringComparison.Ordinal), "Reasoning-effort update should not return MCP tool error envelope");
                    AssertFalse(resultJson.Contains("CannotVerifyNow", StringComparison.OrdinalIgnoreCase), "Reasoning-effort-only edit should not return cannot-verify-now note");

                    Captain? updated = await testDb.Driver.Captains.ReadAsync(captain.Id).ConfigureAwait(false);
                    AssertNotNull(updated, "Captain should exist after reasoning-effort update");
                    AssertEqual("ok-model", updated!.Model, "Model should be unchanged after reasoning-effort edit");
                }
            });

            await RunTest("PutHandler_UsesCaseInsensitiveModelComparison", () =>
            {
                string routes = ReadRepositoryFile("src", "Armada.Server", "Routes", "CaptainRoutes.cs");
                AssertContains("StringComparison.OrdinalIgnoreCase", routes, "PUT handler should compare models without case sensitivity");
                return Task.CompletedTask;
            });

            await RunTest("PutHandler_SoftFailure_UsesQuotaAndCreditDetectors", () =>
            {
                string routes = ReadRepositoryFile("src", "Armada.Server", "Routes", "CaptainRoutes.cs");
                AssertContains("ProviderQuotaLimitDetector.IsCreditAuthBenchSignal(updateValidationError)", routes,
                    "PUT soft-failure branch must classify credit/auth bench signals");
                AssertContains("ProviderQuotaLimitDetector.IsQuotaLimitSignal(updateValidationError)", routes,
                    "PUT soft-failure branch must classify quota limit signals");
                return Task.CompletedTask;
            });

            await RunTest("PutHandler_HardFailure_Returns400", () =>
            {
                string routes = ReadRepositoryFile("src", "Armada.Server", "Routes", "CaptainRoutes.cs");
                AssertContains("if (!isSoftFailure)", routes, "PUT handler must branch hard vs soft validation failures");
                AssertContains("req.Http.Response.StatusCode = 400", routes, "Hard validation failures must return HTTP 400");
                return Task.CompletedTask;
            });

            await RunTest("PutHandler_SoftFailure_LogsWarningAndPersists", () =>
            {
                string routes = ReadRepositoryFile("src", "Armada.Server", "Routes", "CaptainRoutes.cs");
                AssertContains("_Logging?.Warn(_Header + \"model validation cannot be verified for captain \"", routes,
                    "PUT soft-failure path must emit structured warning logging");
                AssertContains("updated = await _database.Captains.UpdateAsync(updated)", routes,
                    "PUT handler must persist captain after soft validation failure");
                return Task.CompletedTask;
            });

            await RunTest("McpHandler_CloneForOptionsBaseline_IncludesModel", () =>
            {
                string tools = ReadRepositoryFile("src", "Armada.Server", "Mcp", "Tools", "McpCaptainTools.cs");
                AssertContains("Model = captain.Model", tools,
                    "Options baseline clone must snapshot Model so metadata-only edits skip validation");
                return Task.CompletedTask;
            });

            await RunTest("ModelChange_InvalidModel_InvokesValidationAndRejects", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                using (CursorValidationShimScope shim = CursorValidationShimScope.Create())
                {
                    AgentLifecycleHandler lifecycle = CreateAgentLifecycleHandler(testDb.Driver);
                    ValidateCaptainModelInvocationRecorder recorder = new ValidateCaptainModelInvocationRecorder(shim.ArgsFile);
                    recorder.CaptureBaseline();

                    Captain captain = await CreateCaptainAsync(testDb.Driver, AgentRuntimeEnum.Cursor, "ok-model").ConfigureAwait(false);
                    Func<JsonElement?, Task<object>> updateHandler = RegisterUpdateCaptainHandler(testDb.Driver, lifecycle);

                    JsonElement args = JsonSerializer.SerializeToElement(new
                    {
                        captainId = captain.Id,
                        model = "bad-model"
                    });
                    object result = await updateHandler(args).ConfigureAwait(false);
                    string resultJson = JsonSerializer.Serialize(result);

                    AssertTrue(recorder.WasValidationLaunched(), "Model change must launch model validation");
                    AssertContains("\"content\"", resultJson, "Invalid model should return MCP tool error envelope");
                    AssertContains("bad-model", resultJson, "Invalid model error should mention requested model");

                    Captain? unchanged = await testDb.Driver.Captains.ReadAsync(captain.Id).ConfigureAwait(false);
                    AssertNotNull(unchanged, "Captain should still exist after rejected update");
                    AssertEqual("ok-model", unchanged!.Model, "Rejected model change must not persist");
                }
            });

            await RunTest("ModelChange_QuotaLimitFailure_BypassedAtLifecycle_PersistsWithoutCannotVerifyNote", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                using (CursorValidationShimScope shim = CursorValidationShimScope.Create())
                {
                    AgentLifecycleHandler lifecycle = CreateAgentLifecycleHandler(testDb.Driver);
                    ValidateCaptainModelInvocationRecorder recorder = new ValidateCaptainModelInvocationRecorder(shim.ArgsFile);
                    recorder.CaptureBaseline();

                    Captain captain = await CreateCaptainAsync(testDb.Driver, AgentRuntimeEnum.Cursor, "ok-model").ConfigureAwait(false);
                    Func<JsonElement?, Task<object>> updateHandler = RegisterUpdateCaptainHandler(testDb.Driver, lifecycle);

                    JsonElement args = JsonSerializer.SerializeToElement(new
                    {
                        captainId = captain.Id,
                        model = "quota-dead-model"
                    });
                    object result = await updateHandler(args).ConfigureAwait(false);
                    string resultJson = JsonSerializer.Serialize(result);

                    AssertTrue(recorder.WasValidationLaunched(), "Model change must launch live validation before lifecycle bypass");
                    AssertFalse(resultJson.Contains("\"content\"", StringComparison.Ordinal), "Quota bypass should not hard-fail the edit");
                    AssertFalse(resultJson.Contains("CannotVerifyNow", StringComparison.OrdinalIgnoreCase),
                        "Quota signals are stripped by ValidateModelBypassingQuotaAsync before MCP soft-failure envelope");

                    Captain? updated = await testDb.Driver.Captains.ReadAsync(captain.Id).ConfigureAwait(false);
                    AssertNotNull(updated, "Captain should exist after quota-bypassed validation");
                    AssertEqual("quota-dead-model", updated!.Model, "Model change should persist when lifecycle bypasses quota errors");
                    AssertTrue(
                        ProviderQuotaLimitDetector.IsQuotaLimitSignal("You have hit your usage limit for Codex"),
                        "Sanity check quota classifier used upstream of lifecycle bypass");
                }
            });

            await RunTest("ModelChange_CreditAuthFailure_PersistsWithCannotVerifyNote", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                using (CursorValidationShimScope shim = CursorValidationShimScope.Create())
                {
                    AgentLifecycleHandler lifecycle = CreateAgentLifecycleHandler(testDb.Driver);
                    Captain captain = await CreateCaptainAsync(testDb.Driver, AgentRuntimeEnum.Cursor, "ok-model").ConfigureAwait(false);
                    Func<JsonElement?, Task<object>> updateHandler = RegisterUpdateCaptainHandler(testDb.Driver, lifecycle);

                    JsonElement args = JsonSerializer.SerializeToElement(new
                    {
                        captainId = captain.Id,
                        model = "credit-dead-model"
                    });
                    object result = await updateHandler(args).ConfigureAwait(false);
                    string resultJson = JsonSerializer.Serialize(result);

                    AssertFalse(resultJson.Contains("\"content\"", StringComparison.Ordinal), "Credit/auth validation failure should not hard-fail the edit");
                    AssertContains("CannotVerifyNow", resultJson, "Credit/auth soft failure must include CannotVerifyNow note in MCP response");
                    AssertContains("ValidationWarning", resultJson, "Credit/auth soft failure must include ValidationWarning in MCP response");

                    Captain? updated = await testDb.Driver.Captains.ReadAsync(captain.Id).ConfigureAwait(false);
                    AssertNotNull(updated, "Captain should exist after soft validation failure");
                    AssertEqual("credit-dead-model", updated!.Model, "Model change should persist on credit/auth validation failure");
                    AssertTrue(
                        ProviderQuotaLimitDetector.IsCreditAuthBenchSignal("insufficient credits for this account"),
                        "Sanity check credit classifier used by edit layer");
                }
            });

            await RunTest("RuntimeChange_InvokesValidationAndRejectsInvalidModel", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                using (CursorValidationShimScope shim = CursorValidationShimScope.Create())
                {
                    AgentLifecycleHandler lifecycle = CreateAgentLifecycleHandler(testDb.Driver);
                    ValidateCaptainModelInvocationRecorder recorder = new ValidateCaptainModelInvocationRecorder(shim.ArgsFile);
                    recorder.CaptureBaseline();

                    Captain captain = await CreateCaptainAsync(testDb.Driver, AgentRuntimeEnum.Codex, "bad-model").ConfigureAwait(false);
                    Func<JsonElement?, Task<object>> updateHandler = RegisterUpdateCaptainHandler(testDb.Driver, lifecycle);

                    JsonElement args = JsonSerializer.SerializeToElement(new
                    {
                        captainId = captain.Id,
                        runtime = "Cursor"
                    });
                    object result = await updateHandler(args).ConfigureAwait(false);
                    string resultJson = JsonSerializer.Serialize(result);

                    AssertTrue(recorder.WasValidationLaunched(), "Runtime change must launch model validation");
                    AssertContains("\"content\"", resultJson, "Runtime change with invalid model should return MCP tool error envelope");
                    AssertContains("bad-model", resultJson, "Validation error should mention the configured model");

                    Captain? unchanged = await testDb.Driver.Captains.ReadAsync(captain.Id).ConfigureAwait(false);
                    AssertNotNull(unchanged, "Captain should still exist after rejected runtime change");
                    AssertEqual(AgentRuntimeEnum.Codex, unchanged!.Runtime, "Rejected runtime change must not persist");
                }
            });

            await RunTest("ReasoningEffort_InvalidValue_RejectsWithoutModelValidation", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                using (CursorValidationShimScope shim = CursorValidationShimScope.Create())
                {
                    AgentLifecycleHandler lifecycle = CreateAgentLifecycleHandler(testDb.Driver);
                    ValidateCaptainModelInvocationRecorder recorder = new ValidateCaptainModelInvocationRecorder(shim.ArgsFile);
                    recorder.CaptureBaseline();

                    Captain captain = await CreateCaptainAsync(testDb.Driver, AgentRuntimeEnum.Cursor, "bad-model").ConfigureAwait(false);
                    Func<JsonElement?, Task<object>> updateHandler = RegisterUpdateCaptainHandler(testDb.Driver, lifecycle);

                    JsonElement args = JsonSerializer.SerializeToElement(new
                    {
                        captainId = captain.Id,
                        reasoningEffort = "not-a-tier"
                    });
                    object result = await updateHandler(args).ConfigureAwait(false);
                    string resultJson = JsonSerializer.Serialize(result);

                    AssertFalse(recorder.WasValidationLaunched(), "Invalid reasoning effort should fail before model validation");
                    AssertContains("\"content\"", resultJson, "Invalid reasoning effort should return MCP tool error envelope");
                }
            });

            await RunTest("SameModel_CaseInsensitive_SkipsValidation", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                using (CursorValidationShimScope shim = CursorValidationShimScope.Create())
                {
                    AgentLifecycleHandler lifecycle = CreateAgentLifecycleHandler(testDb.Driver);
                    ValidateCaptainModelInvocationRecorder recorder = new ValidateCaptainModelInvocationRecorder(shim.ArgsFile);
                    recorder.CaptureBaseline();

                    Captain captain = await CreateCaptainAsync(testDb.Driver, AgentRuntimeEnum.Cursor, "Claude-Sonnet-4").ConfigureAwait(false);
                    Func<JsonElement?, Task<object>> updateHandler = RegisterUpdateCaptainHandler(testDb.Driver, lifecycle);

                    JsonElement args = JsonSerializer.SerializeToElement(new
                    {
                        captainId = captain.Id,
                        model = "claude-sonnet-4"
                    });
                    object result = await updateHandler(args).ConfigureAwait(false);
                    string resultJson = JsonSerializer.Serialize(result);

                    AssertFalse(recorder.WasValidationLaunched(), "Case-insensitive same-model update must not launch validation");
                    AssertFalse(resultJson.Contains("\"content\"", StringComparison.Ordinal), "Same-model case-insensitive update should not error");
                }
            });
        }

        private static async Task<Captain> CreateCaptainAsync(DatabaseDriver database, AgentRuntimeEnum runtime, string? model)
        {
            Captain captain = new Captain("update-validation-captain", runtime)
            {
                Model = model
            };
            return await database.Captains.CreateAsync(captain).ConfigureAwait(false);
        }

        private static string ReadRepositoryFile(params string[] relativePath)
        {
            return File.ReadAllText(Path.Combine(FindRepositoryRoot(), Path.Combine(relativePath)));
        }

        private static string FindRepositoryRoot()
        {
            DirectoryInfo? current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current != null)
            {
                if (Directory.Exists(Path.Combine(current.FullName, "src")) &&
                    Directory.Exists(Path.Combine(current.FullName, "test")))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }

            throw new DirectoryNotFoundException("Could not locate repository root from test base directory.");
        }

        private static Func<JsonElement?, Task<object>> RegisterUpdateCaptainHandler(
            DatabaseDriver database,
            AgentLifecycleHandler agentLifecycle)
        {
            Func<JsonElement?, Task<object>>? updateHandler = null;
            IAdmiralService admiral = new StubAdmiralService(database);
            ArmadaSettings settings = new ArmadaSettings();
            McpCaptainTools.Register(
                (name, _, _, handler) =>
                {
                    if (name == "armada_update_captain")
                    {
                        updateHandler = handler;
                    }
                },
                database,
                admiral,
                settings,
                null,
                agentLifecycle);

            if (updateHandler == null)
            {
                throw new InvalidOperationException("armada_update_captain handler was not registered");
            }

            return updateHandler;
        }

        private static AgentLifecycleHandler CreateAgentLifecycleHandler(DatabaseDriver database)
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            ArmadaSettings settings = new ArmadaSettings();
            settings.LogDirectory = Path.Combine(Path.GetTempPath(), "armada_captain_update_validation_" + Guid.NewGuid().ToString("N"));
            AgentRuntimeFactory runtimeFactory = new AgentRuntimeFactory(logging);
            IAdmiralService admiral = new StubAdmiralService(database);
            IMessageTemplateService templateService = new MessageTemplateService(logging);

            return new AgentLifecycleHandler(
                logging,
                database,
                settings,
                runtimeFactory,
                admiral,
                templateService,
                null,
                null,
                (eventType, message, entityType, entityId, captainId, missionId, vesselId, voyageId) => Task.CompletedTask);
        }

        /// <summary>
        /// Observes the Cursor validation shim args file to record whether live model validation launched.
        /// </summary>
        private sealed class ValidateCaptainModelInvocationRecorder
        {
            private readonly string _ArgsFilePath;
            private long _BaselineLength;

            public ValidateCaptainModelInvocationRecorder(string argsFilePath)
            {
                _ArgsFilePath = argsFilePath;
            }

            public string ArgsFilePath => _ArgsFilePath;

            public void CaptureBaseline()
            {
                if (!File.Exists(_ArgsFilePath))
                {
                    _BaselineLength = 0;
                    return;
                }

                FileInfo info = new FileInfo(_ArgsFilePath);
                _BaselineLength = info.Length;
            }

            public bool WasValidationLaunched()
            {
                if (!File.Exists(_ArgsFilePath))
                {
                    return false;
                }

                FileInfo info = new FileInfo(_ArgsFilePath);
                return info.Length > _BaselineLength;
            }
        }

        private sealed class StubAdmiralService : IAdmiralService
        {
            private readonly DatabaseDriver _Database;

            public StubAdmiralService(DatabaseDriver database)
            {
                _Database = database ?? throw new ArgumentNullException(nameof(database));
            }

            public Func<Captain, Mission, Dock, Task<int>>? OnLaunchAgent { get; set; }
            public Func<Captain, Task>? OnStopAgent { get; set; }
            public Func<Mission, Dock, Task>? OnCaptureDiff { get; set; }
            public Func<Mission, Dock, Task>? OnMissionComplete { get; set; }
            public Func<Voyage, Task>? OnVoyageComplete { get; set; }
            public Func<Mission, Task<bool>>? OnReconcilePullRequest { get; set; }
            public Func<Task<int>>? OnReconcileMergeEntries { get; set; }
            public Func<int, bool>? OnIsProcessExitHandled { get; set; }

            public Task<Voyage> DispatchVoyageAsync(string title, string description, string vesselId, List<MissionDescription> missionDescriptions, CancellationToken token = default)
            {
                throw new NotSupportedException();
            }

            public Task<Voyage> DispatchVoyageAsync(string title, string description, string vesselId, List<MissionDescription> missionDescriptions, List<SelectedPlaybook>? selectedPlaybooks, CancellationToken token = default)
            {
                throw new NotSupportedException();
            }

            public Task<Voyage> DispatchVoyageAsync(string title, string description, string vesselId, List<MissionDescription> missionDescriptions, string? pipelineId, CancellationToken token = default)
            {
                throw new NotSupportedException();
            }

            public Task<Voyage> DispatchVoyageAsync(string title, string description, string vesselId, List<MissionDescription> missionDescriptions, string? pipelineId, List<SelectedPlaybook>? selectedPlaybooks, CancellationToken token = default)
            {
                throw new NotSupportedException();
            }

            public Task<Mission> DispatchMissionAsync(Mission mission, CancellationToken token = default)
            {
                throw new NotSupportedException();
            }

            public Task<Pipeline?> ResolvePipelineAsync(string? pipelineIdOrName, Vessel vessel, CancellationToken token = default)
            {
                return Task.FromResult<Pipeline?>(null);
            }

            public Task<ArmadaStatus> GetStatusAsync(CancellationToken token = default)
            {
                throw new NotSupportedException();
            }

            public Task RecallCaptainAsync(string captainId, CancellationToken token = default)
            {
                return Task.CompletedTask;
            }

            public Task RecallAllAsync(CancellationToken token = default)
            {
                return Task.CompletedTask;
            }

            public Task HealthCheckAsync(CancellationToken token = default)
            {
                return Task.CompletedTask;
            }

            public Task CleanupStaleCaptainsAsync(CancellationToken token = default)
            {
                return Task.CompletedTask;
            }

            public Task HandleProcessExitAsync(int processId, int? exitCode, string captainId, string missionId, CancellationToken token = default)
            {
                return Task.CompletedTask;
            }
        }

        private sealed class CursorValidationShimScope : IDisposable
        {
            public string ArgsFile { get; }

            private readonly string _TempDirectory;
            private readonly string _OriginalPath;
            private readonly bool _SetWindowsAgentOverride;
            private readonly string? _OriginalCursorAgentOverride;

            private CursorValidationShimScope(
                string tempDirectory,
                string argsFile,
                string originalPath,
                bool setWindowsAgentOverride,
                string? originalCursorAgentOverride)
            {
                _TempDirectory = tempDirectory;
                ArgsFile = argsFile;
                _OriginalPath = originalPath;
                _SetWindowsAgentOverride = setWindowsAgentOverride;
                _OriginalCursorAgentOverride = originalCursorAgentOverride;
            }

            public static CursorValidationShimScope Create()
            {
                string tempDirectory = Path.Combine(Path.GetTempPath(), "armada_captain_update_shim_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDirectory);

                string argsFile = Path.Combine(tempDirectory, "cursor-args.txt");
                string originalPath = Environment.GetEnvironmentVariable("PATH") ?? String.Empty;
                string? originalCursorAgentOverride = Environment.GetEnvironmentVariable("ARMADA_TEST_CURSOR_AGENT");
                bool setWindowsAgentOverride = false;

                Environment.SetEnvironmentVariable("ARMADA_TEST_CURSOR_ARGS_FILE", argsFile);

                if (OperatingSystem.IsWindows())
                {
                    string shimPath = Path.Combine(tempDirectory, "cursor-agent.cmd");
                    File.WriteAllText(shimPath, BuildWindowsShim());
                    Environment.SetEnvironmentVariable("ARMADA_TEST_CURSOR_AGENT", shimPath);
                    setWindowsAgentOverride = true;
                }
                else
                {
                    string shimPath = Path.Combine(tempDirectory, "cursor-agent");
                    File.WriteAllText(shimPath, BuildUnixShim());
                    File.SetUnixFileMode(
                        shimPath,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                        UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                        UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                    Environment.SetEnvironmentVariable("PATH", tempDirectory + Path.PathSeparator + originalPath);
                }

                return new CursorValidationShimScope(
                    tempDirectory,
                    argsFile,
                    originalPath,
                    setWindowsAgentOverride,
                    originalCursorAgentOverride);
            }

            public void Dispose()
            {
                Environment.SetEnvironmentVariable("ARMADA_TEST_CURSOR_ARGS_FILE", null);
                Environment.SetEnvironmentVariable("PATH", _OriginalPath);

                if (_SetWindowsAgentOverride)
                {
                    Environment.SetEnvironmentVariable("ARMADA_TEST_CURSOR_AGENT", _OriginalCursorAgentOverride);
                }

                try
                {
                    Directory.Delete(_TempDirectory, true);
                }
                catch
                {
                }
            }

            private static string BuildWindowsShim()
            {
                return "@echo off\r\n" +
                    "setlocal EnableExtensions EnableDelayedExpansion\r\n" +
                    "set \"ARGS_FILE=%ARMADA_TEST_CURSOR_ARGS_FILE%\"\r\n" +
                    "set \"ALL_ARGS=%*\"\r\n" +
                    ">> \"%ARGS_FILE%\" echo(!ALL_ARGS!\r\n" +
                    "set \"MODEL=\"\r\n" +
                    ":loop\r\n" +
                    "if \"%~1\"==\"\" goto done\r\n" +
                    ">> \"%ARGS_FILE%\" echo %~1\r\n" +
                    "if /I \"%~1\"==\"--model\" set \"MODEL=%~2\"\r\n" +
                    "shift\r\n" +
                    "goto loop\r\n" +
                    ":done\r\n" +
                    "if /I \"%MODEL%\"==\"bad-model\" (\r\n" +
                    "  >&2 echo unknown model '%MODEL%'\r\n" +
                    "  exit /b 3\r\n" +
                    ")\r\n" +
                    "if /I \"%MODEL%\"==\"credit-dead-model\" (\r\n" +
                    "  >&2 echo insufficient credits for this account\r\n" +
                    "  exit /b 5\r\n" +
                    ")\r\n" +
                    "if /I \"%MODEL%\"==\"quota-dead-model\" (\r\n" +
                    "  >&2 echo You have hit your usage limit for Codex\r\n" +
                    "  exit /b 5\r\n" +
                    ")\r\n" +
                    "echo ok\r\n" +
                    "exit /b 0\r\n";
            }

            private static string BuildUnixShim()
            {
                return "#!/usr/bin/env sh\n" +
                    "args_file=\"$ARMADA_TEST_CURSOR_ARGS_FILE\"\n" +
                    "printf '%s\\n' \"$*\" >> \"$args_file\"\n" +
                    "prev=\"\"\n" +
                    "model=\"\"\n" +
                    "for arg in \"$@\"; do\n" +
                    "  printf '%s\\n' \"$arg\" >> \"$args_file\"\n" +
                    "  if [ \"$prev\" = \"--model\" ]; then\n" +
                    "    model=\"$arg\"\n" +
                    "  fi\n" +
                    "  prev=\"$arg\"\n" +
                    "done\n" +
                    "if [ \"$model\" = \"bad-model\" ]; then\n" +
                    "  printf '%s\\n' \"unknown model '$model'\" >&2\n" +
                    "  exit 3\n" +
                    "fi\n" +
                    "if [ \"$model\" = \"credit-dead-model\" ]; then\n" +
                    "  printf '%s\\n' \"insufficient credits for this account\" >&2\n" +
                    "  exit 5\n" +
                    "fi\n" +
                    "if [ \"$model\" = \"quota-dead-model\" ]; then\n" +
                    "  printf '%s\\n' \"You have hit your usage limit for Codex\" >&2\n" +
                    "  exit 5\n" +
                    "fi\n" +
                    "printf '%s\\n' ok\n" +
                    "exit 0\n";
            }
        }
    }
}
