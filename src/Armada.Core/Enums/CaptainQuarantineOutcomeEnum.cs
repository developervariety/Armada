namespace Armada.Core.Enums
{
    using System.Text.Json.Serialization;

    /// <summary>
    /// Outcome of a manual captain quarantine or release request.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum CaptainQuarantineOutcomeEnum
    {
        /// <summary>
        /// The captain is now quarantined with the requested reason and expiry.
        /// </summary>
        Quarantined,

        /// <summary>
        /// The quarantine was released and the captain is Idle.
        /// </summary>
        Released,

        /// <summary>
        /// Release was requested for a captain that is not quarantined. Nothing changed.
        /// </summary>
        NotQuarantined,

        /// <summary>
        /// Quarantine was refused because the captain owns a mission, dock or process, or is not Idle. Nothing changed.
        /// </summary>
        Busy,

        /// <summary>
        /// No captain with that identifier is visible to the caller.
        /// </summary>
        NotFound,

        /// <summary>
        /// The request was invalid (missing reason or an expiry that is not in the future). Nothing changed.
        /// </summary>
        InvalidRequest
    }
}
