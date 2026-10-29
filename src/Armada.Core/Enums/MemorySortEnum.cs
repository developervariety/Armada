namespace Armada.Core.Enums
{
    using System.Runtime.Serialization;
    using System.Text.Json.Serialization;

    /// <summary>
    /// Column a memory enumeration is sorted by. The direction comes from <c>EnumerationQuery.Order</c>
    /// (CreatedAscending sorts ascending, CreatedDescending descending). Ties are broken by created time, then ID,
    /// in the same direction.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum MemorySortEnum
    {
        /// <summary>
        /// Creation time.
        /// </summary>
        [EnumMember(Value = "Created")]
        Created,

        /// <summary>
        /// Last update time.
        /// </summary>
        [EnumMember(Value = "Updated")]
        Updated,

        /// <summary>
        /// Salience (importance). The historical default order is salience descending, then newest first.
        /// </summary>
        [EnumMember(Value = "Salience")]
        Salience,

        /// <summary>
        /// Vessel name (the memory's vessel, else its source vessel), case-insensitive; memories with no vessel
        /// sort as an empty name.
        /// </summary>
        [EnumMember(Value = "Vessel")]
        Vessel,

        /// <summary>
        /// Memory type.
        /// </summary>
        [EnumMember(Value = "Type")]
        Type,

        /// <summary>
        /// Topic, case-insensitive.
        /// </summary>
        [EnumMember(Value = "Topic")]
        Topic,

        /// <summary>
        /// Summary (or content when the summary is empty), case-insensitive.
        /// </summary>
        [EnumMember(Value = "Summary")]
        Summary,

        /// <summary>
        /// Visibility scope (TenantWide or UserSpecific).
        /// </summary>
        [EnumMember(Value = "Visibility")]
        Visibility
    }
}
