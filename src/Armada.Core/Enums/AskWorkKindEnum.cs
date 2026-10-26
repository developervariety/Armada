namespace Armada.Core.Enums
{
    using System.Text.Json.Serialization;

    /// <summary>Kind of work record created by an Ask action.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum AskWorkKindEnum
    {
        /// <summary>A mission record.</summary>
        Mission,
        /// <summary>A voyage record.</summary>
        Voyage,
        /// <summary>An accepted long-running job.</summary>
        Job
    }
}
