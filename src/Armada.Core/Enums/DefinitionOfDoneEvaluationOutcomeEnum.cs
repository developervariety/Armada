namespace Armada.Core.Enums
{
    using System.Text.Json.Serialization;

    /// <summary>
    /// Recorded outcome of one definition-of-done gate evaluation at mission completion.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum DefinitionOfDoneEvaluationOutcomeEnum
    {
        /// <summary>
        /// The gate ran its required commands and they passed.
        /// </summary>
        Passed,

        /// <summary>
        /// The gate did not run commands because it does not apply (disabled, persona not applied, doc-only marker).
        /// Completion accepts a skipped gate; a skip is not evidence that a build or test ran.
        /// </summary>
        Skipped,

        /// <summary>
        /// The gate was not run because the mission changed nothing, so its commands would measure the base commit.
        /// </summary>
        NotVerifiable,

        /// <summary>
        /// A required command failed.
        /// </summary>
        Failed,

        /// <summary>
        /// The gate could not complete its evaluation because of an unexpected error.
        /// </summary>
        EvaluationError
    }
}
