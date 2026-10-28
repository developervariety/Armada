namespace Armada.Core.Storage
{
    using System.Text.Json.Serialization;

    /// <summary>How a table row count was obtained.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum StorageCountKindEnum
    {
        /// <summary>The provider reported no count.</summary>
        Unavailable,

        /// <summary>The count comes from provider statistics and can lag the table.</summary>
        Estimated,

        /// <summary>The count comes from a bounded <c>COUNT(*)</c> over the table.</summary>
        Exact
    }
}
