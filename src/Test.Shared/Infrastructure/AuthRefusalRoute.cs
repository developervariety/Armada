namespace Test.Shared.Infrastructure
{
    using System;
    using System.Net.Http;
    using System.Threading.Tasks;

    /// <summary>
    /// A protected REST route that a caller without valid credentials must be refused on. A write route carries
    /// a valid request body where it needs one, and an authenticated read that proves the refused request
    /// changed nothing.
    /// </summary>
    public sealed class AuthRefusalRoute
    {
        #region Public-Members

        /// <summary>
        /// Short scenario name, used in test case names.
        /// </summary>
        public string Scenario { get; }

        /// <summary>
        /// HTTP method.
        /// </summary>
        public HttpMethod Method { get; }

        /// <summary>
        /// Request path, including any query string.
        /// </summary>
        public string Path { get; }

        /// <summary>
        /// Build a valid request body that carries the given unique marker, or null for a route without a body.
        /// </summary>
        public Func<string, object>? BuildBody { get; }

        /// <summary>
        /// Authenticated read that reports whether the refused request's write is visible (for example a row carrying
        /// the given marker), or null for a read route.
        /// </summary>
        public Func<HttpClient, string, Task<bool>>? WriteObservedAsync { get; }

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Create a refusal route.
        /// </summary>
        /// <param name="scenario">Short scenario name.</param>
        /// <param name="method">HTTP method.</param>
        /// <param name="path">Request path.</param>
        /// <param name="buildBody">Valid body builder for a write route.</param>
        /// <param name="writeObservedAsync">Authenticated no-write check for a write route.</param>
        public AuthRefusalRoute(
            string scenario,
            HttpMethod method,
            string path,
            Func<string, object>? buildBody = null,
            Func<HttpClient, string, Task<bool>>? writeObservedAsync = null)
        {
            Scenario = scenario ?? throw new ArgumentNullException(nameof(scenario));
            Method = method ?? throw new ArgumentNullException(nameof(method));
            Path = path ?? throw new ArgumentNullException(nameof(path));
            if (buildBody != null && writeObservedAsync == null)
                throw new ArgumentException("A route with a body needs a no-write check.");
            BuildBody = buildBody;
            WriteObservedAsync = writeObservedAsync;
        }

        #endregion
    }
}
