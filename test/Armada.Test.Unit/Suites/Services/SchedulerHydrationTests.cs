namespace Armada.Test.Unit.Suites.Services
{
    using System.Collections.Generic;
    using System.Threading.Tasks;
    using Armada.Core.Database.Sqlite;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// Regression tests verifying that the scheduler hot path does not hydrate full
    /// mission rows when checking vessel-level concurrency and broad-scope locks.
    /// Also covers the doctor endpoint count-only behaviour for failed missions.
    /// </summary>
    public class SchedulerHydrationTests : TestSuite
    {
        /// <summary>
        /// Suite name.
        /// </summary>
        public override string Name => "Scheduler Hydration";

        #region Private-Methods

        private LoggingModule CreateLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        private ArmadaSettings CreateSettings()
        {
            ArmadaSettings settings = new ArmadaSettings();
            settings.DocksDirectory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "armada_hydration_docks_" + System.Guid.NewGuid().ToString("N"));
            settings.ReposDirectory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "armada_hydration_repos_" + System.Guid.NewGuid().ToString("N"));
            return settings;
        }

        private string ReadRepoFile(string relativePath)
        {
            string? directory = System.IO.Directory.GetCurrentDirectory();
            while (!String.IsNullOrEmpty(directory))
            {
                string candidate = System.IO.Path.Combine(directory, relativePath);
                if (System.IO.File.Exists(candidate))
                {
                    return System.IO.File.ReadAllText(candidate);
                }

                directory = System.IO.Directory.GetParent(directory)?.FullName;
            }

            throw new System.IO.FileNotFoundException("Could not find repository file", relativePath);
        }

        private static string ExtractMethodBody(string source, string signature)
        {
            int signatureIndex = source.IndexOf(signature, StringComparison.Ordinal);
            if (signatureIndex < 0) throw new InvalidOperationException("Signature not found: " + signature);

            int openBraceIndex = source.IndexOf('{', signatureIndex);
            if (openBraceIndex < 0) throw new InvalidOperationException("Opening brace not found: " + signature);

            int depth = 0;
            for (int i = openBraceIndex; i < source.Length; i++)
            {
                if (source[i] == '{')
                {
                    depth++;
                }
                else if (source[i] == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        return source.Substring(openBraceIndex, i - openBraceIndex + 1);
                    }
                }
            }

            throw new InvalidOperationException("Closing brace not found: " + signature);
        }

        #endregion

        /// <summary>
        /// Run all scheduler hydration regression tests.
        /// </summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("GetActiveVesselSummaries_OnlyReturnsAssignedAndInProgress", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;

                    Fleet fleet = new Fleet("HydrationFleet");
                    await db.Fleets.CreateAsync(fleet);

                    Vessel vessel = new Vessel("HydrationVessel", "https://github.com/test/repo");
                    vessel.FleetId = fleet.Id;
                    await db.Vessels.CreateAsync(vessel);

                    Voyage voyage = new Voyage("HydrationVoyage");
                    voyage.Status = VoyageStatusEnum.InProgress;
                    await db.Voyages.CreateAsync(voyage);

                    // Create missions in various statuses
                    Mission assigned = new Mission("Assigned Mission");
                    assigned.Status = MissionStatusEnum.Assigned;
                    assigned.VesselId = vessel.Id;
                    assigned.VoyageId = voyage.Id;
                    assigned.Description = "This description should not be returned by GetActiveVesselSummariesAsync";
                    await db.Missions.CreateAsync(assigned);

                    Mission inProgress = new Mission("InProgress Mission");
                    inProgress.Status = MissionStatusEnum.InProgress;
                    inProgress.VesselId = vessel.Id;
                    inProgress.VoyageId = voyage.Id;
                    await db.Missions.CreateAsync(inProgress);

                    Mission pending = new Mission("Pending Mission");
                    pending.Status = MissionStatusEnum.Pending;
                    pending.VesselId = vessel.Id;
                    pending.VoyageId = voyage.Id;
                    await db.Missions.CreateAsync(pending);

                    Mission workProduced = new Mission("WorkProduced Mission");
                    workProduced.Status = MissionStatusEnum.WorkProduced;
                    workProduced.VesselId = vessel.Id;
                    workProduced.VoyageId = voyage.Id;
                    await db.Missions.CreateAsync(workProduced);

                    Mission failed = new Mission("Failed Mission");
                    failed.Status = MissionStatusEnum.Failed;
                    failed.VesselId = vessel.Id;
                    failed.VoyageId = voyage.Id;
                    await db.Missions.CreateAsync(failed);

                    List<ActiveMissionSummary> summaries = await db.Missions.GetActiveVesselSummariesAsync(vessel.Id);

                    // Only Assigned and InProgress should be returned
                    AssertEqual(2, summaries.Count, "Only Assigned and InProgress missions should be returned");
                    AssertTrue(summaries.Exists(s => s.Id == assigned.Id), "Assigned mission should be in summaries");
                    AssertTrue(summaries.Exists(s => s.Id == inProgress.Id), "InProgress mission should be in summaries");
                    AssertTrue(!summaries.Exists(s => s.Id == pending.Id), "Pending mission should NOT be in summaries");
                    AssertTrue(!summaries.Exists(s => s.Id == workProduced.Id), "WorkProduced mission should NOT be in summaries");
                    AssertTrue(!summaries.Exists(s => s.Id == failed.Id), "Failed mission should NOT be in summaries");
                }
            });

            await RunTest("GetActiveVesselSummaries_NoHeavyColumns_DescriptionIsEmpty", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;

                    Fleet fleet = new Fleet("NoHeavyFleet");
                    await db.Fleets.CreateAsync(fleet);

                    Vessel vessel = new Vessel("NoHeavyVessel", "https://github.com/test/repo");
                    vessel.FleetId = fleet.Id;
                    await db.Vessels.CreateAsync(vessel);

                    Voyage voyage = new Voyage("NoHeavyVoyage");
                    voyage.Status = VoyageStatusEnum.InProgress;
                    await db.Voyages.CreateAsync(voyage);

                    Mission m = new Mission("Active Mission With Heavy Content");
                    m.Status = MissionStatusEnum.InProgress;
                    m.VesselId = vessel.Id;
                    m.VoyageId = voyage.Id;
                    m.Description = "A very large description that should not be loaded during active-vessel checks";
                    m.AgentOutput = "A very large agent output that should not be loaded during active-vessel checks";
                    await db.Missions.CreateAsync(m);

                    List<ActiveMissionSummary> summaries = await db.Missions.GetActiveVesselSummariesAsync(vessel.Id);

                    AssertEqual(1, summaries.Count, "One active mission should be returned");
                    ActiveMissionSummary s = summaries[0];
                    AssertEqual(m.Id, s.Id, "Summary id matches");
                    AssertEqual("Active Mission With Heavy Content", s.Title, "Summary title matches");
                    AssertEqual(MissionStatusEnum.InProgress, s.Status, "Summary status matches");
                    // ActiveMissionSummary has no Description field -- compilation guarantees no heavy load
                }
            });

            await RunTest("GetActiveVesselSummaries_BroadScopeTitle_DetectedCorrectly", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();

                    Fleet fleet = new Fleet("BroadScopeFleet");
                    await db.Fleets.CreateAsync(fleet);

                    Vessel vessel = new Vessel("BroadScopeVessel", "https://github.com/test/repo");
                    vessel.FleetId = fleet.Id;
                    await db.Vessels.CreateAsync(vessel);

                    Voyage voyage = new Voyage("BroadScopeVoyage");
                    voyage.Status = VoyageStatusEnum.InProgress;
                    await db.Voyages.CreateAsync(voyage);

                    // Create an in-progress broad-scope mission
                    Mission broadMission = new Mission("Refactor entire authentication module");
                    broadMission.Status = MissionStatusEnum.InProgress;
                    broadMission.VesselId = vessel.Id;
                    broadMission.VoyageId = voyage.Id;
                    await db.Missions.CreateAsync(broadMission);

                    List<ActiveMissionSummary> summaries = await db.Missions.GetActiveVesselSummariesAsync(vessel.Id);
                    AssertEqual(1, summaries.Count, "Broad-scope active mission should be in summaries");

                    IDockService dockService = new DockService(logging, db, settings, new StubGitService());
                    CaptainService captainService = new CaptainService(logging, db, settings, new StubGitService(), dockService);
                    captainService.OnLaunchAgent = (captain, mission, dock) => Task.FromResult(9999);
                    MissionService missionService = new MissionService(logging, db, settings, dockService, captainService);

                    // MissionService.IsBroadScope on summary should detect the broad-scope title
                    AssertTrue(missionService.IsBroadScope(summaries[0]), "IsBroadScope(ActiveMissionSummary) should detect broad-scope title");

                    // Create a new mission to try to assign while broad mission is running
                    Mission newMission = new Mission("Add logging to auth service");
                    newMission.Status = MissionStatusEnum.Pending;
                    newMission.VesselId = vessel.Id;
                    newMission.VoyageId = voyage.Id;
                    await db.Missions.CreateAsync(newMission);

                    Captain captain = new Captain("test-captain");
                    captain.State = CaptainStateEnum.Idle;
                    await db.Captains.CreateAsync(captain);

                    // TryAssignAsync must block new assignments while a broad-scope mission runs
                    bool assigned = await missionService.TryAssignAsync(newMission, vessel);
                    AssertFalse(assigned, "TryAssignAsync should block new assignments while broad-scope mission is in progress");
                }
            });

            await RunTest("CountByStatusAsync_TenantScoped_ReturnsCorrectCounts", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;

                    // Use the default tenant that is seeded on InitializeAsync
                    string tenantId = Armada.Core.Constants.DefaultTenantId;

                    Fleet fleet = new Fleet("CountFleet");
                    fleet.TenantId = tenantId;
                    await db.Fleets.CreateAsync(fleet);

                    Voyage voyage = new Voyage("CountVoyage");
                    voyage.Status = VoyageStatusEnum.InProgress;
                    voyage.TenantId = tenantId;
                    await db.Voyages.CreateAsync(voyage);

                    Vessel vessel = new Vessel("CountVessel", "https://github.com/test/repo");
                    vessel.FleetId = fleet.Id;
                    vessel.TenantId = tenantId;
                    await db.Vessels.CreateAsync(vessel);

                    // Create missions with the default tenant and various statuses
                    for (int i = 0; i < 3; i++)
                    {
                        Mission m = new Mission("Failed Mission " + i);
                        m.Status = MissionStatusEnum.Failed;
                        m.TenantId = tenantId;
                        m.VesselId = vessel.Id;
                        m.VoyageId = voyage.Id;
                        m.Description = "Large description " + new string('x', 1000);
                        await db.Missions.CreateAsync(m);
                    }

                    for (int i = 0; i < 2; i++)
                    {
                        Mission m = new Mission("Completed Mission " + i);
                        m.Status = MissionStatusEnum.WorkProduced;
                        m.TenantId = tenantId;
                        m.VesselId = vessel.Id;
                        m.VoyageId = voyage.Id;
                        await db.Missions.CreateAsync(m);
                    }

                    System.Collections.Generic.Dictionary<MissionStatusEnum, int> counts =
                        await db.Missions.CountByStatusAsync(tenantId);

                    counts.TryGetValue(MissionStatusEnum.Failed, out int failedCount);
                    counts.TryGetValue(MissionStatusEnum.WorkProduced, out int wpCount);

                    AssertEqual(3, failedCount, "Tenant-scoped failed count should be 3");
                    AssertEqual(2, wpCount, "Tenant-scoped WorkProduced count should be 2");
                }
            });

            await RunTest("CountByStatusAsync_TenantScoped_ExcludesOtherTenants", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;

                    string tenantId = Armada.Core.Constants.DefaultTenantId;
                    TenantMetadata otherTenant = new TenantMetadata("Other Count Tenant");
                    await db.Tenants.CreateAsync(otherTenant);

                    Fleet fleet = new Fleet("TenantFilterFleet");
                    fleet.TenantId = tenantId;
                    await db.Fleets.CreateAsync(fleet);

                    Voyage voyage = new Voyage("TenantFilterVoyage");
                    voyage.Status = VoyageStatusEnum.InProgress;
                    voyage.TenantId = tenantId;
                    await db.Voyages.CreateAsync(voyage);

                    Vessel vessel = new Vessel("TenantFilterVessel", "https://github.com/test/repo");
                    vessel.FleetId = fleet.Id;
                    vessel.TenantId = tenantId;
                    await db.Vessels.CreateAsync(vessel);

                    Mission defaultTenantMission = new Mission("Default Tenant Failed");
                    defaultTenantMission.Status = MissionStatusEnum.Failed;
                    defaultTenantMission.TenantId = tenantId;
                    defaultTenantMission.VesselId = vessel.Id;
                    defaultTenantMission.VoyageId = voyage.Id;
                    await db.Missions.CreateAsync(defaultTenantMission);

                    Mission otherTenantMission = new Mission("Other Tenant Failed");
                    otherTenantMission.Status = MissionStatusEnum.Failed;
                    otherTenantMission.TenantId = otherTenant.Id;
                    await db.Missions.CreateAsync(otherTenantMission);

                    Dictionary<MissionStatusEnum, int> defaultCounts = await db.Missions.CountByStatusAsync(tenantId);
                    Dictionary<MissionStatusEnum, int> otherCounts = await db.Missions.CountByStatusAsync(otherTenant.Id);

                    defaultCounts.TryGetValue(MissionStatusEnum.Failed, out int defaultFailedCount);
                    otherCounts.TryGetValue(MissionStatusEnum.Failed, out int otherFailedCount);

                    AssertEqual(1, defaultFailedCount, "Tenant-scoped count should not include failed missions from other tenants");
                    AssertEqual(1, otherFailedCount, "Other tenant count should remain independently scoped");
                }
            });

            await RunTest("CountByStatusAsync_Admin_ReturnsCorrectAggregateCounts", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;

                    Fleet fleet = new Fleet("AdminCountFleet");
                    await db.Fleets.CreateAsync(fleet);

                    Voyage voyage = new Voyage("AdminCountVoyage");
                    voyage.Status = VoyageStatusEnum.InProgress;
                    await db.Voyages.CreateAsync(voyage);

                    Vessel vessel = new Vessel("AdminCountVessel", "https://github.com/test/repo");
                    vessel.FleetId = fleet.Id;
                    await db.Vessels.CreateAsync(vessel);

                    // Create missions without tenant (null TenantId allowed for admin missions)
                    for (int i = 0; i < 3; i++)
                    {
                        Mission m = new Mission("Failed Mission " + i);
                        m.Status = MissionStatusEnum.Failed;
                        m.VesselId = vessel.Id;
                        m.VoyageId = voyage.Id;
                        await db.Missions.CreateAsync(m);
                    }

                    Mission pending = new Mission("Pending Mission");
                    pending.Status = MissionStatusEnum.Pending;
                    pending.VesselId = vessel.Id;
                    pending.VoyageId = voyage.Id;
                    await db.Missions.CreateAsync(pending);

                    // Admin count (no tenant filter) should see all missions grouped by status
                    System.Collections.Generic.Dictionary<MissionStatusEnum, int> counts =
                        await db.Missions.CountByStatusAsync();

                    counts.TryGetValue(MissionStatusEnum.Failed, out int failedCount);
                    counts.TryGetValue(MissionStatusEnum.Pending, out int pendingCount);

                    AssertEqual(3, failedCount, "Admin count should return 3 failed missions");
                    AssertEqual(1, pendingCount, "Admin count should return 1 pending mission");
                }
            });

            await RunTest("IsBroadScope_ActiveMissionSummary_MatchesMissionBehavior", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();

                    IDockService dockService = new DockService(logging, db, settings, new StubGitService());
                    CaptainService captainService = new CaptainService(logging, db, settings, new StubGitService(), dockService);
                    MissionService missionService = new MissionService(logging, db, settings, dockService, captainService);

                    string[] broadTitles = new string[]
                    {
                        "Refactor entire codebase",
                        "Rename across all files",
                        "Rewrite authentication service",
                        "Restructure project layout",
                        "Upgrade framework to v10"
                    };

                    string[] normalTitles = new string[]
                    {
                        "Add logging to login endpoint",
                        "Fix null reference in token handler",
                        "Update README with examples"
                    };

                    foreach (string title in broadTitles)
                    {
                        Mission m = new Mission(title);
                        ActiveMissionSummary summary = new ActiveMissionSummary { Id = "x", Title = title, Status = MissionStatusEnum.InProgress };
                        AssertEqual(
                            missionService.IsBroadScope(m),
                            missionService.IsBroadScope(summary),
                            "IsBroadScope(Mission) and IsBroadScope(ActiveMissionSummary) must agree for: " + title);
                        AssertTrue(missionService.IsBroadScope(summary), "Title should be detected as broad-scope: " + title);
                    }

                    foreach (string title in normalTitles)
                    {
                        Mission m = new Mission(title);
                        ActiveMissionSummary summary = new ActiveMissionSummary { Id = "x", Title = title, Status = MissionStatusEnum.InProgress };
                        AssertEqual(
                            missionService.IsBroadScope(m),
                            missionService.IsBroadScope(summary),
                            "IsBroadScope(Mission) and IsBroadScope(ActiveMissionSummary) must agree for: " + title);
                        AssertFalse(missionService.IsBroadScope(summary), "Title should NOT be detected as broad-scope: " + title);
                    }
                }
            });

            await RunTest("TryAssignAsync_SourceGuard_UsesActiveSummariesOnly", async () =>
            {
                string source = ReadRepoFile(System.IO.Path.Combine("src", "Armada.Core", "Services", "MissionService.cs"));
                string method = ExtractMethodBody(source, "public async Task<bool> TryAssignAsync");

                AssertContains("GetActiveVesselSummariesAsync", method, "TryAssignAsync should use the lightweight active mission projection");
                AssertContains("int concurrentCount = activeSummaries.Count;", method, "Concurrent capacity should use the lightweight active summary count");
                AssertFalse(method.Contains("EnumerateByVesselAsync", StringComparison.Ordinal), "TryAssignAsync must not hydrate full vessel missions");
            });

            await RunTest("GetActiveVesselSummaries_SourceGuard_AllBackendsUseLightProjection", async () =>
            {
                string[] paths = new string[]
                {
                    System.IO.Path.Combine("src", "Armada.Core", "Database", "Sqlite", "Implementations", "MissionMethods.cs"),
                    System.IO.Path.Combine("src", "Armada.Core", "Database", "Mysql", "Implementations", "MissionMethods.cs"),
                    System.IO.Path.Combine("src", "Armada.Core", "Database", "Postgresql", "Implementations", "MissionMethods.cs"),
                    System.IO.Path.Combine("src", "Armada.Core", "Database", "SqlServer", "Implementations", "MissionMethods.cs")
                };

                foreach (string path in paths)
                {
                    string source = ReadRepoFile(path);
                    string method = ExtractMethodBody(source, "GetActiveVesselSummariesAsync");

                    AssertContains("SELECT id, title, status FROM missions", method, path + " should select only scheduler summary columns");
                    AssertContains("status IN ('Assigned','InProgress')", method, path + " should filter to active assignment statuses in SQL");
                    AssertFalse(method.Contains("SELECT *", StringComparison.OrdinalIgnoreCase), path + " must not use SELECT * for active summaries");
                    AssertFalse(method.Contains("description", StringComparison.OrdinalIgnoreCase), path + " must not select description");
                    AssertFalse(method.Contains("diff_snapshot", StringComparison.OrdinalIgnoreCase), path + " must not select diff_snapshot");
                    AssertFalse(method.Contains("agent_output", StringComparison.OrdinalIgnoreCase), path + " must not select agent_output");
                    AssertFalse(method.Contains("playbook", StringComparison.OrdinalIgnoreCase), path + " must not select playbook snapshots");
                }
            });

            await RunTest("CountByStatusAsync_SourceGuard_AllBackendsUseGroupByCounts", async () =>
            {
                string[] paths = new string[]
                {
                    System.IO.Path.Combine("src", "Armada.Core", "Database", "Sqlite", "Implementations", "MissionMethods.cs"),
                    System.IO.Path.Combine("src", "Armada.Core", "Database", "Mysql", "Implementations", "MissionMethods.cs"),
                    System.IO.Path.Combine("src", "Armada.Core", "Database", "Postgresql", "Implementations", "MissionMethods.cs"),
                    System.IO.Path.Combine("src", "Armada.Core", "Database", "SqlServer", "Implementations", "MissionMethods.cs")
                };

                foreach (string path in paths)
                {
                    string source = ReadRepoFile(path);
                    AssertContains("SELECT status, COUNT(*) AS cnt FROM missions GROUP BY status;", source, path + " should count statuses without hydrating rows");
                    AssertContains("SELECT status, COUNT(*) AS cnt FROM missions WHERE tenant_id = @tenantId GROUP BY status;", source, path + " should count tenant statuses without hydrating rows");
                }
            });

            await RunTest("DoctorRoute_SourceGuard_UsesCountOnlyFailedMissionLogic", async () =>
            {
                string source = ReadRepoFile(System.IO.Path.Combine("src", "Armada.Server", "Routes", "StatusRoutes.cs"));
                int failedMissionStart = source.IndexOf("// 6. Failed Missions", StringComparison.Ordinal);
                int runtimeStart = source.IndexOf("// 7. Agent Runtimes", StringComparison.Ordinal);
                AssertTrue(failedMissionStart >= 0, "Doctor failed mission section should exist");
                AssertTrue(runtimeStart > failedMissionStart, "Doctor runtime section should follow failed mission section");

                string failedMissionSection = source.Substring(failedMissionStart, runtimeStart - failedMissionStart);
                AssertContains("CountByStatusAsync", failedMissionSection, "Doctor failed mission check should use count-only database APIs");
                AssertFalse(failedMissionSection.Contains("EnumerateByStatusAsync", StringComparison.Ordinal), "Doctor failed mission check must not enumerate failed mission rows");
                AssertFalse(failedMissionSection.Contains("EnumerateAsync", StringComparison.Ordinal), "Doctor failed mission check must not enumerate full mission rows");
            });

            await RunTest("DashboardRefresh_SourceGuard_DoctorRefreshIsViewGated", async () =>
            {
                string source = ReadRepoFile(System.IO.Path.Combine("src", "Armada.Server", "wwwroot", "js", "dashboard.js"));
                string refreshMethod = ExtractMethodBody(source, "async refresh()");

                AssertContains("this.view === 'doctor' ? this.refreshDoctorStatus() : Promise.resolve()", refreshMethod, "Background refresh should call doctor only on the doctor view");
                AssertFalse(source.Contains("setInterval(() => this.refreshDoctorStatus()", StringComparison.Ordinal), "Dashboard timer must not poll doctor directly");
            });
        }
    }
}
