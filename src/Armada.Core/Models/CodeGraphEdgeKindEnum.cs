namespace Armada.Core.Models
{
    using System.Text.Json.Serialization;

    /// <summary>
    /// Discriminates the kind of relationship between two symbols in the code graph.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum CodeGraphEdgeKindEnum
    {
        /// <summary>Source type contains (declares) the target symbol.</summary>
        Contains,

        /// <summary>Source type inherits from the target type.</summary>
        Inherits,

        /// <summary>Source type implements the target interface.</summary>
        Implements,

        /// <summary>Source method or constructor calls the target method.</summary>
        Calls,

        /// <summary>Source file imports (using directive) the target namespace.</summary>
        Imports,

        /// <summary>Source symbol references the target type.</summary>
        References,

        /// <summary>Unknown edge kind.</summary>
        Unknown
    }
}
