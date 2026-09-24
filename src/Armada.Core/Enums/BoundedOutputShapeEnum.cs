namespace Armada.Core.Enums
{
    using System.Text.Json.Serialization;

    /// <summary>
    /// Which part of a process output stream is kept when it exceeds its byte budget.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum BoundedOutputShapeEnum
    {
        /// <summary>Keep the beginning and a rolling end; the middle is dropped. Suits logs, whose verdict comes last.</summary>
        HeadAndTail,

        /// <summary>Keep the beginning only. Suits a document read front to back.</summary>
        Head
    }
}
