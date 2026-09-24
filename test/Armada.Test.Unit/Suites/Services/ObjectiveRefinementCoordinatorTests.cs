namespace Armada.Test.Unit.Suites.Services
{
    using Armada.Core.Database;
    using Armada.Core.Database.Sqlite;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Runtimes;
    using Armada.Server;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    public class ObjectiveRefinementCoordinatorTests : TestSuite
    {
        public override string Name => "Objective Refinement Coordinator";

        protected override async Task RunTestsAsync()
        {
            await RunTest("Runtime JSON carries structured dispatch preparation", () =>
            {
                string json = "{\"summary\":\"Prepared\",\"preparation\":{\"requiredForDispatch\":true,\"requiredClaimKinds\":[\"SourcePath\",\"ResponseRule\"],\"requiredSiblingInputs\":[{\"vesselRef\":\"ReferenceSource\",\"relativePath\":\"../ReferenceSource\",\"requiredArtifactPaths\":null}],\"source\":{\"vesselId\":\"source-vessel\",\"ref\":\"main\",\"resolvedCommit\":\"0123456789abcdef0123456789abcdef01234567\"},\"target\":null,\"claims\":[{\"id\":\"source-path\",\"kind\":\"SourcePath\",\"text\":\"Read source.cs\",\"evidenceLinks\":null,\"dependsOn\":\"Source\",\"state\":\"Verified\",\"verifiedUtc\":\"2026-09-12T00:00:00Z\"}]}}";
                bool parsed = ObjectiveRefinementCoordinator.TryParseSummaryResponse(json, out ObjectiveRefinementSummaryResponse? summary);
                AssertTrue(parsed);
                AssertNotNull(summary?.Preparation);
                AssertTrue(summary!.Preparation!.RequiredForDispatch);
                AssertEqual(2, summary.Preparation.RequiredClaimKinds.Count);
                AssertEqual(ObjectivePreparationClaimKindEnum.ResponseRule, summary.Preparation.RequiredClaimKinds[1]);
                AssertEqual(1, summary.Preparation.Claims.Count);
                AssertEqual("ReferenceSource", summary.Preparation.RequiredSiblingInputs[0].VesselRef);
                AssertNotNull(summary.Preparation.RequiredSiblingInputs[0].RequiredArtifactPaths);
                AssertNotNull(summary.Preparation.Claims[0].EvidenceLinks);
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("Runtime JSON without preparation preserves the null apply signal", () =>
            {
                bool parsed = ObjectiveRefinementCoordinator.TryParseSummaryResponse(
                    "{\"summary\":\"No repository preparation change\"}", out ObjectiveRefinementSummaryResponse? summary);
                AssertTrue(parsed);
                AssertNull(summary!.Preparation);
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("ApplyAsync updates objective fields and selects the source refinement message", async () =>
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
            }).ConfigureAwait(false);
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
            public ObjectiveRefinementCoordinator Coordinator { get; }
            public ObjectiveService Objectives { get; }

            private readonly string _rootDirectory;
            private readonly LoggingModule _logging;

            public CoordinatorFixture(SqliteDatabaseDriver database)
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
                    new AgentRuntimeFactory(_logging),
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

        private static async Task<Objective> RequireObjectiveAsync(DatabaseDriver database, string objectiveId)
        {
            Objective? objective = await database.Objectives.ReadAsync(objectiveId).ConfigureAwait(false);
            return objective ?? throw new Exception("Expected objective " + objectiveId);
        }
    }
}
