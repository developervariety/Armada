namespace Test.Shared.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    internal sealed class AskThreadCompletionReadBarrier : IAskThreadMethods
    {
        private readonly IAskThreadMethods _Inner;
        private readonly string _PausedRequestId;
        private int _CompletionCommitted;
        private int _PauseUsed;
        private readonly TaskCompletionSource<bool> _Release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> FinalReadEntered { get; } = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        public AskThreadCompletionReadBarrier(IAskThreadMethods inner, string pausedRequestId)
        {
            _Inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _PausedRequestId = pausedRequestId ?? throw new ArgumentNullException(nameof(pausedRequestId));
        }

        public void ReleaseFinalRead()
        {
            _Release.TrySetResult(true);
        }

        public Task<AskThread> CreateAsync(AskThread thread, CancellationToken token = default)
        {
            return _Inner.CreateAsync(thread, token);
        }

        public Task<AskThread?> ReadAsync(string tenantId, string userId, string threadId, CancellationToken token = default)
        {
            return _Inner.ReadAsync(tenantId, userId, threadId, token);
        }

        public Task<EnumerationResult<AskThread>> EnumerateAsync(string tenantId, string userId, int pageNumber, int pageSize, bool includeArchived, CancellationToken token = default)
        {
            return _Inner.EnumerateAsync(tenantId, userId, pageNumber, pageSize, includeArchived, token);
        }

        public Task<AskThreadDeleteOutcomeEnum> TryDeleteAsync(string tenantId, string userId, string threadId, CancellationToken token = default)
        {
            return _Inner.TryDeleteAsync(tenantId, userId, threadId, token);
        }

        public Task<AskThread?> UpdateAsync(AskThread thread, CancellationToken token = default)
        {
            return _Inner.UpdateAsync(thread, token);
        }

        public Task<AskTurnReservation> TryReserveTurnAsync(string tenantId, string userId, AskTurn turn, CancellationToken token = default)
        {
            return _Inner.TryReserveTurnAsync(tenantId, userId, turn, token);
        }

        public async Task<AskTurn?> ReadTurnAsync(string tenantId, string userId, string threadId, string requestId, CancellationToken token = default)
        {
            AskTurn? turn = await _Inner.ReadTurnAsync(tenantId, userId, threadId, requestId, token).ConfigureAwait(false);
            if (String.Equals(requestId, _PausedRequestId, StringComparison.Ordinal)
                && Volatile.Read(ref _CompletionCommitted) == 1
                && Interlocked.CompareExchange(ref _PauseUsed, 1, 0) == 0)
            {
                FinalReadEntered.TrySetResult(true);
                await _Release.Task.WaitAsync(token).ConfigureAwait(false);
            }
            return turn;
        }

        public Task<List<AskTurn>> ReadHistoryAsync(string tenantId, string userId, string threadId, int limit, CancellationToken token = default)
        {
            return _Inner.ReadHistoryAsync(tenantId, userId, threadId, limit, token);
        }

        public Task<List<AskTurn>> EnumerateTurnsAsync(string tenantId, string userId, string threadId, long? beforeSequence, int pageSize, CancellationToken token = default)
        {
            return _Inner.EnumerateTurnsAsync(tenantId, userId, threadId, beforeSequence, pageSize, token);
        }

        public Task<bool> MarkRunningAsync(string tenantId, string userId, string threadId, string requestId, CancellationToken token = default)
        {
            return _Inner.MarkRunningAsync(tenantId, userId, threadId, requestId, token);
        }

        public Task<bool> MarkReadAsync(string tenantId, string userId, string threadId, CancellationToken token = default)
        {
            return _Inner.MarkReadAsync(tenantId, userId, threadId, token);
        }

        public Task<bool> MarkUnreadAsync(string tenantId, string userId, string threadId, CancellationToken token = default)
        {
            return _Inner.MarkUnreadAsync(tenantId, userId, threadId, token);
        }

        public async Task<bool> CompleteTurnAsync(string tenantId, string userId, string threadId, string requestId, CaptainChatResponse response, CancellationToken token = default)
        {
            bool completed = await _Inner.CompleteTurnAsync(tenantId, userId, threadId, requestId, response, token).ConfigureAwait(false);
            if (completed && String.Equals(requestId, _PausedRequestId, StringComparison.Ordinal))
                Interlocked.Exchange(ref _CompletionCommitted, 1);
            return completed;
        }
    }

}
