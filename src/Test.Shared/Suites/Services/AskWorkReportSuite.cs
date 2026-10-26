namespace Test.Shared.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Server;
    using SyslogLogging;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>Checks owner-scoped work reports against current typed delivery evidence.</summary>
    public sealed class AskWorkReportSuite : IArmadaTestSuite
    {
        private const string SuiteId = "Services.AskWorkReport";

        /// <summary>Builds the work report contract cases.</summary>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>
            {
                CaseAsync("delivery_requires_current_landed_fact", "Delivery requires a current typed landing fact", DeliveryRequiresCurrentLandedFactAsync),
                CaseAsync("rescue_lineage_stays_distinct", "Rescue work stays distinct from its parent mission", RescueLineageStaysDistinctAsync),
                CaseAsync("voyage_and_check_summary_are_bounded", "Voyage reports defer delivery to mission reports and withhold check output", VoyageAndCheckSummaryAreBoundedAsync)
            };
            return new TestSuiteDescriptor(SuiteId, "Ask work reports", cases);
        }

        private static async Task DeliveryRequiresCurrentLandedFactAsync()
        {
            using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
            {
                DatabaseDriver database = testDb.Driver;
                Fixture fixture = await CreateFixtureAsync(database).ConfigureAwait(false);
                DateTime start = DateTime.UtcNow.AddMinutes(-5);
                Mission workProduced = await CreateMissionAsync(database, fixture, "Produced", MissionStatusEnum.WorkProduced, "commit-produced").ConfigureAwait(false);
                Mission landingFailed = await CreateMissionAsync(database, fixture, "Landing failed", MissionStatusEnum.LandingFailed, "commit-failed").ConfigureAwait(false);
                Mission completeWithoutLanding = await CreateMissionAsync(database, fixture, "Complete without landing fact", MissionStatusEnum.Complete, "commit-unconfirmed").ConfigureAwait(false);
                Mission landedAfterRetry = await CreateMissionAsync(database, fixture, "Landed after retry", MissionStatusEnum.Complete, "commit-landed").ConfigureAwait(false);
                Mission restartedAfterLanding = await CreateMissionAsync(database, fixture, "Restarted after landing", MissionStatusEnum.Complete, "commit-restarted").ConfigureAwait(false);

                await AddFactAsync(database, fixture, workProduced, MissionAttemptFactTypeEnum.AttemptStarted, start).ConfigureAwait(false);
                await AddFactAsync(database, fixture, landingFailed, MissionAttemptFactTypeEnum.AttemptStarted, start).ConfigureAwait(false);
                await AddFactAsync(database, fixture, landingFailed, MissionAttemptFactTypeEnum.Failed, start.AddSeconds(1)).ConfigureAwait(false);
                await AddFactAsync(database, fixture, completeWithoutLanding, MissionAttemptFactTypeEnum.AttemptStarted, start).ConfigureAwait(false);
                await AddFactAsync(database, fixture, landedAfterRetry, MissionAttemptFactTypeEnum.AttemptStarted, start).ConfigureAwait(false);
                await AddFactAsync(database, fixture, landedAfterRetry, MissionAttemptFactTypeEnum.Retried, start.AddSeconds(1)).ConfigureAwait(false);
                await AddFactAsync(database, fixture, landedAfterRetry, MissionAttemptFactTypeEnum.Landed, start.AddSeconds(2)).ConfigureAwait(false);
                await AddFactAsync(database, fixture, restartedAfterLanding, MissionAttemptFactTypeEnum.AttemptStarted, start).ConfigureAwait(false);
                await AddFactAsync(database, fixture, restartedAfterLanding, MissionAttemptFactTypeEnum.Landed, start.AddSeconds(1)).ConfigureAwait(false);
                await AddFactAsync(database, fixture, restartedAfterLanding, MissionAttemptFactTypeEnum.Restarted, start.AddSeconds(2)).ConfigureAwait(false);

                List<AskWorkReport> reports = await ReadReportsAsync(database, fixture,
                    MissionReference(workProduced), MissionReference(landingFailed), MissionReference(completeWithoutLanding),
                    MissionReference(landedAfterRetry), MissionReference(restartedAfterLanding)).ConfigureAwait(false);
                AskWorkReport produced = Find(reports, workProduced.Id);
                AssertTrue(produced.ProducedWork, "WorkProduced with a recorded commit establishes produced work");
                AssertFalse(produced.Delivered, "WorkProduced does not establish delivery");
                AskWorkReport failed = Find(reports, landingFailed.Id);
                AssertTrue(failed.ProducedWork, "LandingFailed with a recorded commit establishes produced work");
                AssertFalse(failed.Delivered, "LandingFailed does not establish delivery");
                AssertFalse(Find(reports, completeWithoutLanding.Id).Delivered, "Complete without a typed Landed fact is unconfirmed");
                AssertTrue(Find(reports, landedAfterRetry.Id).Delivered, "a Landed fact after the latest retry establishes delivery");
                AssertFalse(Find(reports, restartedAfterLanding.Id).Delivered, "a later restart invalidates the earlier landing fact");
            }
        }

        private static async Task RescueLineageStaysDistinctAsync()
        {
            using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
            {
                DatabaseDriver database = testDb.Driver;
                Fixture fixture = await CreateFixtureAsync(database).ConfigureAwait(false);
                DateTime start = DateTime.UtcNow.AddMinutes(-3);
                Mission parent = await CreateMissionAsync(database, fixture, "Parent mission", MissionStatusEnum.LandingFailed, "parent-commit").ConfigureAwait(false);
                Mission child = await CreateMissionAsync(database, fixture, "Rescue mission", MissionStatusEnum.Complete, "rescue-commit", parent.Id).ConfigureAwait(false);
                await AddFactAsync(database, fixture, parent, MissionAttemptFactTypeEnum.AttemptStarted, start).ConfigureAwait(false);
                await AddFactAsync(database, fixture, parent, MissionAttemptFactTypeEnum.Failed, start.AddSeconds(1)).ConfigureAwait(false);
                await AddFactAsync(database, fixture, child, MissionAttemptFactTypeEnum.AttemptStarted, start.AddSeconds(2), true, parent.Id).ConfigureAwait(false);
                await AddFactAsync(database, fixture, child, MissionAttemptFactTypeEnum.Landed, start.AddSeconds(3), true, parent.Id).ConfigureAwait(false);

                List<AskWorkReport> reports = await ReadReportsAsync(database, fixture, MissionReference(parent), MissionReference(child)).ConfigureAwait(false);
                AskWorkReport parentReport = Find(reports, parent.Id);
                AskWorkReport childReport = Find(reports, child.Id);
                AssertEqual(child.Id, parentReport.RescueMissionId, "the parent names its distinct rescue child");
                AssertEqual(parent.Id, childReport.ParentMissionId, "the rescue card names its parent");
                AssertFalse(parentReport.Delivered, "a child's landing does not promote its parent's delivery");
                AssertTrue(childReport.Delivered, "the rescue's own typed landing establishes its delivery");
                AssertFalse(String.Equals(parentReport.Id, childReport.Id, StringComparison.Ordinal), "parent and rescue remain separate work cards");
            }
        }

        private static async Task VoyageAndCheckSummaryAreBoundedAsync()
        {
            using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
            {
                DatabaseDriver database = testDb.Driver;
                Fixture fixture = await CreateFixtureAsync(database).ConfigureAwait(false);
                Voyage voyage = await database.Voyages.CreateAsync(new Voyage
                {
                    TenantId = fixture.Thread.TenantId,
                    UserId = fixture.Thread.UserId,
                    Title = "Voyage with observed mission delivery",
                    Status = VoyageStatusEnum.Complete
                }).ConfigureAwait(false);
                Mission mission = await CreateMissionAsync(database, fixture, "Voyage member", MissionStatusEnum.WorkProduced, "voyage-commit", voyageId: voyage.Id).ConfigureAwait(false);
                string outputMarker = "private-check-output-" + Guid.NewGuid().ToString("N");
                await database.CheckRuns.CreateAsync(new CheckRun
                {
                    TenantId = fixture.Thread.TenantId,
                    UserId = fixture.Thread.UserId,
                    VoyageId = voyage.Id,
                    MissionId = mission.Id,
                    Label = "Unit checks",
                    Command = "run checks",
                    Status = CheckRunStatusEnum.Failed,
                    Summary = "Unit checks failed",
                    Output = outputMarker,
                    CreatedUtc = DateTime.UtcNow.AddMinutes(-1)
                }).ConfigureAwait(false);

                List<AskWorkReport> reports = await ReadReportsAsync(database, fixture, VoyageReference(voyage)).ConfigureAwait(false);
                AskWorkReport voyageReport = Find(reports, voyage.Id);
                AssertEqual("See mission delivery", voyageReport.DeliveryState, "voyage delivery defers to its mission reports");
                AssertFalse(voyageReport.Delivered, "a voyage does not claim aggregate delivery");
                AskWorkReport missionReport = Find(reports, mission.Id);
                AssertTrue(missionReport.ProducedWork, "the report includes the voyage member mission");
                AssertFalse(missionReport.Delivered, "the mission has no typed landing evidence");
                AssertContains("0 passed, 1 failed, 0 pending or running", missionReport.Summary, "the summary reports stored check statuses as counts");
                AssertFalse(missionReport.Summary.Contains(outputMarker, StringComparison.Ordinal), "check output is not copied into a work summary");
                AssertFalse(missionReport.Summary.Contains("ready", StringComparison.OrdinalIgnoreCase), "the report does not invent a readiness verdict");
            }
        }

        private static async Task<Fixture> CreateFixtureAsync(DatabaseDriver database)
        {
            TenantMetadata tenant = new TenantMetadata("Ask work report tests");
            await database.Tenants.CreateAsync(tenant).ConfigureAwait(false);
            UserMaster user = new UserMaster(tenant.Id, "ask-work-reports@example.invalid", "test-password");
            user.IsAdmin = true;
            await database.Users.CreateAsync(user).ConfigureAwait(false);
            AskThread thread = await database.AskThreads.CreateAsync(new AskThread
            {
                TenantId = tenant.Id,
                UserId = user.Id,
                Title = "Work report fixture"
            }).ConfigureAwait(false);
            AskTurn turn = new AskTurn { ThreadId = thread.Id, RequestId = "request_" + Guid.NewGuid().ToString("N"), CaptainId = "cpt_work_report", Content = "Record the work report fixture." };
            AskTurnReservation reservation = await database.AskThreads.TryReserveTurnAsync(tenant.Id, user.Id, turn).ConfigureAwait(false);
            AssertEqual(AskTurnReservationResultEnum.Created, reservation.Result);
            AssertTrue(await database.AskThreads.MarkRunningAsync(tenant.Id, user.Id, thread.Id, turn.RequestId).ConfigureAwait(false));
            AssertNotNull(await database.AskThreads.ReadAsync(tenant.Id, user.Id, thread.Id).ConfigureAwait(false));
            return new Fixture(thread, turn, AuthContext.Authenticated(tenant.Id, user.Id, true, true, "Session"));
        }

        private static async Task<Mission> CreateMissionAsync(DatabaseDriver database, Fixture fixture, string title, MissionStatusEnum status, string? commitHash, string? parentMissionId = null, string? voyageId = null)
        {
            return await database.Missions.CreateAsync(new Mission
            {
                TenantId = fixture.Thread.TenantId,
                UserId = fixture.Thread.UserId,
                Title = title,
                Status = status,
                CommitHash = commitHash,
                ParentMissionId = parentMissionId,
                VoyageId = voyageId
            }).ConfigureAwait(false);
        }

        private static Task<MissionAttemptFact> AddFactAsync(DatabaseDriver database, Fixture fixture, Mission mission, MissionAttemptFactTypeEnum type, DateTime createdUtc, bool isRescue = false, string? parentMissionId = null)
        {
            return database.MissionAttemptFacts.CreateAsync(new MissionAttemptFact
            {
                TenantId = fixture.Thread.TenantId,
                UserId = fixture.Thread.UserId,
                MissionId = mission.Id,
                VoyageId = mission.VoyageId,
                RootMissionId = parentMissionId ?? mission.Id,
                ParentMissionId = parentMissionId,
                FactType = type,
                IsRescue = isRescue,
                CreatedUtc = createdUtc
            });
        }

        private static async Task<List<AskWorkReport>> ReadReportsAsync(DatabaseDriver database, Fixture fixture, params AskWorkReference[] references)
        {
            AskActionProposal proposal = new AskActionProposal
            {
                TenantId = fixture.Thread.TenantId,
                UserId = fixture.Thread.UserId,
                ThreadId = fixture.Thread.Id,
                RequestId = fixture.Turn.RequestId,
                ToolName = "create_objective",
                ArgumentsJson = "{}",
                Fingerprint = new string('a', 64),
                CreatedUtc = DateTime.UtcNow,
                ExpiresUtc = DateTime.UtcNow.AddMinutes(10)
            };
            AskActionProposal? stored = await database.AskActionProposals.TryCreateAsync(proposal).ConfigureAwait(false);
            AssertNotNull(stored, "the public store accepts a proposal for the active turn");
            AssertTrue(await database.AskActionProposals.TryClaimAsync(proposal.TenantId, proposal.UserId, proposal.ThreadId, proposal.Id, proposal.Fingerprint, DateTime.UtcNow).ConfigureAwait(false));
            AssertTrue(await database.AskActionProposals.CompleteAsync(proposal.TenantId, proposal.UserId, proposal.ThreadId, proposal.Id, AskProposalStateEnum.Executed, "Recorded tool result", new List<AskWorkReference>(references), DateTime.UtcNow).ConfigureAwait(false));

            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            AskActionService service = new AskActionService(database, new LongRunningJobService(), logging);
            List<AskWorkReport>? reports = await service.WorkReportsAsync(fixture.Owner, fixture.Thread.Id).ConfigureAwait(false);
            AssertNotNull(reports, "the active human owner can read the thread work reports");
            return reports!;
        }

        private static AskWorkReference MissionReference(Mission mission) => new AskWorkReference { Kind = AskWorkKindEnum.Mission, Id = mission.Id };

        private static AskWorkReference VoyageReference(Voyage voyage) => new AskWorkReference { Kind = AskWorkKindEnum.Voyage, Id = voyage.Id };

        private static AskWorkReport Find(List<AskWorkReport> reports, string id)
        {
            return reports.Single(report => String.Equals(report.Id, id, StringComparison.Ordinal));
        }

        private static TestCaseDescriptor CaseAsync(string id, string name, Func<Task> run)
        {
            return new TestCaseDescriptor(SuiteId, id, name, (CancellationToken _) => run(), new List<string> { TestTags.Positive });
        }

        private sealed class Fixture
        {
            public AskThread Thread { get; }
            public AskTurn Turn { get; }
            public AuthContext Owner { get; }

            public Fixture(AskThread thread, AskTurn turn, AuthContext owner)
            {
                Thread = thread;
                Turn = turn;
                Owner = owner;
            }
        }
    }
}
