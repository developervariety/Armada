namespace Armada.Core.Models
{
    using System;
    using Armada.Core.Enums;

    /// <summary>
    /// Authoritative token usage reported by an agent runtime.
    /// </summary>
    /// <remarks>
    /// A runtime builds every sample with <see cref="FromUncachedInput"/> or <see cref="FromInclusiveInput"/>, which
    /// split the provider's input into three buckets under <see cref="TokenUsageRuleEnum.SeparateInputBuckets"/>:
    /// <see cref="UncachedInputTokens"/>, <see cref="CacheReadTokens"/> and <see cref="CacheWriteTokens"/>, with
    /// <see cref="InputTokens"/> their sum. A sample with no rule (a payload stored before the buckets existed) reads as
    /// <see cref="TokenUsageRuleEnum.Legacy"/>: its input count keeps the runtime's own meaning.
    /// </remarks>
    public class RuntimeTokenUsage
    {
        /// <summary>
        /// Runtime that emitted the usage record.
        /// </summary>
        public string Runtime { get; set; } = "";

        /// <summary>
        /// Model identifier used for the request.
        /// </summary>
        public string Model { get; set; } = "";

        /// <summary>
        /// Authoritative telemetry source.
        /// </summary>
        public string Source { get; set; } = "";

        /// <summary>
        /// Counting rule that produced the input counts.
        /// </summary>
        public TokenUsageRuleEnum UsageRule { get; set; } = TokenUsageRuleEnum.Legacy;

        /// <summary>
        /// All input tokens. Under <see cref="TokenUsageRuleEnum.SeparateInputBuckets"/> this is uncached plus
        /// cache-read plus cache-write input; under <see cref="TokenUsageRuleEnum.Legacy"/> it is the provider's own
        /// input figure.
        /// </summary>
        public long InputTokens { get; set; }

        /// <summary>
        /// Input tokens that were neither read from nor written to a prompt cache. Zero under
        /// <see cref="TokenUsageRuleEnum.Legacy"/>.
        /// </summary>
        public long UncachedInputTokens { get; set; }

        /// <summary>
        /// Output tokens reported by the provider.
        /// </summary>
        public long OutputTokens { get; set; }

        /// <summary>
        /// Reasoning tokens reported by the provider.
        /// </summary>
        public long ReasoningTokens { get; set; }

        /// <summary>
        /// Input tokens read from a prompt cache.
        /// </summary>
        public long CacheReadTokens { get; set; }

        /// <summary>
        /// Input tokens written to a prompt cache.
        /// </summary>
        public long CacheWriteTokens { get; set; }

        /// <summary>
        /// Total tokens reported directly by the provider, when available.
        /// </summary>
        public long? ProviderTotalTokens { get; set; }

        /// <summary>
        /// Build a sample from a provider that reports uncached input separately from cache reads and cache writes
        /// (for example Anthropic's <c>input_tokens</c>, <c>cache_read_input_tokens</c> and
        /// <c>cache_creation_input_tokens</c>).
        /// </summary>
        /// <param name="source">Telemetry source.</param>
        /// <param name="uncachedInput">Input tokens that did not touch the cache.</param>
        /// <param name="cacheReadInput">Input tokens read from the cache.</param>
        /// <param name="cacheWriteInput">Input tokens written to the cache.</param>
        /// <param name="output">Output tokens.</param>
        /// <returns>A sample under <see cref="TokenUsageRuleEnum.SeparateInputBuckets"/>.</returns>
        public static RuntimeTokenUsage FromUncachedInput(string source, long? uncachedInput, long? cacheReadInput, long? cacheWriteInput, long? output)
        {
            long uncached = NonNegative(uncachedInput);
            long read = NonNegative(cacheReadInput);
            long write = NonNegative(cacheWriteInput);
            return new RuntimeTokenUsage
            {
                Source = source ?? "",
                UsageRule = TokenUsageRuleEnum.SeparateInputBuckets,
                UncachedInputTokens = uncached,
                CacheReadTokens = read,
                CacheWriteTokens = write,
                InputTokens = Add(Add(uncached, read), write),
                OutputTokens = NonNegative(output)
            };
        }

        /// <summary>
        /// Build a sample from a provider whose input count already includes the cached input (for example OpenAI's
        /// <c>input_tokens</c> with <c>cached_input_tokens</c>, or Gemini's prompt count with its <c>cached</c> count).
        /// The uncached bucket is the input count less the cache-read and cache-write counts, never below zero.
        /// </summary>
        /// <param name="source">Telemetry source.</param>
        /// <param name="inclusiveInput">All input tokens, cached input included.</param>
        /// <param name="cacheReadInput">Input tokens read from the cache.</param>
        /// <param name="cacheWriteInput">Input tokens written to the cache.</param>
        /// <param name="output">Output tokens.</param>
        /// <returns>A sample under <see cref="TokenUsageRuleEnum.SeparateInputBuckets"/>.</returns>
        public static RuntimeTokenUsage FromInclusiveInput(string source, long? inclusiveInput, long? cacheReadInput, long? cacheWriteInput, long? output)
        {
            long read = NonNegative(cacheReadInput);
            long write = NonNegative(cacheWriteInput);
            long uncached = Math.Max(0, NonNegative(inclusiveInput) - read - write);
            return FromUncachedInput(source, uncached, read, write, output);
        }

        private static long NonNegative(long? value)
        {
            return Math.Max(0, value ?? 0);
        }

        private static long Add(long left, long right)
        {
            return right > 0 && left > Int64.MaxValue - right ? Int64.MaxValue : left + right;
        }
    }
}
