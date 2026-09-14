namespace Armada.Core.Database.Mysql.Implementations
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using MySqlConnector;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Harbor;

    /// <summary>MySQL implementation of durable Harbor runner enrollment operations.</summary>
    public sealed class HarborRunnerEnrollmentMethods : IHarborRunnerEnrollmentMethods
    {
        private readonly string _ConnectionString;

        /// <summary>Instantiate the enrollment methods.</summary>
        /// <param name="connectionString">MySQL connection string.</param>
        public HarborRunnerEnrollmentMethods(string connectionString)
        {
            _ConnectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        }

        /// <inheritdoc />
        public async Task<HarborRunnerEnrollment?> ReadAsync(string runnerId, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(runnerId)) throw new ArgumentException("Runner identifier is required.", nameof(runnerId));
            using (MySqlConnection connection = new MySqlConnection(_ConnectionString))
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (MySqlCommand command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT runner_id, tenant_id, user_id, auth_method, credential_id, generation, active, created_utc, last_update_utc, revoked_utc, revoked_by_user_id FROM harbor_runner_enrollments WHERE runner_id = @runner_id;";
                    command.Parameters.AddWithValue("@runner_id", runnerId.Trim());
                    using (MySqlDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        if (await reader.ReadAsync(token).ConfigureAwait(false)) return FromReader(reader);
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
            using (MySqlConnection connection = new MySqlConnection(_ConnectionString))
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                if (expectedGeneration == 0)
                {
                    using (MySqlCommand insert = connection.CreateCommand())
                    {
                        insert.CommandText = @"INSERT INTO harbor_runner_enrollments
                            (runner_id, tenant_id, user_id, auth_method, credential_id, generation, active, created_utc, last_update_utc)
                            VALUES (@runner_id, @tenant_id, @user_id, @auth_method, @credential_id, @generation, 1, @created_utc, @last_update_utc);";
                        Bind(insert, enrollment);
                        try
                        {
                            return await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false) == 1;
                        }
                        catch (MySqlException exception) when (exception.Number == 1062)
                        {
                            return false;
                        }
                    }
                }

                using (MySqlTransaction transaction = await connection.BeginTransactionAsync(token).ConfigureAwait(false))
                {
                    using (MySqlCommand update = connection.CreateCommand())
                    {
                        update.Transaction = transaction;
                        update.CommandText = @"UPDATE harbor_runner_enrollments SET tenant_id = @tenant_id, user_id = @user_id,
                            auth_method = @auth_method, credential_id = @credential_id, generation = @generation, active = 1,
                            last_update_utc = @last_update_utc, revoked_utc = NULL, revoked_by_user_id = NULL
                            WHERE runner_id = @runner_id AND active = 0 AND generation = @expected_generation;";
                        Bind(update, enrollment);
                        update.Parameters.AddWithValue("@expected_generation", expectedGeneration);
                        int changed = await update.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                        await transaction.CommitAsync(token).ConfigureAwait(false);
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
            using (MySqlConnection connection = new MySqlConnection(_ConnectionString))
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (MySqlCommand command = connection.CreateCommand())
                {
                    command.CommandText = @"UPDATE harbor_runner_enrollments SET active = 0, generation = generation + 1,
                        last_update_utc = @revoked_utc, revoked_utc = @revoked_utc, revoked_by_user_id = @revoked_by_user_id
                        WHERE runner_id = @runner_id AND active = 1 AND generation = @expected_generation;";
                    command.Parameters.AddWithValue("@runner_id", runnerId.Trim());
                    command.Parameters.AddWithValue("@expected_generation", expectedGeneration);
                    command.Parameters.AddWithValue("@revoked_utc", revokedUtc.ToUniversalTime());
                    command.Parameters.AddWithValue("@revoked_by_user_id", revokedByUserId.Trim());
                    return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) == 1;
                }
            }
        }

        private static void Bind(MySqlCommand command, HarborRunnerEnrollment enrollment)
        {
            command.Parameters.AddWithValue("@runner_id", enrollment.RunnerId);
            command.Parameters.AddWithValue("@tenant_id", enrollment.TenantId);
            command.Parameters.AddWithValue("@user_id", enrollment.UserId);
            command.Parameters.AddWithValue("@auth_method", enrollment.AuthMethod);
            command.Parameters.AddWithValue("@credential_id", (object?)enrollment.CredentialId ?? DBNull.Value);
            command.Parameters.AddWithValue("@generation", enrollment.Generation);
            command.Parameters.AddWithValue("@created_utc", enrollment.CreatedUtc.ToUniversalTime());
            command.Parameters.AddWithValue("@last_update_utc", enrollment.LastUpdateUtc.ToUniversalTime());
        }

        private static void ValidateEnrollmentGeneration(HarborRunnerEnrollment enrollment, long expectedGeneration)
        {
            if (expectedGeneration < 0 || expectedGeneration == Int64.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(expectedGeneration), "Expected generation is outside the supported range.");
            if (enrollment.Generation <= 0 || enrollment.Generation == Int64.MaxValue
                || enrollment.Generation != expectedGeneration + 1)
                throw new ArgumentOutOfRangeException(nameof(enrollment), "Enrollment generation must be expected generation plus one.");
        }

        private static HarborRunnerEnrollment FromReader(MySqlDataReader reader)
        {
            return new HarborRunnerEnrollment
            {
                RunnerId = reader.GetString(0),
                TenantId = reader.GetString(1),
                UserId = reader.GetString(2),
                AuthMethod = reader.GetString(3),
                CredentialId = reader.IsDBNull(4) ? null : reader.GetString(4),
                Generation = reader.GetInt64(5),
                Active = reader.GetBoolean(6),
                CreatedUtc = ToUtc(reader.GetDateTime(7)),
                LastUpdateUtc = ToUtc(reader.GetDateTime(8)),
                RevokedUtc = reader.IsDBNull(9) ? null : ToUtc(reader.GetDateTime(9)),
                RevokedByUserId = reader.IsDBNull(10) ? null : reader.GetString(10)
            };
        }

        private static DateTime ToUtc(DateTime value)
        {
            return DateTime.SpecifyKind(value, DateTimeKind.Utc);
        }
    }
}
