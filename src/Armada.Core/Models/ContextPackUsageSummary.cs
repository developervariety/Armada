namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;

    /// <summary>
    /// Projection of a mission.context_pack_usage event payload summarizing
    /// how a captain used (or bypassed) the staged context pack during a mission.
    /// </summary>
    public class ContextPackUsageSummary
    {
        #region Public-Members

        /// <summary>
        /// Event type emitted by MissionService when context pack usage telemetry is captured.
        /// </summary>
        public const string EventType = "mission.context_pack_usage";

        /// <summary>
        /// Mission identifier this summary belongs to.
        /// </summary>
        public string MissionId { get; set; } = "";

        /// <summary>
        /// Whether the captain log file existed and was readable.
        /// </summary>
        public bool LogAvailable { get; set; }

        /// <summary>
        /// Whether the mission had a prestaged _briefing/context-pack.md entry.
        /// </summary>
        public bool ContextPackStaged { get; set; }

        /// <summary>
        /// Compliance classification for pack-first discovery.
        /// Values include: ReadBeforeSearch, SearchBeforeRead, PackReadNoSearch,
        /// SearchWithoutPackRead, PackStagedNoSearchNoRead, NoPackStagedSearchUsed,
        /// NoPackStagedNoSearch, LogUnavailablePackStaged, LogUnavailableNoPackStaged.
        /// </summary>
        public string ContextPackCompliance { get; set; } = "";

        /// <summary>
        /// First log offset where the context pack was read, if observed.
        /// </summary>
        public int? FirstContextPackReadOffset { get; set; }

        /// <summary>
        /// First log offset where a search tool call was observed, if any.
        /// </summary>
        public int? FirstSearchToolOffset { get; set; }

        /// <summary>
        /// Number of Grep/Glob/armada_code_search tool calls observed.
        /// </summary>
        public int SearchToolCallCount { get; set; }

        /// <summary>
        /// Prestaged files the captain Read.
        /// </summary>
        public List<string> FilesReadFromPack { get; set; } = new List<string>();

        /// <summary>
        /// Prestaged files the captain never Read (selector over-included).
        /// </summary>
        public List<string> FilesIgnoredFromPack { get; set; } = new List<string>();

        /// <summary>
        /// Non-prestaged files the captain Read after a Glob/Grep/code-search (selector miss).
        /// </summary>
        public List<string> FilesGrepDiscovered { get; set; } = new List<string>();

        /// <summary>
        /// Files the captain Edited or Wrote during the mission.
        /// </summary>
        public List<string> FilesEdited { get; set; } = new List<string>();

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate an empty summary.
        /// </summary>
        public ContextPackUsageSummary()
        {
        }

        /// <summary>
        /// Deserialize the payload of a mission.context_pack_usage event into a summary.
        /// Returns null when the event payload is missing or unparseable.
        /// </summary>
        /// <param name="evt">The ArmadaEvent whose EventType is mission.context_pack_usage.</param>
        public static ContextPackUsageSummary? FromEventPayload(ArmadaEvent evt)
        {
            if (evt == null) throw new ArgumentNullException(nameof(evt));
            if (String.IsNullOrEmpty(evt.Payload)) return null;
            return JsonSerializer.Deserialize<ContextPackUsageSummary>(
                evt.Payload,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }

        #endregion
    }
}
