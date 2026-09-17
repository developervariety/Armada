namespace Armada.Core.Enums
{
    using System.Runtime.Serialization;
    using System.Text.Json.Serialization;

    /// <summary>
    /// What a typed-decision evaluation case checks.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum TypedDecisionEvalCaseKindEnum
    {
        /// <summary>
        /// Each variant has reference answers stated under the case's assumptions; the model must
        /// match them. A pair normally changes one relevant fact, so the reference answer changes too.
        /// </summary>
        [EnumMember(Value = "Reference")]
        Reference,

        /// <summary>
        /// The two variants differ only in a fact that must not matter; the named answers must agree.
        /// </summary>
        [EnumMember(Value = "Consistency")]
        Consistency
    }
}
