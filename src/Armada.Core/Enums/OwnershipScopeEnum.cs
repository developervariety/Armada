namespace Armada.Core.Enums
{
    using System.Text.Json.Serialization;

    /// <summary>
    /// Who may see a configuration record inside its tenant. This is ownership, not applicability:
    /// it says which callers may read or change the record, never which vessels or workflows it applies to.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum OwnershipScopeEnum
    {
        /// <summary>
        /// Visible to every user in the owning tenant. Only tenant administrators and global
        /// administrators may change or delete it.
        /// </summary>
        TenantWide,

        /// <summary>
        /// Visible to and editable by the owning user, plus tenant and global administrators.
        /// </summary>
        UserSpecific
    }
}
