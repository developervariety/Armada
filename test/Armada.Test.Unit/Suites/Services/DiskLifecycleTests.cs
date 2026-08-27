namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.IO;
    using System.Linq;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// Coverage for the disk-lifecycle reconciler: byte classification per owned category,
    /// dry-run default, opt-in deletion, retention, active-dock protection, sibling protection,
    /// and fail-closed handling of symlinks.
    /// </summary>
    public class DiskLifecycleTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Disk Lifecycle";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("Scan classifies owned categories and protects active and sibling paths", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                Layout layout = CreateLayout();

                try
                {
                    // Old mission log (reclaimable) and a fresh one (protected).
                    Directory.CreateDirectory(Path.Combine(layout.Settings.LogDirectory, "missions"));
                    string oldLog = Path.Combine(layout.Settings.LogDirectory, "missions", "msn_old.log");
                    File.WriteAllText(oldLog, "old log");
                    File.SetLastWriteTimeUtc(oldLog, DateTime.UtcNow.AddDays(-40));
                    string freshLog = Path.Combine(layout.Settings.LogDirectory, "missions", "msn_fresh.log");
                    File.WriteAllText(freshLog, "fresh log");

                    // Shared sibling dir (protected by sibling-path detection). The vessel must exist
                    // before the dock row because docks carry a foreign key to vessels.
                    Directory.CreateDirectory(layout.SiblingPath);
                    File.WriteAllText(Path.Combine(layout.SiblingPath, ".git"), "gitdir: /tmp/nowhere\n");
                    Vessel vessel = await CreateVesselWithSiblingAsync(testDb, layout).ConfigureAwait(false);

                    // Orphan dock dir (old, gitdir marker) and an active dock dir (protected).
                    Directory.CreateDirectory(layout.OrphanDockPath);
                    File.WriteAllText(Path.Combine(layout.OrphanDockPath, ".git"), "gitdir: /tmp/nowhere\n");
                    File.SetLastWriteTimeUtc(layout.OrphanDockPath, DateTime.UtcNow.AddDays(-2));

                    Directory.CreateDirectory(layout.ActiveDockPath);
                    File.WriteAllText(Path.Combine(layout.ActiveDockPath, ".git"), "gitdir: /tmp/nowhere\n");
                    await CreateActiveDockAsync(testDb, vessel.Id, layout.ActiveDockPath).ConfigureAwait(false);

                    // Old backup past retention, a mid one also past retention, and a fresh one;
                    // the newest MinBackupCount (2) are always protected.
                    Directory.CreateDirectory(layout.BackupsPath);
                    string oldBackup = Path.Combine(layout.BackupsPath, "old.zip");
                    File.WriteAllText(oldBackup, "old backup");
                    File.SetLastWriteTimeUtc(oldBackup, DateTime.UtcNow.AddDays(-40));
                    string midBackup = Path.Combine(layout.BackupsPath, "mid.zip");
                    File.WriteAllText(midBackup, "mid backup");
                    File.SetLastWriteTimeUtc(midBackup, DateTime.UtcNow.AddDays(-20));
                    string freshBackup = Path.Combine(layout.BackupsPath, "fresh.zip");
                    File.WriteAllText(freshBackup, "fresh backup");

                    DiskLifecycleService service = new DiskLifecycleService(testDb.Driver, layout.Settings, logging);
                    DiskLifecycleReport report = await service.ScanAsync().ConfigureAwait(false);

                    DiskLifecycleCategory docks = report.Categories.First(c => c.Category == "docks");
                    DiskLifecycleCategory logs = report.Categories.First(c => c.Category == "missionLogs");
                    DiskLifecycleCategory backups = report.Categories.First(c => c.Category == "backups");

                    AssertTrue(
                        report.Actions.Any(a => a.Category == "docks" && a.Path == layout.OrphanDockPath && a.Disposition == "dry-run-reclaim"),
                        "Old orphan dock dir must be flagged reclaimable.");
                    AssertTrue(
                        report.Actions.Any(a => a.Category == "docks" && a.Path == layout.ActiveDockPath && a.Disposition == "protected"),
                        "Active dock dir must be protected.");
                    AssertTrue(
                        report.Actions.Any(a => a.Category == "docks" && a.Path == layout.SiblingPath && a.Disposition == "protected"),
                        "Shared sibling dir must be protected from the orphan sweep.");
                    AssertTrue(
                        report.Actions.Any(a => a.Category == "missionLogs" && a.Path == oldLog && a.Disposition == "dry-run-reclaim"),
                        "Expired mission log must be flagged reclaimable.");
                    AssertFalse(
                        report.Actions.Any(a => a.Category == "missionLogs" && a.Path == freshLog),
                        "Fresh mission log must not be flagged.");
                    AssertTrue(
                        report.Actions.Any(a => a.Category == "backups" && a.Path == oldBackup && a.Disposition == "dry-run-reclaim"),
                        "Backup past retention must be flagged reclaimable.");
                    AssertEqual(2, backups.ProtectedItems, "Newest backups must be protected by the minimum-count rule.");
                    AssertTrue(report.DryRun, "Scan must always be non-destructive.");
                }
                finally
                {
                    Cleanup(layout);
                }
            }).ConfigureAwait(false);

            await RunTest("Scan judges a nested dock by its checkout: an old nested orphan is reclaimable, an active nested dock is protected", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                Layout layout = CreateLayout();

                try
                {
                    Vessel vessel = await CreateVesselWithSiblingAsync(testDb, layout).ConfigureAwait(false);
                    string vesselDir = Path.Combine(layout.Settings.DocksDirectory, "ExampleVessel");

                    // Nested orphan: docks/ExampleVessel/msn_nested_orphan/ExampleVessel/.git, old.
                    string orphanRoot = Path.Combine(vesselDir, "msn_nested_orphan");
                    string orphanCheckout = Path.Combine(orphanRoot, "ExampleVessel");
                    Directory.CreateDirectory(orphanCheckout);
                    File.WriteAllText(Path.Combine(orphanCheckout, ".git"), "gitdir: /tmp/nowhere\n");
                    Directory.CreateDirectory(Path.Combine(orphanRoot, "ExampleSibling"));
                    File.SetLastWriteTimeUtc(orphanCheckout, DateTime.UtcNow.AddDays(-2));
                    File.SetLastWriteTimeUtc(orphanRoot, DateTime.UtcNow.AddDays(-2));

                    // Nested active dock: the dock row names the CHECKOUT, one level below the root.
                    string activeRoot = Path.Combine(vesselDir, "msn_nested_active");
                    string activeCheckout = Path.Combine(activeRoot, "ExampleVessel");
                    Directory.CreateDirectory(activeCheckout);
                    File.WriteAllText(Path.Combine(activeCheckout, ".git"), "gitdir: /tmp/nowhere\n");
                    File.SetLastWriteTimeUtc(activeCheckout, DateTime.UtcNow.AddDays(-2));
                    await CreateActiveDockAsync(testDb, vessel.Id, activeCheckout).ConfigureAwait(false);

                    DiskLifecycleService service = new DiskLifecycleService(testDb.Driver, layout.Settings, logging);
                    DiskLifecycleReport report = await service.ScanAsync().ConfigureAwait(false);

                    AssertTrue(
                        report.Actions.Any(a => a.Category == "docks" && a.Path == orphanRoot && a.Disposition == "dry-run-reclaim"),
                        "An old nested orphan is reclaimable as a whole directory (its root).");
                    AssertTrue(
                        report.Actions.Any(a => a.Category == "docks" && a.Path == activeRoot && a.Disposition == "protected"),
                        "A nested dock whose checkout is an active dock is protected by its root.");
                }
                finally
                {
                    Cleanup(layout);
                }
            }).ConfigureAwait(false);

            await RunTest("Scan protects a gate-leased dock and a WorkProduced-mission dock from the orphan sweep", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                Layout layout = CreateLayout();

                try
                {
                    Vessel vessel = await CreateVesselWithSiblingAsync(testDb, layout).ConfigureAwait(false);

                    // Orphan dock dir (old, gitdir marker) stays reclaimable.
                    Directory.CreateDirectory(layout.OrphanDockPath);
                    File.WriteAllText(Path.Combine(layout.OrphanDockPath, ".git"), "gitdir: /tmp/nowhere\n");
                    File.SetLastWriteTimeUtc(layout.OrphanDockPath, DateTime.UtcNow.AddDays(-2));

                    // A dock directory whose mission is WorkProduced (mid-gate) with an old
                    // timestamp: protected by the mission-status loop even though the dock row is
                    // inactive.
                    string producedDockPath = Path.Combine(Path.GetDirectoryName(layout.ActiveDockPath)!, "msn_produced");
                    Directory.CreateDirectory(producedDockPath);
                    File.WriteAllText(Path.Combine(producedDockPath, ".git"), "gitdir: /tmp/nowhere\n");
                    File.SetLastWriteTimeUtc(producedDockPath, DateTime.UtcNow.AddDays(-2));
                    Dock producedDock = new Dock(vessel.Id)
                    {
                        WorktreePath = producedDockPath,
                        Active = false
                    };
                    producedDock = await testDb.Driver.Docks.CreateAsync(producedDock).ConfigureAwait(false);
                    Mission produced = new Mission("produced-mission", "mid-gate");
                    produced.VesselId = vessel.Id;
                    produced.Status = MissionStatusEnum.WorkProduced;
                    produced.DockId = producedDock.Id;
                    await testDb.Driver.Missions.CreateAsync(produced).ConfigureAwait(false);

                    // A dock directory pinned by a definition-of-done gate lease, also old:
                    // protected only by the lease.
                    string leasedDockPath = Path.Combine(Path.GetDirectoryName(layout.ActiveDockPath)!, "msn_leased");
                    Directory.CreateDirectory(leasedDockPath);
                    File.WriteAllText(Path.Combine(leasedDockPath, ".git"), "gitdir: /tmp/nowhere\n");
                    File.SetLastWriteTimeUtc(leasedDockPath, DateTime.UtcNow.AddDays(-2));
                    string leasedDockId = "msn_leased";
                    DockLeaseRegistry.Acquire(leasedDockId);
                    try
                    {
                        DiskLifecycleService service = new DiskLifecycleService(testDb.Driver, layout.Settings, logging);
                        DiskLifecycleReport report = await service.ScanAsync().ConfigureAwait(false);

                        AssertTrue(
                            report.Actions.Any(a => a.Category == "docks" && a.Path == layout.OrphanDockPath && a.Disposition == "dry-run-reclaim"),
                            "Old orphan dock dir must still be flagged reclaimable.");
                        AssertTrue(
                            report.Actions.Any(a => a.Category == "docks" && a.Path == producedDockPath && a.Disposition == "protected"),
                            "A WorkProduced-mission dock must be protected from the orphan sweep.");
                        AssertTrue(
                            report.Actions.Any(a => a.Category == "docks" && a.Path == leasedDockPath && a.Disposition == "protected"),
                            "A gate-leased dock directory must be protected from the orphan sweep.");
                    }
                    finally
                    {
                        DockLeaseRegistry.Release(leasedDockId);
                    }
                }
                finally
                {
                    Cleanup(layout);
                }
            }).ConfigureAwait(false);

            await RunTest("Reconcile in dry-run mode deletes nothing; enabled mode deletes only eligible items", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                Layout layout = CreateLayout();

                try
                {
                    Directory.CreateDirectory(Path.Combine(layout.Settings.LogDirectory, "missions"));
                    string oldLog = Path.Combine(layout.Settings.LogDirectory, "missions", "msn_old.log");
                    File.WriteAllText(oldLog, "old log");
                    File.SetLastWriteTimeUtc(oldLog, DateTime.UtcNow.AddDays(-40));

                    DiskLifecycleService service = new DiskLifecycleService(testDb.Driver, layout.Settings, logging);

                    // Default settings: dry-run on, deletion off.
                    DiskLifecycleReport dry = await service.ReconcileAsync().ConfigureAwait(false);
                    AssertTrue(File.Exists(oldLog), "Dry-run reconcile must not delete anything.");
                    AssertTrue(dry.DryRun, "Default reconcile must report dry-run.");
                    AssertTrue(
                        dry.Actions.Any(a => a.Path == oldLog && a.Disposition == "dry-run-reclaim"),
                        "Dry-run reconcile must record the eligible item.");

                    // Opt in: deletion allowed.
                    layout.Settings.DiskLifecycle.Enabled = true;
                    layout.Settings.DiskLifecycle.DryRun = false;
                    DiskLifecycleReport live = await service.ReconcileAsync().ConfigureAwait(false);
                    AssertFalse(File.Exists(oldLog), "Enabled reconcile must delete eligible expired items.");
                    AssertTrue(
                        live.Actions.Any(a => a.Path == oldLog && a.Disposition == "reclaimed"),
                        "Enabled reconcile must record the reclaimed item.");
                }
                finally
                {
                    Cleanup(layout);
                }
            }).ConfigureAwait(false);

            await RunTest("Temp artifacts and leftover integration worktrees are reclaimable past retention", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                Layout layout = CreateLayout();

                try
                {
                    string tempDir = Path.GetTempPath();
                    string oldCheckDir = Path.Combine(tempDir, "armada-chk-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(oldCheckDir);
                    File.WriteAllText(Path.Combine(oldCheckDir, "artifact.bin"), "data");
                    File.SetLastWriteTimeUtc(oldCheckDir, DateTime.UtcNow.AddDays(-3));

                    string oldTestDb = Path.Combine(tempDir, "armada_test_" + Guid.NewGuid().ToString("N") + ".db");
                    File.WriteAllText(oldTestDb, "sqlite");
                    File.SetLastWriteTimeUtc(oldTestDb, DateTime.UtcNow.AddDays(-3));

                    string integrationDir = Path.Combine(layout.Settings.DocksDirectory, "_integration", "msn_leftover");
                    Directory.CreateDirectory(integrationDir);
                    File.SetLastWriteTimeUtc(integrationDir, DateTime.UtcNow.AddDays(-3));

                    DiskLifecycleService service = new DiskLifecycleService(testDb.Driver, layout.Settings, logging);
                    layout.Settings.DiskLifecycle.Enabled = true;
                    layout.Settings.DiskLifecycle.DryRun = false;

                    DiskLifecycleReport report = await service.ReconcileAsync().ConfigureAwait(false);

                    AssertFalse(Directory.Exists(oldCheckDir), "Expired temp checkout dir must be reclaimed.");
                    AssertFalse(File.Exists(oldTestDb), "Expired test-db sidecar must be reclaimed.");
                    AssertFalse(Directory.Exists(integrationDir), "Leftover integration worktree must be reclaimed.");

                    DiskLifecycleCategory tempCategory = report.Categories.First(c => c.Category == "tempArtifacts");
                    AssertTrue(tempCategory.ReclaimableItems >= 2, "Temp-artifact category must account for reclaimed items.");
                }
                finally
                {
                    Cleanup(layout);
                }
            }).ConfigureAwait(false);

            await RunTest("Fail closed: a symlinked dock dir is skipped, never deleted", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                Layout layout = CreateLayout();

                bool symlinkSupported = true;
                try
                {
                    string outsideTarget = Path.Combine(layout.Settings.DataDirectory, "outside-target");
                    Directory.CreateDirectory(outsideTarget);
                    File.WriteAllText(Path.Combine(outsideTarget, ".git"), "gitdir: /tmp/nowhere\n");

                    string vesselDir = Path.Combine(layout.Settings.DocksDirectory, "ExampleVessel");
                    Directory.CreateDirectory(vesselDir);
                    string linkPath = Path.Combine(vesselDir, "evil-link");
                    try
                    {
                        Directory.CreateSymbolicLink(linkPath, outsideTarget);
                    }
                    catch
                    {
                        symlinkSupported = false;
                    }

                    if (symlinkSupported)
                    {
                        layout.Settings.DiskLifecycle.Enabled = true;
                        layout.Settings.DiskLifecycle.DryRun = false;

                        DiskLifecycleService service = new DiskLifecycleService(testDb.Driver, layout.Settings, logging);
                        DiskLifecycleReport report = await service.ReconcileAsync().ConfigureAwait(false);

                        AssertTrue(Directory.Exists(linkPath), "A symlinked dock dir must never be deleted.");
                        AssertTrue(Directory.Exists(outsideTarget), "The symlink target must never be deleted.");
                        AssertTrue(
                            report.Actions.Any(a => a.Path == linkPath && a.Disposition == "skipped"),
                            "A symlinked dock dir must be recorded as skipped.");
                    }
                }
                finally
                {
                    Cleanup(layout);
                }
            }).ConfigureAwait(false);

            await RunTest("Merge-queue worktree with a live entry is protected", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                Layout layout = CreateLayout();

                try
                {
                    MergeEntry entry = new MergeEntry("armada/test/msn_live");
                    entry.Status = MergeStatusEnum.Testing;
                    entry = await testDb.Driver.MergeEntries.CreateAsync(entry).ConfigureAwait(false);

                    string liveDir = Path.Combine(layout.Settings.DocksDirectory, "_merge-queue", entry.Id);
                    Directory.CreateDirectory(liveDir);
                    File.SetLastWriteTimeUtc(liveDir, DateTime.UtcNow.AddDays(-3));

                    string deadDir = Path.Combine(layout.Settings.DocksDirectory, "_merge-queue", "mrg_dead");
                    Directory.CreateDirectory(deadDir);
                    File.SetLastWriteTimeUtc(deadDir, DateTime.UtcNow.AddDays(-3));

                    layout.Settings.DiskLifecycle.Enabled = true;
                    layout.Settings.DiskLifecycle.DryRun = false;
                    DiskLifecycleService service = new DiskLifecycleService(testDb.Driver, layout.Settings, logging);
                    DiskLifecycleReport report = await service.ReconcileAsync().ConfigureAwait(false);

                    AssertTrue(Directory.Exists(liveDir), "A merge-queue worktree referenced by a live entry must be protected.");
                    AssertFalse(Directory.Exists(deadDir), "An unreferenced merge-queue worktree past retention must be reclaimed.");
                }
                finally
                {
                    Cleanup(layout);
                }
            }).ConfigureAwait(false);
        }

        #region Private-Methods

        private static LoggingModule CreateLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        private sealed class Layout
        {
            public Armada.Core.Settings.ArmadaSettings Settings { get; set; } = null!;

            public string OrphanDockPath { get; set; } = String.Empty;

            public string ActiveDockPath { get; set; } = String.Empty;

            public string SiblingPath { get; set; } = String.Empty;

            public string BackupsPath { get; set; } = String.Empty;
        }

        private static Layout CreateLayout()
        {
            Armada.Core.Settings.ArmadaSettings settings = new Armada.Core.Settings.ArmadaSettings();
            settings.DataDirectory = Path.Combine(Path.GetTempPath(), "armada-disklife-" + Guid.NewGuid().ToString("N"));
            settings.DocksDirectory = Path.Combine(settings.DataDirectory, "docks");
            settings.ReposDirectory = Path.Combine(settings.DataDirectory, "repos");
            settings.LogDirectory = Path.Combine(settings.DataDirectory, "logs");

            string vesselDir = Path.Combine(settings.DocksDirectory, "ExampleVessel");
            Directory.CreateDirectory(vesselDir);

            return new Layout
            {
                Settings = settings,
                OrphanDockPath = Path.Combine(vesselDir, "msn_orphan"),
                ActiveDockPath = Path.Combine(vesselDir, "msn_active"),
                SiblingPath = Path.Combine(vesselDir, "ExampleSibling"),
                BackupsPath = Path.Combine(settings.DataDirectory, "backups")
            };
        }

        private static void Cleanup(Layout layout)
        {
            try
            {
                if (Directory.Exists(layout.Settings.DataDirectory))
                {
                    Directory.Delete(layout.Settings.DataDirectory, true);
                }
            }
            catch
            {
            }
        }

        private static async Task<Dock> CreateActiveDockAsync(TestDatabase testDb, string vesselId, string worktreePath)
        {
            Dock dock = new Dock(vesselId);
            dock.WorktreePath = worktreePath;
            return await testDb.Driver.Docks.CreateAsync(dock).ConfigureAwait(false);
        }

        private static async Task<Vessel> CreateVesselWithSiblingAsync(TestDatabase testDb, Layout layout)
        {
            Vessel vessel = new Vessel("vsl_example", "https://github.com/test/repo.git");
            vessel.Name = "ExampleVessel";
            vessel.SiblingRepos = System.Text.Json.JsonSerializer.Serialize(new[]
            {
                new SiblingRepo { RelativePath = "../ExampleSibling", RepoUrl = "https://github.com/test/ExampleSibling.git" }
            });
            return await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);
        }

        #endregion
    }
}
