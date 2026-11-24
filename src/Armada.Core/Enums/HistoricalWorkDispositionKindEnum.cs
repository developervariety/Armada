namespace Armada.Core.Enums
{
    using System.Text.Json.Serialization;
    /// <summary>Explicit owner disposition of saved historical work, not a machine acceptance verdict.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum HistoricalWorkDispositionKindEnum
    {
        /// <summary>No disposition was supplied.</summary>
        Unspecified,
        /// <summary>The exact owner states that the bound historical work remains unaccepted.</summary>
        Unaccepted,
        /// <summary>The work is accepted and cannot use this recovery.</summary>
        Accepted
    }
}
