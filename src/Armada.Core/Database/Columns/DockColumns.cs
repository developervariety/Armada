namespace Armada.Core.Database
{
    using System;
    using System.Data;
    using Armada.Core.Models;

    /// <summary>
    /// The docks row-to-model contract, shared by every provider. The git anchor snapshot is optional evidence:
    /// the anchor rule reads one that is oversized, invalid or recorded for another dock as absent.
    /// </summary>
    internal static class DockColumns
    {
        /// <summary>
        /// Read a docks row.
        /// </summary>
        /// <param name="record">Reader positioned on a docks row.</param>
        /// <param name="values">Provider value converter.</param>
        /// <returns>The dock.</returns>
        internal static Dock Read(IDataRecord record, StoredValueConverter values)
        {
            StoredRow row = new StoredRow(record, values, "Dock");
            Dock dock = new Dock();
            string id = row.Text("id");
            string vesselId = row.Text("vessel_id");
            dock.GitAnchorsSnapshot = DockGitAnchorPersistence.Read((object?)row.TextOrNull("git_anchors_json") ?? DBNull.Value, id, vesselId);
            dock.Id = id;
            dock.TenantId = row.NullableText("tenant_id");
            dock.UserId = row.NullableText("user_id");
            dock.VesselId = vesselId;
            dock.CaptainId = row.NullableText("captain_id");
            dock.WorktreePath = row.NullableText("worktree_path");
            dock.BranchName = row.NullableText("branch_name");
            dock.Active = row.Bool("active");
            dock.CreatedUtc = row.Utc("created_utc");
            dock.LastUpdateUtc = row.Utc("last_update_utc");
            return dock;
        }
    }
}
