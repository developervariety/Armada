namespace Armada.Test.Unit.Suites.Services
{
    using Armada.Core.Database.Sqlite;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    public class InboxServiceTests : TestSuite
    {
        public override string Name => "Inbox Service";

        protected override async Task RunTestsAsync()
        {
            await RunTest("GetInboxAsync surfaces a Review mission", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Mission mission = new Mission("Review me")
                    {
                        Status = MissionStatusEnum.Review
                    };
                    await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                    InboxService service = CreateService(testDb.Driver);
                    List<InboxItem> items = await service.GetInboxAsync().ConfigureAwait(false);

                    InboxItem? review = items.FirstOrDefault(item => item.Kind == "review");
                    AssertNotNull(review, "a review mission must produce a review inbox item");
                    AssertEqual("Review: Review me", review!.Title);
                    AssertEqual(mission.Id, review.EntityId);
                    AssertEqual("/missions/" + mission.Id, review.Href);
                }
            });

            await RunTest("GetInboxAsync marks a LandingFailed mission as critical", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Mission mission = new Mission("Land this")
                    {
                        Status = MissionStatusEnum.LandingFailed,
                        FailureReason = "merge conflicts in: src/Foo.cs"
                    };
                    await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                    InboxService service = CreateService(testDb.Driver);
                    List<InboxItem> items = await service.GetInboxAsync().ConfigureAwait(false);

                    InboxItem? landingFailed = items.FirstOrDefault(item => item.Kind == "landing_failed");
                    AssertNotNull(landingFailed, "a landing-failed mission must produce an inbox item");
                    AssertEqual(InboxSeverityEnum.Critical, landingFailed!.Severity);
                    AssertEqual("merge conflicts in: src/Foo.cs", landingFailed.Detail);
                }
            });

            await RunTest("GetInboxAsync surfaces a Failed mission", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Mission mission = new Mission("Failed work")
                    {
                        Status = MissionStatusEnum.Failed,
                        FailureReason = "gate failed"
                    };
                    await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                    InboxService service = CreateService(testDb.Driver);
                    List<InboxItem> items = await service.GetInboxAsync().ConfigureAwait(false);

                    InboxItem? failed = items.FirstOrDefault(item => item.Kind == "failed");
                    AssertNotNull(failed, "a failed mission must produce an inbox item");
                    AssertEqual(InboxSeverityEnum.Warning, failed!.Severity);
                    AssertEqual("gate failed", failed.Detail);
                }
            });

            await RunTest("GetInboxAsync surfaces a stalled captain", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Captain captain = new Captain("stalled-captain")
                    {
                        State = CaptainStateEnum.Stalled
                    };
                    await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);

                    InboxService service = CreateService(testDb.Driver);
                    List<InboxItem> items = await service.GetInboxAsync().ConfigureAwait(false);

                    InboxItem? stalled = items.FirstOrDefault(item => item.Kind == "stalled_captain");
                    AssertNotNull(stalled, "a stalled captain must produce an inbox item");
                    AssertEqual("Stalled captain: stalled-captain", stalled!.Title);
                    AssertEqual("/captains/" + captain.Id, stalled.Href);
                }
            });

            await RunTest("GetInboxAsync surfaces a failed merge entry", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    MergeEntry entry = new MergeEntry("armada/captain/msn_x", "main")
                    {
                        Status = MergeStatusEnum.Failed
                    };
                    await testDb.Driver.MergeEntries.CreateAsync(entry).ConfigureAwait(false);

                    InboxService service = CreateService(testDb.Driver);
                    List<InboxItem> items = await service.GetInboxAsync().ConfigureAwait(false);

                    InboxItem? mergeFailed = items.FirstOrDefault(item => item.Kind == "merge_failed");
                    AssertNotNull(mergeFailed, "a failed merge entry must produce an inbox item");
                    AssertEqual(InboxSeverityEnum.Critical, mergeFailed!.Severity);
                    AssertEqual("/merge-queue/" + entry.Id, mergeFailed.Href);
                }
            });

            await RunTest("GetInboxAsync excludes informational missions", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Mission complete = new Mission("Done work")
                    {
                        Status = MissionStatusEnum.Complete
                    };
                    await testDb.Driver.Missions.CreateAsync(complete).ConfigureAwait(false);

                    InboxService service = CreateService(testDb.Driver);
                    List<InboxItem> items = await service.GetInboxAsync().ConfigureAwait(false);

                    AssertEqual(0, items.Count, "a completed mission is informational and must not appear in the inbox");
                }
            });

            await RunTest("GetInboxAsync orders critical items first", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Mission review = new Mission("Review this")
                    {
                        Status = MissionStatusEnum.Review
                    };
                    await testDb.Driver.Missions.CreateAsync(review).ConfigureAwait(false);

                    Mission landingFailed = new Mission("Land this")
                    {
                        Status = MissionStatusEnum.LandingFailed
                    };
                    await testDb.Driver.Missions.CreateAsync(landingFailed).ConfigureAwait(false);

                    InboxService service = CreateService(testDb.Driver);
                    List<InboxItem> items = await service.GetInboxAsync().ConfigureAwait(false);

                    AssertTrue(items.Count >= 2, "the inbox must contain both seeded items");
                    AssertEqual(InboxSeverityEnum.Critical, items[0].Severity, "the most-urgent item must come first");
                }
            });
        }

        private static InboxService CreateService(SqliteDatabaseDriver database)
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return new InboxService(database, logging);
        }
    }
}
