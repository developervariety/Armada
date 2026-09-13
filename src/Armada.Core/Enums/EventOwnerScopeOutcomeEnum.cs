namespace Armada.Core.Enums
{
    using System.Text.Json.Serialization;

    /// <summary>
    /// Result of giving an event its owner's tenant and user.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum EventOwnerScopeOutcomeEnum
    {
        /// <summary>
        /// The event already carried a tenant; it was left unchanged.
        /// </summary>
        AlreadyScoped,

        /// <summary>
        /// The owner was resolved and the event now carries its tenant and user.
        /// </summary>
        Scoped,

        /// <summary>
        /// No referenced record exists or carries a tenant, so the event stays visible only to an unscoped administrator.
        /// </summary>
        NoOwnerRecord,

        /// <summary>
        /// Reading a referenced record failed; the event stays unscoped and the detail names the failure.
        /// </summary>
        LookupFailed
    }
}
