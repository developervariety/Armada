namespace Armada.Core.Enums
{
    using System.Text.Json.Serialization;

    /// <summary>
    /// Which post-land regression class a Check guards or an incident reports.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum RegressionPurposeEnum
    {
        /// <summary>The record is not about a post-land regression.</summary>
        None = 0,

        /// <summary>A consumer of the landed change stopped building or passing its tests.</summary>
        Consumer = 1,

        /// <summary>A tracked ledger or recorded evidence stopped matching the landed change.</summary>
        Ledger = 2
    }
}
