namespace Armada.Core.Database
{
    using System;
    using System.Collections.Generic;
    using System.Data;
    using System.Data.Common;
    using System.Text.Json;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>Persist model metadata without changing routing or landing policy.</summary>
    internal static class BackendMetadataPersistence
    {
        internal static void AddCaptain(DbCommand command, Captain captain)
        {
            Add(command, "tier", TierName(captain.Tier));
            TierRoutingPersistence.AddCaptain(command, captain);
        }

        internal static void AddVoyage(DbCommand command, Voyage voyage)
        {
            Add(command, "source_planning_session_id", voyage.SourcePlanningSessionId);
            Add(command, "source_planning_message_id", voyage.SourcePlanningMessageId);
        }

        internal static void AddVessel(DbCommand command, Vessel vessel)
        {
            // The preserved PostgreSQL column is INTEGER, despite a later skipped BOOLEAN declaration.
            DbParameter enabled = command.CreateParameter();
            enabled.ParameterName = "@secret_scan_enabled";
            enabled.DbType = DbType.Int32;
            enabled.Value = vessel.SecretScanEnabled ? 1 : 0;
            command.Parameters.Add(enabled);
            Add(command, "protected_path_patterns_json", JsonSerializer.Serialize(vessel.ProtectedPathPatterns ?? new List<string>()));
            Add(command, "private_identifier_denylist_json", JsonSerializer.Serialize(vessel.PrivateIdentifierDenylist ?? new List<string>()));
        }

        private static string? TierName(CaptainTierEnum? tier)
        {
            if (!tier.HasValue) return null;
            if (!Enum.IsDefined(tier.Value)) throw new ArgumentOutOfRangeException(nameof(tier));
            return tier.Value.ToString();
        }

        private static void Add(DbCommand command, string name, string? value)
        {
            DbParameter parameter = command.CreateParameter();
            parameter.ParameterName = "@" + name;
            parameter.DbType = DbType.String;
            parameter.Value = (object?)value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }
    }
}
