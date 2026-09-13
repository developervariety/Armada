namespace Armada.Core.Enums
{
    using System.Text.Json.Serialization;

    /// <summary>Source of the effective landing configuration.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum LandingConfigurationSourceEnum
    {
        /// <summary>Explicit voyage override.</summary>
        Voyage,
        /// <summary>Explicit vessel mode.</summary>
        Vessel,
        /// <summary>Explicit global mode.</summary>
        Global,
        /// <summary>Legacy voyage and global boolean flags.</summary>
        Legacy
    }
}
