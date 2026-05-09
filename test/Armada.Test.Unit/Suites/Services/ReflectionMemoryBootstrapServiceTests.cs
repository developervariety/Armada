namespace Armada.Test.Unit.Suites.Services
{
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// Tests for ReflectionMemoryBootstrapService: playbook creation, idempotency, and DefaultPlaybooks attachment.
    /// </summary>
    public class ReflectionMemoryBootstrapServiceTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Reflection Memory Bootstrap Service";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("NewVessel_NoDefaults_GetsPlaybookAndDefaultPlaybooksEntry", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Fleet fleet = new Fleet("rmb-fleet-1");
                    fleet = await testDb.Driver.Fleets.CreateAsync(fleet).ConfigureAwait(false);

                    Vessel vessel = new Vessel("rmb-vessel-one", "https://github.com/test/rmb-1.git");
                    vessel.FleetId = fleet.Id;
                    vessel.TenantId = Constants.DefaultTenantId;
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    SyslogLogging.LoggingModule logging = new SyslogLogging.LoggingModule();
                    logging.Settings.EnableConsole = false;
                    ReflectionMemoryBootstrapService svc = new ReflectionMemoryBootstrapService(testDb.Driver, logging);
                    await svc.BootstrapAsync().ConfigureAwait(false);

                    Vessel? updated = await testDb.Driver.Vessels.ReadAsync(vessel.Id).ConfigureAwait(false);
                    AssertNotNull(updated, "Vessel should exist after bootstrap");

                    List<SelectedPlaybook> defaults = updated!.GetDefaultPlaybooks();
                    AssertEqual(1, defaults.Count, "Vessel should have exactly one DefaultPlaybooks entry");
                    AssertEqual(PlaybookDeliveryModeEnum.InstructionWithReference, defaults[0].DeliveryMode, "Delivery mode should be InstructionWithReference");

                    bool playbookExists = await testDb.Driver.Playbooks.ExistsByFileNameAsync(
                        Constants.DefaultTenantId, "vessel-rmb-vessel-one-learned.md").ConfigureAwait(false);
                    AssertTrue(playbookExists, "Learned playbook file should exist in the tenant");

                    Playbook? playbook = await testDb.Driver.Playbooks.ReadByFileNameAsync(
                        Constants.DefaultTenantId, "vessel-rmb-vessel-one-learned.md").ConfigureAwait(false);
                    AssertNotNull(playbook, "Playbook should be readable by filename");
                    AssertEqual(defaults[0].PlaybookId, playbook!.Id, "DefaultPlaybooks entry should reference the created playbook");
                }
            });

            await RunTest("Bootstrap_RerunOnSameVessel_DoesNotDuplicatePlaybookOrEntry", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Fleet fleet = new Fleet("rmb-fleet-2");
                    fleet = await testDb.Driver.Fleets.CreateAsync(fleet).ConfigureAwait(false);

                    Vessel vessel = new Vessel("rmb-vessel-two", "https://github.com/test/rmb-2.git");
                    vessel.FleetId = fleet.Id;
                    vessel.TenantId = Constants.DefaultTenantId;
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    SyslogLogging.LoggingModule logging = new SyslogLogging.LoggingModule();
                    logging.Settings.EnableConsole = false;
                    ReflectionMemoryBootstrapService svc = new ReflectionMemoryBootstrapService(testDb.Driver, logging);

                    await svc.BootstrapAsync().ConfigureAwait(false);
                    await svc.BootstrapAsync().ConfigureAwait(false);

                    Vessel? updated = await testDb.Driver.Vessels.ReadAsync(vessel.Id).ConfigureAwait(false);
                    AssertNotNull(updated, "Vessel should exist after bootstrap");
                    List<SelectedPlaybook> defaults = updated!.GetDefaultPlaybooks();
                    AssertEqual(1, defaults.Count, "Re-running bootstrap must not add duplicate DefaultPlaybooks entries");

                    List<Armada.Core.Models.Playbook> allPlaybooks = await testDb.Driver.Playbooks.EnumerateAsync(
                        Constants.DefaultTenantId).ConfigureAwait(false);
                    int learnedCount = 0;
                    foreach (Armada.Core.Models.Playbook pb in allPlaybooks)
                    {
                        if (pb.FileName == "vessel-rmb-vessel-two-learned.md") learnedCount++;
                    }
                    AssertEqual(1, learnedCount, "Re-running bootstrap must not create duplicate playbook rows");
                }
            });

            await RunTest("Bootstrap_ExistingLearnedPlaybook_AttachesExistingPlaybookWithoutDuplicateRow", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Fleet fleet = new Fleet("rmb-fleet-5");
                    fleet = await testDb.Driver.Fleets.CreateAsync(fleet).ConfigureAwait(false);

                    Playbook existingPlaybook = new Playbook("vessel-rmb-vessel-five-learned.md", "# Existing Learned Facts\n\nNo accepted reflection facts yet.");
                    existingPlaybook.TenantId = Constants.DefaultTenantId;
                    existingPlaybook = await testDb.Driver.Playbooks.CreateAsync(existingPlaybook).ConfigureAwait(false);

                    Vessel vessel = new Vessel("rmb-vessel-five", "https://github.com/test/rmb-5.git");
                    vessel.FleetId = fleet.Id;
                    vessel.TenantId = Constants.DefaultTenantId;
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    SyslogLogging.LoggingModule logging = new SyslogLogging.LoggingModule();
                    logging.Settings.EnableConsole = false;
                    ReflectionMemoryBootstrapService svc = new ReflectionMemoryBootstrapService(testDb.Driver, logging);
                    await svc.BootstrapAsync().ConfigureAwait(false);

                    Vessel? updated = await testDb.Driver.Vessels.ReadAsync(vessel.Id).ConfigureAwait(false);
                    AssertNotNull(updated, "Vessel should exist after bootstrap");
                    List<SelectedPlaybook> defaults = updated!.GetDefaultPlaybooks();
                    AssertEqual(1, defaults.Count, "Existing learned playbook should be attached once");
                    AssertEqual(existingPlaybook.Id, defaults[0].PlaybookId, "DefaultPlaybooks entry should reference the existing playbook row");

                    List<Playbook> allPlaybooks = await testDb.Driver.Playbooks.EnumerateAsync(Constants.DefaultTenantId).ConfigureAwait(false);
                    int learnedCount = 0;
                    foreach (Playbook pb in allPlaybooks)
                    {
                        if (pb.FileName == "vessel-rmb-vessel-five-learned.md") learnedCount++;
                    }
                    AssertEqual(1, learnedCount, "Bootstrap must reuse the existing deterministic learned playbook row");
                }
            });

            await RunTest("Bootstrap_DefaultAlreadyReferencesLearnedPlaybook_DoesNotAppendDuplicateSelection", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Fleet fleet = new Fleet("rmb-fleet-6");
                    fleet = await testDb.Driver.Fleets.CreateAsync(fleet).ConfigureAwait(false);

                    Playbook existingPlaybook = new Playbook("vessel-rmb-vessel-six-learned.md", "# Existing Learned Facts\n\nNo accepted reflection facts yet.");
                    existingPlaybook.TenantId = Constants.DefaultTenantId;
                    existingPlaybook = await testDb.Driver.Playbooks.CreateAsync(existingPlaybook).ConfigureAwait(false);

                    List<SelectedPlaybook> existingDefaults = new List<SelectedPlaybook>
                    {
                        new SelectedPlaybook { PlaybookId = existingPlaybook.Id, DeliveryMode = PlaybookDeliveryModeEnum.InlineFullContent }
                    };

                    Vessel vessel = new Vessel("rmb-vessel-six", "https://github.com/test/rmb-6.git");
                    vessel.FleetId = fleet.Id;
                    vessel.TenantId = Constants.DefaultTenantId;
                    vessel.DefaultPlaybooks = JsonSerializer.Serialize(existingDefaults,
                        new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    SyslogLogging.LoggingModule logging = new SyslogLogging.LoggingModule();
                    logging.Settings.EnableConsole = false;
                    ReflectionMemoryBootstrapService svc = new ReflectionMemoryBootstrapService(testDb.Driver, logging);
                    await svc.BootstrapAsync().ConfigureAwait(false);

                    Vessel? updated = await testDb.Driver.Vessels.ReadAsync(vessel.Id).ConfigureAwait(false);
                    AssertNotNull(updated, "Vessel should exist after bootstrap");
                    List<SelectedPlaybook> defaults = updated!.GetDefaultPlaybooks();
                    AssertEqual(1, defaults.Count, "Existing learned playbook selection must not be duplicated");
                    AssertEqual(existingPlaybook.Id, defaults[0].PlaybookId, "Existing selection should still reference the learned playbook");
                    AssertEqual(PlaybookDeliveryModeEnum.InlineFullContent, defaults[0].DeliveryMode, "Existing caller-managed delivery mode must be preserved");
                }
            });

            await RunTest("Bootstrap_VesselWithExistingDefaults_PreservesExistingEntries", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Fleet fleet = new Fleet("rmb-fleet-3");
                    fleet = await testDb.Driver.Fleets.CreateAsync(fleet).ConfigureAwait(false);

                    List<SelectedPlaybook> existing = new List<SelectedPlaybook>
                    {
                        new SelectedPlaybook { PlaybookId = "pbk_existing_001", DeliveryMode = PlaybookDeliveryModeEnum.InlineFullContent }
                    };

                    Vessel vessel = new Vessel("rmb-vessel-three", "https://github.com/test/rmb-3.git");
                    vessel.FleetId = fleet.Id;
                    vessel.TenantId = Constants.DefaultTenantId;
                    vessel.DefaultPlaybooks = JsonSerializer.Serialize(existing,
                        new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    SyslogLogging.LoggingModule logging = new SyslogLogging.LoggingModule();
                    logging.Settings.EnableConsole = false;
                    ReflectionMemoryBootstrapService svc = new ReflectionMemoryBootstrapService(testDb.Driver, logging);
                    await svc.BootstrapAsync().ConfigureAwait(false);

                    Vessel? updated = await testDb.Driver.Vessels.ReadAsync(vessel.Id).ConfigureAwait(false);
                    AssertNotNull(updated, "Vessel should exist after bootstrap");
                    List<SelectedPlaybook> defaults = updated!.GetDefaultPlaybooks();
                    AssertEqual(2, defaults.Count, "Bootstrap should append the learned playbook without removing existing entries");
                    AssertEqual("pbk_existing_001", defaults[0].PlaybookId, "Existing entry must be preserved in first position");
                    AssertEqual(PlaybookDeliveryModeEnum.InstructionWithReference, defaults[1].DeliveryMode, "New learned playbook must use InstructionWithReference");
                }
            });

            await RunTest("Bootstrap_PlaybookTenantId_MatchesVesselTenantId", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Fleet fleet = new Fleet("rmb-fleet-4");
                    fleet = await testDb.Driver.Fleets.CreateAsync(fleet).ConfigureAwait(false);

                    Vessel vessel = new Vessel("rmb-vessel-four", "https://github.com/test/rmb-4.git");
                    vessel.FleetId = fleet.Id;
                    vessel.TenantId = Constants.DefaultTenantId;
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    SyslogLogging.LoggingModule logging = new SyslogLogging.LoggingModule();
                    logging.Settings.EnableConsole = false;
                    ReflectionMemoryBootstrapService svc = new ReflectionMemoryBootstrapService(testDb.Driver, logging);
                    await svc.BootstrapAsync().ConfigureAwait(false);

                    Playbook? playbook = await testDb.Driver.Playbooks.ReadByFileNameAsync(
                        Constants.DefaultTenantId, "vessel-rmb-vessel-four-learned.md").ConfigureAwait(false);
                    AssertNotNull(playbook, "Learned playbook should be created");
                    AssertEqual(Constants.DefaultTenantId, playbook!.TenantId, "Playbook TenantId must match vessel TenantId");
                    AssertTrue(playbook.Active, "Playbook should be active");
                    AssertFalse(string.IsNullOrWhiteSpace(playbook.Content), "Playbook content must not be empty");
                }
            });

            await RunTest("Bootstrap_VesselWithoutTenant_SkipsPlaybookCreation", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Fleet fleet = new Fleet("rmb-fleet-7");
                    fleet = await testDb.Driver.Fleets.CreateAsync(fleet).ConfigureAwait(false);

                    Vessel vessel = new Vessel("rmb-vessel-seven", "https://github.com/test/rmb-7.git");
                    vessel.FleetId = fleet.Id;
                    vessel.TenantId = null;
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    SyslogLogging.LoggingModule logging = new SyslogLogging.LoggingModule();
                    logging.Settings.EnableConsole = false;
                    ReflectionMemoryBootstrapService svc = new ReflectionMemoryBootstrapService(testDb.Driver, logging);
                    await svc.BootstrapAsync().ConfigureAwait(false);

                    Vessel? updated = await testDb.Driver.Vessels.ReadAsync(vessel.Id).ConfigureAwait(false);
                    AssertNotNull(updated, "Vessel should exist after bootstrap");
                    AssertEqual(0, updated!.GetDefaultPlaybooks().Count, "Tenantless vessels should not receive DefaultPlaybooks entries");

                    List<Playbook> allPlaybooks = await testDb.Driver.Playbooks.EnumerateAsync().ConfigureAwait(false);
                    AssertEqual(0, allPlaybooks.Count, "Tenantless vessels should not create learned playbook rows");
                }
            });
        }
    }
}
