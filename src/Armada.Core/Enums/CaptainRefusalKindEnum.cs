namespace Armada.Core.Enums
{
    using System.Runtime.Serialization;
    using System.Text.Json.Serialization;

    /// <summary>
    /// Why a captain declined its mission. A declared refusal is the typed signal the brief asks for; the
    /// other kinds are recognised from runtime output only when no typed signal was written.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum CaptainRefusalKindEnum
    {
        /// <summary>The captain did not refuse.</summary>
        [EnumMember(Value = "None")]
        None,

        /// <summary>The captain wrote the structured refusal marker with its reason.</summary>
        [EnumMember(Value = "DeclaredRefusal")]
        DeclaredRefusal,

        /// <summary>The provider's own safety gate refused the request.</summary>
        [EnumMember(Value = "ProviderSafeguardBlock")]
        ProviderSafeguardBlock,

        /// <summary>The model declined the work in prose without writing the structured marker.</summary>
        [EnumMember(Value = "ModelPolicyRefusal")]
        ModelPolicyRefusal
    }
}
