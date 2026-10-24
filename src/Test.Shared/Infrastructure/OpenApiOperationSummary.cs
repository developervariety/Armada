namespace Test.Shared.Infrastructure
{
    /// <summary>
    /// One OpenAPI operation's summary and deprecated flag.
    /// </summary>
    public sealed class OpenApiOperationSummary
    {
        /// <summary>
        /// Operation summary.
        /// </summary>
        public string? Summary { get; set; } = null;

        /// <summary>
        /// Whether the operation is deprecated.
        /// </summary>
        public bool Deprecated { get; set; } = false;
    }
}
