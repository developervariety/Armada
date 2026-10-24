namespace Armada.Server
{
    using System.Runtime.Serialization;
    using System.Text.Json.Serialization;

    /// <summary>
    /// Outcome of a persona create, update, or delete, mapped by each surface to its own error shape.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum PersonaWriteStatusEnum
    {
        /// <summary>
        /// The write succeeded.
        /// </summary>
        [EnumMember(Value = "Ok")]
        Ok,

        /// <summary>
        /// The persona does not exist or is not visible to the caller (REST 404, NotFound).
        /// </summary>
        [EnumMember(Value = "NotFound")]
        NotFound,

        /// <summary>
        /// The caller may see the persona but not change it (REST 403, Forbidden).
        /// </summary>
        [EnumMember(Value = "Forbidden")]
        Forbidden,

        /// <summary>
        /// The request is invalid: a required field is missing, the routing fields do not validate, the default captain
        /// is not visible, or the persona is built in and cannot be deleted (REST 400, InvalidArgument).
        /// </summary>
        [EnumMember(Value = "Invalid")]
        Invalid
    }
}
