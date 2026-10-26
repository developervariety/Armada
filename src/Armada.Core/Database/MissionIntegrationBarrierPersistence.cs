namespace Armada.Core.Database
{
    using System;
    using System.Data;
    using System.Globalization;
    using System.Text;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>Bounded storage and conditional writes for Architect integration barriers.</summary>
    internal static partial class MissionIntegrationBarrierPersistence
    {
        internal const int MaximumMembers = 50;
        internal const int MaximumBytes = 65536;
        private static readonly JsonSerializerOptions _Json = CreateJsonOptions();

        internal static void Read(IDataRecord record, Mission mission)
        {
            object raw = record["integration_barrier_json"];
            mission.IntegrationBarrierWriteRevision = Convert.ToInt64(record["integration_barrier_revision"], CultureInfo.InvariantCulture);
            mission.IntegrationBarrierWriteStamp = record["last_update_utc"];
            mission.IntegrationBarrierRawJsonPresent = raw != DBNull.Value;
            mission.IntegrationBarrierRawJson = raw == DBNull.Value ? null : (string)raw;
            if (!mission.IntegrationBarrierRawJsonPresent)
            {
                mission.IntegrationBarrierInvalid = mission.IntegrationBarrierWriteRevision != 0;
                return;
            }

            string json = mission.IntegrationBarrierRawJson!;
            if (Encoding.UTF8.GetByteCount(json) > MaximumBytes)
            {
                mission.IntegrationBarrierInvalid = true;
                return;
            }

            try
            {
                MissionIntegrationBarrier? barrier = Deserialize(json);
                if (barrier == null || !IsValid(barrier, mission))
                {
                    mission.IntegrationBarrierInvalid = true;
                    return;
                }
                mission.IntegrationBarrier = barrier;
            }
            catch (JsonException)
            {
                mission.IntegrationBarrierInvalid = true;
            }
        }

        internal static void ValidateForCreate(Mission mission)
        {
            if (mission.IntegrationBarrierInvalid || mission.IntegrationBarrierRawJsonPresent)
                throw new InvalidOperationException("A new mission cannot copy raw integration barrier storage state.");
            if (mission.IntegrationBarrier == null) return;
            ValidateDocument(mission.IntegrationBarrier, mission);
            if (mission.IntegrationBarrier.Phase == MissionIntegrationBarrierPhaseEnum.Preparing
                && mission.IntegrationBarrier.Members.Count != 0)
                throw new InvalidOperationException("A preparing integration barrier cannot contain accepted members.");
        }

        internal static string? JsonForWrite(Mission mission)
        {
            if (mission.IntegrationBarrierInvalid) return mission.IntegrationBarrierRawJson;
            if (mission.IntegrationBarrier != null) return Serialize(mission.IntegrationBarrier, mission);
            return mission.IntegrationBarrierRawJsonPresent ? mission.IntegrationBarrierRawJson : null;
        }

        internal static void BindCreate(StoredParameters parameters, Mission mission)
        {
            parameters
                .Text("integration_barrier_json", JsonForWrite(mission))
                .Long("integration_barrier_revision", RevisionForWrite(mission));
        }

        internal static long RevisionForWrite(Mission mission) => mission.IntegrationBarrierWriteRevision;

        internal static string Serialize(MissionIntegrationBarrier barrier, Mission mission)
        {
            ValidateDocument(barrier, mission);
            string json = JsonSerializer.Serialize(barrier, _Json);
            if (Encoding.UTF8.GetByteCount(json) > MaximumBytes)
                throw new InvalidOperationException("Integration barrier exceeds its storage limit.");
            return json;
        }

        private static MissionIntegrationBarrier? Deserialize(string json) =>
            JsonSerializer.Deserialize<MissionIntegrationBarrier>(json, _Json);

        private static JsonSerializerOptions CreateJsonOptions()
        {
            JsonSerializerOptions options = new JsonSerializerOptions();
            options.Converters.Add(new JsonStringEnumConverter());
            return options;
        }
    }
}
