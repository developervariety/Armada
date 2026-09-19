namespace Armada.Core.Services
{
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// The deterministic dispatch-staleness rule: the configured policy, except a stale index that
    /// touches no indexable source always Proceeds. The <c>dispatch_staleness</c> decision tie-breaks
    /// over this verdict and cannot introduce a wait the rule would not, or lift a Block.
    /// </summary>
    public static class DispatchStalenessRules
    {
        /// <summary>Wait rank: Proceed is none, RefreshInline is a bounded wait, Block is a hard wait.</summary>
        public static int Rank(CodeIndexDispatchStalenessPolicyEnum policy)
        {
            return policy == CodeIndexDispatchStalenessPolicyEnum.Block ? 2
                : policy == CodeIndexDispatchStalenessPolicyEnum.RefreshInline ? 1
                : 0;
        }

        /// <summary>
        /// The rule verdict for one dispatch: Proceed when the stale index is not relevant, otherwise
        /// the configured policy. Update-in-progress is a wait only under Block.
        /// </summary>
        public static CodeIndexDispatchStalenessPolicyEnum RuleVerdict(
            CodeIndexDispatchStalenessPolicyEnum policy,
            CodeIndexStalenessRelevance? relevance,
            bool updateInProgress)
        {
            if (relevance != null && !relevance.IsRelevant && !relevance.DiffUnavailable && !updateInProgress)
                return CodeIndexDispatchStalenessPolicyEnum.Proceed;
            if (updateInProgress && policy != CodeIndexDispatchStalenessPolicyEnum.Block)
                return CodeIndexDispatchStalenessPolicyEnum.Proceed;
            return policy;
        }

        /// <summary>
        /// Combine the rule with a model choice. A Block rule always wins. A model choice that would
        /// wait more than the rule is discarded. Unavailable or unnamed choices keep the rule.
        /// </summary>
        public static CodeIndexDispatchStalenessPolicyEnum Combine(
            CodeIndexDispatchStalenessPolicyEnum rule,
            CodeIndexDispatchStalenessPolicyEnum? model)
        {
            if (rule == CodeIndexDispatchStalenessPolicyEnum.Block) return rule;
            if (!model.HasValue) return rule;
            return Rank(model.Value) <= Rank(rule) ? model.Value : rule;
        }
    }
}
