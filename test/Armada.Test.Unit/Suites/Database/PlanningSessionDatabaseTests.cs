namespace Armada.Test.Unit.Suites.Database
{
    using Armada.Core.Database.Sqlite;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    public class PlanningSessionDatabaseTests : TestSuite
    {
        public override string Name => "Planning Session Database";

        protected override async Task RunTestsAsync()
        {
            await RunTest("CreateAsync and ReadAsync persist session state", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
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
            });

            await RunTest("EnumerateByCaptainAsync and UpdateAsync track status changes", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
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
            });

            await RunTest("Message create enumerate and cascade delete work", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
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
            });

            await RunTest("Voyage planning lineage persists through update", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
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
            });
        }
    }
}
