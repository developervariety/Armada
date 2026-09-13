namespace Armada.Core.Enums
{
    using System.Text.Json.Serialization;

    /// <summary>
    /// Recorded auto-land predicate decision for a mission's merge entry.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum AutoLandDecisionOutcomeEnum
    {
        /// <summary>
        /// An auto_land_triggered event was recorded: processing of the merge entry was started. The entry can still
        /// be held for deep review; read the merge entry audit lane.
        /// </summary>
        Triggered,

        /// <summary>
        /// An auto_land_skipped event was recorded: the entry was not processed automatically. The reason says why,
        /// for example a failed predicate, an unavailable diff, or a landing-drain safety-net hold during calibration
        /// or after a critical trigger even when the predicate passed.
        /// </summary>
        Skipped
    }
}
