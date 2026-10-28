namespace Armada.Core.Storage
{
    using System.Text.Json.Serialization;

    /// <summary>Outcome of measuring one storage location, one scan or the database.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum StorageStatusEnum
    {
        /// <summary>No measurement has run.</summary>
        Pending,

        /// <summary>Every entry under the location was measured within the bounds.</summary>
        Complete,

        /// <summary>A bound was reached or an entry could not be read; the figures are a lower bound and the warnings name why.</summary>
        Partial,

        /// <summary>The location does not exist.</summary>
        Missing,

        /// <summary>The location was not measured: the scan budget ran out before it, or it could not be opened.</summary>
        Unavailable
    }
}
