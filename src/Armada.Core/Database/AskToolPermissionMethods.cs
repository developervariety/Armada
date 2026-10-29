namespace Armada.Core.Database
{
    using System;
    using System.Collections.Generic;
    using System.Data.Common;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>Provider-neutral storage for Ask native tool permission requests and remembered rules.</summary>
    public sealed class AskToolPermissionMethods : IAskToolPermissionMethods
    {
        /// <summary>Longest tool input kept with a request, in characters.</summary>
        public const int MaxInputChars = 8000;

        private const string _RequestColumns = "id, tenant_id, user_id, thread_id, request_id, tool_name, input_json, tool_use_id, state, decision_source, rule_id, created_utc, expires_utc, decided_utc";
        private const string _RuleColumns = "id, tenant_id, user_id, thread_id, tool_name, action, created_utc";
        private readonly Func<DbConnection> _ConnectionFactory;
        private readonly DatabaseTypeEnum _Provider;

        /// <summary>Create the storage.</summary>
        /// <param name="connectionFactory">Creates a closed provider connection.</param>
        /// <param name="provider">Database provider.</param>
        public AskToolPermissionMethods(Func<DbConnection> connectionFactory, DatabaseTypeEnum provider)
        {
            _ConnectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
            _Provider = provider;
        }

        /// <inheritdoc />
        public async Task<AskToolPermissionRequest> CreateRequestAsync(AskToolPermissionRequest request, CancellationToken token = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            ValidateOwner(request.TenantId, request.UserId);
            if (String.IsNullOrWhiteSpace(request.ThreadId) || String.IsNullOrWhiteSpace(request.RequestId) || String.IsNullOrWhiteSpace(request.ToolName))
                throw new ArgumentException("Thread, turn and tool names are required.", nameof(request));
            if (request.ToolName.Length > 128) throw new ArgumentException("Tool name exceeds 128 characters.", nameof(request));
            if (request.InputJson == null) request.InputJson = "{}";
            if (request.InputJson.Length > MaxInputChars) request.InputJson = request.InputJson.Substring(0, MaxInputChars);
            if (request.ToolUseId != null && request.ToolUseId.Length > 256) request.ToolUseId = request.ToolUseId.Substring(0, 256);
            using (DbConnection connection = _ConnectionFactory())
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (IDisposable? writeLease = await Sqlite.SqliteWriteGate.EnterAsync(connection, token).ConfigureAwait(false))
                using (DbCommand command = connection.CreateCommand())
                {
                    command.CommandText = "INSERT INTO ask_tool_permission_requests (" + _RequestColumns + ") VALUES (@id, @tenant_id, @user_id, @thread_id, @request_id, @tool_name, @input_json, @tool_use_id, @state, @decision_source, @rule_id, @created_utc, @expires_utc, @decided_utc);";
                    Add(command, "@id", request.Id);
                    Add(command, "@tenant_id", request.TenantId);
                    Add(command, "@user_id", request.UserId);
                    Add(command, "@thread_id", request.ThreadId);
                    Add(command, "@request_id", request.RequestId);
                    Add(command, "@tool_name", request.ToolName);
                    Add(command, "@input_json", request.InputJson);
                    Add(command, "@tool_use_id", request.ToolUseId);
                    Add(command, "@state", request.State.ToString());
                    Add(command, "@decision_source", request.DecisionSource?.ToString());
                    Add(command, "@rule_id", request.RuleId);
                    ProductionFactSql.AddUtc(command, "@created_utc", request.CreatedUtc, _Provider);
                    ProductionFactSql.AddUtc(command, "@expires_utc", request.ExpiresUtc, _Provider);
                    ProductionFactSql.AddUtc(command, "@decided_utc", request.DecidedUtc, _Provider);
                    await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }
            return request;
        }

        /// <inheritdoc />
        public async Task<AskToolPermissionRequest?> ReadRequestAsync(string tenantId, string userId, string threadId, string requestId, CancellationToken token = default)
        {
            ValidateOwner(tenantId, userId);
            using (DbConnection connection = _ConnectionFactory())
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (DbCommand command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT " + _RequestColumns + " FROM ask_tool_permission_requests WHERE tenant_id = @tenant_id AND user_id = @user_id AND thread_id = @thread_id AND id = @id;";
                    BindOwner(command, tenantId, userId, threadId);
                    Add(command, "@id", requestId);
                    using (DbDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
                        return await reader.ReadAsync(token).ConfigureAwait(false) ? ReadRequest(reader) : null;
                }
            }
        }

        /// <inheritdoc />
        public async Task<List<AskToolPermissionRequest>> EnumerateRequestsAsync(string tenantId, string userId, string threadId, int limit = 100, CancellationToken token = default)
        {
            ValidateOwner(tenantId, userId);
            int bounded = Math.Max(1, Math.Min(500, limit));
            List<AskToolPermissionRequest> result = new List<AskToolPermissionRequest>();
            using (DbConnection connection = _ConnectionFactory())
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (DbCommand command = connection.CreateCommand())
                {
                    string order = " ORDER BY created_utc DESC, id DESC";
                    command.CommandText = _Provider == DatabaseTypeEnum.SqlServer
                        ? "SELECT TOP (@limit) " + _RequestColumns + " FROM ask_tool_permission_requests WHERE tenant_id = @tenant_id AND user_id = @user_id AND thread_id = @thread_id" + order + ";"
                        : "SELECT " + _RequestColumns + " FROM ask_tool_permission_requests WHERE tenant_id = @tenant_id AND user_id = @user_id AND thread_id = @thread_id" + order + " LIMIT @limit;";
                    BindOwner(command, tenantId, userId, threadId);
                    Add(command, "@limit", bounded);
                    using (DbDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
                        while (await reader.ReadAsync(token).ConfigureAwait(false)) result.Add(ReadRequest(reader));
                }
            }
            DateTime now = DateTime.UtcNow;
            foreach (AskToolPermissionRequest request in result)
                if (request.State == AskToolPermissionStateEnum.Pending && request.ExpiresUtc <= now) request.State = AskToolPermissionStateEnum.Expired;
            return result;
        }

        /// <inheritdoc />
        public async Task<bool> TryDecideAsync(string tenantId, string userId, string threadId, string requestId, bool allow, AskToolPermissionSourceEnum source, DateTime nowUtc, CancellationToken token = default)
        {
            ValidateOwner(tenantId, userId);
            using (DbConnection connection = _ConnectionFactory())
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (IDisposable? writeLease = await Sqlite.SqliteWriteGate.EnterAsync(connection, token).ConfigureAwait(false))
                using (DbCommand command = connection.CreateCommand())
                {
                    command.CommandText = "UPDATE ask_tool_permission_requests SET state = @to, decision_source = @source, decided_utc = @now WHERE tenant_id = @tenant_id AND user_id = @user_id AND thread_id = @thread_id AND id = @id AND state = @pending AND expires_utc > @now;";
                    BindOwner(command, tenantId, userId, threadId);
                    Add(command, "@id", requestId);
                    Add(command, "@to", (allow ? AskToolPermissionStateEnum.Allowed : AskToolPermissionStateEnum.Denied).ToString());
                    Add(command, "@source", source.ToString());
                    Add(command, "@pending", AskToolPermissionStateEnum.Pending.ToString());
                    ProductionFactSql.AddUtc(command, "@now", nowUtc, _Provider);
                    return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) == 1;
                }
            }
        }

        /// <inheritdoc />
        public async Task<bool> TryExpireAsync(string tenantId, string userId, string threadId, string requestId, DateTime nowUtc, CancellationToken token = default)
        {
            ValidateOwner(tenantId, userId);
            using (DbConnection connection = _ConnectionFactory())
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (IDisposable? writeLease = await Sqlite.SqliteWriteGate.EnterAsync(connection, token).ConfigureAwait(false))
                using (DbCommand command = connection.CreateCommand())
                {
                    command.CommandText = "UPDATE ask_tool_permission_requests SET state = @to, decision_source = @source, decided_utc = @now WHERE tenant_id = @tenant_id AND user_id = @user_id AND thread_id = @thread_id AND id = @id AND state = @pending;";
                    BindOwner(command, tenantId, userId, threadId);
                    Add(command, "@id", requestId);
                    Add(command, "@to", AskToolPermissionStateEnum.Expired.ToString());
                    Add(command, "@source", AskToolPermissionSourceEnum.Expiry.ToString());
                    Add(command, "@pending", AskToolPermissionStateEnum.Pending.ToString());
                    ProductionFactSql.AddUtc(command, "@now", nowUtc, _Provider);
                    return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) == 1;
                }
            }
        }

        /// <inheritdoc />
        public async Task<AskToolPermissionRule> UpsertRuleAsync(AskToolPermissionRule rule, CancellationToken token = default)
        {
            if (rule == null) throw new ArgumentNullException(nameof(rule));
            ValidateOwner(rule.TenantId, rule.UserId);
            if (String.IsNullOrWhiteSpace(rule.ThreadId) || String.IsNullOrWhiteSpace(rule.ToolName) || rule.ToolName.Length > 128)
                throw new ArgumentException("A thread and a tool name of 1 to 128 characters are required.", nameof(rule));
            using (DbConnection connection = _ConnectionFactory())
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (IDisposable? writeLease = await Sqlite.SqliteWriteGate.EnterAsync(connection, token).ConfigureAwait(false))
                using (DbTransaction transaction = await connection.BeginTransactionAsync(token).ConfigureAwait(false))
                {
                    using (DbCommand delete = connection.CreateCommand())
                    {
                        delete.Transaction = transaction;
                        delete.CommandText = "DELETE FROM ask_tool_permission_rules WHERE tenant_id = @tenant_id AND user_id = @user_id AND thread_id = @thread_id AND tool_name = @tool_name;";
                        BindOwner(delete, rule.TenantId, rule.UserId, rule.ThreadId);
                        Add(delete, "@tool_name", rule.ToolName);
                        await delete.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    }
                    using (DbCommand insert = connection.CreateCommand())
                    {
                        insert.Transaction = transaction;
                        insert.CommandText = "INSERT INTO ask_tool_permission_rules (" + _RuleColumns + ") VALUES (@id, @tenant_id, @user_id, @thread_id, @tool_name, @action, @created_utc);";
                        Add(insert, "@id", rule.Id);
                        BindOwner(insert, rule.TenantId, rule.UserId, rule.ThreadId);
                        Add(insert, "@tool_name", rule.ToolName);
                        Add(insert, "@action", rule.Action.ToString());
                        ProductionFactSql.AddUtc(insert, "@created_utc", rule.CreatedUtc, _Provider);
                        await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    }
                    await transaction.CommitAsync(token).ConfigureAwait(false);
                }
            }
            return rule;
        }

        /// <inheritdoc />
        public async Task<AskToolPermissionRule?> ReadRuleAsync(string tenantId, string userId, string threadId, string toolName, CancellationToken token = default)
        {
            ValidateOwner(tenantId, userId);
            using (DbConnection connection = _ConnectionFactory())
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (DbCommand command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT " + _RuleColumns + " FROM ask_tool_permission_rules WHERE tenant_id = @tenant_id AND user_id = @user_id AND thread_id = @thread_id AND tool_name = @tool_name;";
                    BindOwner(command, tenantId, userId, threadId);
                    Add(command, "@tool_name", toolName);
                    using (DbDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
                        return await reader.ReadAsync(token).ConfigureAwait(false) ? ReadRule(reader) : null;
                }
            }
        }

        /// <inheritdoc />
        public async Task<List<AskToolPermissionRule>> EnumerateRulesAsync(string tenantId, string userId, string threadId, CancellationToken token = default)
        {
            ValidateOwner(tenantId, userId);
            List<AskToolPermissionRule> result = new List<AskToolPermissionRule>();
            using (DbConnection connection = _ConnectionFactory())
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (DbCommand command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT " + _RuleColumns + " FROM ask_tool_permission_rules WHERE tenant_id = @tenant_id AND user_id = @user_id AND thread_id = @thread_id ORDER BY created_utc ASC, id ASC;";
                    BindOwner(command, tenantId, userId, threadId);
                    using (DbDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
                        while (await reader.ReadAsync(token).ConfigureAwait(false)) result.Add(ReadRule(reader));
                }
            }
            return result;
        }

        /// <inheritdoc />
        public async Task<bool> DeleteRuleAsync(string tenantId, string userId, string threadId, string ruleId, CancellationToken token = default)
        {
            ValidateOwner(tenantId, userId);
            using (DbConnection connection = _ConnectionFactory())
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (IDisposable? writeLease = await Sqlite.SqliteWriteGate.EnterAsync(connection, token).ConfigureAwait(false))
                using (DbCommand command = connection.CreateCommand())
                {
                    command.CommandText = "DELETE FROM ask_tool_permission_rules WHERE tenant_id = @tenant_id AND user_id = @user_id AND thread_id = @thread_id AND id = @id;";
                    BindOwner(command, tenantId, userId, threadId);
                    Add(command, "@id", ruleId);
                    return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) == 1;
                }
            }
        }

        private static AskToolPermissionRequest ReadRequest(DbDataReader reader)
        {
            string stateText = ProductionFactSql.ReadText(reader["state"])!;
            if (!Enum.TryParse(stateText, false, out AskToolPermissionStateEnum state)) throw new InvalidOperationException("Stored Ask tool permission request has unknown state: " + stateText);
            string? sourceText = ProductionFactSql.ReadText(reader["decision_source"]);
            AskToolPermissionSourceEnum? source = null;
            if (!String.IsNullOrEmpty(sourceText) && Enum.TryParse(sourceText, false, out AskToolPermissionSourceEnum parsedSource)) source = parsedSource;
            return new AskToolPermissionRequest
            {
                Id = ProductionFactSql.ReadText(reader["id"])!, TenantId = ProductionFactSql.ReadText(reader["tenant_id"])!, UserId = ProductionFactSql.ReadText(reader["user_id"])!,
                ThreadId = ProductionFactSql.ReadText(reader["thread_id"])!, RequestId = ProductionFactSql.ReadText(reader["request_id"])!,
                ToolName = ProductionFactSql.ReadText(reader["tool_name"])!, InputJson = ProductionFactSql.ReadText(reader["input_json"])!,
                ToolUseId = ProductionFactSql.ReadText(reader["tool_use_id"]), State = state, DecisionSource = source,
                RuleId = ProductionFactSql.ReadText(reader["rule_id"]),
                CreatedUtc = ProductionFactSql.ReadUtc(reader["created_utc"]), ExpiresUtc = ProductionFactSql.ReadUtc(reader["expires_utc"]),
                DecidedUtc = ProductionFactSql.ReadNullableUtc(reader["decided_utc"])
            };
        }

        private static AskToolPermissionRule ReadRule(DbDataReader reader)
        {
            string actionText = ProductionFactSql.ReadText(reader["action"])!;
            if (!Enum.TryParse(actionText, false, out AskToolPermissionRuleActionEnum action)) throw new InvalidOperationException("Stored Ask tool permission rule has unknown action: " + actionText);
            return new AskToolPermissionRule
            {
                Id = ProductionFactSql.ReadText(reader["id"])!, TenantId = ProductionFactSql.ReadText(reader["tenant_id"])!, UserId = ProductionFactSql.ReadText(reader["user_id"])!,
                ThreadId = ProductionFactSql.ReadText(reader["thread_id"])!, ToolName = ProductionFactSql.ReadText(reader["tool_name"])!,
                Action = action, CreatedUtc = ProductionFactSql.ReadUtc(reader["created_utc"])
            };
        }

        private static void BindOwner(DbCommand command, string tenantId, string userId, string threadId)
        {
            Add(command, "@tenant_id", tenantId);
            Add(command, "@user_id", userId);
            Add(command, "@thread_id", threadId);
        }

        private static void Add(DbCommand command, string name, object? value) => ProductionFactSql.Add(command, name, value);

        private static void ValidateOwner(string? tenantId, string? userId)
        {
            if (String.IsNullOrWhiteSpace(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            if (String.IsNullOrWhiteSpace(userId)) throw new ArgumentNullException(nameof(userId));
        }
    }
}
