namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Threading.Tasks;
    using Armada.Core.Services.Archive;
    using Armada.Core.Settings;
    using Armada.Test.Common;

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
                return Task.CompletedTask;
            }).ConfigureAwait(false);
        }
    }
}
