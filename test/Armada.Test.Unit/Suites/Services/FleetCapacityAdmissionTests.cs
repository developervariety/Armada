namespace Armada.Test.Unit.Suites.Services
{
    using System.Text.Json;
    using Armada.Core;
    using Armada.Core.Database;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Database.Sqlite;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using TestResourcePressure = global::Test.Shared.Infrastructure.TestResourcePressure;
    using SyslogLogging;

    /// <summary>
    /// Tests the durable universal work-capacity admission gate.
    /// </summary>
    public sealed class FleetCapacityAdmissionTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Fleet Capacity Admission";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("Two database drivers race for the final fleet slot and one wins", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                ArmadaSettings settings = Settings(2, 2);
                Vessel vessel = await CreateVesselAsync(testDb.Driver, "vsl_global").ConfigureAwait(false);
                await CreateWorkAsync(testDb.Driver, vessel, "existing").ConfigureAwait(false);

                using SqliteDatabaseDriver secondDriver = new SqliteDatabaseDriver(testDb.ConnectionString, logging);
                Task<bool>[] contenders =
                {
                    TryCreateWorkAsync(testDb.Driver, settings, logging, vessel, "first"),
                    TryCreateWorkAsync(secondDriver, settings, logging, vessel, "second")
                };
                bool[] outcomes = await Task.WhenAll(contenders).ConfigureAwait(false);

                AssertEqual(1, outcomes.Count(won => won), "Exactly one contender must obtain the final fleet slot.");
                AssertEqual(2, await CountWorkVoyagesAsync(testDb.Driver).ConfigureAwait(false),
                    "The durable census must not exceed the fleet limit.");
            }).ConfigureAwait(false);

            await RunTest("Transitive sibling vessels race for one lane slot and one wins", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                ArmadaSettings settings = Settings(5, 1);
                Vessel alpha = await CreateVesselAsync(testDb.Driver, "vsl_alpha").ConfigureAwait(false);
                Vessel beta = await CreateVesselAsync(testDb.Driver, "vsl_beta").ConfigureAwait(false);
                Vessel gamma = await CreateVesselAsync(testDb.Driver, "vsl_gamma").ConfigureAwait(false);
                alpha.SiblingRepos = JsonSerializer.Serialize(new List<SiblingRepo>
                {
                    new SiblingRepo { VesselRef = beta.Id, RelativePath = "../Beta", BuildParticipant = true }
                });
                beta.SiblingRepos = JsonSerializer.Serialize(new List<SiblingRepo>
                {
                    new SiblingRepo { VesselRef = gamma.Id, RelativePath = "../Gamma", BuildParticipant = true }
                });
                await testDb.Driver.Vessels.UpdateAsync(alpha).ConfigureAwait(false);
                await testDb.Driver.Vessels.UpdateAsync(beta).ConfigureAwait(false);

                using SqliteDatabaseDriver secondDriver = new SqliteDatabaseDriver(testDb.ConnectionString, logging);
                bool[] outcomes = await Task.WhenAll(
                    TryCreateWorkAsync(testDb.Driver, settings, logging, alpha, "alpha"),
                    TryCreateWorkAsync(secondDriver, settings, logging, gamma, "gamma")).ConfigureAwait(false);

                AssertEqual(1, outcomes.Count(won => won), "Exactly one transitive sibling contender must win.");
                AssertEqual(1, await CountWorkVoyagesAsync(testDb.Driver).ConfigureAwait(false),
                    "The sibling lane must stay at its configured limit.");
            }).ConfigureAwait(false);

            await RunTest("Bare voyage is free until its first mission and later missions reuse its slot", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                ArmadaSettings settings = Settings(1, 1);
                Vessel vessel = await CreateVesselAsync(testDb.Driver, "vsl_bare").ConfigureAwait(false);
                Voyage bare = await testDb.Driver.Voyages.CreateAsync(new Voyage("bare")
                {
                    TenantId = Constants.DefaultTenantId,
                    Status = VoyageStatusEnum.Open
                }).ConfigureAwait(false);
                FleetCapacityAdmission admission = new FleetCapacityAdmission(testDb.Driver, settings, logging);

                await using (FleetCapacityReservation first = await admission.AcquireAsync(vessel, bare.Id).ConfigureAwait(false))
                {
                    await testDb.Driver.Missions.CreateAsync(new Mission("first", "first")
                    {
                        TenantId = Constants.DefaultTenantId,
                        VoyageId = bare.Id,
                        VesselId = vessel.Id
                    }).ConfigureAwait(false);
                    await first.VerifyOwnershipAsync().ConfigureAwait(false);
                }

                await using FleetCapacityReservation second = await admission.AcquireAsync(vessel, bare.Id).ConfigureAwait(false);
                AssertNotNull(second, "A later mission in the same voyage must not consume a second slot.");
            }).ConfigureAwait(false);

            await RunTest("Typed refusal includes the candidate and sorted lane", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                ArmadaSettings settings = Settings(1, 1);
                Vessel vessel = await CreateVesselAsync(testDb.Driver, "vsl_typed").ConfigureAwait(false);
                await CreateWorkAsync(testDb.Driver, vessel, "occupied").ConfigureAwait(false);
                FleetCapacityAdmission admission = new FleetCapacityAdmission(testDb.Driver, settings, logging);

                try
                {
                    await admission.AcquireAsync(vessel, null).ConfigureAwait(false);
                    throw new InvalidOperationException("Expected a typed capacity refusal.");
                }
                catch (FleetCapacityAdmissionException ex)
                {
                    AssertEqual("fleet_capacity_reached", ex.Code, "refusal code");
                    AssertEqual(vessel.Id, ex.CandidateVesselId, "candidate vessel");
                    AssertEqual(vessel.Id, String.Join(",", ex.LaneMembers), "sorted lane");
                    AssertEqual(1, ex.ActiveCount, "active count");
                    AssertEqual(1, ex.Limit, "limit");
                }
            }).ConfigureAwait(false);

            await RunTest("Sibling-lane refusal has its own typed code", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                ArmadaSettings settings = Settings(5, 1);
                Vessel alpha = await CreateVesselAsync(testDb.Driver, "vsl_lane_a").ConfigureAwait(false);
                Vessel beta = await CreateVesselAsync(testDb.Driver, "vsl_lane_b").ConfigureAwait(false);
                alpha.SiblingRepos = JsonSerializer.Serialize(new List<SiblingRepo>
                {
                    new SiblingRepo { VesselRef = beta.Id, RelativePath = "../Beta", BuildParticipant = true }
                });
                await testDb.Driver.Vessels.UpdateAsync(alpha).ConfigureAwait(false);
                await CreateWorkAsync(testDb.Driver, alpha, "occupied lane").ConfigureAwait(false);

                try
                {
                    await new FleetCapacityAdmission(testDb.Driver, settings, logging)
                        .AcquireAsync(beta, null).ConfigureAwait(false);
                    throw new InvalidOperationException("Expected sibling-lane refusal.");
                }
                catch (FleetCapacityAdmissionException ex)
                {
                    AssertEqual("sibling_lane_capacity_reached", ex.Code, "lane refusal code");
                    AssertEqual(1, ex.ActiveCount, "lane active count");
                }
            }).ConfigureAwait(false);

            await RunTest("Existing voyage cannot add a newly occupied sibling lane", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                ArmadaSettings settings = Settings(5, 1);
                Vessel alpha = await CreateVesselAsync(testDb.Driver, "vsl_existing_a").ConfigureAwait(false);
                Vessel beta = await CreateVesselAsync(testDb.Driver, "vsl_existing_b").ConfigureAwait(false);
                await CreateWorkAsync(testDb.Driver, beta, "beta lane").ConfigureAwait(false);
                Voyage alphaVoyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("alpha work")
                {
                    TenantId = Constants.DefaultTenantId,
                    Status = VoyageStatusEnum.Open
                }).ConfigureAwait(false);
                await testDb.Driver.Missions.CreateAsync(new Mission("alpha", "alpha")
                {
                    TenantId = Constants.DefaultTenantId,
                    VoyageId = alphaVoyage.Id,
                    VesselId = alpha.Id,
                    Status = MissionStatusEnum.Pending
                }).ConfigureAwait(false);

                try
                {
                    await new FleetCapacityAdmission(testDb.Driver, settings, logging)
                        .AcquireAsync(beta, alphaVoyage.Id).ConfigureAwait(false);
                    throw new InvalidOperationException("Expected new-lane refusal.");
                }
                catch (FleetCapacityAdmissionException ex)
                {
                    AssertEqual("sibling_lane_capacity_reached", ex.Code, "new lane refusal code");
                }
            }).ConfigureAwait(false);

            await RunTest("Restart refuses a terminal standalone mission when fleet is full", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                ArmadaSettings settings = Settings(1, 1);
                Vessel vessel = await CreateVesselAsync(testDb.Driver, "vsl_restart").ConfigureAwait(false);
                await CreateWorkAsync(testDb.Driver, vessel, "occupied").ConfigureAwait(false);
                Mission terminal = await testDb.Driver.Missions.CreateAsync(new Mission("terminal", "terminal")
                {
                    TenantId = Constants.DefaultTenantId,
                    VesselId = vessel.Id,
                    Status = MissionStatusEnum.Failed
                }).ConfigureAwait(false);

                await AssertThrowsAsync<FleetCapacityAdmissionException>(async () =>
                    await new MissionRestartService(testDb.Driver, settings, logging)
                        .RestartAsync(terminal).ConfigureAwait(false), "full fleet must reject restart").ConfigureAwait(false);
                Mission? persisted = await testDb.Driver.Missions.ReadAsync(terminal.Id).ConfigureAwait(false);
                AssertEqual(MissionStatusEnum.Failed, persisted!.Status, "refused restart must preserve terminal state");
            }).ConfigureAwait(false);

            await RunTest("Commit-boundary verification detects lease takeover", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                ArmadaSettings settings = Settings(2, 2);
                Vessel vessel = await CreateVesselAsync(testDb.Driver, "vsl_takeover").ConfigureAwait(false);
                FleetCapacityAdmission admission = new FleetCapacityAdmission(testDb.Driver, settings, logging, TimeSpan.FromMinutes(1));
                await using FleetCapacityReservation first = await admission.AcquireAsync(vessel, null).ConfigureAwait(false);
                string leaseName = FleetCapacityAdmission.BuildLeaseName(Constants.DefaultTenantId);
                CoordinationLease? lease = await testDb.Driver.CoordinationLeases.ReadAsync(leaseName).ConfigureAwait(false);
                AssertNotNull(lease, "capacity lease");
                await testDb.Driver.CoordinationLeases.ReleaseAsync(leaseName, lease!.Holder).ConfigureAwait(false);
                await using FleetCapacityReservation second = await admission.AcquireAsync(vessel, null).ConfigureAwait(false);

                await AssertThrowsAsync<InvalidOperationException>(async () =>
                    await first.VerifyOwnershipAsync().ConfigureAwait(false), "stale holder must fail commit-boundary verification").ConfigureAwait(false);
            }).ConfigureAwait(false);

            await RunTest("Two Admiral instances cannot both persist the final work voyage", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                ArmadaSettings settings = Settings(1, 1);
                settings.DocksDirectory = Path.Combine(Path.GetTempPath(), "armada_capacity_docks_" + Guid.NewGuid().ToString("N"));
                settings.ReposDirectory = Path.Combine(Path.GetTempPath(), "armada_capacity_repos_" + Guid.NewGuid().ToString("N"));
                Vessel vessel = await CreateVesselAsync(testDb.Driver, "vsl_admiral_race").ConfigureAwait(false);
                using SqliteDatabaseDriver secondDriver = new SqliteDatabaseDriver(testDb.ConnectionString, logging);
                AdmiralService first = CreateAdmiral(testDb.Driver, settings, logging);
                AdmiralService second = CreateAdmiral(secondDriver, settings, logging);

                async Task<bool> DispatchAsync(AdmiralService admiral, string title)
                {
                    try
                    {
                        await admiral.DispatchVoyageQueuedAsync(
                            title,
                            "race",
                            vessel.Id,
                            new List<MissionDescription> { new MissionDescription(title, "work") },
                            null,
                            null).ConfigureAwait(false);
                        return true;
                    }
                    catch (FleetCapacityAdmissionException)
                    {
                        return false;
                    }
                }

                bool[] outcomes = await Task.WhenAll(
                    DispatchAsync(first, "first"),
                    DispatchAsync(second, "second")).ConfigureAwait(false);
                AssertEqual(1, outcomes.Count(won => won), "Exactly one Admiral dispatch must win.");
                AssertEqual(1, await CountWorkVoyagesAsync(testDb.Driver).ConfigureAwait(false),
                    "The losing Admiral must create no work rows.");
            }).ConfigureAwait(false);

            await RunTest("Admission counts active work from the footprint query without enumerating history", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                Vessel vessel = await CreateVesselAsync(testDb.Driver, "vsl_footprint").ConfigureAwait(false);
                Vessel other = await CreateVesselAsync(testDb.Driver, "vsl_footprint_other").ConfigureAwait(false);

                // Retained history: ended voyages with missions in every status, finished standalone
                // missions, and active-looking work in another tenant. None of it occupies capacity.
                foreach (VoyageStatusEnum ended in new[] { VoyageStatusEnum.Complete, VoyageStatusEnum.Failed, VoyageStatusEnum.Cancelled })
                {
                    Voyage history = await testDb.Driver.Voyages.CreateAsync(new Voyage("history " + ended)
                    {
                        TenantId = Constants.DefaultTenantId,
                        Status = ended
                    }).ConfigureAwait(false);
                    foreach (MissionStatusEnum status in Enum.GetValues<MissionStatusEnum>())
                    {
                        await testDb.Driver.Missions.CreateAsync(new Mission("history " + ended + " " + status, "done")
                        {
                            TenantId = Constants.DefaultTenantId, VoyageId = history.Id, VesselId = other.Id, Status = status
                        }).ConfigureAwait(false);
                    }
                }
                foreach (MissionStatusEnum finished in new[] { MissionStatusEnum.Complete, MissionStatusEnum.Failed, MissionStatusEnum.Cancelled, MissionStatusEnum.LandingFailed })
                {
                    await testDb.Driver.Missions.CreateAsync(new Mission("standalone " + finished, "done")
                    {
                        TenantId = Constants.DefaultTenantId, VesselId = other.Id, Status = finished
                    }).ConfigureAwait(false);
                }
                await testDb.Driver.Tenants.CreateAsync(new TenantMetadata { Id = "ten_footprint_other", Name = "ten_footprint_other" }).ConfigureAwait(false);
                await testDb.Driver.Missions.CreateAsync(new Mission("other tenant", "not counted")
                {
                    TenantId = "ten_footprint_other", VesselId = other.Id, Status = MissionStatusEnum.InProgress
                }).ConfigureAwait(false);

                // Active work: an Open voyage whose only mission is already Complete still holds its
                // slot, and a standalone mission in Review holds its own.
                Voyage live = await testDb.Driver.Voyages.CreateAsync(new Voyage("live")
                {
                    TenantId = Constants.DefaultTenantId,
                    Status = VoyageStatusEnum.Open
                }).ConfigureAwait(false);
                await testDb.Driver.Missions.CreateAsync(new Mission("live finished stage", "stage")
                {
                    TenantId = Constants.DefaultTenantId, VoyageId = live.Id, VesselId = other.Id, Status = MissionStatusEnum.Complete
                }).ConfigureAwait(false);
                await testDb.Driver.Missions.CreateAsync(new Mission("standalone review", "active")
                {
                    TenantId = Constants.DefaultTenantId, VesselId = other.Id, Status = MissionStatusEnum.Review
                }).ConfigureAwait(false);

                using HistoryGuardDriver guarded = new HistoryGuardDriver(testDb.ConnectionString, logging);
                try
                {
                    await new FleetCapacityAdmission(guarded, Settings(2, 5), logging).AcquireAsync(vessel, null).ConfigureAwait(false);
                    throw new InvalidOperationException("Expected the fleet to be full with two active work units.");
                }
                catch (FleetCapacityAdmissionException ex)
                {
                    AssertEqual("fleet_capacity_reached", ex.Code, "refusal code");
                    AssertEqual(2, ex.ActiveCount, "Exactly the live voyage and the active standalone mission occupy capacity.");
                }

                await using FleetCapacityReservation admitted = await new FleetCapacityAdmission(guarded, Settings(3, 5), logging)
                    .AcquireAsync(vessel, null).ConfigureAwait(false);
                AssertNotNull(admitted, "A third slot admits new work.");
                AssertEqual(0, guarded.HistoryEnumerations, "Admission must not enumerate full mission or voyage history.");
            }).ConfigureAwait(false);
        }

        private static ArmadaSettings Settings(int fleet, int lane)
        {
            ArmadaSettings settings = new ArmadaSettings();
            settings.AutonomousObjectiveScheduler.MaxConcurrentVoyages = fleet;
            settings.AutonomousObjectiveScheduler.MaxConcurrentVoyagesPerVessel = lane;
            return settings;
        }

        private static LoggingModule CreateLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        private static AdmiralService CreateAdmiral(
            DatabaseDriver database,
            ArmadaSettings settings,
            LoggingModule logging)
        {
            StubGitService git = new StubGitService();
            IDockService docks = new DockService(logging, database, settings, git);
            ICaptainService captains = new CaptainService(logging, database, settings, git, docks);
            IMissionService missions = new MissionService(logging, database, settings, docks, captains, resourcePressureAdmission: TestResourcePressure.Unconstrained(settings));
            IVoyageService voyages = new VoyageService(logging, database);
            return new AdmiralService(logging, database, settings, captains, missions, voyages, docks, git: git);
        }

        private static async Task<Vessel> CreateVesselAsync(DatabaseDriver database, string id)
        {
            return await database.Vessels.CreateAsync(new Vessel(id, "https://example.test/" + id + ".git")
            {
                Id = id,
                TenantId = Constants.DefaultTenantId,
                UserId = Constants.DefaultUserId
            }).ConfigureAwait(false);
        }

        private static async Task CreateWorkAsync(DatabaseDriver database, Vessel vessel, string suffix)
        {
            Voyage voyage = await database.Voyages.CreateAsync(new Voyage("work " + suffix)
            {
                TenantId = Constants.DefaultTenantId,
                Status = VoyageStatusEnum.Open
            }).ConfigureAwait(false);
            await database.Missions.CreateAsync(new Mission("work " + suffix, "work")
            {
                TenantId = Constants.DefaultTenantId,
                VoyageId = voyage.Id,
                VesselId = vessel.Id,
                Status = MissionStatusEnum.Pending
            }).ConfigureAwait(false);
        }

        private static async Task<bool> TryCreateWorkAsync(
            DatabaseDriver database,
            ArmadaSettings settings,
            LoggingModule logging,
            Vessel vessel,
            string suffix)
        {
            FleetCapacityAdmission admission = new FleetCapacityAdmission(database, settings, logging);
            try
            {
                await using FleetCapacityReservation reservation = await admission
                    .AcquireAsync(vessel, null).ConfigureAwait(false);
                await CreateWorkAsync(database, vessel, suffix).ConfigureAwait(false);
                await reservation.VerifyOwnershipAsync().ConfigureAwait(false);
                return true;
            }
            catch (FleetCapacityAdmissionException)
            {
                return false;
            }
        }

        /// <summary>
        /// A SQLite driver whose mission and voyage full-history enumerations fail and are counted.
        /// </summary>
        internal sealed class HistoryGuardDriver : SqliteDatabaseDriver
        {
            private int _HistoryEnumerations;

            public HistoryGuardDriver(string connectionString, LoggingModule logging)
                : base(connectionString, logging)
            {
                Missions = HistoryGuardProxy<IMissionMethods>.Wrap(Missions, this);
                Voyages = HistoryGuardProxy<IVoyageMethods>.Wrap(Voyages, this);
            }

            public int HistoryEnumerations => Volatile.Read(ref _HistoryEnumerations);

            internal void RecordHistoryEnumeration()
            {
                Interlocked.Increment(ref _HistoryEnumerations);
            }
        }

        /// <summary>
        /// Forwards every call to the provider, but refuses the unfiltered EnumerateAsync overloads
        /// that return every retained row.
        /// </summary>
        internal class HistoryGuardProxy<T> : System.Reflection.DispatchProxy where T : class
        {
            private T _Inner = null!;
            private HistoryGuardDriver _Owner = null!;

            internal static T Wrap(T inner, HistoryGuardDriver owner)
            {
                T proxy = Create<T, HistoryGuardProxy<T>>();
                HistoryGuardProxy<T> guard = (HistoryGuardProxy<T>)(object)proxy;
                guard._Inner = inner;
                guard._Owner = owner;
                return proxy;
            }

            /// <inheritdoc />
            protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args)
            {
                if (targetMethod == null) throw new ArgumentNullException(nameof(targetMethod));
                System.Reflection.ParameterInfo[] parameters = targetMethod.GetParameters();
                bool fullHistory = targetMethod.Name == "EnumerateAsync"
                    && parameters.All(parameter => parameter.ParameterType == typeof(string) || parameter.ParameterType == typeof(CancellationToken));
                if (fullHistory)
                {
                    _Owner.RecordHistoryEnumeration();
                    throw new InvalidOperationException("Full " + typeof(T).Name + " history was enumerated.");
                }

                try
                {
                    return targetMethod.Invoke(_Inner, args);
                }
                catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException != null)
                {
                    throw ex.InnerException;
                }
            }
        }

        private static async Task<int> CountWorkVoyagesAsync(DatabaseDriver database)
        {
            List<Voyage> voyages = await database.Voyages.EnumerateAsync(Constants.DefaultTenantId).ConfigureAwait(false);
            int count = 0;
            foreach (Voyage voyage in voyages.Where(v => v.Status == VoyageStatusEnum.Open || v.Status == VoyageStatusEnum.InProgress))
            {
                if ((await database.Missions.EnumerateByVoyageAsync(voyage.Id).ConfigureAwait(false))
                    .Any(mission => !String.IsNullOrWhiteSpace(mission.VesselId))) count++;
            }
            return count;
        }
    }
}
