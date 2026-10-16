namespace Armada.Core.Enums
{
    using System.Runtime.Serialization;
    using System.Text.Json.Serialization;

    /// <summary>
    /// Where the Workspace's Open in Terminal opened (or would open) a terminal.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum WorkspaceTerminalLocationEnum
    {
        /// <summary>
        /// On the desktop of the Harbor that holds the vessel's checkout.
        /// </summary>
        [EnumMember(Value = "Harbor")]
        Harbor,

        /// <summary>
        /// On the Admiral's computer, where the vessel's working directory is.
        /// </summary>
        [EnumMember(Value = "AdmiralHost")]
        AdmiralHost,

        /// <summary>
        /// Nowhere: the vessel has no checkout.
        /// </summary>
        [EnumMember(Value = "None")]
        None
    }
}
