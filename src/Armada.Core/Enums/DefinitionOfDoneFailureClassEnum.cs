namespace Armada.Core.Enums
{
    using System.Text.Json.Serialization;

    /// <summary>
    /// Classification of why a definition-of-done command failed. Provides a stable,
    /// structured reason alongside the bounded actionable command diagnostics.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum DefinitionOfDoneFailureClassEnum
    {
        /// <summary>
        /// Source compilation failed.
        /// </summary>
        Compile,

        /// <summary>
        /// A test command completed with one or more failing tests.
        /// </summary>
        TestFail,

        /// <summary>
        /// The command exceeded the configured execution timeout.
        /// </summary>
        Timeout,

        /// <summary>
        /// Command launch, setup, restore, dependency resolution, or another
        /// unclassified infrastructure operation failed.
        /// </summary>
        Infra
    }
}
