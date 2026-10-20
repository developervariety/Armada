namespace Armada.Core.Enums
{
    using System.Text.Json.Serialization;

    /// <summary>
    /// How one path changed between two commits, as git's tree comparison reports it.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum ChangedPathStatusEnum
    {
        /// <summary>The path exists only at the later commit.</summary>
        Added,

        /// <summary>The path exists at both commits with different content or mode.</summary>
        Modified,

        /// <summary>The path exists only at the earlier commit.</summary>
        Deleted,

        /// <summary>The content moved from a previous path to this path.</summary>
        Renamed,

        /// <summary>The entry changed type, for example from a file to a symbolic link.</summary>
        TypeChanged
    }
}
