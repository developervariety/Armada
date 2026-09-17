namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Net.Sockets;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Server;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// Bounded objective admission waits, durable dispatch attempt records and crash reconciliation.
    /// </summary>
    public class ObjectiveDispatchAttemptTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Objective Dispatch Attempts";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("A busy admission returns a bounded retryable result instead of waiting for cancellation", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                AuthContext auth = Auth();
                Objective objective = await CreateObjectiveAsync(testDb, "Busy admission").ConfigureAwait(false);
                ObjectiveService holder = new ObjectiveService(testDb.Driver);
                ObjectiveService contender = new ObjectiveService(testDb.Driver, dispatchAdmissionWait: TimeSpan.FromMilliseconds(200));

                await using (ObjectiveDispatchAdmission held = await holder.AcquireDispatchAdmissionAsync(auth, objective.Id).ConfigureAwait(false))
                {
                    using (CancellationTokenSource cancel = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                    {
                        Stopwatch watch = Stopwatch.StartNew();
                        Exception? failure = null;
                        try
                        {
                            await contender.AcquireDispatchAdmissionAsync(auth, objective.Id, cancel.Token).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            failure = ex;
                        }

                        watch.Stop();
                        AssertTrue(failure is ObjectiveDispatchBusyException,
                            "A busy admission must surface as a retryable busy result, got " + (failure?.GetType().Name ?? "success") + ".");
                        AssertTrue(watch.Elapsed < TimeSpan.FromSeconds(3), "The busy result must arrive within the bounded wait.");
                        ObjectiveDispatchBusyException busy = (ObjectiveDispatchBusyException)failure!;
                        AssertEqual(objective.Id, busy.ObjectiveId);
                        AssertTrue(busy.RetryAfter > TimeSpan.Zero, "The busy result must suggest a retry delay.");
                    }
                }

                await using (ObjectiveDispatchAdmission retried = await contender.AcquireDispatchAdmissionAsync(auth, objective.Id).ConfigureAwait(false))
                {
                    AssertEqual(objective.Id, retried.Objective.Id, "A retry after release must be admitted.");
                }
            });

            await RunTest("The running server's health loop reconciles a crashed dispatch attempt", async () =>
            {
                string tempDir = Path.Combine(Path.GetTempPath(), "armada_attempt_health_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDir);
                DatabaseSettings dbSettings = new DatabaseSettings
                {
                    Type = DatabaseTypeEnum.Sqlite,
                    Filename = Path.Combine(tempDir, "armada.db")
                };
                ArmadaSettings settings = new ArmadaSettings
                {
                    DataDirectory = tempDir,
                    DatabasePath = dbSettings.Filename,
                    Database = dbSettings,
                    LogDirectory = Path.Combine(tempDir, "logs"),
                    DocksDirectory = Path.Combine(tempDir, "docks"),
                    ReposDirectory = Path.Combine(tempDir, "repos"),
                    AdmiralPort = FreePort(),
                    McpPort = FreePort(),
                    ApiKey = "test-key-" + Guid.NewGuid().ToString("N"),
                    HeartbeatIntervalSeconds = 5
                };
                settings.Rest.Hostname = "127.0.0.1";
                settings.AutonomousObjectiveScheduler.Enabled = false;
                // Bind this server's settings to its own temp file: the server watches its settings file,
                // so the machine-wide default would let the host operator's live settings reach this test.
                settings.SettingsFilePath = Path.Combine(tempDir, "settings.json");
                settings.InitializeDirectories();
                SyslogLogging.LoggingModule logging = new SyslogLogging.LoggingModule();
                logging.Settings.EnableConsole = false;

                ArmadaServer server = new ArmadaServer(logging, settings, quiet: true);
                // The reconciliation runs on a health loop tick, not at startup, so a short tick keeps the test
                // from waiting out the five-second settings floor.
                server.HealthLoopInterval = TimeSpan.FromMilliseconds(200);
                try
                {
                    await server.StartAsync().ConfigureAwait(false);
                    using (DatabaseDriver driver = await DatabaseDriverFactory.CreateAndInitializeAsync(dbSettings).ConfigureAwait(false))
                    {
                        AuthContext auth = Auth();
                        Vessel vessel = await driver.Vessels.CreateAsync(new Vessel("health-loop-attempt", "https://github.com/test/health-loop-attempt.git")
                        {
                            TenantId = auth.TenantId,
                            UserId = auth.UserId
                        }).ConfigureAwait(false);
                        Objective objective = await driver.Objectives.CreateAsync(new Objective
                        {
                            TenantId = auth.TenantId,
                            UserId = auth.UserId,
                            Title = "Health loop attempt",
                            Status = ObjectiveStatusEnum.Planned,
                            VesselIds = new List<string> { vessel.Id }
                        }).ConfigureAwait(false);
                        ObjectiveService crashed = new ObjectiveService(driver, dispatchAdmissionTtl: TimeSpan.FromMilliseconds(100));
                        ObjectiveDispatchAdmission admission = await crashed.AcquireDispatchAdmissionAsync(
                            auth, new[] { objective.Id }, Descriptor("Health loop attempt", vessel.Id)).ConfigureAwait(false);
                        Voyage orphan = await driver.Voyages.CreateAsync(new Voyage("Health loop attempt")
                        {
                            TenantId = auth.TenantId,
                            UserId = auth.UserId,
                            Status = VoyageStatusEnum.Open
                        }).ConfigureAwait(false);
                        await admission.RecordVoyageCreatedAsync(orphan).ConfigureAwait(false);
                        await admission.AbandonAsCrashedAsync().ConfigureAwait(false);

                        VoyageStatusEnum status = VoyageStatusEnum.Open;
                        DateTime deadline = DateTime.UtcNow.AddSeconds(40);
                        while (DateTime.UtcNow < deadline)
                        {
                            status = (await driver.Voyages.ReadAsync(orphan.Id).ConfigureAwait(false))!.Status;
                            if (status == VoyageStatusEnum.Cancelled) break;
                            await Task.Delay(100).ConfigureAwait(false);
                        }

                        AssertEqual(VoyageStatusEnum.Cancelled, status,
                            "The server's periodic health loop must reconcile the crashed attempt created after startup.");
                        List<ArmadaEvent> closed = await driver.Events
                            .EnumerateByEntityAsync(ObjectiveDispatchAdmission.AttemptEntityType, admission.AttemptId).ConfigureAwait(false);
                        AssertTrue(closed.Any(evt => evt.EventType == ObjectiveDispatchAdmission.ClosedEventType),
                            "The health loop must close the reconciled attempt.");
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

            await RunTest("Retention purge never deletes attempt records inside the reconciliation look-back", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                DateTime now = DateTime.UtcNow;
                async Task<ArmadaEvent> SeedAsync(string eventType, string? entityType, DateTime createdUtc)
                {
                    return await testDb.Driver.Events.CreateAsync(new ArmadaEvent(eventType, eventType)
                    {
                        EntityType = entityType,
                        EntityId = entityType == null ? null : "entity-" + Guid.NewGuid().ToString("N"),
                        CreatedUtc = createdUtc
                    }).ConfigureAwait(false);
                }

                ArmadaEvent openInsideLookBack = await SeedAsync(ObjectiveDispatchAdmission.StartedEventType, ObjectiveDispatchAdmission.AttemptEntityType, now.AddDays(-3)).ConfigureAwait(false);
                ArmadaEvent voyageInsideLookBack = await SeedAsync(ObjectiveDispatchAdmission.VoyageCreatedEventType, ObjectiveDispatchAdmission.AttemptEntityType, now.AddDays(-3)).ConfigureAwait(false);
                ArmadaEvent attemptBeyondLookBack = await SeedAsync(ObjectiveDispatchAdmission.StartedEventType, ObjectiveDispatchAdmission.AttemptEntityType,
                    now - ObjectiveDispatchAdmission.ReconciliationLookBack - TimeSpan.FromDays(1)).ConfigureAwait(false);
                ArmadaEvent oldUntyped = await SeedAsync("mission.created", null, now.AddDays(-3)).ConfigureAwait(false);
                ArmadaEvent oldOtherEntity = await SeedAsync("incident.snapshot", "incident", now.AddDays(-3)).ConfigureAwait(false);
                ArmadaEvent recent = await SeedAsync("mission.created", null, now).ConfigureAwait(false);

                SyslogLogging.LoggingModule logging = new SyslogLogging.LoggingModule();
                logging.Settings.EnableConsole = false;
                await new DataExpiryService(logging, testDb.Driver, 1, 0).PurgeExpiredDataAsync().ConfigureAwait(false);

                AssertNotNull(await testDb.Driver.Events.ReadAsync(openInsideLookBack.Id).ConfigureAwait(false),
                    "An attempt record inside the look-back must survive a shorter retention period.");
                AssertNotNull(await testDb.Driver.Events.ReadAsync(voyageInsideLookBack.Id).ConfigureAwait(false),
                    "The attempt's voyage record must survive with it.");
                AssertNull(await testDb.Driver.Events.ReadAsync(attemptBeyondLookBack.Id).ConfigureAwait(false),
                    "An attempt record older than the look-back follows normal retention.");
                AssertNull(await testDb.Driver.Events.ReadAsync(oldUntyped.Id).ConfigureAwait(false),
                    "An expired event without an entity type is still purged.");
                AssertNull(await testDb.Driver.Events.ReadAsync(oldOtherEntity.Id).ConfigureAwait(false),
                    "An expired event of another entity type is still purged.");
                AssertNotNull(await testDb.Driver.Events.ReadAsync(recent.Id).ConfigureAwait(false));
            });

            await RunTest("Admission takes objective leases in one stable order whatever the request order", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                AuthContext auth = Auth();
                Objective first = await CreateObjectiveAsync(testDb, "Order one").ConfigureAwait(false);
                Objective second = await CreateObjectiveAsync(testDb, "Order two").ConfigureAwait(false);
                ObjectiveService objectives = new ObjectiveService(testDb.Driver);
                List<string> forward;
                List<string> reverse;

                await using (ObjectiveDispatchAdmission admission = await objectives.AcquireDispatchAdmissionAsync(
                    auth, new[] { first.Id, second.Id }, null).ConfigureAwait(false))
                {
                    forward = admission.Objectives.Select(item => item.Id).ToList();
                }
                await using (ObjectiveDispatchAdmission admission = await objectives.AcquireDispatchAdmissionAsync(
                    auth, new[] { second.Id, first.Id }, null).ConfigureAwait(false))
                {
                    reverse = admission.Objectives.Select(item => item.Id).ToList();
                }

                AssertEqual(2, forward.Count);
                AssertTrue(forward.SequenceEqual(reverse), "Both request orders must acquire in the same lease order.");
                List<string> expected = new[] { first.Id, second.Id }
                    .OrderBy(id => ObjectiveService.BuildDispatchAdmissionLeaseName(auth.TenantId, id), StringComparer.Ordinal)
                    .ToList();
                AssertTrue(forward.SequenceEqual(expected), "The order is the ordinal lease-name order.");
            });

            await RunTest("A busy later lease releases the earlier lease so a waiting dispatch never holds another objective", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                AuthContext auth = Auth();
                Objective x = await CreateObjectiveAsync(testDb, "Hold and wait x").ConfigureAwait(false);
                Objective y = await CreateObjectiveAsync(testDb, "Hold and wait y").ConfigureAwait(false);
                List<Objective> ordered = new[] { x, y }
                    .OrderBy(item => ObjectiveService.BuildDispatchAdmissionLeaseName(auth.TenantId, item.Id), StringComparer.Ordinal)
                    .ToList();
                string earlierLease = ObjectiveService.BuildDispatchAdmissionLeaseName(auth.TenantId, ordered[0].Id);
                string laterLease = ObjectiveService.BuildDispatchAdmissionLeaseName(auth.TenantId, ordered[1].Id);
                AssertTrue(await testDb.Driver.CoordinationLeases.TryAcquireAsync(
                    laterLease, "other-dispatch", TimeSpan.FromMinutes(1), auth.TenantId).ConfigureAwait(false));

                ObjectiveService contender = new ObjectiveService(testDb.Driver, dispatchAdmissionWait: TimeSpan.FromMilliseconds(200));
                ObjectiveDispatchBusyException? busy = null;
                try
                {
                    await contender.AcquireDispatchAdmissionAsync(auth, new[] { ordered[1].Id, ordered[0].Id }, null).ConfigureAwait(false);
                }
                catch (ObjectiveDispatchBusyException ex)
                {
                    busy = ex;
                }

                AssertNotNull(busy, "The busy later objective must refuse the whole admission.");
                AssertEqual(ordered[1].Id, busy!.ObjectiveId);
                AssertNull(await testDb.Driver.CoordinationLeases.ReadAsync(earlierLease).ConfigureAwait(false),
                    "The earlier lease must be released when the admission gives up.");
            });

            await RunTest("A dispatch that lost its lease cannot link the objective", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                AuthContext auth = Auth();
                Objective objective = await CreateObjectiveAsync(testDb, "Fenced link").ConfigureAwait(false);
                ObjectiveService objectives = new ObjectiveService(testDb.Driver);
                string leaseName = ObjectiveService.BuildDispatchAdmissionLeaseName(auth.TenantId, objective.Id);

                await using (ObjectiveDispatchAdmission admission = await objectives.AcquireDispatchAdmissionAsync(auth, objective.Id).ConfigureAwait(false))
                {
                    await testDb.Driver.CoordinationLeases.ReleaseAsync(leaseName, admission.AttemptId).ConfigureAwait(false);
                    AssertTrue(await testDb.Driver.CoordinationLeases.TryAcquireAsync(
                        leaseName, "takeover-dispatch", TimeSpan.FromMinutes(1), auth.TenantId).ConfigureAwait(false));
                    Voyage voyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("Fenced voyage")
                    {
                        TenantId = auth.TenantId,
                        UserId = auth.UserId,
                        Status = VoyageStatusEnum.Open
                    }).ConfigureAwait(false);

                    ObjectiveDispatchOwnershipLostException? lost = null;
                    try
                    {
                        await objectives.LinkVoyageAsync(auth, objective.Id, voyage.Id, default, false, admission).ConfigureAwait(false);
                    }
                    catch (ObjectiveDispatchOwnershipLostException ex)
                    {
                        lost = ex;
                    }

                    AssertNotNull(lost, "Linking must re-confirm lease ownership before the objective write.");
                    Objective stored = (await testDb.Driver.Objectives.ReadAsync(objective.Id).ConfigureAwait(false))!;
                    AssertEqual(0, stored.VoyageIds.Count, "A holder without its lease must not link.");
                    CoordinationLease lease = (await testDb.Driver.CoordinationLeases.ReadAsync(leaseName).ConfigureAwait(false))!;
                    AssertEqual("takeover-dispatch", lease.Holder, "The fenced holder must not disturb the new owner.");
                }

                CoordinationLease after = (await testDb.Driver.CoordinationLeases.ReadAsync(leaseName).ConfigureAwait(false))!;
                AssertEqual("takeover-dispatch", after.Holder, "Disposing the fenced admission must not release the new owner's lease.");
            });

            await RunTest("An expired lease from a stopped holder is taken over within the bounded wait", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                AuthContext auth = Auth();
                Objective objective = await CreateObjectiveAsync(testDb, "Expired holder").ConfigureAwait(false);
                string leaseName = ObjectiveService.BuildDispatchAdmissionLeaseName(auth.TenantId, objective.Id);
                AssertTrue(await testDb.Driver.CoordinationLeases.TryAcquireAsync(
                    leaseName, "stopped-holder", TimeSpan.FromMilliseconds(150), auth.TenantId).ConfigureAwait(false));

                ObjectiveService contender = new ObjectiveService(testDb.Driver, dispatchAdmissionWait: TimeSpan.FromSeconds(3));
                await using (ObjectiveDispatchAdmission admitted = await contender.AcquireDispatchAdmissionAsync(auth, objective.Id).ConfigureAwait(false))
                {
                    CoordinationLease lease = (await testDb.Driver.CoordinationLeases.ReadAsync(leaseName).ConfigureAwait(false))!;
                    AssertFalse(lease.Holder == "stopped-holder", "The expired holder must no longer own the admission.");
                }
            });

            await RunTest("A crash after voyage creation and before linking is reconciled by cancelling the orphan", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                AuthContext auth = Auth();
                Vessel vessel = await CreateVesselAsync(testDb, "crash-after-create").ConfigureAwait(false);
                Objective objective = await CreateObjectiveAsync(testDb, "Crash after create", vessel.Id).ConfigureAwait(false);
                ObjectiveService crashed = new ObjectiveService(testDb.Driver, dispatchAdmissionTtl: TimeSpan.FromMilliseconds(100));

                ObjectiveDispatchAdmission admission = await crashed.AcquireDispatchAdmissionAsync(
                    auth, new[] { objective.Id }, Descriptor("Crash after create", vessel.Id)).ConfigureAwait(false);
                Voyage orphan = await CreateVoyageWithMissionAsync(testDb, "Crash after create", vessel.Id).ConfigureAwait(false);
                await admission.RecordVoyageCreatedAsync(orphan).ConfigureAwait(false);
                await admission.AbandonAsCrashedAsync().ConfigureAwait(false);
                await Task.Delay(300).ConfigureAwait(false);

                ObjectiveService reconciler = new ObjectiveService(testDb.Driver);
                ObjectiveDispatchAttemptReconciliationResult result = await reconciler.ReconcileDispatchAttemptsAsync().ConfigureAwait(false);

                AssertEqual(1, result.CancelledOrphans, "The crashed attempt's unlinked voyage must be cancelled.");
                Voyage stored = (await testDb.Driver.Voyages.ReadAsync(orphan.Id).ConfigureAwait(false))!;
                AssertEqual(VoyageStatusEnum.Cancelled, stored.Status);
                List<Mission> missions = await testDb.Driver.Missions.EnumerateByVoyageAsync(orphan.Id).ConfigureAwait(false);
                AssertTrue(missions.All(mission => mission.Status == MissionStatusEnum.Cancelled), "No orphan mission may stay active.");

                ObjectiveDispatchAttemptReconciliationResult again = await reconciler.ReconcileDispatchAttemptsAsync().ConfigureAwait(false);
                AssertEqual(0, again.Examined, "A reconciled attempt is closed and never examined again.");

                await using (ObjectiveDispatchAdmission retry = await reconciler.AcquireDispatchAdmissionAsync(auth, objective.Id).ConfigureAwait(false))
                {
                    AssertEqual(objective.Id, retry.Objective.Id, "Reconciliation releases admission for a retry.");
                }
            });

            await RunTest("A crash before the voyage id is recorded is matched by the attempt descriptor and reconciled", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                AuthContext auth = Auth();
                Vessel vessel = await CreateVesselAsync(testDb, "crash-during-create").ConfigureAwait(false);
                Objective objective = await CreateObjectiveAsync(testDb, "Crash during create", vessel.Id).ConfigureAwait(false);
                ObjectiveService crashed = new ObjectiveService(testDb.Driver, dispatchAdmissionTtl: TimeSpan.FromMilliseconds(100));

                ObjectiveDispatchAdmission admission = await crashed.AcquireDispatchAdmissionAsync(
                    auth, new[] { objective.Id }, Descriptor("Crash during create", vessel.Id)).ConfigureAwait(false);
                Voyage orphan = await CreateVoyageWithMissionAsync(testDb, "Crash during create", vessel.Id).ConfigureAwait(false);
                Voyage unrelated = await CreateVoyageWithMissionAsync(testDb, "Unrelated operator voyage", vessel.Id).ConfigureAwait(false);
                await admission.AbandonAsCrashedAsync().ConfigureAwait(false);
                await Task.Delay(300).ConfigureAwait(false);

                ObjectiveDispatchAttemptReconciliationResult result = await new ObjectiveService(testDb.Driver)
                    .ReconcileDispatchAttemptsAsync().ConfigureAwait(false);

                AssertEqual(1, result.CancelledOrphans);
                AssertEqual(VoyageStatusEnum.Cancelled, (await testDb.Driver.Voyages.ReadAsync(orphan.Id).ConfigureAwait(false))!.Status);
                AssertEqual(VoyageStatusEnum.Open, (await testDb.Driver.Voyages.ReadAsync(unrelated.Id).ConfigureAwait(false))!.Status,
                    "A voyage that does not match the attempt descriptor is never touched.");
            });

            await RunTest("Reconciliation keeps a linked winning voyage after a crash before the attempt closed", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                AuthContext auth = Auth();
                Vessel vessel = await CreateVesselAsync(testDb, "crash-after-link").ConfigureAwait(false);
                Objective objective = await CreateObjectiveAsync(testDb, "Crash after link", vessel.Id).ConfigureAwait(false);
                ObjectiveService crashed = new ObjectiveService(testDb.Driver, dispatchAdmissionTtl: TimeSpan.FromMilliseconds(100));

                ObjectiveDispatchAdmission admission = await crashed.AcquireDispatchAdmissionAsync(
                    auth, new[] { objective.Id }, Descriptor("Crash after link", vessel.Id)).ConfigureAwait(false);
                Voyage winner = await CreateVoyageWithMissionAsync(testDb, "Crash after link", vessel.Id).ConfigureAwait(false);
                await admission.RecordVoyageCreatedAsync(winner).ConfigureAwait(false);
                await crashed.LinkVoyageAsync(auth, objective.Id, winner.Id).ConfigureAwait(false);
                await admission.AbandonAsCrashedAsync().ConfigureAwait(false);
                await Task.Delay(300).ConfigureAwait(false);

                ObjectiveDispatchAttemptReconciliationResult result = await new ObjectiveService(testDb.Driver)
                    .ReconcileDispatchAttemptsAsync().ConfigureAwait(false);

                AssertEqual(1, result.Examined, "The unclosed attempt must be examined.");
                AssertEqual(1, result.Kept);
                AssertEqual(0, result.CancelledOrphans, "A linked winner is never cancelled.");
                AssertEqual(VoyageStatusEnum.Open, (await testDb.Driver.Voyages.ReadAsync(winner.Id).ConfigureAwait(false))!.Status);
            });

            await RunTest("Reconciliation leaves an attempt alone while its owner still holds the lease", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                AuthContext auth = Auth();
                Vessel vessel = await CreateVesselAsync(testDb, "live-owner").ConfigureAwait(false);
                Objective objective = await CreateObjectiveAsync(testDb, "Live owner", vessel.Id).ConfigureAwait(false);
                ObjectiveService owner = new ObjectiveService(testDb.Driver);

                await using (ObjectiveDispatchAdmission admission = await owner.AcquireDispatchAdmissionAsync(
                    auth, new[] { objective.Id }, Descriptor("Live owner", vessel.Id)).ConfigureAwait(false))
                {
                    Voyage inFlight = await CreateVoyageWithMissionAsync(testDb, "Live owner", vessel.Id).ConfigureAwait(false);
                    await admission.RecordVoyageCreatedAsync(inFlight).ConfigureAwait(false);

                    ObjectiveDispatchAttemptReconciliationResult result = await new ObjectiveService(testDb.Driver)
                        .ReconcileDispatchAttemptsAsync().ConfigureAwait(false);

                    AssertEqual(1, result.LiveOwners, "A live owner's attempt must be skipped.");
                    AssertEqual(0, result.CancelledOrphans);
                    AssertEqual(VoyageStatusEnum.Open, (await testDb.Driver.Voyages.ReadAsync(inFlight.Id).ConfigureAwait(false))!.Status,
                        "An in-flight dispatch is never cancelled by reconciliation.");
                }
            });

            await RunTest("Reconciliation cancels a crashed orphan without touching the objective's later winner", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                AuthContext auth = Auth();
                Vessel vessel = await CreateVesselAsync(testDb, "orphan-and-winner").ConfigureAwait(false);
                Objective objective = await CreateObjectiveAsync(testDb, "Orphan and winner", vessel.Id).ConfigureAwait(false);
                ObjectiveService crashed = new ObjectiveService(testDb.Driver, dispatchAdmissionTtl: TimeSpan.FromMilliseconds(100));

                ObjectiveDispatchAdmission lost = await crashed.AcquireDispatchAdmissionAsync(
                    auth, new[] { objective.Id }, Descriptor("Orphan and winner", vessel.Id)).ConfigureAwait(false);
                Voyage orphan = await CreateVoyageWithMissionAsync(testDb, "Orphan and winner", vessel.Id).ConfigureAwait(false);
                await lost.RecordVoyageCreatedAsync(orphan).ConfigureAwait(false);
                await lost.AbandonAsCrashedAsync().ConfigureAwait(false);
                await Task.Delay(300).ConfigureAwait(false);

                ObjectiveService retrying = new ObjectiveService(testDb.Driver);
                Voyage winner;
                await using (ObjectiveDispatchAdmission retry = await retrying.AcquireDispatchAdmissionAsync(
                    auth, new[] { objective.Id }, Descriptor("Orphan and winner retry", vessel.Id)).ConfigureAwait(false))
                {
                    winner = await CreateVoyageWithMissionAsync(testDb, "Orphan and winner retry", vessel.Id).ConfigureAwait(false);
                    await retry.RecordVoyageCreatedAsync(winner).ConfigureAwait(false);
                    await retrying.LinkVoyageAsync(auth, objective.Id, winner.Id).ConfigureAwait(false);
                    retry.MarkLinked();
                }

                ObjectiveDispatchAttemptReconciliationResult result = await new ObjectiveService(testDb.Driver)
                    .ReconcileDispatchAttemptsAsync().ConfigureAwait(false);

                AssertEqual(1, result.CancelledOrphans);
                AssertEqual(VoyageStatusEnum.Cancelled, (await testDb.Driver.Voyages.ReadAsync(orphan.Id).ConfigureAwait(false))!.Status);
                AssertEqual(VoyageStatusEnum.Open, (await testDb.Driver.Voyages.ReadAsync(winner.Id).ConfigureAwait(false))!.Status,
                    "The later winner must stay active.");
                Objective stored = (await testDb.Driver.Objectives.ReadAsync(objective.Id).ConfigureAwait(false))!;
                AssertEqual(1, stored.VoyageIds.Count);
                AssertEqual(winner.Id, stored.VoyageIds[0]);
            });
        }

        private static AuthContext Auth()
        {
            return AuthContext.Authenticated(
                Armada.Core.Constants.DefaultTenantId,
                Armada.Core.Constants.DefaultUserId,
                false,
                true,
                "UnitTest");
        }

        private static int FreePort()
        {
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        private static ObjectiveDispatchAttemptDescriptor Descriptor(string title, string vesselId)
        {
            return new ObjectiveDispatchAttemptDescriptor { Title = title, VesselId = vesselId };
        }

        private static async Task<Vessel> CreateVesselAsync(TestDatabase testDb, string name)
        {
            return await testDb.Driver.Vessels.CreateAsync(new Vessel(name, "https://github.com/test/" + name + ".git")
            {
                TenantId = Armada.Core.Constants.DefaultTenantId,
                UserId = Armada.Core.Constants.DefaultUserId
            }).ConfigureAwait(false);
        }

        private static async Task<Objective> CreateObjectiveAsync(TestDatabase testDb, string title, string? vesselId = null)
        {
            return await testDb.Driver.Objectives.CreateAsync(new Objective
            {
                TenantId = Armada.Core.Constants.DefaultTenantId,
                UserId = Armada.Core.Constants.DefaultUserId,
                Title = title,
                Status = ObjectiveStatusEnum.Planned,
                BacklogState = ObjectiveBacklogStateEnum.ReadyForDispatch,
                VesselIds = vesselId == null ? new List<string>() : new List<string> { vesselId }
            }).ConfigureAwait(false);
        }

        private static async Task<Voyage> CreateVoyageWithMissionAsync(TestDatabase testDb, string title, string vesselId)
        {
            Voyage voyage = await testDb.Driver.Voyages.CreateAsync(new Voyage(title)
            {
                TenantId = Armada.Core.Constants.DefaultTenantId,
                UserId = Armada.Core.Constants.DefaultUserId,
                Status = VoyageStatusEnum.Open
            }).ConfigureAwait(false);
            await testDb.Driver.Missions.CreateAsync(new Mission(title + " work")
            {
                TenantId = Armada.Core.Constants.DefaultTenantId,
                UserId = Armada.Core.Constants.DefaultUserId,
                VoyageId = voyage.Id,
                VesselId = vesselId,
                Status = MissionStatusEnum.Pending
            }).ConfigureAwait(false);
            return voyage;
        }
    }
}
