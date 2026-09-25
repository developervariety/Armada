namespace Armada.Test.Unit.Suites.Services
{
    using Microsoft.Data.Sqlite;
    using Armada.Core;
    using Armada.Core.Database;
    using Armada.Core.Database.Sqlite;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Runtimes;
    using Armada.Server;
    using Armada.Server.Mcp;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using TestResourcePressure = global::Test.Shared.Infrastructure.TestResourcePressure;
    using SyslogLogging;

    public class PlanningSessionCoordinatorTests : TestSuite
    {
        public override string Name => "Planning Session Coordinator";

        protected override async Task RunTestsAsync()
        {
            await RunTest("A planning reply stores the answer and keeps tool activity records out of the message", async () =>
            {
                LoggingModule transformLogging = new LoggingModule();
                transformLogging.Settings.EnableConsole = false;
                List<string> records = new OpenCodeRecordTransform(transformLogging).Records(CaptainChatServiceTests.OpenCodeToolThenAnswer);

                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                using (CoordinatorFixture fixture = new CoordinatorFixture(testDb.Driver, records))
                {
                    Vessel vessel = await fixture.CreateVesselAsync("planning-activity").ConfigureAwait(false);
                    Captain captain = await fixture.CreateCaptainAsync("planner-opencode", AgentRuntimeEnum.OpenCode).ConfigureAwait(false);
                    PlanningSession session = await fixture.Coordinator.CreateAsync(
                        null,
                        null,
                        captain,
                        vessel,
                        new PlanningSessionCreateRequest { Title = "Plan with tools" }).ConfigureAwait(false);

                    await fixture.Coordinator.SendMessageAsync(session, "List the entries").ConfigureAwait(false);

                    string content = String.Empty;
                    DateTime deadline = DateTime.UtcNow.AddSeconds(20);
                    while (DateTime.UtcNow < deadline)
                    {
                        PlanningSession current = (await testDb.Driver.PlanningSessions.ReadAsync(session.Id).ConfigureAwait(false))!;
                        List<PlanningSessionMessage> messages = await testDb.Driver.PlanningSessionMessages.EnumerateBySessionAsync(session.Id).ConfigureAwait(false);
                        PlanningSessionMessage? assistant = messages.FindLast(message => String.Equals(message.Role, "Assistant", StringComparison.OrdinalIgnoreCase));
                        content = assistant?.Content ?? String.Empty;
                        if (current.Status != PlanningSessionStatusEnum.Responding && content.Length > 0) break;
                        await Task.Delay(100).ConfigureAwait(false);
                    }

                    Console.WriteLine("PLANNING content: " + content.Replace("\n", "\\n"));
                    AssertContains("Here are the entries", content, "The planning message holds the answer");
                    AssertFalse(content.Contains(ActivityRecords.ActivityMarker, StringComparison.Ordinal), "No activity record reaches the planning message");
                }
            });

            await RunTest("A planning reply larger than the live buffer keeps a marked, bounded tail", async () =>
            {
                // A planning runtime can stream far more output than a reply needs. The stored reply
                // must stay bounded however many chunks arrive, say that it was truncated, and keep
                // the newest output, which is where the planner's answer lands.
                const int capChars = 256 * 1024;
                const string truncationMarker = "[ARMADA: planning output truncated to retain tail]";
                const string finalLine = "FINAL PLAN: ship the bounded buffer";
                List<string> records = new List<string>();
                for (int i = 0; i < 12; i++)
                {
                    records.Add("chunk-" + i.ToString("D2") + ": " + new string('p', 32 * 1024));
                }
                records.Add("repeated line");
                records.Add("repeated line");
                records.Add(finalLine);

                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                using (CoordinatorFixture fixture = new CoordinatorFixture(testDb.Driver, records))
                {
                    Vessel vessel = await fixture.CreateVesselAsync("planning-bounded-output").ConfigureAwait(false);
                    Captain captain = await fixture.CreateCaptainAsync("planner-bounded-output").ConfigureAwait(false);
                    PlanningSession session = await fixture.Coordinator.CreateAsync(
                        null,
                        null,
                        captain,
                        vessel,
                        new PlanningSessionCreateRequest { Title = "Plan with a large reply" }).ConfigureAwait(false);

                    await fixture.Coordinator.SendMessageAsync(session, "Write a long plan").ConfigureAwait(false);

                    string content = String.Empty;
                    DateTime deadline = DateTime.UtcNow.AddSeconds(20);
                    while (DateTime.UtcNow < deadline)
                    {
                        PlanningSession current = (await testDb.Driver.PlanningSessions.ReadAsync(session.Id).ConfigureAwait(false))!;
                        List<PlanningSessionMessage> messages = await testDb.Driver.PlanningSessionMessages.EnumerateBySessionAsync(session.Id).ConfigureAwait(false);
                        PlanningSessionMessage? assistant = messages.FindLast(message => String.Equals(message.Role, "Assistant", StringComparison.OrdinalIgnoreCase));
                        content = assistant?.Content ?? String.Empty;
                        if (current.Status != PlanningSessionStatusEnum.Responding && content.Length > 0) break;
                        await Task.Delay(100).ConfigureAwait(false);
                    }

                    int streamedChars = 0;
                    foreach (string record in records) streamedChars += record.Length;
                    AssertTrue(streamedChars > capChars, "the replayed output must exceed the buffer to exercise the bound");
                    AssertTrue(content.Length <= capChars,
                        "the stored reply must stay within the live buffer bound; got " + content.Length + " characters");
                    AssertTrue(content.Length > capChars / 2, "the bound must keep a useful tail, not discard the output");
                    AssertStartsWith(truncationMarker, content, "a truncated reply must say that it was truncated");
                    AssertEqual(content.IndexOf(truncationMarker, StringComparison.Ordinal), content.LastIndexOf(truncationMarker, StringComparison.Ordinal),
                        "repeated truncation must not stack markers");
                    AssertTrue(content.EndsWith(finalLine, StringComparison.Ordinal), "the newest output must survive truncation");
                    AssertContains("repeated line" + Environment.NewLine + "repeated line", content, "repeated chunks near the tail must all be kept");
                    AssertContains("chunk-11: ", content, "the newest large chunk must survive truncation");
                    AssertFalse(content.Contains("chunk-00: ", StringComparison.Ordinal), "the oldest output must be dropped first");
                }
            });

            await RunTest("DispatchAsync links every planning objective inside dispatch admission", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                using (CoordinatorFixture fixture = new CoordinatorFixture(testDb.Driver))
                {
                    PlanningObjectivesResult planning = await CreateTwoObjectivePlanningAsync(fixture, testDb, "multi-link").ConfigureAwait(false);

                    Voyage voyage = await fixture.Coordinator.DispatchAsync(
                        planning.Session,
                        new PlanningSessionDispatchRequest { MessageId = planning.MessageId, Title = "Multi-objective dispatch" }).ConfigureAwait(false);

                    foreach (Objective objective in new[] { planning.Primary, planning.Secondary })
                    {
                        Objective stored = (await testDb.Driver.Objectives.ReadAsync(objective.Id).ConfigureAwait(false))!;
                        AssertTrue(stored.VoyageIds.Contains(voyage.Id),
                            "The planning dispatch itself must link objective " + objective.Title + "; no caller may link after creation.");
                        AssertNull(await testDb.Driver.CoordinationLeases.ReadAsync(
                                ObjectiveService.BuildDispatchAdmissionLeaseName(planning.TenantId, objective.Id)).ConfigureAwait(false),
                            "Every admission must be released after the admitted links.");
                    }
                }
            });

            await RunTest("DispatchAsync refuses before creating a voyage when any planning objective is already dispatched", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                using (CoordinatorFixture fixture = new CoordinatorFixture(testDb.Driver))
                {
                    PlanningObjectivesResult planning = await CreateTwoObjectivePlanningAsync(fixture, testDb, "multi-refuse").ConfigureAwait(false);
                    Voyage winner = await testDb.Driver.Voyages.CreateAsync(new Voyage("Existing winner")
                    {
                        TenantId = planning.TenantId,
                        UserId = planning.UserId,
                        Status = VoyageStatusEnum.InProgress
                    }).ConfigureAwait(false);
                    Objective secondary = (await testDb.Driver.Objectives.ReadAsync(planning.Secondary.Id).ConfigureAwait(false))!;
                    secondary.VoyageIds.Add(winner.Id);
                    secondary.Status = ObjectiveStatusEnum.InProgress;
                    await testDb.Driver.Objectives.UpdateAsync(secondary).ConfigureAwait(false);

                    Exception? failure = null;
                    try
                    {
                        await fixture.Coordinator.DispatchAsync(
                            planning.Session,
                            new PlanningSessionDispatchRequest { MessageId = planning.MessageId, Title = "Refused planning dispatch" }).ConfigureAwait(false);
                    }
                    catch (InvalidOperationException ex)
                    {
                        failure = ex;
                    }

                    AssertNotNull(failure, "A planning dispatch covering an already-dispatched objective must be refused.");
                    AssertContains("objective_already_dispatched", failure!.Message);
                    List<Voyage> voyages = await testDb.Driver.Voyages.EnumerateAsync().ConfigureAwait(false);
                    AssertEqual(1, voyages.Count, "Admission must refuse before any planning voyage is created.");
                    Objective primary = (await testDb.Driver.Objectives.ReadAsync(planning.Primary.Id).ConfigureAwait(false))!;
                    AssertEqual(0, primary.VoyageIds.Count, "No objective may be linked by a refused dispatch.");
                    foreach (Objective objective in new[] { planning.Primary, planning.Secondary })
                    {
                        AssertNull(await testDb.Driver.CoordinationLeases.ReadAsync(
                                ObjectiveService.BuildDispatchAdmissionLeaseName(planning.TenantId, objective.Id)).ConfigureAwait(false),
                            "A refused dispatch releases every admission.");
                    }
                }
            });

            await RunTest("DispatchAsync defaults to the latest non-empty assistant response", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                using (CoordinatorFixture fixture = new CoordinatorFixture(testDb.Driver))
                {
                    Vessel vessel = await fixture.CreateVesselAsync("planning-vessel-default-dispatch").ConfigureAwait(false);
                    Captain captain = await fixture.CreateCaptainAsync("planner-default-dispatch").ConfigureAwait(false);

                    PlanningSession session = await fixture.Coordinator.CreateAsync(
                        null,
                        null,
                        captain,
                        vessel,
                        new PlanningSessionCreateRequest
                        {
                            Title = "Default source selection"
                        }).ConfigureAwait(false);

                    await testDb.Driver.PlanningSessionMessages.CreateAsync(new PlanningSessionMessage
                    {
                        PlanningSessionId = session.Id,
                        Role = "Assistant",
                        Sequence = 1,
                        Content = "Older planning response"
                    }).ConfigureAwait(false);

                    await testDb.Driver.PlanningSessionMessages.CreateAsync(new PlanningSessionMessage
                    {
                        PlanningSessionId = session.Id,
                        Role = "Assistant",
                        Sequence = 2,
                        Content = ""
                    }).ConfigureAwait(false);

                    PlanningSessionMessage latestAssistant = await testDb.Driver.PlanningSessionMessages.CreateAsync(new PlanningSessionMessage
                    {
                        PlanningSessionId = session.Id,
                        Role = "Assistant",
                        Sequence = 3,
                        Content = "Latest planning response should be dispatched."
                    }).ConfigureAwait(false);

                    Voyage voyage = await fixture.Coordinator.DispatchAsync(
                        session,
                        new PlanningSessionDispatchRequest()).ConfigureAwait(false);

                    AssertEqual(session.Id, voyage.SourcePlanningSessionId);
                    AssertEqual(latestAssistant.Id, voyage.SourcePlanningMessageId);

                    List<Mission> missions = await testDb.Driver.Missions.EnumerateByVoyageAsync(voyage.Id).ConfigureAwait(false);
                    AssertEqual(1, missions.Count);
                    AssertEqual("Latest planning response should be dispatched.", missions[0].Description);
                }
            });

            await RunTest("RecoverSessionsAsync restores responding session to active state", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                using (CoordinatorFixture fixture = new CoordinatorFixture(testDb.Driver))
                {
                    Vessel vessel = await fixture.CreateVesselAsync().ConfigureAwait(false);
                    Captain captain = await fixture.CreateCaptainAsync("planner-recover").ConfigureAwait(false);

                    PlanningSession session = await fixture.Coordinator.CreateAsync(
                        null,
                        null,
                        captain,
                        vessel,
                        new PlanningSessionCreateRequest
                        {
                            Title = "Recover me"
                        }).ConfigureAwait(false);

                    session.Status = PlanningSessionStatusEnum.Responding;
                    session.ProcessId = Int32.MaxValue;
                    await testDb.Driver.PlanningSessions.UpdateAsync(session).ConfigureAwait(false);

                    captain.State = CaptainStateEnum.Idle;
                    captain.CurrentDockId = null;
                    captain.ProcessId = null;
                    await testDb.Driver.Captains.UpdateAsync(captain).ConfigureAwait(false);

                    await fixture.Coordinator.RecoverSessionsAsync().ConfigureAwait(false);

                    PlanningSession? recoveredSession = await testDb.Driver.PlanningSessions.ReadAsync(session.Id).ConfigureAwait(false);
                    AssertNotNull(recoveredSession);
                    AssertEqual(PlanningSessionStatusEnum.Active, recoveredSession!.Status);
                    AssertNull(recoveredSession.ProcessId);

                    Captain? recoveredCaptain = await testDb.Driver.Captains.ReadAsync(captain.Id).ConfigureAwait(false);
                    AssertNotNull(recoveredCaptain);
                    AssertEqual(CaptainStateEnum.Planning, recoveredCaptain!.State);
                    AssertEqual(session.DockId, recoveredCaptain.CurrentDockId);

                    List<PlanningSessionMessage> messages = await testDb.Driver.PlanningSessionMessages.EnumerateBySessionAsync(session.Id).ConfigureAwait(false);
                    AssertEqual(1, messages.Count);
                    AssertEqual("System", messages[0].Role);
                    AssertContains("interrupted during server recovery", messages[0].Content);
                }
            });
        }

        private sealed class PlanningObjectivesResult
        {
            public string TenantId { get; set; } = String.Empty;

            public string UserId { get; set; } = String.Empty;

            public PlanningSession Session { get; set; } = null!;

            public string MessageId { get; set; } = String.Empty;

            public Objective Primary { get; set; } = null!;

            public Objective Secondary { get; set; } = null!;
        }

        private static async Task<PlanningObjectivesResult> CreateTwoObjectivePlanningAsync(
            CoordinatorFixture fixture,
            TestDatabase testDb,
            string name)
        {
            CoordinatorFixture.TenantUserResult tenantUser = await fixture.CreateTenantUserAsync("Tenant " + name).ConfigureAwait(false);
            Pipeline pipeline = await fixture.CreatePipelineAsync(tenantUser.TenantId, "Pipeline " + name).ConfigureAwait(false);
            Vessel vessel = await fixture.CreateVesselAsync("vessel-" + name, tenantUser.TenantId, tenantUser.UserId).ConfigureAwait(false);
            Captain captain = await fixture.CreateCaptainAsync("planner-" + name, AgentRuntimeEnum.ClaudeCode, tenantUser.TenantId, tenantUser.UserId).ConfigureAwait(false);
            AuthContext auth = AuthContext.Authenticated(tenantUser.TenantId, tenantUser.UserId, false, true, "UnitTest");
            Objective primary = await fixture.Objectives.CreateAsync(auth, new ObjectiveUpsertRequest
            {
                Title = "Primary " + name,
                VesselIds = new List<string> { vessel.Id }
            }).ConfigureAwait(false);
            Objective secondary = await fixture.Objectives.CreateAsync(auth, new ObjectiveUpsertRequest
            {
                Title = "Secondary " + name,
                VesselIds = new List<string> { vessel.Id }
            }).ConfigureAwait(false);

            PlanningSession session = await fixture.Coordinator.CreateAsync(
                tenantUser.TenantId,
                tenantUser.UserId,
                captain,
                vessel,
                new PlanningSessionCreateRequest
                {
                    Title = "Plan " + name,
                    PipelineId = pipeline.Id,
                    ObjectiveId = primary.Id
                }).ConfigureAwait(false);
            await fixture.Objectives.LinkPlanningSessionAsync(auth, primary.Id, session.Id).ConfigureAwait(false);
            await fixture.Objectives.LinkPlanningSessionAsync(auth, secondary.Id, session.Id).ConfigureAwait(false);

            PlanningSessionMessage message = await testDb.Driver.PlanningSessionMessages.CreateAsync(new PlanningSessionMessage
            {
                PlanningSessionId = session.Id,
                Role = "Assistant",
                Sequence = 1,
                Content = "Implement both planned objectives."
            }).ConfigureAwait(false);

            return new PlanningObjectivesResult
            {
                TenantId = tenantUser.TenantId,
                UserId = tenantUser.UserId,
                Session = session,
                MessageId = message.Id,
                Primary = primary,
                Secondary = secondary
            };
        }

        private sealed class CoordinatorFixture : IDisposable
        {
            public sealed class TenantUserResult
            {
                public string TenantId { get; set; } = String.Empty;

                public string UserId { get; set; } = String.Empty;
            }

            public SqliteDatabaseDriver Database { get; }
            public ArmadaSettings Settings { get; }
            public StubGitService Git { get; }
            public PlanningSessionCoordinator Coordinator { get; }
            public ObjectiveService Objectives { get; }

            private readonly string _rootDirectory;
            private readonly LoggingModule _logging;

            public CoordinatorFixture(SqliteDatabaseDriver database, IReadOnlyList<string>? replayRecords = null)
            {
                Database = database;
                _logging = CreateLogging();
                _rootDirectory = Path.Combine(Path.GetTempPath(), "armada_planning_fixture_" + Guid.NewGuid().ToString("N"));

                Settings = new ArmadaSettings
                {
                    DataDirectory = _rootDirectory,
                    DatabasePath = Path.Combine(_rootDirectory, "armada.db"),
                    LogDirectory = Path.Combine(_rootDirectory, "logs"),
                    DocksDirectory = Path.Combine(_rootDirectory, "docks"),
                    ReposDirectory = Path.Combine(_rootDirectory, "repos")
                };
                Settings.InitializeDirectories();

                Git = new StubGitService();
                DockService docks = new DockService(_logging, Database, Settings, Git);
                AdmiralService admiral = CreateAdmiralService(_logging, Database, Settings, Git);
                AgentRuntimeFactory runtimeFactory = replayRecords != null
                    ? new ReplayRuntimeFactory(_logging, replayRecords)
                    : new AgentRuntimeFactory(_logging);
                Objectives = new ObjectiveService(Database, _logging);

                Coordinator = new PlanningSessionCoordinator(
                    _logging,
                    Database,
                    Settings,
                    docks,
                    admiral,
                    runtimeFactory,
                    (eventType, message, entityType, entityId, captainId, missionId, vesselId, voyageId) => Task.CompletedTask,
                    objectiveService: Objectives);
            }

            public async Task<TenantUserResult> CreateTenantUserAsync(string tenantName = "Planning Tenant")
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

            public async Task<Vessel> CreateVesselAsync(string name = "planning-vessel", string? tenantId = null, string? userId = null)
            {
                Fleet fleet = new Fleet("planning-fleet-" + name);
                fleet.TenantId = tenantId;
                fleet.UserId = userId;
                fleet = await Database.Fleets.CreateAsync(fleet).ConfigureAwait(false);

                Vessel vessel = new Vessel(name, "https://github.com/test/" + name + ".git")
                {
                    TenantId = tenantId,
                    UserId = userId,
                    FleetId = fleet.Id,
                    LocalPath = Path.Combine(Settings.ReposDirectory, name + ".git"),
                    WorkingDirectory = Path.Combine(Settings.ReposDirectory, name + ".git"),
                    DefaultBranch = "main",
                    ProjectContext = "Test project context"
                };

                return await Database.Vessels.CreateAsync(vessel).ConfigureAwait(false);
            }

            public async Task<Captain> CreateCaptainAsync(string name, AgentRuntimeEnum runtime = AgentRuntimeEnum.ClaudeCode, string? tenantId = null, string? userId = null)
            {
                Captain captain = new Captain(name, runtime)
                {
                    TenantId = tenantId,
                    UserId = userId
                };
                return await Database.Captains.CreateAsync(captain).ConfigureAwait(false);
            }

            public async Task<Playbook> CreatePlaybookAsync(string tenantId, string userId, string fileName)
            {
                Playbook playbook = new Playbook(fileName, "# " + fileName + "\n\nUse the repository planning conventions.")
                {
                    TenantId = tenantId,
                    UserId = userId,
                    Description = "Planning playbook for coordinator tests"
                };
                return await Database.Playbooks.CreateAsync(playbook).ConfigureAwait(false);
            }

            public async Task<Pipeline> CreatePipelineAsync(string tenantId, string name)
            {
                Pipeline pipeline = new Pipeline(name)
                {
                    TenantId = tenantId,
                    Stages = new List<PipelineStage>
                    {
                        new PipelineStage(1, "Architect"),
                        new PipelineStage(2, "Worker"),
                        new PipelineStage(3, "Judge")
                    }
                };
                return await Database.Pipelines.CreateAsync(pipeline).ConfigureAwait(false);
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

            private static AdmiralService CreateAdmiralService(LoggingModule logging, DatabaseDriver db, ArmadaSettings settings, StubGitService git)
            {
                IDockService dockService = new DockService(logging, db, settings, git);
                ICaptainService captainService = new CaptainService(logging, db, settings, git, dockService);
                IMissionService missionService = new MissionService(logging, db, settings, dockService, captainService, resourcePressureAdmission: TestResourcePressure.Unconstrained(settings));
                IVoyageService voyageService = new VoyageService(logging, db);
                return new AdmiralService(logging, db, settings, captainService, missionService, voyageService, dockService);
            }
        }
    }
}
