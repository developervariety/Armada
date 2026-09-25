namespace Armada.Core.Database
{
    using System.Data;
    using Armada.Core.Models;

    /// <summary>
    /// The judge_follow_ups row-to-model contract, shared by every provider.
    /// </summary>
    internal static class JudgeFollowUpColumns
    {
        /// <summary>
        /// Read a judge_follow_ups row.
        /// </summary>
        /// <param name="record">Reader positioned on a judge_follow_ups row.</param>
        /// <param name="values">Provider value converter.</param>
        /// <returns>The follow-up.</returns>
        internal static JudgeFollowUp Read(IDataRecord record, StoredValueConverter values)
        {
            StoredRow row = new StoredRow(record, values, "JudgeFollowUp");
            JudgeFollowUp item = new JudgeFollowUp();
            item.Id = row.Text("id");
            item.TenantId = row.NullableText("tenant_id");
            item.UserId = row.NullableText("user_id");
            item.JudgeMissionId = row.Text("judge_mission_id");
            item.ReviewedMissionId = row.Text("reviewed_mission_id");
            item.VoyageId = row.NullableText("voyage_id");
            item.VesselId = row.NullableText("vessel_id");
            item.MergeEntryId = row.NullableText("merge_entry_id");
            item.JudgeVerdict = row.Text("judge_verdict");
            item.SuggestedFollowUps = row.NullableText("suggested_follow_ups");
            item.AuditVerdict = row.Text("audit_verdict");
            item.AuditNotes = row.NullableText("audit_notes");
            item.AuditRecommendedAction = row.NullableText("audit_recommended_action");
            item.AuditCompletedUtc = row.NullableUtc("audit_completed_utc");
            item.CreatedUtc = row.Utc("created_utc");
            item.LastUpdateUtc = row.Utc("last_update_utc");
            return item;
        }

        /// <summary>
        /// Bind every stored judge_follow_ups column, each in the form its provider stores it.
        /// </summary>
        /// <param name="parameters">Parameters of a command addressing the judge_follow_ups table.</param>
        /// <param name="item">Row to bind.</param>
        internal static void Write(StoredParameters parameters, JudgeFollowUp item)
        {
            parameters
                .Text("id", item.Id)
                .Text("tenant_id", item.TenantId)
                .Text("user_id", item.UserId)
                .Text("judge_mission_id", item.JudgeMissionId)
                .Text("reviewed_mission_id", item.ReviewedMissionId)
                .Text("voyage_id", item.VoyageId)
                .Text("vessel_id", item.VesselId)
                .Text("merge_entry_id", item.MergeEntryId)
                .Text("judge_verdict", item.JudgeVerdict)
                .Text("suggested_follow_ups", item.SuggestedFollowUps)
                .Text("audit_verdict", item.AuditVerdict)
                .Text("audit_notes", item.AuditNotes)
                .Text("audit_recommended_action", item.AuditRecommendedAction)
                .Utc("audit_completed_utc", item.AuditCompletedUtc)
                .Utc("created_utc", item.CreatedUtc)
                .Utc("last_update_utc", item.LastUpdateUtc);
        }
    }
}
