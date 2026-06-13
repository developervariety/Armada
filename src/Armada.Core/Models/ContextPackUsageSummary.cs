namespace Armada.Core.Models
{
    using System.Collections.Generic;
    using System.Text.Json;

    /// <summary>
    /// Compact projection of a persisted <c>mission.context_pack_usage</c> event for status surfaces.
    /// </summary>
    public sealed class ContextPackUsageSummary
    {
        #region Public-Members

        /// <summary>Event type for context pack usage telemetry.</summary>
        public const string EventType = "mission.context_pack_usage";

        /// <summary>Compliance classification from pack usage mining.</summary>
        public string Compliance { get; set; } = "";

        /// <summary>Whether a context pack was staged for the mission.</summary>
        public bool PackStaged { get; set; }

        /// <summary>Whether the captain log was available for mining.</summary>
        public bool LogAvailable { get; set; }

        /// <summary>Number of search-tool calls observed in the captain log.</summary>
        public int SearchToolCallCount { get; set; }

        /// <summary>First log offset where the context pack was read, if observed.</summary>
        public int? FirstContextPackReadOffset { get; set; }

        /// <summary>First log offset where a search tool was used, if observed.</summary>
        public int? FirstSearchToolOffset { get; set; }

        /// <summary>Count of prestaged pack files the captain read.</summary>
        public int FilesReadFromPackCount { get; set; }

        /// <summary>Count of prestaged pack files the captain ignored.</summary>
        public int FilesIgnoredFromPackCount { get; set; }

        /// <summary>Count of non-pack files discovered via grep, glob, or code search.</summary>
        public int FilesGrepDiscoveredCount { get; set; }

        /// <summary>Count of files the captain edited or wrote.</summary>
        public int FilesEditedCount { get; set; }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Projects a persisted event payload into a <see cref="ContextPackUsageSummary"/>.
        /// Returns null when the payload is missing, blank, or cannot be parsed.
        /// </summary>
        /// <param name="payloadJson">JSON payload from a context pack usage event.</param>
        /// <returns>A summary instance, or null when projection is not possible.</returns>
        public static ContextPackUsageSummary? FromEventPayload(string? payloadJson)
        {
            if (String.IsNullOrWhiteSpace(payloadJson))
            {
                return null;
            }

            try
            {
                ContextPackUsagePayloadMirror? mirror = JsonSerializer.Deserialize<ContextPackUsagePayloadMirror>(payloadJson);
                if (mirror == null)
                {
                    return null;
                }

                ContextPackUsageSummary summary = new ContextPackUsageSummary();
                summary.Compliance = mirror.ContextPackCompliance ?? "";
                summary.PackStaged = mirror.ContextPackStaged;
                summary.LogAvailable = mirror.LogAvailable;
                summary.SearchToolCallCount = mirror.SearchToolCallCount;
                summary.FirstContextPackReadOffset = mirror.FirstContextPackReadOffset;
                summary.FirstSearchToolOffset = mirror.FirstSearchToolOffset;
                summary.FilesReadFromPackCount = mirror.FilesReadFromPack != null ? mirror.FilesReadFromPack.Count : 0;
                summary.FilesIgnoredFromPackCount = mirror.FilesIgnoredFromPack != null ? mirror.FilesIgnoredFromPack.Count : 0;
                summary.FilesGrepDiscoveredCount = mirror.FilesGrepDiscovered != null ? mirror.FilesGrepDiscovered.Count : 0;
                summary.FilesEditedCount = mirror.FilesEdited != null ? mirror.FilesEdited.Count : 0;
                return summary;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        #endregion

        #region Private-Members

        /// <summary>Mirror of the emitter payload shape in <c>MissionService.EmitContextPackUsageTelemetryAsync</c>.</summary>
        private sealed class ContextPackUsagePayloadMirror
        {
            /// <summary>Mission identifier.</summary>
            public string? MissionId { get; set; }

            /// <summary>Whether the captain log was available.</summary>
            public bool LogAvailable { get; set; }

            /// <summary>Whether a context pack was staged.</summary>
            public bool ContextPackStaged { get; set; }

            /// <summary>Compliance classification string.</summary>
            public string? ContextPackCompliance { get; set; }

            /// <summary>First context pack read offset.</summary>
            public int? FirstContextPackReadOffset { get; set; }

            /// <summary>First search tool offset.</summary>
            public int? FirstSearchToolOffset { get; set; }

            /// <summary>Search tool call count.</summary>
            public int SearchToolCallCount { get; set; }

            /// <summary>Files read from the pack.</summary>
            public List<string>? FilesReadFromPack { get; set; }

            /// <summary>Pack files ignored.</summary>
            public List<string>? FilesIgnoredFromPack { get; set; }

            /// <summary>Files discovered via search tools.</summary>
            public List<string>? FilesGrepDiscovered { get; set; }

            /// <summary>Files edited by the captain.</summary>
            public List<string>? FilesEdited { get; set; }
        }

        #endregion
    }
}
