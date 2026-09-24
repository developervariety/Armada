namespace Armada.Core.Models
{
    using System;
    using Armada.Core;
    using Armada.Core.Enums;

    /// <summary>
    /// A single persisted token-usage record: the tokens a model consumed for one unit of work
    /// (a mission run, an Ask Armada turn, or a planning turn). Records are written at the point the
    /// work completes and aggregated by the token-usage summary APIs and dashboard charts.
    /// <para>
    /// <see cref="UsageRule"/> names the counting rule that produced the input counts. Under
    /// <see cref="TokenUsageRuleEnum.SeparateInputBuckets"/> input is stored as three buckets the same way for every
    /// runtime (<see cref="UncachedInputTokens"/>, <see cref="CacheReadInputTokens"/>,
    /// <see cref="CacheWriteInputTokens"/>); <see cref="InputTokens"/> is their sum, <see cref="CachedTokens"/> is the
    /// cache-read bucket and <see cref="TotalTokens"/> is input plus output. A record written before the buckets
    /// existed reads as <see cref="TokenUsageRuleEnum.Legacy"/>: it has no buckets, and its input count is whatever its
    /// runtime's provider called input. When a runtime does not report real usage the counts are estimated and
    /// <see cref="Estimated"/> is set so the UI can flag them.
    /// </para>
    /// </summary>
    public class TokenUsageRecord
    {
        #region Public-Members

        /// <summary>
        /// Unique identifier (tku_ prefix).
        /// </summary>
        public string Id
        {
            get => _Id;
            set => _Id = value;
        }

        /// <summary>
        /// Owning tenant identifier.
        /// </summary>
        public string? TenantId { get; set; } = null;

        /// <summary>
        /// User the work was performed on behalf of, when known.
        /// </summary>
        public string? UserId { get; set; } = null;

        /// <summary>
        /// Model identifier that consumed the tokens (for example "claude-sonnet-4" or "gpt-5").
        /// Empty or "unknown" when the runtime did not report a model.
        /// </summary>
        public string Model { get; set; } = string.Empty;

        /// <summary>
        /// Agent runtime that produced the usage (for example "claudecode", "codex", "mux").
        /// </summary>
        public string? Runtime { get; set; } = null;

        /// <summary>
        /// Work surface that generated the usage: "mission", "chat", or "planning".
        /// </summary>
        public string Source { get; set; } = string.Empty;

        /// <summary>
        /// Identifier of the originating unit of work (mission id, chat/planning session id), when known.
        /// </summary>
        public string? SourceId { get; set; } = null;

        /// <summary>
        /// Vessel the work ran against, when known.
        /// </summary>
        public string? VesselId { get; set; } = null;

        /// <summary>
        /// Captain that performed the work, when known.
        /// </summary>
        public string? CaptainId { get; set; } = null;

        /// <summary>
        /// Input (prompt) tokens consumed. Clamped to be non-negative. Under
        /// <see cref="TokenUsageRuleEnum.SeparateInputBuckets"/> it is the sum of the three input buckets; under
        /// <see cref="TokenUsageRuleEnum.Legacy"/> it is the runtime's own input figure.
        /// </summary>
        public long InputTokens
        {
            get => _InputTokens;
            set => _InputTokens = value < 0 ? 0 : value;
        }

        /// <summary>
        /// Output (completion) tokens produced. Clamped to be non-negative.
        /// </summary>
        public long OutputTokens
        {
            get => _OutputTokens;
            set => _OutputTokens = value < 0 ? 0 : value;
        }

        /// <summary>
        /// Cache-read tokens served from a prompt cache. Clamped to be non-negative.
        /// </summary>
        public long CachedTokens
        {
            get => _CachedTokens;
            set => _CachedTokens = value < 0 ? 0 : value;
        }

        /// <summary>
        /// Total tokens for the record. Clamped to be non-negative.
        /// </summary>
        public long TotalTokens
        {
            get => _TotalTokens;
            set => _TotalTokens = value < 0 ? 0 : value;
        }

        /// <summary>
        /// Counting rule that produced the input counts. A row stored before the rule existed reads as
        /// <see cref="TokenUsageRuleEnum.Legacy"/>.
        /// </summary>
        public TokenUsageRuleEnum UsageRule { get; set; } = TokenUsageRuleEnum.Legacy;

        /// <summary>
        /// Input tokens that were neither read from nor written to a prompt cache. Null under
        /// <see cref="TokenUsageRuleEnum.Legacy"/>.
        /// </summary>
        public long? UncachedInputTokens
        {
            get => _UncachedInputTokens;
            set => _UncachedInputTokens = ClampNullable(value);
        }

        /// <summary>
        /// Input tokens read from a prompt cache. Null under <see cref="TokenUsageRuleEnum.Legacy"/>.
        /// </summary>
        public long? CacheReadInputTokens
        {
            get => _CacheReadInputTokens;
            set => _CacheReadInputTokens = ClampNullable(value);
        }

        /// <summary>
        /// Input tokens written to a prompt cache. Null under <see cref="TokenUsageRuleEnum.Legacy"/>.
        /// </summary>
        public long? CacheWriteInputTokens
        {
            get => _CacheWriteInputTokens;
            set => _CacheWriteInputTokens = ClampNullable(value);
        }

        /// <summary>
        /// True when the counts are estimated (the runtime did not report real usage) rather than measured.
        /// </summary>
        public bool Estimated { get; set; } = false;

        /// <summary>
        /// Creation timestamp (UTC).
        /// </summary>
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

        #endregion

        #region Private-Members

        private string _Id = Constants.IdGenerator.GenerateKSortable(Constants.TokenUsageIdPrefix, 24);
        private long _InputTokens = 0;
        private long _OutputTokens = 0;
        private long _CachedTokens = 0;
        private long _TotalTokens = 0;
        private long? _UncachedInputTokens = null;
        private long? _CacheReadInputTokens = null;
        private long? _CacheWriteInputTokens = null;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public TokenUsageRecord()
        {
        }

        #endregion

        #region Private-Methods

        private static long? ClampNullable(long? value)
        {
            return value.HasValue && value.Value < 0 ? 0 : value;
        }

        #endregion
    }
}
