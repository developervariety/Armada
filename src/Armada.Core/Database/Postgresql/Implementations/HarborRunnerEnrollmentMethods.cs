namespace Armada.Core.Database.Postgresql.Implementations
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Npgsql;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Harbor;

    /// <summary>PostgreSQL implementation of durable Harbor runner enrollment operations.</summary>
    public sealed class HarborRunnerEnrollmentMethods : IHarborRunnerEnrollmentMethods
    {
        private readonly NpgsqlDataSource _DataSource;

        /// <summary>Instantiate the enrollment methods.</summary>
        /// <param name="dataSource">PostgreSQL data source.</param>
        public HarborRunnerEnrollmentMethods(NpgsqlDataSource dataSource)
        {
            _DataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        }

        /// <inheritdoc />
        public async Task<HarborRunnerEnrollment?> ReadAsync(string runnerId, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(runnerId)) throw new ArgumentException("Runner identifier is required.", nameof(runnerId));
            using (NpgsqlConnection connection = await _DataSource.OpenConnectionAsync(token).ConfigureAwait(false))
            {
                using (NpgsqlCommand command = new NpgsqlCommand("SELECT runner_id, tenant_id, user_id, auth_method, credential_id, generation, active, created_utc, last_update_utc, revoked_utc, revoked_by_user_id FROM harbor_runner_enrollments WHERE runner_id = @runner_id;", connection))
                {
                    command.Parameters.AddWithValue("@runner_id", runnerId.Trim());
                    using (NpgsqlDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
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
            using (NpgsqlConnection connection = await _DataSource.OpenConnectionAsync(token).ConfigureAwait(false))
            {
                using (NpgsqlTransaction transaction = await connection.BeginTransactionAsync(token).ConfigureAwait(false))
                {
                    using (NpgsqlCommand command = new NpgsqlCommand(@"INSERT INTO harbor_runner_enrollments
                        (runner_id, tenant_id, user_id, auth_method, credential_id, generation, active, created_utc, last_update_utc)
                        VALUES (@runner_id, @tenant_id, @user_id, @auth_method, @credential_id, @generation, TRUE, @created_utc, @last_update_utc)
                        ON CONFLICT (runner_id) DO UPDATE SET tenant_id = EXCLUDED.tenant_id, user_id = EXCLUDED.user_id,
                        auth_method = EXCLUDED.auth_method, credential_id = EXCLUDED.credential_id,
                        generation = EXCLUDED.generation, active = TRUE, last_update_utc = EXCLUDED.last_update_utc,
                        revoked_utc = NULL, revoked_by_user_id = NULL
                        WHERE harbor_runner_enrollments.active = FALSE
                          AND harbor_runner_enrollments.generation = @expected_generation;", connection, transaction))
                    {
                        Bind(command, enrollment);
                        command.Parameters.AddWithValue("@expected_generation", expectedGeneration);
                        int changed = await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
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
            using (NpgsqlConnection connection = await _DataSource.OpenConnectionAsync(token).ConfigureAwait(false))
            {
                using (NpgsqlCommand command = new NpgsqlCommand(@"UPDATE harbor_runner_enrollments SET active = FALSE,
                    generation = generation + 1, last_update_utc = @revoked_utc, revoked_utc = @revoked_utc,
                    revoked_by_user_id = @revoked_by_user_id
                    WHERE runner_id = @runner_id AND active = TRUE AND generation = @expected_generation;", connection))
                {
                    command.Parameters.AddWithValue("@runner_id", runnerId.Trim());
                    command.Parameters.AddWithValue("@expected_generation", expectedGeneration);
                    command.Parameters.AddWithValue("@revoked_utc", revokedUtc.ToUniversalTime());
                    command.Parameters.AddWithValue("@revoked_by_user_id", revokedByUserId.Trim());
                    return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) == 1;
                }
            }
        }

        private static void Bind(NpgsqlCommand command, HarborRunnerEnrollment enrollment)
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

        private static HarborRunnerEnrollment FromReader(NpgsqlDataReader reader)
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
                CreatedUtc = DateTime.SpecifyKind(reader.GetDateTime(7), DateTimeKind.Utc),
                LastUpdateUtc = DateTime.SpecifyKind(reader.GetDateTime(8), DateTimeKind.Utc),
                RevokedUtc = reader.IsDBNull(9) ? null : DateTime.SpecifyKind(reader.GetDateTime(9), DateTimeKind.Utc),
                RevokedByUserId = reader.IsDBNull(10) ? null : reader.GetString(10)
            };
        }
    }
}
