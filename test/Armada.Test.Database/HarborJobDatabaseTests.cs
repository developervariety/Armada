namespace Armada.Test.Database
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Harbor;
    using Armada.Core.Settings;

    /// <summary>Provider-backed Harbor job record persistence, revision guard and restart reconciliation.</summary>
    internal sealed class HarborJobDatabaseTests
    {
        private readonly DatabaseDriver _Driver;
        private readonly DatabaseSettings _Settings;

        internal HarborJobDatabaseTests(DatabaseDriver driver, DatabaseSettings settings)
        {
            _Driver = driver ?? throw new ArgumentNullException(nameof(driver));
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        internal async Task VerifyAsync(CancellationToken token)
        {
            int version = VersionFor(_Settings.Type);
            Dictionary<int, string> history = await new MigrationScenarioRunner(_Settings).ReadHistoryAsync(token).ConfigureAwait(false);
            DatabaseAssert.True(history.ContainsKey(version), "Harbor job migration " + version + " is recorded in history");
            DatabaseAssert.True(history[version].StartsWith("Persist Harbor job records|", StringComparison.Ordinal), "Harbor job migration " + version + " description: " + history[version]);

            string suffix = Guid.NewGuid().ToString("N");
            string runnerId = "hbr_jobs_" + suffix;
            HarborJobRecord running = NewRecord("hjob_run_" + suffix, runnerId, HarborJobStateEnum.Pending, 1);
            await _Driver.HarborJobs.CreateAsync(running, token).ConfigureAwait(false);

            HarborJobRecord? stored = await _Driver.HarborJobs.ReadAsync(running.JobId, token).ConfigureAwait(false);
            DatabaseAssert.NotNull(stored, "Harbor job record reads back");
            DatabaseAssert.Equal("mission:" + suffix, stored!.LaunchKey, "launch key");
            DatabaseAssert.Equal(4L, stored.EnrollmentGeneration, "enrollment generation");
            DatabaseAssert.True(stored.ProcessId == null && stored.ExitCode == null && stored.CompletedUtc == null, "nullable columns read back as null");
            DatabaseAssert.Equal("msn_" + suffix, stored.MissionId, "mission id");

            running.State = HarborJobStateEnum.Running;
            running.ProcessId = 4242;
            running.NextOutputSequence = 17;
            running.SessionGeneration = 9;
            running.Revision = 3;
            DatabaseAssert.True(await _Driver.HarborJobs.TryUpdateAsync(running, token).ConfigureAwait(false), "a higher revision replaces the record");
            HarborJobRecord delayed = NewRecord(running.JobId, runnerId, HarborJobStateEnum.Pending, 2);
            DatabaseAssert.True(!await _Driver.HarborJobs.TryUpdateAsync(delayed, token).ConfigureAwait(false), "a delayed lower revision never overwrites a newer state");
            DatabaseAssert.True(!await _Driver.HarborJobs.TryUpdateAsync(running, token).ConfigureAwait(false), "the same revision is not applied twice");
            stored = await _Driver.HarborJobs.ReadAsync(running.JobId, token).ConfigureAwait(false);
            DatabaseAssert.Equal(HarborJobStateEnum.Running, stored!.State, "state after the guarded update");
            DatabaseAssert.Equal(17L, stored.NextOutputSequence, "output sequence after the guarded update");
            DatabaseAssert.Equal(4242, stored.ProcessId ?? 0, "process id after the guarded update");

            HarborJobRecord exited = NewRecord("hjob_done_" + suffix, runnerId, HarborJobStateEnum.Exited, 5);
            exited.ExitCode = 0;
            exited.CompletedUtc = DateTime.UtcNow;
            await _Driver.HarborJobs.CreateAsync(exited, token).ConfigureAwait(false);

            List<HarborJobRecord> active = await _Driver.HarborJobs.EnumerateAsync(new HarborJobQuery { RunnerId = runnerId, ActiveOnly = true }, token).ConfigureAwait(false);
            DatabaseAssert.Equal(1, active.Count, "active filter excludes terminal jobs");
            DatabaseAssert.Equal(running.JobId, active[0].JobId, "active job listed");
            List<HarborJobRecord> scoped = await _Driver.HarborJobs.EnumerateAsync(new HarborJobQuery { RunnerId = runnerId, TenantId = "ten_jobs", UserId = "usr_jobs", Limit = 1 }, token).ConfigureAwait(false);
            DatabaseAssert.Equal(1, scoped.Count, "limit bounds the result");

            int failed = await _Driver.HarborJobs.FailActiveAsync(HarborJobCoordinator.ReasonAdmiralRestarted, DateTime.UtcNow, token).ConfigureAwait(false);
            DatabaseAssert.True(failed >= 1, "restart reconciliation fails the unfinished job");
            HarborJobRecord? reconciled = await _Driver.HarborJobs.ReadAsync(running.JobId, token).ConfigureAwait(false);
            DatabaseAssert.Equal(HarborJobStateEnum.Lost, reconciled!.State, "unfinished job lost after restart");
            DatabaseAssert.Equal(HarborJobCoordinator.ReasonAdmiralRestarted, reconciled.FailureReason, "restart reason recorded");
            DatabaseAssert.Equal(4L, reconciled.Revision, "restart reconciliation advances the revision");
            DatabaseAssert.True(reconciled.CompletedUtc.HasValue, "restart reconciliation records completion");
            HarborJobRecord? untouched = await _Driver.HarborJobs.ReadAsync(exited.JobId, token).ConfigureAwait(false);
            DatabaseAssert.Equal(HarborJobStateEnum.Exited, untouched!.State, "terminal jobs keep their state");

            using (DatabaseDriver reopened = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false))
            {
                HarborJobRecord? survived = await reopened.HarborJobs.ReadAsync(running.JobId, token).ConfigureAwait(false);
                DatabaseAssert.NotNull(survived, "Harbor job record survives provider reopen");
                DatabaseAssert.Equal(HarborJobStateEnum.Lost, survived!.State, "reopened state");
            }
        }

        private static HarborJobRecord NewRecord(string jobId, string runnerId, HarborJobStateEnum state, long revision)
        {
            string suffix = runnerId.Substring("hbr_jobs_".Length);
            DateTime now = DateTime.UtcNow;
            return new HarborJobRecord
            {
                JobId = jobId,
                RunnerId = runnerId,
                LaunchKey = "mission:" + suffix,
                TenantId = "ten_jobs",
                UserId = "usr_jobs",
                EnrollmentGeneration = 4,
                SessionGeneration = 2,
                State = state,
                MissionId = "msn_" + suffix,
                CaptainId = "cpt_" + suffix,
                Revision = revision,
                CreatedUtc = now,
                LastUpdateUtc = now
            };
        }

        private static int VersionFor(DatabaseTypeEnum type)
        {
            return type switch
            {
                DatabaseTypeEnum.Sqlite => 99,
                DatabaseTypeEnum.Postgresql => 100,
                DatabaseTypeEnum.Mysql => 91,
                DatabaseTypeEnum.SqlServer => 94,
                _ => throw new NotSupportedException()
            };
        }
    }
}
