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

        /// <summary>
        /// Bind every stored coordination_claims column, each in the form its provider stores it.
        /// </summary>
        /// <param name="parameters">Parameters of a command addressing the coordination_claims table.</param>
        /// <param name="claim">Row to bind.</param>
        internal static void Write(StoredParameters parameters, CoordinationClaim claim)
        {
            parameters
                .Text("id", claim.Id)
                .Text("coordination_room_id", claim.CoordinationRoomId)
                .Text("tenant_id", claim.TenantId)
                .Text("participant_key", claim.ParticipantKey)
                .Text("display_name", claim.DisplayName)
                .Text("subject_type", claim.SubjectType.ToString())
                .Text("subject_id", claim.SubjectId)
                .Text("note", claim.Note)
                .Text("status", claim.Status.ToString())
                .Utc("expires_utc", claim.ExpiresUtc)
                .Utc("created_utc", claim.CreatedUtc)
                .Utc("last_update_utc", claim.LastUpdateUtc);
        }
    }
}
