namespace Armada.Server
{
    using System;

    /// <summary>
    /// One REST request the admiral has started and not yet answered. See <see cref="InFlightRequestRegistry"/>.
    /// </summary>
    public sealed class InFlightRequest
    {
        #region Public-Members

        /// <summary>HTTP method.</summary>
        public string Method { get; }

        /// <summary>Request URL with its query string.</summary>
        public string Url { get; }

        /// <summary>When the request started.</summary>
        public DateTime StartUtc { get; }

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="method">HTTP method.</param>
        /// <param name="url">Request URL with its query string.</param>
        /// <param name="startUtc">When the request started.</param>
        public InFlightRequest(string method, string url, DateTime startUtc)
        {
            Method = method ?? String.Empty;
            Url = url ?? String.Empty;
            StartUtc = startUtc;
        }

        #endregion
    }
}
