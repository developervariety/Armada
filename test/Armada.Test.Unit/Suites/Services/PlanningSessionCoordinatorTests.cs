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
    using SyslogLogging;

    public class PlanningSessionCoordinatorTests : TestSuite
    {
        public override string Name => "Planning Session Coordinator";

        protected override async Task RunTestsAsync()
        {
            await RunTest("CreateAsync reserves captain and dock", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                using (CoordinatorFixture fixture = new CoordinatorFixture(testDb.Driver))
                {
                    Vessel vessel = await fixture.CreateVesselAsync().ConfigureAwait(false);
                    Captain captain = await fixture.CreateCaptainAsync("planner-1").ConfigureAwait(false);

                    PlanningSession session = await fixture.Coordinator.CreateAsync(
                        null,
                        null,
                        captain,
                        vessel,
                        new PlanningSessionCreateRequest
                        {
                            Title = "Plan API hardening"
                        }).ConfigureAwait(false);

                    AssertEqual(PlanningSessionStatusEnum.Active, session.Status);
                    AssertNotNull(session.DockId);
                    AssertNotNull(session.BranchName);
                    AssertStartsWith(Constants.BranchPrefix + "planning/", session.BranchName!);

                    Captain? updatedCaptain = await testDb.Driver.Captains.ReadAsync(captain.Id).ConfigureAwait(false);
                    AssertNotNull(updatedCaptain);
                    AssertEqual(CaptainStateEnum.Planning, updatedCaptain!.State);
                    AssertEqual(session.DockId, updatedCaptain.CurrentDockId);

                    Dock? dock = await testDb.Driver.Docks.ReadAsync(session.DockId!).ConfigureAwait(false);
                    AssertNotNull(dock);
                    AssertEqual(vessel.Id, dock!.VesselId);
                    AssertEqual(session.BranchName, dock.BranchName);
                }
            });

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

            await RunTest("CreateAsync rejects unsupported custom runtime", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                using (CoordinatorFixture fixture = new CoordinatorFixture(testDb.Driver))
                {
                    Vessel vessel = await fixture.CreateVesselAsync().ConfigureAwait(false);
                    Captain captain = await fixture.CreateCaptainAsync("custom-planner", AgentRuntimeEnum.Custom).ConfigureAwait(false);

                    InvalidOperationException ex = await CaptureExceptionAsync<InvalidOperationException>(() =>
                        fixture.Coordinator.CreateAsync(
                            null,
                            null,
                            captain,
                            vessel,
                            new PlanningSessionCreateRequest())).ConfigureAwait(false);

                    AssertContains("built-in ClaudeCode, Codex, Gemini, Cursor, OpenCode, and Mux runtimes", ex.Message);
                }
            });

            await RunTest("CreateAsync failure resets captain and marks session failed", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                using (CoordinatorFixture fixture = new CoordinatorFixture(testDb.Driver))
                {
                    fixture.Git.ShouldThrowOnWorktree = true;
                    Vessel vessel = await fixture.CreateVesselAsync().ConfigureAwait(false);
                    Captain captain = await fixture.CreateCaptainAsync("planner-failure").ConfigureAwait(false);

                    InvalidOperationException ex = await CaptureExceptionAsync<InvalidOperationException>(() =>
                        fixture.Coordinator.CreateAsync(
                            null,
                            null,
                            captain,
                            vessel,
                            new PlanningSessionCreateRequest
                            {
                                Title = "Broken session"
                            })).ConfigureAwait(false);

                    AssertContains("Dock provisioning failed", ex.Message);

                    Captain? updatedCaptain = await testDb.Driver.Captains.ReadAsync(captain.Id).ConfigureAwait(false);
                    AssertNotNull(updatedCaptain);
                    AssertEqual(CaptainStateEnum.Idle, updatedCaptain!.State);
                    AssertNull(updatedCaptain.CurrentDockId);

                    List<PlanningSession> sessions = await testDb.Driver.PlanningSessions.EnumerateByCaptainAsync(captain.Id).ConfigureAwait(false);
                    AssertEqual(1, sessions.Count);
                    AssertEqual(PlanningSessionStatusEnum.Failed, sessions[0].Status);
                    AssertContains("Dock provisioning failed", sessions[0].FailureReason ?? String.Empty);
                }
            });

            await RunTest("CreateAsync rejects double-booking a captain already reserved for planning", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                using (CoordinatorFixture fixture = new CoordinatorFixture(testDb.Driver))
                {
                    Vessel vesselOne = await fixture.CreateVesselAsync("planning-vessel-a").ConfigureAwait(false);
                    Vessel vesselTwo = await fixture.CreateVesselAsync("planning-vessel-b").ConfigureAwait(false);
                    Captain captain = await fixture.CreateCaptainAsync("planner-double-booked").ConfigureAwait(false);

                    await fixture.Coordinator.CreateAsync(
                        null,
                        null,
                        captain,
                        vesselOne,
                        new PlanningSessionCreateRequest
                        {
                            Title = "First plan"
                        }).ConfigureAwait(false);

                    InvalidOperationException ex = await CaptureExceptionAsync<InvalidOperationException>(() =>
                        fixture.Coordinator.CreateAsync(
                            null,
                            null,
                            captain,
                            vesselTwo,
                            new PlanningSessionCreateRequest
                            {
                                Title = "Second plan"
                            })).ConfigureAwait(false);

                    AssertContains("is not idle", ex.Message);
                }
            });

            await RunTest("DispatchAsync creates voyage and mission lineage from assistant message", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                using (CoordinatorFixture fixture = new CoordinatorFixture(testDb.Driver))
                {
                    CoordinatorFixture.TenantUserResult tenantUser = await fixture.CreateTenantUserAsync().ConfigureAwait(false);
                    Playbook playbook = await fixture.CreatePlaybookAsync(tenantUser.TenantId, tenantUser.UserId, "planning-playbook.md").ConfigureAwait(false);
                    Pipeline pipeline = await fixture.CreatePipelineAsync(tenantUser.TenantId, "Full planning pipeline").ConfigureAwait(false);
                    Vessel vessel = await fixture.CreateVesselAsync("planning-vessel-dispatch", tenantUser.TenantId, tenantUser.UserId).ConfigureAwait(false);
                    Captain captain = await fixture.CreateCaptainAsync("planner-dispatch", AgentRuntimeEnum.ClaudeCode, tenantUser.TenantId, tenantUser.UserId).ConfigureAwait(false);
                    AuthContext auth = AuthContext.Authenticated(tenantUser.TenantId, tenantUser.UserId, false, true, "UnitTest");
                    Objective objective = await fixture.Objectives.CreateAsync(auth, new ObjectiveUpsertRequest
                    {
                        Title = "Prepared planning objective",
                        VesselIds = new List<string> { vessel.Id },
                        Preparation = new ObjectivePreparation
                        {
                            Claims = new List<ObjectivePreparationClaim>
                            {
                                new ObjectivePreparationClaim
                                {
                                    Id = "opc_planning",
                                    Kind = ObjectivePreparationClaimKindEnum.DispatchEntryPoint,
                                    Text = "Keep the planning objective linked through dispatch."
                                }
                            }
                        }
                    }).ConfigureAwait(false);

                    PlanningSession session = await fixture.Coordinator.CreateAsync(
                        tenantUser.TenantId,
                        tenantUser.UserId,
                        captain,
                        vessel,
                        new PlanningSessionCreateRequest
                        {
                            Title = "Repository plan",
                            PipelineId = pipeline.Id,
                            ObjectiveId = objective.Id,
                            SelectedPlaybooks = new List<SelectedPlaybook>
                            {
                                new SelectedPlaybook
                                {
                                    PlaybookId = playbook.Id,
                                    DeliveryMode = PlaybookDeliveryModeEnum.InlineFullContent
                                }
                            }
                        }).ConfigureAwait(false);

                    PlanningSessionMessage assistantMessage = await testDb.Driver.PlanningSessionMessages.CreateAsync(new PlanningSessionMessage
                    {
                        PlanningSessionId = session.Id,
                        Role = "Assistant",
                        Sequence = 1,
                        Content = "Implement the API hardening changes and add regression tests."
                    }).ConfigureAwait(false);

                    Voyage voyage = await fixture.Coordinator.DispatchAsync(
                        session,
                        new PlanningSessionDispatchRequest
                        {
                            MessageId = assistantMessage.Id,
                            Title = "API hardening dispatch"
                        }).ConfigureAwait(false);

                    AssertEqual(session.Id, voyage.SourcePlanningSessionId);
                    AssertEqual(assistantMessage.Id, voyage.SourcePlanningMessageId);
                    AssertEqual("API hardening dispatch", voyage.Title);
                    AssertEqual(objective.Id, session.ObjectiveId);

                    PlanningSession? persistedSession = await testDb.Driver.PlanningSessions.ReadAsync(session.Id).ConfigureAwait(false);
                    AssertEqual(objective.Id, persistedSession?.ObjectiveId,
                        "The planning session must persist its objective before a later dispatch.");

                    Voyage? persistedVoyage = await testDb.Driver.Voyages.ReadAsync(voyage.Id).ConfigureAwait(false);
                    AssertNotNull(persistedVoyage);
                    AssertEqual(session.Id, persistedVoyage!.SourcePlanningSessionId);
                    AssertEqual(assistantMessage.Id, persistedVoyage.SourcePlanningMessageId);

                    List<SelectedPlaybook> voyageSelections = await testDb.Driver.Playbooks.GetVoyageSelectionsAsync(voyage.Id).ConfigureAwait(false);
                    AssertEqual(1, voyageSelections.Count);
                    AssertEqual(playbook.Id, voyageSelections[0].PlaybookId);

                    List<Mission> missions = await testDb.Driver.Missions.EnumerateByVoyageAsync(voyage.Id).ConfigureAwait(false);
                    AssertEqual(3, missions.Count);
                    AssertTrue(missions.Exists(m => m.Persona == "Architect"), "Pipeline dispatch should include the Architect stage");
                    AssertTrue(missions.Exists(m => m.Persona == "Worker"), "Pipeline dispatch should include the Worker stage");
                    AssertTrue(missions.Exists(m => m.Persona == "Judge"), "Pipeline dispatch should include the Judge stage");

                    Mission architectMission = missions.Find(m => m.Persona == "Architect")
                        ?? throw new Exception("Expected architect mission to exist");
                    AssertContains("Implement the API hardening changes and add regression tests.", architectMission.Description ?? String.Empty);
                    AssertContains("Keep the planning objective linked through dispatch.", architectMission.Description ?? String.Empty);

                    Objective? linkedObjective = await fixture.Objectives.ReadAsync(auth, objective.Id).ConfigureAwait(false);
                    AssertTrue(linkedObjective!.VoyageIds.Contains(voyage.Id),
                        "Planning dispatch must preserve objective voyage lineage.");

                    List<MissionPlaybookSnapshot> snapshots = await testDb.Driver.Playbooks.GetMissionSnapshotsAsync(architectMission.Id).ConfigureAwait(false);
                    AssertEqual(1, snapshots.Count);
                    AssertEqual(playbook.Id, snapshots[0].PlaybookId);
                    AssertContains("planning-playbook.md", snapshots[0].FileName ?? String.Empty);
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

            await RunTest("StopAsync releases captain and dock", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                using (CoordinatorFixture fixture = new CoordinatorFixture(testDb.Driver))
                {
                    Vessel vessel = await fixture.CreateVesselAsync().ConfigureAwait(false);
                    Captain captain = await fixture.CreateCaptainAsync("planner-stop").ConfigureAwait(false);

                    PlanningSession session = await fixture.Coordinator.CreateAsync(
                        null,
                        null,
                        captain,
                        vessel,
                        new PlanningSessionCreateRequest
                        {
                            Title = "Stop me"
                        }).ConfigureAwait(false);

                    PlanningSession stopped = await fixture.Coordinator.StopAsync(session).ConfigureAwait(false);

                    AssertEqual(PlanningSessionStatusEnum.Stopped, stopped.Status);
                    AssertNull(stopped.ProcessId);
                    AssertNotNull(stopped.CompletedUtc);

                    Captain? updatedCaptain = await testDb.Driver.Captains.ReadAsync(captain.Id).ConfigureAwait(false);
                    AssertNotNull(updatedCaptain);
                    AssertEqual(CaptainStateEnum.Idle, updatedCaptain!.State);
                    AssertNull(updatedCaptain.CurrentDockId);
                    AssertNull(updatedCaptain.ProcessId);

                    Dock? dock = await testDb.Driver.Docks.ReadAsync(session.DockId!).ConfigureAwait(false);
                    AssertNotNull(dock);
                    AssertFalse(dock!.Active);
                    AssertNull(dock.CaptainId);
                }
            });

            await RunTest("DeleteAsync removes session transcript and releases resources", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                using (CoordinatorFixture fixture = new CoordinatorFixture(testDb.Driver))
                {
                    Vessel vessel = await fixture.CreateVesselAsync("planning-vessel-delete").ConfigureAwait(false);
                    Captain captain = await fixture.CreateCaptainAsync("planner-delete").ConfigureAwait(false);

                    PlanningSession session = await fixture.Coordinator.CreateAsync(
                        null,
                        null,
                        captain,
                        vessel,
                        new PlanningSessionCreateRequest
                        {
                            Title = "Delete me"
                        }).ConfigureAwait(false);

                    await testDb.Driver.PlanningSessionMessages.CreateAsync(new PlanningSessionMessage
                    {
                        PlanningSessionId = session.Id,
                        Role = "Assistant",
                        Sequence = 1,
                        Content = "Planning transcript content"
                    }).ConfigureAwait(false);

                    await fixture.Coordinator.DeleteAsync(session).ConfigureAwait(false);

                    AssertNull(await testDb.Driver.PlanningSessions.ReadAsync(session.Id).ConfigureAwait(false));
                    AssertEqual(0, (await testDb.Driver.PlanningSessionMessages.EnumerateBySessionAsync(session.Id).ConfigureAwait(false)).Count);

                    Captain? updatedCaptain = await testDb.Driver.Captains.ReadAsync(captain.Id).ConfigureAwait(false);
                    AssertNotNull(updatedCaptain);
                    AssertEqual(CaptainStateEnum.Idle, updatedCaptain!.State);
                    AssertNull(updatedCaptain.CurrentDockId);

                    Dock? dock = await testDb.Driver.Docks.ReadAsync(session.DockId!).ConfigureAwait(false);
                    AssertNotNull(dock);
                    AssertFalse(dock!.Active);
                }
            });

            await RunTest("MaintainSessionsAsync stops inactive planning sessions", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                using (CoordinatorFixture fixture = new CoordinatorFixture(testDb.Driver))
                {
                    fixture.Settings.PlanningSessionInactivityTimeoutMinutes = 1;
                    Vessel vessel = await fixture.CreateVesselAsync("planning-vessel-maintain-stop").ConfigureAwait(false);
                    Captain captain = await fixture.CreateCaptainAsync("planner-maintain-stop").ConfigureAwait(false);

                    PlanningSession session = await fixture.Coordinator.CreateAsync(
                        null,
                        null,
                        captain,
                        vessel,
                        new PlanningSessionCreateRequest
                        {
                            Title = "Inactive session"
                        }).ConfigureAwait(false);

                    using (SqliteConnection conn = new SqliteConnection(testDb.ConnectionString))
                    {
                        await conn.OpenAsync().ConfigureAwait(false);
                        using (SqliteCommand cmd = conn.CreateCommand())
                        {
                            cmd.CommandText = "UPDATE planning_sessions SET last_update_utc = @lastUpdateUtc WHERE id = @id;";
                            cmd.Parameters.AddWithValue("@id", session.Id);
                            cmd.Parameters.AddWithValue("@lastUpdateUtc", DateTime.UtcNow.AddMinutes(-10).ToString("O"));
                            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                        }
                    }

                    await fixture.Coordinator.MaintainSessionsAsync().ConfigureAwait(false);

                    PlanningSession? updatedSession = await testDb.Driver.PlanningSessions.ReadAsync(session.Id).ConfigureAwait(false);
                    AssertNotNull(updatedSession);
                    AssertEqual(PlanningSessionStatusEnum.Stopped, updatedSession!.Status);
                    AssertNotNull(updatedSession.CompletedUtc);

                    Captain? updatedCaptain = await testDb.Driver.Captains.ReadAsync(captain.Id).ConfigureAwait(false);
                    AssertNotNull(updatedCaptain);
                    AssertEqual(CaptainStateEnum.Idle, updatedCaptain!.State);
                }
            });

            await RunTest("MaintainSessionsAsync stops abandoned planning sessions when inactivity timeout is disabled", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                using (CoordinatorFixture fixture = new CoordinatorFixture(testDb.Driver))
                {
                    fixture.Settings.PlanningSessionInactivityTimeoutMinutes = 0;
                    fixture.Settings.PlanningSessionAbandonmentTimeoutMinutes = 1;
                    Vessel vessel = await fixture.CreateVesselAsync("planning-vessel-maintain-abandonment").ConfigureAwait(false);
                    Captain captain = await fixture.CreateCaptainAsync("planner-maintain-abandonment").ConfigureAwait(false);

                    PlanningSession session = await fixture.Coordinator.CreateAsync(
                        null,
                        null,
                        captain,
                        vessel,
                        new PlanningSessionCreateRequest
                        {
                            Title = "Abandoned session"
                        }).ConfigureAwait(false);

                    using (SqliteConnection conn = new SqliteConnection(testDb.ConnectionString))
                    {
                        await conn.OpenAsync().ConfigureAwait(false);
                        using (SqliteCommand cmd = conn.CreateCommand())
                        {
                            cmd.CommandText = "UPDATE planning_sessions SET last_update_utc = @lastUpdateUtc WHERE id = @id;";
                            cmd.Parameters.AddWithValue("@id", session.Id);
                            cmd.Parameters.AddWithValue("@lastUpdateUtc", DateTime.UtcNow.AddMinutes(-10).ToString("O"));
                            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                        }
                    }

                    await fixture.Coordinator.MaintainSessionsAsync().ConfigureAwait(false);

                    PlanningSession? updatedSession = await testDb.Driver.PlanningSessions.ReadAsync(session.Id).ConfigureAwait(false);
                    AssertNotNull(updatedSession);
                    AssertEqual(PlanningSessionStatusEnum.Stopped, updatedSession!.Status);

                    Captain? updatedCaptain = await testDb.Driver.Captains.ReadAsync(captain.Id).ConfigureAwait(false);
                    AssertNotNull(updatedCaptain);
                    AssertEqual(CaptainStateEnum.Idle, updatedCaptain!.State);
                }
            });

            await RunTest("MaintainSessionsAsync deletes retained stopped sessions", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                using (CoordinatorFixture fixture = new CoordinatorFixture(testDb.Driver))
                {
                    fixture.Settings.PlanningSessionRetentionDays = 1;
                    Vessel vessel = await fixture.CreateVesselAsync("planning-vessel-retention").ConfigureAwait(false);
                    Captain captain = await fixture.CreateCaptainAsync("planner-retention").ConfigureAwait(false);

                    PlanningSession session = await fixture.Coordinator.CreateAsync(
                        null,
                        null,
                        captain,
                        vessel,
                        new PlanningSessionCreateRequest
                        {
                            Title = "Retention session"
                        }).ConfigureAwait(false);

                    await testDb.Driver.PlanningSessionMessages.CreateAsync(new PlanningSessionMessage
                    {
                        PlanningSessionId = session.Id,
                        Role = "Assistant",
                        Sequence = 1,
                        Content = "Retention transcript"
                    }).ConfigureAwait(false);

                    PlanningSession stopped = await fixture.Coordinator.StopAsync(session).ConfigureAwait(false);
                    stopped.CompletedUtc = DateTime.UtcNow.AddDays(-5);
                    stopped.LastUpdateUtc = DateTime.UtcNow.AddDays(-5);
                    await testDb.Driver.PlanningSessions.UpdateAsync(stopped).ConfigureAwait(false);

                    await fixture.Coordinator.MaintainSessionsAsync().ConfigureAwait(false);

                    AssertNull(await testDb.Driver.PlanningSessions.ReadAsync(session.Id).ConfigureAwait(false));
                    AssertEqual(0, (await testDb.Driver.PlanningSessionMessages.EnumerateBySessionAsync(session.Id).ConfigureAwait(false)).Count);
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
                    session.ProcessId = 999999;
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

            await RunTest("RecoverSessionsAsync stops abandoned active sessions and releases captains", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                using (CoordinatorFixture fixture = new CoordinatorFixture(testDb.Driver))
                {
                    fixture.Settings.PlanningSessionInactivityTimeoutMinutes = 0;
                    fixture.Settings.PlanningSessionAbandonmentTimeoutMinutes = 1;
                    Vessel vessel = await fixture.CreateVesselAsync("planning-vessel-recover-abandonment").ConfigureAwait(false);
                    Captain captain = await fixture.CreateCaptainAsync("planner-recover-abandonment").ConfigureAwait(false);

                    PlanningSession session = await fixture.Coordinator.CreateAsync(
                        null,
                        null,
                        captain,
                        vessel,
                        new PlanningSessionCreateRequest
                        {
                            Title = "Recover abandoned session"
                        }).ConfigureAwait(false);

                    using (SqliteConnection conn = new SqliteConnection(testDb.ConnectionString))
                    {
                        await conn.OpenAsync().ConfigureAwait(false);
                        using (SqliteCommand cmd = conn.CreateCommand())
                        {
                            cmd.CommandText = "UPDATE planning_sessions SET last_update_utc = @lastUpdateUtc WHERE id = @id;";
                            cmd.Parameters.AddWithValue("@id", session.Id);
                            cmd.Parameters.AddWithValue("@lastUpdateUtc", DateTime.UtcNow.AddMinutes(-10).ToString("O"));
                            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                        }
                    }

                    await fixture.Coordinator.RecoverSessionsAsync().ConfigureAwait(false);

                    PlanningSession? recoveredSession = await testDb.Driver.PlanningSessions.ReadAsync(session.Id).ConfigureAwait(false);
                    AssertNotNull(recoveredSession);
                    AssertEqual(PlanningSessionStatusEnum.Stopped, recoveredSession!.Status);
                    AssertNotNull(recoveredSession.CompletedUtc);

                    Captain? recoveredCaptain = await testDb.Driver.Captains.ReadAsync(captain.Id).ConfigureAwait(false);
                    AssertNotNull(recoveredCaptain);
                    AssertEqual(CaptainStateEnum.Idle, recoveredCaptain!.State);
                    AssertNull(recoveredCaptain.CurrentDockId);
                }
            });

            await RunTest("RecoverSessionsAsync completes stopping session cleanup", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                using (CoordinatorFixture fixture = new CoordinatorFixture(testDb.Driver))
                {
                    Vessel vessel = await fixture.CreateVesselAsync("planning-vessel-recover-stop").ConfigureAwait(false);
                    Captain captain = await fixture.CreateCaptainAsync("planner-recover-stop").ConfigureAwait(false);

                    PlanningSession session = await fixture.Coordinator.CreateAsync(
                        null,
                        null,
                        captain,
                        vessel,
                        new PlanningSessionCreateRequest
                        {
                            Title = "Recover stop"
                        }).ConfigureAwait(false);

                    session.Status = PlanningSessionStatusEnum.Stopping;
                    session.ProcessId = null;
                    await testDb.Driver.PlanningSessions.UpdateAsync(session).ConfigureAwait(false);

                    captain.State = CaptainStateEnum.Planning;
                    captain.CurrentDockId = session.DockId;
                    captain.ProcessId = null;
                    await testDb.Driver.Captains.UpdateAsync(captain).ConfigureAwait(false);

                    await fixture.Coordinator.RecoverSessionsAsync().ConfigureAwait(false);

                    PlanningSession? recoveredSession = await testDb.Driver.PlanningSessions.ReadAsync(session.Id).ConfigureAwait(false);
                    AssertNotNull(recoveredSession);
                    AssertEqual(PlanningSessionStatusEnum.Stopped, recoveredSession!.Status);
                    AssertNotNull(recoveredSession.CompletedUtc);

                    Captain? recoveredCaptain = await testDb.Driver.Captains.ReadAsync(captain.Id).ConfigureAwait(false);
                    AssertNotNull(recoveredCaptain);
                    AssertEqual(CaptainStateEnum.Idle, recoveredCaptain!.State);
                    AssertNull(recoveredCaptain.CurrentDockId);

                    Dock? recoveredDock = await testDb.Driver.Docks.ReadAsync(session.DockId!).ConfigureAwait(false);
                    AssertNotNull(recoveredDock);
                    AssertFalse(recoveredDock!.Active);
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

        private static async Task<TException> CaptureExceptionAsync<TException>(Func<Task> action) where TException : Exception
        {
            try
            {
                await action().ConfigureAwait(false);
            }
            catch (TException ex)
            {
                return ex;
            }

            throw new Exception("Assertion failed: expected " + typeof(TException).Name + " but no exception was thrown");
        }
    }
}
