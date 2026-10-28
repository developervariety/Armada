namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Data.Sqlite;
    using SyslogLogging;
    using Armada.Core.Settings;
    using Armada.Core.Storage;
    using Armada.Test.Common;

    /// <summary>
    /// Tests the administrator storage inventory: the bounded read-only file scan, database metadata, the
    /// cached snapshot with manual refresh, and the bounded persistent growth history.
    /// </summary>
    public sealed class StorageInventoryTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Storage Inventory";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("Scan reports exact counts, sizes, dates and largest files for a real tree", () =>
            {
                string root = TempDirectory();
                try
                {
                    WriteFile(Path.Combine(root, "a.bin"), 100);
                    WriteFile(Path.Combine(root, "sub", "b.bin"), 250);
                    WriteFile(Path.Combine(root, "sub", "deep", "c.bin"), 50);
                    Directory.CreateDirectory(Path.Combine(root, "empty"));
                    DateTime old = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
                    File.SetLastWriteTimeUtc(Path.Combine(root, "a.bin"), old);

                    StorageScanReport report = new StorageFileCollector().Collect(Locations(Loc("Docks", root)), new StorageSettings());

                    AssertEqual(StorageStatusEnum.Complete, report.Status, "report status");
                    AssertEqual(0, report.PartialReasons.Count, "no partial reason");
                    StorageLocation location = report.Locations.Single();
                    AssertEqual(400L, location.Bytes ?? -1, "bytes");
                    AssertEqual(3L, location.FileCount, "file count");
                    AssertEqual(old, location.OldestWriteUtc ?? DateTime.MinValue, "oldest write");
                    AssertEqual("b.bin", Path.GetFileName(location.LargestFiles[0].Path), "largest file first");
                    AssertEqual(3, location.LargestFiles.Count, "every file listed within the limit");
                    AssertEqual(6L, report.EntriesExamined, "files plus directories examined");
                }
                finally { Remove(root); }
                return Task.CompletedTask;
            });

            await RunTest("A more specific location owns its subtree so no file is counted twice", () =>
            {
                string root = TempDirectory();
                try
                {
                    WriteFile(Path.Combine(root, "top.bin"), 10);
                    WriteFile(Path.Combine(root, "logs", "missions", "m.log"), 200);
                    WriteFile(Path.Combine(root, "logs", "admiral.log"), 30);

                    StorageScanReport report = new StorageFileCollector().Collect(
                        Locations(Loc("Other", root), Loc("Logs", Path.Combine(root, "logs")), Loc("MissionLogs", Path.Combine(root, "logs", "missions"))),
                        new StorageSettings());

                    AssertEqual(10L, BytesOf(report, "Other"), "the data root keeps only its own files");
                    AssertEqual(30L, BytesOf(report, "Logs"), "logs exclude mission logs");
                    AssertEqual(200L, BytesOf(report, "MissionLogs"), "mission logs");
                    AssertEqual(240L, report.Locations.Sum(l => l.Bytes ?? 0), "the categories add up to the whole tree");
                }
                finally { Remove(root); }
                return Task.CompletedTask;
            });

            await RunTest("An entry bound ends the scan partial and names EntryLimit", () =>
            {
                string root = TempDirectory();
                try
                {
                    for (int i = 0; i < 150; i++) WriteFile(Path.Combine(root, "f" + i + ".bin"), 1);
                    StorageSettings settings = new StorageSettings { MaxEntries = 100 };

                    StorageScanReport report = new StorageFileCollector().Collect(Locations(Loc("Docks", root)), settings);

                    AssertEqual(StorageStatusEnum.Partial, report.Status, "report is partial");
                    AssertTrue(report.PartialReasons.Contains(StorageWarningCodes.EntryLimit), "reason names the entry limit");
                    AssertEqual(100L, report.EntriesExamined, "exactly the bound is examined");
                    StorageLocation location = report.Locations.Single();
                    AssertEqual(StorageStatusEnum.Partial, location.Status, "location is partial");
                    AssertTrue(location.Warnings.Contains(StorageWarningCodes.EntryLimit), "location names the entry limit");
                    AssertTrue((location.Bytes ?? 0) < 150, "figures are a lower bound");
                }
                finally { Remove(root); }
                return Task.CompletedTask;
            });

            await RunTest("A location the budget never reached is Unavailable with NotReached", () =>
            {
                string first = TempDirectory();
                string second = TempDirectory();
                try
                {
                    for (int i = 0; i < 120; i++) WriteFile(Path.Combine(first, "f" + i + ".bin"), 1);
                    WriteFile(Path.Combine(second, "later.bin"), 5);

                    StorageScanReport report = new StorageFileCollector().Collect(
                        Locations(Loc("Docks", first), Loc("Other", second)), new StorageSettings { MaxEntries = 100 });

                    StorageLocation later = report.Locations.Single(l => l.Category == "Other");
                    AssertEqual(StorageStatusEnum.Unavailable, later.Status, "unreached location status");
                    AssertTrue(later.Warnings.Contains(StorageWarningCodes.NotReached), "unreached location says why");
                    AssertEqual(StorageStatusEnum.Partial, report.Status, "report is partial");
                }
                finally { Remove(first); Remove(second); }
                return Task.CompletedTask;
            });

            await RunTest("A depth bound stops below the limit and names DepthLimit", () =>
            {
                string root = TempDirectory();
                try
                {
                    WriteFile(Path.Combine(root, "d1", "near.bin"), 7);
                    WriteFile(Path.Combine(root, "d1", "d2", "far.bin"), 9);

                    StorageScanReport report = new StorageFileCollector().Collect(Locations(Loc("Docks", root)), new StorageSettings { MaxDepth = 1 });

                    AssertEqual(7L, BytesOf(report, "Docks"), "only the file within the depth is counted");
                    AssertTrue(report.PartialReasons.Contains(StorageWarningCodes.DepthLimit), "reason names the depth limit");
                    AssertEqual(StorageStatusEnum.Partial, report.Status, "report is partial");
                }
                finally { Remove(root); }
                return Task.CompletedTask;
            });

            await RunTest("A time bound stops the scan and names TimeLimit", () =>
            {
                string root = TempDirectory();
                try
                {
                    for (int i = 0; i < 5; i++) WriteFile(Path.Combine(root, "f" + i + ".bin"), 1);
                    StorageFileCollector collector = new StorageFileCollector();
                    bool slowed = false;
                    collector.BeforeEntryRead = path =>
                    {
                        if (slowed) return;
                        slowed = true;
                        Thread.Sleep(1300);
                    };

                    StorageScanReport report = collector.Collect(Locations(Loc("Docks", root)), new StorageSettings { TimeoutSeconds = 1 });

                    AssertTrue(report.PartialReasons.Contains(StorageWarningCodes.TimeLimit), "reason names the time limit");
                    AssertEqual(StorageStatusEnum.Partial, report.Status, "report is partial");
                    AssertTrue(report.Locations.Single().FileCount < 5, "files after the limit are not measured");
                }
                finally { Remove(root); }
                return Task.CompletedTask;
            });

            await RunTest("A file that vanishes during the scan is tolerated and counted", () =>
            {
                string root = TempDirectory();
                try
                {
                    WriteFile(Path.Combine(root, "stays.bin"), 40);
                    WriteFile(Path.Combine(root, "goes.bin"), 500);
                    StorageFileCollector collector = new StorageFileCollector();
                    collector.BeforeEntryRead = path =>
                    {
                        if (Path.GetFileName(path) == "goes.bin") File.Delete(path);
                    };

                    StorageScanReport report = collector.Collect(Locations(Loc("Docks", root)), new StorageSettings());

                    StorageLocation location = report.Locations.Single();
                    AssertEqual(40L, location.Bytes ?? -1, "the vanished file is not counted");
                    AssertEqual(1L, location.FileCount, "one file remains");
                    AssertEqual(1L, location.VanishedEntries, "the vanished entry is counted");
                    AssertEqual(StorageStatusEnum.Complete, report.Status, "a removed file is not a lower bound");
                }
                finally { Remove(root); }
                return Task.CompletedTask;
            });

            await RunTest("Links below the root are not followed", () =>
            {
                if (OperatingSystem.IsWindows())
                {
                    SkipTest("Links below the root are not followed", "Creating symbolic links needs a privilege on Windows.");
                    return Task.CompletedTask;
                }
                string root = TempDirectory();
                string outside = TempDirectory();
                try
                {
                    WriteFile(Path.Combine(root, "inside.bin"), 25);
                    WriteFile(Path.Combine(outside, "big.bin"), 1_000_000);
                    Directory.CreateSymbolicLink(Path.Combine(root, "dirlink"), outside);
                    File.CreateSymbolicLink(Path.Combine(root, "filelink"), Path.Combine(outside, "big.bin"));

                    StorageScanReport report = new StorageFileCollector().Collect(Locations(Loc("Docks", root)), new StorageSettings());

                    StorageLocation location = report.Locations.Single();
                    AssertEqual(25L, location.Bytes ?? -1, "only bytes inside the root are counted");
                    AssertEqual(1L, location.FileCount, "linked files are not counted");
                    AssertEqual(2L, location.LinksSkipped, "both links are counted as skipped");
                    AssertTrue(location.LargestFiles.All(f => !f.Path.Contains("big.bin")), "nothing outside the root is listed");
                }
                finally { Remove(root); Remove(outside); }
                return Task.CompletedTask;
            });

            await RunTest("A missing root reports Missing without making the scan partial", () =>
            {
                string root = Path.Combine(TempDirectory(), "absent");
                try
                {
                    StorageScanReport report = new StorageFileCollector().Collect(Locations(Loc("Docks", root)), new StorageSettings());
                    AssertEqual(StorageStatusEnum.Missing, report.Locations.Single().Status, "location status");
                    AssertEqual(StorageStatusEnum.Complete, report.Status, "an absent optional directory is not a bound");
                }
                finally { Remove(Path.GetDirectoryName(root)!); }
                return Task.CompletedTask;
            });

            await RunTest("Storage settings clamp every bound to a safe range", () =>
            {
                StorageSettings settings = new StorageSettings { MaxEntries = 0, MaxDepth = 100000, TimeoutSeconds = 0, RefreshIntervalSeconds = 1, HistoryCapacity = 1000000, LargestFileLimit = 0 };
                AssertEqual(100, settings.MaxEntries, "entries lower bound");
                AssertEqual(256, settings.MaxDepth, "depth upper bound");
                AssertEqual(1, settings.TimeoutSeconds, "timeout lower bound");
                AssertEqual(30, settings.RefreshIntervalSeconds, "interval lower bound");
                AssertEqual(2016, settings.HistoryCapacity, "history upper bound");
                AssertEqual(1, settings.LargestFileLimit, "largest-file lower bound");
                return Task.CompletedTask;
            });

            await RunTest("History keeps only the newest samples and survives a reload", () =>
            {
                string directory = TempDirectory();
                try
                {
                    string path = Path.Combine(directory, StorageHistoryStore.FileName);
                    StorageHistoryStore store = new StorageHistoryStore(path);
                    List<StorageHistoryPoint> history = new List<StorageHistoryPoint>();
                    for (int i = 1; i <= 5; i++)
                        history = store.Append(history, new StorageHistoryPoint { MeasuredUtc = new DateTime(2026, 1, i, 0, 0, 0, DateTimeKind.Utc), ObservedFileBytes = i * 10 }, 3, out bool saved);

                    AssertEqual(3, history.Count, "bounded in memory");
                    List<StorageHistoryPoint> reloaded = new StorageHistoryStore(path).Load(out bool readable);
                    AssertTrue(readable, "file is readable");
                    AssertEqual(3, reloaded.Count, "bounded on disk");
                    AssertEqual(30L, reloaded[0].ObservedFileBytes, "oldest kept sample is the third");
                    AssertEqual(50L, reloaded[2].ObservedFileBytes, "newest sample last");
                }
                finally { Remove(directory); }
                return Task.CompletedTask;
            });

            await RunTest("An unreadable history file restarts empty and reports it", () =>
            {
                string directory = TempDirectory();
                try
                {
                    string path = Path.Combine(directory, StorageHistoryStore.FileName);
                    File.WriteAllText(path, "{ not a list");
                    StorageHistoryStore store = new StorageHistoryStore(path);

                    List<StorageHistoryPoint> loaded = store.Load(out bool readable);
                    AssertFalse(readable, "corruption is reported");
                    AssertEqual(0, loaded.Count, "history restarts empty");

                    List<StorageHistoryPoint> next = store.Append(loaded, new StorageHistoryPoint { ObservedFileBytes = 1 }, 3, out bool saved);
                    AssertTrue(saved, "the next sample replaces the corrupt file");
                    AssertEqual(1, new StorageHistoryStore(path).Load(out bool again).Count, "file is valid again");
                    AssertTrue(again, "reload readable");
                }
                finally { Remove(directory); }
                return Task.CompletedTask;
            });

            await RunTest("Database measurement lists tables, allocation and exact counts on SQLite", async () =>
            {
                string directory = TempDirectory();
                try
                {
                    string database = CreateSqliteDatabase(directory);
                    DatabaseSettings settings = new DatabaseSettings { Filename = database };
                    DatabaseStorageMeasurer measurer = new DatabaseStorageMeasurer(settings);

                    StorageDatabase estimated = await measurer.MeasureAsync(false);
                    AssertEqual("Sqlite", estimated.Provider, "provider");
                    AssertEqual(StorageStatusEnum.Complete, estimated.Status, "status");
                    AssertEqual(new FileInfo(database).Length, estimated.AllocatedBytes ?? -1, "allocation equals the page-aligned file size");
                    AssertEqual("alpha,beta", String.Join(",", estimated.Tables.Select(t => t.Name)), "tables in name order");
                    AssertEqual(StorageCountKindEnum.Unavailable, estimated.Tables[0].CountKind, "no count without the exact request");
                    AssertTrue(estimated.Warnings.Contains(StorageWarningCodes.TableSizesUnavailable), "missing per-table sizes are named");
                    AssertTrue((estimated.UnusedBytes ?? -1) >= 0, "free-list bytes reported");

                    StorageDatabase exact = await measurer.MeasureAsync(true);
                    AssertEqual(3L, exact.Tables.Single(t => t.Name == "alpha").Rows ?? -1, "exact rows alpha");
                    AssertEqual(1L, exact.Tables.Single(t => t.Name == "beta").Rows ?? -1, "exact rows beta");
                    AssertEqual(StorageCountKindEnum.Exact, exact.Tables[0].CountKind, "count kind");
                }
                finally { Remove(directory); }
            });

            await RunTest("Collection caches the snapshot and a read never scans", async () =>
            {
                StorageLayout layout = CreateLayout();
                try
                {
                    using (StorageInventoryService service = new StorageInventoryService(layout.Settings, CreateLogging()))
                    {
                        StorageSnapshot before = service.Read();
                        AssertNull(before.MeasuredUtc, "nothing measured before the first collection");
                        AssertNull(before.Files, "no file scan yet");

                        await service.CollectAsync(false);
                        StorageSnapshot first = service.Read();
                        AssertEqual(StorageStatusEnum.Complete, first.Files!.Status, "scan complete");
                        AssertEqual(200L, CategoryBytes(first, "Docks"), "dock bytes");
                        AssertEqual(100L, CategoryBytes(first, "MissionLogs"), "mission log bytes");
                        AssertEqual(50L, CategoryBytes(first, "Logs"), "other log bytes");
                        AssertEqual(300L, CategoryBytes(first, "Repositories"), "repository bytes");
                        AssertEqual(400L, CategoryBytes(first, "Backups"), "backup bytes");
                        AssertEqual(10L, CategoryBytes(first, "ArchiveStaging"), "archive staging bytes");
                        AssertEqual(20L, CategoryBytes(first, "CodeIndex"), "code index bytes");
                        AssertEqual(30L, CategoryBytes(first, "OperatorEvidence"), "operator evidence bytes");
                        AssertEqual(5L, CategoryBytes(first, "Other"), "remainder of the data directory");
                        AssertEqual(new FileInfo(layout.Settings.Database.Filename).Length, CategoryBytes(first, "DatabaseFiles"), "database file bytes");
                        AssertEqual("Sqlite", first.Database.Provider, "database provider");
                        AssertEqual(1, first.History.Count, "one growth sample");
                        AssertEqual(first.Files.Locations.Sum(l => l.Bytes ?? 0), first.History[0].ObservedFileBytes, "the sample sums the locations");

                        WriteFile(Path.Combine(layout.Settings.DocksDirectory, "dock", "new.bin"), 1000);
                        StorageSnapshot cached = service.Read();
                        AssertEqual(200L, CategoryBytes(cached, "Docks"), "a read returns the cache and does not rescan");
                        AssertEqual(first.MeasuredUtc, cached.MeasuredUtc, "same measurement");

                        await service.CollectAsync(false);
                        StorageSnapshot refreshed = service.Read();
                        AssertEqual(1200L, CategoryBytes(refreshed, "Docks"), "a refresh measures the change");
                        AssertEqual(2, refreshed.History.Count, "the refresh adds a growth sample");
                    }
                }
                finally { Remove(layout.Root); Remove(layout.Evidence); }
            });

            await RunTest("Collection is read-only: no file is changed, moved or deleted", async () =>
            {
                StorageLayout layout = CreateLayout();
                try
                {
                    Dictionary<string, long> before = Listing(layout.Root).Concat(Listing(layout.Evidence)).ToDictionary(p => p.Key, p => p.Value);
                    using (StorageInventoryService service = new StorageInventoryService(layout.Settings, CreateLogging()))
                        await service.CollectAsync(true);
                    Dictionary<string, long> after = Listing(layout.Root).Concat(Listing(layout.Evidence)).ToDictionary(p => p.Key, p => p.Value);

                    List<string> added = after.Keys.Except(before.Keys).ToList();
                    AssertEqual(1, added.Count, "only one file appears");
                    AssertEqual(StorageHistoryStore.FileName, Path.GetFileName(added[0]), "the one new file is the history file");
                    foreach (KeyValuePair<string, long> entry in before)
                    {
                        AssertTrue(after.ContainsKey(entry.Key), "still present: " + entry.Key);
                        AssertEqual(entry.Value, after[entry.Key], "same length: " + entry.Key);
                    }
                }
                finally { Remove(layout.Root); Remove(layout.Evidence); }
            });

            await RunTest("Concurrent refresh requests join the running collection", async () =>
            {
                StorageLayout layout = CreateLayout();
                try
                {
                    TaskCompletionSource<bool> gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    using (StorageInventoryService service = new StorageInventoryService(layout.Settings, CreateLogging()))
                    {
                        service.BeforeCollect = async token => await gate.Task.ConfigureAwait(false);
                        AssertTrue(service.RequestRefresh(), "the first request starts a collection");
                        AssertFalse(service.RequestRefresh(), "a second request joins it");
                        AssertTrue(service.Read().Collecting, "the snapshot reports collecting");

                        gate.SetResult(true);
                        AssertTrue(await WaitUntil(() => !service.Read().Collecting && service.Read().MeasuredUtc != null), "the collection finishes");
                        AssertEqual(1, service.Read().History.Count, "one collection ran for both requests");
                        AssertTrue(service.RequestRefresh(), "a new request starts after the collection ends");
                        await WaitUntil(() => !service.Read().Collecting);
                    }
                }
                finally { Remove(layout.Root); Remove(layout.Evidence); }
            });

            await RunTest("Manual refresh works when the background refresh is off", async () =>
            {
                StorageLayout layout = CreateLayout();
                try
                {
                    layout.Settings.Storage.Enabled = false;
                    using (StorageInventoryService service = new StorageInventoryService(layout.Settings, CreateLogging()))
                    {
                        service.Start();
                        AssertEqual(0, service.Read().RefreshIntervalSeconds, "background refresh is reported off");
                        await Task.Delay(300);
                        AssertNull(service.Read().MeasuredUtc, "nothing collects on its own");

                        AssertTrue(service.RequestRefresh(), "a manual request starts");
                        AssertTrue(await WaitUntil(() => service.Read().MeasuredUtc != null), "the manual refresh fills the cache");
                    }
                }
                finally { Remove(layout.Root); Remove(layout.Evidence); }
            });

            await RunTest("The background loop collects at start and reports its interval", async () =>
            {
                StorageLayout layout = CreateLayout();
                try
                {
                    layout.Settings.Storage.RefreshIntervalSeconds = 120;
                    using (StorageInventoryService service = new StorageInventoryService(layout.Settings, CreateLogging()))
                    {
                        service.Start();
                        AssertTrue(await WaitUntil(() => service.Read().MeasuredUtc != null), "the first collection runs at start");
                        AssertEqual(120, service.Read().RefreshIntervalSeconds, "interval is reported");
                    }
                }
                finally { Remove(layout.Root); Remove(layout.Evidence); }
            });

            await RunTest("History persists across a restart and stays within the capacity", async () =>
            {
                StorageLayout layout = CreateLayout();
                try
                {
                    layout.Settings.Storage.HistoryCapacity = 2;
                    using (StorageInventoryService service = new StorageInventoryService(layout.Settings, CreateLogging()))
                    {
                        for (int i = 0; i < 4; i++) await service.CollectAsync(false);
                        AssertEqual(2, service.Read().History.Count, "bounded by the capacity");
                    }
                    using (StorageInventoryService restarted = new StorageInventoryService(layout.Settings, CreateLogging()))
                    {
                        StorageSnapshot snapshot = restarted.Read();
                        AssertEqual(2, snapshot.History.Count, "history is read back before any new collection");
                        AssertNull(snapshot.MeasuredUtc, "figures are not invented from history");
                    }
                }
                finally { Remove(layout.Root); Remove(layout.Evidence); }
            });

            await RunTest("An unreadable database is named in the snapshot while the file scan still reports", async () =>
            {
                StorageLayout layout = CreateLayout();
                try
                {
                    layout.Settings.Database.Filename = Path.Combine(layout.Root, "absent.db");
                    using (StorageInventoryService service = new StorageInventoryService(layout.Settings, CreateLogging()))
                    {
                        await service.CollectAsync(false);
                        StorageSnapshot snapshot = service.Read();
                        AssertEqual(StorageStatusEnum.Unavailable, snapshot.Database.Status, "database status");
                        AssertTrue(snapshot.Database.Warnings.Contains(StorageWarningCodes.DatabaseUnavailable), "reason is named");
                        AssertNotNull(snapshot.Files, "the file scan is still cached");
                        AssertEqual(200L, CategoryBytes(snapshot, "Docks"), "file figures unaffected");
                    }
                }
                finally { Remove(layout.Root); Remove(layout.Evidence); }
            });
        }

        private sealed class StorageLayout
        {
            public ArmadaSettings Settings { get; set; } = null!;
            public string Root { get; set; } = String.Empty;
            public string Evidence { get; set; } = String.Empty;
        }

        private static StorageLayout CreateLayout()
        {
            string root = TempDirectory();
            string evidence = TempDirectory();
            ArmadaSettings settings = new ArmadaSettings();
            settings.DataDirectory = root;
            settings.DocksDirectory = Path.Combine(root, "docks");
            settings.ReposDirectory = Path.Combine(root, "repos");
            settings.LogDirectory = Path.Combine(root, "logs");
            settings.CodeIndex.IndexDirectory = Path.Combine(root, "code-index");
            settings.CaptainEvidenceRoots = new List<CaptainEvidenceRoot> { new CaptainEvidenceRoot { Path = evidence } };
            WriteFile(Path.Combine(settings.DocksDirectory, "dock", "work.bin"), 200);
            WriteFile(Path.Combine(settings.ReposDirectory, "repo", "pack.bin"), 300);
            WriteFile(Path.Combine(settings.LogDirectory, "missions", "m.log"), 100);
            WriteFile(Path.Combine(settings.LogDirectory, "admiral.log"), 50);
            WriteFile(Path.Combine(root, "backups", "b.zip"), 400);
            WriteFile(Path.Combine(root, "archive-staging", "s.bin"), 10);
            WriteFile(Path.Combine(root, "code-index", "i.bin"), 20);
            WriteFile(Path.Combine(evidence, "e.bin"), 30);
            WriteFile(Path.Combine(root, "settings.json"), 5);
            settings.Database.Filename = CreateSqliteDatabase(root);
            return new StorageLayout { Settings = settings, Root = root, Evidence = evidence };
        }

        private static string CreateSqliteDatabase(string directory)
        {
            string path = Path.Combine(directory, "armada.db");
            using (SqliteConnection connection = new SqliteConnection("Data Source=" + path + ";Pooling=False"))
            {
                connection.Open();
                using (SqliteCommand command = connection.CreateCommand())
                {
                    command.CommandText = "CREATE TABLE alpha (id INTEGER PRIMARY KEY, name TEXT); CREATE INDEX alpha_name ON alpha(name);"
                        + "CREATE TABLE beta (id INTEGER PRIMARY KEY);"
                        + "INSERT INTO alpha (name) VALUES ('one'), ('two'), ('three'); INSERT INTO beta DEFAULT VALUES;";
                    command.ExecuteNonQuery();
                }
            }
            return path;
        }

        private static LoggingModule CreateLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        private static List<StorageLocation> Locations(params StorageLocation[] roots)
        {
            return roots.ToList();
        }

        private static StorageLocation Loc(string category, string path)
        {
            return new StorageLocation { Category = category, Path = path };
        }

        private static long BytesOf(StorageScanReport report, string category)
        {
            return report.Locations.Single(l => l.Category == category).Bytes ?? -1;
        }

        private static long CategoryBytes(StorageSnapshot snapshot, string category)
        {
            return snapshot.Files!.Locations.Where(l => l.Category == category).Sum(l => l.Bytes ?? 0);
        }

        private static Dictionary<string, long> Listing(string root)
        {
            return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToDictionary(f => f, f => new FileInfo(f).Length);
        }

        private static async Task<bool> WaitUntil(Func<bool> condition)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < deadline)
            {
                if (condition()) return true;
                await Task.Delay(25).ConfigureAwait(false);
            }
            return condition();
        }

        private static string TempDirectory()
        {
            string path = Path.Combine(Path.GetTempPath(), "armada-storage-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        private static void WriteFile(string path, int length)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, new byte[length]);
        }

        private static void Remove(string path)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, true); }
            catch (IOException) { /* Temporary test data is removed with the process temp root. */ }
        }
    }
}
