namespace Armada.Core.Context
{
    /// <summary>
    /// The disclosure tier of a context chunk.
    ///
    /// A CORE chunk ships inline in every orchestrator session and every captain brief and is never
    /// retrieval-gated: its absence on a task could cause a leak, an unsafe or unauthorized outward
    /// action, an unproven success claim, a destructive remote or git operation, or dispatching
    /// Armada work that must be direct-edit. A LEAF chunk is fetched by topic, on demand, when the
    /// task needs it. Nothing is promoted to core by a model; core is an explicit owner allowlist.
    /// </summary>
    public enum ContextTierEnum
    {
        /// <summary>Fetched by topic on demand. The default for any source not on the core allowlist.</summary>
        Leaf,

        /// <summary>Always inline, never retrieved. Set only by the owner-approved tier configuration.</summary>
        Core
    }
}
