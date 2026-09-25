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

        /// <summary>
        /// Bind every stored coordination_participants column, each in the form its provider stores it.
        /// </summary>
        /// <param name="parameters">Parameters of a command addressing the coordination_participants table.</param>
        /// <param name="participant">Row to bind.</param>
        internal static void Write(StoredParameters parameters, CoordinationParticipant participant)
        {
            parameters
                .Text("id", participant.Id)
                .Text("coordination_room_id", participant.CoordinationRoomId)
                .Text("tenant_id", participant.TenantId)
                .Text("participant_key", participant.ParticipantKey)
                .Text("display_name", participant.DisplayName)
                .Utc("last_seen_utc", participant.LastSeenUtc)
                .Utc("created_utc", participant.CreatedUtc)
                .Utc("last_update_utc", participant.LastUpdateUtc);
        }
    }
}
