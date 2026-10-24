namespace Test.Shared.Infrastructure
{
    /// <summary>
    /// One HTTP request an api/client.ts export sends, parsed from its source.
    /// </summary>
    public sealed class DashboardApiCall
    {
        #region Public-Members

        /// <summary>
        /// Export name.
        /// </summary>
        public string Export { get; set; } = "";

        /// <summary>
        /// HTTP method, upper case.
        /// </summary>
        public string Method { get; set; } = "";

        /// <summary>
        /// Path with each interpolated segment replaced by <c>x</c> and the query string removed.
        /// </summary>
        public string Path { get; set; } = "";

        #endregion
    }
}
