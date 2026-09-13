namespace Armada.Core.Enums
{
    using System.Text.Json.Serialization;

    /// <summary>Reason recorded by the resource-pressure admission policy.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum ResourcePressureReasonEnum
    {
        /// <summary>The implementation did not supply a typed reason.</summary>
        Unknown,
        /// <summary>Normal pressure checks are disabled.</summary>
        Disabled,
        /// <summary>The configured pressure checks permitted launch.</summary>
        CapacityAvailable,
        /// <summary>The OOM suspension deadline has not elapsed.</summary>
        OomCooldown,
        /// <summary>Measured available memory is below the configured minimum.</summary>
        InsufficientMemory,
        /// <summary>The active workload count reached the build limit.</summary>
        BuildLimit
    }
}
