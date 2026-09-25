namespace Armada.Core.Database
{
    using System.Collections.Generic;
    using System.Data;
    using System.Text.Json;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// The vessels row-to-model contract, shared by every provider. Every provider's schema carries every vessel
    /// column and every vessel read selects the whole row, so each column is read as present. The landing mode and
    /// branch cleanup policy tolerate names outside the model and read those as unset.
    /// </summary>
    internal static class VesselColumns
    {
        // Vessel JSON lists are written with the serializer defaults, so they are read with them.
        private static readonly JsonSerializerOptions _Json = new JsonSerializerOptions();

        /// <summary>
        /// Read a vessels row.
        /// </summary>
        /// <param name="record">Reader positioned on a vessels row.</param>
        /// <param name="values">Provider value converter.</param>
        /// <returns>The vessel.</returns>
        internal static Vessel Read(IDataRecord record, StoredValueConverter values)
        {
            StoredRow row = new StoredRow(record, values, "Vessel");
            Vessel vessel = new Vessel();
            vessel.Id = row.Text("id");
            vessel.TenantId = row.NullableText("tenant_id");
            vessel.UserId = row.NullableText("user_id");
            vessel.FleetId = row.NullableText("fleet_id");
            vessel.Name = row.Text("name");
            vessel.RepoUrl = row.NullableText("repo_url");
            vessel.LocalPath = row.NullableText("local_path");
            vessel.WorkingDirectory = row.NullableText("working_directory");
            vessel.GitHubTokenOverride = row.NullableText("github_token_override");
            vessel.DefaultBranch = row.Text("default_branch");
            vessel.ProjectContext = row.NullableText("project_context");
            vessel.StyleGuide = row.NullableText("style_guide");
            vessel.EnableModelContext = row.Bool("enable_model_context");
            vessel.ModelContext = row.NullableText("model_context");
            vessel.LandingMode = row.EnumOrNull<LandingModeEnum>("landing_mode", false);
            vessel.BranchCleanupPolicy = row.EnumOrNull<BranchCleanupPolicyEnum>("branch_cleanup_policy", false);
            vessel.ArchitectMaxMissionsPerVoyage = row.NullableInt("architect_max_missions_per_voyage");
            vessel.AllowConcurrentMissions = row.Bool("allow_concurrent_missions");
            vessel.RequirePassingChecksToLand = row.Bool("require_passing_checks_to_land");
            vessel.ProtectedBranchPatterns = row.Json<List<string>>("protected_branch_patterns", _Json) ?? new List<string>();
            vessel.ReleaseBranchPrefix = row.Text("release_branch_prefix");
            vessel.HotfixBranchPrefix = row.Text("hotfix_branch_prefix");
            vessel.RequirePullRequestForProtectedBranches = row.Bool("require_pull_request_for_protected_branches");
            vessel.RequireMergeQueueForReleaseBranches = row.Bool("require_merge_queue_for_release_branches");
            vessel.DefaultPipelineId = row.NullableText("default_pipeline_id");
            vessel.ProtectedPaths = ProtectedPaths(row);
            vessel.AutoLandPredicate = row.TextOrNull("auto_land_predicate");
            vessel.DefaultPlaybooks = row.TextOrNull("default_playbooks");
            vessel.SiblingRepos = row.TextOrNull("sibling_repos");
            vessel.AutoLandCalibrationLandedCount = row.Int("auto_land_calibration_landed_count");
            vessel.SecretScanEnabled = row.Bool("secret_scan_enabled");
            vessel.ProtectedPathPatterns = row.Json<List<string>>("protected_path_patterns_json", _Json) ?? new List<string>();
            vessel.PrivateIdentifierDenylist = row.Json<List<string>>("private_identifier_denylist_json", _Json) ?? new List<string>();
            vessel.Active = row.Bool("active");
            vessel.CreatedUtc = row.Utc("created_utc");
            vessel.LastUpdateUtc = row.Utc("last_update_utc");
            return vessel;
        }

        /// <summary>
        /// Bind every stored vessels column, each in the form its provider stores it. An empty protected-path list is
        /// stored as none, and the branch-pattern, protected-pattern and denylist lists are stored as JSON arrays.
        /// </summary>
        /// <param name="parameters">Parameters of a command addressing the vessels table.</param>
        /// <param name="vessel">Vessel to bind.</param>
        internal static void Write(StoredParameters parameters, Vessel vessel)
        {
            parameters
                .Text("id", vessel.Id)
                .Text("tenant_id", vessel.TenantId)
                .Text("user_id", vessel.UserId)
                .Text("fleet_id", vessel.FleetId)
                .Text("name", vessel.Name)
                .Text("repo_url", vessel.RepoUrl)
                .Text("local_path", vessel.LocalPath)
                .Text("working_directory", vessel.WorkingDirectory)
                .Text("github_token_override", vessel.GitHubTokenOverride)
                .Text("project_context", vessel.ProjectContext)
                .Text("style_guide", vessel.StyleGuide)
                .Bool("enable_model_context", vessel.EnableModelContext)
                .Text("model_context", vessel.ModelContext)
                .Text("landing_mode", vessel.LandingMode?.ToString())
                .Text("branch_cleanup_policy", vessel.BranchCleanupPolicy?.ToString())
                .Bool("allow_concurrent_missions", vessel.AllowConcurrentMissions)
                .Bool("require_passing_checks_to_land", vessel.RequirePassingChecksToLand)
                .Text("protected_branch_patterns", JsonSerializer.Serialize(vessel.ProtectedBranchPatterns ?? new List<string>()))
                .Text("release_branch_prefix", vessel.ReleaseBranchPrefix ?? "release/")
                .Text("hotfix_branch_prefix", vessel.HotfixBranchPrefix ?? "hotfix/")
                .Bool("require_pull_request_for_protected_branches", vessel.RequirePullRequestForProtectedBranches)
                .Bool("require_merge_queue_for_release_branches", vessel.RequireMergeQueueForReleaseBranches)
                .Text("default_pipeline_id", vessel.DefaultPipelineId)
                .Text("protected_paths", vessel.ProtectedPaths != null && vessel.ProtectedPaths.Count > 0 ? JsonSerializer.Serialize(vessel.ProtectedPaths) : null)
                .Text("auto_land_predicate", vessel.AutoLandPredicate)
                .Int("auto_land_calibration_landed_count", vessel.AutoLandCalibrationLandedCount)
                .Text("default_playbooks", vessel.DefaultPlaybooks)
                .Text("sibling_repos", vessel.SiblingRepos)
                .Int("architect_max_missions_per_voyage", vessel.ArchitectMaxMissionsPerVoyage)
                .Text("default_branch", vessel.DefaultBranch)
                .Bool("active", vessel.Active)
                .Bool("secret_scan_enabled", vessel.SecretScanEnabled)
                .Text("protected_path_patterns_json", JsonSerializer.Serialize(vessel.ProtectedPathPatterns ?? new List<string>()))
                .Text("private_identifier_denylist_json", JsonSerializer.Serialize(vessel.PrivateIdentifierDenylist ?? new List<string>()))
                .Utc("created_utc", vessel.CreatedUtc)
                .Utc("last_update_utc", vessel.LastUpdateUtc);
        }

        /// <summary>
        /// Protected paths are stored as a JSON list; an empty list reads as none, as it is written.
        /// </summary>
        private static List<string>? ProtectedPaths(StoredRow row)
        {
            List<string>? paths = row.Json<List<string>>("protected_paths", _Json);
            return paths != null && paths.Count > 0 ? paths : null;
        }
    }
}
