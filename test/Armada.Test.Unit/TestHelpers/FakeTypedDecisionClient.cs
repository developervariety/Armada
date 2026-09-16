namespace Armada.Test.Unit.TestHelpers
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;

    /// <summary>
    /// A fake typed-decision client for adapter tests. It returns a scripted result (fixed or per
    /// request), records the call count, the last request, and the exact token it received (so a test
    /// can prove the adapter forwards the caller's token for timeout linking), and can be configured to
    /// throw (so a test can prove the adapter never lets a client fault reach its caller).
    /// </summary>
    public sealed class FakeTypedDecisionClient : ITypedDecisionClient
    {
        private readonly Func<TypedDecisionRequest, TypedDecisionResult> _Responder;
        private readonly Exception? _Throw;

        /// <summary>How many times the client was called.</summary>
        public int CallCount { get; private set; }

        /// <summary>How many times the client was called (alias of <see cref="CallCount"/>).</summary>
        public int Calls => CallCount;

        /// <summary>The last request received.</summary>
        public TypedDecisionRequest? LastRequest { get; private set; }

        /// <summary>The token received on the last call.</summary>
        public CancellationToken LastToken { get; private set; }

        /// <summary>Create a fake that returns the supplied result on every call.</summary>
        /// <param name="result">The result to return.</param>
        public FakeTypedDecisionClient(TypedDecisionResult result)
            : this(_ => result ?? throw new ArgumentNullException(nameof(result)))
        {
        }

        /// <summary>Create a fake with a per-request responder.</summary>
        /// <param name="responder">Produces the result for each request.</param>
        public FakeTypedDecisionClient(Func<TypedDecisionRequest, TypedDecisionResult> responder)
        {
            _Responder = responder ?? throw new ArgumentNullException(nameof(responder));
        }

        private FakeTypedDecisionClient(Exception throwEx)
        {
            _Throw = throwEx;
            _Responder = _ => new TypedDecisionResult { Available = false, UnavailableReason = "exception" };
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
            return Task.FromResult(_Responder(request));
        }

        /// <summary>Build an available result carrying one noul answer.</summary>
        /// <param name="questionId">The question id.</param>
        /// <param name="noul">The noul value, reused as the confidence.</param>
        /// <returns>An available result.</returns>
        public static TypedDecisionResult Noul(string questionId, double noul)
        {
            Dictionary<string, TypedAnswer> answers = new Dictionary<string, TypedAnswer>(StringComparer.Ordinal)
            {
                [questionId] = new TypedAnswer { Type = "noul", Noul = noul, Confidence = noul }
            };
            return new TypedDecisionResult { Available = true, Answers = answers, InputTokens = 10, OutputTokens = 5, LatencyMs = 12 };
        }

        /// <summary>Build an available result carrying a noul plus a choice answer.</summary>
        /// <param name="noulId">The noul question id.</param>
        /// <param name="noul">The noul value, reused as the confidence.</param>
        /// <param name="choiceId">The choice question id.</param>
        /// <param name="choice">The chosen option.</param>
        /// <returns>An available result.</returns>
        public static TypedDecisionResult NoulAndChoice(string noulId, double noul, string choiceId, string choice)
        {
            Dictionary<string, TypedAnswer> answers = new Dictionary<string, TypedAnswer>(StringComparer.Ordinal)
            {
                [noulId] = new TypedAnswer { Type = "noul", Noul = noul, Confidence = noul },
                [choiceId] = new TypedAnswer { Type = "choice", Choice = choice, Confidence = noul }
            };
            return new TypedDecisionResult { Available = true, Answers = answers, InputTokens = 10, OutputTokens = 5, LatencyMs = 12 };
        }

        /// <summary>Build an unavailable result.</summary>
        /// <param name="reason">The unavailable reason.</param>
        /// <returns>An unavailable result.</returns>
        public static TypedDecisionResult Unavailable(string reason)
        {
            return new TypedDecisionResult { Available = false, UnavailableReason = reason };
        }
    }
}
