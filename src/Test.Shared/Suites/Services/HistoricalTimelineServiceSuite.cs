namespace Test.Shared.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using SyslogLogging;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// Descriptors for <see cref="HistoricalTimelineService"/>: aggregation of current Armada
    /// entities into a single filterable timeline over a live SQLite store. Positive cases assert
    /// cross-entity aggregation, vessel/actor/source/text filtering, postmortem narrowing, and
    /// refinement-session routing; negative cases cover the audited null-argument guards.
    /// </summary>
    public sealed class HistoricalTimelineServiceSuite : IArmadaTestSuite
    {
        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the HistoricalTimelineService suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(CaseAsync("enumerate_aggregates_current_entities_into_one_timeline", "EnumerateAsync aggregates current Armada entities into one timeline", TestTags.Positive, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);

                string tenantId = "ten_history";
                string userId = "usr_history";
                string workingDirectory = Path.Combine(Path.GetTempPath(), "armada-history-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(workingDirectory);

                try
                {
                    await EnsureTenantAndUserAsync(testDb, tenantId, userId).ConfigureAwait(false);

                    Vessel vessel = CreateVessel(tenantId, userId, workingDirectory);
                    await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Voyage voyage = new Voyage
                    {
                        TenantId = tenantId,
                        UserId = userId,
                        Title = "History Voyage",
                        Description = "Voyage description",
                        Status = VoyageStatusEnum.Open
                    };
                    await testDb.Driver.Voyages.CreateAsync(voyage).ConfigureAwait(false);

                    Mission mission = new Mission
                    {
                        TenantId = tenantId,
                        UserId = userId,
                        VesselId = vessel.Id,
                        VoyageId = voyage.Id,
                        Title = "History Mission",
                        Description = "Mission description",
                        Status = MissionStatusEnum.WorkProduced
                    };
                    await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                    CheckRun checkRun = new CheckRun
                    {
                        TenantId = tenantId,
                        UserId = userId,
                        VesselId = vessel.Id,
                        MissionId = mission.Id,
                        VoyageId = voyage.Id,
                        Type = CheckRunTypeEnum.UnitTest,
                        Status = CheckRunStatusEnum.Passed,
                        Command = "dotnet test",
                        Summary = "10 passed",
                        Output = "Passed! 10 tests run.",
                        DurationMs = 1234,
                        StartedUtc = DateTime.UtcNow.AddMinutes(-2),
                        CompletedUtc = DateTime.UtcNow.AddMinutes(-1)
                    };
                    await testDb.Driver.CheckRuns.CreateAsync(checkRun).ConfigureAwait(false);

                    Release release = new Release
                    {
                        TenantId = tenantId,
                        UserId = userId,
                        VesselId = vessel.Id,
                        Title = "History Release",
                        Version = "1.0.0",
                        TagName = "v1.0.0",
                        Summary = "Release summary",
                        Status = ReleaseStatusEnum.Candidate,
                        VoyageIds = new List<string> { voyage.Id },
                        MissionIds = new List<string> { mission.Id },
                        CheckRunIds = new List<string> { checkRun.Id }
                    };
                    await testDb.Driver.Releases.CreateAsync(release).ConfigureAwait(false);

                    RequestHistoryEntry requestEntry = new RequestHistoryEntry
                    {
                        TenantId = tenantId,
                        UserId = userId,
                        PrincipalDisplay = userId + "@armada.test",
                        AuthMethod = "Session",
                        Method = "GET",
                        Route = "/api/v1/status",
                        RouteTemplate = "/api/v1/status",
                        QueryString = "trace=history",
                        StatusCode = 200,
                        DurationMs = 12.5,
                        RequestSizeBytes = 10,
                        ResponseSizeBytes = 20,
                        RequestContentType = "application/json",
                        ResponseContentType = "application/json",
                        IsSuccess = true,
                        CreatedUtc = DateTime.UtcNow
                    };
                    RequestHistoryDetail requestDetail = new RequestHistoryDetail
                    {
                        RequestHistoryId = requestEntry.Id,
                        RequestHeadersJson = "{\"X-Test\":\"history\"}",
                        ResponseHeadersJson = "{\"Content-Type\":\"application/json\"}",
                        RequestBodyText = "{\"trace\":\"history\"}",
                        ResponseBodyText = "{\"ok\":true}"
                    };
                    await testDb.Driver.RequestHistory.CreateAsync(requestEntry, requestDetail).ConfigureAwait(false);

                    HistoricalTimelineService service = new HistoricalTimelineService(testDb.Driver);
                    AuthContext auth = AuthContext.Authenticated(tenantId, userId, false, true, "UnitTest");

                    EnumerationResult<HistoricalTimelineEntry> result = await service.EnumerateAsync(auth, new HistoricalTimelineQuery
                    {
                        PageNumber = 1,
                        PageSize = 50
                    }).ConfigureAwait(false);

                    AssertTrue(result.TotalRecords >= 5, "Expected voyage, mission, check run, release, and request history entries.");
                    AssertTrue(result.Objects.Exists(entry => entry.SourceType == "Voyage" && entry.SourceId == voyage.Id));
                    AssertTrue(result.Objects.Exists(entry => entry.SourceType == "Mission" && entry.SourceId == mission.Id));
                    AssertTrue(result.Objects.Exists(entry => entry.SourceType == "CheckRun" && entry.SourceId == checkRun.Id));
                    AssertTrue(result.Objects.Exists(entry => entry.SourceType == "Release" && entry.SourceId == release.Id));
                    AssertTrue(result.Objects.Exists(entry => entry.SourceType == "Request" && entry.SourceId == requestEntry.Id));
                }
                finally
                {
                    TryDeleteDirectory(workingDirectory);
                }
            }));

            cases.Add(CaseAsync("enumerate_filters_by_vessel_actor_source_type_and_text", "EnumerateAsync filters by vessel, actor, source type, and text", TestTags.Positive, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);

                string tenantId = "ten_history_filter";
                string userId = "usr_history_filter";
                string workingDirectory = Path.Combine(Path.GetTempPath(), "armada-history-filter-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(workingDirectory);

                try
                {
                    await EnsureTenantAndUserAsync(testDb, tenantId, userId).ConfigureAwait(false);

                    Vessel vessel = CreateVessel(tenantId, userId, workingDirectory);
                    vessel.Name = "Filter Vessel";
                    await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    CheckRun matchingRun = new CheckRun
                    {
                        TenantId = tenantId,
                        UserId = userId,
                        VesselId = vessel.Id,
                        Type = CheckRunTypeEnum.Build,
                        Status = CheckRunStatusEnum.Passed,
                        Command = "dotnet build",
                        Summary = "Filter summary"
                    };
                    await testDb.Driver.CheckRuns.CreateAsync(matchingRun).ConfigureAwait(false);

                    CheckRun otherRun = new CheckRun
                    {
                        TenantId = tenantId,
                        UserId = userId,
                        VesselId = null,
                        Type = CheckRunTypeEnum.Build,
                        Status = CheckRunStatusEnum.Failed,
                        Command = "dotnet build",
                        Summary = "Other summary"
                    };
                    await testDb.Driver.CheckRuns.CreateAsync(otherRun).ConfigureAwait(false);

                    HistoricalTimelineService service = new HistoricalTimelineService(testDb.Driver);
                    AuthContext auth = AuthContext.Authenticated(tenantId, userId, false, false, "UnitTest");

                    EnumerationResult<HistoricalTimelineEntry> result = await service.EnumerateAsync(auth, new HistoricalTimelineQuery
                    {
                        PageNumber = 1,
                        PageSize = 25,
                        VesselId = vessel.Id,
                        Actor = userId,
                        Text = "Filter summary",
                        SourceTypes = new List<string> { "CheckRun" }
                    }).ConfigureAwait(false);

                    AssertEqual(1, result.Objects.Count);
                    AssertEqual(matchingRun.Id, result.Objects[0].SourceId);
                    AssertEqual("CheckRun", result.Objects[0].SourceType);
                }
                finally
                {
                    TryDeleteDirectory(workingDirectory);
                }
            }));

            cases.Add(CaseAsync("enumerate_narrows_to_postmortem_linked_incident_context", "EnumerateAsync can narrow to postmortem-linked incident context", TestTags.Positive, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);

                string tenantId = "ten_history_postmortem";
                string userId = "usr_history_postmortem";
                string workingDirectory = Path.Combine(Path.GetTempPath(), "armada-history-postmortem-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(workingDirectory);

                try
                {
                    await EnsureTenantAndUserAsync(testDb, tenantId, userId).ConfigureAwait(false);

                    Vessel vessel = CreateVessel(tenantId, userId, workingDirectory);
                    await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Voyage voyage = new Voyage
                    {
                        TenantId = tenantId,
                        UserId = userId,
                        Title = "Postmortem Voyage",
                        Description = "Shared voyage",
                        Status = VoyageStatusEnum.Open
                    };
                    await testDb.Driver.Voyages.CreateAsync(voyage).ConfigureAwait(false);

                    Mission mission = new Mission
                    {
                        TenantId = tenantId,
                        UserId = userId,
                        VesselId = vessel.Id,
                        VoyageId = voyage.Id,
                        Title = "Postmortem Mission",
                        Description = "Shared mission",
                        Status = MissionStatusEnum.WorkProduced
                    };
                    await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                    DeploymentEnvironment environment = new DeploymentEnvironment
                    {
                        TenantId = tenantId,
                        UserId = userId,
                        VesselId = vessel.Id,
                        Name = "production",
                        Kind = EnvironmentKindEnum.Production,
                        Active = true,
                        IsDefault = true
                    };
                    await testDb.Driver.Environments.CreateAsync(environment).ConfigureAwait(false);

                    Release releaseWithPostmortem = new Release
                    {
                        TenantId = tenantId,
                        UserId = userId,
                        VesselId = vessel.Id,
                        Title = "Release With Postmortem",
                        Version = "1.0.0",
                        TagName = "v1.0.0",
                        Status = ReleaseStatusEnum.Candidate,
                        MissionIds = new List<string> { mission.Id },
                        VoyageIds = new List<string> { voyage.Id }
                    };
                    await testDb.Driver.Releases.CreateAsync(releaseWithPostmortem).ConfigureAwait(false);

                    Release releaseWithoutPostmortem = new Release
                    {
                        TenantId = tenantId,
                        UserId = userId,
                        VesselId = vessel.Id,
                        Title = "Release Without Postmortem",
                        Version = "1.0.1",
                        TagName = "v1.0.1",
                        Status = ReleaseStatusEnum.Candidate
                    };
                    await testDb.Driver.Releases.CreateAsync(releaseWithoutPostmortem).ConfigureAwait(false);

                    Deployment deploymentWithPostmortem = new Deployment
                    {
                        TenantId = tenantId,
                        UserId = userId,
                        VesselId = vessel.Id,
                        EnvironmentId = environment.Id,
                        EnvironmentName = environment.Name,
                        ReleaseId = releaseWithPostmortem.Id,
                        MissionId = mission.Id,
                        VoyageId = voyage.Id,
                        Title = "Deploy With Postmortem",
                        Status = DeploymentStatusEnum.RolledBack,
                        VerificationStatus = DeploymentVerificationStatusEnum.Passed
                    };
                    await testDb.Driver.Deployments.CreateAsync(deploymentWithPostmortem).ConfigureAwait(false);

                    Deployment deploymentWithoutPostmortem = new Deployment
                    {
                        TenantId = tenantId,
                        UserId = userId,
                        VesselId = vessel.Id,
                        EnvironmentId = environment.Id,
                        EnvironmentName = environment.Name,
                        ReleaseId = releaseWithoutPostmortem.Id,
                        Title = "Deploy Without Postmortem",
                        Status = DeploymentStatusEnum.Succeeded,
                        VerificationStatus = DeploymentVerificationStatusEnum.Passed
                    };
                    await testDb.Driver.Deployments.CreateAsync(deploymentWithoutPostmortem).ConfigureAwait(false);

                    IncidentService incidentService = new IncidentService(testDb.Driver);
                    AuthContext auth = AuthContext.Authenticated(tenantId, userId, false, true, "UnitTest");

                    Incident incidentWithPostmortem = await incidentService.CreateAsync(auth, new IncidentUpsertRequest
                    {
                        Title = "Incident With Postmortem",
                        Summary = "Rollback and review required",
                        Status = IncidentStatusEnum.Closed,
                        RootCause = "Release shipped a bad migration",
                        Severity = IncidentSeverityEnum.Critical,
                        EnvironmentId = environment.Id,
                        EnvironmentName = environment.Name,
                        DeploymentId = deploymentWithPostmortem.Id,
                        ReleaseId = releaseWithPostmortem.Id,
                        VesselId = vessel.Id,
                        MissionId = mission.Id,
                        VoyageId = voyage.Id,
                        RollbackDeploymentId = deploymentWithPostmortem.Id,
                        Postmortem = "Confirmed root cause and follow-up actions"
                    }).ConfigureAwait(false);

                    Incident incidentWithoutPostmortem = await incidentService.CreateAsync(auth, new IncidentUpsertRequest
                    {
                        Title = "Incident Without Postmortem",
                        Summary = "Still under investigation",
                        Status = IncidentStatusEnum.Open,
                        Severity = IncidentSeverityEnum.High,
                        EnvironmentId = environment.Id,
                        EnvironmentName = environment.Name,
                        DeploymentId = deploymentWithoutPostmortem.Id,
                        ReleaseId = releaseWithoutPostmortem.Id,
                        VesselId = vessel.Id
                    }).ConfigureAwait(false);

                    HistoricalTimelineService service = new HistoricalTimelineService(testDb.Driver);
                    EnumerationResult<HistoricalTimelineEntry> result = await service.EnumerateAsync(auth, new HistoricalTimelineQuery
                    {
                        PageNumber = 1,
                        PageSize = 100,
                        PostmortemOnly = true
                    }).ConfigureAwait(false);

                    AssertTrue(result.Objects.Exists(entry => entry.SourceType == "Incident" && entry.SourceId == incidentWithPostmortem.Id));
                    AssertTrue(result.Objects.Exists(entry => entry.SourceType == "Deployment" && entry.SourceId == deploymentWithPostmortem.Id));
                    AssertTrue(result.Objects.Exists(entry => entry.SourceType == "Release" && entry.SourceId == releaseWithPostmortem.Id));
                    AssertFalse(result.Objects.Exists(entry => entry.SourceType == "Incident" && entry.SourceId == incidentWithoutPostmortem.Id));
                    AssertFalse(result.Objects.Exists(entry => entry.SourceType == "Deployment" && entry.SourceId == deploymentWithoutPostmortem.Id));
                    AssertFalse(result.Objects.Exists(entry => entry.SourceType == "Release" && entry.SourceId == releaseWithoutPostmortem.Id));
                }
                finally
                {
                    TryDeleteDirectory(workingDirectory);
                }
            }));

            cases.Add(CaseAsync("enumerate_includes_backlog_refinement_sessions_routed_to_backlog_detail", "EnumerateAsync includes backlog refinement sessions and routes them to backlog detail", TestTags.Positive, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);

                string tenantId = "ten_history_refinement";
                string userId = "usr_history_refinement";
                await EnsureTenantAndUserAsync(testDb, tenantId, userId).ConfigureAwait(false);

                AuthContext auth = AuthContext.Authenticated(tenantId, userId, false, true, "UnitTest");
                ObjectiveService objectives = new ObjectiveService(testDb.Driver);
                Objective objective = await objectives.CreateAsync(auth, new ObjectiveUpsertRequest
                {
                    Title = "History backlog item",
                    Status = ObjectiveStatusEnum.Scoped
                }).ConfigureAwait(false);
                Captain captain = new Captain("history-refinement-captain")
                {
                    TenantId = tenantId,
                    UserId = userId
                };
                await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);

                DateTime startedUtc = DateTime.UtcNow.AddMinutes(-5);
                ObjectiveRefinementSession session = new ObjectiveRefinementSession
                {
                    TenantId = tenantId,
                    UserId = userId,
                    ObjectiveId = objective.Id,
                    CaptainId = captain.Id,
                    Title = "Refine history coverage",
                    Status = ObjectiveRefinementSessionStatusEnum.Completed,
                    CreatedUtc = startedUtc.AddMinutes(-1),
                    StartedUtc = startedUtc,
                    CompletedUtc = startedUtc.AddMinutes(2),
                    LastUpdateUtc = startedUtc.AddMinutes(2)
                };
                await testDb.Driver.ObjectiveRefinementSessions.CreateAsync(session).ConfigureAwait(false);

                HistoricalTimelineService service = new HistoricalTimelineService(testDb.Driver);
                EnumerationResult<HistoricalTimelineEntry> result = await service.EnumerateAsync(auth, new HistoricalTimelineQuery
                {
                    PageNumber = 1,
                    PageSize = 25,
                    ObjectiveId = objective.Id,
                    SourceTypes = new List<string> { "ObjectiveRefinementSession" }
                }).ConfigureAwait(false);

                AssertEqual(1, result.Objects.Count);
                HistoricalTimelineEntry entry = result.Objects[0];
                AssertEqual("ObjectiveRefinementSession", entry.SourceType);
                AssertEqual(session.Id, entry.SourceId);
                AssertEqual(objective.Id, entry.ObjectiveId);
                AssertEqual("/backlog/" + objective.Id, entry.Route);
                AssertEqual(ObjectiveRefinementSessionStatusEnum.Completed.ToString(), entry.Status);
            }));

            cases.Add(CaseAsync("enumerate_reports_a_source_the_provider_does_not_store", "EnumerateAsync omits and names a source the database provider does not store", TestTags.Negative, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);

                string tenantId = "ten_history_unsupported";
                string userId = "usr_history_unsupported";
                await EnsureTenantAndUserAsync(testDb, tenantId, userId).ConfigureAwait(false);
                Voyage voyage = new Voyage
                {
                    TenantId = tenantId,
                    UserId = userId,
                    Title = "Voyage beside an unsupported source",
                    Status = VoyageStatusEnum.Open
                };
                await testDb.Driver.Voyages.CreateAsync(voyage).ConfigureAwait(false);

                LoggingModule logging = new LoggingModule();
                logging.Settings.EnableConsole = false;
                List<string> messages = new List<string>();
                object gate = new object();
                logging.MessageLogged += entry =>
                {
                    lock (gate) messages.Add(entry.Message ?? String.Empty);
                };

                // A provider that stores no planning sessions refuses every read.
                PropertyInfo planningSessions = typeof(Armada.Core.Database.DatabaseDriver).GetProperty(nameof(Armada.Core.Database.DatabaseDriver.PlanningSessions))!;
                object? original = planningSessions.GetValue(testDb.Driver);
                planningSessions.SetValue(testDb.Driver, new Test.Shared.Infrastructure.RefusingPlanningSessionMethods());
                EnumerationResult<HistoricalTimelineEntry> result;
                try
                {
                    HistoricalTimelineService service = new HistoricalTimelineService(testDb.Driver, logging);
                    AuthContext auth = AuthContext.Authenticated(tenantId, userId, true, true, "UnitTest");
                    result = await service.EnumerateAsync(auth, new HistoricalTimelineQuery { PageNumber = 1, PageSize = 50 }).ConfigureAwait(false);
                }
                finally
                {
                    planningSessions.SetValue(testDb.Driver, original);
                }

                HistoricalTimelineResult? timeline = result as HistoricalTimelineResult;
                AssertNotNull(timeline, "the timeline result carries its unavailable sources");
                AssertEqual("Planning", String.Join(",", timeline!.UnavailableSources), "the unsupported source is named");
                AssertTrue(timeline.Objects.Exists(entry => entry.SourceType == "Voyage" && entry.SourceId == voyage.Id), "the supported sources are still listed");
                List<string> logged;
                lock (gate) logged = new List<string>(messages);
                AssertTrue(
                    logged.Exists(message => message.Contains("Planning entries are not in the timeline", StringComparison.Ordinal)),
                    "the omitted source is logged: " + String.Join(" | ", logged));
            }));

            cases.Add(CaseAsync("enumerate_reads_only_the_newest_records_of_each_unbounded_source", "EnumerateAsync reads at most its per-source limit, newest first, and names each truncated source", TestTags.Positive, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);

                string tenantId = "ten_history_bound";
                string userId = "usr_history_bound";
                string workingDirectory = Path.Combine(Path.GetTempPath(), "armada-history-bound-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(workingDirectory);

                try
                {
                    await EnsureTenantAndUserAsync(testDb, tenantId, userId).ConfigureAwait(false);
                    AuthContext auth = AuthContext.Authenticated(tenantId, userId, true, true, "UnitTest");
                    Vessel vessel = CreateVessel(tenantId, userId, workingDirectory);
                    await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    // An incident is listed from its snapshot however many newer events the server holds.
                    Incident incident = await new IncidentService(testDb.Driver).CreateAsync(auth, new IncidentUpsertRequest
                    {
                        Title = "Incident older than the event limit",
                        Status = IncidentStatusEnum.Open,
                        Severity = IncidentSeverityEnum.High,
                        VesselId = vessel.Id
                    }).ConfigureAwait(false);

                    DateTime baseUtc = DateTime.UtcNow.AddHours(1);
                    List<string> missionIds = new List<string>();
                    List<string> eventIds = new List<string>();
                    List<string> requestIds = new List<string>();
                    List<string> checkIds = new List<string>();
                    for (int i = 0; i < 5; i++)
                    {
                        DateTime createdUtc = baseUtc.AddMinutes(i);
                        Mission mission = await testDb.Driver.Missions.CreateAsync(new Mission
                        {
                            TenantId = tenantId,
                            UserId = userId,
                            VesselId = vessel.Id,
                            Title = "Bounded mission " + i,
                            Status = MissionStatusEnum.Complete,
                            CreatedUtc = createdUtc,
                            LastUpdateUtc = createdUtc
                        }).ConfigureAwait(false);
                        missionIds.Add(mission.Id);

                        ArmadaEvent armadaEvent = await testDb.Driver.Events.CreateAsync(new ArmadaEvent("history.bound", "Bounded event " + i)
                        {
                            TenantId = tenantId,
                            UserId = userId,
                            CreatedUtc = createdUtc
                        }).ConfigureAwait(false);
                        eventIds.Add(armadaEvent.Id);

                        RequestHistoryEntry request = new RequestHistoryEntry
                        {
                            TenantId = tenantId,
                            UserId = userId,
                            Method = "GET",
                            Route = "/api/v1/status",
                            RouteTemplate = "/api/v1/status",
                            StatusCode = 200,
                            IsSuccess = true,
                            CreatedUtc = createdUtc
                        };
                        await testDb.Driver.RequestHistory.CreateAsync(request, null).ConfigureAwait(false);
                        requestIds.Add(request.Id);

                        if (i < 3)
                        {
                            CheckRun run = await testDb.Driver.CheckRuns.CreateAsync(new CheckRun
                            {
                                TenantId = tenantId,
                                UserId = userId,
                                VesselId = vessel.Id,
                                Type = CheckRunTypeEnum.Build,
                                Status = CheckRunStatusEnum.Passed,
                                Summary = "Bounded check " + i,
                                CreatedUtc = createdUtc
                            }).ConfigureAwait(false);
                            checkIds.Add(run.Id);
                        }
                    }

                    HistoricalTimelineService service = new HistoricalTimelineService(testDb.Driver, null, 3);
                    HistoricalTimelineResult result = await service.EnumerateAsync(auth, new HistoricalTimelineQuery
                    {
                        PageNumber = 1,
                        PageSize = 500
                    }).ConfigureAwait(false);

                    AssertEqual(3, service.SourceRowLimit);
                    AssertSourceHoldsNewest(result, "Mission", missionIds, 3);
                    AssertSourceHoldsNewest(result, "Event", eventIds, 3);
                    AssertSourceHoldsNewest(result, "Request", requestIds, 3);
                    AssertSourceHoldsNewest(result, "CheckRun", checkIds, 3);
                    AssertTrue(result.TruncatedSources.Contains("Mission"), "missions over the limit are named: " + String.Join(",", result.TruncatedSources));
                    AssertTrue(result.TruncatedSources.Contains("Event"), "events over the limit are named");
                    AssertTrue(result.TruncatedSources.Contains("Request"), "requests over the limit are named");
                    AssertFalse(result.TruncatedSources.Contains("CheckRun"), "a source holding exactly the limit is not named");
                    AssertTrue(result.Objects.Exists(entry => entry.SourceType == "Incident" && entry.SourceId == incident.Id),
                        "the incident is still listed from its snapshot");
                }
                finally
                {
                    TryDeleteDirectory(workingDirectory);
                }
            }));

            cases.Add(CaseAsync("enumerate_excludes_read_requests_before_the_source_bound", "EnumerateAsync leaves out GET, HEAD and OPTIONS requests in the database, so polling cannot fill the request source", TestTags.Positive, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);

                string tenantId = "ten_history_reads";
                string userId = "usr_history_reads";
                await EnsureTenantAndUserAsync(testDb, tenantId, userId).ConfigureAwait(false);
                AuthContext auth = AuthContext.Authenticated(tenantId, userId, true, true, "UnitTest");

                // Six read-only requests are newer than the two writes, and the source holds three records, so a
                // filter applied after the bounded read would find no write at all.
                DateTime baseUtc = DateTime.UtcNow.AddHours(-1);
                List<string> writeIds = new List<string>();
                string[] methods = { "POST", "DELETE", "GET", "HEAD", "OPTIONS", "get", "GET", "GET" };
                for (int i = 0; i < methods.Length; i++)
                {
                    RequestHistoryEntry request = new RequestHistoryEntry
                    {
                        TenantId = tenantId,
                        UserId = userId,
                        Method = methods[i],
                        Route = "/api/v1/status",
                        RouteTemplate = "/api/v1/status",
                        StatusCode = 200,
                        IsSuccess = true,
                        CreatedUtc = baseUtc.AddMinutes(i)
                    };
                    await testDb.Driver.RequestHistory.CreateAsync(request, null).ConfigureAwait(false);
                    if (i < 2) writeIds.Add(request.Id);
                }

                HistoricalTimelineService service = new HistoricalTimelineService(testDb.Driver, null, 3);
                HistoricalTimelineResult everything = await service.EnumerateAsync(auth, new HistoricalTimelineQuery
                {
                    PageNumber = 1,
                    PageSize = 500,
                    SourceTypes = new List<string> { "Request" }
                }).ConfigureAwait(false);
                AssertEqual(3, everything.Objects.Count, "without the flag the newest three requests are read");
                AssertTrue(everything.Objects.TrueForAll(entry => !writeIds.Contains(entry.SourceId)), "the newest requests are all reads");
                AssertTrue(everything.TruncatedSources.Contains("Request"), "the request source is cut at its limit");

                HistoricalTimelineResult writesOnly = await service.EnumerateAsync(auth, new HistoricalTimelineQuery
                {
                    PageNumber = 1,
                    PageSize = 500,
                    SourceTypes = new List<string> { "Request" },
                    ExcludeReadRequests = true
                }).ConfigureAwait(false);
                AssertEqual(2, (int)writesOnly.TotalRecords, "the total counts only the writes");
                AssertEqual(writeIds[1] + "," + writeIds[0], String.Join(",", writesOnly.Objects.Select(entry => entry.SourceId)), "only the two writes are listed, newest first");
                AssertFalse(writesOnly.TruncatedSources.Contains("Request"), "two writes are within the source limit, so the source is not cut");
            }));

            cases.Add(CaseAsync("enumerate_orders_oldest_first_when_asked", "EnumerateAsync lists newest first by default and oldest first on request, with a fixed order for equal times", TestTags.Positive, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);

                string tenantId = "ten_history_order";
                string userId = "usr_history_order";
                await EnsureTenantAndUserAsync(testDb, tenantId, userId).ConfigureAwait(false);
                AuthContext auth = AuthContext.Authenticated(tenantId, userId, true, true, "UnitTest");

                DateTime baseUtc = DateTime.UtcNow.AddHours(-1);
                DateTime tied = baseUtc.AddMinutes(5);
                List<RequestHistoryEntry> created = new List<RequestHistoryEntry>();
                DateTime[] times = { baseUtc, tied, baseUtc.AddMinutes(9), tied };
                foreach (DateTime createdUtc in times)
                {
                    RequestHistoryEntry request = new RequestHistoryEntry
                    {
                        TenantId = tenantId,
                        UserId = userId,
                        Method = "POST",
                        Route = "/api/v1/status",
                        RouteTemplate = "/api/v1/status",
                        StatusCode = 200,
                        IsSuccess = true,
                        CreatedUtc = createdUtc
                    };
                    await testDb.Driver.RequestHistory.CreateAsync(request, null).ConfigureAwait(false);
                    created.Add(request);
                }

                HistoricalTimelineService service = new HistoricalTimelineService(testDb.Driver);
                HistoricalTimelineQuery query = new HistoricalTimelineQuery { PageNumber = 1, PageSize = 500, SourceTypes = new List<string> { "Request" } };

                HistoricalTimelineResult newestFirst = await service.EnumerateAsync(auth, query).ConfigureAwait(false);
                List<string> descending = newestFirst.Objects.Select(entry => entry.SourceId).ToList();
                AssertEqual(created[2].Id, descending[0], "the newest request comes first by default");
                AssertEqual(created[0].Id, descending[3], "the oldest request comes last by default");

                query.Order = EnumerationOrderEnum.CreatedAscending;
                HistoricalTimelineResult oldestFirst = await service.EnumerateAsync(auth, query).ConfigureAwait(false);
                List<string> ascending = oldestFirst.Objects.Select(entry => entry.SourceId).ToList();
                AssertEqual(created[0].Id, ascending[0], "the oldest request comes first when asked");
                AssertEqual(created[2].Id, ascending[3], "the newest request comes last when asked");
                AssertEqual(String.Join(",", descending.AsEnumerable().Reverse()), String.Join(",", ascending), "the two orders are exact opposites, including the entries with equal times");
            }));

            // Audit addition: null auth is rejected (confirmed against source).

            cases.Add(CaseAsync("enumerate_null_auth_throws", "EnumerateAsync NullAuth Throws", TestTags.Negative, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                HistoricalTimelineService service = new HistoricalTimelineService(testDb.Driver);
                await AssertThrowsAsync<ArgumentNullException>(() => service.EnumerateAsync(null!, new HistoricalTimelineQuery()));
            }));

            // Audit addition: null query is rejected (confirmed against source).

            cases.Add(CaseAsync("enumerate_null_query_throws", "EnumerateAsync NullQuery Throws", TestTags.Negative, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                HistoricalTimelineService service = new HistoricalTimelineService(testDb.Driver);
                AuthContext auth = AuthContext.Authenticated("ten_hist_null", "usr_hist_null", false, true, "UnitTest");
                await AssertThrowsAsync<ArgumentNullException>(() => service.EnumerateAsync(auth, null!));
            }));

            return new TestSuiteDescriptor(
                suiteId: "Services.HistoricalTimelineService",
                displayName: "Historical Timeline Service",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        private static async Task EnsureTenantAndUserAsync(TestDatabase testDb, string tenantId, string userId)
        {
            TenantMetadata? existingTenant = await testDb.Driver.Tenants.ReadAsync(tenantId).ConfigureAwait(false);
            if (existingTenant == null)
            {
                await testDb.Driver.Tenants.CreateAsync(new TenantMetadata
                {
                    Id = tenantId,
                    Name = tenantId
                }).ConfigureAwait(false);
            }

            UserMaster? existingUser = await testDb.Driver.Users.ReadByIdAsync(userId).ConfigureAwait(false);
            if (existingUser == null)
            {
                await testDb.Driver.Users.CreateAsync(new UserMaster
                {
                    Id = userId,
                    TenantId = tenantId,
                    Email = userId + "@armada.test",
                    PasswordSha256 = UserMaster.ComputePasswordHash("password"),
                    IsTenantAdmin = true
                }).ConfigureAwait(false);
            }
        }

        private static Vessel CreateVessel(string tenantId, string userId, string workingDirectory)
        {
            return new Vessel
            {
                TenantId = tenantId,
                UserId = userId,
                Name = "History Vessel",
                RepoUrl = "file:///tmp/armada-history.git",
                LocalPath = workingDirectory,
                WorkingDirectory = workingDirectory,
                DefaultBranch = "main"
            };
        }

        private static void AssertSourceHoldsNewest(HistoricalTimelineResult result, string sourceType, List<string> idsOldestFirst, int expected)
        {
            List<string> listed = result.Objects
                .Where(entry => entry.SourceType == sourceType)
                .Select(entry => entry.SourceId)
                .ToList();
            List<string> newest = idsOldestFirst.Skip(Math.Max(0, idsOldestFirst.Count - expected)).ToList();
            AssertEqual(expected, listed.Count, sourceType + " entries listed");
            foreach (string id in newest)
                AssertTrue(listed.Contains(id), sourceType + " keeps its newest record " + id);
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, true);
            }
            catch
            {
            }
        }

        private static TestCaseDescriptor CaseAsync(string caseId, string displayName, string tag, Func<Task> body)
        {
            return new TestCaseDescriptor(
                suiteId: "Services.HistoricalTimelineService",
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) => body(),
                tags: new List<string> { tag });
        }

        #endregion
    }
}
