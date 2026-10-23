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
        Infra,

        /// <summary>
        /// Every test run the command reported finished with zero failed tests, but the
        /// command still exited non-zero: a step after the tests (a wrapper's verification,
        /// a cleanup, a teardown) failed. No test failed, so it is not a test failure.
        /// </summary>
        PostTestStep
    }
}
