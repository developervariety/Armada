namespace Armada.Test.Unit.TestHelpers
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;

    /// <summary>
    /// A fake typed-decision client for adapter tests. Returns a configured result, records the call
    /// count, the last request, and the exact token it received (so a test can prove the adapter
    /// forwards the caller's token to the client for timeout linking), and can be configured to throw
    /// to prove the adapter never lets a client fault reach its caller.
    /// </summary>
    public sealed class FakeTypedDecisionClient : ITypedDecisionClient
    {
        private readonly TypedDecisionResult _Result;
        private readonly Exception? _Throw;

        /// <summary>How many times the client was called.</summary>
        public int CallCount { get; private set; }

        /// <summary>The last request received.</summary>
        public TypedDecisionRequest? LastRequest { get; private set; }

        /// <summary>The token received on the last call.</summary>
        public CancellationToken LastToken { get; private set; }

        /// <summary>Create a fake that returns the supplied result.</summary>
        /// <param name="result">The result to return from every call.</param>
        public FakeTypedDecisionClient(TypedDecisionResult result)
        {
            _Result = result ?? throw new ArgumentNullException(nameof(result));
        }

        private FakeTypedDecisionClient(Exception throwEx)
        {
            _Throw = throwEx;
            _Result = new TypedDecisionResult { Available = false, UnavailableReason = "exception" };
        }

        /// <summary>Create a fake that throws on every call.</summary>
        /// <param name="throwEx">The exception to throw.</param>
        /// <returns>A throwing fake.</returns>
        public static FakeTypedDecisionClient Throwing(Exception throwEx) => new FakeTypedDecisionClient(throwEx);

        /// <inheritdoc />
        public Task<TypedDecisionResult> DecideAsync(TypedDecisionRequest request, CancellationToken token)
        {
            CallCount++;
            LastRequest = request;
            LastToken = token;
            if (_Throw != null) throw _Throw;
            return Task.FromResult(_Result);
        }
    }
}
