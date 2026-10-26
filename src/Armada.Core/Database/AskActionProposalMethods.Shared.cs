namespace Armada.Core.Database
{
    using System;
    using System.Collections.Generic;
    using System.Data.Common;
    using System.Text.Json;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    public sealed partial class AskActionProposalMethods
    {
        private void BindProposal(DbCommand command, AskActionProposal proposal)
        {
            Add(command, "@id", proposal.Id); Add(command, "@tenant_id", proposal.TenantId); Add(command, "@user_id", proposal.UserId); Add(command, "@thread_id", proposal.ThreadId); Add(command, "@request_id", proposal.RequestId); Add(command, "@tool_name", proposal.ToolName); Add(command, "@arguments_json", proposal.ArgumentsJson); Add(command, "@fingerprint", proposal.Fingerprint); Add(command, "@state", AskProposalStateEnum.Pending.ToString()); AddUtc(command, "@created_utc", proposal.CreatedUtc); AddUtc(command, "@expires_utc", proposal.ExpiresUtc); Add(command, "@work_json", "[]");
        }

        private void AddUtc(DbCommand command, string name, DateTime? value)
            => StoredValueBinder.For(_Provider).AddTimestamp(command, name, "ask_action_proposals", name == "@now" ? "decided_utc" : name.Substring(1), value);

        private static void Add(DbCommand command, string name, object? value)
        {
            DbParameter parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }

        private static void AddOwner(DbCommand command, string tenantId, string userId, string threadId, string proposalId)
        {
            Add(command, "@tenant_id", tenantId); Add(command, "@user_id", userId); Add(command, "@thread_id", threadId); Add(command, "@id", proposalId);
        }

        private static void ValidateOwner(string tenantId, string userId)
        {
            if (String.IsNullOrWhiteSpace(tenantId) || String.IsNullOrWhiteSpace(userId)) throw new ArgumentException("Tenant and user identifiers are required.");
        }

        private bool IsUniqueViolation(DbException exception)
        {
            if (_Provider == DatabaseTypeEnum.Postgresql && exception is Npgsql.PostgresException postgres) return postgres.SqlState == "23505";
            if (_Provider == DatabaseTypeEnum.Mysql && exception is MySqlConnector.MySqlException mysql) return mysql.Number == 1062;
            if (_Provider == DatabaseTypeEnum.SqlServer && exception is Microsoft.Data.SqlClient.SqlException sqlServer) return sqlServer.Number == 2601 || sqlServer.Number == 2627;
            if (_Provider == DatabaseTypeEnum.Sqlite && exception is Microsoft.Data.Sqlite.SqliteException sqlite) return sqlite.SqliteErrorCode == 19;
            return false;
        }

        private static bool IsHex(string value)
        {
            foreach (char character in value) if (!Uri.IsHexDigit(character)) return false;
            return true;
        }

        private static List<AskWorkReference> BoundWorkReferences(List<AskWorkReference>? references)
        {
            List<AskWorkReference> result = new List<AskWorkReference>();
            if (references == null) return result;
            if (references.Count > 100) throw new ArgumentException("At most 100 work references may be stored.", nameof(references));
            foreach (AskWorkReference reference in references)
            {
                if (reference == null || !Enum.IsDefined(reference.Kind) || String.IsNullOrWhiteSpace(reference.Id) || reference.Id.Length > 128) throw new ArgumentException("A work reference has an invalid kind or identifier.", nameof(references));
                result.Add(new AskWorkReference { Kind = reference.Kind, Id = reference.Id });
            }
            return result;
        }

        private static string Bound(string? value, int limit)
        {
            string text = value ?? String.Empty;
            return text.Length <= limit ? text : text.Substring(0, limit);
        }
    }
}
