namespace Test.Shared.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Reflection;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Database.Sqlite;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Runtimes;
    using Armada.Runtimes.Interfaces;
    using Armada.Server;
    using SyslogLogging;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// Descriptors for <see cref="ObjectiveRefinementCoordinator"/>: transcript persistence and
    /// active-state recovery when the captain runtime is unsupported, assistant-transcript fallback
    /// summarization, objective field application with source-message selection, and stop cleanup
    /// that releases the captain. Turn-claim cases cover a failed write before launch, the next
    /// request at the moment the session is Active, and a stop that finishes while a turn is
    /// failing. Each case runs against a fresh SQLite store with a disposable refinement coordinator
    /// fixture.
    /// </summary>
    public sealed class ObjectiveRefinementCoordinatorSuite : IArmadaTestSuite
    {
        #region Private-Members

        private const string SuiteId = "Services.ObjectiveRefinementCoordinator";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the Objective Refinement Coordinator suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(CaseAsync("opencode_provider_error_is_persisted_without_applying_proposal", "OpenCode provider error is persisted without applying a proposal", TestTags.Negative, async () =>
            {
                await OpenCodeTestEnvironmentGate.Instance.WaitAsync().ConfigureAwait(false);
                string root = Path.Combine(Path.GetTempPath(), "armada-refinement-opencode-error-" + Guid.NewGuid().ToString("N"));
                string script = Path.Combine(root, OperatingSystem.IsWindows() ? "opencode.cmd" : "opencode");
                string? prior = null;
                try
                {
                    Directory.CreateDirectory(root);
                    prior = Environment.GetEnvironmentVariable("ARMADA_TEST_OPENCODE");
                    string fixtureText = (await File.ReadAllTextAsync(Path.Combine(FindRepositoryRoot(), "src", "Test.Shared", "Fixtures", "opencode-api-error.jsonl")).ConfigureAwait(false)).Trim();
                    string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(fixtureText));
                    string scriptText = OperatingSystem.IsWindows()
                        ? "@echo off\npowershell -NoProfile -Command \"[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" + encoded + "'))\"\n"
                        : "#!/bin/sh\nprintf '%s' '" + encoded + "' | base64 -d\n";
                    await File.WriteAllTextAsync(script, scriptText).ConfigureAwait(false);
                    if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                    Environment.SetEnvironmentVariable("ARMADA_TEST_OPENCODE", script);
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    using (CoordinatorFixture state = new CoordinatorFixture(testDb.Driver))
                    {
                        CoordinatorFixture.TenantUserResult user = await state.CreateTenantUserAsync("OpenCode refinement").ConfigureAwait(false);
                        Objective objective = await state.CreateObjectiveAsync("Provider failure objective", user.TenantId, user.UserId).ConfigureAwait(false);
                        string originalTitle = objective.Title;
                        Captain captain = await state.CreateCaptainAsync("OpenCode refinement fixture", AgentRuntimeEnum.OpenCode, user.TenantId, user.UserId, CaptainStateEnum.Refining).ConfigureAwait(false);
                        ObjectiveRefinementSession session = await state.CreateSessionAsync(objective, captain).ConfigureAwait(false);
                        await state.Coordinator.SendMessageAsync(session, "Ask provider.").ConfigureAwait(false);
                        await WaitForAsync(async () =>
                        {
                            ObjectiveRefinementSession? current = await testDb.Driver.ObjectiveRefinementSessions.ReadAsync(session.Id).ConfigureAwait(false);
                            List<ObjectiveRefinementMessage> rows = await testDb.Driver.ObjectiveRefinementMessages.EnumerateBySessionAsync(session.Id).ConfigureAwait(false);
                            return current != null && current.ProcessId == null && current.Status == ObjectiveRefinementSessionStatusEnum.Active && (current.FailureReason ?? String.Empty).Contains("opencode error", StringComparison.Ordinal) && rows.Exists(item => item.Role == "Assistant" && (item.Content ?? String.Empty).Contains("opencode error", StringComparison.Ordinal));
                        }).ConfigureAwait(false);
                        ObjectiveRefinementSession saved = await testDb.Driver.ObjectiveRefinementSessions.ReadAsync(session.Id) ?? throw new Exception("refinement session disappeared");
                        List<ObjectiveRefinementMessage> messages = await testDb.Driver.ObjectiveRefinementMessages.EnumerateBySessionAsync(session.Id).ConfigureAwait(false);
                        AssertContains("opencode error", saved.FailureReason ?? String.Empty);
                        AssertTrue(messages.Exists(item => item.Role == "Assistant" && (item.Content ?? String.Empty).Contains("opencode error", StringComparison.Ordinal)), "Provider error must be persisted.");
                        AssertEqual(ObjectiveRefinementSessionStatusEnum.Active, saved.Status);
                        Objective unchanged = await testDb.Driver.Objectives.ReadAsync(objective.Id) ?? throw new Exception("objective disappeared");
                        AssertEqual(originalTitle, unchanged.Title);
                    }
                }
                finally
                {
                    try
                    {
                        if (prior != null) Environment.SetEnvironmentVariable("ARMADA_TEST_OPENCODE", prior);
                        else Environment.SetEnvironmentVariable("ARMADA_TEST_OPENCODE", null);
                        if (Directory.Exists(root)) Directory.Delete(root, true);
                    }
                    finally { OpenCodeTestEnvironmentGate.Instance.Release(); }
                }
            }));

            cases.Add(CaseAsync("send_message_async_creates_transcript_rows_and_recovers_to_active_state_when_runtime_is_unsupported", "SendMessageAsync creates transcript rows and recovers to active state when runtime is unsupported", TestTags.Negative, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                using CoordinatorFixture fixture = new CoordinatorFixture(testDb.Driver);

                CoordinatorFixture.TenantUserResult tenantUser = await fixture.CreateTenantUserAsync().ConfigureAwait(false);
                Objective objective = await fixture.CreateObjectiveAsync("Refine backlog capture", tenantUser.TenantId, tenantUser.UserId).ConfigureAwait(false);
                Captain captain = await fixture.CreateCaptainAsync("refinement-custom", AgentRuntimeEnum.Custom, tenantUser.TenantId, tenantUser.UserId, CaptainStateEnum.Refining).ConfigureAwait(false);
                ObjectiveRefinementSession session = await fixture.CreateSessionAsync(objective, captain).ConfigureAwait(false);

                ObjectiveRefinementMessage userMessage = await fixture.Coordinator.SendMessageAsync(session, "Clarify API retry behavior.").ConfigureAwait(false);

                await WaitForAsync(async () =>
                {
                    List<ObjectiveRefinementMessage> messages = await testDb.Driver.ObjectiveRefinementMessages.EnumerateBySessionAsync(session.Id).ConfigureAwait(false);
                    ObjectiveRefinementSession? refreshed = await testDb.Driver.ObjectiveRefinementSessions.ReadAsync(session.Id).ConfigureAwait(false);
                    return messages.Count == 2
                        && messages.Exists(message => message.Role == "Assistant" && !String.IsNullOrWhiteSpace(message.Content))
                        && refreshed?.Status == ObjectiveRefinementSessionStatusEnum.Active;
                }).ConfigureAwait(false);

                List<ObjectiveRefinementMessage> persistedMessages = await testDb.Driver.ObjectiveRefinementMessages.EnumerateBySessionAsync(session.Id).ConfigureAwait(false);
                ObjectiveRefinementMessage assistantMessage = persistedMessages.Find(message => message.Role == "Assistant")
                    ?? throw new Exception("Expected assistant refinement message");
                ObjectiveRefinementSession persistedSession = await RequireSessionAsync(testDb.Driver, session.Id).ConfigureAwait(false);

                AssertEqual("User", userMessage.Role);
                AssertEqual(2, persistedMessages.Count);
                AssertContains("Refinement response failed", assistantMessage.Content);
                AssertContains("built-in ClaudeCode, Codex, Gemini, Cursor, OpenCode, and Mux runtimes", persistedSession.FailureReason ?? String.Empty);
                AssertEqual(ObjectiveRefinementSessionStatusEnum.Active, persistedSession.Status);
            }));

            cases.Add(CaseAsync("summarize_async_falls_back_to_transcript_parsing_when_runtime_prompt_execution_is_unavailable", "SummarizeAsync falls back to transcript parsing when runtime prompt execution is unavailable", TestTags.Positive, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                using CoordinatorFixture fixture = new CoordinatorFixture(testDb.Driver);

                CoordinatorFixture.TenantUserResult tenantUser = await fixture.CreateTenantUserAsync().ConfigureAwait(false);
                Objective objective = await fixture.CreateObjectiveAsync("Fallback summary", tenantUser.TenantId, tenantUser.UserId).ConfigureAwait(false);
                Captain captain = await fixture.CreateCaptainAsync("summary-custom", AgentRuntimeEnum.Custom, tenantUser.TenantId, tenantUser.UserId, CaptainStateEnum.Refining).ConfigureAwait(false);
                ObjectiveRefinementSession session = await fixture.CreateSessionAsync(objective, captain).ConfigureAwait(false);
                ObjectiveRefinementMessage assistant = await fixture.CreateMessageAsync(session, "Assistant", 2,
                    "Summary paragraph.\n\n### Acceptance Criteria\n- Keep replay data wired\n- Preserve captain selection\n\n### Non-Goals\n- No CLI rewrite\n\n### Rollout Constraints\n- Ship behind feature flag").ConfigureAwait(false);

                ObjectiveRefinementSummaryResponse summary = await fixture.Coordinator.SummarizeAsync(
                    session,
                    new ObjectiveRefinementSummaryRequest { MessageId = assistant.Id }).ConfigureAwait(false);

                AssertEqual(session.Id, summary.SessionId);
                AssertEqual(assistant.Id, summary.MessageId);
                AssertEqual("assistant-fallback", summary.Method);
                AssertContains("Summary paragraph.", summary.Summary);
                AssertEqual(2, summary.AcceptanceCriteria.Count);
                AssertEqual("Keep replay data wired", summary.AcceptanceCriteria[0]);
                AssertEqual("No CLI rewrite", summary.NonGoals[0]);
                AssertEqual("Ship behind feature flag", summary.RolloutConstraints[0]);
            }));

            cases.Add(CaseAsync("apply_async_updates_objective_fields_and_selects_the_source_refinement_message", "ApplyAsync updates objective fields and selects the source refinement message", TestTags.Positive, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                using CoordinatorFixture fixture = new CoordinatorFixture(testDb.Driver);

                CoordinatorFixture.TenantUserResult tenantUser = await fixture.CreateTenantUserAsync().ConfigureAwait(false);
                Vessel vessel = await fixture.CreateVesselAsync("refinement-apply", tenantUser.TenantId, tenantUser.UserId).ConfigureAwait(false);
                Objective objective = await fixture.CreateObjectiveAsync("Apply refinement", tenantUser.TenantId, tenantUser.UserId).ConfigureAwait(false);
                objective.Preparation = new ObjectivePreparation
                {
                    RequiredSiblingInputs = new List<ObjectivePreparationSiblingInput>
                    {
                        new ObjectivePreparationSiblingInput { VesselRef = "ReferenceSource", RelativePath = "../ReferenceSource" }
                    }
                };
                objective = await testDb.Driver.Objectives.UpdateAsync(objective).ConfigureAwait(false);
                Captain captain = await fixture.CreateCaptainAsync("apply-custom", AgentRuntimeEnum.Custom, tenantUser.TenantId, tenantUser.UserId, CaptainStateEnum.Refining).ConfigureAwait(false);
                ObjectiveRefinementSession session = await fixture.CreateSessionAsync(objective, captain, vesselId: vessel.Id).ConfigureAwait(false);
                ObjectiveRefinementMessage assistant = await fixture.CreateMessageAsync(session, "Assistant", 2,
                    "Refined backlog summary.\n\n### Acceptance Criteria\n- Persist normalized objective fields\n\n### Non-Goals\n- No schema rollback\n\n### Rollout Constraints\n- Validate with SQLite first").ConfigureAwait(false);

                (ObjectiveRefinementSummaryResponse Summary, Objective Objective) applied = await fixture.Coordinator.ApplyAsync(
                    AuthContext.Authenticated(tenantUser.TenantId, tenantUser.UserId, false, true, "UnitTest"),
                    objective,
                    session,
                    new ObjectiveRefinementApplyRequest
                    {
                        MessageId = assistant.Id,
                        MarkMessageSelected = true,
                        PromoteBacklogState = true
                    },
                    fixture.Objectives).ConfigureAwait(false);

                Objective persistedObjective = await RequireObjectiveAsync(testDb.Driver, objective.Id).ConfigureAwait(false);
                List<ObjectiveRefinementMessage> persistedMessages = await testDb.Driver.ObjectiveRefinementMessages.EnumerateBySessionAsync(session.Id).ConfigureAwait(false);
                ObjectiveRefinementMessage selected = persistedMessages.Find(message => message.Id == assistant.Id)
                    ?? throw new Exception("Expected selected assistant message");

                AssertEqual("assistant-fallback", applied.Summary.Method);
                AssertEqual(ObjectiveStatusEnum.Scoped, applied.Objective.Status);
                AssertEqual(ObjectiveBacklogStateEnum.ReadyForPlanning, applied.Objective.BacklogState);
                AssertContains("Refined backlog summary.", applied.Objective.RefinementSummary ?? String.Empty);
                AssertEqual("Persist normalized objective fields", applied.Objective.AcceptanceCriteria[0]);
                AssertEqual("No schema rollback", applied.Objective.NonGoals[0]);
                AssertEqual("Validate with SQLite first", applied.Objective.RolloutConstraints[0]);
                AssertTrue(applied.Objective.RefinementSessionIds.Contains(session.Id), "Expected session linkage on updated objective.");
                AssertEqual("ReferenceSource", applied.Objective.Preparation.RequiredSiblingInputs[0].VesselRef,
                    "A runtime summary that omits preparation must preserve existing preparation.");
                AssertEqual(ObjectiveStatusEnum.Scoped, persistedObjective.Status);
                AssertTrue(selected.IsSelected, "Expected source refinement message to be selected.");
            }));

            cases.Add(CaseAsync("stop_async_releases_the_captain_and_marks_the_session_stopped_when_no_runtime_process_can_be_created", "StopAsync releases the captain and marks the session stopped when no runtime process can be created", TestTags.Positive, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                using CoordinatorFixture fixture = new CoordinatorFixture(testDb.Driver);

                CoordinatorFixture.TenantUserResult tenantUser = await fixture.CreateTenantUserAsync().ConfigureAwait(false);
                Objective objective = await fixture.CreateObjectiveAsync("Stop refinement", tenantUser.TenantId, tenantUser.UserId).ConfigureAwait(false);
                Captain captain = await fixture.CreateCaptainAsync("stop-custom", AgentRuntimeEnum.Custom, tenantUser.TenantId, tenantUser.UserId, CaptainStateEnum.Refining).ConfigureAwait(false);
                ObjectiveRefinementSession session = await fixture.CreateSessionAsync(objective, captain, processId: 1234).ConfigureAwait(false);

                ObjectiveRefinementSession stopped = await fixture.Coordinator.StopAsync(session).ConfigureAwait(false);
                Captain persistedCaptain = await RequireCaptainAsync(testDb.Driver, captain.Id).ConfigureAwait(false);

                AssertEqual(ObjectiveRefinementSessionStatusEnum.Stopped, stopped.Status);
                AssertNull(stopped.ProcessId);
                AssertTrue(stopped.CompletedUtc.HasValue, "Expected stop to set completion timestamp.");
                AssertEqual(CaptainStateEnum.Idle, persistedCaptain.State);
                AssertNull(persistedCaptain.ProcessId);
            }));

            cases.Add(CaseAsync("persistence_failure_before_turn_start_releases_the_claim", "A persistence failure before the turn starts releases the session claim", TestTags.Negative, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                using SessionHookDriver hooked = new SessionHookDriver(testDb.Driver);
                using CoordinatorFixture fixture = new CoordinatorFixture(hooked);

                CoordinatorFixture.TenantUserResult tenantUser = await fixture.CreateTenantUserAsync().ConfigureAwait(false);
                Objective objective = await fixture.CreateObjectiveAsync("Claim released on failed write", tenantUser.TenantId, tenantUser.UserId).ConfigureAwait(false);
                Captain captain = await fixture.CreateCaptainAsync("claim-custom", AgentRuntimeEnum.Custom, tenantUser.TenantId, tenantUser.UserId, CaptainStateEnum.Refining).ConfigureAwait(false);
                ObjectiveRefinementSession session = await fixture.CreateSessionAsync(objective, captain).ConfigureAwait(false);
                hooked.Messages.FailCreatesRemaining = 1;

                IOException? persistenceFailure = null;
                try
                {
                    await fixture.Coordinator.SendMessageAsync(session, "First attempt.").ConfigureAwait(false);
                }
                catch (IOException ex)
                {
                    persistenceFailure = ex;
                }

                AssertNotNull(persistenceFailure, "The failed write must reach the caller.");
                ObjectiveRefinementMessage retry = await fixture.Coordinator.SendMessageAsync(session, "Retry after the failed write.").ConfigureAwait(false);
                AssertEqual("User", retry.Role);

                await WaitForAsync(async () =>
                {
                    ObjectiveRefinementSession? current = await testDb.Driver.ObjectiveRefinementSessions.ReadAsync(session.Id).ConfigureAwait(false);
                    return current?.Status == ObjectiveRefinementSessionStatusEnum.Active;
                }).ConfigureAwait(false);
            }));

            cases.Add(CaseAsync("active_session_accepts_the_next_turn_and_keeps_that_turn_claim", "A session that has just become Active accepts the next message, and that new turn keeps its claim after the finished turn releases", TestTags.Negative, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                using SessionHookDriver hooked = new SessionHookDriver(testDb.Driver);
                GatedRefinementRuntime runtime = new GatedRefinementRuntime();
                using CoordinatorFixture fixture = new CoordinatorFixture(hooked, new SingleRuntimeFactory(runtime));

                CoordinatorFixture.TenantUserResult tenantUser = await fixture.CreateTenantUserAsync().ConfigureAwait(false);
                Objective objective = await fixture.CreateObjectiveAsync("Active means the next turn can start", tenantUser.TenantId, tenantUser.UserId).ConfigureAwait(false);
                Captain captain = await fixture.CreateCaptainAsync("active-ready", AgentRuntimeEnum.ClaudeCode, tenantUser.TenantId, tenantUser.UserId, CaptainStateEnum.Refining).ConfigureAwait(false);
                ObjectiveRefinementSession session = await fixture.CreateSessionAsync(objective, captain).ConfigureAwait(false);

                TaskCompletionSource<Exception?> nextRequest = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
                TaskCompletionSource<bool> allowFinishedTurnToReturn = new TaskCompletionSource<bool>(TaskCreationOptions.None);
                int handledActive = 0;
                int hookReturned = 0;
                hooked.Sessions.AfterUpdateAsync = async updated =>
                {
                    if (updated.Id != session.Id || updated.Status != ObjectiveRefinementSessionStatusEnum.Active) return;
                    if (Interlocked.Exchange(ref handledActive, 1) == 1) return;

                    try
                    {
                        await fixture.Coordinator.SendMessageAsync(updated, "Next turn.").ConfigureAwait(false);
                        nextRequest.TrySetResult(null);
                    }
                    catch (Exception ex)
                    {
                        nextRequest.TrySetResult(ex);
                        return;
                    }

                    await allowFinishedTurnToReturn.Task;
                    Volatile.Write(ref hookReturned, 1);
                };

                await fixture.Coordinator.SendMessageAsync(session, "First turn.").ConfigureAwait(false);
                Exception? nextError = await WaitGateAsync(nextRequest.Task, "Expected the finished turn to publish Active.").ConfigureAwait(false);
                AssertNull(nextError, "The next message at the moment the session became Active");
                await WaitGateAsync(runtime.SecondTurnEntered.Task, "Expected the next turn to reach its runtime.").ConfigureAwait(false);

                allowFinishedTurnToReturn.TrySetResult(true);
                AssertEqual(1, Volatile.Read(ref hookReturned), "The finished turn must leave its session update before the new claim is checked.");

                InvalidOperationException? busy = null;
                try
                {
                    await fixture.Coordinator.SummarizeAsync(session, new ObjectiveRefinementSummaryRequest()).ConfigureAwait(false);
                }
                catch (InvalidOperationException ex)
                {
                    busy = ex;
                }

                AssertNotNull(busy, "The new turn must still own the session after the finished turn releases.");
                AssertContains("already generating a response", busy!.Message);

                runtime.ReleaseSecondTurn.TrySetResult(true);
                await WaitForAsync(async () =>
                {
                    ObjectiveRefinementSession? current = await testDb.Driver.ObjectiveRefinementSessions.ReadAsync(session.Id).ConfigureAwait(false);
                    return current?.Status == ObjectiveRefinementSessionStatusEnum.Active;
                }).ConfigureAwait(false);
            }));

            cases.Add(CaseAsync("stop_during_turn_failure_keeps_the_session_stopped", "A stop that finishes while a turn is failing keeps the session Stopped and the captain Idle", TestTags.Negative, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                using SessionHookDriver hooked = new SessionHookDriver(testDb.Driver);
                using CoordinatorFixture fixture = new CoordinatorFixture(hooked, new SingleRuntimeFactory(new ThrowingRefinementRuntime()));

                CoordinatorFixture.TenantUserResult tenantUser = await fixture.CreateTenantUserAsync().ConfigureAwait(false);
                Objective objective = await fixture.CreateObjectiveAsync("Stop wins over a failing turn", tenantUser.TenantId, tenantUser.UserId).ConfigureAwait(false);
                Captain captain = await fixture.CreateCaptainAsync("stop-during-failure", AgentRuntimeEnum.ClaudeCode, tenantUser.TenantId, tenantUser.UserId, CaptainStateEnum.Refining).ConfigureAwait(false);
                ObjectiveRefinementSession session = await fixture.CreateSessionAsync(objective, captain).ConfigureAwait(false);

                int phase = 0;
                TaskCompletionSource<ObjectiveRefinementSessionStatusEnum> completionWrite = new TaskCompletionSource<ObjectiveRefinementSessionStatusEnum>(TaskCreationOptions.RunContinuationsAsynchronously);
                TaskCompletionSource<bool> allowCompletionWriteToReturn = new TaskCompletionSource<bool>(TaskCreationOptions.None);
                hooked.Messages.AfterUpdateAsync = async message =>
                {
                    if (message.ObjectiveRefinementSessionId != session.Id) return;
                    if ((message.Content ?? String.Empty).IndexOf("Refinement response failed", StringComparison.Ordinal) < 0) return;
                    if (Interlocked.Exchange(ref phase, 1) != 0) return;

                    await fixture.Coordinator.StopAsync(session).ConfigureAwait(false);
                    Volatile.Write(ref phase, 2);
                };
                hooked.Sessions.AfterUpdateAsync = async updated =>
                {
                    if (updated.Id != session.Id || Volatile.Read(ref phase) != 2) return;
                    if (!completionWrite.TrySetResult(updated.Status)) return;
                    await allowCompletionWriteToReturn.Task;
                };

                try
                {
                    await fixture.Coordinator.SendMessageAsync(session, "Turn that fails.").ConfigureAwait(false);
                    ObjectiveRefinementSessionStatusEnum written = await WaitGateAsync(completionWrite.Task, "Expected the failing turn to write the session after the stop.").ConfigureAwait(false);
                    AssertEqual(ObjectiveRefinementSessionStatusEnum.Stopped, written, "The failing turn must not replace Stopped.");
                    Captain persistedCaptain = await RequireCaptainAsync(testDb.Driver, captain.Id).ConfigureAwait(false);
                    AssertEqual(CaptainStateEnum.Idle, persistedCaptain.State, "A stopped captain stays Idle when the failing turn settles.");
                }
                finally
                {
                    allowCompletionWriteToReturn.TrySetResult(true);
                }
            }));

            return new TestSuiteDescriptor(
                suiteId: SuiteId,
                displayName: "Objective Refinement Coordinator",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        private sealed class CoordinatorFixture : IDisposable
        {
            public sealed class TenantUserResult
            {
                public string TenantId { get; set; } = String.Empty;

                public string UserId { get; set; } = String.Empty;
            }

            public DatabaseDriver Database { get; }
            public ArmadaSettings Settings { get; }
            public ObjectiveRefinementCoordinator Coordinator { get; }
            public ObjectiveService Objectives { get; }

            private readonly string _rootDirectory;
            private readonly LoggingModule _logging;

            public CoordinatorFixture(DatabaseDriver database, AgentRuntimeFactory? runtimeFactory = null)
            {
                Database = database;
                Objectives = new ObjectiveService(database);
                _logging = CreateLogging();
                _rootDirectory = Path.Combine(Path.GetTempPath(), "armada_refinement_fixture_" + Guid.NewGuid().ToString("N"));

                Settings = new ArmadaSettings
                {
                    DataDirectory = _rootDirectory,
                    DatabasePath = Path.Combine(_rootDirectory, "armada.db"),
                    LogDirectory = Path.Combine(_rootDirectory, "logs"),
                    DocksDirectory = Path.Combine(_rootDirectory, "docks"),
                    ReposDirectory = Path.Combine(_rootDirectory, "repos")
                };
                Settings.InitializeDirectories();

                Coordinator = new ObjectiveRefinementCoordinator(
                    _logging,
                    Database,
                    Settings,
                    runtimeFactory ?? new AgentRuntimeFactory(_logging),
                    (eventType, message, entityType, entityId, captainId, missionId, vesselId, voyageId) => Task.CompletedTask);
            }

            public async Task<TenantUserResult> CreateTenantUserAsync(string tenantName = "Refinement Tenant")
            {
                TenantMetadata tenant = new TenantMetadata(tenantName);
                tenant = await Database.Tenants.CreateAsync(tenant).ConfigureAwait(false);

                UserMaster user = new UserMaster(tenant.Id, tenantName.Replace(" ", String.Empty).ToLowerInvariant() + "@example.com", "password");
                user = await Database.Users.CreateAsync(user).ConfigureAwait(false);

                return new TenantUserResult
                {
                    TenantId = tenant.Id,
                    UserId = user.Id
                };
            }

            public async Task<Objective> CreateObjectiveAsync(string title, string? tenantId = null, string? userId = null)
            {
                Objective objective = new Objective
                {
                    Title = title,
                    TenantId = tenantId,
                    UserId = userId,
                    Status = ObjectiveStatusEnum.Draft,
                    BacklogState = ObjectiveBacklogStateEnum.Inbox
                };
                return await Database.Objectives.CreateAsync(objective).ConfigureAwait(false);
            }

            public async Task<Vessel> CreateVesselAsync(string name, string? tenantId = null, string? userId = null)
            {
                Fleet fleet = new Fleet("fleet-" + name)
                {
                    TenantId = tenantId,
                    UserId = userId
                };
                fleet = await Database.Fleets.CreateAsync(fleet).ConfigureAwait(false);

                Vessel vessel = new Vessel(name, "https://github.com/test/" + name + ".git")
                {
                    TenantId = tenantId,
                    UserId = userId,
                    FleetId = fleet.Id,
                    LocalPath = Path.Combine(Settings.ReposDirectory, name + ".git"),
                    WorkingDirectory = Path.Combine(Settings.ReposDirectory, name + ".git"),
                    DefaultBranch = "main"
                };
                return await Database.Vessels.CreateAsync(vessel).ConfigureAwait(false);
            }

            public async Task<Captain> CreateCaptainAsync(
                string name,
                AgentRuntimeEnum runtime,
                string? tenantId = null,
                string? userId = null,
                CaptainStateEnum state = CaptainStateEnum.Idle)
            {
                Captain captain = new Captain(name, runtime)
                {
                    TenantId = tenantId,
                    UserId = userId,
                    State = state
                };
                return await Database.Captains.CreateAsync(captain).ConfigureAwait(false);
            }

            public async Task<ObjectiveRefinementSession> CreateSessionAsync(
                Objective objective,
                Captain captain,
                string? title = null,
                string? vesselId = null,
                int? processId = null)
            {
                ObjectiveRefinementSession session = new ObjectiveRefinementSession
                {
                    ObjectiveId = objective.Id,
                    TenantId = objective.TenantId,
                    UserId = objective.UserId,
                    CaptainId = captain.Id,
                    Title = title ?? "Refine: " + objective.Title,
                    VesselId = vesselId,
                    Status = ObjectiveRefinementSessionStatusEnum.Active,
                    ProcessId = processId,
                    StartedUtc = DateTime.UtcNow,
                    CreatedUtc = DateTime.UtcNow,
                    LastUpdateUtc = DateTime.UtcNow
                };
                return await Database.ObjectiveRefinementSessions.CreateAsync(session).ConfigureAwait(false);
            }

            public async Task<ObjectiveRefinementMessage> CreateMessageAsync(
                ObjectiveRefinementSession session,
                string role,
                int sequence,
                string content)
            {
                ObjectiveRefinementMessage message = new ObjectiveRefinementMessage
                {
                    ObjectiveRefinementSessionId = session.Id,
                    ObjectiveId = session.ObjectiveId,
                    TenantId = session.TenantId,
                    UserId = session.UserId,
                    Role = role,
                    Sequence = sequence,
                    Content = content,
                    CreatedUtc = DateTime.UtcNow,
                    LastUpdateUtc = DateTime.UtcNow
                };
                return await Database.ObjectiveRefinementMessages.CreateAsync(message).ConfigureAwait(false);
            }

            public void Dispose()
            {
                try
                {
                    if (Directory.Exists(_rootDirectory))
                        Directory.Delete(_rootDirectory, true);
                }
                catch
                {
                }
            }

            private static LoggingModule CreateLogging()
            {
                LoggingModule logging = new LoggingModule();
                logging.Settings.EnableConsole = false;
                return logging;
            }
        }

        private static async Task<T> WaitGateAsync<T>(Task<T> task, string label)
        {
            Task finished = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(15))).ConfigureAwait(false);
            AssertTrue(finished == task, label);
            return await task.ConfigureAwait(false);
        }

        private static async Task WaitForAsync(Func<Task<bool>> condition, int timeoutMs = 5000)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                if (await condition().ConfigureAwait(false))
                    return;
                await Task.Delay(50).ConfigureAwait(false);
            }

            throw new TimeoutException("Timed out waiting for refinement coordinator background work.");
        }

        private static string FindRepositoryRoot()
        {
            DirectoryInfo? current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current != null)
            {
                if (File.Exists(Path.Combine(current.FullName, "README.md")) && Directory.Exists(Path.Combine(current.FullName, "src"))) return current.FullName;
                current = current.Parent;
            }
            throw new DirectoryNotFoundException("Could not find the Armada repository root.");
        }

        private static async Task<ObjectiveRefinementSession> RequireSessionAsync(DatabaseDriver database, string sessionId)
        {
            ObjectiveRefinementSession? session = await database.ObjectiveRefinementSessions.ReadAsync(sessionId).ConfigureAwait(false);
            return session ?? throw new Exception("Expected refinement session " + sessionId);
        }

        private static async Task<Objective> RequireObjectiveAsync(DatabaseDriver database, string objectiveId)
        {
            Objective? objective = await database.Objectives.ReadAsync(objectiveId).ConfigureAwait(false);
            return objective ?? throw new Exception("Expected objective " + objectiveId);
        }

        private static async Task<Captain> RequireCaptainAsync(DatabaseDriver database, string captainId)
        {
            Captain? captain = await database.Captains.ReadAsync(captainId).ConfigureAwait(false);
            return captain ?? throw new Exception("Expected captain " + captainId);
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

        /// <summary>
        /// Database driver that shares a real driver's entity accessors and wraps refinement session and message
        /// writes so a test can act when a write has reached the database and before the caller continues.
        /// Disposing this driver does not dispose the inner driver.
        /// </summary>
        private sealed class SessionHookDriver : DatabaseDriver
        {
            private readonly DatabaseDriver _Inner;

            public SessionHookDriver(DatabaseDriver inner)
            {
                _Inner = inner ?? throw new ArgumentNullException(nameof(inner));
                foreach (PropertyInfo property in typeof(DatabaseDriver).GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    MethodInfo? setter = property.SetMethod;
                    if (setter == null || property.GetIndexParameters().Length > 0) continue;
                    setter.Invoke(this, new object?[] { property.GetValue(inner) });
                }

                Sessions = new SessionUpdateHook(inner.ObjectiveRefinementSessions);
                Messages = new MessageWriteHook(inner.ObjectiveRefinementMessages);
                ObjectiveRefinementSessions = Sessions;
                ObjectiveRefinementMessages = Messages;
            }

            public SessionUpdateHook Sessions { get; }

            public MessageWriteHook Messages { get; }

            public override Task InitializeAsync(CancellationToken token = default)
            {
                return _Inner.InitializeAsync(token);
            }

            public override Task<int> GetSchemaVersionAsync(CancellationToken token = default)
            {
                return _Inner.GetSchemaVersionAsync(token);
            }

            public override void Dispose()
            {
            }
        }

        /// <summary>
        /// Runs a hook after each refinement-session update is stored.
        /// </summary>
        private sealed class SessionUpdateHook : IObjectiveRefinementSessionMethods
        {
            private readonly IObjectiveRefinementSessionMethods _Inner;

            public SessionUpdateHook(IObjectiveRefinementSessionMethods inner)
            {
                _Inner = inner ?? throw new ArgumentNullException(nameof(inner));
            }

            public Func<ObjectiveRefinementSession, Task>? AfterUpdateAsync { get; set; }

            public Task<ObjectiveRefinementSession> CreateAsync(ObjectiveRefinementSession session, CancellationToken token = default)
            {
                return _Inner.CreateAsync(session, token);
            }

            public async Task<ObjectiveRefinementSession> UpdateAsync(ObjectiveRefinementSession session, CancellationToken token = default)
            {
                ObjectiveRefinementSession updated = await _Inner.UpdateAsync(session, token).ConfigureAwait(false);
                Func<ObjectiveRefinementSession, Task>? hook = AfterUpdateAsync;
                if (hook != null) await hook(updated).ConfigureAwait(false);
                return updated;
            }

            public Task<ObjectiveRefinementSession?> ReadAsync(string id, CancellationToken token = default)
            {
                return _Inner.ReadAsync(id, token);
            }

            public Task<ObjectiveRefinementSession?> ReadAsync(string tenantId, string id, CancellationToken token = default)
            {
                return _Inner.ReadAsync(tenantId, id, token);
            }

            public Task<ObjectiveRefinementSession?> ReadAsync(string tenantId, string userId, string id, CancellationToken token = default)
            {
                return _Inner.ReadAsync(tenantId, userId, id, token);
            }

            public Task DeleteAsync(string id, CancellationToken token = default)
            {
                return _Inner.DeleteAsync(id, token);
            }

            public Task<List<ObjectiveRefinementSession>> EnumerateAsync(CancellationToken token = default)
            {
                return _Inner.EnumerateAsync(token);
            }

            public Task<List<ObjectiveRefinementSession>> EnumerateAsync(string tenantId, CancellationToken token = default)
            {
                return _Inner.EnumerateAsync(tenantId, token);
            }

            public Task<List<ObjectiveRefinementSession>> EnumerateAsync(string tenantId, string userId, CancellationToken token = default)
            {
                return _Inner.EnumerateAsync(tenantId, userId, token);
            }

            public Task<List<ObjectiveRefinementSession>> EnumerateByObjectiveAsync(string objectiveId, CancellationToken token = default)
            {
                return _Inner.EnumerateByObjectiveAsync(objectiveId, token);
            }

            public Task<List<ObjectiveRefinementSession>> EnumerateByCaptainAsync(string captainId, CancellationToken token = default)
            {
                return _Inner.EnumerateByCaptainAsync(captainId, token);
            }

            public Task<List<ObjectiveRefinementSession>> EnumerateByStatusAsync(ObjectiveRefinementSessionStatusEnum status, CancellationToken token = default)
            {
                return _Inner.EnumerateByStatusAsync(status, token);
            }
        }

        /// <summary>
        /// Fails a bounded number of message creates, and runs a hook after each message update is stored.
        /// </summary>
        private sealed class MessageWriteHook : IObjectiveRefinementMessageMethods
        {
            private readonly IObjectiveRefinementMessageMethods _Inner;

            public MessageWriteHook(IObjectiveRefinementMessageMethods inner)
            {
                _Inner = inner ?? throw new ArgumentNullException(nameof(inner));
            }

            public int FailCreatesRemaining { get; set; }

            public Func<ObjectiveRefinementMessage, Task>? AfterUpdateAsync { get; set; }

            public Task<ObjectiveRefinementMessage> CreateAsync(ObjectiveRefinementMessage message, CancellationToken token = default)
            {
                if (FailCreatesRemaining > 0)
                {
                    FailCreatesRemaining--;
                    throw new IOException("persistence failed before the turn started");
                }

                return _Inner.CreateAsync(message, token);
            }

            public async Task<ObjectiveRefinementMessage> UpdateAsync(ObjectiveRefinementMessage message, CancellationToken token = default)
            {
                ObjectiveRefinementMessage updated = await _Inner.UpdateAsync(message, token).ConfigureAwait(false);
                Func<ObjectiveRefinementMessage, Task>? hook = AfterUpdateAsync;
                if (hook != null) await hook(updated).ConfigureAwait(false);
                return updated;
            }

            public Task<ObjectiveRefinementMessage?> ReadAsync(string id, CancellationToken token = default)
            {
                return _Inner.ReadAsync(id, token);
            }

            public Task DeleteAsync(string id, CancellationToken token = default)
            {
                return _Inner.DeleteAsync(id, token);
            }

            public Task DeleteBySessionAsync(string objectiveRefinementSessionId, CancellationToken token = default)
            {
                return _Inner.DeleteBySessionAsync(objectiveRefinementSessionId, token);
            }

            public Task<List<ObjectiveRefinementMessage>> EnumerateBySessionAsync(string objectiveRefinementSessionId, CancellationToken token = default)
            {
                return _Inner.EnumerateBySessionAsync(objectiveRefinementSessionId, token);
            }

            public Task<List<ObjectiveRefinementMessage>> EnumerateByObjectiveAsync(string objectiveId, CancellationToken token = default)
            {
                return _Inner.EnumerateByObjectiveAsync(objectiveId, token);
            }
        }

        /// <summary>
        /// Factory that returns one runtime for every captain runtime type.
        /// </summary>
        private sealed class SingleRuntimeFactory : AgentRuntimeFactory
        {
            private readonly IAgentRuntime _Runtime;

            public SingleRuntimeFactory(IAgentRuntime runtime)
                : base(new LoggingModule())
            {
                _Runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
            }

            public override IAgentRuntime Create(AgentRuntimeEnum runtimeType)
            {
                return _Runtime;
            }
        }

        /// <summary>
        /// Refinement runtime whose first start finishes immediately and whose second start waits until released.
        /// Later starts finish immediately so a claim check cannot block them.
        /// </summary>
        private sealed class GatedRefinementRuntime : IAgentRuntime
        {
            private int _Starts;

            public TaskCompletionSource<bool> SecondTurnEntered { get; } = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            public TaskCompletionSource<bool> ReleaseSecondTurn { get; } = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            public string Name => "gated-refinement";

            public bool SupportsResume => false;

            public bool SupportsPlanningSessions => true;

            public event Action<int, string>? OnOutputReceived;

            public event Action<int, string>? OnStdoutReceived;

            public event Action<int, RuntimeTokenUsage>? OnTokenUsageReceived;

            public event Action<int, RuntimeTokenUsage>? OnProviderProgressReceived;

            public event Action<int>? OnProcessStarted;

            public event Action<int, int?>? OnProcessExited;

            public async Task<int> StartAsync(
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
            {
                int start = Interlocked.Increment(ref _Starts);
                int processId = 41000 + start;
                if (start == 2)
                {
                    SecondTurnEntered.TrySetResult(true);
                    await ReleaseSecondTurn.Task.ConfigureAwait(false);
                }

                if (!String.IsNullOrWhiteSpace(finalMessageFilePath))
                    await File.WriteAllTextAsync(finalMessageFilePath, "refinement answer " + start, token).ConfigureAwait(false);
                OnProcessExited?.Invoke(processId, 0);
                return processId;
            }

            public Task<AgentStopResult> StopAsync(int processId, CancellationToken token = default)
            {
                return Task.FromResult(AgentStopResult.Stopped());
            }

            public Task<bool> IsRunningAsync(int processId, CancellationToken token = default)
            {
                return Task.FromResult(false);
            }
        }

        /// <summary>
        /// Refinement runtime that accepts the launch and then fails before a process id exists.
        /// </summary>
        private sealed class ThrowingRefinementRuntime : IAgentRuntime
        {
            public string Name => "throwing-refinement";

            public bool SupportsResume => false;

            public bool SupportsPlanningSessions => true;

            public event Action<int, string>? OnOutputReceived;

            public event Action<int, string>? OnStdoutReceived;

            public event Action<int, RuntimeTokenUsage>? OnTokenUsageReceived;

            public event Action<int, RuntimeTokenUsage>? OnProviderProgressReceived;

            public event Action<int>? OnProcessStarted;

            public event Action<int, int?>? OnProcessExited;

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
            {
                throw new InvalidOperationException("refinement runtime stopped");
            }

            public Task<AgentStopResult> StopAsync(int processId, CancellationToken token = default)
            {
                return Task.FromResult(AgentStopResult.NotRunning());
            }

            public Task<bool> IsRunningAsync(int processId, CancellationToken token = default)
            {
                return Task.FromResult(false);
            }
        }

        #endregion
    }
}
