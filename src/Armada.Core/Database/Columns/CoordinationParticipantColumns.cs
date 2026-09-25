namespace Armada.Core.Database
{
    using System.Data;
    using Armada.Core.Models;

    /// <summary>
    /// The coordination_participants row-to-model contract, shared by every provider that stores the coordination board. Every read
    /// selects the whole row, so each column is read as present.
    /// </summary>
    internal static class CoordinationParticipantColumns
    {
        /// <summary>
        /// Read a coordination_participants row.
        /// </summary>
        /// <param name="record">Reader positioned on a coordination_participants row.</param>
        /// <param name="values">Provider value converter.</param>
        /// <returns>The coordination participant.</returns>
        internal static CoordinationParticipant Read(IDataRecord record, StoredValueConverter values)
        {
            StoredRow row = new StoredRow(record, values, "CoordinationParticipant");
            return new CoordinationParticipant
            {
                Id = row.Text("id"),
                CoordinationRoomId = row.Text("coordination_room_id"),
                TenantId = row.NullableText("tenant_id"),
                ParticipantKey = row.Text("participant_key"),
                DisplayName = row.Text("display_name"),
                LastSeenUtc = row.Utc("last_seen_utc"),
                CreatedUtc = row.Utc("created_utc"),
                LastUpdateUtc = row.Utc("last_update_utc")
            };
        }
    }
}
