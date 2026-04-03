namespace Armada.Core.Enums
{
    using System.Runtime.Serialization;
    using System.Text.Json.Serialization;

    /// <summary>
    /// Current connection state for the outbound remote-control tunnel.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum RemoteTunnelStateEnum
    {
        /// <summary>
        /// Remote control is disabled in settings.
        /// </summary>
        [EnumMember(Value = "Disabled")]
        Disabled,

        /// <summary>
        /// The tunnel is not currently connected.
        /// </summary>
        [EnumMember(Value = "Disconnected")]
        Disconnected,

        /// <summary>
        /// The tunnel is attempting to connect.
        /// </summary>
        [EnumMember(Value = "Connecting")]
        Connecting,

        /// <summary>
        /// The tunnel is connected to the control plane.
        /// </summary>
        [EnumMember(Value = "Connected")]
        Connected,

        /// <summary>
        /// The tunnel encountered an error and will retry.
        /// </summary>
        [EnumMember(Value = "Error")]
        Error,

        /// <summary>
        /// The tunnel is shutting down.
        /// </summary>
        [EnumMember(Value = "Stopping")]
        Stopping
    }
}
