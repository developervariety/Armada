namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services.Archive;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// Coverage for the history archive: the object key layout writers and the expiry job share,
    /// and the settings defaults that keep history while no archive is configured.
    /// </summary>
    public class ArchiveTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "History Archive";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("Archive keys place raw files, database exports, manifests and trace records under one prefix by month", () =>
            {
                DateTime when = new DateTime(2026, 7, 29, 2, 23, 12, DateTimeKind.Utc);
                (string Actual, string Expected)[] cases = new[]
                {
                    (ArchiveKeys.Raw("armada", "missionLogs", when, "msn_a.log"), "armada/raw/missionLogs/2026/07/msn_a.log"),
                    (ArchiveKeys.Raw("armada", "operatorEvidence", when, "proof-v1\\logs/run.txt"), "armada/raw/operatorEvidence/2026/07/proof-v1/logs/run.txt"),
                    (ArchiveKeys.Raw("", "diffs", when, "msn_a.diff"), "raw/diffs/2026/07/msn_a.diff"),
                    (ArchiveKeys.Database("armada/", "missions", when, "run1"), "armada/raw/db/missions/2026-07-29T022312Z-run1.jsonl.gz"),
                    (ArchiveKeys.Manifest("armada", when, "run1"), "armada/raw/manifests/2026/07/2026-07-29T022312Z-run1.jsonl"),
                    (ArchiveKeys.Trace("armada", when, "msn_a"), "armada/trace/2026/07/msn_a.json"),
                    (ArchiveKeys.FolderPrefix("armada", ArchiveKeys.RawFolder), "armada/raw/")
                };
                foreach ((string actual, string expected) in cases)
                {
                    AssertEqual(expected, actual, "archive key");
                }
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("An archived path cannot climb out of its category with a dot-dot segment", () =>
            {
                DateTime when = DateTime.UtcNow;
                bool refused = false;
                try
                {
                    ArchiveKeys.Raw("armada", "missionLogs", when, "../settings.json");
                }
                catch (ArgumentException)
                {
                    refused = true;
                }
                AssertTrue(refused, "a '..' segment is refused, not written as a key");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("Archive settings keep history and stay off until a bucket, endpoint and credentials file are set", () =>
            {
                ArchiveSettings settings = new ArchiveSettings();
                AssertFalse(settings.Enabled, "the archive is off by default");
                AssertTrue(settings.KeepHistoryWithoutArchive, "history is kept by default while no archive is configured");
                AssertFalse(settings.IsConfigured, "an empty section is not configured");
                AssertEqual(90, settings.RawRetentionDays, "raw retention default");
                AssertEqual(365, settings.TraceRetentionDays, "trace retention default");
                settings.Endpoint = "https://s3.example.com/";
                settings.Bucket = "history";
                settings.CredentialsFile = "/run/archive/credentials";
                AssertTrue(settings.IsConfigured, "endpoint, bucket and credentials file configure the sink");
                AssertEqual("https://s3.example.com", settings.Endpoint, "a trailing slash is trimmed");
                settings.RawRetentionDays = 0;
                AssertEqual(1, settings.RawRetentionDays, "raw retention never reaches zero, which would delete at once");
                AssertEqual(HistoryRetentionModeEnum.Keep, settings.ResolveHistoryMode(), "configured but disabled keeps history");
                settings.Enabled = true;
                AssertEqual(HistoryRetentionModeEnum.Archive, settings.ResolveHistoryMode(), "enabled and configured archives history");
                settings.Bucket = "";
                AssertEqual(HistoryRetentionModeEnum.Keep, settings.ResolveHistoryMode(), "enabled without a bucket keeps history");
                settings.KeepHistoryWithoutArchive = false;
                AssertEqual(HistoryRetentionModeEnum.Delete, settings.ResolveHistoryMode(), "with keeping off and no archive, history is deleted");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("The archive deny list refuses credentials, settings and their backups, keys and secret folders", () =>
            {
                (string Path, bool Denied)[] cases = new[]
                {
                    ("auth.json", true),
                    ("logs/settings.json", true),
                    ("settings.json.bak", true),
                    ("settings.20261022.json", true),
                    ("settings-backup.json", true),
                    (".env", true),
                    (".env.production", true),
                    ("deploy.env", true),
                    (".git-credentials", true),
                    ("cloud_credentials", true),
                    ("client_secret.txt", true),
                    ("mcp-auth-header", true),
                    ("server.pem", true),
                    ("tls.key", true),
                    ("id_ed25519.pub", true),
                    ("secrets/token.txt", true),
                    ("home/.ssh/config", true),
                    ("missions/msn_a.log", false),
                    ("diffs/msn_a.diff", false),
                    ("operator-evidence/proof/receipt.json", false),
                    ("project/session.jsonl", false),
                    ("keyboard.md", false)
                };
                foreach ((string path, bool denied) in cases)
                    AssertEqual(denied, ArchiveDenyList.IsDenied(path), "deny list for " + path);
                AssertTrue(ArchiveDenyList.IsDenied("notes.private", new[] { "*.private" }), "a configured pattern is denied");
                AssertFalse(ArchiveDenyList.IsDenied("notes.private"), "without the configured pattern the file is allowed");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("Archive expiry deletes raw objects past raw retention and trace objects past trace retention, under its own prefix only", async () =>
            {
                LoggingModule logging = new LoggingModule();
                logging.Settings.EnableConsole = false;
                ArchiveSettings settings = new ArchiveSettings();
                FakeArchiveSink sink = new FakeArchiveSink();
                DateTime now = DateTime.UtcNow;
                byte[] body = new byte[] { 1, 2, 3 };
                sink.Seed("armada/raw/missionLogs/2026/01/old.log", body, now.AddDays(-120));
                sink.Seed("armada/raw/missionLogs/2026/06/recent.log", body, now.AddDays(-30));
                sink.Seed("armada/trace/2025/06/old.json", body, now.AddDays(-400));
                sink.Seed("armada/trace/2026/01/kept.json", body, now.AddDays(-120));
                sink.Seed("other/raw/missionLogs/2026/01/foreign.log", body, now.AddDays(-400));
                ArchiveExpiryService service = new ArchiveExpiryService(logging, () => settings) { ArchiveSinkFactory = _ => sink };

                ArchiveExpiryResult off = await service.ExpireAsync().ConfigureAwait(false);
                AssertFalse(off.Ran, "a disabled archive is not listed");
                AssertEqual(0, sink.Deleted.Count, "a disabled archive deletes nothing");

                settings.Enabled = true;
                settings.Endpoint = "http://archive.example.invalid";
                settings.Bucket = "history";
                settings.CredentialsFile = "/run/archive/credentials";
                ArchiveExpiryResult run = await service.ExpireAsync().ConfigureAwait(false);

                AssertTrue(run.Ran, "an active archive is listed");
                AssertEqual(2, run.Deleted, "one raw and one trace object are past retention");
                AssertTrue(sink.Deleted.Contains("armada/raw/missionLogs/2026/01/old.log"), "the old raw object is deleted");
                AssertTrue(sink.Deleted.Contains("armada/trace/2025/06/old.json"), "the old trace object is deleted");
                AssertTrue(sink.Objects.ContainsKey("armada/raw/missionLogs/2026/06/recent.log"), "a raw object inside raw retention stays");
                AssertTrue(sink.Objects.ContainsKey("armada/trace/2026/01/kept.json"), "a trace object older than raw retention but inside trace retention stays");
                AssertTrue(sink.Objects.ContainsKey("other/raw/missionLogs/2026/01/foreign.log"), "an object under another prefix is never touched");
            }).ConfigureAwait(false);
        }
    }
}
