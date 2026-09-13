namespace Armada.Core.Database
{
    using System;
    using System.Collections.Generic;
    using System.Data;
    using System.Data.Common;
    using System.Text.Json;
    using Armada.Core.Models;

    /// <summary>Storage for preview configuration; actual landing gates remain in their services.</summary>
    internal static class VesselPreviewPersistence
    {
        internal static void AddParameters(DbCommand command, Vessel vessel)
        {
            Add(command, "require_passing_checks_to_land", vessel.RequirePassingChecksToLand, DbType.Boolean);
            Add(command, "protected_branch_patterns", JsonSerializer.Serialize(vessel.ProtectedBranchPatterns ?? new List<string>()), DbType.String);
            Add(command, "release_branch_prefix", vessel.ReleaseBranchPrefix ?? "release/", DbType.String);
            Add(command, "hotfix_branch_prefix", vessel.HotfixBranchPrefix ?? "hotfix/", DbType.String);
            Add(command, "require_pull_request_for_protected_branches", vessel.RequirePullRequestForProtectedBranches, DbType.Boolean);
            Add(command, "require_merge_queue_for_release_branches", vessel.RequireMergeQueueForReleaseBranches, DbType.Boolean);
        }

        internal static void Read(DbDataReader reader, Vessel vessel)
        {
            vessel.RequirePassingChecksToLand = Convert.ToBoolean(reader["require_passing_checks_to_land"]);
            vessel.ProtectedBranchPatterns = JsonSerializer.Deserialize<List<string>>((string)reader["protected_branch_patterns"])
                ?? throw new InvalidOperationException("Stored protected branch patterns must be a JSON array.");
            vessel.ReleaseBranchPrefix = (string)reader["release_branch_prefix"];
            vessel.HotfixBranchPrefix = (string)reader["hotfix_branch_prefix"];
            vessel.RequirePullRequestForProtectedBranches = Convert.ToBoolean(reader["require_pull_request_for_protected_branches"]);
            vessel.RequireMergeQueueForReleaseBranches = Convert.ToBoolean(reader["require_merge_queue_for_release_branches"]);
        }

        private static void Add(DbCommand command, string name, object value, DbType type)
        {
            DbParameter parameter = command.CreateParameter();
            parameter.ParameterName = "@" + name;
            parameter.Value = value;
            parameter.DbType = type;
            command.Parameters.Add(parameter);
        }
    }
}
