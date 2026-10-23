namespace Test.Shared.Suites.Runtimes
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Runtimes;
    using SyslogLogging;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// Descriptors for <see cref="HarborProcessLedger"/>: agent processes that outlive a Harbor run are stopped by the
    /// next one, and a process that merely reused a recorded id is left alone.
    /// </summary>
    public sealed class HarborProcessLedgerSuite : IArmadaTestSuite
    {
        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the Harbor process ledger suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(Case("orphan_from_earlier_run_is_stopped", "An agent process recorded by an earlier Harbor run that is still running is stopped at the next startup", TestTags.Reliability, () =>
            {
                if (OperatingSystem.IsWindows()) return;
                string path = Path.Combine(Path.GetTempPath(), "armada-ledger-" + Guid.NewGuid().ToString("N") + ".json");
                using Process orphan = Process.Start(new ProcessStartInfo("sleep", "60") { UseShellExecute = false })!;
                try
                {
                    new HarborProcessLedger(path, Logging()).Record("job-1", orphan.Id);
                    AssertEqual(1, new HarborProcessLedger(path, Logging()).StopOrphans(), "the next run stops the orphan");
                    AssertTrue(orphan.WaitForExit(5000), "the orphan exited");
                    AssertEqual(0, new HarborProcessLedger(path, Logging()).StopOrphans(), "the ledger starts empty afterwards");
                }
                finally
                {
                    try { if (!orphan.HasExited) orphan.Kill(true); } catch { }
                    try { File.Delete(path); } catch { }
                }
            }));

            cases.Add(Case("reused_process_id_is_left_alone", "A running process whose start time differs from the record (a reused id) is not stopped; exited jobs are forgotten", TestTags.Negative, () =>
            {
                if (OperatingSystem.IsWindows()) return;
                string path = Path.Combine(Path.GetTempPath(), "armada-ledger-" + Guid.NewGuid().ToString("N") + ".json");
                using Process unrelated = Process.Start(new ProcessStartInfo("sleep", "60") { UseShellExecute = false })!;
                try
                {
                    List<HarborProcessLedgerEntry> stale = new List<HarborProcessLedgerEntry>
                    {
                        new HarborProcessLedgerEntry { JobId = "old-job", ProcessId = unrelated.Id, StartedUtc = DateTime.UtcNow.AddHours(-3) }
                    };
                    File.WriteAllText(path, JsonSerializer.Serialize(stale));
                    AssertEqual(0, new HarborProcessLedger(path, Logging()).StopOrphans(), "a reused id is not an orphan");
                    AssertFalse(unrelated.HasExited, "the unrelated process keeps running");

                    HarborProcessLedger ledger = new HarborProcessLedger(path, Logging());
                    ledger.Record("job-2", unrelated.Id);
                    ledger.Remove("job-2");
                    ledger.Record("api-loop", 2000000001);
                    AssertEqual(0, new HarborProcessLedger(path, Logging()).StopOrphans(), "an exited job and an in-process loop are not recorded");
                    AssertFalse(unrelated.HasExited);
                }
                finally
                {
                    try { if (!unrelated.HasExited) unrelated.Kill(true); } catch { }
                    try { File.Delete(path); } catch { }
                }
            }));

            return new TestSuiteDescriptor(
                suiteId: "Runtimes.HarborProcessLedger",
                displayName: "Harbor Process Ledger",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        private static LoggingModule Logging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        private static TestCaseDescriptor Case(string caseId, string displayName, string tag, Action body)
        {
            return new TestCaseDescriptor(
                suiteId: "Runtimes.HarborProcessLedger",
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) =>
                {
                    body();
                    return Task.CompletedTask;
                },
                tags: new List<string> { tag });
        }

        #endregion
    }
}
