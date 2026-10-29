namespace Armada.Core.Enums
{
    using System.Text.Json.Serialization;

    /// <summary>
    /// Which body a persona prompt renders. Current is the full prompt. Lean and Compact are shorter bodies
    /// for readers that follow a short, line-per-demand prompt better than a long one.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum PersonaPromptVariantEnum
    {
        /// <summary>The full persona prompt.</summary>
        Current = 0,

        /// <summary>One demand per line, with the output contract first and last.</summary>
        Lean = 1,

        /// <summary>The same demands and tool rules in the fewest words.</summary>
        Compact = 2
    }
}
