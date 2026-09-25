namespace Armada.Core.Database
{
    using System.Data;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// The voyages row-to-model contract, shared by every provider. A landing mode that is blank or not a mode
    /// name reads as no override; an empty planning source reads as no source.
    /// </summary>
    internal static class VoyageColumns
    {
        /// <summary>
        /// Read a voyages row.
        /// </summary>
        /// <param name="record">Reader positioned on a voyages row.</param>
        /// <param name="values">Provider value converter.</param>
        /// <returns>The voyage.</returns>
        internal static Voyage Read(IDataRecord record, StoredValueConverter values)
        {
            StoredRow row = new StoredRow(record, values, "Voyage");
            Voyage voyage = new Voyage();
            voyage.Id = row.Text("id");
            voyage.TenantId = row.NullableText("tenant_id");
            voyage.UserId = row.NullableText("user_id");
            voyage.Title = row.Text("title");
            voyage.Description = row.NullableText("description");
            voyage.Status = row.Enum<VoyageStatusEnum>("status");
            voyage.CreatedUtc = row.Utc("created_utc");
            voyage.CompletedUtc = row.NullableUtc("completed_utc");
            voyage.LastUpdateUtc = row.Utc("last_update_utc");
            voyage.AutoPush = row.NullableBool("auto_push");
            voyage.AutoCreatePullRequests = row.NullableBool("auto_create_pull_requests");
            voyage.AutoMergePullRequests = row.NullableBool("auto_merge_pull_requests");
            voyage.LandingMode = row.EnumOrNull<LandingModeEnum>("landing_mode", ignoreCase: false);
            voyage.SourcePlanningSessionId = row.NullableText("source_planning_session_id");
            voyage.SourcePlanningMessageId = row.NullableText("source_planning_message_id");
            voyage.CaptainOverridesJson = row.NullableText("captain_overrides_json");
            return voyage;
        }

        /// <summary>
        /// Bind every stored voyages column, each in the form its provider stores it.
        /// </summary>
        /// <param name="parameters">Parameters of a command addressing the voyages table.</param>
        /// <param name="voyage">Row to bind.</param>
        internal static void Write(StoredParameters parameters, Voyage voyage)
        {
            parameters
                .Text("id", voyage.Id)
                .Text("tenant_id", voyage.TenantId)
                .Text("user_id", voyage.UserId)
                .Text("title", voyage.Title)
                .Text("description", voyage.Description)
                .Text("status", voyage.Status.ToString())
                .Utc("created_utc", voyage.CreatedUtc)
                .Utc("completed_utc", voyage.CompletedUtc)
                .Utc("last_update_utc", voyage.LastUpdateUtc)
                .Bool("auto_push", voyage.AutoPush)
                .Bool("auto_create_pull_requests", voyage.AutoCreatePullRequests)
                .Bool("auto_merge_pull_requests", voyage.AutoMergePullRequests)
                .Text("landing_mode", voyage.LandingMode?.ToString())
                .Text("source_planning_session_id", voyage.SourcePlanningSessionId)
                .Text("source_planning_message_id", voyage.SourcePlanningMessageId)
                .Text("captain_overrides_json", voyage.CaptainOverridesJson);
        }
    }
}
