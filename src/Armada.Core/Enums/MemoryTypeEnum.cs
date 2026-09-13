namespace Armada.Core.Enums
{
    using System.Text.Json.Serialization;

    /// <summary>
    /// Classification of a native captain memory record. Working memory (the live context window,
    /// loaded files, recent tool results) is transient and is never stored, so only the three
    /// durable categories exist.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum MemoryTypeEnum
    {
        /// <summary>
        /// What happened and when: actions taken and key decisions with their reasons.
        /// </summary>
        Episodic,

        /// <summary>
        /// A fact that stands without its episode.
        /// </summary>
        Semantic,

        /// <summary>
        /// How to do something: workflows, checklists, and repeatable procedures.
        /// </summary>
        Procedural
    }
}
