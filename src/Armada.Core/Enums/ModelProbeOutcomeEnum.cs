namespace Armada.Core.Enums
{
    using System.Runtime.Serialization;
    using System.Text.Json.Serialization;

    /// <summary>
    /// What one probe observed.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum ModelProbeOutcomeEnum
    {
        /// <summary>
        /// The observation met Armada's contract.
        /// </summary>
        [EnumMember(Value = "Passed")]
        Passed,

        /// <summary>
        /// The probe ran and observed a problem (a mismatch, a violation, a malformed answer, a timeout).
        /// </summary>
        [EnumMember(Value = "Failed")]
        Failed,

        /// <summary>
        /// The probe cannot be applied to this runtime or could not be started (the reason is recorded); never a failure of the model.
        /// </summary>
        [EnumMember(Value = "CouldNotRun")]
        CouldNotRun
    }
}
