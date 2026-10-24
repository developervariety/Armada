namespace Armada.Core.Database
{
    using System;
    using System.Data;
    using System.Data.Common;
    using System.Text;
    using System.Text.Json;
    using System.Text.RegularExpressions;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>Validated, versioned Harbor checkout lifecycle storage and compare-and-set writes.</summary>
    internal static partial class HarborCheckoutLifecyclePersistence
    {
        internal const int MaxBytes = 16384;

        internal static HarborCheckoutLifecycleIntent? Read(object value)
        {
            if (value == DBNull.Value) return null;
            if (value is not string json || String.IsNullOrEmpty(json) || Encoding.UTF8.GetByteCount(json) > MaxBytes)
                throw InvalidIntent();
            HarborCheckoutLifecycleIntent? intent;
            try
            {
                intent = JsonSerializer.Deserialize<HarborCheckoutLifecycleIntent>(json);
            }
            catch (JsonException)
            {
                throw InvalidIntent();
            }
            if (intent == null || !Valid(intent)) throw InvalidIntent();
            intent.StoredJson = json;
            return intent;
        }

        internal static void Add(DbCommand command, Dock dock)
        {
            HarborCheckoutLifecycleIntent? intent = dock.HostCheckoutLifecycle;
            if (intent != null && !Valid(intent)) throw InvalidIntent();
            DbParameter parameter = command.CreateParameter();
            parameter.ParameterName = "@host_checkout_lifecycle_json";
            parameter.DbType = DbType.String;
            parameter.Value = intent == null ? DBNull.Value : Serialize(intent);
            command.Parameters.Add(parameter);
        }

        internal static string? SerializeForWrite(HarborCheckoutLifecycleIntent? intent)
        {
            if (intent == null) return null;
            if (!Valid(intent)) throw InvalidIntent();
            return Serialize(intent);
        }

        private static bool Valid(HarborCheckoutLifecycleIntent intent)
        {
            return intent.Version == 1
                && !String.IsNullOrWhiteSpace(intent.IntentId)
                && intent.IntentId.Length <= 191
                && !String.IsNullOrWhiteSpace(intent.BindingRevision)
                && intent.BindingRevision.Length <= 191
                && !String.IsNullOrWhiteSpace(intent.DockId)
                && intent.DockId.Length <= 191
                && (intent.MissionId == null || intent.MissionId.Length <= 191)
                && (intent.BranchName == null || intent.BranchName.Length <= 512)
                && Regex.IsMatch(intent.InitialHead ?? String.Empty, @"\A(?:[0-9a-fA-F]{40}|[0-9a-fA-F]{64})\z")
                && Regex.IsMatch(intent.ExpectedHead ?? String.Empty, @"\A(?:[0-9a-fA-F]{40}|[0-9a-fA-F]{64})\z")
                && (intent.Phase != HarborCheckoutLifecyclePhaseEnum.Prepared
                    || String.Equals(intent.ExpectedHead, intent.InitialHead, StringComparison.OrdinalIgnoreCase))
                && Enum.IsDefined(intent.Purpose)
                && Enum.IsDefined(intent.Phase)
                && (intent.Phase < HarborCheckoutLifecyclePhaseEnum.CheckoutRemoved || intent.RemovalConfirmed)
                && (intent.Phase != HarborCheckoutLifecyclePhaseEnum.Terminal || intent.RemovalConfirmed)
                && (intent.SnapshotCommitSha == null || Regex.IsMatch(intent.SnapshotCommitSha, @"\A(?:[0-9a-fA-F]{40}|[0-9a-fA-F]{64})\z"))
                && (intent.AnchoredHead == null || Regex.IsMatch(intent.AnchoredHead, @"\A(?:[0-9a-fA-F]{40}|[0-9a-fA-F]{64})\z"))
                && (intent.SnapshotRefName == null || (intent.SnapshotRefName.Length <= 512 && intent.SnapshotRefName.StartsWith("refs/", StringComparison.Ordinal)))
                && ValidRef(intent.DockAnchorRef)
                && ValidRef(intent.MissionAnchorRef)
                && ValidRef(intent.BranchAnchorRef);
        }

        private static bool ValidRef(string? refName)
        {
            return refName == null || (refName.Length <= 512 && refName.StartsWith("refs/", StringComparison.Ordinal));
        }

        private static string Serialize(HarborCheckoutLifecycleIntent intent)
        {
            string json = JsonSerializer.Serialize(intent);
            if (Encoding.UTF8.GetByteCount(json) > MaxBytes) throw InvalidIntent();
            return json;
        }

        private static string BinaryEquals(string column, string parameter, DatabaseTypeEnum provider)
        {
            string left = "COALESCE(" + column + ", '')";
            string right = "COALESCE(" + parameter + ", '')";
            return provider switch
            {
                DatabaseTypeEnum.Sqlite => left + " = " + right + " COLLATE BINARY",
                DatabaseTypeEnum.Postgresql => "convert_to(" + left + ", 'UTF8') = convert_to(" + right + ", 'UTF8')",
                DatabaseTypeEnum.Mysql => "BINARY " + left + " = BINARY " + right,
                DatabaseTypeEnum.SqlServer => "CONVERT(varbinary(max), " + left + ") = CONVERT(varbinary(max), " + right + ")",
                _ => throw new NotSupportedException()
            };
        }

        private static InvalidOperationException InvalidIntent() =>
            new InvalidOperationException("harbor_checkout_lifecycle_intent_invalid");
    }
}
