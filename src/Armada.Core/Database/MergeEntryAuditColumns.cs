namespace Armada.Core.Database
{
    using System;
    using System.Data;
    using Armada.Core.Models;

    /// <summary>
    /// The merge-entry audit columns every provider stores and reads back. The column-to-property
    /// contract is written here once; each provider supplies only how it converts its stored boolean
    /// and timestamp representations, so a provider reader cannot silently omit an audit value that its
    /// writes persist.
    /// </summary>
    internal static class MergeEntryAuditColumns
    {
        #region Internal-Methods

        /// <summary>
        /// Populate the nine audit properties of <paramref name="entry"/> from the current row.
        /// </summary>
        /// <param name="record">Reader positioned on a merge_entries row.</param>
        /// <param name="entry">Entry to populate.</param>
        /// <param name="readBool">Provider conversion of a stored nullable boolean.</param>
        /// <param name="readUtc">Provider conversion of a stored nullable UTC timestamp.</param>
        internal static void Read(IDataRecord record, MergeEntry entry, Func<object, bool?> readBool, Func<object, DateTime?> readUtc)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            if (readBool == null) throw new ArgumentNullException(nameof(readBool));
            if (readUtc == null) throw new ArgumentNullException(nameof(readUtc));

            entry.AuditLane = Text(record["audit_lane"]);
            entry.AuditConventionPassed = readBool(record["audit_convention_passed"]);
            entry.AuditConventionNotes = Text(record["audit_convention_notes"]);
            entry.AuditCriticalTrigger = Text(record["audit_critical_trigger"]);
            entry.AuditDeepPicked = readBool(record["audit_deep_picked"]);
            entry.AuditDeepCompletedUtc = readUtc(record["audit_deep_completed_utc"]);
            entry.AuditDeepVerdict = Text(record["audit_deep_verdict"]);
            entry.AuditDeepNotes = Text(record["audit_deep_notes"]);
            entry.AuditDeepRecommendedAction = Text(record["audit_deep_recommended_action"]);
        }

        #endregion

        #region Private-Methods

        private static string? Text(object value)
        {
            return value as string;
        }

        #endregion
    }
}
