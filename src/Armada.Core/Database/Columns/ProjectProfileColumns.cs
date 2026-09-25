namespace Armada.Core.Database
{
    using System.Collections.Generic;
    using System.Data;
    using System.Text.Json;
    using Armada.Core.Models;

    /// <summary>
    /// The project_profiles row-to-model contract, shared by every provider.
    /// </summary>
    internal static class ProjectProfileColumns
    {
        private static readonly JsonSerializerOptions _Json = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        /// <summary>
        /// Read a project_profiles row. An unrecognised stored scope reads as the model's default scope.
        /// </summary>
        /// <param name="record">Reader positioned on a project_profiles row.</param>
        /// <param name="values">Provider value converter.</param>
        /// <returns>The project profile.</returns>
        internal static ProjectProfile Read(IDataRecord record, StoredValueConverter values)
        {
            StoredRow row = new StoredRow(record, values, "ProjectProfile");
            ProjectProfile profile = new ProjectProfile
            {
                Id = row.Text("id"),
                TenantId = row.NullableText("tenant_id"),
                UserId = row.NullableText("user_id"),
                Name = row.Text("name"),
                Description = row.NullableText("description"),
                FleetId = row.NullableText("fleet_id"),
                VesselId = row.NullableText("vessel_id"),
                IsDefault = row.Bool("is_default"),
                Active = row.Bool("active"),
                DefaultPipelineId = row.NullableText("default_pipeline_id"),
                WorkflowProfileId = row.NullableText("workflow_profile_id"),
                CreatedUtc = row.Utc("created_utc"),
                LastUpdateUtc = row.Utc("last_update_utc")
            };

            profile.Scope = row.EnumOrFallback("scope", profile.Scope);
            profile.PersonaOverrides = row.Json<List<PersonaOverride>>("persona_overrides_json", _Json) ?? new List<PersonaOverride>();
            profile.Skills = row.Json<List<string>>("skills_json", _Json) ?? new List<string>();
            profile.AuthorizationPolicy = row.NullableText("authorization_policy");
            return profile;
        }

        /// <summary>
        /// Bind every stored project_profiles column, each in the form its provider stores it.
        /// </summary>
        /// <param name="parameters">Parameters of a command addressing the project_profiles table.</param>
        /// <param name="profile">Row to bind.</param>
        internal static void Write(StoredParameters parameters, ProjectProfile profile)
        {
            parameters
                .Text("id", profile.Id)
                .Text("tenant_id", profile.TenantId)
                .Text("user_id", profile.UserId)
                .Text("name", profile.Name)
                .Text("description", profile.Description)
                .Text("fleet_id", profile.FleetId)
                .Text("vessel_id", profile.VesselId)
                .Bool("is_default", profile.IsDefault)
                .Bool("active", profile.Active)
                .Text("default_pipeline_id", profile.DefaultPipelineId)
                .Text("workflow_profile_id", profile.WorkflowProfileId)
                .Utc("created_utc", profile.CreatedUtc)
                .Utc("last_update_utc", profile.LastUpdateUtc)
                .Text("scope", profile.Scope.ToString())
                .Text("persona_overrides_json", Serialize(profile.PersonaOverrides))
                .Text("skills_json", Serialize(profile.Skills))
                .Text("authorization_policy", profile.AuthorizationPolicy);
        }

        /// <summary>
        /// A list column is stored as JSON, and an absent one as its empty form.
        /// </summary>
        private static string Serialize<T>(T value)
        {
            return JsonSerializer.Serialize(value ?? System.Activator.CreateInstance<T>(), _Json);
        }
    }
}
