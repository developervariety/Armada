namespace Armada.Test.Unit.Suites.Routes
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Reflection;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Server;
    using Armada.Server.Mcp.Tools;
    using Armada.Server.WebSocket;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// Captain emergency stop, deletion and restart follow one rule on every interface. Stop all stops every
    /// working captain, planning session and refinement session and reports each failure. Deletion refuses a
    /// Working, Planning or Refining captain or one with an active mission, and removes the captain's events,
    /// planning sessions and refinement sessions on single and batch paths. Restart keeps the captain record.
    /// </summary>
    public sealed class CaptainAdministrationTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Captain Administration";

        private static readonly JsonSerializerOptions _ServerJsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        private static readonly CaptainStateEnum[] _BusyStates = new[]
        {
            CaptainStateEnum.Planning, CaptainStateEnum.Refining, CaptainStateEnum.Working
        };

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("Mcp stop_all stops every working captain and every active planning and refinement session", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Fixture fx = await Fixture.CreateAsync(testDb.Driver).ConfigureAwait(false);
                    StopAllScenario scenario = await StopAllScenario.CreateAsync(testDb.Driver).ConfigureAwait(false);
                    Dictionary<string, Func<JsonElement?, Task<object>>> tools = RegisterMcpTools(fx);

                    string json = JsonSerializer.Serialize(await tools["armada_stop_all"](JsonSerializer.SerializeToElement(new { })).ConfigureAwait(false));

                    await scenario.AssertAllStoppedAsync(this, fx, json).ConfigureAwait(false);
                }
            });

            await RunTest("WebSocket stop_all stops every working captain and every active planning and refinement session", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Fixture fx = await Fixture.CreateAsync(testDb.Driver).ConfigureAwait(false);
                    StopAllScenario scenario = await StopAllScenario.CreateAsync(testDb.Driver).ConfigureAwait(false);
                    WebSocketCommandHandler handler = CreateWebSocketHandler(fx);

                    string json = JsonSerializer.Serialize(await handler.HandleCommandAsync(
                        "stop_all", new WebSocketCommand { Action = "stop_all" }, "", McpTestCaller.Operator).ConfigureAwait(false));

                    AssertContains("command.result", json, "stop_all returns a result");
                    await scenario.AssertAllStoppedAsync(this, fx, json).ConfigureAwait(false);
                }
            });

            await RunTest("Stop all counts and names a session it could not stop and does not report all_stopped", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Fixture fx = await Fixture.CreateAsync(testDb.Driver).ConfigureAwait(false);
                    StopAllScenario scenario = await StopAllScenario.CreateAsync(testDb.Driver).ConfigureAwait(false);
                    fx.Administration.StopRefinementSession = (session, token) => throw new InvalidOperationException("refinement runtime did not exit");
                    Dictionary<string, Func<JsonElement?, Task<object>>> tools = RegisterMcpTools(fx);
                    WebSocketCommandHandler handler = CreateWebSocketHandler(fx);

                    string mcpJson = JsonSerializer.Serialize(await tools["armada_stop_all"](JsonSerializer.SerializeToElement(new { })).ConfigureAwait(false));
                    AssertContains("\"Status\":\"stopped_with_failures\"", mcpJson, "An unstopped session must not read as all_stopped");
                    AssertContains("\"CaptainsStopped\":1", mcpJson, "The working captain is still stopped");
                    AssertContains("\"PlanningSessionsStopped\":1", mcpJson, "The planning session is still stopped");
                    AssertContains("\"RefinementSessionsFailed\":1", mcpJson, "The refinement failure is counted");
                    AssertContains("\"Failed\":1", mcpJson, "The total failure count is reported");
                    AssertContains(scenario.RefinementSessionId, mcpJson, "The failed session is named");
                    AssertContains("refinement runtime did not exit", mcpJson, "The failure reason is carried");

                    string wsJson = JsonSerializer.Serialize(await handler.HandleCommandAsync(
                        "stop_all", new WebSocketCommand { Action = "stop_all" }, "", McpTestCaller.Operator).ConfigureAwait(false));
                    AssertContains("\"Status\":\"stopped_with_failures\"", wsJson, "WebSocket reports the same failure");
                    AssertContains(scenario.RefinementSessionId, wsJson, "WebSocket names the failed session");
                }
            });

            await RunTest("Stop all with no coordinator for a session kind reports those sessions as failed stops", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    StopAllScenario scenario = await StopAllScenario.CreateAsync(testDb.Driver).ConfigureAwait(false);
                    List<string> recalled = new List<string>();
                    CaptainAdministrationService service = new CaptainAdministrationService(testDb.Driver, (id, token) =>
                    {
                        recalled.Add(id);
                        return Task.CompletedTask;
                    });

                    CaptainStopAllResult result = await service.StopAllAsync().ConfigureAwait(false);

                    AssertEqual(CaptainStopAllResult.PartialStatus, result.Status);
                    AssertEqual(1, result.CaptainsStopped);
                    AssertEqual(1, result.PlanningSessionsFailed);
                    AssertEqual(1, result.RefinementSessionsFailed);
                    AssertEqual(2, result.Failures.Count);
                    AssertTrue(result.Failures.Any(f => f.Id == scenario.PlanningSessionId), "The planning session is named");
                }
            });

            await RunTest("Every interface refuses to delete a Working, Planning or Refining captain and leaves it in place", async () =>
            {
                foreach (CaptainStateEnum state in _BusyStates)
                {
                    foreach (string surface in DeleteSurfaces)
                    {
                        using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                        {
                            Fixture fx = await Fixture.CreateAsync(testDb.Driver).ConfigureAwait(false);
                            Captain captain = await CreateCaptainAsync(testDb.Driver, "busy-" + state, state).ConfigureAwait(false);
                            await CreateDependentsAsync(testDb.Driver, captain.Id).ConfigureAwait(false);

                            string json = await DeleteThroughAsync(fx, surface, captain.Id).ConfigureAwait(false);

                            AssertNotNull(await testDb.Driver.Captains.ReadAsync(captain.Id).ConfigureAwait(false), surface + " must keep a " + state + " captain");
                            AssertContains("Working, Planning, or Refining", json, surface + " names the busy rule for a " + state + " captain");
                            AssertEqual(1, (await testDb.Driver.PlanningSessions.EnumerateByCaptainAsync(captain.Id).ConfigureAwait(false)).Count, surface + " keeps the dependents of a refused delete");
                        }
                    }
                }
            });

            await RunTest("Every interface refuses to delete a captain with an Assigned or InProgress mission", async () =>
            {
                foreach (MissionStatusEnum status in new[] { MissionStatusEnum.Assigned, MissionStatusEnum.InProgress })
                {
                    foreach (string surface in DeleteSurfaces)
                    {
                        using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                        {
                            Fixture fx = await Fixture.CreateAsync(testDb.Driver).ConfigureAwait(false);
                            Captain captain = await CreateCaptainAsync(testDb.Driver, "mission-owner", CaptainStateEnum.Idle).ConfigureAwait(false);
                            Mission mission = new Mission("owned mission", "");
                            mission.CaptainId = captain.Id;
                            mission.Status = status;
                            await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                            string json = await DeleteThroughAsync(fx, surface, captain.Id).ConfigureAwait(false);

                            AssertContains("1 active mission(s)", json, surface + " names the active mission for " + status);
                            AssertNotNull(await testDb.Driver.Captains.ReadAsync(captain.Id).ConfigureAwait(false), surface + " must keep a captain with a " + status + " mission");
                        }
                    }
                }
            });

            await RunTest("Every interface deletes a captain in every other state and removes its events, planning sessions and refinement sessions", async () =>
            {
                foreach (CaptainStateEnum state in Enum.GetValues<CaptainStateEnum>().Where(s => !_BusyStates.Contains(s)))
                {
                    foreach (string surface in DeleteSurfaces)
                    {
                        using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                        {
                            Fixture fx = await Fixture.CreateAsync(testDb.Driver).ConfigureAwait(false);
                            Captain captain = await CreateCaptainAsync(testDb.Driver, "removable-" + state, state).ConfigureAwait(false);
                            Captain bystander = await CreateCaptainAsync(testDb.Driver, "bystander", CaptainStateEnum.Idle).ConfigureAwait(false);
                            await CreateDependentsAsync(testDb.Driver, captain.Id).ConfigureAwait(false);
                            await CreateDependentsAsync(testDb.Driver, bystander.Id).ConfigureAwait(false);

                            string json = await DeleteThroughAsync(fx, surface, captain.Id).ConfigureAwait(false);

                            string where = surface + " (" + state + ")";
                            AssertNull(await testDb.Driver.Captains.ReadAsync(captain.Id).ConfigureAwait(false), where + " deletes the captain: " + json);
                            AssertEqual(0, (await testDb.Driver.Events.EnumerateByCaptainAsync(captain.Id, 500).ConfigureAwait(false)).Count, where + " removes the captain's events");
                            AssertEqual(0, (await testDb.Driver.PlanningSessions.EnumerateByCaptainAsync(captain.Id).ConfigureAwait(false)).Count, where + " removes the captain's planning sessions");
                            AssertEqual(0, (await testDb.Driver.ObjectiveRefinementSessions.EnumerateByCaptainAsync(captain.Id).ConfigureAwait(false)).Count, where + " removes the captain's refinement sessions");
                            AssertEqual(1, (await testDb.Driver.Events.EnumerateByCaptainAsync(bystander.Id, 500).ConfigureAwait(false)).Count, where + " keeps another captain's events");
                            AssertEqual(1, (await testDb.Driver.PlanningSessions.EnumerateByCaptainAsync(bystander.Id).ConfigureAwait(false)).Count, where + " keeps another captain's planning sessions");
                            AssertEqual(1, (await testDb.Driver.ObjectiveRefinementSessions.EnumerateByCaptainAsync(bystander.Id).ConfigureAwait(false)).Count, where + " keeps another captain's refinement sessions");
                        }
                    }
                }
            });

            await RunTest("Restart keeps the identifier, every configuration field and ownership and resets only runtime state", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Fixture fx = await Fixture.CreateAsync(testDb.Driver).ConfigureAwait(false);
                    Captain original = await CreateFullyConfiguredCaptainAsync(testDb.Driver, CaptainStateEnum.Stalled).ConfigureAwait(false);

                    CaptainRestartResult result = await fx.Administration.RestartAsync(original.Id, McpTestCaller.Operator).ConfigureAwait(false);

                    AssertEqual(CaptainAdministrationOutcomeEnum.Completed, result.Outcome, result.Message);
                    Captain? stored = await testDb.Driver.Captains.ReadAsync(original.Id).ConfigureAwait(false);
                    AssertNotNull(stored, "Restart keeps the captain record");
                    AssertConfigurationEqual(original, stored!);
                    AssertEqual(original.TenantId, stored!.TenantId, "TenantId");
                    AssertEqual(original.UserId, stored.UserId, "UserId");
                    AssertEqual(original.CreatedUtc.ToString("O"), stored.CreatedUtc.ToString("O"), "CreatedUtc");
                    AssertEqual(1, (await testDb.Driver.Captains.EnumerateAsync().ConfigureAwait(false)).Count, "Restart creates no second captain");
                    AssertEqual(1, fx.StoppedProcesses.Count, "The leftover process is stopped once");
                    AssertEqual(CaptainStateEnum.Idle, stored.State, "A captain without a hold returns to Idle");
                    AssertNull(stored.ProcessId, "ProcessId is cleared");
                    AssertNull(stored.CurrentMissionId, "CurrentMissionId is cleared");
                    AssertNull(stored.CurrentDockId, "CurrentDockId is cleared");
                    AssertEqual(0, stored.RecoveryAttempts, "RecoveryAttempts is reset");
                    AssertNull(stored.LastHeartbeatUtc, "LastHeartbeatUtc is cleared");
                }
            });

            await RunTest("Restart keeps a quarantine hold and its reason", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Fixture fx = await Fixture.CreateAsync(testDb.Driver).ConfigureAwait(false);
                    Captain original = await CreateFullyConfiguredCaptainAsync(testDb.Driver, CaptainStateEnum.Quarantined).ConfigureAwait(false);

                    CaptainRestartResult result = await fx.Administration.RestartAsync(original.Id, McpTestCaller.Operator).ConfigureAwait(false);

                    AssertEqual(CaptainAdministrationOutcomeEnum.Completed, result.Outcome, result.Message);
                    Captain stored = (await testDb.Driver.Captains.ReadAsync(original.Id).ConfigureAwait(false))!;
                    AssertEqual(CaptainStateEnum.Quarantined, stored.State, "The hold stays in place");
                    AssertEqual(original.QuarantineReason, stored.QuarantineReason, "QuarantineReason");
                    AssertNotNull(stored.QuarantineUntilUtc, "QuarantineUntilUtc");
                }
            });

            await RunTest("A refused or failed restart leaves the captain unchanged", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Fixture fx = await Fixture.CreateAsync(testDb.Driver).ConfigureAwait(false);

                    foreach (CaptainStateEnum state in _BusyStates)
                    {
                        Captain busy = await CreateFullyConfiguredCaptainAsync(testDb.Driver, state).ConfigureAwait(false);
                        CaptainRestartResult refused = await fx.Administration.RestartAsync(busy.Id, McpTestCaller.Operator).ConfigureAwait(false);
                        AssertEqual(CaptainAdministrationOutcomeEnum.Busy, refused.Outcome, state + " is refused");
                        await AssertUnchangedAsync(testDb.Driver, busy).ConfigureAwait(false);
                    }

                    Captain failing = await CreateFullyConfiguredCaptainAsync(testDb.Driver, CaptainStateEnum.Stalled).ConfigureAwait(false);
                    fx.Administration.StopProcess = captain => throw new InvalidOperationException("process would not exit");
                    CaptainRestartResult failed = await fx.Administration.RestartAsync(failing.Id, McpTestCaller.Operator).ConfigureAwait(false);
                    AssertEqual(CaptainAdministrationOutcomeEnum.Failed, failed.Outcome, "A process that will not stop fails the restart");
                    AssertContains("process would not exit", failed.Message);
                    await AssertUnchangedAsync(testDb.Driver, failing).ConfigureAwait(false);

                    CaptainRestartResult missing = await fx.Administration.RestartAsync("cpt_missing", McpTestCaller.Operator).ConfigureAwait(false);
                    AssertEqual(CaptainAdministrationOutcomeEnum.NotFound, missing.Outcome);
                }
            });
        }

        private static readonly string[] DeleteSurfaces = new[] { "rest-single", "rest-batch", "mcp-single", "mcp-batch", "websocket" };

        private async Task<string> DeleteThroughAsync(Fixture fx, string surface, string captainId)
        {
            switch (surface)
            {
                case "rest-single":
                    // CaptainRoutes maps this result to 204, 404 or 409.
                    return JsonSerializer.Serialize(await fx.Administration.DeleteAsync(captainId, McpTestCaller.Operator).ConfigureAwait(false));
                case "rest-batch":
                    return JsonSerializer.Serialize(await fx.Administration.DeleteManyAsync(new[] { captainId }, McpTestCaller.Operator).ConfigureAwait(false));
                case "mcp-single":
                    return JsonSerializer.Serialize(await RegisterMcpTools(fx)["armada_delete_captain"](
                        JsonSerializer.SerializeToElement(new { captainId = captainId })).ConfigureAwait(false));
                case "mcp-batch":
                    return JsonSerializer.Serialize(await RegisterMcpTools(fx)["armada_delete_captains"](
                        JsonSerializer.SerializeToElement(new { ids = new[] { captainId } })).ConfigureAwait(false));
                case "websocket":
                    return JsonSerializer.Serialize(await CreateWebSocketHandler(fx).HandleCommandAsync(
                        "delete_captain", new WebSocketCommand { Action = "delete_captain", Id = captainId }, "", McpTestCaller.Operator).ConfigureAwait(false));
                default:
                    throw new ArgumentOutOfRangeException(nameof(surface), surface, "Unknown delete surface");
            }
        }

        private async Task AssertUnchangedAsync(DatabaseDriver database, Captain expected)
        {
            Captain? stored = await database.Captains.ReadAsync(expected.Id).ConfigureAwait(false);
            AssertNotNull(stored, "The captain still exists");
            AssertConfigurationEqual(expected, stored!);
            AssertEqual(expected.State, stored!.State, "State is unchanged");
            AssertEqual(expected.ProcessId, stored.ProcessId, "ProcessId is unchanged");
            AssertEqual(expected.CurrentMissionId, stored.CurrentMissionId, "CurrentMissionId is unchanged");
            AssertEqual(expected.RecoveryAttempts, stored.RecoveryAttempts, "RecoveryAttempts is unchanged");
            AssertEqual(expected.TenantId, stored.TenantId, "TenantId is unchanged");
        }

        private void AssertConfigurationEqual(Captain expected, Captain actual)
        {
            foreach (string field in CaptainInputMapping.CallerSettableFieldNames)
            {
                object? expectedValue = typeof(Captain).GetProperty(field)!.GetValue(expected);
                object? actualValue = typeof(Captain).GetProperty(field)!.GetValue(actual);
                AssertEqual(expectedValue, actualValue, field + " survives");
            }
        }

        private static async Task<Captain> CreateCaptainAsync(DatabaseDriver database, string name, CaptainStateEnum state)
        {
            Captain captain = new Captain(name + "-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            captain.State = state;
            return await database.Captains.CreateAsync(captain).ConfigureAwait(false);
        }

        private static async Task<Captain> CreateFullyConfiguredCaptainAsync(DatabaseDriver database, CaptainStateEnum state)
        {
            Captain captain = new Captain("configured-" + Guid.NewGuid().ToString("N").Substring(0, 6), AgentRuntimeEnum.Codex);
            captain.TenantId = Constants.DefaultTenantId;
            captain.UserId = Constants.DefaultUserId;
            captain.Model = "configured-model";
            ModelEndpoint endpoint = await database.ModelEndpoints.CreateAsync(new ModelEndpoint
            {
                Name = "restart-endpoint-" + Guid.NewGuid().ToString("N").Substring(0, 6),
                Kind = ModelEndpointKindEnum.Inference,
                Scope = ScopeEnum.TenantWide,
                Provider = ModelProviderEnum.OpenAICompatible,
                BaseUrl = "https://provider.example.test",
                Model = "configured-model",
                Enabled = true
            }).ConfigureAwait(false);
            captain.ModelEndpointId = endpoint.Id;
            captain.ApiKey = "configured-key";
            captain.ApiBaseUrl = "https://provider.example.test/v1";
            captain.SystemInstructions = "configured instructions";
            captain.AllowedPersonas = "[\"Worker\",\"Judge\"]";
            captain.PreferredPersona = "Judge";
            captain.RuntimeOptionsJson = "{\"reasoningEffort\":\"high\"}";
            captain.Tier = CaptainTierEnum.Premium;
            captain.PreferenceRank = 7;
            captain.DefaultPlaybooks = "[{\"playbookId\":\"pbk_configured\",\"deliveryMode\":\"InlineFullContent\"}]";
            captain.State = state;
            captain.ProcessId = 4242;
            captain.CurrentDockId = "dck_leftover";
            captain.RecoveryAttempts = 2;
            captain.LastHeartbeatUtc = DateTime.UtcNow.AddMinutes(-5);
            if (state == CaptainStateEnum.Quarantined)
            {
                captain.QuarantineReason = "operator hold";
                captain.QuarantineUntilUtc = DateTime.UtcNow.AddHours(1);
            }
            return await database.Captains.CreateAsync(captain).ConfigureAwait(false);
        }

        private static async Task CreateDependentsAsync(DatabaseDriver database, string captainId)
        {
            await database.Events.CreateAsync(new ArmadaEvent
            {
                EventType = "captain.assigned",
                Message = "captain.assigned",
                CaptainId = captainId
            }).ConfigureAwait(false);
            await database.PlanningSessions.CreateAsync(new PlanningSession
            {
                CaptainId = captainId,
                VesselId = "vsl_planning",
                Title = "Planning for " + captainId,
                Status = PlanningSessionStatusEnum.Stopped
            }).ConfigureAwait(false);
            Objective objective = await database.Objectives.CreateAsync(new Objective { Title = "Objective for " + captainId }).ConfigureAwait(false);
            await database.ObjectiveRefinementSessions.CreateAsync(new ObjectiveRefinementSession
            {
                ObjectiveId = objective.Id,
                CaptainId = captainId,
                Title = "Refinement for " + captainId,
                Status = ObjectiveRefinementSessionStatusEnum.Completed
            }).ConfigureAwait(false);
        }

        private static Dictionary<string, Func<JsonElement?, Task<object>>> RegisterMcpTools(Fixture fx)
        {
            Dictionary<string, Func<JsonElement?, Task<object>>> tools = new Dictionary<string, Func<JsonElement?, Task<object>>>(StringComparer.Ordinal);
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            McpCaptainTools.Register(
                (name, _, _, handler) => tools[name] = McpTestCaller.Wrap(handler),
                fx.Database,
                fx.Admiral,
                new ArmadaSettings(),
                null,
                null,
                logging,
                null,
                fx.Administration);
            return tools;
        }

        private static WebSocketCommandHandler CreateWebSocketHandler(Fixture fx)
        {
            WebSocketCommandHandler handler = new WebSocketCommandHandler(
                fx.Admiral,
                fx.Database,
                null!,
                null,
                null,
                null,
                _ServerJsonOptions,
                mission => { },
                voyage => { });
            handler.CaptainAdministration = fx.Administration;
            return handler;
        }

        /// <summary>
        /// A database, an admiral whose recall returns the captain to Idle, and the shared service with recording stops.
        /// </summary>
        private sealed class Fixture
        {
            public DatabaseDriver Database { get; private set; } = null!;
            public IAdmiralService Admiral { get; private set; } = null!;
            public CaptainAdministrationService Administration { get; private set; } = null!;
            public List<string> Recalled { get; } = new List<string>();
            public List<string> StoppedPlanning { get; } = new List<string>();
            public List<string> StoppedRefinement { get; } = new List<string>();
            public List<string> StoppedProcesses { get; } = new List<string>();

            public static Task<Fixture> CreateAsync(DatabaseDriver database)
            {
                Fixture fx = new Fixture();
                fx.Database = database;
                fx.Admiral = RecallAdmiralProxy.Create(database, fx.Recalled);
                fx.Administration = new CaptainAdministrationService(database, (id, token) => fx.Admiral.RecallCaptainAsync(id, token));
                fx.Administration.StopProcess = captain =>
                {
                    fx.StoppedProcesses.Add(captain.Id);
                    return Task.CompletedTask;
                };
                fx.Administration.StopPlanningSession = async (session, token) =>
                {
                    fx.StoppedPlanning.Add(session.Id);
                    session.Status = PlanningSessionStatusEnum.Stopped;
                    await database.PlanningSessions.UpdateAsync(session, token).ConfigureAwait(false);
                };
                fx.Administration.StopRefinementSession = async (session, token) =>
                {
                    fx.StoppedRefinement.Add(session.Id);
                    session.Status = ObjectiveRefinementSessionStatusEnum.Stopped;
                    await database.ObjectiveRefinementSessions.UpdateAsync(session, token).ConfigureAwait(false);
                };
                return Task.FromResult(fx);
            }
        }

        /// <summary>
        /// One working captain, one active planning session and one active refinement session, plus an idle captain and
        /// finished sessions that stop all must leave alone.
        /// </summary>
        private sealed class StopAllScenario
        {
            public string WorkingCaptainId { get; private set; } = "";
            public string IdleCaptainId { get; private set; } = "";
            public string PlanningSessionId { get; private set; } = "";
            public string RefinementSessionId { get; private set; } = "";

            public static async Task<StopAllScenario> CreateAsync(DatabaseDriver database)
            {
                StopAllScenario scenario = new StopAllScenario();
                Captain working = await CreateCaptainAsync(database, "working", CaptainStateEnum.Working).ConfigureAwait(false);
                Captain planner = await CreateCaptainAsync(database, "planner", CaptainStateEnum.Planning).ConfigureAwait(false);
                Captain refiner = await CreateCaptainAsync(database, "refiner", CaptainStateEnum.Refining).ConfigureAwait(false);
                Captain idle = await CreateCaptainAsync(database, "idle", CaptainStateEnum.Idle).ConfigureAwait(false);
                scenario.WorkingCaptainId = working.Id;
                scenario.IdleCaptainId = idle.Id;

                PlanningSession planning = await database.PlanningSessions.CreateAsync(new PlanningSession
                {
                    CaptainId = planner.Id,
                    VesselId = "vsl_planning",
                    Title = "Active planning",
                    Status = PlanningSessionStatusEnum.Responding
                }).ConfigureAwait(false);
                await database.PlanningSessions.CreateAsync(new PlanningSession
                {
                    CaptainId = idle.Id,
                    VesselId = "vsl_planning",
                    Title = "Finished planning",
                    Status = PlanningSessionStatusEnum.Stopped
                }).ConfigureAwait(false);
                scenario.PlanningSessionId = planning.Id;

                Objective objective = await database.Objectives.CreateAsync(new Objective { Title = "Refined objective" }).ConfigureAwait(false);
                ObjectiveRefinementSession refinement = await database.ObjectiveRefinementSessions.CreateAsync(new ObjectiveRefinementSession
                {
                    ObjectiveId = objective.Id,
                    CaptainId = refiner.Id,
                    Title = "Active refinement",
                    Status = ObjectiveRefinementSessionStatusEnum.Active
                }).ConfigureAwait(false);
                scenario.RefinementSessionId = refinement.Id;
                return scenario;
            }

            public async Task AssertAllStoppedAsync(CaptainAdministrationTests owner, Fixture fx, string resultJson)
            {
                owner.AssertTrue(fx.Recalled.SequenceEqual(new[] { WorkingCaptainId }), "Only the working captain is recalled");
                owner.AssertTrue(fx.StoppedPlanning.SequenceEqual(new[] { PlanningSessionId }), "The active planning session is stopped, the finished one is not");
                owner.AssertTrue(fx.StoppedRefinement.SequenceEqual(new[] { RefinementSessionId }), "The active refinement session is stopped");
                Captain? working = await fx.Database.Captains.ReadAsync(WorkingCaptainId).ConfigureAwait(false);
                owner.AssertEqual(CaptainStateEnum.Idle, working!.State, "The working captain is Idle after the stop");
                PlanningSession? planning = await fx.Database.PlanningSessions.ReadAsync(PlanningSessionId).ConfigureAwait(false);
                owner.AssertEqual(PlanningSessionStatusEnum.Stopped, planning!.Status, "The planning session is Stopped");
                ObjectiveRefinementSession? refinement = await fx.Database.ObjectiveRefinementSessions.ReadAsync(RefinementSessionId).ConfigureAwait(false);
                owner.AssertEqual(ObjectiveRefinementSessionStatusEnum.Stopped, refinement!.Status, "The refinement session is Stopped");
                owner.AssertContains("\"Status\":\"all_stopped\"", resultJson, "Every stop succeeded");
                owner.AssertContains("\"Stopped\":3", resultJson, "One captain and two sessions are reported stopped");
                owner.AssertContains("\"Failed\":0", resultJson, "No failure is reported");
            }
        }

        /// <summary>
        /// An admiral double: recall returns the captain to Idle and records it; recall-all recalls every working captain.
        /// Any other member is not used by these tests and throws.
        /// </summary>
        public class RecallAdmiralProxy : DispatchProxy
        {
            private DatabaseDriver _Database = null!;
            private List<string> _Recalled = null!;

            /// <summary>
            /// Create the double.
            /// </summary>
            /// <param name="database">Database driver.</param>
            /// <param name="recalled">Receives each recalled captain identifier.</param>
            /// <returns>The admiral double.</returns>
            public static IAdmiralService Create(DatabaseDriver database, List<string> recalled)
            {
                IAdmiralService proxy = DispatchProxy.Create<IAdmiralService, RecallAdmiralProxy>();
                RecallAdmiralProxy self = (RecallAdmiralProxy)(object)proxy;
                self._Database = database;
                self._Recalled = recalled;
                return proxy;
            }

            /// <inheritdoc />
            protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            {
                if (targetMethod == null) throw new ArgumentNullException(nameof(targetMethod));
                if (targetMethod.Name == nameof(IAdmiralService.RecallCaptainAsync))
                    return RecallAsync((string)args![0]!);
                if (targetMethod.Name == nameof(IAdmiralService.RecallAllAsync))
                    return RecallAllAsync();
                if (targetMethod.Name.StartsWith("get_", StringComparison.Ordinal) || targetMethod.Name.StartsWith("set_", StringComparison.Ordinal))
                    return null;
                throw new NotSupportedException("The admiral double does not implement " + targetMethod.Name + ".");
            }

            private async Task RecallAsync(string captainId)
            {
                _Recalled.Add(captainId);
                Captain? captain = await _Database.Captains.ReadAsync(captainId).ConfigureAwait(false);
                if (captain == null) throw new InvalidOperationException("Captain not found: " + captainId);
                captain.State = CaptainStateEnum.Idle;
                captain.ProcessId = null;
                captain.CurrentMissionId = null;
                await _Database.Captains.UpdateAsync(captain).ConfigureAwait(false);
            }

            private async Task RecallAllAsync()
            {
                foreach (Captain captain in await _Database.Captains.EnumerateByStateAsync(CaptainStateEnum.Working).ConfigureAwait(false))
                    await RecallAsync(captain.Id).ConfigureAwait(false);
            }
        }
    }
}
