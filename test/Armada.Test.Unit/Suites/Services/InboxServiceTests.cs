namespace Armada.Test.Unit.Suites.Services
{
    using Armada.Core;
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

            await RunTest("GetInboxAsync hides a Failed mission whose voyage has halted", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Voyage halted = await testDb.Driver.Voyages.CreateAsync(new Voyage("Halted voyage")
                    {
                        Status = VoyageStatusEnum.Failed
                    }).ConfigureAwait(false);
                    Voyage live = await testDb.Driver.Voyages.CreateAsync(new Voyage("Live voyage")
                    {
                        Status = VoyageStatusEnum.InProgress
                    }).ConfigureAwait(false);

                    Mission haltedFailure = new Mission("Stage failed in a halted voyage")
                    {
                        Status = MissionStatusEnum.Failed,
                        VoyageId = halted.Id
                    };
                    Mission liveFailure = new Mission("Stage failed in a live voyage")
                    {
                        Status = MissionStatusEnum.Failed,
                        VoyageId = live.Id
                    };
                    Mission haltedLanding = new Mission("Landing failed in a halted voyage")
                    {
                        Status = MissionStatusEnum.LandingFailed,
                        VoyageId = halted.Id
                    };
                    await testDb.Driver.Missions.CreateAsync(haltedFailure).ConfigureAwait(false);
                    await testDb.Driver.Missions.CreateAsync(liveFailure).ConfigureAwait(false);
                    await testDb.Driver.Missions.CreateAsync(haltedLanding).ConfigureAwait(false);

                    InboxService service = CreateService(testDb.Driver);
                    List<InboxItem> items = await service.GetInboxAsync().ConfigureAwait(false);

                    AssertTrue(items.All(item => item.EntityId != haltedFailure.Id), "a failed mission in a halted voyage is carried by its incident, not the inbox");
                    AssertTrue(items.All(item => item.EntityId != haltedLanding.Id), "a landing failure in a halted voyage is carried by its incident, not the inbox");
                    AssertTrue(items.Any(item => item.Kind == "failed" && item.EntityId == liveFailure.Id), "a failed mission in a live voyage stays actionable");
                }
            });

            await RunTest("GetInboxAsync follows a rescue mission to the voyage it rescues", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Voyage halted = await testDb.Driver.Voyages.CreateAsync(new Voyage("Halted voyage")
                    {
                        Status = VoyageStatusEnum.Cancelled
                    }).ConfigureAwait(false);
                    Voyage live = await testDb.Driver.Voyages.CreateAsync(new Voyage("Live voyage")
                    {
                        Status = VoyageStatusEnum.Open
                    }).ConfigureAwait(false);

                    Mission haltedParent = await testDb.Driver.Missions.CreateAsync(new Mission("Original in halted voyage")
                    {
                        Status = MissionStatusEnum.Failed,
                        VoyageId = halted.Id
                    }).ConfigureAwait(false);
                    Mission liveParent = await testDb.Driver.Missions.CreateAsync(new Mission("Original in live voyage")
                    {
                        Status = MissionStatusEnum.Failed,
                        VoyageId = live.Id
                    }).ConfigureAwait(false);

                    Mission haltedRescue = new Mission("Rescue 1: halted")
                    {
                        Status = MissionStatusEnum.Failed,
                        ParentMissionId = haltedParent.Id
                    };
                    Mission liveRescue = new Mission("Rescue 1: live")
                    {
                        Status = MissionStatusEnum.LandingFailed,
                        ParentMissionId = liveParent.Id
                    };
                    Mission orphan = new Mission("No voyage anywhere")
                    {
                        Status = MissionStatusEnum.Failed
                    };
                    await testDb.Driver.Missions.CreateAsync(haltedRescue).ConfigureAwait(false);
                    await testDb.Driver.Missions.CreateAsync(liveRescue).ConfigureAwait(false);
                    await testDb.Driver.Missions.CreateAsync(orphan).ConfigureAwait(false);

                    InboxService service = CreateService(testDb.Driver);
                    List<InboxItem> items = await service.GetInboxAsync().ConfigureAwait(false);

                    AssertTrue(items.All(item => item.EntityId != haltedRescue.Id), "a rescue of a halted voyage is not actionable on its own");
                    AssertTrue(items.Any(item => item.Kind == "landing_failed" && item.EntityId == liveRescue.Id), "a rescue whose parent voyage is live stays actionable");
                    AssertTrue(items.Any(item => item.Kind == "failed" && item.EntityId == orphan.Id), "a mission with no voyage in its chain stays visible");
                }
            });

            await RunTest("GetInboxAsync surfaces open incidents and hides closed ones", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    IncidentService incidents = new IncidentService(testDb.Driver);
                    AuthContext auth = AuthContext.Authenticated(Constants.DefaultTenantId, Constants.DefaultUserId, true, true, "UnitTest");

                    Incident open = await incidents.CreateAsync(auth, new IncidentUpsertRequest
                    {
                        Title = "Judge rejected the verdict",
                        Status = IncidentStatusEnum.Open,
                        Severity = IncidentSeverityEnum.High,
                        RecoveryNotes = "Autonomous policy stopped before rescue dispatch."
                    }).ConfigureAwait(false);
                    Incident monitoring = await incidents.CreateAsync(auth, new IncidentUpsertRequest
                    {
                        Title = "Host memory watch",
                        Status = IncidentStatusEnum.Monitoring,
                        Severity = IncidentSeverityEnum.Low
                    }).ConfigureAwait(false);
                    Incident closed = await incidents.CreateAsync(auth, new IncidentUpsertRequest
                    {
                        Title = "Already handled",
                        Status = IncidentStatusEnum.Closed,
                        Severity = IncidentSeverityEnum.Critical
                    }).ConfigureAwait(false);

                    InboxService service = CreateService(testDb.Driver);
                    List<InboxItem> items = await service.GetInboxAsync().ConfigureAwait(false);

                    InboxItem? openItem = items.FirstOrDefault(item => item.Kind == "open_incident" && item.EntityId == open.Id);
                    AssertNotNull(openItem, "an open incident must produce an inbox item");
                    AssertEqual(InboxSeverityEnum.Critical, openItem!.Severity, "a High incident is critical in the inbox");
                    AssertEqual("Autonomous policy stopped before rescue dispatch.", openItem.Detail);
                    AssertEqual("/incidents/" + open.Id, openItem.Href);

                    InboxItem? monitoringItem = items.FirstOrDefault(item => item.Kind == "open_incident" && item.EntityId == monitoring.Id);
                    AssertNotNull(monitoringItem, "a monitoring incident must produce an inbox item");
                    AssertEqual(InboxSeverityEnum.Warning, monitoringItem!.Severity, "a Low incident is a warning in the inbox");

                    AssertTrue(items.All(item => item.EntityId != closed.Id), "a closed incident is not actionable");
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
