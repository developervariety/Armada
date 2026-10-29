namespace Armada.Test.Unit.Suites.Services
{
    using System.Net;
    using System.Net.Http;
    using System.Text;
    using System.Text.Json;
    using Armada.Core;
    using Armada.Core.Database;
    using Armada.Core.Database.Sqlite;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Server;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    public class InboxServiceTests : TestSuite
    {
        public override string Name => "Inbox Service";

        protected override async Task RunTestsAsync()
        {
            await RunTest("GetInboxAsync lists a Review mission only when its stage requires a review decision", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Mission mission = new Mission("Review me")
                    {
                        Status = MissionStatusEnum.Review,
                        RequiresReview = true,
                        ReviewRequestedUtc = DateTime.UtcNow
                    };
                    await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);
                    // A captain reported Review as progress on a stage that asks for no review.
                    Mission progress = new Mission("Still working")
                    {
                        Status = MissionStatusEnum.Review
                    };
                    await testDb.Driver.Missions.CreateAsync(progress).ConfigureAwait(false);

                    InboxService service = CreateService(testDb.Driver);
                    List<InboxItem> items = await service.GetInboxAsync().ConfigureAwait(false);

                    List<InboxItem> reviews = items.Where(item => item.Kind == "review").ToList();
                    AssertEqual(1, reviews.Count, "only the mission whose stage requires review waits on a review decision");
                    AssertEqual("Review: Review me", reviews[0].Title);
                    AssertEqual(mission.Id, reviews[0].EntityId);
                    AssertEqual("/missions/" + mission.Id, reviews[0].Href);
                    AssertFalse(items.Any(item => item.EntityId == progress.Id), "Review reported as progress is not an operator action");
                }
            });

            await RunTest("GetInboxAsync surfaces a Judge PASS held for operator review", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Mission held = new Mission("Judge held")
                    {
                        Persona = "Judge",
                        Status = MissionStatusEnum.WorkProduced,
                        HeldForOperatorReview = true,
                        HeldForOperatorReviewReason = "review substance is asserted, not evidenced"
                    };
                    await testDb.Driver.Missions.CreateAsync(held).ConfigureAwait(false);
                    Mission notHeld = new Mission("Judge produced")
                    {
                        Persona = "Judge",
                        Status = MissionStatusEnum.WorkProduced
                    };
                    await testDb.Driver.Missions.CreateAsync(notHeld).ConfigureAwait(false);

                    InboxService service = CreateService(testDb.Driver);
                    List<InboxItem> items = await service.GetInboxAsync().ConfigureAwait(false);

                    List<InboxItem> holds = items.Where(item => item.Kind == "judge_pass_held").ToList();
                    AssertEqual(1, holds.Count, "only the held mission is listed as a held Judge PASS");
                    AssertEqual(held.Id, holds[0].EntityId);
                    AssertEqual(InboxSeverityEnum.Warning, holds[0].Severity);
                    AssertContains("review substance is asserted, not evidenced", holds[0].Detail, "the item names the hold reason");
                    AssertContains("armada_review_hold", holds[0].Detail, "the item names the operator action");
                }
            });

            await RunTest("GetInboxAsync marks a LandingFailed mission as critical", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Voyage live = await testDb.Driver.Voyages.CreateAsync(new Voyage("Live voyage")
                    {
                        Status = VoyageStatusEnum.InProgress
                    }).ConfigureAwait(false);
                    Mission mission = new Mission("Land this")
                    {
                        Status = MissionStatusEnum.LandingFailed,
                        VoyageId = live.Id,
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

            await RunTest("GetInboxAsync hides a Failed mission with a null voyage", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Mission orphan = new Mission("Failed work with no voyage")
                    {
                        Status = MissionStatusEnum.Failed,
                        FailureReason = "worker_produced_no_commits"
                    };
                    await testDb.Driver.Missions.CreateAsync(orphan).ConfigureAwait(false);

                    InboxService service = CreateService(testDb.Driver);
                    List<InboxItem> items = await service.GetInboxAsync().ConfigureAwait(false);

                    AssertTrue(items.All(item => item.EntityId != orphan.Id), "a failed mission with a null voyage is not listed");
                }
            });

            await RunTest("GetInboxAsync surfaces a Failed mission", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Voyage live = await testDb.Driver.Voyages.CreateAsync(new Voyage("Live voyage")
                    {
                        Status = VoyageStatusEnum.InProgress
                    }).ConfigureAwait(false);
                    Mission mission = new Mission("Failed work")
                    {
                        Status = MissionStatusEnum.Failed,
                        VoyageId = live.Id,
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
                    AssertTrue(items.All(item => item.EntityId != orphan.Id), "a mission with no voyage in its chain is not listed");
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
                        RootCause = "Handled by an operator",
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
                        Status = MissionStatusEnum.Review,
                        RequiresReview = true
                    };
                    await testDb.Driver.Missions.CreateAsync(review).ConfigureAwait(false);

                    Voyage live = await testDb.Driver.Voyages.CreateAsync(new Voyage("Live voyage")
                    {
                        Status = VoyageStatusEnum.InProgress
                    }).ConfigureAwait(false);
                    Mission landingFailed = new Mission("Land this")
                    {
                        Status = MissionStatusEnum.LandingFailed,
                        VoyageId = live.Id
                    };
                    await testDb.Driver.Missions.CreateAsync(landingFailed).ConfigureAwait(false);

                    InboxService service = CreateService(testDb.Driver);
                    List<InboxItem> items = await service.GetInboxAsync().ConfigureAwait(false);

                    AssertTrue(items.Count >= 2, "the inbox must contain both seeded items");
                    AssertEqual(InboxSeverityEnum.Critical, items[0].Severity, "the most-urgent item must come first");
                }
            });
            await RunTest("POST /api/v1/inbox/enumerate pages the inbox with a true total in the most-urgent-first order and a bounded page size", async () =>
            {
                string tempDir = Path.Combine(Path.GetTempPath(), "armada_inbox_pages_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDir);
                ArmadaSettings settings = LongRunningJobServiceTests.NewServerSettings(tempDir);
                // The incident sweep opens and closes incidents in the background after startup; with it on, the
                // inbox changes between the whole-list read and the page reads.
                settings.IncidentLifecycle.Enabled = false;
                using (DatabaseDriver driver = await DatabaseDriverFactory.CreateAndInitializeAsync(settings.Database).ConfigureAwait(false))
                {
                    for (int i = 1; i <= 3; i++)
                        await driver.Missions.CreateAsync(new Mission("Review me " + i) { Status = MissionStatusEnum.Review, RequiresReview = true }).ConfigureAwait(false);
                    AuthContext auth = AuthContext.Authenticated(Constants.DefaultTenantId, Constants.DefaultUserId, true, true, "UnitTest");
                    await new IncidentService(driver).CreateAsync(auth, new IncidentUpsertRequest
                    {
                        Title = "Landing stopped",
                        Status = IncidentStatusEnum.Open,
                        Severity = IncidentSeverityEnum.Critical
                    }).ConfigureAwait(false);
                }

                LoggingModule logging = new LoggingModule();
                logging.Settings.EnableConsole = false;
                ArmadaServer server = new ArmadaServer(logging, settings, quiet: true);
                try
                {
                    await server.StartAsync().ConfigureAwait(false);
                    using (HttpClient client = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:" + settings.AdmiralPort), Timeout = TimeSpan.FromSeconds(30) })
                    {
                        client.DefaultRequestHeaders.Add("X-Api-Key", settings.ApiKey);

                        HttpResponseMessage wholeResponse = await client.GetAsync("/api/v1/inbox").ConfigureAwait(false);
                        List<InboxItem> whole = JsonHelper.Deserialize<List<InboxItem>>(await wholeResponse.Content.ReadAsStringAsync().ConfigureAwait(false));
                        AssertEqual(4, whole.Count, "the whole-list route returns every item");

                        List<InboxItem> paged = new List<InboxItem>();
                        for (int pageNumber = 1; pageNumber <= 2; pageNumber++)
                        {
                            EnumerationResult<InboxItem> page = await ReadInboxPageAsync(client, new { PageNumber = pageNumber, PageSize = 3 }).ConfigureAwait(false);
                            AssertEqual(4L, page.TotalRecords, "page " + pageNumber + " reports the whole count, not the page length");
                            AssertEqual(2, page.TotalPages, "page count");
                            AssertEqual(pageNumber == 1 ? 3 : 1, page.Objects.Count, "page " + pageNumber + " length");
                            paged.AddRange(page.Objects);
                        }
                        AssertEqual(String.Join(",", whole.Select(item => item.EntityId)), String.Join(",", paged.Select(item => item.EntityId)), "pages concatenate in the most-urgent-first order of the whole list");
                        AssertEqual(InboxSeverityEnum.Critical, paged[0].Severity, "the most urgent item leads the first page");

                        EnumerationResult<InboxItem> beyond = await ReadInboxPageAsync(client, new { PageNumber = 3, PageSize = 3 }).ConfigureAwait(false);
                        AssertEqual(4L, beyond.TotalRecords, "a page past the end still reports the true total");
                        AssertEqual(0, beyond.Objects.Count, "a page past the end is empty");

                        EnumerationResult<InboxItem> oversize = await ReadInboxPageAsync(client, new { PageNumber = 1, PageSize = 5000 }).ConfigureAwait(false);
                        AssertEqual(EnumerationQuery.MaxPageSize, oversize.PageSize, "the page size is clamped to the maximum");
                        AssertEqual(5000, oversize.RequestedPageSize ?? 0, "the response names the size that was asked for");
                        AssertEqual(4, oversize.Objects.Count, "a large page holds every item");
                    }
                }
                finally
                {
                    server.Stop();
                    try
                    {
                        Directory.Delete(tempDir, true);
                    }
                    catch (IOException)
                    {
                        // A stopped server can briefly hold log handles; the temporary directory is disposable.
                    }
                }
            });
        }

        private async Task<EnumerationResult<InboxItem>> ReadInboxPageAsync(HttpClient client, object query)
        {
            HttpResponseMessage response = await client.PostAsync("/api/v1/inbox/enumerate",
                new StringContent(JsonSerializer.Serialize(query), Encoding.UTF8, "application/json")).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            AssertEqual(HttpStatusCode.OK, response.StatusCode, "the paged inbox is served: " + body);
            return JsonHelper.Deserialize<EnumerationResult<InboxItem>>(body);
        }

        private static InboxService CreateService(SqliteDatabaseDriver database)
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return new InboxService(database, logging);
        }
    }
}
