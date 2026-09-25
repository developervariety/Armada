namespace Armada.Core.Database
{
    using System;
    using System.Data;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// The playbooks and mission_playbook_snapshots row-to-model contracts, shared by every provider.
    /// </summary>
    internal static class PlaybookColumns
    {
        /// <summary>
        /// Read a playbooks row.
        /// </summary>
        /// <param name="record">Reader positioned on a playbooks row.</param>
        /// <param name="values">Provider value converter.</param>
        /// <returns>The playbook.</returns>
        internal static Playbook Read(IDataRecord record, StoredValueConverter values)
        {
            StoredRow row = new StoredRow(record, values, "Playbook");
            return new Playbook
            {
                Id = row.Text("id"),
                TenantId = row.NullableText("tenant_id"),
                UserId = row.NullableText("user_id"),
                FileName = row.Text("file_name"),
                Description = row.NullableText("description"),
                Content = row.Text("content"),
                Active = row.Bool("active"),
                CreatedUtc = row.Utc("created_utc"),
                LastUpdateUtc = row.Utc("last_update_utc")
            };
        }

        /// <summary>
        /// Bind every stored playbooks column, each in the form its provider stores it.
        /// </summary>
        /// <param name="parameters">Parameters of a command addressing the playbooks table.</param>
        /// <param name="playbook">Row to bind.</param>
        internal static void Write(StoredParameters parameters, Playbook playbook)
        {
            parameters
                .Text("id", playbook.Id)
                .Text("tenant_id", playbook.TenantId)
                .Text("user_id", playbook.UserId)
                .Text("file_name", playbook.FileName)
                .Text("description", playbook.Description)
                .Text("content", playbook.Content)
                .Bool("active", playbook.Active)
                .Utc("created_utc", playbook.CreatedUtc)
                .Utc("last_update_utc", playbook.LastUpdateUtc);
        }

        /// <summary>
        /// Read a mission_playbook_snapshots row. An unrecognised stored delivery mode reads as inline full content.
        /// </summary>
        /// <param name="record">Reader positioned on a mission_playbook_snapshots row.</param>
        /// <param name="values">Provider value converter.</param>
        /// <returns>The snapshot.</returns>
        internal static MissionPlaybookSnapshot ReadSnapshot(IDataRecord record, StoredValueConverter values)
        {
            StoredRow row = new StoredRow(record, values, "MissionPlaybookSnapshot");
            return new MissionPlaybookSnapshot
            {
                PlaybookId = row.NullableText("playbook_id"),
                FileName = row.Text("file_name"),
                Description = row.NullableText("description"),
                Content = row.Text("content"),
                DeliveryMode = row.EnumOrFallback("delivery_mode", PlaybookDeliveryModeEnum.InlineFullContent),
                ResolvedPath = row.NullableText("resolved_path"),
                WorktreeRelativePath = row.NullableText("worktree_relative_path"),
                SourceLastUpdateUtc = row.NullableUtc("source_last_update_utc")
            };
        }

        /// <summary>
        /// Bind the stored mission_playbook_snapshots columns a snapshot holds, each in the form its provider stores
        /// it. The mission and selection order are bound by the statement that writes them.
        /// </summary>
        /// <param name="parameters">Parameters of a command addressing the mission_playbook_snapshots table.</param>
        /// <param name="snapshot">Snapshot to bind.</param>
        internal static void WriteSnapshot(StoredParameters parameters, MissionPlaybookSnapshot snapshot)
        {
            parameters
                .Text("playbook_id", snapshot.PlaybookId)
                .Text("file_name", snapshot.FileName ?? String.Empty)
                .Text("description", snapshot.Description)
                .Text("content", snapshot.Content ?? String.Empty)
                .Text("delivery_mode", snapshot.DeliveryMode.ToString())
                .Text("resolved_path", snapshot.ResolvedPath)
                .Text("worktree_relative_path", snapshot.WorktreeRelativePath)
                .Utc("source_last_update_utc", snapshot.SourceLastUpdateUtc);
        }
    }
}
