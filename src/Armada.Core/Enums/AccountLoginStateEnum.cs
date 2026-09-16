namespace Armada.Core.Enums
{
    using System.Runtime.Serialization;
    using System.Text.Json.Serialization;

    /// <summary>State of one dashboard-driven account login.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum AccountLoginStateEnum
    {
        /// <summary>The login process runs and waits for the owner to finish in a browser or paste a code.</summary>
        [EnumMember(Value = "Pending")]
        Pending,

        /// <summary>The runtime reported a completed login, or the key was stored.</summary>
        [EnumMember(Value = "Succeeded")]
        Succeeded,

        /// <summary>The login ended without success; see the safe reason.</summary>
        [EnumMember(Value = "Failed")]
        Failed,

        /// <summary>The login was not finished within its lifetime and its process was stopped.</summary>
        [EnumMember(Value = "Expired")]
        Expired,

        /// <summary>The operator cancelled the login and its process was stopped.</summary>
        [EnumMember(Value = "Cancelled")]
        Cancelled
    }
}
