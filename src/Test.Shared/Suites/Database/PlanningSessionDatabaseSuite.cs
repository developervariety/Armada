namespace Test.Shared.Suites.Database
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Database.Sqlite;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// Descriptors for planning-session database operations: create/read round-tripping of session
    /// state (status, pipeline, branch, selected playbooks), enumeration by captain and by status
    /// alongside update-driven status transitions, transcript-message create/enumerate with cascade
    /// delete of messages when the parent session is removed, and voyage planning-lineage persistence
    /// through update. Positive cases cover the persistence and query happy paths; negative audit
    /// cases cover cross-tenant read fencing and not-found reads returning null.
    /// </summary>
    public sealed class PlanningSessionDatabaseSuite : IArmadaTestSuite
    {
        #region Private-Members

        private const string SuiteId = "Database.PlanningSessionDatabase";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the Planning Session Database suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(CaseAsync("create_and_read_persist_session_state", "CreateAsync and ReadAsync persist session state", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    PlanningSession session = new PlanningSession
                    {
                        CaptainId = "cpt_test",
                        VesselId = "vsl_test",
                        Title = "Planning Session",
                        Status = PlanningSessionStatusEnum.Active,
                        PipelineId = "ppl_test",
                        BranchName = "armada/planning",
                        SelectedPlaybooks = new List<SelectedPlaybook>
                        {
                            new SelectedPlaybook
                            {
                                PlaybookId = "plb_123",
                                DeliveryMode = PlaybookDeliveryModeEnum.InstructionWithReference
                            }
                        }
                    };

                    await db.PlanningSessions.CreateAsync(session);

                    PlanningSession? read = await db.PlanningSessions.ReadAsync(session.Id);
                    AssertNotNull(read);
                    AssertEqual(session.Title, read!.Title);
                    AssertEqual(PlanningSessionStatusEnum.Active, read.Status);
                    AssertEqual("ppl_test", read.PipelineId);
                    AssertEqual("armada/planning", read.BranchName);
                    AssertEqual(1, read.SelectedPlaybooks.Count);
                    AssertEqual("plb_123", read.SelectedPlaybooks[0].PlaybookId);
                }
            }));

            cases.Add(CaseAsync("enumerate_by_captain_and_update_track_status_changes", "EnumerateByCaptainAsync and UpdateAsync track status changes", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    PlanningSession session = new PlanningSession
                    {
                        CaptainId = "cpt_shared",
                        VesselId = "vsl_test",
                        Title = "Captain Session"
                    };

                    await db.PlanningSessions.CreateAsync(session);

                    List<PlanningSession> byCaptain = await db.PlanningSessions.EnumerateByCaptainAsync("cpt_shared");
                    AssertEqual(1, byCaptain.Count);

                    session.Status = PlanningSessionStatusEnum.Stopped;
                    session.CompletedUtc = DateTime.UtcNow;
                    await db.PlanningSessions.UpdateAsync(session);

                    List<PlanningSession> stopped = await db.PlanningSessions.EnumerateByStatusAsync(PlanningSessionStatusEnum.Stopped);
                    AssertEqual(1, stopped.Count);
                    AssertEqual(session.Id, stopped[0].Id);
                }
            }));

            cases.Add(CaseAsync("message_create_enumerate_and_cascade_delete_work", "Message create enumerate and cascade delete work", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    PlanningSession session = new PlanningSession
                    {
                        CaptainId = "cpt_test",
                        VesselId = "vsl_test",
                        Title = "Transcript Session"
                    };
                    await db.PlanningSessions.CreateAsync(session);

                    await db.PlanningSessionMessages.CreateAsync(new PlanningSessionMessage
                    {
                        PlanningSessionId = session.Id,
                        Role = "User",
                        Sequence = 1,
                        Content = "Plan this"
                    });
                    await db.PlanningSessionMessages.CreateAsync(new PlanningSessionMessage
                    {
                        PlanningSessionId = session.Id,
                        Role = "Assistant",
                        Sequence = 2,
                        Content = "Dispatch draft"
                    });

                    List<PlanningSessionMessage> messages = await db.PlanningSessionMessages.EnumerateBySessionAsync(session.Id);
                    AssertEqual(2, messages.Count);
                    AssertEqual("User", messages[0].Role);
                    AssertEqual("Assistant", messages[1].Role);

                    await db.PlanningSessions.DeleteAsync(session.Id);

                    AssertNull(await db.PlanningSessions.ReadAsync(session.Id));
                    AssertEqual(0, (await db.PlanningSessionMessages.EnumerateBySessionAsync(session.Id)).Count);
                }
            }));

            cases.Add(CaseAsync("voyage_planning_lineage_persists_through_update", "Voyage planning lineage persists through update", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    Voyage voyage = new Voyage("From Planning");
                    await db.Voyages.CreateAsync(voyage);

                    voyage.SourcePlanningSessionId = "psn_test";
                    voyage.SourcePlanningMessageId = "psm_test";
                    await db.Voyages.UpdateAsync(voyage);

                    Voyage? read = await db.Voyages.ReadAsync(voyage.Id);
                    AssertNotNull(read);
                    AssertEqual("psn_test", read!.SourcePlanningSessionId);
                    AssertEqual("psm_test", read.SourcePlanningMessageId);
                }
            }));

            cases.Add(CaseAsync("read_non_existent_returns_null_audit", "ReadAsync NonExistent ReturnsNull", TestTags.Negative, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;
                    PlanningSession? read = await db.PlanningSessions.ReadAsync("psn_nonexistent");
                    AssertNull(read);
                }
            }));

            cases.Add(CaseAsync("cross_tenant_read_returns_null_audit", "ReadAsync CrossTenant ReturnsNull", TestTags.Negative, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    DatabaseDriver db = testDb.Driver;

                    // planning_sessions.tenant_id is a foreign key to tenants(id); the owning
                    // tenant must exist for the row to insert once FK enforcement is on.
                    TenantMetadata owner = new TenantMetadata("Owner");
                    await db.Tenants.CreateAsync(owner);

                    PlanningSession session = new PlanningSession
                    {
                        TenantId = owner.Id,
                        CaptainId = "cpt_test",
                        VesselId = "vsl_test",
                        Title = "Tenant Fenced Session"
                    };
                    await db.PlanningSessions.CreateAsync(session);

                    PlanningSession? sameTenant = await db.PlanningSessions.ReadAsync(owner.Id, session.Id);
                    AssertNotNull(sameTenant);

                    PlanningSession? otherTenant = await db.PlanningSessions.ReadAsync("ten_intruder", session.Id);
                    AssertNull(otherTenant);
                }
            }));

            return new TestSuiteDescriptor(
                suiteId: SuiteId,
                displayName: "Planning Session Database",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        private static TestCaseDescriptor CaseAsync(string caseId, string displayName, string tag, Func<Task> body)
        {
            return new TestCaseDescriptor(
                suiteId: SuiteId,
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) => body(),
                tags: new List<string> { tag });
        }

        #endregion
    }
}
