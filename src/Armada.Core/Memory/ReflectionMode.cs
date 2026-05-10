namespace Armada.Core.Memory
{
    /// <summary>
    /// Mode of a MemoryConsolidator reflection mission. Drives brief assembly,
    /// accept-time validation, and event payload semantics.
    /// </summary>
    public enum ReflectionMode
    {
        /// <summary>
        /// v1 behavior: distill terminal-mission evidence into the learned-facts playbook.
        /// New facts may be added; the full evidence bundle is included in the brief.
        /// </summary>
        Consolidate = 0,

        /// <summary>
        /// v2-F4 reorganize-only mode: restructure (group, merge, drop stale, reorder, reword)
        /// without adding facts. Brief skips the evidence bundle and ships just the current
        /// playbook, recent commit subjects, and recently-rejected reorganize proposals.
        /// </summary>
        Reorganize = 1,

        /// <summary>
        /// v2-F4 combined mode: consolidate new evidence AND reorganize structurally in one
        /// mission. Brief includes the v1 evidence bundle plus reorganize instructions.
        /// </summary>
        ConsolidateAndReorganize = 2,

        /// <summary>
        /// v2-F1 pack-curate mode: mine completed-mission captain logs for the four pack-usage
        /// buckets (prestaged-Read / ignored / grep-discovered / Edited) and propose deltas
        /// to the vessel_pack_hints table. Brief is JSON-output-oriented (NOT markdown).
        /// </summary>
        PackCurate = 3,
    }
}
