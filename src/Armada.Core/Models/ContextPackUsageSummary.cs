namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;

    /// <summary>
    /// Strongly-typed projection of a <c>mission.context_pack_usage</c> ArmadaEvent payload.
    /// Exposes a verified pack-read indicator derived exclusively from the miner's observed
    /// read offset -- never from the captain's self-reported Pack: line.
    /// Constructed via <see cref="FromEventPayload"/>.
    /// </summary>
    public sealed class ContextPackUsageSummary
    {
        #region Public-Members

        /// <summary>Mission identifier this summary belongs to.</summary>
        public string MissionId { get; set; } = "";

        /// <summary>Whether the captain log file was available and readable when the event was emitted.</summary>
        public bool LogAvailable { get; set; }

        /// <summary>Whether the mission had a prestaged <c>_briefing/context-pack.md</c> at dispatch time.</summary>
        public bool ContextPackStaged { get; set; }

        /// <summary>
        /// Raw compliance classification from the miner: ReadBeforeSearch, SearchBeforeRead,
        /// PackReadNoSearch, SearchWithoutPackRead, PackStagedNoSearchNoRead,
        /// NoPackStagedSearchUsed, NoPackStagedNoSearch, LogUnavailablePackStaged,
        /// or LogUnavailableNoPackStaged.
        /// </summary>
        public string ContextPackCompliance { get; set; } = "";

        /// <summary>
        /// First log offset at which the miner observed a <c>_briefing/context-pack.md</c> read tool call.
        /// Null when no read was detected. This is the authoritative source for <see cref="PackReadVerified"/>.
        /// </summary>
        public int? FirstContextPackReadOffset { get; set; }

        /// <summary>First log offset at which a search tool (Grep/Glob/armada_code_search) was observed. Null if none.</summary>
        public int? FirstSearchToolOffset { get; set; }

        /// <summary>Total number of search-tool calls observed in the captain log.</summary>
        public int SearchToolCallCount { get; set; }

        /// <summary>Prestaged files the captain actually read.</summary>
        public List<string> FilesReadFromPack { get; set; } = new List<string>();

        /// <summary>Prestaged files the captain never read.</summary>
        public List<string> FilesIgnoredFromPack { get; set; } = new List<string>();

        /// <summary>Non-prestaged files read after a search-tool call (grep-discovered).</summary>
        public List<string> FilesGrepDiscovered { get; set; } = new List<string>();

        /// <summary>Files the captain edited or wrote.</summary>
        public List<string> FilesEdited { get; set; } = new List<string>();

        /// <summary>
        /// True when <see cref="FirstContextPackReadOffset"/> has a value, meaning the miner
        /// observed an actual file-read tool call for <c>_briefing/context-pack.md</c> in the
        /// captain log. False when the offset is null, regardless of any self-reported
        /// Pack: line in the captain's output (anti-fabrication guarantee).
        /// </summary>
        public bool PackReadVerified { get; set; }

        #endregion

        #region Private-Members

        private static readonly JsonSerializerOptions _DeserializeOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        #endregion

        #region Constructors-and-Factories

        private ContextPackUsageSummary()
        {
        }

        /// <summary>
        /// Deserialize a <c>mission.context_pack_usage</c> ArmadaEvent payload JSON string
        /// into a <see cref="ContextPackUsageSummary"/>. Returns null on malformed or empty
        /// payloads without throwing.
        /// </summary>
        /// <param name="payloadJson">Raw JSON payload string from the ArmadaEvent. May be null or empty.</param>
        /// <returns>Populated summary, or null when the payload is absent, empty, or unreadable.</returns>
        public static ContextPackUsageSummary? FromEventPayload(string? payloadJson)
        {
            if (String.IsNullOrWhiteSpace(payloadJson)) return null;
            try
            {
                ContextPackUsagePayload? dto = JsonSerializer.Deserialize<ContextPackUsagePayload>(
                    payloadJson, _DeserializeOptions);
                if (dto == null) return null;

                ContextPackUsageSummary summary = new ContextPackUsageSummary();
                summary.MissionId = dto.MissionId ?? "";
                summary.LogAvailable = dto.LogAvailable;
                summary.ContextPackStaged = dto.ContextPackStaged;
                summary.ContextPackCompliance = dto.ContextPackCompliance ?? "";
                summary.FirstContextPackReadOffset = dto.FirstContextPackReadOffset;
                summary.FirstSearchToolOffset = dto.FirstSearchToolOffset;
                summary.SearchToolCallCount = dto.SearchToolCallCount;
                summary.FilesReadFromPack = dto.FilesReadFromPack ?? new List<string>();
                summary.FilesIgnoredFromPack = dto.FilesIgnoredFromPack ?? new List<string>();
                summary.FilesGrepDiscovered = dto.FilesGrepDiscovered ?? new List<string>();
                summary.FilesEdited = dto.FilesEdited ?? new List<string>();
                // Verified iff the miner recorded an actual read offset -- self-reported Pack: lines cannot set this.
                summary.PackReadVerified = dto.FirstContextPackReadOffset.HasValue;
                return summary;
            }
            catch (Exception)
            {
                return null;
            }
        }

        #endregion

        #region Private-Types

        /// <summary>Internal DTO matching the anonymous-type shape emitted by EmitContextPackUsageTelemetryAsync.</summary>
        private sealed class ContextPackUsagePayload
        {
            /// <summary>Mission identifier.</summary>
            public string? MissionId { get; set; }

            /// <summary>Whether the captain log was available.</summary>
            public bool LogAvailable { get; set; }

            /// <summary>Whether the context pack was staged.</summary>
            public bool ContextPackStaged { get; set; }

            /// <summary>Compliance classification string from the miner.</summary>
            public string? ContextPackCompliance { get; set; }

            /// <summary>First log offset where the context pack was read, or null.</summary>
            public int? FirstContextPackReadOffset { get; set; }

            /// <summary>First log offset where a search tool was observed, or null.</summary>
            public int? FirstSearchToolOffset { get; set; }

            /// <summary>Total search tool call count.</summary>
            public int SearchToolCallCount { get; set; }

            /// <summary>Files read from the pack.</summary>
            public List<string>? FilesReadFromPack { get; set; }

            /// <summary>Files in the pack that were not read.</summary>
            public List<string>? FilesIgnoredFromPack { get; set; }

            /// <summary>Files discovered via grep/search after pack was skipped.</summary>
            public List<string>? FilesGrepDiscovered { get; set; }

            /// <summary>Files the captain edited or wrote.</summary>
            public List<string>? FilesEdited { get; set; }
        }

        #endregion
    }
}
