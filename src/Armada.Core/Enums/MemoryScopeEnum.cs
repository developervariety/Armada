namespace Armada.Core.Enums
{
    using System.Text.Json.Serialization;

    /// <summary>
    /// Ownership scope of a native memory record inside its tenant.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum MemoryScopeEnum
    {
        /// <summary>
        /// Visible to every user in the tenant. Only tenant administrators and global
        /// administrators may change or delete it.
        /// </summary>
        TenantWide,

        /// <summary>
        /// Visible to and editable by the owning user, plus tenant and global administrators.
        /// </summary>
        UserSpecific
    }
}
