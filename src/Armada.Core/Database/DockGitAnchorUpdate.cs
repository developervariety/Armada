namespace Armada.Core.Database
{
    using System;
    using System.Collections.Generic;
    using System.Data.Common;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>Conditional evidence writes that cannot restore stale dock ownership.</summary>
    internal static class DockGitAnchorUpdate
    {
        internal static async Task<bool> CompleteAsync(DbConnection connection, DatabaseTypeEnum provider,
            string dockId, string captainId, DockGitAnchorSnapshot expected, DockGitAnchorSnapshot completed,
            CancellationToken token)
        {
            if (String.IsNullOrEmpty(dockId)) throw new ArgumentNullException(nameof(dockId));
            if (String.IsNullOrEmpty(captainId)) throw new ArgumentNullException(nameof(captainId));
            if (expected == null) throw new ArgumentNullException(nameof(expected));
            if (completed == null) throw new ArgumentNullException(nameof(completed));
            string expectedJson = DockGitAnchorPersistence.Serialize(expected);
            string completedJson = DockGitAnchorPersistence.Serialize(completed);
            if (expected.StoredJson != null)
            {
                DockGitAnchorSnapshot? stored = DockGitAnchorPersistence.Read(expected.StoredJson, dockId, expected.VesselId);
                if (stored == null || DockGitAnchorPersistence.Serialize(stored) != expectedJson)
                    throw new InvalidOperationException("Loaded dock anchor seed was modified.");
            }
            if (expected.State != DockGitAnchorStateEnum.Seeded || completed.State == DockGitAnchorStateEnum.Seeded
                || expected.DockId != dockId || completed.DockId != dockId || expected.MissionId != completed.MissionId
                || expected.VesselId != completed.VesselId || expected.ProvisionedCommit != completed.ProvisionedCommit
                || expected.ProvisionedUtc != completed.ProvisionedUtc)
                throw new InvalidOperationException("Dock anchor completion cannot change provisioning identity.");
            using (DbCommand command = connection.CreateCommand())
            {
                command.CommandText = "UPDATE docks SET git_anchors_json = @completed WHERE id = @id "
                    + "AND captain_id = @captain AND vessel_id = @vessel AND active = @active AND "
                    + BinaryEquals("git_anchors_json", "@expected", provider) + ";";
                StoredValueBinder.Value(command, "@id", dockId);
                StoredValueBinder.Value(command, "@captain", captainId);
                StoredValueBinder.Value(command, "@vessel", expected.VesselId);
                StoredValueBinder.For(provider).For(command, "docks").Bool("@active", "active", true);
                StoredValueBinder.Value(command, "@expected", expected.StoredJson ?? expectedJson);
                StoredValueBinder.Value(command, "@completed", completedJson);
                await connection.OpenAsync(token).ConfigureAwait(false);
                return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) == 1;
            }
        }

        internal static string PreserveOnSameOwnerSql(DatabaseTypeEnum provider)
        {
            List<string> matches = new List<string>();
            foreach (string column in new[] { "tenant_id", "user_id", "vessel_id", "captain_id", "worktree_path", "branch_name" })
                matches.Add(BinaryEquals(column, "@" + column, provider));
            matches.Add("active = @active");
            return "git_anchors_json = CASE WHEN " + String.Join(" AND ", matches) + " THEN git_anchors_json ELSE NULL END";
        }

        private static string BinaryEquals(string column, string parameter, DatabaseTypeEnum provider)
        {
            string left = "COALESCE(" + column + ", '')";
            string right = "COALESCE(" + parameter + ", '')";
            return provider switch
            {
                DatabaseTypeEnum.Sqlite => left + " = " + right + " COLLATE BINARY",
                DatabaseTypeEnum.Postgresql => "convert_to(" + left + ", 'UTF8') = convert_to(" + right + ", 'UTF8')",
                DatabaseTypeEnum.Mysql => "BINARY " + left + " = BINARY " + right,
                DatabaseTypeEnum.SqlServer => "CONVERT(varbinary(max), " + left + ") = CONVERT(varbinary(max), " + right + ")",
                _ => throw new NotSupportedException()
            };
        }
    }
}
