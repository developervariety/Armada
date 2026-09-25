namespace Armada.Core.Database
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using Armada.Core.Models;

    /// <summary>
    /// Provider-neutral helpers for memory storage: the guarded update and tag handling.
    /// </summary>
    internal static class MemoryRows
    {
        /// <summary>
        /// Guarded update. Ownership (tenant) and creation time never change through an update.
        /// </summary>
        internal const string UpdateSql =
            "UPDATE memories SET user_id = @user_id, scope = @scope, type = @type, topic = @topic, memory_key = @memory_key, " +
            "summary = @summary, content = @content, salience = @salience, version = @version, source_kind = @source_kind, " +
            "source_voyage_id = @source_voyage_id, source_mission_id = @source_mission_id, source_vessel_id = @source_vessel_id, " +
            "source_detail = @source_detail, vessel_id = @vessel_id, last_update_utc = @last_update_utc " +
            "WHERE id = @id AND tenant_id = @tenant_id AND version = @expected_version;";

        /// <summary>
        /// The distinct, non-empty tags to persist, in stable order.
        /// </summary>
        internal static List<string> TagsToWrite(Memory memory)
        {
            return memory.Tags
                .Where(tag => !String.IsNullOrWhiteSpace(tag))
                .Select(tag => tag.Trim())
                .Distinct(StringComparer.Ordinal)
                .OrderBy(tag => tag, StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>
        /// Attach grouped tag rows to their records. Tags are ordered ordinally so every provider
        /// returns the same order regardless of collation.
        /// </summary>
        internal static void AttachTags(List<Memory> memories, Dictionary<string, List<string>> tags)
        {
            foreach (Memory memory in memories)
            {
                if (tags.TryGetValue(memory.Id, out List<string>? found))
                    memory.Tags = found.OrderBy(tag => tag, StringComparer.Ordinal).ToList();
                else
                    memory.Tags = new List<string>();
            }
        }

        /// <summary>
        /// Tag a timestamp as UTC without shifting it.
        /// </summary>
        internal static DateTime AsUtc(DateTime value)
        {
            if (value.Kind == DateTimeKind.Utc) return value;
            if (value.Kind == DateTimeKind.Local) return value.ToUniversalTime();
            return DateTime.SpecifyKind(value, DateTimeKind.Utc);
        }
    }
}
