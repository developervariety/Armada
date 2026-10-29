namespace Armada.Core.Enums
{
    using System.Runtime.Serialization;
    using System.Text.Json.Serialization;

    /// <summary>
    /// Rendering variant of a persona prompt (docs/MODEL_HETEROGENEITY.md, section 3.2 and Phase 4). Each variant is a
    /// prompt template in the template store: <c>Current</c> is the persona's own template
    /// (<c>persona.&lt;name&gt;</c>), the others add a suffix (<c>persona.&lt;name&gt;.lean</c>,
    /// <c>persona.&lt;name&gt;.compact</c>). Every variant keeps the same completion and signal lines; acceptance
    /// criteria and project rules are delivered outside the persona prompt and are never changed by the variant.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum PromptVariantEnum
    {
        /// <summary>
        /// Today's default persona prompt, unchanged (the default).
        /// </summary>
        [EnumMember(Value = "Current")]
        Current = 0,

        /// <summary>
        /// One demand per line, the output contract repeated at the end, less emphasis wording and boilerplate.
        /// </summary>
        [EnumMember(Value = "Lean")]
        Lean = 1,

        /// <summary>
        /// A shorter tier for readers with a small context.
        /// </summary>
        [EnumMember(Value = "Compact")]
        Compact = 2
    }
}
