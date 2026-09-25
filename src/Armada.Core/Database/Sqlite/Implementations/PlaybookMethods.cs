namespace Armada.Core.Database.Sqlite.Implementations
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Data.Sqlite;
    using SyslogLogging;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Settings;

    /// <summary>
    /// SQLite implementation of playbook persistence and associations.
    /// </summary>
    public class PlaybookMethods : IPlaybookMethods
    {
        private readonly SqliteDatabaseDriver _Driver;
        private readonly DatabaseSettings _Settings;
        private readonly LoggingModule _Logging;

        /// <summary>
        /// Instantiate.
        /// </summary>
        public PlaybookMethods(SqliteDatabaseDriver driver, DatabaseSettings settings, LoggingModule logging)
        {
            _Driver = driver ?? throw new ArgumentNullException(nameof(driver));
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
        }

        /// <inheritdoc />
        public async Task<Playbook> CreateAsync(Playbook playbook, CancellationToken token = default)
        {
            if (playbook == null) throw new ArgumentNullException(nameof(playbook));
            playbook.LastUpdateUtc = DateTime.UtcNow;

            using (SqliteConnection conn = new SqliteConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqliteCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"INSERT INTO playbooks
                        (id, tenant_id, user_id, file_name, description, content, active, created_utc, last_update_utc)
                        VALUES (@id, @tenant_id, @user_id, @file_name, @description, @content, @active, @created_utc, @last_update_utc);";
                    AddPlaybookParameters(cmd, playbook);
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }

            return playbook;
        }

        /// <inheritdoc />
        public async Task<Playbook?> ReadAsync(string id, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));

            using (SqliteConnection conn = new SqliteConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqliteCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT * FROM playbooks WHERE id = @id;";
                    StoredValueBinder.Value(cmd, "@id", id);
                    using (SqliteDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        if (await reader.ReadAsync(token).ConfigureAwait(false))
                            return PlaybookColumns.Read(reader, SqliteDatabaseDriver.StoredValues);
                    }
                }
            }

            return null;
        }

        /// <inheritdoc />
        public async Task<Playbook?> ReadAsync(string tenantId, string id, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            if (String.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));

            using (SqliteConnection conn = new SqliteConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqliteCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT * FROM playbooks WHERE tenant_id = @tenant_id AND id = @id;";
                    StoredValueBinder.Value(cmd, "@tenant_id", tenantId);
                    StoredValueBinder.Value(cmd, "@id", id);
                    using (SqliteDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        if (await reader.ReadAsync(token).ConfigureAwait(false))
                            return PlaybookColumns.Read(reader, SqliteDatabaseDriver.StoredValues);
                    }
                }
            }

            return null;
        }

        /// <inheritdoc />
        public async Task<Playbook?> ReadByFileNameAsync(string tenantId, string fileName, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            if (String.IsNullOrEmpty(fileName)) throw new ArgumentNullException(nameof(fileName));

            using (SqliteConnection conn = new SqliteConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqliteCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT * FROM playbooks WHERE tenant_id = @tenant_id AND file_name = @file_name;";
                    StoredValueBinder.Value(cmd, "@tenant_id", tenantId);
                    StoredValueBinder.Value(cmd, "@file_name", fileName);
                    using (SqliteDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        if (await reader.ReadAsync(token).ConfigureAwait(false))
                            return PlaybookColumns.Read(reader, SqliteDatabaseDriver.StoredValues);
                    }
                }
            }

            return null;
        }

        /// <inheritdoc />
        public async Task<Playbook> UpdateAsync(Playbook playbook, CancellationToken token = default)
        {
            if (playbook == null) throw new ArgumentNullException(nameof(playbook));
            playbook.LastUpdateUtc = DateTime.UtcNow;

            using (SqliteConnection conn = new SqliteConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqliteCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"UPDATE playbooks SET
                        tenant_id = @tenant_id,
                        user_id = @user_id,
                        file_name = @file_name,
                        description = @description,
                        content = @content,
                        active = @active,
                        last_update_utc = @last_update_utc
                        WHERE id = @id;";
                    AddPlaybookParameters(cmd, playbook);
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }

            return playbook;
        }

        /// <inheritdoc />
        public async Task DeleteAsync(string id, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));

            using (SqliteConnection conn = new SqliteConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqliteTransaction tx = conn.BeginTransaction())
                {
                    await DeleteVoyageSelectionsByPlaybookAsync(conn, tx, id, token).ConfigureAwait(false);
                    await DeleteMissionSnapshotsByPlaybookAsync(conn, tx, id, token).ConfigureAwait(false);
                    using (SqliteCommand cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = tx;
                        cmd.CommandText = "DELETE FROM playbooks WHERE id = @id;";
                        StoredValueBinder.Value(cmd, "@id", id);
                        await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    }

                    tx.Commit();
                }
            }
        }

        /// <inheritdoc />
        public async Task<List<Playbook>> EnumerateAsync(CancellationToken token = default)
        {
            List<Playbook> results = new List<Playbook>();

            using (SqliteConnection conn = new SqliteConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqliteCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT * FROM playbooks ORDER BY file_name;";
                    using (SqliteDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            results.Add(PlaybookColumns.Read(reader, SqliteDatabaseDriver.StoredValues));
                    }
                }
            }

            return results;
        }

        /// <inheritdoc />
        public async Task<EnumerationResult<Playbook>> EnumerateAsync(EnumerationQuery query, CancellationToken token = default)
        {
            return await EnumerateInternalAsync(null, query, token).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<List<Playbook>> EnumerateAsync(string tenantId, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            List<Playbook> results = new List<Playbook>();

            using (SqliteConnection conn = new SqliteConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqliteCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT * FROM playbooks WHERE tenant_id = @tenant_id ORDER BY file_name;";
                    StoredValueBinder.Value(cmd, "@tenant_id", tenantId);
                    using (SqliteDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            results.Add(PlaybookColumns.Read(reader, SqliteDatabaseDriver.StoredValues));
                    }
                }
            }

            return results;
        }

        /// <inheritdoc />
        public async Task<EnumerationResult<Playbook>> EnumerateAsync(string tenantId, EnumerationQuery query, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            return await EnumerateInternalAsync(tenantId, query, token).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<bool> ExistsAsync(string id, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));

            using (SqliteConnection conn = new SqliteConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqliteCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM playbooks WHERE id = @id;";
                    StoredValueBinder.Value(cmd, "@id", id);
                    long count = (long)(await cmd.ExecuteScalarAsync(token).ConfigureAwait(false))!;
                    return count > 0;
                }
            }
        }

        /// <inheritdoc />
        public async Task<bool> ExistsByFileNameAsync(string tenantId, string fileName, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            if (String.IsNullOrEmpty(fileName)) throw new ArgumentNullException(nameof(fileName));

            using (SqliteConnection conn = new SqliteConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqliteCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM playbooks WHERE tenant_id = @tenant_id AND file_name = @file_name;";
                    StoredValueBinder.Value(cmd, "@tenant_id", tenantId);
                    StoredValueBinder.Value(cmd, "@file_name", fileName);
                    long count = (long)(await cmd.ExecuteScalarAsync(token).ConfigureAwait(false))!;
                    return count > 0;
                }
            }
        }

        /// <inheritdoc />
        public async Task SetVoyageSelectionsAsync(string voyageId, List<SelectedPlaybook> selections, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(voyageId)) throw new ArgumentNullException(nameof(voyageId));
            selections ??= new List<SelectedPlaybook>();

            using (SqliteConnection conn = new SqliteConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqliteTransaction tx = conn.BeginTransaction())
                {
                    using (SqliteCommand deleteCmd = conn.CreateCommand())
                    {
                        deleteCmd.Transaction = tx;
                        deleteCmd.CommandText = "DELETE FROM voyage_playbooks WHERE voyage_id = @voyage_id;";
                        StoredValueBinder.Value(deleteCmd, "@voyage_id", voyageId);
                        await deleteCmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    }

                    for (int i = 0; i < selections.Count; i++)
                    {
                        SelectedPlaybook selection = selections[i];
                        using (SqliteCommand insertCmd = conn.CreateCommand())
                        {
                            insertCmd.Transaction = tx;
                            insertCmd.CommandText = @"INSERT INTO voyage_playbooks
                                (voyage_id, playbook_id, selection_order, delivery_mode)
                                VALUES (@voyage_id, @playbook_id, @selection_order, @delivery_mode);";
                            StoredValueBinder.Value(insertCmd, "@voyage_id", voyageId);
                            StoredValueBinder.Value(insertCmd, "@playbook_id", selection.PlaybookId);
                            StoredValueBinder.Value(insertCmd, "@selection_order", i);
                            StoredValueBinder.Value(insertCmd, "@delivery_mode", selection.DeliveryMode.ToString());
                            await insertCmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                        }
                    }

                    tx.Commit();
                }
            }
        }

        /// <inheritdoc />
        public async Task<List<SelectedPlaybook>> GetVoyageSelectionsAsync(string voyageId, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(voyageId)) throw new ArgumentNullException(nameof(voyageId));
            List<SelectedPlaybook> results = new List<SelectedPlaybook>();

            using (SqliteConnection conn = new SqliteConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqliteCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"SELECT playbook_id, delivery_mode
                        FROM voyage_playbooks
                        WHERE voyage_id = @voyage_id
                        ORDER BY selection_order;";
                    StoredValueBinder.Value(cmd, "@voyage_id", voyageId);
                    using (SqliteDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                        {
                            results.Add(VoyagePlaybookColumns.Read(reader, SqliteDatabaseDriver.StoredValues));
                        }
                    }
                }
            }

            return results;
        }

        /// <inheritdoc />
        public async Task SetMissionSnapshotsAsync(string missionId, List<MissionPlaybookSnapshot> snapshots, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(missionId)) throw new ArgumentNullException(nameof(missionId));
            snapshots ??= new List<MissionPlaybookSnapshot>();

            using (SqliteConnection conn = new SqliteConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqliteTransaction tx = conn.BeginTransaction())
                {
                    using (SqliteCommand deleteCmd = conn.CreateCommand())
                    {
                        deleteCmd.Transaction = tx;
                        deleteCmd.CommandText = "DELETE FROM mission_playbook_snapshots WHERE mission_id = @mission_id;";
                        StoredValueBinder.Value(deleteCmd, "@mission_id", missionId);
                        await deleteCmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    }

                    for (int i = 0; i < snapshots.Count; i++)
                    {
                        MissionPlaybookSnapshot snapshot = snapshots[i];
                        using (SqliteCommand insertCmd = conn.CreateCommand())
                        {
                            insertCmd.Transaction = tx;
                            insertCmd.CommandText = @"INSERT INTO mission_playbook_snapshots
                                (mission_id, playbook_id, selection_order, file_name, description, content, delivery_mode, resolved_path, worktree_relative_path, source_last_update_utc)
                                VALUES (@mission_id, @playbook_id, @selection_order, @file_name, @description, @content, @delivery_mode, @resolved_path, @worktree_relative_path, @source_last_update_utc);";
                            PlaybookColumns.WriteSnapshot(SqliteDatabaseDriver.StoredBinder.For(insertCmd, "mission_playbook_snapshots"), snapshot);
                            StoredValueBinder.Value(insertCmd, "@mission_id", missionId);
                            StoredValueBinder.Value(insertCmd, "@selection_order", i);
                            await insertCmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                        }
                    }

                    tx.Commit();
                }
            }
        }

        /// <inheritdoc />
        public async Task<List<MissionPlaybookSnapshot>> GetMissionSnapshotsAsync(string missionId, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(missionId)) throw new ArgumentNullException(nameof(missionId));
            List<MissionPlaybookSnapshot> results = new List<MissionPlaybookSnapshot>();

            using (SqliteConnection conn = new SqliteConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqliteCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"SELECT *
                        FROM mission_playbook_snapshots
                        WHERE mission_id = @mission_id
                        ORDER BY selection_order;";
                    StoredValueBinder.Value(cmd, "@mission_id", missionId);
                    using (SqliteDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            results.Add(PlaybookColumns.ReadSnapshot(reader, SqliteDatabaseDriver.StoredValues));
                    }
                }
            }

            return results;
        }

        private async Task<EnumerationResult<Playbook>> EnumerateInternalAsync(string? tenantId, EnumerationQuery query, CancellationToken token)
        {
            query ??= new EnumerationQuery();

            using (SqliteConnection conn = new SqliteConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);

                List<string> conditions = new List<string>();
                List<SqliteParameter> parameters = new List<SqliteParameter>();

                if (!String.IsNullOrEmpty(tenantId))
                {
                    conditions.Add("tenant_id = @tenant_id");
                    parameters.Add(StoredValueBinder.Parameter(new SqliteParameter(), "@tenant_id", tenantId));
                }
                if (query.CreatedAfter.HasValue)
                {
                    conditions.Add("created_utc > @created_after");
                    parameters.Add(SqliteDatabaseDriver.StoredBinder.Timestamp(new SqliteParameter(), "@created_after", "playbooks", "created_utc", query.CreatedAfter.Value));
                }
                if (query.CreatedBefore.HasValue)
                {
                    conditions.Add("created_utc < @created_before");
                    parameters.Add(SqliteDatabaseDriver.StoredBinder.Timestamp(new SqliteParameter(), "@created_before", "playbooks", "created_utc", query.CreatedBefore.Value));
                }

                string whereClause = conditions.Count > 0 ? " WHERE " + String.Join(" AND ", conditions) : "";
                string orderDirection = query.Order == EnumerationOrderEnum.CreatedAscending ? "ASC" : "DESC";

                long totalCount = 0;
                using (SqliteCommand countCmd = conn.CreateCommand())
                {
                    countCmd.CommandText = "SELECT COUNT(*) FROM playbooks" + whereClause + ";";
                    foreach (SqliteParameter p in parameters) countCmd.Parameters.Add(new SqliteParameter(p.ParameterName, p.Value));
                    totalCount = (long)(await countCmd.ExecuteScalarAsync(token).ConfigureAwait(false))!;
                }

                List<Playbook> results = new List<Playbook>();
                using (SqliteCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT * FROM playbooks" + whereClause +
                        " ORDER BY created_utc " + orderDirection +
                        " LIMIT " + query.PageSize + " OFFSET " + query.Offset + ";";
                    foreach (SqliteParameter p in parameters) cmd.Parameters.Add(new SqliteParameter(p.ParameterName, p.Value));
                    using (SqliteDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            results.Add(PlaybookColumns.Read(reader, SqliteDatabaseDriver.StoredValues));
                    }
                }

                return EnumerationResult<Playbook>.Create(query, results, totalCount);
            }
        }

        private static void AddPlaybookParameters(SqliteCommand cmd, Playbook playbook)
        {
            PlaybookColumns.Write(SqliteDatabaseDriver.StoredBinder.For(cmd, "playbooks"), playbook);
        }

        private static async Task DeleteVoyageSelectionsByPlaybookAsync(SqliteConnection conn, SqliteTransaction tx, string playbookId, CancellationToken token)
        {
            using (SqliteCommand cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM voyage_playbooks WHERE playbook_id = @playbook_id;";
                StoredValueBinder.Value(cmd, "@playbook_id", playbookId);
                await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
        }

        private static async Task DeleteMissionSnapshotsByPlaybookAsync(SqliteConnection conn, SqliteTransaction tx, string playbookId, CancellationToken token)
        {
            using (SqliteCommand cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM mission_playbook_snapshots WHERE playbook_id = @playbook_id;";
                StoredValueBinder.Value(cmd, "@playbook_id", playbookId);
                await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
        }
    }
}
