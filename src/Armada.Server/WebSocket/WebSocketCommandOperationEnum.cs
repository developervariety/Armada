namespace Armada.Server.WebSocket
{
    using System.Text.Json.Serialization;

    /// <summary>
    /// What a WebSocket command does to the records it names.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum WebSocketCommandOperationEnum
    {
        /// <summary>Reads one record.</summary>
        Read,

        /// <summary>Lists records.</summary>
        List,

        /// <summary>Creates a record.</summary>
        Create,

        /// <summary>Changes a record.</summary>
        Update,

        /// <summary>Deletes a record.</summary>
        Delete,

        /// <summary>Runs an operation with side effects beyond one record.</summary>
        Action
    }
}
