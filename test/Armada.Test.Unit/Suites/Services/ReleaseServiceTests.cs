namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// Unit coverage for first-class release drafting, derivation, and refresh flows.
    /// </summary>
    public class ReleaseServiceTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Release Service";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("CreateAsync derives version, missions, and artifacts from linked work", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                WorkflowProfileService workflowProfiles = new WorkflowProfileService(testDb.Driver, logging);
                ReleaseService releases = new ReleaseService(testDb.Driver, workflowProfiles, logging);

                string tenantId = "ten_release";
                string userId = "usr_release";
                string workingDirectory = Path.Combine(Path.GetTempPath(), "armada-release-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(workingDirectory);

                try
                {
                    await EnsureTenantAndUserAsync(testDb, tenantId, userId).ConfigureAwait(false);

                    Vessel vessel = CreateVessel(tenantId, userId, workingDirectory);
                    vessel.Name = "Release Vessel";
                    await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    WorkflowProfile profile = new WorkflowProfile
                    {
                        TenantId = tenantId,
                        UserId = userId,
                        Name = "Release Workflow",
                        Scope = WorkflowProfileScopeEnum.Vessel,
                        VesselId = vessel.Id,
                        ReleaseVersioningCommand = "echo 2.3.4"
                    };
                    await testDb.Driver.WorkflowProfiles.CreateAsync(profile).ConfigureAwait(false);

                    Voyage voyage = new Voyage
                    {
                        TenantId = tenantId,
                        UserId = userId,
                        Title = "Release Voyage",
                        Description = "Voyage for release drafting",
                        Status = VoyageStatusEnum.Open
                    };
                    await testDb.Driver.Voyages.CreateAsync(voyage).ConfigureAwait(false);

                    Mission mission = new Mission
                    {
                        TenantId = tenantId,
                        UserId = userId,
                        VesselId = vessel.Id,
                        VoyageId = voyage.Id,
                        Title = "Release Mission",
                        Description = "Mission in release",
                        Status = MissionStatusEnum.Complete,
                        PrUrl = "https://example.test/pr/123"
                    };
                    await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                    CheckRun checkRun = new CheckRun
                    {
                        TenantId = tenantId,
                        UserId = userId,
                        WorkflowProfileId = profile.Id,
                        VesselId = vessel.Id,
                        MissionId = mission.Id,
                        VoyageId = voyage.Id,
                        Type = CheckRunTypeEnum.ReleaseVersioning,
                        Status = CheckRunStatusEnum.Passed,
                        Command = "echo 2.3.4",
                        Summary = "Release candidate 2.3.4",
                        Artifacts = new List<CheckRunArtifact>
                        {
                            new CheckRunArtifact
                            {
                                Path = "artifacts/app.zip",
                                SizeBytes = 1024,
                                LastWriteUtc = DateTime.UtcNow
                            }
                        }
                    };
                    await testDb.Driver.CheckRuns.CreateAsync(checkRun).ConfigureAwait(false);

                    AuthContext auth = AuthContext.Authenticated(tenantId, userId, false, false, "UnitTest");
                    Release release = await releases.CreateAsync(auth, new ReleaseUpsertRequest
                    {
                        WorkflowProfileId = profile.Id,
                        VoyageIds = new List<string> { voyage.Id },
                        CheckRunIds = new List<string> { checkRun.Id },
                        Status = ReleaseStatusEnum.Candidate
                    }).ConfigureAwait(false);

                    AssertStartsWith("rel_", release.Id);
                    AssertEqual(vessel.Id, release.VesselId);
                    AssertEqual(profile.Id, release.WorkflowProfileId);
                    AssertEqual("2.3.4", release.Version);
                    AssertEqual("v2.3.4", release.TagName);
                    AssertEqual(ReleaseStatusEnum.Candidate, release.Status);
                    AssertTrue(release.MissionIds.Exists(id => id == mission.Id), "Expected mission linked through voyage.");
                    AssertTrue(release.Artifacts.Count == 1, "Expected one derived artifact.");
                    AssertEqual("artifacts/app.zip", release.Artifacts[0].Path);
                    AssertContains(vessel.Name, release.Summary ?? String.Empty);
                    AssertContains(mission.Id, release.Notes ?? String.Empty);
                }
                finally
                {
                    TryDeleteDirectory(workingDirectory);
                }
            }).ConfigureAwait(false);

            await RunTest("CreateAsync bumps prior semantic version when no explicit version exists", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                WorkflowProfileService workflowProfiles = new WorkflowProfileService(testDb.Driver, logging);
                ReleaseService releases = new ReleaseService(testDb.Driver, workflowProfiles, logging);

                string tenantId = "ten_release_bump";
                string userId = "usr_release_bump";
                string workingDirectory = Path.Combine(Path.GetTempPath(), "armada-release-bump-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(workingDirectory);

                try
                {
                    await EnsureTenantAndUserAsync(testDb, tenantId, userId).ConfigureAwait(false);

                    Vessel vessel = CreateVessel(tenantId, userId, workingDirectory);
                    vessel.Name = "Version Vessel";
                    await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    await testDb.Driver.Releases.CreateAsync(new Release
                    {
                        TenantId = tenantId,
                        UserId = userId,
                        VesselId = vessel.Id,
                        Title = "Previous Release",
                        Version = "1.4.2",
                        TagName = "v1.4.2",
                        Status = ReleaseStatusEnum.Shipped
                    }).ConfigureAwait(false);

                    AuthContext auth = AuthContext.Authenticated(tenantId, userId, false, false, "UnitTest");
                    Release release = await releases.CreateAsync(auth, new ReleaseUpsertRequest
                    {
                        VesselId = vessel.Id,
                        Title = "Next Release"
                    }).ConfigureAwait(false);

                    AssertEqual("1.4.3", release.Version);
                    AssertEqual("v1.4.3", release.TagName);
                }
                finally
                {
                    TryDeleteDirectory(workingDirectory);
                }
            }).ConfigureAwait(false);

            await RunTest("RefreshAsync rebuilds derived artifacts from linked checks", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                WorkflowProfileService workflowProfiles = new WorkflowProfileService(testDb.Driver, logging);
                ReleaseService releases = new ReleaseService(testDb.Driver, workflowProfiles, logging);

                string tenantId = "ten_release_refresh";
                string userId = "usr_release_refresh";
                string workingDirectory = Path.Combine(Path.GetTempPath(), "armada-release-refresh-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(workingDirectory);

                try
                {
                    await EnsureTenantAndUserAsync(testDb, tenantId, userId).ConfigureAwait(false);

                    Vessel vessel = CreateVessel(tenantId, userId, workingDirectory);
                    await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    CheckRun checkRun = new CheckRun
                    {
                        TenantId = tenantId,
                        UserId = userId,
                        VesselId = vessel.Id,
                        Type = CheckRunTypeEnum.PublishArtifact,
                        Status = CheckRunStatusEnum.Passed,
                        Command = "echo publish",
                        Summary = "Published artifacts",
                        Artifacts = new List<CheckRunArtifact>
                        {
                            new CheckRunArtifact
                            {
                                Path = "artifacts/one.zip",
                                SizeBytes = 128,
                                LastWriteUtc = DateTime.UtcNow
                            }
                        }
                    };
                    await testDb.Driver.CheckRuns.CreateAsync(checkRun).ConfigureAwait(false);

                    AuthContext auth = AuthContext.Authenticated(tenantId, userId, false, false, "UnitTest");
                    Release release = await releases.CreateAsync(auth, new ReleaseUpsertRequest
                    {
                        VesselId = vessel.Id,
                        Title = "Refreshable Release",
                        CheckRunIds = new List<string> { checkRun.Id }
                    }).ConfigureAwait(false);
                    AssertEqual(1, release.Artifacts.Count);

                    checkRun.Artifacts.Add(new CheckRunArtifact
                    {
                        Path = "artifacts/two.zip",
                        SizeBytes = 256,
                        LastWriteUtc = DateTime.UtcNow
                    });
                    await testDb.Driver.CheckRuns.UpdateAsync(checkRun).ConfigureAwait(false);

                    Release refreshed = await releases.RefreshAsync(auth, release.Id).ConfigureAwait(false);
                    AssertEqual(2, refreshed.Artifacts.Count);
                    AssertEqual("Refreshable Release", refreshed.Title);
                }
                finally
                {
                    TryDeleteDirectory(workingDirectory);
                }
            }).ConfigureAwait(false);

            await RunTest("UpdateAsync Candidate to Shipped dispatches webhook exactly once", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                WorkflowProfileService workflowProfiles = new WorkflowProfileService(testDb.Driver, logging);
                RecordingWebhookDispatcher webhooks = new RecordingWebhookDispatcher();
                ReleaseService releases = new ReleaseService(testDb.Driver, workflowProfiles, logging, webhooks);

                string tenantId = "ten_release_webhook";
                string userId = "usr_release_webhook";
                string workingDirectory = Path.Combine(Path.GetTempPath(), "armada-release-webhook-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(workingDirectory);

                try
                {
                    await EnsureTenantAndUserAsync(testDb, tenantId, userId).ConfigureAwait(false);

                    Vessel vessel = CreateVessel(tenantId, userId, workingDirectory);
                    await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    AuthContext auth = AuthContext.Authenticated(tenantId, userId, false, false, "UnitTest");
                    Release release = await releases.CreateAsync(auth, new ReleaseUpsertRequest
                    {
                        VesselId = vessel.Id,
                        Title = "Approved Release",
                        Status = ReleaseStatusEnum.Candidate
                    }).ConfigureAwait(false);
                    AssertEqual(0, webhooks.Payloads.Count);

                    Release shipped = await releases.UpdateAsync(auth, release.Id, new ReleaseUpsertRequest
                    {
                        Status = ReleaseStatusEnum.Shipped
                    }).ConfigureAwait(false);

                    AssertEqual(1, webhooks.Payloads.Count);
                    AssertEqual(release.Id, webhooks.Payloads[0].ReleaseId);
                    AssertEqual(vessel.Id, webhooks.Payloads[0].VesselId);
                    AssertEqual("release.shipped", webhooks.Payloads[0].Event);
                    AssertEqual(ReleaseStatusEnum.Shipped, shipped.Status);
                    AssertNotNull(shipped.PublishedUtc);

                    Release reshipped = await releases.UpdateAsync(auth, release.Id, new ReleaseUpsertRequest
                    {
                        Status = ReleaseStatusEnum.Shipped,
                        Summary = "Touch while already shipped"
                    }).ConfigureAwait(false);

                    AssertEqual(ReleaseStatusEnum.Shipped, reshipped.Status);
                    AssertEqual(1, webhooks.Payloads.Count);
                }
                finally
                {
                    TryDeleteDirectory(workingDirectory);
                }
            }).ConfigureAwait(false);

            await RunTest("CreateAsync created directly as Shipped dispatches webhook", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                WorkflowProfileService workflowProfiles = new WorkflowProfileService(testDb.Driver, logging);
                RecordingWebhookDispatcher webhooks = new RecordingWebhookDispatcher();
                ReleaseService releases = new ReleaseService(testDb.Driver, workflowProfiles, logging, webhooks);

                string tenantId = "ten_release_whcreate";
                string userId = "usr_release_whcreate";
                string workingDirectory = Path.Combine(Path.GetTempPath(), "armada-release-whcreate-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(workingDirectory);

                try
                {
                    await EnsureTenantAndUserAsync(testDb, tenantId, userId).ConfigureAwait(false);

                    Vessel vessel = CreateVessel(tenantId, userId, workingDirectory);
                    await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    AuthContext auth = AuthContext.Authenticated(tenantId, userId, false, false, "UnitTest");
                    Release release = await releases.CreateAsync(auth, new ReleaseUpsertRequest
                    {
                        VesselId = vessel.Id,
                        Title = "Direct Shipped",
                        Status = ReleaseStatusEnum.Shipped
                    }).ConfigureAwait(false);

                    AssertEqual(1, webhooks.Payloads.Count);
                    AssertEqual(release.Id, webhooks.Payloads[0].ReleaseId);
                }
                finally
                {
                    TryDeleteDirectory(workingDirectory);
                }
            }).ConfigureAwait(false);

            await RunTest("UpdateAsync webhook failure does not fail the release update", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                WorkflowProfileService workflowProfiles = new WorkflowProfileService(testDb.Driver, logging);
                FailingWebhookDispatcher webhooks = new FailingWebhookDispatcher();
                ReleaseService releases = new ReleaseService(testDb.Driver, workflowProfiles, logging, webhooks);

                string tenantId = "ten_release_whfail";
                string userId = "usr_release_whfail";
                string workingDirectory = Path.Combine(Path.GetTempPath(), "armada-release-whfail-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(workingDirectory);

                try
                {
                    await EnsureTenantAndUserAsync(testDb, tenantId, userId).ConfigureAwait(false);

                    Vessel vessel = CreateVessel(tenantId, userId, workingDirectory);
                    await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    AuthContext auth = AuthContext.Authenticated(tenantId, userId, false, false, "UnitTest");
                    Release release = await releases.CreateAsync(auth, new ReleaseUpsertRequest
                    {
                        VesselId = vessel.Id,
                        Title = "Failing Webhook Release",
                        Status = ReleaseStatusEnum.Candidate
                    }).ConfigureAwait(false);

                    Release shipped = await releases.UpdateAsync(auth, release.Id, new ReleaseUpsertRequest
                    {
                        Status = ReleaseStatusEnum.Shipped
                    }).ConfigureAwait(false);

                    AssertEqual(ReleaseStatusEnum.Shipped, shipped.Status);
                    AssertEqual(1, webhooks.Attempts);

                    EnumerationResult<ArmadaEvent> evtResult = await testDb.Driver.Events.EnumerateAsync(new EnumerationQuery()).ConfigureAwait(false);
                    List<ArmadaEvent> events = evtResult.Objects;
                    AssertTrue(events.Exists(evt => evt.EventType == "release.webhook.failed" && evt.EntityId == release.Id),
                        "Expected a release.webhook.failed event recorded.");
                }
                finally
                {
                    TryDeleteDirectory(workingDirectory);
                }
            }).ConfigureAwait(false);
            await RunTest("ListWebhookEventsAsync returns delivery events within caller scope", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                WorkflowProfileService workflowProfiles = new WorkflowProfileService(testDb.Driver, logging);
                RecordingWebhookDispatcher webhooks = new RecordingWebhookDispatcher();
                ReleaseService releases = new ReleaseService(testDb.Driver, workflowProfiles, logging, webhooks);

                string tenantId = "ten_release_whlist";
                string userId = "usr_release_whlist";
                string workingDirectory = Path.Combine(Path.GetTempPath(), "armada-release-whlist-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(workingDirectory);

                try
                {
                    await EnsureTenantAndUserAsync(testDb, tenantId, userId).ConfigureAwait(false);

                    Vessel vessel = CreateVessel(tenantId, userId, workingDirectory);
                    await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    AuthContext auth = AuthContext.Authenticated(tenantId, userId, false, false, "UnitTest");
                    Release release = await releases.CreateAsync(auth, new ReleaseUpsertRequest
                    {
                        VesselId = vessel.Id,
                        Title = "Listed Webhook Release",
                        Status = ReleaseStatusEnum.Candidate
                    }).ConfigureAwait(false);
                    await releases.UpdateAsync(auth, release.Id, new ReleaseUpsertRequest
                    {
                        Status = ReleaseStatusEnum.Shipped
                    }).ConfigureAwait(false);

                    List<ArmadaEvent> events = await releases.ListWebhookEventsAsync(auth, release.Id).ConfigureAwait(false);

                    AssertEqual(1, events.Count);
                    AssertEqual(release.Id, events[0].EntityId);
                    AssertEqual("release", events[0].EntityType);
                    AssertEqual("release.webhook.delivered", events[0].EventType);

                    AssertThrows<InvalidOperationException>(
                        () => releases.ListWebhookEventsAsync(auth, "rel_does_not_exist").GetAwaiter().GetResult());
                }
                finally
                {
                    TryDeleteDirectory(workingDirectory);
                }
            }).ConfigureAwait(false);
        }

        private static LoggingModule CreateLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

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
                Name = "Release Workflow Vessel",
                RepoUrl = "file:///tmp/armada-release.git",
                LocalPath = workingDirectory,
                WorkingDirectory = workingDirectory,
                DefaultBranch = "main"
            };
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

        private sealed class RecordingWebhookDispatcher : IReleaseWebhookDispatcher
        {
            public List<ReleaseWebhookPayload> Payloads { get; } = new List<ReleaseWebhookPayload>();

            public Task<WebhookDispatchResult> DispatchAsync(ReleaseWebhookPayload payload, CancellationToken token = default)
            {
                Payloads.Add(payload);
                return Task.FromResult(new WebhookDispatchResult
                {
                    Outcome = WebhookDispatchOutcome.Success,
                    StatusCode = 200
                });
            }
        }

        private sealed class FailingWebhookDispatcher : IReleaseWebhookDispatcher
        {
            public int Attempts { get; private set; }

            public Task<WebhookDispatchResult> DispatchAsync(ReleaseWebhookPayload payload, CancellationToken token = default)
            {
                Attempts += 1;
                return Task.FromResult(new WebhookDispatchResult
                {
                    Outcome = WebhookDispatchOutcome.RetriableFailure,
                    ErrorMessage = "simulated network error"
                });
            }
        }
    }
}
