namespace Armada.Core.Enums
{
    /// <summary>
    /// Where a prior-art candidate was found: the four surfaces the deterministic retriever searches
    /// for work that already exists. Every candidate carries exactly one, so a reader knows whether the
    /// overlap is on the default branch, on a branch never landed, on a preserved or recovery ref, or
    /// in another open objective's scope.
    /// </summary>
    public enum PriorArtWhereEnum
    {
        /// <summary>The candidate is on the default branch at the target tip — work already landed.</summary>
        Landed,

        /// <summary>The candidate is on a mission branch that exists in the vessel repository but never landed.</summary>
        UnlandedBranch,

        /// <summary>The candidate is on a preserved or recovery ref (armada-preserved or recover) tip.</summary>
        RecoverRef,

        /// <summary>The candidate is another open objective whose identifiers overlap this one's.</summary>
        OpenObjective
    }
}
