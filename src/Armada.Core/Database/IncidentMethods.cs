namespace Armada.Core.Database
{
    using System;
    using System.Collections.Generic;
    using System.Data.Common;
    using System.Linq;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;

    /// <summary>
    /// Incident projection persistence, one implementation for every provider. A snapshot event and the projection
    /// row it produces are written in one transaction, so a read of the projection never lags the events.
    /// </summary>
    internal sealed class IncidentMethods : IIncidentMethods
    {
        #region Internal-Members

        /// <summary>
        /// The incidents table.
        /// </summary>
        internal static readonly StoredTable<IncidentProjection> Table = new StoredTable<IncidentProjection>(
            "incidents",
            new[]
            {
                "id", "tenant_id", "user_id", "status", "severity", "title", "summary", "environment_id", "environment_name", "check_run_id",
                "deployment_id", "release_id", "vessel_id", "mission_id", "voyage_id", "impact", "root_cause", "recovery_notes", "postmortem",
                "detected_utc", "last_update_utc", "snapshot_id", "payload"
            },
            Array.Empty<string>(),
            IncidentColumns.Read,
            IncidentColumns.Write);

        /// <summary>
        /// Order of an incident page, newest update first; the identifier keeps rows that share an update time in one order.
        /// </summary>
        internal const string NewestFirst = "last_update_utc DESC, id DESC";

        /// <summary>
        /// Order of an incident page, oldest update first.
        /// </summary>
        internal const string OldestFirst = "last_update_utc ASC, id ASC";

        #endregion

        #region Private-Members

        private const int _DefaultPageSize = 50;
        private const string _EventInsertSql = "INSERT INTO events (id, tenant_id, user_id, event_type, entity_type, entity_id, captain_id, mission_id, vessel_id, voyage_id, message, payload, created_utc) "
            + "VALUES (@id, @tenant_id, @user_id, @event_type, @entity_type, @entity_id, @captain_id, @mission_id, @vessel_id, @voyage_id, @message, @payload, @created_utc);";

        // The projection keeps the newest snapshot by update time then event identifier.
        private const string _NewerSnapshotGuard = " AND (last_update_utc < @last_update_utc OR (last_update_utc = @last_update_utc AND snapshot_id <= @snapshot_id))";

        private static readonly string[] _SearchColumns = { "title", "summary", "impact", "root_cause", "recovery_notes", "postmortem", "environment_name" };

        private readonly StoredMethods<IncidentProjection> _Rows;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="dialect">Provider the statements run on.</param>
        internal IncidentMethods(StoredDialect dialect)
        {
            _Rows = new StoredMethods<IncidentProjection>(dialect, Table);
        }

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        public async Task WriteSnapshotAsync(ArmadaEvent snapshot, Incident incident, CancellationToken token = default)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            if (incident == null) throw new ArgumentNullException(nameof(incident));

            IncidentProjection projection = IncidentProjection.From(snapshot, incident);
            using (DbConnection connection = await _Rows.Dialect.OpenAsync(token).ConfigureAwait(false))
            using (IDisposable? writeLease = await Sqlite.SqliteWriteGate.EnterAsync(connection, token).ConfigureAwait(false))
            using (DbTransaction transaction = await connection.BeginTransactionAsync(token).ConfigureAwait(false))
            {
                using (DbCommand command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = _EventInsertSql;
                    EventColumns.Write(_Rows.Dialect.Binder.For(command, "events"), snapshot);
                    await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }

                // MySQL reports changed rows, so a snapshot identical to the stored one reads as no row; the
                // existence check tells that apart from an incident with no row yet.
                int updated;
                using (DbCommand command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = Table.UpdateSql.TrimEnd(';') + _NewerSnapshotGuard + ";";
                    IncidentColumns.Write(_Rows.Dialect.Binder.For(command, Table.Name), projection);
                    updated = await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }

                if (updated == 0 && !await ExistsAsync(connection, transaction, incident.Id, token).ConfigureAwait(false))
                    await _Rows.InsertAsync(projection, connection, transaction, token).ConfigureAwait(false);

                await transaction.CommitAsync(token).ConfigureAwait(false);
            }
        }

        /// <inheritdoc />
        public async Task<Incident?> ReadAsync(IncidentReadScope scope, string id, CancellationToken token = default)
        {
            if (scope == null) throw new ArgumentNullException(nameof(scope));
            if (String.IsNullOrWhiteSpace(id)) throw new ArgumentNullException(nameof(id));

            StoredFilter filter = Scoped(scope).Key("id", id);
            IncidentProjection? projection = await _Rows.FirstAsync(filter, token).ConfigureAwait(false);
            return projection?.Incident;
        }

        /// <inheritdoc />
        public async Task<EnumerationResult<Incident>> EnumerateAsync(IncidentReadScope scope, IncidentQuery query, CancellationToken token = default)
        {
            if (scope == null) throw new ArgumentNullException(nameof(scope));
            if (query == null) throw new ArgumentNullException(nameof(query));

            StoredFilter filter = Scoped(scope);
            if (query.FromUtc.HasValue) filter.Time("detected_utc", ">=", "@from_utc", query.FromUtc.Value.ToUniversalTime());
            if (query.ToUtc.HasValue) filter.Time("detected_utc", "<=", "@to_utc", query.ToUtc.Value.ToUniversalTime());
            IgnoreCase(filter, "vessel_id", query.VesselId);
            IgnoreCase(filter, "environment_id", query.EnvironmentId);
            IgnoreCase(filter, "check_run_id", query.CheckRunId);
            IgnoreCase(filter, "deployment_id", query.DeploymentId);
            IgnoreCase(filter, "release_id", query.ReleaseId);
            IgnoreCase(filter, "mission_id", query.MissionId);
            IgnoreCase(filter, "voyage_id", query.VoyageId);
            filter.Name("status", query.Status);
            filter.Name("severity", query.Severity);
            Search(filter, query.Search);
            if (query.ExcludeTerminal)
                filter.Condition("status NOT IN ('" + IncidentStatusEnum.Closed + "', '" + IncidentStatusEnum.RolledBack + "')", null);

            if (query.OldestFirst && query.AfterLastUpdateUtc.HasValue)
            {
                DateTime afterUtc = query.AfterLastUpdateUtc.Value;
                string afterId = query.AfterId ?? String.Empty;
                filter.Condition("(last_update_utc > @after_utc OR (last_update_utc = @after_utc AND id > @after_id))",
                    p => p.Utc("@after_utc", "last_update_utc", afterUtc).Text("@after_id", "id", afterId));
            }

            int pageSize = query.PageSize < 1 ? _DefaultPageSize : Math.Min(query.PageSize, EnumerationResult<Incident>.DefaultMaxPageSize);
            int pageNumber = query.PageNumber < 1 ? 1 : query.PageNumber;
            EnumerationResult<IncidentProjection> page = await _Rows.PageAsync(filter, query.OldestFirst ? OldestFirst : NewestFirst, pageNumber, pageSize, token).ConfigureAwait(false);

            EnumerationResult<Incident> result = new EnumerationResult<Incident>
            {
                PageNumber = page.PageNumber,
                PageSize = page.PageSize,
                TotalRecords = page.TotalRecords,
                TotalPages = page.TotalPages,
                Objects = page.Objects.Select(item => item.Incident).ToList()
            };
            result.ReportPageSizeLimit(query.PageSize, EnumerationResult<Incident>.DefaultMaxPageSize);
            return result;
        }

        /// <inheritdoc />
        public async Task<IncidentCountSummary> CountAsync(IncidentReadScope scope, CancellationToken token = default)
        {
            if (scope == null) throw new ArgumentNullException(nameof(scope));

            IncidentCountSummary summary = new IncidentCountSummary();
            foreach (KeyValuePair<string, long> group in await CountGroupsAsync(Scoped(scope), "status", token).ConfigureAwait(false))
            {
                if (Enum.TryParse(group.Key, false, out IncidentStatusEnum status)) summary.ByStatus[status] = (int)group.Value;
            }

            foreach (KeyValuePair<string, long> group in await CountGroupsAsync(Scoped(scope).Name("status", (IncidentStatusEnum?)IncidentStatusEnum.Open), "severity", token).ConfigureAwait(false))
            {
                if (Enum.TryParse(group.Key, false, out IncidentSeverityEnum severity)) summary.OpenBySeverity[severity] = (int)group.Value;
            }

            return summary;
        }

        /// <inheritdoc />
        public async Task<bool> DeleteAsync(IncidentReadScope scope, string id, CancellationToken token = default)
        {
            if (scope == null) throw new ArgumentNullException(nameof(scope));
            if (String.IsNullOrWhiteSpace(id)) throw new ArgumentNullException(nameof(id));

            StoredFilter visible = Scoped(scope).Key("id", id);
            using (DbConnection connection = await _Rows.Dialect.OpenAsync(token).ConfigureAwait(false))
            using (IDisposable? writeLease = await Sqlite.SqliteWriteGate.EnterAsync(connection, token).ConfigureAwait(false))
            using (DbTransaction transaction = await connection.BeginTransactionAsync(token).ConfigureAwait(false))
            {
                using (DbCommand command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = _Rows.Dialect.SelectAny(Table.Name, visible.Where);
                    visible.Bind(command, _Rows.Dialect.Binder);
                    object? found = await command.ExecuteScalarAsync(token).ConfigureAwait(false);
                    if (found == null || found == DBNull.Value) return false;
                }

                using (DbCommand command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = "DELETE FROM events WHERE event_type = @event_type AND entity_type = @entity_type AND entity_id = @entity_id;";
                    StoredValueBinder.Value(command, "@event_type", IncidentService.SnapshotEventType);
                    StoredValueBinder.Value(command, "@entity_type", IncidentService.IncidentEntityType);
                    StoredValueBinder.Value(command, "@entity_id", id);
                    await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }

                using (DbCommand command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = "DELETE FROM " + Table.Name + " WHERE id = @id;";
                    StoredValueBinder.Value(command, "@id", id);
                    await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }

                await transaction.CommitAsync(token).ConfigureAwait(false);
                return true;
            }
        }

        /// <inheritdoc />
        public async Task<IncidentBackfillResult> BackfillFromEventsAsync(CancellationToken token = default)
        {
            IncidentBackfillResult result = new IncidentBackfillResult();
            List<ArmadaEvent> newest = new List<ArmadaEvent>();
            using (DbConnection connection = await _Rows.Dialect.OpenAsync(token).ConfigureAwait(false))
            {
                // The two identifier sets are compared here, not in SQL, because the events and incidents columns
                // need not share a collation.
                HashSet<string> projected = new HashSet<string>(StringComparer.Ordinal);
                using (DbCommand command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT id FROM " + Table.Name + ";";
                    using (DbDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
                        while (await reader.ReadAsync(token).ConfigureAwait(false)) projected.Add(Convert.ToString(reader.GetValue(0)) ?? String.Empty);
                }

                List<string> missing = new List<string>();
                using (DbCommand command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT DISTINCT entity_id FROM events WHERE event_type = @event_type AND entity_type = @entity_type AND entity_id IS NOT NULL;";
                    StoredValueBinder.Value(command, "@event_type", IncidentService.SnapshotEventType);
                    StoredValueBinder.Value(command, "@entity_type", IncidentService.IncidentEntityType);
                    using (DbDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                        {
                            string entityId = Convert.ToString(reader.GetValue(0)) ?? String.Empty;
                            if (!projected.Contains(entityId)) missing.Add(entityId);
                        }
                    }
                }

                foreach (string entityId in missing)
                {
                    using (DbCommand command = connection.CreateCommand())
                    {
                        // Newest snapshot first, so the first row is the incident's current record.
                        command.CommandText = "SELECT * FROM events WHERE event_type = @event_type AND entity_type = @entity_type AND entity_id = @entity_id ORDER BY created_utc DESC, id DESC;";
                        StoredValueBinder.Value(command, "@event_type", IncidentService.SnapshotEventType);
                        StoredValueBinder.Value(command, "@entity_type", IncidentService.IncidentEntityType);
                        StoredValueBinder.Value(command, "@entity_id", entityId);
                        using (DbDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
                        {
                            if (await reader.ReadAsync(token).ConfigureAwait(false)) newest.Add(EventColumns.Read(reader, _Rows.Dialect.Values));
                        }
                    }
                }
            }

            List<IncidentProjection> projections = new List<IncidentProjection>();
            foreach (ArmadaEvent snapshot in newest)
            {
                Incident? incident = null;
                try
                {
                    incident = IncidentSnapshotCodec.Deserialize(snapshot.Payload, snapshot.TenantId, snapshot.UserId, snapshot.CreatedUtc);
                }
                catch (JsonException)
                {
                }

                if (incident == null || !String.Equals(incident.Id, snapshot.EntityId, StringComparison.Ordinal)) result.Unreadable.Add(snapshot.EntityId ?? String.Empty);
                else projections.Add(IncidentProjection.From(snapshot, incident));
            }

            if (projections.Count == 0) return result;
            using (DbConnection connection = await _Rows.Dialect.OpenAsync(token).ConfigureAwait(false))
            using (IDisposable? writeLease = await Sqlite.SqliteWriteGate.EnterAsync(connection, token).ConfigureAwait(false))
            using (DbTransaction transaction = await connection.BeginTransactionAsync(token).ConfigureAwait(false))
            {
                foreach (IncidentProjection projection in projections)
                    await _Rows.InsertAsync(projection, connection, transaction, token).ConfigureAwait(false);
                await transaction.CommitAsync(token).ConfigureAwait(false);
            }

            result.Projected = projections.Count;
            return result;
        }

        #endregion

        #region Private-Methods

        private StoredFilter Scoped(IncidentReadScope scope)
        {
            StoredFilter filter = Table.Filter();
            if (scope.MatchesNothing) return filter.Condition("1 = 0", null);
            if (scope.TenantId != null) filter.Key("tenant_id", scope.TenantId);
            if (scope.UserId != null) filter.Key("user_id", scope.UserId);
            return filter;
        }

        private static void IgnoreCase(StoredFilter filter, string column, string? value)
        {
            if (String.IsNullOrWhiteSpace(value)) return;
            string lowered = value.ToLowerInvariant();
            string parameter = "@" + column;
            filter.Condition("LOWER(" + column + ") = " + parameter, p => p.Text(parameter, column, lowered));
        }

        private static void Search(StoredFilter filter, string? search)
        {
            if (String.IsNullOrWhiteSpace(search)) return;
            string pattern = "%" + EscapeLike(search.Trim().ToLowerInvariant()) + "%";
            filter.Condition("(" + String.Join(" OR ", _SearchColumns.Select(column => "LOWER(" + column + ") LIKE @search ESCAPE '!'")) + ")",
                p => p.Text("@search", "title", pattern));
        }

        private static string EscapeLike(string value)
        {
            return value.Replace("!", "!!", StringComparison.Ordinal).Replace("%", "!%", StringComparison.Ordinal)
                .Replace("_", "!_", StringComparison.Ordinal).Replace("[", "![", StringComparison.Ordinal);
        }

        private async Task<bool> ExistsAsync(DbConnection connection, DbTransaction transaction, string id, CancellationToken token)
        {
            using (DbCommand command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = _Rows.Dialect.SelectAny(Table.Name, " WHERE id = @id");
                StoredValueBinder.Value(command, "@id", id);
                object? found = await command.ExecuteScalarAsync(token).ConfigureAwait(false);
                return found != null && found != DBNull.Value;
            }
        }

        private async Task<List<KeyValuePair<string, long>>> CountGroupsAsync(StoredFilter filter, string column, CancellationToken token)
        {
            List<KeyValuePair<string, long>> groups = new List<KeyValuePair<string, long>>();
            using (DbConnection connection = await _Rows.Dialect.OpenAsync(token).ConfigureAwait(false))
            using (DbCommand command = connection.CreateCommand())
            {
                command.CommandText = "SELECT " + column + ", COUNT(*) FROM " + Table.Name + filter.Where + " GROUP BY " + column + ";";
                filter.Bind(command, _Rows.Dialect.Binder);
                using (DbDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(token).ConfigureAwait(false))
                        groups.Add(new KeyValuePair<string, long>(Convert.ToString(reader.GetValue(0)) ?? String.Empty, Convert.ToInt64(reader.GetValue(1))));
                }
            }
            return groups;
        }

        #endregion
    }
}
