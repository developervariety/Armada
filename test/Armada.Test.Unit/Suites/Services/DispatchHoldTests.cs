namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Net.Sockets;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Server;
    using Armada.Server.Mcp;
    using Armada.Server.Mcp.Tools;
    using Armada.Server.Routes;
    using Armada.Server.WebSocket;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using TestResourcePressure = global::Test.Shared.Infrastructure.TestResourcePressure;
    using SyslogLogging;
    using WatsonWebserver;
    using WatsonWebserver.Core;

    /// <summary>
    /// Tests for the fleet-wide dispatch hold: state transitions, the admiral
    /// dispatch guard, and the armada_dispatch_hold tool including its
    /// coordination-board announcements.
    /// </summary>
    public class DispatchHoldTests : TestSuite
    {
        private static readonly JsonSerializerOptions _JsonOpts = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };

        public override string Name => "DispatchHold";

        protected override async Task RunTestsAsync()
        {
            await RunTest("Engage snapshot and clear round-trips", () =>
            {
                DispatchHold hold = new DispatchHold();
                AssertNull(hold.Snapshot(), "a fresh hold must not be active");

                hold.Engage("rebuilding admiral", "session-a");
                DispatchHoldSnapshot? snap = hold.Snapshot();
                AssertNotNull(snap);
                AssertEqual("rebuilding admiral", snap!.Reason);
                AssertEqual("session-a", snap.SetBy);

                hold.Clear();
                AssertNull(hold.Snapshot(), "a cleared hold must not be active");
                return Task.CompletedTask;
            });

            await RunTest("ThrowIfActive names the holder reason and recovery action", () =>
            {
                DispatchHold hold = new DispatchHold();
                hold.ThrowIfActive();

                hold.Engage("schema migration", "session-b");
                Exception? captured = Capture(() => hold.ThrowIfActive());
                AssertNotNull(captured, "an active hold must refuse dispatch");
                AssertTrue(captured is InvalidOperationException, "the refusal must be an InvalidOperationException");
                AssertContains("Dispatch hold active", captured!.Message);
                AssertContains("schema migration", captured.Message);
                AssertContains("session-b", captured.Message);
                AssertContains("armada_dispatch_hold", captured.Message);

                hold.Clear();
                hold.ThrowIfActive();
                return Task.CompletedTask;
            });

            await RunTest("Engage requires a reason and a named session", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    (Func<JsonElement?, Task<object>> handler, _, _) = BuildToolHarness(testDb);

                    JsonElement missingReason = JsonSerializer.SerializeToElement(new { action = "engage", setBy = "s" }, _JsonOpts);
                    object result = await handler(missingReason).ConfigureAwait(false);
                    AssertContains("reason is required", JsonSerializer.Serialize(result));

                    JsonElement missingSetBy = JsonSerializer.SerializeToElement(new { action = "engage", reason = "r" }, _JsonOpts);
                    result = await handler(missingSetBy).ConfigureAwait(false);
                    AssertContains("setBy is required", JsonSerializer.Serialize(result));
                }
            });

            await RunTest("Engage announces on the board and status reports the hold until cleared", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    (Func<JsonElement?, Task<object>> handler, CoordinationService coordination, DispatchHold hold) = BuildToolHarness(testDb);

                    JsonElement engage = JsonSerializer.SerializeToElement(new { action = "engage", reason = "redeploy", setBy = "session-a" }, _JsonOpts);
                    object result = await handler(engage).ConfigureAwait(false);
                    string resultJson = JsonSerializer.Serialize(result);
                    AssertContains("\"Active\":true", resultJson);

                    var messages = await coordination.ReadMessagesAsync(CoordinationService.DefaultRoomKey);
                    AssertEqual(1, messages.Count);
                    AssertContains("[hold]", messages[0].Content);
                    AssertContains("redeploy", messages[0].Content);
                    AssertContains("session-a", messages[0].Content);

                    JsonElement status = JsonSerializer.SerializeToElement(new { action = "status" }, _JsonOpts);
                    result = await handler(status).ConfigureAwait(false);
                    AssertContains("\"Active\":true", JsonSerializer.Serialize(result));
                    AssertNotNull(hold.Snapshot());

                    JsonElement clear = JsonSerializer.SerializeToElement(new { action = "clear" }, _JsonOpts);
                    await handler(clear).ConfigureAwait(false);

                    var after = await coordination.ReadMessagesAsync(CoordinationService.DefaultRoomKey);
                    AssertEqual(2, after.Count);
                    AssertContains("resumed", after[1].Content);
                    AssertNull(hold.Snapshot());
                }
            });

            await RunTest("AdmiralService refuses dispatches while held and resumes after clear", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;
                    DispatchHold hold = new DispatchHold();
                    AdmiralService admiral = BuildAdmiral(testDb, logging, hold);
                    hold.Engage("probe redeploy", "session-hold");

                    Mission mission = new Mission { Title = "hold-probe", VesselId = "vsl_example" };
                    Exception? refused = await CaptureAsync(() => admiral.DispatchMissionAsync(mission));
                    AssertNotNull(refused, "a held admiral must refuse the dispatch");
                    AssertContains("Dispatch hold active", refused!.Message);

                    hold.Clear();

                    Vessel vessel = new Vessel { Name = "example-vessel", RepoUrl = "https://git.example.com/example.git" };
                    await testDb.Driver.Vessels.CreateAsync(vessel);

                    Mission mission2 = new Mission { Title = "hold-probe-2", VesselId = vessel.Id };
                    Mission dispatched = await admiral.DispatchMissionAsync(mission2);
                    AssertNotNull(dispatched);
                    AssertStartsWith("msn_", dispatched.Id);
                }
            });

            await RunSharedHoldTestAsync();
            await RunEntryPointComparisonTestAsync();
        }

        private async Task RunSharedHoldTestAsync()
        {
            await RunTest("One hold refuses operator, scheduler and autonomous rescue dispatch, and all resume after clear", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;
                    DispatchHold hold = new DispatchHold();
                    AdmiralService admiral = BuildAdmiral(testDb, logging, hold, 10);

                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(new Vessel
                    {
                        TenantId = Constants.DefaultTenantId,
                        UserId = Constants.DefaultUserId,
                        Name = "held-vessel",
                        RepoUrl = "https://git.example.com/held.git",
                        DefaultBranch = "main"
                    });
                    Vessel operatorVessel = await testDb.Driver.Vessels.CreateAsync(new Vessel
                    {
                        TenantId = Constants.DefaultTenantId,
                        UserId = Constants.DefaultUserId,
                        Name = "held-operator-vessel",
                        RepoUrl = "https://git.example.com/held-operator.git",
                        DefaultBranch = "main"
                    });
                    Vessel schedulerVessel = await testDb.Driver.Vessels.CreateAsync(new Vessel
                    {
                        TenantId = Constants.DefaultTenantId,
                        UserId = Constants.DefaultUserId,
                        Name = "held-scheduler-vessel",
                        RepoUrl = "https://git.example.com/held-scheduler.git",
                        DefaultBranch = "main"
                    });

                    Objective objective = await testDb.Driver.Objectives.CreateAsync(new Objective
                    {
                        TenantId = Constants.DefaultTenantId,
                        UserId = Constants.DefaultUserId,
                        Title = "Held objective",
                        Status = ObjectiveStatusEnum.Scoped,
                        AutoDispatchEnabled = true,
                        VesselIds = new List<string> { schedulerVessel.Id }
                    });

                    Voyage parent = await testDb.Driver.Voyages.CreateAsync(new Voyage("Parent voyage", "pipeline")
                    {
                        TenantId = Constants.DefaultTenantId,
                        UserId = Constants.DefaultUserId,
                        Status = VoyageStatusEnum.Failed
                    });
                    Mission failed = await testDb.Driver.Missions.CreateAsync(new Mission
                    {
                        TenantId = Constants.DefaultTenantId,
                        UserId = Constants.DefaultUserId,
                        VesselId = vessel.Id,
                        VoyageId = parent.Id,
                        Persona = "Worker",
                        Title = "Failed worker",
                        Description = "Original mission description",
                        Status = MissionStatusEnum.Failed,
                        FailureReason = "DoD gate failed: classification=TestFail; unit-test command exited 1",
                        CommitHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                        CompletedUtc = DateTime.UtcNow.AddMinutes(-1),
                        LastUpdateUtc = DateTime.UtcNow.AddMinutes(-1)
                    });

                    ArmadaSettings schedulerSettings = new ArmadaSettings
                    {
                        AutonomousObjectiveScheduler = new AutonomousObjectiveSchedulerSettings
                        {
                            Enabled = true,
                            IntervalMinutes = 1,
                            MaxConcurrentVoyages = 3
                        }
                    };
                    IncidentService incidents = new IncidentService(testDb.Driver);
                    RunbookService runbooks = new RunbookService(testDb.Driver, logging);
                    AutonomousRecoveryOrchestrator orchestrator = new AutonomousRecoveryOrchestrator(
                        testDb.Driver, admiral, incidents, runbooks, new ArmadaSettings(), logging,
                        null, null, null, null, null, null, null, null, hold);

                    hold.Engage("redeploy window", "session-hold");

                    Exception? operatorRefusal = await CaptureAsync(() => admiral.DispatchMissionAsync(
                        new Mission { Title = "operator", VesselId = operatorVessel.Id }));
                    AssertTrue(operatorRefusal is DispatchHoldActiveException, "operator dispatch must be refused by the hold");

                    AutonomousObjectiveScheduler heldScheduler = BuildScheduler(testDb, admiral, schedulerSettings, logging, hold);
                    await heldScheduler.SweepAsync();
                    AssertEqual("dispatch_hold", heldScheduler.LastSkipReason, "the scheduler names the hold");

                    await orchestrator.HandleMissionOutcomeAsync(failed, false);
                    await orchestrator.SweepAsync();

                    List<Voyage> heldVoyages = await testDb.Driver.Voyages.EnumerateAsync();
                    AssertEqual(1, heldVoyages.Count, "no voyage may be created while held, not even one cancelled at once; found: "
                        + String.Join(", ", heldVoyages.Select(v => v.Title + "/" + v.Status)));
                    List<Mission> heldRescues = (await testDb.Driver.Missions.EnumerateByVesselAsync(vessel.Id))
                        .Where(m => m.ParentMissionId == failed.Id).ToList();
                    AssertEqual(0, heldRescues.Count, "no rescue mission may exist while held");

                    AuthContext auth = AuthContext.Authenticated(Constants.DefaultTenantId, Constants.DefaultUserId, false, true, "UnitTest");
                    EnumerationResult<Incident> incidentPage = await incidents.EnumerateAsync(auth, new IncidentQuery
                    {
                        MissionId = failed.Id,
                        PageNumber = 1,
                        PageSize = 10
                    });
                    AssertEqual(1, incidentPage.Objects.Count, "the refused rescue is recorded on one incident");
                    string notes = incidentPage.Objects[0].RecoveryNotes ?? String.Empty;
                    AssertContains("dispatch_hold", notes, "the refusal is named on the incident");
                    AssertContains("redeploy window", notes, "the note carries the hold reason");
                    AssertEqual(1, CountOccurrences(notes, "dispatch_hold"), "repeat sweeps under one hold record the refusal once");

                    Mission? heldOriginal = await testDb.Driver.Missions.ReadAsync(failed.Id);
                    AssertEqual(0, heldOriginal!.RecoveryAttempts, "a refused rescue spends no recovery budget");

                    hold.Clear();

                    Mission operatorMission = await admiral.DispatchMissionAsync(new Mission { Title = "operator", VesselId = operatorVessel.Id });
                    AssertStartsWith("msn_", operatorMission.Id);

                    AutonomousObjectiveScheduler clearScheduler = BuildScheduler(testDb, admiral, schedulerSettings, logging, hold);
                    await clearScheduler.SweepAsync();
                    Objective? dispatchedObjective = await testDb.Driver.Objectives.ReadAsync(objective.Id);
                    AssertTrue(dispatchedObjective!.VoyageIds.Count == 1,
                        "the scheduler dispatches after clear; skip=" + clearScheduler.LastSkipReason + " summary=" + clearScheduler.LastResultSummary);

                    await orchestrator.SweepAsync();
                    List<Mission> rescues = (await testDb.Driver.Missions.EnumerateByVesselAsync(vessel.Id))
                        .Where(m => m.ParentMissionId == failed.Id).ToList();
                    AssertEqual(1, rescues.Count, "the deferred rescue is dispatched on the first sweep after clear");
                    AssertTrue(!String.IsNullOrEmpty(rescues[0].VoyageId) && rescues[0].VoyageId != parent.Id,
                        "the rescue runs in its own rescue voyage");
                }
            });
        }

        /// <summary>
        /// What one dispatch entry point did with one request.
        /// </summary>
        private sealed class EntryPointOutcome
        {
            public string EntryPoint { get; set; } = String.Empty;
            public bool Refused { get; set; }
            public bool NamesHold { get; set; }
            public bool NamesHolder { get; set; }
            public bool AcceptedJob { get; set; }
            public int VoyagesCreated { get; set; }
            public int MissionsCreated { get; set; }
            public string Detail { get; set; } = String.Empty;

            public string Signature => "refused=" + Refused + " namesHold=" + NamesHold + " namesHolder=" + NamesHolder
                + " acceptedJob=" + AcceptedJob + " voyages=" + VoyagesCreated + " missions=" + MissionsCreated;
        }

        private async Task RunEntryPointComparisonTestAsync()
        {
            await RunTest("Every dispatch entry point refuses one hold at submission by name, accepts and creates nothing, and dispatches after clear", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;
                    DispatchHold hold = new DispatchHold();
                    AdmiralService admiral = BuildAdmiral(testDb, logging, hold, 50);
                    AuthContext operatorCaller = McpTestCaller.Operator;

                    async Task<Vessel> VesselAsync(string name)
                    {
                        return await testDb.Driver.Vessels.CreateAsync(new Vessel
                        {
                            TenantId = Constants.DefaultTenantId,
                            UserId = Constants.DefaultUserId,
                            Name = name,
                            RepoUrl = "https://git.example.com/" + name + ".git",
                            DefaultBranch = "main"
                        });
                    }

                    // One vessel per entry point, so a per-vessel ceiling can never be the refusal.
                    Vessel mcpVessel = await VesselAsync("ep-mcp");
                    Vessel aliasVessel = await VesselAsync("ep-alias");
                    Vessel restVessel = await VesselAsync("ep-rest");
                    Vessel wsVessel = await VesselAsync("ep-ws");
                    Vessel planningVessel = await VesselAsync("ep-planning");
                    Vessel schedulerVessel = await VesselAsync("ep-scheduler");
                    Vessel rescueVessel = await VesselAsync("ep-rescue");

                    await testDb.Driver.Objectives.CreateAsync(new Objective
                    {
                        TenantId = Constants.DefaultTenantId,
                        UserId = Constants.DefaultUserId,
                        Title = "Scheduler entry point objective",
                        Status = ObjectiveStatusEnum.Scoped,
                        AutoDispatchEnabled = true,
                        VesselIds = new List<string> { schedulerVessel.Id }
                    });
                    Voyage rescueParent = await testDb.Driver.Voyages.CreateAsync(new Voyage("Rescue parent", "pipeline")
                    {
                        TenantId = Constants.DefaultTenantId,
                        UserId = Constants.DefaultUserId,
                        Status = VoyageStatusEnum.Failed
                    });
                    Mission failed = await testDb.Driver.Missions.CreateAsync(new Mission
                    {
                        TenantId = Constants.DefaultTenantId,
                        UserId = Constants.DefaultUserId,
                        VesselId = rescueVessel.Id,
                        VoyageId = rescueParent.Id,
                        Persona = "Worker",
                        Title = "Failed worker",
                        Description = "Original mission description",
                        Status = MissionStatusEnum.Failed,
                        FailureReason = "DoD gate failed: classification=TestFail; unit-test command exited 1",
                        CommitHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                        CompletedUtc = DateTime.UtcNow.AddMinutes(-1),
                        LastUpdateUtc = DateTime.UtcNow.AddMinutes(-1)
                    });
                    PlanningSession planningSession = await testDb.Driver.PlanningSessions.CreateAsync(new PlanningSession
                    {
                        TenantId = Constants.DefaultTenantId,
                        UserId = Constants.DefaultUserId,
                        VesselId = planningVessel.Id,
                        Title = "Entry point planning",
                        Status = PlanningSessionStatusEnum.Active
                    });
                    await testDb.Driver.PlanningSessionMessages.CreateAsync(new PlanningSessionMessage
                    {
                        PlanningSessionId = planningSession.Id,
                        Role = "Assistant",
                        Sequence = 1,
                        Content = "Implement the planned change."
                    });

                    // Entry point 1 and 2: the MCP armada_dispatch tool, plain and alias-ordered, with the
                    // background job service the admiral registers.
                    LongRunningJobService jobs = new LongRunningJobService();
                    ObjectiveService objectives = new ObjectiveService(testDb.Driver);
                    Func<JsonElement?, Task<object>>? mcpDispatch = null;
                    McpVoyageTools.Register(
                        (name, _, _, handler) => { if (name == "armada_dispatch") mcpDispatch = McpTestCaller.Wrap(handler); },
                        testDb.Driver,
                        admiral,
                        null,
                        null,
                        logging,
                        null,
                        objectives,
                        jobs);
                    AssertNotNull(mcpDispatch, "armada_dispatch must be registered");

                    // Entry point 4: WebSocket create_voyage.
                    JsonSerializerOptions wsJson = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    WebSocketCommandHandler ws = new WebSocketCommandHandler(admiral, testDb.Driver, null!, null, null, null, wsJson, _ => { }, _ => { });

                    // Entry point 5: planning-session dispatch, the seam both the REST and MCP planning routes call.
                    string planningRoot = Path.Combine(Path.GetTempPath(), "armada_hold_planning_" + Guid.NewGuid().ToString("N"));
                    ArmadaSettings planningSettings = new ArmadaSettings
                    {
                        DataDirectory = planningRoot,
                        DatabasePath = Path.Combine(planningRoot, "unused.db"),
                        LogDirectory = Path.Combine(planningRoot, "logs"),
                        DocksDirectory = Path.Combine(planningRoot, "docks"),
                        ReposDirectory = Path.Combine(planningRoot, "repos")
                    };
                    planningSettings.InitializeDirectories();
                    PlanningSessionCoordinator planning = new PlanningSessionCoordinator(
                        logging,
                        testDb.Driver,
                        planningSettings,
                        new DockService(logging, testDb.Driver, planningSettings, new GitService(logging)),
                        admiral,
                        new Armada.Runtimes.AgentRuntimeFactory(logging),
                        (a, b, c, d, e, f, g, h) => Task.CompletedTask,
                        null,
                        objectives);

                    // Entry point 6 and 7: the autonomous scheduler and autonomous rescue.
                    ArmadaSettings schedulerSettings = new ArmadaSettings
                    {
                        AutonomousObjectiveScheduler = new AutonomousObjectiveSchedulerSettings
                        {
                            Enabled = true,
                            IntervalMinutes = 1,
                            MaxConcurrentVoyages = 50
                        }
                    };
                    IncidentService incidents = new IncidentService(testDb.Driver);
                    AutonomousRecoveryOrchestrator orchestrator = new AutonomousRecoveryOrchestrator(
                        testDb.Driver, admiral, incidents, new RunbookService(testDb.Driver, logging), new ArmadaSettings(), logging,
                        null, null, null, null, null, null, null, null, hold);
                    AuthContext incidentReader = AuthContext.Authenticated(Constants.DefaultTenantId, Constants.DefaultUserId, false, true, "UnitTest");

                    // Entry point 3: REST POST /api/v1/voyages, served by the real route on a loopback port.
                    using (VoyageRouteHost rest = VoyageRouteHost.Start(testDb, admiral, objectives, logging, operatorCaller))
                    {
                        async Task<EntryPointOutcome> MeasureAsync(string entryPoint, Func<Task<string>> call, Func<string, bool> refused)
                        {
                            int voyagesBefore = (await testDb.Driver.Voyages.EnumerateAsync()).Count;
                            int missionsBefore = (await testDb.Driver.Missions.EnumerateAsync()).Count;
                            string detail;
                            try
                            {
                                detail = await call();
                            }
                            catch (Exception ex)
                            {
                                detail = "threw " + ex.GetType().Name + ": " + ex.Message;
                            }
                            int voyagesAfter = (await testDb.Driver.Voyages.EnumerateAsync()).Count;
                            int missionsAfter = (await testDb.Driver.Missions.EnumerateAsync()).Count;
                            return new EntryPointOutcome
                            {
                                EntryPoint = entryPoint,
                                Refused = refused(detail),
                                NamesHold = detail.Contains("dispatch_hold", StringComparison.Ordinal),
                                NamesHolder = detail.Contains("session-hold", StringComparison.Ordinal),
                                AcceptedJob = detail.Contains("\"JobId\"", StringComparison.Ordinal),
                                VoyagesCreated = voyagesAfter - voyagesBefore,
                                MissionsCreated = missionsAfter - missionsBefore,
                                Detail = detail.Length > 400 ? detail.Substring(0, 400) : detail
                            };
                        }

                        async Task<string> McpAsync(Vessel vessel, bool aliased)
                        {
                            object[] missions = aliased
                                ? new object[]
                                {
                                    new { title = "second", description = "d2", alias = "B", dependsOnMissionAlias = "A" },
                                    new { title = "first", description = "d1", alias = "A" }
                                }
                                : new object[] { new { title = "only", description = "d" } };
                            JsonElement args = JsonSerializer.SerializeToElement(new
                            {
                                title = "entry point " + vessel.Name,
                                vesselId = vessel.Id,
                                codeContextMode = "off",
                                missions = missions
                            });
                            object result = await mcpDispatch!(args);
                            string json = JsonSerializer.Serialize(result);
                            LongRunningJob? accepted = JsonSerializer.Deserialize<LongRunningJob>(json);
                            if (accepted == null || String.IsNullOrEmpty(accepted.JobId)) return json;

                            // An accepted job: follow it to its end so the outcome counts what it created.
                            DateTime deadline = DateTime.UtcNow.AddSeconds(20);
                            while (DateTime.UtcNow < deadline)
                            {
                                if (jobs.TryGetStatus(accepted.JobId, out LongRunningJob? status) && status != null
                                    && status.Status != LongRunningJobStatusEnum.Accepted && status.Status != LongRunningJobStatusEnum.Running)
                                    return json + " -> " + status.Status + " " + status.FailureMessage + " " + JsonSerializer.Serialize(status.Result);
                                await Task.Delay(25);
                            }
                            return json + " -> still unfinished after 20 s";
                        }

                        async Task<string> RestAsync(Vessel vessel)
                        {
                            string body = JsonSerializer.Serialize(new
                            {
                                Title = "entry point rest",
                                VesselId = vessel.Id,
                                CodeContextMode = "off",
                                Missions = new[] { new { Title = "only", Description = "d" } }
                            });
                            using (StringContent content = new StringContent(body, Encoding.UTF8, "application/json"))
                            using (HttpResponseMessage response = await rest.Client.PostAsync("api/v1/voyages", content))
                            {
                                return "HTTP " + (int)response.StatusCode + " " + await response.Content.ReadAsStringAsync();
                            }
                        }

                        async Task<string> WsAsync(Vessel vessel)
                        {
                            string raw = JsonSerializer.Serialize(new
                            {
                                Route = "command",
                                action = "create_voyage",
                                data = new
                                {
                                    Title = "entry point ws",
                                    VesselId = vessel.Id,
                                    Missions = new[] { new { Title = "only", Description = "d" } }
                                }
                            });
                            object result = await ws.HandleCommandAsync("create_voyage", new WebSocketCommand { Action = "create_voyage" }, raw, operatorCaller);
                            return JsonSerializer.Serialize(result);
                        }

                        async Task<string> PlanningAsync()
                        {
                            Voyage voyage = await planning.DispatchAsync(planningSession, new PlanningSessionDispatchRequest { Title = "entry point planning" });
                            return "created " + voyage.Id;
                        }

                        async Task<string> SchedulerAsync()
                        {
                            AutonomousObjectiveScheduler scheduler = BuildScheduler(testDb, admiral, schedulerSettings, logging, hold);
                            await scheduler.SweepAsync();
                            List<ArmadaEvent> events = await testDb.Driver.Events.EnumerateRecentAsync(200);
                            string holdEvent = String.Join(" | ", events
                                .Where(e => e.EventType == "objective_scheduler.skipped_dispatch_hold")
                                .Select(e => e.Message));
                            return "skip=" + scheduler.LastSkipReason + " summary=" + scheduler.LastResultSummary + " " + holdEvent;
                        }

                        async Task<string> RescueAsync()
                        {
                            await orchestrator.HandleMissionOutcomeAsync(failed, false);
                            await orchestrator.SweepAsync();
                            EnumerationResult<Incident> page = await incidents.EnumerateAsync(incidentReader, new IncidentQuery
                            {
                                MissionId = failed.Id,
                                PageNumber = 1,
                                PageSize = 10
                            });
                            List<Mission> rescues = (await testDb.Driver.Missions.EnumerateByVesselAsync(rescueVessel.Id))
                                .Where(m => m.ParentMissionId == failed.Id).ToList();
                            return "rescues=" + rescues.Count + " notes=" + String.Join(" | ", page.Objects.Select(i => i.RecoveryNotes));
                        }

                        bool OperatorRefused(string detail) => detail.Contains("dispatch_hold_active", StringComparison.Ordinal)
                            && !detail.Contains("\"JobId\"", StringComparison.Ordinal);

                        hold.Engage("entry point comparison", "session-hold");

                        List<EntryPointOutcome> held = new List<EntryPointOutcome>
                        {
                            await MeasureAsync("mcp armada_dispatch", () => McpAsync(mcpVessel, false), OperatorRefused),
                            await MeasureAsync("mcp armada_dispatch alias-ordered", () => McpAsync(aliasVessel, true), OperatorRefused),
                            await MeasureAsync("rest POST /api/v1/voyages", () => RestAsync(restVessel),
                                detail => detail.StartsWith("HTTP 409", StringComparison.Ordinal) && OperatorRefused(detail)),
                            await MeasureAsync("websocket create_voyage", () => WsAsync(wsVessel),
                                detail => detail.Contains("command.error", StringComparison.Ordinal) && OperatorRefused(detail)),
                            await MeasureAsync("planning-session dispatch", PlanningAsync, OperatorRefused),
                            await MeasureAsync("autonomous scheduler", SchedulerAsync,
                                detail => detail.StartsWith("skip=dispatch_hold ", StringComparison.Ordinal)),
                            await MeasureAsync("autonomous rescue", RescueAsync,
                                detail => detail.StartsWith("rescues=0 ", StringComparison.Ordinal) && detail.Contains("dispatch_hold", StringComparison.Ordinal))
                        };

                        string heldTable = String.Join(Environment.NewLine, held.Select(o => o.EntryPoint + ": " + o.Signature + " :: " + o.Detail));
                        AssertEqual(7, held.Count, "every entry point is measured");
                        AssertEqual(1, held.Select(o => o.Signature).Distinct().Count(),
                            "every entry point must behave the same under the hold:" + Environment.NewLine + heldTable);
                        EntryPointOutcome sample = held[0];
                        AssertTrue(sample.Refused && sample.NamesHold && sample.NamesHolder && !sample.AcceptedJob
                            && sample.VoyagesCreated == 0 && sample.MissionsCreated == 0,
                            "the shared behaviour is a named refusal that accepts and creates nothing:" + Environment.NewLine + heldTable);

                        hold.Clear();

                        // The same entry points with the hold cleared. Each must dispatch, so a refusal above
                        // was the hold and not a fixture that could never have dispatched.
                        List<EntryPointOutcome> clear = new List<EntryPointOutcome>
                        {
                            await MeasureAsync("mcp armada_dispatch", () => McpAsync(mcpVessel, false), d => !d.Contains("Succeeded", StringComparison.Ordinal)),
                            await MeasureAsync("mcp armada_dispatch alias-ordered", () => McpAsync(aliasVessel, true), d => !d.Contains("Succeeded", StringComparison.Ordinal)),
                            await MeasureAsync("rest POST /api/v1/voyages", () => RestAsync(restVessel), d => !d.StartsWith("HTTP 201", StringComparison.Ordinal)),
                            await MeasureAsync("websocket create_voyage", () => WsAsync(wsVessel), d => !d.Contains("command.result", StringComparison.Ordinal)),
                            await MeasureAsync("planning-session dispatch", PlanningAsync, d => !d.StartsWith("created vyg_", StringComparison.Ordinal)),
                            await MeasureAsync("autonomous scheduler", SchedulerAsync, d => d.Contains("skip=dispatch_hold", StringComparison.Ordinal)),
                            await MeasureAsync("autonomous rescue", RescueAsync, d => !d.StartsWith("rescues=1 ", StringComparison.Ordinal))
                        };

                        string clearTable = String.Join(Environment.NewLine, clear.Select(o => o.EntryPoint + ": " + o.Signature + " :: " + o.Detail));
                        foreach (EntryPointOutcome outcome in clear)
                        {
                            AssertFalse(outcome.Refused, outcome.EntryPoint + " must dispatch once the hold is cleared:" + Environment.NewLine + clearTable);
                            AssertTrue(outcome.VoyagesCreated >= 1 || outcome.MissionsCreated >= 1,
                                outcome.EntryPoint + " must create its work once the hold is cleared:" + Environment.NewLine + clearTable);
                        }
                    }
                }
            });
        }

        /// <summary>
        /// Serves the real voyage routes on a loopback port, authenticating every request as one caller.
        /// </summary>
        private sealed class VoyageRouteHost : IDisposable
        {
            private readonly Webserver _Server;
            private readonly CancellationTokenSource _Cancellation;

            public HttpClient Client { get; }

            private VoyageRouteHost(Webserver server, CancellationTokenSource cancellation, HttpClient client)
            {
                _Server = server;
                _Cancellation = cancellation;
                Client = client;
            }

            public static VoyageRouteHost Start(TestDatabase database, Armada.Core.Services.Interfaces.IAdmiralService admiral, ObjectiveService objectives, LoggingModule logging, AuthContext caller)
            {
                int port;
                using (TcpListener listener = new TcpListener(IPAddress.Loopback, 0))
                {
                    listener.Start();
                    port = ((IPEndPoint)listener.LocalEndpoint).Port;
                    listener.Stop();
                }

                WebserverSettings webserverSettings = new WebserverSettings();
                webserverSettings.Hostname = "127.0.0.1";
                webserverSettings.Port = port;
                Webserver server = new Webserver(webserverSettings, async (HttpContextBase ctx) =>
                {
                    ctx.Response.StatusCode = 404;
                    await ctx.Response.Send().ConfigureAwait(false);
                });

                JsonSerializerOptions jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
                VoyageRoutes routes = new VoyageRoutes(
                    database.Driver,
                    admiral,
                    (a, b, c, d, e, f, g, h) => Task.CompletedTask,
                    null,
                    logging,
                    objectives,
                    null,
                    null,
                    jsonOptions);
                routes.Register(server, ctx => Task.FromResult(caller), new AuthorizationService());

                CancellationTokenSource cancellation = new CancellationTokenSource();
                server.Start(cancellation.Token);
                HttpClient client = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:" + port + "/"), Timeout = TimeSpan.FromSeconds(30) };
                return new VoyageRouteHost(server, cancellation, client);
            }

            public void Dispose()
            {
                Client.Dispose();
                _Cancellation.Cancel();
                try { _Server.Stop(); }
                catch (Exception exception) { Console.WriteLine("Voyage route test server stop failed: " + exception.Message); }
                _Server.Dispose();
                _Cancellation.Dispose();
            }
        }

        private static int CountOccurrences(string text, string value)
        {
            int count = 0;
            int index = text.IndexOf(value, StringComparison.Ordinal);
            while (index >= 0)
            {
                count++;
                index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal);
            }
            return count;
        }

        private static AutonomousObjectiveScheduler BuildScheduler(
            TestDatabase testDb,
            AdmiralService admiral,
            ArmadaSettings settings,
            LoggingModule logging,
            DispatchHold hold)
        {
            return new AutonomousObjectiveScheduler(
                testDb.Driver,
                new ObjectiveService(testDb.Driver),
                admiral,
                new Armada.Test.Unit.Suites.Recovery.MergeRecoveryHandlerRebasePathTests.StubMergeQueueServiceForRecovery(),
                settings,
                logging,
                null,
                hold);
        }

        private static Exception? Capture(Action action)
        {
            try
            {
                action();
                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        }

        private static async Task<Exception?> CaptureAsync(Func<Task> action)
        {
            try
            {
                await action().ConfigureAwait(false);
                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        }

        private (Func<JsonElement?, Task<object>> Handler, CoordinationService Coordination, DispatchHold Hold) BuildToolHarness(TestDatabase testDb)
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            DispatchHold hold = new DispatchHold();
            CoordinationService coordination = new CoordinationService(logging, testDb.Driver);

            Func<JsonElement?, Task<object>>? handler = null;
            McpCoordinationTools.Register(
                (name, _, _, h) => { if (name == "armada_dispatch_hold") handler = h; },
                testDb.Driver,
                coordination,
                hold);
            AssertNotNull(handler, "armada_dispatch_hold handler must be registered");
            return (handler!, coordination, hold);
        }

        private AdmiralService BuildAdmiral(TestDatabase testDb, LoggingModule logging, DispatchHold hold, int maxConcurrentVoyages = 1)
        {
            ArmadaSettings settings = new ArmadaSettings
            {
                AutonomousObjectiveScheduler = new AutonomousObjectiveSchedulerSettings
                {
                    MaxConcurrentVoyages = maxConcurrentVoyages
                },
                DataDirectory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "armada_hold_fixture_" + Guid.NewGuid().ToString("N")),
                DatabasePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "armada_hold_fixture_" + Guid.NewGuid().ToString("N"), "unused.db"),
                LogDirectory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "armada_hold_fixture_" + Guid.NewGuid().ToString("N"), "logs"),
                DocksDirectory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "armada_hold_fixture_" + Guid.NewGuid().ToString("N"), "docks"),
                ReposDirectory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "armada_hold_fixture_" + Guid.NewGuid().ToString("N"), "repos")
            };
            settings.InitializeDirectories();

            GitService git = new GitService(logging);
            DockService docks = new DockService(logging, testDb.Driver, settings, git);
            CaptainService captains = new CaptainService(logging, testDb.Driver, settings, git, docks);
            MissionService missions = new MissionService(logging, testDb.Driver, settings, docks, captains, resourcePressureAdmission: TestResourcePressure.Unconstrained(settings));
            VoyageService voyages = new VoyageService(logging, testDb.Driver);
            return new AdmiralService(logging, testDb.Driver, settings, captains, missions, voyages, docks, null, null, null, null, git, hold);
        }
    }
}
