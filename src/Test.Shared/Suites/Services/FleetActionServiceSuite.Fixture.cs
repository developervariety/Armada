namespace Test.Shared.Suites.Services
{
    using System;
    using System.Reflection;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Server;
    using SyslogLogging;
    using Test.Shared.Infrastructure;

    public sealed partial class FleetActionServiceSuite
    {
        private static async Task<Fixture> CreateFixtureAsync(DatabaseDriver database)
        {
            TenantMetadata tenant = await database.Tenants.CreateAsync(new TenantMetadata("Fleet Action test")).ConfigureAwait(false);
            UserMaster user = new UserMaster(tenant.Id, "fleet-action-" + Guid.NewGuid().ToString("N") + "@example.invalid", "test-password")
            {
                IsAdmin = true,
                Active = true
            };
            user = await database.Users.CreateAsync(user).ConfigureAwait(false);
            Armada.Core.Services.Interfaces.IAdmiralService admiral = DispatchProxy.Create<IAdmiralService, TestAdmiral>();
            ArmadaSettings settings = new ArmadaSettings();
            settings.CodeIndex.Enabled = false;
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            FleetActionService service = new FleetActionService(database, admiral, settings, logging);
            AuthContext owner = AuthContext.Authenticated(tenant.Id, user.Id, true, user.IsTenantAdmin, "Session");
            return new Fixture(database, service, owner, tenant, user, admiral, settings, logging);
        }

        private static async Task<Vessel> CreateVesselAsync(Fixture fixture, string name)
        {
            return await fixture.Database.Vessels.CreateAsync(new Vessel(name, "https://example.invalid/" + Guid.NewGuid().ToString("N") + ".git")
            {
                TenantId = fixture.Tenant.Id,
                UserId = fixture.User.Id
            }).ConfigureAwait(false);
        }

        private static FleetActionUpsertRequest Definition(string name = "Saved mission")
        {
            return new FleetActionUpsertRequest { Name = name, Active = true, PromptTemplate = "Inspect {vesselName} ({vesselId}).", Concurrency = 2 };
        }

        private static FleetActionMissionRunner CreateRunner(Fixture fixture)
        {
            MissionOperations operations = new MissionOperations(fixture.Database, fixture.Settings, null, null, null, fixture.Logging);
            VoyageDispatchService dispatcher = new VoyageDispatchService(fixture.Database, fixture.Admiral, fixture.Logging, settings: fixture.Settings);
            return new FleetActionMissionRunner(fixture.Database, fixture.Service, fixture.Admiral, dispatcher, operations, fixture.Settings, fixture.Logging);
        }

        private static async Task<Captain> CreateWorkerCaptainAsync(Fixture fixture)
        {
            return await fixture.Database.Captains.CreateAsync(new Captain("fleet-action-worker")
            {
                TenantId = fixture.Tenant.Id,
                UserId = fixture.User.Id,
                AllowedPersonas = "Worker",
                PreferredPersona = "Worker",
                Tier = CaptainTierEnum.Premium
            }).ConfigureAwait(false);
        }

        private sealed class Fixture
        {
            public DatabaseDriver Database { get; }
            public FleetActionService Service { get; }
            public AuthContext Owner { get; }
            public TenantMetadata Tenant { get; }
            public UserMaster User { get; }
            public IAdmiralService Admiral { get; }
            public ArmadaSettings Settings { get; }
            public LoggingModule Logging { get; }

            public Fixture(DatabaseDriver database, FleetActionService service, AuthContext owner, TenantMetadata tenant, UserMaster user,
                IAdmiralService admiral, ArmadaSettings settings, LoggingModule logging)
            {
                Database = database; Service = service; Owner = owner; Tenant = tenant; User = user;
                Admiral = admiral; Settings = settings; Logging = logging;
            }
        }

    }
}
