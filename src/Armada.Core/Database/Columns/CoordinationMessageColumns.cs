namespace Armada.Core.Database
{
    using System.Data;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// The coordination_messages row-to-model contract, shared by every provider that stores the coordination board. Every read
    /// selects the whole row, so each column is read as present.
    /// </summary>
    internal static class CoordinationMessageColumns
    {
        /// <summary>
        /// Read a coordination_messages row.
        /// </summary>
        /// <param name="record">Reader positioned on a coordination_messages row.</param>
        /// <param name="values">Provider value converter.</param>
        /// <returns>The coordination message.</returns>
        internal static CoordinationMessage Read(IDataRecord record, StoredValueConverter values)
        {
            StoredRow row = new StoredRow(record, values, "CoordinationMessage");
            return new CoordinationMessage
            {
                Id = row.Text("id"),
                CoordinationRoomId = row.Text("coordination_room_id"),
                TenantId = row.NullableText("tenant_id"),
                AuthorType = row.Enum<CoordinationAuthorTypeEnum>("author_type"),
                AuthorId = row.NullableText("author_id"),
                AuthorName = row.Text("author_name"),
                Content = row.Text("content"),
                VoyageId = row.NullableText("voyage_id"),
                MissionId = row.NullableText("mission_id"),
                VesselId = row.NullableText("vessel_id"),
                IncidentId = row.NullableText("incident_id"),
                ToParticipantKey = row.NullableText("to_participant_key"),
                CreatedUtc = row.Utc("created_utc"),
                LastUpdateUtc = row.Utc("last_update_utc")
            };
        }
    }
}
