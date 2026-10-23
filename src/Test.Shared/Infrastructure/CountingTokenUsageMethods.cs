namespace Test.Shared.Infrastructure
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Models;

    /// <summary>
    /// Passes every call to a real token-usage method set and counts the reads, so a test can show how many times a
    /// caller goes to the database for one answer. The real set supplies every result.
    /// </summary>
    public sealed class CountingTokenUsageMethods : ITokenUsageMethods
    {
        #region Private-Members

        private readonly ITokenUsageMethods _Inner;

        #endregion

        #region Public-Members

        /// <summary>
        /// Number of read calls made through this set.
        /// </summary>
        public int Reads { get; private set; }

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="inner">Real method set every call passes to.</param>
        public CountingTokenUsageMethods(ITokenUsageMethods inner)
        {
            _Inner = inner ?? throw new ArgumentNullException(nameof(inner));
        }

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        public Task<TokenUsageRecord> CreateAsync(TokenUsageRecord record, CancellationToken token = default)
        {
            return _Inner.CreateAsync(record, token);
        }

        /// <inheritdoc />
        public Task<TokenUsageRecord?> ReadAsync(string id, TokenUsageQuery? query = null, CancellationToken token = default)
        {
            Reads++;
            return _Inner.ReadAsync(id, query, token);
        }

        /// <inheritdoc />
        public Task<EnumerationResult<TokenUsageRecord>> EnumerateAsync(TokenUsageQuery query, CancellationToken token = default)
        {
            Reads++;
            return _Inner.EnumerateAsync(query, token);
        }

        /// <inheritdoc />
        public Task<List<TokenUsageRecord>> EnumerateForSummaryAsync(TokenUsageQuery query, CancellationToken token = default)
        {
            Reads++;
            return _Inner.EnumerateForSummaryAsync(query, token);
        }

        /// <inheritdoc />
        public Task<List<TokenUsageRecord>> EnumerateBySourceIdsAsync(TokenUsageQuery scope, string source, IReadOnlyCollection<string> sourceIds, CancellationToken token = default)
        {
            Reads++;
            return _Inner.EnumerateBySourceIdsAsync(scope, source, sourceIds, token);
        }

        /// <inheritdoc />
        public Task<List<ArmadaEvent>> EnumerateLegacyEventsAsync(TokenUsageQuery query, CancellationToken token = default)
        {
            Reads++;
            return _Inner.EnumerateLegacyEventsAsync(query, token);
        }

        /// <inheritdoc />
        public Task<int> DeleteByFilterAsync(TokenUsageQuery query, CancellationToken token = default)
        {
            return _Inner.DeleteByFilterAsync(query, token);
        }

        #endregion
    }
}
