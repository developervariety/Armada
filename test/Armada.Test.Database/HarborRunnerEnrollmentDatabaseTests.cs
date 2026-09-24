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

    /// <summary>Provider-backed Harbor enrollment persistence and compare-and-set tests.</summary>
    internal sealed class HarborRunnerEnrollmentDatabaseTests
    {
        private readonly DatabaseDriver _Driver;
        private readonly DatabaseSettings _Settings;

        internal HarborRunnerEnrollmentDatabaseTests(DatabaseDriver driver, DatabaseSettings settings)
        {
            _Driver = driver ?? throw new ArgumentNullException(nameof(driver));
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        internal async Task VerifyAsync(CancellationToken token)
        {
            string suffix = Guid.NewGuid().ToString("N");
            string runnerId = "hbr_db_" + suffix;
            HarborRunnerEnrollment enrollment = NewEnrollment(runnerId, 1);
            DatabaseAssert.True(await _Driver.HarborRunnerEnrollments.TryEnrollAsync(enrollment, 0, token).ConfigureAwait(false), "Initial enrollment insert");

            using (DatabaseDriver reopened = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false))
            {
                HarborRunnerEnrollment? stored = await reopened.HarborRunnerEnrollments.ReadAsync(runnerId, token).ConfigureAwait(false);
                DatabaseAssert.NotNull(stored, "Enrollment survives provider reopen");
                DatabaseAssert.Equal(1L, stored!.Generation, "Enrollment generation survives reopen");
            }

            string raceRunnerId = "hbr_race_" + suffix;
            HarborRunnerEnrollment raceEnrollment = NewEnrollment(raceRunnerId, 1);
            List<Task<bool>> insertAttempts = new List<Task<bool>>();
            for (int index = 0; index < 8; index++)
                insertAttempts.Add(_Driver.HarborRunnerEnrollments.TryEnrollAsync(raceEnrollment, 0, token));
            bool[] insertResults = await Task.WhenAll(insertAttempts).ConfigureAwait(false);
            int insertWinners = 0;
            foreach (bool result in insertResults) if (result) insertWinners++;
            DatabaseAssert.Equal(1, insertWinners, "Exactly one provider CAS insert wins");

            DatabaseAssert.True(await _Driver.HarborRunnerEnrollments.TryRevokeAsync(raceRunnerId, 1, "usr_revoke", DateTime.UtcNow, token).ConfigureAwait(false), "Provider CAS revoke");
            HarborRunnerEnrollment replacement = NewEnrollment(raceRunnerId, 3);
            List<Task<bool>> reenrollAttempts = new List<Task<bool>>();
            for (int index = 0; index < 8; index++)
                reenrollAttempts.Add(_Driver.HarborRunnerEnrollments.TryEnrollAsync(replacement, 2, token));
            bool[] reenrollResults = await Task.WhenAll(reenrollAttempts).ConfigureAwait(false);
            int reenrollWinners = 0;
            foreach (bool result in reenrollResults) if (result) reenrollWinners++;
            DatabaseAssert.Equal(1, reenrollWinners, "Exactly one provider CAS re-enrollment wins");

            // A re-enrollment that expects a later generation must never create the row: the row the caller
            // read may have been removed since, and every provider refuses rather than recreating it.
            string vanishedRunnerId = "hbr_vanished_" + suffix;
            DatabaseAssert.True(!await _Driver.HarborRunnerEnrollments.TryEnrollAsync(NewEnrollment(vanishedRunnerId, 5), 4, token).ConfigureAwait(false),
                "Re-enrollment against a missing row is refused");
            DatabaseAssert.True(await _Driver.HarborRunnerEnrollments.ReadAsync(vanishedRunnerId, token).ConfigureAwait(false) == null,
                "Refused re-enrollment creates no row");

            if (_Settings.Type == DatabaseTypeEnum.Mysql)
            {
                string overlengthRunnerId = "hbr_" + new string('界', 447);
                bool rejected = false;
                try
                {
                    await _Driver.HarborRunnerEnrollments.TryEnrollAsync(NewEnrollment(overlengthRunnerId, 1), 0, token).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    rejected = true;
                }
                DatabaseAssert.True(rejected, "MySQL rejects an overlength Unicode runner identifier");
                DatabaseAssert.True(await _Driver.HarborRunnerEnrollments.ReadAsync(overlengthRunnerId, token).ConfigureAwait(false) == null,
                    "MySQL does not truncate an overlength runner identifier");
            }
        }

        private static HarborRunnerEnrollment NewEnrollment(string runnerId, long generation)
        {
            DateTime now = DateTime.UtcNow;
            return new HarborRunnerEnrollment
            {
                RunnerId = runnerId,
                TenantId = "ten_db",
                UserId = "usr_db",
                AuthMethod = "Bearer",
                CredentialId = "crd_db",
                Generation = generation,
                Active = true,
                CreatedUtc = now,
                LastUpdateUtc = now
            };
        }
    }
}
