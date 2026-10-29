namespace Armada.Core.Database
{
    using System;
    using System.Collections.Generic;
    using System.Data.Common;
    using System.Globalization;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>Provider-neutral persistence for saved Ask threads and durable turns.</summary>
    public sealed partial class AskThreadMethods : IAskThreadMethods
    {
        private const string _ThreadColumns = "id, tenant_id, user_id, title, captain_id, pinned, archived, created_utc, last_activity_utc, active_turn_id, unread_count";
        private const string _TurnColumns = "thread_id, request_id, sequence, captain_id, content, show_thinking, state, response_json, created_utc, completed_utc";
        private const string _TurnSelectColumns = "t.thread_id, t.request_id, t.sequence, t.captain_id, t.content, t.show_thinking, t.state, t.response_json, t.created_utc, t.completed_utc";
        private readonly Func<DbConnection> _ConnectionFactory;
        private readonly DatabaseTypeEnum _Provider;

        /// <summary>Create provider-neutral Ask thread persistence.</summary>
        /// <param name="connectionFactory">Creates a closed provider connection.</param>
        /// <param name="provider">Database provider.</param>
        public AskThreadMethods(Func<DbConnection> connectionFactory, DatabaseTypeEnum provider)
        {
            _ConnectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
            _Provider = provider;
        }

        private void AddUtc(DbCommand command, string parameterName, string table, string column, DateTime? value)
        {
            StoredValueBinder.For(_Provider).AddTimestamp(command, parameterName, table, column, value);
        }

        /// <inheritdoc />
        public async Task<AskThread> CreateAsync(AskThread thread, CancellationToken token = default)
        {
            if (thread == null) throw new ArgumentNullException(nameof(thread));
            ValidateOwner(thread.TenantId, thread.UserId);
            using (DbConnection connection = _ConnectionFactory())
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (IDisposable? writeLease = await Sqlite.SqliteWriteGate.EnterAsync(connection, token).ConfigureAwait(false))
                using (DbCommand command = connection.CreateCommand())
                {
                    command.CommandText = "INSERT INTO ask_threads (" + _ThreadColumns + ") VALUES (@id, @tenant_id, @user_id, @title, @captain_id, @pinned, @archived, @created_utc, @last_activity_utc, @active_turn_id, @unread_count);";
                    BindThread(command, thread);
                    await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }
            return thread;
        }

        /// <inheritdoc />
        public async Task<AskThread?> ReadAsync(string tenantId, string userId, string threadId, CancellationToken token = default)
        {
            ValidateOwner(tenantId, userId);
            if (String.IsNullOrWhiteSpace(threadId)) return null;
            using (DbConnection connection = _ConnectionFactory())
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (DbCommand command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT " + _ThreadColumns + " FROM ask_threads WHERE tenant_id = @tenant_id AND user_id = @user_id AND id = @id;";
                    Add(command, "@tenant_id", tenantId);
                    Add(command, "@user_id", userId);
                    Add(command, "@id", threadId);
                    using (DbDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
                        return await reader.ReadAsync(token).ConfigureAwait(false) ? ReadThread(reader) : null;
                }
            }
        }

        /// <inheritdoc />
        public async Task<EnumerationResult<AskThread>> EnumerateAsync(string tenantId, string userId, int pageNumber, int pageSize, bool includeArchived, CancellationToken token = default)
        {
            ValidateOwner(tenantId, userId);
            int size = pageSize < 1 ? 50 : Math.Min(pageSize, 200);
            int page = pageNumber < 1 ? 1 : pageNumber;
            long offset = (long)(page - 1) * size;
            string archivedClause = includeArchived ? String.Empty : " AND archived = @archived";
            EnumerationResult<AskThread> result = new EnumerationResult<AskThread> { PageNumber = page, PageSize = size };
            using (DbConnection connection = _ConnectionFactory())
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (DbCommand count = connection.CreateCommand())
                {
                    count.CommandText = "SELECT COUNT(*) FROM ask_threads WHERE tenant_id = @tenant_id AND user_id = @user_id" + archivedClause + ";";
                    BindOwner(count, tenantId, userId, includeArchived);
                    result.TotalRecords = Convert.ToInt64(await count.ExecuteScalarAsync(token).ConfigureAwait(false), CultureInfo.InvariantCulture);
                }
                using (DbCommand query = connection.CreateCommand())
                {
                    query.CommandText = "SELECT " + _ThreadColumns + " FROM ask_threads WHERE tenant_id = @tenant_id AND user_id = @user_id" + archivedClause + " ORDER BY pinned DESC, last_activity_utc DESC, id ASC " + PageSql() + ";";
                    BindOwner(query, tenantId, userId, includeArchived);
                    Add(query, "@page_size", size);
                    Add(query, "@offset", offset);
                    using (DbDataReader reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false))
                        while (await reader.ReadAsync(token).ConfigureAwait(false)) result.Objects.Add(ReadThread(reader));
                }
            }
            result.TotalPages = (int)Math.Ceiling((double)result.TotalRecords / size);
            return result;
        }

    }
}
