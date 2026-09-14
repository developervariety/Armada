namespace Armada.Core.Harbor
{
    using System;
    using System.Threading.Tasks;

    /// <summary>
    /// A typed pending response owned by one runner connection generation.
    /// </summary>
    public sealed class HarborPendingRequest<T>
    {
        private readonly TaskCompletionSource<T> _Completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Correlation identifier.</summary>
        public string RequestId { get; }

        /// <summary>Runner that owns the request.</summary>
        public string RunnerId { get; }

        /// <summary>Connection generation that owns the request.</summary>
        public long Generation { get; }

        /// <summary>Task completed by the matching response.</summary>
        public Task<T> Completion => _Completion.Task;

        internal HarborPendingRequest(string requestId, string runnerId, long generation)
        {
            RequestId = requestId;
            RunnerId = runnerId;
            Generation = generation;
        }

        internal bool TryComplete(object response)
        {
            if (response is not T typed) return false;
            return _Completion.TrySetResult(typed);
        }

        internal void Cancel()
        {
            _Completion.TrySetCanceled();
        }
    }
}
