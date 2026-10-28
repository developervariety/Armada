namespace Armada.Core.Enums
{
    using System.Text.Json.Serialization;

    /// <summary>
    /// Whether a repository health report was computed. Unavailable means the owner host
    /// could not be asked, so no checkout was read on any other host.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum RepositoryHealthAvailabilityEnum
    {
        /// <summary>The report was computed on the host that owns the checkout.</summary>
        Available,

        /// <summary>The owner host could not be asked. No substitute host was read.</summary>
        Unavailable
    }
}
