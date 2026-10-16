namespace Armada.Core.Enums
{
    using System.Runtime.Serialization;
    using System.Text.Json.Serialization;

    /// <summary>
    /// A decision on a pending CLI permission request.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum CliPermissionDecisionEnum
    {
        /// <summary>
        /// Allow this one call.
        /// </summary>
        [EnumMember(Value = "AllowOnce")]
        AllowOnce,

        /// <summary>
        /// Allow this call and store an allow rule (admins only).
        /// </summary>
        [EnumMember(Value = "AllowAndRemember")]
        AllowAndRemember,

        /// <summary>
        /// Allow this call and every later CLI tool call in the request's Ask conversation: the thread's policy is set to
        /// Bypass, pending requests from the thread are allowed, and prompts still arriving from the running turn are
        /// allowed without asking (the thread owner, when an admin).
        /// </summary>
        [EnumMember(Value = "AllowForConversation")]
        AllowForConversation,

        /// <summary>
        /// Deny this call.
        /// </summary>
        [EnumMember(Value = "Deny")]
        Deny
    }
}
