namespace Test.Shared.Infrastructure
{
    using System.Collections.Generic;

    /// <summary>
    /// The paths of an OpenAPI document, read only as far as the operations' summary and deprecated flag.
    /// </summary>
    public sealed class OpenApiPathsDocument
    {
        /// <summary>
        /// Route template to HTTP method (lower case) to operation.
        /// </summary>
        public Dictionary<string, Dictionary<string, OpenApiOperationSummary>> Paths { get; set; } = new Dictionary<string, Dictionary<string, OpenApiOperationSummary>>();
    }
}
