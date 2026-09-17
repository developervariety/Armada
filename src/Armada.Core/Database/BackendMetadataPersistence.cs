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

        internal static void ReadCaptain(DbDataReader reader, Captain captain)
        {
            captain.Tier = ReadTier(reader["tier"]);
            TierRoutingPersistence.ReadCaptain(reader, captain);
        }

        internal static void AddMission(DbCommand command, Mission mission)
        {
            Add(command, "tier", TierName(mission.Tier));
            Add(command, "requested_captain_id", mission.RequestedCaptainId);
            MissionOperatorHoldPersistence.Add(command, mission);
        }

        internal static void ReadMission(DbDataReader reader, Mission mission)
        {
            MissionAdmissionPersistence.Read(reader, mission);
            mission.Tier = ReadTier(reader["tier"]);
            mission.RequestedCaptainId = NullableText(reader["requested_captain_id"]);
            MissionOperatorHoldPersistence.Read(reader, mission);
        }

        internal static void AddVoyage(DbCommand command, Voyage voyage)
        {
            Add(command, "source_planning_session_id", voyage.SourcePlanningSessionId);
            Add(command, "source_planning_message_id", voyage.SourcePlanningMessageId);
        }

        internal static void ReadVoyage(DbDataReader reader, Voyage voyage)
        {
            voyage.SourcePlanningSessionId = NullableText(reader["source_planning_session_id"]);
            voyage.SourcePlanningMessageId = NullableText(reader["source_planning_message_id"]);
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

        internal static void ReadVessel(DbDataReader reader, Vessel vessel)
        {
            vessel.SecretScanEnabled = Convert.ToBoolean(reader["secret_scan_enabled"]);
            vessel.ProtectedPathPatterns = ReadList(reader["protected_path_patterns_json"]);
            vessel.PrivateIdentifierDenylist = ReadList(reader["private_identifier_denylist_json"]);
        }

        private static List<string> ReadList(object value) => value == DBNull.Value ? new List<string>()
            : JsonSerializer.Deserialize<List<string>>((string)value) ?? throw new InvalidOperationException("Stored patterns must be a JSON array.");

        private static string? NullableText(object value) => value == DBNull.Value ? null : (string)value;

        private static CaptainTierEnum? ReadTier(object value)
        {
            if (value == DBNull.Value) return null;
            string name = (string)value;
            if (!Enum.TryParse(name, out CaptainTierEnum tier) || !Enum.IsDefined(tier) || tier.ToString() != name)
                throw new InvalidOperationException("Stored captain tier is invalid.");
            return tier;
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
