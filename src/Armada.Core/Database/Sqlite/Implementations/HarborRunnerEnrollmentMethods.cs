namespace Armada.Core.Database.Sqlite.Implementations
{
    using System;
    using System.Globalization;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Data.Sqlite;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Harbor;

    /// <summary>SQLite implementation of durable Harbor runner enrollment operations.</summary>
    public sealed class HarborRunnerEnrollmentMethods : IHarborRunnerEnrollmentMethods
    {
        private readonly SqliteDatabaseDriver _Driver;

        /// <summary>Instantiate the enrollment methods.</summary>
        /// <param name="driver">SQLite database driver.</param>
        public HarborRunnerEnrollmentMethods(SqliteDatabaseDriver driver)
        {
            _Driver = driver ?? throw new ArgumentNullException(nameof(driver));
        }

        /// <inheritdoc />
        public async Task<HarborRunnerEnrollment?> ReadAsync(string runnerId, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(runnerId)) throw new ArgumentException("Runner identifier is required.", nameof(runnerId));
            using (SqliteConnection connection = new SqliteConnection(_Driver.ConnectionString))
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (SqliteCommand command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT runner_id, tenant_id, user_id, auth_method, credential_id, generation, active, created_utc, last_update_utc, revoked_utc, revoked_by_user_id FROM harbor_runner_enrollments WHERE runner_id = @runner_id;";
                    StoredValueBinder.Value(command, "@runner_id", runnerId.Trim());
                    using (SqliteDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        if (await reader.ReadAsync(token).ConfigureAwait(false)) return HarborRunnerEnrollmentColumns.Read(reader, SqliteDatabaseDriver.StoredValues);
                    }
                }
            }
            return null;
        }

        /// <inheritdoc />
        public async Task<bool> TryEnrollAsync(HarborRunnerEnrollment enrollment, long expectedGeneration, CancellationToken token = default)
        {
            if (enrollment == null) throw new ArgumentNullException(nameof(enrollment));
            ValidateEnrollmentGeneration(enrollment, expectedGeneration);
            using (SqliteConnection connection = new SqliteConnection(_Driver.ConnectionString))
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (SqliteTransaction transaction = connection.BeginTransaction())
                {
                    using (SqliteCommand insert = connection.CreateCommand())
                    {
                        insert.Transaction = transaction;
                        insert.CommandText = @"INSERT OR IGNORE INTO harbor_runner_enrollments
                            (runner_id, tenant_id, user_id, auth_method, credential_id, generation, active, created_utc, last_update_utc)
                            VALUES (@runner_id, @tenant_id, @user_id, @auth_method, @credential_id, @generation, 1, @created_utc, @last_update_utc);";
                        Bind(insert, enrollment);
                        int inserted = expectedGeneration == 0
                            ? await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false)
                            : 0;
                        if (inserted == 1)
                        {
                            transaction.Commit();
                            return true;
                        }
                    }

                    using (SqliteCommand update = connection.CreateCommand())
                    {
                        update.Transaction = transaction;
                        update.CommandText = @"UPDATE harbor_runner_enrollments SET tenant_id = @tenant_id, user_id = @user_id,
                            auth_method = @auth_method, credential_id = @credential_id, generation = @generation, active = 1,
                            last_update_utc = @last_update_utc, revoked_utc = NULL, revoked_by_user_id = NULL
                            WHERE runner_id = @runner_id AND active = 0 AND generation = @expected_generation;";
                        Bind(update, enrollment);
                        StoredValueBinder.Value(update, "@expected_generation", expectedGeneration);
                        int changed = await update.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                        transaction.Commit();
                        return changed == 1;
                    }
                }
            }
        }

        /// <inheritdoc />
        public async Task<bool> TryRevokeAsync(string runnerId, long expectedGeneration, string revokedByUserId, DateTime revokedUtc, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(runnerId)) throw new ArgumentException("Runner identifier is required.", nameof(runnerId));
            if (String.IsNullOrWhiteSpace(revokedByUserId)) throw new ArgumentException("Revoking administrator is required.", nameof(revokedByUserId));
            if (expectedGeneration <= 0 || expectedGeneration == Int64.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(expectedGeneration), "Expected generation must be positive and incrementable.");
            using (SqliteConnection connection = new SqliteConnection(_Driver.ConnectionString))
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (SqliteCommand command = connection.CreateCommand())
                {
                    command.CommandText = @"UPDATE harbor_runner_enrollments SET active = 0, generation = generation + 1,
                        last_update_utc = @last_update_utc, revoked_utc = @revoked_utc, revoked_by_user_id = @revoked_by_user_id
                        WHERE runner_id = @runner_id AND active = 1 AND generation = @expected_generation;";
                    StoredValueBinder.Value(command, "@runner_id", runnerId.Trim());
                    StoredValueBinder.Value(command, "@expected_generation", expectedGeneration);
                    SqliteDatabaseDriver.StoredBinder.For(command, "harbor_runner_enrollments").Utc("@last_update_utc", "last_update_utc", revokedUtc);
                    SqliteDatabaseDriver.StoredBinder.For(command, "harbor_runner_enrollments").Utc("@revoked_utc", "revoked_utc", revokedUtc);
                    StoredValueBinder.Value(command, "@revoked_by_user_id", revokedByUserId.Trim());
                    return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) == 1;
                }
            }
        }

        private static void Bind(SqliteCommand command, HarborRunnerEnrollment enrollment)
        {
            HarborRunnerEnrollmentColumns.Write(SqliteDatabaseDriver.StoredBinder.For(command, "harbor_runner_enrollments"), enrollment);
        }

        private static void ValidateEnrollmentGeneration(HarborRunnerEnrollment enrollment, long expectedGeneration)
        {
            if (expectedGeneration < 0 || expectedGeneration == Int64.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(expectedGeneration), "Expected generation is outside the supported range.");
            if (enrollment.Generation <= 0 || enrollment.Generation == Int64.MaxValue
                || enrollment.Generation != expectedGeneration + 1)
                throw new ArgumentOutOfRangeException(nameof(enrollment), "Enrollment generation must be expected generation plus one.");
        }
    }
}
