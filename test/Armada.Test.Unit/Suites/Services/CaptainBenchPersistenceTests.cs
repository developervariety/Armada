namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.IO;
    using System.Threading;
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
    /// Covers the operator-bench vs quota-backoff distinction in the quarantine restore sweep. An
    /// operator bench with no expiry is an indefinite manual hold (null window) that must persist
    /// across the sweep — including an aggressive probe-driven restore — until an explicit unbench,
    /// so a benched captain is never auto-un-benched mid-voyage. A finite backoff window still
    /// auto-restores once it elapses.
    /// </summary>
    public sealed class CaptainBenchPersistenceTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Captain Bench Persistence";

        private static LoggingModule CreateLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        private static ArmadaSettings CreateSettings()
        {
            string id = Guid.NewGuid().ToString("N");
            ArmadaSettings settings = new ArmadaSettings();
            settings.DocksDirectory = Path.Combine(Path.GetTempPath(), "armada_bench_docks_" + id);
            settings.ReposDirectory = Path.Combine(Path.GetTempPath(), "armada_bench_repos_" + id);
            settings.LogDirectory = Path.Combine(Path.GetTempPath(), "armada_bench_logs_" + id);
            settings.MinIdleCaptains = 0;
            return settings;
        }

        /// <summary>Quota probe double that always reports the captain as recovered, so a probe-driven
        /// restore sweep would clear any captain it is allowed to probe.</summary>
        private sealed class AlwaysRecoveredProbe : ICaptainQuotaProbe
        {
            public Task<bool> HasRecoveredAsync(Captain captain, CancellationToken token = default) => Task.FromResult(true);
        }

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("IndefiniteOperatorBench_HasNoExpiryWindow", async () =>
            {
                using (TestDatabase testDatabase = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    SqliteDatabaseDriver database = testDatabase.Driver;
                    CaptainQuarantineService quarantine = new CaptainQuarantineService(database, CreateSettings(), CreateLogging());

                    Captain captain = await database.Captains.CreateAsync(new Captain("indefinite-bench-captain")).ConfigureAwait(false);

                    Captain? benched = await quarantine.BenchAsync(captain.Id, "operator hold", null).ConfigureAwait(false);

                    AssertNotNull(benched, "BenchAsync should resolve the captain.");
                    AssertEqual(CaptainStateEnum.Quarantined, benched!.State, "An operator bench should quarantine the captain.");
                    AssertNull(benched.QuarantineUntilUtc,
                        "An operator bench with no expiry must persist a null window (indefinite hold), not a coerced backoff deadline.");
                }
            }).ConfigureAwait(false);

            await RunTest("IndefiniteOperatorBench_SurvivesProbeRestoreSweep", async () =>
            {
                using (TestDatabase testDatabase = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    SqliteDatabaseDriver database = testDatabase.Driver;
                    ArmadaSettings settings = CreateSettings();
                    settings.CaptainQuarantine.UseProbeOnRestore = true;
                    CaptainQuarantineService quarantine = new CaptainQuarantineService(database, settings, CreateLogging(), new AlwaysRecoveredProbe());

                    Captain captain = await database.Captains.CreateAsync(new Captain("held-bench-captain")).ConfigureAwait(false);
                    await quarantine.BenchAsync(captain.Id, "operator hold", null).ConfigureAwait(false);

                    await quarantine.RestoreExpiredQuarantinesAsync().ConfigureAwait(false);

                    Captain? after = await database.Captains.ReadAsync(captain.Id).ConfigureAwait(false);
                    AssertNotNull(after, "Captain should remain persisted after the restore sweep.");
                    AssertEqual(CaptainStateEnum.Quarantined, after!.State,
                        "An indefinite operator bench must survive a positive-probe restore sweep (not auto-un-benched mid-voyage).");
                    AssertNull(after.QuarantineUntilUtc, "The indefinite hold should still carry a null window after the sweep.");
                }
            }).ConfigureAwait(false);

            await RunTest("TimedOperatorBench_KeepsExplicitExpiryAndHoldsUntilElapsed", async () =>
            {
                using (TestDatabase testDatabase = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    SqliteDatabaseDriver database = testDatabase.Driver;
                    CaptainQuarantineService quarantine = new CaptainQuarantineService(database, CreateSettings(), CreateLogging());

                    Captain captain = await database.Captains.CreateAsync(new Captain("timed-bench-captain")).ConfigureAwait(false);
                    DateTime until = DateTime.UtcNow.AddMinutes(30);
                    await quarantine.BenchAsync(captain.Id, "operator hold until window", until).ConfigureAwait(false);

                    Captain? benched = await database.Captains.ReadAsync(captain.Id).ConfigureAwait(false);
                    AssertNotNull(benched!.QuarantineUntilUtc, "An operator bench with an explicit expiry should retain a finite window.");

                    await quarantine.RestoreExpiredQuarantinesAsync().ConfigureAwait(false);

                    Captain? after = await database.Captains.ReadAsync(captain.Id).ConfigureAwait(false);
                    AssertEqual(CaptainStateEnum.Quarantined, after!.State,
                        "A timed operator bench whose window has not elapsed must stay quarantined.");
                }
            }).ConfigureAwait(false);

            await RunTest("ElapsedBackoffBench_RestoresOnSweep", async () =>
            {
                using (TestDatabase testDatabase = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    SqliteDatabaseDriver database = testDatabase.Driver;
                    CaptainQuarantineService quarantine = new CaptainQuarantineService(database, CreateSettings(), CreateLogging());

                    // Seed a quota/backoff quarantine whose finite window has already elapsed.
                    Captain captain = new Captain("elapsed-backoff-captain");
                    captain.State = CaptainStateEnum.Quarantined;
                    captain.QuarantineReason = "provider backoff";
                    captain.QuarantineUntilUtc = DateTime.UtcNow.AddMinutes(-5);
                    captain = await database.Captains.CreateAsync(captain).ConfigureAwait(false);

                    await quarantine.RestoreExpiredQuarantinesAsync().ConfigureAwait(false);

                    Captain? after = await database.Captains.ReadAsync(captain.Id).ConfigureAwait(false);
                    AssertEqual(CaptainStateEnum.Idle, after!.State,
                        "A finite backoff window that has elapsed should auto-restore the captain to Idle.");
                    AssertNull(after.QuarantineUntilUtc, "A cleared quarantine should drop its window.");
                }
            }).ConfigureAwait(false);
        }
    }
}
