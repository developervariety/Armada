namespace Armada.Core.Models
{
    using System.Text.Json.Serialization;

    /// <summary>
    /// Discriminates the kind of symbol in a code graph symbol record.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum CodeGraphSymbolKindEnum
    {
        /// <summary>Namespace declaration.</summary>
        Namespace,

        /// <summary>Class declaration.</summary>
        Class,

        /// <summary>Interface declaration.</summary>
        Interface,

        /// <summary>Record declaration.</summary>
        Record,

        /// <summary>Enum declaration.</summary>
        Enum,

        /// <summary>Struct declaration.</summary>
        Struct,

        /// <summary>Method declaration.</summary>
        Method,

        /// <summary>Constructor declaration.</summary>
        Constructor,

        /// <summary>Property declaration.</summary>
        Property,

        /// <summary>Field declaration.</summary>
        Field,

        /// <summary>Delegate declaration.</summary>
        Delegate,

        /// <summary>Unknown symbol kind.</summary>
        Unknown
    }
}
