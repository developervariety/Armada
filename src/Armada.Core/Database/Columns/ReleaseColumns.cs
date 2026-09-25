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

        /// <summary>
        /// Bind every stored releases column, each in the form its provider stores it.
        /// </summary>
        /// <param name="parameters">Parameters of a command addressing the releases table.</param>
        /// <param name="release">Row to bind.</param>
        internal static void Write(StoredParameters parameters, Release release)
        {
            parameters
                .Text("id", release.Id)
                .Text("tenant_id", release.TenantId)
                .Text("user_id", release.UserId)
                .Text("vessel_id", release.VesselId)
                .Text("workflow_profile_id", release.WorkflowProfileId)
                .Text("title", release.Title)
                .Text("version", release.Version)
                .Text("tag_name", release.TagName)
                .Text("summary", release.Summary)
                .Text("notes", release.Notes)
                .Text("status", release.Status.ToString())
                .Text("voyage_ids_json", JsonSerializer.Serialize(release.VoyageIds ?? new List<string>(), _Json))
                .Text("mission_ids_json", JsonSerializer.Serialize(release.MissionIds ?? new List<string>(), _Json))
                .Text("check_run_ids_json", JsonSerializer.Serialize(release.CheckRunIds ?? new List<string>(), _Json))
                .Text("artifacts_json", JsonSerializer.Serialize(release.Artifacts ?? new List<ReleaseArtifact>(), _Json))
                .Utc("created_utc", release.CreatedUtc)
                .Utc("last_update_utc", release.LastUpdateUtc)
                .Utc("published_utc", release.PublishedUtc);
        }
    }
}
