namespace Armada.Core.Database
{
    using System.Collections.Generic;
    using System.Data;
    using System.Text.Json;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// The releases row-to-model contract, shared by every provider. A status name outside the model reads as the
    /// model's default status; a linked-id or artifact list that is null or blank reads as empty, and one that is
    /// not valid JSON is named rather than read as empty.
    /// </summary>
    internal static class ReleaseColumns
    {
        private static readonly JsonSerializerOptions _Json = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        /// <summary>
        /// Read a releases row.
        /// </summary>
        /// <param name="record">Reader positioned on a releases row.</param>
        /// <param name="values">Provider value converter.</param>
        /// <returns>The release.</returns>
        internal static Release Read(IDataRecord record, StoredValueConverter values)
        {
            StoredRow row = new StoredRow(record, values, "Release");
            Release release = new Release();
            release.Id = row.Text("id");
            release.TenantId = row.NullableText("tenant_id");
            release.UserId = row.NullableText("user_id");
            release.VesselId = row.NullableText("vessel_id");
            release.WorkflowProfileId = row.NullableText("workflow_profile_id");
            release.Title = row.Text("title");
            release.Version = row.NullableText("version");
            release.TagName = row.NullableText("tag_name");
            release.Summary = row.NullableText("summary");
            release.Notes = row.NullableText("notes");
            release.Status = row.EnumOrFallback("status", ReleaseStatusEnum.Draft);
            release.VoyageIds = row.Json<List<string>>("voyage_ids_json", _Json) ?? new List<string>();
            release.MissionIds = row.Json<List<string>>("mission_ids_json", _Json) ?? new List<string>();
            release.CheckRunIds = row.Json<List<string>>("check_run_ids_json", _Json) ?? new List<string>();
            release.Artifacts = row.Json<List<ReleaseArtifact>>("artifacts_json", _Json) ?? new List<ReleaseArtifact>();
            release.CreatedUtc = row.Utc("created_utc");
            release.LastUpdateUtc = row.Utc("last_update_utc");
            release.PublishedUtc = row.NullableUtc("published_utc");
            return release;
        }
    }
}
