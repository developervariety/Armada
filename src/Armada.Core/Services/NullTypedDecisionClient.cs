namespace Armada.Core.Services
{
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;

    /// <summary>
    /// The typed-decision client used when the feature is off or no key is present. Every call
    /// returns an unavailable result with reason <c>disabled</c>, so every consumer falls back to
    /// its deterministic rule. This is the default wiring.
    /// </summary>
    public sealed class NullTypedDecisionClient : ITypedDecisionClient
    {
        /// <inheritdoc />
        public Task<TypedDecisionResult> DecideAsync(TypedDecisionRequest request, CancellationToken token)
        {
            return Task.FromResult(new TypedDecisionResult
            {
                Available = false,
                UnavailableReason = "disabled",
                Answers = new Dictionary<string, TypedAnswer>()
            });
        }
    }
}
