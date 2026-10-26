namespace Armada.Core.Enums
{
    using System.Runtime.Serialization;
    using System.Text.Json.Serialization;

    /// <summary>
    /// What a stage check (the vessel's build and unit-test commands run in the dock after a stage) established.
    /// Only <see cref="Passed"/> means the stage's work was verified.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum StageCheckStatusEnum
    {
        /// <summary>
        /// Every configured command ran and succeeded (or failed on the stage's starting commit too).
        /// </summary>
        [EnumMember(Value = "Passed")]
        Passed,

        /// <summary>
        /// A configured command failed in the dock and does not fail on the stage's starting commit.
        /// </summary>
        [EnumMember(Value = "Failed")]
        Failed,

        /// <summary>
        /// The check did not apply to this stage; the reason says why. The work was not verified.
        /// </summary>
        [EnumMember(Value = "Skipped")]
        Skipped,

        /// <summary>
        /// The check applied but could not run to a result (Harbor away, timeout, an error). The work was not verified.
        /// </summary>
        [EnumMember(Value = "CouldNotRun")]
        CouldNotRun
    }
}
