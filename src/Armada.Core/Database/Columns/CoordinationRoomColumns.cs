namespace Armada.Core.Database
{
    using System.Data;
    using Armada.Core.Models;

    /// <summary>
    /// The coordination_rooms row-to-model contract, shared by every provider that stores the coordination board. Every read
    /// selects the whole row, so each column is read as present.
    /// </summary>
    internal static class CoordinationRoomColumns
    {
        /// <summary>
        /// Read a coordination_rooms row.
        /// </summary>
        /// <param name="record">Reader positioned on a coordination_rooms row.</param>
        /// <param name="values">Provider value converter.</param>
        /// <returns>The coordination room.</returns>
        internal static CoordinationRoom Read(IDataRecord record, StoredValueConverter values)
        {
            StoredRow row = new StoredRow(record, values, "CoordinationRoom");
            return new CoordinationRoom
            {
                Id = row.Text("id"),
                TenantId = row.NullableText("tenant_id"),
                UserId = row.NullableText("user_id"),
                Key = row.Text("key"),
                Name = row.Text("name"),
                Description = row.NullableText("description"),
                CreatedUtc = row.Utc("created_utc"),
                LastUpdateUtc = row.Utc("last_update_utc")
            };
        }

        /// <summary>
        /// Bind every stored coordination_rooms column, each in the form its provider stores it.
        /// </summary>
        /// <param name="parameters">Parameters of a command addressing the coordination_rooms table.</param>
        /// <param name="room">Row to bind.</param>
        internal static void Write(StoredParameters parameters, CoordinationRoom room)
        {
            parameters
                .Text("id", room.Id)
                .Text("tenant_id", room.TenantId)
                .Text("user_id", room.UserId)
                .Text("key", room.Key)
                .Text("name", room.Name)
                .Text("description", room.Description)
                .Utc("created_utc", room.CreatedUtc)
                .Utc("last_update_utc", room.LastUpdateUtc);
        }
    }
}
