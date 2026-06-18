namespace Armada.Server
{
    using Armada.Core.Models;

    /// <summary>
    /// Structured result returned by the shared voyage dispatch service.
    /// </summary>
    public sealed class VoyageDispatchResult
    {
        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="value">Response payload.</param>
        /// <param name="statusCode">HTTP-style status code for REST callers.</param>
        public VoyageDispatchResult(object value, int statusCode)
        {
            Value = value;
            StatusCode = statusCode;
        }

        /// <summary>
        /// Response payload. MCP returns this directly.
        /// </summary>
        public object Value { get; }

        /// <summary>
        /// HTTP-style status code for REST callers.
        /// </summary>
        public int StatusCode { get; }

        /// <summary>
        /// True when the dispatch completed successfully.
        /// </summary>
        public bool Succeeded => StatusCode >= 200 && StatusCode < 300;

        /// <summary>
        /// Created voyage when the payload is a voyage.
        /// </summary>
        public Voyage? Voyage => Value as Voyage;

        /// <summary>
        /// Create a successful result.
        /// </summary>
        /// <param name="value">Response payload.</param>
        /// <returns>Successful result.</returns>
        public static VoyageDispatchResult Success(object value)
        {
            return new VoyageDispatchResult(value, 201);
        }

        /// <summary>
        /// Create a bad-request result.
        /// </summary>
        /// <param name="value">Response payload.</param>
        /// <returns>Bad-request result.</returns>
        public static VoyageDispatchResult BadRequest(object value)
        {
            return new VoyageDispatchResult(value, 400);
        }

        /// <summary>
        /// Create a not-found result.
        /// </summary>
        /// <param name="value">Response payload.</param>
        /// <returns>Not-found result.</returns>
        public static VoyageDispatchResult NotFound(object value)
        {
            return new VoyageDispatchResult(value, 404);
        }
    }
}
