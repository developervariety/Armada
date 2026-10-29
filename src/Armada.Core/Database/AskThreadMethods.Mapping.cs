namespace Armada.Core.Database
{
    using System;
    using System.Data.Common;
    using System.Globalization;
    using System.Text.Json;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    public sealed partial class AskThreadMethods
    {
        private void BindThread(DbCommand command, AskThread thread)
        {
            Add(command, "@id", thread.Id);
            Add(command, "@tenant_id", thread.TenantId);
            Add(command, "@user_id", thread.UserId);
            Add(command, "@title", thread.Title);
            Add(command, "@captain_id", thread.CaptainId);
            ProductionFactSql.AddBool(command, "@pinned", thread.Pinned, _Provider);
            ProductionFactSql.AddBool(command, "@archived", thread.Archived, _Provider);
            AddUtc(command, "@created_utc", "ask_threads", "created_utc", thread.CreatedUtc);
            AddUtc(command, "@last_activity_utc", "ask_threads", "last_activity_utc", thread.LastActivityUtc);
            Add(command, "@active_turn_id", thread.ActiveTurnId);
            Add(command, "@unread_count", thread.UnreadCount);
        }

        private static AskThread ReadThread(DbDataReader reader)
        {
            return new AskThread
            {
                Id = Text(reader, "id")!, TenantId = Text(reader, "tenant_id")!, UserId = Text(reader, "user_id")!,
                Title = Text(reader, "title")!, CaptainId = Text(reader, "captain_id"),
                Pinned = ProductionFactSql.ReadBool(reader["pinned"]), Archived = ProductionFactSql.ReadBool(reader["archived"]),
                CreatedUtc = ProductionFactSql.ReadUtc(reader["created_utc"]), LastActivityUtc = ProductionFactSql.ReadUtc(reader["last_activity_utc"]),
                ActiveTurnId = Text(reader, "active_turn_id"),
                UnreadCount = Convert.ToInt32(reader["unread_count"], CultureInfo.InvariantCulture)
            };
        }

        private static AskTurn ReadTurn(DbDataReader reader)
        {
            string stateText = Convert.ToString(reader["state"], CultureInfo.InvariantCulture)!;
            if (!Enum.TryParse(stateText, false, out AskTurnStateEnum state)) throw new InvalidOperationException("Stored Ask turn has unknown state: " + stateText);
            string? responseText = Text(reader, "response_json");
            return new AskTurn
            {
                ThreadId = Text(reader, "thread_id")!, RequestId = Text(reader, "request_id")!, Sequence = Convert.ToInt64(reader["sequence"], CultureInfo.InvariantCulture),
                CaptainId = Text(reader, "captain_id")!, Content = Text(reader, "content")!, ShowThinking = ProductionFactSql.ReadBool(reader["show_thinking"]),
                State = state, Response = String.IsNullOrWhiteSpace(responseText) ? null : JsonSerializer.Deserialize<CaptainChatResponse>(responseText),
                CreatedUtc = ProductionFactSql.ReadUtc(reader["created_utc"]), CompletedUtc = ProductionFactSql.ReadNullableUtc(reader["completed_utc"])
            };
        }

        private static void Add(DbCommand command, string name, object? value) => ProductionFactSql.Add(command, name, value);
        private static string? Text(DbDataReader reader, string name) => ProductionFactSql.ReadText(reader[name]);
        private static void ValidateOwner(string? tenantId, string? userId)
        {
            if (String.IsNullOrWhiteSpace(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            if (String.IsNullOrWhiteSpace(userId)) throw new ArgumentNullException(nameof(userId));
        }

        private void BindOwner(DbCommand command, string tenantId, string userId, bool includeArchived)
        {
            Add(command, "@tenant_id", tenantId);
            Add(command, "@user_id", userId);
            if (!includeArchived) ProductionFactSql.AddBool(command, "@archived", false, _Provider);
        }

        private string PageSql() => _Provider == DatabaseTypeEnum.SqlServer
            ? "OFFSET @offset ROWS FETCH NEXT @page_size ROWS ONLY"
            : "LIMIT @page_size OFFSET @offset";
    }
}
