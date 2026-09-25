namespace Armada.Core.Database
{
    using System.Data;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// The memories row-to-model contract, shared by every provider. A scope, type or source-kind name outside the
    /// model reads as the model's default. Tags live in their own table and are read by the method set.
    /// </summary>
    internal static class MemoryColumns
    {
        /// <summary>
        /// Read a memories row.
        /// </summary>
        /// <param name="record">Reader positioned on a memories row.</param>
        /// <param name="values">Provider value converter.</param>
        /// <returns>The memory, without its tags.</returns>
        internal static Memory Read(IDataRecord record, StoredValueConverter values)
        {
            StoredRow row = new StoredRow(record, values, "Memory");
            Memory memory = new Memory();
            memory.Id = row.Text("id");
            memory.TenantId = row.NullableText("tenant_id");
            memory.UserId = row.NullableText("user_id");
            memory.Scope = row.EnumOrFallback("scope", MemoryScopeEnum.TenantWide);
            memory.Type = row.EnumOrFallback("type", MemoryTypeEnum.Semantic);
            memory.Topic = row.NullableText("topic");
            memory.Key = row.NullableText("memory_key");
            memory.Summary = row.NullableText("summary");
            memory.Content = row.Text("content");
            memory.Salience = row.Double("salience");
            memory.Version = row.Int("version");
            memory.SourceKind = row.EnumOrFallback("source_kind", MemorySourceKindEnum.Manual);
            memory.SourceVoyageId = row.NullableText("source_voyage_id");
            memory.SourceMissionId = row.NullableText("source_mission_id");
            memory.SourceVesselId = row.NullableText("source_vessel_id");
            memory.SourceDetail = row.NullableText("source_detail");
            memory.VesselId = row.NullableText("vessel_id");
            memory.CreatedUtc = row.Utc("created_utc");
            memory.LastUpdateUtc = row.Utc("last_update_utc");
            return memory;
        }

        /// <summary>
        /// Bind every stored memories column, each in the form its provider stores it.
        /// </summary>
        /// <param name="parameters">Parameters of a command addressing the memories table.</param>
        /// <param name="memory">Row to bind.</param>
        internal static void Write(StoredParameters parameters, Memory memory)
        {
            parameters
                .Text("id", memory.Id)
                .Text("tenant_id", memory.TenantId)
                .Text("user_id", memory.UserId)
                .Text("scope", memory.Scope.ToString())
                .Text("type", memory.Type.ToString())
                .Text("topic", memory.Topic)
                .Text("memory_key", memory.Key)
                .Text("summary", memory.Summary)
                .Text("content", memory.Content)
                .Double("salience", memory.Salience)
                .Int("version", memory.Version)
                .Text("source_kind", memory.SourceKind.ToString())
                .Text("source_voyage_id", memory.SourceVoyageId)
                .Text("source_mission_id", memory.SourceMissionId)
                .Text("source_vessel_id", memory.SourceVesselId)
                .Text("source_detail", memory.SourceDetail)
                .Text("vessel_id", memory.VesselId)
                .Utc("created_utc", memory.CreatedUtc)
                .Utc("last_update_utc", memory.LastUpdateUtc);
        }
    }
}
