namespace Armada.Core.Json
{
    using System.Text.Json;

    /// <summary>
    /// Process-wide JsonSerializerOptions singletons.
    ///
    /// Background: each JsonSerializerOptions instance maintains its own internal
    /// type-info cache and emits its own DynamicMethod-backed serializer factories
    /// the first time it sees a given type. Those emitted methods live in the .NET
    /// runtime's CodeManager arena, which is unmanaged memory that is essentially
    /// never released for the lifetime of the process.
    ///
    /// dotMemory profiling against a running admiral observed 15 concurrent live
    /// JsonSerializerOptions instances retaining 2.28 MB of managed type-info caches
    /// AND a much larger unmanaged tail (the JIT-emitted DynamicILGenerator /
    /// DynamicMethod entries were among the biggest managed roots, but each is a
    /// proxy for a much larger native code-gen footprint). Multiple JsonSerializerOptions
    /// that produce identical JSON behaviour multiply that cost by N for no gain.
    ///
    /// Use these singletons everywhere the options would otherwise be `new`'d
    /// inline. JsonSerializerOptions becomes effectively read-only after the first
    /// serialization, so sharing across callers is safe.
    /// </summary>
    public static class JsonDefaults
    {
        /// <summary>
        /// Default for OpenAI-compatible REST clients (DeepSeek embedding /
        /// inference, OpenCode Server, etc.) and any internal JSON parsing that
        /// just needs case-insensitive property matching.
        /// </summary>
        public static readonly JsonSerializerOptions Insensitive = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        /// <summary>
        /// Default for Web-style serialization (camelCase property names, case-insensitive
        /// reads, AllowReadingFromString for numbers). Used by the public REST API,
        /// MCP tool helpers, and most orchestration services that read browser/agent
        /// payloads.
        /// </summary>
        public static readonly JsonSerializerOptions Web = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        /// <summary>
        /// Default for human-readable output: WriteIndented = true. Used by
        /// RequestHistoryCaptureService and similar diagnostic dumps.
        /// </summary>
        public static readonly JsonSerializerOptions Indented = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        /// <summary>
        /// Like <see cref="Web"/> but also indented. Used where a Web-style payload is
        /// dumped to disk for review.
        /// </summary>
        public static readonly JsonSerializerOptions WebIndented = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        };
    }
}
