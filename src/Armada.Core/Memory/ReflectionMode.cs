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

        /// <summary>
        /// v2-F2 persona-curate mode: aggregate cross-vessel mission-pattern evidence for one
        /// persona role and propose updates to the persona-&lt;name&gt;-learned playbook. Cross-
        /// vessel scope; HabitPatternMiner produces the brief evidence bundle.
        /// </summary>
        PersonaCurate = 4,

        /// <summary>
        /// v2-F2 captain-curate mode: aggregate cross-vessel mission-pattern evidence for one
        /// captain id and propose updates to the captain-&lt;sanitized-id&gt;-learned playbook.
        /// Cross-vessel scope; the captain learned playbook is lazy-created on first accept.
        /// </summary>
        CaptainCurate = 5,

        /// <summary>
        /// v2-F3 fleet-curate mode: aggregate mission-pattern evidence across ALL active vessels
        /// in a fleet AND read each vessel's vessel-&lt;repo&gt;-learned playbook for promotion
        /// candidates. Propose updates to the fleet-&lt;id&gt;-learned playbook plus a JSON sidecar
        /// describing per-vessel disableFromVessels ripples (vessel-scope notes that should be
        /// retired because the same fact now lives at fleet scope). Fleet scope; the fleet
        /// learned playbook is lazy-created on first accept. Strict accept-time validation:
        /// promotion gates (>=2 vessels, >=3 missions) and vessel-fleet conflict detection BLOCK.
        /// </summary>
        FleetCurate = 6,
    }
}
