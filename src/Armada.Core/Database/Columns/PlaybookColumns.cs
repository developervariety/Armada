namespace Armada.Core.Database
{
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
    }
}
