namespace Armada.Core.Database.Mysql
{
    using System;
    using System.Security.Cryptography;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using MySqlConnector;
    using Armada.Core.Database.Mysql.Queries;

    /// <summary>
    /// Resumes pending MySQL migrations across implicit DDL commits.
    /// Historical completed migrations retain their original ledger records.
    /// </summary>
    internal sealed class MysqlMigrationRunner
    {
        private readonly MySqlConnection _Connection;
        private readonly MysqlSchemaCompatibility _Compatibility;
        private readonly Action<int, int>? _Checkpoint;

        internal MysqlMigrationRunner(MySqlConnection connection, Action<int, int>? checkpoint = null)
        {
            _Connection = connection;
            _Checkpoint = checkpoint;
            _Compatibility = new MysqlSchemaCompatibility(connection);
        }

        internal async Task InitializeJournalAsync(CancellationToken token)
        {
            await _Compatibility.ExecuteSqlAsync(@"CREATE TABLE IF NOT EXISTS schema_migration_statements (
                version INT NOT NULL, ordinal INT NOT NULL, checksum VARCHAR(64) NOT NULL,
                completed_utc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
                PRIMARY KEY (version, ordinal));", token).ConfigureAwait(false);
        }

        internal async Task EnsurePrerequisitesAsync(CancellationToken token)
        {
            await _Compatibility.EnsureUniqueContractsAsync(token).ConfigureAwait(false);
            await _Compatibility.ExecuteSqlAsync(@"CREATE TABLE IF NOT EXISTS schema_repairs (
                id VARCHAR(128) NOT NULL PRIMARY KEY, checksum VARCHAR(64) NOT NULL,
                applied_utc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6));", token).ConfigureAwait(false);
            const string id = "fork-operational-prerequisites-v1";
            string checksum = Hash(String.Join("\n", ForkOperationalSchema.Statements));
            using (MySqlCommand command = _Connection.CreateCommand())
            {
                command.CommandText = "SELECT checksum FROM schema_repairs WHERE id=@id;";
                command.Parameters.AddWithValue("@id", id);
                object? found = await command.ExecuteScalarAsync(token).ConfigureAwait(false);
                if (found != null && found != DBNull.Value && (string)found != checksum)
                    throw new InvalidOperationException("MySQL prerequisite repair definition changed");
            }
            foreach (string statement in ForkOperationalSchema.Statements)
                await _Compatibility.ExecuteAsync(statement, 0, token).ConfigureAwait(false);
            using (MySqlCommand command = _Connection.CreateCommand())
            {
                command.CommandText = "INSERT INTO schema_repairs (id,checksum) SELECT @id,@checksum WHERE NOT EXISTS (SELECT 1 FROM schema_repairs WHERE id=@id);";
                command.Parameters.AddWithValue("@id", id);
                command.Parameters.AddWithValue("@checksum", checksum);
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
        }

        internal async Task ApplyAsync(SchemaMigration migration, CancellationToken token)
        {
            for (int ordinal = 0; ordinal < migration.Statements.Count; ordinal++)
            {
                string statement = migration.Statements[ordinal];
                string checksum = Hash(statement);
                bool completed;
                using (MySqlCommand command = _Connection.CreateCommand())
                {
                    command.CommandText = "SELECT checksum FROM schema_migration_statements WHERE version=@version AND ordinal=@ordinal;";
                    command.Parameters.AddWithValue("@version", migration.Version);
                    command.Parameters.AddWithValue("@ordinal", ordinal);
                    object? found = await command.ExecuteScalarAsync(token).ConfigureAwait(false);
                    completed = found != null && found != DBNull.Value;
                    if (completed && (string)found! != checksum)
                        throw new InvalidOperationException("MySQL pending migration statement changed: " + migration.Version + "/" + ordinal);
                }
                bool parallelIndex = migration.Version == 42;
                bool packIndex = migration.Version == 43 && ordinal >= 2;
                bool ddl = parallelIndex || packIndex || statement.TrimStart().StartsWith("CREATE ", StringComparison.OrdinalIgnoreCase)
                    || statement.TrimStart().StartsWith("ALTER ", StringComparison.OrdinalIgnoreCase)
                    || statement.TrimStart().StartsWith("DROP ", StringComparison.OrdinalIgnoreCase);
                if (ddl)
                {
                    if (parallelIndex)
                    {
                        if (ordinal >= TableQueries.MigrationV42Statements.Length || statement != TableQueries.MigrationV42Statements[ordinal])
                            throw new InvalidOperationException("Unrecognized parallel stage index transition");
                        if (ordinal == 0) await _Compatibility.EnsureParallelStageIndexAsync(token).ConfigureAwait(false);
                    }
                    else if (packIndex)
                    {
                        if (ordinal >= TableQueries.MigrationV43Statements.Length || statement != TableQueries.MigrationV43Statements[ordinal])
                            throw new InvalidOperationException("Unrecognized pack hint index transition");
                        if (ordinal == 2) await _Compatibility.EnsureIndexAsync("vessel_pack_hints", "idx_vessel_pack_hints_vessel", "vessel_id, active", false, token).ConfigureAwait(false);
                    }
                    else
                        await _Compatibility.ExecuteAsync(statement, migration.Version, token).ConfigureAwait(false);
                    _Checkpoint?.Invoke(migration.Version, ordinal);
                    if (!completed) await RecordStatementAsync(migration.Version, ordinal, checksum, null, token).ConfigureAwait(false);
                }
                else if (!completed)
                {
                    // DML and its checkpoint are atomic. DDL uses catalog verification instead,
                    // because MySQL commits it independently of the caller's transaction.
                    using (MySqlTransaction transaction = await _Connection.BeginTransactionAsync(token).ConfigureAwait(false))
                    {
                        using (MySqlCommand command = _Connection.CreateCommand())
                        {
                            command.Transaction = transaction;
                            command.CommandText = statement;
                            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                        }
                        _Checkpoint?.Invoke(migration.Version, ordinal);
                        await RecordStatementAsync(migration.Version, ordinal, checksum, transaction, token).ConfigureAwait(false);
                        await transaction.CommitAsync(token).ConfigureAwait(false);
                    }
                }
            }
            using (MySqlCommand command = _Connection.CreateCommand())
            {
                command.CommandText = "INSERT INTO schema_migrations (version,description,applied_utc) VALUES (@version,@description,@applied);";
                command.Parameters.AddWithValue("@version", migration.Version);
                command.Parameters.AddWithValue("@description", migration.Description);
                command.Parameters.AddWithValue("@applied", DateTime.UtcNow);
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
            _Checkpoint?.Invoke(migration.Version, -2);
        }

        private async Task RecordStatementAsync(int version, int ordinal, string checksum, MySqlTransaction? transaction, CancellationToken token)
        {
            using (MySqlCommand command = _Connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "INSERT INTO schema_migration_statements (version,ordinal,checksum) VALUES (@version,@ordinal,@checksum);";
                command.Parameters.AddWithValue("@version", version);
                command.Parameters.AddWithValue("@ordinal", ordinal);
                command.Parameters.AddWithValue("@checksum", checksum);
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
        }

        private static string Hash(string statement) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(statement)));
    }
}
