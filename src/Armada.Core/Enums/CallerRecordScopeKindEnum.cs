namespace Armada.Core.Enums
{
    using System.Text.Json.Serialization;

    /// <summary>
    /// How far a caller's read reaches across stored records.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum CallerRecordScopeKindEnum
    {
        /// <summary>
        /// The caller reads no record.
        /// </summary>
        None,

        /// <summary>
        /// The caller is a global administrator and reads every record.
        /// </summary>
        Global,

        /// <summary>
        /// The caller is a tenant administrator and reads the records of its own tenant.
        /// </summary>
        Tenant,

        /// <summary>
        /// The caller reads only the records it owns.
        /// </summary>
        User
    }
}
