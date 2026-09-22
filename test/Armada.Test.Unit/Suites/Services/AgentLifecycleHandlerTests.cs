namespace Armada.Test.Unit.Suites.Services
{
    using System.Collections.Concurrent;
    using System.Diagnostics;
    using System.IO;
    using System.Reflection;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Runtimes;
    using Armada.Server;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// Tests for runtime model validation and launch passthrough in AgentLifecycleHandler.
    /// </summary>
    public class AgentLifecycleHandlerTests : TestSuite
    {
        /// <summary>
        /// Suite name.
        /// </summary>
        public override string Name => "Agent Lifecycle Handler";

        /// <summary>
        /// Run all tests.
        /// </summary>
        protected override async Task RunTestsAsync()
        {
            // ----------------------------------------------------------------
            // Mission MCP credential is scoped to the mission owner, never the
            // admiral launch credential (cross-tenant privilege escalation fix).
            // ----------------------------------------------------------------

            await RunTest("A tenant-admin-dispatched mission's captain is scoped to its own tenant, not global admin, and cannot reach another tenant", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    // A tenant admin of tenant B dispatches a mission. Its captain must reach only tenant B's
                    // records with tenant B's own privileges, never the admiral launch credential (which maps
                    // to global admin in the default tenant and reaches every operator-only tool).
                    SessionTokenService tokens = new SessionTokenService();
                    AgentLifecycleHandler handler = CreateHandler(testDb.Driver, out ArmadaSettings settings, sessionTokens: tokens);

                    TenantMetadata tenantB = new TenantMetadata("Tenant B");
                    await testDb.Driver.Tenants.CreateAsync(tenantB).ConfigureAwait(false);
                    UserMaster adminB = new UserMaster(tenantB.Id, "admin-b@example.com", "password");
                    adminB.IsTenantAdmin = true;
                    adminB.IsAdmin = false;
                    await testDb.Driver.Users.CreateAsync(adminB).ConfigureAwait(false);

                    TenantMetadata tenantA = new TenantMetadata("Tenant A");
                    await testDb.Driver.Tenants.CreateAsync(tenantA).ConfigureAwait(false);

                    Mission mission = new Mission("Cross-tenant probe")
                    {
                        TenantId = tenantB.Id,
                        UserId = adminB.Id
                    };

                    McpCredentialReference credential = await handler.ResolveMissionMcpCredentialAsync(mission).ConfigureAwait(false);
                    AssertTrue(credential.HasToken, "the mission carries a scoped MCP credential");
                    AssertFalse(string.Equals(credential.Token, McpLaunchCredential.Token, StringComparison.Ordinal),
                        "the mission credential is never the admiral launch credential value");

                    AuthenticationService auth = CreateAuthenticationService(testDb.Driver, tokens);
                    AuthContext ctx = await auth.AuthenticateAsync("Bearer " + credential.Token, null, null).ConfigureAwait(false);

                    AssertTrue(ctx.IsAuthenticated, "the mission credential authenticates");
                    AssertEqual(tenantB.Id, ctx.TenantId, "the mission is scoped to its owning tenant");
                    AssertEqual(adminB.Id, ctx.UserId, "the mission is scoped to its owning user");
                    AssertFalse(ctx.IsAdmin, "the mission never runs as a global admin, so operator-only tools are unreachable");
                    AssertFalse(string.Equals(ctx.TenantId, Armada.Core.Constants.DefaultTenantId, StringComparison.Ordinal) && ctx.IsAdmin,
                        "the mission never reaches the default tenant as global admin");
                    AssertFalse(string.Equals(ctx.TenantId, tenantA.Id, StringComparison.Ordinal),
                        "the mission cannot read another tenant's records: its scope is its own tenant");
                }
            });

            await RunTest("An autonomous mission runs with the objective owner's scope carried on its voyage, not global admin", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    // Autonomous dispatch has no interactive caller. When the mission itself carries no owner,
                    // the objective owner carried on its voyage is the scope, never global admin.
                    SessionTokenService tokens = new SessionTokenService();
                    AgentLifecycleHandler handler = CreateHandler(testDb.Driver, out ArmadaSettings settings, sessionTokens: tokens);

                    TenantMetadata tenantC = new TenantMetadata("Tenant C");
                    await testDb.Driver.Tenants.CreateAsync(tenantC).ConfigureAwait(false);
                    UserMaster ownerC = new UserMaster(tenantC.Id, "owner-c@example.com", "password");
                    ownerC.IsTenantAdmin = false;
                    ownerC.IsAdmin = false;
                    await testDb.Driver.Users.CreateAsync(ownerC).ConfigureAwait(false);

                    Voyage voyage = new Voyage("Autonomous voyage")
                    {
                        TenantId = tenantC.Id,
                        UserId = ownerC.Id
                    };
                    await testDb.Driver.Voyages.CreateAsync(voyage).ConfigureAwait(false);

                    Mission mission = new Mission("Autonomous mission")
                    {
                        VoyageId = voyage.Id
                        // No TenantId/UserId: the owner falls back to the voyage (the objective owner).
                    };

                    McpCredentialReference credential = await handler.ResolveMissionMcpCredentialAsync(mission).ConfigureAwait(false);
                    AssertTrue(credential.HasToken, "the autonomous mission carries a scoped MCP credential");
                    AssertFalse(string.Equals(credential.Token, McpLaunchCredential.Token, StringComparison.Ordinal),
                        "the autonomous mission credential is never the admiral launch credential value");

                    AuthenticationService auth = CreateAuthenticationService(testDb.Driver, tokens);
                    AuthContext ctx = await auth.AuthenticateAsync("Bearer " + credential.Token, null, null).ConfigureAwait(false);
                    AssertEqual(tenantC.Id, ctx.TenantId, "the autonomous mission is scoped to the objective owner's tenant");
                    AssertEqual(ownerC.Id, ctx.UserId, "the autonomous mission is scoped to the objective owner's user");
                    AssertFalse(ctx.IsAdmin, "the autonomous mission is not a global admin");
                }
            });

            await RunTest("A mission launch plan never carries the admiral launch credential value in its environment", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    SessionTokenService tokens = new SessionTokenService();
                    AgentLifecycleHandler handler = CreateHandler(testDb.Driver, out ArmadaSettings settings, sessionTokens: tokens);

                    Captain captain = new Captain("cursor-mission-captain", AgentRuntimeEnum.Cursor);
                    Mission mission = new Mission("Owned mission")
                    {
                        TenantId = Armada.Core.Constants.DefaultTenantId,
                        UserId = Armada.Core.Constants.DefaultUserId
                    };

                    CaptainLaunchIsolationPlan? plan = await handler.PrepareCaptainLaunchIsolationAsync(captain, mission).ConfigureAwait(false);
                    AssertTrue(plan != null, "a seeded mission launch produces a plan");
                    AssertTrue(plan!.EnvironmentOverrides.ContainsKey(McpLaunchCredential.EnvironmentVariable),
                        "the launch variable carries the mission credential so the dock configuration resolves");
                    AssertFalse(plan.EnvironmentOverrides.ContainsValue(McpLaunchCredential.Token),
                        "the admiral launch credential value never appears in a mission captain's environment");
                    // The value present is a scoped session token, not the launch credential.
                    AuthenticationService auth = CreateAuthenticationService(testDb.Driver, tokens);
                    string carried = plan.EnvironmentOverrides[McpLaunchCredential.EnvironmentVariable];
                    // A default-tenant, default-user mission needs the default tenant present to authenticate;
                    // proving the value is not the launch token is enough here.
                    AssertFalse(string.Equals(carried, McpLaunchCredential.Token, StringComparison.Ordinal),
                        "the launch variable holds a scoped session token, not the launch credential");
                    _ = auth;
                }
            });

            await RunTest("A legitimate same-tenant mission still launches with working MCP wiring", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    // The fix must not remove what a normal mission can legitimately do: the captain still
                    // receives a credential its dock and scoped MCP configuration can resolve.
                    SessionTokenService tokens = new SessionTokenService();
                    AgentLifecycleHandler handler = CreateHandler(testDb.Driver, out ArmadaSettings settings, sessionTokens: tokens);

                    Captain claude = new Captain("claude-mission-captain", AgentRuntimeEnum.ClaudeCode);
                    Mission mission = new Mission("Same-tenant mission")
                    {
                        TenantId = Armada.Core.Constants.DefaultTenantId,
                        UserId = Armada.Core.Constants.DefaultUserId
                    };

                    CaptainLaunchIsolationPlan? plan = await handler.PrepareCaptainLaunchIsolationAsync(claude, mission).ConfigureAwait(false);
                    AssertTrue(plan != null, "a same-tenant mission still gets a launch plan");
                    AssertTrue(plan!.EnvironmentOverrides.TryGetValue(McpLaunchCredential.EnvironmentVariable, out string? value) && !string.IsNullOrEmpty(value),
                        "the captain still carries an MCP credential so its tools work");
                    AssertTrue(plan.ExtraArguments.Contains("--strict-mcp-config"),
                        "Claude Code still receives its scoped MCP configuration");
                    AssertTrue(plan.FilesToWrite.Exists(f => f.RelativePath == "armada-mcp.json"),
                        "the scoped MCP config file is still written");
                    AssertTrue(plan.FilesToWrite.TrueForAll(f => !f.Contents.Contains(value!, StringComparison.Ordinal)),
                        "the credential value never lands in a scoped config file, only the variable reference");
                }
            });

            await RunTest("IsProcessExitHandled holds while exit handling is in flight, beyond the retention window", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    TaskCompletionSource<bool> release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    StubAdmiralService admiral = new StubAdmiralService();
                    admiral.OnHandleProcessExit = (processId, exitCode, captainId, missionId) => release.Task;
                    AgentLifecycleHandler handler = CreateHandler(testDb.Driver, out _, null, admiral);
                    handler.HandledExitRetention = TimeSpan.FromMilliseconds(1);

                    Task handling = handler.HandleAgentProcessExitedAsync(4242, 0, "cpt_test", "msn_test");
                    await Task.Delay(50).ConfigureAwait(false);

                    // The completion handler is still running well past the retention window: the
                    // health check must still see the exit as handled, or it reads the finished
                    // process as a crash and fails a mission that is being completed.
                    AssertTrue(handler.IsProcessExitHandled(4242), "in-flight exit stays handled regardless of retention");

                    release.SetResult(true);
                    await handling.ConfigureAwait(false);
                    await Task.Delay(20).ConfigureAwait(false);

                    AssertFalse(handler.IsProcessExitHandled(4242), "after completion the marker expires with the retention window");
                }
            });

            await RunTest("IsProcessExitHandled recognises a completed exit within the retention window and not a foreign PID", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    AgentLifecycleHandler handler = CreateHandler(testDb.Driver, out _);
                    handler.HandledExitRetention = TimeSpan.FromMinutes(5);

                    await handler.HandleAgentProcessExitedAsync(4343, 0, "cpt_test", "msn_test").ConfigureAwait(false);

                    AssertTrue(handler.IsProcessExitHandled(4343), "a just-completed exit is recognised within retention");
                    AssertFalse(handler.IsProcessExitHandled(4344), "a PID that never exited through the handler is not handled");
                }
            });

            await RunTest("ValidateModelAsync returns null and forwards model to runtime", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                using (CursorShimScope shim = CursorShimScope.Create())
                {
                    AgentLifecycleHandler handler = CreateHandler(testDb.Driver, out _);

                    string? error = await handler.ValidateModelAsync(AgentRuntimeEnum.Cursor, "gpt-5.4-mini").ConfigureAwait(false);
                    string args = await WaitForRecordedArgsAsync(shim.ArgsFile, "gpt-5.4-mini").ConfigureAwait(false);

                    AssertNull(error, "Valid model should pass validation");
                    AssertContains("--model", args, "Validation runtime args should include model flag");
                    AssertContains("gpt-5.4-mini", args, "Validation runtime args should include requested model");
                }
            });

            await RunTest("ValidateCaptainModelAsync returns extracted runtime error for invalid model", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                using (CursorShimScope shim = CursorShimScope.Create())
                {
                    AgentLifecycleHandler handler = CreateHandler(testDb.Driver, out _);
                    Captain captain = new Captain("validation-captain", AgentRuntimeEnum.Cursor)
                    {
                        Model = "bad-model"
                    };

                    string? error = await handler.ValidateCaptainModelAsync(captain).ConfigureAwait(false);
                    string args = await WaitForRecordedArgsAsync(shim.ArgsFile, "bad-model").ConfigureAwait(false);

                    AssertNotNull(error, "Invalid model should return an error");
                    AssertContains("bad-model", error!, "Error should include invalid model");
                    AssertContains("unknown model 'bad-model'", error!, "Error should include runtime output");
                    AssertContains("--model", args, "Captain validation should launch runtime with model flag");
                }
            });

            await RunTest("ValidateCaptainModelAsync returns timeout error when runtime does not exit", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                using (CursorShimScope shim = CursorShimScope.Create())
                {
                    // The hang-model shim blocks longer than this ceiling but far shorter than the 30 s
                    // production ceiling, under which the validation would succeed and never reach the
                    // timeout path. Drive a short ceiling instead of making the fake runtime outlast 30 seconds.
                    AgentLifecycleHandler handler = CreateHandler(
                        testDb.Driver, out _, TimeSpan.FromSeconds(1));
                    Captain captain = new Captain("timeout-captain", AgentRuntimeEnum.Cursor)
                    {
                        Model = "hang-model"
                    };

                    string? error = await handler.ValidateCaptainModelAsync(captain).ConfigureAwait(false);
                    string args = await WaitForRecordedArgsAsync(shim.ArgsFile, "hang-model").ConfigureAwait(false);

                    AssertNotNull(error, "Timed-out validation should return an error");
                    AssertContains("hang-model", error!, "Error should include requested model");
                    AssertContains("timed out", error!, "Error should report validation timeout");
                    AssertContains("--model", args, "Timed-out validation should still launch runtime with model flag");
                }
            });

            await RunTest("CursorShimScope on Windows uses temp override and restores environment", () =>
            {
                if (!OperatingSystem.IsWindows())
                    return;

                string? originalOverride = Environment.GetEnvironmentVariable("ARMADA_TEST_CURSOR_AGENT");
                string sentinelOverride = Path.Combine(Path.GetTempPath(), "armada_original_cursor_agent.cmd");

                try
                {
                    Environment.SetEnvironmentVariable("ARMADA_TEST_CURSOR_AGENT", sentinelOverride);

                    using (CursorShimScope shim = CursorShimScope.Create())
                    {
                        string? overridePath = Environment.GetEnvironmentVariable("ARMADA_TEST_CURSOR_AGENT");
                        AssertNotNull(overridePath, "Windows shim scope must set ARMADA_TEST_CURSOR_AGENT");
                        AssertFalse(
                            String.Equals(sentinelOverride, overridePath, StringComparison.OrdinalIgnoreCase),
                            "Windows shim scope must replace the caller override while active");
                        string appDataNpm = Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                            "npm");
                        AssertFalse(
                            overridePath!.StartsWith(appDataNpm, StringComparison.OrdinalIgnoreCase),
                            "Windows shim scope must not write the active shim under APPDATA npm");
                        AssertTrue(File.Exists(overridePath), "Windows shim override must point to the temp shim file");
                    }

                    AssertEqual(
                        sentinelOverride,
                        Environment.GetEnvironmentVariable("ARMADA_TEST_CURSOR_AGENT"),
                        "Windows shim scope must restore the prior test override on dispose");
                }
                finally
                {
                    Environment.SetEnvironmentVariable("ARMADA_TEST_CURSOR_AGENT", originalOverride);
                }
            });

            await RunTest("ValidateCaptainModelAsync rejects invalid Mux runtime options JSON", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    AgentLifecycleHandler handler = CreateHandler(testDb.Driver, out _);
                    Captain captain = new Captain("mux-captain", AgentRuntimeEnum.Mux)
                    {
                        RuntimeOptionsJson = "{not valid json}"
                    };

                    string? error = await handler.ValidateCaptainModelAsync(captain).ConfigureAwait(false);

                    AssertNotNull(error, "Mux validation should fail when runtime options JSON is invalid");
                    AssertContains("invalid JSON", error!, "Mux validation should report invalid JSON");
                }
            });

            await RunTest("ValidateNativeEndpointAdmission accepts an enabled inference endpoint and rejects the wrong kind, disabled, and a model mismatch", () =>
            {
                Captain captain = new Captain("external-judge", AgentRuntimeEnum.ClaudeCode)
                {
                    TenantId = "ten_ep",
                    UserId = "usr_ep",
                    ModelEndpointId = "mep_native",
                    Model = "claude-fable-5"
                };
                ModelEndpoint endpoint = new ModelEndpoint
                {
                    Id = "mep_native",
                    TenantId = "ten_ep",
                    UserId = "usr_ep",
                    Name = "External Fable",
                    Provider = ModelProviderEnum.Anthropic,
                    Kind = ModelEndpointKindEnum.Inference,
                    Scope = ScopeEnum.TenantWide,
                    BaseUrl = "https://api.example.com/v1",
                    ApiKey = "sk-endpoint",
                    Model = "claude-fable-5",
                    Enabled = true
                };

                AssertNull(AgentLifecycleHandler.ValidateNativeEndpointAdmission(captain, endpoint), "an enabled inference endpoint the captain owns is admitted");
                AssertNotNull(AgentLifecycleHandler.ValidateNativeEndpointAdmission(captain, null), "a missing endpoint is rejected");

                endpoint.Kind = ModelEndpointKindEnum.Embedding;
                AssertContains("inference", AgentLifecycleHandler.ValidateNativeEndpointAdmission(captain, endpoint)!, "an embedding endpoint is rejected");
                endpoint.Kind = ModelEndpointKindEnum.Inference;

                endpoint.Enabled = false;
                AssertContains("disabled", AgentLifecycleHandler.ValidateNativeEndpointAdmission(captain, endpoint)!, "a disabled endpoint is rejected");
                endpoint.Enabled = true;

                captain.Model = "some-other-model";
                AssertContains("must match", AgentLifecycleHandler.ValidateNativeEndpointAdmission(captain, endpoint)!, "a model mismatch is rejected");
                return Task.CompletedTask;
            });

            await RunTest("A native captain's referenced inference endpoint drives the runtime provider resolver", () =>
            {
                Captain captain = new Captain("external-judge", AgentRuntimeEnum.ClaudeCode)
                {
                    TenantId = "ten_ep",
                    ModelEndpointId = "mep_native",
                    Model = "claude-fable-5"
                };
                ModelEndpoint endpoint = new ModelEndpoint
                {
                    Id = "mep_native",
                    TenantId = "ten_ep",
                    Provider = ModelProviderEnum.Anthropic,
                    Kind = ModelEndpointKindEnum.Inference,
                    BaseUrl = "https://api.example.com/v1",
                    ApiKey = "sk-endpoint",
                    Model = "claude-fable-5",
                    Enabled = true
                };

                AgentLifecycleHandler.ApplyNativeEndpointCredentials(captain, endpoint);
                AssertEqual("https://api.example.com/v1", captain.ApiBaseUrl!, "the endpoint base URL is carried onto the launch snapshot");
                AssertEqual("sk-endpoint", captain.ApiKey!, "the endpoint key is carried onto the launch snapshot");

                ResolvedModelProvider? resolved = ModelProviderResolver.Resolve(captain, null, new ModelProvidersSettings());
                AssertNotNull(resolved, "the runtime provider resolver must resolve from the endpoint credentials");
                AssertEqual("https://api.example.com/v1", resolved!.BaseUrl, "the runtime uses the endpoint base URL");
                AssertEqual("sk-endpoint", resolved.ApiKey, "the runtime uses the endpoint key");
                return Task.CompletedTask;
            });

            await RunTest("HandleLaunchAgentAsync passes captain model to runtime startup", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                using (CursorShimScope shim = CursorShimScope.Create())
                {
                    AgentLifecycleHandler handler = CreateHandler(testDb.Driver, out ArmadaSettings settings);
                    string worktreePath = Path.Combine(Path.GetTempPath(), "armada_cursor_launch_" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(worktreePath);

                    try
                    {
                        Captain captain = new Captain("launch-captain", AgentRuntimeEnum.Cursor)
                        {
                            Model = "cursor-model"
                        };

                        Mission mission = new Mission("Launch mission")
                        {
                            Persona = "Test Engineer",
                            BranchName = "feature/model-pass"
                        };

                        Dock dock = new Dock
                        {
                            BranchName = "feature/model-pass",
                            WorktreePath = worktreePath
                        };
                        string logFilePath = Path.Combine(settings.LogDirectory, "missions", mission.Id + ".log");

                        int processId = await handler.HandleLaunchAgentAsync(captain, mission, dock).ConfigureAwait(false);
                        string logContents = await WaitForFileContainsAsync(logFilePath, "cursor-model").ConfigureAwait(false);

                        AssertTrue(processId > 0, "Launch should return a process id");
                        AssertContains("--model cursor-model", logContents, "Launch log should include captain model flag");
                    }
                    finally
                    {
                        try { Directory.Delete(worktreePath, true); } catch { }
                    }
                }
            });

            await RunTest("HandleLaunchAgentAsync omits commit trailers on a read-only mission", async () =>
            {
                // Probe papercut 2026-08-09: the launch prompt supplied git commit trailers on a
                // Research mission whose rules forbid any commit - dead weight that contradicts the
                // brief and costs tokens on every read-only dispatch.
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                using (CursorShimScope shim = CursorShimScope.Create())
                {
                    AgentLifecycleHandler handler = CreateHandler(testDb.Driver, out ArmadaSettings settings);
                    string worktreePath = Path.Combine(Path.GetTempPath(), "armada_cursor_readonly_" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(worktreePath);

                    try
                    {
                        Captain captain = new Captain("readonly-captain", AgentRuntimeEnum.Cursor)
                        {
                            Model = "cursor-model"
                        };

                        Mission readOnly = new Mission("Read-only mission")
                        {
                            Persona = "Worker",
                            Mode = MissionModeEnum.Audit,
                            BranchName = "feature/readonly"
                        };

                        Dock dock = new Dock
                        {
                            BranchName = "feature/readonly",
                            WorktreePath = worktreePath
                        };
                        string logFilePath = Path.Combine(settings.LogDirectory, "missions", readOnly.Id + ".log");

                        int processId = await handler.HandleLaunchAgentAsync(captain, readOnly, dock).ConfigureAwait(false);
                        string logContents = await WaitForFileContainsAsync(logFilePath, "Mission: Read-only mission").ConfigureAwait(false);

                        AssertTrue(processId > 0, "Launch should return a process id");
                        AssertFalse(logContents.Contains("IMPORTANT", StringComparison.OrdinalIgnoreCase),
                            "a read-only mission must not receive commit-trailer instructions");
                        AssertFalse(logContents.Contains("append the following trailers", StringComparison.OrdinalIgnoreCase),
                            "the commit-trailer block must be absent from a read-only launch prompt");
                    }
                    finally
                    {
                        try { Directory.Delete(worktreePath, true); } catch { }
                    }
                }
            });

            await RunTest("HandleLaunchAgentAsync persists returned process id before returning", async () =>
            {                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                using (CursorShimScope shim = CursorShimScope.Create())
                {
                    AgentLifecycleHandler handler = CreateHandler(testDb.Driver, out _);
                    string worktreePath = Path.Combine(Path.GetTempPath(), "armada_cursor_pid_launch_" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(worktreePath);

                    try
                    {
                        Mission mission = new Mission("Launch PID mission")
                        {
                            Persona = "Worker",
                            BranchName = "feature/pid-registration",
                            Status = MissionStatusEnum.Assigned,
                            AssignmentState = MissionAssignmentStateEnum.Assigned
                        };

                        Captain captain = new Captain("launch-pid-captain", AgentRuntimeEnum.Cursor)
                        {
                            Model = "slow-launch-model",
                            State = CaptainStateEnum.Working,
                            CurrentMissionId = mission.Id
                        };

                        mission.CaptainId = captain.Id;

                        await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);
                        await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                        Dock dock = new Dock
                        {
                            BranchName = "feature/pid-registration",
                            WorktreePath = worktreePath
                        };

                        int processId = await handler.HandleLaunchAgentAsync(captain, mission, dock).ConfigureAwait(false);

                        Captain? persistedCaptain = await testDb.Driver.Captains.ReadAsync(captain.Id).ConfigureAwait(false);
                        Mission? persistedMission = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);

                        AssertTrue(processId > 0, "Launch should return a process id");
                        AssertNotNull(persistedCaptain, "Captain row should still exist");
                        AssertNotNull(persistedMission, "Mission row should still exist");
                        AssertEqual(processId, persistedCaptain!.ProcessId, "Captain row should have the returned process id before launch returns");
                        AssertEqual(processId, persistedMission!.ProcessId, "Mission row should have the returned process id before launch returns");
                    }
                    finally
                    {
                        try { Directory.Delete(worktreePath, true); } catch { }
                    }
                }
            });

            await RunTest("PersistStartedProcessIdAsync ExistingDifferentMissionProcessId LeavesRowsUnchanged", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    AgentLifecycleHandler handler = CreateHandler(testDb.Driver, out _);
                    Mission mission = new Mission("Existing PID mission")
                    {
                        Status = MissionStatusEnum.InProgress,
                        ProcessId = 111111
                    };

                    Captain captain = new Captain("existing-pid-captain", AgentRuntimeEnum.Cursor)
                    {
                        State = CaptainStateEnum.Working,
                        CurrentMissionId = mission.Id,
                        ProcessId = null
                    };

                    mission.CaptainId = captain.Id;

                    await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);
                    await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                    string launchKey = captain.Id + ":" + mission.Id;
                    RegisterPendingLaunch(handler, launchKey, captain.Id, mission.Id);
                    await InvokePersistStartedProcessIdAsync(handler, 222222, launchKey).ConfigureAwait(false);

                    Captain? persistedCaptain = await testDb.Driver.Captains.ReadAsync(captain.Id).ConfigureAwait(false);
                    Mission? persistedMission = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);

                    AssertNotNull(persistedCaptain, "Captain row should still exist");
                    AssertNotNull(persistedMission, "Mission row should still exist");
                    AssertNull(persistedCaptain!.ProcessId, "Persistence must not record a new PID when the mission already has a different PID");
                    AssertEqual(111111, persistedMission!.ProcessId, "Persistence must not overwrite an existing different mission PID");
                    AssertNull(persistedMission.StartedUtc, "Rejected PID persistence should not mark the mission started");
                }
            });

            await RunTest("HandleAgentHeartbeat updates mission and voyage timestamps", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    AgentLifecycleHandler handler = CreateHandler(testDb.Driver, out _);

                    Captain captain = new Captain("heartbeat-captain", AgentRuntimeEnum.Cursor);
                    await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);

                    Voyage voyage = new Voyage("Heartbeat voyage", "Telemetry proof");
                    await testDb.Driver.Voyages.CreateAsync(voyage).ConfigureAwait(false);

                    Mission mission = new Mission("Heartbeat mission")
                    {
                        VoyageId = voyage.Id
                    };
                    await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                    Mission? beforeMission = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    Voyage? beforeVoyage = await testDb.Driver.Voyages.ReadAsync(voyage.Id).ConfigureAwait(false);
                    AssertNotNull(beforeMission);
                    AssertNotNull(beforeVoyage);

                    RegisterTrackedProcess(handler, 424242, captain.Id, mission.Id);

                    await Task.Delay(20).ConfigureAwait(false);
                    handler.HandleAgentHeartbeat(424242, "still running");

                    await WaitForConditionAsync(async () =>
                    {
                        Captain? refreshedCaptain = await testDb.Driver.Captains.ReadAsync(captain.Id).ConfigureAwait(false);
                        Mission? refreshedMission = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                        Voyage? refreshedVoyage = await testDb.Driver.Voyages.ReadAsync(voyage.Id).ConfigureAwait(false);

                        return refreshedCaptain?.LastHeartbeatUtc.HasValue == true
                            && refreshedMission != null
                            && refreshedMission.LastUpdateUtc > beforeMission!.LastUpdateUtc
                            && refreshedVoyage != null
                            && refreshedVoyage.LastUpdateUtc > beforeVoyage!.LastUpdateUtc;
                    }).ConfigureAwait(false);
                }
            });

            // A silent-but-running process must keep telemetry fresh WITHOUT advancing the captain's
            // output heartbeat: stall detection measures that value's age, so refreshing it here is
            // what stops a silent agent from ever being detected as stalled. This test used to assert
            // LastHeartbeatUtc was set, which pinned exactly that masking behaviour as intended.
            await RunTest("Silent running process refreshes liveness telemetry without masking a stall", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    AgentLifecycleHandler handler = CreateHandler(testDb.Driver, out ArmadaSettings settings);
                    settings.HeartbeatIntervalSeconds = 5;
                    // Several liveness ticks fit well inside the wait below; the production floor is five seconds.
                    handler.ProcessLivenessInterval = TimeSpan.FromMilliseconds(200);

                    Captain captain = new Captain("silent-heartbeat-captain", AgentRuntimeEnum.Cursor);
                    await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);

                    Voyage voyage = new Voyage("Silent heartbeat voyage", "Telemetry proof");
                    await testDb.Driver.Voyages.CreateAsync(voyage).ConfigureAwait(false);

                    Mission mission = new Mission("Silent heartbeat mission")
                    {
                        VoyageId = voyage.Id
                    };
                    await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                    Mission? beforeMission = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    Voyage? beforeVoyage = await testDb.Driver.Voyages.ReadAsync(voyage.Id).ConfigureAwait(false);
                    AssertNotNull(beforeMission);
                    AssertNotNull(beforeVoyage);

                    using Process process = StartSilentProcess();
                    RegisterTrackedProcess(handler, process.Id, captain.Id, mission.Id);
                    StartTrackedProcessHeartbeat(handler, process.Id, captain.Id, mission.Id);

                    try
                    {
                        await WaitForConditionAsync(async () =>
                        {
                            Captain? refreshedCaptain = await testDb.Driver.Captains.ReadAsync(captain.Id).ConfigureAwait(false);
                            Mission? refreshedMission = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                            Voyage? refreshedVoyage = await testDb.Driver.Voyages.ReadAsync(voyage.Id).ConfigureAwait(false);

                            return refreshedCaptain?.LastProcessAliveUtc.HasValue == true
                                && refreshedMission != null
                                && refreshedMission.LastUpdateUtc > beforeMission!.LastUpdateUtc
                                && refreshedVoyage != null
                                && refreshedVoyage.LastUpdateUtc > beforeVoyage!.LastUpdateUtc;
                        }, TimeSpan.FromSeconds(8)).ConfigureAwait(false);

                        // The captain produced no output, so the value stall detection reads must
                        // still be unset. If the liveness loop advanced it, a silent agent would
                        // look freshly active for as long as its process stayed up.
                        Captain? silentCaptain = await testDb.Driver.Captains.ReadAsync(captain.Id).ConfigureAwait(false);
                        AssertNotNull(silentCaptain);
                        Assert(
                            !silentCaptain!.LastHeartbeatUtc.HasValue,
                            "A silent process must not advance the captain output heartbeat");
                    }
                    finally
                    {
                        try
                        {
                            if (!process.HasExited)
                            {
                                process.Kill(entireProcessTree: true);
                                process.WaitForExit(5000);
                            }
                        }
                        catch { }
                    }
                }
            });

            await RunTest("StartProcessLivenessHeartbeat_WhenCtsDisposedDuringDelay_DoesNotFaultTask", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    AgentLifecycleHandler handler = CreateHandler(testDb.Driver, out ArmadaSettings settings);
                    settings.HeartbeatIntervalSeconds = 5;

                    Captain captain = new Captain("dispose-heartbeat-captain", AgentRuntimeEnum.Cursor);
                    await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);

                    Mission mission = new Mission("Dispose heartbeat mission");
                    await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                    Exception? unobservedException = null;
                    EventHandler<UnobservedTaskExceptionEventArgs> unobservedHandler = (_, args) =>
                    {
                        if (unobservedException == null)
                        {
                            unobservedException = args.Exception;
                        }

                        args.SetObserved();
                    };

                    TaskScheduler.UnobservedTaskException += unobservedHandler;
                    using Process process = StartSilentProcess();
                    try
                    {
                        RegisterTrackedProcess(handler, process.Id, captain.Id, mission.Id);
                        StartTrackedProcessHeartbeat(handler, process.Id, captain.Id, mission.Id);

                        StopTrackedProcessHeartbeat(handler, process.Id);

                        await WaitForConditionAsync(async () =>
                        {
                            await Task.CompletedTask.ConfigureAwait(false);
                            return !HasProcessHeartbeatLoop(handler, process.Id);
                        }, TimeSpan.FromSeconds(3)).ConfigureAwait(false);

                        await Task.Delay(200).ConfigureAwait(false);
                        GC.Collect();
                        GC.WaitForPendingFinalizers();

                        AssertNull(unobservedException, "Disposing heartbeat CTS during delay should not surface an unobserved exception");
                    }
                    finally
                    {
                        TaskScheduler.UnobservedTaskException -= unobservedHandler;
                        try
                        {
                            if (!process.HasExited)
                            {
                                process.Kill(entireProcessTree: true);
                                process.WaitForExit(5000);
                            }
                        }
                        catch { }
                    }
                }
            });

            await RunTest("HandleAgentOutput bounds streamed output and retains tail with truncation marker", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    AgentLifecycleHandler handler = CreateHandler(testDb.Driver, out _);
                    int processId = 919191;
                    string missionId = "msn_bounded_output_capture";
                    RegisterTrackedProcess(handler, processId, "captain-bounded", missionId);

                    // Each line is ~1 KiB; feeding 4096 of them is ~4 MiB total -- well past the 256 KiB cap.
                    string filler = new string('a', 1024);
                    int totalLines = 4096;
                    for (int i = 0; i < totalLines; i++)
                    {
                        handler.HandleAgentOutput(processId, "line " + i + " " + filler);
                    }

                    string? output = handler.GetAndClearMissionOutput(missionId);

                    AssertNotNull(output, "Streamed output should still be available after bounded appends");
                    AssertTrue(output!.Length < 1024 * 1024, "Bounded output must not retain multi-MiB transcripts");
                    AssertContains("[ARMADA: streamed output truncated to retain tail]", output, "Truncation marker should be inserted when buffer is clipped");
                    AssertContains("line " + (totalLines - 1), output, "Most recent tail should be retained");
                    AssertFalse(output.Contains("line 0 ", StringComparison.Ordinal), "Older head content should be dropped to preserve memory cap");
                }
            });

            await RunTest("HandleAgentOutput bounds single oversized runtime chunk", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    AgentLifecycleHandler handler = CreateHandler(testDb.Driver, out _);
                    int processId = 929292;
                    string missionId = "msn_single_oversized_output_capture";
                    RegisterTrackedProcess(handler, processId, "captain-single-oversized", missionId);

                    string oversizedChunk = "single oversized chunk " + new string('b', 2 * 1024 * 1024);
                    handler.HandleAgentOutput(processId, oversizedChunk);

                    string? output = handler.GetAndClearMissionOutput(missionId);

                    AssertNotNull(output, "Streamed output should still be available after a single oversized append");
                    AssertTrue(output!.Length < 1024 * 1024, "A single runtime output chunk must still obey the mission output cap");
                    AssertContains("[ARMADA: streamed output truncated to retain tail]", output, "Truncation marker should be inserted for a single oversized chunk");
                    AssertFalse(output.Contains("single oversized chunk", StringComparison.Ordinal), "Older head content from the oversized chunk should be dropped");
                }
            });

            await RunTest("DiscardUnclaimedMissionOutput removes streamed buffer and final-message artifact", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    AgentLifecycleHandler handler = CreateHandler(testDb.Driver, out _);
                    string missionId = "msn_discard_unclaimed_output";
                    string artifactDirectory = Path.Combine(Path.GetTempPath(), "armada_discard_output_" + Guid.NewGuid().ToString("N"));
                    string artifactPath = Path.Combine(artifactDirectory, missionId + ".txt");
                    Directory.CreateDirectory(artifactDirectory);

                    try
                    {
                        SeedMissionOutput(handler, missionId, "leftover streamed output");
                        RegisterFinalMessageArtifact(handler, missionId, artifactPath);
                        await File.WriteAllTextAsync(artifactPath, "leftover final message").ConfigureAwait(false);

                        handler.DiscardUnclaimedMissionOutput(missionId);

                        // After discard, retrieval should observe nothing -- buffer cleared and file removed.
                        string? output = handler.GetAndClearMissionOutput(missionId);
                        AssertNull(output, "DiscardUnclaimedMissionOutput should leave no streamed output behind");
                        AssertFalse(File.Exists(artifactPath), "DiscardUnclaimedMissionOutput should delete the final-message artifact");
                    }
                    finally
                    {
                        try { Directory.Delete(artifactDirectory, true); } catch { }
                    }
                }
            });

            await RunTest("HandleAgentOutput stores a papercut in the mission owner's scope", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    AgentLifecycleHandler handler = CreateHandler(testDb.Driver, out _);
                    string tenantId = Armada.Core.Constants.DefaultTenantId;
                    string userId = Armada.Core.Constants.DefaultUserId;

                    Vessel vessel = new Vessel("PapercutScopeVessel", "https://github.com/test/papercut-scope");
                    Voyage voyage = new Voyage("Papercut scope voyage");
                    Captain captain = new Captain("papercut-scope-captain", AgentRuntimeEnum.Cursor);
                    Mission mission = new Mission("Papercut scope mission");
                    mission.TenantId = tenantId;
                    mission.UserId = userId;
                    mission.VesselId = vessel.Id;
                    mission.VoyageId = voyage.Id;
                    mission.Persona = "Worker";
                    mission.CaptainId = captain.Id;
                    captain.CurrentMissionId = mission.Id;

                    await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);
                    await testDb.Driver.Voyages.CreateAsync(voyage).ConfigureAwait(false);
                    await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);
                    await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                    int processId = 838383;
                    RegisterTrackedProcess(handler, processId, captain.Id, mission.Id);
                    handler.HandleAgentOutput(
                        processId,
                        "[ARMADA:PAPERCUT] {\"category\":\"MissingDoc\",\"severity\":\"Low\",\"title\":\"Scope check\",\"path\":\"README.md\"}");

                    List<ArmadaEvent> stored = await WaitForPapercutEventsAsync(testDb.Driver, 1).ConfigureAwait(false);
                    AssertEqual(1, stored.Count, "The marker line should produce exactly one papercut event");

                    EnumerationQuery query = new EnumerationQuery { MissionId = mission.Id, EventType = PapercutParser.EventType, PageNumber = 1, PageSize = 10 };
                    EnumerationResult<ArmadaEvent> owner = await testDb.Driver.Events.EnumerateAsync(tenantId, userId, query).ConfigureAwait(false);
                    AssertEqual(1, owner.Objects.Count, "The mission owner's scoped read must find the papercut");
                    EnumerationResult<ArmadaEvent> other = await testDb.Driver.Events.EnumerateAsync(tenantId, "usr_other_owner", query).ConfigureAwait(false);
                    AssertEqual(0, other.Objects.Count, "Another user's scoped read must not find the papercut");
                }
            });

            await RunTest("HandleAgentOutput stores a papercut marker as an event", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    AgentLifecycleHandler handler = CreateHandler(testDb.Driver, out _);

                    Vessel vessel = new Vessel("PapercutVessel", "https://github.com/test/papercut");
                    Voyage voyage = new Voyage("Papercut voyage");
                    Captain captain = new Captain("papercut-captain", AgentRuntimeEnum.Cursor);
                    Mission mission = new Mission("Papercut mission");
                    mission.VesselId = vessel.Id;
                    mission.VoyageId = voyage.Id;
                    mission.Persona = "Worker";
                    mission.CaptainId = captain.Id;
                    captain.CurrentMissionId = mission.Id;

                    await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);
                    await testDb.Driver.Voyages.CreateAsync(voyage).ConfigureAwait(false);
                    await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);
                    await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                    int processId = 828282;
                    RegisterTrackedProcess(handler, processId, captain.Id, mission.Id);

                    handler.HandleAgentOutput(
                        processId,
                        "[ARMADA:PAPERCUT] {\"category\":\"MissingDoc\",\"severity\":\"Medium\",\"title\":\"README names a build command that does not exist\",\"path\":\"README.md\"}");

                    List<ArmadaEvent> stored = await WaitForPapercutEventsAsync(testDb.Driver, 1).ConfigureAwait(false);

                    AssertEqual(1, stored.Count, "The marker line should produce exactly one papercut event");

                    Papercut? papercut = PapercutService.TryFromEvent(stored[0]);
                    AssertNotNull(papercut, "The stored event should read back as a papercut");
                    AssertEqual(PapercutCategoryEnum.MissingDoc, papercut!.Category);
                    AssertEqual(PapercutSeverityEnum.Medium, papercut.Severity);
                    AssertEqual("README.md", papercut.Path);

                    // The admiral supplies the context, never the captain.
                    AssertEqual(mission.Id, papercut.MissionId);
                    AssertEqual(captain.Id, papercut.CaptainId);
                    AssertEqual(vessel.Id, papercut.VesselId);
                    AssertEqual(voyage.Id, papercut.VoyageId);
                    AssertEqual("Cursor", papercut.Runtime);

                    // A papercut is not progress: it must not reach the progress signal stream.
                    List<Signal> signals = await testDb.Driver.Signals.EnumerateRecentAsync(50).ConfigureAwait(false);
                    AssertEqual(0, signals.Count, "A papercut must not be recorded as a progress signal");
                }
            });

            await RunTest("HandleAgentOutput ignores a papercut from a judge mission", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    AgentLifecycleHandler handler = CreateHandler(testDb.Driver, out _);

                    Vessel vessel = new Vessel("PapercutJudgeVessel", "https://github.com/test/papercut-judge");
                    Captain captain = new Captain("papercut-judge-captain", AgentRuntimeEnum.Cursor);
                    Mission mission = new Mission("Judge mission");
                    mission.VesselId = vessel.Id;
                    mission.Persona = "Judge";
                    mission.CaptainId = captain.Id;
                    captain.CurrentMissionId = mission.Id;

                    await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);
                    await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);
                    await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                    int processId = 838383;
                    RegisterTrackedProcess(handler, processId, captain.Id, mission.Id);

                    handler.HandleAgentOutput(
                        processId,
                        "[ARMADA:PAPERCUT] {\"category\":\"RepoFriction\",\"severity\":\"Low\",\"title\":\"the code under review is hard to follow\"}");

                    await Task.Delay(750).ConfigureAwait(false);

                    List<ArmadaEvent> stored = await testDb.Driver.Events
                        .EnumerateByTypeAsync(PapercutParser.EventType, 50)
                        .ConfigureAwait(false);

                    AssertEqual(0, stored.Count, "A judge reports through its verdict, not through papercuts");
                }
            });

            await RunTest("HandleAgentOutput caps stored papercuts per mission", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    AgentLifecycleHandler handler = CreateHandler(testDb.Driver, out _);

                    Vessel vessel = new Vessel("PapercutCapVessel", "https://github.com/test/papercut-cap");
                    Captain captain = new Captain("papercut-cap-captain", AgentRuntimeEnum.Cursor);
                    Mission mission = new Mission("Capped mission");
                    mission.VesselId = vessel.Id;
                    mission.Persona = "Worker";
                    mission.CaptainId = captain.Id;
                    captain.CurrentMissionId = mission.Id;

                    await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);
                    await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);
                    await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                    int processId = 848484;
                    RegisterTrackedProcess(handler, processId, captain.Id, mission.Id);

                    for (int i = 0; i < 18; i++)
                    {
                        handler.HandleAgentOutput(
                            processId,
                            "[ARMADA:PAPERCUT] {\"category\":\"Other\",\"severity\":\"Low\",\"title\":\"complaint number " + i + "\"}");
                    }

                    List<ArmadaEvent> stored = await WaitForPapercutEventsAsync(testDb.Driver, 10).ConfigureAwait(false);
                    await Task.Delay(500).ConfigureAwait(false);

                    stored = await testDb.Driver.Events
                        .EnumerateByTypeAsync(PapercutParser.EventType, 50)
                        .ConfigureAwait(false);

                    AssertEqual(10, stored.Count, "The per-mission cap must bound how many reports one mission can store");
                }
            });

            await RunTest("GetAndClearMissionOutput prefers final message artifact over streamed output", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    AgentLifecycleHandler handler = CreateHandler(testDb.Driver, out _);
                    string missionId = "msn_final_output_prefers_artifact";
                    string artifactDirectory = Path.Combine(Path.GetTempPath(), "armada_final_output_" + Guid.NewGuid().ToString("N"));
                    string artifactPath = Path.Combine(artifactDirectory, missionId + ".txt");
                    Directory.CreateDirectory(artifactDirectory);

                    try
                    {
                        SeedMissionOutput(handler, missionId, "streamed intermediate output");
                        RegisterFinalMessageArtifact(handler, missionId, artifactPath);
                        await File.WriteAllTextAsync(artifactPath, "[ARMADA:RESULT] COMPLETE\ncanonical final response").ConfigureAwait(false);

                        string? output = handler.GetAndClearMissionOutput(missionId);

                        AssertNotNull(output);
                        AssertContains("canonical final response", output!, "Canonical final response should win over streamed output");
                        AssertFalse(output!.Contains("streamed intermediate output", StringComparison.Ordinal), "Stream noise should not be persisted as AgentOutput when a final artifact exists");
                        AssertFalse(File.Exists(artifactPath), "Final message artifact should be deleted after retrieval");
                    }
                    finally
                    {
                        try { Directory.Delete(artifactDirectory, true); } catch { }
                    }
                }
            });

            await RunTest("HandleAgentOutput_ToolActivitySignal_RecordsProviderProgress", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    AgentLifecycleHandler handler = CreateHandler(testDb.Driver, out _);
                    ProviderProgressTracker tracker = new ProviderProgressTracker();
                    handler.SetProviderProgress(tracker);

                    Vessel vessel = new Vessel("ActivityVessel", "https://github.com/test/activity");
                    Voyage voyage = new Voyage("Activity voyage");
                    Captain captain = new Captain("activity-captain", AgentRuntimeEnum.ClaudeCode);
                    Mission mission = new Mission("Activity mission");
                    mission.VesselId = vessel.Id;
                    mission.VoyageId = voyage.Id;
                    mission.Persona = "Judge";
                    mission.CaptainId = captain.Id;
                    captain.CurrentMissionId = mission.Id;

                    await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);
                    await testDb.Driver.Voyages.CreateAsync(voyage).ConfigureAwait(false);
                    await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);
                    await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                    int processId = 929292;
                    RegisterTrackedProcess(handler, processId, captain.Id, mission.Id);

                    // A Judge running a long foreground tool call (a test suite) emits activity lines
                    // while its token-usage narration is quiet. That activity must refresh provider
                    // progress so the captain is not misclassified as a provider_silent_stall.
                    handler.HandleAgentOutput(processId, "[ARMADA:ACTIVITY] tool bash dotnet test src/Foo.Tests (ok)");

                    bool recorded = tracker.TryGet(captain.Id, out DateTime? progressUtc);
                    AssertTrue(recorded && progressUtc.HasValue,
                        "A tool-activity signal must refresh provider progress, or an actively-working captain is nudged mid-work");
                }
            });

            await RunTest("A process that outlives its first terminal marker is stopped after the grace period and completes cleanly", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    StopRecordingRuntime runtime = new StopRecordingRuntime();
                    TaskCompletionSource<int?> exitSeen = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously);
                    StubAdmiralService admiral = new StubAdmiralService();
                    admiral.OnHandleProcessExit = (pid, code, cpt, msn) =>
                    {
                        exitSeen.TrySetResult(code);
                        return Task.CompletedTask;
                    };
                    AgentLifecycleHandler handler = CreateHandler(testDb.Driver, out ArmadaSettings settings, null, admiral,
                        new StopRecordingRuntimeFactory(CreateLogging(), runtime));
                    settings.AutonomousRecovery.TerminalMarkerGraceSeconds = 60;
                    TerminalMarkerTracker markers = new TerminalMarkerTracker();
                    handler.SetTerminalMarkers(markers);

                    Captain captain = await testDb.Driver.Captains.CreateAsync(new Captain("verdict-captain", AgentRuntimeEnum.ClaudeCode)).ConfigureAwait(false);
                    Mission mission = await testDb.Driver.Missions.CreateAsync(new Mission("Judge mission") { Persona = "Judge", CaptainId = captain.Id }).ConfigureAwait(false);

                    int processId = 939393;
                    RegisterTrackedProcess(handler, processId, captain.Id, mission.Id);

                    handler.HandleAgentOutput(processId, "## Verdict");
                    AssertFalse(markers.TryGet(mission.Id, out _), "Prose is not a terminal marker.");

                    handler.HandleAgentOutput(processId, "[ARMADA:VERDICT] PASS");
                    handler.HandleAgentOutput(processId, "[ARMADA:VERDICT] NEEDS_REVISION");
                    AssertTrue(markers.TryGet(mission.Id, out TerminalMarkerRecord? first), "The verdict line is recorded.");
                    AssertEqual("PASS", first!.Value, "A later re-review never replaces the first verdict.");

                    bool stoppedEarly = await handler.EnforceTerminalMarkerGraceAsync(
                        processId, captain.Id, mission.Id, first.FirstSeenUtc.AddSeconds(30)).ConfigureAwait(false);
                    AssertFalse(stoppedEarly, "Inside the grace period the process may still exit on its own.");
                    AssertEqual(0, runtime.StopCalls.Count);

                    bool stopped = await handler.EnforceTerminalMarkerGraceAsync(
                        processId, captain.Id, mission.Id, first.FirstSeenUtc.AddSeconds(61)).ConfigureAwait(false);
                    AssertTrue(stopped, "After the grace period the handler stops the process.");
                    AssertEqual(1, runtime.StopCalls.Count);
                    AssertEqual(processId, runtime.StopCalls[0]);

                    // The stop kills the process, which reports a non-zero exit. It must still complete.
                    handler.HandleAgentProcessExited(processId, 137);
                    Task finished = await Task.WhenAny(exitSeen.Task, Task.Delay(TimeSpan.FromSeconds(10))).ConfigureAwait(false);
                    AssertTrue(finished == exitSeen.Task, "The exit reaches the admiral.");
                    AssertEqual(0, exitSeen.Task.Result, "A process stopped after its terminal marker completes as a clean exit.");
                    AssertFalse(markers.TryGet(mission.Id, out _), "The marker is cleared once the process has exited.");
                }
            });

            // ----------------------------------------------------------------
            // Agent status markers report progress only; completion runs its checks
            // ----------------------------------------------------------------

            await RunTest("Agent status markers cannot move a mission to a post-work or terminal status", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    AgentLifecycleHandler handler = CreateHandler(testDb.Driver, out _);
                    Captain captain = await testDb.Driver.Captains.CreateAsync(new Captain("status-captain", AgentRuntimeEnum.ClaudeCode)).ConfigureAwait(false);
                    Mission mission = await testDb.Driver.Missions.CreateAsync(
                        new Mission("Status marker mission") { Status = MissionStatusEnum.InProgress, CaptainId = captain.Id }).ConfigureAwait(false);

                    int processId = 949494;
                    RegisterTrackedProcess(handler, processId, captain.Id, mission.Id);

                    MissionStatusEnum[] refused = new[]
                    {
                        MissionStatusEnum.WorkProduced, MissionStatusEnum.Complete, MissionStatusEnum.Failed,
                        MissionStatusEnum.Cancelled, MissionStatusEnum.PullRequestOpen, MissionStatusEnum.LandingFailed
                    };
                    int expectedSignals = 0;
                    foreach (MissionStatusEnum status in refused)
                    {
                        handler.HandleAgentOutput(processId, "[ARMADA:STATUS] " + status);
                        expectedSignals++;
                        await WaitForProgressSignalsAsync(testDb.Driver, captain.Id, expectedSignals).ConfigureAwait(false);
                        Mission? afterRefused = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                        AssertEqual(MissionStatusEnum.InProgress, afterRefused!.Status, "An agent status marker must not set " + status);
                    }

                    // A progress status is still applied, and from Testing a terminal marker is still refused.
                    handler.HandleAgentOutput(processId, "[ARMADA:STATUS] Testing");
                    expectedSignals++;
                    await WaitForProgressSignalsAsync(testDb.Driver, captain.Id, expectedSignals).ConfigureAwait(false);
                    Mission? testing = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertEqual(MissionStatusEnum.Testing, testing!.Status, "A progress status marker is applied");

                    handler.HandleAgentOutput(processId, "[ARMADA:STATUS] Complete");
                    expectedSignals++;
                    await WaitForProgressSignalsAsync(testDb.Driver, captain.Id, expectedSignals).ConfigureAwait(false);
                    Mission? stillTesting = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertEqual(MissionStatusEnum.Testing, stillTesting!.Status, "Testing to Complete is not an agent-reportable transition");
                }
            });

            await RunTest("Completion still runs after a captain emits a Complete status marker", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    AgentLifecycleHandler handler = CreateHandler(testDb.Driver, out ArmadaSettings settings);
                    StubGitService git = new StubGitService();
                    LoggingModule logging = CreateLogging();
                    IDockService dockService = new DockService(logging, testDb.Driver, settings, git);
                    ICaptainService captainService = new CaptainService(logging, testDb.Driver, settings, git, dockService);
                    IMissionService missionService = new MissionService(logging, testDb.Driver, settings, dockService, captainService,
                        resourcePressureAdmission: global::Test.Shared.Infrastructure.TestResourcePressure.Unconstrained(settings));

                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(new Vessel("status-vessel", "https://github.com/test/status.git")).ConfigureAwait(false);
                    Captain captain = new Captain("completion-captain", AgentRuntimeEnum.ClaudeCode);
                    captain.State = CaptainStateEnum.Working;
                    await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);
                    Dock dock = new Dock(vessel.Id);
                    dock.CaptainId = captain.Id;
                    dock.WorktreePath = Path.Combine(Path.GetTempPath(), "armada_test_wt_" + Guid.NewGuid().ToString("N"));
                    dock.BranchName = "armada/completion-captain/msn_status";
                    dock.Active = true;
                    await testDb.Driver.Docks.CreateAsync(dock).ConfigureAwait(false);
                    Mission mission = new Mission("Completion gate mission");
                    mission.Status = MissionStatusEnum.InProgress;
                    mission.CaptainId = captain.Id;
                    mission.DockId = dock.Id;
                    mission.VesselId = vessel.Id;
                    await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);
                    captain.CurrentMissionId = mission.Id;
                    captain.CurrentDockId = dock.Id;
                    await testDb.Driver.Captains.UpdateAsync(captain).ConfigureAwait(false);

                    int processId = 959595;
                    RegisterTrackedProcess(handler, processId, captain.Id, mission.Id);
                    handler.HandleAgentOutput(processId, "[ARMADA:STATUS] Complete");
                    await WaitForProgressSignalsAsync(testDb.Driver, captain.Id, 1).ConfigureAwait(false);

                    // The exit path's completion handler skips a mission already in a post-work or
                    // terminal status, so the marker must leave the mission where completion can act.
                    await missionService.HandleCompletionAsync(captain, mission.Id).ConfigureAwait(false);

                    Mission? completed = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertEqual(MissionStatusEnum.WorkProduced, completed!.Status, "Completion runs and hands the work to landing");
                    Captain? released = await testDb.Driver.Captains.ReadAsync(captain.Id).ConfigureAwait(false);
                    AssertEqual(CaptainStateEnum.Idle, released!.State, "Completion releases the captain");
                }
            });

            // ----------------------------------------------------------------
            // One output record can carry several markers; each one is routed
            // ----------------------------------------------------------------

            await RunTest("A message before the result in one record still records the terminal marker", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    AgentLifecycleHandler handler = CreateHandler(testDb.Driver, out _);
                    TerminalMarkerTracker markers = new TerminalMarkerTracker();
                    handler.SetTerminalMarkers(markers);
                    Captain captain = await testDb.Driver.Captains.CreateAsync(new Captain("record-captain", AgentRuntimeEnum.Codex)).ConfigureAwait(false);
                    Mission mission = await testDb.Driver.Missions.CreateAsync(
                        new Mission("Record mission") { Status = MissionStatusEnum.InProgress, CaptainId = captain.Id }).ConfigureAwait(false);

                    int processId = 969696;
                    RegisterTrackedProcess(handler, processId, captain.Id, mission.Id);
                    handler.HandleAgentOutput(processId, "[ARMADA:MESSAGE] Wired the rows and ran the suite\n\n[ARMADA:RESULT] COMPLETE\nSummary follows.");

                    AssertTrue(markers.TryGet(mission.Id, out TerminalMarkerRecord? first), "The result marker after a message is recorded");
                    AssertEqual("COMPLETE", first!.Value);
                    await WaitForProgressSignalsAsync(testDb.Driver, captain.Id, 2).ConfigureAwait(false);
                }
            });

            await RunTest("A papercut and a verdict in one record are both routed", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    AgentLifecycleHandler handler = CreateHandler(testDb.Driver, out _);
                    TerminalMarkerTracker markers = new TerminalMarkerTracker();
                    handler.SetTerminalMarkers(markers);
                    Captain captain = await testDb.Driver.Captains.CreateAsync(new Captain("papercut-verdict-captain", AgentRuntimeEnum.ClaudeCode)).ConfigureAwait(false);
                    Mission mission = await testDb.Driver.Missions.CreateAsync(
                        new Mission("Papercut verdict mission") { Status = MissionStatusEnum.InProgress, CaptainId = captain.Id, Persona = "Worker" }).ConfigureAwait(false);

                    int processId = 979797;
                    RegisterTrackedProcess(handler, processId, captain.Id, mission.Id);
                    handler.HandleAgentOutput(
                        processId,
                        "[ARMADA:PAPERCUT] {\"category\":\"MissingDoc\",\"severity\":\"Low\",\"title\":\"Record check\"}\n[ARMADA:VERDICT] PASS");

                    AssertTrue(markers.TryGet(mission.Id, out TerminalMarkerRecord? first), "The verdict after a papercut is recorded");
                    AssertEqual("PASS", first!.Value);
                    List<ArmadaEvent> stored = await WaitForPapercutEventsAsync(testDb.Driver, 1).ConfigureAwait(false);
                    AssertEqual(1, stored.Count, "The papercut is stored");
                }
            });

            await RunTest("MissionProcessOwnership_RequiresRegisteredCaptainGeneration", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    AgentLifecycleHandler handler = CreateHandler(testDb.Driver, out _);
                    Captain captain = new Captain("manual-proof-captain", AgentRuntimeEnum.ClaudeCode)
                    {
                        State = CaptainStateEnum.Working,
                        ProcessId = Environment.ProcessId
                    };
                    Mission mission = new Mission("manual-proof-process")
                    {
                        CaptainId = captain.Id,
                        Status = MissionStatusEnum.InProgress,
                        ProcessId = Environment.ProcessId,
                        StartedUtc = DateTime.UtcNow
                    };
                    captain.CurrentMissionId = mission.Id;
                    await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);
                    await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                    AssertFalse(await handler.IsMissionProcessActiveAsync(mission).ConfigureAwait(false),
                        "An unregistered process generation must fail closed");
                    RegisterTrackedProcess(handler, Environment.ProcessId, captain.Id, mission.Id);
                    AssertTrue(await handler.IsMissionProcessActiveAsync(mission).ConfigureAwait(false),
                        "A live process must be active only with matching captain and mission ownership");

                    captain.State = CaptainStateEnum.Quarantined;
                    await testDb.Driver.Captains.UpdateAsync(captain).ConfigureAwait(false);
                    AssertTrue(await handler.IsMissionProcessActiveAsync(mission).ConfigureAwait(false),
                        "A live owned process remains active while its captain is quarantined");

                    mission.ProcessId = null;
                    captain.ProcessId = null;
                    await testDb.Driver.Missions.UpdateAsync(mission).ConfigureAwait(false);
                    await testDb.Driver.Captains.UpdateAsync(captain).ConfigureAwait(false);
                    AssertTrue(await handler.IsMissionProcessActiveAsync(mission).ConfigureAwait(false),
                        "A registered live process remains active when persistence has not recorded its PID");

                    mission.ProcessId = Environment.ProcessId + 1;
                    captain.ProcessId = Environment.ProcessId + 1;
                    await testDb.Driver.Missions.UpdateAsync(mission).ConfigureAwait(false);
                    await testDb.Driver.Captains.UpdateAsync(captain).ConfigureAwait(false);
                    AssertTrue(await handler.IsMissionProcessActiveAsync(mission).ConfigureAwait(false),
                        "A registered live process remains active when persisted PIDs are stale");

                    int handledProcessId = Environment.ProcessId + 100000;
                    RegisterTrackedProcess(handler, handledProcessId, captain.Id, mission.Id);
                    MarkProcessExitHandled(handler, handledProcessId);
                    AssertTrue(await handler.IsMissionProcessActiveAsync(mission).ConfigureAwait(false),
                        "A handled stale mapping must not hide a second live mapping for the mission");
                }
            });

            await RunTest("MissionProcessOwnership_UnknownRuntimeLivenessFailsClosed", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    AgentLifecycleHandler handler = CreateHandler(testDb.Driver, out _);
                    Captain captain = new Captain("manual-unknown-runtime", AgentRuntimeEnum.Custom)
                    {
                        State = CaptainStateEnum.Working,
                        ProcessId = Environment.ProcessId
                    };
                    Mission mission = new Mission("manual-unknown-runtime-process")
                    {
                        CaptainId = captain.Id,
                        Status = MissionStatusEnum.InProgress,
                        ProcessId = Environment.ProcessId,
                        StartedUtc = DateTime.UtcNow
                    };
                    captain.CurrentMissionId = mission.Id;
                    await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);
                    await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);
                    RegisterTrackedProcess(handler, Environment.ProcessId, captain.Id, mission.Id);

                    InvalidOperationException? captured = null;
                    try
                    {
                        await handler.IsMissionProcessActiveAsync(mission).ConfigureAwait(false);
                    }
                    catch (InvalidOperationException ex)
                    {
                        captured = ex;
                    }
                    AssertNotNull(captured, "unsupported runtime liveness must be observable");
                    AssertEqual("manual_completion_process_liveness_unknown", captured!.Message,
                        "unsupported runtime has a stable fail-closed reason");
                }
            });

            await RunTest("MissionProcessOwnership_ApiEndpointSyntheticProcessFollowsItsLoop", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    AgentLifecycleHandler handler = CreateHandler(testDb.Driver, out _);
                    Captain captain = new Captain("manual-api-endpoint", AgentRuntimeEnum.ApiEndpoint)
                    {
                        State = CaptainStateEnum.Working
                    };
                    Mission mission = new Mission("manual-api-endpoint-process")
                    {
                        CaptainId = captain.Id,
                        Status = MissionStatusEnum.InProgress,
                        StartedUtc = DateTime.UtcNow
                    };
                    captain.CurrentMissionId = mission.Id;
                    await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);
                    await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                    int syntheticProcessId = Int32.MaxValue - 17;
                    RegisterTrackedProcess(handler, syntheticProcessId, captain.Id, mission.Id);
                    Armada.Core.ProcessSupervisor.RegisterSyntheticProcess(syntheticProcessId);
                    try
                    {
                        AssertTrue(await handler.IsMissionProcessActiveAsync(mission).ConfigureAwait(false),
                            "A running in-process API loop must count as a live mission process");
                    }
                    finally
                    {
                        Armada.Core.ProcessSupervisor.UnregisterSyntheticProcess(syntheticProcessId);
                    }

                    AssertFalse(await handler.IsMissionProcessActiveAsync(mission).ConfigureAwait(false),
                        "An API loop that has exited must not keep the mission process active");
                }
            });
        }

        private AgentLifecycleHandler CreateHandler(DatabaseDriver database, out ArmadaSettings settings, TimeSpan? modelValidationTimeout = null, IAdmiralService? admiralOverride = null, AgentRuntimeFactory? runtimeFactoryOverride = null, ISessionTokenService? sessionTokens = null)
        {
            LoggingModule logging = CreateLogging();
            settings = CreateSettings();
            AgentRuntimeFactory runtimeFactory = runtimeFactoryOverride ?? new AgentRuntimeFactory(logging);
            IAdmiralService admiral = admiralOverride ?? new StubAdmiralService();
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
                (eventType, message, entityType, entityId, captainId, missionId, vesselId, voyageId) => Task.CompletedTask,
                modelValidationTimeout,
                sessionTokens);
        }

        private static AuthenticationService CreateAuthenticationService(DatabaseDriver database, ISessionTokenService sessionTokens)
        {
            LoggingModule logging = CreateLogging();
            return new AuthenticationService(database, sessionTokens, new ArmadaSettings(), logging);
        }

        private sealed class StopRecordingRuntimeFactory : AgentRuntimeFactory
        {
            private readonly Armada.Runtimes.Interfaces.IAgentRuntime _Runtime;

            public StopRecordingRuntimeFactory(LoggingModule logging, Armada.Runtimes.Interfaces.IAgentRuntime runtime)
                : base(logging)
            {
                _Runtime = runtime;
            }

            public override Armada.Runtimes.Interfaces.IAgentRuntime Create(AgentRuntimeEnum runtimeType) => _Runtime;
        }

        private sealed class StopRecordingRuntime : Armada.Runtimes.Interfaces.IAgentRuntime
        {
            public List<int> StopCalls { get; } = new List<int>();

            public string Name => "StopRecording";

            public bool SupportsResume => false;

            public bool SupportsPlanningSessions => false;

            public event Action<int, string>? OnOutputReceived { add { } remove { } }

            public event Action<int, string>? OnStdoutReceived { add { } remove { } }

            public event Action<int, RuntimeTokenUsage>? OnTokenUsageReceived { add { } remove { } }

            public event Action<int, RuntimeTokenUsage>? OnProviderProgressReceived { add { } remove { } }

            public event Action<int>? OnProcessStarted { add { } remove { } }

            public event Action<int, int?>? OnProcessExited { add { } remove { } }

            public Task<int> StartAsync(
                string workingDirectory,
                string prompt,
                Dictionary<string, string>? environment = null,
                string? logFilePath = null,
                string? finalMessageFilePath = null,
                string? model = null,
                Captain? captain = null,
                bool showThinking = false,
                CancellationToken token = default,
                CaptainLaunchIsolationPlan? isolationPlan = null)
                => throw new NotSupportedException("This runtime only records stops.");

            public Task StopAsync(int processId, CancellationToken token = default)
            {
                lock (StopCalls) StopCalls.Add(processId);
                return Task.CompletedTask;
            }

            public Task<bool> IsRunningAsync(int processId, CancellationToken token = default) => Task.FromResult(true);
        }

        private static LoggingModule CreateLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        private static ArmadaSettings CreateSettings()
        {
            ArmadaSettings settings = new ArmadaSettings();
            settings.LogDirectory = Path.Combine(Path.GetTempPath(), "armada_lifecycle_logs_" + Guid.NewGuid().ToString("N"));
            return settings;
        }

        private static async Task<string> WaitForRecordedArgsAsync(string argsFile, string? expectedSubstring = null)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(5);

            while (DateTime.UtcNow < deadline)
            {
                if (File.Exists(argsFile))
                {
                    string contents = await File.ReadAllTextAsync(argsFile).ConfigureAwait(false);
                    if (!String.IsNullOrWhiteSpace(contents) &&
                        (String.IsNullOrEmpty(expectedSubstring) || contents.Contains(expectedSubstring, StringComparison.Ordinal)))
                    {
                        return contents;
                    }
                }

                await Task.Delay(50).ConfigureAwait(false);
            }

            throw new TimeoutException("Timed out waiting for runtime shim args file: " + argsFile);
        }

        private static async Task<string> WaitForFileContainsAsync(string path, string expectedSubstring)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(5);

            while (DateTime.UtcNow < deadline)
            {
                if (File.Exists(path))
                {
                    string contents = await ReadSharedTextAsync(path).ConfigureAwait(false);
                    if (contents.Contains(expectedSubstring, StringComparison.Ordinal))
                    {
                        return contents;
                    }
                }

                await Task.Delay(50).ConfigureAwait(false);
            }

            throw new TimeoutException("Timed out waiting for expected content in file: " + path);
        }

        private static async Task<string> ReadSharedTextAsync(string path)
        {
            using FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using StreamReader reader = new StreamReader(stream);
            return await reader.ReadToEndAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// Wait for stored papercut events. The handler writes them on a background task, so a read
        /// taken immediately after the output line races the write.
        /// </summary>
        private static async Task<List<ArmadaEvent>> WaitForPapercutEventsAsync(DatabaseDriver database, int expected)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(10);
            List<ArmadaEvent> stored = new List<ArmadaEvent>();

            while (DateTime.UtcNow < deadline)
            {
                stored = await database.Events
                    .EnumerateByTypeAsync(PapercutParser.EventType, 50)
                    .ConfigureAwait(false);

                if (stored.Count >= expected) return stored;
                await Task.Delay(100).ConfigureAwait(false);
            }

            return stored;
        }

        private static void RegisterTrackedProcess(AgentLifecycleHandler handler, int processId, string captainId, string missionId)
        {
            FieldInfo captainField = typeof(AgentLifecycleHandler).GetField("_ProcessToCaptain", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Could not find _ProcessToCaptain field");
            FieldInfo missionField = typeof(AgentLifecycleHandler).GetField("_ProcessToMission", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Could not find _ProcessToMission field");

            Dictionary<int, string> captainMap = (Dictionary<int, string>)(captainField.GetValue(handler)
                ?? throw new InvalidOperationException("Captain process map was null"));
            Dictionary<int, string> missionMap = (Dictionary<int, string>)(missionField.GetValue(handler)
                ?? throw new InvalidOperationException("Mission process map was null"));

            lock (captainMap)
            {
                captainMap[processId] = captainId;
                missionMap[processId] = missionId;
            }
        }

        private static void MarkProcessExitHandled(AgentLifecycleHandler handler, int processId)
        {
            FieldInfo handledField = typeof(AgentLifecycleHandler).GetField("_HandledProcessExits", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Could not find handled process map");
            ConcurrentDictionary<int, DateTime> handled = (ConcurrentDictionary<int, DateTime>)(handledField.GetValue(handler)
                ?? throw new InvalidOperationException("Handled process map was null"));
            handled[processId] = DateTime.UtcNow;
        }

        private static void RegisterPendingLaunch(AgentLifecycleHandler handler, string launchKey, string captainId, string missionId)
        {
            FieldInfo launchesField = typeof(AgentLifecycleHandler).GetField("_PendingLaunches", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Could not find _PendingLaunches field");

            object launches = launchesField.GetValue(handler)
                ?? throw new InvalidOperationException("Pending launches map was null");
            Type launchValueType = launches.GetType().GetGenericArguments()[1];
            object launchValue = Activator.CreateInstance(launchValueType, captainId, missionId)
                ?? throw new InvalidOperationException("Pending launch value could not be created");
            MethodInfo setItem = launches.GetType().GetProperty("Item")?.SetMethod
                ?? throw new InvalidOperationException("Could not find pending launch indexer");

            setItem.Invoke(launches, new object[] { launchKey, launchValue });
        }

        private static async Task InvokePersistStartedProcessIdAsync(AgentLifecycleHandler handler, int processId, string launchKey)
        {
            MethodInfo method = typeof(AgentLifecycleHandler).GetMethod("PersistStartedProcessIdAsync", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Could not find PersistStartedProcessIdAsync method");
            Task task = (Task)(method.Invoke(handler, new object[] { processId, launchKey })
                ?? throw new InvalidOperationException("PersistStartedProcessIdAsync did not return a task"));

            await task.ConfigureAwait(false);
        }

        private static void SeedMissionOutput(AgentLifecycleHandler handler, string missionId, string output)
        {
            FieldInfo outputField = typeof(AgentLifecycleHandler).GetField("_MissionOutput", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Could not find _MissionOutput field");

            System.Collections.Concurrent.ConcurrentDictionary<string, System.Text.StringBuilder> outputMap =
                (System.Collections.Concurrent.ConcurrentDictionary<string, System.Text.StringBuilder>)(outputField.GetValue(handler)
                ?? throw new InvalidOperationException("Mission output map was null"));

            outputMap[missionId] = new System.Text.StringBuilder(output);
        }

        private static void RegisterFinalMessageArtifact(AgentLifecycleHandler handler, string missionId, string artifactPath)
        {
            FieldInfo artifactField = typeof(AgentLifecycleHandler).GetField("_MissionFinalMessageFiles", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Could not find _MissionFinalMessageFiles field");

            System.Collections.Concurrent.ConcurrentDictionary<string, string> artifactMap =
                (System.Collections.Concurrent.ConcurrentDictionary<string, string>)(artifactField.GetValue(handler)
                ?? throw new InvalidOperationException("Mission final message map was null"));

            artifactMap[missionId] = artifactPath;
        }

        private static void StartTrackedProcessHeartbeat(AgentLifecycleHandler handler, int processId, string captainId, string missionId)
        {
            MethodInfo method = typeof(AgentLifecycleHandler).GetMethod("StartProcessLivenessHeartbeat", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Could not find StartProcessLivenessHeartbeat method");
            method.Invoke(handler, new object[] { processId, captainId, missionId });
        }

        private static void StopTrackedProcessHeartbeat(AgentLifecycleHandler handler, int processId)
        {
            MethodInfo method = typeof(AgentLifecycleHandler).GetMethod("StopProcessLivenessHeartbeat", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Could not find StopProcessLivenessHeartbeat method");
            method.Invoke(handler, new object[] { processId });
        }

        private static bool HasProcessHeartbeatLoop(AgentLifecycleHandler handler, int processId)
        {
            FieldInfo loopsField = typeof(AgentLifecycleHandler).GetField("_ProcessHeartbeatLoops", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Could not find _ProcessHeartbeatLoops field");

            System.Collections.Concurrent.ConcurrentDictionary<int, System.Threading.CancellationTokenSource> loops =
                (System.Collections.Concurrent.ConcurrentDictionary<int, System.Threading.CancellationTokenSource>)(loopsField.GetValue(handler)
                ?? throw new InvalidOperationException("Process heartbeat loop map was null"));

            return loops.ContainsKey(processId);
        }

        private static Process StartSilentProcess()
        {
            ProcessStartInfo startInfo;
            if (OperatingSystem.IsWindows())
            {
                startInfo = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c ping 127.0.0.1 -n 10 >nul",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
            }
            else
            {
                startInfo = new ProcessStartInfo
                {
                    FileName = "/bin/sh",
                    Arguments = "-c \"sleep 10\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
            }

            return Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start silent heartbeat test process");
        }

        private static async Task WaitForProgressSignalsAsync(DatabaseDriver database, string captainId, int expected)
        {
            await WaitForConditionAsync(async () =>
            {
                List<Signal> recent = await database.Signals.EnumerateRecentAsync(200).ConfigureAwait(false);
                return recent.Count(signal => signal.FromCaptainId == captainId) >= expected;
            }, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }

        private static async Task WaitForConditionAsync(Func<Task<bool>> predicate, TimeSpan? timeout = null)
        {
            DateTime deadline = DateTime.UtcNow.Add(timeout ?? TimeSpan.FromSeconds(3));

            while (DateTime.UtcNow < deadline)
            {
                if (await predicate().ConfigureAwait(false))
                    return;

                await Task.Delay(50).ConfigureAwait(false);
            }

            throw new TimeoutException("Timed out waiting for asynchronous condition");
        }

        private sealed class StubAdmiralService : IAdmiralService
        {
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
                throw new NotImplementedException();
            }

            public Task<Voyage> DispatchVoyageAsync(string title, string description, string vesselId, List<MissionDescription> missionDescriptions, List<SelectedPlaybook>? selectedPlaybooks, CancellationToken token = default)
            {
                throw new NotImplementedException();
            }

            public Task<Voyage> DispatchVoyageAsync(string title, string description, string vesselId, List<MissionDescription> missionDescriptions, string? pipelineId, CancellationToken token = default)
            {
                throw new NotImplementedException();
            }

            public Task<Voyage> DispatchVoyageAsync(string title, string description, string vesselId, List<MissionDescription> missionDescriptions, string? pipelineId, List<SelectedPlaybook>? selectedPlaybooks, CancellationToken token = default)
            {
                throw new NotImplementedException();
            }

            public Task<Mission> DispatchMissionAsync(Mission mission, CancellationToken token = default)
            {
                throw new NotImplementedException();
            }

            public Task<Pipeline?> ResolvePipelineAsync(string? pipelineIdOrName, Vessel vessel, CancellationToken token = default)
            {
                return Task.FromResult<Pipeline?>(null);
            }

            public Task<ArmadaStatus> GetStatusAsync(CancellationToken token = default)
            {
                throw new NotImplementedException();
            }

            public Task RecallCaptainAsync(string captainId, CancellationToken token = default)
            {
                throw new NotImplementedException();
            }

            public Task RecallAllAsync(CancellationToken token = default)
            {
                throw new NotImplementedException();
            }
            public Task StopAllAgentProcessesAsync(CancellationToken token = default) => Task.CompletedTask;

            public Task HealthCheckAsync(CancellationToken token = default)
            {
                throw new NotImplementedException();
            }

            public Task CleanupStaleCaptainsAsync(CancellationToken token = default)
            {
                throw new NotImplementedException();
            }

            public Func<int, int?, string, string, Task>? OnHandleProcessExit { get; set; }

            public Task HandleProcessExitAsync(int processId, int? exitCode, string captainId, string missionId, CancellationToken token = default)
            {
                if (OnHandleProcessExit != null) return OnHandleProcessExit(processId, exitCode, captainId, missionId);
                return Task.CompletedTask;
            }
        }

        private sealed class CursorShimScope : IDisposable
        {
            public string ArgsFile { get; }

            private readonly string _tempDirectory;
            private readonly string _originalPath;
            // True when ARMADA_TEST_CURSOR_AGENT was set (Windows path); restored in Dispose.
            private readonly bool _setWindowsAgentOverride;
            private readonly string? _originalCursorAgentOverride;

            private CursorShimScope(
                string tempDirectory,
                string argsFile,
                string originalPath,
                bool setWindowsAgentOverride,
                string? originalCursorAgentOverride)
            {
                _tempDirectory = tempDirectory;
                ArgsFile = argsFile;
                _originalPath = originalPath;
                _setWindowsAgentOverride = setWindowsAgentOverride;
                _originalCursorAgentOverride = originalCursorAgentOverride;
            }

            public static CursorShimScope Create()
            {
                string tempDirectory = Path.Combine(Path.GetTempPath(), "armada_cursor_shim_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDirectory);

                string argsFile = Path.Combine(tempDirectory, "cursor-args.txt");
                string originalPath = Environment.GetEnvironmentVariable("PATH") ?? String.Empty;
                string? originalCursorAgentOverride = Environment.GetEnvironmentVariable("ARMADA_TEST_CURSOR_AGENT");
                bool setWindowsAgentOverride = false;

                Environment.SetEnvironmentVariable("ARMADA_TEST_CURSOR_ARGS_FILE", argsFile);

                if (OperatingSystem.IsWindows())
                {
                    // Write the test shim inside the temp directory and point
                    // ARMADA_TEST_CURSOR_AGENT at it. This avoids touching %APPDATA%\npm
                    // (a user-global path that leaks stale shims on abnormal test termination)
                    // and prevents the shim from racing with the official Cursor install path
                    // that CursorRuntime now prefers over npm.
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

                return new CursorShimScope(
                    tempDirectory,
                    argsFile,
                    originalPath,
                    setWindowsAgentOverride,
                    originalCursorAgentOverride);
            }

            public void Dispose()
            {
                Environment.SetEnvironmentVariable("ARMADA_TEST_CURSOR_ARGS_FILE", null);
                Environment.SetEnvironmentVariable("PATH", _originalPath);

                if (_setWindowsAgentOverride)
                {
                    Environment.SetEnvironmentVariable("ARMADA_TEST_CURSOR_AGENT", _originalCursorAgentOverride);
                }

                try { Directory.Delete(_tempDirectory, true); } catch { }
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
                    "if /I \"%MODEL%\"==\"hang-model\" (\r\n" +
                    "  ping 127.0.0.1 -n 5 >nul\r\n" +
                    "  exit /b 0\r\n" +
                    ")\r\n" +
                    "if /I \"%MODEL%\"==\"slow-launch-model\" (\r\n" +
                    "  ping 127.0.0.1 -n 3 >nul\r\n" +
                    "  exit /b 0\r\n" +
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
                    "if [ \"$model\" = \"hang-model\" ]; then\n" +
                    // Only has to outlast the 1 s ceiling the timeout test drives, with margin for a
                    // loaded host; every second beyond that is time the test waits on a result it has.
                    "  sleep 3\n" +
                    "  exit 0\n" +
                    "fi\n" +
                    "if [ \"$model\" = \"slow-launch-model\" ]; then\n" +
                    "  sleep 2\n" +
                    "  exit 0\n" +
                    "fi\n" +
                    "printf '%s\\n' ok\n" +
                    "exit 0\n";
            }
        }
    }
}
