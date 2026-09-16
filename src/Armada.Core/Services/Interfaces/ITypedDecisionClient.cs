namespace Armada.Core.Services.Interfaces
{
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;

    /// <summary>
    /// Client for the typed-decision system (TypeSafe Jev). A decision point sends a redacted
    /// state and a set of typed questions and receives typed answers. The client is advisory:
    /// it never throws into a caller, and an unavailable result means the caller uses its
    /// deterministic rule.
    /// </summary>
    public interface ITypedDecisionClient
    {
        /// <summary>
        /// Decide the supplied request. Returns typed answers when available, or a result with
        /// <c>Available == false</c> and a reason on any timeout, non-2xx, or parse failure.
        /// Never throws into the caller.
        /// </summary>
        /// <param name="request">The decision point, redacted state, and questions.</param>
        /// <param name="token">Cancellation token linked to the caller.</param>
        /// <returns>The typed-decision result; never null.</returns>
        Task<TypedDecisionResult> DecideAsync(TypedDecisionRequest request, CancellationToken token);
    }
}
