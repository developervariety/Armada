namespace Armada.Core.Enums
{
    using System.Runtime.Serialization;
    using System.Text.Json.Serialization;

    /// <summary>
    /// How voyage dispatch reacts when a vessel's code index is stale (its indexed commit is behind
    /// the current default-branch commit) or a refresh is in progress.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum CodeIndexDispatchStalenessPolicyEnum
    {
        /// <summary>
        /// Dispatch proceeds against the current index and schedules a debounced background refresh.
        /// The context pack may trail the newest landed commit until the refresh lands. This removes
        /// the per-dispatch reindex wait and is the default.
        /// </summary>
        [EnumMember(Value = "Proceed")]
        Proceed,

        /// <summary>
        /// Dispatch first runs an incremental refresh (only changed files are re-embedded) bounded by a
        /// timeout, then proceeds. No manual reindex, at the cost of a short dispatch delay. On timeout
        /// or failure it falls back to Proceed.
        /// </summary>
        [EnumMember(Value = "RefreshInline")]
        RefreshInline,

        /// <summary>
        /// Dispatch is blocked until the index is Fresh, so generated context packs and search always
        /// reflect the newest landed commit. The operator refreshes and retries. The legacy behaviour.
        /// </summary>
        [EnumMember(Value = "Block")]
        Block
    }
}
