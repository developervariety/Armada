namespace Armada.Core.Database
{
    using System;
    using System.Data;
    using System.Data.Common;
    using System.Text;
    using System.Text.Json;
    using Armada.Core.Models;

    /// <summary>Bound versioned persistence for the remote checkout identity on a dock.</summary>
    internal static class HarborCheckoutBindingPersistence
    {
        internal const int MaxBytes = 16384;

        internal static readonly string[] SqliteStatements =
        {
            @"ALTER TABLE docks ADD COLUMN host_checkout_json TEXT NULL;",
            @"ALTER TABLE docks ADD COLUMN provision_attempt_id TEXT NULL;",
            @"ALTER TABLE docks ADD COLUMN host_checkout_lifecycle_json TEXT NULL;",
            @"ALTER TABLE missions ADD COLUMN dock_provision_attempt_id TEXT NULL;",
            @"CREATE UNIQUE INDEX IF NOT EXISTS uq_docks_provision_attempt ON docks(provision_attempt_id) WHERE provision_attempt_id IS NOT NULL;"
        };

        internal static readonly string[] PostgresqlStatements =
        {
            @"ALTER TABLE docks ADD COLUMN IF NOT EXISTS host_checkout_json TEXT NULL;",
            @"ALTER TABLE docks ADD COLUMN IF NOT EXISTS provision_attempt_id TEXT NULL;",
            @"ALTER TABLE docks ADD COLUMN IF NOT EXISTS host_checkout_lifecycle_json TEXT NULL;",
            @"ALTER TABLE missions ADD COLUMN IF NOT EXISTS dock_provision_attempt_id TEXT NULL;",
            @"CREATE UNIQUE INDEX IF NOT EXISTS uq_docks_provision_attempt ON docks(provision_attempt_id) WHERE provision_attempt_id IS NOT NULL;"
        };

        internal static readonly string[] MysqlStatements =
        {
            @"ALTER TABLE docks ADD COLUMN host_checkout_json LONGTEXT CHARACTER SET utf8mb4 NULL;",
            @"ALTER TABLE docks ADD COLUMN provision_attempt_id VARCHAR(191) NULL;",
            @"ALTER TABLE docks ADD COLUMN host_checkout_lifecycle_json LONGTEXT CHARACTER SET utf8mb4 NULL;",
            @"ALTER TABLE missions ADD COLUMN dock_provision_attempt_id VARCHAR(191) NULL;",
            @"CREATE UNIQUE INDEX uq_docks_provision_attempt ON docks(provision_attempt_id);"
        };

        internal static readonly string[] SqlServerStatements =
        {
            @"IF COL_LENGTH('docks', 'host_checkout_json') IS NULL ALTER TABLE docks ADD host_checkout_json NVARCHAR(MAX) NULL;",
            @"IF COL_LENGTH('docks', 'provision_attempt_id') IS NULL ALTER TABLE docks ADD provision_attempt_id NVARCHAR(450) NULL;",
            @"IF COL_LENGTH('docks', 'host_checkout_lifecycle_json') IS NULL ALTER TABLE docks ADD host_checkout_lifecycle_json NVARCHAR(MAX) NULL;",
            @"IF COL_LENGTH('missions', 'dock_provision_attempt_id') IS NULL ALTER TABLE missions ADD dock_provision_attempt_id NVARCHAR(450) NULL;",
            @"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'uq_docks_provision_attempt' AND object_id = OBJECT_ID('docks')) CREATE UNIQUE INDEX uq_docks_provision_attempt ON docks(provision_attempt_id) WHERE provision_attempt_id IS NOT NULL;"
        };

        internal static string Serialize(HarborCheckoutBinding binding)
        {
            if (binding == null) throw new ArgumentNullException(nameof(binding));
            if (!binding.IsComplete) throw new InvalidOperationException("harbor_checkout_binding_invalid: binding identity is incomplete");
            string json = JsonSerializer.Serialize(binding);
            if (Encoding.UTF8.GetByteCount(json) > MaxBytes)
                throw new InvalidOperationException("harbor_checkout_binding_invalid: binding exceeds its byte limit");
            return json;
        }

        internal static HarborCheckoutBinding? Read(object value, string? tenantId, string? userId)
        {
            if (value == DBNull.Value) return null;
            if (value is not string json || String.IsNullOrEmpty(json)
                || Encoding.UTF8.GetByteCount(json) > MaxBytes)
                throw InvalidStoredBinding();

            HarborCheckoutBinding? binding;
            try
            {
                binding = JsonSerializer.Deserialize<HarborCheckoutBinding>(json);
            }
            catch (JsonException)
            {
                throw InvalidStoredBinding();
            }

            if (binding == null || !binding.IsComplete
                || !String.Equals(binding.TenantId, tenantId, StringComparison.Ordinal)
                || !String.Equals(binding.UserId, userId, StringComparison.Ordinal))
                throw InvalidStoredBinding();

            return binding;
        }

        internal static void Add(DbCommand command, Dock dock)
        {
            HarborCheckoutBinding? binding = dock.HostCheckout;
            if (binding != null && (!binding.IsComplete
                || !String.Equals(binding.TenantId, dock.TenantId, StringComparison.Ordinal)
                || !String.Equals(binding.UserId, dock.UserId, StringComparison.Ordinal)))
                throw new InvalidOperationException("harbor_checkout_binding_invalid: binding owner does not match the dock");

            DbParameter parameter = command.CreateParameter();
            parameter.ParameterName = "@host_checkout_json";
            parameter.DbType = DbType.String;
            parameter.Value = binding == null ? DBNull.Value : Serialize(binding);
            command.Parameters.Add(parameter);
        }

        private static InvalidOperationException InvalidStoredBinding() =>
            new InvalidOperationException("harbor_checkout_binding_invalid: stored dock checkout identity is malformed or mismatched");
    }
}
