namespace Armada.Core.Enums
{
    using System.Runtime.Serialization;
    using System.Text.Json.Serialization;

    /// <summary>
    /// Capability/cost tier for a captain, used by dispatch to route a mission of a given complexity to
    /// an appropriately-capable captain. Ordered from cheapest to strongest so tiers can be compared.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum CaptainTierEnum
    {
        /// <summary>
        /// Cheap, fast captains for routine/mechanical work.
        /// </summary>
        [EnumMember(Value = "Economy")]
        Economy = 0,

        /// <summary>
        /// General-purpose captains (the default).
        /// </summary>
        [EnumMember(Value = "Standard")]
        Standard = 1,

        /// <summary>
        /// Strong captains reserved for complex/high-stakes work.
        /// </summary>
        [EnumMember(Value = "Premium")]
        Premium = 2
    }
}
