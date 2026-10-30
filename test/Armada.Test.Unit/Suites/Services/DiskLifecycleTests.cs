namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Archive;
    using Armada.Core.Settings;
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
            await RunTest("Deletion is refused for a path that matches an allowed root only when case is ignored", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                // Outside the temp directory and the data directory, which are allowed roots themselves.
                string baseDirectory = Path.Combine(AppContext.BaseDirectory, "disklife-case-" + Guid.NewGuid().ToString("N"));
                string allowedRoot = Path.Combine(baseDirectory, "allowed");
                string caseVariant = Path.Combine(baseDirectory, "ALLOWED", "victim");
                try
                {
                    Directory.CreateDirectory(Path.Combine(allowedRoot, "reclaimable"));
                    Directory.CreateDirectory(caseVariant);
                    Layout layout = CreateLayout();
                    try
                    {
                        layout.Settings.DiskLifecycle.AllowedRoots.Add(allowedRoot);
                        DiskLifecycleService service = new DiskLifecycleService(testDb.Driver, layout.Settings, CreateLogging());

                        AssertTrue(service.IsPathAllowed(Path.Combine(allowedRoot, "reclaimable")), "a path below an allowed root may be deleted");
                        AssertFalse(service.IsPathAllowed(caseVariant),
                            "a path under a directory whose name differs from the allowed root only by case is not inside it");
                    }
                    finally
                    {
                        Cleanup(layout);
                    }
                }
                finally
                {
                    if (Directory.Exists(baseDirectory)) Directory.Delete(baseDirectory, true);
                }
            });

            await RunTest("Scan classifies owned categories and protects active and sibling paths", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                Layout layout = CreateLayout();
                // History is deleted without a copy only when keeping it is turned off.
                layout.Settings.Archive.KeepHistoryWithoutArchive = false;

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

            // With the protected docks unreadable, any dock might belong to a live mission; the sweep must
            // not judge docks against a partial protected set.
            await RunTest("The orphan-dock scan is skipped and reported when the protected docks cannot be read", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Layout layout = CreateLayout();
                try
                {
                    await CreateVesselWithSiblingAsync(testDb, layout).ConfigureAwait(false);
                    Directory.CreateDirectory(layout.OrphanDockPath);
                    File.WriteAllText(Path.Combine(layout.OrphanDockPath, ".git"), "gitdir: /tmp/nowhere\n");
                    File.SetLastWriteTimeUtc(layout.OrphanDockPath, DateTime.UtcNow.AddDays(-2));
                    using (Microsoft.Data.Sqlite.SqliteConnection conn = new Microsoft.Data.Sqlite.SqliteConnection(testDb.ConnectionString))
                    {
                        await conn.OpenAsync().ConfigureAwait(false);
                        using (Microsoft.Data.Sqlite.SqliteCommand cmd = conn.CreateCommand())
                        {
                            cmd.CommandText = "ALTER TABLE missions RENAME TO missions_unreadable;";
                            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                        }
                    }

                    DiskLifecycleReport report = await new DiskLifecycleService(testDb.Driver, layout.Settings, CreateLogging()).ScanAsync().ConfigureAwait(false);

                    AssertFalse(report.Actions.Any(a => a.Category == "docks" && a.Path == layout.OrphanDockPath && a.Disposition == "dry-run-reclaim"),
                        "No dock is judged against a partial protected set");
                    AssertTrue(report.Errors.Any(e => e.Contains("protected docks could not be read", StringComparison.Ordinal)), "The skipped scan is reported");
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
                // History is deleted without a copy only when keeping it is turned off.
                layout.Settings.Archive.KeepHistoryWithoutArchive = false;

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

            await RunTest("With the archive active an expired history file is uploaded and verified before it is freed, and the pass writes a manifest", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Layout layout = CreateLayout();

                try
                {
                    string oldLog = CreateRetentionFile(Path.Combine(layout.Settings.LogDirectory, "missions"), "msn_old.log", DateTime.UtcNow.AddDays(-40));
                    string freshLog = CreateRetentionFile(Path.Combine(layout.Settings.LogDirectory, "missions"), "msn_fresh.log", DateTime.UtcNow);
                    FakeArchiveSink sink = new FakeArchiveSink();
                    DiskLifecycleService service = CreateArchivingService(testDb, layout, sink);

                    DiskLifecycleReport dry = await service.ScanAsync().ConfigureAwait(false);
                    AssertEqual(0, sink.Objects.Count, "A scan uploads nothing");
                    AssertTrue(dry.Actions.Any(a => a.Path == oldLog && a.Disposition == "dry-run-reclaim"), "A scan reports the file it would archive");

                    layout.Settings.DiskLifecycle.Enabled = true;
                    layout.Settings.DiskLifecycle.DryRun = false;
                    DiskLifecycleReport live = await service.ReconcileAsync().ConfigureAwait(false);

                    string key = sink.Objects.Keys.Single(k => k.EndsWith("/msn_old.log", StringComparison.Ordinal));
                    AssertTrue(key.StartsWith("armada/raw/missionLogs/", StringComparison.Ordinal), "The log is stored under the raw mission-log prefix: " + key);
                    AssertEqual(new string('d', 1024), System.Text.Encoding.UTF8.GetString(sink.Objects[key]), "The stored object is the file's content");
                    AssertFalse(File.Exists(oldLog), "The archived log is freed");
                    AssertTrue(File.Exists(freshLog), "A log inside its retention stays");
                    AssertEqual("Archive", live.HistoryMode, "The pass reports archive mode");
                    AssertEqual(1, live.ArchivedItems, "One file is archived");
                    AssertTrue(live.Actions.Any(a => a.Path == oldLog && a.Disposition == "reclaimed" && a.Reason.Contains(key, StringComparison.Ordinal)), "The action names the object key");
                    AssertTrue(live.ArchiveManifestKey != null && live.ArchiveManifestKey.StartsWith("armada/raw/manifests/", StringComparison.Ordinal), "The pass uploads a manifest: " + live.ArchiveManifestKey);
                    string manifest = System.Text.Encoding.UTF8.GetString(sink.Objects[live.ArchiveManifestKey!]);
                    AssertTrue(manifest.Contains(key, StringComparison.Ordinal) && manifest.Contains("\"verified\":true", StringComparison.Ordinal), "The manifest records the verified object: " + manifest);
                }
                finally
                {
                    Cleanup(layout);
                }
            }).ConfigureAwait(false);

            await RunTest("An upload that fails verification keeps the file and records one event", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Layout layout = CreateLayout();

                try
                {
                    string failing = CreateRetentionFile(Path.Combine(layout.Settings.LogDirectory, "missions"), "msn_fail.log", DateTime.UtcNow.AddDays(-40));
                    string passing = CreateRetentionFile(Path.Combine(layout.Settings.LogDirectory, "missions"), "msn_pass.log", DateTime.UtcNow.AddDays(-40));
                    FakeArchiveSink sink = new FakeArchiveSink { FailKeysContaining = "msn_fail" };
                    DiskLifecycleService service = CreateArchivingService(testDb, layout, sink);
                    layout.Settings.DiskLifecycle.Enabled = true;
                    layout.Settings.DiskLifecycle.DryRun = false;

                    DiskLifecycleReport live = await service.ReconcileAsync().ConfigureAwait(false);

                    AssertTrue(File.Exists(failing), "A file whose upload was not verified is kept");
                    AssertFalse(File.Exists(passing), "A verified file in the same pass is freed");
                    AssertEqual(1, live.ArchiveFailures, "The report counts the failed upload");
                    AssertTrue(live.Actions.Any(a => a.Path == failing && a.Disposition == "skipped" && a.Reason.Contains("sha256_mismatch", StringComparison.Ordinal)), "The action names the failure code");
                    List<ArmadaEvent> events = await testDb.Driver.Events.EnumerateByTypeAsync("disk_lifecycle.archive_failed", 10).ConfigureAwait(false);
                    AssertEqual(1, events.Count, "One archive-failure event is recorded");
                    AssertTrue(events[0].Message!.Contains(failing, StringComparison.Ordinal), "The event names the kept file");
                    AssertTrue(events[0].Message!.Contains("kept locally", StringComparison.Ordinal), "The event says the file is kept: " + events[0].Message);

                    File.WriteAllText(layout.Settings.Archive.CredentialsFile, "accessKeyId=exampleaccess\nsecretAccessKey=examplesecret\n");
                    using (HttpClient receiptClient = new HttpClient(new StubHttpMessageHandler(HttpStatusCode.InternalServerError,
                        "<Error><Code>InternalError</Code><Message>PRIVATE_RESPONSE</Message></Error>",
                        new Dictionary<string, string[]> { { "x-amz-request-id", new[] { "retained-request-42" } } })))
                    {
                        service.ArchiveSinkFactory = settings => new S3ArchiveSink(settings, receiptClient, retryDelay: (delay, token) => Task.CompletedTask);
                        DiskLifecycleReport failed = await service.ReconcileAsync().ConfigureAwait(false);
                        AssertTrue(File.Exists(failing), "HTTP500 keeps the unverified local file");
                        AssertTrue(failed.ArchiveFailures > 0);
                        AssertTrue(failed.Actions.Any(action => action.Path == failing && action.Disposition == "skipped"
                            && action.Reason.Contains("upload_failed", StringComparison.Ordinal)),
                            "the action retains the existing failure-code contract");
                        List<ArmadaEvent> receipts = await testDb.Driver.Events.EnumerateByTypeAsync("disk_lifecycle.archive_failed", 10).ConfigureAwait(false);
                        AssertTrue(receipts.Any(item => item.Message!.Contains("request_id=retained-request-42", StringComparison.Ordinal)),
                            "the durable failure event retains the provider support receipt");
                        AssertFalse(receipts.Any(item => item.Message!.Contains("PRIVATE_RESPONSE", StringComparison.Ordinal)),
                            "the event never carries the provider response body");
                    }
                }
                finally
                {
                    Cleanup(layout);
                }
            }).ConfigureAwait(false);

            foreach (string recovery in new[] { "put-recovers", "ambiguous-put", "head-recovers", "put-exhausted", "head-exhausted", "put-forbidden", "verify-checksum", "retry-after", "cancel-backoff", "budget-backoff" })
            {
                await RunTest("S3 archive retry preserves verification and request identity: " + recovery, async () =>
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        Layout layout = CreateLayout();
                        try
                        {
                            string file = CreateRetentionFile(Path.Combine(layout.Settings.LogDirectory, "missions"), "msn_retry.log", DateTime.UtcNow.AddDays(-40));
                            byte[] expected = await File.ReadAllBytesAsync(file).ConfigureAwait(false);
                            string sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(expected)).ToLowerInvariant();
                            string md5 = Convert.ToBase64String(System.Security.Cryptography.MD5.HashData(expected));
                            DiskLifecycleService service = CreateArchivingService(testDb, layout, new FakeArchiveSink());
                            layout.Settings.DiskLifecycle.Enabled = true;
                            layout.Settings.DiskLifecycle.DryRun = false;
                            File.WriteAllText(layout.Settings.Archive.CredentialsFile, "accessKeyId=exampleaccess\nsecretAccessKey=examplesecret\n");
                            Dictionary<string, byte[]> stored = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                            Dictionary<string, string> hashes = new Dictionary<string, string>(StringComparer.Ordinal);
                            HashSet<HttpRequestMessage> requests = new HashSet<HttpRequestMessage>();
                            HashSet<HttpContent> contents = new HashSet<HttpContent>();
                            HashSet<string> signatures = new HashSet<string>(StringComparer.Ordinal);
                            List<System.IO.Stream> streams = new List<System.IO.Stream>();
                            int puts = 0;
                            int headsAfterPut = 0;
                            int targetHeads = 0;
                            int clocks = 0;
                            List<TimeSpan> delays = new List<TimeSpan>();
                            using (CancellationTokenSource caller = new CancellationTokenSource())
                            using (HttpClient http = new HttpClient(new StubHttpMessageHandler(async (request, token) =>
                            {
                                string path = request.RequestUri!.AbsolutePath;
                                bool target = path.EndsWith("/msn_retry.log", StringComparison.Ordinal);
                                if (request.Method == HttpMethod.Put)
                                {
                                    System.IO.Stream stream = await request.Content!.ReadAsStreamAsync(token).ConfigureAwait(false);
                                    using (MemoryStream body = new MemoryStream())
                                    {
                                        await stream.CopyToAsync(body, token).ConfigureAwait(false);
                                        byte[] data = body.ToArray();
                                        if (target)
                                        {
                                            puts++;
                                            AssertTrue(data.SequenceEqual(expected), "every PUT reopens and sends the entire original file");
                                            AssertEqual(md5, Convert.ToBase64String(request.Content.Headers.ContentMD5!), "each fresh content keeps its MD5");
                                            AssertEqual(sha, request.Headers.GetValues(S3ArchiveSink.Sha256MetadataHeader).Single(), "each request keeps the expected SHA");
                                            AssertEqual((long)expected.Length, request.Content.Headers.ContentLength!.Value, "each request keeps the length");
                                            requests.Add(request); contents.Add(request.Content); streams.Add(stream);
                                            signatures.Add(request.Headers.GetValues("Authorization").Single());
                                        }
                                        bool fails = target && (recovery is "put-exhausted" or "put-forbidden" or "retry-after"
                                            || recovery is "put-recovers" or "ambiguous-put" or "cancel-backoff" or "budget-backoff" && puts == 1);
                                        if (!fails || recovery == "ambiguous-put")
                                        {
                                            stored[path] = data;
                                            hashes[path] = request.Headers.GetValues(S3ArchiveSink.Sha256MetadataHeader).Single();
                                        }
                                        if (fails)
                                        {
                                            HttpStatusCode status = recovery == "put-forbidden" ? HttpStatusCode.Forbidden
                                                : recovery == "retry-after" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.InternalServerError;
                                            HttpResponseMessage failure = new HttpResponseMessage(status) { Content = new StringContent("<Error><Code>InternalError</Code><Message>PRIVATE_RESPONSE</Message></Error>") };
                                            failure.Headers.TryAddWithoutValidation("x-amz-request-id", "retry-" + puts);
                                            if (recovery == "retry-after") failure.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromHours(1));
                                            return failure;
                                        }
                                        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(String.Empty) };
                                    }
                                }
                                if (request.Method == HttpMethod.Head)
                                {
                                    if (target) targetHeads++;
                                    if (!stored.ContainsKey(path)) return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent(String.Empty) };
                                    if (target)
                                    {
                                        headsAfterPut++;
                                        if (recovery == "head-exhausted" || recovery == "head-recovers" && headsAfterPut == 1)
                                            return new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("<Error><Code>InternalError</Code></Error>") };
                                    }
                                    HttpResponseMessage head = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Array.Empty<byte>()) };
                                    head.Content.Headers.ContentLength = stored[path].LongLength;
                                    head.Headers.TryAddWithoutValidation(S3ArchiveSink.Sha256MetadataHeader, target && recovery == "verify-checksum" ? new string('0', 64) : hashes[path]);
                                    return head;
                                }
                                throw new InvalidOperationException("Unexpected archive request method");
                            })))
                            {
                                if (recovery == "budget-backoff") http.Timeout = TimeSpan.FromSeconds(2);
                                S3ArchiveSink sink = new S3ArchiveSink(layout.Settings.Archive, http,
                                    () => new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(++clocks),
                                    (delay, token) =>
                                    {
                                        AssertTrue(streams.All(stream => !stream.CanRead), "PUT streams close before any retry backoff");
                                        delays.Add(delay);
                                        if (recovery == "budget-backoff") return Task.Delay(Timeout.InfiniteTimeSpan, token);
                                        if (recovery == "cancel-backoff") caller.Cancel();
                                        token.ThrowIfCancellationRequested();
                                        return Task.CompletedTask;
                                    });
                                service.ArchiveSinkFactory = settings => sink;
                                if (recovery == "budget-backoff")
                                {
                                    ArchivePutResult expired = await sink.PutFileAsync("raw/msn_retry.log", file, sha, caller.Token).ConfigureAwait(false);
                                    AssertFalse(expired.Verified, "operation budget cannot verify an unfinished upload");
                                    AssertEqual("upload_failed", expired.FailureCode, "budget expiry retains the upload failure");
                                    AssertFalse(caller.IsCancellationRequested, "operation expiry is separate from caller cancellation");
                                    AssertEqual(1, puts, "operation expiry prevents another PUT");
                                    AssertEqual(0, targetHeads, "operation expiry prevents a verification request");
                                    AssertTrue(File.Exists(file), "the operation budget keeps the unverified source");
                                    return;
                                }
                                if (recovery == "cancel-backoff")
                                {
                                    bool cancelled = false;
                                    try { await sink.PutFileAsync("raw/msn_retry.log", file, sha, caller.Token).ConfigureAwait(false); }
                                    catch (OperationCanceledException) when (caller.IsCancellationRequested) { cancelled = true; }
                                    AssertTrue(cancelled, "caller cancellation propagates during backoff");
                                    AssertEqual(1, puts, "cancellation prevents a second upload");
                                    AssertTrue(File.Exists(file), "cancellation retains the unverified source");
                                    return;
                                }
                                DiskLifecycleReport report = await service.ReconcileAsync().ConfigureAwait(false);
                                bool success = recovery is "put-recovers" or "ambiguous-put" or "head-recovers";
                                AssertEqual(!success, File.Exists(file), "only verified archive bytes permit release: " + recovery);
                                AssertEqual(success ? 0 : 1, report.ArchiveFailures, "the report preserves the final verification outcome: " + recovery);
                                AssertEqual(recovery == "put-recovers" ? 2 : recovery == "put-exhausted" ? 3 : 1, puts, "PUT attempts stay bounded and successful or ambiguous stored uploads are not repeated: " + recovery);
                                AssertEqual(puts, requests.Count, "each PUT has a fresh request");
                                AssertEqual(puts, contents.Count, "each PUT has fresh content");
                                AssertEqual(puts, signatures.Count, "each PUT is signed with the advancing request clock");
                                AssertTrue(streams.All(stream => !stream.CanRead), "request streams are disposed before the pass completes");
                                int expectedDelays = recovery is "put-exhausted" or "head-exhausted" ? 2
                                    : recovery is "put-recovers" or "ambiguous-put" or "head-recovers" ? 1 : 0;
                                AssertEqual(expectedDelays, delays.Count, "backoff occurs only for bounded transient recovery");
                                for (int i = 0; i < delays.Count; i++) AssertEqual(TimeSpan.FromSeconds(Math.Pow(2, i)), delays[i], "backoff increases between attempts");
                                if (recovery == "head-recovers") AssertEqual(2, headsAfterPut, "verification retries HEAD without another PUT");
                                if (recovery == "head-exhausted") AssertEqual(3, headsAfterPut, "verification exhaustion is bounded");
                                if (recovery == "put-exhausted")
                                {
                                    List<ArmadaEvent> failures = await testDb.Driver.Events.EnumerateByTypeAsync("disk_lifecycle.archive_failed", 10).ConfigureAwait(false);
                                    AssertTrue(failures.Any(evt => evt.Message!.Contains("request_id=retry-3", StringComparison.Ordinal)), "exhaustion retains the last provider support receipt");
                                    AssertFalse(failures.Any(evt => evt.Message!.Contains("PRIVATE_RESPONSE", StringComparison.Ordinal)), "provider message bodies remain private");
                                }
                            }
                        }
                        finally { Cleanup(layout); }
                    }
                }).ConfigureAwait(false);
            }

            await RunTest("A read-back cap stops the run after the first cap, keeps the rest locally, and reports one cap event", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Layout layout = CreateLayout();

                try
                {
                    string missions = Path.Combine(layout.Settings.LogDirectory, "missions");
                    List<string> logs = new List<string>
                    {
                        CreateRetentionFile(missions, "msn_a.log", DateTime.UtcNow.AddDays(-40)),
                        CreateRetentionFile(missions, "msn_b.log", DateTime.UtcNow.AddDays(-40)),
                        CreateRetentionFile(missions, "msn_c.log", DateTime.UtcNow.AddDays(-40))
                    };
                    FakeArchiveSink sink = new FakeArchiveSink { CapExceeded = true };
                    DiskLifecycleService service = CreateArchivingService(testDb, layout, sink);
                    layout.Settings.DiskLifecycle.Enabled = true;
                    layout.Settings.DiskLifecycle.DryRun = false;

                    DiskLifecycleReport live = await service.ReconcileAsync().ConfigureAwait(false);

                    AssertEqual(3, logs.Count(File.Exists), "Every history file is kept locally when the read-back cap is hit");
                    AssertEqual(1, sink.VerifyCalls, "The run verifies once and then stops hitting the store, so no further capped request is sent");
                    AssertFalse(sink.Objects.Keys.Any(k => k.EndsWith(".log", StringComparison.Ordinal)), "No history file is uploaded once the cap is reached");
                    AssertTrue(live.ArchiveTransactionCapReached, "The report flags the transaction cap");
                    AssertEqual(3, live.ArchiveSkippedForCap, "The report counts every history item kept for the cap");
                    AssertEqual(0, live.ArchiveFailures, "A cap is not counted as per-file verify failures");
                    AssertFalse(live.Actions.Any(a => a.Reason.Contains("verify_failed", StringComparison.Ordinal)), "No per-file verify_failed is recorded for the cap");
                    List<ArmadaEvent> capEvents = await testDb.Driver.Events.EnumerateByTypeAsync("disk_lifecycle.archive_cap_reached", 10).ConfigureAwait(false);
                    AssertEqual(1, capEvents.Count, "Exactly one cap event is recorded for the whole run");
                    List<ArmadaEvent> failEvents = await testDb.Driver.Events.EnumerateByTypeAsync("disk_lifecycle.archive_failed", 10).ConfigureAwait(false);
                    AssertEqual(0, failEvents.Count, "No per-file archive-failure event is recorded for the cap");
                    AssertTrue(capEvents[0].Message!.Contains("cap", StringComparison.OrdinalIgnoreCase) && capEvents[0].Message!.Contains("locally", StringComparison.Ordinal),
                        "The single event says the files are kept for the cap: " + capEvents[0].Message);
                }
                finally
                {
                    Cleanup(layout);
                }
            }).ConfigureAwait(false);

            await RunTest("The S3 sink reads a 403 cap body as a cap refusal and a non-cap 403 as a per-object verify failure", async () =>
            {
                Layout layout = CreateLayout();
                try
                {
                    Directory.CreateDirectory(layout.Settings.DataDirectory);
                    string credentials = Path.Combine(layout.Settings.DataDirectory, "archive-credentials");
                    File.WriteAllText(credentials, "accessKeyId=exampleaccess\nsecretAccessKey=examplesecret\n");
                    ArchiveSettings settings = new ArchiveSettings
                    {
                        Enabled = true,
                        Endpoint = "https://store.example.invalid",
                        Bucket = "history",
                        CredentialsFile = credentials
                    };

                    string capBody = "<Error><Code>403</Code><Message>Cap exceeded. Transaction cap exceeded for this account.</Message></Error>";
                    using (HttpClient capClient = new HttpClient(new StubHttpMessageHandler(HttpStatusCode.Forbidden, capBody)))
                    {
                        S3ArchiveSink sink = new S3ArchiveSink(settings, capClient);
                        ArchivePutResult result = await sink.VerifyStoredAsync("armada/raw/missionLogs/x/one.log", 10, "abc", CancellationToken.None).ConfigureAwait(false);
                        AssertEqual(ArchivePutResult.CapExceededCode, result.FailureCode, "A 403 whose body names a cap is a cap refusal");
                        AssertFalse(result.Verified, "A cap refusal is not a verified read-back");
                    }

                    string authBody = "<Error><Code>AccessDenied</Code><Message>Access Denied</Message></Error>";
                    using (HttpClient authClient = new HttpClient(new StubHttpMessageHandler(HttpStatusCode.Forbidden, authBody)))
                    {
                        S3ArchiveSink sink = new S3ArchiveSink(settings, authClient);
                        ArchivePutResult result = await sink.VerifyStoredAsync("armada/raw/missionLogs/x/two.log", 10, "abc", CancellationToken.None).ConfigureAwait(false);
                        AssertEqual("verify_failed", result.FailureCode, "A 403 that does not name a cap stays a per-object verify failure, not a cap");
                    }

                    string errorBody = "<Error><Code>InternalError</Code><Message>PRIVATE_RESPONSE</Message><RequestId>xml-request-42</RequestId><HostId>xml-host+/=</HostId></Error>";
                    using (HttpClient failedClient = new HttpClient(new StubHttpMessageHandler(HttpStatusCode.InternalServerError, errorBody,
                        new Dictionary<string, string[]> { { "x-amz-request-id", new[] { "header-request-42" } }, { "x-amz-id-2", new[] { "header-host+/=" } }, { "X-Private", new[] { "PRIVATE_HEADER" } } })))
                    {
                        S3ArchiveSink sink = new S3ArchiveSink(settings, failedClient);
                        ArchivePutResult put = await sink.PutBytesAsync("diagnostic.log", new byte[] { 1, 2 }, "text/plain").ConfigureAwait(false);
                        AssertEqual("upload_failed", put.FailureCode);
                        AssertFalse(put.Verified);
                        AssertContains("HTTP 500 InternalError", put.FailureMessage!);
                        AssertContains("request_id=header-request-42", put.FailureMessage!);
                        AssertContains("host_id=header-host+/=", put.FailureMessage!);
                        AssertFalse(put.FailureMessage!.Contains("PRIVATE_RESPONSE", StringComparison.Ordinal), "provider message bodies are not copied into diagnostics");
                        ArchivePutResult head = await sink.VerifyStoredAsync("diagnostic.log", 2, "abc").ConfigureAwait(false);
                        AssertEqual("verify_failed", head.FailureCode);
                        AssertContains("request_id=header-request-42", head.FailureMessage!);
                        AssertFalse(put.FailureMessage!.Contains("PRIVATE_HEADER", StringComparison.Ordinal));
                        AssertFalse(put.FailureMessage!.Contains("xml-request", StringComparison.Ordinal), "only allowlisted response headers are included");
                    }
                    foreach (string[] unsafeId in new[] { new[] { "unsafe id" }, new[] { new string('x', 513) }, new[] { "one", "two" } })
                    {
                        using (HttpClient unsafeClient = new HttpClient(new StubHttpMessageHandler(HttpStatusCode.InternalServerError,
                            "<Error><Code>InternalError</Code><Message>PRIVATE_RESPONSE</Message></Error>",
                            new Dictionary<string, string[]> { { "x-amz-request-id", unsafeId }, { "x-amz-id-2", unsafeId } })))
                        {
                            ArchivePutResult failed = await new S3ArchiveSink(settings, unsafeClient).PutBytesAsync(
                                "diagnostic.log", new byte[] { 1 }, "text/plain").ConfigureAwait(false);
                            AssertEqual("upload_failed", failed.FailureCode);
                            AssertFalse(failed.FailureMessage!.Contains("request_id=", StringComparison.Ordinal));
                            AssertFalse(failed.FailureMessage!.Contains("host_id=", StringComparison.Ordinal));
                            AssertFalse(failed.FailureMessage!.Contains("PRIVATE_RESPONSE", StringComparison.Ordinal));
                        }
                    }
                }
                finally
                {
                    Cleanup(layout);
                }
            }).ConfigureAwait(false);

            await RunTest("With no archive configured expired history is kept by default, and an enabled but incomplete archive is reported", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Layout layout = CreateLayout();

                try
                {
                    string oldLog = CreateRetentionFile(Path.Combine(layout.Settings.LogDirectory, "missions"), "msn_old.log", DateTime.UtcNow.AddDays(-40));
                    string oldDiff = CreateRetentionFile(Path.Combine(layout.Settings.LogDirectory, "diffs"), "msn_old.diff", DateTime.UtcNow.AddDays(-40));
                    string oldFinal = CreateRetentionFile(Path.Combine(layout.Settings.LogDirectory, "final-messages"), "msn_old.md", DateTime.UtcNow.AddDays(-40));
                    string expired = CreateEvidenceFolder(Path.Combine(layout.Settings.LogDirectory, "operator-evidence"), "proof-v0", DateTime.UtcNow.AddDays(-20));
                    layout.Settings.DiskLifecycle.OperatorEvidenceRetentionDays = 14;
                    layout.Settings.DiskLifecycle.Enabled = true;
                    layout.Settings.DiskLifecycle.DryRun = false;
                    DiskLifecycleService service = new DiskLifecycleService(testDb.Driver, layout.Settings, CreateLogging());
                    service.FreeBytesProbe = _ => 500L * 1024 * 1024 * 1024;

                    DiskLifecycleReport live = await service.ReconcileAsync().ConfigureAwait(false);

                    AssertEqual("Keep", live.HistoryMode, "Default settings keep history");
                    AssertTrue(File.Exists(oldLog) && File.Exists(oldDiff) && File.Exists(oldFinal), "Expired logs, diffs and final messages are kept");
                    AssertTrue(File.Exists(Path.Combine(expired, "receipt.json")), "An idle evidence folder is not removed whole");
                    AssertTrue(live.Actions.Any(a => a.Path == oldLog && a.Disposition == "kept" && a.Reason.Contains("no archive is configured", StringComparison.Ordinal)), "The kept file names why");

                    layout.Settings.Archive.Enabled = true;
                    DiskLifecycleReport incomplete = await service.ReconcileAsync().ConfigureAwait(false);
                    AssertTrue(File.Exists(oldLog), "An enabled archive without endpoint, bucket or credentials still keeps history");
                    AssertTrue(incomplete.Errors.Any(e => e.Contains("archive is enabled but", StringComparison.Ordinal)), "The incomplete archive is reported: " + String.Join(" | ", incomplete.Errors));
                }
                finally
                {
                    Cleanup(layout);
                }
            }).ConfigureAwait(false);

            await RunTest("Deny-listed files are neither uploaded nor freed", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Layout layout = CreateLayout();

                try
                {
                    string root = Path.Combine(layout.Settings.LogDirectory, "instructions");
                    string auth = CreateRetentionFile(root, "auth.json", DateTime.UtcNow.AddDays(-40));
                    string settingsBackup = CreateRetentionFile(root, "settings.json.bak", DateTime.UtcNow.AddDays(-40));
                    string custom = CreateRetentionFile(root, "operator.private", DateTime.UtcNow.AddDays(-40));
                    string brief = CreateRetentionFile(root, "msn_old.md", DateTime.UtcNow.AddDays(-40));
                    FakeArchiveSink sink = new FakeArchiveSink();
                    DiskLifecycleService service = CreateArchivingService(testDb, layout, sink);
                    layout.Settings.Archive.DenyPatterns.Add("*.private");
                    layout.Settings.DiskLifecycle.Enabled = true;
                    layout.Settings.DiskLifecycle.DryRun = false;

                    DiskLifecycleReport live = await service.ReconcileAsync().ConfigureAwait(false);

                    AssertTrue(File.Exists(auth) && File.Exists(settingsBackup) && File.Exists(custom), "Deny-listed files stay on disk");
                    AssertFalse(sink.Objects.Keys.Any(k => k.Contains("auth.json", StringComparison.Ordinal) || k.Contains("settings.json", StringComparison.Ordinal) || k.Contains(".private", StringComparison.Ordinal)),
                        "No deny-listed file is uploaded: " + String.Join(", ", sink.Objects.Keys));
                    AssertFalse(File.Exists(brief), "An ordinary instruction snapshot is archived and freed");
                    AssertEqual(3, live.Actions.Count(a => a.Disposition == "kept" && a.Reason.Contains("deny list", StringComparison.Ordinal)), "Each deny-listed file is reported kept");
                }
                finally
                {
                    Cleanup(layout);
                }
            }).ConfigureAwait(false);

            await RunTest("A captain session transcript is freed only after a verified upload, never by a plain delete", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Layout layout = CreateLayout();

                try
                {
                    string sessions = Path.Combine(layout.Settings.DataDirectory, "captain-sessions");
                    string old = CreateRetentionFile(Path.Combine(sessions, "project-a"), "session-old.jsonl", DateTime.UtcNow.AddDays(-45));
                    string fresh = CreateRetentionFile(Path.Combine(sessions, "project-a"), "session-new.jsonl", DateTime.UtcNow);
                    layout.Settings.Archive.SessionRoots.Add(new ArchiveSessionRoot { Path = sessions, Name = "runtime-a", Pattern = "*.jsonl", RetentionDays = 30 });
                    layout.Settings.Archive.KeepHistoryWithoutArchive = false;
                    layout.Settings.DiskLifecycle.Enabled = true;
                    layout.Settings.DiskLifecycle.DryRun = false;

                    DiskLifecycleService plain = new DiskLifecycleService(testDb.Driver, layout.Settings, CreateLogging());
                    DiskLifecycleReport noArchive = await plain.ReconcileAsync().ConfigureAwait(false);
                    AssertTrue(File.Exists(old), "Without an archive a session transcript is kept even with keeping turned off");
                    AssertTrue(noArchive.Actions.Any(a => a.Path == old && a.Disposition == "kept"), "The kept transcript is reported");

                    FakeArchiveSink sink = new FakeArchiveSink();
                    DiskLifecycleService service = CreateArchivingService(testDb, layout, sink);
                    await service.ReconcileAsync().ConfigureAwait(false);

                    AssertFalse(File.Exists(old), "An archived transcript past its retention is freed");
                    AssertTrue(File.Exists(fresh), "A transcript inside its retention stays");
                    AssertTrue(sink.Objects.Keys.Any(k => k.StartsWith("armada/raw/captainSessions-runtime-a/", StringComparison.Ordinal) && k.EndsWith("/project-a/session-old.jsonl", StringComparison.Ordinal)),
                        "The transcript is stored under its session root with its relative path: " + String.Join(", ", sink.Objects.Keys));
                }
                finally
                {
                    Cleanup(layout);
                }
            }).ConfigureAwait(false);

            await RunTest("An idle operator-evidence folder is archived without its payload before it is removed", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Layout layout = CreateLayout();

                try
                {
                    string expired = CreateEvidenceFolder(Path.Combine(layout.Settings.LogDirectory, "operator-evidence"), "proof-v0", DateTime.UtcNow.AddDays(-20));
                    FakeArchiveSink sink = new FakeArchiveSink();
                    DiskLifecycleService service = CreateArchivingService(testDb, layout, sink);
                    layout.Settings.DiskLifecycle.OperatorEvidenceRetentionDays = 14;
                    layout.Settings.DiskLifecycle.Enabled = true;
                    layout.Settings.DiskLifecycle.DryRun = false;

                    await service.ReconcileAsync().ConfigureAwait(false);

                    AssertFalse(Directory.Exists(expired), "The archived folder is removed");
                    List<string> keys = sink.Objects.Keys.Where(k => k.StartsWith("armada/raw/operatorEvidence/", StringComparison.Ordinal)).ToList();
                    AssertTrue(keys.Any(k => k.EndsWith("/proof-v0/receipt.json", StringComparison.Ordinal)), "The receipt is archived: " + String.Join(", ", keys));
                    AssertTrue(keys.Any(k => k.EndsWith("/proof-v0/checkout/src/Program.cs", StringComparison.Ordinal)), "Source files are archived");
                    AssertFalse(keys.Any(k => k.Contains("/.git/", StringComparison.Ordinal) || k.Contains("/bin/", StringComparison.Ordinal)), "Clones and build outputs are not archived");
                }
                finally
                {
                    Cleanup(layout);
                }
            }).ConfigureAwait(false);

            await RunTest("An expired folder of at least the bundle size is archived as one verified tar.gz object that holds every archivable file", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Layout layout = CreateLayout();

                try
                {
                    string expired = CreateEvidenceFolder(Path.Combine(layout.Settings.LogDirectory, "operator-evidence"), "proof-big", DateTime.UtcNow.AddDays(-20));
                    File.WriteAllText(Path.Combine(expired, "checkout", "src", "Extra.cs"), "class Extra { }");
                    File.SetLastWriteTimeUtc(Path.Combine(expired, "checkout", "src", "Extra.cs"), DateTime.UtcNow.AddDays(-20));
                    FakeArchiveSink sink = new FakeArchiveSink();
                    DiskLifecycleService service = CreateArchivingService(testDb, layout, sink);
                    layout.Settings.DiskLifecycle.OperatorEvidenceRetentionDays = 14;
                    layout.Settings.DiskLifecycle.ArchiveBundleMinFiles = 3;
                    layout.Settings.DiskLifecycle.ArchiveMaxFilesPerPass = 1;
                    layout.Settings.DiskLifecycle.Enabled = true;
                    layout.Settings.DiskLifecycle.DryRun = false;

                    DiskLifecycleReport live = await service.ReconcileAsync().ConfigureAwait(false);

                    AssertFalse(Directory.Exists(expired), "The bundled folder is freed");
                    List<string> keys = sink.Objects.Keys.Where(k => k.StartsWith("armada/raw/operatorEvidence/", StringComparison.Ordinal)).ToList();
                    AssertEqual(1, keys.Count, "The folder is one object: " + String.Join(", ", keys));
                    AssertTrue(keys[0].EndsWith("/proof-big.tar.gz", StringComparison.Ordinal), "The object is keyed by the folder: " + keys[0]);
                    List<string> entries = ReadBundleEntries(sink.Objects[keys[0]]);
                    AssertEqual("checkout/src/Extra.cs|checkout/src/Program.cs|receipt.json", String.Join("|", entries.OrderBy(e => e, StringComparer.Ordinal)),
                        "The bundle holds every archivable file by its path in the folder, and no clone or build output");
                    AssertEqual(1, live.ArchivedItems, "The bundle counts as one archived object");
                    AssertFalse(live.ArchiveCapped, "A bundle counts as one file against the pass bound");
                    AssertFalse(Directory.EnumerateFiles(Path.GetTempPath(), "armada-archive-bundle-*").Any(f => File.GetCreationTimeUtc(f) >= live.ScannedUtc.AddSeconds(-1)),
                        "The temporary bundle is removed");
                    string manifest = System.Text.Encoding.UTF8.GetString(sink.Objects[live.ArchiveManifestKey!]);
                    AssertContains("\"bundledFiles\":3", manifest, "The manifest records how many files the bundle holds");
                    AssertContains(expired.Replace("\\", "\\\\"), manifest, "The manifest records the folder the bundle packs");
                }
                finally
                {
                    Cleanup(layout);
                }
            }).ConfigureAwait(false);

            await RunTest("A deny-listed file keeps only itself: the rest of its folder is archived and freed, and the next pass keeps the file without an upload", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Layout layout = CreateLayout();

                try
                {
                    string expired = CreateEvidenceFolder(Path.Combine(layout.Settings.LogDirectory, "operator-evidence"), "proof-denied", DateTime.UtcNow.AddDays(-20));
                    string denied = Path.Combine(expired, "checkout", ".vscode", "settings.json");
                    Directory.CreateDirectory(Path.GetDirectoryName(denied)!);
                    File.WriteAllText(denied, "{ \"editor.tabSize\": 4 }");
                    File.SetLastWriteTimeUtc(denied, DateTime.UtcNow.AddDays(-20));
                    Directory.SetLastWriteTimeUtc(Path.GetDirectoryName(denied)!, DateTime.UtcNow.AddDays(-20));
                    Directory.SetLastWriteTimeUtc(Path.Combine(expired, "checkout"), DateTime.UtcNow.AddDays(-20));
                    Directory.SetLastWriteTimeUtc(expired, DateTime.UtcNow.AddDays(-20));
                    FakeArchiveSink sink = new FakeArchiveSink();
                    DiskLifecycleService service = CreateArchivingService(testDb, layout, sink);
                    layout.Settings.DiskLifecycle.OperatorEvidenceRetentionDays = 14;
                    layout.Settings.DiskLifecycle.Enabled = true;
                    layout.Settings.DiskLifecycle.DryRun = false;

                    DiskLifecycleReport first = await service.ReconcileAsync().ConfigureAwait(false);

                    AssertTrue(File.Exists(denied), "The deny-listed file stays where it was");
                    AssertFalse(File.Exists(Path.Combine(expired, "receipt.json")), "An archived file beside it is freed");
                    AssertFalse(Directory.Exists(Path.Combine(expired, "checkout", "src")), "An emptied folder is removed");
                    AssertFalse(Directory.Exists(Path.Combine(expired, "checkout", ".git")), "The clone is removed");
                    AssertFalse(sink.Objects.Keys.Any(k => k.Contains("settings.json", StringComparison.Ordinal)), "The deny-listed file is not uploaded: " + String.Join(", ", sink.Objects.Keys));
                    AssertTrue(sink.Objects.Keys.Any(k => k.EndsWith("/proof-denied/receipt.json", StringComparison.Ordinal)), "The rest of the folder is archived");
                    AssertTrue(first.Actions.Any(a => a.Path == expired && a.Disposition == "reclaimed" && a.Reason.Contains("stay in place", StringComparison.Ordinal)),
                        "The action says the deny-listed file stays");

                    int puts = sink.Puts.Count;
                    foreach (string directory in new[] { Path.GetDirectoryName(denied)!, Path.Combine(expired, "checkout"), expired })
                        Directory.SetLastWriteTimeUtc(directory, DateTime.UtcNow.AddDays(-20));
                    DiskLifecycleReport second = await service.ReconcileAsync().ConfigureAwait(false);

                    AssertTrue(File.Exists(denied), "The next pass keeps the deny-listed file");
                    AssertEqual(puts, sink.Puts.Count, "The next pass uploads nothing");
                    AssertTrue(second.Actions.Any(a => a.Path == expired && a.Disposition == "kept" && a.Reason.Contains("only deny-listed files", StringComparison.Ordinal)),
                        "The next pass reports the folder kept for its deny-listed file");
                }
                finally
                {
                    Cleanup(layout);
                }
            }).ConfigureAwait(false);

            await RunTest("A pass reports it is advancing while it runs and not before or after", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Layout layout = CreateLayout();

                try
                {
                    DiskLifecycleService service = new DiskLifecycleService(testDb.Driver, layout.Settings, CreateLogging());
                    service.FreeBytesProbe = _ => 500L * 1024 * 1024 * 1024;
                    bool duringPass = false;
                    service.OnPassStarted = token =>
                    {
                        duringPass = service.IsPassAdvancing(TimeSpan.FromMinutes(10));
                        return Task.CompletedTask;
                    };

                    AssertFalse(service.IsPassAdvancing(TimeSpan.FromMinutes(10)), "No pass is running before the first pass");
                    await service.ScanAsync().ConfigureAwait(false);

                    AssertTrue(duringPass, "A running pass that just started is advancing");
                    AssertFalse(service.IsPassAdvancing(TimeSpan.FromMinutes(10)), "A finished pass is not advancing");
                }
                finally
                {
                    Cleanup(layout);
                }
            }).ConfigureAwait(false);

            await RunTest("Expired brief-file folders beside instruction snapshots are archived and freed, recent ones are kept", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Layout layout = CreateLayout();

                try
                {
                    string instructions = Path.Combine(layout.Settings.LogDirectory, "instructions");
                    string oldMemory = Path.Combine(instructions, "msn_old.memory");
                    string oldMission = Path.Combine(instructions, "msn_old.mission");
                    string freshMemory = Path.Combine(instructions, "msn_fresh.memory");
                    CreateRetentionFile(oldMemory, "01-core.md", DateTime.UtcNow.AddDays(-20));
                    CreateRetentionFile(oldMemory, "02-mission.md", DateTime.UtcNow.AddDays(-20));
                    CreateRetentionFile(oldMission, "brief.md", DateTime.UtcNow.AddDays(-20));
                    CreateRetentionFile(freshMemory, "01-core.md", DateTime.UtcNow.AddDays(-20));
                    CreateRetentionFile(freshMemory, "02-mission.md", DateTime.UtcNow);
                    FakeArchiveSink sink = new FakeArchiveSink();
                    DiskLifecycleService service = CreateArchivingService(testDb, layout, sink);
                    layout.Settings.DiskLifecycle.InstructionRetentionDays = 7;
                    layout.Settings.DiskLifecycle.Enabled = true;
                    layout.Settings.DiskLifecycle.DryRun = false;

                    DiskLifecycleReport live = await service.ReconcileAsync().ConfigureAwait(false);

                    List<string> keys = sink.Objects.Keys.Where(k => k.StartsWith("armada/raw/instructions/", StringComparison.Ordinal)).ToList();
                    AssertTrue(keys.Any(k => k.EndsWith("/msn_old.memory/01-core.md", StringComparison.Ordinal)), "A file of an expired memory folder is archived: " + String.Join(", ", keys));
                    AssertTrue(keys.Any(k => k.EndsWith("/msn_old.mission/brief.md", StringComparison.Ordinal)), "A file of an expired description folder is archived");
                    AssertEqual(3, keys.Count, "Only the expired folders' files are archived: " + String.Join(", ", keys));
                    AssertFalse(Directory.Exists(oldMemory), "The archived memory folder is freed");
                    AssertFalse(Directory.Exists(oldMission), "The archived description folder is freed");
                    AssertTrue(File.Exists(Path.Combine(freshMemory, "01-core.md")), "A folder with a recent file is kept whole");
                    DiskLifecycleCategory category = live.Categories.Single(c => c.Category == "instructions");
                    AssertEqual(2, category.ReclaimableItems, "Each expired folder is one reclaimable item");
                    AssertEqual(1, category.ProtectedItems, "The recent folder is one protected item");
                }
                finally
                {
                    Cleanup(layout);
                }
            }).ConfigureAwait(false);

            await RunTest("An archived item that cannot be freed is reported and retried without a second upload; read-only folders inside an item do not block its removal", async () =>
            {
                // The owner bits mean nothing to a privileged process, so the scenario cannot arise there.
                if (OperatingSystem.IsWindows() || Environment.IsPrivilegedProcess) return;
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Layout layout = CreateLayout();
                string missions = Path.Combine(layout.Settings.LogDirectory, "missions");
                UnixFileMode readOnlyDirectory = UnixFileMode.UserRead | UnixFileMode.UserExecute;
                UnixFileMode writableDirectory = readOnlyDirectory | UnixFileMode.UserWrite;

                try
                {
                    string evidence = CreateEvidenceFolder(Path.Combine(layout.Settings.LogDirectory, "operator-evidence"), "proof-ro", DateTime.UtcNow.AddDays(-20));
                    string lockedSource = Path.Combine(evidence, "checkout", "src");
                    File.SetUnixFileMode(lockedSource, readOnlyDirectory);
                    File.SetUnixFileMode(Path.Combine(evidence, "checkout"), readOnlyDirectory);
                    string log = CreateRetentionFile(missions, "msn_locked.log", DateTime.UtcNow.AddDays(-40));
                    File.SetUnixFileMode(missions, readOnlyDirectory);

                    FakeArchiveSink sink = new FakeArchiveSink();
                    DiskLifecycleService service = CreateArchivingService(testDb, layout, sink);
                    layout.Settings.DiskLifecycle.OperatorEvidenceRetentionDays = 14;
                    layout.Settings.DiskLifecycle.Enabled = true;
                    layout.Settings.DiskLifecycle.DryRun = false;

                    DiskLifecycleReport first = await service.ReconcileAsync().ConfigureAwait(false);

                    AssertFalse(Directory.Exists(evidence), "An archived folder holding read-only folders is freed");
                    AssertTrue(File.Exists(log), "A file whose removal is refused stays");
                    AssertEqual(readOnlyDirectory, File.GetUnixFileMode(missions) & writableDirectory, "The folder that holds the item is not changed");
                    AssertEqual(1, first.ArchiveFreeFailures, "The report counts the item that could not be freed");
                    AssertTrue(first.Errors.Any(e => e.Contains(log, StringComparison.Ordinal)), "The report's errors name the item: " + String.Join(" | ", first.Errors));
                    List<ArmadaEvent> events = await testDb.Driver.Events.EnumerateByTypeAsync("disk_lifecycle.archive_free_failed", 10).ConfigureAwait(false);
                    AssertEqual(1, events.Count, "One free-failure event is recorded");
                    AssertTrue(events[0].Message!.Contains(log, StringComparison.Ordinal), "The event names the item");

                    int logUploads = sink.Puts.Count(k => k.EndsWith("/msn_locked.log", StringComparison.Ordinal));
                    File.SetUnixFileMode(missions, writableDirectory);
                    DiskLifecycleReport second = await service.ReconcileAsync().ConfigureAwait(false);

                    AssertFalse(File.Exists(log), "The next pass frees the item");
                    AssertEqual(logUploads, sink.Puts.Count(k => k.EndsWith("/msn_locked.log", StringComparison.Ordinal)), "The next pass does not upload the verified file again");
                    AssertEqual(1, second.ArchiveReusedItems, "The report counts the file found already archived");
                    AssertEqual(0, second.ArchiveFreeFailures, "Nothing is left unfreed");
                }
                finally
                {
                    if (Directory.Exists(layout.Settings.DataDirectory))
                    {
                        foreach (string directory in Directory.EnumerateDirectories(layout.Settings.DataDirectory, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
                            File.SetUnixFileMode(directory, File.GetUnixFileMode(directory) | writableDirectory);
                    }
                    Cleanup(layout);
                }
            }).ConfigureAwait(false);

            await RunTest("A pass archives at most its file bound, reports the deferred items, and the next pass continues", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Layout layout = CreateLayout();

                try
                {
                    string missions = Path.Combine(layout.Settings.LogDirectory, "missions");
                    List<string> logs = new List<string>
                    {
                        CreateRetentionFile(missions, "msn_a.log", DateTime.UtcNow.AddDays(-40)),
                        CreateRetentionFile(missions, "msn_b.log", DateTime.UtcNow.AddDays(-40)),
                        CreateRetentionFile(missions, "msn_c.log", DateTime.UtcNow.AddDays(-40))
                    };
                    FakeArchiveSink sink = new FakeArchiveSink();
                    DiskLifecycleService service = CreateArchivingService(testDb, layout, sink);
                    layout.Settings.DiskLifecycle.ArchiveMaxFilesPerPass = 2;
                    layout.Settings.DiskLifecycle.Enabled = true;
                    layout.Settings.DiskLifecycle.DryRun = false;

                    DiskLifecycleReport first = await service.ReconcileAsync().ConfigureAwait(false);

                    AssertEqual(1, logs.Count(File.Exists), "Two logs are archived and freed, one is kept");
                    AssertTrue(first.ArchiveCapped, "The report says the pass reached its bound");
                    AssertEqual(1, first.ArchiveDeferredItems, "The report counts the deferred item");
                    AssertTrue(first.Actions.Any(a => a.Disposition == "deferred" && logs.Contains(a.Path) && File.Exists(a.Path)), "The deferred item is recorded by path");
                    ArmadaEvent? recorded = (await testDb.Driver.Events.EnumerateByTypeAsync("disk_lifecycle.reconcile", 5).ConfigureAwait(false)).FirstOrDefault();
                    AssertNotNull(recorded, "The pass records its report event");
                    AssertContains("deferred to the next pass", recorded!.Message ?? String.Empty, "The report event says items were deferred");

                    DiskLifecycleReport second = await service.ReconcileAsync().ConfigureAwait(false);

                    AssertEqual(0, logs.Count(File.Exists), "The next pass archives and frees the deferred log");
                    AssertFalse(second.ArchiveCapped, "A pass under its bound is not capped");
                }
                finally
                {
                    Cleanup(layout);
                }
            }).ConfigureAwait(false);

            await RunTest("An unreadable folder is reported by name, not as nothing to reclaim", async () =>
            {
                if (OperatingSystem.IsWindows()) return;
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Layout layout = CreateLayout();
                    string missionLogs = Path.Combine(layout.Settings.LogDirectory, "missions");
                    try
                    {
                        Directory.CreateDirectory(missionLogs);
                        File.WriteAllText(Path.Combine(missionLogs, "msn_hidden.log"), "log");
                        File.SetUnixFileMode(missionLogs, UnixFileMode.None);
                        DiskLifecycleService service = new DiskLifecycleService(testDb.Driver, layout.Settings, CreateLogging());

                        DiskLifecycleReport report = await service.ScanAsync().ConfigureAwait(false);

                        AssertTrue(report.ErrorCount >= 1, "an unreadable mission-log folder must be counted as an error");
                        AssertTrue(report.Errors.Any(e => e.Contains(missionLogs, StringComparison.Ordinal)),
                            "the error must name the unreadable folder; got: " + String.Join(" | ", report.Errors));
                        ArmadaEvent? recorded = (await testDb.Driver.Events.EnumerateByTypeAsync("disk_lifecycle.scan", 5).ConfigureAwait(false)).FirstOrDefault();
                        AssertNotNull(recorded, "the scan records its report event");
                        AssertContains(missionLogs, recorded!.Payload ?? String.Empty, "the recorded report must carry the error");
                    }
                    finally
                    {
                        if (Directory.Exists(missionLogs))
                            File.SetUnixFileMode(missionLogs, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                        Cleanup(layout);
                    }
                }
            }).ConfigureAwait(false);

            await RunTest("Two passes started together run one after the other, never at once", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Layout layout = CreateLayout();
                    try
                    {
                        DiskLifecycleService service = new DiskLifecycleService(testDb.Driver, layout.Settings, CreateLogging());
                        int active = 0;
                        int maximumActive = 0;
                        int started = 0;
                        TaskCompletionSource<bool> firstStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                        TaskCompletionSource<bool> releaseFirst = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                        service.OnPassStarted = async token =>
                        {
                            int now = Interlocked.Increment(ref active);
                            lock (firstStarted) { maximumActive = Math.Max(maximumActive, now); }
                            if (Interlocked.Increment(ref started) == 1)
                            {
                                firstStarted.TrySetResult(true);
                                await releaseFirst.Task.ConfigureAwait(false);
                            }
                            Interlocked.Decrement(ref active);
                        };

                        Task<DiskLifecycleReport> operatorPass = service.ReconcileAsync();
                        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                        Task<DiskLifecycleReport> loopPass = service.ReconcileAsync();
                        await Task.Delay(200).ConfigureAwait(false);
                        AssertEqual(1, Volatile.Read(ref started), "the second pass must wait while the first is running");

                        releaseFirst.TrySetResult(true);
                        await Task.WhenAll(operatorPass, loopPass).WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);

                        AssertEqual(2, started, "both passes run");
                        AssertEqual(1, maximumActive, "passes never overlap");
                    }
                    finally
                    {
                        Cleanup(layout);
                    }
                }
            }).ConfigureAwait(false);

            await RunTest("Operator evidence loses idle clones and build outputs, keeps receipts, and skips recent and kept folders", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Layout layout = CreateLayout();
                // History is deleted without a copy only when keeping it is turned off.
                layout.Settings.Archive.KeepHistoryWithoutArchive = false;

                try
                {
                    string evidence = Path.Combine(layout.Settings.LogDirectory, "operator-evidence");
                    string idle = CreateEvidenceFolder(evidence, "proof-v1", DateTime.UtcNow.AddDays(-2));
                    string recent = CreateEvidenceFolder(evidence, "proof-v2", DateTime.UtcNow);
                    string kept = CreateEvidenceFolder(evidence, "proof-pinned", DateTime.UtcNow.AddDays(-2), keep: true);
                    string expired = CreateEvidenceFolder(evidence, "proof-v0", DateTime.UtcNow.AddDays(-20));

                    DiskLifecycleService service = new DiskLifecycleService(testDb.Driver, layout.Settings, CreateLogging());
                    service.FreeBytesProbe = _ => 500L * 1024 * 1024 * 1024;

                    DiskLifecycleReport dry = await service.ScanAsync().ConfigureAwait(false);
                    DiskLifecycleCategory dryCategory = dry.Categories.First(c => c.Category == "operatorEvidence");
                    AssertEqual(4, dryCategory.TotalItems, "Every evidence folder is counted");
                    AssertTrue(dryCategory.TotalBytes > 0, "Nested files are measured, not only the top folder");
                    AssertTrue(Directory.Exists(Path.Combine(idle, "checkout", ".git")), "A scan deletes nothing");

                    layout.Settings.DiskLifecycle.Enabled = true;
                    layout.Settings.DiskLifecycle.DryRun = false;
                    DiskLifecycleReport keepFolders = await service.ReconcileAsync().ConfigureAwait(false);
                    AssertTrue(File.Exists(Path.Combine(expired, "receipt.json")), "With no folder retention set, an old folder keeps its files");
                    AssertFalse(Directory.Exists(Path.Combine(expired, "checkout", ".git")), "With no folder retention set, an old folder still loses its payload");
                    AssertEqual(4, keepFolders.Categories.First(c => c.Category == "operatorEvidence").ReclaimableItems, "The .git and bin of the idle and the old folder are reclaimable");
                    AssertTrue(keepFolders.Actions.Any(a => a.Category == "operatorEvidence" && a.Disposition == "reclaimed" && a.Reason.Contains("receipts and logs kept", StringComparison.Ordinal)),
                        "A payload reclaim records its reason");

                    layout.Settings.DiskLifecycle.OperatorEvidenceRetentionDays = 14;
                    DiskLifecycleReport live = await service.ReconcileAsync().ConfigureAwait(false);

                    AssertFalse(Directory.Exists(Path.Combine(idle, "checkout", ".git")), "An idle clone's .git is reclaimed");
                    AssertFalse(Directory.Exists(Path.Combine(idle, "checkout", "bin")), "An idle build output is reclaimed");
                    AssertTrue(File.Exists(Path.Combine(idle, "receipt.json")), "The receipt stays");
                    AssertTrue(File.Exists(Path.Combine(idle, "checkout", "src", "Program.cs")), "Source files stay");
                    AssertTrue(Directory.Exists(Path.Combine(recent, "checkout", ".git")), "A recently written folder is protected");
                    AssertTrue(Directory.Exists(Path.Combine(kept, "checkout", ".git")), "A folder with the keep marker is protected");
                    AssertFalse(Directory.Exists(expired), "A folder idle past the folder retention is removed whole");
                    AssertFalse(live.LowSpace, "Ample free space is not low-space mode");

                    DiskLifecycleCategory category = live.Categories.First(c => c.Category == "operatorEvidence");
                    AssertEqual(1, category.ReclaimableItems, "With a folder retention set, the old folder is reclaimable whole");
                    AssertEqual(3, category.ProtectedItems, "The recent, the kept and the already stripped folders are protected");
                }
                finally
                {
                    Cleanup(layout);
                }
            }).ConfigureAwait(false);

            await RunTest("An idle evidence payload holding a read-only folder is removed", async () =>
            {
                // The owner bits mean nothing to a privileged process, so the scenario cannot arise there.
                if (OperatingSystem.IsWindows() || Environment.IsPrivilegedProcess) return;
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Layout layout = CreateLayout();
                UnixFileMode readOnlyDirectory = UnixFileMode.UserRead | UnixFileMode.UserExecute;

                try
                {
                    string idle = CreateEvidenceFolder(Path.Combine(layout.Settings.LogDirectory, "operator-evidence"), "proof-ro-payload", DateTime.UtcNow.AddDays(-2));
                    string locked = Path.Combine(idle, "checkout", "bin", "runtimes");
                    Directory.CreateDirectory(locked);
                    File.WriteAllText(Path.Combine(locked, "native.so"), "n");
                    File.SetLastWriteTimeUtc(Path.Combine(locked, "native.so"), DateTime.UtcNow.AddDays(-2));
                    Directory.SetLastWriteTimeUtc(locked, DateTime.UtcNow.AddDays(-2));
                    Directory.SetLastWriteTimeUtc(Path.Combine(idle, "checkout", "bin"), DateTime.UtcNow.AddDays(-2));
                    File.SetUnixFileMode(locked, readOnlyDirectory);
                    DiskLifecycleService service = new DiskLifecycleService(testDb.Driver, layout.Settings, CreateLogging());
                    service.FreeBytesProbe = _ => 500L * 1024 * 1024 * 1024;
                    layout.Settings.DiskLifecycle.Enabled = true;
                    layout.Settings.DiskLifecycle.DryRun = false;

                    DiskLifecycleReport live = await service.ReconcileAsync().ConfigureAwait(false);

                    AssertFalse(Directory.Exists(Path.Combine(idle, "checkout", "bin")), "The build output is removed although it holds a read-only folder");
                    AssertTrue(File.Exists(Path.Combine(idle, "receipt.json")), "The receipt stays");
                    AssertFalse(live.Actions.Any(a => a.Disposition == "skipped" && a.Reason.Contains("removal failed", StringComparison.Ordinal)), "No removal is reported failed");
                }
                finally
                {
                    if (Directory.Exists(layout.Settings.DataDirectory))
                    {
                        foreach (string directory in Directory.EnumerateDirectories(layout.Settings.DataDirectory, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
                            File.SetUnixFileMode(directory, File.GetUnixFileMode(directory) | UnixFileMode.UserWrite | UnixFileMode.UserRead | UnixFileMode.UserExecute);
                    }
                    Cleanup(layout);
                }
            }).ConfigureAwait(false);

            await RunTest("Low free space shortens operator-evidence payload retention", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Layout layout = CreateLayout();

                try
                {
                    string evidence = Path.Combine(layout.Settings.LogDirectory, "operator-evidence");
                    string folder = CreateEvidenceFolder(evidence, "proof-v3", DateTime.UtcNow.AddHours(-8));
                    layout.Settings.DiskLifecycle.Enabled = true;
                    layout.Settings.DiskLifecycle.DryRun = false;

                    DiskLifecycleService service = new DiskLifecycleService(testDb.Driver, layout.Settings, CreateLogging());
                    service.FreeBytesProbe = _ => 500L * 1024 * 1024 * 1024;
                    DiskLifecycleReport ample = await service.ReconcileAsync().ConfigureAwait(false);
                    AssertTrue(Directory.Exists(Path.Combine(folder, "checkout", ".git")), "Eight idle hours are inside the normal 24-hour retention");
                    AssertEqual(500L * 1024 * 1024 * 1024, ample.FreeBytes, "The report carries the measured free space");

                    service.FreeBytesProbe = _ => 2L * 1024 * 1024 * 1024;
                    DiskLifecycleReport low = await service.ReconcileAsync().ConfigureAwait(false);
                    AssertTrue(low.LowSpace, "Free space under the threshold is low-space mode");
                    AssertFalse(Directory.Exists(Path.Combine(folder, "checkout", ".git")), "Low-space mode reclaims payload idle past its shorter retention");
                    AssertTrue(File.Exists(Path.Combine(folder, "receipt.json")), "Low-space mode still keeps the receipt");

                    DiskLifecycleReport measured = await new DiskLifecycleService(testDb.Driver, layout.Settings, CreateLogging()).ScanAsync().ConfigureAwait(false);
                    AssertTrue(measured.FreeBytes > 0, "The default probe reads the real free space of the log volume");
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

                    string oldTestHome = Path.Combine(tempDir, "armada-test-home-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(oldTestHome);
                    File.SetLastWriteTimeUtc(oldTestHome, DateTime.UtcNow.AddDays(-3));
                    string freshBuildTemp = Path.Combine(tempDir, "MSBuildTemp" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(freshBuildTemp);

                    string integrationDir = Path.Combine(layout.Settings.DocksDirectory, "_integration", "msn_leftover");
                    Directory.CreateDirectory(integrationDir);
                    File.SetLastWriteTimeUtc(integrationDir, DateTime.UtcNow.AddDays(-3));

                    DiskLifecycleService service = new DiskLifecycleService(testDb.Driver, layout.Settings, logging);
                    layout.Settings.DiskLifecycle.Enabled = true;
                    layout.Settings.DiskLifecycle.DryRun = false;

                    DiskLifecycleReport report = await service.ReconcileAsync().ConfigureAwait(false);

                    AssertFalse(Directory.Exists(oldCheckDir), "Expired temp checkout dir must be reclaimed.");
                    AssertFalse(File.Exists(oldTestDb), "Expired test-db sidecar must be reclaimed.");
                    AssertFalse(Directory.Exists(oldTestHome), "An expired test-run home left by a killed test run must be reclaimed.");
                    AssertTrue(Directory.Exists(freshBuildTemp), "A build temp folder inside retention must be kept.");
                    Directory.Delete(freshBuildTemp, true);
                    AssertFalse(Directory.Exists(integrationDir), "Leftover integration worktree must be reclaimed.");

                    DiskLifecycleCategory tempCategory = report.Categories.First(c => c.Category == "tempArtifacts");
                    AssertTrue(tempCategory.ReclaimableItems >= 2, "Temp-artifact category must account for reclaimed items.");
                }
                finally
                {
                    Cleanup(layout);
                }
            }).ConfigureAwait(false);

            await RunTest("Temp reclaim keeps the checkout directory of a live check run and reclaims a finished one", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                Layout layout = CreateLayout();
                string tempDir = Path.GetTempPath();
                string liveDir = String.Empty;
                string finishedDir = String.Empty;

                try
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                        new Vessel("vsl_check_reclaim", "https://github.com/test/repo.git")).ConfigureAwait(false);

                    CheckRun live = await testDb.Driver.CheckRuns.CreateAsync(new CheckRun
                    {
                        VesselId = vessel.Id,
                        Type = CheckRunTypeEnum.UnitTest,
                        Source = CheckRunSourceEnum.Armada,
                        Status = CheckRunStatusEnum.Running,
                        Command = "dotnet test",
                        StartedUtc = DateTime.UtcNow.AddDays(-2)
                    }).ConfigureAwait(false);
                    CheckRun finished = await testDb.Driver.CheckRuns.CreateAsync(new CheckRun
                    {
                        VesselId = vessel.Id,
                        Type = CheckRunTypeEnum.Build,
                        Source = CheckRunSourceEnum.Armada,
                        Status = CheckRunStatusEnum.Passed,
                        Command = "dotnet build"
                    }).ConfigureAwait(false);

                    // A long suite leaves its checkout untouched for longer than the retention window.
                    liveDir = Path.Combine(tempDir, "armada-chk-" + live.Id + "-" + Guid.NewGuid().ToString("N"));
                    finishedDir = Path.Combine(tempDir, "armada-chk-" + finished.Id + "-" + Guid.NewGuid().ToString("N"));
                    foreach (string dir in new[] { liveDir, finishedDir })
                    {
                        Directory.CreateDirectory(dir);
                        File.WriteAllText(Path.Combine(dir, "artifact.bin"), "data");
                        File.SetLastWriteTimeUtc(dir, DateTime.UtcNow.AddDays(-3));
                    }

                    layout.Settings.DiskLifecycle.Enabled = true;
                    layout.Settings.DiskLifecycle.DryRun = false;
                    DiskLifecycleService service = new DiskLifecycleService(testDb.Driver, layout.Settings, logging);

                    DiskLifecycleReport report = await service.ReconcileAsync().ConfigureAwait(false);

                    AssertTrue(Directory.Exists(liveDir), "The checkout of a Running check must not be reclaimed.");
                    AssertTrue(
                        report.Actions.Any(a => a.Category == "tempArtifacts" && a.Path == Path.GetFullPath(liveDir) && a.Disposition == "protected"),
                        "The live checkout must be recorded as protected.");
                    AssertFalse(Directory.Exists(finishedDir), "The checkout of a finished check past retention must be reclaimed.");
                }
                finally
                {
                    foreach (string dir in new[] { liveDir, finishedDir })
                    {
                        if (!String.IsNullOrEmpty(dir) && Directory.Exists(dir)) Directory.Delete(dir, true);
                    }
                    Cleanup(layout);
                }
            }).ConfigureAwait(false);

            await RunTest("An extra retention root reclaims idle children and keeps the newest, recent and kept ones; a scan deletes nothing", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Layout layout = CreateLayout();

                try
                {
                    string root = Path.Combine(layout.Settings.DataDirectory, "operator-dumps");
                    string newest = CreateRetentionFile(root, "dump-newest.bin", DateTime.UtcNow);
                    string recent = CreateRetentionFile(root, "dump-recent.bin", DateTime.UtcNow.AddHours(-1));
                    string oldFile = CreateRetentionFile(root, "dump-old.bin", DateTime.UtcNow.AddDays(-8));
                    string oldFolder = CreateEvidenceFolder(root, "rehearsal-old", DateTime.UtcNow.AddDays(-9));
                    string keptFolder = CreateEvidenceFolder(root, "rehearsal-pinned", DateTime.UtcNow.AddDays(-20), keep: true);
                    layout.Settings.DiskLifecycle.ExtraRetentionRoots.Add(new Armada.Core.Settings.DiskLifecycleRetentionRoot { Path = root, RetentionHours = 72, KeepNewest = 1 });

                    DiskLifecycleService service = new DiskLifecycleService(testDb.Driver, layout.Settings, CreateLogging());
                    service.FreeBytesProbe = _ => 500L * 1024 * 1024 * 1024;

                    DiskLifecycleReport scan = await service.ScanAsync().ConfigureAwait(false);
                    DiskLifecycleCategory scanned = scan.Categories.First(c => c.Category == "extraRoot:operator-dumps");
                    AssertEqual(5, scanned.TotalItems, "Every direct child is one item");
                    AssertEqual(2, scanned.ReclaimableItems, "The idle file and the idle folder are reclaimable");
                    AssertEqual(3, scanned.ProtectedItems, "The newest, the recent and the kept child are protected");
                    AssertTrue(File.Exists(oldFile) && Directory.Exists(oldFolder), "A scan deletes nothing");

                    layout.Settings.DiskLifecycle.Enabled = true;
                    DiskLifecycleReport dryRun = await service.ReconcileAsync().ConfigureAwait(false);
                    AssertTrue(dryRun.DryRun, "Dry-run settings make reconcile a dry run");
                    AssertTrue(File.Exists(oldFile) && Directory.Exists(oldFolder), "A dry-run reconcile deletes nothing");

                    layout.Settings.DiskLifecycle.DryRun = false;
                    DiskLifecycleReport live = await service.ReconcileAsync().ConfigureAwait(false);
                    AssertFalse(File.Exists(oldFile), "An idle file is removed");
                    AssertFalse(Directory.Exists(oldFolder), "An idle folder is removed whole");
                    AssertTrue(File.Exists(newest), "The newest child is kept");
                    AssertTrue(File.Exists(recent), "A child written inside the retention is kept");
                    AssertTrue(Directory.Exists(keptFolder), "A child holding the keep marker is kept");
                    AssertTrue(Directory.Exists(root), "The retention root itself stays");
                    AssertEqual(2, live.Actions.Count(a => a.Category == "extraRoot:operator-dumps" && a.Disposition == "reclaimed"), "Each removal is recorded");
                }
                finally
                {
                    Cleanup(layout);
                }
            }).ConfigureAwait(false);

            await RunTest("An extra retention root keeps its newest children even when every child is idle", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Layout layout = CreateLayout();

                try
                {
                    string root = Path.Combine(layout.Settings.DataDirectory, "deploy-dumps");
                    string first = CreateRetentionFile(root, "dump-a.bin", DateTime.UtcNow.AddDays(-30));
                    string second = CreateRetentionFile(root, "dump-b.bin", DateTime.UtcNow.AddDays(-20));
                    string third = CreateRetentionFile(root, "dump-c.bin", DateTime.UtcNow.AddDays(-10));
                    layout.Settings.DiskLifecycle.ExtraRetentionRoots.Add(new Armada.Core.Settings.DiskLifecycleRetentionRoot { Path = root, RetentionHours = 24, KeepNewest = 2 });
                    layout.Settings.DiskLifecycle.Enabled = true;
                    layout.Settings.DiskLifecycle.DryRun = false;

                    DiskLifecycleService service = new DiskLifecycleService(testDb.Driver, layout.Settings, CreateLogging());
                    service.FreeBytesProbe = _ => 500L * 1024 * 1024 * 1024;
                    await service.ReconcileAsync().ConfigureAwait(false);

                    AssertFalse(File.Exists(first), "Only the oldest idle child is removed");
                    AssertTrue(File.Exists(second) && File.Exists(third), "The two newest children are kept though both are idle");
                }
                finally
                {
                    Cleanup(layout);
                }
            }).ConfigureAwait(false);

            await RunTest("Fail closed: a symlinked child of an extra retention root is skipped, never deleted", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Layout layout = CreateLayout();

                try
                {
                    string root = Path.Combine(layout.Settings.DataDirectory, "scratch-root");
                    Directory.CreateDirectory(root);
                    string outsideTarget = CreateEvidenceFolder(layout.Settings.DataDirectory, "outside-target", DateTime.UtcNow.AddDays(-30));
                    string linkPath = Path.Combine(root, "linked");
                    try
                    {
                        Directory.CreateSymbolicLink(linkPath, outsideTarget);
                    }
                    catch
                    {
                        return;
                    }

                    AgeLinkItself(linkPath, DateTime.UtcNow.AddDays(-30));
                    layout.Settings.DiskLifecycle.ExtraRetentionRoots.Add(new Armada.Core.Settings.DiskLifecycleRetentionRoot { Path = root, RetentionHours = 1, KeepNewest = 0 });
                    layout.Settings.DiskLifecycle.Enabled = true;
                    layout.Settings.DiskLifecycle.DryRun = false;

                    DiskLifecycleService service = new DiskLifecycleService(testDb.Driver, layout.Settings, CreateLogging());
                    service.FreeBytesProbe = _ => 500L * 1024 * 1024 * 1024;
                    DiskLifecycleReport report = await service.ReconcileAsync().ConfigureAwait(false);

                    AssertTrue(Directory.Exists(linkPath), "A symlinked child is never deleted");
                    AssertTrue(File.Exists(Path.Combine(outsideTarget, "receipt.json")), "The link target is never deleted");
                    AssertTrue(report.Actions.Any(a => a.Path == linkPath && a.Disposition == "skipped"), "The symlinked child is recorded as skipped");
                    AssertEqual(0, report.Categories.First(c => c.Category == "extraRoot:scratch-root").ReclaimableItems, "A symlink is never counted reclaimable");
                }
                finally
                {
                    Cleanup(layout);
                }
            }).ConfigureAwait(false);

            await RunTest("An extra retention root that is not an absolute path is reported and removes nothing", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Layout layout = CreateLayout();

                try
                {
                    layout.Settings.DiskLifecycle.ExtraRetentionRoots.Add(new Armada.Core.Settings.DiskLifecycleRetentionRoot { Path = "relative-dumps", RetentionHours = 1, KeepNewest = 0 });
                    layout.Settings.DiskLifecycle.Enabled = true;
                    layout.Settings.DiskLifecycle.DryRun = false;

                    DiskLifecycleService service = new DiskLifecycleService(testDb.Driver, layout.Settings, CreateLogging());
                    service.FreeBytesProbe = _ => 500L * 1024 * 1024 * 1024;
                    DiskLifecycleReport report = await service.ReconcileAsync().ConfigureAwait(false);

                    AssertTrue(report.Errors.Any(e => e.Contains("relative-dumps", StringComparison.Ordinal)), "The refused root is named in the report errors");
                    AssertFalse(report.Categories.Any(c => c.Category.StartsWith("extraRoot:", StringComparison.Ordinal)), "A refused root yields no category");
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

            await RunTest("Fail closed: a symlinked merge-queue worktree is skipped, never counted reclaimable", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                Layout layout = CreateLayout();

                bool symlinkSupported = true;
                try
                {
                    string checkout = Path.Combine(layout.Settings.DataDirectory, "real-checkout");
                    Directory.CreateDirectory(checkout);
                    File.WriteAllText(Path.Combine(checkout, "source.cs"), new string('x', 4096));
                    File.SetLastWriteTimeUtc(checkout, DateTime.UtcNow.AddDays(-3));

                    string queueRoot = Path.Combine(layout.Settings.DocksDirectory, "_merge-queue");
                    Directory.CreateDirectory(queueRoot);
                    string linkPath = Path.Combine(queueRoot, "ExampleVessel");
                    try
                    {
                        Directory.CreateSymbolicLink(linkPath, checkout);
                    }
                    catch
                    {
                        symlinkSupported = false;
                    }

                    if (symlinkSupported)
                    {
                        AgeLinkItself(linkPath, DateTime.UtcNow.AddDays(-3));
                        layout.Settings.DiskLifecycle.Enabled = true;
                        layout.Settings.DiskLifecycle.DryRun = false;
                        DiskLifecycleService service = new DiskLifecycleService(testDb.Driver, layout.Settings, logging);
                        DiskLifecycleReport report = await service.ReconcileAsync().ConfigureAwait(false);

                        DiskLifecycleCategory queue = report.Categories.Single(c => c.Category == "mergeQueueWorktrees");
                        AssertEqual(0, queue.ReclaimableItems, "A symlinked merge-queue worktree must not be counted reclaimable.");
                        AssertEqual(0L, queue.ReclaimableBytes, "The symlink target's bytes must not be counted reclaimable.");
                        AssertTrue(File.Exists(Path.Combine(checkout, "source.cs")), "The symlink target must never be deleted.");
                        AssertTrue(
                            report.Actions.Any(a => a.Path == linkPath && a.Disposition == "skipped" && a.Reason == "symlink or outside allowed roots"),
                            "A symlinked merge-queue worktree must be recorded as skipped for being a symlink.");
                    }
                }
                finally
                {
                    Cleanup(layout);
                }
            }).ConfigureAwait(false);
        }

        #region Private-Methods

        // A leftover link carries its own old timestamp. .NET has no API that sets a link's timestamp
        // without following it, so this uses touch -h where it exists and does nothing elsewhere.
        private static void AgeLinkItself(string linkPath, DateTime whenUtc)
        {
            if (OperatingSystem.IsWindows()) return;
            try
            {
                ProcessStartInfo info = new ProcessStartInfo("touch")
                {
                    UseShellExecute = false,
                    RedirectStandardError = true
                };
                info.ArgumentList.Add("-h");
                info.ArgumentList.Add("-d");
                info.ArgumentList.Add("@" + new DateTimeOffset(whenUtc).ToUnixTimeSeconds());
                info.ArgumentList.Add(linkPath);
                using (Process? process = Process.Start(info))
                {
                    process?.WaitForExit(10000);
                }
            }
            catch
            {
                // Without touch the link stays fresh; the skip assertion still applies.
            }
        }

        private static LoggingModule CreateLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        // Returns one fixed status and body for every request, so a sink test can drive the read-back
        // verify without a real object store.
        private sealed class StubHttpMessageHandler : HttpMessageHandler
        {
            private readonly HttpStatusCode _Status;
            private readonly string _Body;
            private readonly Dictionary<string, string[]>? _Headers;
            private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? _Responder;

            public StubHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
            {
                _Responder = responder;
                _Body = String.Empty;
            }

            public StubHttpMessageHandler(HttpStatusCode status, string body, Dictionary<string, string[]>? headers = null)
            {
                _Status = status;
                _Body = body;
                _Headers = headers;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (_Responder != null) return _Responder(request, cancellationToken);
                HttpResponseMessage response = new HttpResponseMessage(_Status)
                {
                    Content = new StringContent(_Body)
                };
                if (_Headers != null)
                    foreach (KeyValuePair<string, string[]> header in _Headers)
                        response.Headers.TryAddWithoutValidation(header.Key, header.Value);
                return Task.FromResult(response);
            }
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

        private static string CreateEvidenceFolder(string evidenceRoot, string name, DateTime writtenUtc, bool keep = false)
        {
            string folder = Path.Combine(evidenceRoot, name);
            Directory.CreateDirectory(Path.Combine(folder, "checkout", ".git", "objects"));
            Directory.CreateDirectory(Path.Combine(folder, "checkout", "src"));
            Directory.CreateDirectory(Path.Combine(folder, "checkout", "bin"));
            File.WriteAllText(Path.Combine(folder, "receipt.json"), "{\"exit\":0}");
            File.WriteAllText(Path.Combine(folder, "checkout", ".git", "objects", "pack.bin"), new string('p', 4096));
            File.WriteAllText(Path.Combine(folder, "checkout", "src", "Program.cs"), "class Program { }");
            File.WriteAllText(Path.Combine(folder, "checkout", "bin", "app.dll"), new string('b', 2048));
            if (keep) File.WriteAllText(Path.Combine(folder, ".armada-keep"), "pinned");

            foreach (string file in Directory.EnumerateFiles(folder, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }))
                File.SetLastWriteTimeUtc(file, writtenUtc);
            foreach (string directory in Directory.EnumerateDirectories(folder, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }).OrderByDescending(d => d.Length))
                Directory.SetLastWriteTimeUtc(directory, writtenUtc);
            Directory.SetLastWriteTimeUtc(folder, writtenUtc);
            return folder;
        }

        private static List<string> ReadBundleEntries(byte[] bundle)
        {
            List<string> entries = new List<string>();
            using (MemoryStream compressed = new MemoryStream(bundle))
            {
                using (System.IO.Compression.GZipStream gzip = new System.IO.Compression.GZipStream(compressed, System.IO.Compression.CompressionMode.Decompress))
                {
                    using (System.Formats.Tar.TarReader reader = new System.Formats.Tar.TarReader(gzip))
                    {
                        System.Formats.Tar.TarEntry? entry;
                        while ((entry = reader.GetNextEntry()) != null)
                        {
                            if (entry.EntryType == System.Formats.Tar.TarEntryType.RegularFile) entries.Add(entry.Name);
                        }
                    }
                }
            }
            return entries;
        }

        private static DiskLifecycleService CreateArchivingService(TestDatabase testDb, Layout layout, FakeArchiveSink sink)
        {
            layout.Settings.Archive.Enabled = true;
            layout.Settings.Archive.Endpoint = "http://archive.example.invalid";
            layout.Settings.Archive.Bucket = "history";
            layout.Settings.Archive.CredentialsFile = Path.Combine(layout.Settings.DataDirectory, "archive-credentials");
            DiskLifecycleService service = new DiskLifecycleService(testDb.Driver, layout.Settings, CreateLogging());
            service.FreeBytesProbe = _ => 500L * 1024 * 1024 * 1024;
            service.ArchiveSinkFactory = _ => sink;
            return service;
        }

        private static string CreateRetentionFile(string root, string name, DateTime writtenUtc)
        {
            Directory.CreateDirectory(root);
            string file = Path.Combine(root, name);
            File.WriteAllText(file, new string('d', 1024));
            File.SetLastWriteTimeUtc(file, writtenUtc);
            return file;
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
