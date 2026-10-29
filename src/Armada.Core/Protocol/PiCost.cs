namespace Armada.Core.Protocol
{
    using System.Text.Json.Serialization;

    /// <summary>
    /// Cost of one pi model response in US dollars, from the <c>usage.cost</c> object of a pi <c>--mode json</c>
    /// assistant message.
    /// </summary>
    public class PiCost
    {
        #region Public-Members

        /// <summary>
        /// Input cost, or null.
        /// </summary>
        [JsonPropertyName("input")]
        public double? Input { get; set; } = null;

        /// <summary>
        /// Output cost, or null.
        /// </summary>
        [JsonPropertyName("output")]
        public double? Output { get; set; } = null;

        /// <summary>
        /// Cache-read cost, or null.
        /// </summary>
        [JsonPropertyName("cacheRead")]
        public double? CacheRead { get; set; } = null;

        /// <summary>
        /// Cache-write cost, or null.
        /// </summary>
        [JsonPropertyName("cacheWrite")]
        public double? CacheWrite { get; set; } = null;

        /// <summary>
        /// Total cost, or null.
        /// </summary>
        [JsonPropertyName("total")]
        public double? Total { get; set; } = null;

        #endregion
    }
}
