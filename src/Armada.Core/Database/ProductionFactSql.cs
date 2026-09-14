namespace Armada.Core.Database
{
    using System;
    using System.Data;
    using System.Data.Common;
    using System.Globalization;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// Provider-neutral SQL helpers for the append-only production fact stores. Each store has one
    /// implementation for every provider; only the migration DDL differs per provider.
    /// </summary>
    internal static class ProductionFactSql
    {
        internal static void Add(DbCommand command, string name, object? value)
        {
            DbParameter parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value ?? DBNull.Value;
            if (value == null) parameter.DbType = DbType.String;
            command.Parameters.Add(parameter);
        }

        internal static void AddUtc(DbCommand command, string name, DateTime? value, DatabaseTypeEnum provider)
        {
            DbParameter parameter = command.CreateParameter();
            parameter.ParameterName = name;
            if (!value.HasValue)
            {
                parameter.DbType = provider == DatabaseTypeEnum.Sqlite ? DbType.String : DbType.DateTime2;
                parameter.Value = DBNull.Value;
                if (provider == DatabaseTypeEnum.Postgresql) parameter.DbType = DbType.DateTimeOffset;
            }
            else
            {
                DateTime utc = value.Value.Kind == DateTimeKind.Utc ? value.Value : DateTime.SpecifyKind(value.Value.ToUniversalTime(), DateTimeKind.Utc);
                if (provider == DatabaseTypeEnum.Sqlite) parameter.Value = utc.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);
                else if (provider == DatabaseTypeEnum.SqlServer) { parameter.DbType = DbType.DateTime2; parameter.Value = utc; }
                else parameter.Value = utc;
            }
            command.Parameters.Add(parameter);
        }

        internal static void AddBool(DbCommand command, string name, bool value, DatabaseTypeEnum provider)
        {
            Add(command, name, provider == DatabaseTypeEnum.Sqlite ? (object)(value ? 1 : 0) : value);
        }

        internal static DateTime ReadUtc(object value)
        {
            if (value is DateTime dateTime) return DateTime.SpecifyKind(dateTime, DateTimeKind.Utc);
            if (value is DateTimeOffset offset) return offset.UtcDateTime;
            return DateTime.SpecifyKind(DateTime.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!,
                CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal), DateTimeKind.Utc);
        }

        internal static DateTime? ReadNullableUtc(object value) => value == DBNull.Value ? null : ReadUtc(value);

        internal static string? ReadText(object value) => value == DBNull.Value ? null : Convert.ToString(value, CultureInfo.InvariantCulture);

        internal static bool ReadBool(object value) => Convert.ToBoolean(value, CultureInfo.InvariantCulture);

        internal static long? ReadNullableLong(object value) => value == DBNull.Value ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture);

        /// <summary>
        /// Build a bounded window SELECT. One extra row is requested so the caller can report truncation.
        /// </summary>
        internal static void BuildWindowSelect(DbCommand command, DatabaseTypeEnum provider, string table, string columns, ProductionFactQuery query)
        {
            string where = " WHERE created_utc >= @from_utc AND created_utc < @to_utc";
            if (query.TenantId != null) where += " AND tenant_id = @tenant_id";
            if (query.UserId != null) where += " AND user_id = @user_id";
            string order = " ORDER BY created_utc ASC, id ASC";
            command.CommandText = provider == DatabaseTypeEnum.SqlServer
                ? "SELECT TOP (@row_limit) " + columns + " FROM " + table + where + order + ";"
                : "SELECT " + columns + " FROM " + table + where + order + " LIMIT @row_limit;";
            AddUtc(command, "@from_utc", query.FromUtc == DateTime.MinValue ? new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc) : query.FromUtc, provider);
            AddUtc(command, "@to_utc", query.ToUtc == DateTime.MaxValue ? new DateTime(9000, 1, 1, 0, 0, 0, DateTimeKind.Utc) : query.ToUtc, provider);
            if (query.TenantId != null) Add(command, "@tenant_id", query.TenantId);
            if (query.UserId != null) Add(command, "@user_id", query.UserId);
            Add(command, "@row_limit", query.Limit + 1);
        }

        internal static string? BoundCode(string? value, int maximumLength)
        {
            if (String.IsNullOrWhiteSpace(value)) return null;
            char[] chars = value.Trim().ToLowerInvariant().ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                char c = chars[i];
                bool allowed = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_' || c == '.' || c == ':' || c == '-';
                if (!allowed) chars[i] = '_';
            }
            string code = new string(chars);
            return code.Length > maximumLength ? code.Substring(0, maximumLength) : code;
        }
    }
}
