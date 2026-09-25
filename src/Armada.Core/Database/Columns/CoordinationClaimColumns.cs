namespace Armada.Core.Database
{
    using System.Data;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// The coordination_claims row-to-model contract, shared by every provider that stores the coordination board. Every read
    /// selects the whole row, so each column is read as present.
    /// </summary>
    internal static class CoordinationClaimColumns
    {
        /// <summary>
        /// Read a coordination_claims row.
        /// </summary>
        /// <param name="record">Reader positioned on a coordination_claims row.</param>
        /// <param name="values">Provider value converter.</param>
        /// <returns>The coordination claim.</returns>
        internal static CoordinationClaim Read(IDataRecord record, StoredValueConverter values)
        {
            StoredRow row = new StoredRow(record, values, "CoordinationClaim");
            return new CoordinationClaim
            {
                Id = row.Text("id"),
                CoordinationRoomId = row.Text("coordination_room_id"),
                TenantId = row.NullableText("tenant_id"),
                ParticipantKey = row.Text("participant_key"),
                DisplayName = row.Text("display_name"),
                SubjectType = row.Enum<CoordinationClaimSubjectEnum>("subject_type"),
                SubjectId = row.Text("subject_id"),
                Note = row.NullableText("note"),
                Status = row.Enum<CoordinationClaimStatusEnum>("status"),
                ExpiresUtc = row.Utc("expires_utc"),
                CreatedUtc = row.Utc("created_utc"),
                LastUpdateUtc = row.Utc("last_update_utc")
            };
        }
    }
}
